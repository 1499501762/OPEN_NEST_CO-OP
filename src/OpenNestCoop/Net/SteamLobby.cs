using System;
using System.Collections.Generic;
using OpenNestCoop.Core;
#if !MELONLOADER
using Steamworks;
#else
using Steamworks = Il2CppSteamworks;
#endif

namespace OpenNestCoop.Net;

public class LobbyInfo
{
    public ulong Id;
    public string Name = "";
    public string OwnerName = "";
    public int Players;
    public int MaxPlayers;
    public bool IsFull;
}

/// <summary>
/// Steam 大厅封装：创建/加入/浏览/离开 + 成员变化回调。
/// 大厅仅用于"发现与会话"，游戏数据走 SteamTransport P2P。
/// </summary>
public class SteamLobby
{
    public bool IsHost;
    public CSteamID LobbyID;
    public ulong HostSteamId => LobbyID.IsValid() ? (ulong)SteamMatchmaking.GetLobbyOwner(LobbyID) : 0;

    public List<LobbyInfo> Browser = new();
    public int MaxPlayers;

    private string _pendingName = "联机房间";

    private CallResult<LobbyCreated_t> _created;
    private CallResult<LobbyEnter_t> _entered;
    private CallResult<LobbyMatchList_t> _list;
    private Callback<LobbyChatUpdate_t> _chatUpdate;
    private Callback<GameLobbyJoinRequested_t> _joinRequested;
    private bool _registered;

    // 大厅列表延迟填充（Steam 回调内只做本地安全调用收集 ID，详情在安全上下文填充）
    private readonly List<ulong> _pendingIds = new();
    private bool _lobbyListPending;

    /// <summary>进入房间成功（创建成功或加入成功）。</summary>
    public event Action Entered;
    /// <summary>离开房间。</summary>
    public event Action Left;
    /// <summary>大厅浏览器刷新完成。</summary>
    public event Action ListRefreshed;
    /// <summary>房间成员变化（加入/离开/主机变更）。</summary>
    public event Action MembersChanged;

    /// <summary>
    /// 确保 Steam 回调已注册。必须在 Steam 初始化之后调用（插件 Load 可能早于游戏初始化 Steam）。
    /// </summary>
    public void EnsureRegistered()
    {
        if (_registered) return;
        _registered = true;
        // 持有引用防止被 GC。互操作委托不能直接绑定 C# lambda，需经 System.Action 隐式转换。
        _created = CallResult<LobbyCreated_t>.Create((Action<LobbyCreated_t, bool>)OnLobbyCreated);
        _entered = CallResult<LobbyEnter_t>.Create((Action<LobbyEnter_t, bool>)OnLobbyEntered);
        _list = CallResult<LobbyMatchList_t>.Create((Action<LobbyMatchList_t, bool>)OnLobbyList);
        _chatUpdate = Callback<LobbyChatUpdate_t>.Create((Action<LobbyChatUpdate_t>)OnLobbyChatUpdate);
        _joinRequested = Callback<GameLobbyJoinRequested_t>.Create((Action<GameLobbyJoinRequested_t>)OnGameLobbyJoinRequested);
    }

    public void CreateLobby(string name, int max)
    {
        EnsureRegistered();
        _pendingName = string.IsNullOrWhiteSpace(name) ? "联机房间" : name;
        MaxPlayers = Math.Max(2, Math.Min(max, 8));
        var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, MaxPlayers);
        _created.Set(call, (Action<LobbyCreated_t, bool>)OnLobbyCreated);
        CoopRuntime.LogSource?.LogInfo($"creating lobby (max {MaxPlayers} players)...");
    }

    public void RefreshBrowser()
    {
        EnsureRegistered();
        Browser.Clear();
        try
        {
            SteamMatchmaking.AddRequestLobbyListStringFilter(
                NetConfig.LobbyTagKey, NetConfig.LobbyTagValue, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListFilterSlotsAvailable(1); // 只列有位置的
            var call = SteamMatchmaking.RequestLobbyList();
            _list.Set(call, (Action<LobbyMatchList_t, bool>)OnLobbyList);
            CoopRuntime.LogSource?.LogInfo($"requesting lobby list (call={call.m_SteamAPICall})");
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"refresh lobby exception: {ex.Message}");
        }
    }

    public void JoinLobby(ulong lobbyId)
    {
        EnsureRegistered();
        var call = SteamMatchmaking.JoinLobby((CSteamID)lobbyId);
        _entered.Set(call, (Action<LobbyEnter_t, bool>)OnLobbyEntered);
    }

    public void JoinLobby(CSteamID lobbyId)
    {
        EnsureRegistered();
        var call = SteamMatchmaking.JoinLobby(lobbyId);
        _entered.Set(call, (Action<LobbyEnter_t, bool>)OnLobbyEntered);
    }

    public void LeaveLobby()
    {
        if (LobbyID.IsValid())
        {
            try { SteamMatchmaking.LeaveLobby(LobbyID); } catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"LeaveLobby: {ex.Message}"); }
        }
        LobbyID = default;
        IsHost = false;
        Left?.Invoke();
    }

    /// <summary>当前房间 SteamID 成员列表。</summary>
    public List<ulong> GetMembers()
    {
        var list = new List<ulong>();
        if (!LobbyID.IsValid()) return list;
        int n = SteamMatchmaking.GetNumLobbyMembers(LobbyID);
        for (int i = 0; i < n; i++)
            list.Add((ulong)SteamMatchmaking.GetLobbyMemberByIndex(LobbyID, i));
        return list;
    }

    // ---- 回调 ----

    private void OnLobbyCreated(LobbyCreated_t p, bool ioFailure)
    {
        if (ioFailure || p.m_eResult != EResult.k_EResultOK)
        {
            CoopRuntime.LogSource?.LogError($"create lobby failed: {p.m_eResult}");
            LastError = $"Failed to create lobby: {p.m_eResult}";
            Left?.Invoke();
            return;
        }
        LobbyID = (CSteamID)p.m_ulSteamIDLobby;
        IsHost = true;
        MaxPlayers = SteamMatchmaking.GetLobbyMemberLimit(LobbyID);
        SteamMatchmaking.SetLobbyData(LobbyID, NetConfig.LobbyTagKey, NetConfig.LobbyTagValue);
        SteamMatchmaking.SetLobbyData(LobbyID, NetConfig.LobbyNameKey, _pendingName);
        SteamMatchmaking.SetLobbyData(LobbyID, NetConfig.LobbyMaxKey, MaxPlayers.ToString());
        SteamMatchmaking.SetLobbyJoinable(LobbyID, true);
        CoopRuntime.LogSource?.LogInfo($"lobby created: {LobbyID}");
        Entered?.Invoke();
    }

    private void OnLobbyEntered(LobbyEnter_t p, bool ioFailure)
    {
        // k_EChatRoomEnterResponseSuccess == 1
        if (ioFailure || p.m_EChatRoomEnterResponse != 1)
        {
            CoopRuntime.LogSource?.LogWarning($"join lobby failed: response {p.m_EChatRoomEnterResponse}");
            LastError = $"Failed to join lobby: response {p.m_EChatRoomEnterResponse}";
            Left?.Invoke();
            return;
        }
        LobbyID = (CSteamID)p.m_ulSteamIDLobby;
        IsHost = false;
        MaxPlayers = SteamMatchmaking.GetLobbyMemberLimit(LobbyID);
        CoopRuntime.LogSource?.LogInfo($"joined lobby: {LobbyID}");
        Entered?.Invoke();
    }

    private void OnLobbyList(LobbyMatchList_t p, bool ioFailure)
    {
        // IL2CPP interop 下 LobbyMatchList_t.m_nLobbiesMatching 可能读到垃圾值（曾出现 17 亿），
        // 绝不能用它做循环上界（会卡死）。改用 GetLobbyByIndex（本地安全、不触发网络请求）
        // 逐项收集真实大厅 ID，直到返回无效 lobby 即停止。
        // 其余详情（GetLobbyData 等）在 NetManager.Update 的安全上下文填充。
        _pendingIds.Clear();
        for (int i = 0; i < 256; i++)
        {
            var id = SteamMatchmaking.GetLobbyByIndex(i);
            if (!id.IsValid()) break;
            _pendingIds.Add((ulong)id);
        }
            CoopRuntime.LogSource?.LogInfo($"lobby list callback: collected {_pendingIds.Count} lobbies");
        _lobbyListPending = true;
    }

    /// <summary>由 NetManager.Update 在 RunCallbacks 之外调用：填充 Browser（安全上下文）。</summary>
    public void PollPendingLobbyList()
    {
        if (!_lobbyListPending) return;
        _lobbyListPending = false;
        try
        {
            Browser.Clear();
            foreach (var id in _pendingIds)
            {
                var cid = (CSteamID)id;
                var info = new LobbyInfo
                {
                    Id = id,
                    Name = SteamMatchmaking.GetLobbyData(cid, NetConfig.LobbyNameKey),
                    Players = SteamMatchmaking.GetNumLobbyMembers(cid),
                };
                var owner = SteamMatchmaking.GetLobbyOwner(cid);
                info.OwnerName = SafePersonaName(owner);
                info.MaxPlayers = ParseMax(SteamMatchmaking.GetLobbyData(cid, NetConfig.LobbyMaxKey));
                if (info.MaxPlayers <= 0) info.MaxPlayers = SteamMatchmaking.GetLobbyMemberLimit(cid);
                info.IsFull = info.Players >= info.MaxPlayers;
                if (string.IsNullOrEmpty(info.Name))
                    info.Name = (info.OwnerName.Length > 0 ? info.OwnerName : ("#" + id)) + " 的房间";
                Browser.Add(info);
            }
            _pendingIds.Clear();
            CoopRuntime.LogSource?.LogInfo($"lobby list refresh: {Browser.Count} rooms");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"lobby list fill exception: {ex.Message}"); }
        ListRefreshed?.Invoke();
    }

    /// <summary>仅对好友取 persona 名（本地缓存）；非好友不调用，避免触发网络请求阻塞/死锁。</summary>
    private static string SafePersonaName(CSteamID sid)
    {
        try
        {
            if (SteamFriends.HasFriend(sid, EFriendFlags.k_EFriendFlagImmediate))
                return SteamFriends.GetFriendPersonaName(sid);
        }
        catch { }
        return "";
    }

    private void OnLobbyChatUpdate(LobbyChatUpdate_t p)
    {
        if (!LobbyID.IsValid() || (ulong)p.m_ulSteamIDLobby != (ulong)LobbyID) return;
        MembersChanged?.Invoke();
    }

    /// <summary>好友接受邀请后从覆盖层进入。</summary>
    private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t p)
    {
            CoopRuntime.LogSource?.LogInfo($"friend join request, auto-joining lobby {p.m_steamIDLobby}");
        JoinLobby(p.m_steamIDLobby);
    }

    private static int ParseMax(string s)
    {
        return int.TryParse(s, out var v) ? v : 0;
    }

    public string LastError = "";
}
