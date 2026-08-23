# OpenNestCore 自定义任务框架（Custom Mission Framework）

> **目的**：OpenNestCore 抽象出的**平台无关自定义任务引擎**——覆盖游戏原生任务系统
> （SleepyNodes 状态图引擎）的全部功能类别：地图任务目标创建、前置/后置任务链、
> 任务内 Fire 目标序列、事件/分支/并行/计时器/奖励等。模组作者用 **JSON 文件** 或
> **C# 脚本（Builder）** 定义任务，由 Core 引擎驱动，经桥接落到真实游戏。2026-08-23。
>
> **关联**：`docs/TASK_SYSTEM.md`（游戏原生任务系统研究）、`docs/TELEPRINTER_MISSION.md`（打字机/任务管理器）、
> `docs/API.md`（联机消息协议）。**代码**：`src/OpenNestCore/Tasks/` + `src/OpenNestCoop/GameSync/OncMissionBridge.cs`。

---

## 更新记录

- 2026-08-23 初稿：任务框架（图引擎 + JSON/脚本定义 + 桥接 + 前置后置 + 同步序号）设计文档。
- 2026-08-23 新增「游戏侧对接发现」节（MissionMapLoader / MissionSaveManager / ResourcesModule 限制 + 反编译工具链，源自 UI 调研交接）。
- 2026-08-23 默认宿主新增 `LoadMapSprite`（走 `MissionMapLoader.Acquire`，`Il2CppSystem.Action<Sprite>` 经 `DelegateSupport.ConvertDelegate` 桥接）。

## 一、为什么抽象（定位）

游戏原生任务是 **SleepyNodes 可视化节点图状态机**（Unity 编辑器里连线，见 `docs/TASK_SYSTEM.md`），
第三方模组无法在编辑器里制作、也无法脱离游戏类型复用。本框架把**同一套图模型**抽象成平台无关的
`OpenNestCore.Tasks`：

- **不依赖游戏 Assembly-CSharp**（只依赖 UnityEngine 基元 + Il2CppInterop），任何 IL2CPP 模组都能用；
- **两种等价定义方式**：JSON 任务文件（数据驱动，可热加载/分享）或 C# 脚本（Builder，可写逻辑）；
- **桥接层**（OpenNestCoop 游戏侧）负责把节点动作落到真实游戏（场景/打字机/通知/地图实体/奖励/解锁）；
- **联机"同步序号"预留**：`OncMissionRuntime.BuildSyncState()` 导出序号 + 目标状态小负载。

## 二、核心概念：节点图

任务 = **一张有向图**（`OncMission.Nodes`），节点（`OncNode`）通过出边（`To`）与入边（`From`）连接：

| 概念 | 说明 | 对应游戏（SleepyNodes） |
|---|---|---|
| `OncNode` | 图节点：`Id` + `Kind` + `From`/`To` + 参数 | `Node` / `StateNode` / `EventNode` |
| 出边 `To` 多个 | 分支 / 并行（Split） | `NodePort` 多出边 / `State_SplitBranch` |
| 入边 `From` 多个 | 汇合（全部完成才激活本节点） | 多入边 / `State_WaitBarrier` |
| `OncEventRoute` | 事件分流（事件 → 目标节点） | `EventNode` 事件路由 |
| `OncMission` | 任务图：`Id/DisplayName/Description/SceneName/Seed/Nodes/Objectives/Requires` | `MissionGraph` |
| `OncObjective` | 任务目标（进度追踪） | `ObjectiveGraph` / `State_Objective` |
| `OncOperation` | 战役：一组带前置/后置的任务 | `OperationGraph` / `MissionNode` 解锁链 |
| `OncEvent` | 运行时事件（驱动等待节点） | `EventNode/EventData` |

**执行模型**（`OncMissionRuntime`）：入口（Start 节点或 nodes[0]）激活 → 瞬时节点执行动作后沿出边继续 →
挂起节点（等待秒/事件/实体/计时器）留在激活表 → `End` 成功 / `Fail` 失败 / 无 End 且走完 = 自动成功。

## 三、节点类型（OncNodeKind）— 覆盖原生全部功能类别

| 类别 | `OncNodeKind` | 说明（对应游戏节点） |
|---|---|---|
| 入口/结束 | `Start` / `End` / `Fail` | State_Start / State_End / State_MissionFailed |
| 等待 | `WaitSeconds` / `WaitForEvent` / `WaitEntityDestroyed` / `WaitTimerExpired` | State_WaitSeconds / EventNode / State_WaitEntityDestroyed / Event_OnGenericTimerReachedTime |
| 分支/并行 | `Branch` / `RandomBranch` / `Split` | State_ConditionBranch / State_RandomBranch / State_SplitBranch（多入边=汇合） |
| 目标 | `Objective`(Start/Set/Add) / `ObjectiveComplete` / `ObjectiveFail` | State_Objective + ObjectiveGraph |
| 输出 | `Teleprinter` / `Notify` / `SceneNotification` | State_TeleprinterText / State_SendUINotification / State_SendSceneNotification |
| 地图实体（Fire 目标） | `SpawnEntity` / `MoveEntity` / `DamageEntity` / `SetEntityState` / `Impact` | State_SpawnMapEntity / State_MoveMapEntity / State_DamageEntity / State_SetEntityState / State_TriggerImpact |
| 奖励/资源 | `AddRequisitionPoints` / `AddShell` / `AddPowderCharge` | State_AddRequisitionPoints / State_AddShell / State_AddPowderCharge |
| 计时器 | `StartTimer` / `StopTimer` / `PauseTimer` / `ResumeTimer` / `AddTimerTime` | State_GenericTimer / State_StartTimer / State_PauseTimer / State_UnpauseTimer / State_TimerAddTime |
| 其他 | `UnlockSceneObject` / `Custom` | State_UnlockSceneObject / 自定义 StateNode |

> `Custom` 节点：C# 脚本定义时直接挂 `Action<OncMissionContext>` 回调（最灵活）；JSON 定义时用
> `CustomData` 字符串，由宿主/模组解释执行。

## 四、定义方式一：JSON 任务文件

任务文件是普通 JSON（`OncJson` 解析，IL2CPP 安全；字段名与节点参数一致）。示例
（"坚守阵地"：打印 → 开始目标 → 等 300s → 目标完成 → 结束）：

```json
{
  "Id": "custom.defend",
  "DisplayName": "坚守阵地",
  "Description": "保护阵地 5 分钟",
  "SceneName": "",
  "Seed": 42,
  "Requires": ["custom.intro"],
  "Objectives": [
    { "Id": "survive", "Title": "坚守 5 分钟", "Description": "存活 300 秒", "Type": "survive", "Target": 300 }
  ],
  "Nodes": [
    { "Id": "n1", "Kind": "Start", "To": ["n2"] },
    { "Id": "n2", "Kind": "Print", "Message": "敌军接近！坚守阵地！", "To": ["n3"] },
    { "Id": "n3", "Kind": "Notify", "Title": "任务开始", "Message": "守住防线！", "Duration": 5, "To": ["n4"] },
    { "Id": "n4", "Kind": "Objective", "ObjectiveId": "survive", "ObjectiveAction": "Start", "To": ["n5"] },
    { "Id": "n5", "Kind": "WaitSeconds", "Seconds": 300, "To": ["n6"] },
    { "Id": "n6", "Kind": "ObjectiveComplete", "ObjectiveId": "survive", "To": ["n7"] },
    { "Id": "n7", "Kind": "End" }
  ]
}
```

**事件分流示例**（"等待击杀或超时"）：

```json
{
  "Id": "wait.branch", "DisplayName": "击杀或撤退",
  "Nodes": [
    { "Id": "n1", "Kind": "Start", "To": ["n2"] },
    { "Id": "n2", "Kind": "Branch", "EventId": "enemy.killed", "Timeout": 120,
      "Routes": [ { "EventId": "enemy.killed", "TargetNodeId": "win" } ], "To": ["lose"] },
    { "Id": "win", "Kind": "End" },
    { "Id": "lose", "Kind": "Fail" }
  ]
}
```

**Fire 目标序列示例**（生成敌人 → 等敌人被摧毁 → 生成下一波）：

```json
{
  "Id": "fire.waves", "DisplayName": "击退三波",
  "Nodes": [
    { "Id": "n1", "Kind": "Start", "To": ["spawn1"] },
    { "Id": "spawn1", "Kind": "SpawnEntity", "EntityId": "enemy1", "EntityName": "敌方步兵", "X": 8, "Y": 5, "Health": 50, "Role": "Enemy", "To": ["wait1"] },
    { "Id": "wait1", "Kind": "WaitEntityDestroyed", "EntityId": "enemy1", "To": ["print1"] },
    { "Id": "print1", "Kind": "Print", "Message": "第一波击退！", "To": ["end"] },
    { "Id": "end", "Kind": "End" }
  ]
}
```

**前置/后置（战役）**：任务 `Requires` 数组声明前置任务 id（全部完成才解锁）；也可用
`OncOperation`（见下）。

## 五、定义方式二：C# 脚本（Builder）

与 JSON 等价，产出同一张任务图：

```csharp
using OpenNestCore.Tasks;

var mission = OncMissionBuilder.Create("custom.defend")
    .Named("坚守阵地").Described("保护阵地 5 分钟").InScene("").WithSeed(42)
    .Requires("custom.intro")                       // 前置任务
    .Objective(new OncObjective { Id = "survive", Title = "坚守 5 分钟", Type = "survive", Target = 300 })
    .Print("敌军接近！坚守阵地！")
    .Notify("任务开始", "守住防线！", 5f)
    .Objective("survive", OncObjectiveAction.Start)
    .Wait(300f)
    .ObjectiveComplete("survive")
    .End()
    .Build();

// 事件分支 / 实体（Fire 目标）：
var wave = OncMissionBuilder.Create("fire.waves")
    .SpawnEntity("enemy1", "敌方步兵", 8f, 5f, 50, 0, "Enemy")
    .WaitEntityDestroyed("enemy1")
    .Print("第一波击退！")
    .End()
    .Build();

// 自定义回调节点：
OncMissionBuilder.Create("custom.scripted")
    .Custom(ctx => { ctx.Raise("done"); })
    .End()
    .Build();
```

Builder 自动按添加顺序连线（前一节点 → 当前节点）；分支/汇合用
`OncNode.Or(eventId, target)` / `OncNode.ToNode(id)` / `OncNode.FromNode(id)` 显式补充。

## 六、运行时 API（OncMissionRuntime）

```csharp
var runtime = new OncMissionRuntime(mission, host);   // host 可为 null（纯逻辑跑）
runtime.OnNodeEntered += (rt, node) => ...;           // 订阅生命周期
runtime.Start();                                      // 启动
runtime.Update(dt);                                   // 每帧驱动
runtime.Raise("enemy.killed");                        // 喂事件（驱动 WaitFor/Branch）
runtime.CompleteObjective("survive");                 // 目标 API
runtime.SetObjectiveProgress("survive", 150);
runtime.AddObjectiveProgress("survive", 1);
var left = runtime.TimerRemaining("t");               // 计时器查询
var sync = runtime.BuildSyncState();                  // 联机"同步序号"负载
```

- 生命周期事件：`OnStarted` / `OnNodeEntered` / `OnNodeCompleted` / `OnCompleted` / `OnFailed` / `OnCanceled` / `OnObjectiveChanged`
- 结束：`Complete()` / `Fail()` / `Cancel()`；任务变量 `Variables`（≈ StateGraph.Variables）

## 七、桥接（落到真实游戏）

### 接口 `IOncMissionHost`（Core 定义契约，游戏侧实现）

生命周期 / `LoadMissionScene` / `IsMissionUnlocked` / `ApplySeed` / `ShowNotification` /
`PrintTeleprinter` / `SendSceneNotification` / `SpawnEntity` / `MoveEntity` / `DamageEntity` /
`SetEntityState` / `TriggerImpact` / `IsEntityDestroyed` / `AddRequisitionPoints` / `AddShell` /
`AddPowderCharge` / `UnlockSceneObject`。继承 `OncMissionHostAdapter`（空实现）只覆写需要的。

### `OncMissionBridge`（OpenNestCoop 游戏侧，默认宿主）

```csharp
OncMissionBridge.Register(mission);                       // 脚本注册
OncMissionBridge.RegisterFromJson(json);                  // JSON 注册
OncMissionBridge.Register(operation);                     // 战役注册（任务也注册）
OncMissionBridge.RegisterHost(myHost);                    // 覆写默认宿主
OncMissionBridge.Start("custom.defend");                  // 启动（检查前置解锁）
OncMissionBridge.Stop();
OncMissionBridge.Raise("enemy.killed");                   // 事件
OncMissionBridge.MarkMissionCompleted("custom.intro");    // 标记完成（前置解锁）
// 每帧由 CoopBehaviour 自动调 OncMissionBridge.Update(dt)
```

默认宿主能力矩阵：

| 节点动作 | 默认实现 | 说明 |
|---|---|---|
| `LoadMissionScene` | ✅ MapCard 匹配 → `ActivateMission()`；回退 `SceneManager.LoadScene` | 空 SceneName = 当前场景 |
| `PrintTeleprinter` / `ShowNotification` | ✅ 调 `Teleprinter.SubmitLines`+`TryStart` / `UINotificationManager.ShowNotification` | |
| `ApplySeed` | ✅ `FireMission.useFixedSeed/fixedSeed` | 任务内容随机一致 |
| `IsMissionUnlocked` | ✅ 检查 `mission.Requires` 是否都 `MarkMissionCompleted` | 前置/后置 |
| `SpawnEntity` / `MoveEntity` / `DamageEntity` | ✅ `FireMission.CreateMapEntity/MoveMapEntity` + `Entities` 读写 | ⚠️ X/Y 直接当世界坐标 (X,0,Y)，网格换算建议宿主覆写 |
| `IsEntityDestroyed` | ✅ `FireMission.Entities` 查 `!IsAlive || Health<=0` | 找不到 = 视为已摧毁 |
| `LoadMapSprite` | ✅ `MissionMapLoader.Acquire`（按 scene/MissionID 解析 MissionGraph） | 异步回调 Sprite；地图图（IronRoadMap/Topography）走游戏加载通道，勿用 `Resources.Load`（ResourcesModule 裁剪）；回调 `Il2CppSystem.Action<Sprite>` 用 `DelegateSupport.ConvertDelegate` 桥接 |
| `AddRequisitionPoints` / `AddShell` / `AddPowderCharge` / `UnlockSceneObject` / `SetEntityState` / `TriggerImpact` | ⚠️ 仅日志（占位） | 涉 AntiTamper/坐标/资源系统，建议宿主覆写精确实现 |

## 八、前置 / 后置任务

- **任务级**：`OncMission.Requires`（前置任务 id 数组）——`IsMissionUnlocked` 检查全部完成才可启动。
- **战役级**：`OncOperation.Missions`（`OncMissionRef { MissionId, Requires, Condition }`）——
  注册战役时把 `Requires` 合并到任务；任务完成（`OnMissionCompleted`）自动 `MarkMissionCompleted` 解锁后续。
- 原生任务完成也可 `OncMissionBridge.MarkMissionCompleted("原生任务id")` 作为前置。

## 九、联机同步（"同步序号"方案，预留）

按用户方案：**只同步序号**（不整图重放），保留后续接入：

```csharp
var state = runtime.BuildSyncState();              // DoneCount + ActiveNodeIds + 目标状态
string json = OncMissionIO.SaveSyncState(state);   // 小 JSON 负载
var back = OncMissionIO.LoadSyncState(json);
```

负载内容：`MissionId` / `IsFinished` / `IsSuccess` / `DoneCount`（已完成节点数）/
`ActiveNodeIds`（当前激活节点序号）/ `Objectives`（目标状态 + 进度）。后续可复用
`MissionSync`/`MissionEventSync` 的快照 + 事件思路接入 `CoopSyncRegistry`（`docs/API.md`）。
**本次不实现网络传输**，待任务文件规模评估后接入。

## 十、源码结构与使用步骤

```
src/OpenNestCore/Tasks/
├─ OncTask.cs            — 建模：OncNodeKind/OncNode/OncEvent/OncObjective/OncMission/OncOperation/OncMissionRef/OncMissionContext
├─ OncMissionBuilder.cs  — C# 脚本定义（fluent）
├─ OncMissionRuntime.cs  — 图状态机运行时 + 目标/计时器 + BuildSyncState
├─ IOncMissionHost.cs    — 桥接契约 + OncMissionHostAdapter 空实现
├─ OncJson.cs            — 轻量 JSON 解析/序列化（IL2CPP 安全）
└─ OncMissionIO.cs       — 任务/战役/同步负载 ↔ JSON
```

**模组接入步骤**：① 引用 OpenNestCore（或链接源码）→ ② 用 JSON 或 Builder 定义任务 →
③ `OncMissionBridge.Register(...)`（联机模组）或自行实现 `IOncMissionHost` 并驱动 `OncMissionRuntime` →
④ 启动任务、`Raise` 事件 → ⑤（可选）接入"同步序号"联机。

## 十一、游戏侧对接发现（UI 调研交接，2026-08-23）

来自「自定义任务」子代理的反编译调研（Cpp2IL diffable-cs，141073 方法，**比 `tools/dump_Assembly-CSharp.txt` 新**）。
与自定义任务直接相关的游戏侧事实：

### 11.1 反编译工具链（可复用）

- Cpp2IL（MLLoader 捆绑 2022.1.0-pre）：`& "$g\MLLoader\MelonLoader\Dependencies\Il2CppAssemblyGenerator\Cpp2IL\Cpp2IL.exe" --game-path $g --exe-name "Iron Nest Heavy Turret Simulator" --output-as diffable-cs --output-to "$env:TEMP\onnest_cs"`
  → 全量类/方法签名在 `$env:TEMP\onnest_cs\DiffableCs\Assembly-CSharp\`。
- diffable-cs **只有签名无方法体**；要方法体可 `--output-as dll_il_recovery` + ilspycmd（8.2 全局可用）反编译。
- ⚠️ `tools/dump_Assembly-CSharp.txt`（老 dump）过旧，缺 `MissionMapLoader`/`MissionSaveManager` 等运行时类——以 Cpp2IL diffable-cs 为准。

### 11.2 `MissionMapLoader`（地图图加载通道）

- `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` 单例：`Acquire(MissionGraph mission, bool topography, Action<Sprite> onLoaded)` +
  `Release(path)` + 引用计数缓存 `loadedSprites`；内部 `Resources.LoadAsync`（ResourceRequest）加载 `IronRoadMap`/`IronRoadTopography` 图。
- → 自定义任务要**地图图（目标地图/地形图）走 `MissionMapLoader`**，别自己 `Resources.Load`。
  ✅ 默认宿主已实现 `LoadMapSprite`（`OncMissionBridge.LoadMapSprite(missionId, topography, onLoaded)`，见第七节能力矩阵）。

### 11.3 `MissionSaveManager`（存档集成参照）

- 同样 runtime-init 单例：捕获/序列化 `MapTokens`/`Punchcards`（`PunchcardSaveData`）/`ValveController`/`MapEntity`/
  `ShellDefinition`/`DraggableItem`/`MissionSaveIdentity`/`ChildTransformSaveData`/`MissionGraphNotificationListener`。
- → 自定义任务做存档集成时参照它。

### 11.4 `ResourcesModule` 对模组不可用

- `UnityEngine.ResourcesModule` 在 BepInEx interop / unity-libs / Cpp2IL DummyDll / MelonLoader UnityDependencies **全部不存在**
  → 模组不能直接 `Resources.Load`（编译无引用 + 运行时 MissingMethodException 风险）。要走游戏内加载通道（如 `MissionMapLoader`）或运行时抓取。
- ✅ 但 **`Resources.FindObjectsOfTypeAll&lt;T&gt;()`（CoreModule 的静态方法）可用**——本框架默认宿主
  （`OncMissionBridge`）用它在场景里找 `MapCard`/`Teleprinter`/`FireMission` 等，不受影响；模组里 MissionSync 等也在用。

## 十二、限制 / 待办

- 图执行默认**单线程每帧驱动**；不支持宿主迁移/暂停（单机范围）。
- 默认宿主的坐标换算（网格 X/Y → 世界坐标）与资源发放（补给点/炮弹/发射药/解锁/着弹）为占位，
  需要精确逻辑时覆写 `IOncMissionHost`。
- 联机"同步序号"未接网络层（待任务文件规模评估）；`Custom` 节点回调不随 JSON 传输。
- 与游戏原生 MissionManager 结算/统计（`MissionStatsTracker`）未打通（自定义任务完成走
  `OnMissionCompleted` 通知，不写原生结算）。
