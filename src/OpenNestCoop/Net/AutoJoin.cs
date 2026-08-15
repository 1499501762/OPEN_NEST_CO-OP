using System;
using System.IO;
using OpenNestCoop.Core;

namespace OpenNestCoop.Net;

/// <summary>
/// 自动联机（测试用）：通过命令行参数启动 host / client 并自动配对。
///
/// 参数：
///   --autohost         自动创建房间（Steam 模式主机）。创建成功后把 lobby id 写到共享文件。
///   --autojoin         自动加入房间（Steam 模式）：从共享文件读取 host 的 lobby id 并加入。
///   --autolobby <file> 自定义共享文件路径（默认 %TEMP%/open_nest_lobby.txt）。
///   --local host       本地回环模式 host（不经 Steam，监听 TCP，供双开测试）。
///   --local join       本地回环模式 client（不经 Steam，连 127.0.0.1 端口）。
///   --localport <n>    本地模式端口（默认 29507）。
///
/// 用途：双开测试——
///   - 两个 Steam 会话（跨机/两账号）：--autohost + --autojoin（走 Steam）。
///   - 同机免 Steam：--local host + --local join（TCP 回环，无需两个 Steam 会话）。
/// </summary>
public static class AutoJoin
{
    /// <summary>是否已处理（一次性）。</summary>
    private static bool _handled;

    /// <summary>是否要求自动建房（Steam）。</summary>
    public static bool WantHost;
    /// <summary>是否要求自动加入（Steam）。</summary>
    public static bool WantJoin;
    /// <summary>本地模式：host。</summary>
    public static bool WantLocalHost;
    /// <summary>本地模式：client。</summary>
    public static bool WantLocalJoin;
    /// <summary>本地模式端口。</summary>
    public static int LocalPort = NetConfig.LocalDefaultPort;
    /// <summary>共享文件路径。</summary>
    public static string LobbyFile = "";
    /// <summary>新同步方案（--sync new 走 V2 测试版，默认 old V1 稳定线）。双端必须一致（握手校验，见 NetManager）。</summary>
    public static bool WantNewSync;

    private static bool _hostTriggered;

    // 中途加入时暂停游戏（防加入过程中场景/操作干扰）；成功/失败后恢复。
    private static bool _pausedForJoin;

    /// <summary>自动联机过程中是否已请求暂停（供外部检测，如 CoopBehaviour 状态机避免误恢复）。</summary>
    public static bool PausedForJoin => _pausedForJoin;

    /// <summary>解析命令行参数（CoopRuntime.Startup 调用）。</summary>
    public static void ParseCommandLine()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").Trim();
                if (a.Equals("--autohost", StringComparison.OrdinalIgnoreCase)) WantHost = true;
                else if (a.Equals("--autojoin", StringComparison.OrdinalIgnoreCase)) WantJoin = true;
                else if (a.Equals("--autolobby", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    LobbyFile = args[++i];
                else if (a.Equals("--local", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string v = (args[++i] ?? "").Trim();
                    if (v.Equals("host", StringComparison.OrdinalIgnoreCase)) WantLocalHost = true;
                    else if (v.Equals("join", StringComparison.OrdinalIgnoreCase)) WantLocalJoin = true;
                }
                else if (a.Equals("--localport", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out int p) && p > 0 && p < 65536) LocalPort = p;
                }
                else if (a.Equals("--sync", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    // 同步方案：old（默认，V1 稳定线）/ new（SyncV2 分层，开发中）
                    string v = (args[++i] ?? "").Trim();
                    if (v.Equals("new", StringComparison.OrdinalIgnoreCase)) WantNewSync = true;
                }
            }
            if (LobbyFile.Length == 0)
                LobbyFile = Path.Combine(Path.GetTempPath(), "open_nest_lobby.txt");
            bool any = WantHost || WantJoin || WantLocalHost || WantLocalJoin;
            if (any)
                CoopRuntime.LogSource?.LogInfo($"[AutoJoin] args: host={WantHost} join={WantJoin} localHost={WantLocalHost} localJoin={WantLocalJoin} port={LocalPort}");
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"[AutoJoin] failed to parse args: {ex.Message}");
        }
    }

    /// <summary>由 NetManager.Update 调用（每帧，内部一次性）。本地模式直接触发；Steam 模式等 SteamReady。</summary>
    public static void TryStart(NetManager net)
    {
        if (_handled || net == null) return;
        if (!WantHost && !WantJoin && !WantLocalHost && !WantLocalJoin) return;
        if (net.State != SessionState.Idle) return;

        // 本地回环模式：不经 Steam，直接建房/加入
        if (WantLocalHost || WantLocalJoin)
        {
            net.LocalMode = true;
            net.LocalPort = LocalPort;
            if (WantLocalHost)
            {
                _handled = true;
                _hostTriggered = true;
                // host 建房：不暂停（主机本地正常操作；建房即时完成）
                net.CreateLobby();
                CoopRuntime.LogSource?.LogInfo($"[AutoJoin] local mode host (port {LocalPort})");
            }
            else
            {
                // client：可能 host 还没就绪，连接失败不置 handled，下一帧重试。
                // 首次尝试加入时暂停游戏（中途加入防干扰）；成功/失败后恢复。
                if (!_pausedForJoin) { PauseForJoin(); _pausedForJoin = true; }
                if (net.JoinLobby(new LobbyInfo()))
                {
                    _handled = true;
                    CoopRuntime.LogSource?.LogInfo($"[AutoJoin] local mode client connected (port {LocalPort})");
                }
                else
                {
                    CoopRuntime.LogSource?.LogInfo("[AutoJoin] local client connect failed, retrying...");
                }
            }
            return;
        }

        // Steam 模式：等 SteamReady
        if (!net.SteamReady) return;
        if (WantHost)
        {
            _handled = true;
            _hostTriggered = true;
            net.CreateLobby();
            CoopRuntime.LogSource?.LogInfo("[AutoJoin] auto-host triggered");
        }
        else if (WantJoin)
        {
            if (!_pausedForJoin) { PauseForJoin(); _pausedForJoin = true; }
            _handled = true;
            TryJoinFromFile(net);
        }
    }

    /// <summary>加入开始：暂停游戏（RequestGlobalPause）——加入过程中场景/实体生成不受玩家操作干扰。</summary>
    private static void PauseForJoin()
    {
        try
        {
            if (PauseManager.IsPaused) return; // 已暂停（可能玩家自己暂停）不重复请求
            PauseManager.RequestGlobalPause();
            CoopRuntime.LogSource?.LogInfo("[AutoJoin] auto-join: game paused (resumed after join)");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[AutoJoin] PauseForJoin: {ex.Message}"); }
    }

    /// <summary>加入成功/失败后恢复暂停（ReleaseGlobalPause）。在会话状态变为 Hosting/Joined（成功）
    /// 或回到 Idle（失败放弃）时由外部调用。</summary>
    public static void ResumeIfPaused()
    {
        if (!_pausedForJoin) return;
        _pausedForJoin = false;
        try
        {
            if (PauseManager.IsPaused)
            {
                PauseManager.ReleaseGlobalPause();
                CoopRuntime.LogSource?.LogInfo("[AutoJoin] auto-join done: game resumed");
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[AutoJoin] ResumeIfPaused: {ex.Message}"); }
    }

    /// <summary>host 建房成功后调用（把 lobby id 写入共享文件）。</summary>
    public static void OnHostEntered(NetManager net)
    {
        if (!_hostTriggered) return;
        try
        {
            ulong id = net.Lobby.LobbyID.IsValid() ? (ulong)net.Lobby.LobbyID : 0;
            if (id == 0) return;
            File.WriteAllText(LobbyFile, id.ToString());
            CoopRuntime.LogSource?.LogInfo($"[AutoJoin] host lobby id {id} → {LobbyFile}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[AutoJoin] failed to write lobby id: {ex.Message}"); }
    }

    private static void TryJoinFromFile(NetManager net)
    {
        try
        {
            if (!File.Exists(LobbyFile))
            {
                CoopRuntime.LogSource?.LogWarning($"[AutoJoin] lobby file {LobbyFile} not found, retrying");
                _handled = false; // 允许重试（host 可能还没写完）
                return;
            }
            string text = File.ReadAllText(LobbyFile).Trim();
            if (!ulong.TryParse(text, out ulong id) || id == 0)
            {
                CoopRuntime.LogSource?.LogWarning($"[AutoJoin] invalid lobby file content: '{text}'");
                _handled = false;
                return;
            }
            net.JoinLobby(new LobbyInfo { Id = id });
            CoopRuntime.LogSource?.LogInfo($"[AutoJoin] auto-joining lobby {id}");
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"[AutoJoin] join failed: {ex.Message}");
            _handled = false;
        }
    }
}
