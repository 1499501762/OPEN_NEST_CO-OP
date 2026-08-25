using System;
using System.Collections.Generic;

namespace OpenNestCore.Tasks;

/// <summary>
/// 自定义任务节点类型。覆盖游戏任务系统（SleepyNodes 状态图引擎，见 docs/TASK_SYSTEM.md）
/// 的**全部功能类别**的平台无关抽象：入口/结束、等待、事件、分支/并行/汇合、目标、
/// 打字机/通知、地图实体（Fire 目标）、奖励、计时器、解锁、自定义动作。
/// 模组作者用这些节点以 **JSON 文件** 或 **C# 脚本（<see cref="OncMissionBuilder"/>）** 定义任务图。
/// </summary>
public enum OncNodeKind
{
    // ---- 入口 / 结束 ----
    /// <summary>任务图入口（= State_Start）。</summary>
    Start,
    /// <summary>任务成功结束（= State_End）。</summary>
    End,
    /// <summary>任务失败结束（= State_MissionFailed）。</summary>
    Fail,

    // ---- 等待 ----
    /// <summary>等待秒数后沿出边继续（= State_WaitSeconds）。</summary>
    WaitSeconds,
    /// <summary>等待事件后继续（= EventNode）；用 Routes 做多事件分流。</summary>
    WaitForEvent,
    /// <summary>等待实体被摧毁（= State_WaitEntityDestroyed，按 <see cref="EntityId"/>）。</summary>
    WaitEntityDestroyed,
    /// <summary>等待计时器到期（= Event_OnGenericTimerReachedTime，按 <see cref="TimerId"/>）。</summary>
    WaitTimerExpired,

    // ---- 分支 / 并行 / 汇合 ----
    /// <summary>条件/事件分支：Routes 命中后只走目标出边（= State_ConditionBranch / EventNode）。</summary>
    Branch,
    /// <summary>随机选一条出边走（= State_RandomBranch）。</summary>
    RandomBranch,
    /// <summary>并行：激活所有出边（= State_SplitBranch）。多入边节点天然是汇合（= Join）。</summary>
    Split,

    // ---- 目标 ----
    /// <summary>目标动作：开始/设进度/加进度（= State_Objective + ObjectiveGraph）。</summary>
    Objective,
    /// <summary>标记目标完成。</summary>
    ObjectiveComplete,
    /// <summary>标记目标失败。</summary>
    ObjectiveFail,

    // ---- 输出（打字机 / 通知）----
    /// <summary>打字机打印文本（= State_TeleprinterText）。</summary>
    Teleprinter,
    /// <summary>UI 通知（= State_SendUINotification）。</summary>
    Notify,
    /// <summary>场景通知（= State_SendSceneNotification，按 <see cref="NotifId"/>）。</summary>
    SceneNotification,

    // ---- 地图实体（Fire 目标：生成/移动/伤害/状态/着弹）----
    /// <summary>在地图生成实体（= State_SpawnMapEntity；Health/Armour/X/Y/EntityId 等）。</summary>
    SpawnEntity,
    /// <summary>移动实体到地图坐标（= State_MoveMapEntity）。</summary>
    MoveEntity,
    /// <summary>对实体造成伤害（= State_DamageEntity）。</summary>
    DamageEntity,
    /// <summary>设置实体状态（= State_SetEntityState）。</summary>
    SetEntityState,
    /// <summary>触发着弹（= State_TriggerImpact / ImpactGraph，X/Y 落点）。</summary>
    Impact,

    // ---- 奖励 / 资源 ----
    /// <summary>加补给点（= State_AddRequisitionPoints）。</summary>
    AddRequisitionPoints,
    /// <summary>加炮弹（= State_AddShell，按 <see cref="ShellId"/> + <see cref="Value"/>）。</summary>
    AddShell,
    /// <summary>加发射药（= State_AddPowderCharge）。</summary>
    AddPowderCharge,

    // ---- 计时器 ----
    /// <summary>启动/重置计时器（= State_GenericTimer / State_StartTimer）。</summary>
    StartTimer,
    /// <summary>停止计时器（= State_StopTimer）。</summary>
    StopTimer,
    /// <summary>暂停计时器（= State_PauseTimer）。</summary>
    PauseTimer,
    /// <summary>恢复计时器（= State_UnpauseTimer）。</summary>
    ResumeTimer,
    /// <summary>计时器加时（= State_TimerAddTime）。</summary>
    AddTimerTime,

    // ---- 其他 ----
    /// <summary>解锁场景对象（= State_UnlockSceneObject，按 <see cref="NotifId"/>）。</summary>
    UnlockSceneObject,
    /// <summary>自定义动作（C# 回调，最灵活；JSON 里用 <see cref="CustomData"/> 交由宿主解释）。</summary>
    Custom,
}

/// <summary>事件分流表项：当事件 <see cref="EventId"/> 触发时，走向 <see cref="TargetNodeId"/> 出边。</summary>
public sealed class OncEventRoute
{
    public string EventId;
    public string TargetNodeId;

    public OncEventRoute() { }
    public OncEventRoute(string eventId, string targetNodeId) { EventId = eventId; TargetNodeId = targetNodeId; }
}

/// <summary>目标动作类型（<see cref="OncNodeKind.Objective"/> 节点）。</summary>
public enum OncObjectiveAction
{
    /// <summary>开始目标（进度归 0，进入追踪）。</summary>
    Start,
    /// <summary>设置目标进度为指定值。</summary>
    SetProgress,
    /// <summary>目标进度增加指定值。</summary>
    AddProgress,
}

/// <summary>目标状态（运行态；JSON 导入导出不含运行态）。</summary>
public enum OncObjectiveStatus
{
    Inactive,
    Active,
    Completed,
    Failed,
}

/// <summary>
/// 自定义任务图节点。出边（<see cref="To"/>）多个 = 分支/并行；入边（<see cref="From"/>）多个 = 汇合。
/// 用静态工厂构造（<see cref="OncNode.Start"/> 等），链式配置；也可由 JSON 反序列化（<see cref="OncMissionIO"/>）。
/// </summary>
public sealed class OncNode
{
    /// <summary>节点唯一 id（任务图内）。</summary>
    public string Id;

    /// <summary>节点类型。</summary>
    public OncNodeKind Kind;

    /// <summary>入边（前置节点 id；运行时全部完成才激活本节点 = 汇合）。</summary>
    public List<string> From = new List<string>();

    /// <summary>出边（后置节点 id；多个 = 分支/并行）。</summary>
    public List<string> To = new List<string>();

    // ---- 参数（按 Kind 使用子集；全部标量/列表，JSON 友好）----

    /// <summary>等待秒数（WaitSeconds）/ 计时器初始秒数（StartTimer）/ 移动用时（MoveEntity）。</summary>
    public float Seconds;

    /// <summary>等待的事件 id（WaitForEvent / Branch）。</summary>
    public string EventId;

    /// <summary>事件分流表（EventId → TargetNodeId；WaitForEvent / Branch）。</summary>
    public List<OncEventRoute> Routes;

    /// <summary>等待超时秒（&lt;=0 永不超时；超时沿出边继续）。</summary>
    public float Timeout;

    /// <summary>通知标题（Notify）。</summary>
    public string Title;

    /// <summary>通知描述（Notify）/ 打字机文本（Teleprinter）。</summary>
    public string Message;

    /// <summary>通知时长秒（Notify）。</summary>
    public float Duration;

    /// <summary>目标 id（Objective 系）。</summary>
    public string ObjectiveId;

    /// <summary>目标动作（Objective）。</summary>
    public OncObjectiveAction ObjectiveAction;

    /// <summary>目标进度/数值（Objective 的 Set/Add、AddRequisitionPoints、AddShell 数量）。</summary>
    public int Value;

    // ---- 实体 ----
    /// <summary>实体 id（SpawnEntity 生成后引用；MoveEntity/DamageEntity/SetEntityState/WaitEntityDestroyed 定位）。</summary>
    public string EntityId;

    /// <summary>实体显示名（SpawnEntity）。</summary>
    public string EntityName;

    /// <summary>实体生命（SpawnEntity）。</summary>
    public int Health;

    /// <summary>实体装甲（SpawnEntity）。</summary>
    public int Armour;

    /// <summary>实体星数（SpawnEntity）。</summary>
    public int Stars;

    /// <summary>实体图标/角色（SpawnEntity，字符串便于 JSON）。</summary>
    public string Role;

    /// <summary>地图坐标 X / Y（SpawnEntity 生成点、MoveEntity 目标、Impact 落点；网格 0..19）。</summary>
    public float X;

    /// <summary>地图坐标 X / Y。</summary>
    public float Y;

    /// <summary>平滑移动（MoveEntity）。</summary>
    public bool Smooth;

    // ---- 资源 / 计时 / 解锁 ----
    /// <summary>炮弹 id（AddShell）。</summary>
    public string ShellId;

    /// <summary>弹舱（AddShell，&lt;0 默认）。</summary>
    public int Slot;

    /// <summary>计时器 id（StartTimer/StopTimer/PauseTimer/ResumeTimer/AddTimerTime/WaitTimerExpired）。</summary>
    public string TimerId;

    /// <summary>解锁/通知 id（UnlockSceneObject/SceneNotification）。</summary>
    public string NotifId;

    /// <summary>自定义数据（Custom 节点：宿主解释的 JSON 字符串）。</summary>
    public string CustomData;

    // ---- 脚本回调（C# 定义时用；JSON 导入不携带）----
    /// <summary>自定义动作回调（Custom，C# 脚本定义时）。</summary>
    [NonSerialized]
    public Action<OncMissionContext> Action;

    // ================= 静态工厂（脚本定义） =================

    public static OncNode Start() => new OncNode { Kind = OncNodeKind.Start };
    public static OncNode End() => new OncNode { Kind = OncNodeKind.End };
    public static OncNode Fail() => new OncNode { Kind = OncNodeKind.Fail };

    /// <summary>等待秒数后继续。</summary>
    public static OncNode Wait(float seconds) => new OncNode { Kind = OncNodeKind.WaitSeconds, Seconds = seconds };

    /// <summary>等待事件（命中沿出边继续；可 <see cref="Or"/> 分流 / <see cref="WithTimeout"/>）。</summary>
    public static OncNode WaitFor(string eventId) => new OncNode { Kind = OncNodeKind.WaitForEvent, EventId = eventId };

    /// <summary>事件分支：按事件走不同出边。</summary>
    public static OncNode Branch(params OncEventRoute[] routes)
    {
        var n = new OncNode { Kind = OncNodeKind.Branch };
        if (routes != null)
        {
            n.Routes = new List<OncEventRoute>(routes);
            if (routes.Length > 0) n.EventId = routes[0].EventId;
        }
        return n;
    }

    /// <summary>随机分支：随机选一条出边。</summary>
    public static OncNode Random() => new OncNode { Kind = OncNodeKind.RandomBranch };

    /// <summary>并行：激活所有出边（配合多入边节点天然汇合）。</summary>
    public static OncNode Split() => new OncNode { Kind = OncNodeKind.Split };

    /// <summary>目标动作（开始/设进度/加进度）。</summary>
    public static OncNode Objective(string objectiveId, OncObjectiveAction action, int value = 0)
        => new OncNode { Kind = OncNodeKind.Objective, ObjectiveId = objectiveId, ObjectiveAction = action, Value = value };

    /// <summary>标记目标完成。</summary>
    public static OncNode ObjectiveComplete(string objectiveId)
        => new OncNode { Kind = OncNodeKind.ObjectiveComplete, ObjectiveId = objectiveId };

    /// <summary>标记目标失败。</summary>
    public static OncNode ObjectiveFail(string objectiveId)
        => new OncNode { Kind = OncNodeKind.ObjectiveFail, ObjectiveId = objectiveId };

    /// <summary>打字机打印文本。</summary>
    public static OncNode Print(string text) => new OncNode { Kind = OncNodeKind.Teleprinter, Message = text };

    /// <summary>UI 通知（标题/描述/时长秒）。</summary>
    public static OncNode Notify(string title, string description, float duration = 4f)
        => new OncNode { Kind = OncNodeKind.Notify, Title = title, Message = description, Duration = duration };

    /// <summary>地图生成实体（Fire 目标）。</summary>
    public static OncNode SpawnEntity(string entityId, string name, float x, float y, int health = 100, int armour = 0, string role = null, int stars = 0)
        => new OncNode
        {
            Kind = OncNodeKind.SpawnEntity,
            EntityId = entityId,
            EntityName = name,
            X = x, Y = y,
            Health = health, Armour = armour,
            Role = role, Stars = stars,
        };

    /// <summary>移动实体到地图坐标。</summary>
    public static OncNode MoveEntity(string entityId, float x, float y, bool smooth = true, float seconds = 3f)
        => new OncNode { Kind = OncNodeKind.MoveEntity, EntityId = entityId, X = x, Y = y, Smooth = smooth, Seconds = seconds };

    /// <summary>对实体造成伤害。</summary>
    public static OncNode DamageEntity(string entityId, int damage)
        => new OncNode { Kind = OncNodeKind.DamageEntity, EntityId = entityId, Value = damage };

    /// <summary>等待实体被摧毁（沿出边继续）。</summary>
    public static OncNode WaitEntityDestroyed(string entityId)
        => new OncNode { Kind = OncNodeKind.WaitEntityDestroyed, EntityId = entityId };

    /// <summary>加补给点。</summary>
    public static OncNode AddRequisitionPoints(int amount)
        => new OncNode { Kind = OncNodeKind.AddRequisitionPoints, Value = amount };

    /// <summary>加炮弹（shellId + 数量 + 弹舱）。</summary>
    public static OncNode AddShell(string shellId, int amount, int slot = -1)
        => new OncNode { Kind = OncNodeKind.AddShell, ShellId = shellId, Value = amount, Slot = slot };

    /// <summary>加发射药。</summary>
    public static OncNode AddPowderCharge(int amount)
        => new OncNode { Kind = OncNodeKind.AddPowderCharge, Value = amount };

    /// <summary>启动计时器（timerId + 秒数）。</summary>
    public static OncNode StartTimer(string timerId, float seconds)
        => new OncNode { Kind = OncNodeKind.StartTimer, TimerId = timerId, Seconds = seconds };

    /// <summary>停止计时器。</summary>
    public static OncNode StopTimer(string timerId) => new OncNode { Kind = OncNodeKind.StopTimer, TimerId = timerId };

    /// <summary>等待计时器到期。</summary>
    public static OncNode WaitTimerExpired(string timerId)
        => new OncNode { Kind = OncNodeKind.WaitTimerExpired, TimerId = timerId };

    /// <summary>解锁场景对象（objectId）。</summary>
    public static OncNode Unlock(string objectId) => new OncNode { Kind = OncNodeKind.UnlockSceneObject, NotifId = objectId };

    /// <summary>触发着弹（落点 X/Y）。</summary>
    public static OncNode Impact(float x, float y) => new OncNode { Kind = OncNodeKind.Impact, X = x, Y = y };

    /// <summary>自定义动作（C# 回调）。</summary>
    public static OncNode Custom(Action<OncMissionContext> action) => new OncNode { Kind = OncNodeKind.Custom, Action = action };

    // ================= 链式配置 =================

    /// <summary>指定节点 id（便于 Goto/连线）。</summary>
    public OncNode As(string id) { Id = id; return this; }

    /// <summary>连出边到目标节点。</summary>
    public OncNode ToNode(string nodeId) { To.Add(nodeId); return this; }

    /// <summary>连入边（前置节点；用于汇合）。</summary>
    public OncNode FromNode(string nodeId) { From.Add(nodeId); return this; }

    /// <summary>添加事件分流（EventId → 目标节点 id）。</summary>
    public OncNode Or(string eventId, string targetNodeId)
    {
        if (Routes == null) Routes = new List<OncEventRoute>();
        Routes.Add(new OncEventRoute(eventId, targetNodeId));
        return this;
    }

    /// <summary>设置等待超时（超时沿出边继续）。</summary>
    public OncNode WithTimeout(float seconds) { Timeout = seconds; return this; }
}

/// <summary>自定义任务事件（喂给运行时 Raise，驱动 WaitFor/Branch 节点）。</summary>
public sealed class OncEvent
{
    public string Id;
    public object Payload;

    public OncEvent() { }
    public OncEvent(string id, object payload = null) { Id = id; Payload = payload; }

    public static OncEvent Of(string id, object payload = null) => new OncEvent(id, payload);
}

/// <summary>自定义任务目标（进度追踪；由 Objective 系节点驱动，可被桥接查询/上报）。</summary>
public sealed class OncObjective
{
    /// <summary>目标 id（任务图内唯一）。</summary>
    public string Id;
    /// <summary>目标标题（UI 显示）。</summary>
    public string Title;
    /// <summary>目标描述。</summary>
    public string Description;
    /// <summary>目标类型（字符串，如 "destroy"/"survive"/"fire"；宿主可据此驱动进度）。</summary>
    public string Type;
    /// <summary>目标值（&lt;=0 = 无进度，仅完成/失败判定）。</summary>
    public int Target;

    // ---- 运行态（不参与 JSON 导入导出）----
    public int Progress;
    public OncObjectiveStatus Status = OncObjectiveStatus.Inactive;

    public float ProgressNormalized => Target > 0 ? Math.Min(1f, (float)Progress / Target) : 0f;
    public bool IsCompleted => Status == OncObjectiveStatus.Completed;
    public bool IsActive => Status == OncObjectiveStatus.Active;
}

/// <summary>自定义任务在选任务面板的卡片配置（JSON "Card" 字段；位置/尺寸/颜色由任务脚本定义）。</summary>
public sealed class OncMissionCard
{
    /// <summary>卡片相对父容器左上 x（向右；&lt;0 = 自动，按生成顺序排）。</summary>
    public float X = -1f;
    /// <summary>卡片相对父容器左上 y（向下；&lt;0 = 自动，按生成顺序排）。</summary>
    public float Y = -1f;
    /// <summary>卡片宽（&lt;=0 = 默认，参考原生卡片或 200）。</summary>
    public float Width;
    /// <summary>卡片高（&lt;=0 = 默认，参考原生卡片或 140）。</summary>
    public float Height;
    /// <summary>标题颜色（十六进制 "RRGGBB" 或 "AARRGGBB"；空 = 白）。</summary>
    public string TitleColor;
    /// <summary>背景颜色（同上；空 = 默认深色）。</summary>
    public string Background;
}

/// <summary>自定义任务图（≈ MissionGraph 的平台无关抽象）。</summary>
public sealed class OncMission
{
    public string Id;
    public string DisplayName;
    public string Description;
    /// <summary>卡片类型（映射原生 MissionTypes）："Tutorial" / "Campaign" / "Challenge" / "Chill"；空 = Campaign。</summary>
    public string MissionType;
    /// <summary>要加载的任务场景名；空 = 在当前场景运行。</summary>
    public string SceneName;
    /// <summary>任务随机种子（&lt;=0 不设置）。</summary>
    public int Seed = -1;
    /// <summary>入口节点 id；空 = 自动用第一个 Start 节点或 nodes[0]。</summary>
    public string EntryPointId;

    /// <summary>任务图节点。</summary>
    public List<OncNode> Nodes = new List<OncNode>();
    /// <summary>任务目标。</summary>
    public List<OncObjective> Objectives = new List<OncObjective>();

    // ---- 前置/后置任务 ----
    /// <summary>前置任务 id 列表（全部完成才可解锁本任务）。</summary>
    public List<string> Requires = new List<string>();
    /// <summary>解锁条件表达式/事件（预留，字符串；宿主解释）。</summary>
    public string UnlockCondition;

    /// <summary>选任务面板卡片配置（可选；位置/尺寸/颜色，见 <see cref="OncMissionCard"/>）。</summary>
    public OncMissionCard Card;

    /// <summary>按 id 查节点。</summary>
    public OncNode Node(string id)
    {
        if (id == null) return null;
        for (int i = 0; i < Nodes.Count; i++)
            if (Nodes[i] != null && Nodes[i].Id == id) return Nodes[i];
        return null;
    }

    /// <summary>按 id 查目标。</summary>
    public OncObjective Objective(string id)
    {
        if (id == null) return null;
        for (int i = 0; i < Objectives.Count; i++)
            if (Objectives[i] != null && Objectives[i].Id == id) return Objectives[i];
        return null;
    }
}

/// <summary>战役里的任务引用（前置/后置任务链）。</summary>
public sealed class OncMissionRef
{
    /// <summary>任务 id（须已在注册表中注册）。</summary>
    public string MissionId;
    /// <summary>前置任务 id 列表（全部完成才解锁）。</summary>
    public List<string> Requires = new List<string>();
    /// <summary>解锁条件（预留字符串）。</summary>
    public string Condition;

    public OncMissionRef() { }
    public OncMissionRef(string missionId) { MissionId = missionId; }
}

/// <summary>自定义战役（≈ OperationGraph 的平台无关抽象：一组带前置/后置关系的任务）。</summary>
public sealed class OncOperation
{
    public string Id;
    public string DisplayName;
    public string Description;
    public List<OncMissionRef> Missions = new List<OncMissionRef>();

    public OncMissionRef Find(string missionId)
    {
        if (missionId == null) return null;
        for (int i = 0; i < Missions.Count; i++)
            if (Missions[i] != null && Missions[i].MissionId == missionId) return Missions[i];
        return null;
    }
}

/// <summary>
/// 任务运行上下文（传给 <see cref="OncNodeKind.Custom"/> 动作；也可被宿主使用）。
/// 提供任务变量（≈ StateGraph.Variables）、运行时引用、宿主引用。
/// </summary>
public sealed class OncMissionContext
{
    public OncMission Mission;
    public OncMissionRuntime Runtime;
    public IOncMissionHost Host;
    public float DeltaTime;
    public float Time;      // 任务开始后经过的秒数
    public Dictionary<string, object> Variables;

    /// <summary>触发任务事件（驱动 WaitFor/Branch 节点）。</summary>
    public void Raise(string eventId, object payload = null) => Runtime?.Raise(eventId, payload);
}
