using System;
using System.Linq;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
#if !MELONLOADER
using TMPro;
#else
using TMPro = Il2CppTMPro;
#endif

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 任务打字机打印同步（MsgType=134）：Teleprinter.SubmitLines / ClearAll / ClearAlarm 事件广播。
///
/// 打字机（Teleprinter）显示任务目标指示文本（目标确认/坐标/阶段提示）。任务状态机
/// （SleepyNodes MissionGraph 节点，如 State_TeleprinterText）在**主机**跑，触发打字机打印；
/// 客机没有跑完整任务图 → 打字机文本两端不同（目标位置已由 FireMission seed 同步，
/// 但"打印动作"没同步）。
///
/// 方案：Harmony patch Teleprinter.SubmitLines（postfix，提取打印文本行）+ ClearAll/ClearAlarm，
/// 主机广播；客机收到后本地找到对应打字机执行相同打印/清除。防环：应用远端时 IsApplying=true。
/// </summary>
public sealed class TeleprinterSync : ISyncedModule
{
    public byte MsgType => 134;
    private const byte MsgTypeId = 134;

    // 事件类型（消息第二字节）
    private const byte EvPrint = 1;   // SubmitLines
    private const byte EvState = 2;   // 完整状态同步（_currentFullRich / _tmp.text）
    private const byte EvClearAll = 3;
    private const byte EvClearAlarm = 4;
    private const byte EvAppend = 5;  // AppendInstant（直接追加富文本块）

    /// <summary>应用远端打字机事件时的防环标志。</summary>
    public static bool IsApplying;
    private static int _log;
    private static int _stateDiag;   // Tick 状态诊断降频（每 20 次状态变化打一次）
    private static int _applyDiag;   // applied state 日志降频（每 10 次应用打一次）

    // 状态同步：打字机类型 -> 最近一次完整富文本（检测变化）
    private readonly System.Collections.Generic.Dictionary<byte, string> _lastRich = new();
    private float _stateTimer;
    private const float StateInterval = 0.5f;
    // ⚠️ 打印中高频广播间隔：打字机打印中 revealed 逐字增加，若用 0.5s 扫描会漏掉中间态
    //（短文本 <0.5s 打印完）→ 客机只收到最终态 → 无逐字动画。打印中缩到 0.1s 捕获中间 reveal 序列。
    private const float StateIntervalPrinting = 0.1f;
    private bool _anyPrinting;          // 是否有打字机正在打印（决定高频广播）
    private float _printingCheckTimer;  // 打印状态检查降频（0.25s）

    // ---------------- 本地事件（Harmony patch 调用） ----------------

    /// <summary>本地 Teleprinter.SubmitLines 被调用（postfix）→ 主机广播打印文本行。</summary>
    public static void OnLocalPrint(Teleprinter printer, System.Collections.Generic.List<string> lines)
    {
        try
        {
            if (IsApplying) return;
            var net = CoopRuntime.Net;
            if (net == null || printer == null) return;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
            // 打字机事件仅主机广播（主机权威）：客机打字机若被任务/反射异步触发 SubmitLines
            // 会回发干扰主机（主机收到后 Apply → 重新 SubmitLines → 打印队列重置 → 打字机卡住）。
            if (!net.IsHost) return;
            if (lines == null || lines.Count == 0) return;
            // 取打印机类型（TeleprinterType）用于跨端定位同一台打字机
            byte ptype = 0;
            try { ptype = (byte)(int)printer.TeleprinterType; } catch { }
            var w = NetProtocol.Begin((MsgType)MsgTypeId);
            w.Put(EvPrint);
            w.Put(ptype);
            w.Put((byte)Math.Min(lines.Count, 255));
            for (int i = 0; i < lines.Count && i < 255; i++)
                w.Put(lines[i] ?? "");
            Broadcast(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"TeleprinterSync OnLocalPrint: {ex.Message}"); }
    }

    /// <summary>本地 Teleprinter.AppendInstant 被调用（直接追加富文本块）→ 主机广播。
    /// 打字机任务文本可能通过 AppendInstant 逐块追加（区块/编号行），SubmitLines 只覆盖
    /// 队列打印——两者都同步才能完整复现打字机内容。</summary>
    public static void OnLocalAppend(Teleprinter printer, string chunkRich, bool prepend)
    {
        try
        {
            if (IsApplying) return;
            var net = CoopRuntime.Net;
            if (net == null || printer == null) return;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
            if (!net.IsHost) return;
            if (string.IsNullOrEmpty(chunkRich)) return;
            byte ptype = 0;
            try { ptype = (byte)(int)printer.TeleprinterType; } catch { }
            var w = NetProtocol.Begin((MsgType)MsgTypeId);
            w.Put(EvAppend);
            w.Put(ptype);
            w.Put(prepend ? (byte)1 : (byte)0);
            w.Put(chunkRich);
            Broadcast(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"TeleprinterSync OnLocalAppend: {ex.Message}"); }
    }

    /// <summary>本地 Teleprinter.ClearAll 被调用 → 广播清除。</summary>
    public static void OnLocalClearAll(Teleprinter printer)
    {
        try
        {
            if (IsApplying) return;
            var net = CoopRuntime.Net;
            if (net == null || printer == null) return;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
            if (!net.IsHost) return;
            byte ptype = 0;
            try { ptype = (byte)(int)printer.TeleprinterType; } catch { }
            var w = NetProtocol.Begin((MsgType)MsgTypeId);
            w.Put(EvClearAll);
            w.Put(ptype);
            Broadcast(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"TeleprinterSync OnLocalClearAll: {ex.Message}"); }
    }

    /// <summary>本地 Teleprinter.ClearAlarm 被调用 → 广播清除报警。</summary>
    public static void OnLocalClearAlarm(Teleprinter printer)
    {
        try
        {
            if (IsApplying) return;
            var net = CoopRuntime.Net;
            if (net == null || printer == null) return;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
            if (!net.IsHost) return;
            byte ptype = 0;
            try { ptype = (byte)(int)printer.TeleprinterType; } catch { }
            var w = NetProtocol.Begin((MsgType)MsgTypeId);
            w.Put(EvClearAlarm);
            w.Put(ptype);
            Broadcast(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"TeleprinterSync OnLocalClearAlarm: {ex.Message}"); }
    }



    private static void Broadcast(NetDataWriter w)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        var data = NetProtocol.Snapshot(w);
        if (net.IsHost)
        {
            foreach (var p in net.Roster)
                if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
        }
        else if (net.HostSteamId != 0)
            net.Transport.Send(net.HostSteamId, data, true);
        // 打字机打印/清除事件 → 即时同步通知灯状态（灯亮灭时间短，等轮询会错过）
        if (net.IsHost)
        {
            try { ButtonClickSync.BroadcastNotificationLights(); }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"TeleprinterSync notif-lights instant-sync: {ex.Message}"); }
        }
        if ((++_log % 5) == 1)
            CoopRuntime.LogSource?.LogInfo($"[Teleprinter] local ev={(w.Data.Length > 1 ? w.Data[1] : (byte)0)} isHost={net.IsHost}");
    }

    // ---------------- 网络包 ----------------

    /// <summary>状态同步：定期扫描所有打字机的完整富文本（_currentFullRich），变化则广播。
    /// 打字机文本最终都落到 _currentFullRich（打印动画的完整目标文本），直接同步它最可靠——
    /// 不依赖 SubmitLines/AppendInstant 事件（那些 patch 可能因 IL2CPP 集合/反射问题失败）。</summary>
    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        // 打印中高频扫描：任一打字机 IsPrinting 则用 0.1s 间隔捕获中间 reveal 序列，
        // 否则 0.5s 兜底。先扫一次判断（FindObjectsOfTypeAll 仅每帧一次判断成本可忽略，
        // 但避免重复扫描——用轻量标志缓存）。
        bool anyPrinting = _anyPrinting;
        if (!_anyPrinting || _printingCheckTimer <= 0f)
        {
            _printingCheckTimer = 0.25f;
            try
            {
                anyPrinting = false;
                var allP = UnityEngine.Object.FindObjectsOfType<Teleprinter>(true);
                if (allP != null)
                    foreach (var tp in allP)
                        if (tp != null) { try { if (tp.IsPrinting) { anyPrinting = true; break; } } catch { } }
                _anyPrinting = anyPrinting;
            }
            catch { }
        }
        else
        {
            _printingCheckTimer -= dt;
            anyPrinting = _anyPrinting;
        }
        _stateTimer += dt;
        float interval = anyPrinting ? StateIntervalPrinting : StateInterval;
        if (_stateTimer < interval) return;
        _stateTimer = 0f;
        // 打字机状态仅主机广播（主机权威）：客机不扫描/不上行打字机状态，
        // 避免客机打字机状态变化回发主机 → 主机 Apply → 停协程/设文本 → 主机打字机卡住。
        if (net.State != SessionState.Hosting) return;
        if (IsApplying) return;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<Teleprinter>(true);
            if (all == null || all.Length == 0) return;
            foreach (var tp in all)
            {
                if (tp == null) continue;
                byte ptype = 0;
                try { ptype = (byte)(int)tp.TeleprinterType; } catch { continue; }
                string rich = "";
                try { rich = tp._currentFullRich ?? ""; } catch { }
                if (string.IsNullOrEmpty(rich))
                {
                    // 读 _tmp.text（TMP_Text 类型在 BepInEx/ML 命名空间不同，用反射避免类型编译差异）
                    try
                    {
                        var p = typeof(Teleprinter).GetField("_tmp");
                        if (p != null)
                        {
                            var tmpObj = p.GetValue(tp);
                            if (tmpObj != null)
                            {
                                var textProp = tmpObj.GetType().GetProperty("text");
                                if (textProp != null)
                                {
                                    var tv = textProp.GetValue(tmpObj);
                                    rich = tv == null ? "" : tv.ToString();
                                }
                            }
                        }
                    }
                    catch { }
                }
                if (rich.Length == 0) continue;
                // 变化检测：文本 **或** 打字进度（revealed）变化都广播——revealed 变化让客机
                // 打字机跟随主机的逐字打印动画（打字针移动/文本揭示），不只文本变化。
                int revealed = 0;
                try { revealed = tp._currentRevealedCharIndex; } catch { }
                string sig2 = rich + "|" + revealed.ToString();
                if (_lastRich.TryGetValue(ptype, out var last) && last == sig2) continue;
                _lastRich[ptype] = sig2;
                // 诊断：打印打字机视觉状态字段（打字针/纸张/揭示数），降频（每 20 次状态变化打一次）
                if ((++_stateDiag % 20) == 1)
                {
                    try
                    {
                        string diag = "";
                        try { diag += $" animTyping={tp._animTypingState}"; } catch { }
                        try { diag += $" isRunning={tp._isRunning}"; } catch { }
                        try { diag += $" isPrinting={tp.IsPrinting}"; } catch { }
                        try { diag += $" revealed={tp._currentRevealedCharIndex}"; } catch { }
                        try
                        {
                            var mask = tp._revealMask;
                            diag += $" maskCount={(mask == null ? -1 : mask.Count)}";
                        }
                        catch { }
                        try
                        {
                            var pt = tp.paperTransform;
                            diag += $" paper={(pt == null ? "null" : pt.localPosition.ToString())}";
                        }
                        catch { }
                        try { diag += $" initPaper={tp._initialPaperLocalPos}"; } catch { }
                        try
                        {
                            var ta = tp.typerAnimator;
                            diag += $" typerAnim={(ta == null ? "null" : "ok")}";
                            if (ta != null && !string.IsNullOrEmpty(tp.typingBoolName))
                                diag += $" typingBool={ta.GetBool(tp.typingBoolName)}";
                            if (ta != null)
                            {
                                var st = ta.GetCurrentAnimatorStateInfo(0);
                                diag += $" animState={st.shortNameHash} norm={st.normalizedTime:0.00} len={st.length:0.00}";
                            }
                        }
                        catch { }
                        CoopRuntime.LogSource?.LogInfo($"[Teleprinter] state ptype={ptype}{diag} rich='{Truncate(rich)}'");
                    }
                    catch { }
                }
                // 打包视觉状态：揭示字符数 + 纸张位置 + 打字针敲击状态（客机应用后同步显示，
                // 避免文本直接设完但纸张没动（太靠上）或揭示数=0（打字针反复尝试打字→抽搐））
                // revealed 已在上面变化检测读取
                float px = 0f, py = 0f, pz = 0f;
                bool paperOk = false;
                try
                {
                    var pt = tp.paperTransform;
                    if (pt != null) { var lp = pt.localPosition; px = lp.x; py = lp.y; pz = lp.z; paperOk = true; }
                }
                catch { }
                bool animTyping = false;
                try { animTyping = tp._animTypingState; } catch { }
                var w = NetProtocol.Begin((MsgType)MsgTypeId);
                w.Put(EvState);
                w.Put(ptype);
                w.Put(rich);
                w.Put(revealed);
                w.Put(paperOk ? (byte)1 : (byte)0);
                w.Put(px); w.Put(py); w.Put(pz);
                w.Put(animTyping ? (byte)1 : (byte)0);
                var data = NetProtocol.Snapshot(w);
                if (net.IsHost)
                {
                    foreach (var p in net.Roster)
                        if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
                }
                else if (net.HostSteamId != 0)
                    net.Transport.Send(net.HostSteamId, data, true);
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"TeleprinterSync Tick: {ex.Message}"); }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            byte ev = r.GetByte();
            byte ptype = r.GetByte();
            if (net.IsHost)
            {
                // 主机转发给其他客户端（星型拓扑）
                foreach (var p in net.Roster)
                    if (!p.IsLocal && (ulong)p.SteamId != from)
                        net.Transport.Send(p.SteamId, data, true);
                // 打字机事件/状态仅由主机权威广播：主机收到的打字机包（异常回发）只转发，
                // 不本地 Apply——Apply 会 DrainAllJobsInstant/停协程/设文本 → 主机打字机卡住。
                return;
            }
            Apply(ev, ptype, r);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"TeleprinterSync OnPacket: {ex.Message}"); }
    }

    private static void Apply(byte ev, byte ptype, NetDataReader r)
    {
        try
        {
            IsApplying = true;
            try
            {
                var tp = FindPrinter(ptype);
                switch (ev)
                {
                    case EvPrint:
                    {
                        int n = r.GetByte();
                        var lines = new System.Collections.Generic.List<string>(n);
                        for (int i = 0; i < n; i++)
                            lines.Add(r.GetString());
                        if (tp == null) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] apply print but printer null ptype={ptype}"); return; }
                        // 防重复打印（2026-08-15）：客机本地任务图/同 seed 也会打印初始任务文本，
                        // 主机 EvPrint 再复现 → 打字机"多打一份"（初始文本双份）。若打印机当前完整富文本
                        // 已等于本次行文本 → 本地已打印同内容，跳过复现（子任务新文本不匹配 → 正常复现）。
                        try
                        {
                            string joined = string.Join("\n", lines).Trim();
                            string curRich = (tp._currentFullRich ?? "").Trim();
                            if (curRich.Length > 0 && curRich == joined)
                            {
                                CoopRuntime.LogSource?.LogInfo($"[Teleprinter] skip print (already shown) ptype={ptype} n={n}");
                                break;
                            }
                        }
                        catch { }
                        // 复现打印：把行文本逐个交给打字机（SubmitLines 需要 IEnumerable<string>）。
                        // interop 签名是 Il2CppSystem.Collections.Generic.IEnumerable<string>。
                        // ⚠️ 客机打字机动画/类型名根因与修复（2026-08-13，已确认正常）：
                        // 1) 无动画：旧实现反射 Invoke 传 Il2Cpp List → List→IEnumerable 运行时转换失败
                        //    → SubmitLines 从未成功 → 打字机协程不启动。修复：TryCast<IEnumerable<string>>()
                        //    运行时转换 + 直接调用 SubmitLines（编译期类型匹配）→ 协程启动逐字动画。
                        // 2) 类型名：根因在主机侧 PostTeleprinterPrint 用非泛型 IEnumerable 提取失败 →
                        //    ToString 类型名广播（已修，HarmonyPatches）。客机侧 Il2Cpp List Add(托管string)
                        //    会被装箱成 Object → 元素变类型名，故用 Il2CppSystem.String 隐式转换后 Add。
                        try
                        {
                            var il2cppLines = new Il2CppSystem.Collections.Generic.List<string>();
                            foreach (var s in lines)
                            {
                                try
                                {
                                    Il2CppSystem.String ilstr = s ?? ""; // 托管 string → Il2CppString 隐式转换
                                    il2cppLines.Add(ilstr);
                                }
                                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] il2cppLines.Add fail: {ex.Message}"); }
                            }
                            try
                            {
                                // TryCast 到 IEnumerable 启动协程（编译期 List 不能直接转接口）
                                var val = ((Il2CppObjectBase)il2cppLines)
                                    .TryCast<Il2CppSystem.Collections.Generic.IEnumerable<string>>();
                                if (val != null)
                                {
                                    var job = tp.SubmitLines("", val, null, false);
                                    // 兜底：若游戏从 val 生成的 job.lines 异常（类型名），用正确行替换
                                    if (job != null)
                                    {
                                        try
                                        {
                                            var jl = job.lines;
                                            if (jl != null && jl.Count > 0 && (jl[0] ?? "").StartsWith("Il2CppSystem.", StringComparison.Ordinal))
                                            {
                                                jl.Clear();
                                                foreach (var s in lines)
                                                {
                                                    Il2CppSystem.String ilstr = s ?? "";
                                                    jl.Add(ilstr);
                                                }
                                            }
                                        }
                                        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] fix job.lines: {ex.Message}"); }
                                    }
                                }
                                else
                                    CoopRuntime.LogSource?.LogWarning("[Teleprinter] apply print: TryCast IEnumerable failed");
                            }
                            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] apply print: {ex.Message}"); }
                            // 启动打印动画：SubmitLines 排队后需 TryStart 才启动逐字打印
                            // （revealed 逐字增加 + 打字针敲击）。打字机可能已被任务触发打印。
                            try { tp.TryStart(true); } catch { }
                            // 诊断：打字机打印动画是否启动（降频，每 10 次打一次）
                            try
                            {
                                if ((++_applyDiag % 10) == 7)
                                    CoopRuntime.LogSource?.LogInfo($"[Teleprinter] after print ptype={ptype} isPrinting={tp.IsPrinting} revealed={tp._currentRevealedCharIndex} isRunning={tp._isRunning}");
                            }
                            catch { }
                        }
                        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] apply print: {ex.Message}"); }
                        CoopRuntime.LogSource?.LogInfo($"[Teleprinter] applied print n={n} ptype={ptype}");
                        break;
                    }
                    case EvAppend:
                    {
                        bool prepend = r.GetByte() != 0;
                        string chunk = r.GetString();
                        if (tp == null) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] apply append but printer null ptype={ptype}"); return; }
                        try { tp.AppendInstant(chunk ?? "", prepend); }
                        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] apply append: {ex.Message}"); }
                        CoopRuntime.LogSource?.LogInfo($"[Teleprinter] applied append prepend={prepend} ptype={ptype} chunk='{Truncate(chunk)}'");
                        break;
                    }
                    case EvState:
                    {
                        string rich = r.GetString();
                        // 附加视觉状态（主机广播）：揭示字符数 + 纸张位置 + 打字针敲击
                        int revealed = 0;
                        bool paperOk = false;
                        float px = 0f, py = 0f, pz = 0f;
                        bool animTyping = false;
                        try
                        {
                            if (r.AvailableBytes >= 4) revealed = r.GetInt();
                            if (r.AvailableBytes >= 1) paperOk = r.GetByte() != 0;
                            if (r.AvailableBytes >= 12) { px = r.GetFloat(); py = r.GetFloat(); pz = r.GetFloat(); }
                            if (r.AvailableBytes >= 1) animTyping = r.GetByte() != 0;
                        }
                        catch { }
                        if (tp == null) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] apply state but printer null ptype={ptype}"); return; }
                        // 打字机是否正在打印（EvPrint 触发的逐字打印动画）。
                        // ⚠️ 修复（2026-08-13）：打印中**不要**无条件 DrainAllJobsInstant/停协程——
                        // 那会停掉客机打字机自身的逐字动画，而 EvState 只同步最终态 revealed → 动画消失
                        // （客机打字机从"打印中"直接跳到完整文本，无逐字过程）。
                        // 现在 EvPrint 已用 TryCast 成功调用 SubmitLines → 打字机协程启动逐字打印
                        //（内容=主机文本，打字机自己揭示）。因此：**只要打字机正在打印就保留动画**，
                        // 不设文本/reveal/mask（打字机协程自己逐字揭示，无需干预）；
                        // 仅在打字机**空闲**（打印完成/未启动）时才强制同步最终态（内容兜底）。
                        bool printing = false;
                        try { printing = tp.IsPrinting; } catch { }
                        bool keepLocalAnimation = printing;
                        // ⚠️ 修复（2026-08-13）：**无论打印中还是空闲，都设 _currentFullRich = 主机正确内容**。
                        // 打字机协程逐字揭示的底本就是 _currentFullRich——若打印中不设，它停留在
                        // SubmitLines 时游戏写入的值：TryCast 接口对象被游戏 ToString() 成
                        // 'Il2CppSystem.Collections.Generic.IEnumerable`1[System.String]' → 动画中显示类型名。
                        // 设 _currentFullRich 不破坏动画（reveal/mask 由协程驱动，逐字揭示照常）。
                        try { tp._currentFullRich = rich; } catch { }
                        // 设置显示文本（GetTmpText 优先反射 _tmp 字段，兜底子物体 TMP_Text）
                        bool tmpSet = false;
                        var tmpObj = GetTmpText(tp);
                        if (tmpObj != null)
                        {
                            try
                            {
                                var textProp = tmpObj.GetType().GetProperty("text");
                                if (textProp != null)
                                {
                                    // ⚠️ 打印中也设 _tmp.text = 主机正确内容：SubmitLines 时游戏可能把
                                    // TryCast 接口对象 ToString 成类型名写进 _tmp.text → 打字机显示类型名。
                                    // 设 _tmp.text = 正确 rich 不破坏动画（reveal/mask 由打字机协程逐字驱动）。
                                    textProp.SetValue(tmpObj, rich);
                                    tmpSet = true;
                                    if ((_applyDiag % 10) == 3)
                                        CoopRuntime.LogSource?.LogInfo($"[Teleprinter] set tmp.text ok (obj={tmpObj.GetType().Name}) keepAnim={keepLocalAnimation}");
                                }
                            }
                            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] set tmp.text: {ex.Message}"); }
                        }
                        if (!tmpSet)
                            CoopRuntime.LogSource?.LogWarning("[Teleprinter] failed to set display text (both _tmp reflection and child objects failed)");
                        // 1) 揭示字符数对齐主机 + 2) 重建逐字揭示遮罩——
                        //    ⚠️ 保留动画时（keepLocalAnimation）跳过：客机打字机自己的协程在逐字推进 reveal，
                        //    强制设 targetRev 会把动画瞬间推到最终态 → 动画消失。
                        int targetRev = revealed; // 供日志用（仅 !keepLocalAnimation 时生效）
                        if (!keepLocalAnimation)
                        {
                            // 打字机空闲：揭示字符数对齐主机（只增不减：打字机可能已揭示更多，
                            // 不回退 → 文本不会倒退）。注意打印中的逐字动画由打字机自己的协程
                            // 驱动（EvPrint SubmitLines 启动），这里仅作空闲时的最终态兜底。
                            int curRev = 0;
                            try { curRev = tp._currentRevealedCharIndex; } catch { }
                            targetRev = Math.Max(curRev, revealed);
                            try { tp._currentRevealedCharIndex = targetRev; } catch { }
                            // 重建逐字揭示遮罩（前 targetRev 个 true，打字动画显示"打字进度"）
                            try
                            {
                                var tmpObj2 = GetTmpText(tp);
                                if (tmpObj2 != null)
                                {
                                    try
                                    {
                                        var fm = tmpObj2.GetType().GetMethod("ForceMeshUpdate");
                                        fm?.Invoke(tmpObj2, null);
                                    }
                                    catch { }
                                    int charCount = 0;
                                    try
                                    {
                                        var ti = tmpObj2.GetType().GetProperty("textInfo")?.GetValue(tmpObj2);
                                        if (ti != null)
                                        {
                                            var cc = ti.GetType().GetProperty("characterCount");
                                            if (cc != null) charCount = (int)cc.GetValue(ti);
                                        }
                                    }
                                    catch { }
                                    if (charCount > 0)
                                    {
                                        var mask = new Il2CppSystem.Collections.Generic.List<bool>();
                                        for (int i = 0; i < charCount; i++)
                                            mask.Add(i < targetRev);
                                        try { tp._revealMask = mask; } catch { }
                                        try { tp.ApplyAlphaMaskToText(); } catch { }
                                    }
                                }
                            }
                            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] apply reveal mask: {ex.Message}"); }
                        }
                        // 3) 纸张位置 = 主机值（否则文本直接设完但纸张没随行数下移 → 文本挤在纸张顶部=太靠上）
                        if (paperOk)
                        {
                            try
                            {
                                var pt = tp.paperTransform;
                                if (pt != null)
                                {
                                    var lp = pt.localPosition;
                                    lp.x = px; lp.y = py; lp.z = pz;
                                    pt.localPosition = lp;
                                }
                            }
                            catch { }
                        }
                        // 4) 打字针敲击状态跟随主机（打印中也设：停协程后客机打字机不再自己驱动打字针，
                        //    由 animator typingBool 控制——EvState 设主机敲击状态 → 客机打字针跟随主机节奏）。
                        //    animTyping=true 敲击 / false 静止。
                        try
                        {
                            var ta = tp.typerAnimator;
                            if (ta != null && !string.IsNullOrEmpty(tp.typingBoolName))
                            {
                                try { ta.SetBool(tp.typingBoolName, animTyping); } catch { }
                            }
                        }
                        catch { }
                        try { tp._animTypingState = animTyping; } catch { }
                        // 每次状态应用都打（打字机状态不频繁；keepLocalAnimation 分支对诊断客机动画恢复至关重要）
                        if ((++_applyDiag % 10) == 1)
                            CoopRuntime.LogSource?.LogInfo($"[Teleprinter] applied state ptype={ptype} printing={printing} keepAnim={keepLocalAnimation} revealed={targetRev} animTyping={animTyping} paper=({px:0.00},{py:0.00},{pz:0.00}) rich='{Truncate(rich)}'");
                        break;
                    }
                    case EvClearAll:
                        if (tp != null) { try { tp.ClearAll(); } catch { } }
                        CoopRuntime.LogSource?.LogInfo($"[Teleprinter] applied clear-all ptype={ptype}");
                        break;
                    case EvClearAlarm:
                        if (tp != null) { try { tp.ClearAlarm(); } catch { } }
                        CoopRuntime.LogSource?.LogInfo($"[Teleprinter] applied clear-alarm ptype={ptype}");
                        break;
                }
            }
            finally { IsApplying = false; }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"TeleprinterSync Apply: {ex.Message}"); }
    }

    /// <summary>获取打字机显示文本对象（TMP_Text）：优先反射 _tmp 字段，失败则从子物体找 TMP_Text。</summary>
    private static object GetTmpText(Teleprinter tp)
    {
        try
        {
            var p = typeof(Teleprinter).GetField("_tmp",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance);
            if (p == null)
            {
                foreach (var fi in typeof(Teleprinter).GetFields(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance))
                {
                    if (fi.FieldType.Name.Contains("TMP_Text")) { p = fi; break; }
                }
            }
            if (p != null)
            {
                var tmpObj = p.GetValue(tp);
                if (tmpObj != null) return tmpObj;
            }
            // 兜底：子物体 TMP_Text（BepInEx interop：TMPro.TMP_Text；ML：Il2CppTMPro.TMP_Text）
            var texts = tp.GetComponentsInChildren<TMPro.TMP_Text>(true);
            if (texts != null && texts.Length > 0)
            {
                foreach (var t in texts)
                    if (t != null && t.text != null && t.text.Length > 0) return t;
                return texts[0];
            }
        }
        catch { }
        return null;
    }

    /// <summary>按打印机类型找打字机实例（Teleprinter.GetTeleprinter 静态注册表）。</summary>
    private static Teleprinter FindPrinter(byte ptype)
    {
        try
        {
            // Teleprinter/Teleprinters 是嵌套枚举；用 int 强转构造
            var type = (Teleprinter.Teleprinters)ptype;
            return Teleprinter.GetTeleprinter(type);
        }
        catch { return null; }
    }

    private static string Truncate(string s, int max = 240)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { }
}
