using System;
using System.Collections.Generic;
using System.Linq;
using OpenNestCoop.Core;
using OpenNestCoop.Core.Loc;
using OpenNestCoop.GameSync;
using LiteNetLib.Utils;
#if !MELONLOADER
using Steamworks;
#else
using Steamworks = Il2CppSteamworks;
#endif

namespace OpenNestCoop.Net;

public enum SessionState
{
    Idle,
    Hosting,
    Joined,
}

/// <summary>
/// 联机会话状态机（单例，由 CoopBehaviour.Update 驱动）。
/// 拓扑：星型 —— 主机权威。所有游戏状态在主机计算，广播到各客户端；
/// 客户端只把本机"角色输入"上行给主机。
/// </summary>
public class NetManager
{
    public SessionState State { get; private set; } = SessionState.Idle;
    public SteamLobby Lobby { get; } = new();
    public ITransport Transport { get; private set; } = new SteamTransport();
    public List<PlayerSession> Roster { get; } = new();
    public List<string> ChatLog { get; } = new();

    public PlayerSession Local { get; private set; }
    public List<LobbyInfo> Browser => Lobby.Browser;

    public bool MenuOpen = true;
    public string PendingLobbyName = "";   // 默认房间名在 Init 里按语言键填充（DefaultRoomName）
    public int PendingMaxPlayers = NetConfig.DefaultMaxPlayers;
    /// <summary>创建房间密码 / 加入有密码房间时用户输入的密码（明文，握手时带过去由主机校验 hash）。</summary>
    public string PendingPassword = "";
    public bool Refreshing;
    public string LastError = "";

    /// <summary>本次会话封禁名单（主机维护）：被踢的 SteamId 加入，收到其 Hello 拒绝加入。</summary>
    private readonly HashSet<ulong> _banned = new();
    /// <summary>是否被主机踢出（被踢端标志：收到 Kick 消息置位，用于 UI 提示 + 阻止重连）。</summary>
    public bool WasKicked;

    /// <summary>本地回环模式（双开测试，不经 Steam）：host 监听 TCP，client 连 127.0.0.1。</summary>
    public bool LocalMode;
    /// <summary>本地模式端口（host 监听 / client 连接）。</summary>
    public int LocalPort = NetConfig.LocalDefaultPort;
    /// <summary>本地模式 host 的 peerId（host=1，client 记住 host=1）。</summary>
    public const ulong LocalHostPeerId = 1;

    // ⚠️ 2026-09-12 新增：**局域网联机**（TCP 直连，不经 Steam 大厅）——见 docs/LAN.md
    /// <summary>局域网模式（主机监听任意网卡 / 客机 TCP 直连）。身份规则：SteamID 优先，拿不到则用配置里的 FakeID。</summary>
    public bool LanMode;
    /// <summary>非 Steam 传输模式（本地回环测试 / 局域网）：大厅/邀请/浏览/成员同步等 Steam 操作一律跳过。</summary>
    public bool NonSteam => LocalMode || LanMode;
    /// <summary>局域网目标主机（IP 或机器名）——客机加入时输入。</summary>
    public string LanJoinHost = "";
    // 局域网断线（后台线程只入队/置标志 → 主线程处理；见 UpdateLocal）
    private readonly System.Collections.Concurrent.ConcurrentQueue<ulong> _lanPeersGone = new();
    private volatile bool _lanHostLost;
    /// <summary>局域网端口（主机监听 / 客机连接 / UDP 发现）；默认取配置 `[LAN] Port`。</summary>
    public int LanJoinPort = NetConfig.LocalDefaultPort;
    /// <summary>局域网自定义用户名（UI 输入，空 = 用配置 `[Identity] Name` → Steam 昵称 → `Player<FakeID后4位>`）。
    /// 建房/加入时生效并写回配置（下次预填）。**允许重名**（身份才是唯一键）。</summary>
    public string LanLocalName = "";
    /// <summary>本端联机显示名（局域网：优先 UI 里填的名字）。</summary>
    public string LocalDisplayName => string.IsNullOrWhiteSpace(LanLocalName) ? Core.Identity.LocalName() : LanLocalName.Trim();
    /// <summary>本端在联机里的身份：真实 SteamID 优先（即使局域网模式，只要 Steam 就绪就用 SteamID）；
    /// 拿不到则用配置里的 FakeID（首次自动生成并落盘，见 <see cref="Core.Identity"/>）。</summary>
    public ulong LocalIdentity => Core.Identity.Local;

    public ulong HostSteamId => NonSteam ? LocalHostPeerId : Lobby.HostSteamId;

    // 合包缓冲（不可靠周期状态：帧末合并成一个 UDP 包，省 Steam 每包约 30B 头）
    private readonly List<byte[]> _broadcastQueue = new();
    private readonly List<byte[]> _hostQueue = new();
    // E 分级：容忍丢失的高频连续状态（unreliable 通道）独立队列——减少 reliable 拥塞/延迟抖动；
    // 发送端需提供低频 reliable 保底（心跳/全量）防长期丢失，最终对齐由保底负责。
    private readonly List<byte[]> _broadcastQueueU = new();
    private readonly List<byte[]> _hostQueueU = new();
    // ⚠️ 合包上限由 NetworkGovernor 分级动态控制（High=256 与原 BatchMaxItems 一致）——
    // 负载高（低档）→ 上限缩小 → 超限丢弃（RecordDrop 喂回评估器触发降频）。
    public bool IsHost => NonSteam ? (State == SessionState.Hosting) : Lobby.IsHost;

    // ⚠️ 2026-08-26：通用大包分片（Steam P2P 单包硬性限制）——单子包超过阈值自动切成多段 Fragment，
    // 接收端重组。Steam unreliable 单包 ~1200B / reliable 1MB 是硬限制，超过整包被拒收/丢。
    // 分片阈值取 900B（留余量给 Batch 头 + 可靠包重传膨胀），确保任何单包安全。
    private const int FragmentThreshold = 900;
    private ushort _fragId; // 分片序号（发送端自增，接收端按 from+fragId 重组）
    /// <summary>分片重组缓冲项（引用类型！值类型元组副本会导致 got 计数不写回字典 → 分片永不重组）。</summary>
    private sealed class FragBuf
    {
        public int Total;
        public byte[][] Segs;
        public int Got;
        public float First;
    }
    /// <summary>接收端分片重组缓冲：key=(from, fragId) → FragBuf。</summary>
    private readonly System.Collections.Generic.Dictionary<(ulong from, ushort fragId), FragBuf> _fragBuf = new();
    private float _fragCleanTimer;
    /// <summary>Steam 是否已初始化（游戏启动后由 Heathen 完成）。本地模式恒 true。</summary>
    public bool SteamReady;

    private ulong _joinedHostId;      // 加入方记住主机
    private float _pingTimer;
    private float _steamProbeAt;      // Steam 就绪探测的下次到期时间（2Hz；见 Update 里的说明）
    private bool _steamContextAttempted;
    private bool _creatingLobby;      // 创建大厅异步窗口期防重复点击/重复创建
    private int _flushLog;
    private int _batchRecvLog;
    private int _pktRecvLog;
    // A1: 合包 writer 字段复用（Reset 保留内部 buffer，省每帧 new NetDataWriter + Snapshot 合包副本）
    private NetDataWriter _batchWriter;

    // 消息收发统计（每 10s 汇总打印后清零，便于定位丢包/路由）
    private readonly int[] _recvStats = new int[256];
    private readonly int[] _sendStats = new int[256];
    private float _statsTimer;

    public event Action StateChanged;
    public event Action RosterChanged;
    public event Action ChatChanged;

    public void Init()
    {
        Lobby.Entered += OnLobbyEntered;
        Lobby.Left += OnLobbyLeft;
        Lobby.ListRefreshed += () => { Refreshing = false; };
        Lobby.MembersChanged += OnMembersChanged;

        Local = new PlayerSession { SteamId = 0, Name = "Steam initializing...", IsLocal = true, PlayerId = 255 };

        // 恢复上次房间设置（房间名/人数/密码），建房 UI 预填
        Core.LobbySettings.Load(out var rn, out var mp, out var pwd);
        if (!string.IsNullOrEmpty(rn)) PendingLobbyName = rn;
        PendingMaxPlayers = Math.Max(2, Math.Min(8, mp));
        PendingPassword = pwd ?? "";

        // 局域网：端口 + 上次地址从配置预填（docs/CONFIG.md `[LAN]`）
        try
        {
            LanJoinPort = Core.CoopConfig.LanPort > 0 ? Core.CoopConfig.LanPort : NetConfig.LocalDefaultPort;
            LanJoinHost = Core.CoopConfig.LanLastHost ?? "";
            LanLocalName = Core.CoopConfig.LocalName ?? "";
            // 默认房间名：按语言键取（语言文件可改；LobbySettings 已有值则不覆盖）
            if (string.IsNullOrWhiteSpace(PendingLobbyName)) PendingLobbyName = Loc("DefaultRoomName");
        }
        catch { }
    }

    public void Shutdown()
    {
        try { if (State != SessionState.Idle) LeaveSession(); } catch { }
        try { LanDiscovery.StopAnnounce(); } catch { }
        State = SessionState.Idle;
    }

    // ================= 注册管理（前导字节统一分配/管理 + 优先级 + 路由 + 注册表同步通道） =================
    // 每个同步模块自行注册（静态采用 / 动态 channelKey / 函数式回调），优先级随注册一起提供（缺省有默认）；
    // 管理器统一分配前导字节（自动避开框架枚举 / 已采用模块 / V2 200-229）、统一按前导字节路由，
    // 并经前导字节 1（Hello 上行注册表）/2（Welcome 下发主机权威注册表）同步双端路由表。

    /// <summary>注册表条目：通道键 → 前导字节（int，支持 2 字节扩展 ≥256）+ 模块 + 优先级。</summary>
    private sealed class ChannelEntry
    {
        public string Key;
        public int Type;
        public ISyncedModule Module;
        public NetModulePriority Priority;
    }

    /// <summary>动态通道：channelKey → 条目。</summary>
    private readonly Dictionary<string, ChannelEntry> _channels = new();
    /// <summary>前导字节 → 条目（统一路由表）。</summary>
    private readonly Dictionary<int, ChannelEntry> _channelsByType = new();
    /// <summary>已占用前导字节（框架枚举 + 静态采用 + 动态分配）。</summary>
    private readonly HashSet<int> _usedTypes = new();
    /// <summary>静态采用条目（硬编码 MsgType 模块；key 仅用于注册表同步展示/校验）。</summary>
    private readonly List<(string key, int type)> _staticChannels = new();
    private bool _reservedBuilt;

    /// <summary>动态分配优先段（1 字节内首选）：230-254（V2 到 229 为止，高位空闲）、160-199、34-99。</summary>
    private static readonly byte[][] ChannelRanges =
    {
        new byte[] { 230, 254 },
        new byte[] { 160, 199 },
        new byte[] { 34, 99 },
    };

    private void EnsureReservedTypes()
    {
        if (_reservedBuilt) return;
        _reservedBuilt = true;
        // 框架保留：MsgType 枚举全部值（1-33 / 120 / 122 / 133 / 145 / 146 / 200-229）不可被动态分配占用
        try
        {
            foreach (MsgType t in Enum.GetValues(typeof(MsgType)))
                if ((byte)t != 0) _usedTypes.Add((byte)t);
        }
        catch { }
    }

    // ---- 注册（模块自行注册 + 优先级；优先级缺省 = 模块 NetPriority，默认 Normal） ----

    /// <summary>静态采用既有硬编码 MsgType 模块（含附加类型）：记录占用 + 优先级 + 加入统一路由表（不重新分配）。
    /// 优先级缺省 → 取模块 <see cref="ISyncedModule.NetPriority"/>（默认 Normal）。由 CoopSyncRegistry.RegisterModule 调用。</summary>
    public void AdoptModule(ISyncedModule module, NetModulePriority? priority = null, params byte[] extraTypes)
    {
        EnsureReservedTypes();
        if (module == null) return;
        var pri = priority ?? module.NetPriority;
        int t = module.MsgType;
        if (t != 0) AdoptOneChannel("S:" + module.GetType().Name, t, module, pri);
        if (extraTypes != null)
            foreach (var e in extraTypes)
                if (e != 0) AdoptOneChannel("S:" + module.GetType().Name + "#" + e, e, module, pri);
    }

    private void AdoptOneChannel(string key, int t, ISyncedModule module, NetModulePriority pri)
    {
        _usedTypes.Add(t);
        // ⚠️ 2026-09-12：动态/静态模块只要优先级是 Critical/High → 登记为关键类型（防合包丢弃丢包）
        if (pri == NetModulePriority.Critical || pri == NetModulePriority.High) RegisterCriticalType(t);
        if (!_channelsByType.ContainsKey(t))
            _channelsByType[t] = new ChannelEntry { Key = key, Type = t, Module = module, Priority = pri };
        foreach (var s in _staticChannels) if (s.key == key) return; // 幂等（类名+附加类型去重）
        _staticChannels.Add((key, t));
    }

    /// <summary>动态自注册：模块用稳定 channelKey 注册，管理器分配空闲前导字节 + 记录优先级（缺省 → 模块 NetPriority）。
    /// 幂等：同一 key 已注册 → 返回已分配字节。⚠️ 本入口仅登记通道表（路由/优先级）；Tick/会话生命周期由
    /// CoopSyncRegistry.RegisterDynamicChannel 或函数式 <see cref="RegisterChannel(string, ChannelCallbacks, NetModulePriority)"/> 挂接。</summary>
    public int RegisterChannel(string channelKey, ISyncedModule module, NetModulePriority? priority = null)
    {
        EnsureReservedTypes();
        if (string.IsNullOrEmpty(channelKey) || module == null) return 0;
        if (_channels.TryGetValue(channelKey, out var existing)) return existing.Type; // 幂等
        int t = AllocateType();
        if (t == 0)
        {
            try { CoopLog.Warn("Net.channelNoType", () => $"[RegMgr] no free leading byte for channel '{channelKey}'"); } catch { }
            return 0;
        }
        var entry = new ChannelEntry { Key = channelKey, Type = t, Module = module, Priority = priority ?? module.NetPriority };
        _channels[channelKey] = entry;
        _channelsByType[t] = entry;
        _usedTypes.Add(t);
        // ⚠️ 2026-09-12：动态通道按优先级登记关键类型（动态分配的类型不在硬编码表内，
        // 不登记则合包满时会被丢弃且不重发 → 丢包）。见 RegisterCriticalType。
        if (entry.Priority == NetModulePriority.Critical || entry.Priority == NetModulePriority.High)
            RegisterCriticalType(t);
        try { CoopLog.Info("Net.channelDyn", () => $"[RegMgr] channel '{channelKey}' → type {t} ({module.GetType().Name}, pri={entry.Priority}, critical={IsCriticalType(t)}, width={NetProtocol.HeaderWidth})", 2f); } catch { }
        return t;
    }

    // ---- 函数式注册 API（用函数/回调注册同步通道，无需实现 ISyncedModule） ----

    /// <summary>函数式注册：用回调容器注册同步通道（管理器分配前导字节 + 默认 Normal 优先级）。
    /// 自动挂接 Tick/会话生命周期/中途加入（经 CoopSyncRegistry.AttachModule）。</summary>
    public int RegisterChannel(string channelKey, ChannelCallbacks callbacks, NetModulePriority priority = NetModulePriority.Normal)
    {
        if (string.IsNullOrEmpty(channelKey) || callbacks == null) return 0;
        var adapter = new FuncChannel(this, channelKey, callbacks);
        int t = RegisterChannel(channelKey, (ISyncedModule)adapter, priority);
        if (t == 0) return 0;
        CoopSyncRegistry.AttachModule(adapter, t);
        return t;
    }

    /// <summary>函数式注册便捷重载：直接传回调函数（onPacket 必需，其余可缺省）。</summary>
    public int RegisterChannel(string channelKey, Action<ulong, byte[]> onPacket, Action<float> tick = null, NetModulePriority priority = NetModulePriority.Normal)
        => RegisterChannel(channelKey, new ChannelCallbacks { OnPacket = onPacket, Tick = tick }, priority);

    /// <summary>函数式注册适配器：把回调包成 ISyncedModule 接入统一路由/Tick/生命周期。</summary>
    private sealed class FuncChannel : ISyncedModule
    {
        private readonly NetManager _net;
        private readonly string _key;
        private readonly ChannelCallbacks _cb;

        public FuncChannel(NetManager net, string key, ChannelCallbacks cb)
        {
            _net = net; _key = key; _cb = cb;
        }

        public int MsgType => _net?.ChannelType(_key) ?? 0; // 由注册管理器分配的前导字节

        public void Tick(float dt) { try { _cb.Tick?.Invoke(dt); } catch { } }
        public void OnPacket(ulong from, byte[] data) { try { _cb.OnPacket?.Invoke(from, data); } catch { } }
        public void OnSessionStarted() { try { _cb.OnSessionStarted?.Invoke(); } catch { } }
        public void OnSessionEnded() { try { _cb.OnSessionEnded?.Invoke(); } catch { } }
        public void Reset() { try { _cb.Reset?.Invoke(); } catch { } }
        public void OnLateJoin(ulong steamId) { try { _cb.OnLateJoin?.Invoke(steamId); } catch { } }
    }

    // ---- 查询 ----

    /// <summary>查询某通道分配的前导字节（动态注册模块发送消息时用；未注册返回 0）。int 支持 2 字节扩展 ≥256。</summary>
    public int ChannelType(string channelKey)
        => channelKey != null && _channels.TryGetValue(channelKey, out var e) ? e.Type : 0;

    /// <summary>查询某前导字节注册的优先级（未注册返回 null，调用方回退模块 NetPriority）。</summary>
    public NetModulePriority? ChannelPriority(int type)
        => _channelsByType.TryGetValue(type, out var e) ? e.Priority : (NetModulePriority?)null;

    /// <summary>前导字节 → 通道键（诊断/日志用）。</summary>
    public string ChannelKeyOf(int type)
        => _channelsByType.TryGetValue(type, out var e) ? e.Key : "";

    /// <summary>分配空闲前导字节：① 1 字节优先段 → ② 全 1 字节空间（真正用尽）→ ③ 1 字节用尽自动扩展：
    /// 前导字节升为 2 字节（HeaderWidth=2，经 Hello/Welcome 握手沟通），从 2 字节池（0x0300+，
    /// 避开 bootstrap 高字节 1/2）分配。返回 0 = 全用尽（理论上 2 字节 64K 空间不会耗尽）。</summary>
    private int AllocateType()
    {
        // ① 1 字节优先段
        foreach (var range in ChannelRanges)
        {
            for (byte b = range[0]; ; b++)
            {
                if (!_usedTypes.Contains(b) && !_channelsByType.ContainsKey(b)) return b;
                if (b == range[1]) break;
            }
        }
        // ② 全 1 字节空间扫描（包含优先段之外的剩余空闲，如 7-9 / 123-129 / 147-159 / 255 等）
        for (int b = 1; b <= 255; b++)
            if (!_usedTypes.Contains(b) && !_channelsByType.ContainsKey(b)) return b;
        // ③ 1 字节用尽 → 自动扩展为 2 字节前导字节（本端宽度，握手时与对端沟通确认）
        if (NetProtocol.HeaderWidth < 2)
        {
            NetProtocol.HeaderWidth = 2;
            try { CoopLog.Info("Net.headerWidth2", () => $"[RegMgr] 1-byte leading type exhausted → auto-extend to 2-byte header (HeaderWidth=2)", 2f); } catch { }
        }
        // 2 字节池：0x0300..0xFFFE（避开 bootstrap 高字节 1/2 的 0x0100-0x02FF 段）
        for (int t = 0x0300; t <= 0xFFFE; t++)
            if (!_usedTypes.Contains(t) && !_channelsByType.ContainsKey(t)) return t;
        return 0;
    }

    // ---- 统一路由 ----

    /// <summary>按前导字节分发给已注册模块（OnPacket 优先调用；int 兼容 2 字节扩展类型）。命中返回 true。</summary>
    public bool TryRoute(int type, ulong from, byte[] data)
    {
        if (_channelsByType.TryGetValue(type, out var e))
        {
            try { e.Module.OnPacket(from, data); }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[RegMgr] Route type={type}: {ex.Message}"); }
            return true;
        }
        return false;
    }

    // ---- 注册表同步通道（前导字节 1=Hello 上行 / 2=Welcome 下发） ----

    /// <summary>把本端注册表写入 writer（附加在 Hello/Welcome 包尾部）。
    /// 格式：[byte 条目数] 每项 [string key][byte type]。key 前缀 D:=动态通道，S:=静态采用。</summary>
    public void WriteChannelTable(NetDataWriter w)
    {
        EnsureReservedTypes();
        var items = new List<(string key, int type)>(_staticChannels.Count + _channels.Count);
        items.AddRange(_staticChannels);
        foreach (var kv in _channels) items.Add(("D:" + kv.Key, kv.Value.Type));
        if (items.Count > 255) items.RemoveRange(255, items.Count - 255);
        w.Put((byte)items.Count);
        // 类型按 ushort（2 字节）写——兼容 2 字节扩展类型（握手包只有低频一次，开销可忽略）
        foreach (var (key, type) in items) { w.Put(key); w.Put((ushort)type); }
    }

    /// <summary>主机：读取并校验客户端 Hello 里的注册表。客户端注册了主机没有的通道 → 返回 false（拒绝加入）。</summary>
    public bool VerifyHostChannelTable(NetDataReader r)
    {
        EnsureReservedTypes();
        try
        {
            int n = r.GetByte();
            if (n > 255) return false;
            int dyn = 0, st = 0;
            for (int i = 0; i < n; i++)
            {
                string key = r.GetString();
                int t = r.GetUShort();
                if (string.IsNullOrEmpty(key)) return false;
                if (key.StartsWith("D:"))
                {
                    string k = key.Substring(2);
                    if (!_channels.TryGetValue(k, out var e))
                    {
                        try { CoopLog.Warn("Net.channelUnknown", () => $"[RegMgr] client channel '{k}' unknown to host — reject"); } catch { }
                        return false; // 客户端比主机新/不同 build → 无法同步
                    }
                    if (e.Type != t) // 同 build 不应发生；发生则以主机为准（后续 Welcome 下发）
                        try { CoopLog.Warn("Net.channelTypeDiff", () => $"[RegMgr] channel '{k}' type client={t} host={e.Type} (host authoritative)"); } catch { }
                    dyn++;
                }
                else if (key.StartsWith("S:"))
                {
                    bool known = false;
                    foreach (var s in _staticChannels) if (s.key == key) { known = true; break; }
                    if (!known)
                    {
                        try { CoopLog.Warn("Net.channelUnknown", () => $"[RegMgr] client static '{key}' unknown to host — reject"); } catch { }
                        return false;
                    }
                    st++;
                }
                // 未知前缀：忽略（向前兼容）
            }
            try { CoopLog.Info("Net.channelVerify", () => $"[RegMgr] client registration ok dyn={dyn} static={st}", 2f); } catch { }
            return true;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[RegMgr] VerifyHostChannelTable: {ex.Message}"); return false; }
    }

    /// <summary>客户端：应用主机 Welcome 里的权威注册表。动态通道字节以主机为准（重映射）；静态/未知仅记录。</summary>
    public void ApplyHostChannelTable(NetDataReader r)
    {
        EnsureReservedTypes();
        try
        {
            int n = r.GetByte();
            if (n > 255) return;
            int matched = 0, remapped = 0;
            for (int i = 0; i < n; i++)
            {
                string key = r.GetString();
                int t = r.GetUShort();
                if (string.IsNullOrEmpty(key)) continue;
                if (key.StartsWith("D:"))
                {
                    string k = key.Substring(2);
                    if (_channels.TryGetValue(k, out var e))
                    {
                        if (e.Type != t)
                        {
                            _channelsByType.Remove(e.Type);
                            int old = e.Type;
                            e.Type = t;
                            _channelsByType[t] = e;
                            _usedTypes.Add(t);
                            remapped++;
                            try { CoopLog.Info("Net.channelRemap", () => $"[RegMgr] channel '{k}' {old}→{t} (host authoritative)", 2f); } catch { }
                        }
                        matched++;
                    }
                    else
                    {
                        try { CoopLog.Info("Net.channelHostExtra", () => $"[RegMgr] host channel '{k}' absent locally (ignored)", 2f); } catch { }
                    }
                }
                else if (key.StartsWith("S:"))
                {
                    // 静态（硬编码）类型：同 build 必然一致，仅计数
                    foreach (var s in _staticChannels) if (s.key == key) { matched++; break; }
                }
            }
            try { CoopLog.Info("Net.channelApply", () => $"[RegMgr] applied host table matched={matched}/{n} remapped={remapped}", 2f); } catch { }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[RegMgr] ApplyHostChannelTable: {ex.Message}"); }
    }

    /// <summary>会话开始日志：列出全部已管理通道（诊断）。</summary>
    public void LogChannelTable(string who)
    {
        EnsureReservedTypes();
        try
        {
            var sb = new System.Text.StringBuilder($"[RegMgr] {who} registration table: ");
            foreach (var kv in _channels) sb.Append($"{kv.Key}→{kv.Value.Type}({kv.Value.Priority}) ");
            foreach (var s in _staticChannels) sb.Append($"{s.key}→{s.type} ");
            CoopLog.Info("Net.channelTable", () => sb.ToString(), 5f);
        }
        catch { }
    }

    /// <summary>
    /// 游戏（Heathen）可能只原生初始化了 Steam，而没有初始化 Steamworks.NET 的托管静态上下文
    /// （CSteamAPIContext / CallbackDispatcher），导致 SteamMatchmaking / SteamNetworking 等
    /// 抛 “Steamworks is not initialized”。这里手动补齐，所有调用都幂等、无副作用。
    /// </summary>
    private void EnsureSteamContext()
    {
        if (_steamContextAttempted) return;
        try
        {
            if (!SteamAPI.IsSteamRunning()) return;
            SteamAPI.Init();
            CSteamAPIContext.Init();
            CallbackDispatcher.Initialize();
            _steamContextAttempted = true;
            CoopRuntime.LogSource?.LogInfo("Steamworks.NET managed context manually initialized");
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"Steamworks context init failed: {ex.Message}");
        }
    }

    public void Update(float dt)
    {
        // 本地回环（双开测试）/ 局域网：不经 Steam 大厅，直接驱动对应传输
        if (NonSteam)
        {
            UpdateLocal(dt);
            return;
        }

        // 确保 Steamworks.NET 托管上下文已初始化（游戏可能只原生初始化了 Steam）
        EnsureSteamContext();

        // 探测 Steam 就绪
        // ⚠️ 2026-09-13：帧剖析实测这行每帧 ~0.12ms（`Steam.Probe` = 8–14 ms/s，两次 Steam IPC/帧），
        //    纯浪费 → 降频到 2Hz（SteamReady 只会“就绪/未就绪”，半秒延迟无关紧要）。
        _steamProbeAt -= dt;
        if (_steamProbeAt <= 0f)
        {
            _steamProbeAt = 0.5f;
            long _tSteam = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                SteamReady = SteamAPI.IsSteamRunning() && SteamUser.GetSteamID().IsValid();
            }
            catch { SteamReady = false; }
            FrameProfiler.Instance.AddMs("Steam.Probe", (System.Diagnostics.Stopwatch.GetTimestamp() - _tSteam) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        if (SteamReady)
        {
            // 懒注册 Steam 回调 + 初始化本地玩家信息
            Lobby.EnsureRegistered();
            if (Local == null || Local.SteamId == 0)
            {
                try
                {
                    Local = new PlayerSession
                    {
                        SteamId = (ulong)SteamUser.GetSteamID(),
                        Name = SteamFriends.GetPersonaName(),
                        IsLocal = true,
                        PlayerId = 255,
                    };
                }
                catch { }
            }
            // 泵 Steam 回调（游戏本身也在泵，多泵无害）
            long _tCb = System.Diagnostics.Stopwatch.GetTimestamp();
            try { SteamAPI.RunCallbacks(); } catch { }
            FrameProfiler.Instance.AddMs("Steam.Callbacks", (System.Diagnostics.Stopwatch.GetTimestamp() - _tCb) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

            // 自动联机（--autohost / --autojoin）：Steam 就绪后触发建房/加入
            long _tAj = System.Diagnostics.Stopwatch.GetTimestamp();
            AutoJoin.TryStart(this);
            FrameProfiler.Instance.AddMs("Steam.AutoJoin", (System.Diagnostics.Stopwatch.GetTimestamp() - _tAj) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

            // 大厅列表填充：Steam 回调内不做事，改在此安全上下文处理（防回调内同步 API 死锁）
            long _tLobby = System.Diagnostics.Stopwatch.GetTimestamp();
            Lobby.PollPendingLobbyList();
            FrameProfiler.Instance.AddMs("Steam.LobbyList", (System.Diagnostics.Stopwatch.GetTimestamp() - _tLobby) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

            // 清空 P2P 入包队列（仅在 Steam 就绪时，避免未初始化异常刷屏）
            long _tPoll = System.Diagnostics.Stopwatch.GetTimestamp();
            while (Transport.Poll(out ulong from, out byte[] data))
            {
                // 延迟模拟：入队延迟包，到期再分发；未启用则直通
                if (NetLagSim.Enqueue(from, data)) continue;
                try { OnPacket(from, data); }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"packet process exception (from {from}): {ex}"); }
            }
            NetLagSim.Flush((f, d) => { try { OnPacket(f, d); } catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"lagged packet process exception (from {f}): {ex}"); } });
            FrameProfiler.Instance.AddMs("Steam.Poll", (System.Diagnostics.Stopwatch.GetTimestamp() - _tPoll) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        UpdateCommon(dt);
    }

    /// <summary>本地回环模式驱动（双开测试，不经 Steam）。</summary>
    private void UpdateLocal(float dt)
    {
        // 局域网：Steam 仍可能就绪（**身份优先 SteamID**），但大厅/邀请/成员同步一律不走 Steam
        if (LanMode)
        {
            try { Steamworks.SteamAPI.RunCallbacks(); } catch { }
            try { SteamReady = Steamworks.SteamAPI.IsSteamRunning() && Steamworks.SteamUser.GetSteamID().IsValid(); }
            catch { SteamReady = false; }
        }
        else
        {
            SteamReady = true;   // 本地回环测试：不需要 Steam
        }
        AutoJoin.TryStart(this);
        // 局域网：主线程处理断线队列 / 主机失联（后台线程不做名单·UI 操作）
        if (LanMode)
        {
            while (_lanPeersGone.TryDequeue(out var goneId)) ProcessLanPeerLeft(goneId);
            if (_lanHostLost)
            {
                _lanHostLost = false;
                LastError = Loc("ErrHostDisconnected");
                CoopRuntime.LogSource?.LogInfo("[Net] LAN host connection lost → leave session");
                LeaveSession();
                return;
            }
        }
        if (Local == null || Local.SteamId == 0)
        {
            Local = new PlayerSession
            {
                SteamId = LanMode ? LocalIdentity : (LocalMode ? (LocalPeerIdOf()) : 0),
                Name = LanMode ? LocalDisplayName
                               : (LocalMode ? (IsHost ? Loc("LocalHostName") : Loc("LocalClientName")) : "Steam initializing..."),
                IsLocal = true,
                PlayerId = 255,
            };
        }
        while (Transport.Poll(out ulong from, out byte[] data))
        {
            // 延迟模拟：入队延迟包，到期再分发；未启用则直通
            if (NetLagSim.Enqueue(from, data)) continue;
            try { OnPacket(from, data); }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"local packet process exception (from {from}): {ex}"); }
        }
        NetLagSim.Flush((f, d) => { try { OnPacket(f, d); } catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"lagged packet process exception (from {f}): {ex}"); } });
        UpdateCommon(dt);
    }

    /// <summary>本地模式本端 peerId（host=1，client=2）。</summary>
    private ulong LocalPeerIdOf() => (Transport as LocalTransport)?.LocalPeerId ?? 0;

    /// <summary>帧性能剖析便捷（F7 诊断）：测量 action 耗时（ms）记入 <see cref="FrameProfiler"/>（按名归因）。</summary>
    private static void Profile(string name, Action act)
    {
        if (act == null) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { act(); }
        finally
        {
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            FrameProfiler.Instance.AddMs(name, (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }
    }

    /// <summary>心跳 + 各同步模块 Tick + 合包 + 统计（Steam 与本地模式共用）。</summary>
    private void UpdateCommon(float dt)
    {
        // ⚠️ 网络负载调控器（NetworkGovernor）：1s 采样评估 + 动态升降级。
        // 模块频率按各自 NetPriority 精细化缩放（ModuleFreq）——关键模块几乎不降、高频容忍模块优先降。
        Profile("NetworkGovernor", () => NetworkGovernor.Instance.Tick(dt));

        // 心跳/延迟（用原始 dt——RTT 测量不能被频率缩放）
        _pingTimer += dt;
        if (_pingTimer >= NetConfig.PingInterval)
        {
            _pingTimer = 0f;
            if (State == SessionState.Hosting)
            {
                foreach (var p in Roster)
                {
                    if (p.IsLocal) continue;
                    var w = NetProtocol.Begin(MsgType.Ping);
                    w.Put(Environment.TickCount64);
                    Transport.Send(p.SteamId, NetProtocol.Snapshot(w), false);
                    p.LastPingSentTicks = Environment.TickCount64;
                    // ⚠️ 2026-09-05 丢包率误判修复：RecordPingSent 每发一个 Ping 记一次（原来 if 块外又记
                    // 一次 → 主机 _pingSent 翻倍 → loss=(2N-N)/2N=50% > 25% 硬信号 → NetworkGovernor 误降
                    // Critical → 所有 Low 模块降频 → 列车窜/打字机不同步/开炮不同步）。
                    NetworkGovernor.Instance.RecordPingSent();
                }
            }
            else if (State == SessionState.Joined && HostSteamId != 0)
            {
                var w = NetProtocol.Begin(MsgType.Ping);
                w.Put(Environment.TickCount64);
                Transport.Send(HostSteamId, NetProtocol.Snapshot(w), false);
                Local.LastPingSentTicks = Environment.TickCount64;
                NetworkGovernor.Instance.RecordPingSent();
            }
        }

        // ---- 同步方案分支（--sync old|new）----
        // V1 硬编码同步模块（PlayerSync/RecordPlayerSync/ReloadSync/MapSync/ControlSync 含 ValueSync）
        // 是 V1 专属：--sync new 时不跑，避免与 SyncV2 分层模块（CoopSyncRegistry 注册）双重同步/互踩。
        if (!OpenNestCoop.Net.AutoJoin.WantNewSync)
        {
            // ⚠️ per-module 网络分级：V1 硬编码模块按各自优先级缩放（关键交互几乎不降、高频容忍优先降）
            // 帧性能剖析：每模块耗时记入 FrameProfiler（F7 诊断）
            Profile("PlayerSync", () => PlayerSync.Tick(dt * NetworkGovernor.Instance.ModuleFreq(NetModulePriority.Critical)));       // 玩家化身（关键）
            Profile("RecordPlayerSync", () => RecordPlayerSync.Tick(dt * NetworkGovernor.Instance.ModuleFreq(NetModulePriority.Low)));  // 唱片机（容忍丢失）
            Profile("ReloadSync", () => ReloadSync.Tick(dt * NetworkGovernor.Instance.ModuleFreq(NetModulePriority.Critical)));        // 装填/开火（关键）
            Profile("MapSync", () => MapSync.Tick(dt * NetworkGovernor.Instance.ModuleFreq(NetModulePriority.Normal)));                 // 地图标记
            Profile("ControlSync", () => ControlSync.Tick(dt * NetworkGovernor.Instance.ModuleFreq(NetModulePriority.Critical)));       // 交互控件（含 ValueSync）
        }

        // 自定义同步模块（V1 注册模块 / SyncV2 分层模块）——每模块按自己的 NetPriority 缩放（TickAll 内，并逐模块计时）
        Profile("TickAll", () => CoopSyncRegistry.TickAll(dt));

        // 帧末：合包发出不可靠状态包（计时）
        Profile("FlushBatch", () => FlushBatch());

        // ⚠️ 2026-08-26：周期清理过期分片缓冲（防内存泄漏——某端未收齐的 Fragment 段）
        _fragCleanTimer += dt;
        if (_fragCleanTimer >= 10f)
        {
            _fragCleanTimer = 0f;
            try
            {
                if (_fragBuf.Count > 0)
                {
                    float now = UnityEngine.Time.realtimeSinceStartup;
                    var stale = new System.Collections.Generic.List<(ulong, ushort)>();
                    foreach (var kv in _fragBuf)
                        if (now - kv.Value.First > 10f) stale.Add(kv.Key);
                    foreach (var k in stale) _fragBuf.Remove(k);
                }
            }
            catch { }
        }

        // 每 10s 汇总收发统计（周期增量，打印后清零）
        _statsTimer += dt;
        if (_statsTimer >= 10f)
        {
            _statsTimer = 0f;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 256; i++)
            {
                if (_recvStats[i] > 0 || _sendStats[i] > 0)
                {
                    sb.Append($" {(MsgType)i}:R{_recvStats[i]}/S{_sendStats[i]}");
                    _recvStats[i] = 0; _sendStats[i] = 0;
                }
            }
            CoopLog.Info("Net.stats10s", () => $"[Net] stats 10s  state={State}{sb}", 10f);
        }
    }

    /// <summary>额外登记的关键消息类型集合（<see cref="RegisterCriticalType"/> 显式登记 +
    /// Critical/High 优先级模块自动登记）。用 int 以覆盖 2 字节扩展类型（≥256）。
    /// ⚠️ 2026-09-12：旧实现只有硬编码 byte 表（且调用点写死 `t &lt; 256`）→ 动态通道
    /// （`shotparams`/`blocker`/铁巢图标 146 等）**不在保护范围**，合包满时被丢弃且不重发
    /// = “模组丢包”根因之一。改为「硬编码表 ∪ 登记集合」后动态通道同样受保护。</summary>
    private static readonly System.Collections.Generic.HashSet<int> _criticalTypes = new();

    /// <summary>登记关键消息类型（幂等；可多次调用）。合包上限丢弃时该类型**永不丢**（reliable 时）。
    /// 用于“边沿触发 / 丢了就永久不同步”的消息（事件、交互、参数下发）。
    /// 何时需要显式登记：
    ///   ① 模块优先级不是 Critical/High，但其中某几个类型视为关键（如混合模块的附加类型）；
    ///   ② 函数式注册（<see cref="RegisterChannel(string, ISyncedModule, NetModulePriority?)"/>）之外
    ///      自管发送的通道；
    ///   ③ 第三方扩展模组用自己的通道发关键消息时主动登记（见 `docs/API.md` 第 3 节）。
    /// 硬编码表内的框架类型（GunFire/Impact/ReloadState/…）已默认保护，**无需重复登记**。</summary>
    public static void RegisterCriticalType(int t)
    {
        if (t <= 0) return;
        try { _criticalTypes.Add(t); } catch { }
    }

    /// <summary>批量登记关键消息类型（见 <see cref="RegisterCriticalType(int)"/>）。</summary>
    public static void RegisterCriticalTypes(params int[] types)
    {
        if (types == null) return;
        foreach (var t in types) RegisterCriticalType(t);
    }

    /// <summary>查询某类型是否被保护（硬编码表 ∪ 登记集合）——供模块自检/诊断打印。</summary>
    public static bool IsCriticalRegistered(int t) => IsCriticalType(t);

    /// <summary>关键消息类型（事件/交互/关键状态——边沿触发或影响一致性，丢失即不同步）。
    /// 合包上限丢弃时保护这些类型（不丢）；只丢可重发的周期状态。
    /// 判定 = 硬编码表（下 switch）∪ <see cref="_criticalTypes"/>（显式登记 / Critical·High 优先级模块）。</summary>
    private static bool IsCriticalType(int t)
    {
        if (_criticalTypes.Contains(t)) return true;
        switch (t)
        {
            case (byte)MsgType.TurretState:       // 炮塔状态
            case (byte)MsgType.GunFire:           // 开火事件（直发，兜底保护）
            case (byte)MsgType.Impact:            // 炮弹落点
            case (byte)MsgType.ReloadState:       // 装填状态
            case (byte)MsgType.ReloadCmd:         // 装填上行
            case (byte)MsgType.FireRequest:       // 开火请求
            case (byte)MsgType.ControlState:      // 控件状态广播（炮塔操作）
            case (byte)MsgType.ControlCmd:        // 控件输入上行（操作者→主机）
            case (byte)MsgType.MapMarkerAdd:
            case (byte)MsgType.MapMarkerRemove:
            case (byte)MsgType.MapMarkerClearAll:
            case (byte)MsgType.MapMarkerUpdate:   // 战术标记增删改（事件）
            case (byte)MsgType.ReloadAdvance:     // 装填推进/回退
            case (byte)MsgType.PowderEvent:       // 发射药事件
            case (byte)MsgType.CatEvent:          // 猫交互事件
            case (byte)MsgType.V2Event:           // V2 事件层（泛型事件，谁操作谁发）
            case (byte)MsgType.V2Button:          // V2 按钮/交互控件状态
            case (byte)MsgType.V2ReloadCmd:       // V2 装填上行
                return true;
        }
        return false;
    }

    /// <summary>把状态子包加入合包缓冲（toAll=true 广播给所有非本地，false 发主机）。
    /// reliable=false 走 unreliable 通道（容忍丢失的高频连续状态，减少 reliable 拥塞）——
    /// 发送端必须提供低频 reliable 保底（心跳/全量广播）防长期丢失。默认 reliable 兼容现有调用。
    /// ⚠️ 2026-08-26：单子包超过 <see cref="FragmentThreshold"/>（900B）自动切段（Fragment），
    /// 接收端重组——Steam P2P 单包硬性限制（unreliable ~1200B / reliable 1MB）下大包必须拆小。</summary>
    public void EnqueueBatch(byte[] data, bool toAll, bool reliable = true)
    {
        if (data == null || data.Length == 0) return;
        // ⚠️ 2026-08-26：大包自动分片（Steam P2P 单包硬性限制）——超阈值切成多段 Fragment 入队，接收端重组
        if (data.Length > FragmentThreshold)
        {
            Fragmentize(data, seg => EnqueueRaw(seg, toAll, reliable));
            return;
        }
        EnqueueRaw(data, toAll, reliable);
    }

    /// <summary>真正入队（EnqueueRaw）：单子包（含分片段）加入合包缓冲，帧末 FlushBatch 合并发出。
    /// 分片段也走同一队列（合包），接收端 Batch 拆包后重组。</summary>
    private void EnqueueRaw(byte[] data, bool toAll, bool reliable)
    {
        try
        {
            // 前导字节类型按宽度解析（1 字节 / 2 字节大端 ushort）——兼容 2 字节扩展
            int t = NetProtocol.TypeLen >= 2 ? ((data[0] << 8) | (data.Length > 1 ? (data[1] & 0xFF) : 0)) : (data[0] & 0xFF);
            if (t >= 0 && t < 256) _sendStats[t]++;
            // per-module 带宽占用：按 MsgType 归因入队字节（NetworkGovernor 诊断统计；仅统计 1 字节段）
            if (t < 256) NetworkGovernor.Instance.RecordTypeBytes((byte)t, data.Length);
            // ⚠️ 合包上限由 NetworkGovernor 分级动态控制：负载高（低档）→ 上限缩小 → 超限丢弃。
            // 丢弃 = 负载过高信号（RecordDrop 喂回评估器 → 触发降频）；发送端 reliable 保底/心跳负责最终对齐。
            // 注意：只有 reliable 丢弃算拥塞信号——unreliable 本身容忍丢失（Critical 档还会主动关闭），
            // 若计入会误判拥塞 → 卡死在最低档无法回升。
            int maxItems = NetworkGovernor.Instance.MaxBatchItems;
            // ⚠️ 关键类型（事件/交互/装填/开火/落点等边沿触发，丢了永久不同步）**不参与合包上限丢弃**——
            // 合包满时关键包仍入队（FlushBatch 拆包发出）；只丢可重发的周期状态。避免网络分级"吞关键包"导致
            // 客机炮弹落点/装填/交互不同步。2 字节扩展类型按非关键处理（周期状态，可重发）。
            bool critical = reliable && IsCriticalType(t);
            bool dropped = false;
            if (reliable)
            {
                if (toAll) { if (_broadcastQueue.Count < maxItems || critical) _broadcastQueue.Add(data); else dropped = true; }
                else { if (_hostQueue.Count < maxItems || critical) _hostQueue.Add(data); else dropped = true; }
            }
            else
            {
                if (toAll) { if (_broadcastQueueU.Count < maxItems) _broadcastQueueU.Add(data); }
                else { if (_hostQueueU.Count < maxItems) _hostQueueU.Add(data); }
            }
            if (dropped) NetworkGovernor.Instance.RecordDrop();
        }
        catch { }
    }

    /// <summary>大包切段：把 data 切成多段，每段构造 Fragment 子包 [Fragment][origType][fragId][total][index][payload]
    /// （payload = 原始 data 不含首字节类型）。切完回调 <paramref name="enqueue"/>（入队发送）。
    /// ⚠️ 2026-08-26：Steam P2P 单包硬性限制——unreliable ~1200B / reliable 1MB，超限整包拒收/丢。
    /// 通用分片：任何模块的大包自动拆小，接收端重组后交给原模块（对模块透明）。</summary>
    private void Fragmentize(byte[] data, Action<byte[]> enqueue)
    {
        try
        {
            int typeLen = NetProtocol.TypeLen; // 前导字节宽度（1/2）
            int origType = typeLen >= 2 ? ((data[0] << 8) | (data[1] & 0xFF)) : (data[0] & 0xFF);
            int payloadLen = data.Length - typeLen; // 去掉前导字节
            int maxSeg = FragmentThreshold - 8; // 预留 Fragment 头（type + origType 2B + fragId 2B + total + index ≈ 8B）
            if (maxSeg < 64) maxSeg = 64;
            int total = (payloadLen + maxSeg - 1) / maxSeg;
            if (total > 255) total = 255; // 防溢出
            ushort fid = ++_fragId;
            for (int i = 0; i < total; i++)
            {
                int off = i * maxSeg + typeLen; // 跳过前导字节
                int len = Math.Min(maxSeg, data.Length - off);
                var w = NetProtocol.Begin(MsgType.Fragment);
                // 内部格式：origType 恒按 ushort 写（兼容 2 字节扩展类型；两端同 build）
                w.Put((ushort)origType);
                w.Put(fid);
                w.Put((byte)total);
                w.Put((byte)i);
                w.Put(data, off, len);
                enqueue(NetProtocol.Snapshot(w));
            }
        }
        catch { }
    }

    /// <summary>重组分片：收齐后拼回原始 data（前导字节 origType + 各段 payload），递归 OnPacket 处理。</summary>
    private bool ReassembleFragment(ulong from, byte[] data, out byte[] full)
    {
        full = null;
        try
        {
            var r = new NetDataReader(data);
            r.SkipBytes(NetProtocol.TypeLen); // 跳过 Fragment 类型（宽度感知）
            int origType = r.GetUShort();     // 内部格式恒 ushort
            ushort fid = r.GetUShort();
            int total = r.GetByte();
            int index = r.GetByte();
            if (total <= 0 || index < 0 || index >= total) return false;
            var key = (from, fid);
            if (!_fragBuf.TryGetValue(key, out var fb))
            {
                if (_fragBuf.Count > 128) { try { _fragBuf.Clear(); } catch { } } // 防无限增长
                fb = new FragBuf { Total = total, Segs = new byte[total][], Got = 0, First = UnityEngine.Time.realtimeSinceStartup };
                _fragBuf[key] = fb;
            }
            // 提取 payload（剩余全部字节）
            int avail = r.AvailableBytes;
            if (avail <= 0 || avail > 4096) return false;
            var seg = new byte[avail];
            System.Array.Copy(r.RawData, r.Position, seg, 0, avail);
            // ⚠️ FragBuf 是引用类型：fb 即字典里的实例，修改直接生效（值类型元组副本曾导致 got 永不写回 → 永不重组）
            if (fb.Segs[index] == null) { fb.Segs[index] = seg; fb.Got++; }
            if (fb.Got < fb.Total) return false;
            // 收齐：拼接（前导字节按宽度还原，保证递归 OnPacket 的 TypeOf 正确）
            int typeLen = NetProtocol.TypeLen;
            int len = typeLen;
            for (int i = 0; i < fb.Total; i++) len += fb.Segs[i]?.Length ?? 0;
            full = new byte[len];
            if (typeLen >= 2) { full[0] = (byte)(origType >> 8); full[1] = (byte)(origType & 0xFF); }
            else full[0] = (byte)origType;
            int pos = typeLen;
            for (int i = 0; i < fb.Total; i++) { var s = fb.Segs[i]; if (s == null) return false; System.Array.Copy(s, 0, full, pos, s.Length); pos += s.Length; }
            _fragBuf.Remove(key);
            return true;
        }
        catch { return false; }
    }

    /// <summary>帧末把缓冲的子包合并成 Batch 包发出。
    /// 用 reliable 通道：Steam P2P unreliable 单包上限约 1200B（超限整包被拒收），且高频率下
    /// 丢包率高（任务内状态同步全断的元凶之一）。reliable 上限 1MB + 自动重传，保证送达；
    /// 仍按字节阈值拆包，避免单包过大（reliable 大包也会显著增加延迟/拥塞）。</summary>
    private void FlushBatch()
    {
        try
        {
            // ⚠️ 拆包阈值由 NetworkGovernor 分级动态控制（负载高 → 包更小 → 单包延迟/重传成本更低）
            int maxPacketBytes = NetworkGovernor.Instance.MaxPacketBytes;
            if (_broadcastQueue.Count > 0)
            {
                int subs = _broadcastQueue.Count, bytes = 0;
                foreach (var d in _broadcastQueue) bytes += d.Length + 2;
                if ((++_flushLog % 30) == 1)
                    CoopLog.Debug("Net.flush", () => $"[Net] flush toAll subs={subs} bytes≈{bytes} peers={Roster.Count - 1}");
                NetworkGovernor.Instance.RecordQueue(_broadcastQueue.Count);
                foreach (var group in SplitBatches(_broadcastQueue, maxPacketBytes))
                {
                    var (packetData, packetLen) = BuildBatch(group);
                    // 模拟 Steam P2P 流量/单包限制：超限整组丢弃（日志节流）
                    if (!NetLagSim.AllowSend(packetLen, true)) continue;
                    int peers = 0;
                    foreach (var p in Roster)
                        if (!p.IsLocal) { Transport.Send(p.SteamId, packetData, packetLen, true); peers++; }
                    if (peers > 0) NetworkGovernor.Instance.RecordSent(packetLen * peers);
                }
                _broadcastQueue.Clear();
            }
            if (_hostQueue.Count > 0)
            {
                int subs = _hostQueue.Count, bytes = 0;
                foreach (var d in _hostQueue) bytes += d.Length + 2;
                if ((++_flushLog % 30) == 1)
                    CoopLog.Debug("Net.flushHost", () => $"[Net] flush toHost subs={subs} bytes≈{bytes}");
                NetworkGovernor.Instance.RecordQueue(_hostQueue.Count);
                foreach (var group in SplitBatches(_hostQueue, maxPacketBytes))
                {
                    var (packetData, packetLen) = BuildBatch(group);
                    // 模拟 Steam P2P 流量/单包限制：超限整组丢弃（日志节流）
                    if (!NetLagSim.AllowSend(packetLen, true)) continue;
                    if (HostSteamId != 0) { Transport.Send(HostSteamId, packetData, packetLen, true); NetworkGovernor.Instance.RecordSent(packetLen); }
                }
                _hostQueue.Clear();
            }
            // E 分级：unreliable 通道（容忍丢失的高频连续状态）——发送端的心跳/全量 reliable 保底负责最终对齐。
            // ⚠️ unreliable 消息只要任意分片丢失 → 整条全部丢弃（不会收到残缺版）。因此：
            //   ① 不合并成大 Batch（分片整条丢 + 连坐多个子包）——每个子包单独小包发送，互不影响；
            //   ② 单条超过安全阈值 → 降级 reliable 发送（保证送达，避免大 unreliable 分片整条丢）。
            int unreliableMax = NetworkGovernor.Instance.UnreliableMaxBytes;
            if (NetworkGovernor.Instance.AllowUnreliable)
            {
                if (_broadcastQueueU.Count > 0)
                {
                    NetworkGovernor.Instance.RecordQueue(_broadcastQueueU.Count);
                    int peers = 0;
                    foreach (var d in _broadcastQueueU)
                    {
                        bool rel = d.Length > unreliableMax;
                        // 模拟 Steam P2P 带宽/单包/丢包限制：拒绝则丢弃该条（unreliable 容忍丢失）
                        if (!NetLagSim.AllowSend(d.Length, rel)) continue;
                        foreach (var p in Roster)
                            if (!p.IsLocal) { Transport.Send(p.SteamId, d, d.Length, rel); peers++; }
                        NetworkGovernor.Instance.RecordSent(d.Length * peers);
                    }
                    _broadcastQueueU.Clear();
                }
                if (_hostQueueU.Count > 0)
                {
                    foreach (var d in _hostQueueU)
                    {
                        bool rel = d.Length > unreliableMax;
                        if (!NetLagSim.AllowSend(d.Length, rel)) continue;
                        if (HostSteamId != 0) { Transport.Send(HostSteamId, d, d.Length, rel); NetworkGovernor.Instance.RecordSent(d.Length); }
                    }
                    _hostQueueU.Clear();
                }
            }
            else
            {
                // ⚠️ Critical（最低档）：unreliable 通道关闭——高频连续状态直接丢弃（发送端的
                // reliable 保底/心跳负责最终对齐），最大化节省带宽、避免 unreliable 丢包干扰。
                // 这是主动策略性丢弃，不计入 RecordDrop（否则误判拥塞 → 卡死在 Critical 无法回升）。
                _broadcastQueueU.Clear();
                _hostQueueU.Clear();
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"FlushBatch: {ex.Message}"); }
    }

    /// <summary>把子包列表按总字节阈值拆成多个子包组（每组随后 BuildBatch 成一个包）。</summary>
    private static List<List<byte[]>> SplitBatches(List<byte[]> items, int maxBytes)
    {
        var groups = new List<List<byte[]>>();
        var cur = new List<byte[]>();
        int curBytes = 0;
        foreach (var d in items)
        {
            int add = d.Length + 2; // +ushort 长度前缀
            if (cur.Count > 0 && curBytes + add > maxBytes)
            {
                groups.Add(cur);
                cur = new List<byte[]>();
                curBytes = 0;
            }
            cur.Add(d);
            curBytes += add;
        }
        if (cur.Count > 0) groups.Add(cur);
        return groups;
    }

    private (byte[] data, int len) BuildBatch(List<byte[]> items)
    {
        if (_batchWriter == null) _batchWriter = new NetDataWriter();
        else _batchWriter.Reset(); // 复用内部 buffer（省每帧 new + Snapshot 合包副本）；调用方同步发给各 peer，下一轮 Reset 前安全
        _batchWriter.Put((byte)MsgType.Batch);
        int n = Math.Min(items.Count, 255);
        _batchWriter.Put((byte)n);
        for (int i = 0; i < n; i++)
        {
            var d = items[i];
            // 子包长度用 ushort（byte 上限 255 会截断 EntitySync 等大包）
            int len = d.Length;
            _batchWriter.Put((ushort)len);
            _batchWriter.Put(d, 0, len);
        }
        return (_batchWriter.Data, _batchWriter.Length);
    }

    /// <summary>直发大包（单播，不走合包队列）：超 <see cref="FragmentThreshold"/> 自动分片（Steam P2P 单包
    /// 硬性限制）。StateSnapshot 中途加入快照 / ReloadSync 全量等直发路径用——避免大单播包被 Steam 拒收/丢。</summary>
    public void SendDirectFragmented(ulong to, byte[] data, bool reliable)
    {
        if (to == 0 || data == null || data.Length == 0) return;
        if (data.Length > FragmentThreshold)
            Fragmentize(data, seg => { try { Transport.Send(to, seg, seg.Length, reliable); } catch { } });
        else
        {
            try { Transport.Send(to, data, data.Length, reliable); } catch { }
        }
    }

    // ---- 大厅操作 ----

    /// <summary>创建房间：Steam 模式建房（异步）；本地模式监听 TCP 端口。建房前保存房间设置供下次预填。</summary>
    public void CreateLobby()
    {
        if (State != SessionState.Idle || _creatingLobby) return;
        Core.LobbySettings.Save(PendingLobbyName, PendingMaxPlayers, PendingPassword);
        if (LocalMode)
        {
            LastError = "";
            if (LocalStartHost())
            {
                _creatingLobby = false;
                OnLobbyEntered();
            }
            return;
        }
        if (!SteamReady) { LastError = Loc("ErrSteamNotReady"); return; }
        LastError = "";
        _creatingLobby = true; // 创建是异步的，回调前 State 仍为 Idle，防重复创建多个大厅
        Lobby.CreateLobby(PendingLobbyName, PendingMaxPlayers, PendingPassword);
    }

    public void RefreshBrowser()
    {
        if (State != SessionState.Idle) return;
        if (NonSteam) return;
        if (!SteamReady) { LastError = Loc("ErrSteamNotReady"); return; }
        Refreshing = true;
        Lobby.RefreshBrowser();
    }

    /// <summary>最近进入的房间 Steam lobby id（0=无）。大厅 UI 显示"重连上次房间"按钮用。</summary>
    public ulong LastLobbyId => Core.LobbySettings.RecentLobbyId;

    /// <summary>快速重连最近进入的房间（Steam 模式；无记录/本地模式返回 false）。</summary>
    public bool JoinRecentLobby()
    {
        if (State != SessionState.Idle) return false;
        if (NonSteam) return false;
        if (!SteamReady) { LastError = Loc("ErrSteamNotReady"); return false; }
        var id = Core.LobbySettings.RecentLobbyId;
        if (id == 0) return false;
        LastError = "";
        Lobby.JoinLobby(id);
        return true;
    }

    public bool JoinLobby(LobbyInfo info)
    {
        if (State != SessionState.Idle) return false;
        if (LanMode) return false;      // 局域网走 JoinLanRoom（IP 直连）
        if (LocalMode)
        {
            LastError = "";
            if (LocalStartClient())
            {
                OnLobbyEntered();
                return true;
            }
            return false;
        }
        if (!SteamReady) { LastError = Loc("ErrSteamNotReady"); return false; }
        // 有密码的房间必须已设置 PendingPassword（UI 弹输入框预校验后设置）
        if (info.HasPassword && string.IsNullOrEmpty(PendingPassword))
        {
            LastError = Loc("ErrPasswordProtected");
            return false;
        }
        LastError = "";
        Lobby.JoinLobby(info.Id);
        return true;
    }

    /// <summary>加入前客户端本地预校验房间密码（用 LobbyInfo 的 pwd hash）。不匹配不加入，
    /// 避免"错误密码先加入大厅再被主机踢"。仅预校验，主机 OnHello 仍做权威校验（防绕过）。</summary>
    public bool VerifyRoomPassword(LobbyInfo info, string password)
        => info == null || !info.HasPassword || NetConfig.HashPassword(password ?? "") == info.PasswordHash;

    public void LeaveSession()
    {
        if (NonSteam)
        {
            if (LocalMode) (Transport as LocalTransport)?.Dispose();
            else StopLanSession();
            OnLobbyLeft();
            return;
        }
        Lobby.LeaveLobby();
        // OnLobbyLeft 会清理状态
    }

    /// <summary>结束局域网会话：停 UDP 应答 + 关 TCP 连接 + 清发现列表。</summary>
    private void StopLanSession()
    {
        try { LanDiscovery.StopAnnounce(); } catch { }
        try { (Transport as LanTransport)?.Dispose(); } catch { }
        try { LanDiscovery.Clear(); } catch { }
        LanMode = false;
    }

    // ---- 踢人 / 封禁 / 邀请 ----

    /// <summary>主机踢出成员（默认本次会话封禁，防止其重新加入）。</summary>
    public void KickPlayer(ulong steamId, bool ban = true)
    {
        if (!IsHost) return;
        PlayerSession p = null;
        foreach (var s in Roster) if (s.SteamId == steamId) { p = s; break; }
        if (p == null || p.IsLocal) return;

        if (ban) _banned.Add(steamId);

        // 通知被踢者（Kick 消息，reliable）→ 对端 LeaveSession（带原因，被踢端 UI 提示）
        try
        {
            var w = NetProtocol.Begin(MsgType.Kick);
            w.Put("Kicked by host");
            var data = NetProtocol.Snapshot(w);
            Transport.Send(steamId, data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Net] Kick send failed: {ex.Message}"); }

        // 关闭 Steam P2P 会话（可选，尽力而为）
        try
        {
#if !MELONLOADER
            SteamNetworking.CloseP2PSessionWithUser((CSteamID)steamId);
#endif
        }
        catch { }

        // 从名单移除 + 广播
        Roster.Remove(p);
        BroadcastRoster();
        RosterChanged?.Invoke();
        CoopRuntime.LogSource?.LogInfo($"[Net] kicked {p.Name} (ban={ban})");
    }

    /// <summary>主机解除本次会话封禁（允许该 SteamId 重新加入）。</summary>
    public void UnbanPlayer(ulong steamId)
    {
        if (!IsHost) return;
        _banned.Remove(steamId);
        CoopRuntime.LogSource?.LogInfo($"[Net] unbanned {steamId}");
    }

    /// <summary>当前是否被封禁（主机查询用，或本地回显）。</summary>
    public bool IsBanned(ulong steamId) => _banned.Contains(steamId);

    /// <summary>打开 Steam 好友邀请对话框（Steam overlay）。非 Steam 模式（本地回环）不支持。</summary>
    public void InviteFriends()
    {
        if (NonSteam)
        {
            LastError = LocalMode ? Loc("ErrInviteUnsupportedLocal") : Loc("ErrInviteUnsupportedLan");
            CoopRuntime.LogSource?.LogInfo($"[Net] invite not supported in {(LocalMode ? "local" : "LAN")} mode");
            return;
        }
        try
        {
#if !MELONLOADER
            if (!Lobby.LobbyID.IsValid()) return;
            SteamFriends.ActivateGameOverlayInviteDialog(Lobby.LobbyID);
            CoopRuntime.LogSource?.LogInfo("[Net] Steam invite dialog opened");
#else
            CoopRuntime.LogSource?.LogInfo("[Net] ML-mode invite not supported yet (Steam overlay API difference)");
#endif
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Net] InviteFriends: {ex.Message}"); }
    }

    /// <summary>被踢端处理：收到 Kick 消息 → 标记 + 显示原因 + 离开会话（UI 提示）。</summary>
    private void OnKicked(NetDataReader r)
    {
        WasKicked = true;
        string reason = "";
        try { if (r != null && r.AvailableBytes > 0) reason = LocalizeReason(r.GetString()); } catch { }
        if (reason.Length > 0)
        {
            LastError = reason;
            CoopRuntime.LogSource?.LogInfo($"[Net] kicked: {reason}");
        }
        else
        {
            CoopRuntime.LogSource?.LogInfo("[Net] kicked by host");
        }
        if (NonSteam)
        {
            if (LocalMode) (Transport as LocalTransport)?.Dispose();
            else StopLanSession();
            OnLobbyLeft();
        }
        else
        {
            Lobby.LeaveLobby();
        }
    }

    // ---- 本地回环模式（双开测试，不经 Steam） ----

    /// <summary>本地模式：作为 host 监听 TCP 端口，等待 client 连接。</summary>
    public bool LocalStartHost()
    {
        try
        {
            var lt = new LocalTransport();
            lt.ClientConnected += () => OnLocalClientConnected(lt);
            if (!lt.StartHost(LocalPort)) return false;
            Transport = lt;
            Local = new PlayerSession
            {
                SteamId = 1,
                Name = Loc("LocalHostName"),
                IsLocal = true,
                PlayerId = 0,
                IsHost = true,
                Role = CrewRole.Commander,
            };
            CoopRuntime.LogSource?.LogInfo($"[Net] local host ready (port {LocalPort})");
            return true;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Net] LocalStartHost: {ex.Message}"); return false; }
    }

    /// <summary>本地模式：作为 client 连接 host 的 TCP 端口。</summary>
    public bool LocalStartClient()
    {
        try
        {
            var lt = new LocalTransport();
            if (!lt.Connect(LocalPort)) return false;
            Transport = lt;
            Local = new PlayerSession
            {
                SteamId = 2,
                Name = Loc("LocalClientName"),
                IsLocal = true,
                PlayerId = 255,
            };
            CoopRuntime.LogSource?.LogInfo($"[Net] local client connecting host (port {LocalPort})");
            return true;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Net] LocalStartClient: {ex.Message}"); return false; }
    }

    /// <summary>本地模式：host 检测到 client 连上 → 分配 PlayerId + 发 Welcome（复用 OnHello 流程）。</summary>
    private void OnLocalClientConnected(LocalTransport lt)
    {
        try
        {
            CoopRuntime.LogSource?.LogInfo("[Net] local host got client connection, waiting for Hello...");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Net] OnLocalClientConnected: {ex.Message}"); }
    }

    // ---- 局域网模式（TCP 直连，不经 Steam 大厅；见 docs/LAN.md） ----

    /// <summary>局域网：从冲突身份派生一个唯一 id（高 16 位固定 0xFACE → 日志/UI 显示为 Fake#xxxx）。
    /// 用于“同一台机器 + 同一 Steam 账号双开”或“两个客户端声称同一 SteamID/FakeID”的情况。</summary>
    private ulong DeriveUniqueLanId(ulong baseId)
    {
        ulong seed = baseId & 0x0000FFFFFFFFFFFFUL;
        for (ulong salt = 1; salt < 4096; salt++)
        {
            ulong cand = 0xFACE000000000000UL | ((seed + salt) & 0x0000FFFFFFFFFFFFUL);
            if (Local != null && cand == Local.SteamId) continue;
            bool used = false;
            foreach (var p in Roster) if (p.SteamId == cand) { used = true; break; }
            if (!used)
            {
                var lt = Transport as LanTransport;
                if (lt != null && lt.HasPeer(cand)) continue;
                return cand;
            }
        }
        return 0xFACE00000000FFFFUL;
    }

    /// <summary>已发现的局域网房间（UI 列表用；后台扫描线程填充）。</summary>
    public List<LanDiscovery.LanRoom> LanRooms => LanDiscovery.Rooms;
    /// <summary>正在扫描局域网。</summary>
    public bool LanScanning => LanDiscovery.Scanning;

    /// <summary>扫描局域网房间（后台线程，不阻塞 UI）。同时探当前端口 + 默认端口（主机改了端口也能发现）。</summary>
    public void ScanLan()
    {
        try
        {
            int p = LanJoinPort;
            int d = Core.CoopConfig.LanPort > 0 ? Core.CoopConfig.LanPort : NetConfig.LocalDefaultPort;
            if (d != p) LanDiscovery.Scan(new[] { p, d });
            else LanDiscovery.Scan(p);
        }
        catch { }
    }

    /// <summary>局域网加入前的密码预校验（用发现包里的 hash；主机仍会权威校验，防绕过）。</summary>
    public bool VerifyLanPassword(LanDiscovery.LanRoom room, string password)
        => room == null || !room.HasPassword || NetConfig.HashPassword(password ?? "") == room.PasswordHash;

    /// <summary>创建局域网房间：主机监听任意网卡（<see cref="LanJoinPort"/>）+ UDP 应答发现。
    /// 身份用 <see cref="LocalIdentity"/>（SteamID 优先，否则 FakeID）。</summary>
    public bool CreateLanRoom()
    {
        if (State != SessionState.Idle || _creatingLobby) return false;
        try { Core.LobbySettings.Save(PendingLobbyName, PendingMaxPlayers, PendingPassword); } catch { }
        LastError = "";
        LanMode = true;
        try
        {
            var lt = new LanTransport();
            lt.PeerConnected += OnLanPeerConnected;
            lt.PeerDisconnected += OnLanPeerDisconnected;
            if (!lt.StartHost(LanJoinPort))
            {
                LanMode = false;
                LastError = Loc("ErrLanPortInUse", LanJoinPort);
                return false;
            }
            Transport = lt;
            Local = new PlayerSession
            {
                SteamId = LocalIdentity,
                Name = LocalDisplayName,
                IsLocal = true,
                IsHost = true,
                PlayerId = 0,
                Role = CrewRole.Commander,
            };
            PersistLanName();
            CoopRuntime.LogSource?.LogInfo($"[Net] LAN host ready port={LanJoinPort} identity={LocalIdentity} ({Core.Identity.Tag(LocalIdentity)}) name='{Local.Name}'");
            OnLobbyEntered();
            return true;
        }
        catch (Exception ex)
        {
            LanMode = false;
            LastError = Loc("ErrLanHostFailed", ex.Message);
            CoopRuntime.LogSource?.LogWarning($"[Net] CreateLanRoom: {ex.Message}");
            return false;
        }
    }

    /// <summary>加入局域网房间（TCP 直连；Hello 里声明身份）。成功返回 true（失败看 <see cref="LastError"/>）。</summary>
    public bool JoinLanRoom()
    {
        if (State != SessionState.Idle) return false;
        if (string.IsNullOrWhiteSpace(LanJoinHost)) { LastError = Loc("ErrLanNoIp"); return false; }
        LastError = "";
        LanMode = true;
        try
        {
            var lt = new LanTransport();
            lt.HostDisconnected += OnLanHostDisconnected;
            if (!lt.Connect(LanJoinHost.Trim(), LanJoinPort))
            {
                LanMode = false;
                LastError = Loc("ErrLanUnreachable", LanJoinHost.Trim(), LanJoinPort);
                return false;
            }
            Transport = lt;
            try { Core.CoopConfig.Set("LAN", "LastHost", LanJoinHost.Trim()); } catch { }   // 记住上次地址
            try { LanDiscovery.Clear(); } catch { }
            Local = new PlayerSession
            {
                SteamId = LocalIdentity,
                Name = LocalDisplayName,
                IsLocal = true,
                PlayerId = 255,
            };
            PersistLanName();
            CoopRuntime.LogSource?.LogInfo($"[Net] LAN client connected {LanJoinHost.Trim()}:{LanJoinPort} identity={LocalIdentity} ({Core.Identity.Tag(LocalIdentity)}) name='{Local.Name}'");
            OnLobbyEntered();
            return true;
        }
        catch (Exception ex)
        {
            LanMode = false;
            LastError = Loc("ErrLanJoinFailed", ex.Message);
            CoopRuntime.LogSource?.LogWarning($"[Net] JoinLanRoom: {ex.Message}");
            return false;
        }
    }

    /// <summary>局域网：把 UI 里填的用户名写回配置（下次预填）。空值不写（保留配置里的值）。</summary>
    private void PersistLanName()
    {
        try
        {
            var n = (LanLocalName ?? "").Trim();
            if (n.Length > 0 && n != (Core.CoopConfig.LocalName ?? "")) Core.CoopConfig.Set("Identity", "Name", n);
        }
        catch { }
    }

    /// <summary>局域网主机：开始应答 UDP 查询（队友“扫描局域网”能看到本房间）。</summary>
    private void StartLanAnnounce()
    {
        try
        {
            LanDiscovery.StartAnnounce(LanJoinPort, () =>
                (PendingLobbyName, Roster.Count, PendingMaxPlayers, RoomPasswordHash()));
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Net] StartLanAnnounce: {ex.Message}"); }
    }

    /// <summary>局域网主机：新连接接入（尚未 Hello；peerId 是临时连接 id，Hello 后重绑为身份）。
    /// ⚠️ 本回调在**后台读线程**触发——只允许日志（不碰名单/UI），否则 IL2CPP 跨线程访问 Unity 对象会崩。</summary>
    private void OnLanPeerConnected(ulong tempPeerId)
    {
        CoopRuntime.LogSource?.LogInfo($"[Net] LAN peer connected (temp id {tempPeerId}), waiting for Hello...");
    }

    /// <summary>局域网主机：连接断开（后台线程）→ 只入队，真正的名单移除在主线程 <see cref="ProcessLanPeerLeft"/>。</summary>
    private void OnLanPeerDisconnected(ulong peerId)
    {
        _lanPeersGone.Enqueue(peerId);
    }

    /// <summary>局域网客机：与主机断开（后台线程）→ 只置标志，主线程离开会话。</summary>
    private void OnLanHostDisconnected() { _lanHostLost = true; }

    /// <summary>局域网主机：在主线程移除离开的成员 + 广播新名单。</summary>
    private void ProcessLanPeerLeft(ulong peerId)
    {
        try
        {
            PlayerSession gone = null;
            foreach (var p in Roster) if (p.SteamId == peerId) { gone = p; break; }
            if (gone == null) return;
            Roster.Remove(gone);
            BroadcastRoster();
            RosterChanged?.Invoke();
            CoopRuntime.LogSource?.LogInfo($"[Net] LAN peer {peerId} ({gone.Name}) left");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Net] ProcessLanPeerLeft: {ex.Message}"); }
    }

    public void SendChat(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        var w = NetProtocol.Begin(MsgType.Chat);
        w.Put(Local.Name);
        w.Put(text);
        var data = NetProtocol.Snapshot(w);

        if (State == SessionState.Hosting)
        {
            AddChat($"{Local.Name}", text);
            foreach (var p in Roster)
                if (!p.IsLocal) Transport.Send(p.SteamId, data, true);
        }
        else if (State == SessionState.Joined && HostSteamId != 0)
        {
            // 本地回显（主机转发给其它人；自己立即显示）
            AddChat($"{Local.Name}", text);
            Transport.Send(HostSteamId, data, true);
        }
    }

    /// <summary>主机分配角色（随名单广播给全员）。</summary>
    public void SetRole(ulong steamId, CrewRole role)
    {
        if (!IsHost) return;
        PlayerSession p = null;
        foreach (var s in Roster) if (s.SteamId == steamId) { p = s; break; }
        if (p == null) return;
        p.Role = role;
        BroadcastRoster();
        RosterChanged?.Invoke();
    }

    // ---- 大厅回调 ----

    private void OnLobbyEntered()
    {
        LastError = "";
        _creatingLobby = false;
        bool isHost = NonSteam ? (Local != null && Local.IsHost) : Lobby.IsHost;
        if (isHost)
        {
            State = SessionState.Hosting;
            Local.PlayerId = 0;
            Local.IsHost = true;
            Local.Role = CrewRole.Commander;
            Roster.Clear();
            Roster.Add(Local);
            BroadcastRoster();
            // 自动建房（--autohost）：把 lobby id 写共享文件供 client 自动加入
            if (!NonSteam) AutoJoin.OnHostEntered(this);
            // 局域网：开始应答 UDP 查询（队友的“扫描局域网”能看到本房间）
            if (LanMode) StartLanAnnounce();
        }
        else
        {
            _joinedHostId = HostSteamId;
            // 记录最近进入的房间（Steam 快速重连用）并持久化；局域网/本地模式不走 Steam 大厅
            if (!NonSteam)
            {
                Core.LobbySettings.RecentLobbyId = Lobby.LobbyID.IsValid() ? (ulong)Lobby.LobbyID : 0;
                Core.LobbySettings.Save(PendingLobbyName, PendingMaxPlayers, PendingPassword);
            }
            State = SessionState.Joined;
            // 向主机自我介绍（同步方案 + 前导字节宽度 + 握手版本 + 模组版本 + 房间密码 + 昵称；任一不符会被主机拒绝）
            var w = NetProtocol.Begin(MsgType.Hello);
            w.Put((byte)(OpenNestCoop.Net.AutoJoin.WantNewSync ? 1 : 0)); // syncScheme: 0=old 1=new
            w.Put((byte)NetProtocol.HeaderWidth); // 前导字节宽度沟通（1/2）：Hello/Welcome 恒 1 字节，宽度在载荷内沟通
            w.Put(NetConfig.HandshakeVersion); // 握手协议版本（4 = 含前导字节宽度 + 注册通道表）
            w.Put(NetConfig.Version);          // 模组版本号
            w.Put(PendingPassword ?? "");      // 房间密码（明文，主机按 hash 校验）
            // 局域网：声明本端身份（SteamID 优先，否则 FakeID）——主机据此把传输 peerId 重绑到该身份
            if (LanMode) w.Put(LocalIdentity);
            w.Put(Local.Name);
            // 注册通道（前导字节 1）：客户端把本端注册表附加在 Hello 上行，主机校验后下发权威表
            WriteChannelTable(w);
            Transport.Send(_joinedHostId, NetProtocol.Snapshot(w), true);
        }
        // 注册通道诊断：会话开始列出本端注册表（模块 → 前导字节 + 优先级）
        LogChannelTable(State == SessionState.Hosting ? "host" : "client");
        StateChanged?.Invoke();
        RosterChanged?.Invoke();
    }

    private void OnLobbyLeft()
    {
        _creatingLobby = false;
        State = SessionState.Idle;
        Roster.Clear();
        ChatLog.Clear();
        _joinedHostId = 0;
        _banned.Clear();     // 本次会话封禁：会话结束清空（下次建房重新开始）
        WasKicked = false;   // 被踢提示：会话结束清空
        StateChanged?.Invoke();
        RosterChanged?.Invoke();
        ChatChanged?.Invoke();
    }

    /// <summary>成员变化（加入/离开/主机变更）。</summary>
    private void OnMembersChanged()
    {
        // 本地/局域网：成员变化由 Hello（加入）+ TCP 断开事件维护，不读 Steam 大厅
        if (NonSteam) return;
        if (State == SessionState.Hosting)
        {
            SyncHostRoster();
        }
        else if (State == SessionState.Joined)
        {
            // 主机离开/换人 → 简单处理：结束会话返回大厅
            if (!Lobby.LobbyID.IsValid() || Lobby.HostSteamId == 0 || Lobby.HostSteamId != _joinedHostId)
            {
                if (_joinedHostId != 0)
                {
                    LastError = Loc("ErrHostLeft");
                    CoopRuntime.LogSource?.LogInfo("host left, returning to lobby");
                }
                Lobby.LeaveLobby();
            }
        }
    }

    private void SyncHostRoster()
    {
        if (NonSteam) return;   // 局域网/本地：名单由 Hello/断线事件维护，无 Steam 大厅成员表
        var members = Lobby.GetMembers();
        bool changed = false;

        // 加入新成员
        foreach (var sid in members)
        {
            if (sid == Local.SteamId) continue;
            bool exists = false;
            foreach (var p in Roster) if (p.SteamId == sid) { exists = true; break; }
            if (exists) continue;
            Roster.Add(new PlayerSession
            {
                SteamId = sid,
                Name = SteamFriends.GetFriendPersonaName((CSteamID)sid),
                PlayerId = NextFreeId(),
                IsHost = false,
            });
            changed = true;
            CoopRuntime.LogSource?.LogInfo($"new member joined: {SteamFriends.GetFriendPersonaName((CSteamID)sid)}");
        }

        // 移除离开者
        int removed = Roster.RemoveAll(p => !p.IsLocal && !members.Contains(p.SteamId));
        if (removed > 0)
        {
            changed = true;
            CoopRuntime.LogSource?.LogInfo($"{removed} member(s) left");
        }

        if (changed)
        {
            BroadcastRoster();
            RosterChanged?.Invoke();
        }
    }

    private byte NextFreeId()
    {
        for (byte id = 1; id < 32; id++)
        {
            bool taken = false;
            foreach (var p in Roster) if (p.PlayerId == id) { taken = true; break; }
            if (!taken) return id;
        }
        return 0;
    }

    // ---- 消息处理 ----

    private void OnPacket(ulong from, byte[] data)
    {
        var r = new NetDataReader(data);
        int type = NetProtocol.TypeOf(r); // int：兼容 2 字节扩展类型（≥256）
        if (type >= 0 && type < 256) _recvStats[type]++;

        // 合包容器：拆包后递归处理各子包
        if (type == (int)MsgType.Batch)
        {
            try
            {
                int n = r.GetByte();
                if ((++_batchRecvLog % 20) == 1)
                    CoopLog.Debug("Net.recvBatch", () => $"[Net] recv batch n={n} bytes={data.Length} from={from}");
                for (int i = 0; i < n; i++)
                {
                    if (r.AvailableBytes < 2)
                    {
                        CoopRuntime.LogSource?.LogWarning($"[Net] batch ended early {i}/{n} (missing length prefix avail={r.AvailableBytes})");
                        break;
                    }
                    int len = r.GetUShort();
                    if (len <= 0 || r.AvailableBytes < len)
                    {
                        CoopRuntime.LogSource?.LogWarning($"[Net] batch sub-packet exception idx={i} len={len} avail={r.AvailableBytes} bytes={data.Length}");
                        break;
                    }
                    // 注意：不要用 GetRemainingBytes()+SkipBytes —— LiteNetLib 的
                    // GetRemainingBytes() 会把 reader 位置移到末尾，再 SkipBytes 就过头了
                    // （avail 变负），导致 Batch 里第一个子包之后的子包全部丢失！
                    var sub = new byte[len];
                    try { System.Array.Copy(r.RawData, r.Position, sub, 0, len); }
                    catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Net] batch sub-packet copy failed: {ex.Message}"); break; }
                    r.SkipBytes(len);
                    OnPacket(from, sub);
                }
            }
            catch (Exception ex)
            {
                CoopRuntime.LogSource?.LogWarning($"[Net] batch parse exception: {ex.Message} (bytes={data.Length} from={from})");
            }
            return;
        }

        // ⚠️ 2026-08-26：大包分片重组（Steam P2P 单包硬性限制）——Fragment 子包先缓冲，收齐后重组为原始
        // 数据再递归 OnPacket（对模块透明）。Batch 容器内拆出的 Fragment 子包也会走到这里。
        if (type == (int)MsgType.Fragment)
        {
            if (ReassembleFragment(from, data, out var full) && full != null)
                OnPacket(from, full); // 递归处理重组后的原始包
            return;
        }

        if ((++_pktRecvLog % 100) == 1)
            CoopLog.Debug("Net.recvPkt", () => $"[Net] recv pkt type={type} len={data.Length} from={from}");

        // 注册管理器统一路由（优先：含动态分配 + 静态采用的全部模块，按前导字节分发）
        if (TryRoute(type, from, data)) return;
        // 自定义同步模块路由（兼容兜底：仅登记在注册表、未经注册管理器的旧路径）
        if (CoopSyncRegistry.TryRoute(type, from, data)) return;

        // 2 字节扩展类型：未注册的路由不到 → 忽略（不落入 1 字节 switch 造成错配）
        if (type > 255) return;

        switch ((MsgType)type)
        {
            case MsgType.Hello:
                if (State != SessionState.Hosting) break;
                OnHello(from, r);
                break;

            case MsgType.Welcome:
                if (State != SessionState.Joined) break;
                OnWelcome(r);
                break;

            case MsgType.Roster:
                if (State != SessionState.Joined) break;
                OnRoster(r);
                break;

            case MsgType.Ping:
                OnPing(from, r);
                break;

            case MsgType.Pong:
                OnPong(from, r);
                break;

            case MsgType.Chat:
                OnChat(from, r, data);
                break;

            case MsgType.Kick:
                OnKicked(r);
                break;

            case MsgType.GunFire:
                TurretSync.OnGunFire(data);
                break;

            case MsgType.PlayerPos:
                PlayerSync.OnPacket(from, data);
                break;

            case MsgType.PlayerState:
                PlayerSync.OnStatePacket(from, data);
                break;

            case MsgType.RecordState:
                RecordPlayerSync.OnState(data);
                break;

            case MsgType.RecordCmd:
                RecordPlayerSync.OnCmd(data);
                break;

            case MsgType.ReloadState:
                ReloadSync.OnState(data);
                break;

            case MsgType.ReloadCmd:
                ReloadSync.OnCmd(data);
                break;

            case MsgType.ReloadAdvance:
                ReloadSync.OnAdvanceEvent(data);
                break;

            case MsgType.PowderEvent:
                ReloadSync.OnPowderEvent(data);
                break;

            case MsgType.FireRequest:
                ReloadSync.OnFireRequest(data);
                break;

            case MsgType.MapMarkerAdd:
                MapSync.OnAdd(from, data);
                break;

            case MsgType.MapMarkerRemove:
                MapSync.OnRemove(from, data);
                break;

            case MsgType.MapMarkerClearAll:
                MapSync.OnClearAll(from, data);
                break;

            case MsgType.MapMarkerUpdate:
                MapSync.OnUpdate(from, data);
                break;

            case MsgType.ControlState:
                ControlSync.OnState(data);
                break;

            case MsgType.ControlCmd:
                ControlSync.OnCmd(from, data);
                break;

            case MsgType.ControlFull:
                ControlSync.OnFullState(data);
                break;
        }
    }

    /// <summary>取语言键文案（界面/错误提示全上语言文件；见 docs/LOCALIZATION.md）。</summary>
    private static string Loc(string key, params object[] args) => LocFile.Get(key, args);

    /// <summary>把拒绝/踢出原因编码为语言键（`@Key` 或 `@Key|arg1|arg2`）——接收端按**本端语言**显示。
    /// 旧端/非键文本（不以 @ 开头）原样传递，兼容。</summary>
    private static string KeyedReason(string key, params object[] args)
        => "@" + key + (args == null || args.Length == 0 ? "" : "|" + string.Join("|", args));

    /// <summary>把收到的原因文本本地化：`@Key|a|b` → 本端语言的文案；普通文本原样返回。</summary>
    private static string LocalizeReason(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw[0] != '@') return raw ?? "";
        try
        {
            var parts = raw.Substring(1).Split('|');
            if (parts.Length == 0 || string.IsNullOrEmpty(parts[0])) return raw;
            if (parts.Length == 1) return Loc(parts[0]);
            return Loc(parts[0], parts.Skip(1).Cast<object>().ToArray());
        }
        catch { return raw; }
    }

    /// <summary>拒绝加入：发一条带原因的可信 Kick（对端显示原因后离开大厅）。</summary>
    private void RejectJoin(ulong from, string reason)
    {
        CoopRuntime.LogSource?.LogWarning($"[Net] reject join {from}: {reason}");
        try
        {
            var kickW = NetProtocol.Begin(MsgType.Kick);
            kickW.Put(reason ?? "");
            Transport.Send(from, NetProtocol.Snapshot(kickW), true);
        }
        catch { }
    }

    private void OnHello(ulong from, NetDataReader r)
    {
        // 封禁检查：本次会话被踢/被封禁的 SteamId 拒绝加入
        if (_banned.Contains(from))
        {
            CoopRuntime.LogSource?.LogInfo($"[Net] rejected banned member join: {from}");
            RejectJoin(from, KeyedReason("RejectBanned"));
            return;
        }

        // 同步方案校验：两端必须一致（协议不同无法同步）
        int remoteScheme = r.GetByte();
        int localScheme = OpenNestCoop.Net.AutoJoin.WantNewSync ? 1 : 0;
        if (remoteScheme != localScheme)
        {
            RejectJoin(from, KeyedReason("RejectSchemeMismatch"));
            return;
        }

        // 前导字节宽度沟通（注册通道）：客户端声明的宽度（1/2）必须与本端一致
        int remoteWidth = -1;
        try { remoteWidth = r.GetByte(); } catch { }
        if (remoteWidth != NetProtocol.HeaderWidth)
        {
            RejectJoin(from, $"Header width mismatch: room={NetProtocol.HeaderWidth} you={remoteWidth}");
            return;
        }

        // 握手协议版本：旧客户端（Hello 无此字段）→ 拒绝，提示更新
        int handshakeVer = -1;
        try { handshakeVer = r.GetByte(); } catch { }
        if (handshakeVer != NetConfig.HandshakeVersion)
        {
            RejectJoin(from, KeyedReason("RejectOutdated"));
            return;
        }

        // 模组版本号核对：不符拒绝
        string remoteVer = "";
        try { remoteVer = r.GetString(); } catch { }
        if (remoteVer != NetConfig.Version)
        {
            RejectJoin(from, $"Mod version mismatch: room={NetConfig.Version} you={remoteVer}");
            return;
        }

        // 房间密码校验（主机权威）：输入密码 hash 必须等于房间密码 hash
        string pwd = "";
        try { pwd = r.GetString(); } catch { }
        string roomHash = RoomPasswordHash();
        if (!string.IsNullOrEmpty(roomHash) && NetConfig.HashPassword(pwd) != roomHash)
        {
            RejectJoin(from, KeyedReason("RejectWrongPassword"));
            return;
        }

        // 局域网：客户端在密码后声明身份（SteamID 优先，否则 FakeID）→ 把传输 peerId 重绑到该身份。
        // ⚠️ 重绑后 `from` 一律改用声明身份（名单键 / 踢人封禁 / 回包目标 / 中途加入快照）；
        //    同一身份重复连接 → 拒绝（防冒用/防止同一身份双开同时进来）。
        //    必须放在读名之前（封禁检查要用重绑后的身份）。
        if (LanMode)
        {
            ulong declared = 0;
            try { declared = r.GetULong(); } catch { }
            if (declared != 0)
            {
                // ⚠️ 身份冲突：①与主机自己的身份相同（**同一台机器 + 同一 Steam 账号双开**必现）
                //    ②名单里已被别的成员占用 → 派生一个局域网内唯一的 id（0xFACE 前缀，log/UI 显示 Fake#xxxx），
                //    并通过 Welcome 回告给客机（客机采纳后 MarkLocal/名单都正确）。
                ulong effective = declared;
                bool conflict = effective == Local.SteamId;
                if (!conflict)
                    foreach (var s in Roster) if (s.SteamId == effective) { conflict = true; break; }
                if (conflict)
                {
                    effective = DeriveUniqueLanId(declared);
                    CoopRuntime.LogSource?.LogInfo($"[Net] LAN identity {declared} conflict → derived {effective} ({Core.Identity.Tag(effective)})");
                }
                if (effective != from)
                {
                    var lt = Transport as LanTransport;
                    if (lt == null || !lt.RebindPeer(from, effective))
                    {
                        RejectJoin(from, KeyedReason("RejectDuplicateIdentity"));
                        return;
                    }
                }
                from = effective;
            }
            if (_banned.Contains(from)) { RejectJoin(from, KeyedReason("RejectBanned")); return; }
        }
        var name = r.GetString();

        // 注册通道（前导字节 1）：读取并校验客户端注册表——客户端带主机不认识的通道 → 拒绝（build 不兼容）
        try { if (!VerifyHostChannelTable(r)) { RejectJoin(from, KeyedReason("RejectRegistrationMismatch")); return; } } catch { }

        PlayerSession session = null;
        foreach (var s in Roster) if (s.SteamId == from) { session = s; break; }
        if (session == null)
        {
            int maxPlayers = NonSteam ? PendingMaxPlayers : Lobby.MaxPlayers;
            if (Roster.Count >= maxPlayers) return; // 已满
            session = new PlayerSession { SteamId = from, Name = name, PlayerId = NextFreeId() };
            Roster.Add(session);
        }
        else
        {
            session.Name = name;
        }

        // 发 Welcome（分配序号 + 全量名单 + 同步方案 + 前导字节宽度 + 握手版本 + 主机版本 + 权威注册表）
        var w = NetProtocol.Begin(MsgType.Welcome);
        w.Put((byte)(OpenNestCoop.Net.AutoJoin.WantNewSync ? 1 : 0)); // syncScheme
        w.Put((byte)NetProtocol.HeaderWidth); // 前导字节宽度沟通（主机权威，与本端一致）
        w.Put(NetConfig.HandshakeVersion);
        w.Put(NetConfig.Version);
        w.Put(session.PlayerId);
        NetProtocol.WriteRoster(w, Roster);
        // 局域网：回告本端在主机侧的身份（= 客户端声明值；极端回退时纠正）
        // ⚠️ 必须放在注册表之前（WriteChannelTable 的结构必须在包尾）
        if (LanMode) w.Put(from);
        // 注册通道（前导字节 2）：主机权威注册表随 Welcome 下发，客户端采纳
        WriteChannelTable(w);
        Transport.Send(from, NetProtocol.Snapshot(w), true);

        // 广播新名单
        BroadcastRoster();
        RosterChanged?.Invoke();

        // 中途加入：给新成员发全量状态快照（任务 + 装填等），让 TA 对齐当前游戏状态
        SendLateJoinSnapshot(from);
    }

    /// <summary>中途加入快照：主机把当前关键状态单播给新加入/重连的成员。</summary>
    private void SendLateJoinSnapshot(ulong steamId)
    {
        try
        {
            // 各 ISyncedModule 的 OnLateJoin（任务 + StateSnapshotSync 统一容器等需要初始对齐的模块）。
            // 注意：ReloadSync（装填）不在首次发——新成员此时可能还在主菜单/加载任务场景，
            // 过早发会被 ResolveGuns() 空而丢弃；等新成员场景加载完成后 RequestSnapshot（MsgType=31）
            // 触发补发时再单独发（见 StateSnapshotSync.OnPacket）。
            foreach (var m in CoopSyncRegistry.Modules)
            {
                try { m.OnLateJoin(steamId); }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"OnLateJoin {m.GetType().Name}: {ex.Message}"); }
            }
            // 其余周期全量模块（ValueSync 2s 心跳 / CatSync / EntitySync / RecordItemSync）自动对齐，无需在此处理
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"SendLateJoinSnapshot: {ex.Message}"); }
    }

    /// <summary>当前房间密码 hash（Steam 模式读 LobbyData；本地模式用 PendingPassword）。空=无密码。</summary>
    private string RoomPasswordHash()
    {
        if (NonSteam) return NetConfig.HashPassword(PendingPassword);
        try { return Lobby.PasswordHash; } catch { return ""; }
    }

    private void OnWelcome(NetDataReader r)
    {
        // 同步方案校验：两端不一致 → 离开（协议不同无法同步）
        int remoteScheme = r.GetByte();
        int localScheme = OpenNestCoop.Net.AutoJoin.WantNewSync ? 1 : 0;
        if (remoteScheme != localScheme)
        {
            CoopRuntime.LogSource?.LogWarning($"[Net] sync scheme mismatch: remote={remoteScheme} local={localScheme}, leave");
            LastError = Loc("RejectSchemeMismatch");
            LeaveSession();
            return;
        }
        // 前导字节宽度沟通（注册通道）：主机声明的宽度必须与本端一致
        int remoteWidth = -1;
        try { remoteWidth = r.GetByte(); } catch { }
        if (remoteWidth != NetProtocol.HeaderWidth)
        {
            LastError = Loc("ErrHeaderWidthMismatch", remoteWidth, NetProtocol.HeaderWidth);
            CoopRuntime.LogSource?.LogWarning($"[Net] header width mismatch host={remoteWidth} local={NetProtocol.HeaderWidth}, leave");
            LeaveSession();
            return;
        }
        // 握手协议版本 + 主机模组版本核对：不符离开
        int handshakeVer = -1;
        try { handshakeVer = r.GetByte(); } catch { }
        if (handshakeVer != NetConfig.HandshakeVersion)
        {
            LastError = Loc("RejectOutdated");
            CoopRuntime.LogSource?.LogWarning("[Net] handshake version mismatch, leave");
            LeaveSession();
            return;
        }
        string hostVer = "";
        try { hostVer = r.GetString(); } catch { }
        if (hostVer != NetConfig.Version)
        {
            LastError = Loc("ErrModVersionMismatch", hostVer, NetConfig.Version);
            CoopRuntime.LogSource?.LogWarning($"[Net] host version mismatch host={hostVer} local={NetConfig.Version}, leave");
            LeaveSession();
            return;
        }
        var pid = r.GetByte();
        var roster = NetProtocol.ReadRoster(r);
        // 局域网：读回主机认定的本端身份（放在注册表之前；必须在 MarkLocal 之前写入 Local.SteamId）
        if (LanMode)
        {
            try
            {
                if (r.AvailableBytes >= 8)
                {
                    var mine = r.GetULong();
                    if (mine != 0 && Local != null) Local.SteamId = mine;
                }
            }
            catch { }
        }
        Local.PlayerId = pid;
        Roster.Clear();
        Roster.AddRange(roster);
        MarkLocal();
        // 注册通道（前导字节 2）：采纳主机权威注册表（动态通道前导字节以主机为准）
        try { ApplyHostChannelTable(r); } catch { }
        State = SessionState.Joined;
        RosterChanged?.Invoke();
        StateChanged?.Invoke();
        CoopRuntime.LogSource?.LogInfo($"received host welcome, assigned as #{pid}");
    }

    private void OnRoster(NetDataReader r)
    {
        Roster.Clear();
        Roster.AddRange(NetProtocol.ReadRoster(r));
        MarkLocal();
        RosterChanged?.Invoke();
    }

    private void OnPing(ulong from, NetDataReader r)
    {
        var ticks = r.GetLong();
        var w = NetProtocol.Begin(MsgType.Pong);
        w.Put(ticks);
        Transport.Send(from, NetProtocol.Snapshot(w), false);
    }

    private void OnPong(ulong from, NetDataReader r)
    {
        var ticks = r.GetLong();
        PlayerSession session = null;
        foreach (var s in Roster) if (s.SteamId == from) { session = s; break; }
        if (session == null) session = Local;
        if (session != null)
        {
            session.PingMs = (float)(Environment.TickCount64 - ticks);
            // 喂 RTT 给网络负载调控器（拥塞检测用）
            NetworkGovernor.Instance.RecordRtt(session.PingMs);
            NetworkGovernor.Instance.RecordPongRecv(); // 丢包率统计（Ping/Pong 探测）
        }
    }

    private void OnChat(ulong from, NetDataReader r, byte[] raw)
    {
        var name = r.GetString();
        var text = r.GetString();
        AddChat(name, text);
        // 主机转发给其他人
        if (State == SessionState.Hosting)
        {
            foreach (var p in Roster)
                if (!p.IsLocal && p.SteamId != from) Transport.Send(p.SteamId, raw, true);
        }
    }

    private void MarkLocal()
    {
        foreach (var p in Roster)
        {
            p.IsLocal = p.SteamId == Local.SteamId;
            if (p.IsLocal) Local = p;
        }
    }

    private void AddChat(string name, string text)
    {
        ChatLog.Add($"[{name}] {text}");
        if (ChatLog.Count > NetConfig.MaxChatLines)
            ChatLog.RemoveRange(0, ChatLog.Count - NetConfig.MaxChatLines);
        ChatChanged?.Invoke();
    }

    private void BroadcastRoster()
    {
        var w = NetProtocol.Begin(MsgType.Roster);
        NetProtocol.WriteRoster(w, Roster);
        var data = NetProtocol.Snapshot(w);
        foreach (var p in Roster)
            if (!p.IsLocal) Transport.Send(p.SteamId, data, true);
    }
}

/// <summary>函数式通道注册回调容器（<see cref="NetManager.RegisterChannel(string, ChannelCallbacks, NetModulePriority)"/> 用）：
/// 用函数/回调注册同步通道，无需实现 ISyncedModule。任一回调可缺省（null 则该项不驱动）。</summary>
public sealed class ChannelCallbacks
{
    /// <summary>收包处理（必填）：(from, data)。</summary>
    public Action<ulong, byte[]> OnPacket;
    /// <summary>每帧/周期驱动（可选）：缩放后的 dt。</summary>
    public Action<float> Tick;
    /// <summary>会话开始（可选）：进入房间（主机或已加入）。</summary>
    public Action OnSessionStarted;
    /// <summary>会话结束（可选）：离开/被踢。</summary>
    public Action OnSessionEnded;
    /// <summary>中途加入（可选）：主机把本通道当前状态单播给新成员 steamId。</summary>
    public Action<ulong> OnLateJoin;
    /// <summary>重置（可选）。</summary>
    public Action Reset;
}
