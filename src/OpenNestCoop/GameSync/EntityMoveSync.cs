using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 移动实体命令同步（2026-08-31）：同步任务图移动节点 <see cref="SleepyNodes.State_MoveMapEntity"/> 的移动命令——
/// 列车/移动目标实际由该节点驱动（非 FireMission.MoveMapEntity，后者从未被任务图触发）。
/// 主机 State_MoveMapEntity.OnEnter 触发时解析出实体+目标格子并广播，客机跳过本地 OnEnter，
/// 收到广播后用 FireMission.MoveMapEntity 4 参执行（走游戏原生 Internal_MoveEntity 协程插值），
/// 两端移动轨迹天然一致（修"任务图两端时间戳基准不同 → 插值相位不同 → 列车漂移"根因）。
/// 触发时刻差 = 网络延迟（~170ms），对慢速移动实体影响极小且不累积漂移。
/// </summary>
public sealed class EntityMoveSync : ISyncedModule
{
    /// <summary>自注册通道键（稳定字符串，双端一致；前导字节由 NetManager 分配）。</summary>
    public const string ChannelKey = "entity.move.cmd";

    /// <summary>本模块前导字节：由注册管理器为 ChannelKey 分配（动态注册）。</summary>
    public int MsgType => CoopRuntime.Net?.ChannelType(ChannelKey) ?? 0;

    public NetModulePriority NetPriority => NetModulePriority.Normal;

    /// <summary>客机收到广播后执行 MoveMapEntity 时的放行标志——
    /// <c>PreMoveMapEntity</c> 据此区分“本地任务图触发”（拦截）vs“广播驱动执行”（放行）。</summary>
    public static bool IsApplyingRemote;

    // ⚠️ 模块自注册：程序集加载时入队（V1 方案，动态通道），Startup FlushPending 统一注册
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SelfRegister() => CoopSyncRegistry.PendingRegister(false, () => new EntityMoveSync(), ChannelKey);

    /// <summary>主机广播移动命令：entityId + 目标世界坐标 + 是否平滑移动 + 用时 + 状态更新（可选）。</summary>
    public static void Broadcast(string entityId, float worldX, float worldZ, bool smooth, float timespan, bool updateState, int stateToAdd)
    {
        try
        {
            if (string.IsNullOrEmpty(entityId)) return;
            if (IsApplyingRemote) return; // 客机广播驱动执行时不重复广播
            var net = CoopRuntime.Net;
            if (net == null) return;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
            int type = net.ChannelType(ChannelKey);
            if (type == 0) return;
            var w = NetProtocol.Begin((MsgType)type);
            w.Put(entityId);
            w.Put(worldX); w.Put(worldZ);
            w.Put(smooth ? (byte)1 : (byte)0);
            w.Put(timespan);
            w.Put(updateState ? (byte)1 : (byte)0);
            w.Put(stateToAdd);
            var data = NetProtocol.Snapshot(w);
            if (net.IsHost)
            {
                foreach (var p in net.Roster)
                    if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
            CoopLog.Info("EntityMoveSync.broadcast", () => $"[EntityMove] broadcast '{entityId}' -> ({worldX:0.##},{worldZ:0.##}) t={timespan:0.##} smooth={smooth} updState={updateState} state={stateToAdd}", 1f);
        }
        catch (Exception ex) { CoopLog.Warn("EntityMoveSync.broadcast", () => $"EntityMoveSync broadcast error: {ex.Message}"); }
    }

    public void Tick(float dt) { }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { IsApplyingRemote = false; }
    public void Reset() { IsApplyingRemote = false; }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return; // 仅客机处理（主机是移动发起方）
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            string entityId = r.GetString();
            float worldX = r.GetFloat();
            float worldZ = r.GetFloat();
            bool smooth = r.GetByte() != 0;
            float timespan = r.GetFloat();
            bool updateState = false;
            int stateToAdd = 0;
            if (r.AvailableBytes > 0) updateState = r.GetByte() != 0;
            if (r.AvailableBytes > 0) stateToAdd = r.GetInt();
            // 用相同参数执行 MoveMapEntity（游戏原生插值，两端轨迹一致）
            try
            {
                var fm = FireMission.Instance;
                if (fm == null || fm.Entities == null) return;
                MapEntity ent = null;
                try { if (fm.Entities.TryGetValue(entityId ?? "", out var e)) ent = e; } catch { }
                if (ent == null) return;
                IsApplyingRemote = true;
                try
                {
                    fm.MoveMapEntity(ent, new Vector3(worldX, 0, worldZ), smooth, timespan);
                    if (updateState) fm.SetEntityState(ent, (MapEntityStates)stateToAdd);
                }
                finally { IsApplyingRemote = false; }
                CoopLog.Info("EntityMoveSync.apply", () => $"[EntityMove] apply '{entityId}' -> ({worldX:0.##},{worldZ:0.##}) t={timespan:0.##} smooth={smooth} updState={updateState} state={stateToAdd}", 1f);
            }
            catch (Exception ex) { CoopLog.Warn("EntityMoveSync.apply", () => $"EntityMoveSync apply error: {ex.Message}"); }
        }
        catch (Exception ex) { CoopLog.Warn("EntityMoveSync.recv", () => $"EntityMoveSync OnPacket error: {ex.Message}"); }
    }
}
