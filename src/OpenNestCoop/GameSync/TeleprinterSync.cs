using System;
using System.Linq;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
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
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"TeleprinterSync 通知灯即时同步: {ex.Message}"); }
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
        _stateTimer += dt;
        if (_stateTimer < StateInterval) return;
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
                        if (tp == null) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] apply print 但打字机 null ptype={ptype}"); return; }
                        // 复现打印：把行文本逐个交给打字机（SubmitLines 需要 IEnumerable<string>）。
                        // interop 签名是 Il2CppSystem.Collections.Generic.IEnumerable<string>，编译期托管/Il2Cpp List 均不匹配；
                        // 用反射调用（跳过编译期类型检查），运行时传 Il2Cpp List（其实现 Il2Cpp IEnumerable）。
                        // 注意：此路径作为辅助；主路径是 EvState 状态同步（直接设文本，可靠）。
                        try
                        {
                            var il2cppLines = new Il2CppSystem.Collections.Generic.List<string>();
                            foreach (var s in lines)
                            {
                                try { il2cppLines.Add(s ?? ""); } catch { }
                            }
                            // SubmitLines 反射调用：Il2Cpp List 不实现 Il2Cpp IEnumerable
                            // （interop 类型系统裁剪），反射 Invoke 会类型转换失败（已知）——
                            // 打字机通常被任务状态机自己触发打印（内容一致），此路径仅作补充。
                            // 独立 try-catch，不阻断后续 TryStart。
                            try
                            {
                                var m = typeof(Teleprinter).GetMethods()
                                    .FirstOrDefault(x => x.Name == "SubmitLines" && x.GetParameters().Length == 4);
                                if (m != null)
                                    m.Invoke(tp, new object[] { "", il2cppLines, null, false });
                            }
                            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] apply print: {ex.Message}"); }
                            // 启动打印动画：SubmitLines 排队后需 TryStart 才启动逐字打印
                            // （revealed 逐字增加 + 打字针敲击）。打字机可能已被任务触发打印。
                            try { tp.TryStart(true); } catch { }
                            // 诊断：打字机打印动画是否启动（降频）
                            try
                            {
                                if ((_applyDiag % 10) == 7)
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
                        if (tp == null) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] apply append 但打字机 null ptype={ptype}"); return; }
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
                        if (tp == null) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] apply state 但打字机 null ptype={ptype}"); return; }
                        // 打字机是否正在打印（EvPrint 触发的逐字打印动画）：
                        // 打印中**停掉客机打字机自身逐字协程**（避免客机旧打印队列覆盖 EvState 同步的新内容），
                        // reveal/mask/打字针均由下方 EvState 按主机进度/状态驱动 → 打印中内容+动画同步。
                        bool printing = false;
                        try { printing = tp.IsPrinting; } catch { }
                        // 打印中：停掉客机打字机自身逐字协程（reveal 由下方 EvState 按主机进度驱动）——
                        // 否则客机打字机按**自己的旧打印队列**逐字揭示（客机队列是任务更新前的内容）
                        // → 动画中内容不同步（任务更新继续打印时显示旧文本，打印完才同步）。
                        // 打字针敲击由 animator 驱动（typingBool 由下方 animTyping 跟随主机）。
                        if (printing)
                        {
                            try { tp.DrainAllJobsInstant(); } catch { }
                            try
                            {
                                var runner = tp._runner;
                                if (runner != null) { try { tp.StopCoroutine(runner); } catch { } }
                                try { tp._runner = null; } catch { }
                            }
                            catch { }
                        }
                        // 内容同步（始终设 _currentFullRich + _tmp.text = 主机内容——**打印中也设**，
                        // 否则客机打字机显示的是客机任务状态机打印的内容，可能与主机不同 → 动画中内容不同步；
                        // 动画结束后 EvState 才对齐 → 用户看到"内容同步发生在动画后"）。
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
                                    textProp.SetValue(tmpObj, rich);
                                    tmpSet = true;
                                    if ((_applyDiag % 10) == 3)
                                        CoopRuntime.LogSource?.LogInfo($"[Teleprinter] set tmp.text ok (obj={tmpObj.GetType().Name})");
                                }
                            }
                            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] set tmp.text: {ex.Message}"); }
                        }
                        if (!tmpSet)
                            CoopRuntime.LogSource?.LogWarning("[Teleprinter] 未能设置显示文本（_tmp 反射 + 子物体均失败）");
                        // 1) 揭示字符数对齐主机（只增不减：打字机可能已揭示更多，不回退 → 文本不会倒退）
                        int curRev = 0;
                        try { curRev = tp._currentRevealedCharIndex; } catch { }
                        int targetRev = Math.Max(curRev, revealed);
                        try { tp._currentRevealedCharIndex = targetRev; } catch { }
                        // 2) 重建逐字揭示遮罩（前 targetRev 个 true，打字动画显示"打字进度"）
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
                        if ((++_applyDiag % 10) == 1)
                            CoopRuntime.LogSource?.LogInfo($"[Teleprinter] applied state ptype={ptype} printing={printing} revealed={targetRev} animTyping={animTyping} paper=({px:0.00},{py:0.00},{pz:0.00}) rich='{Truncate(rich)}'");
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

    private static string Truncate(string s, int max = 40)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { }
}
