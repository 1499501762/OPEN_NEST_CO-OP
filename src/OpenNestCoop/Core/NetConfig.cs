namespace OpenNestCoop.Core;

public static class NetConfig
{
    public const string Guid = "dev.open-nest.coop";
    public const string Name = "Open Nest Co-op";
    public const string Version = "0.2.1-Alpha-2";

    public const int DefaultMaxPlayers = 4;
    public const int P2PChannel = 0;

    // 本地回环测试（双开，不经 Steam）：host 监听此端口，client 连 127.0.0.1
    public const int LocalDefaultPort = 29507;

    // 大厅发现标记：只列出装了本 mod 的房间
    public const string LobbyTagKey = "OpenNestCoop";
    public const string LobbyTagValue = "1";
    public const string LobbyNameKey = "name";
    public const string LobbyMaxKey = "max";
    public const string LobbyVersionKey = "ver";   // 房间模组版本号（浏览列表显示/标识 + 握手核对）
    public const string LobbyPasswordKey = "pwd";  // 房间密码（FNV-1a hash，非空=有密码）
    public const string LobbyLoaderKey = "loader"; // 房间宿主加载器标识（仅显示，不核对）

    /// <summary>本端模组加载器标识（仅显示用）：BepInEx / MelonLoader。</summary>
    public static string LoaderName =>
#if MELONLOADER
        "MelonLoader";
#else
        "BepInEx";
#endif

    /// <summary>握手协议版本（Hello/Welcome 结构版本；2 = 含版本号+密码字段；3 = 追加注册通道表；
    /// 4 = 追加前导字节宽度沟通（HeaderWidth 1/2，1 字节用尽自动扩展为 2 字节））。旧客户端无此字段 → 拒绝。</summary>
    public const byte HandshakeVersion = 4;

    public const float PingInterval = 3f;
    public const int MaxChatLines = 80;

    /// <summary>房间密码 hash（FNV-1a 64 → hex）。纯 C# 无依赖，跨 BepInEx/MelonLoader 一致。
    /// 仅用于"防止随便进"，非安全加密（LobbyData 对所有人可见）。</summary>
    public static string HashPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return "";
        ulong h = 14695981039346656037UL;
        var bytes = System.Text.Encoding.UTF8.GetBytes(password);
        for (int i = 0; i < bytes.Length; i++) { h ^= bytes[i]; h *= 1099511628211UL; }
        return h.ToString("x16");
    }
}
