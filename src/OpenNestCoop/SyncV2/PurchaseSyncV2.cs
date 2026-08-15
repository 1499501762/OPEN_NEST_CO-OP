using System;
using LiteNetLib.Utils;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 补给购买事件同步（PurchaseSyncV2，MsgType=223）。M7：把 V1 <c>PurchaseSync</c>（132）迁入分层架构。
/// <see cref="V2Authority.Host"/>：**购买对所有人生效**——主机执行购买（扣征用点+应用效果）→ 广播 → 对端执行；
/// 客机拦截本地执行 → 上报主机（ev=2）→ 主机权威执行 + 广播（避免两端重复扣点/双效果）。
/// 效果同步（弹药/发射药库存）由 ShellSyncV2/RequisitionSyncV2 负责。
/// </summary>
public sealed class PurchaseSyncV2 : ISyncedModule
{
    public static PurchaseSyncV2 Instance { get; } = new PurchaseSyncV2();

    private PurchaseSyncV2() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2Purchase;

    /// <summary>应用远端购买事件时的防环（Harmony patch 据此不重复上报）。</summary>
    public static bool IsApplying;

    /// <summary>本地拉征用杆（AttemptRequisition，Harmony PreRequisition，V2 分支）：
    /// 主机放行+广播；客机拦截本地执行、上报主机。返回是否继续本地执行。</summary>
    public bool OnLocalPurchase(RequisitionSlot slot)
    {
        try
        {
            if (IsApplying) return true;
            if (!Store.IsOnline || slot == null) return true;
            if (Store.IsHost)
            {
                BroadcastPurchase(); // 主机：广播购买事件给所有远端，自身继续执行
                return true;
            }
            var net = _net;
            if (net == null || net.HostSteamId == 0) return true;
            var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2Purchase);
            w.Put((byte)2); // ev=2 购买请求（来自客机）
            net.Transport.Send(net.HostSteamId, NetProtocol.Snapshot(w), true);
            CoopRuntime.LogSource?.LogInfo("[PurchaseV2] client purchase REQ -> host (intercept local)");
            return false; // 拦截：客机不本地执行，等主机广播
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[PurchaseSyncV2] OnLocalPurchase: {ex.Message}"); }
        return true;
    }

    public void Tick(float dt) { }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            byte ev = r.GetByte();
            var net = _net;
            if (Store.IsHost)
            {
                if (ev == 2)
                {
                    // 客机购买请求 → 主机权威执行 → 广播给所有客机
                    ExecuteHostPurchase();
                    BroadcastPurchase();
                    return;
                }
                // 主机自身购买广播：转发给其他客机
                if (net != null)
                    for (int i = 0; i < net.Roster.Count; i++)
                    {
                        var p = net.Roster[i];
                        if (p != null && !p.IsLocal && (ulong)p.SteamId != from)
                            net.Transport.Send(p.SteamId, data, true);
                    }
            }
            ApplyPurchase();
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[PurchaseSyncV2] OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { IsApplying = false; }

    private void BroadcastPurchase()
    {
        var net = _net;
        if (net == null) return;
        try
        {
            var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2Purchase);
            w.Put((byte)1); // ev=1 购买
            var data = NetProtocol.Snapshot(w);
            for (int i = 0; i < net.Roster.Count; i++)
            {
                var p = net.Roster[i];
                if (p != null && !p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[PurchaseSyncV2] BroadcastPurchase: {ex.Message}"); }
    }

    private static void ExecuteHostPurchase()
    {
        try
        {
            var slots = UnityEngine.Object.FindObjectsOfType<RequisitionSlot>();
            if (slots == null || slots.Length == 0) return;
            for (int i = 0; i < slots.Length; i++)
            {
                var s = slots[i];
                if (s == null) continue;
                if (s.CurrentCard != null) { s.AttemptRequisition(); return; }
            }
            slots[0].AttemptRequisition();
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[PurchaseSyncV2] ExecuteHostPurchase: {ex.Message}"); }
    }

    private static void ApplyPurchase()
    {
        IsApplying = true;
        try
        {
            var slots = UnityEngine.Object.FindObjectsOfType<RequisitionSlot>();
            if (slots == null || slots.Length == 0) return;
            for (int i = 0; i < slots.Length; i++)
            {
                var s = slots[i];
                if (s == null) continue;
                if (s.CurrentCard != null) { s.AttemptRequisition(); return; }
            }
            slots[0].AttemptRequisition();
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[PurchaseSyncV2] ApplyPurchase: {ex.Message}"); }
        finally { IsApplying = false; }
    }
}
