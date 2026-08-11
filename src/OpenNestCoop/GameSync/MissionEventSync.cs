using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 任务过渡事件同步（MsgType=130）：任务完成/失败/重载/回菜单/结束回菜单。
/// 任务**开始**由 MissionSync（102）同步（scene+phase 轮询）。
/// 任务**过渡**（完成/失败/重载/回菜单）是事件——游戏逻辑在两端各自跑（实体同步驱动击杀/目标一致），
/// 但完成/失败/重载/回菜单需要跨端一致触发，故主机权威广播事件，对端执行同样操作。
/// 触发：Harmony patch MissionManager 的 FinishMission/MarkMissionComplete/MarkMissionFailed/
///       ReloadCurrentMission/ReturnToMap/EndOperationAndReturnToMenu（见 HarmonyPatches）。
/// </summary>
public sealed class MissionEventSync : ISyncedModule
{
    public byte MsgType => 130;

    // 事件类型
    public const byte EvFinish = 1;           // FinishMission()
    public const byte EvComplete = 2;         // MarkMissionComplete(bool) + 参数
    public const byte EvFailed = 3;           // MarkMissionFailed(bool) + 参数
    public const byte EvReload = 4;           // ReloadCurrentMission()
    public const byte EvReturnMap = 5;        // ReturnToMap()
    public const byte EvEndOperation = 6;     // EndOperationAndReturnToMenu()

    /// <summary>应用远端事件时的防环标志（Harmony patch 据此不重复上报）。</summary>
    public static bool IsApplying;
    private static int _log;

    /// <summary>本地任务过渡被触发（Harmony patch 调用）→ 上报主机（或主机直接广播）。</summary>
    public static void OnLocalEvent(byte ev, bool flag)
    {
        try
        {
            if (IsApplying) return; // 应用远端事件时不再上报（防环）
            var net = CoopRuntime.Net;
            if (net == null) return;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
            var w = NetProtocol.Begin((MsgType)130);
            w.Put(ev);
            w.Put(flag ? (byte)1 : (byte)0);
            var data = NetProtocol.Snapshot(w);
            if (net.IsHost)
            {
                // 主机权威：广播给所有远端
                foreach (var p in net.Roster)
                    if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
            else if (net.HostSteamId != 0)
            {
                // 客户端：上报主机（主机决定并转发）
                net.Transport.Send(net.HostSteamId, data, true);
            }
            if ((++_log % 5) == 1)
                CoopRuntime.LogSource?.LogInfo($"[MissionEvent] local ev={ev} flag={flag} isHost={net.IsHost}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MissionEventSync OnLocalEvent: {ex.Message}"); }
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
            bool flag = r.GetByte() != 0;
            if (net.IsHost)
            {
                // 主机转发给其他客户端（星型拓扑）
                foreach (var p in net.Roster)
                    if (!p.IsLocal && (ulong)p.SteamId != from)
                        net.Transport.Send(p.SteamId, data, true);
            }
            Apply(ev, flag);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MissionEventSync OnPacket: {ex.Message}"); }
    }

    private static void Apply(byte ev, bool flag)
    {
        var m = GetManager();
        if (m == null) { CoopRuntime.LogSource?.LogWarning($"[MissionEvent] apply ev={ev} manager null"); return; }
        IsApplying = true;
        try
        {
            switch (ev)
            {
                case EvFinish: m.FinishMission(); break;
                case EvComplete: m.MarkMissionComplete(flag); break;
                case EvFailed: m.MarkMissionFailed(flag); break;
                case EvReload: m.ReloadCurrentMission(); break;
                case EvReturnMap: m.ReturnToMap(); break;
                case EvEndOperation: m.EndOperationAndReturnToMenu(); break;
                default: CoopRuntime.LogSource?.LogWarning($"[MissionEvent] unknown ev={ev}"); break;
            }
            CoopRuntime.LogSource?.LogInfo($"[MissionEvent] applied ev={ev} flag={flag}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MissionEventSync Apply: {ex.Message}"); }
        finally { IsApplying = false; }
    }

    private static MissionManager GetManager()
    {
        try { return MissionManager.Instance; }
        catch { return null; }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { IsApplying = false; }
}
