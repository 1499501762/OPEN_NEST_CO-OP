using System;
using UnityEngine;
using OpenNestCoop.Net;
using LiteNetLib.Utils;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 补给购买事件同步（MsgType=132，主机权威）。
/// 场景：Supply console 玩家把卡片插入 RequisitionSlot → 拉征用杆 → RequisitionSlot.AttemptRequisition()
/// → 花征用点（CR_SpendRequisitionPoints）→ 应用效果（弹药/发射药/侦察机/重新定位）。
/// **购买对所有人生效**：主机执行购买（扣征用点 + 应用效果），广播事件 → 对端执行同样操作。
/// 客机购买：拦截本地执行 → 上报主机 → 主机执行 + 广播 → 所有人一致（含客机）。
/// 效果同步：弹药（ShellSync）、发射药库存（RequisitionSync req/powder/stock）已由其他模块同步。
/// </summary>
public sealed class PurchaseSync : ISyncedModule
{
    public byte MsgType => 132;

    /// <summary>应用远端购买事件时的防环标志（Harmony patch 据此不重复上报）。</summary>
    public static bool IsApplying;
    private static int _log;
    private static int _diag;
    /// <summary>客机收到购买事件（ev=1）的计数（诊断 apply ×2 根因：D 端间隔 2ms 收到 2 个包）。</summary>
    private static int _recvDiag;
    /// <summary>上次应用购买时间（去抖：同一次购买重复广播 <0.3s 视为同一事件，只应用一次）。</summary>
    private static float _lastApplyAt;

    /// <summary>本地拉征用杆（AttemptRequisition 被调用，Harmony patch）→ 上报主机或直接广播。</summary>
    public static bool OnLocalPurchase(RequisitionSlot slot)
    {
        try
        {
            if (IsApplying) return true; // 应用远端购买时放行本地执行（不重复上报）
            var net = CoopRuntime.Net;
            if (net == null) return true;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return true;
            if (slot == null) return true;

            if (net.IsHost)
            {
                // 主机：广播购买事件给所有远端（主机自身继续执行）
                var w = NetProtocol.Begin((MsgType)132);
                w.Put((byte)1); // ev=1 购买
                var data = NetProtocol.Snapshot(w);
                foreach (var p in net.Roster)
                    if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
                if ((++_log % 5) == 1)
                    CoopRuntime.LogSource?.LogInfo($"[Purchase] host purchase broadcast");
                return true; // 主机放行本地执行
            }
            else
            {
                // 客机：拦截本地执行，上报主机（主机权威执行购买，避免两端重复扣点/双效果）
                var w = NetProtocol.Begin((MsgType)132);
                w.Put((byte)2); // ev=2 购买请求（来自客机）
                var data = NetProtocol.Snapshot(w);
                net.Transport.Send(net.HostSteamId, data, true);
                if ((++_diag % 10) == 1)
                    CoopRuntime.LogSource?.LogInfo($"[Purchase] client purchase REQ -> host (intercept local)");
                return false; // 拦截：客机不本地执行，等主机广播
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"PurchaseSync OnLocalPurchase: {ex.Message}"); }
        return true;
    }

    public void Tick(float dt) { }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            byte ev = r.GetByte();
            if (net.IsHost)
            {
                if (ev == 2)
                {
                    // 客机购买请求 → 主机执行购买（权威）→ 广播给所有客机
                    CoopRuntime.LogSource?.LogInfo($"[Purchase] host run purchase for client {from}");
                    ExecuteHostPurchase();
                    // 广播购买事件（含发起客机）
                    var w = NetProtocol.Begin((MsgType)132);
                    w.Put((byte)1);
                    var bdata = NetProtocol.Snapshot(w);
                    foreach (var p in net.Roster)
                        if (!p.IsLocal) net.Transport.Send(p.SteamId, bdata, true);
                    return;
                }
                else
                {
                    // 主机自身购买广播：转发给其他客机（不含发起者？发起者=主机）
                    foreach (var p in net.Roster)
                        if (!p.IsLocal && (ulong)p.SteamId != from)
                            net.Transport.Send(p.SteamId, data, true);
                }
            }
            // 诊断（客机）：收到购买事件次数——定位 apply ×2（间隔 2ms 收到 2 个 ev=1）根因
            if (!net.IsHost && (++_recvDiag % 5) == 1)
                CoopRuntime.LogSource?.LogInfo($"[Purchase] recv ev={ev} from={from} total={_recvDiag}");
            // 对端（含客机）：执行购买（应用效果 + 花征用点）
            ApplyPurchase();
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"PurchaseSync OnPacket: {ex.Message}"); }
    }

    /// <summary>主机权威执行购买：调用 RequisitionSlot.AttemptRequisition（花征用点 + 应用效果）。
    /// ⚠️ 防环（2026-08-15）：AttemptRequisition 内部会触发 PreRequisition patch（OnLocalPurchase），
    /// 若不设 IsApplying，主机代表客机执行购买时会再次走 host 分支广播 ev=1 → 客机收到重复购买事件
    /// （日志连续 3 次 “apply purchase (slot 0)”）→ 重复扣点/重复应用效果。此处置 IsApplying 抑制重广播
    /// （OnPacket 已负责广播 ev=1 给客机）。</summary>
    private static void ExecuteHostPurchase()
    {
        try
        {
            if (IsApplying) return; // 防环：正在应用/执行购买时不重入
            IsApplying = true;
            try
            {
                var slots = UnityEngine.Object.FindObjectsOfType<RequisitionSlot>();
                if (slots == null || slots.Length == 0)
                {
                    CoopRuntime.LogSource?.LogWarning("[Purchase] host: RequisitionSlot not found");
                    return;
                }
                // 找有卡片的槽位（CurrentCard != null）执行购买
                for (int i = 0; i < slots.Length; i++)
                {
                    var s = slots[i];
                    if (s == null) continue;
                    if (s.CurrentCard != null)
                    {
                        s.AttemptRequisition();
                        return;
                    }
                }
                // 兜底：第一个槽位
                slots[0].AttemptRequisition();
            }
            finally { IsApplying = false; }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"PurchaseSync ExecuteHostPurchase: {ex.Message}"); }
    }

    /// <summary>对端执行购买（防环：IsApplying 期间不再上报）。
    /// ⚠️ 幂等去抖（2026-08-15）：同一次购买被重复广播（间隔 2ms 收到 2 个 ev=1）时只应用一次，
    /// 避免重复扣点/重复应用效果。0.3s 内重复视为同一事件。</summary>
    private static void ApplyPurchase()
    {
        float now = Time.realtimeSinceStartup;
        if (now - _lastApplyAt < 0.3f)
        {
            if ((++_diag % 10) == 1)
                CoopRuntime.LogSource?.LogInfo("[Purchase] apply purchase SKIPPED (dedup)");
            return;
        }
        _lastApplyAt = now;
        IsApplying = true;
        try
        {
            var slots = UnityEngine.Object.FindObjectsOfType<RequisitionSlot>();
            if (slots == null || slots.Length == 0) return;
            for (int i = 0; i < slots.Length; i++)
            {
                var s = slots[i];
                if (s == null) continue;
                if (s.CurrentCard != null)
                {
                    s.AttemptRequisition();
                    CoopRuntime.LogSource?.LogInfo($"[Purchase] apply purchase (slot {i})");
                    return;
                }
            }
            slots[0].AttemptRequisition();
            CoopRuntime.LogSource?.LogInfo("[Purchase] apply purchase (fallback slot 0)");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"PurchaseSync ApplyPurchase: {ex.Message}"); }
        finally { IsApplying = false; }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { IsApplying = false; }
}
