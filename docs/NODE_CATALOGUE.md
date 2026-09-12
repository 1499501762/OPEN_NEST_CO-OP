# 原生任务节点目录（NODE CATALOGUE）

> IRON NEST 任务系统（SleepyNodes 状态图引擎）的**完整原生节点目录**——48 个 `State_*` + 13 个 `Event_*` = **61 个节点类型**，
> 与参考模组（Iron Nest Mission Tools 1.1.2）声明的一致。来源：游戏 interop
> `Assembly-CSharp.dll`（`MelonLoader\Il2CppAssemblies`，命名空间 `Il2CppSleepyNodes`）metadata 提取
> （类型名）+ `ref/mission editor/Docs/Observador.definition.json`（字段结构）+ `docs/CUSTOM_MISSION.md` 13.x 实测。
> 提取工具：`tools/NodeMetaDump`（离线 .NET metadata 读取，不依赖 ilspycmd/网络）。
>
> 用途：① 写原生格式自定义任务（`CSM/*.json`，`csm_native` 路径）时查节点名/字段；
> ② `OncMissionImporter` Core→原生转换的映射依据；③ 参考模组编辑器能力的对标。

---

## 一、节点类型总览（61）

| 分类 | 数量 | 节点 |
|---|---|---|
| 入口 / 结束 | 6 | `State_Start` / `State_End` / `State_MissionFailed` / `State_CardActionStart` / `State_Blocker` / `State_SaveMission` |
| 流程路径 | 7 | `State_WaitSeconds` / `State_WaitBarrier` / `State_WaitForNotification` / `State_ConditionBranch` / `State_RandomBranch` / `State_SplitBranch` / `State_TestNode` |
| 时间 | 10 | `State_StartTimer` / `State_StopTimer` / `State_PauseTimer` / `State_UnpauseTimer` / `State_TimerAddTime` / `State_GenericTimer` / `State_WaitShellLanded` / `Event_TimeInterval` / `Event_OnGenericTimerStarted` / `Event_OnGenericTimerReachedTime` |
| 地图实体 | 9 | `State_SpawnMapEntity` / `State_MoveMapEntity` / `State_DamageEntity` / `State_SetEntityState` / `State_WaitEntityDestroyed` / `State_EntitySelector` / `State_SpawnScoutPlane` / `State_MoveTurret` / `State_SetTurretLocation` |
| 文本 / 输出 | 3 | `State_TeleprinterText` / `State_SendUINotification` / `State_SendSceneNotification` |
| 信号 / 事件 | 10 | `Event_EntityDestroyed` / `Event_ShellLanded` / `Event_TurretMovement` / `Event_OnNotification` / `Event_OnCounterBatteryEvent` / `Event_OnTimerExpired` / `Event_OnMissionCompleted` / `Event_OnMissionFailed` / `Event_OnRequisitionPointsSpent` / `Event_OnMedalsChanged` |
| 炮弹 / 着弹 | 5 | `State_TriggerImpact` / `State_ImpactStart` / `State_ClearSignalAlarm` / `State_ClearTeleprinter` / `State_Newspaper` |
| 炮塔 / 弹舱 | 5 | `State_AddShell` / `State_AddPowderCharge` / `State_AddRequisitionPoints` / `State_QueueArticle` / `State_UnlockSceneObject` |
| 点数 / 卡牌 / 奖章 | 6 | `State_AddPunchcard` / `State_AddMedals` / `State_AddLeaderboardPoints` / `State_SetCustomMedalValue` / `State_CustomTrackingVariable` / `State_Objective` |

> 分组参考参考模组 CHANGELOG 的 9 个 band；`Event_*` 基类 = `EventNode`（接线方式与 `State_*` 不同，
> 见 §四）。**7 个节点类型被原生 importer 拒绝**（参考模组原话），编辑器不可测试——具体是哪 7 个未在文档列出，
> 写任务时避开疑似"编辑器专用"节点（`State_Blocker`/`State_CardActionStart`/`State_Newspaper` 等）。

## 二、State_* 节点字段速查（NodeData）

> 精确 JSON 结构见 `ref/mission editor/Docs/Observador.definition.json`（完整样例）与 `examples/csm_native/`。
> TextIdentifier = `{"Raw": "...", "Key": ""}`；导入后需 `RepairImportedTexts` 回填（见 CUSTOM_MISSION.md 13.1）。

### 常用节点（我们已验证 / 参考模组样例确认）

| 节点 | NodeData 字段 | 说明 |
|---|---|---|
| `State_Start` | `NodeID` | 图入口（基类 StateNodeEntry） |
| `State_End` | `NodeID`, `ForceSetState`(bool) | 成功结束 |
| `State_MissionFailed` | `NodeID` | 失败结束 |
| `State_WaitSeconds` | `Seconds` | 等待秒数 |
| `State_TeleprinterText` | `Text`(TextIdentifier), `Printer`(0=Primary), `OnlyQueue`(bool), `WaitUntilComplete`(bool), `AlarmState`("Low"/"High"/"None"), `EntityIDToReplace`(List<StringReplacement>) | 打字机简报；`OnlyQueue=false` 出字、`WaitUntilComplete=false` 不卡任务、`EntityIDToReplace=[]` 防 NRE |
| `State_SendUINotification` | `Text_Title`(TextIdentifier), `Text_Description`(TextIdentifier), `Duration` | UI 通知 |
| `State_SpawnMapEntity` | `ID`, `DisplayName`(TextIdentifier), `Role`(int 复合枚举), `Icon`(string), `Health`, `Armour`, `Stars`, `NumberToSpawn`, `Scale`, `StartingState`, `PresetIcon`(bool), `SetContextVariable`(bool), `LastSpawnedEntity`(enum), `LocationToSpawn`{`LocationType`(0=Grid/1=Zone), `ZoneID`, `FuzzyLocation`(bool), `RandomiseSubgrid`(bool)}, `ImmuneShells` | 地图生成实体（Fire 目标）。`Role` 复合枚举：`33`=Enemy Field Artillery；**131617**=`Target\|EnemyGroup3\|Infantry`（可被炮击识别）；`6`=Ally, Infantry。`LocationToSpawn.LocationType=1` + `ZoneID`="Enemy"/"Allied"（场景预置 Zone）|
| `State_WaitEntityDestroyed` | `Entites`/`targetSelection`(TargetSelection) | 等待实体摧毁。TargetSelection：`SourceType`(0=FromContext/1=FromFilter), `ContextKey`(EntityContextKeys), `Filter`, `CountType`(0=All/1=Count), `Count`, `SortType`。**为 null → 立即通过**（不等待）；配 `{SourceType:0, ContextKey:0(EntityTarget), CountType:0, Count:0}` 等该实体摧毁 |
| `State_DamageEntity` | `EntityFilter`, `Damage` | 伤害实体 |
| `State_AddRequisitionPoints` | `Amount` | 加补给点 |
| `State_AddPowderCharge` | `Amount` | 加发射药 |
| `State_AddShell` | `ShellType`, `Amount` | 加炮弹 |
| `State_StartTimer` | `TimerName`, `Duration` | 启动计时器 |
| `State_StopTimer` / `PauseTimer` / `UnpauseTimer` | `TimerName` | 停/暂停/恢复计时器 |
| `State_TimerAddTime` | `TimerName`, `Time` | 计时器加时 |
| `State_CustomTrackingVariable` | `variableName`(string), `CustomVariableKey` | **自定义追踪变量**（脚本化模块载体：`variableName="onc.script.<name>"` → 桥接层分派脚本模块，见 CUSTOM_MISSION.md 15 节）|

### 次常用 / 待验证字段（getter 已确认存在，JSON 结构未全量验证）

| 节点 | 已知字段（get_ 确认） | 说明 |
|---|---|---|
| `State_MoveMapEntity` | `LocationToMoveTo` | 移动实体 |
| `State_SetEntityState` | `StateToAdd`, `StateValue` | 设实体状态 |
| `State_TriggerImpact` / `State_ImpactStart` | `LocationHit` / `LocationFilter` / `LocationContextKeys` | 着弹 |
| `State_SendSceneNotification` | `NotifID` | 场景通知 |
| `State_UnlockSceneObject` | `UnlockedSceneObjects`(List), `Unlocks`, `UnlockedBy`, `UnlockCondition` | 解锁场景对象 |
| `State_WaitForNotification` | `NotificationID`(string) | 等通知事件（近似 `OncNodeKind.WaitForEvent`）|
| `State_GenericTimer` | `TimerName`, `Duration` | 通用计时器（近似 `OncNodeKind.WaitTimerExpired` 兜底）|
| `State_ConditionBranch` | `Conditions`(List), `ConditionType`, `Operator`, `CompareAbsoluteValues` | 条件分支（`OncNodeKind.Branch`）|
| `State_Objective` | `Objective`(ObjectiveGraph 引用) | **缺完整 Objective 引用 → 中断图**（CUSTOM_MISSION.md 13.5）|
| `State_AddPunchcard` | `Punchcard` / `PunchcardId` | 卡牌 |
| `State_AddMedals` / `SetCustomMedalValue` | `Medals` / `MedalID` / `FilterByMedalID` | 奖章 |
| `State_EntitySelector` | `FilterEntitys` / `FilterEntityType` / `FilterCondition` / `FilterIndex` | 实体选择 |
| `State_MoveTurret` / `SetTurretLocation` | `Turret`, `TurretGrid`, `LocationToMoveTo` | 铁巢位置 |
| `State_Newspaper` | `Article` / `ArticlePools` / `NewspaperMessageId` | 报纸 |
| `State_SpawnScoutPlane` | — | 侦察机 |

## 三、Event_* 节点（事件接线）

Event 节点**不在主执行线**，靠事件触发唤醒（参考模组：**指向一个事件 = 唤醒 sleeping 事件**，`EnableOnStart=false` 的事件
被指向后才会激活）。13 个：

| 事件节点 | 触发条件 | EventData 类 |
|---|---|---|
| `Event_EntityDestroyed` | 实体被摧毁 | `EventData_EntityDestroyed` |
| `Event_ShellLanded` | 炮弹着弹 | `EventData_Impact` |
| `Event_TimeInterval` | 时间间隔 | — |
| `Event_TurretMovement` | 铁巢移动 | `EventData_TurretMovement` |
| `Event_OnNotification` | UI 通知 | `EventData_Notification` |
| `Event_OnGenericTimerStarted` | 通用计时器启动 | `EventData_Timer` |
| `Event_OnGenericTimerReachedTime` | 通用计时器到点 | `EventData_Timer` |
| `Event_OnTimerExpired` | 计时器到期 | `EventData_Timer` |
| `Event_OnMissionCompleted` | 任务完成 | — |
| `Event_OnMissionFailed` | 任务失败 | — |
| `Event_OnCounterBatteryEvent` | 反炮兵事件 | — |
| `Event_OnRequisitionPointsSpent` | 补给点消耗 | `EventData_RequisitionPointsSpent` |
| `Event_OnMedalsChanged` | 奖章变化 | — |

## 四、OncNodeKind → 原生节点映射（OncMissionImporter）

| OncNodeKind | 原生 | 备注 |
|---|---|---|
| Start / End / Fail | State_Start / State_End / State_MissionFailed | |
| WaitSeconds | State_WaitSeconds | |
| WaitEntityDestroyed | State_WaitEntityDestroyed | 自动配 Entites(FromContext+EntityTarget) |
| WaitForEvent | State_WaitForNotification | 近似；原生精确事件等待走 Event_* 接线 |
| WaitTimerExpired | State_GenericTimer | 兜底（原生精确 = Event_OnGenericTimerReachedTime）|
| Branch / RandomBranch / Split | State_ConditionBranch / State_RandomBranch / State_SplitBranch | Branch 条件字段待验证 |
| Objective / ObjectiveComplete / ObjectiveFail | State_Objective | ⚠️ 需完整 ObjectiveGraph 引用，否则断图 |
| Teleprinter / Notify / SceneNotification | State_TeleprinterText / State_SendUINotification / State_SendSceneNotification | |
| SpawnEntity / MoveEntity / DamageEntity / SetEntityState / Impact | State_SpawnMapEntity / State_MoveMapEntity / State_DamageEntity / State_SetEntityState / State_TriggerImpact | |
| AddRequisitionPoints / AddShell / AddPowderCharge | State_AddRequisitionPoints / State_AddShell / State_AddPowderCharge | |
| StartTimer / StopTimer / PauseTimer / ResumeTimer / AddTimerTime | State_StartTimer / State_StopTimer / State_PauseTimer / State_UnpauseTimer / State_TimerAddTime | |
| UnlockSceneObject | State_UnlockSceneObject | |
| **Scripted** | **State_CustomTrackingVariable** | 脚本化模块载体（`variableName="onc.script.<name>"`）|
| Custom | State_TestNode | C# 回调无法序列化到原生 |

## 五、参考模组对标（Iron Nest Mission Tools 1.1.2）

| 能力 | 参考模组 | 我们 |
|---|---|---|
| 节点目录（61） | ✅ 编辑器内置 palette | ✅ 本文档 + `tools/NodeMetaDump` |
| 写原生 MissionImporter JSON | ✅ | ✅ `OncMissionImporter.ExportMission` + `csm_native` |
| 任务预检 | ✅ 实体 ID 6 种标记 / token / 事件接线 / 孤立节点 | 🔄 部分（见 CUSTOM_MISSION.md 13.5 修复项）|
| 选任务面板卡片 | ✅ 编辑器内 | ✅ `OncMissionCardInjector`（3D 克隆卡片）|
| 游戏内测试 | ✅ 一键 Test | ✅ `StartNative`（CSM 文件夹自动加载）|
| 脚本化模块 | ❌（编辑器不执行逻辑）| ✅ **新增**（本分支核心，见 CUSTOM_MISSION.md 15 节）|

## 六、事件源选择纪律（A+B vs C）

**脚本模块/任务图响应游戏事件有两条轨，同一任务、同一事件只选一条**（防双触发）：

| 轨 | 事件管线 | 适用 |
|---|---|---|
| **A+B（桥接层）** | 我们的 Harmony hook → `OncMissionBridge.Raise` → ①Core 图 `WaitForEvent`/`Branch` ②脚本模块事件订阅（`RegisterScriptedHook`） | Core 引擎任务；原生任务兜底 |
| **C（原生 `Event_*` 节点）** | 游戏自己触发 → 原生图 `Event_*` 节点 | 原生格式任务（首选） |

- 事件清单（A 桥接）：`mission.started/completed/failed/finished/reloaded/map/menu`、`shell.landed`、
  `interact.click`/`interact.slot`、`gun.fired`、`requisition.spent`、`notification.shown`、
  `teleprinter.printed`、`counterbattery`、`turret.moved`、`timer.expired[.<id>]`、
  `entity.destroyed[.<id>]`（轮询 `FireMission.Entities`）。详见 `docs/CUSTOM_MISSION.md` 15.6/15.7 节。
- 危险组合：同一事件**同时**接 C 的 `Event_*` 节点和 B 的订阅 → 脚本模块被触发两次。
- 选型规则：**Core 任务走 A+B；原生任务 C 为主、B 兜底**（当 `Event_*` 导入被拒时）。
- C 模板：`examples/csm_native/01_07_event_template.json`（`State_WaitForNotification` + `Event_OnNotification`
  接线，⚠️ 待实测——若 `Event_*` 被 importer 拒绝则原生任务事件全走 B 兜底）。
