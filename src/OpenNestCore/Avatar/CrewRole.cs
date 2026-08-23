namespace OpenNestCore.Avatar;

/// <summary>炮组分工角色（供化身视觉/动作状态使用）。</summary>
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
