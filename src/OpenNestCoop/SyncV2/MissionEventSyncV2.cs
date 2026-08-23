using LiteNetLib.Utils;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 任务过渡事件同步（MissionEventSyncV2）。M7：把 V1 <c>MissionEventSync</c>（MsgType=130）迁入分层架构。
/// 纯事件，走 EventLayer（<see cref="V2Authority.Operator"/>：谁的游戏逻辑触发 → 广播 → 对端复现；
/// 防环由 EventLayer 的 _reproducing id 级 guard 承担，复现期间 Harmony patch 再触发会被抑制）。
/// 事件：任务完成/失败/重载/回菜单/结束回菜单（开始由任务状态同步负责）。
/// </summary>
public sealed class MissionEventSyncV2
{
    public static MissionEventSyncV2 Instance { get; } = new MissionEventSyncV2();

    private MissionEventSyncV2()
    {
        EventLayer.Instance.Register(EventId, V2Authority.Operator, Reproduce);
    }

    public const string EventId = "v2/mission/event";

    public const byte EvFinish = 1, EvComplete = 2, EvFailed = 3, EvReload = 4, EvReturnMap = 5, EvEndOperation = 6;

    /// <summary>本地任务过渡被触发（Harmony patch，V2 分支）→ 广播。</summary>
    public void OnLocalEvent(byte ev, bool flag)
    {
        if (!HostDataLayer.Instance.IsOnline) return;
        EventLayer.Instance.Raise(EventId, w =>
        {
            w.Put(ev);
            w.Put(flag ? (byte)1 : (byte)0);
        });
    }

    /// <summary>EventLayer 复现：对端执行同样任务过渡（防环由 EventLayer 承担）。</summary>
    private static void Reproduce(NetDataReader r)
    {
        byte ev = r.GetByte();
        bool flag = r.GetByte() != 0;
        var m = MissionManager.Instance;
        if (m == null) return;
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
            }
            CoopRuntime.LogSource?.LogInfo($"[MissionEventV2] applied ev={ev} flag={flag}");
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MissionEventSyncV2] Reproduce: {ex.Message}"); }
    }
}
