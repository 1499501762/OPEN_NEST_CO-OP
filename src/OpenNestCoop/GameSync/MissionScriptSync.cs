using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 脚本化模块事件广播（A4 联机）：主机权威把"脚本事件"广播给全员，客机收到后本地
/// <see cref="OncMissionBridge.Raise"/>（触发脚本模块事件订阅 B + Core 图 WaitForEvent/Branch）。
///
/// 触发：脚本模块调 <see cref="OncScriptContext.Broadcast"/>（自定义/主机逻辑事件需要跨端一致时），
/// 经 <see cref="OncMissionBridge"/> 默认宿主 → 本模块 <see cref="Broadcast"/> 发送。
/// 游戏事件（shell.landed / interact.click / entity.destroyed 等）两端本地自然触发，**不需要**广播
/// （广播反而双触发），脚本模块请用本地 <see cref="OncScriptContext.Raise"/>。
///
/// ⚠️ 消息类型：**函数注册**（不再硬编码枚举）——稳定 channelKey 自注册，由 NetManager 注册管理器
/// 统一分配前导字节（见 CoffeeSync 参考实现）。发送经 <see cref="MsgType"/> 取分配字节。
/// </summary>
public sealed class MissionScriptSync : ISyncedModule
{
    /// <summary>自注册通道键（稳定字符串，双端一致；前导字节由 NetManager 分配）。</summary>
    public const string ChannelKey = "mission.script.event";

    /// <summary>本模块前导字节：由注册管理器为 ChannelKey 分配（动态注册）。</summary>
    public int MsgType => OpenNestCoop.Core.CoopRuntime.Net?.ChannelType(ChannelKey) ?? 0;

    /// <summary>应用远端广播时的防环标志（收到广播后不再重复上报/广播）。</summary>
    public static bool IsApplying;

    // ⚠️ 模块自注册：程序集加载时入队（V1 方案，动态通道），Startup FlushPending 统一注册
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SelfRegister() => CoopSyncRegistry.PendingRegister(false, () => new MissionScriptSync(), ChannelKey);

    /// <summary>
    /// 广播脚本事件（主机权威）：主机 → 全员；客机调用则只发主机（由主机决定是否转发）。
    /// 主机本地已由调用方 Raise（BroadcastScriptEvent 内先本地后广播），收到包不重复 Apply。
    /// </summary>
    public static void Broadcast(string eventId)
    {
        try
        {
            if (string.IsNullOrEmpty(eventId)) return;
            if (IsApplying) return;
            var net = CoopRuntime.Net;
            if (net == null) return;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
            int type = net.ChannelType(ChannelKey);
            if (type == 0) return; // 未注册
            var w = NetProtocol.Begin((MsgType)type);
            w.Put(eventId);
            var data = NetProtocol.Snapshot(w);
            if (net.IsHost)
            {
                foreach (var p in net.Roster)
                    if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
            else if (net.HostSteamId != 0)
            {
                // 客机：上报主机（主机决定转发）
                net.Transport.Send(net.HostSteamId, data, true);
            }
            CoopLog.Debug("onc.mission.script", () => $"OncMission script event broadcast: '{eventId}' isHost={net.IsHost}");
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.script", () => $"OncMission script broadcast error: {ex.Message}"); }
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
            string eventId = r.GetString();
            // 应用防环：本端 Broadcast（广播者已本地 Raise）不重复；收到包则需本地 Raise
            if (IsApplying) return;
            IsApplying = true;
            try
            {
                if (net.IsHost)
                {
                    // 主机：转发给其他客户端（星型拓扑）+ 本地也 Raise（客户端来源的广播，主机尚未触发）
                    foreach (var p in net.Roster)
                        if (!p.IsLocal && (ulong)p.SteamId != from)
                            net.Transport.Send(p.SteamId, data, true);
                    OncMissionBridge.Raise(eventId);
                }
                else
                {
                    // 客机：本地应用（触发脚本模块事件订阅 + Core 图）
                    OncMissionBridge.Raise(eventId);
                }
            }
            finally { IsApplying = false; }
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.script", () => $"OncMission script packet error: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }

    public void Reset()
    {
        try { IsApplying = false; } catch { }
    }

    public void OnLateJoin(ulong steamId) { }
}
