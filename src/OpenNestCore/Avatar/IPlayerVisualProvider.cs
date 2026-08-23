using UnityEngine;

namespace OpenNestCore.Avatar;

/// <summary>
/// 抽象动作状态——供角色视觉提供者驱动骨架/动画。
/// 传输层只传"意图"（干什么/多快），具体骨骼/动画由提供者本地映射（两端同动画资源时可预测播放）。
/// 如需精确同步 Animator 参数或骨骼变换，请用联机运行时的同步注册表注册为值绑定。
/// </summary>
public enum PlayerAction : byte
{
    Idle = 0,
    Moving = 1,
    Reloading = 2,          // 正在装填
    LoadingShell = 3,       // 推弹/退壳
    AdjustingElevation = 4, // 调俯仰/射角
    OperatingDevice = 5,    // 操作其它设备（阀门/摇把/机器）
    Custom = 255,           // 自定义动作（配合 DeviceId 区分）
}

/// <summary>
/// 化身姿态/动作状态（供角色视觉提供者驱动骨架/动画）。
/// Position/Yaw 由联机运行时插值后写入；Speed/Moving 供走路动画；Action/DeviceId 供设备/任务动画。
/// MoveFwd/MoveStrafe 是本地空间速度分量（正=前/右），供横移姿态；Airborne/Crouched 供跳跃/蹲下姿态。
/// </summary>
public struct AvatarPose
{
    public Vector3 Position;
    public float Yaw;
    public float Speed;        // 估算移动速度（米/秒）
    public bool Moving;
    public CrewRole Role;      // 当前角色分工
    public PlayerAction Action; // 当前动作（驱动动画状态机）
    public int DeviceId;       // 正在操作的设备/炮（0 = 无）

    // ---- 移动方向/姿态 ----
    public float MoveFwd;      // 本地空间前进速度分量（米/秒，正=向前）
    public float MoveStrafe;   // 本地空间横向速度分量（米/秒，正=向右）
    public bool Airborne;      // 空中（跳跃/下落）
    public bool Crouched;      // 蹲下
    public bool Sprinting;     // 奔跑
    public float Pitch;        // 摄像机俯仰角（度，抬头为正）——驱动头部转向
}

/// <summary>
/// 角色视觉提供者接口——其他模组可注册自定义的玩家模型/骨架/动画。
/// 注册方式：PlayerVisualRegistry.Register(myProvider)（任何模组在加载时调用即可）。
/// 未注册时用联机运行时的内置默认。
/// </summary>
public interface IPlayerVisualProvider
{
    /// <summary>创建并挂载视觉到 root 下。返回视觉根（Update/Destroy 用）；失败返回 null 则回退默认。</summary>
    GameObject Create(Transform root, string playerName, Color tint);

    /// <summary>每帧更新（位置/朝向已在 root 上；这里驱动动作/动画/billboard）。</summary>
    void Update(GameObject visual, float dt, ref AvatarPose pose);

    /// <summary>销毁视觉（root 由联机运行时统一销毁，这里可清理子对象/资源）。</summary>
    void Destroy(GameObject visual);
}

/// <summary>
/// 角色视觉提供者注册表：别的模组通过 Register 注入自定义模型/骨架/动画。
/// </summary>
public static class PlayerVisualRegistry
{
    public static IPlayerVisualProvider Provider { get; private set; }

    /// <summary>注册自定义角色视觉提供者（覆盖默认）。传入 null 可恢复默认。</summary>
    public static void Register(IPlayerVisualProvider provider) => Provider = provider;

    public static void Clear() => Provider = null;
}
