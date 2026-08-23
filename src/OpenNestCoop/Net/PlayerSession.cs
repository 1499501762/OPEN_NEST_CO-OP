namespace OpenNestCoop.Net;

public class PlayerSession
{
    public ulong SteamId;
    public string Name = "";
    /// <summary>房间内玩家序号，0 = 主机。</summary>
    public byte PlayerId;
    public OpenNestCore.Avatar.CrewRole Role = OpenNestCore.Avatar.CrewRole.None;
    public bool IsHost;
    public bool IsLocal;
    /// <summary>往返延迟（毫秒）。</summary>
    public float PingMs;
    public long LastPingSentTicks;
}
