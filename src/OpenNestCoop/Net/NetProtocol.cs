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
    PlayerPos = 16      // 客户端 -> 主机 -> 其他客户端：玩家世界位置/朝向（unreliable 连续值）
    ,   PlayerState = 33 // 玩家离散状态标记（空中/蹲下/冲刺 + 俯仰）——必须 reliable（丢失会卡状态），变化才发
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
    ,
    // ---- SyncV2 分层新方案（MsgType ≥200，独立段，不与现有 1-32/100+/120/131/133/134 重叠）----
    // 路由走 CoopSyncRegistry.TryRoute（各层实现 ISyncedModule），无需在此 switch 分发。
    V2Event = 200,   // EventLayer：泛型事件广播 + 对端复现（MsgType=200）
    V2Value = 201,   // ValueLayer：值同步（MsgType=201）
    V2Button = 202,  // ButtonLayer：按钮/交互控件状态（MsgType=202）
    V2HostData = 203 // HostDataLayer：主机权威数据层全量快照（中途加入/基线对齐，MsgType=203）
    ,   V2Control = 204 // ControlSyncV2：控件发现层（仅 Tick 驱动，无自身网络消息，占位保留）
    ,   V2Player = 205 // PlayerSyncV2：玩家位置/朝向同步（Operator 权威，10Hz+死区，星型中继）
    ,   V2Entity = 206 // EntitySyncV2：任务实体状态同步（Host 权威，客机聚合上行→主机广播）
    ,   V2Cat = 207 // CatSyncV2：猫 AI 状态软同步（Host 权威，held 上行；交互事件走 EventLayer 200）
    ,   V2Record = 208 // RecordPlayerSyncV2：唱片机状态（谁操作谁变更，经主机传播）
    ,   V2ReloadState = 209 // ReloadSyncV2：装填状态快照（Host 权威，主机→全员）
    ,   V2ReloadCmd = 210 // ReloadSyncV2：装填状态上行（客机→主机）
    ,   V2ReloadSnapshotReq = 214 // ReloadSyncV2：客机进入炮台场景后请求补发装填快照（客机→主机）
    ,   V2ReconPhoto = 215 // ReconPhotoSyncV2：侦察照片 seed 广播（Host 权威）
    ,   V2Coffee = 216 // CoffeeSyncV2：咖啡机冲煮状态（Host 权威）
    ,   V2CounterBattery = 217 // CounterBatterySyncV2：反炮兵落点 seed 广播（Host 权威）
    ,   V2RecordItem = 218 // RecordItemSyncV2：唱片物品位置（Host 权威）
    ,   V2Shell = 219 // ShellSyncV2：弹舱弹种（Host 权威，变化+心跳）
    ,   V2Sequence = 220 // SequenceSyncV2：发射台开关序列（谁变化谁广播，OnLateJoin 快照）
    ,   V2Hatch = 221 // HatchSyncV2：舱门/楼梯盖板 IsOpen（谁变化谁广播，OnLateJoin 快照）
    ,   V2MapToken = 222 // MapTokenSyncV2：战术令牌位置/朝向/active（谁变化谁广播，OnLateJoin 快照）
    ,   V2Purchase = 223 // PurchaseSyncV2：补给购买事件（Host 权威，客机请求→主机执行→广播）
    ,   V2Punchcard = 224 // PunchcardSyncV2：征信点卡牌位置/状态（Host 权威）
    ,   V2PunchcardSlot = 225 // PunchcardSyncV2：卡牌入槽/出槽事件（Operator）
    ,   V2MapMarker = 226 // MapMarkerSyncV2：战术地图标记/画线（事件驱动+擦除检测，OnLateJoin 快照）
    ,   V2Mission = 227 // MissionSyncV2：任务 scene/phase/seed（Host 权威，OnLateJoin 快照）
    ,   V2Teleprinter = 228 // TeleprinterSyncV2：打字机打印/状态/清除（Host 权威）
    ,   V2GunLink = 229 // GunLinkSyncV2：仰角联动/锁定插销 isLinked（谁变化谁广播，主机中继）
}

public static class NetProtocol
{
    // A1: NetDataWriter 对象池。发送路径为单线程帧内驱动（CoopBehaviour.Update），无并发。
    // Begin 从池取 writer，Snapshot 取副本后自动归还，内部 buffer 被 Reset 复用，
    // 大幅减少每包 new NetDataWriter（+内部 byte[]）的托管分配 —— GC STW 卡帧的主要来源。
    private const int MaxPooledWriters = 64;
    private static readonly System.Collections.Generic.Stack<NetDataWriter> _writerPool = new();

    public static NetDataWriter Begin(MsgType type)
    {
        var w = _writerPool.Count > 0 ? _writerPool.Pop() : new NetDataWriter();
        w.Reset(); // LiteNetLib Reset() 只重置位置，保留已分配的内部 buffer
        w.Put((byte)type);
        return w;
    }

    public static MsgType TypeOf(NetDataReader r) => (MsgType)r.GetByte();

    /// <summary>从 writer 取出完整字节数组（副本，安全），并把 writer 归还池（内部 buffer 复用）。</summary>
    public static byte[] Snapshot(NetDataWriter w)
    {
        var data = new byte[w.Length];
        System.Array.Copy(w.Data, data, w.Length);
        Recycle(w);
        return data;
    }

    /// <summary>把 writer 归还池（内部 buffer 复用）。Snapshot 已自动调用；
    /// 仅在"写完但不走 Snapshot"的自定义发送路径需要手动调用。</summary>
    public static void Recycle(NetDataWriter w)
    {
        if (w == null) return;
        if (_writerPool.Count < MaxPooledWriters) _writerPool.Push(w);
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
