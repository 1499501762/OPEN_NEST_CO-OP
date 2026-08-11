using System;
using System.Collections.Generic;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 交互控件同步（刻度盘/旋钮/曲柄 = DialInteractable，滑块/杠杆 = LinearSliderInteractable）。
/// 现为 ValueSync 通用框架的薄封装：只需"发现控件并注册绑定"，统一同步逻辑在 ValueSync。
/// 周期重扫：任务场景加载后才存在的控件（楼梯拉杆、开火台、装药、弹药类型、阀门等）
/// 也会被注册；控件销毁时自动移除绑定。
/// 未来咖啡机/引擎/压力/灯光等设备状态也用同样方式注册到 ValueSync。
/// </summary>
public static class ControlSync
{
    private static float _scanTimer;
    private static readonly HashSet<string> _registeredPaths = new();
    private static int _dialDiag;
    private static int _probeDiag;
    private static int _allLeverDiag;
    private static int _leverRegDiag;
    private static float _logTimer;

    public static void Tick(float dt)
    {
        _logTimer += dt;
        if (_logTimer >= 5f)
        {
            _logTimer = 0f;
            string sample = "";
            int i = 0;
            // 诊断：打印方向角/Lever 相关注册控件（每 4 次 20s，避免刷屏）
            if ((++_leverRegDiag % 4) == 1)
            {
                string leverDiag = "";
                foreach (var p in _registeredPaths)
                {
                    if (p.IndexOf("Rotation", StringComparison.OrdinalIgnoreCase) >= 0
                        || p.IndexOf("Lever", StringComparison.OrdinalIgnoreCase) >= 0
                        || p.IndexOf("Bearing", StringComparison.OrdinalIgnoreCase) >= 0)
                        leverDiag += (leverDiag.Length > 0 ? " | " : "") + p;
                }
                if (leverDiag.Length > 0)
                    CoopRuntime.LogSource?.LogInfo($"[ControlSync] LEVER-REG: {leverDiag}");
            }
            foreach (var p in _registeredPaths)
            {
                if (i >= 3) break;
                sample += (sample.Length > 0 ? " | " : "") + p;
                i++;
            }
            CoopRuntime.LogSource?.LogInfo($"[ControlSync] registered={_registeredPaths.Count} sample=[{sample}]");
            // 诊断：扫描场景里所有方向角/Lever/Bearing 相关控件（含未注册/被跳过的）——定位"方向角 Lever"对象。
            // 每 4 次（20s）一次，避免大段日志刷屏。
            if ((++_allLeverDiag % 4) == 1)
            {
                try
                {
                    var allD = UnityEngine.Resources.FindObjectsOfTypeAll<DialInteractable>();
                    var allS = UnityEngine.Resources.FindObjectsOfTypeAll<LinearSliderInteractable>();
                    string diag2 = "";
                    if (allD != null)
                        foreach (var dd in allD)
                        {
                            if (dd == null || dd.transform == null) continue;
                            string p = PathOf(dd.transform);
                            if (p.IndexOf("Rotation", StringComparison.OrdinalIgnoreCase) >= 0
                                || p.IndexOf("Lever", StringComparison.OrdinalIgnoreCase) >= 0
                                || p.IndexOf("Bearing", StringComparison.OrdinalIgnoreCase) >= 0)
                                diag2 += (diag2.Length > 0 ? " | " : "") + "D:" + p;
                        }
                    if (allS != null)
                        foreach (var ss in allS)
                        {
                            if (ss == null || ss.transform == null) continue;
                            string p = PathOf(ss.transform);
                            if (p.IndexOf("Rotation", StringComparison.OrdinalIgnoreCase) >= 0
                                || p.IndexOf("Lever", StringComparison.OrdinalIgnoreCase) >= 0
                                || p.IndexOf("Bearing", StringComparison.OrdinalIgnoreCase) >= 0)
                                diag2 += (diag2.Length > 0 ? " | " : "") + "S:" + p;
                        }
                    if (diag2.Length > 0)
                        CoopRuntime.LogSource?.LogInfo($"[ControlSync] ALL-LEVER: {diag2}");
                }
                catch { }
                // 诊断：炮塔方向角状态（DesiredRotation / rotationDial backdrive / 当前角度）——
                // 定位"方向角 Lever 数值同步但 Lever 角度（倾斜）不同步"：对端 DesiredRotation 生效后
                // 炮塔是否转到相同角度 → rotationDial/Spur Gear backdrive 是否跟随。
                try
                {
                    var turrets = UnityEngine.Resources.FindObjectsOfTypeAll<TurretController>();
                    if (turrets != null && turrets.Length > 0)
                    {
                        var tt = turrets[0];
                        string td = "";
                        try { td += $" desiredRot={tt.DesiredRotation:0.0}"; } catch { }
                        try
                        {
                            var rd = tt.rotationDial;
                            if (rd != null && rd.transform != null)
                            {
                                td += $" rotDial='{PathOf(rd.transform)}'";
                                try { td += $" av={rd.accumulatedValue:0.0}"; } catch { }
                                try { td += $" drag={rd.isDragging}"; } catch { }
                            }
                            else td += " rotDial=null";
                        }
                        catch { }
                        CoopRuntime.LogSource?.LogInfo($"[ControlSync] TURRET:{td}");
                    }
                }
                catch { }
            }
        }
        _scanTimer -= dt;
        if (_scanTimer <= 0f)
        {
            _scanTimer = 1.0f; // 1.0s（原 0.5s，降低 Rescan 的 FindObjectsOfType 扫描 CPU）
            Rescan();
        }
        ValueSync.Tick(dt);
    }

    public static void OnCmd(ulong from, byte[] data) => ValueSync.OnCmd(from, data);
    public static void OnState(byte[] data) => ValueSync.OnState(data);

    /// <summary>周期重扫场景控件：增量注册新控件，移除已销毁控件的绑定。</summary>
    private static void Rescan()
    {
        try
        {
            var current = new HashSet<string>();

            // ⚠️ 方向角同步：方向角 Gear（rotationDial，即场景 Turret/Rotation Console/.Wheel Parent/.Spur Gear 12 DRIVER）
            // 的 accumulatedValue 是**无限累积**（backdrive 显示炮塔转动圈数，host 持续增长如 -48901、client -25200，
            // 两端基准不同）——同步绝对累积值必然错乱（“主机→客机失败”根因）。
            // **正确方案**：同步 `TurretController.DesiredRotation`（有界角度状态，谁操作谁权威）——对端设置
            // DesiredRotation → 游戏本地逻辑驱动炮塔 → Gear backdrive 自然跟随 → 两端角度一致。
            // 不做插值（直接设值，非插值平滑）。仰角 Lever/Gear（elevationDial/elevationSpeedDial）有界，走 dials 扫描即可。
            var turrets = UnityEngine.Resources.FindObjectsOfTypeAll<TurretController>();
            CoopRuntime.LogSource?.LogInfo($"[ControlSync] TurretController found={(turrets?.Length ?? 0)}");

            var dials = UnityEngine.Resources.FindObjectsOfTypeAll<DialInteractable>();
            if (dials != null)
                foreach (var d in dials)
                {
                    if (d == null || d.transform == null) continue;
                    var path = PathOf(d.transform);
                    if (string.IsNullOrEmpty(path)) continue;
                    // 显示/从动型控件不注册（非玩家输入，同步纯浪费带宽且可能覆盖游戏本地计算）：
                    // - Range/Bearing Dial、Split flap display：坐标计算器显示
                    // - 链条（Starter Chain/Trigger Chain）：机械动画显示
                    // - Locking Lever（锁止拉杆）：走 ButtonClickSync（点击事件），不走值同步
                    if (IsDisplayOnlyControl(path)) continue;
                    // 方向角 Gear（Spur Gear 12 DRIVER）accumulatedValue 无限累积不同步——由专门注册的
                    // DesiredRotation 状态同步覆盖（见后），这里跳过避免用累积值双向同步
                    if (IsRotationGear(path)) continue;
                    current.Add(path);
                    if (_registeredPaths.Contains(path) || ValueSync.Has(path)) continue;
                    var dd = d;
                    // ⚠️ 读取源用 小写 accumulatedValue（可读写 backing 属性）——大写 AccumulatedValue（只读）在 IL2CPP 下读恒 0（曲柄/转盘失效根因）
                    var b = ValueSync.AddFloat(path,
                        () => dd.accumulatedValue, v => dd.SetDialValue(v),
                        0.001f, true, () => dd.isDragging);
                    // 锁止拉杆（Locking Lever Rotation/Elevation）：对端 Lever 角度需一致——
                    // 之前当 display-only 跳过值同步只走点击 → 对端 Lever 停在旧角度（"Lever 角度不同步"根因）。
                    // 不用插值（避免持续拉向远端覆盖本地操作，同 Elevation Lever）；busy=isDragging（谁操作谁权威）。
                    if (path.IndexOf("Locking Lever", StringComparison.OrdinalIgnoreCase) >= 0)
                        b.Interpolate = false;
                    // 弹道计算机/装药相关：跳过心跳（避免无操作时对端反复应用触发摇杆/压力声音循环）
                    if (path.IndexOf("Charge", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("PressureSystem", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("Magazine", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("Ballistic", StringComparison.OrdinalIgnoreCase) >= 0)
                        b.NoHeartbeat = true;
                    _registeredPaths.Add(path);
                }

            // 曲柄飞轮：同步 Energy（绝对值）。飞轮旋转由 Energy 驱动且带惯性/衰减，
            // 仅同步 slider 值会因两端帧率/时间步不同而分叉（主机→客机不同步的根因）。
            var spinners = UnityEngine.Resources.FindObjectsOfTypeAll<SliderEnergyMomentumSpinner>();
            if (spinners != null)
                foreach (var sp in spinners)
                {
                    if (sp == null || sp.transform == null) continue;
                    var path = PathOf(sp.transform) + "/energy";
                    if (string.IsNullOrEmpty(path) || path == "/energy") continue;
                    current.Add(path);
                    if (_registeredPaths.Contains(path) || ValueSync.Has(path)) continue;
                    var ss = sp;
                    ValueSync.AddFloat(path,
                        () => ss.energy, v => ss.energy = v,
                        0.2f, true, () => false);
                    _registeredPaths.Add(path);
                }

            // 方向角同步（专门注册）：方向角 Gear 累积值无限，改同步 `turret.DesiredRotation`（有界角度，
            // 谁操作谁权威——isDragging busy 本地优先）。对端设置 DesiredRotation → 游戏本地逻辑驱动炮塔
            // → Gear backdrive 自然跟随 → 两端角度一致。30Hz 高频 + 不插值（直接设值）。
            if (turrets != null)
                foreach (var t in turrets)
                {
                    if (t == null) continue;
                    const string rotId = "__turret/rotation";
                    // ⚠️ 必须 current.Add(rotId)：否则 Rescan 尾部"移除已消失控件"会因 rotId 不在 current 中
                    // 把它从 _registeredPaths 移除 → 每次 Rescan 重复注册 + binding 反复删建 → 同步不稳定（方向角失效根因）
                    current.Add(rotId);
                    if (ValueSync.Has(rotId)) continue;
                    var tt = t;
                    var rb = ValueSync.AddFloat(rotId,
                        () => tt.DesiredRotation, v => tt.DesiredRotation = v,
                        0.05f, false, () => IsRotationDragging(tt));
                    rb.HighFreq = true; // 30Hz 高频（方向角专用）
                    _registeredPaths.Add(rotId);
                    CoopRuntime.LogSource?.LogInfo($"[ControlSync] turret rotation '{rotId}' reg (DesiredRotation, bidirectional, HighFreq) rot={tt.DesiredRotation:0.0}");
                }

            var sliders = UnityEngine.Resources.FindObjectsOfTypeAll<LinearSliderInteractable>();
            if (sliders != null)
                foreach (var s in sliders)
                {
                    if (s == null || s.transform == null) continue;
                    var path = PathOf(s.transform);
                    if (string.IsNullOrEmpty(path)) continue;
                    // 显示/从动型控件不注册（链条动画、坐标显示等）
                    if (IsDisplayOnlyControl(path)) continue;
                    current.Add(path);
                    if (_registeredPaths.Contains(path) || ValueSync.Has(path)) continue;
                    var ss = s;
                    // 仰角物理拉杆（Elevation Lever Left/Right）：输入源，物理位置双向同步。
                    // ⚠️ 不用插值（Interpolate=false）——插值会持续把本地 Lever 拉向远端目标，覆盖本地操作
                    // （"客机 Lever 数值回退 0"根因：host 广播值 0.35 插值覆盖 client 拉到 35 的操作）。
                    // 直接应用：谁操作谁上行，对端立即跟随，互不打架。
                    var b = ValueSync.AddFloat(path,
                        () => ss.Value, v => ss.SetSliderValue(v),
                        0.001f, false, () => ss.isDragging);
                    // 拉环（Trigger/Starter Chain）：拉动时链的位置有视觉表现，需对端高频一致
                    // （30Hz HighFreq，与炮塔 Lever/Gear 同级——拉环快速拉动时低频会丢中间位置）
                    if (IsChain(path))
                    {
                        b.HighFreq = true;
                    }
                    // 仰角物理拉杆（Elevation Lever Left/Right）：30Hz 高频——快速拖动时 0.2s 低频
                    // 会丢中间值 → 两端 Lever 物理位置不同步（Synchrony TurretLeverBridge 同 0.033s）。
                    if (path.IndexOf("Elevation Lever", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        b.HighFreq = true;
                    }
                    // 仰角"Desired"从动值（联动 follower 输出）：client 本地未驱动时读 0，双向同步会上行 0 覆盖 host
                    // （仰角回退根因）。只接收 host 广播（ClientNoSend）。
                    if (IsElevationDesiredFollower(path))
                    {
                        b.ClientNoSend = true;
                    }
                    // 诊断：仰角 Lever / 方向角相关控件注册状态（ClientNoSend/ClientNoApply）
                    if (path.IndexOf("Elevation Lever", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("Elevation Desired", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("Spur Gear", StringComparison.OrdinalIgnoreCase) >= 0)
                        CoopRuntime.LogSource?.LogInfo($"[ControlSync] reg '{path}' noSend={b.ClientNoSend} noApply={b.ClientNoApply} drag={ss.isDragging} val={ss.Value:0.00}");
                    _registeredPaths.Add(path);
                }

            CoopRuntime.LogSource?.LogInfo($"[ControlSync] scan dials={(dials?.Length ?? 0)} sliders={(sliders?.Length ?? 0)}");

            // 诊断：列出前 6 个控件路径 + 值（低频每 20 次 Rescan≈10s，避免 0.5s 大日志刷屏卡顿）
            if ((++_probeDiag % 20) == 1)
            {
                string vals = "";
                if (dials != null)
                    for (int i = 0; i < dials.Length && i < 6; i++)
                    {
                        string nm = "";
                        float av = 0f;
                        try { nm = PathOf(dials[i].transform); } catch { nm = "PATH_ERR"; }
                        try { av = dials[i].AccumulatedValue; } catch { }
                        vals += (vals.Length > 0 ? " | " : "") + "d:" + nm + "=" + av.ToString("0.00");
                    }
                if (sliders != null)
                    for (int i = 0; i < sliders.Length && i < 6; i++)
                    {
                        string nm = "";
                        float sv = 0f;
                        try { nm = PathOf(sliders[i].transform); } catch { nm = "PATH_ERR"; }
                        try { sv = sliders[i].Value; } catch { }
                        vals += (vals.Length > 0 ? " | " : "") + "s:" + nm + "=" + sv.ToString("0.00");
                    }
                if (vals.Length > 0)
                    CoopRuntime.LogSource?.LogInfo($"[ControlSync] probe= {vals}");
            }

            // 转盘专项诊断（低频）：方向角/仰角/瞄准转盘——确认 AccumulatedValue 读取 + isDragging
            if ((++_dialDiag % 40) == 1 && dials != null)
            {
                foreach (var d in dials)
                {
                    if (d == null || d.transform == null) continue;
                    string nm = "";
                    try { nm = PathOf(d.transform); } catch { continue; }
                    if (nm.IndexOf("Spur Gear", StringComparison.OrdinalIgnoreCase) < 0
                        && nm.IndexOf("Elevation", StringComparison.OrdinalIgnoreCase) < 0
                        && nm.IndexOf("Aiming", StringComparison.OrdinalIgnoreCase) < 0
                        && nm.IndexOf("Rotation Console", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    float av = 0f;
                    bool drag = false;
                    try { av = d.accumulatedValue; } catch { }
                    try { drag = d.isDragging; } catch { }
                    // Locking Lever（锁止拉杆）视觉角度可能由 transform 旋转表示（accumulatedValue 恒 0）——
                    // 打印 localEulerAngles 确认旋转轴/值，用于同步 Lever 物理倾斜。
                    string rot = "";
                    try { var lr = d.transform.localEulerAngles; rot = $"({lr.x:0.#},{lr.y:0.#},{lr.z:0.#})"; } catch { }
                    CoopRuntime.LogSource?.LogInfo($"[ControlSync] dial-diag '{nm}' av={av:0.###} drag={drag} rot={rot}");
                }
            }

            // 移除已消失控件的绑定（场景切换后旧控件销毁）
            if (_registeredPaths.Count > 0)
            {
                var gone = new List<string>();
                foreach (var p in _registeredPaths)
                    if (!current.Contains(p))
                        gone.Add(p);
                foreach (var p in gone)
                {
                    ValueSync.Remove(p);
                    _registeredPaths.Remove(p);
                }
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ControlSync Rescan: {ex.Message}"); }
    }

    /// <summary>场景路径（根 → 当前），用于跨端定位同一控件。</summary>
    private static string PathOf(Transform t)
    {
        if (t == null) return "";
        string path = t.name ?? "";
        var p = t.parent;
        while (p != null)
        {
            path = (p.name ?? "") + "/" + path;
            p = p.parent;
        }
        return path;
    }

    // ---------------- 炮塔控制输入统一识别（TurretSync 共用） ----------------

    /// <summary>判断控件路径是否为"炮塔曲柄输入"（方向角/仰角曲柄 Spur Gear）。
    /// 这些曲柄是 TurretController.rotationDial/elevationDial（从动刻度盘）：
    /// 玩家拖曲柄 → 游戏本地驱动 DesiredRotation/Elevation → 炮塔转 → backdrive 曲柄视觉跟随。
    /// 曲柄值（累积角度，巨大如 -25000）**不走 ValueSync**（上行会把 host 曲柄拧到几十圈 → 曲柄失效根因）。
    /// 注意：仰角物理拉杆（Elevation Lever）是**输入源**，物理位置应双向同步（不在此列）；Elevation Desired 是联动从动值（单独处理）。</summary>
    public static bool IsTurretCrankInput(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        // 曲柄（Spur Gear）——方向角（Rotation Console）与仰角（Elevation Console）下都是
        if (path.IndexOf("Spur Gear", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        // 方向角控制台（Rotation Console 下的操纵杆/曲柄，backdrive 从动）
        if (path.IndexOf("Rotation Console", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        // 仰角控制台下的曲柄已由 Spur Gear 覆盖，这里兜底（排除 Elevation Lever 物理拉杆）
        if (path.IndexOf("Elevation Console", StringComparison.OrdinalIgnoreCase) >= 0
            && path.IndexOf("Elevation Lever", StringComparison.OrdinalIgnoreCase) < 0) return true;
        return false;
    }

    /// <summary>仰角"Desired/Current"从动值（Elevation Desired Left/Right、Elevation Current Left/Right）：
    /// GunElevationLink 联动 follower 的输出（Desired=期望、Current=当前），游戏内部从 Lever 计算，
    /// client 本地未驱动时读 0 → 若双向同步会上行 0 覆盖 host（仰角回退根因）。
    /// 只接收 host 广播（ClientNoSend），不参与上行；真正的输入源是 Elevation Lever Left/Right（物理拉杆）。</summary>
    public static bool IsElevationDesiredFollower(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.IndexOf("Elevation Desired", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf("Elevation Current", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>拉环（Trigger/Starter Chain）：触发开火/重启引擎的拉环，拉动时链的位置有视觉表现，
    /// 需对端高频一致（30Hz HighFreq）。Trigger Chain 的点击开火由 GunFire 事件同步，这里只同步链的位置视觉。</summary>
    public static bool IsChain(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.IndexOf("Trigger Chain", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf("Starter Chain", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>方向角 Gear（Turret/Rotation Console/.Wheel Parent/.Spur Gear 12 DRIVER）：
    /// accumulatedValue 无限累积（backdrive 显示炮塔圈数），同步累积值两端基准不同必然错乱，
    /// 不走 dials 通用注册——由专门注册的 DesiredRotation 状态同步覆盖。</summary>
    public static bool IsRotationGear(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        // 方向角控制台下的 Spur Gear（区别于仰角 Spur Gear）
        return path.IndexOf("Rotation Console", StringComparison.OrdinalIgnoreCase) >= 0
            && path.IndexOf("Spur Gear", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>方向角是否正在被本地操作（拖方向角 Gear/Lever）——本地操作中暂停远端 DesiredRotation 覆盖。</summary>
    public static bool IsRotationDragging(TurretController turret)
    {
        try
        {
            if (turret == null) return false;
            if (turret.rotationDial != null) { try { if (turret.rotationDial.isDragging) return true; } catch { } }
            if (turret.rotationSpeedDial != null) { try { if (turret.rotationSpeedDial.isDragging) return true; } catch { } }
            // 兜底：扫描方向角 Gear 拖拽
            var dials = UnityEngine.Resources.FindObjectsOfTypeAll<DialInteractable>();
            if (dials != null)
                foreach (var d in dials)
                {
                    if (d == null || !d.isDragging || d.transform == null) continue;
                    if (IsRotationGear(PathOf(d.transform))) return true;
                }
        }
        catch { }
        return false;
    }

    /// <summary>显示/从动型控件（非玩家输入）：不注册到 ValueSync。
    /// 这些是游戏内部计算的显示（坐标计算器、锁止拉杆），同步无意义且浪费带宽，
    /// 甚至会覆盖游戏本地计算导致异常。锁止拉杆走 ButtonClickSync（点击事件）。
    /// 注意：拉环（Trigger/Starter Chain）**保留**位置值同步——它们不是纯显示，拉动拉环时
    /// 链的位置有视觉表现，需对端一致（Starter Chain 还兼作重启引擎的点击事件，走 ButtonClickSync）。</summary>
    public static bool IsDisplayOnlyControl(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        // ⚠️ Requisition Control Panel 的坐标计算器 dial（ConsoleControl_CoordinatesBearing 下）：
        // 是玩家**输入**（调方位角/距离，如 .fine Range Dial / .Gross Bearing Dial / Range Dial），
        // 非纯显示——必须保留 ValueSync 同步（用户操作对端要跟随）。优先于下方 Range/Bearing 跳过判断。
        if (path.IndexOf("Requisition Control", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        // ⚠️ Fire Mission Card Printer 的方位输入转盘（Bearing Dial Parent 下，如 .fine Range Dial /
        // .Gross Range Dial）：玩家**输入**射击任务方位（和 Requisition Control 坐标计算器 dial 同类），
        // 非纯显示——保留同步（否则下方 Range Dial 规则会误判为显示控件跳过）。
        if (path.IndexOf("Fire Mission Card Printer", StringComparison.OrdinalIgnoreCase) >= 0
            && path.IndexOf("Bearing Dial Parent", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        // 坐标计算器显示（Range/Bearing Dial、Split flap display）
        if (path.IndexOf("Range Dial", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (path.IndexOf("Bearing Dial", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (path.IndexOf("Split flap", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (path.IndexOf("Split flap display", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        // ⚠️ Locking Lever（仰角/方向角锁止拉杆）**不**当纯显示跳过：它是可调节角度的拨杆
        // （DialInteractable，拖到不同角度保持），用户操作后对端 Lever 角度需一致 → 注册值同步
        // （ControlSync dials 循环里已给 Locking Lever 设 Interpolate=false）。点击同步（ButtonClickSync）保留兜底。
        return false;
    }
}
