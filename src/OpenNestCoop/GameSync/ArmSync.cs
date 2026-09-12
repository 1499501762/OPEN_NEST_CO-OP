using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 预备激发同步（ArmedFireRelayOneShot，MsgType=140）——状态同步（2026-09-01）。
///
/// 背景：`Universal Button Arm Left/Right`（预备激发火炮拉杆）点击实际触发的是
/// `ArmedFireRelayOneShot.ToggleLeft()/ToggleRight()`（切换语义），且实例是**单实例**
/// （Trigger Console/Arming and Fireing Relay，管左右双炮，path 不含 Left/Right）。
/// 直接 patch ArmLeft/ArmRight 抓不到点击（点击走动画事件链调 Toggle）。
///
/// 方案：Harmony patch 全部 6 个方法（ArmLeft/ArmRight/DisarmLeft/DisarmRight/ToggleLeft/ToggleRight）
/// postfix → 执行后读 `IsLeftArmed()/IsRightArmed()` 广播**状态**（非事件/点击）。对端 ApplyState 按
/// 目标值调 ArmLeft/DisarmLeft 对齐（状态对齐比 toggle 语义健壮——两端状态不一致时 toggle 结果不同）。
/// 主机 5s 心跳强制广播（兜底非 patch 方法 SetLeftArmed 直接改状态 + 收敛短暂分歧 + 丢包恢复）。
/// 去重：`_lastStateSig` 按 path 签名去重（状态真变了才广播 + 防回发），不搞全局时间戳去重
/// （会吞掉快速连点/第二次点击）。
/// </summary>
public sealed class ArmSync : ISyncedModule
{
    public int MsgType => 140;

    // ⚠️ 模块自注册：程序集加载时入队（V1 方案），Startup FlushPending 统一注册
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SelfRegister() => CoopSyncRegistry.PendingRegister(false, () => new ArmSync());
    public const byte MsgTypeId = 140;

    private const byte EvState = 1;

    /// <summary>正在复现远端预备激发操作（防环：应用远端时本地方法不再转发）。</summary>
    public static bool IsApplyingArm;
    private static int _log;
    private float _tickAcc;
    /// <summary>每实例最近广播的状态签名（"L1R0"），签名未变跳过（防回发 + 去重）。</summary>
    private static readonly System.Collections.Generic.Dictionary<string, string> _lastStateSig = new();

    /// <summary>主机 5s 心跳强制广播所有实例状态：兜底非 patch 方法直接改状态 + 收敛分歧 + 丢包恢复。</summary>
    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null || !net.IsHost) return;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        _tickAcc += dt;
        if (_tickAcc < 5f) return;
        _tickAcc = 0f;
        try { BroadcastAllRelays(net); }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ArmSync Tick: {ex.Message}"); }
    }

    /// <summary>强制广播所有 ArmedFireRelayOneShot 实例当前 armed 状态。</summary>
    private static void BroadcastAllRelays(NetManager net)
    {
        var relays = UnityEngine.Object.FindObjectsOfType<Zagreekie.Tools.ArmedFireRelayOneShot>(true);
        if (relays == null || relays.Length == 0) return;
        foreach (var relay in relays)
            if (relay != null && relay.transform != null)
                BroadcastState(net, relay, force: true);
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
            if (ev != EvState) return;
            string path = r.GetString();
            bool left = r.GetByte() != 0;
            bool right = r.GetByte() != 0;
            if (net.IsHost)
            {
                // 主机中继给其他客机（不含发起者）
                foreach (var p in net.Roster)
                    if (!p.IsLocal && (ulong)p.SteamId != from)
                        net.Transport.Send(p.SteamId, data, true);
            }
            ApplyState(path, left, right);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ArmSync OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { IsApplyingArm = false; _lastStateSig.Clear(); _tickAcc = 0f; }

    /// <summary>本地 ArmedFireRelayOneShot 状态变化（Harmony postfix，方法执行后调用）→ 读
    /// IsLeftArmed/IsRightArmed 广播状态（签名变化才发）。谁操作谁上报；主机中继给其他客机。</summary>
    public static void OnLocalArmState(Zagreekie.Tools.ArmedFireRelayOneShot relay)
    {
        try
        {
            if (IsApplyingArm) return; // 防环：正在应用远端时不转发
            var net = CoopRuntime.Net;
            if (net == null || relay == null || relay.transform == null) return;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
            BroadcastState(net, relay, force: false);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ArmSync OnLocalArmState: {ex.Message}"); }
    }

    /// <summary>打包并发送状态（left/right bool）。签名未变且非 force 时跳过（防回发）。</summary>
    private static void BroadcastState(NetManager net, Zagreekie.Tools.ArmedFireRelayOneShot relay, bool force)
    {
        string path = PathOf(relay.transform);
        if (string.IsNullOrEmpty(path)) return;
        bool left = relay.IsLeftArmed();
        bool right = relay.IsRightArmed();
        string sig = (left ? "L1" : "L0") + (right ? "R1" : "R0");
        if (!force && _lastStateSig.TryGetValue(path, out var last) && last == sig) return;
        _lastStateSig[path] = sig;
        var w = NetProtocol.Begin((MsgType)MsgTypeId);
        w.Put(EvState);
        w.Put(path);
        w.Put(left ? (byte)1 : (byte)0);
        w.Put(right ? (byte)1 : (byte)0);
        var data = NetProtocol.Snapshot(w);
        if (net.IsHost)
        {
            foreach (var p in net.Roster)
                if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
        }
        else if (net.HostSteamId != 0)
            net.Transport.Send(net.HostSteamId, data, true);
        if ((++_log % 20) == 1)
            CoopRuntime.LogSource?.LogInfo($"[ArmSync] state{(force ? " hb" : "")} '{path}' L={(left ? 1 : 0)} R={(right ? 1 : 0)}");
    }

    /// <summary>对端应用状态：定位 ArmedFireRelayOneShot 实例（单实例管双炮）→ 与目标状态不一致时
    /// 调 ArmLeft/DisarmLeft/ArmRight/DisarmRight 对齐（IsApplyingArm 防环，不依赖按钮 active）。</summary>
    private static void ApplyState(string path, bool wantLeft, bool wantRight)
    {
        var relays = UnityEngine.Object.FindObjectsOfType<Zagreekie.Tools.ArmedFireRelayOneShot>(true);
        if (relays == null || relays.Length == 0)
        {
            CoopRuntime.LogSource?.LogWarning($"[ArmSync] applyState NO RELAY FOUND (path='{path}')");
            return;
        }
        var relay = relays[0]; // 单实例（Arming and Fireing Relay 管左右双炮）
        IsApplyingArm = true;
        try
        {
            if (relay.IsLeftArmed() != wantLeft)
            {
                if (wantLeft) relay.ArmLeft(); else relay.DisarmLeft();
            }
            if (relay.IsRightArmed() != wantRight)
            {
                if (wantRight) relay.ArmRight(); else relay.DisarmRight();
            }
            // 应用后记录签名，防本地 postfix/Tick 把刚应用回来的状态又广播回去（争抢/多余流量）
            try
            {
                if (!string.IsNullOrEmpty(path))
                    _lastStateSig[path] = (relay.IsLeftArmed() ? "L1" : "L0") + (relay.IsRightArmed() ? "R1" : "R0");
            }
            catch { }
            CoopLog.Info("ArmSync.applyState", () => $"[ArmSync] applyState '{path}' L={(wantLeft ? 1 : 0)} R={(wantRight ? 1 : 0)}", 0.5f);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ArmSync applyState: {ex.Message}"); }
        finally { IsApplyingArm = false; }
    }

    private static string PathOf(Transform t)
    {
        try
        {
            string path = t.name ?? "";
            var p = t.parent;
            while (p != null)
            {
                path = (p.name ?? "") + "/" + path;
                p = p.parent;
            }
            return path;
        }
        catch { return ""; }
    }
}
