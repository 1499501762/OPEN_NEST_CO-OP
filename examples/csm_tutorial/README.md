# 教程式任务（examples/csm_tutorial）

> 一套**由浅入深**的自定义任务教程（Core 格式为主，最后一课原生格式）。每课聚焦一个能力，
> 照 README 的顺序跑一遍即可掌握任务引擎 + 脚本化模块。
> 完整机制见 `docs/CUSTOM_MISSION.md`（第 15/16 节）与 `docs/NODE_CATALOGUE.md`。

## 快速开始

1. **部署**：`. .\scripts\env.ps1` + 构建（`src\OpenNestCoop` BepInEx 端 / `OpenNestCoop.MelonMod` MLL 端），
   `.\scripts\deploy.ps1`（⚠️ 先关游戏再部署）。
2. **放任务**：把要测的 `tutorial_XX.json` 复制到游戏根目录 `CSM/`（`C:\Iron Nest Heavy Turret Simulator\CSM\`），
   启动游戏即自动注册；进选任务面板点对应卡片（或代码 `OncMissionBridge.Start("tutorial.01.basic")`）。
3. **看日志**：注册 `OncMission registered` → 节点执行 `OncMission node '<id>'` → 结束
   `OncMission completed/failed`。详细测试指南见 `docs/CUSTOM_MISSION.md` 第 16 节。

> ⚠️ 教程 6 需要 C# 注册两个脚本模块（见下）。教程 3 需在能开炮的场景测试（`shell.landed` 事件）。
> `tutorial_01~06` 是 Core 格式；`tutorial_07` 是原生 MissionImporter 格式（同一 `CSM/` 目录自动识别）。

## 逐课讲解

| 课 | 文件 | 演示 | 看什么 |
|---|---|---|---|
| 1 | `tutorial_01_basic_flow.json` | **顺序流程**：`Start`→`Print`(打字机)→`Notify`→`WaitSeconds`→`End` | 打字机出字、通知弹出、5s 后结束 |
| 2 | `tutorial_02_objective_timer.json` | **目标+计时器**：`Objective(Start)`→`StartTimer`→`WaitTimerExpired`→`ObjectiveComplete` | HUD 显示目标进度；10s 后目标完成 |
| 3 | `tutorial_03_event_branch.json` | **事件分支（A 桥接）**：`Branch` 等 `shell.landed`，`Routes` 命中→成功、超时→失败 | 开一炮 → 命中走成功；不开炮 120s 走失败 |
| 4 | `tutorial_04_entities.json` | **地图实体**：`SpawnEntity`(Role=Target)→`WaitEntityDestroyed` | 生成敌人 → 炮击摧毁 → 打印完成 |
| 5 | `tutorial_05_scripted.json` | **脚本化模块（内置）**：`Scripted` 节点分派 `print`/`announce`/`log`（B6 内置，无需注册） | 三个模块依次执行（打字机/通知/日志） |
| 6 | `tutorial_06_scripted_flow.json` | **脚本条件/挂起（B1/B2）**：`ScriptedWait`（挂起直到模块置 BoolResult=true）+ `ScriptedCondition`（布尔结果走 To[0]/To[1]） | 需注册 `wait_player_ready` / `is_timer_ok` 模块（见下） |
| 7 | `tutorial_07_native.json` | **原生格式**：`State_Start`→`State_TeleprinterText`→`State_WaitSeconds`→`State_End` | 打字机/引擎/任务完成全原生行为 |

## 教程 6 需要的模块注册（C#，`CoopRuntime.Startup` 或插件启动时）

```csharp
// 挂起：第 5 次轮询后"好了"（演示每帧问 BoolResult）
int readyCount = 0;
OncMissionBridge.RegisterScriptedModule("wait_player_ready", ctx =>
{
    readyCount++;
    ctx.BoolResult = readyCount >= 5; // 5 帧后完成
    CoopLog.Info("tutorial", () => $"wait_player_ready poll {readyCount} → {ctx.BoolResult}");
});
// 条件：按任务变量/计时器决定走 To[0] 还是 To[1]
OncMissionBridge.RegisterScriptedModule("is_timer_ok", ctx =>
{
    // 示例：读运行时计时器剩余（Core 引擎任务有 Runtime）
    float left = ctx.Runtime != null ? ctx.Runtime.TimerRemaining("t1") : -1f;
    ctx.BoolResult = left < 0f || left > 5f; // 计时器还剩 >5s → 走 To[0]（成功）
    CoopLog.Info("tutorial", () => $"is_timer_ok t1_left={left} → {ctx.BoolResult}");
});
```

## 常见问题

- 卡片不出现 → 确认 JSON 在 `CSM/`、格式正确、日志 `[OncCard] built N custom 3D card(s)`。
- 任务卡住不推进 → 看日志卡在哪个节点；`WaitForEvent`/`ScriptedWait` 挂起是正常的（等事件/等模块置 true）。
- 脚本模块未执行 → 看 `scripted module not registered: '<name>'`（名字拼写/未注册）。
- 教程 6 不注册模块 → `ScriptedWait`/`ScriptedCondition` 默认按 true 处理（挂起立即过、走 To[0]），不会崩。
