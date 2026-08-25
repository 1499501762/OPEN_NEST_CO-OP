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
    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.NestMove; // 146
    public static NestSync Instance;

    /// <summary>防环：正在应用远端铁巢位置（应用时放行本地 MoveTurret/SetTurretLocation，不重复广播）。</summary>
    private static bool _applying;

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
        if (!Online() || !IsHost() || _applying) return;
        BroadcastPos(worldPos, instant: false);
    }

    /// <summary>TurretController.SetTurretLocation postfix：主机设置铁巢位置（snap）→ 广播。</summary>
    public static void PostTurretSetLocation(Vector3 worldPos)
    {
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
            Apply(pos, instant);
            CoopLog.Info("nest.move.recv", () => $"[NestSync] client apply pos=({x:0.##},{y:0.##},{z:0.##}) instant={instant}", 0.5f);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"NestSync OnPacket: {ex.Message}"); }
    }

    /// <summary>应用铁巢位置（防环：_applying 放行本地 MoveTurret/SetTurretLocation，不触发 postfix 重复广播）。</summary>
    private static void Apply(Vector3 pos, bool instant)
    {
        try
        {
            var tt = TurretController.Instance;
            if (tt == null) return;
            _applying = true;
            try
            {
                if (instant) tt.SetTurretLocation(pos);
                else tt.MoveTurret(pos);
            }
            finally { _applying = false; }
        }
        catch { }
    }

    public void Tick(float dt) { /* 事件驱动（MoveTurret/SetTurretLocation patch） */ }
    public void OnSessionStarted() { }
    public void OnSessionEnded() { }
    public void Reset() { _applying = false; }
}
