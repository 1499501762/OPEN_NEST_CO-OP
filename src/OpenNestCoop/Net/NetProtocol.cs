using LiteNetLib.Utils;

namespace OpenNestCoop.Net;

/// <summary>联机消息类型。前导字节为消息类型。</summary>
public enum MsgType : byte
{
    /// <summary>客户端 -&gt; 主机：自我介绍（携带昵称）</summary>
    Hello = 1,
    /// <summary>主机 -&gt; 客户端：分配玩家序号 + 全量名单</summary>
    Welcome = 2,
    /// <summary>主机 -&gt; 全员：全量名单更新</summary>
    Roster = 3,
    Ping = 4,
    Pong = 5,
    Chat = 6,
    // ---- M2：共享单炮塔 ----
    TurretState = 10,   // 主机 -> 全员：炮塔旋转 + 各炮俯仰快照
    GunFire = 11,       // 主机 -> 全员：开火事件（gunIndex）
    TurretInput = 12,   // 客户端(瞄准手) -> 主机：期望旋转/俯仰
    // ---- M3 起 ----
    Impact = 13,        // 主机 -> 全员：炮弹落点
    MissionState = 14,  // 主机 -> 全员：任务/目标状态
    CounterBattery = 15, // 主机 -> 全员：反炮兵事件（落点/侦察 seed 走 ISyncedModule 100+）
    // ---- 合包容器 ----
    Batch = 120,        // 外层容器：多个不可靠状态子包合并（省 Steam 每包头）
    // ---- M2.6：玩家化身 ----
    PlayerPos = 16      // 客户端 -> 主机 -> 其他客户端：玩家世界位置/朝向
    ,
    // ---- M2.8：唱片机/播放器 ----
    RecordState = 17,   // 主机 -> 全员：唱片机播放状态（isPlaying/trackIndex/音量）
    RecordCmd = 18,     // 客户端 -> 主机：唱片机本地变化上行
    // ---- M3a：装填/开火 ----
    ReloadState = 19,   // 主机 -> 全员：每炮装填状态快照（stateIndex/selectedCharges）
    ReloadCmd = 20,     // 客户端 -> 主机：装填状态本地变化上行
    FireRequest = 21,   // 客户端 -> 主机：开火请求
    // ---- M3b：地图标记 ----
    MapMarkerAdd = 22,  // 客户端 -> 主机 -> 全员：放置地图标记（id/prefabIdx/origin/tip）
    MapMarkerRemove = 23, // 客户端 -> 主机 -> 全员：移除地图标记（id）
    MapMarkerClearAll = 24, // 客户端 -> 主机 -> 全员：清空地图标记
    // ---- M3c：实时交互同步 ----
    ControlState = 25,  // 主机 -> 全员：控件值（刻度盘/旋钮/曲柄/滑块：kind+path+value）
    ControlCmd = 26,    // 客户端 -> 主机：本地控件变化上行
    MapMarkerUpdate = 27 // 客户端 -> 主机 -> 全员：地图标记实时拖拽位置（id+origin+tip）
    ,
    ReloadAdvance = 28 // 任意端 -> 全员：装填推进/回退事件（gunIndex + dir）
    ,
    PowderEvent = 29 // 任意端 -> 全员：发射药事件（gunIndex + ev(1=选药量,2=投放) + chargeIndex）
    ,
    StateSnapshot = 30 // 主机 -> 新加入者：中途加入全量状态容器（方案 B：状态注册表）
    ,
    SnapshotRequest = 31 // 客机 -> 主机：任务场景加载完成后请求补发快照
    ,    Kick = 32 // 主机 -> 被踢成员：踢出/封禁（reliable）
    ,    CatEvent = 133 // 任意端 -> 全员：玩家-猫交互事件（1=拾起 2=放下 3=驱赶 4=抚摸）
}

public static class NetProtocol
{
    public static NetDataWriter Begin(MsgType type)
    {
        var w = new NetDataWriter();
        w.Put((byte)type);
        return w;
    }

    public static MsgType TypeOf(NetDataReader r) => (MsgType)r.GetByte();

    /// <summary>从 writer 取出完整字节数组（副本，安全）。</summary>
    public static byte[] Snapshot(NetDataWriter w)
    {
        var data = new byte[w.Length];
        System.Array.Copy(w.Data, data, w.Length);
        return data;
    }

    public static void WriteRoster(NetDataWriter w, System.Collections.Generic.IList<PlayerSession> roster)
    {
        w.Put((byte)roster.Count);
        foreach (var p in roster)
        {
            w.Put(p.SteamId);
            w.Put(p.PlayerId);
            w.Put((byte)p.Role);
            w.Put(p.IsHost ? (byte)1 : (byte)0);
            w.Put(p.Name ?? "");
        }
    }

    public static System.Collections.Generic.List<PlayerSession> ReadRoster(NetDataReader r)
    {
        var list = new System.Collections.Generic.List<PlayerSession>();
        int n = r.GetByte();
        for (int i = 0; i < n; i++)
        {
            list.Add(new PlayerSession
            {
                SteamId = r.GetULong(),
                PlayerId = r.GetByte(),
                Role = (CrewRole)r.GetByte(),
                IsHost = r.GetByte() != 0,
                Name = r.GetString()
            });
        }
        return list;
    }
}
