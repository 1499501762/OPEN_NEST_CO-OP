using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 交互按钮层（ButtonLayer，MsgType=202）。分层：
/// - 点击事件 → EventLayer（<see cref="V2Authority.Operator"/>：谁点谁本地执行完整效果 + 广播，
///   对端 OnClickDown/OnClickUp 复现）。本层只负责：识别受跟踪按钮（关键词）+ 触发点击事件 + 复现点击。
/// - toggle 状态 → 本层（MsgType=202）：心跳轮询最终 toggler bool（瞬时拉杆/楼梯盖板手柄等多 toggler），
///   变化广播 + 对端 SetBool 对齐，跨端状态一致。
/// - 吸收优化（ARCHITECTURE.md B2）：实例缓存 + 3s 低频刷新，去 V1 每 0.8s FindObjectsOfType&lt;LookAtTarget&gt;。
/// - 防环（约束#3）：复现点击用对象级 <see cref="ApplyingTarget"/>（只抑制同目标再广播，不吞其他按钮点击）。
/// </summary>
public sealed class ButtonLayer : ISyncedModule
{
    public static ButtonLayer Instance { get; } = new ButtonLayer();

    private ButtonLayer()
    {
        // 点击事件交给 EventLayer（Operator 权威：谁点谁发，对端复现）
        EventLayer.Instance.Register(ClickEventId, V2Authority.Operator, ReproduceClick);
    }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2Button;

    public const string ClickEventId = "v2/button/click";

    private static readonly string[] Keywords =
        { "Lever", "Rammer", "Hatch", "Primer", "Confirm", "Breech",
          "Power", "Reset", "Delete", "Measure", "Kill", "Lock", "Elevation", "Range", "Damage",
          "Link", "Firing", "Trigger", "Launch", "Lanyard", "Fire", "Safety", "Safty", "Switch", "Arm",
          "Starter", "Crank",
          "Light", "Lighting", "Notification",
          "Horn", "Siren",
          "Cylinder", "Move", "Dispenc", "Dispenser", "Load",
          "Requisition", "Punchcard", "征用" };

    /// <summary>正在复现的远端点击目标（对象级防环，约束#3）。</summary>
    public LookAtTarget ApplyingTarget;

    // ---- 待应用点击队列（按钮未激活/冷却时延迟到就绪，快速连续操作不吞事件）----
    private sealed class PendingClick { public string Id; public float ReadyAt; public List<bool> States; }
    private readonly List<PendingClick> _pendingClicks = new();
    private const float PendingTimeout = 3f;

    // ---- toggle 状态（MsgType=202）----
    private const float TogglePollInterval = 0.8f;
    private const float CacheRefreshInterval = 3f;
    private float _pollTimer, _cacheTimer;
    /// <summary>受跟踪按钮实例缓存（B2 优化：低频刷新，去每 0.8s 全场景扫描）。</summary>
    private readonly List<LookAtTarget> _targetCache = new();
    /// <summary>按钮 id → 最近 toggler 签名（检测变化才广播；应用远端后更新，防环）。</summary>
    private readonly Dictionary<string, string> _lastToggleSig = new();

    private int _diagCounter;

    // ---------------- ISyncedModule ----------------

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;
        RefreshCache(dt);
        PollToggleStates(dt);
        ProcessPendingClicks();

        _diagCounter++;
        if (_diagCounter % 250 == 1)
            CoopLog.Info("SyncV2.button", () => $"[SyncV2] ButtonLayer cached={_targetCache.Count} trackedSig={_lastToggleSig.Count} host={Store.IsHost}", 5f);
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            string id = r.GetString();
            int tc = r.GetByte();
            var states = new List<bool>(tc);
            for (int i = 0; i < tc; i++) states.Add(r.GetByte() != 0);
            // 主机中继：客机上行的 toggle 状态转发给其他客机（星型拓扑）
            if (Store.IsHost && from != 0)
            {
                var net = _net;
                if (net != null)
                    for (int i = 0; i < net.Roster.Count; i++)
                    {
                        var p = net.Roster[i];
                        if (p != null && !p.IsLocal && (ulong)p.SteamId != from)
                            net.Transport.Send(p.SteamId, data, true);
                    }
            }
            // 直接 SetBool 对齐（不触发点击/动画，校正瞬时拉杆回弹后的最终状态）
            ApplyToggleState(id, states);
            MarkToggleApplied(id, states); // 防环：应用后更新签名，避免下轮轮询又广播回
            CoopLog.Debug("SyncV2.buttonRecv", () => $"[SyncV2] ButtonLayer recv toggle id='{id}' n={tc}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ButtonLayer] OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset()
    {
        ApplyingTarget = null;
        _pendingClicks.Clear();
        _lastToggleSig.Clear();
        _targetCache.Clear();
    }

    // ---------------- 点击（→ EventLayer，Operator 权威） ----------------

    /// <summary>本地 LookAtTarget.OnClickDown 被调用（Harmony PreLookClick，V2 分支）→ 广播点击事件。</summary>
    public void OnLocalClick(LookAtTarget t)
    {
        try
        {
            // 对象级防环：只抑制正在复现的同一个 target；玩家此时点其他按钮不被吞（快速连点不丢事件）
            if (ApplyingTarget != null && t != null && ReferenceEquals(t, ApplyingTarget)) return;
            if (t == null || !Store.IsOnline) return;
            if (!ShouldTrack(t)) return;
            string id = PathOf(t.transform);
            // 打字机通知灯：广播状态（202）而非点击（EventLayer 复现点击会与打字机 SetBool 冲突）——由 PollToggleStates 即时覆盖
            if (IsNotificationLight(id))
            {
                if (Store.IsHost) BroadcastToggleStateFor(t);
                return;
            }
            // 组装点击事件负载：目标 id + toggle 型按钮的最终 bool（对端应用后对齐）
            var togglers = t.GetComponents<AnimatorBoolToggler>();
            int tc = togglers?.Length ?? 0;
            if (tc > 8) tc = 8;
            var states = new List<bool>(tc);
            for (int i = 0; i < tc; i++)
            {
                try { states.Add(togglers[i].GetBool()); }
                catch { states.Add(false); }
            }
            EventLayer.Instance.Raise(ClickEventId, w =>
            {
                w.Put(id);
                w.Put((byte)states.Count);
                for (int i = 0; i < states.Count; i++) w.Put(states[i] ? (byte)1 : (byte)0);
            });
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ButtonLayer] OnLocalClick: {ex.Message}"); }
    }

    /// <summary>EventLayer 点击事件复现（对端收到 → 完整复现点击：动画 + 逻辑）。</summary>
    private static void ReproduceClick(NetDataReader r)
    {
        var layer = Instance;
        try
        {
            string id = r.GetString();
            int tc = r.GetByte();
            var states = new List<bool>(tc);
            for (int i = 0; i < tc; i++) states.Add(r.GetByte() != 0);
            var t = layer.FindTarget(id);
            if (t == null) return;
            // 未激活/冷却：排队延迟到就绪（快速连续操作不吞事件）
            if (!t.isActive)
            {
                layer.QueueOrMerge(id, Time.realtimeSinceStartup + 0.3f, states);
                return;
            }
            if (t.nextAllowedClickTime > Time.realtimeSinceStartup)
            {
                layer.QueueOrMerge(id, t.nextAllowedClickTime, states);
                return;
            }
            layer.DoApplyClick(t, states);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ButtonLayer] ReproduceClick: {ex.Message}"); }
    }

    private void DoApplyClick(LookAtTarget t, List<bool> states)
    {
        try
        {
            ApplyingTarget = t;
            try { t.OnClickDown(); t.OnClickUp(); }
            finally { ApplyingTarget = null; }
            // 单 toggle 开关（灯/安全开关）：点击可能触发 Toggle 但丢事件/争抢会状态反，
            // 强制 SetBool(权威最终值) 对齐（多 toggler 不做——OnClickDown 已完整驱动全部 toggler，
            // SetBool 会打断动画；瞬时回弹 delay>0 也跳过，由 202 轮询校正）。
            if (states != null && states.Count == 1)
            {
                var tg = t.GetComponents<AnimatorBoolToggler>();
                if (tg != null && tg.Length > 0)
                {
                    try
                    {
                        float delay = 0f;
                        try { delay = tg[0].delay; } catch { }
                        if (delay <= 0f)
                        {
                            bool want = states[0];
                            bool have;
                            try { have = tg[0].GetBool(); } catch { have = want; }
                            if (have != want) tg[0].SetBool(want);
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ButtonLayer] DoApplyClick: {ex.Message}"); }
    }

    private void QueueOrMerge(string id, float readyAt, List<bool> states)
    {
        bool isToggle = states != null && states.Count > 0;
        for (int i = 0; i < _pendingClicks.Count; i++)
        {
            if (_pendingClicks[i].Id != id) continue;
            if (isToggle && i > 3) continue; // toggle 积压保护：只保留前几次
            _pendingClicks[i].ReadyAt = Math.Min(_pendingClicks[i].ReadyAt, readyAt);
            if (!isToggle) return; // 非 toggle 合并（防 Switch 被过期点击反转）
        }
        if (_pendingClicks.Count >= 64) _pendingClicks.RemoveAt(0);
        _pendingClicks.Add(new PendingClick { Id = id, ReadyAt = readyAt, States = states });
    }

    private void ProcessPendingClicks()
    {
        if (_pendingClicks.Count == 0) return;
        float now = Time.realtimeSinceStartup;
        for (int i = _pendingClicks.Count - 1; i >= 0; i--)
        {
            var p = _pendingClicks[i];
            if (now >= p.ReadyAt)
            {
                _pendingClicks.RemoveAt(i);
                var t = FindTarget(p.Id);
                if (t != null && t.isActive) DoApplyClick(t, p.States);
            }
            else if (now - p.ReadyAt > PendingTimeout)
            {
                _pendingClicks.RemoveAt(i); // 超时丢弃（防堆积/过期点击反转状态）
            }
        }
    }

    // ---------------- toggle 状态（MsgType=202） ----------------

    /// <summary>低频刷新实例缓存（B2 优化：3s 一次 FindObjectsOfType，而非每 0.8s）。</summary>
    private void RefreshCache(float dt)
    {
        _cacheTimer += dt;
        if (_cacheTimer < CacheRefreshInterval) return;
        _cacheTimer = 0f;
        try
        {
            var targets = UnityEngine.Object.FindObjectsOfType<LookAtTarget>(true);
            _targetCache.Clear();
            if (targets == null) return;
            for (int i = 0; i < targets.Length; i++)
                if (targets[i] != null) _targetCache.Add(targets[i]);
        }
        catch { }
    }

    /// <summary>心跳轮询多 toggler 按钮最终状态，变化广播（202）——对端 SetBool 校正。
    /// 只轮询多 toggler 的瞬时拉杆/楼梯盖板手柄；单 toggler 开关由点击事件 + DoApplyClick 对齐。
    /// <b>仅主机广播</b>：主机是唯一对齐广播源（客机只接收应用）——客机轮询会把“应用后回驱/陈旧”
    /// 状态回灌主机 → 争抢/回跳（Switch/发射台争抢根因）。</summary>
    private void PollToggleStates(float dt)
    {
        _pollTimer += dt;
        if (_pollTimer < TogglePollInterval) return;
        _pollTimer = 0f;
        for (int i = 0; i < _targetCache.Count; i++)
        {
            var t = _targetCache[i];
            if (t == null || !ShouldTrack(t)) continue;
            var tg = t.GetComponents<AnimatorBoolToggler>();
            if (tg == null || tg.Length == 0) continue;
            string id = PathOf(t.transform);
            // Lever（仰角/方向角/锁止拉杆）：由 ValueLayer 值同步权威处理，不轮询（SetBool 与值同步/点击动画冲突）
            if (id.IndexOf("Lever", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            // 通知灯：打字机/任务驱动（可无点击事件），单 toggler 也轮询对齐
            bool notifLight = IsNotificationLight(id);
            // 非通知灯只轮询多 toggler（瞬时拉杆/楼梯盖板手柄等）
            if (!notifLight && tg.Length < 2) continue;
            // 客机不广播（只接收应用）；主机权威单向广播对齐，防争抢
            if (!Store.IsHost) continue;
            string sig = "";
            var states = new List<bool>(tg.Length);
            for (int k = 0; k < tg.Length; k++)
            {
                bool b = false;
                try { b = tg[k].GetBool(); } catch { }
                states.Add(b);
                sig += b ? "1" : "0";
            }
            if (_lastToggleSig.TryGetValue(id, out var last) && last == sig) continue;
            _lastToggleSig[id] = sig;
            BroadcastToggleState(id, states);
        }
    }

    private void BroadcastToggleState(string id, List<bool> states)
    {
        Store.Broadcast((byte)OpenNestCoop.Net.MsgType.V2Button, w =>
        {
            w.Put(id);
            w.Put((byte)states.Count);
            for (int i = 0; i < states.Count; i++) w.Put(states[i] ? (byte)1 : (byte)0);
        }, reliable: true);
    }

    private void BroadcastToggleStateFor(LookAtTarget t)
    {
        try
        {
            var tg = t.GetComponents<AnimatorBoolToggler>();
            if (tg == null || tg.Length == 0) return;
            string id = PathOf(t.transform);
            var states = new List<bool>(tg.Length);
            for (int i = 0; i < tg.Length; i++)
            {
                try { states.Add(tg[i].GetBool()); }
                catch { states.Add(false); }
            }
            BroadcastToggleState(id, states);
        }
        catch { }
    }

    /// <summary>应用远端 toggle 状态：找到按钮 SetBool 对齐所有 toggler（不触发点击/动画）。</summary>
    private void ApplyToggleState(string id, List<bool> states)
    {
        var t = FindTarget(id);
        if (t == null) return;
        var tg = t.GetComponents<AnimatorBoolToggler>();
        if (tg == null) return;
        int n = Math.Min(tg.Length, states.Count);
        for (int i = 0; i < n; i++)
        {
            try
            {
                bool want = states[i];
                bool have;
                try { have = tg[i].GetBool(); } catch { have = want; }
                if (have != want) tg[i].SetBool(want);
            }
            catch { }
        }
    }

    /// <summary>防环：应用远端 toggle 状态后更新签名——否则下轮 PollToggleStates 读到新状态又广播回（指示灯争抢）。</summary>
    private static void MarkToggleApplied(string id, List<bool> states)
    {
        string sig = "";
        for (int i = 0; i < states.Count; i++) sig += states[i] ? "1" : "0";
        Instance._lastToggleSig[id] = sig;
    }

    // ---------------- 识别/查找 ----------------

    private LookAtTarget FindTarget(string id)
    {
        for (int i = 0; i < _targetCache.Count; i++)
        {
            var t = _targetCache[i];
            if (t == null) continue;
            try { if (PathOf(t.transform) == id) return t; } catch { }
        }
        return null;
    }

    private static bool ShouldTrack(LookAtTarget t)
    {
        try
        {
            string nm = t.gameObject.name ?? "";
            string path = PathOf(t.transform) ?? "";
            // Trigger Chain（激发拉环）：开火已由 EventLayer 开火事件同步，不走点击（否则双重触发开火两次）
            if (path.IndexOf("Trigger Chain", StringComparison.OrdinalIgnoreCase) >= 0
                || nm.IndexOf("Trigger chain", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            // 发射台开关序列（LookAtTargetUnlockSequence5）：由 SequenceSyncV2 专门同步（含点击复现），
            // 不走本层点击/toggle，避免双驱动反复回跳（发射台 Switch 争抢根因）
            try
            {
                var seq = t.GetComponentInParent<LookAtTargetUnlockSequence5>(true);
                if (seq != null) return false;
            }
            catch { }
            foreach (var k in Keywords)
                if (nm.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
        catch { return false; }
    }

    private static bool IsNotificationLight(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        return id.IndexOf("Notification Light", StringComparison.OrdinalIgnoreCase) >= 0
            || id.IndexOf("Message Notifications", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string PathOf(Transform t)
    {
        if (t == null) return "";
        string path = t.name ?? "";
        var p = t.parent;
        while (p != null) { path = (p.name ?? "") + "/" + path; p = p.parent; }
        return path;
    }
}
