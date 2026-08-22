# 打字机（Teleprinter）与 MissionManager 同步文档

> **目的**：记录任务打字机（Teleprinter）和任务管理器（MissionManager）的联机同步实现、
> 关键属性（来自 dump_Assembly-CSharp.txt）与同步消息格式。2026-08-22 整理。
>
> **关联**：`docs/INTERACTABLES.md`（实体术语表）、`docs/DECOUPLING.md`（解耦状态）。

---

## 一、任务打字机 Teleprinter

### 1.1 类概述

`Teleprinter`（UnityEngine.MonoBehaviour）：任务文本打印显示（逐字动画 + 纸带进给 + 敲击声）。
任务目标/通知通过打字机打印出来。多台打字机用静态 `Lookup` 字典（`Teleprinters` 枚举）索引。

### 1.2 同步模块：TeleprinterSync（MsgType=134）

**方案**：**事件解耦**——Harmony patch `SubmitLines` / `AppendInstant` / `ClearAll` / `ClearAlarm`，
广播事件 → 对端调用同名方法复现。**仅主机广播**（打字机由主机任务图权威驱动；客机打字机若被
任务/反射异步触发 SubmitLines 会回发干扰主机 → 打印队列重置卡住）。

**事件类型**：
| 常量 | 值 | 对应游戏方法 | 说明 |
|---|---|---|---|
| `EvPrint` | 1 | `SubmitLines(lines)` | 打印多行文本（主通道） |
| `EvState` | 2 | —（完整状态同步） | `_currentFullRich` + `_currentRevealedCharIndex` + `_revealMask`，兜底对齐 |
| `EvClearAll` | 3 | `ClearAll()` | 清除全部 |
| `EvClearAlarm` | 4 | `ClearAlarm()` | 清除报警 |
| `EvAppend` | 5 | `AppendInstant(chunk, prepend)` | 直接追加富文本块 |

**关键实现细节**：
- **仅主机广播**（`EvPrint` 等）：客机本地 SubmitLines 被抑制（`PreTeleprinterPrint`），防"双份打字机"。
- **逐字动画**：`EvPrint` 用 `TryCast<IEnumerable<string>>()` 运行时转换 + 直接调 `SubmitLines` →
  打字机协程启动逐字打印（revealed 逐字增加）；打印中扫描间隔缩到 0.1s 捕获中间 reveal 序列
  （0.5s 会漏掉 <0.5s 的短文本中间态）。
- **状态兜底（EvState）**：广播完整富文本 `_currentFullRich` + 揭示字符数 `_currentRevealedCharIndex`
  + 纸带局部位置 `paperTransform.localPosition` + 敲击状态 `_animTypingState`（`_revealMask` 仅本地诊断读取，不上传）；
  变化才广播，空闲时设最终态（打印中保留本地动画）。
- **敲击同步**：`EvState` 设主机 `_animTypingState` → 客机打字针跟随主机节奏。

### 1.2b 同步模块：TeleprinterSyncV2（MsgType=228，V2 分层）

`--sync new` 方案（`OpenNestCoop.SyncV2`）把 V1 `TeleprinterSync` 迁入分层架构（里程碑 M7）：

- **类**：`SyncV2/TeleprinterSyncV2.cs`（`MsgType = V2Teleprinter(228)`）。
- **权威模型**：`V2Authority.Host`——打字机事件/状态仅主机广播，客机只接收应用（经
  `HostDataLayer.Instance` 的 `IHostStore` 收发包，`Store.IsHost` 判定）。
- **事件集与 V1 完全一致**：`EvPrint(1) / EvState(2) / EvClearAll(3) / EvClearAlarm(4) / EvAppend(5)`，载荷同 V1。
- **Harmony 接线**：`PostTeleprinterPrint` 方案感知——`--sync new` 走 `TeleprinterSyncV2.Instance.OnLocalPrint`，
  默认 old 走 `TeleprinterSync.OnLocalPrint`；`PreTeleprinterPrint` 客机本地打印抑制对 V1/V2 通用
  （按 `TeleprinterSyncV2.IsApplying` / `TeleprinterSync.IsApplying` 放行网络复现）。

### 1.3 关键属性（dump_Assembly-CSharp.txt）

| 属性 | 类型 | 说明 |
|---|---|---|
| `_currentFullRich` | string | 当前完整富文本（EvState 同步内容） |
| `_currentRevealedCharIndex` | int | 已揭示字符数（逐字动画进度） |
| `_revealMask` | List\<bool\> | 逐字揭示掩码 |
| `_isRunning` / `IsPrinting` | bool | 打印协程是否运行中 |
| `_tmp` | TMP_Text | TMPro 文本组件 |
| `_pendingJobs` | Queue\<PrintJob\> | 待打印任务队列 |
| `_runner` | Coroutine | 打印协程 |
| `_animTypingState` | bool | 敲击动画状态（打字针跟随） |
| `interJobDelay` / `_cachedPausePerLetter` | float | 任务间隔/每字停顿 |
| `CurrentLineCount` | int | 当前行数 |
| `accumulatePaperFeed` / `invertPaperDirection` | bool | 纸带进给方向 |
| `onAllJobsCompleted` / `onCharacterPrinted` | UnityEvent | 完成/打印事件 |
| `Lookup` | Dictionary\<Teleprinters, Teleprinter\> | 打字机索引 |

### 1.4 主要方法

`SubmitLines(...)`（打印，主入口）、`AppendInstant(chunk, prepend)`（追加）、`ClearAll()`（清除）、
`ClearAlarm()`（清除报警）、`ForceCompleteAll()`（强制完成）、`DrainAllJobsInstant()`（瞬时排空）。

### 1.5 Harmony patch

| 方法 | patch | 作用 |
|---|---|---|
| `SubmitLines` | prefix `PreTeleprinterPrint` + postfix `PostTeleprinterPrint` | 客机抑制本地打印（V1/V2 通用）+ 主机提取文本行广播（方案感知：V2→`TeleprinterSyncV2`，old→`TeleprinterSync`） |
| `AppendInstant` | postfix `PostTeleprinterAppend` | 广播追加块（V1 `OnLocalAppend`） |
| `ClearAll` | postfix `PostTeleprinterClearAll` | 广播清除（V1 `OnLocalClearAll`） |
| `ClearAlarm` | postfix `PostTeleprinterClearAlarm` | 广播清除报警（V1 `OnLocalClearAlarm`） |

### 1.6 任务打字机通知同步（NotificationSync / NotificationSyncV2）

任务状态机还通过 **UINotificationManager.ShowNotification** 在打字机/界面弹出任务通知（目标确认/阶段提示等），
由通知同步模块广播复现：

- **V1**：`NotificationSync`（MsgType=131）——patch `UINotificationManager.ShowNotification`（postfix `PostShowNotification`），
  主机广播 title/description/lifetime，客机本地 `ShowNotification` 复现；防环 `IsApplying`。
- **V2**：`NotificationSyncV2`（`--sync new`，里程碑 M7）——纯事件走 `EventLayer`（事件 id `v2/notification`，
  `V2Authority.Operator`：谁触发谁广播，对端复现，防环由 EventLayer `_reproducing` 承担）。
  `PostShowNotification` 方案感知：V2 → `NotificationSyncV2.Instance.OnLocalShow`，old → `NotificationSync.OnLocalShow`。

---

## 二、任务管理器 MissionManager

### 2.1 类概述

`MissionManager`（单例 `Instance`）：任务/战役流程管理（主菜单 ↔ 选任务 ↔ 任务中 ↔ 结算）。
持有当前 MissionGraph/OperationGraph，驱动 GamePhase 切换。

### 2.2 同步模块

#### MissionEventSync（MsgType=130）—— 任务过渡事件（事件解耦）

Harmony patch 6 个方法 → 广播 → 对端调用同名方法（`m.FinishMission()` 等）：

| 常量 | 值 | 游戏方法 | 触发 patch |
|---|---|---|---|
| `EvFinish` | 1 | `FinishMission()` | `PreMissionFinish` |
| `EvComplete` | 2 | `MarkMissionComplete(bool)` | `PreMissionComplete` |
| `EvFailed` | 3 | `MarkMissionFailed(bool)` | `PreMissionFailed` |
| `EvReload` | 4 | `ReloadCurrentMission()` | `PreMissionReload` |
| `EvReturnMap` | 5 | `ReturnToMap()` | `PreMissionReturnMap` |
| `EvEndOperation` | 6 | `EndOperationAndReturnToMenu()` | `PreMissionEndOperation` |

**要点**：prefix 先上报同步再放行原方法（两端各自执行任务逻辑）；必须 try/catch（prefix 异常会中断原方法结算流程）。

#### MissionSync（MsgType=102）—— 任务状态/seed 同步（状态广播）

同步 `CurrentMissionSceneName`（任务标识）+ `GamePhase`（阶段）+ 任务随机 **seed**：

| 字段 | 类型 | 说明 |
|---|---|---|
| scene | string | 任务标识（优先 `CurrentMissionSceneName`，回退 `CurrentMission.MissionID`） |
| phase | byte | `GamePhase` 转 byte（2=任务中） |
| seed | int | 任务随机种子（任务内容/目标位置一致的源头） |

**要点**：
- **主机生成 seed**：进入任务（phase==2 且 scene 有效）时若还没有 seed，生成固定 seed 应用本地
  `FireMission`（`useFixedSeed=true`）并广播；记住到 `_hostSeed`（FireMission.seed 读不到时稳定回退，
  避免每 0.5s 重新生成 → 任务目标不同步）。
- **客机应用**：收到 seed → `_pendingSeed` + 静态 `PendingSeed` 持续重试 → `FireMission.GenerateMission`
  prefix 应用固定 seed → 两端随机一致。
- **保活重发**：scene 有效时每 2s 保活重发（新成员加入/漂移兜底）。
- **加载任务**：客机收到 phase==2 + scene → `TryLoadMissionScene` 匹配 MapCard/MissionGraph 加载；
  空 scene 只同步 phase 不加载（防空名进选任务界面弹窗）。
- **GamePhase 语义**（从 `GetPhaseByte` = `(byte)(int)CurrentPhase`，任务中=2）。

### 2.3 关键属性（dump_Assembly-CSharp.txt）

| 属性 | 类型 | 说明 |
|---|---|---|
| `Instance` | MissionManager | 单例 |
| `CurrentMission` | MissionGraph | 当前任务图 |
| `CurrentMissionSceneName` | string | 当前任务场景名（同步主键） |
| `CurrentOperation` | OperationGraph | 当前战役图 |
| `CurrentPhase` | GamePhase | 当前阶段（主菜单/选任务/任务中/结算） |
| `CurrentMissionState` | MissionState | 任务状态 |
| `MissionChanged` / `MissionChanging` | Action\<MissionGraph,MissionGraph\> | 任务切换回调 |
| `PhaseChanged` | Action\<GamePhase,GamePhase\> | 阶段切换回调 |
| `MainMenuLoaded` / `MainMenuLoading` / `MainMenuUnloaded` | Action\<string\> | 主菜单生命周期 |
| `SceneObject_EndOfMission` | GameObject | 任务结算场景对象 |
| `TurretGrid` | DraggableItemGridArea | 炮塔网格 |
| `autoLoadMainMenuOnStart` / `autoManageMainMenu` | bool | 主菜单自动管理 |

### 2.4 主要方法（同步相关）

`FinishMission()`、`MarkMissionComplete(bool)`、`MarkMissionFailed(bool)`、`ReloadCurrentMission()`、
`ReturnToMap()`、`EndOperationAndReturnToMenu()`、`SetPhase(GamePhase)`、`LoadMission(scene)`、
`UnloadCurrentMissionSceneIfAny()`、`OnPhaseChanged(prev, next)` / `HandlePhaseChanged(prev, next)`。

---

## 三、组件属性速查（可交互组件 dump）

> 供同步开发参考：哪些属性是同步取值/设值目标。完整列表见 `dump_Assembly-CSharp.txt`。

### LookAtTarget（点击按钮/拉杆入口）

`animator`、`clickCooldownSeconds`（冷却）、`currentMalfunction`、`cursorManager`、
`autoFindCursorManagerByTag`、`debugLogs`、`alwaysReleaseToSameTarget`、`cursorManagerTag`。
（同步用：`OnClickDown()`/`OnClickUp()`、`isActive`、`nextAllowedClickTime`）

### AnimatorBoolToggler（toggle 布尔开关）

`animator`、`delay`（回弹延迟——>0 瞬时回弹，跳过立即 SetBool）、`parameterName`（如 `IsOpen`）、
`directTarget`、`discoveryMode`、`autoRefreshOnEnable`、`tryRefreshIfMissingOnCall`。
（同步用：`GetBool()`/`SetBool()`、`delay`）

### DialInteractable（刻度盘/旋钮/齿轮）

`accumulatedValue`（小写，可读写——同步取值源）、`AccumulatedValue`（大写，只读，IL2CPP 下读恒 0 ⚠️）、
`isDragging`（busy 判定）、`_MeasuredRotationSpeed`/`_NormalizedRotationSpeed`（转速）、`baseLocalPosition` 等。

### LinearSliderInteractable（滑块/杠杆/拉环）

`Value`、`currentDistance`/`CurrentDistance`、`accumulatedValue`、`isDragging`（busy）、
`baseLocalPosition`、`alwaysReleaseToSameTarget`、`_MeasuredLinearSpeed`/`_NormalizedLinearSpeed`。
（`LinearSliderAutoRetractor` 自动回弹——拉环类）

### Interactable（通用可交互底层）

`isInteractable`/`IsInteractable`、`isPassive`/`IsPassive`、`promptText`、`restrictToAllowedColliders`、
`allowedColliders`、`cursorOverride`/`cursorGrabOverride`、`populateFromChildrenOneShot`。
⚠️ 仰角锁止 `Wheel Blocker`/`Handle Blocker` 是此类型（非 LookAtTarget）。

### PowderChargeController（发射药）

`currentSelectedCharges`（当前选药量——ReloadSync 同步）、`loadChargesButton`（投放按钮）、
`chargeButtons`/`chargeDispensers`（选药按钮/分配器列表）、`chargeSelectionStateKey`、
`maxCharges`、`DispensedChargesFloat`、`dispenseTrigger`、`chamberSlot`、`disableButtonsWhenInventoryEmpty`、
`autoReactivateOnInventoryRefill`、`ApplyInventoryAvailabilityToUI()`（刷新装药按钮 UI）。

### ArmedFireRelayOneShot（预备激发，P0 解耦目标）

`_leftArmed`/`_rightArmed`（左/右炮预备状态）、`ArmLeft()`/`ArmRight()`/`ArmBoth()`（预备）、
`DisarmLeft()`/`DisarmRight()`/`DisarmAll()`（解除）、`_leftArmedEvent`/`_rightArmedEvent`/
`_leftDisarmedEvent`/`_rightDisarmedEvent`（状态事件）、`_anyArmedEvent`/`_allDisarmedEvent`、
`_fireLeft`/`_fireRight`（开火 UnityEvent 触发）、`_clearOnEnable`/`_disarmBeforeInvoke`。

### CylinderShellSelector（弹舱/切弹）

`loadButton`（上弹按钮）、`moveButton`（切弹舱按钮）、`shellPrefabs`、`slots`、`SlotCount`、
`artilleryReloadController`、`loadStateKey`/`moveStateKeys`、`lastLoadedShellPrefab`、
`onShellDeployedByPlayer`、`AFRotateDone()`/`AFRotateMid()`/`AnimationEvent_RepopulateSlotA()`。
