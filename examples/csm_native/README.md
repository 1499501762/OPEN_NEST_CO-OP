# 原生格式自定义任务样板（examples/csm_native）

> 这些是**游戏原生 MissionImporter JSON 格式**的自定义任务样板（`NodeType`=原生 `State_*` 名）。
> 启动链路：`CSM/*.json` → `RegisterNativeFromJson` → 卡片点击/`MissionSync` → `StartNative(id)`
> → `OncMissionImporter.Import`（WrapExportPackage）→ `StartOperation` 跑**完整原生图**
> （打字机/引擎/任务完成/床交互全原生行为）。见 `docs/CUSTOM_MISSION.md` 第 13 节。

## 怎么用

1. 把 `*.json` 复制到**游戏根目录的 `CSM/` 文件夹**，启动时自动注册（文件名序）。
2. 选任务面板点卡片启动；联机走 `MissionSync`/`MissionSyncV2`（主机广播 `@c:<id>` 前缀）。
3. 场景名（`SceneName`）：教程 `Mission tutorial 1~4` / **`Mission Chill`** / `Mission Challenging` / 其余 `MissionBase`。

## 样板清单

| 文件 | Id | 演示 |
|---|---|---|
| `01_01_defend.json` | `sample.defend` | 简报（TeleprinterText）/通知（SendUINotification）/计时（StartTimer）|
| `01_02_killwave.json` | `sample.killwave` | 地图实体波次：SpawnMapEntity（SetContextVariable）+ WaitEntityDestroyed（Entites TargetSelection）|
| `01_03_branch.json` | `sample.branch` | 分支（SplitBranch / ConditionBranch / RandomBranch）|
| `01_04_full_operation.json` | `sample.full` | 完整综合：简报/计时/Split 并行/奖励/双结局 |
| `01_05_scripted.json` | `sample.scripted` | **脚本化模块**：`State_CustomTrackingVariable` 载体（`variableName="onc.script.announce"`）+ 节点 ID 锚点（`onc_script_<name>`）→ 桥接层分派 `OncMissionBridge.RegisterScriptedModule` 注册的 C# 模块 |
| `01_07_event_template.json` | `sample.eventwire` | **原生事件接线（C 方案模板）**：`State_WaitForNotification` + `Event_OnNotification` 接线（⚠️ 待实测——若 `Event_*` 被 importer 拒绝则原生任务事件走 B 兜底） |
| `02_campaign.json` | `sample.campaign` | 战役（前置/后置）|
| `10_native_sample.json` | 原生导出 | 原生 MissionImporter 导出格式参考 |

## 脚本化模块锚点约定（原生图里跑 C# 逻辑）

- **`State_CustomTrackingVariable` 载体**：`NodeData: { "variableName": "onc.script.<name>", "CustomVariableKey": "<name>" }`。
  图进入该节点 → 桥接层（`PollNativeScripted` 每帧轮询 `CurrentState`，纯读取零 Harmony）分派模块 `<name>`。
- **节点 ID 锚点**：任意原生节点 `ID = "onc_script_<name>"` → 进入时分派模块 `<name>`（最稳，不依赖载体节点被 importer 接受）。
- 模块注册：`OncMissionBridge.RegisterScriptedModule("announce", ctx => { ... })`；内置 `announce`/`ping`。
- 详见 `docs/CUSTOM_MISSION.md` 第 15 节。

## 字段速查

- 节点：`ID` + `NodeType`（原生 `State_*` 名）+ `NodeData` + `Connections`（Source/Destination 节点 + 字段名）。
- 文本字段用 TextIdentifier：`{ "Raw": "...", "Key": "" }`（导入后 `RepairImportedTexts` 回填）。
- 完整原生节点目录 + 字段：`docs/NODE_CATALOGUE.md`（48 `State_*` + 13 `Event_*` = 61）。
- 参考模组完整样例：`ref/mission editor/Docs/Observador.definition.json`。
