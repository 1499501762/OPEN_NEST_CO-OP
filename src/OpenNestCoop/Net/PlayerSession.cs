namespace OpenNestCoop.Net;

/// <summary>炮组分工角色（M2 起启用；M1 只登记）。</summary>
public enum CrewRole : byte
{
    None = 0,
    /// <summary>指挥/主机</summary>
    Commander = 1,
    /// <summary>瞄准手：控制炮塔转向/俯仰</summary>
    Gunner = 2,
    /// <summary>装填手：选择/装填炮弹</summary>
    Loader = 3,
    /// <summary>射击诸元：操作弹道计算机/下达射击</summary>
    FireControl = 4,
}

public class PlayerSession
{
    public ulong SteamId;
    public string Name = "";
    /// <summary>房间内玩家序号，0 = 主机。</summary>
    public byte PlayerId;
    public CrewRole Role = CrewRole.None;
    public bool IsHost;
    public bool IsLocal;
    /// <summary>往返延迟（毫秒）。</summary>
    public float PingMs;
    public long LastPingSentTicks;
}
