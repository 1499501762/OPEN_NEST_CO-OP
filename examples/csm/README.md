# 自定义任务参考样板（examples/csm）

> 这些是**纯新增**的自定义任务样板（Id 前缀 `sample.*`），**不覆盖/不修改任何原生任务**。
> 框架：`OpenNestCore.Tasks`（见 `docs/CUSTOM_MISSION.md`）。

## 怎么用

1. 把 `*.json` 复制到**游戏根目录的 `CSM/` 文件夹**（`<游戏根目录>/CSM/`）。启动时 `CoopRuntime.Startup`
   会自动按**文件名序**读取注册（`01_*.json` 先于 `02_*.json`）。
2. 在游戏里启动任务：
   - **选任务面板卡片**：启动游戏进入选任务面板，自定义任务卡片会自动生成（克隆原生卡片，标题/类型从 JSON 读），点击即启动。
   - 或代码触发：`OncMissionBridge.Start("sample.defend")` 等。
3. 也可不复制到 CSM，用 `OncMissionBridge.LoadFromFolder("你的路径")` 手动加载。

## 样板清单（每个演示什么）

| 文件 | Id | 演示 |
|---|---|---|
| `01_01_defend.json` | `sample.defend` | 顺序流程：打印/通知/计时器（WaitSeconds）/目标（Objective+Complete） |
| `01_02_killwave.json` | `sample.killwave` | 地图实体（Fire 目标）：SpawnEntity/WaitEntityDestroyed/目标进度（AddProgress） |
| `01_03_branch.json` | `sample.branch` | 事件分支：Branch/Routes/Timeout → End（成功）/Fail（失败） |
| `01_04_full_operation.json` | `sample.full` | **完整综合**：简报/目标/限时计时/并行竞争（击杀线 vs 倒计时线，先到者定胜负）/奖励/成功失败双结局 |
| `01_05_scripted.json` | `sample.scripted` | **脚本化模块**：`Scripted` 节点按 `ModuleName` 分派到宿主注册的 C# 脚本模块（`announce`/`ping` 内置），`ModuleArgs` 传 JSON 参数 |
| `01_06_events.json` | `sample.events` | **游戏事件响应（A 方案）**：`Branch` 等待游戏事件（`mission.started`/`shell.landed`/`mission.completed`），事件命中→成功线、超时→失败线 |
| `01_07_scripted_flow.json` | `sample.scriptflow` | **脚本化流程（B1/B2/A2）**：`Scripted` 动作 + `StartTimer` + `ScriptedWait` 挂起（模块问"好了没"）+ `ScriptedCondition` 条件分支（模块布尔结果走不同结局） |
| `02_campaign.json` | `sample.campaign` | 战役（前置/后置）：OncOperation + OncMissionRef.Requires |

## 节点/字段速查（JSON 字段与 `OncNode` 一致）

- 连线：每个节点 `To`（出边，多个=并行/分支）、`From`（入边，多个=汇合）。**必须连 To**（否则任务立即结束）。
- `Kind` 取值（`OncNodeKind`）：`Start/End/Fail/WaitSeconds/WaitForEvent/WaitEntityDestroyed/WaitTimerExpired/
  Branch/RandomBranch/Split/Objective/ObjectiveComplete/ObjectiveFail/Teleprinter/Notify/SceneNotification/
  SpawnEntity/MoveEntity/DamageEntity/SetEntityState/Impact/AddRequisitionPoints/AddShell/AddPowderCharge/
  StartTimer/StopTimer/PauseTimer/ResumeTimer/AddTimerTime/UnlockSceneObject/Custom/Scripted`。
- **脚本化模块（`Scripted`）**：`ModuleName`（分派键，须先 `OncMissionBridge.RegisterScriptedModule(name, fn)` 注册；
  内置 `announce`/`ping`）+ 可选 `ModuleArgs`（JSON 字符串，传给模块）。原生侧经
  `State_CustomTrackingVariable` 载体（`variableName="onc.script.<name>"`）或节点 ID `onc_script_<name>` 触发，
  见 `docs/CUSTOM_MISSION.md` 第 15 节。
- **游戏事件（A 方案桥接）**：任务图 `WaitForEvent`/`Branch` 可直接等游戏事件（事件源见下表）；
  `Branch` 用 `Routes`（事件命中→指定节点）+ `To`（超时→兜底）。事件清单：
  `mission.started/completed/failed/finished/reloaded/map/menu`、`shell.landed`、`interact.click`/`interact.slot`、
  `gun.fired`、`requisition.spent`、`notification.shown`、`teleprinter.printed`、`counterbattery`、`turret.moved`、
  `timer.expired[.<id>]`、`entity.destroyed[.<id>]`。
- **脚本模块事件订阅（B 方案，需 C# 注册）**：`OncMissionBridge.RegisterScriptedHook("shell.landed", "myModule")`
  ——游戏事件触发时**异步、可多次**地分派脚本模块（`ctx.Event` 携带 EventId/Payload），不依赖节点流程。
  ⚠️ 事件源纪律：同一事件只选一条轨（Core 走 A+B / 原生任务 C 原生 `Event_*` 为主），防双触发，见
  `docs/NODE_CATALOGUE.md` 第六节。
- **脚本化条件/挂起（B1/B2）**：`ScriptedCondition`（模块设 `ctx.BoolResult`，true 走 To[0]/false 走 To[1]）、
  `ScriptedWait`（挂起直到模块 BoolResult=true）。模块需 C# 注册。
- **声明式 JSON 内置模块（B6，无需注册）**：`announce`/`print`/`requisition`/`shell`/`powder`/`log`/`ping`
  ——`ModuleName` 直接写，`ModuleArgs` 传 JSON。
- **计时器到期（A2）**：`StartTimer` 归零 → `timer.expired.<id>` 事件（脚本模块可订阅）。
- **原生结算（D，可选）**：任务 JSON 加 `"NativeComplete": true` → 完成/失败时额外走原生 MissionManager 结算。
- `ObjectiveAction`：`Start/SetProgress/AddProgress`。
- 目标进度自动完成：`Target>0` 且进度 ≥ Target 时自动 Completed。
- **选任务面板卡片（可选 `"Card"` 字段）**：`X/Y`（相对父容器左上，右下为正）、`Width/Height`（尺寸）、
  `TitleColor/Background`（十六进制 `#RRGGBB`/`#AARRGGBB`）。非自动布局时用 `Card` 定位卡片；未配置字段回退默认。
- **`"SceneName"`（任务场景，原生效果关键）**：填**真实任务场景名**（**用 `Mission Challenging`（默认）/
  `Mission Chill`（可切换）；`MissionBase` 也可试**。⚠️ **教程场景 `Mission tutorial 1~4` 实测无效**
  （绑定原生教程初始化流程，加载后无任务内容）。场景名可从日志 `[OncCard] template mission scene='...'`
  或 `<游戏根目录>_Data/globalgamemanagers` 提取）。
  点卡片会 `SceneManager.LoadScene(SceneName)` **进入任务场景**（像原生任务一样），然后自定义引擎在场景内
  跑简报/通知/目标/实体 + **任务 HUD**（左上角任务名/简报/目标列表）；留空则在当前场景（选任务面板）后台跑（无场景切换）。
- **`"#sym"` 符号机制（定义任务所在场景等）**：JSON 顶层可定义符号表 `"#sym": { "名字": "值" }`，
  字段值写 `"#sym:名字"` 引用（解析时查符号表替换）。例：
  ```json
  { "#sym": { "SceneName": "Mission tutorial 1" }, "SceneName": "#sym:SceneName", ... }
  ```
  也支持 `"SceneName": "#sym:Mission tutorial 2"`（无符号表时剥掉 `#sym:` 前缀直接用后面的名字）。
  好处：场景名集中定义、多任务可复用同一符号、避免硬编码。
- 完整字段说明见 `docs/CUSTOM_MISSION.md` 第四节。
