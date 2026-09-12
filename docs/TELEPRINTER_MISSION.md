# 打字机（Teleprinter）与 MissionManager 同步文档

> **目的**：记录任务打字机（Teleprinter）和任务管理器（MissionManager）的联机同步实现、
> 关键属性（来自 dump_Assembly-CSharp.txt）与同步消息格式。2026-08-22 整理。
>
> **关联**：`docs/INTERACTABLES.md`（实体术语表）、`docs/DECOUPLING.md`（解耦状态）。
>
> **更新记录**：
> - 2026-09-12（六）**修正上一版导致的“右侧一直空白”**（用户实测）：上一版“本地动画期间一律不写”的判据是
>   `IsPrinting`，但实测**本机协程已死而 `IsPrinting` 仍为 true** → 永远不写 → 一直空白。
>   判据改为“**本机揭示数是否真的在前进**”：
>   ① 在前进（健康本地动画）→ 不写状态，让本机协程自己逐字打（平滑，与旧版左机体验一致）；
>   ② 不前进（协程已死/卡住）→ 不再当作“动画中”，**改由主机状态驱动**（每个 EvState 写一次文本/揭示数/
   遮罩/纸张/打字针）→ 该台会“跟着主机逐字显示”，不再空白；同时写下揭示数后同步更新基准，
>   使下一个包继续跟随（否则会写一次跳一次，跟随变 5Hz 抖动）。
>   ③ 另修：**换新文本**（底本不同）时揭示数必须跟随主机（从 0 重新逐字）——旧版 `max(旧值,新值)` 会把
>   新文本一上来就整段显示（无动画）。
> - 2026-09-12（五）**定位到“本地打印任务一开始就死”的根因**：双端日志对比——主机右侧（ptype=1）**正常打印完**
>   （`state ptype=1 isPrinting=False revealed=463 maskCount=463`）；客机 `applied print n=17 ptype=1` 后
>   `after print ptype=1 isPrinting=True revealed=0 isRunning=True` —— **打印启动了但揭示数一直 0（无动画）**，
>   随后 `IsPrinting` 变回 False → 直接跳到最终态 `revealed=463`（旧兵底强制同步）。
>   上一版加的严格门槛 `STUCK` 未触发（因为它是“任务已静默结束”而非“卡在中途”）。
>   最可能使本地任务死掉的就是**我们每 0.1s 覆写内部字段与协程打架**（与之前 STUCK 那次 rev 冻在 714 同一现象）。
>   修法：①**本地动画期间（`keepLocalAnimation`）一律不写**打字机内部状态/视觉（底本/文本/揭示数/遮罩/
>   纸张/打字针），只在空闲时写完整最终态；②卡死时用主机文本**重起一次本地动画打印**（`TryRestartPrint`，
>   不只是清状态）→ 这台才有动画；③`EvPrint` 提交新任务前若本机已卡死 → 先定向复位（新任务不排在坏任务后）；
>   ④`job.lines` **无条件**用正确行覆盖（旧版只在首行以 `Il2CppSystem.` 开头时修 → 类型名/长度会错）。
> - 2026-09-12（四）**拿到卡死现场 + 改为严格门槛的定向复位**：实测日志（一次中打印中卡死）
>   `[Teleprinter] STUCK (stall) ptype=1 isRunning=True hasJobs=True printing=True lineCount=35 prevLine=0
>   rev=714 fullRichLen=935 maskCount=779 baselineSet=True baselineY=0.29 animTyping=True`
>   → 本机协程还在跑（`_runner`/`_isRunning`/`_pendingJobs` 非空）但揭示数冻在 714（主机已领先）。
>   修法：①卡死判定加**严格门槛**——必顶“主机揭示数 > 本机揭示数”（健康动画每秒都在前进，不会被误判）；
>   ②新增 `ResetLocalRunState()`：**只**停本机打印协程（`StopCoroutine(_runner)`）+ 清 `_pendingJobs` +
>   `_isRunning=false`，**不碰** `ForceCompleteAll`/`DrainAllJobsInstant`；③`EvState` 包**追加主机行游标**
>   （`CurrentLineCount` + `_prevLineNum`），客机强制同步最终态时一并对齐 → 修“后续新任务的换行/打字针起始位置错”。
> - 2026-09-12（三）**撤销“强制收尾”**（用户实测：“打字机打字动画完全被破坏了”）：上一版用游戏自带
>   `ForceCompleteAll()`/`DrainAllJobsInstant()` 做“干净收尾”，实测会把**后续所有打印的逐字动画全部弄没**
>   （已从代码与 DLL 中完全移除，含 `IsStalled`/`CompleteLocally` 与三处调用）。
>   现改为**纯诊断** `LogTpStuck()`（只记录、不改）：`[Teleprinter] STUCK (why) ptype=.. isRunning=.. hasJobs=..
>   printing=.. lineCount=.. prevLine=.. rev=.. fullRichLen=.. maskCount=.. baselineSet=.. baselineY=.. animTyping=..`
>   ——用于下一轮定位“强制同步不完全”到底漏了哪个内部状态（任务队列/协程/行游标/纸张基线），再靶向修。
>   强制同步最终态（文本/揭示数/遮罩/纸张/打字针）本身保留不变。
> - 2026-09-12（二）**卡死“强制同步最终态”改为完整收尾**（用户实测：“开局右侧一台空白、无任何动画，
>   然后被打字完的补发强制同步同步掉了，但强制同步不完全导致后续后发的打字任务的打字针起始位置/动画状态/
>   换行都错误”）。根因：旧实现只写视觉状态（`_currentFullRich`/`_tmp.text`/`revealed`/`_revealMask`/
>   `paperTransform`/打字针 bool）——但打字机内部还有 **`_pendingJobs` 任务队列 / `_runner` 协程 /
>   行游标（`_prevLineNum`/`CurrentLineCount`）/ 纸张基线（`_baselineSet`/`_baselineWorldY`）** 没复位，
>   坏任务会一直堵在队列里 → 新任务排在它后面 → 打字针起始位置/动画状态/换行全错。
>   修法（本版）：①新增 `CompleteLocally()`——用**游戏自带** `ForceCompleteAll()`（失败时退 `DrainAllJobsInstant()`）
>   干净收尾 + 清空 `_pendingJobs` + `_isRunning=false` + 清卡死基准；②`EvState` 卡死分支不再只设
>   `keepLocalAnimation=false`，而是先 `CompleteLocally` 再写最终态；③`EvPrint` 提交**前**如果本地已卡死，
>   先 `CompleteLocally`（否则新任务排在坏任务后面）；④`Tick` 新增**客机侧卡死自愈**
>   （`net.State==Joined` + 揭示数超时未前进 → `CompleteLocally`），不依赖主机状态包。
>   诊断：`[Teleprinter] local complete (why) ptype=.. force=.. isRunning=.. hasJobs=.. printing=.. lineCount=.. prevLine=.. rev=.. baselineSet=..`。
> - 2026-09-12 修复“开局打字机偶发不同步”（见 §1.2c）：①**丢弃路径**：`Apply` 在打字机对象尚未注册
>   （`FindPrinter`=null）时只打 warning 就 `return` 丢弃，而 `EvState` 只在**文本变化**时广播（主机 `_lastRich`
>   已更新）→ 主机不再重发 → 永久不同步；修法：丢弃时按 `ev+ptype` **暂存原始包字节**，`Tick` 里每 0.25s
>   重试直到打字机注册（纯本地重试，不加网络流量）。②**卡在打印中**：客机自己的 `IsPrinting` 可能长期为真
>   （协程卡住/标记残留；实测日志主机 `isPrinting=False` 而客机持续 `printing=True` 2 分钟）→ 旧逻辑
>   `keepLocalAnimation=true` **永远跳过**主机最终态（revealed/遮罩/纸张/敲击）→ 揭示进度与纸张位置不同步；
>   修法：揭示数 `StallSeconds=2s` 未前进 → 判定卡死 → 强制走最终态同步。
> - 2026-08-22 建文。

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

### 1.2c 已知问题：开局打字机偶发不同步（2026-09-12 ✅ 已修）

| 现象 | 分析 | 状态 |
|---|---|---|
| 开局（客机加入/任务场景加载前后）偶发：客机某台打字机空白/内容与主机不一致，且此后不再自愈 | `Apply` 里 `EvPrint`/`EvState`/`EvAppend` 均有 `if (tp == null) { warning; return; }`（`FindPrinter` → `Teleprinter.GetTeleprinter(ptype)`）→ 事件被**丢弃**；而主机侧 `EvState` 只在 `rich+revealed` **变化**时广播（`_lastRich` 已写入）→ 不会再发 → 永久不同步。是否命中完全取决于加入/加载时序 → **偶发** | ✅ 已修 |

**修法**（`TeleprinterSync.cs`）：

1. `Apply(byte ev, byte ptype, NetDataReader r, byte[] raw)` 新增 `raw` 参数（原始包字节）。
2. 三处 `tp == null` 分支改为 `StashTp(ev, ptype, raw, what)`：按 `ev*256+ptype` 暂存**最新一份**原始包。
3. `Tick` 顶部（**在所有 host-only 早期返回之前**，客户端也会跑）每 0.25s 调 `RetryPendingTeleprinter()`：
   打字机注册后重新解包并调 `Apply`（日志 `[Teleprinter] retry stashed ev=… ptype=…`）；
   上限 240 次×0.25s ≈ 60s 后放弃（避免无界堆积）。
4. `Reset()` 清空暂存。

**第二次迭代（2026-09-12，仍偶发不同步）**：实测日志显示两条不同步都不在“丢弃”路径上——
主机 `[Teleprinter] state ptype=0 … isPrinting=False revealed=524 maskCount=0`，“同一时刻客机
`applied state ptype=0 printing=True keepAnim=True`” → **客机卡在打印中**（`IsPrinting` 残留/协程未完成）
→ `keepLocalAnimation=true` 永远跳过最终态。修法：客机在 `Apply(EvState)` 中记录每台打字机的
“揭示数变化时刻”，若 `IsPrinting=true` 但 `_revealedCharIndex` **2s 内未前进** → 判定卡死 →
强制 `keepLocalAnimation=false` 走最终态（日志 `[Teleprinter] local print stalled … → 强制同步最终态`）；
新一次 `EvPrint` 会重置该基准。

> 验证要点：客机日志出现 `apply … but printer null … → 暂存待打字机注册后重试`，随后出现
> `retry stashed … （打字机已注册）`，之后 `applied print/state` 正常——即不再丢失开局简报。

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
