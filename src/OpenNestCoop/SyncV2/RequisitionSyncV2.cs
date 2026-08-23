using System;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 补给/征用同步（RequisitionSyncV2）。M7：把 V1 <c>RequisitionSync</c> 迁入分层架构——
/// 征用点 + 发射药库存注册为 <see cref="ValueLayer"/> **Host 权威**值绑定（客机只接收不上行）。
/// 购买事件由 PurchaseSyncV2 处理（主机权威）。
/// </summary>
public static class RequisitionSyncV2
{
    private static bool _registered;
    private static PowderChargeInventory _powderInv;
    private static int _powderSeenLog;
    private static MissionStatsTracker _statsTracker;
    private static bool _statsTried;

    /// <summary>注册补给值绑定到 ValueLayer（--sync new 时 bootstrap 调用）。</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            // 发射药库存（Host 权威，客机只接收）：购买发射药后库存变化 → 主机广播 → 一致
            ValueLayer.Instance.RegisterInt("req/powder/stock",
                () =>
                {
                    var p = GetPowderInv();
                    if (p == null) return -1;
                    int c = 0;
                    try { c = p.CurrentCharges; } catch { }
                    if ((++_powderSeenLog % 20) == 1) CoopRuntime.LogSource?.LogInfo($"[RequisitionV2] powder stock getter={c}");
                    return c;
                },
                v =>
                {
                    var p = GetPowderInv();
                    if (p == null) return;
                    try
                    {
                        int cur = p.CurrentCharges;
                        if (v < 0 || v == cur) return;
                        p.AddCharges(v - cur);
                    }
                    catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[RequisitionV2] powder: {ex.Message}"); }
                },
                1f, false, null, V2Authority.Host);

            // 征用点（Host 权威，客机只接收显示，不写回——点数由主机购买事件权威驱动）
            ValueLayer.Instance.RegisterInt("req/points",
                () => ReadPoints(),
                v => { /* 只读 */ },
                1f, false, null, V2Authority.Host);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[RequisitionV2] Register: {ex.Message}"); }
    }

    private static MissionStatsTracker GetStatsTracker()
    {
        if (_statsTried) return _statsTracker;
        _statsTried = true;
        try { _statsTracker = MissionStatsTracker.Instance; } catch { _statsTracker = null; }
        return _statsTracker;
    }

    private static int ReadPoints()
    {
        try
        {
            var st = GetStatsTracker();
            if (st == null) return -1;
            return st.RequisitionPoints;
        }
        catch { return -1; }
    }

    private static PowderChargeInventory GetPowderInv()
    {
        try
        {
            if (_powderInv == null || _powderInv.gameObject == null)
                _powderInv = UnityEngine.Object.FindFirstObjectByType<PowderChargeInventory>();
        }
        catch { _powderInv = null; }
        return _powderInv;
    }
}
