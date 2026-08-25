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
- 2026-08-23 新增「原生任务覆盖与范本」节（流程级 Harmony 挂钩：MissionManager.StartOperation/LoadMission 拦截 + MissionGraph.OnMissionLoaded 协同事件 + 统一任务 ID + 范本）。
- 2026-08-23 新增外部 `CSM` 文件夹按文件名序读取 JSON 任务/战役（`OncMissionBridge.LoadFromFolder` / `LoadFromGameFolder`，`CoopRuntime.Startup` 自动调用）。
- 2026-08-23 新增 `examples/csm/` 参考样板（4 个纯新增任务：defend/killwave/branch/campaign，已做结构+运行时校验）。
- 2026-08-23 新增完整综合样板 `examples/csm/01_04_full_operation.json`（并行竞争：击杀线 vs 限时倒计时，成功/失败双结局，已校验）。
- 2026-08-23 新增**选任务面板脚本生成原生任务卡片**（`OncMissionCardInjector`：patch MapCardManager.UpdateMapCards + MapCard.ActivateMission，**克隆原生模板卡片作 3D 骨架**创建与原生同构的 3D 卡片，清原生绑定/改标题/定位/拦截点击启动自定义任务；不继承原生解锁/完成/奖章状态）。
- 2026-08-23 卡片位置/属性支持从任务脚本 `Card` 配置读（`X/Y/Width/Height/TitleColor/Background`，十六进制颜色），非自动布局用 `Card` 定位。
- 2026-08-23 修订 11.4：`Resources` 类在 Unity 6000 并入 `UnityEngine.CoreModule`，双端 csproj 已引用 → `Resources.Load` 编译/运行都正常（无 MissingMethodException）；不存在的是**独立程序集** `UnityEngine.ResourcesModule.dll`（不能显式 Reference）；真正限制是按名 Load 拿不到（实测候选名全 null）→ 地图图仍走 `MissionMapLoader`。
- 2026-08-23 11.4 补运行时实测：`Resources.Load&lt;GameObject&gt;`（AchievementDisplay 3 图）/`LoadAsync` 双端成功；`Resources.Load&lt;Sprite&gt;` 按名全 null；`Resources` 类经 ilspycmd 确认在 `UnityEngine.CoreModule.dll`；独立 `ResourcesModule` 程序集双端 interop 目录均无。
- 2026-08-23 11.4 再验证（防"画靶"）：候选 sprite 名（UIElement8px/UIFoldoutOpened/UIFoldoutClosed/UICheckMark/IronRoadMap）经 UnityPy 扫 `resources.assets`（287 Sprite）**确认真实存在**，但运行时同步 `Resources.Load&lt;Sprite&gt;` 对它们仍返回 null；同步 `Resources.Load&lt;GameObject&gt;` 与 `LoadAsync` 均成功 → 限制仅在"同步按名加载精灵"。
- 2026-08-23 11.4 **最终裁决**（运行时对照）：`Load('IronRoadMap')` 泛型 + **非泛型都 null**（IronRoadMap 是根目录真实资源，`LoadAll` 同名可拿到）→ 与路径/泛型实例化无关；`LoadAll&lt;Sprite&gt;`（233 个）与 `LoadAsync` 正常；`Load` 对 GameObject 正常 → 同步 `Resources.Load` 按名加载 **Sprite** 在 IL2CPP 下返回 null，`LoadAll`/`LoadAsync` 才是可用通道；UIElement8px 等连 `LoadAll` 也不在 Resources 索引（对象在文件里但未纳入可加载索引）。
- 2026-08-24 新增「原生 MissionImporter 格式（csm_native）」节：直接写**游戏原生 `MissionImporter` JSON 格式**（NodeType=原生 State_* 名 / NodeData=节点字段 / Connections=连线），经 `ImportMission` 构造完整原生图 + `StartOperation` 运行——打字机/引擎/任务完成全原生行为。记录关键字段语义（`OnlyQueue`/`WaitUntilComplete`/`waitForTrigger`/`EngineStart`）与导入坑（ExportPackage 包装、TextIdentifier repair）。
- 2026-08-25 13.3 修正引擎供电：自定义任务引擎 running 但 `EnginePowerController.Power` 卡 0.284（原生 0.490 上升）——根因是 running 为场景默认值未走完整启动流程（false→true 边沿才升功率）。修复：`EngineStart="on"` 时调 `DieselEngineStateRelay.ForceEngineOn()`（public）触发完整启动 → Power=0.490 + Power Lever 保持抬起位 + 面板灯全绿（与原生一致）。证伪：设 Dial 到 target 熄火、点击 Power Lever 拨到拉下位。新增原生任务引擎参考捕获 `ScheduleNativeEngineRef`（进原生任务 3s/8s/13s 采样对比）。
- 2026-08-25 13.3 二轮修正：`ForceEngineOn` 会**联动拨动 Power Lever 到拉下位**（用户否决拉下位，要抬起位+有电）。最终方案 = 保电重启（ForceEngineOff→1.5s→ForceEngineOn 触发完整启动边沿升 Power）→ 5s 复查 Power≥0.4 → `RestorePowerLeverUp`（`InterpolatedTransformController.SetInterpolantImmediate(0)` 拨回抬起位）→ 保电监控（每 3s 查 Power 掉回再 ForceEngineOn）。记录 Power Lever 位置联动供电（点击→interp 0→1→Power 升）。
- 2026-08-25 13.3 结论定案：守卫修复后**原生场景本身无引擎供电 bug**（用户实测正常）——之前"断电"是场景问题（守卫缺失导致干预泄漏到原生任务），非引擎机制问题。`sample.defend` 换 **Chill 场景**（`SceneName="Mission Chill"`）测试 + **移除 `EngineStart`**（用户"不用干预了"，用场景默认引擎状态，不写字段即完全走原生流程）。
- 2026-08-25 新增**原生场景列表 dump**：`OncMissionCardInjector.PostUpdateMapCards` 在选任务面板输出 `[OncCard] scene-list ...`（每张原生 MapCard 的 mission + SceneReference.sceneName）——自定义任务 `SceneName` 填对应值即进对应场景。实测：教程 1~4 → `Mission tutorial 1~4`；`Chill` → **`Mission Chill`**；`ChallangeFDC` → `Mission Challenging`；其余战役 → `MissionBase`（SceneReference 未列动态场景）。
- 2026-08-25 新增**原生生成节点取证 + 敌人生成修复**（13.5 节）：`DumpSpawnNodes` dump 当前图所有 `State_SpawnMapEntity` 完整字段（Role/Health/NumberToSpawn/LocationToSpawn.ZoneID 等）；原生 SiegeOfCartagena 13 生成节点取证（`Role` 复合枚举 + `zone='EnemyFrontline'` 等场景预置 Zone 名 + `fuzzy`）。**图启动修复**：`StartOperation` 后 `StartMissionRuntime` 不建立主执行线（CurrentState null）→ `OncMissionHooks.PostMissionLoaded` 对 native 自定义图调 `graph.Run()` 启动。**`State_Objective` 中断图**：NodeData 缺 `Objective` 引用 → 图无法推进到生成节点；killwave 已移除 Objective 节点 → `State_SpawnMapEntity` 正常执行（`entities=5`，`complete=True`）。
- 2026-08-25 联机同步接入（`MissionSync` 102 / `MissionSyncV2` 227）：主机广播自定义任务 `@c:<MissionID>` 前缀（`GetMissionId` 对 `IsNativeCustomGraph` 图返回前缀 ID），客机 `TryLoadMissionScene` 对 `@c:` 走 `OncMissionBridge.StartNative(id)`——按 ID 启动，**不再模拟点击原生 MapCard**（自定义任务 scene 名如 "Mission tutorial 4" 与原生同名会撞卡片进错任务）；主机广播任务图当前节点 nodeId（原生图 `CurrentState.Node.NodeID`，同步序号）；主机收到 `@c:` 不覆盖 `CurrentMissionSceneName`（防缓存污染）。Core：`OncMissionSyncState` 补 `DoneNodeIds` + `OncMissionRuntime.ApplySyncState` 闭环 `BuildSyncState`（Core 引擎任务联机用）。
- 2026-08-25 修复**自定义任务 HUD 泄漏到原生任务**（13.6 节）：自定义任务信息窗口（`OncMissionHud`，`UiKit` Canvas sortingOrder=32000、DontDestroyOnLoad、右下角）只应在自定义引擎任务（`Start`/`StartRuntimeInScene` 的 `_current` runtime `IsRunning`）时显示——若自定义引擎任务**中途退出未正常完成**（runtime 未 Stop），`_current` 残留 `IsRunning`，HUD 每帧 `Refresh` 持续显示；之后进入原生任务时 HUD **错误弹出**（`_current == null` 分支的 `Hide()` 不会执行）。修复：`OncMissionBridge.Update` 在刷新前加**离开任务守卫**——`_current.IsRunning` 但 `MissionManager.CurrentPhase != (GamePhase)2`（任务中）→ 调 `Stop()`（内部清 `_current` + `OncMissionHud.Hide()`）后 return。验证：自定义任务运行中（phase=2）守卫不触发、HUD 正常；退出到选任务/主菜单（phase≠2）→ 立即 Stop + Hide → 原生任务不再弹出。


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
③ 注册：代码 `OncMissionBridge.Register(...)`，或**直接把 JSON 放到游戏根目录 `CSM/` 文件夹**（启动自动按文件名序读取）→
④ `OncMissionBridge.Start(id)` 启动任务、`Raise` 事件 → ⑤（可选）接入"同步序号"联机。

### 10.1 外部 CSM 文件夹（自动加载）

- **位置**：`<游戏根目录>/CSM/*.json`（游戏根 = `Application.dataPath` 上一级）。启动时 `CoopRuntime.Startup` 自动调
  `OncMissionBridge.LoadFromGameFolder()` 读取注册。
- **按文件名序**注册（`Array.Sort` 字典序）；自动识别战役（JSON 含 `"Missions"`）vs 任务。
- 手动：`OncMissionBridge.LoadFromFolder(path)`（任意路径）/ `LoadFromGameFolder("CSM")`。
- 单个文件解析失败只跳过不中断；文件夹不存在静默（返回 0）。

> 📁 **参考样板**：`examples/csm/` 提供 5 个**纯新增**（不覆盖原生任务）的可直接复制样板——
> `01_01_defend.json`（顺序+目标+计时）、`01_02_killwave.json`（地图实体击杀波次）、
> `01_03_branch.json`（事件分支+超时）、`01_04_full_operation.json`（**完整综合**：限时/并行竞争/奖励/双结局）、
> `02_campaign.json`（前置/后置战役）；均通过结构 + 运行时模拟校验。
> 复制到游戏根目录 `CSM/` 即自动按文件名序注册（详见 `examples/csm/README.md`）。

**执行链（现状）**：任务 JSON（或 Builder）→ 注册进内存表（`Register`/CSM 文件夹）→ `Start(id)` 建
`OncMissionRuntime` → `CoopBehaviour.Update` 每帧 `OncMissionBridge.Update(dt)` 驱动节点流转 → 节点动作经
`IOncMissionHost`（默认 `OncMissionBridge` 宿主）落到游戏。**不是运行时实时读文件**，启动/注册时一次性读入内存。

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

### 11.4 `ResourcesModule` 程序集不存在，但 `Resources` 类（CoreModule）可用

- ⚠️ **不存在的是独立程序集 `UnityEngine.ResourcesModule.dll`**（BepInEx interop / unity-libs / Cpp2IL DummyDll /
  MelonLoader UnityDependencies 里都没有；2026-08-23 已逐一列出双端 interop 目录确认）→ csproj **不能显式
  `<Reference Include="UnityEngine.ResourcesModule">`**。
- ✅ **`Resources` 类在 `UnityEngine.CoreModule.dll`**（ilspycmd 反编译确认：含 `Load`/`LoadAsync`/`LoadAll`/
  `FindObjectsOfTypeAll`/`GetBuiltinResource` + `ResourcesAPI`）。双端 csproj 已引用 CoreModule → `Resources.Load&lt;T&gt;()`
  编译正常。
- ✅ **运行时实测（G/D 双端一致，2026-08-23，最终裁决）**：
  - 候选 sprite 名（UIElement8px/UIFoldoutOpened/UIFoldoutClosed/UICheckMark/IronRoadMap）已用 **UnityPy 扫
    `resources.assets`（287 个 Sprite）确认真实存在**
  - **`Resources.Load`（同步）对 Sprite 全 null**——泛型 `Load&lt;Sprite&gt;` **和非泛型 `Load(string)` 都返回 null**，
    即便 `IronRoadMap` 是根目录真实资源（`LoadAll` 同名能拿到）→ **与路径/泛型实例化无关**
  - **`Resources.LoadAll&lt;Sprite&gt;("")` 正常**：返回 233 个（含 IronRoadMap 及全部地图图）→ 批量加载是可用通道
  - **`Resources.Load` 对 GameObject 正常**：`AchievementDisplay`（泛型 + 非泛型都 OK，3 张 Image）
  - **`Resources.LoadAsync&lt;Sprite&gt;` 正常**：返回非 null `ResourceRequest`
  - 子目录 LoadAll（UI/Sprites/Images/Icons）全 0；UIElement8px 等 UI 素材连 `LoadAll` 也不在 Resources 索引
    （对象在 `resources.assets` 文件里但未纳入 Resources 可加载索引，应走图集/直接引用通道）
  - 全程 **无 MissingMethodException**
- ⚠️ **结论**：`Resources` API 完全可用；**唯一限制 = 同步 `Resources.Load` 按名加载 Sprite 返回 null**
  （泛型/非泛型均然，与路径无关；非 Sprite 如 GameObject 正常）。**模组取 Sprite 用 `LoadAll&lt;T&gt;`（按 `.name` 匹配）或
  `LoadAsync&lt;T&gt;`**；地图图仍走 `MissionMapLoader.Acquire`（见 11.2）。
- ✅ **`Resources.FindObjectsOfTypeAll&lt;T&gt;()`（CoreModule 静态方法）可用**——本框架默认宿主
  （`OncMissionBridge`）用它在场景里找 `MapCard`/`Teleprinter`/`FireMission` 等，不受影响；模组里 MissionSync 等也在用。

## 十二、原生任务覆盖与范本（Harmony 流程级挂钩）

用户需求：**修改/覆盖原生任务 + 统一任务 ID + 做自定义任务范本**。
粒度=**流程级**（只 patch 流程方法，**不碰 StateNode/EventNode 状态机内部**——任务状态机 patch 有进任务崩溃教训，见记忆）。

### 12.1 统一任务 ID 与覆盖注册

- `OncMissionBridge.RegisterOverride(nativeKey, custom)`：`nativeKey` = 原生 MissionID 或场景名；该原生任务启动
  （点卡片 `ActivateMission`→`StartOperation` / `LoadMission`）时**改为启动自定义任务**。
- `OncMissionBridge.ResolveMission(keyOrId)`：统一解析（override 映射 → 注册表 id → 场景匹配）。
- 未注册覆盖的原生任务一律放行（无副作用）。

### 12.2 挂钩实现（`OncMissionHooks`）

- patch `MissionManager.StartOperation` / `LoadMission`（prefix）：被覆盖 → `TryStartOverride` 启动自定义任务 + `return false`
  阻止原生；未覆盖 `return true` 放行。防环：自定义任务已在跑不重复启动。
- patch `MissionGraph.OnMissionLoaded`（postfix）：原生任务图加载完成 → `OncMissionBridge.Raise("native.mission.loaded[.<id>]")`，
  自定义任务可 `WaitFor`（叠加模式）。
- ⚠️ 只 patch 流程方法，不碰任务状态机内部（记忆教训：SignalAlarm patch 曾致进任务崩溃）。

### 12.3 自定义任务范本

- `OncMissionBridge.CreateTemplateFromNative(nativeKey)`：读原生 MissionGraph（按 MissionID/场景名从
  `MissionManager.CurrentMission` / 场景 `MapCard` 解析）→ 生成 `OncMission` 骨架（统一 Id + SceneName + DisplayName），
  模组在此基础上用 Builder 加节点。

```csharp
// 1) 以原生任务为范本
var tpl = OncMissionBridge.CreateTemplateFromNative("Mission tutorial 2");
if (tpl == null) return;
// 2) 基于范本构建自定义任务（改目标/加节点）
var custom = OncMissionBuilder.Create(tpl.Id).Named("自定义坚守").InScene(tpl.SceneName)
    .Print("这是覆盖原生任务的范本！").End().Build();
// 3) 注册覆盖：点原生任务卡片/加载时改为启动 custom
OncMissionBridge.RegisterOverride("Mission tutorial 2", custom);
// 4) 叠加模式：自定义任务等待原生任务图加载
//    WaitFor("native.mission.loaded.Mission tutorial 2")
```

### 12.4 限制

- 覆盖后原生任务图不执行（被阻止），改由自定义任务图驱动；与 `MissionSync` 原生任务联机同步**不混用**
  （两端一致注册 override 才行为一致）。
- 仅流程级挂钩；若需节点级（`StateNode.OnEnter`）挂钩需另评估（任务状态机 patch 风险）。

### 12.5 选任务面板（克隆原生卡片，创建同构 3D 卡片）

自定义任务要出现在**游戏选任务面板**。**反编译结论（Cpp2IL ISIL）**：原生卡片是**场景预置的 3D 世界对象**
（组件 = `MapCard` + `Interactable`，无 Canvas/UGUI）——`MapCardManager.UpdateMapCards` 只把战役任务绑定到
场景卡片并刷新解锁/完成/奖章状态，**全程序集无 `AddComponent<MapCard>`/`new` 卡片的运行时调用点**（UGUI 卡片
在无 Canvas 的 3D 场景不渲染）。因此：
- `OncMissionCardInjector`：patch `MapCardManager.UpdateMapCards`（postfix）——**Instantiate 克隆一张原生模板
  卡片**（第一张原生 MapCard）作 **3D 骨架**（保留背景 3D 网格 + TMP 标题 + `MapCard`/`Interactable` 组件与点击链），
  然后：**清除原生 `Mission`/`Campaign` 绑定**（不继承原生任务/状态）→ 改标题/描述 → 隐藏奖章/检查点图标 →
  定位到模板下方 → 命名 `OncCustom_<id>` 标记。点击走原生 `Interactable` → `ActivateMission`，
  patch `MapCard.ActivateMission`（prefix）**拦截自定义卡片** → `OncMissionBridge.Start(id)` + `return false`（原生放行）。
- **卡片位置/属性从任务脚本读**：`OncMission.Card`（JSON `"Card"`：`X/Y/Width/Height/TitleColor/Background`，颜色
  十六进制 `#RRGGBB`/`#AARRGGBB`）。3D 语义：`X/Y` = 相对模板的**世界偏移**（Y 正往下），`Width/Height` = **缩放系数**
  （1 = 模板原大小），`TitleColor` 设标题色；`Background` 因 3D 背景材质共享暂不直接改（保留模板样式）。
  示例见 `examples/csm/01_01_defend.json`。
- 参考原生卡片世界间距自动下排（取前两张 active 卡片 y 差，回退 0.35）；按日志
  `[OncCard] built N custom 3D card(s) ...` 调 `Card`。
- 点击走 `OncMissionBridge.Start`（含前置解锁检查/场景加载/seed）。

## 十三、原生 MissionImporter 格式（csm_native）—— 2026-08-24 实测

> **定位**：前文（一~十二）是 Core 抽象框架（`OpenNestCore.Tasks`）的自定义格式（`Id/DisplayName/Kind`）。
> 本节约 **游戏原生 `MissionImporter` JSON 格式**（`examples/csm_native/`）：直接用原生 `State_*` 节点名
> （`NodeType`）写图，经 `MissionImporter.ImportMission(json)` 构造**完整原生 MissionGraph**，再
> `StartOperation` 运行——打字机/引擎/任务完成/床交互全**原生行为**（无黑屏/NRE）。

### 13.1 启动链路（`OncMissionBridge.StartNative`）

```
JSON (CSM/*.json) → RegisterNativeFromJson → 卡片点击 → StartNative(id)
  → OncMissionImporter.Import(json)  [WrapExportPackage: 裸 JSON → {MissionName, MissionJson, Files:[]}]
  → RepairImportedTexts  (TextIdentifier 回填)
  → graph.SceneReference = JSON SceneName
  → graph.MissionDescription = JSON MissionDescription
  → ScheduleEngineStart  (可选 EngineStart="on"/"off")
  → new OperationGraph + MissionNode → mm.StartOperation(op, graph)  [2-arg，无 MissionSaveData]
```

### 13.2 关键字段语义（反编译 + 实测确认）

| 字段 | 语义 | 值 |
|---|---|---|
| `SceneName` | 任务场景名（实测值：教程 `"Mission tutorial 1~4"` / **Chill `"Mission Chill"`** / Challange `"Mission Challenging"`；其余战役 `MissionBase`；选任务面板日志 `[OncCard] scene-list` 可查完整列表） | 字符串 |
| `MissionID` / `MissionName` / `MissionType` | 标识 / 显示名 / 类型（Campaign/Chill/Challange/Tutorial） | 字符串 |
| `EngineStart` | **开局引擎状态**（可选，2026-08-24 新增）：`"on"`=点火启动 / `"off"`=停机。**不写该字段 = 不干预**（用场景默认；`sample.defend` 已移除该字段） | 字符串/布尔 |
| `Zones` | 生成区（Allied=炮台 Role 2 / Enemy=目标 Role 1） | 数组 |
| `Nodes` | 图节点（`ID`+`NodeType`+`NodeData`+`Connections`） | 对象 |

**TeleprinterText 节点（打字机简报）**——OnEnter 行为由 JSON 配置决定：

| 字段 | 原生 | 说明 |
|---|---|---|
| `OnlyQueue` | **false**（要出字）| `true` = 只入队不直接打（等驱动）→ 打字机"响但没字" |
| `WaitUntilComplete` | **false** | `true` = 任务等打印完成 → 卡任务（床不可交互）|
| `Text` | `{"Raw":...,"Key":...}` | TextIdentifier；ImportMission 反序列化成 Object → 需 `RepairImportedTexts` 回填 |

**导入坑（实测）**：
- 裸 MissionDefinition JSON → ImportMission 走退化路径 → TextIdentifier 还原成 Object → 简报不显示。
  **必须 `WrapExportPackage`** 包装成 `{MissionName, MissionJson: 字符串, Files: []}`。
- ImportMission 硬编码场景为 `MissionBase`（F3 调试空场景）→ 手动从 JSON 读 `SceneName` 覆盖
  `graph.SceneReference`，否则进空场景。
- `EntityIDToReplace` 为 null → OnEnter 遍历 NRE（打字机不驱动）→ 补空 `List<StringReplacement>`。

### 13.3 引擎开局状态控制（2026-08-24 新增；2026-08-25 修正供电）

**原生机制**：开局引擎状态 = 场景中 `DieselEngineController` 组件的 `forceEngineOn`/`forceEngineOff`
（Unity Inspector 序列化字段）+ `RestoreMissionState(DieselEngineSaveData.EnginesRunning)` 存档恢复。
**不是 MissionDefinition JSON 字段**——原生任务靠场景组件配置 + 存档。

**自定义任务**：`StartNative` 用 **2-arg `StartOperation(op, graph)`（无 MissionSaveData）→ 不恢复存档**
→ 引擎按场景默认（forceEngineOn 配置）。要在自定义任务覆盖开局状态：
- JSON 可选字段 **`EngineStart`**：`"on"`（点火）/ `"off"`（停机）。
- 场景加载后（引擎对象就绪）`TryApplyEngineStart` 找到 `DieselEngineController`，
  `want=="on"` → **`DieselEngineStateRelay.ForceEngineOn()`**（见下）；`want=="off" && running` → `ShutdownEngine()`。
- 不写该字段 = 不干预（用场景默认），原生任务不受影响（只对 `IsNativeCustomGraph` 的图生效）。

**⚠️ "断电"根因与修复（2026-08-25 实测）**：
- 现象：自定义任务引擎 `EnginesRunning=True` 但 `EnginePowerController.Power` 卡 **0.284**（原生 0.490）。
  用户看"主电源抬起位没电"；拉下 Power Lever 才来电，但默认应抬起位 + 有电。
- 根因：引擎 running 是**场景默认值**（没走完整启动流程）。`EnginePowerController.Update()` 的
  `cur` 只在引擎 `EnginesRunning` **false→true 边沿**才用 `riseSpeed`(0.1/s) 向 target 插值上升；
  target = `dieselEngine.FuelMixtureSystemValue`(0.490)。原生引擎"真启动"（cur 升到 0.490），
  自定义 running 是默认值（cur 卡 0.284 不升）。
- ❌ 证伪方案：①设 Dial 到 target(0.900/0.750) → 系统值漂移超运行范围(0.330~0.650) → `fuelRun=False` 熄火；
  ②点击 Power Lever（LookAtTarget.OnClickDown/Up）→ 拨到拉下位（视觉错误，用户明确否决）；
  ③设 `_Power_k__BackingField=1` → Update 每帧覆盖。
- ✅ **修复（二轮最终，保电重启 + 拉杆复位）**：
  1. `DieselEngineStateRelay.ForceEngineOff()` → 等 1.5s → `ForceEngineOn()`（public；场景有 2 个继电器：
     `'EngineOff - EngineOn  Mission graph notification'` + `'DieselEngineStateRelay'`）→ 触发完整启动边沿
     （EnginesRunning false→true）→ Power 升满（0.490）。
  2. 5s 后复查 Power ≥0.4 → **`RestorePowerLeverUp()`**：Power Lever 下 `SwitchControler`
     （`InterpolatedTransformController`，target='Power Lever'）的 `SetInterpolantImmediate(0)` 拨回**抬起位**
     ——因为 `ForceEngineOn` 会**联动拨动 Power Lever 到拉下位**（用户否决拉下位）。直接设插值不触发点击/供电逻辑。
  3. 保电监控（每 3s 查 Power，掉回 <0.4 再 `ForceEngineOn`）维持供电稳定。
- **Power Lever 位置联动供电（实测）**：点击拉杆 → `SwitchControler` interp 0→1（拉下位）→ Power 升
  （0.284→0.384）。原生任务 Power Lever 抬起位（interp=0）+ Power=0.490（有电）——**拉杆位置不是断电根因**，
  差异在引擎是否走完整启动流程。
- **⚠️ 已知遗留**：保电重启期间（开局 ~6s）Power 从 0 升到 0.490，引擎短暂红灯。若需开局立即满电需深入研究引擎启动机制。

**✅ 最终定案（2026-08-25，用户实测确认）**：换 **Chill 场景**（`SceneName="Mission Chill"`）+ **移除 `EngineStart`**（不写字段 = 完全不干预，走原生流程）后——**Chill 场景天然正常：有电 + 拉杆抬起**，无需任何干预。之前 Mission tutorial 4 的"断电"是**该场景守卫缺失**（干预泄漏）所致，非引擎机制/所有场景通病。`sample.defend` 已定案为 Chill 场景 + 无 EngineStart；保电重启逻辑保留为可选能力（JSON 写 `EngineStart` 才启用）。注意：Chill 场景 `EnginePowerController.Power` 数值仍显示 0.283（场景默认 running 未走启动边沿），但用户实测体验正常（灯绿/引擎运行/可操作），该数值不影响实际使用。

**运行时操作方法**（`M3EnvSync` 已用于联机同步，证实可用）：
`AttemptIgnition()` 开机（需 fuelStartOk）/ `ShutdownEngine()` 关机(private) / `EnginesRunning` 读状态 /
`forceEngineOn` 强制开 / `DieselEngineStateRelay.ForceEngineOn/ForceEngineOff/ToggleEngine`（完整启动/停机，含供电，
**会联动拨动 Power Lever**）/ `InterpolatedTransformController.SetInterpolantImmediate(0)`（直接设拉杆位置，不触发供电）。

### 13.4 打字机行为由节点 JSON 配置决定（用户澄清，2026-08-24）

- **有的任务开局等玩家靠近才触发打字，有的则不是** → 行为由每个节点的 `OnlyQueue`/`WaitUntilComplete`
  配置决定，不是统一逻辑。**不要**在 OnEnter postfix 里统一 TryStart 干预（会覆盖节点配置、破坏原生行为）。
- 原生 OnEnter 自己驱动：`SubmitLines` + `TryStart(false)`（带初始延迟/等靠近）或直接打，由 JSON 决定。

### 13.5 敌人生成 / 任务中途生成 / 任务节点（2026-08-25 修复）

**原生生成节点取证**（`DumpSpawnNodes` dump 当前图所有 `State_SpawnMapEntity` 完整字段；原生 SiegeOfCartagena 13 节点）：
- `Role` 是**复合枚举**：`EnemyGroup3, Infantry` / `Ally, Infantry` / `Target, EnemyGroup2, Fortification` / `Spotter`；
  `state=Hidden` 用于伏击/目标（隐藏起点）。
- 生成位置 `LocationToSpawn`：`locType=Zone` + `zone='EnemyFrontline'/'AlliedFrontline'/'EnemyBackline'/'TurretSpawn'`
  （**场景预置 Zone 名**）+ `fuzzy=True`（模糊随机）。`LocationTypes` 枚举：`GridLocation=0 / Zone=1 / ContextLocation=3 / ContextEntity=4 / Relative=5`。
- `Zone` 是 `Il2CppSystem.Object`（非组件），从 `MissionGraph.Zones` 访问；`ImportMission(importZones=true)` 导入 JSON Zones。

**⚠️ 图启动修复**：`StartOperation`（2/3-arg 带空 checkpoint）后 `StartMissionRuntime` **不建立主执行线**
（`CurrentState=(no state)`）→ 原生点卡片走 `LoadMission`→`StartMissionRuntime` 启动图。修复：
`OncMissionHooks.PostMissionLoaded`（`OnMissionLoaded` 触发后，场景就绪）对 native 自定义图（`IsNativeCustomGraph`）
调 **`graph.Run()`** 建立主执行线。⚠️ `StartOperation` 的 Harmony patch 因重载歧义失败（2-arg+3-arg Ambiguous），
`AccessTools.Method` 需按参数区分。

**⚠️ `State_Objective` 中断图**：NodeData 只有 `{"NodeID":"..."}` 无 `Objective`（ObjectiveGraph）引用 →
图进 n2（打字机）后**无法推进到 s1**（生成节点不执行）。killwave 已移除所有 `State_Objective` 节点 →
图正常推进（`State_SpawnMapEntity.OnEnter` 执行、`entities=5`、`complete=True`）。自定义任务要么给
`State_Objective` 配完整 `Objective` 引用，要么不用该节点。

**⚠️ `StartRuntimeInScene` 干扰**：`PostMissionLoaded` 对 native 格式任务误启 Core 抽象引擎（占位 mission 无节点）
→ 干扰原生图。已移除（native 格式只修文本 + Run 图，不启 Core 引擎）。

**验证（killwave 移除 Objective 后）**：
```
State_SpawnMapEntity.OnEnter node='s1' id='sample_enemy_' num=1 role=Enemy, Target hp=50 zone='Enemy'
flow: main='w1'(State_WaitEntityDestroyed) side=[...] entities=1   ← 挂起等摧毁
s1/s2/s3 全部执行，entities=5，complete=True（推进到 e2 End）       ← 中途生成 + 图节点正常
```

**✅ 一波击杀刷新下一波（2026-08-25 修复）**：
- `State_WaitEntityDestroyed.Entites`（TargetSelection）为 null → OnExecute 立即通过（不等待）。JSON 配：
  - s1/s2/s3（Spawn）加 `"SetContextVariable":true,"LastSpawnedEntity":0`（EntityContextKeys.EntityTarget=0）→ 生成后把实体存上下文键
  - w1/w2/w3（Wait）加 `"Entites":{"SourceType":0,"ContextKey":0,"CountType":0,"Count":0}`（FromContext+EntityTarget+All）→ 等待该实体摧毁
- 验证：`flow: main='w1'(State_WaitEntityDestroyed) entities=1 stay=23.6s`（挂起等击杀，击杀后 `w1→w2→w3` 推进）
- **TargetSelection 结构**：`SourceType`（FromContext=0/FromFilter=1）、`ContextKey`（EntityContextKeys）、`Filter`、`CountType`（All=0/Count=1）、`Count`、`SortType`

**✅ 打字机坐标 token 播报（2026-08-25 修复）**：
- 原生 token 机制：`FireMissionTokenProcessor.ProcessBlock(text)` 正则替换 `[GRID <key>]`/`[POINT <key>]`/`[REMAINING <key>]`/`[TIMER <MissionTime>]`/`[BEARING a b]`/`[DISTANCE a b]`。
  `[GRID <turret>]`=铁巢坐标（TryResolveSpecialPosition）；`<key>`=实体 ID/上下文键（LineEvalContext.SelectedIds 从 FireMission 实体按 ID 查）。
- **原生 OnEnter 对 ImportMission 图不自动替换 token** → `PreTeleprinterNodeEnter`（HarmonyPatches，只对 `IsNativeCustomGraph` 图）手动调 `ProcessBlock` 替换后设回 `__instance.Text` → OnEnter 打印坐标文本。
- 验证：`TokenReplace BEFORE='...铁巢位置 [GRID <turret>]...' AFTER='...铁巢位置 B2 5:5，敌人位置 I5 8:2'` ✅
- ⚠️ `ProcessBlock` 返回 Il2Cpp `List<string>`（不能 `string.Join`，需手动索引读）；interop List 不实现 .NET IEnumerable（`as` 失败）

**⚠️ 击中没有击杀反应（2026-08-25 修复）**：
- **根因**：`State_SpawnMapEntity` 的 `Role` 若用纯 `Enemy(1)`，**不被 `ImpactTracker.EvaluateImpact` 识别为炮击目标** → 炮弹命中位置但敌人不掉血（`EvaluateImpact n=1 loc=(8.5,4.5)` 无后续 Destroyed）。
- **修复**：`Role` 改原生可击杀目标复合值 **`Target(0x20)+EnemyGroup3(0x201)+Infantry(0x20000)`=131617** + `Health` 改 **1**（原生 `EnemyInfantry` hp=1）。`MapEntity` 有 `Role/MaxHealth/Health/IsAlive`。
- 验证：`enemywavea hp=0 alive=False state=Destroyed`，波次推进 `w1→w2→w3` ✅
- ⚠️ `EnemyGroup3` 组大小=3 → 每波生成 3 个实体（`enemywavea#1/2/3`）即使 `NumberToSpawn:1`；每波 1 个需去 Group role（如 `Target, Infantry`）
- ⚠️ 实体 ID 末尾数字被原生当"生成序号"剥离（`EnemyWave1`→`EnemyWave`）；用无数字结尾 ID（`EnemyWaveA`）

**遗留**：每波敌人数量（EnemyGroup 组大小）；三波敌人位置较近（共用 `ZoneID` 区）。可后续优化。

### 13.6 自定义任务 HUD 泄漏到原生任务（2026-08-25 修复）

**现象**：自定义任务右下角的信息窗口（`OncMissionHud`）在**原生任务**里也弹出。

**根因**：`OncMissionHud` 只在自定义引擎任务（`OncMissionBridge.Start`/`StartRuntimeInScene` 启动的 `_current` runtime `IsRunning`）时显示（`Show` 于两处、`Update` 每帧 `Refresh`）。但若自定义引擎任务**中途退出未正常完成/失败**（runtime 未走 `OnMissionCompleted`/`OnMissionFailed`/`Stop`），`_current` 残留 `IsRunning` → HUD 每帧继续显示；`Update` 里 `_current == null || !_current.IsRunning` 分支的 `Hide()` 不会执行 → **之后进入原生任务时 HUD 错误弹出**（原生任务 phase 也是 2，原逻辑无法区分）。

**修复**（`OncMissionBridge.Update`）：刷新前加**离开任务守卫**——`_current.IsRunning` 但 `MissionManager.CurrentPhase != (GamePhase)2`（已不在任务中，回到主菜单/选任务）→ 调 `Stop()`（内部清 `_current` + `OncMissionHud.Hide()`）后 `return`。

```csharp
if (_current == null || !_current.IsRunning)
{
    OncMissionHud.Hide();  // 未运行：防残留
    return;
}
// 离开任务守卫：phase≠MissionActive → 自定义 runtime 已退出但未 Stop → 强制停止 + 隐藏 HUD
try
{
    var mm = MissionManager.Instance;
    if (mm != null && (int)mm.CurrentPhase != 2)
    {
        Stop();
        return;
    }
}
catch { }
try { _current.Update(dt); }
```

**验证**：自定义任务运行中（phase=2）守卫不触发、HUD 正常显示；退出到选任务/主菜单（phase≠2）→ 立即 `Stop` + `Hide` → 原生任务不再弹出。

## 十四、限制 / 待办

- 图执行默认**单线程每帧驱动**；不支持宿主迁移/暂停（单机范围）。
- 默认宿主的坐标换算（网格 X/Y → 世界坐标）与资源发放（补给点/炮弹/发射药/解锁/着弹）为占位，
  需要精确逻辑时覆写 `IOncMissionHost`。
- 联机"同步序号"未接网络层（待任务文件规模评估）；`Custom` 节点回调不随 JSON 传输。
- 与游戏原生 MissionManager 结算/统计（`MissionStatsTracker`）未打通（自定义任务完成走
  `OnMissionCompleted` 通知，不写原生结算）。
