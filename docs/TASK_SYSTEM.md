# 游戏任务系统（SleepyNodes 节点图引擎）研究

> **目的**：研究 Iron Nest: Heavy Turret Simulator 的**任务系统实现**（`SleepyNodes` 命名空间的
> 可视化节点图状态机引擎 + `MissionManager` 流程控制器），为 OpenNestCore 抽象「自定义任务框架」
> 提供事实依据。2026-08-23 整理。
>
> **关联**：`docs/TELEPRINTER_MISSION.md`（打字机/任务管理器同步）、`docs/CUSTOM_MISSION.md`
> （Core 自定义任务框架）、`docs/API.md`（消息协议）。

---

## 更新记录

- 2026-08-23 初稿：依据 `tools/dump_Assembly-CSharp.txt` 全量核对 SleepyNodes 图引擎 / 任务类型 / 流程控制器 / 现有同步模块。
- 2026-08-23 第六节映射更新为完整图模型（`OncNode`/`OncOperation`）；自定义任务框架见 `docs/CUSTOM_MISSION.md`。
- 2026-08-25 五/六节更新：`MissionSync`/`MissionSyncV2` 广播增加 `@c:<MissionID>` 自定义任务前缀（防 scene 名撞原生卡片进错任务）+ 任务图当前节点 nodeId（同步序号）；Core `OncMissionSyncState` 补 `DoneNodeIds` + `OncMissionRuntime.ApplySyncState` 闭环 `BuildSyncState`。
- 2026-08-25 客机不进任务修复：①`GetMissionId` 对 `'MissionBase'`（无区分度默认场景名）回退广播 `CurrentMission.MissionID`（客机可按 MissionID 匹配卡片）；②`TryLoadMissionScene` 移除直接调 `m.LoadMission`（MLL interop 签名不匹配 → Method not found），改用 `card.ActivateMission()`/`StartOperation`；③失败不再 `LoadMainMenu`/`EnterBrowsingMap`（避免拉回选任务界面，保持现状等主机保活重发）。
- 2026-08-31 第六节映射 ↔ `src/OpenNestCore/Tasks/*.cs` 核对：`OncNodeKind` 全类别 / `OncOperation`(前置后置) / `OncMission.Requires` / `OncMissionRuntime.BuildSyncState`/`ApplySyncState` 与代码一致，无改动。

## 一、总览：任务是「节点图状态机」，不是「任务列表」

游戏任务系统 = **`SleepyNodes` 节点图（Node Graph）任务状态机引擎**（`Assembly-CSharp` 内，
`SleepyNodes` 命名空间；IL2CPP interop 下 MelonLoader 为 `Il2CppSleepyNodes`，BepInEx 为全局命名空间）。

- 任务（`MissionGraph`）是一张**有向图**：`State_Start` 入口 → 一系列**状态节点**（等待/动作/分支/事件/目标）
  通过 **NodePort 连接**（边）流转 → `State_End`（成功）或 `State_MissionFailed`（失败）。
- 图在 **Unity 编辑器里可视化编辑**（节点 + 端口连线，xNode 风格），运行时由 `StateGraph.Run()`
  从入口启动，逐节点执行 `OnEnter → OnExecute → OnExit`，沿连接流转；`EventNode` 等待游戏事件
  （击杀/着弹/计时器/通知），`SideExecutionPaths` 支持并行执行线。
- 战役（`OperationGraph`）由多个 `MissionNode` 组成（每个指向一张 `MissionGraph` + 解锁条件链）；
  任务可挂独立 `ObjectiveGraph`（目标子状态机）、`MissionPassiveGraph`（常驻被动图）、
  `PunchcardGraph`（补给卡）、`ImpactGraph`（着弹处理）。

## 二、图引擎类层次（依据 dump 核对）

| 类 | 基类 | 职责 / 关键成员 |
|---|---|---|
| `SleepyNodes.Node` | ScriptableObject | 节点基类：`position`(Vector2)、`ports`(NodePort 字典)、`graph`、`GetConnectedNode<T>(fieldName)`、`GetInputValue<T>`、`AddDynamicInput/Output`、`Init()`、`OnCreateConnection`/`OnRemoveConnection`、`ClearConnections` |
| `SleepyNodes.NodePort` | Object | 连接点：`direction`(IO)、`connectionType`、`typeConstraint`、`connections`、`IsConnected`、`Connect/Disconnect`、`GetConnectedNode` |
| `SleepyNodes.NodeGraph` | ScriptableObject | 图容器：`nodes`(List\<Node\>)、`NodeRestriction`/`NodeTypeExludes`(类型白/黑名单)、`AddNode<T>()`、`RemoveNode`、`Clear`、`Copy` |
| `SleepyNodes.StateGraph` | NodeGraph | **状态图**：`EntryPoint`(StateNodeEntry)、`_EventNodes`/`EventNodes`(List\<EventNode\>)、`CurrentState`(NodeExecutionState)、`SideExecutionPaths`(Dictionary\<string,NodeExecutionState\> 并行)、`Variables`(Dictionary\<string,object\> 图级变量)；`Run()`、`Update()`、`SetVariable/TryGetVariable<T>` |
| `SleepyNodes.StateNode` | Node | **状态节点**：`From`、`NodeID`；生命周期 `OnEnter(state)`/`OnExecute(state)`/`OnExit(state,To,outField)`/`OnEvent(data,state)`/`OnNotification(state,notif)`/`ResetNode()`；`GetState<T>/SetState<T>/TryGetState<T>`(在 NodeExecutionState 里存 key-value 运行状态) |
| `SleepyNodes.EventNode` | StateNode | **事件节点**：`EventEnabled`、`EnableOnStart`、`OnlyOnce`、`AlreadyTriggered`、`To`(目标状态)；`CheckShouldRun(EventData)`/`ShouldRun`/`Run`；`OnEnter/OnExecute/OnExit` |
| `SleepyNodes.StateNodeEntry` | Node | 入口节点基类：`To`(首个状态)、`Run(state)` |
| `StateNode/NodeExecutionState` | —（嵌套） | 节点运行时执行状态（含 key-value 状态存储） |
| `EventNode/EventData` | —（嵌套） | 事件数据基类；子类 `EventData_EntityDestroyed/Impact/Notification/RequisitionPointsSpent/Timer/TurretMovement/MissionCompleted/MissionFailed` |

> ⚠️ 注意：`StateGraph` 的 `Variables` 是**图级**变量（跨节点共享）；`StateNode` 的 `GetState/SetState`
> 是**节点级**运行状态（存当前执行上下文）。两者都可用于任务数据传递。

## 三、具体任务图类型

| 类型 | 基类 | 说明 / 关键成员 |
|---|---|---|
| `OperationGraph` | StateGraph | **战役图**：`OperationID`、`displayName`、`description`、`Missions`(List\<MissionNode\>) |
| `MissionNode` | Node | 战役任务节点：`Mission`(MissionGraph)、`UnlockCondition`/`NextUnlockCondition`、`UnlockedBy`/`Unlocks`（解锁链） |
| `MissionGraph` | StateGraph | **任务图**：`MissionID`、`MissionName`/`MissionDescription`(Localisation.TextIdentifier)、`SceneReference`(MissionSceneReference.sceneName)、`MissionType`、`Medals`、`RequisitionPoints`、`PowderCharges`、`RequiredPunchcards`/`UnlockedPunchcards`(PunchcardDefinitionV2)、`PassiveGraphs`(MissionPassiveGraph[])、`Zones`、`mutators`、成就；`Run()`、`OnMissionLoaded/Unloaded`、`ResetNodes` |
| `MissionPassiveGraph` | StateGraph | 被动图（常驻）：`EntryPoint`(State_Start)、`ParentGraph`；`OnMissionStart`/`SendNotification` |
| `ObjectiveGraph` | StateGraph | **目标图**（子状态机）：`EntryPoint`(ObjectiveEntry)、`ParentGraph`、`ParentNode`(State_Objective)、`IsActivated`；`StartObjective`/`Finish(ObjectiveResults)` |
| `ObjectiveEntry` / `ObjectiveStateNode` / `ObjectiveResultNode` | Node / StateNode / ObjectiveStateNode | 目标入口 / 目标状态节点 / 目标结果（`Result`=ObjectiveResults） |
| `PunchcardGraph` | StateGraph | 补给卡图：入口 `State_CardActionStart`（含 PunchardVariableSetup 变量表） |
| `ImpactGraph` | StateGraph | 着弹图：`StartImpact(shell, impactLocation)` 返回受影响实体 |
| `MissionSceneReference` | Object | `sceneName`（任务场景名，MissionSync 同步主键之一） |

### 3.1 具体状态节点 `State_*`（任务内容：动作/等待/分支）

按功能分组（全部继承 `StateNode`，均有 `To` 指向下一步）：

- **入口**：`State_Start`（任务图入口）、`State_CardActionStart`（补给卡入口）、`State_ImpactStart`（着弹入口）
- **结束**：`State_End`（成功，`ForceSetState`）、`State_MissionFailed`（失败，`ForceSetState`）
- **目标**：`State_Objective`（挂 `ObjectiveGraph`，`OnSuccess`/`OnFailure` 分支）
- **等待**：`State_WaitSeconds`（含 Cancel/ResetTime/InstantProgress 分支）、`State_WaitBarrier`（计数屏障 Count/current/AutoReset）、`State_WaitEntityDestroyed`、`State_WaitForNotification`、`State_WaitShellLanded`
- **分支**：`State_ConditionBranch`（ConditionSet→OnFail/To）、`State_RandomBranch`（随机选）、`State_SplitBranch`（并行分叉，InheritContextVariables）
- **计时**：`State_GenericTimer`（TimerID）、`State_StartTimer`/`State_StopTimer`/`State_PauseTimer`/`State_UnpauseTimer`/`State_TimerAddTime`
- **打字机/通知**：`State_TeleprinterText`（Printer/AlarmState/EntityIDToReplace/WaitUntilComplete）、`State_ClearTeleprinter`、`State_QueueArticle`、`State_Newspaper`、`State_SendUINotification`（Text_Title/Text_Description/Duration/Tint）、`State_SendSceneNotification`、`State_ClearSignalAlarm`
- **实体**：`State_SpawnMapEntity`（Health/Armour/Role/Icon/ID/LocationToSpawn/NumberToSpawn/StartingState）、`State_MoveMapEntity`、`State_SetEntityState`、`State_DamageEntity`、`State_EntitySelector`、`State_SpawnScoutPlane`、`State_TriggerImpact`
- **炮塔**：`State_MoveTurret`、`State_SetTurretLocation`
- **奖励/统计**：`State_AddShell`、`State_AddPowderCharge`、`State_AddRequisitionPoints`、`State_AddPunchcard`、`State_AddMedals`、`State_AddLeaderboardPoints`、`State_SetCustomMedalValue`、`State_CustomTrackingVariable`（TrackingVariable/Operation/Source/Value）
- **其他**：`State_Blocker`（阻塞）、`State_UnlockSceneObject`、`State_TestNode`、`State_Newspaper`

### 3.2 具体事件节点 `Event_*`（等待游戏事件）

全部继承 `EventNode`：`Event_OnMissionCompleted`、`Event_OnMissionFailed`、`Event_OnMedalsChanged`、
`Event_OnNotification`、`Event_OnRequisitionPointsSpent`、`Event_OnGenericTimerReachedTime`、
`Event_OnGenericTimerStarted`、`Event_OnTimerExpired`、`Event_EntityDestroyed`、`Event_ShellLanded`、
`Event_TimeInterval`、`Event_TurretMovement`、`Event_OnCounterBatteryEvent`。

## 四、流程控制器（MissionManager 等）

### 4.1 `MissionManager`（单例 `Instance`）

持有当前任务/战役图，驱动 `GamePhase` 切换：

- **属性**：`CurrentMission`(MissionGraph)、`CurrentOperation`(OperationGraph)、`CurrentMissionSceneName`(string)、
  `CurrentMissionState`(MissionState)、`CurrentPhase`(GamePhase)、`mainMenuScene`、`TurretGrid`、`SceneObject_EndOfMission`
- **事件**：`MissionChanged`/`MissionChanging`(Action\<MissionGraph,MissionGraph\>)、`PhaseChanged`(Action\<GamePhase,GamePhase\>)、
  `MainMenuLoaded/Loading/Unloaded/Unloading`(Action\<string\>)
- **方法**：`StartOperation(OperationGraph, MissionGraph)`、`LoadMission(MissionGraph, bool forceReload)`、
  `LoadMainMenu()`、`EnterBrowsingMap()`、`SetPhase(GamePhase)`、`FinishMission()`、`MarkMissionComplete(bool)`、
  `MarkMissionFailed(bool)`、`ReloadCurrentMission()`、`ReturnToMap()`、`EndOperationAndReturnToMenu()`、
  `SaveOperationState()`/`LoadOperationState(OperationState)`、`SetTrackingValue(MedalTrackedValue, float)`、
  `SetCustomTrackingValue(string, float)`/`ModifyCustomTrackingValue(string, float)`、`SetupMissionPunchcards()`、
  `UnloadCurrentMissionSceneIfAny()`/`UnloadMainMenuIfLoaded()`

> ⚠️ 关键坑（记忆核实）：`CurrentOperation` 只在玩家点过任务卡片（`StartOperation`）后才非 null，
> 客机没点过恒为 null；`CurrentMissionSceneName` 某些版本返回空 → 回退 `CurrentMission.MissionID`。
> `GamePhase` 可 cast（MainMenu=0 / BrowsingMap=1 / MissionActive=2）。

### 4.2 周边组件

- **`MapCard`**（MonoBehaviour，选任务界面卡片）：`Campaign`(OperationGraph)、`Mission`(MissionGraph)、
  `Medals`、`ActivateMission()`（点卡片开任务，内部走解锁检查+StartOperation+场景加载）、`Init(MissionGraph)`、`PopulateMissionInfo()`
- **`MapCardManager`**：`Campaign`、`MapCards`、`UpdateMapCards()`、`ForceRevealAll()`
- **`OperationState`**：战役进度（`OperationID`、`CardStates`、`MissionStates`、`RequisitionPoints`、`PowderCharges`）
- **`FireMission`**（单例，任务场景实体生成器）：`seed`/`fixedSeed`/`useFixedSeed`、`Entities`(Dictionary\<string,MapEntity\>)、
  `GenerateMission()`、`CreateMapEntity(...)`、`ProcessEvent(EventNode/EventData)`、`ProcessNotification(string)`、
  `MoveMapEntity(...)`、`RunningImpactGraphs`、`RunningTimers`
- **`MissionStatsTracker`**（单例）：`campaign`/`mission`(Stats)、`TargetsDestroyed`/`ShotsFired`/`HitsOnTargets` 等统计，
  结算 UI 读取
- **`EndOfMissionUIController`**：结算界面

## 五、模组现有的任务相关同步（V1 默认方案）

| 模块 | MsgType | 同步内容 | 要点 |
|---|---|---|---|
| `MissionSync` | 102 | 任务标识（原生 scene 或 `MissionBase` 回退 `MissionID`；自定义任务为 `@c:<MissionID>` 前缀）+ phase（GamePhase）+ 任务随机 seed + 任务图当前节点 nodeId（同步序号） | 主机权威；主机进任务生成固定 seed → 广播 → 客机 `FireMission.useFixedSeed/fixedSeed` 应用（两端随机一致）；客机 phase==2 → `TryLoadMissionScene`：自定义 `@c:` → `OncMissionBridge.StartNative(id)`，原生优先 MapCard.ActivateMission/StartOperation（**不直接调 LoadMission**——MLL interop 签名不匹配 Method not found）；失败不跳选任务界面（等主机保活重发）；nodeId 广播任务图当前主执行线节点 |
| `MissionEventSync` | 130 | 任务过渡事件：Finish(1)/Complete(2)/Failed(3)/Reload(4)/ReturnMap(5)/EndOperation(6) | Harmony patch `MissionManager` 6 方法，prefix 先上报再放行；对端 `Apply` 调同名方法；`IsApplying` 防环 |
| `NotificationSync` | 131 | `UINotificationManager.ShowNotification` 事件（任务通知） | postfix 广播 title/desc/lifetime，对端复现 |
| `TeleprinterSync` | 134 | 打字机打印/状态/清除事件 | 仅主机广播；客机本地打印抑制（详见 `TELEPRINTER_MISSION.md`） |
| 快照 | 30 | `StateSnapshotSync` 注册 `"mission"` 快照（scene/phase/seed） | 中途加入对齐 |

Harmony patch 挂点：`MissionManager.FinishMission/MarkMissionComplete/MarkMissionFailed/ReloadCurrentMission/
ReturnToMap/EndOperationAndReturnToMenu`、`FireMission.GenerateMission`（应用 seed）、`UINotificationManager.ShowNotification`、
`Teleprinter.SubmitLines/ClearAll/ClearAlarm`。

## 六、与 Core 自定义任务框架的映射

「自定义任务框架」（`OpenNestCore.Tasks`，见 `docs/CUSTOM_MISSION.md`）是对上述引擎的**平台无关完整图模型抽象**：

| 游戏任务系统 | Core 抽象 | 说明 |
|---|---|---|
| `Node`/`NodePort`/`StateGraph`/`StateNode`/`EventNode` | `OncNode`（`From`/`To` 连线）+ `OncMissionRuntime`（图状态机） | 节点图 + 执行器；出边多=分支/并行，入边多=汇合 |
| `State_*` 具体节点（40+） | `OncNodeKind`（Wait/Event/Branch/Random/Split/Objective/Teleprinter/Notify/实体/奖励/计时器/解锁/Custom） | 全功能类别通用抽象 |
| `Event_*` + `EventData` | `OncEvent` + `OncEventRoute`（事件分流） | 事件总线 |
| `OperationGraph`/`MissionNode`/解锁链 | `OncOperation`/`OncMissionRef`/`OncMission.Requires` | 前置/后置任务 |
| `ObjectiveGraph`/`ObjectiveStateNode` | `OncObjective` + 目标跟踪 | 目标子状态机 |
| `MissionManager`/`MapCard`/`FireMission`/`Teleprinter`/`UINotificationManager` | `IOncMissionHost`（桥接契约）+ `OncMissionBridge`（游戏侧默认宿主） | 场景/打字机/通知/实体/奖励/seed |
| 联机同步（`MissionSync`/`MissionEventSync`） | `OncMissionRuntime.BuildSyncState()`/`ApplySyncState()`（同步序号） | Core 引擎任务闭环（`OncMissionSyncState` 含 `DoneNodeIds`/`ActiveNodeIds`/`Objectives`）；原生格式 CSM 走原生图，由 `MissionSync` 同步 scene/seed + 原生图当前节点 nodeId |
