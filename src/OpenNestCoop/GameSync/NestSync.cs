using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 铁巢（TurretController）位置同步（MsgType=146，2026-08-26 新增，对齐 Synchrony NestMoveBridge）。
/// 铁巢是玩家基地/堡垒（TurretController）——炮弹从铁巢坐标发射到着弹点，追踪器（Map Table_ Shell
/// Trajectory display）从铁巢坐标一路移动到着弹点。两端铁巢位置初始化不同 → 追踪器轨迹/炮弹落点/打字机
/// [GRID &lt;turret&gt;] 等依赖铁巢基准的功能两端不同（“追踪器还是不同步”根因）。
/// 方案（对齐 Synchrony）：**主机权威**——patch TurretController.MoveTurret/SetTurretLocation，
/// 主机移动铁巢 postfix 广播位置，客机拦截本地移动（除非正在应用广播），接收后防环应用。
/// 铁巢位置两端一致 → 依赖铁巢坐标的本地计算（追踪器/落点/打字机坐标）两端一致。
/// </summary>
public sealed class NestSync : ISyncedModule
{
    public int MsgType => (byte)OpenNestCoop.Net.MsgType.NestMove; // 146

    // ⚠️ 模块自注册：程序集加载时入队（V1 方案），Startup FlushPending 统一注册
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SelfRegister() => CoopSyncRegistry.PendingRegister(false, () => new NestSync());
    public static NestSync Instance;

    /// <summary>防环：正在应用远端铁巢位置（应用时放行本地 MoveTurret/SetTurretLocation，不重复广播）。</summary>
    private static bool _applying;
    /// <summary>铁巢 actual 位置诊断计数（降频）。</summary>
    private static int _actualLog;

    // ⚠️ 2026-09-12：**炮弹起点基准** = 铁巢在战术地图上的图标 `Tactical Map/Canvas/MapRoot/TurretLocation`
    // （`ShellVisual.Initialize` 的 startPos = `GunController.firePoint` = 这个 Transform 的世界位置 →
    //  换算到 board 本地坐标）。实测：铁巢（TurretController）世界位置两端一致，但**客机这个图标停在场景默认值**
    // （两局都是 local (1.550,1.550)）→ 炮弹起点不同 → 整条弹道平移 → 落点不同（aim/射程/飞行时长完全相同）。
    // 故与铁巢同位一起主机权威同步（图标位置/旋转）。
    private static Transform _icon;
    private static float _iconScanAt = -99f;
    private static Vector3 _iconPos;
    private static Quaternion _iconRot = Quaternion.identity;
    private static bool _iconHas;
    private static int _iconLog;
    private float _iconTimer;

    public NestSync() { Instance = this; }

    /// <summary>Hosting/Joined 判断（网络是否在联机任务）。</summary>
    private static bool Online()
    {
        var net = CoopRuntime.Net;
        if (net == null) return false;
        return net.State == SessionState.Hosting || net.State == SessionState.Joined;
    }

    /// <summary>是否主机（权威端：本地移动铁巢 + 广播；客机只接收应用）。</summary>
    private static bool IsHost()
    {
        var net = CoopRuntime.Net;
        return net != null && net.IsHost;
    }

    // ---------------- Harmony patch（HarmonyPatches.Apply 调用） ----------------

    /// <summary>TurretController.MoveTurret/SetTurretLocation prefix：主机放行 + 广播；
    /// 客机拦截本地移动（铁巢位置主机权威，客机靠广播应用）。应用广播时（_applying）放行。</summary>
    public static bool PreTurretMove(Vector3 worldPos)
    {
        if (!Online()) return true;            // 单机/未联机：正常移动
        if (IsHost()) return true;             // 主机：正常移动（postfix 广播）
        if (_applying) return true;            // 客机正在应用远端广播：放行（不重复广播）
        return false;                          // 客机本地移动铁巢：拦截（铁巢位置主机权威，等主机广播）
    }

    /// <summary>TurretController.MoveTurret postfix：主机移动铁巢 → 广播位置（reliable）。</summary>
    public static void PostTurretMove(Vector3 worldPos)
    {
        // A1：脚本化模块事件——铁巢移动（脚本模块可订阅 turret.moved）
        try { OncMissionBridge.Raise("turret.moved", worldPos); } catch { }
        if (!Online() || !IsHost() || _applying) return;
        BroadcastPos(worldPos, instant: false);
    }

    /// <summary>TurretController.SetTurretLocation postfix：主机设置铁巢位置（snap）→ 广播。</summary>
    public static void PostTurretSetLocation(Vector3 worldPos)
    {
        // A1：脚本化模块事件——铁巢位置设置（脚本模块可订阅 turret.moved）
        try { OncMissionBridge.Raise("turret.moved", worldPos); } catch { }
        if (!Online() || !IsHost() || _applying) return;
        BroadcastPos(worldPos, instant: true);
    }

    /// <summary>广播铁巢位置（reliable：位置必须可靠送达，丢了对齐失效）。</summary>
    private static void BroadcastPos(Vector3 pos, bool instant)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var w = NetProtocol.Begin((OpenNestCoop.Net.MsgType)Instance.MsgType);
            w.Put(pos.x); w.Put(pos.y); w.Put(pos.z);
            w.Put(instant ? (byte)1 : (byte)0);
            // ⚠️ 2026-09-12：附加块——铁巢地图图标（炮弹起点）的 localPosition/localRotation。
            // 旧版客机读不到时保持原样（客机按 AvailableBytes 判定）。
            try
            {
                var icon = Icon();
                if (icon != null)
                {
                    var lp = icon.localPosition; var lr = icon.localRotation;
                    w.Put(lp.x); w.Put(lp.y); w.Put(lp.z);
                    w.Put(lr.x); w.Put(lr.y); w.Put(lr.z); w.Put(lr.w);
                }
            }
            catch { }
            net.EnqueueBatch(NetProtocol.Snapshot(w), true); // toAll reliable
            CoopLog.Info("nest.move", () => $"[NestSync] host broadcast pos=({pos.x:0.##},{pos.y:0.##},{pos.z:0.##}) instant={instant}", 0.5f);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"NestSync Broadcast: {ex.Message}"); }
    }

    /// <summary>客户端：收到铁巢位置 → 防环应用（TurretController 移动铁巢）。</summary>
    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return; // 仅客户端处理（主机权威）
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            float x = r.GetFloat(); float y = r.GetFloat(); float z = r.GetFloat();
            bool instant = r.GetByte() != 0;
            var pos = new Vector3(x, y, z);
            // ⚠️ 2026-09-12：附加块（铁巢地图图标 = 炮弹起点基准）。
            bool hasIcon = false;
            try
            {
                if (r.AvailableBytes >= 28)
                {
                    var ip = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    var ir = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
                    _iconPos = ip; _iconRot = ir; _iconHas = true; hasIcon = true;
                }
            }
            catch { hasIcon = false; }
            Apply(pos, instant);
            if (hasIcon) ApplyIcon();
            CoopLog.Info("nest.move.recv", () => $"[NestSync] client apply pos=({x:0.##},{y:0.##},{z:0.##}) instant={instant}", 0.5f);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"NestSync OnPacket: {ex.Message}"); }
    }

    /// <summary>应用铁巢位置（防环：_applying 放行本地 MoveTurret/SetTurretLocation，不触发 postfix 重复广播）。
    /// ⚠️ 2026-08-26 精度修复：客机**始终用 SetTurretLocation（即时精确）**，不用 MoveTurret（插值）——
    /// MoveTurret 插值移动会让客机铁巢位置滞后于主机广播（移动中轻微偏移 = "铁巢轻微不同步（精度）"根因）。
    /// 铁巢是坐标基准（追踪器/落点/打字机 [GRID] 依赖），精度优先于平滑动画。SetTurretLocation 即时到位，
    /// 客机铁巢位置精确 = 主机广播值。instant 标志保留（诊断区分 snap/move）。</summary>
    private static void Apply(Vector3 pos, bool instant)
    {
        try
        {
            var tt = TurretController.Instance;
            if (tt == null) return;
            _applying = true;
            try
            {
                // ⚠️ 2026-08-26：统一用 SetTurretLocation（即时精确），避免 MoveTurret 插值滞后。
                // 铁巢位置低频变化（玩家移动/任务），即时设置无可见跳变感，但坐标精确（基准要求）。
                tt.SetTurretLocation(pos);
            }
            finally { _applying = false; }
            // ⚠️ 精度诊断：应用后读实际位置（SetTurretLocation 即时，delta 应=0）。用 LogSource 直调确保
            // 显示（CoopLog 的 nest 前缀未注册路由，Info 可能被过滤）。
            try
            {
                if (tt.transform != null)
                {
                    var ap = tt.transform.position;
                    float dx = ap.x - pos.x, dy = ap.y - pos.y, dz = ap.z - pos.z;
                    if ((++_actualLog % 5) == 1)
                        CoopRuntime.LogSource?.LogInfo($"[NestSync] client actual pos=({ap.x:0.###},{ap.y:0.###},{ap.z:0.###}) delta=({dx:0.###},{dy:0.###},{dz:0.###}) instant={instant}");
                }
            }
            catch { }
        }
        catch { }
    }

    public void Tick(float dt)
    {
        // ⚠️ 2026-09-12 铁巢地图图标（炮弹起点基准）同步：
        //  - 主机：1.5s 心跳广播（含图标）——图标只在任务开始时被游戏设置一次，单次包丢了就永久不同步；
        //  - 客机：图标晚生成（场景切换/对象未激活）时补应用。
        _iconTimer += dt;
        if (_iconTimer >= 1.5f)
        {
            _iconTimer = 0f;
            try
            {
                if (IsHost() && Online())
                {
                    var ttn = TurretController.Instance;
                    if (ttn != null && ttn.transform != null) BroadcastPos(ttn.transform.position, instant: true);
                }
                else if (_iconHas)
                {
                    ApplyIcon();
                }
            }
            catch { }
        }
        // ⚠️ 2026-08-26 铁巢角度诊断（只读，不改同步）：定期读两端铁巢 transform 的 position + rotation，
        // 确认铁巢角度（rotation）是否两端一致——追踪器（TrajectoryTarget 本地坐标跟随）可能依赖铁巢朝向，
        // 若 rotation 两端不同 → 追踪器方向/起点不同（"弹道追踪器没同步"+"铁巢角度同步可能有问题"）。
        _diagTimer += dt;
        if (_diagTimer >= 5f)
        {
            _diagTimer = 0f;
            try
            {
                var tt = TurretController.Instance;
                if (tt != null && tt.transform != null)
                {
                    var p = tt.transform.position;
                    var r = tt.transform.rotation.eulerAngles;
                    CoopRuntime.LogSource?.LogInfo($"[NestSync] {(IsHost() ? "HOST" : "CLIENT")} pos=({p.x:0.###},{p.y:0.###},{p.z:0.###}) rot=({r.x:0.##},{r.y:0.##},{r.z:0.##})");
                }
            }
            catch { }
            // ⚠️ 2026-08-26 追踪器起点诊断：读弹道追踪器对象（名字含 "Trajectory display"）位置——用户
            // "客机弹道追踪器的起始点不对"：确认追踪器起点（炮弹发射点）两端是否一致。
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<UnityEngine.Transform>(true);
                if (all != null)
                {
                    int shown = 0;
                    foreach (var tr in all)
                    {
                        if (tr == null || tr.name == null || shown >= 3) continue;
                        // ⚠️ 2026-08-31：精确匹配 "Trajectory"（弹道轨迹显示名 "Shell Trajectory display"），
                        // 原 "Trajectory|display" OR 逻辑误匹配勋表/装药计算显示（Medal Display Name/Calculated Charge Display）。
                        if (tr.name.IndexOf("Trajectory", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                        shown++;
                        var tp = tr.position;
                        CoopRuntime.LogSource?.LogInfo($"[NestSync] {(IsHost() ? "HOST" : "CLIENT")} trajectory '{tr.name}' pos=({tp.x:0.##},{tp.y:0.##},{tp.z:0.##})");
                    }
                }
            }
            catch { }
        }
    }
    private float _diagTimer;

    /// <summary>铁巢在战术地图上的图标（炮弹起点基准）——先按路径取，取不到再扫全场景（3s 节流，避免卡顿）。</summary>
    private static Transform Icon()
    {
        if (_icon != null) return _icon;
        try
        {
            var go = UnityEngine.GameObject.Find("Tactical Map/Canvas/MapRoot/TurretLocation");
            if (go != null) { _icon = go.transform; return _icon; }
        }
        catch { }
        if (Time.time - _iconScanAt < 3f) return null;
        _iconScanAt = Time.time;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            if (all != null)
                foreach (var t in all)
                {
                    if (t == null || t.name == null) continue;
                    if (t.name == "TurretLocation") { _icon = t; break; }
                }
        }
        catch { }
        return _icon;
    }

    /// <summary>客机：把主机铁巢图标位置/旋转写到本端图标（幂等；图标未就绪则等 Tick 再试）。</summary>
    private static void ApplyIcon()
    {
        var icon = Icon();
        if (icon == null) return;
        try { icon.localPosition = _iconPos; } catch { }
        try { icon.localRotation = _iconRot; } catch { }
        if ((++_iconLog % 5) == 1)
            CoopRuntime.LogSource?.LogInfo($"[NestSync] client icon apply pos=({_iconPos.x:0.###},{_iconPos.y:0.###},{_iconPos.z:0.###})");
    }
    public void OnSessionStarted() { }
    public void OnSessionEnded() { }
    public void Reset() { _applying = false; }
}
