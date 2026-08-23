using System;

namespace OpenNestCoop.Core;

/// <summary>
/// 记住"房间设置"（2026-08-23）：房间名 / 最大人数 / 房间密码，持久化到本地文件，
/// 下次启动自动恢复（建房 UI 预填）。
/// - 存储：Unity Application.persistentDataPath/open_nest_lobby_settings.txt（双平台一致）。
/// - 格式：roomName\nmaxPlayers\npassword（密码为**明文本地文件**——仅本机记住自己房间的密码，
///   非加密；介意可留空密码）。
/// - 双端各自记住（host 端建房设置）。
/// </summary>
public static class LobbySettings
{
    /// <summary>最近进入的房间 Steam lobby id（0=无）。供"快速重连"。</summary>
    public static ulong RecentLobbyId;

    private static string Path =>
        System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "open_nest_lobby_settings.txt");

    /// <summary>保存房间设置（建房时调用）。失败仅告警，不影响建房。
    /// 格式：roomName\nmaxPlayers\npassword\nrecentLobbyId。</summary>
    public static void Save(string roomName, int maxPlayers, string password)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(roomName ?? "").Append('\n');
            sb.Append(maxPlayers).Append('\n');
            sb.Append(password ?? "").Append('\n');
            sb.Append(RecentLobbyId);
            System.IO.File.WriteAllText(Path, sb.ToString());
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"LobbySettings.Save: {ex.Message}"); }
    }

    /// <summary>读取房间设置；文件不存在/损坏时返回默认值（roomName=""、maxPlayers=Default、password=""）。</summary>
    public static void Load(out string roomName, out int maxPlayers, out string password)
    {
        roomName = "";
        maxPlayers = NetConfig.DefaultMaxPlayers;
        password = "";
        RecentLobbyId = 0;
        try
        {
            if (!System.IO.File.Exists(Path)) return;
            var lines = System.IO.File.ReadAllLines(Path);
            if (lines.Length > 0) roomName = lines[0];
            if (lines.Length > 1) { int v; if (int.TryParse(lines[1], out v) && v >= 2 && v <= 8) maxPlayers = v; }
            if (lines.Length > 2) password = lines[2];
            if (lines.Length > 3) { ulong v; if (ulong.TryParse(lines[3], out v)) RecentLobbyId = v; }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"LobbySettings.Load: {ex.Message}"); }
    }
}
