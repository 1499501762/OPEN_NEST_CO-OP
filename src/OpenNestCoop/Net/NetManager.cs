using System;
using System.Collections.Generic;
using System.Linq;
using OpenNestCoop.Core;
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
    public string PendingLobbyName = "Nest 联机房间";
    public int PendingMaxPlayers = NetConfig.DefaultMaxPlayers;
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

    public ulong HostSteamId => LocalMode ? LocalHostPeerId : Lobby.HostSteamId;

    // 合包缓冲（不可靠周期状态：帧末合并成一个 UDP 包，省 Steam 每包约 30B 头）
    private readonly List<byte[]> _broadcastQueue = new();
    private readonly List<byte[]> _hostQueue = new();
    private const int BatchMaxItems = 256;
    public bool IsHost => LocalMode ? (State == SessionState.Hosting) : Lobby.IsHost;
    /// <summary>Steam 是否已初始化（游戏启动后由 Heathen 完成）。本地模式恒 true。</summary>
    public bool SteamReady;

    private ulong _joinedHostId;      // 加入方记住主机
    private float _pingTimer;
    private bool _steamContextAttempted;
    private bool _creatingLobby;      // 创建大厅异步窗口期防重复点击/重复创建
    private int _flushLog;
    private int _batchRecvLog;
    private int _pktRecvLog;

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

        Local = new PlayerSession { SteamId = 0, Name = "Steam 初始化中…", IsLocal = true, PlayerId = 255 };
    }

    public void Shutdown()
    {
        try { if (State != SessionState.Idle) Lobby.LeaveLobby(); } catch { }
        State = SessionState.Idle;
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
            CoopRuntime.LogSource?.LogInfo("Steamworks.NET 托管上下文已手动初始化");
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"Steamworks 上下文初始化失败: {ex.Message}");
        }
    }

    public void Update(float dt)
    {
        // 本地回环模式（双开测试）：不经 Steam，直接驱动本地传输
        if (LocalMode)
        {
            UpdateLocal(dt);
            return;
        }

        // 确保 Steamworks.NET 托管上下文已初始化（游戏可能只原生初始化了 Steam）
        EnsureSteamContext();

        // 探测 Steam 就绪
        try
        {
            SteamReady = SteamAPI.IsSteamRunning() && SteamUser.GetSteamID().IsValid();
        }
        catch { SteamReady = false; }

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
            try { SteamAPI.RunCallbacks(); } catch { }

            // 自动联机（--autohost / --autojoin）：Steam 就绪后触发建房/加入
            AutoJoin.TryStart(this);

            // 大厅列表填充：Steam 回调内不做事，改在此安全上下文处理（防回调内同步 API 死锁）
            Lobby.PollPendingLobbyList();

            // 清空 P2P 入包队列（仅在 Steam 就绪时，避免未初始化异常刷屏）
            while (Transport.Poll(out ulong from, out byte[] data))
            {
                try { OnPacket(from, data); }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"处理包异常 (来自 {from}): {ex}"); }
            }
        }

        UpdateCommon(dt);
    }

    /// <summary>本地回环模式驱动（双开测试，不经 Steam）。</summary>
    private void UpdateLocal(float dt)
    {
        // 本地模式无 Steam：直接就绪 + 自动触发建房/加入（--local host / --local join）
        SteamReady = true;
        AutoJoin.TryStart(this);
        if (Local == null || Local.SteamId == 0)
        {
            Local = new PlayerSession
            {
                SteamId = LocalMode ? (LocalPeerIdOf()) : 0,
                Name = LocalMode ? (IsHost ? "Host(本地)" : "Client(本地)") : "Steam 初始化中…",
                IsLocal = true,
                PlayerId = 255,
            };
        }
        while (Transport.Poll(out ulong from, out byte[] data))
        {
            try { OnPacket(from, data); }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"处理本地包异常 (来自 {from}): {ex}"); }
        }
        UpdateCommon(dt);
    }

    /// <summary>本地模式本端 peerId（host=1，client=2）。</summary>
    private ulong LocalPeerIdOf() => (Transport as LocalTransport)?.LocalPeerId ?? 0;

    /// <summary>心跳 + 各同步模块 Tick + 合包 + 统计（Steam 与本地模式共用）。</summary>
    private void UpdateCommon(float dt)
    {
        // 心跳/延迟
        _pingTimer += dt;
        if (_pingTimer >= NetConfig.PingInterval)
        {
            _pingTimer = 0f;
            if (State == SessionState.Hosting)
            {
                foreach (var p in Roster.Where(x => !x.IsLocal))
                {
                    var w = NetProtocol.Begin(MsgType.Ping);
                    w.Put(Environment.TickCount64);
                    Transport.Send(p.SteamId, NetProtocol.Snapshot(w), false);
                    p.LastPingSentTicks = Environment.TickCount64;
                }
            }
            else if (State == SessionState.Joined && HostSteamId != 0)
            {
                var w = NetProtocol.Begin(MsgType.Ping);
                w.Put(Environment.TickCount64);
                Transport.Send(HostSteamId, NetProtocol.Snapshot(w), false);
                Local.LastPingSentTicks = Environment.TickCount64;
            }
        }

        // 玩家化身同步（M2.6）
        PlayerSync.Tick(dt);

        // 唱片机同步（M2.8）
        RecordPlayerSync.Tick(dt);

        // 装填/开火同步（M3a）
        ReloadSync.Tick(dt);

        // 地图标记同步（M3b）
        MapSync.Tick(dt);

        // 交互控件同步（M3c：曲柄/旋钮/滑块）
        ControlSync.Tick(dt);

        // 自定义同步模块（开放扩展点）
        CoopSyncRegistry.TickAll(dt);

        // 帧末：合包发出不可靠状态包
        FlushBatch();

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
            CoopRuntime.LogSource?.LogInfo($"[Net] stats 10s  state={State}{sb}");
        }
    }

    /// <summary>把不可靠状态子包加入合包缓冲（toAll=true 广播给所有非本地，false 发主机）。</summary>
    public void EnqueueBatch(byte[] data, bool toAll)
    {
        if (data == null || data.Length == 0) return;
        try
        {
            int t = data[0] & 0xFF;
            if (t < 256) _sendStats[t]++;
            if (toAll) { if (_broadcastQueue.Count < BatchMaxItems) _broadcastQueue.Add(data); }
            else { if (_hostQueue.Count < BatchMaxItems) _hostQueue.Add(data); }
        }
        catch { }
    }

    /// <summary>帧末把缓冲的子包合并成 Batch 包发出。
    /// 用 reliable 通道：Steam P2P unreliable 单包上限约 1200B（超限整包被拒收），且高频率下
    /// 丢包率高（任务内状态同步全断的元凶之一）。reliable 上限 1MB + 自动重传，保证送达；
    /// 仍按字节阈值拆包，避免单包过大（reliable 大包也会显著增加延迟/拥塞）。</summary>
    private void FlushBatch()
    {
        const int MaxPacketBytes = 1000;
        try
        {
            if (_broadcastQueue.Count > 0)
            {
                int subs = _broadcastQueue.Count, bytes = 0;
                foreach (var d in _broadcastQueue) bytes += d.Length + 2;
                if ((++_flushLog % 30) == 1)
                    CoopRuntime.LogSource?.LogInfo($"[Net] flush toAll subs={subs} bytes≈{bytes} peers={Roster.Count - 1}");
                foreach (var group in SplitBatches(_broadcastQueue, MaxPacketBytes))
                {
                    var packet = BuildBatch(group);
                    foreach (var p in Roster)
                        if (!p.IsLocal) Transport.Send(p.SteamId, packet, true);
                }
                _broadcastQueue.Clear();
            }
            if (_hostQueue.Count > 0)
            {
                int subs = _hostQueue.Count, bytes = 0;
                foreach (var d in _hostQueue) bytes += d.Length + 2;
                if ((++_flushLog % 30) == 1)
                    CoopRuntime.LogSource?.LogInfo($"[Net] flush toHost subs={subs} bytes≈{bytes}");
                foreach (var group in SplitBatches(_hostQueue, MaxPacketBytes))
                {
                    var packet = BuildBatch(group);
                    if (HostSteamId != 0) Transport.Send(HostSteamId, packet, true);
                }
                _hostQueue.Clear();
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

    private static byte[] BuildBatch(List<byte[]> items)
    {
        var w = NetProtocol.Begin(MsgType.Batch);
        int n = Math.Min(items.Count, 255);
        w.Put((byte)n);
        for (int i = 0; i < n; i++)
        {
            var d = items[i];
            // 子包长度用 ushort（byte 上限 255 会截断 EntitySync 等大包）
            int len = d.Length;
            w.Put((ushort)len);
            w.Put(d, 0, len);
        }
        return NetProtocol.Snapshot(w);
    }

    // ---- 大厅操作 ----

    /// <summary>创建房间：Steam 模式建房（异步）；本地模式监听 TCP 端口。</summary>
    public void CreateLobby()
    {
        if (State != SessionState.Idle || _creatingLobby) return;
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
        if (!SteamReady) { LastError = "Steam 尚未就绪，请稍候再试"; return; }
        LastError = "";
        _creatingLobby = true; // 创建是异步的，回调前 State 仍为 Idle，防重复创建多个大厅
        Lobby.CreateLobby(PendingLobbyName, PendingMaxPlayers);
    }

    public void RefreshBrowser()
    {
        if (State != SessionState.Idle) return;
        if (LocalMode) return;
        if (!SteamReady) { LastError = "Steam 尚未就绪，请稍候再试"; return; }
        Refreshing = true;
        Lobby.RefreshBrowser();
    }

    public bool JoinLobby(LobbyInfo info)
    {
        if (State != SessionState.Idle) return false;
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
        if (!SteamReady) { LastError = "Steam 尚未就绪，请稍候再试"; return false; }
        LastError = "";
        Lobby.JoinLobby(info.Id);
        return true;
    }

    public void LeaveSession()
    {
        if (LocalMode)
        {
            (Transport as LocalTransport)?.Dispose();
            OnLobbyLeft();
            return;
        }
        Lobby.LeaveLobby();
        // OnLobbyLeft 会清理状态
    }

    // ---- 踢人 / 封禁 / 邀请 ----

    /// <summary>主机踢出成员（默认本次会话封禁，防止其重新加入）。</summary>
    public void KickPlayer(ulong steamId, bool ban = true)
    {
        if (!IsHost) return;
        var p = Roster.FirstOrDefault(x => x.SteamId == steamId);
        if (p == null || p.IsLocal) return;

        if (ban) _banned.Add(steamId);

        // 通知被踢者（Kick 消息，reliable）→ 对端 LeaveSession
        try
        {
            var w = NetProtocol.Begin(MsgType.Kick);
            var data = NetProtocol.Snapshot(w);
            Transport.Send(steamId, data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Net] Kick 发送失败: {ex.Message}"); }

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
        CoopRuntime.LogSource?.LogInfo($"[Net] 已踢出 {p.Name} (ban={ban})");
    }

    /// <summary>主机解除本次会话封禁（允许该 SteamId 重新加入）。</summary>
    public void UnbanPlayer(ulong steamId)
    {
        if (!IsHost) return;
        _banned.Remove(steamId);
        CoopRuntime.LogSource?.LogInfo($"[Net] 已解除封禁 {steamId}");
    }

    /// <summary>当前是否被封禁（主机查询用，或本地回显）。</summary>
    public bool IsBanned(ulong steamId) => _banned.Contains(steamId);

    /// <summary>打开 Steam 好友邀请对话框（Steam overlay）。非 Steam 模式（本地回环）不支持。</summary>
    public void InviteFriends()
    {
        if (LocalMode)
        {
            LastError = "本地回环模式不支持 Steam 邀请";
            CoopRuntime.LogSource?.LogInfo("[Net] 本地模式不支持 Steam 邀请");
            return;
        }
        try
        {
#if !MELONLOADER
            if (!Lobby.LobbyID.IsValid()) return;
            SteamFriends.ActivateGameOverlayInviteDialog(Lobby.LobbyID);
            CoopRuntime.LogSource?.LogInfo("[Net] 已打开 Steam 邀请对话框");
#else
            CoopRuntime.LogSource?.LogInfo("[Net] ML 模式邀请暂不支持（Steam overlay API 差异）");
#endif
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Net] InviteFriends: {ex.Message}"); }
    }

    /// <summary>被踢端处理：收到 Kick 消息 → 标记 + 离开会话（UI 显示提示）。</summary>
    private void OnKicked()
    {
        WasKicked = true;
        CoopRuntime.LogSource?.LogInfo("[Net] 已被主机踢出");
        if (LocalMode)
        {
            (Transport as LocalTransport)?.Dispose();
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
                Name = "Host(本地)",
                IsLocal = true,
                PlayerId = 0,
                IsHost = true,
                Role = CrewRole.Commander,
            };
            CoopRuntime.LogSource?.LogInfo($"[Net] 本地模式 host 就绪（端口 {LocalPort}）");
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
                Name = "Client(本地)",
                IsLocal = true,
                PlayerId = 255,
            };
            CoopRuntime.LogSource?.LogInfo($"[Net] 本地模式 client 连接 host（端口 {LocalPort}）");
            return true;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Net] LocalStartClient: {ex.Message}"); return false; }
    }

    /// <summary>本地模式：host 检测到 client 连上 → 分配 PlayerId + 发 Welcome（复用 OnHello 流程）。</summary>
    private void OnLocalClientConnected(LocalTransport lt)
    {
        try
        {
            CoopRuntime.LogSource?.LogInfo("[Net] 本地 host 收到 client 连接，等待 Hello…");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Net] OnLocalClientConnected: {ex.Message}"); }
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
            foreach (var p in Roster.Where(x => !x.IsLocal))
                Transport.Send(p.SteamId, data, true);
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
        var p = Roster.FirstOrDefault(x => x.SteamId == steamId);
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
        bool isHost = LocalMode ? (Local != null && Local.IsHost) : Lobby.IsHost;
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
            if (!LocalMode) AutoJoin.OnHostEntered(this);
        }
        else
        {
            _joinedHostId = HostSteamId;
            State = SessionState.Joined;
            // 向主机自我介绍
            var w = NetProtocol.Begin(MsgType.Hello);
            w.Put(Local.Name);
            Transport.Send(_joinedHostId, NetProtocol.Snapshot(w), true);
        }
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
                    LastError = "主机已离开房间";
                    CoopRuntime.LogSource?.LogInfo("主机已离开，返回大厅");
                }
                Lobby.LeaveLobby();
            }
        }
    }

    private void SyncHostRoster()
    {
        var members = Lobby.GetMembers();
        bool changed = false;

        // 加入新成员
        foreach (var sid in members)
        {
            if (sid == Local.SteamId) continue;
            if (Roster.Any(p => p.SteamId == sid)) continue;
            Roster.Add(new PlayerSession
            {
                SteamId = sid,
                Name = SteamFriends.GetFriendPersonaName((CSteamID)sid),
                PlayerId = NextFreeId(),
                IsHost = false,
            });
            changed = true;
            CoopRuntime.LogSource?.LogInfo($"新成员加入: {SteamFriends.GetFriendPersonaName((CSteamID)sid)}");
        }

        // 移除离开者
        int removed = Roster.RemoveAll(p => !p.IsLocal && !members.Contains(p.SteamId));
        if (removed > 0)
        {
            changed = true;
            CoopRuntime.LogSource?.LogInfo($"有 {removed} 名成员离开");
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
            if (!Roster.Any(p => p.PlayerId == id))
                return id;
        return 0;
    }

    // ---- 消息处理 ----

    private void OnPacket(ulong from, byte[] data)
    {
        var r = new NetDataReader(data);
        var type = NetProtocol.TypeOf(r);
        if ((int)type < 256) _recvStats[(int)type]++;

        // 合包容器：拆包后递归处理各子包
        if (type == MsgType.Batch)
        {
            try
            {
                int n = r.GetByte();
                if ((++_batchRecvLog % 20) == 1)
                    CoopRuntime.LogSource?.LogInfo($"[Net] recv batch n={n} bytes={data.Length} from={from}");
                for (int i = 0; i < n; i++)
                {
                    if (r.AvailableBytes < 2)
                    {
                        CoopRuntime.LogSource?.LogWarning($"[Net] batch 提前结束 {i}/{n} (缺长度前缀 avail={r.AvailableBytes})");
                        break;
                    }
                    int len = r.GetUShort();
                    if (len <= 0 || r.AvailableBytes < len)
                    {
                        CoopRuntime.LogSource?.LogWarning($"[Net] batch 子包异常 idx={i} len={len} avail={r.AvailableBytes} bytes={data.Length}");
                        break;
                    }
                    // 注意：不要用 GetRemainingBytes()+SkipBytes —— LiteNetLib 的
                    // GetRemainingBytes() 会把 reader 位置移到末尾，再 SkipBytes 就过头了
                    // （avail 变负），导致 Batch 里第一个子包之后的子包全部丢失！
                    var sub = new byte[len];
                    try { System.Array.Copy(r.RawData, r.Position, sub, 0, len); }
                    catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Net] batch 子包复制失败: {ex.Message}"); break; }
                    r.SkipBytes(len);
                    OnPacket(from, sub);
                }
            }
            catch (Exception ex)
            {
                CoopRuntime.LogSource?.LogWarning($"[Net] batch 解析异常: {ex.Message} (bytes={data.Length} from={from})");
            }
            return;
        }

        if ((++_pktRecvLog % 100) == 1)
            CoopRuntime.LogSource?.LogInfo($"[Net] recv pkt type={(byte)type} len={data.Length} from={from}");

        // 自定义同步模块路由（优先，命中则交给模块处理）
        if (CoopSyncRegistry.TryRoute((byte)type, from, data)) return;

        switch (type)
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
                OnKicked();
                break;

            case MsgType.GunFire:
                TurretSync.OnGunFire(data);
                break;

            case MsgType.PlayerPos:
                PlayerSync.OnPacket(from, data);
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
        }
    }

    private void OnHello(ulong from, NetDataReader r)
    {
        // 封禁检查：本次会话被踢/被封禁的 SteamId 拒绝加入
        if (_banned.Contains(from))
        {
            CoopRuntime.LogSource?.LogInfo($"[Net] 拒绝封禁成员加入: {from}");
            try
            {
                // 也发一条 Kick 让对端明确知道被拒（避免对端一直重试 Hello）
                var kickW = NetProtocol.Begin(MsgType.Kick);
                Transport.Send(from, NetProtocol.Snapshot(kickW), true);
            }
            catch { }
            return;
        }
        var name = r.GetString();
        var session = Roster.FirstOrDefault(p => p.SteamId == from);
        if (session == null)
        {
            int maxPlayers = LocalMode ? PendingMaxPlayers : Lobby.MaxPlayers;
            if (Roster.Count >= maxPlayers) return; // 已满
            session = new PlayerSession { SteamId = from, Name = name, PlayerId = NextFreeId() };
            Roster.Add(session);
        }
        else
        {
            session.Name = name;
        }

        // 发 Welcome（分配序号 + 全量名单）
        var w = NetProtocol.Begin(MsgType.Welcome);
        w.Put(session.PlayerId);
        NetProtocol.WriteRoster(w, Roster);
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

    private void OnWelcome(NetDataReader r)
    {
        var pid = r.GetByte();
        var roster = NetProtocol.ReadRoster(r);
        Local.PlayerId = pid;
        Roster.Clear();
        Roster.AddRange(roster);
        MarkLocal();
        State = SessionState.Joined;
        RosterChanged?.Invoke();
        StateChanged?.Invoke();
        CoopRuntime.LogSource?.LogInfo($"收到主机欢迎，我被分配为 #{pid}");
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
        var session = Roster.FirstOrDefault(p => p.SteamId == from);
        if (session == null) session = Local;
        if (session != null)
            session.PingMs = (float)(Environment.TickCount64 - ticks);
    }

    private void OnChat(ulong from, NetDataReader r, byte[] raw)
    {
        var name = r.GetString();
        var text = r.GetString();
        AddChat(name, text);
        // 主机转发给其他人
        if (State == SessionState.Hosting)
        {
            foreach (var p in Roster.Where(x => !x.IsLocal && x.SteamId != from))
                Transport.Send(p.SteamId, raw, true);
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
        foreach (var p in Roster.Where(x => !x.IsLocal))
            Transport.Send(p.SteamId, data, true);
    }
}
