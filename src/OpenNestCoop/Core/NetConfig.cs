namespace OpenNestCoop.Core;

public static class NetConfig
{
    public const string Guid = "dev.open-nest.coop";
    public const string Name = "Open Nest Co-op";
    public const string Version = "0.1.0";

    public const int DefaultMaxPlayers = 4;
    public const int P2PChannel = 0;

    // 本地回环测试（双开，不经 Steam）：host 监听此端口，client 连 127.0.0.1
    public const int LocalDefaultPort = 29507;

    // 大厅发现标记：只列出装了本 mod 的房间
    public const string LobbyTagKey = "OpenNestCoop";
    public const string LobbyTagValue = "1";
    public const string LobbyNameKey = "name";
    public const string LobbyMaxKey = "max";

    public const float PingInterval = 3f;
    public const int MaxChatLines = 80;
}
