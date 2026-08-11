using System;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 补给/征用同步（Supply console，主机权威值绑定 + 购买事件）。
/// - 征用点（MissionStatsTracker.RequisitionPoints）：值绑定，主机权威（客户端只接收不上行，
///   避免客机开局读到 0 上行误清主机点数）。
/// - 发射药库存（PowderChargeInventory.CurrentCharges）：值绑定，主机权威（购买发射药后库存变化两端一致）。
/// - 购买事件（patch RequisitionSlot.CR_SpendRequisitionPoints）：拉征用杆 → 广播 → 对端执行，
///   购买效果对所有人生效（弹药/发射药/侦察机/重新定位由游戏本地逻辑响应）。
/// 说明：弹药（CylinderShellSelector）已由 ShellSync 同步；每炮发射药选择已由 ReloadSync 同步。
/// </summary>
public static class RequisitionSync
{
    private static bool _registered;
    private static PowderChargeInventory _powderInv;
    private static int _powderSeenLog;

    /// <summary>注册补给值绑定（Plugin.Load 调用；对象运行时懒解析）。</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            // 发射药库存（主机权威，客户端只接收）：购买发射药后库存变化 → 主机广播 → 所有人一致
            CoopSyncRegistry.RegisterInt("req/powder/stock",
                () =>
                {
                    var p = GetPowderInv();
                    if (p == null) return -1;
                    int c = 0;
                    try { c = p.CurrentCharges; } catch { }
                    if ((++_powderSeenLog % 20) == 1)
                        CoopRuntime.LogSource?.LogInfo($"[Requisition] powder stock getter={c}");
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
                        if (v > cur) p.AddCharges(v - cur); // 增加：补足差值
                        else p.AddCharges(v - cur);          // 减少：AddCharges 负数 = 消耗
                        CoopRuntime.LogSource?.LogInfo($"[Requisition] powder stock apply {cur}->{v}");
                    }
                    catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"RequisitionSync powder: {ex.Message}"); }
                }).ClientNoSend = true;

            // 征用点（主机权威，客户端只接收）：RequisitionPoints 在 interop **只读**（get only），
            // 不能直接写——购买花点由游戏内部 CR_SpendRequisitionPoints 执行（购买事件同步，见 HarmonyPatches）。
            // 这里只做**主机只读广播**：主机点数变化 → 广播 → 客机显示一致（客机不应用不写）。
            CoopSyncRegistry.RegisterInt("req/points",
                () => ReadPoints(),
                v =>
                {
                    // 只读：客机只接收显示值，不写回（interop get only）——点数由主机购买事件权威驱动
                    if ((++_pointsApplyLog % 20) == 1)
                        CoopRuntime.LogSource?.LogInfo($"[Requisition] points recv={v} (readonly)");
                }).ClientNoSend = true;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"RequisitionSync Register: {ex.Message}"); }
    }

    // ---------------- 征用点读写（直接类型引用，interop 可访问） ----------------

    private static MissionStatsTracker _statsTracker;
    private static bool _statsTried;

    private static MissionStatsTracker GetStatsTracker()
    {
        if (_statsTried) return _statsTracker;
        _statsTried = true;
        try
        {
            _statsTracker = MissionStatsTracker.Instance;
            if (_statsTracker == null)
                CoopRuntime.LogSource?.LogWarning("[Requisition] MissionStatsTracker.Instance 为 null（任务场景未加载）");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"RequisitionSync GetStatsTracker: {ex.Message}"); }
        return _statsTracker;
    }

    private static int _pointsApplyLog;

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
