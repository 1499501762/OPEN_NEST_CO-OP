# 引擎自检任务集（examples/csm_selftest）

> **最小化测试集**：用 **8 个任务**覆盖自定义任务引擎的**全部功能**。每个任务"全部通过 → 自动 End 成功；
> 有功能异常 → 卡住或走 Fail 失败"，据此定位问题。Id 前缀 `selftest.*`，纯新增，不覆盖原生任务。
> 机制见 `docs/CUSTOM_MISSION.md`；F9「任务节点 (3/4)」页可实时看节点图流转，`OpenNestLogs/mission.log` 有周期 dump。

## 运行

1. 部署双端（`deploy.ps1`），把 `selftest_*.json` 复制到游戏根目录 `CSM/`，启动游戏。
2. 进选任务面板点对应卡片（或代码 `OncMissionBridge.Start("selftest.01.output")`）。
3. 每任务应自动 `End` 成功；卡住/`Fail` 即对应功能异常。看打字机消息 `[自检N] ... OK` 确认。

## 覆盖矩阵（功能 → 任务）

| 任务 | 文件 | 覆盖的节点/功能 |
|---|---|---|
| 自检1 | `selftest_01_output.json` | Start / Print(打字机) / Notify / SceneNotification / WaitSeconds / WaitForEvent(超时) / RandomBranch / End |
| 自检2 | `selftest_02_objective.json` | Objective(Start/AddProgress/SetProgress 自动完成) / ObjectiveComplete / UnlockSceneObject / End |
| 自检3 | `selftest_03_timer_branch.json` | Split(并行) / StartTimer / AddTimerTime / PauseTimer / ResumeTimer / WaitTimerExpired / Branch(事件路由+超时→Fail) / **A2 timer.expired 事件** / 多入边汇合 / End |
| 自检4 | `selftest_04_entities.json` | SpawnEntity / MoveEntity / SetEntityState / Impact / DamageEntity(致死) / WaitEntityDestroyed / End |
| 自检5 | `selftest_05_scripted_reward.json` | Scripted 内置模块(print/requisition/shell/powder/log/announce，B6) / Custom(JSON 空跑) / AddRequisitionPoints / AddShell / AddPowderCharge / End |
| 自检6 | `selftest_06_script_flow.json` | ScriptedWait(挂起) / ScriptedCondition(布尔→To[0]/To[1]) / End/Fail（⚠️ 需注册 `selftest_wait`/`selftest_cond`，见下）|
| 自检7 | `selftest_07_native.json` | **原生格式**：State_Start → State_TeleprinterText → State_WaitSeconds → State_End（原生 ImportMission/StartOperation 链路）|
| 自检8 | `selftest_08a/b_campaign.json` | 前置/后置：B.Requires=[A]，完成 A 后 B 才解锁（IsMissionUnlocked）|

**覆盖核对**：全部 `OncNodeKind`（除 `ObjectiveFail` 未主动触发、`Custom` 无回调空跑）✓；事件 A2（timer.expired）✓；脚本化 B1/B2/B6 ✓；并行/汇合/分支/超时 ✓；奖励/解锁/实体 ✓；原生格式 ✓；前置后置战役 ✓。

## 自检6 需要的 C# 模块（`CoopRuntime.Startup` 或插件启动时注册）

```csharp
// 挂起：第 3 次轮询后"好了"（演示每帧问 BoolResult）
int waitCnt = 0;
OncMissionBridge.RegisterScriptedModule("selftest_wait", ctx =>
{
    waitCnt++;
    ctx.BoolResult = waitCnt >= 3; // 3 帧后完成
    CoopLog.Info("selftest", () => $"[自检6] selftest_wait poll {waitCnt} → {ctx.BoolResult}");
});
// 条件：返回 true → 走 To[0](成功线)
OncMissionBridge.RegisterScriptedModule("selftest_cond", ctx =>
{
    ctx.BoolResult = true; // 真实场景可读 ctx.Variables/计时器/游戏状态决定
    CoopLog.Info("selftest", () => $"[自检6] selftest_cond → {ctx.BoolResult}");
});
// ⚠️ 不注册时：ScriptedWait 立即过、ScriptedCondition 走 To[0]，任务仍会 End（默认 true 兜底）。
```

## 验证要点（每任务看什么）

| 任务 | 成功标志（End + 打字机） | 失败迹象 |
|---|---|---|
| 1 | 打字机 `[自检1] ... OK` | 卡在 WaitForEvent（超时 2s 后应过）|
| 2 | HUD 目标 t 3/3 完成 + `[自检2] ... OK` | 目标未完成/进度不对 |
| 3 | `[自检3] 两路汇合 OK`（约 4s 后）| 20s 后 Fail（事件分支超时）|
| 4 | `[自检4] ... OK` | 卡在 WaitEntityDestroyed（伤害没致死）|
| 5 | `[自检5] ... OK` | 某模块未注册报 `not registered`（内置应都在）|
| 6 | `[自检6] 条件→To[0] 成功` | 走 lose → Fail（cond 返回 false）|
| 7 | 原生打字机 `[自检7] ...` + 任务完成 | OnMissionLoaded 未触发 / 图未启动 |
| 8 | 先完成 A 再启动 B；B 打字机 `[自检8B]` | 未完成 A 时启动 B 被拒绝（`locked` 日志）|

**日志**：注册 `OncMission registered: 'selftest.*'`；节点 `OncMission node '<id>' kind=...`；结束
`OncMission completed 'selftest.*'`。F9 任务节点页看节点图 DONE/ACTIVE；`mission.log` 含连线 dump。
