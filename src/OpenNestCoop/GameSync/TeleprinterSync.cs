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
    public int MsgType => 134;

    // ⚠️ 模块自注册：程序集加载时入队（V1 方案），Startup FlushPending 统一注册
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SelfRegister()
    {
        CoopSyncRegistry.PendingRegister(false, () => new TeleprinterSync());
        // ⚠️ 2026-09-04 中途加入快照：客机加入时主机开局 EvPrint 可能已发完（错过）+ EvState 无变化不广播
        // → 客机开局简报永不同步（偶发）。注册快照：新成员加入时收到主机当前打字机文本。
        CoopSyncRegistry.PendingRegister(false, () => StateSnapshotSync.Register("teleprinter", BuildTeleprinterSnapshot, ApplyTeleprinterSnapshot));
    }
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
    private static int _evDiag;      // EvPrint 行内容诊断降频（空行/去重确认）
    /// <summary>主机上次广播的打印行签名（ptype → lines 拼接）。2026-08-31 主机去重：游戏任务图重复触发
    /// SubmitLines（同内容）→ 不再重复广播（否则客机 EvPrint 应用多次 → 打字机累积多换行）。</summary>
    private static readonly System.Collections.Generic.Dictionary<byte, string> _lastPrintSig = new();
    /// <summary>客机上次已应用的打印行签名（ptype → joined）。2026-09-05 去重修复：旧实现用
    /// `tp._currentFullRich == joined` 判断"已打印"——但 _currentFullRich 是**完整累积文本**（EvState 维护），
    /// 而 joined 是**本次新增行**（EvPrint 内容），两者语义不同 → 永远不相等 → 每次 EvPrint 都 SubmitLines
    /// 排队 → 累积重复打印（"客机左打字机重复打字"根因之一）。改用独立字典记录已应用签名。</summary>
    private static readonly System.Collections.Generic.Dictionary<byte, string> _lastAppliedPrintSig = new();

    // 状态同步：打字机类型 -> 最近一次完整富文本（检测变化）
    private readonly System.Collections.Generic.Dictionary<byte, string> _lastRich = new();
    /// <summary>⚠️ 2026-09-12 开局偶发不同步修复（机制）：收到打字机事件时打字机对象**还没注册**
    /// （`FindPrinter`→`Teleprinter.GetTeleprinter` 返回 null，任务场景加载中/加入时序）→ 旧实现
    /// 只打 warning 直接 return **丢弃**；而 EvState 只在**文本变化**时广播（主机 `_lastRich` 已更新）
    /// → 主机不会再发 → 客机开局简报**永久不同步**（偶发 = 完全取决于加入/场景加载时序）。
    /// 修法：丢弃时按 ev+ptype 暂存**原始包字节**（只留最新一份），Tick 里每 0.25s 重试直到
    /// 打字机出现后真正应用——纯本地重试，不增加任何网络流量。</summary>
    private static readonly System.Collections.Generic.Dictionary<int, PendingTpEvent> _pendingTp = new();
    private float _retryTimer;
    /// <summary>⚠️ 2026-09-12 客机"卡在打印中"修复（实测日志：主机 isPrinting=False 而客机 printed=True
    /// 持续 2 分钟）：客机自己的打字机若 `IsPrinting` 长期为 true（协程卡住/标记残留），
    /// 旧实现 `keepLocalAnimation=true` 会**永远跳过**主机最终态（revealed/揭示遮罩/纸张位置/敲击状态）
    /// → 客机揭示进度与纸张位置与主机不同步。这里记录每台打字机"上次揭示数变化时刻"，
    /// 超过 StallSeconds 没变化 → 判定本地打印卡死 → 强制走最终态同步。</summary>
    private static readonly System.Collections.Generic.Dictionary<byte, int> _revLast = new();
    private static readonly System.Collections.Generic.Dictionary<byte, float> _revStamp = new();
    private const float StallSeconds = 2f;
    private sealed class PendingTpEvent
    {
        public byte Ev;
        public byte Ptype;
        public byte[] Raw;
        public int Attempts;
    }
    private float _stateTimer;
    private const float StateInterval = 0.5f;
    // ⚠️ 打印中高频广播间隔：打字机打印中 revealed 逐字增加，若用 0.5s 扫描会漏掉中间态
    //（短文本 <0.5s 打印完）→ 客机只收到最终态 → 无逐字动画。打印中缩到 0.1s 捕获中间 reveal 序列。
    private const float StateIntervalPrinting = 0.1f;
    private bool _anyPrinting;          // 是否有打字机正在打印（决定高频广播）
    private float _printingCheckTimer;  // 打印状态检查降频（0.25s）
    // ⚠️ 实例缓存（2026-08-25 帧性能）：FindObjectsOfType 全场景扫描很贵（曾占 ~95ms/s）——低频刷新（3s + 场景切换）
    private Teleprinter[] _printerCache;
    private float _cacheTimer;
    private int _cacheScene = -1;
    private const float CacheRefreshSec = 3f;

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
            // ⚠️ 2026-08-31 主机去重：游戏任务图可能对同一内容重复触发 SubmitLines（日志：客机同一 EvPrint
            // 应用 5 次 → 打字机多换行/累积）。相同 ptype + 相同行内容不重复广播（EvState 状态同步兜底对齐）。
            string sig = string.Join("\n", lines);
            if (_lastPrintSig.TryGetValue(ptype, out var lastSig) && lastSig == sig) return;
            _lastPrintSig[ptype] = sig;
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
            CoopLog.Debug("Teleprinter.localEv", () => $"[Teleprinter] local ev={(w.Data.Length > 1 ? w.Data[1] : (byte)0)} isHost={net.IsHost}");
    }

    // ---------------- 网络包 ----------------

    /// <summary>场景实例缓存刷新：FindObjectsOfType 每 CacheRefreshSec 或场景切换才扫一次（省全场景扫描）。</summary>
    private void EnsureCache(float dt)
    {
        int sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
        _cacheTimer -= dt;
        if (_printerCache == null || sc != _cacheScene || _cacheTimer <= 0f)
        {
            _cacheTimer = CacheRefreshSec;
            _cacheScene = sc;
            _printerCache = UnityEngine.Object.FindObjectsOfType<Teleprinter>(true);
        }
    }

    /// <summary>状态同步：定期扫描所有打字机的完整富文本（_currentFullRich），变化则广播。
    /// 打字机文本最终都落到 _currentFullRich（打印动画的完整目标文本），直接同步它最可靠——
    /// 不依赖 SubmitLines/AppendInstant 事件（那些 patch 可能因 IL2CPP 集合/反射问题失败）。</summary>
    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        // ⚠️ 2026-09-12 开局偶发不同步修复（见 _pendingTp 注释）：重试那些"收到时打字机对象还不存在"
        // 而暂存的事件。客户端也会跑（不能放在下面 `net.State != Hosting` 的早期返回之后）。
        _retryTimer += dt;
        if (_retryTimer >= 0.25f)
        {
            _retryTimer = 0f;
            RetryPendingTeleprinter();
        }
        // ⚠️ 2026-09-05 移除快照延迟重发（RequestSnapshot）：它每 5s 触发主机重发**所有模块**快照
        // （entity/maptoken/button 等），覆盖客机已同步状态 + 快照应用卡顿（"客机一卡一卡 + 同步有问题"）。
        // 打字机开局兜底由 EvPrint（SubmitLines 事件）+ EvState（0.1~0.5s 状态扫描，文本变化即广播）负责，
        // 无需快照重发。
        // 打印中高频扫描：任一打字机 IsPrinting 则用 0.1s 间隔捕获中间 reveal 序列，
        // 否则 0.5s 兜底。先扫一次判断（FindObjectsOfTypeAll 仅每帧一次判断成本可忽略，
        // 但避免重复扫描——用轻量标志缓存）。
        bool anyPrinting = _anyPrinting;
        if (!_anyPrinting || _printingCheckTimer <= 0f)
        {
            try
            {
                anyPrinting = false;
                EnsureCache(dt);
                var allP = _printerCache;
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
            EnsureCache(dt);
            var all = _printerCache;
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
                        CoopLog.Debug("Teleprinter.state", () => $"[Teleprinter] state ptype={ptype}{diag} rich='{Truncate(rich)}'");
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
                // ⚠️ 2026-09-12：主机行游标/行数一并下发——客机“强制同步最终态”时对齐它，
                // 修“后续新任务的换行/打字针起始位置错”（行游标是打字机内部状态，之前没同步）。
                try { w.Put(tp.CurrentLineCount); } catch { w.Put(0); }
                try { w.Put(tp._prevLineNum); } catch { w.Put(0); }
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
            Apply(ev, ptype, r, data);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"TeleprinterSync OnPacket: {ex.Message}"); }
    }

    private static void Apply(byte ev, byte ptype, NetDataReader r, byte[] raw)
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
                        if (tp == null) { StashTp(ev, ptype, raw, "print"); return; }
                        string joined = ""; // case 级作用域（供去重 + 诊断）
                        // 防重复打印（2026-09-05 重写）：用独立 _lastAppliedPrintSig 字典去重。
                        // ⚠️ 旧实现用 `tp._currentFullRich == joined` 判断"已打印"——但 _currentFullRich 是
                        // **完整累积文本**（EvState 维护），joined 是**本次新增行**，两者语义不同 → 永远
                        // 不相等 → 每次 EvPrint 都 SubmitLines 排队 → 累积重复（"客机左打字机重复打字"）。
                        // 相同内容（网络重发/转发重复）已应用过 → 跳过。
                        try
                        {
                            joined = string.Join("\n", lines).Trim();
                            string curRich = (tp._currentFullRich ?? "").Trim();
                            // ⚠️ 2026-08-26 空行诊断：打印本次行内容（含空行数）——用户"打字机异常空行"：
                            // 确认空行是内容自带（lines 含 "" 空行）还是累积（多份 EvPrint 叠加）。
                            if ((++_evDiag % 5) == 1)
                            {
                                try
                                {
                                    int empty = 0;
                                    for (int ei = 0; ei < lines.Count; ei++)
                                        if (string.IsNullOrEmpty(lines[ei])) empty++;
                                    string preview = joined.Length > 80 ? joined.Substring(0, 80) : joined;
                                    CoopRuntime.LogSource?.LogInfo($"[Teleprinter] EvPrint n={n} empty={empty} len={joined.Length} curRichLen={curRich.Length} dup={_lastAppliedPrintSig.TryGetValue(ptype, out var d) && d == joined} prev='{preview.Replace("\n", "\\n")}'");
                                }
                                catch { }
                            }
                            if (_lastAppliedPrintSig.TryGetValue(ptype, out var lastApplied) && lastApplied == joined)
                            {
                                CoopLog.Debug("Teleprinter.skipDup", () => $"[Teleprinter] skip dup print ptype={ptype} n={n}");
                                break;
                            }
                            _lastAppliedPrintSig[ptype] = joined;
                        }
                        catch { }
                        // interop 签名是 Il2CppSystem.Collections.Generic.IEnumerable<string>。
                        // ⚠️ 客机打字机动画/类型名根因与修复（2026-08-13，已确认正常）：
                        // 1) 无动画：旧实现反射 Invoke 传 Il2Cpp List → List→IEnumerable 运行时转换失败
                        //    → SubmitLines 从未成功 → 打字机协程不启动。修复：TryCast<IEnumerable<string>>()
                        //    运行时转换 + 直接调用 SubmitLines（编译期类型匹配）→ 协程启动逐字动画。
                        // 2) 类型名：根因在主机侧 PostTeleprinterPrint 用非泛型 IEnumerable 提取失败 →
                        //    ToString 类型名广播（已修，HarmonyPatches）。客机侧 Il2Cpp List Add(托管string)
                        //    会被装箱成 Object → 元素变类型名，故用 Il2CppSystem.String 隐式转换后 Add。
                        // ⚠️ 2026-09-12：本机这台已卡死（揭示数冻结 ≥StallSeconds）→ 先靶向复位，否则新任务
                        // 排在坏任务后面（用户实测：后续任务的打字针起始位置/动画状态/换行全错）。
                        try
                        {
                            int curRevPre = 0; bool revOkPre = false;
                            try { curRevPre = tp._currentRevealedCharIndex; revOkPre = true; } catch { }
                            if (revOkPre && tp.IsPrinting && _revLast.TryGetValue(ptype, out var lrPre) && lrPre == curRevPre
                                && _revStamp.TryGetValue(ptype, out var stPre) && UnityEngine.Time.time - stPre > StallSeconds)
                                ResetLocalRunState(tp, ptype, "pre-print");
                        }
                        catch { }
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
                                        // ⚠️ 2026-09-12：**无条件**用正确行覆盖 job.lines（旧实现在首行以
                                        // "Il2CppSystem." 开头时才修——但 TryCast 接口对象被游戏 ToString() 的
                                        // 结果是整个文本变成类型名，首行未必命中 → 动画中显示类型名/长度错）。
                                        try
                                        {
                                            var jl = job.lines;
                                            if (jl != null)
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
                            // ⚠️ 2026-09-12：新一次打印 → 重置卡死检测基准（下一帧揭示数从 0 开始前进）
                            try { _revLast.Remove(ptype); _revStamp[ptype] = UnityEngine.Time.time; } catch { }
                            // ⚠️ 2026-09-05 移除 `tp._currentFullRich = joined`：旧实现把 _currentFullRich
                            // 覆盖成**本次新增行**，而 EvState 又恢复**完整累积文本** → 底本反复切换
                            // （72 ↔ 1135）→ 打字机重复打印/文本错乱（"客机左打字机重复打字"根因）。
                            // _currentFullRich 应由 EvState 唯一维护（完整文本）；去重改用
                            // _lastAppliedPrintSig（见上方）。
                            // 诊断：打字机打印动画是否启动（每 2s 打一次——确认开局 print 应用后 isPrinting，
                            // "客机开局不打字"需确认 SubmitLines+TryStart 后动画是否真正启动）
                            try
                            {
                                CoopLog.Info("Teleprinter.afterPrint", () => $"[Teleprinter] after print ptype={ptype} isPrinting={tp.IsPrinting} revealed={tp._currentRevealedCharIndex} isRunning={tp._isRunning}", 2f);
                            }
                            catch { }
                        }
                        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] apply print: {ex.Message}"); }
                        CoopLog.Debug("Teleprinter.appliedPrint", () => $"[Teleprinter] applied print n={n} ptype={ptype}");
                        break;
                    }
                    case EvAppend:
                    {
                        bool prepend = r.GetByte() != 0;
                        string chunk = r.GetString();
                        if (tp == null) { StashTp(ev, ptype, raw, "append"); return; }
                        try { tp.AppendInstant(chunk ?? "", prepend); }
                        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] apply append: {ex.Message}"); }
                        CoopLog.Debug("Teleprinter.appliedAppend", () => $"[Teleprinter] applied append prepend={prepend} ptype={ptype} chunk='{Truncate(chunk)}'");
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
                        int hostLineCount = -1, hostPrevLine = -1;   // 主机行游标/行数（旧包缺字段=-1，不覆盖）
                        try
                        {
                            if (r.AvailableBytes >= 4) revealed = r.GetInt();
                            if (r.AvailableBytes >= 1) paperOk = r.GetByte() != 0;
                            if (r.AvailableBytes >= 12) { px = r.GetFloat(); py = r.GetFloat(); pz = r.GetFloat(); }
                            if (r.AvailableBytes >= 1) animTyping = r.GetByte() != 0;
                            if (r.AvailableBytes >= 4) hostLineCount = r.GetInt();
                            if (r.AvailableBytes >= 4) hostPrevLine = r.GetInt();
                        }
                        catch { }
                        if (tp == null) { StashTp(ev, ptype, raw, "state"); return; }
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
                        // ⚠️ 2026-09-12（三修）：判据改为“**本机揭示数是否真的在前进**”：
                        //  - 在前进（健康本地动画）→ 不写状态，让本机协程自己逐字打（平滑）；
                        //  - 不前进（协程已死/卡住，而 `IsPrinting` 仍为 true——实测右侧那台就是这样）→
                        //    不再当作“动画中”，改由主机状态驱动（每个 EvState 写一次文本/揭示数/遮罩/纸张）
                        //    → 那台也会“跟着主机逐字显示”，不会再一直空白。
                        bool animating = false;
                        if (printing)
                        {
                            int curRevNow = 0;
                            bool revOk = false;
                            try { curRevNow = tp._currentRevealedCharIndex; revOk = true; } catch { }
                            if (!revOk) animating = true; // 读不到就不干预
                            else if (_revLast.TryGetValue(ptype, out var lastRev) && lastRev == curRevNow)
                            {
                                animating = false; // 与上次相同 = 没前进 → 本地动画已死
                                float nowD = 0f;
                                try { nowD = UnityEngine.Time.time; } catch { }
                                if (_revStamp.TryGetValue(ptype, out var stD) && nowD - stD > StallSeconds)
                                    LogTpStuck(tp, ptype, "dead-anim");
                            }
                            else
                            {
                                animating = true;
                                _revLast[ptype] = curRevNow;
                                try { _revStamp[ptype] = UnityEngine.Time.time; } catch { }
                            }
                        }
                        else
                        {
                            _revLast.Remove(ptype);
                            _revStamp.Remove(ptype);
                        }
                        bool keepLocalAnimation = animating;
                        // ⚠️ 2026-09-04 中途新文本无动画修复：空闲（非打印）+ 文本变化（含开局无旧文本）→
                        // 用 SubmitLines + TryStart 触发逐字动画（否则下面直接设 tmp.text → 文本瞬间出现无动画）。
                        // ⚠️ 2026-09-05 修正：**只有主机"还在打字"（animTyping=true）时才触发 fallback**。
                        // 主机已打印完（animTyping=false，如中途加入/OnLateJoin 强制广播时 revealed 已是最终态）→
                        // 不触发 fallback → keepLocalAnimation 保持 false → 走下方 revealed/_revealMask 完整状态
                        // 同步。旧实现无条件 fallback → keepLocalAnimation=true → 跳过 revealed/mask → 客机从头
                        // 逐字打印，但纸张已同步到底部 → 打字针/纸张/揭示进度错位（"保底只同步内容没同步动画
                        // 机构"根因）。
                        string oldRichForAnim = "";
                        try { oldRichForAnim = tp._currentFullRich ?? ""; } catch { }
                        if (!printing && !string.IsNullOrEmpty(rich) && oldRichForAnim != rich && animTyping)
                        {
                            try
                            {
                                var animLines = rich.Split('\n');
                                var il2cppAnim = new Il2CppSystem.Collections.Generic.List<string>();
                                foreach (var s in animLines)
                                {
                                    try { Il2CppSystem.String ilstr = s ?? ""; il2cppAnim.Add(ilstr); } catch { }
                                }
                                var animVal = ((Il2CppObjectBase)il2cppAnim)
                                    .TryCast<Il2CppSystem.Collections.Generic.IEnumerable<string>>();
                                if (animVal != null)
                                {
                                    try { tp.SubmitLines("", animVal, null, false); } catch { }
                                    try { tp.TryStart(true); } catch { }
                                    keepLocalAnimation = true; // 逐字动画已由 SubmitLines 协程启动
                                    CoopLog.Info("Teleprinter.animFallback", () => $"[Teleprinter] state→SubmitLines anim fallback ptype={ptype} richLen={rich.Length}", 2f);
                                }
                            }
                            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] anim fallback: {ex.Message}"); }
                        }
                        // ⚠️ 2026-09-12 关键：本地协程正在逐字打印（keepLocalAnimation）→ **一律不写**打字机内部
                        // 状态/视觉（底本/文本/揭示数/遮罩/纸张/打字针）。
                        // 实测根因：旧实现每 0.1s 覆写 `_currentFullRich`/`_tmp.text`/`revealed`/`_revealMask`/纸张，
                        // 与本地协程**互相打架** → 协程冻在半路（STUCK 日志：rev=714 冻结、主机已 900+）
                        // → 之后被“强制同步最终态”兜掉 → 后续任务的打字针/换行/动画状态全错。
                        // 只在空闲（未在本地动画）时才写完整最终态；卡死时走上方的“重起打印”路径。
                        if (keepLocalAnimation)
                        {
                            if ((++_applyDiag % 10) == 1)
                                CoopLog.Debug("Teleprinter.appliedState", () => $"[Teleprinter] applied state ptype={ptype} keepAnim=True（本地动画中，不写状态）");
                            break;
                        }
                        // （非动画态）设底本 + 显示文本：底本必须与显示文本一致（否则动画中会显示
                        // TryCast 类型名 / 未揭示部分错位）。
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
                                    // ⚠️ 2026-08-26 打字机空白修复：**打印中也设 _tmp.text = 主机内容**。
                                    // 之前改成"打印中（keepLocalAnimation）不设 _tmp.text"（为避免打字针/文本
                                    // 偏移）——但客机打字机若 IsPrinting=true 而协程没真正揭示（reveal 卡住），
                                    // tmp.text 一直不设 → 打字机空白（"客机打字机有一台空白"根因）。
                                    // 打字针/文本偏移的真正根因是**累积打印多份**（revealed 909 vs 单份 463）——
                                    // 已由 EvPrint 去重（_currentFullRich = joined）解决；累积消除后单份
                                    // tmp.text 与打字机 reveal 匹配（揭示部分可见、未揭示隐藏），不偏移。
                                    // 打印中设 tmp.text 不破坏动画（reveal/mask 由打字机协程逐字驱动）。
                                    textProp.SetValue(tmpObj, rich);
                                    tmpSet = true;
                                    if ((_applyDiag % 10) == 3)
                                        CoopLog.Debug("Teleprinter.tmpText", () => $"[Teleprinter] set tmp.text ok (obj={tmpObj.GetType().Name}) keepAnim={keepLocalAnimation} len={rich.Length}");
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
                            // ⚠️ 2026-09-12：**换新文本**（底本不同）→ 揭示数必须跟随主机（从 0 重新逐字），
                            // 否则 max(旧值, 新值) 会把新文本一上来就整段显示（无动画）。
                            targetRev = (oldRichForAnim != rich) ? revealed : Math.Max(curRev, revealed);
                            try { tp._currentRevealedCharIndex = targetRev; } catch { }
                            // ⚠️ 2026-09-12：写下揭示数后同步更新基准——本机协程已死时，本模块就是“动画驱动者”，
                            // 下一个状态包应继续跟随（否则会写一次跳一次，跟随变 5Hz 抖动）。
                            try { _revLast[ptype] = targetRev; } catch { }
                            // 行游标/行数对齐主机（否则后续新任务的换行/打字针起始位置会错——用户实测）：
                            // 这两个是打字机内部的“行计数”，只靠文本/揭示数/纸张位置无法恢复。
                            try { if (hostPrevLine >= 0) tp._prevLineNum = hostPrevLine; } catch { }
                            try { if (hostLineCount >= 0) tp._CurrentLineCount_k__BackingField = hostLineCount; } catch { }
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
                            CoopLog.Debug("Teleprinter.appliedState", () => $"[Teleprinter] applied state ptype={ptype} printing={printing} keepAnim={keepLocalAnimation} revealed={targetRev} animTyping={animTyping} paper=({px:0.00},{py:0.00},{pz:0.00}) rich='{Truncate(rich)}'");
                        break;
                    }
                    case EvClearAll:
                        if (tp != null) { try { tp.ClearAll(); } catch { } }
                        CoopLog.Debug("Teleprinter.clearAll", () => $"[Teleprinter] applied clear-all ptype={ptype}");
                        break;
                    case EvClearAlarm:
                        if (tp != null) { try { tp.ClearAlarm(); } catch { } }
                        CoopLog.Debug("Teleprinter.clearAlarm", () => $"[Teleprinter] applied clear-alarm ptype={ptype}");
                        break;
                }
            }
            finally { IsApplying = false; }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"TeleprinterSync Apply: {ex.Message}"); }
    }

    /// <summary>用主机文本在本机重新起一次“逐字打印”（用于卡死自愈）。
    /// 用户症状“右侧空白、没有任何动画”——只清状态不够，必须重起一次打印才有动画。</summary>
    private static bool TryRestartPrint(Teleprinter tp, byte ptype, string rich)
    {
        if (tp == null || string.IsNullOrEmpty(rich)) return false;
        try
        {
            var lines = rich.Split('\n');
            var il = new Il2CppSystem.Collections.Generic.List<string>();
            foreach (var s in lines) { try { Il2CppSystem.String ilstr = s ?? ""; il.Add(ilstr); } catch { } }
            var val = ((Il2CppObjectBase)il).TryCast<Il2CppSystem.Collections.Generic.IEnumerable<string>>();
            if (val == null) return false;
            var job = tp.SubmitLines("", val, null, false);
            try
            {
                if (job != null && job.lines != null)
                {
                    job.lines.Clear();
                    foreach (var s in lines) { Il2CppSystem.String ilstr = s ?? ""; job.lines.Add(ilstr); }
                }
            }
            catch { }
            try { tp.TryStart(true); } catch { }
            _revLast.Remove(ptype);
            _revStamp[ptype] = UnityEngine.Time.time;
            CoopLog.Info("Teleprinter.restart", () => $"[Teleprinter] restart print ptype={ptype} len={rich.Length} lines={lines.Length}", 1f);
            return true;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] restart print: {ex.Message}"); return false; }
    }

    /// <summary>定向复位本地打字机的**运行状态**（不用游戏自带 ForceCompleteAll/DrainAllJobsInstant）：
    /// 停掉本机卡住的打印协程 + 清空积压任务队列 + `_isRunning=false`。
    /// ⚠️ 只用严格卡死门槛调用（本机揭示数冻结 + 主机已超过本机）；健康动画不会被碰到。</summary>
    private static void ResetLocalRunState(Teleprinter tp, byte ptype, string why)
    {
        if (tp == null) return;
        try { var r = tp._runner; if (r != null) tp.StopCoroutine(r); } catch { }
        try { tp._runner = null; } catch { }
        try { tp._isRunning = false; } catch { }
        try { if (tp._pendingJobs != null) tp._pendingJobs.Clear(); } catch { }
        _revLast.Remove(ptype);
        _revStamp.Remove(ptype);
        CoopLog.Info("Teleprinter.reset", () => $"[Teleprinter] local run-state reset ({why}) ptype={ptype}", 1f);
    }

    /// <summary>记录“这台打字机卡死了”的完整内部状态（**只诊断、不改**）。
    /// ⚠️ 2026-09-12 二修：旧版这里调 `ForceCompleteAll()`/`DrainAllJobsInstant()` 想“干净收尾”，
    /// 实测**彻底破坏打字动画**（后续打印全部不再逐字）——已改回纯记录。
    /// 诊断字段（供下一轮定位“强制同步不完全”到底漏了哪个内部状态）：任务队列/协程/行游标/纸张基线。</summary>
    private static void LogTpStuck(Teleprinter tp, byte ptype, string why)
    {
        if (tp == null) return;
        string diag = "";
        try { diag += $" isRunning={tp._isRunning}"; } catch { }
        try { diag += $" hasJobs={tp.HasJobs}"; } catch { }
        try { diag += $" printing={tp.IsPrinting}"; } catch { }
        try { diag += $" lineCount={tp.CurrentLineCount}"; } catch { }
        try { diag += $" prevLine={tp._prevLineNum}"; } catch { }
        try { diag += $" rev={tp._currentRevealedCharIndex}"; } catch { }
        try { diag += $" fullRichLen={(tp._currentFullRich ?? "").Length}"; } catch { }
        try { diag += $" maskCount={(tp._revealMask == null ? -1 : tp._revealMask.Count)}"; } catch { }
        try { diag += $" baselineSet={tp._baselineSet} baselineY={tp._baselineWorldY:0.00}"; } catch { }
        try { diag += $" animTyping={tp._animTypingState}"; } catch { }
        CoopLog.Info("Teleprinter.stuck", () => $"[Teleprinter] STUCK ({why}) ptype={ptype}{diag}", 5f);
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

    /// <summary>⚠️ 2026-09-12 开局偶发不同步修复：把"打字机对象还不存在"的事件暂存（只留最新一份）。</summary>
    private static void StashTp(byte ev, byte ptype, byte[] raw, string what)
    {
        try
        {
            if (raw == null) return;
            CoopRuntime.LogSource?.LogWarning($"[Teleprinter] apply {what} but printer null ptype={ptype} → 暂存待打字机注册后重试");
            _pendingTp[ev * 256 + ptype] = new PendingTpEvent { Ev = ev, Ptype = ptype, Raw = raw };
        }
        catch { }
    }

    /// <summary>⚠️ 2026-09-12：重试暂存的打字机事件（打字机注册后真正应用）。
    /// 上限 240 次×0.25s = 60s（场景一直没打字机就放弃，避免无界堆积）。</summary>
    private static void RetryPendingTeleprinter()
    {
        if (_pendingTp.Count == 0) return;
        System.Collections.Generic.List<int> drop = null;
        foreach (var kv in _pendingTp)
        {
            var pe = kv.Value;
            if (pe == null || pe.Raw == null) { (drop ??= new System.Collections.Generic.List<int>()).Add(kv.Key); continue; }
            bool found;
            try { found = FindPrinter(pe.Ptype) != null; } catch { found = false; }
            if (!found)
            {
                if (++pe.Attempts > 240)
                {
                    CoopRuntime.LogSource?.LogWarning($"[Teleprinter] 暂存事件超时放弃 ev={pe.Ev} ptype={pe.Ptype}");
                    (drop ??= new System.Collections.Generic.List<int>()).Add(kv.Key);
                }
                continue;
            }
            (drop ??= new System.Collections.Generic.List<int>()).Add(kv.Key);
            try
            {
                var rr = new NetDataReader(pe.Raw);
                rr.GetByte(); // 消息类型
                byte ev = rr.GetByte();
                byte pt = rr.GetByte();
                CoopRuntime.LogSource?.LogInfo($"[Teleprinter] retry stashed ev={ev} ptype={pt}（打字机已注册）");
                Apply(ev, pt, rr, pe.Raw);
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Teleprinter] retry apply: {ex.Message}"); }
        }
        if (drop != null)
            foreach (var k in drop) _pendingTp.Remove(k);
    }

    private static string Truncate(string s, int max = 240)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    // ---------------- 中途加入快照（StateSnapshotSync "teleprinter"） ----------------

    /// <summary>中途加入：主机读所有打字机的完整富文本（_currentFullRich）打包。</summary>
    private static byte[] BuildTeleprinterSnapshot()
    {
        try
        {
            var printers = UnityEngine.Object.FindObjectsOfType<Teleprinter>(true);
            if (printers == null || printers.Length == 0) return null;
            var w = NetProtocol.Begin((MsgType)MsgTypeId);
            w.Put((byte)printers.Length);
            foreach (var tp in printers)
            {
                if (tp == null) continue;
                byte ptype = 0;
                try { ptype = (byte)(int)tp.TeleprinterType; } catch { }
                string rich = "";
                try { rich = tp._currentFullRich ?? ""; } catch { }
                w.Put(ptype);
                w.Put(rich);
            }
            return NetProtocol.Snapshot(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"TeleprinterSync BuildSnapshot: {ex.Message}"); return null; }
    }

    /// <summary>中途加入：新成员应用打字机文本（设 _currentFullRich + 显示文本，开局简报同步，无需逐字动画）。</summary>
    private static void ApplyTeleprinterSnapshot(byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过类型
            int n = r.GetByte();
            IsApplying = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    byte ptype = r.GetByte();
                    string rich = r.GetString();
                    var tp = FindPrinter(ptype);
                    if (tp == null) continue;
                    try { tp._currentFullRich = rich; } catch { }
                    var tmpObj = GetTmpText(tp);
                    if (tmpObj != null)
                    {
                        try
                        {
                            var textProp = tmpObj.GetType().GetProperty("text");
                            if (textProp != null) textProp.SetValue(tmpObj, rich);
                        }
                        catch { }
                    }
                    CoopLog.Info("Teleprinter.snapshot", () => $"[Teleprinter] snapshot applied ptype={ptype} len={rich?.Length ?? 0}", 2f);
                }
            }
            finally { IsApplying = false; }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"TeleprinterSync ApplySnapshot: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _lastRich.Clear(); _pendingTp.Clear(); _retryTimer = 0f; _revLast.Clear(); _revStamp.Clear(); }

    /// <summary>⚠️ 2026-09-05 新成员加入：重置 _lastRich → 下次 Tick（0.1~0.5s）强制广播当前打字机文本。
    /// 修复"开局偶发无同步"：快照 len=0（新成员加入时主机文本未生成）+ EvState 只在文本变化时广播 →
    /// 新成员加入后主机文本若已稳定（无变化）则永远不广播 → 客机开局简报缺失。</summary>
    public void OnLateJoin(ulong steamId)
    {
        try { _lastRich.Clear(); } catch { }
    }
}
