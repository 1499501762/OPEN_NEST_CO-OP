using LiteNetLib.Utils;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 任务打字机通知同步（NotificationSyncV2）。M7：把 V1 <c>NotificationSync</c>（MsgType=131）迁入分层架构。
/// 纯事件，走 EventLayer（<see cref="V2Authority.Operator"/>：谁触发 ShowNotification → 广播 → 对端复现；
/// 防环由 EventLayer 的 _reproducing id 级 guard 承担）。
/// </summary>
public sealed class NotificationSyncV2
{
    public static NotificationSyncV2 Instance { get; } = new NotificationSyncV2();

    private NotificationSyncV2()
    {
        EventLayer.Instance.Register(EventId, V2Authority.Operator, Reproduce);
    }

    public const string EventId = "v2/notification";

    /// <summary>本地 UINotificationManager.ShowNotification 被调用（Harmony postfix，V2 分支）→ 广播。</summary>
    public void OnLocalShow(string title, string description, float lifetime)
    {
        if (!HostDataLayer.Instance.IsOnline) return;
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(description)) return;
        EventLayer.Instance.Raise(EventId, w =>
        {
            w.Put(title ?? "");
            w.Put(description ?? "");
            w.Put(lifetime);
        });
    }

    /// <summary>EventLayer 复现：对端本地调用 ShowNotification。</summary>
    private static void Reproduce(NetDataReader r)
    {
        string title = r.GetString();
        string description = r.GetString();
        float lifetime = r.GetFloat();
        var mgr = UINotificationManager.Instance;
        if (mgr == null) return;
        try
        {
            var noBorder = new Il2CppSystem.Nullable<UnityEngine.Color>();
            UINotificationManager.ShowNotification(title ?? "", description ?? "", lifetime, noBorder);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[NotificationSyncV2] Reproduce: {ex.Message}"); }
    }
}
