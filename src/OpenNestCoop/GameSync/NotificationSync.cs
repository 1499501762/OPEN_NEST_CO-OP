using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 任务打字机通知同步（MsgType=131）：UINotificationManager.ShowNotification 的事件广播。
///
/// 任务状态机（MissionGraph 节点）在主机跑，触发打字机通知（目标确认/阶段提示等）。
/// 客机只同步了目标实体（位置/状态），没有收到这些通知 → 打字机信息两端不同。
/// 方案：Harmony patch UINotificationManager.ShowNotification（postfix），
/// 主机把 title/description/lifetime 广播给客机；客机收到后本地调用 ShowNotification 复现。
/// 防环：应用远端通知时 IsApplying=true，不再重复上报。
/// </summary>
public sealed class NotificationSync : ISyncedModule
{
    public byte MsgType => 131;
    private const byte MsgTypeId = 131;

    /// <summary>应用远端通知时的防环标志（ShowNotification postfix 据此不重复上报）。</summary>
    public static bool IsApplying;
    private static int _log;

    /// <summary>本地 UINotificationManager.ShowNotification 被调用（Harmony postfix）→ 主机广播。</summary>
    public static void OnLocalShow(string title, string description, float lifetime)
    {
        try
        {
            if (IsApplying) return; // 应用远端通知时不再上报（防环）
            var net = CoopRuntime.Net;
            if (net == null) return;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(description)) return;
            var w = NetProtocol.Begin((MsgType)MsgTypeId);
            w.Put(title ?? "");
            w.Put(description ?? "");
            w.Put(lifetime);
            var data = NetProtocol.Snapshot(w);
            if (net.IsHost)
            {
                // 主机权威：广播给所有远端（通知由主机任务状态机触发）
                foreach (var p in net.Roster)
                    if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
            else if (net.HostSteamId != 0)
            {
                // 客户端本地通知（非任务状态机，如购买提示）→ 上报主机转发
                net.Transport.Send(net.HostSteamId, data, true);
            }
            if ((++_log % 5) == 1)
                CoopRuntime.LogSource?.LogInfo($"[Notification] local show '{title}' '{description}' isHost={net.IsHost}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"NotificationSync OnLocalShow: {ex.Message}"); }
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
            string title = r.GetString();
            string description = r.GetString();
            float lifetime = r.GetFloat();
            if (net.IsHost)
            {
                // 主机转发给其他客户端（星型拓扑）
                foreach (var p in net.Roster)
                    if (!p.IsLocal && (ulong)p.SteamId != from)
                        net.Transport.Send(p.SteamId, data, true);
            }
            Apply(title, description, lifetime);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"NotificationSync OnPacket: {ex.Message}"); }
    }

    private static void Apply(string title, string description, float lifetime)
    {
        try
        {
            var mgr = UINotificationManager.Instance;
            if (mgr == null) { CoopRuntime.LogSource?.LogWarning("[Notification] apply but UINotificationManager is null"); return; }
            IsApplying = true;
            try
            {
                // ShowNotification(title, description, lifetime, Nullable<Color>) —— 静态方法
                // borderColor 用空 Nullable（无边框色）：Il2CppSystem.Nullable<Color> 无参构造
                var noBorder = new Il2CppSystem.Nullable<UnityEngine.Color>();
                UINotificationManager.ShowNotification(title ?? "", description ?? "", lifetime, noBorder);
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Notification] apply ShowNotification: {ex.Message}"); }
            finally { IsApplying = false; }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"NotificationSync Apply: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { }
    public void Reset() { }
}
