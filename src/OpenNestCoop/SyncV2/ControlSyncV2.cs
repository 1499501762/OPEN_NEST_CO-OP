using System;
using System.Collections.Generic;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 控件发现层（ControlSyncV2，MsgType=204 占位，无自身网络消息）。M6：把 V1 <c>ControlSync</c> 的
/// "发现控件并注册值绑定"逻辑迁入 SyncV2——注册进 <see cref="ValueLayer"/>（数值层），
/// 由 ValueLayer 统一做 deadzone/edge/心跳/插值/权威。
///
/// 吸收优化（ARCHITECTURE.md B1）：**场景加载事件驱动 + 12s 低频兜底**，去 V1 每 3s 的
/// 4 次 FindObjectsOfTypeAll 全场景扫描。任务场景加载后控件一次性注册，之后增量空跑。
/// 触发：sceneLoaded / activeSceneChanged 事件置脏 + 首次进入会话置脏 + 12s 兜底。
/// 分类注册（继承 V1 语义）：
///  - DialInteractable（刻度盘/旋钮/曲柄）：Operator 权威，busy=isDragging；Locking Lever 不插值；
///    弹道计算机/装药/压力/弹舱 → NoHeartbeat。
///  - SliderEnergyMomentumSpinner：同步 Energy 绝对值（曲柄飞轮，惯性衰减两端分叉）。
///  - TurretController：__turret/rotation（DesiredRotation，Operator，30Hz，不插值）。
///  - LinearSliderInteractable（拉杆/滑块）：Operator；Chain/Elevation Lever → 30Hz HighFreq；
///    Elevation Desired/Current 从动值 → <see cref="V2Authority.Host"/>（只接收主机广播，防客机读 0 上行覆盖）。
/// </summary>
public sealed class ControlSyncV2 : ISyncedModule
{
    public static ControlSyncV2 Instance { get; } = new ControlSyncV2();

    private ControlSyncV2()
    {
        // 场景驱动：不用 SceneManager.sceneLoaded 事件（IL2CPP 下 UnityAction 无法用方法组 += 订阅），
        // 改为每帧比较 GetActiveScene().buildIndex（整数比较无分配），变化即置脏触发注册；
        // 再加 12s 低频兜底（任务场景加载后控件一次性注册，之后增量空跑，去 V1 3s 全场景扫描）。
    }

    private IHostStore Store => HostDataLayer.Instance;
    private ValueLayer Values => ValueLayer.Instance;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2Control;

    /// <summary>低频兜底间隔（秒）：场景变化检测漏掉/控件迟到生成时兜底。原 V1 3s → 12s（任务场景控件一次性注册）。</summary>
    private const float FallbackInterval = 12f;
    private float _fallbackTimer;
    private bool _dirty = true; // 初始置脏：进会话尽快注册
    private int _lastSceneIdx = -1; // 场景 buildIndex 缓存（整数比较，无字符串分配）
    private readonly HashSet<string> _registered = new();

    // ---------------- ISyncedModule ----------------

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;
        // 场景驱动：GetActiveScene().buildIndex 变化 → 置脏（控件是任务场景对象，换场景后重新注册）
        try
        {
            int idx = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
            if (idx != _lastSceneIdx) { _lastSceneIdx = idx; _dirty = true; }
        }
        catch { }
        // 低频兜底（去 3s 全场景扫描）：事件漏掉/迟到生成控件时也能注册
        _fallbackTimer += dt;
        if (_fallbackTimer >= FallbackInterval) { _fallbackTimer = 0f; _dirty = true; }
        if (!_dirty) return;
        _dirty = false;
        Rescan();
    }

    public void OnPacket(ulong from, byte[] data) { } // 无自身消息
    public void OnSessionStarted() { _dirty = true; }
    public void OnSessionEnded() { ClearAll(); }
    public void Reset() { ClearAll(); }

    private void ClearAll()
    {
        Values.Clear();
        _registered.Clear();
        _dirty = true;
    }


    // ---------------- 发现/注册 ----------------

    private void Rescan()
    {
        try
        {
            var current = new HashSet<string>();

            // ---- DialInteractable（刻度盘/旋钮/曲柄）----
            var dials = UnityEngine.Resources.FindObjectsOfTypeAll<DialInteractable>();
            if (dials != null)
                foreach (var d in dials)
                {
                    if (d == null || d.transform == null) continue;
                    var path = PathOf(d.transform);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (IsDisplayOnlyControl(path)) continue;  // 显示/从动型不注册（坐标计算器/链条动画）
                    if (IsRotationGear(path)) continue;        // 方向角 Gear 累积值无限，由 __turret/rotation 覆盖
                    current.Add(path);
                    if (_registered.Contains(path) || Values.Has(path)) continue;
                    var dd = d;
                    // 读取源用 小写 accumulatedValue（可读写 backing）；set 用 SetDialValue
                    var b = Values.RegisterFloat(path,
                        () => dd.accumulatedValue, v => dd.SetDialValue(v),
                        0.001f, true, () => dd.isDragging, V2Authority.Operator);
                    // Locking Lever（锁止拉杆）：物理倾斜角度需一致，不插值（避免持续拉向远端覆盖本地操作）
                    if (path.IndexOf("Locking Lever", StringComparison.OrdinalIgnoreCase) >= 0) b.Interpolate = false;
                    // 弹道计算机/装药/压力/弹舱：跳过心跳（避免无操作反复应用触发摇杆/压力声音循环）
                    if (path.IndexOf("Charge", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("PressureSystem", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("Magazine", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("Ballistic", StringComparison.OrdinalIgnoreCase) >= 0)
                        b.NoHeartbeat = true;
                    _registered.Add(path);
                }

            // ---- 曲柄飞轮（SliderEnergyMomentumSpinner）：同步 Energy 绝对值（惯性/衰减两端分叉根因）----
            var spinners = UnityEngine.Resources.FindObjectsOfTypeAll<SliderEnergyMomentumSpinner>();
            if (spinners != null)
                foreach (var sp in spinners)
                {
                    if (sp == null || sp.transform == null) continue;
                    var path = PathOf(sp.transform) + "/energy";
                    if (string.IsNullOrEmpty(path) || path == "/energy") continue;
                    current.Add(path);
                    if (_registered.Contains(path) || Values.Has(path)) continue;
                    var ss = sp;
                    Values.RegisterFloat(path, () => ss.energy, v => ss.energy = v, 0.2f, true, null, V2Authority.Operator);
                    _registered.Add(path);
                }

            // ---- 炮塔方向角（TurretController.DesiredRotation，有界角度状态，谁操作谁权威）----
            var turrets = UnityEngine.Resources.FindObjectsOfTypeAll<TurretController>();
            if (turrets != null)
                foreach (var t in turrets)
                {
                    if (t == null) continue;
                    const string rotId = "__turret/rotation";
                    current.Add(rotId); // 必须在 current 中，否则 Rescan 尾部移除逻辑会把它清掉 → 反复注册
                    if (_registered.Contains(rotId) || Values.Has(rotId)) continue;
                    var tt = t;
                    var rb = Values.RegisterFloat(rotId,
                        () => tt.DesiredRotation, v => tt.DesiredRotation = v,
                        0.05f, false, () => IsTurretDragging(tt), V2Authority.Operator);
                    rb.HighFreq = true; // 30Hz 高频（方向角专用）
                    _registered.Add(rotId);
                    CoopRuntime.LogSource?.LogInfo($"[SyncV2] turret rotation '{rotId}' registered (Operator, HighFreq) rot={tt.DesiredRotation:0.0}");
                }

            // ---- LinearSliderInteractable（拉杆/滑块）----
            var sliders = UnityEngine.Resources.FindObjectsOfTypeAll<LinearSliderInteractable>();
            if (sliders != null)
                foreach (var s in sliders)
                {
                    if (s == null || s.transform == null) continue;
                    var path = PathOf(s.transform);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (IsDisplayOnlyControl(path)) continue;
                    current.Add(path);
                    if (_registered.Contains(path) || Values.Has(path)) continue;
                    var ss = s;
                    // 物理拉杆：不插值（谁操作谁上行，对端立即跟随，避免插值覆盖本地操作）
                    var b = Values.RegisterFloat(path,
                        () => ss.Value, v => ss.SetSliderValue(v),
                        0.001f, false, () => ss.isDragging, V2Authority.Operator);
                    // 拉环（Trigger/Starter Chain）：拉动时链的位置有视觉表现，30Hz 高频
                    if (IsChain(path)) b.HighFreq = true;
                    // 滚动/仰角物理拉杆（Scroll Lever / Elevation Lever Left/Right）：30Hz 高频（快速拖动低频丢中间值）
                    if (path.IndexOf("Scroll Lever", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("Elevation Lever", StringComparison.OrdinalIgnoreCase) >= 0) b.HighFreq = true;
                    // 仰角"Desired/Current"从动值：client 本地未驱动时读 0，双向同步会上行 0 覆盖 host
                    // （仰角回退根因）→ Host 权威（只接收主机广播，等价 V1 ClientNoSend）
                    if (IsElevationDesiredFollower(path)) b.Authority = V2Authority.Host;
                    _registered.Add(path);
                }

            // ---- 移除已消失控件的绑定（场景切换后旧控件销毁）----
            if (_registered.Count > 0)
            {
                var gone = new List<string>();
                foreach (var p in _registered)
                    if (!current.Contains(p))
                        gone.Add(p);
                foreach (var p in gone)
                {
                    Values.Remove(p);
                    _registered.Remove(p);
                }
            }

            CoopLog.Debug("SyncV2.controlScan", () => $"[SyncV2] ControlSyncV2 rescan registered={_registered.Count} dials={(dials?.Length ?? 0)} sliders={(sliders?.Length ?? 0)}", 5f);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ControlSyncV2] Rescan: {ex.Message}"); }
    }

    // ---------------- 分类辅助（继承 V1 ControlSync 语义） ----------------

    private static string PathOf(Transform t)
    {
        if (t == null) return "";
        string path = t.name ?? "";
        var p = t.parent;
        while (p != null) { path = (p.name ?? "") + "/" + path; p = p.parent; }
        return path;
    }

    /// <summary>方向角是否正在被本地操作（拖方向角 Gear/Lever）——本地操作中暂停远端 DesiredRotation 覆盖。
    /// ⚠️ 不做 FindObjectsOfTypeAll 兜底扫描（V1 有扫描，IL2CPP 下卡帧）。</summary>
    private static bool IsTurretDragging(TurretController turret)
    {
        try
        {
            if (turret == null) return false;
            if (turret.rotationDial != null) { try { if (turret.rotationDial.isDragging) return true; } catch { } }
            if (turret.rotationSpeedDial != null) { try { if (turret.rotationSpeedDial.isDragging) return true; } catch { } }
        }
        catch { }
        return false;
    }

    /// <summary>方向角 Gear（Rotation Console 下 Spur Gear）：accumulatedValue 无限累积，不走 dials 通用注册。</summary>
    private static bool IsRotationGear(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.IndexOf("Rotation Console", StringComparison.OrdinalIgnoreCase) >= 0
            && path.IndexOf("Spur Gear", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>拉环（Trigger/Starter Chain）：拉动时链的位置有视觉表现，需 30Hz 高频。Trigger Chain 点击开火由事件同步。</summary>
    private static bool IsChain(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.IndexOf("Trigger Chain", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf("Starter Chain", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>仰角"Desired/Current"从动值：联动 follower 输出，client 本地未驱动时读 0 → Host 权威只接收。</summary>
    private static bool IsElevationDesiredFollower(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.IndexOf("Elevation Desired", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf("Elevation Current", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>显示/从动型控件（非玩家输入）：不注册（坐标计算器显示/链条动画），避免同步浪费带宽/覆盖游戏本地计算。
    /// 注意：Requisition Control / Fire Mission Card Printer 的方位输入转盘是玩家输入，不跳过。
    /// ⚠️ 修复（2026-08-15）：Range/Bearing Dial 曾整体排除——但炮的 .Range Dial / .Charge Dial 是玩家输入
    /// （拨动设射程/装药），被排除后客机无法同步。显示表盘是 DialGaugeDisplay（非 DialInteractable），
    /// 扫描本就不收；故只保留 Split flap（纯机械显示）排除。</summary>
    private static bool IsDisplayOnlyControl(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.IndexOf("Requisition Control", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (path.IndexOf("Fire Mission Card Printer", StringComparison.OrdinalIgnoreCase) >= 0
            && path.IndexOf("Bearing Dial Parent", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (path.IndexOf("Split flap", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }
}
