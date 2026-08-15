using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 按钮/拉杆点击同步（LookAtTarget，MsgType=118）。
/// 统一入口：patch LookAtTarget.OnClickDown（Harmony）——这是所有交互按钮/拉杆点击的
/// 唯一入口（点击效果：拉杆动画 AnimatorBoolToggler + onClickDown UnityEvent 绑定逻辑
/// 如 OnLoadChargesPressed/OnChargeButtonPressed + 状态推进 全部由 OnClickDown 内部触发）。
/// 本地点击 → 广播；接收端 OnClickDown+OnClickUp 模拟点击 → 完整复现（含视觉+逻辑）。
/// 按名称关键词筛选，避免同步无关按钮。isClicked 轮询检测不到按住型拉杆，故用 OnClickDown patch。
/// </summary>
public sealed class ButtonClickSync : ISyncedModule
{
    public byte MsgType => 118;
    private static readonly string[] Keywords =
        { "Lever", "Rammer", "Hatch", "Primer", "Confirm", "Breech",
          "Power", "Reset", "Delete", "Measure", "Kill", "Lock", "Elevation", "Range", "Damage",
          "Link", "Firing", "Trigger", "Launch", "Lanyard", "Fire", "Safety", "Safty", "Switch", "Arm",
          "Starter", "Crank",
          "Light", "Lighting", "Notification",
          // 战争警报汽笛（War Horn 路径的 universal button）：任务中警报/汽笛按钮需同步
          "Horn", "Siren",
          // 弹舱/装药（切弹=Universal Button Move Cylinder、装药量=Button Dispencer、上弹=Load shell Rammer）
          "Cylinder", "Move", "Dispenc", "Dispenser", "Load",
          // 征信点补给台（Requisition/Universal Button 拉杆：插卡后拉杆购买）
          "Requisition", "Punchcard", "征用",
          // Map Table 弹道轨迹显示（Map Table_Shell Trajectory display (1)）：点击锁定弹道目标需同步
          "Trajectory" };
    private int _log;
    private int _diagCount;
    private static int _clickDiag;
    private static int _notifDiag;

    /// <summary>待应用点击队列：对端收到点击但目标冷却/未就绪时延迟到就绪再应用（快速操作不吞事件）。
    /// 携带 toggle 型按钮的最终 bool 状态（应用点击后 SetBool 权威对齐）。</summary>
    private sealed class PendingClick { public string Id; public float ReadyAt; public List<bool> TogglerStates; }
    private static readonly List<PendingClick> _pendingClicks = new();
    private const float PendingTimeout = 3f;

    // ---- toggle 状态轮询（楼梯盖板手柄等"瞬时拉杆"回弹后最终状态对齐）----
    /// <summary>附加消息类型：toggle 型按钮的最终状态轮询（MsgType=135，区分点击事件 118）。
    /// 楼梯盖板手柄是"点击→动画开→回弹"的瞬时拉杆，点击事件复现后动画可能因同帧 OnClickDown+OnClickUp
    /// 被跳过而卡在"开"；状态轮询定期广播手柄回弹后的最终 bool，对端 SetBool 校正。</summary>
    public const byte ToggleStateMsgType = 135;
    private const float StateInterval = 0.8f; // 0.8s（原 0.4s，降低高频 FindObjectsOfType 扫描 CPU）
    private float _stateTimer;
    // A2: LookAtTarget 实例缓存。按钮基本是场景静态对象，缓存 + 5s 定时刷新 + 场景切换即时刷新，
    // 避免每 0.8s 一次 FindObjectsOfType<LookAtTarget>(true) 的全场景扫描卡顿。已销毁对象为 Unity fake null，遍历时过滤。
    private static LookAtTarget[] _targetCache;
    private static float _cacheTimer;
    private static int _lastSceneIdx = int.MinValue;
    /// <summary>按钮 id -> 最近一次 toggler 状态签名（检测变化才广播）。static：ApplyClick(static) 复现点击后
    /// 也要更新防环签名（否则轮询把点击后的状态广播回去 → 两端反复切换）。ButtonClickSync 是单例，static 安全。</summary>
    private static readonly Dictionary<string, string> _lastToggleSig = new();

    /// <summary>当前正在复现的远端点击 target（ApplyClick 调 OnClickDown 期间置位）。
    /// 对象级防环：只抑制同一个 target 的 OnClickDown 再转发（防环），
    /// 玩家此时点击其他拉杆（如不同装药拉杆）**不被吞**——快速连续操作不再丢事件。
    /// ⚠️ 不能用全局 bool：复现点击 A 期间，玩家真实点击 B 会被误吞（装药吞事件根因）。</summary>
    public static LookAtTarget ApplyingTarget;
    /// <summary>是否正在复现远端点击（ReloadSync 据此去重：ApplyClick 期间不转发 powder/推进）。</summary>
    public static bool IsApplyingClick => ApplyingTarget != null;
    /// <summary>最近一次本地点击广播时间（ReloadSync 据此去重：LookAtTarget 拉杆的推进已随点击转发）。</summary>
    public static float LastClickAt;

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        // toggle 状态轮询：定期读被跟踪 toggle 型按钮的最终状态，变化广播（对端 SetBool 对齐）。
        // 楼梯盖板手柄等"瞬时拉杆"点击后动画回弹，点击事件复现可能卡在"开"，轮询校正最终状态。
        try { PollToggleStates(net); }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ButtonClickSync PollToggle: {ex.Message}"); }
        // 处理待应用点击队列：冷却就绪后应用（快速连续操作不吞事件）
        if (_pendingClicks.Count > 0)
        {
            for (int i = _pendingClicks.Count - 1; i >= 0; i--)
            {
                var p = _pendingClicks[i];
                float now = Time.realtimeSinceStartup;
                if (now >= p.ReadyAt)
                {
                    _pendingClicks.RemoveAt(i);
                    ApplyClick(p.Id, p.TogglerStates);
                }
                else if (now - p.ReadyAt > PendingTimeout)
                {
                    // 超时：若同 id 有更新的待应用点击则丢弃旧的（保留最新，避免过期点击误触发状态开关）；
                    // 否则也丢弃（防堆积，避免 Switch 状态开关被过期点击反转）
                    _pendingClicks.RemoveAt(i);
                    CoopLog.Debug("ButtonClickSync.dropTimeout", () => $"[ButtonClickSync] click dropped (timeout) '{p.Id}'");
                }
            }
        }
        if ((++_log % 300) == 1)
        {
            // 打印当前被跟踪按钮名称（用活的 scan targets，确认 Charge Rammer / Move Cylinder 等）
            try
            {
                var targets = UnityEngine.Object.FindObjectsOfType<LookAtTarget>(true);
                if (targets == null) return;
                string names = "";
                string untrackedUniversal = "";
                int i = 0;
                foreach (var t in targets)
                {
                    if (t == null) continue;
                    string nm = "?";
                    try { if (t.gameObject != null) nm = t.gameObject.name; } catch { }
                    bool tracked = ShouldTrack(t);
                    if (tracked)
                    {
                        if (i < 30)
                        {
                            names += (names.Length > 0 ? "|" : "") + nm;
                            i++;
                        }
                    }
                    else if (nm.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0
                             || nm.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // 诊断：未被跟踪的按钮（可能漏同步，如 War Hor 路径的 universal button）
                        string pth = "";
                        try { pth = PathOf(t.transform) ?? ""; } catch { }
                        untrackedUniversal += (untrackedUniversal.Length > 0 ? " | " : "") + pth;
                    }
                }
                _diagCount = i;
                CoopLog.Debug("ButtonClickSync.tracked", () => $"[ButtonClickSync] tracked total={_diagCount} names=[{names}]", 5f);
                if (untrackedUniversal.Length > 0)
                    CoopLog.Debug("ButtonClickSync.untracked", () => $"[ButtonClickSync] UNTRACKED universal/button paths=[{untrackedUniversal}]", 5f);
            }
            catch { }
        }
    }

    /// <summary>打字机通知指示灯（Notification Light / hanging round lamp / Message Notifications 下）：
    /// 状态由主机打字机/任务图权威驱动，只有主机广播，客机只接收应用（不争抢）。</summary>
    private static bool IsNotificationLight(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        // 只匹配打字机通知灯（Notification Light / Message Notifications 下的按钮）。
        // ⚠️ 不匹配 "hanging round lamp"（那是所有吊灯的公共子路径，含普通吊灯 Hanging Light /
        // Table lamp——玩家可交互，应正常双向同步，误当指示灯会破坏吊灯同步）。
        return id.IndexOf("Notification Light", StringComparison.OrdinalIgnoreCase) >= 0
            || id.IndexOf("Message Notifications", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>A2: 取 LookAtTarget 实例缓存。场景切换即时刷新；定时（5s）兜底捕捉动态生成/销毁；
    /// 未初始化首次扫描。已销毁对象为 Unity fake null，调用方遍历时过滤。</summary>
    private static LookAtTarget[] GetTargets()
    {
        int sceneIdx = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
        if (sceneIdx != _lastSceneIdx)
        {
            _lastSceneIdx = sceneIdx;
            return RefreshTargetCache();
        }
        _cacheTimer += Time.deltaTime;
        if (_cacheTimer >= 5f || _targetCache == null)
        {
            _cacheTimer = 0f;
            return RefreshTargetCache();
        }
        return _targetCache;
    }

    private static LookAtTarget[] RefreshTargetCache()
    {
        _targetCache = UnityEngine.Object.FindObjectsOfType<LookAtTarget>(true);
        return _targetCache;
    }

    /// <summary>toggle 状态轮询：扫描被跟踪的 toggle 型按钮，读最终 toggler 状态，变化广播。
    /// 只轮询"多 toggler 的瞬时拉杆"（楼梯盖板手柄等）——这类点击后动画回弹，需要校正最终状态；
    /// 单 toggler 开关（灯/SaftySwitch）由点击事件 + ApplyClick 对齐即可，不轮询（避免干扰）。</summary>
    private void PollToggleStates(NetManager net)
    {
        _stateTimer += Time.deltaTime;
        if (_stateTimer < StateInterval) return;
        _stateTimer = 0f;
        // 仅主机广播 toggle 轮询（主机是唯一对齐广播源）：客机轮询会把"应用远端后回驱/陈旧"状态
        // 回灌主机 → 发射台 Switch / Universal Switch Button 反复回跳（争抢根因，2026-08-15 修复）。
        // 客机操作的多 toggler 手柄经点击事件（118）复现 → 主机 toggler 变化 → 主机轮询广播 → 客机对齐，链路完整。
        if (!net.IsHost) return;
        var targets = GetTargets();
        if (targets == null) return;
        var changed = new List<(string id, List<bool> states)>();
        foreach (var t in targets)
        {
            if (t == null || !ShouldTrack(t)) continue;
            var tg = t.GetComponents<AnimatorBoolToggler>();
            if (tg == null || tg.Length < 2) continue; // 只轮询多 toggler（瞬时拉杆/楼梯盖板手柄等）
            string id = PathOf(t.transform);
            // Lever（仰角/方向角/锁止拉杆等）：由 ControlSync 值同步权威处理（谁操作谁权威，30Hz），
            // 不轮询——轮询 SetBool 会与值同步/点击动画冲突（72cef5f 正常版无此轮询，Lever 只靠点击同步）。
            if (id.IndexOf("Lever", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            // 打字机通知指示灯：主机权威单向（状态由主机打字机/任务图驱动，客机只接收，
            // 客机点灯只影响本地按钮动画）——避免打字机驱动 + 双向轮询互相覆盖（争抢）
            if (!net.IsHost && IsNotificationLight(id)) continue;
            var states = new List<bool>(tg.Length);
            string sig = "";
            for (int i = 0; i < tg.Length; i++)
            {
                bool b = false;
                try { b = tg[i].GetBool(); } catch { }
                states.Add(b);
                sig += b ? "1" : "0";
            }
            if (_lastToggleSig.TryGetValue(id, out var last) && last == sig) continue;
            _lastToggleSig[id] = sig;
            changed.Add((id, states));
        }
        if (changed.Count == 0) return;
        foreach (var (id, states) in changed)
        {
            var w = NetProtocol.Begin((MsgType)ToggleStateMsgType);
            w.Put(id);
            w.Put((byte)states.Count);
            for (int i = 0; i < states.Count; i++)
                w.Put(states[i] ? (byte)1 : (byte)0);
            var data = NetProtocol.Snapshot(w);
            if (net.IsHost)
            {
                foreach (var p in net.Roster)
                    if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
            else if (net.HostSteamId != 0)
                net.Transport.Send(net.HostSteamId, data, true);
            CoopLog.Debug("ButtonClickSync.toggleState", () => $"[ButtonClickSync] toggle-state '{id}' ={string.Join(",", states.ConvertAll(x => x ? "1" : "0"))}");
        }
    }

    /// <summary>本地 LookAtTarget.OnClickDown 被调用（Harmony patch）→ 若为受跟踪按钮则广播点击。</summary>
    public static void OnLocalClick(LookAtTarget t)
    {
        try
        {
            // 对象级防环：只有正在复现**同一个** target 时才跳过（防环）；
            // 玩家此时点击其他拉杆（装药拉杆快速连点）正常广播，不吞事件。
            if (ApplyingTarget != null && t != null && ReferenceEquals(t, ApplyingTarget)) return;
            var net = CoopRuntime.Net;
            if (net == null || t == null) return;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
            bool tracked = ShouldTrack(t);
            if ((++_clickDiag % 100) == 1)
            {
                string nm = "?";
                try { if (t.gameObject != null) nm = t.gameObject.name; } catch { }
                CoopRuntime.LogSource?.LogInfo($"[ButtonClickSync] OnClickDown hit='{nm}' tracked={tracked}");
            }
            if (!tracked) return;
            BroadcastClick(t, net);
            LastClickAt = Time.realtimeSinceStartup;
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"ButtonClickSync OnLocalClick: {ex.Message}"); }
    }

    private static bool ShouldTrack(LookAtTarget t)
    {
        try
        {
            // 不检查 GetActive()：OnLocalClick 只在真实点击（OnClickDown patch）时调用，
            // 能点击的按钮必然是激活的；inactive 对象即使被诊断扫描列出也不会上报。
            // 移除 active 检查让"初始 inactive、激活后点按"的开关（如 SaftySwitch 安全开关）也能同步。
            string nm = t.gameObject.name ?? "";
            // 很多交互对象都叫 "Universal Button"（如灯开关），靠完整路径识别：
            // Lighting/Hanging Light/.../Universal Button、Notification Light yellow/... 等
            string path = PathOf(t.transform) ?? "";
            // 拉环（Chain）分类处理：
            // - Trigger Chain（激发拉环）：开火已由 GunController.FireShell → GunFire 事件同步，
            //   不走点击同步（否则与事件同步双重触发，对端开火两次）。
            // - Starter Chain（启动/重启引擎拉环）：引擎宕机时重启引擎的事件——EnginesRunning 状态同步
            //   覆盖不了"重启"动作（状态未变时引擎不会自己重启），必须同步点击让对端也拉一下触发重启。
            if (path.IndexOf("Trigger Chain", StringComparison.OrdinalIgnoreCase) >= 0
                || nm.IndexOf("Trigger chain", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            // 发射台开关序列（LookAtTargetUnlockSequence5，.Check Switch.001~.004 下）：由 SequenceSync
            // 专门同步（含点击复现），不走本层点击/toggle——否则与 SequenceSync 双驱动反复回跳
            // （发射台 Switch / Universal Switch Button 争抢根因，2026-08-15 修复）。
            try
            {
                if (t.GetComponentInParent<LookAtTargetUnlockSequence5>(true) != null) return false;
            }
            catch { }
            // 打字机通知指示灯（Notification Light yellow 等）：指示灯**玩家可交互**（谁操作谁权威），
            // 正常跟踪（点击同步 + 多 toggler 轮询）。轮询已加防环（应用远端状态后更新 _lastToggleSig），
            // 不会与打字机驱动互相覆盖 → 两端不再反复翻转。
            // 楼梯盖板/舱门按钮（Floor Hatch Barbet Stars 等）：是按钮驱动**多个** AnimatorBoolToggler
            //   （4 个，楼梯折叠段）。必须由本模块完整复现点击（OnClickDown 驱动全部 toggler + 事件链），
            //   不能只靠 HatchSync SetBool 单个 IsOpen（其余段不同步 → 卡住）。
            //   故不在此排除——由 Keywords 命中（路径含 Hatch/Stair 时经 Universal Button/按钮关键词）。
            foreach (var k in Keywords)
                if (nm.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
        catch { return false; }
    }

    /// <summary>即时广播所有打字机通知灯当前状态（事件驱动，打字机打印/清除时调用）。
    /// 通知灯亮灭时间可能短于轮询间隔（0.8s），轮询会错过亮的瞬间——打字机事件触发时
    /// 立即扫描通知灯广播状态，客机即时收到 → 灯同步。仅主机广播。</summary>
    public static void BroadcastNotificationLights()
    {
        var net = CoopRuntime.Net;
        if (net == null || !net.IsHost) return;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        try
        {
            var targets = UnityEngine.Object.FindObjectsOfType<LookAtTarget>(true);
            if (targets == null) return;
            foreach (var t in targets)
            {
                if (t == null || !IsNotificationLight(PathOf(t.transform))) continue;
                var tg = t.GetComponents<AnimatorBoolToggler>();
                if (tg == null || tg.Length == 0) continue;
                string id = PathOf(t.transform);
                var lw = NetProtocol.Begin((MsgType)ToggleStateMsgType);
                lw.Put(id);
                int lt = tg.Length; if (lt > 8) lt = 8;
                lw.Put((byte)lt);
                string sig = "";
                for (int i = 0; i < lt; i++)
                {
                    bool b = false;
                    try { b = tg[i].GetBool(); } catch { }
                    lw.Put(b ? (byte)1 : (byte)0);
                    sig += b ? "1" : "0";
                }
                var ld = NetProtocol.Snapshot(lw);
                foreach (var p in net.Roster)
                    if (!p.IsLocal) net.Transport.Send(p.SteamId, ld, true);
                // 诊断：打字机事件触发即时广播时的通知灯状态（确认广播时机是否与灯实际状态同步）
                if ((++_notifDiag % 10) == 1)
                    CoopRuntime.LogSource?.LogInfo($"[ButtonClickSync] notif-lights id='{id}' n={lt} sig={sig}");
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ButtonClickSync BroadcastNotificationLights: {ex.Message}"); }
    }

    private static void BroadcastClick(LookAtTarget t, NetManager net)
    {
        try
        {
            string id = PathOf(t.transform);
            // 打字机通知指示灯：**广播状态（135）而非点击（118）**——打字机驱动灯时会触发按钮
            // OnClickDown（游戏内部），客机若复现点击会切换 toggler[0]（与打字机 SetBool 冲突，
            // 日志 after=[1,0,0,1] want=[0,0,0,1] 即此）。改为把灯 toggler 状态即时广播，
            // 客机 SetBool 对齐（不复现点击）→ 即时同步且不冲突。仅主机广播（客机不上行指示灯）。
            if (IsNotificationLight(id))
            {
                if (net.IsHost)
                {
                    var lw = NetProtocol.Begin((MsgType)ToggleStateMsgType);
                    lw.Put(id);
                    var tg0 = t.GetComponents<AnimatorBoolToggler>();
                    int lt = tg0?.Length ?? 0;
                    if (lt > 8) lt = 8;
                    lw.Put((byte)lt);
                    for (int i = 0; i < lt; i++)
                    {
                        try { lw.Put(tg0[i].GetBool() ? (byte)1 : (byte)0); }
                        catch { lw.Put((byte)0); }
                    }
                    var ld = NetProtocol.Snapshot(lw);
                    foreach (var p in net.Roster)
                        if (!p.IsLocal) net.Transport.Send(p.SteamId, ld, true);
                }
                return;
            }
            var w = NetProtocol.Begin((MsgType)118);
            w.Put(id);
            // toggle 型按钮（AnimatorBoolToggler）：附加全部 bool 最终状态，对端应用后强制对齐。
            // 这类按钮（灯/安全开关/发射台开关）是"开关"语义，点击事件丢失/争抢会导致两端状态反；
            // 广播最终 bool 状态后，对端 SetBool(权威值) → 无论丢多少事件，最终状态一致。
            var togglers = t.GetComponents<AnimatorBoolToggler>();
            int tc = 0;
            if (togglers != null) tc = togglers.Length;
            if (tc > 0)
            {
                if (tc > 8) tc = 8;
                w.Put((byte)tc);
                for (int i = 0; i < tc; i++)
                {
                    try { w.Put(togglers[i].GetBool() ? (byte)1 : (byte)0); }
                    catch { w.Put((byte)0); }
                }
            }
            else
            {
                w.Put((byte)0); // 非 toggle 按钮
            }
            var data = NetProtocol.Snapshot(w);
            if (net.IsHost)
            {
                foreach (var p in net.Roster)
                    if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
            else if (net.HostSteamId != 0)
                net.Transport.Send(net.HostSteamId, data, true);
            CoopRuntime.LogSource?.LogInfo($"[ButtonClickSync] click '{id}' togglers={tc}");
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"ButtonClickSync BroadcastClick: {ex.Message}"); }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            byte msgType = r.GetByte();
            string id = r.GetString();
            int tc = r.GetByte();
            var togglerStates = new List<bool>(tc);
            for (int i = 0; i < tc; i++)
                togglerStates.Add(r.GetByte() != 0);
            if (net.IsHost)
                foreach (var p in net.Roster)
                    if (!p.IsLocal && (ulong)p.SteamId != from)
                        net.Transport.Send(p.SteamId, data, true);
            if (msgType == ToggleStateMsgType)
            {
                // 状态轮询：直接 SetBool 对齐（不触发点击/动画，校正瞬时拉杆回弹后的最终状态）。
                // 防环：应用后把 _lastToggleSig 更新为应用结果——否则下轮 PollToggleStates 读到
                // 新状态（≠ _lastToggleSig）又广播回 → 两端反复互相覆盖（指示灯争抢）。
                ApplyToggleState(id, togglerStates);
                MarkToggleApplied(id, togglerStates);
                return;
            }
            ApplyClick(id, togglerStates);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ButtonClickSync OnPacket: {ex.Message}"); }
    }

    /// <summary>应用 toggle 状态（轮询校正）：找到按钮，SetBool 对齐所有 toggler。</summary>
    private static void ApplyToggleState(string id, List<bool> states)
    {
        var targets = UnityEngine.Object.FindObjectsOfType<LookAtTarget>(true);
        if (targets == null) return;
        foreach (var t in targets)
        {
            if (t == null || PathOf(t.transform) != id) continue;
            var tg = t.GetComponents<AnimatorBoolToggler>();
            if (tg == null) break;
            int n = Math.Min(tg.Length, states.Count);
            for (int i = 0; i < n; i++)
            {
                try
                {
                    bool want = states[i];
                    bool have;
                    try { have = tg[i].GetBool(); } catch { have = want; }
                    if (have != want)
                    {
                        tg[i].SetBool(want);
                        CoopRuntime.LogSource?.LogInfo($"[ButtonClickSync] toggle-state align #{i} '{id}' -> {want}");
                    }
                }
                catch { }
            }
            break;
        }
    }

    /// <summary>防环：应用远端 toggle 状态后，把 _lastToggleSig 更新为应用结果——\n    /// 否则下轮 PollToggleStates 读到新状态（≠ _lastToggleSig）又广播回 → 两端反复互相覆盖。</summary>
    private static void MarkToggleApplied(string id, List<bool> states)
    {
        try
        {
            string sig = "";
            for (int i = 0; i < states.Count; i++)
                sig += states[i] ? "1" : "0";
            _lastToggleSig[id] = sig;
        }
        catch { }
    }

    /// <summary>入队待应用点击。
    /// - 非 toggle 按钮（togglerStates==null）：同 id 合并（防 Switch 状态被过期点击反转）。
    /// - toggle 按钮：**不合并丢弃**（每次点击都保留，快速连点逐一应用）——因为 toggle 按钮丢一次
    ///   事件状态就反；配合最终 bool 状态对齐，即使争抢也能恢复正确状态。
    /// 队列有长度上限（防异常堆积）；超时点击在 Tick 中丢弃。</summary>
    private static void QueueOrMerge(string id, float readyAt, string why, List<bool> togglerStates)
    {
        bool isToggle = togglerStates != null && togglerStates.Count > 0;
        if (!isToggle)
        {
            // 非 toggle：同 id 合并
            for (int i = 0; i < _pendingClicks.Count; i++)
            {
                if (_pendingClicks[i].Id == id)
                {
                    _pendingClicks[i].ReadyAt = Math.Min(_pendingClicks[i].ReadyAt, readyAt); // 取最早就绪时间，尽快应用
                    CoopRuntime.LogSource?.LogInfo($"[ButtonClickSync] click queued-merged {why} '{id}'");
                    return;
                }
            }
        }
        else
        {
            // toggle：同 id 已有排队则更新其 ReadyAt（保持最早），但仍保留本次（不丢）；
            // 若队列里同 id 已积压超过上限，则合并（极端保护，最终状态仍靠权威值对齐）。
            int same = 0;
            for (int i = 0; i < _pendingClicks.Count; i++)
            {
                if (_pendingClicks[i].Id != id) continue;
                same++;
                if (same <= 3)
                    _pendingClicks[i].ReadyAt = Math.Min(_pendingClicks[i].ReadyAt, readyAt);
            }
            if (same > 3)
            {
                // 积压过多：只保留最后一次的权威状态（覆盖前面的，避免执行太多次 Toggle 反转）
                for (int i = _pendingClicks.Count - 1; i >= 0; i--)
                {
                    if (_pendingClicks[i].Id == id)
                    {
                        _pendingClicks[i].TogglerStates = togglerStates;
                        break;
                    }
                }
                CoopRuntime.LogSource?.LogInfo($"[ButtonClickSync] toggle click merged-backlog {why} '{id}'");
                return;
            }
        }
        if (_pendingClicks.Count >= 64)
        {
            _pendingClicks.RemoveAt(0);
            CoopRuntime.LogSource?.LogInfo($"[ButtonClickSync] click queue overflow drop '{id}'");
        }
        _pendingClicks.Add(new PendingClick { Id = id, ReadyAt = readyAt, TogglerStates = togglerStates });
        CoopRuntime.LogSource?.LogInfo($"[ButtonClickSync] click queued {why} '{id}' togglers={(togglerStates == null ? 0 : togglerStates.Count)}");
    }

    private static void ApplyClick(string id, List<bool> togglerStates)
    {
        var targets = UnityEngine.Object.FindObjectsOfType<LookAtTarget>(true);
        if (targets == null) return;
        foreach (var t in targets)
        {
            if (t == null || PathOf(t.transform) != id) continue;
            try
            {
                // 复现远端点击：按钮未激活/冷却时排队延迟到就绪再应用（快速连续操作不吞事件）。
                // 按钮在外层——点击驱动内部事件链（状态推进 + 动画表现），故必须先等按钮可用再点。
                if (!t.isActive)
                {
                    QueueOrMerge(id, Time.realtimeSinceStartup + 0.3f, "(inactive)", togglerStates);
                    break;
                }
                if (t.nextAllowedClickTime > Time.realtimeSinceStartup)
                {
                    QueueOrMerge(id, t.nextAllowedClickTime, "(cooldown)", togglerStates);
                    break;
                }
                ApplyingTarget = t;
                try
                {
                    t.OnClickDown();
                    t.OnClickUp();
                }
                finally { ApplyingTarget = null; }
                // 诊断：复现点击后记录手柄 toggler 状态（用于定位楼梯盖板手柄卡住的机制）
                if (togglerStates != null && togglerStates.Count > 1)
                {
                    string st = "";
                    var tgDiag = t.GetComponents<AnimatorBoolToggler>();
                    if (tgDiag != null)
                        for (int di = 0; di < tgDiag.Length && di < togglerStates.Count; di++)
                        {
                            bool b = false;
                            try { b = tgDiag[di].GetBool(); } catch { }
                            st += (st.Length > 0 ? "," : "") + (b ? "1" : "0");
                        }
                    CoopRuntime.LogSource?.LogInfo($"[ButtonClickSync] applied click '{id}' multi-toggler after=[{st}] want=[{string.Join(",", togglerStates.ConvertAll(x => x ? "1" : "0"))}]");
                }
                // toggle 型按钮：点击可能触发 ToggleBool（按一次切换），但丢事件/争抢会导致两端状态反。
                // 仅对**单 toggler** 按钮（灯/安全开关等简单开关）强制 SetBool(权威最终值) 对齐；
                // 多 toggler 按钮（如楼梯盖板 4 个折叠段）**不**做对齐——OnClickDown 复现已完整驱动
                // 全部 toggler + 事件链，SetBool 会在动画播放中强制设值打断动画 → 卡在中间状态。
                // ⚠️ 瞬时回弹型 Switch（AnimatorBoolToggler.delay>0，点击后延迟回弹）：立即 SetBool 会在
                //    动画播放中强制设值 → 对端回弹。此类跳过立即对齐（由状态轮询 135 校正最终状态）。
                if (togglerStates != null && togglerStates.Count == 1)
                {
                    var tg = t.GetComponents<AnimatorBoolToggler>();
                    if (tg != null && tg.Length > 0)
                    {
                        try
                        {
                            // 检测瞬时回弹型：delay>0 → 跳过立即对齐（避免回弹）
                            float delay = 0f;
                            try { delay = tg[0].delay; } catch { }
                            if (delay > 0f)
                            {
                                CoopRuntime.LogSource?.LogInfo($"[ButtonClickSync] toggle '{id}' is spring-back (delay={delay:0.00}), skip align");
                            }
                            else
                            {
                                bool want = togglerStates[0];
                                bool have;
                                try { have = tg[0].GetBool(); } catch { have = want; }
                                if (have != want)
                                {
                                    tg[0].SetBool(want);
                                    CoopRuntime.LogSource?.LogInfo($"[ButtonClickSync] toggle align '{id}' -> {want}");
                                }
                            }
                        }
                        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ButtonClickSync toggle align: {ex.Message}"); }
                    }
                }
                CoopRuntime.LogSource?.LogInfo($"[ButtonClickSync] applied click '{id}' togglers={(togglerStates == null ? 0 : togglerStates.Count)}");
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ButtonClickSync Apply: {ex.Message}"); }
            break;
        }
    }

    private static string PathOf(Transform t)
    {
        if (t == null) return "";
        string path = t.name ?? "";
        var p = t.parent;
        while (p != null) { path = (p.name ?? "") + "/" + path; p = p.parent; }
        return path;
    }

    // ---------------- 中途加入快照（StateSnapshotSync "button"）----------------
    // 扫描被跟踪的多 toggler 按钮（指示灯/楼梯盖板手柄等），打包当前 toggler 状态，
    // 新客机加入后应用 → 指示灯/手柄初始状态两端一致（不依赖后续变化广播）。

    public static byte[] BuildButtonSnapshot()
    {
        try
        {
            var net = CoopRuntime.Net;
            if (net == null || !net.IsHost) return null;
            var targets = UnityEngine.Object.FindObjectsOfType<LookAtTarget>(true);
            if (targets == null || targets.Length == 0) return null;
            var w = NetProtocol.Begin((MsgType)118); // 快照内部格式复用 118（实际由 StateSnapshotSync 包装分发）
            int count = 0;
            // 先用 List 收集（避免未知数量时预留问题）
            var items = new List<(string id, List<bool> states)>();
            foreach (var t in targets)
            {
                if (t == null || !ShouldTrack(t)) continue;
                var tg = t.GetComponents<AnimatorBoolToggler>();
                if (tg == null || tg.Length == 0) continue;
                string id = PathOf(t.transform);
                var states = new List<bool>(tg.Length);
                for (int i = 0; i < tg.Length; i++)
                {
                    bool b = false;
                    try { b = tg[i].GetBool(); } catch { }
                    states.Add(b);
                }
                items.Add((id, states));
            }
            w.Put((byte)items.Count);
            foreach (var (id, states) in items)
            {
                w.Put(id);
                w.Put((byte)states.Count);
                for (int i = 0; i < states.Count; i++)
                    w.Put(states[i] ? (byte)1 : (byte)0);
                count++;
            }
            if (count == 0) return null;
            CoopRuntime.LogSource?.LogInfo($"[ButtonClickSync] snapshot build n={count}");
            return NetProtocol.Snapshot(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ButtonClickSync BuildButtonSnapshot: {ex.Message}"); }
        return null;
    }

    public static void ApplyButtonSnapshot(byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int n = r.GetByte();
            for (int k = 0; k < n; k++)
            {
                string id = r.GetString();
                int tc = r.GetByte();
                var states = new List<bool>(tc);
                for (int i = 0; i < tc; i++)
                    states.Add(r.GetByte() != 0);
                ApplyToggleState(id, states);
            }
            CoopRuntime.LogSource?.LogInfo($"[ButtonClickSync] snapshot applied n={n}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ButtonClickSync ApplyButtonSnapshot: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _log = 0; ApplyingTarget = null; LastClickAt = 0f; _pendingClicks.Clear(); _lastToggleSig.Clear(); }
}
