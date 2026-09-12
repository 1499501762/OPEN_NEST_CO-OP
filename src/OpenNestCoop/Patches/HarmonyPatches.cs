using System;
using HarmonyLib;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;
using OpenNestCoop.UI;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes;

using OpenNestCoop.Core;
#if MELONLOADER
using SleepyNodes = Il2CppSleepyNodes;
using Localisation = Il2CppLocalisation; // MLL 端 interop 命名空间适配（PreTeleprinterNodeEnter 用 Localisation.TextIdentifier）
#endif
// Harmony 双平台一致：BepInEx 与 MelonLoader 都用 0Harmony.dll（HarmonyLib 2.10.x）。
// ML 的 Il2Cpp 程序集里也有 Harmony 命名空间（HarmonyX 兼容别名），会把裸 'Harmony' 遮蔽成命名空间，
// 故这里用完全限定 HarmonyLib.Harmony（两平台均有 HarmonyLib.*）。
namespace OpenNestCoop.Patches;

/// <summary>
/// Harmony 补丁（M2）。
/// - TurretController.HandleInput：已不再拦截（谁操作谁权威——任何端都可本地操作炮塔输入，
///   由 Lever/Gear 值同步或 DesiredRotation 状态同步上行到主机，主机广播给其他端）。
/// - GunController.FireShell：主机开火后广播事件给全员。
/// </summary>
public static class HarmonyPatches
{
    private static HarmonyLib.Harmony _harmony;

    public static void Apply()
    {
        if (_harmony != null) return;
        _harmony = new HarmonyLib.Harmony("dev.open-nest.coop");

        // 谁操作谁权威：任何端都可本地操作炮塔输入，不再拦截（方向角/仰角由 Lever/Gear 值同步 + DesiredRotation 状态同步）
        // TryPatch(typeof(TurretController), "HandleInput", prefix: nameof(PreHandleInput));
        TryPatch(typeof(GunController), "FireShell", postfix: nameof(PostFireShell));
        TryPatch(typeof(GunController), "RequestFire", prefix: nameof(PreRequestFire), postfix: nameof(PostRequestFire));
        TryPatch(typeof(CounterBatteryCinematicImpactSpawner), "SpawnOne", prefix: nameof(PreSpawnOne));
        TryPatch(typeof(MapReconClearHandle), "RegisterChild", prefix: nameof(PreRegisterChild), postfix: nameof(PostRegisterChild));
        // ⚠️ 2026-09-12 照片角度：**同步随机种子**（用户建议方向）。
        // 照片角度 = 游戏 `RandomUIRotation.Awake` 里用 `UnityEngine.Random` 在 [min,max] 之间 roll
        // （实测 min=5 max=85 axis=Z；`[RurSeed] match=YES` 验证游戏确实用全局 `UnityEngine.Random`）。
        // 全局随机可播种 → 两端在 Awake 前用**同一个种子**（由两端一致的权威落点派生）InitState，
        // 游戏自己 roll 出的角度就相同：**不传角度、不事后覆盖、无 1 帧闪烁**；事后恢复全局随机状态。
        // ⚠️ 2026-09-12 停用：`RandomUIRotation.Awake` 的“同步随机种子”方案实测未生效（双端 [RurSeed] 零日志），
        // 照片角度继续走 ImpactSync 广播游戏真实角度（tilt）那条已存在的路径。
        // ⚠️ 2026-08-23 着弹/炮弹诊断：用户反馈"只打两发但着弹多触发/侦察照片多触发"。
        // 计数定位：每发炮弹创建几个 ShellVisual（Initialize）、着弹评估/特效各触发几次
        // （ImpactTracker.EvaluateImpact static / ShellVisual.SpawnImpactEffectAt）→ 判断是炮弹重复创建还是着弹重复触发。
        TryPatch(typeof(ImpactTracker), "EvaluateImpact", postfix: nameof(PostEvaluateImpact));
        TryPatch(typeof(ShellVisual), "Initialize", postfix: nameof(PostShellInit));
        TryPatch(typeof(ShellVisual), "SpawnImpactEffectAt", postfix: nameof(PostSpawnImpact));
        // ⚠️ 2026-08-23 着弹报告路径诊断：ImpactLocation.EvaluateAndReport / ReportLocationNextFrame 计数——
        // 2 发炮弹 3 次 EvaluateImpact（第 3 次 5 秒后），需定位着弹报告是哪条路径多触发。
        TryPatch(typeof(ImpactLocation), "EvaluateAndReport", postfix: nameof(PostEvalReport));
        TryPatch(typeof(ImpactLocation), "ReportLocationNextFrame", postfix: nameof(PostReportNextFrame));
        // 按钮/拉杆点击统一入口：LookAtTarget.OnClickDown（所有交互按钮/拉杆的点击效果都在这里触发：
        // 拉杆动画 AnimatorBoolToggler + onClickDown UnityEvent 绑定逻辑（OnLoadChargesPressed/OnChargeButtonPressed
        // 等）+ 状态推进）。转发到对端 OnClickDown+OnClickUp → 完整复现（视觉+逻辑）。
        // 注意：不再 patch OnUserInput_Advance/Regress——点击转发后对端 OnClickDown 内部会自然推进，
        // 若再转发推进事件会造成“对端点击推进 + 事件推进”双推进跳步/回退。
        TryPatch(typeof(LookAtTarget), "OnClickDown", prefix: nameof(PreLookClick));
        // 发射台开关序列（LookAtTargetUnlockSequence5）：点击 slot → HandleSlotClicked → 交互事件上报
        // （数值对账由 SequenceSync 主机权威广播；patch 保证客机点击让主机知道，对端执行解锁逻辑）
        TryPatch(typeof(LookAtTargetUnlockSequence5), "HandleSlotClicked", prefix: nameof(PreSeqSlotClick));
        // 交互控件 OnEnable（创建/激活）→ 事件驱动注册（替代周期 Rescan）：控件创建/激活时才扫描，
        // 静默运行期零 FindObjectsOfTypeAll。覆盖场景加载 + 运行时动态实例化。TryPatch 容错：
        // 若某类无 OnEnable override（继承 MonoBehaviour 基类），patch 失败仅警告，场景 buildIndex
        // 变化兜底仍保证场景加载时注册。
        TryPatch(typeof(DialInteractable), "OnEnable", postfix: nameof(PostControlEnable));
        TryPatch(typeof(LinearSliderInteractable), "OnEnable", postfix: nameof(PostControlEnable));
        TryPatch(typeof(SliderEnergyMomentumSpinner), "OnEnable", postfix: nameof(PostControlEnable));
        TryPatch(typeof(TurretController), "OnEnable", postfix: nameof(PostControlEnable));
        // ⚠️ 2026-08-26：.Charge Dial（弹药类型选择，棘爪档位盘）偶发不同步——疑似值源问题。
        // patch DialValueEventWatcher.HandleDialValueChanged（拨动值变化事件）→ 读三个候选字段
        // （accumulatedValue / currentRotationAngle / detentCurrentAngle）+ 路径，确认正确值源。
        TryPatch(typeof(DialValueEventWatcher), "HandleDialValueChanged", postfix: nameof(PostDialValueChanged));
        // 选药量/投放发射药：Button Dispencer / Charge Rammer 点击不走 OnClickDown（走 isClicked + 方法调用），
        // 必须 patch 方法：OnChargeButtonPressed（选药量）+ OnLoadChargesPressed（投放）→ 广播 → 对端执行（含按钮动画+逻辑）
        TryPatch(typeof(PowderChargeController), "OnChargeButtonPressed", prefix: nameof(PrePowderSelect));
        TryPatch(typeof(PowderChargeController), "OnLoadChargesPressed", prefix: nameof(PrePowderLoad));
        // ⚠️ 2026-08-26：铁巢（TurretController）位置同步——patch MoveTurret/SetTurretLocation（对齐
        // Synchrony NestMoveBridge）。铁巢位置两端一致 → 追踪器（炮弹从铁巢坐标发射到着弹点）轨迹一致。
        // 主机权威：主机移动铁巢 postfix 广播，客机拦截本地移动（prefix）+ 接收广播应用（防环）。
        // ⚠️ 必须显式指定参数类型（Vector3）：AccessTools.Method 无参数类型在 IL2CPP 下找不到
        // MoveTurret/SetTurretLocation（有 Vector3 参数）→ patch 失败 → 铁巢位置同步不生效。
        {
            var ttType = typeof(TurretController);
            var miMove = AccessTools.Method(ttType, "MoveTurret", new[] { typeof(Vector3) });
            var miSet = AccessTools.Method(ttType, "SetTurretLocation", new[] { typeof(Vector3) });
            if (miMove != null) TryPatchMethod(ttType, "MoveTurret", miMove,
                prefix: nameof(NestSync.PreTurretMove), postfix: nameof(NestSync.PostTurretMove));
            else CoopRuntime.LogSource?.LogWarning("Harmony: TurretController.MoveTurret not found (Vector3)");
            if (miSet != null) TryPatchMethod(ttType, "SetTurretLocation", miSet,
                prefix: nameof(NestSync.PreTurretMove), postfix: nameof(NestSync.PostTurretSetLocation));
            else CoopRuntime.LogSource?.LogWarning("Harmony: TurretController.SetTurretLocation not found (Vector3)");
        }
        // 仰角联动（GunElevationLinkCoordinator.SetLinked/ToggleLinked）——事件驱动（2026-08-31）：
        // 玩家切换联动 → postfix 广播 isLinked（GunLinkSync 去重），去掉 0.3s 轮询扫描。
        TryPatch(typeof(GunElevationLinkCoordinator), "SetLinked", postfix: nameof(PostGunLinkSet));
        TryPatch(typeof(GunElevationLinkCoordinator), "ToggleLinked", postfix: nameof(PostGunLinkToggle));
        // 预备激发火炮（ArmedFireRelayOneShot.ArmLeft/ArmRight/DisarmLeft/DisarmRight）——事件解耦（P0）：
        // 直接广播业务方法调用，对端调同名方法（不依赖按钮 active，修"客机拉 Arm 没用" inactive 排队丢弃）。
        // prefix：先广播再放行原方法（本地正常执行 + 对端复现），IsApplyingArm 防环。
        TryPatch(typeof(Zagreekie.Tools.ArmedFireRelayOneShot), "ArmLeft", postfix: nameof(PostArmLeft));
        TryPatch(typeof(Zagreekie.Tools.ArmedFireRelayOneShot), "ArmRight", postfix: nameof(PostArmRight));
        TryPatch(typeof(Zagreekie.Tools.ArmedFireRelayOneShot), "DisarmLeft", postfix: nameof(PostDisarmLeft));
        TryPatch(typeof(Zagreekie.Tools.ArmedFireRelayOneShot), "DisarmRight", postfix: nameof(PostDisarmRight));
        TryPatch(typeof(Zagreekie.Tools.ArmedFireRelayOneShot), "ToggleLeft", postfix: nameof(PostToggleLeft));
        TryPatch(typeof(Zagreekie.Tools.ArmedFireRelayOneShot), "ToggleRight", postfix: nameof(PostToggleRight));
        TryPatch(typeof(Zagreekie.Tools.ArmedFireRelayOneShot), "TriggerFire", postfix: nameof(PostTriggerFire));
        // 弹舱动作（CylinderShellSelector.OnLoadButtonClicked 推弹头 / OnMoveButtonClicked 切弹舱）——事件解耦（P1）：
        // 直接广播业务方法调用，对端调同名方法（不依赖按钮 active，修装填区按钮 inactive 排队问题）。
        TryPatch(typeof(CylinderShellSelector), "OnLoadButtonClicked", prefix: nameof(PreCylLoadShell));
        TryPatch(typeof(CylinderShellSelector), "OnMoveButtonClicked", prefix: nameof(PreCylMoveCylinder));
        // 多人模式移除“失焦暂停”：游戏切出去（虚拟机/Alt-Tab）自动暂停，联机时拦截
        TryPatch(typeof(PauseManager), "OnApplicationFocus", prefix: nameof(PreAppFocus));
        // 任务过渡事件（完成/失败/重载/回菜单）：主机/操作端触发 → 广播 → 对端执行同样操作（MissionEventSync）
        TryPatch(typeof(MissionManager), "FinishMission", prefix: nameof(PreMissionFinish));
        TryPatch(typeof(MissionManager), "MarkMissionComplete", prefix: nameof(PreMissionComplete));
        TryPatch(typeof(MissionManager), "MarkMissionFailed", prefix: nameof(PreMissionFailed));
        TryPatch(typeof(MissionManager), "ReloadCurrentMission", prefix: nameof(PreMissionReload));
        TryPatch(typeof(MissionManager), "ReturnToMap", prefix: nameof(PreMissionReturnMap));
        TryPatch(typeof(MissionManager), "EndOperationAndReturnToMenu", prefix: nameof(PreMissionEndOperation));
        // 补给购买（Supply console）：拉征用杆 → AttemptRequisition（主机权威执行购买，购买对所有人生效）
        TryPatch(typeof(RequisitionSlot), "AttemptRequisition", prefix: nameof(PreRequisition));
        // 卡牌入槽/出槽（PunchcardSync）：ItemSlot.PlaceItem/RemoveItem → 广播 → 对端执行
        // （⚠️ 卡牌插入卡槽走 ItemSlot 放置，不走 RequisitionSlot.PlaceCard——那个不触发）
        TryPatch(typeof(ItemSlot), "PlaceItem", postfix: nameof(PostItemSlotPlace));
        TryPatch(typeof(ItemSlot), "RemoveItem", postfix: nameof(PostItemSlotRemove));
        // 任务随机内容一致：FireMission.GenerateMission 生成目标前应用主机 seed
        // （客机收到主机 seed 后，无论何时 GenerateMission 都先设置 useFixedSeed/fixedSeed → 两端随机一致）
        TryPatch(typeof(FireMission), "GenerateMission", prefix: nameof(PreFireMissionGenerate));
        // ⚠️ 2026-08-31：列车/移动目标（FireMission.MoveMapEntity 连续移动，startedAt/endsAt 时间戳插值）——
        // 两端任务图各自触发 MoveMapEntity 且时间戳基准不同 → 两端列车插值相位不同 → 位置漂移（“列车不同步”）。
        // ⚠️ 两个重载（4参/6参）都 patch；显式参数类型（MapEntity + Vector3）避免 AccessTools 无参类型找不到。
        {
            var fmType = typeof(FireMission);
            var miMove4 = AccessTools.Method(fmType, "MoveMapEntity", new[] { typeof(MapEntity), typeof(Vector3), typeof(bool), typeof(float) });
            var miMove6 = AccessTools.Method(fmType, "MoveMapEntity", new[] { typeof(MapEntity), typeof(Vector3), typeof(bool), typeof(float), typeof(double), typeof(double) });
            var preMove = new HarmonyMethod(AccessTools.Method(typeof(HarmonyPatches), nameof(PreMoveMapEntity)));
            if (miMove4 != null) _harmony.Patch(miMove4, prefix: preMove);
            else CoopRuntime.LogSource?.LogWarning("Harmony: FireMission.MoveMapEntity (4-arg) not found");
            if (miMove6 != null) _harmony.Patch(miMove6, prefix: preMove);
            else CoopRuntime.LogSource?.LogWarning("Harmony: FireMission.MoveMapEntity (6-arg) not found");
        }
        // ⚠️ 2026-09-05 撤销 State_MoveMapEntity.OnEnter patch（任务图同步实验）：该 patch 客机 return false 拦截本地
        // OnEnter，但主机 Resolve 广播链路在 killwave 场景未触发/失败 → 客机列车移动被拦截且无广播驱动 → "客机列车
        // 压根不动"。列车位置同步回到 HEAD 方案（EntitySync 主机权威位置广播 + 客机插值跟随）。
        // 任务打字机通知同步：UINotificationManager.ShowNotification 事件 → 主机广播 → 客机复现
        TryPatch(typeof(UINotificationManager), "ShowNotification", postfix: nameof(PostShowNotification));
        // 主菜单联机入口（2026-08-23）：原生主菜单加载完成（MainMenuStateRelay.HandleMainMenuLoaded，private）→
        // 注入联机入口按钮（UI/MainMenuEntry.cs）。TryPatch 找不到方法只打日志不崩；防重复在 MainMenuEntry 内。
        TryPatch(typeof(MainMenuStateRelay), "HandleMainMenuLoaded", postfix: nameof(PostMainMenuLoaded));
        // 任务打字机打印同步：Teleprinter.SubmitLines/ClearAll/ClearAlarm → 主机广播 → 客机复现
        // prefix（PreTeleprinterPrint）：V2 模式客机本地 SubmitLines 抑制——打字机内容由主机权威广播，
        // 客机本地任务图/同 seed 打印会与主机内容重复且不一致（双份打字机根因）。
        TryPatch(typeof(Teleprinter), "SubmitLines", prefix: nameof(PreTeleprinterPrint), postfix: nameof(PostTeleprinterPrint));
        TryPatch(typeof(Teleprinter), "AppendInstant", postfix: nameof(PostTeleprinterAppend));
        TryPatch(typeof(Teleprinter), "ClearAll", postfix: nameof(PostTeleprinterClearAll));
        TryPatch(typeof(Teleprinter), "ClearAlarm", postfix: nameof(PostTeleprinterClearAlarm));
        // ⚠️ 自定义任务诊断：State_TeleprinterText.OnEnter 现场（prefix 只读）。
        // ⚠️ 不加 postfix 干预：原生打字机行为由节点 JSON 字段配置决定（OnlyQueue/WaitUntilComplete）
        // ——有的任务开局就打（OnlyQueue=false），有的等玩家靠近（OnlyQueue=true 或 TryStart 延迟）。
        // postfix TryStart 会覆盖这些配置 → 必须移除，让原生 OnEnter 自己按节点字段驱动。
        TryPatch(typeof(SleepyNodes.State_TeleprinterText), "OnEnter", prefix: nameof(PreTeleprinterNodeEnter));
        // ⚠️ 自定义任务诊断：State_SpawnMapEntity.OnEnter 执行现场（prefix 只读，确认生成节点是否被图驱动）
        TryPatch(typeof(SleepyNodes.State_SpawnMapEntity), "OnEnter", prefix: nameof(PreSpawnNodeEnter));
        // ⚠️ 捕获打字机驱动链：SubmitLines 的 waitForTrigger 参数 + TryStart/RunQueue/ResumeCurrentJob 调用
        // （确认原生任务"玩家靠近才打印动画"的触发机制）。Teleprinter 具体类方法无嵌套参数 → patch 安全。
        TryPatch(typeof(Teleprinter), "SubmitLines", prefix: nameof(PreTeleprinterSubmitCapture));
        TryPatch(typeof(Teleprinter), "TryStart", prefix: nameof(PreTeleprinterTryStartCapture));
        TryPatch(typeof(Teleprinter), "RunQueue", prefix: nameof(PreTeleprinterRunQueueCapture));
        TryPatch(typeof(Teleprinter), "ResumeCurrentJob", prefix: nameof(PreTeleprinterResumeCapture));
        // 玩家-猫交互事件（软同步，MsgType=133）：拾起/放下/驱赶/抚摸 → 广播 → 对端执行
        // （谁操作谁发；对端执行 StartCarrying/StopCarrying/ShooCat/PetTheCat，IsApplyingCat 防环）
        TryPatch(typeof(CatPickUpHandler), "ExecutePickUp", postfix: nameof(PostCatPickUp));
        TryPatch(typeof(CatPickUpHandler), "ExecuteDrop", postfix: nameof(PostCatDrop));
        // 放下：patch CatController.StopCarrying（游戏放下最终都调它——ExecuteDrop/ExecuteExternalDrop/OnDropPerformed
        // 都汇到这里；ExecuteDrop patch 曾未触发（放下不走它））。IsApplyingCat 防环避免应用远端放下时重复广播。
        TryPatch(typeof(CatController), "StopCarrying", postfix: nameof(PostCatStopCarrying));
        // 驱赶：patch CatController.ShooCat（ExecuteShoo 内部最终调它，能拿到精确猫实例）
        TryPatch(typeof(CatController), "ShooCat", postfix: nameof(PostCatShoo));
        TryPatch(typeof(CatController), "PetTheCat", postfix: nameof(PostCatPet));
        // 打断（玩家撞到猫/交互打断）：对端复现同样打断 → AI 状态一致
        TryPatch(typeof(CatController), "InterruptCat", postfix: nameof(PostCatInterrupt));
        // 中文输入法（IME）方案（验证后确定）：
        // TMP_InputField 只唤起 IME（系统级弹出），但**字符事件不路由到它**（独立 Canvas，
        // 诊断确认 TMP_InputField.text 始终为空）→ 轮询 text 无字符。
        // 字符必须经 InputSystem 事件：patch Keyboard.OnTextInput(char)（原生方法，BepInEx
        // Harmony 处理参数 marshall 比手写 Il2Cpp 委托可靠）。回调全 try-catch，只处理 CJK
        // （>0x2E7F）避免与 PollInput 英文双通道。控制键退格/回车也处理。
        TryPatch(typeof(UnityEngine.InputSystem.Keyboard), "OnTextInput", postfix: nameof(PostKeyTextInput));
        // 自定义任务：原生任务脚本流程级挂钩（覆盖原生任务 + 协同事件）。只 patch 流程方法
        // （MissionManager.StartOperation/LoadMission + MissionGraph.OnMissionLoaded），不碰任务状态机。
        // 未注册覆盖时所有原生任务放行（无副作用）。
        OpenNestCoop.GameSync.OncMissionHooks.Apply();
    }

    /// <summary>文本输入转发：InputSystem 调用 OnTextInput(char)（英文直接字符 + 中文 IME 提交字符）。
    /// 只处理 CJK（>0x2E7F）+ 控制键，英文走 PollInput 物理键（避免双通道）。全 try-catch 防崩溃。</summary>
    private static void PostKeyTextInput(UnityEngine.InputSystem.Keyboard __instance, char __0)
    {
        try
        {
            if (__instance != UnityEngine.InputSystem.Keyboard.current) return;
            if (__0 == '\b' || __0 == '\r' || __0 == '\n' || __0 > 0x2E7F)
                OpenNestCoop.UI.CoopUIManager.OnImeText(__0);
        }
        catch { } // 绝不向外抛（防崩溃）
    }

    /// <summary>交互控件 OnEnable（创建/激活）→ 逐控件即时注册（并行方案主路径）。
    /// 不触发全量扫描——静默运行期零 FindObjectsOfTypeAll；Rescan 5s 保底捕获 OnEnable 漏注册
    /// （含 TurretController 无 OnEnable override），漏注册记独立日志 ControlSync.onEnableMiss。</summary>
    private static void PostControlEnable(Component __instance)
    {
        try { ControlSync.OnControlEnabled(__instance); }
        catch { } // 绝不向外抛（防崩溃）
    }

    /// <summary>postfix：原生主菜单加载完成 → 注入联机入口（MainMenuEntry.OnMainMenuLoaded 内防重复）。</summary>
    private static void PostMainMenuLoaded(string sceneName, MainMenuStateRelay __instance)
    {
        try { MainMenuEntry.OnMainMenuLoaded(sceneName); }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony main menu entry: {ex.Message}"); }
    }

    private static void TryPatch(Type target, string method, string prefix = null, string postfix = null)
    {
        try
        {
            var mi = AccessTools.Method(target, method);
            if (mi == null)
            {
                CoopRuntime.LogSource?.LogWarning($"Harmony: cannot find {target.Name}.{method}");
                return;
            }
            _harmony.Patch(mi,
                prefix: prefix != null ? new HarmonyMethod(AccessTools.Method(typeof(HarmonyPatches), prefix)) : null,
                postfix: postfix != null ? new HarmonyMethod(AccessTools.Method(typeof(HarmonyPatches), postfix)) : null);
            CoopRuntime.LogSource?.LogInfo($"Harmony: patched {target.Name}.{method}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony: {target.Name}.{method} patch failed: {ex.Message}"); }
    }

    /// <summary>用显式 MethodInfo patch（IL2CPP 下 AccessTools.Method 无参数类型找不到方法时，
    /// 如 MoveTurret/SetTurretLocation 带 Vector3 参数）。prefix/postfix 在 NestSync 内。</summary>
    private static void TryPatchMethod(Type target, string method, System.Reflection.MethodInfo mi,
        string prefix = null, string postfix = null)
    {
        try
        {
            if (mi == null)
            {
                CoopRuntime.LogSource?.LogWarning($"Harmony: cannot find {target.Name}.{method}");
                return;
            }
            var pre = prefix != null ? AccessTools.Method(typeof(GameSync.NestSync), prefix) : null;
            var post = postfix != null ? AccessTools.Method(typeof(GameSync.NestSync), postfix) : null;
            _harmony.Patch(mi,
                prefix: pre != null ? new HarmonyMethod(pre) : null,
                postfix: post != null ? new HarmonyMethod(post) : null);
            CoopRuntime.LogSource?.LogInfo($"Harmony: patched {target.Name}.{method} (explicit args)");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony: {target.Name}.{method} patch failed: {ex.Message}"); }
    }

    /// <summary>⚠️ 2026-08-26：.Charge Dial 拨动值变化诊断（值源排查）。
    /// DialValueEventWatcher.HandleDialValueChanged(float value) 是拨动值变化的事件回调——patch postfix
    /// 读三个候选字段（accumulatedValue / currentRotationAngle / detentCurrentAngle）+ 注册状态 + 路径，
    /// 确认 Charge Dial（弹药类型选择棘爪档位盘）到底哪个字段反映拨动位置（当前统一读 accumulatedValue，
    /// 若棘爪盘不走它 → 偶发不同步）。路由到 sync.log（key=chargedial.diag）。</summary>
    private static void PostDialValueChanged(DialValueEventWatcher __instance, float value)
    {
        try
        {
            if (__instance == null) return;
            var d = __instance.dial;
            if (d == null || d.transform == null) return;
            string p = "?";
            try { p = PathOfSync(d.transform); } catch { }
            // 只诊断 Charge Dial / Magazine Selection（弹药类型选择）
            if (p.IndexOf("Charge Dial", StringComparison.OrdinalIgnoreCase) < 0
                && p.IndexOf("Magazine Selection", StringComparison.OrdinalIgnoreCase) < 0
                && p.IndexOf("Ballistic", StringComparison.OrdinalIgnoreCase) < 0)
                return;
            float av = 0f, rot = 0f, det = 0f, crt = 0f;
            try { av = d.accumulatedValue; } catch { }
            try { rot = d.currentRotationAngle; } catch { }
            try { det = d.detentCurrentAngle; } catch { }
            try { crt = d.currentRotationAngle; } catch { }
            bool drag = false;
            try { drag = d.isDragging; } catch { }
            CoopLog.Info("chargedial.diag", () =>
                $"[ChargeDialDiag] evVal={value:0.###} av={av:0.###} rot={rot:0.###} det={det:0.###} drag={drag} path='{p}'", 0.25f);
        }
        catch { }
    }

    /// <summary>HarmonyPatches 内路径辅助（取 transform 完整路径，限深度防爆）。</summary>
    private static string PathOfSync(UnityEngine.Transform t)
    {
        if (t == null) return "";
        try
        {
            string path = t.name ?? "";
            var par = t.parent;
            int dep = 0;
            while (par != null && dep < 10) { path = (par.name ?? "") + "/" + path; par = par.parent; dep++; }
            return path;
        }
        catch { return t.name ?? ""; }
    }

    private static bool PreHandleInput()
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return true;   // 主机正常输入
        if (net.State == SessionState.Joined && net.Local?.Role == CrewRole.Gunner) return true; // 瞄准手保留本地输入
        return false;                                  // 其余客户端：跳过本地输入，由主机复制驱动
    }

    private static void PostFireShell(GunController __instance)
    {
        // ⚠️ 2026-09-05 诊断：确认玩家开火到底走不走 FireShell（决定 GunFire 事件是否有效）。
        // 之前日志显示"炮弹生成(ShellVisual.Initialize)紧跟在 Starter chain 点击后且 FireShell 无输出"，
        // 但无法区分是"玩家拉 Trigger chain 开火不走 FireShell"还是"Starter chain 启动引擎的炮击不走 FireShell"。
        try { CoopRuntime.LogSource?.LogInfo($"[TurretSync] FireShell fired gun='{__instance?.name}'"); } catch { }
        LogShotFireDiag(__instance);
        // A1：脚本化模块事件——炮弹发射（两端本地自然触发，脚本模块可订阅 gun.fired）
        try { OpenNestCoop.GameSync.OncMissionBridge.Raise("gun.fired"); } catch { }
        // 方案感知：--sync new 走 SyncV2 EventLayer（MsgType=200）；默认 old 走 V1 TurretSync（MsgType=11）。
        if (OpenNestCoop.Net.AutoJoin.WantNewSync)
        {
            SyncV2.EventLayer.Instance.OnLocalShellFired(__instance);
            return;
        }
        TurretSync.OnLocalGunFired(__instance);
    }

    private static bool PreRequestFire(GunController __instance)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return true;          // 主机正常开火
        if (net.State != SessionState.Joined) return true;   // 单机/未联机（Idle 等）：正常开火，不拦截
        if (ReloadSync.IsApplyingFire || SyncV2.EventLayer.IsApplyingFire) return true; // 网络复现放行（V1/V2 防环）
        if (OpenNestCoop.Net.AutoJoin.WantNewSync)
        {
            // V2：客机本地开火 → 请求事件上行主机（主机执行 + 广播复现，防双开火/绕过装填冷却）
            SyncV2.EventLayer.Instance.OnLocalFireRequest(__instance);
            return false;
        }
        ReloadSync.SendFireRequest(__instance);              // V1：客户端本地开火：上行请求，拦截本地执行
        return false;
    }

    /// <summary>⚠️ 2026-09-05 诊断：RequestFire 执行后打印 gun 关键状态——对比主机 TriggerFire→RequestFire→
    /// FireShell 触发 vs 客机 OnGunFire→RequestFire→无 FireShell 时 GunController 内部状态差在哪
    /// （定位 FireShell 不触发的断点；纯只读，不改机制）。</summary>
    private static void PostRequestFire(GunController __instance)
    {
        try
        {
            var net = CoopRuntime.Net;
            string role = (net == null || !net.IsHost) ? "CLIENT" : "HOST";
            string can = "?", shell = "?", st = "?";
            try { can = __instance.CanFire ? "T" : "F"; } catch { }
            try { shell = __instance.ChamberedShellBlueprint != null ? "Y" : "N"; } catch { }
            try { if (__instance.artilleryReloadController != null) st = __instance.artilleryReloadController.currentStateIndex.ToString(); } catch { }
            // ⚠️ fireSequenceStage 仅 BepInEx interop 有，MLL 无 → 不走该字段诊断（两端源码共享需双平台可编译）
            CoopRuntime.LogSource?.LogInfo($"[TurretSync] RequestFire({role}) gun='{__instance?.name}' canFire={can} chambered={shell} st={st}");
            // ⚠️ 2026-09-12：客机漏开火根因——膛内无弹时 FireShell 不会触发 → 本发无炮弹/无落点/无照片。
            // 用日志把"漏发"变成显式事件（st=装填状态机索引，便于对照主机装填流程到哪一步断）。
            if (shell == "N" && (net == null || !net.IsHost))
                CoopRuntime.LogSource?.LogWarning($"[TurretSync] CLIENT chamber EMPTY at fire → FireShell 不会触发（本发无炮弹）st={st}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony post request fire: {ex.Message}"); }
    }

    private static void PreSpawnOne(CounterBatteryCinematicImpactSpawner __instance)
    {
        // A1：脚本化模块事件——反炮兵来袭（脚本模块可订阅 counterbattery）
        try { OpenNestCoop.GameSync.OncMissionBridge.Raise("counterbattery"); } catch { }
        try
        {
            if (OpenNestCoop.Net.AutoJoin.WantNewSync) { SyncV2.CounterBatterySyncV2.Instance.OnLocalSpawn(); return; }
            CounterBatterySync.Instance?.OnLocalSpawn();
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PreSpawnOne: {ex.Message}"); }
    }

    // ---------------- 着弹/炮弹诊断（2026-08-23）----------------
    // 用户反馈"只打两发但着弹多触发/侦察照片多触发"。计数定位：
    //   ShellVisual.Initialize         —— 炮弹对象创建次数（应 = 实际发射数）
    //   ImpactTracker.EvaluateImpact   —— 着弹评估次数（static 方法；每发炮弹着弹应调 1 次）
    //   ShellVisual.SpawnImpactEffectAt—— 着弹特效生成次数（每发炮弹着弹应调 1 次）
    //   ImpactLocation.EvaluateAndReport / ReportLocationNextFrame —— 着弹报告路径
    // 实测（2026-08-23）：2 发炮弹（Initialize=2）→ EvaluateImpact 3 次（n=1,2 同时同位置=2发齐射；
    // n=3 在 5 秒后位置不同 = 多余一次）→ 需堆栈/时间戳定位第 3 次来源。
    private static int _impactEvalCount, _shellInitCount, _impactFxCount, _evalReportCount, _reportNextFrameCount;

    private static void PostEvaluateImpact(UnityEngine.Vector2 __1)
    {
        // ⚠️ 2026-08-25：落点位置同步改由 PostEvalReport 广播落点标记本地位置（EvaluateImpact loc 坐标系不确定，
        // PositionInRootSpace 用 loc 摆错）。EvaluateImpact 本身不再广播。
        // ⚠️ 2026-08-26：恢复**计数诊断**（不发包）——用户"一发着弹触发多次"：每发炮弹着弹应调 EvaluateImpact 1 次，
        // 若多次 → 炮弹重复着弹/多路径评估。计数 + 时间戳（对比 ShellVisual.Initialize 发射计数）。
        _impactEvalCount++;
        try { CoopLog.Debug("impact.diag", () => $"[ImpactDiag] EvaluateImpact n={_impactEvalCount} t={UnityEngine.Time.time:0.00} loc=({__1.x:0.00},{__1.y:0.00})"); } catch { }
    }
    private static void PostShellInit(ShellVisual __instance)
    {
        _shellInitCount++;
        LogShotInitDiag(__instance);
        try { GameSync.ShotSync.Instance?.OnLocalInit(__instance); } catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ShotSync hook: {ex.Message}"); }
    }
    private static void PostSpawnImpact()
    {
        _impactFxCount++;
        try { CoopLog.Debug("impact.diag", () => $"[ImpactDiag] SpawnImpactEffectAt n={_impactFxCount} t={UnityEngine.Time.time:0.00}"); } catch { }
    }

    // ---------------- 发射参数诊断（2026-09-12）----------------
    // 背景：用户质疑“开火参数明明都是一致的，客机落点怎么会不一致”——实测两端落点差 ~0.8、
    // 飞行时长差 ~1.2s（≈4%）= 典型的“发射参数小幅不同”。两个诊断分别回答：
    //   1) [ShotDiag] fire：开火那一刻两端的**掷瞄/仰角/装药/射程**是否真的相同（客机复现开火用的是
    //      它自己那一刻的本地物理状态，仰角是随时间向目标值靠的物理量）。
    //   2) [ShotDiag] init：这发炮弹**实际的弹道参数**（起点/终点/飞行时长/路径长）两端是否相同。
    // 若 init 的 target 不同 → 根因在发射参数（下游落点标记/追踪器/照片角度全是它生的）→ 由
    // ShotSync（发射参数同步）在源头修正，而不是事后把结果改回来。
    private static void LogShotFireDiag(GunController gun)
    {
        try
        {
            if (gun == null) return;
            var net = CoopRuntime.Net;
            string role = (net == null || !net.IsHost) ? "CLIENT" : "HOST";
            string shell = "?";
            string speed = "?";
            try
            {
                var bp = gun.ChamberedShellBlueprint;
                if (bp != null && bp.shellDefinition != null) shell = bp.shellDefinition.ShellId ?? "?";
                if (bp != null) speed = bp.GetAdjustedShellSpeed().ToString("0.000");
            }
            catch { }
            float elev = 0f, desElev = 0f, elevErr = 0f, range = 0f, ang = 0f, desRot = 0f;
            int powder = -1;
            try { elev = gun.CurrentElevation; } catch { }
            try { desElev = gun.DesiredElevationAngle; } catch { }
            try { elevErr = gun.ElevationErrorDeg; } catch { }
            try { range = gun.CurrentRange; } catch { }
            try { powder = gun.PowderCharges; } catch { }
            try { var tt = TurretController.Instance; if (tt != null) { ang = tt.CurrentAngle; desRot = tt.DesiredRotation; } } catch { }
            string fp = "?", fpw = "?", fpp = "?";
            try
            {
                var f = gun.firePoint;
                if (f != null)
                {
                    var p = f.localPosition; fp = $"({p.x:0.000},{p.y:0.000},{p.z:0.000})";
                    var wp = f.position; fpw = $"({wp.x:0.000},{wp.y:0.000},{wp.z:0.000})";
                    fpp = PathOfSync(f);
                }
            }
            catch { }
            string nm = gun.name ?? "?";
            CoopLog.Debug("shot.diag", () => $"[ShotDiag] fire role={role} gun='{nm}' shell='{shell}' speed={speed} turretAng={ang:0.0000} desRot={desRot:0.0000} elev={elev:0.0000} desElev={desElev:0.0000} elevErr={elevErr:0.0000} range={range:0.000} powder={powder} firePoint={fp} firePointW={fpw} firePointPath='{fpp}'");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ShotDiag fire: {ex.Message}"); }
    }

    /// <summary>这发炮弹的实际弹道参数（两端对照）。</summary>
    private static void LogShotInitDiag(ShellVisual sv)
    {
        try
        {
            var net = CoopRuntime.Net;
            string role = (net == null || !net.IsHost) ? "CLIENT" : "HOST";
            if (sv == null) return;
            string shell = "?";
            try { var d = sv.impactShell; if (d != null) shell = d.ShellId ?? "?"; } catch { }
            Vector2 s = default, t = default;
            float dur = 0f, dist = 0f;
            try { s = sv.startLocalPos; t = sv.targetLocalPos; dur = sv.travelTime; dist = sv.totalPathDistance; } catch { }
            int n = _shellInitCount;
            // 弹道坐标系诊断：着弹坐标是 **boardRect 的本地坐标**；若两端 board 不同（世界位置/缩放/父对象），
            // 则 start/target 会整体平移（实测两端固定差 (-1.100,-0.100)，且 aim/dur/dist 完全相同）。
            string board = "?", par = "?", wS = "?", wT = "?";
            try
            {
                if (sv.transform.parent != null) par = sv.transform.parent.name ?? "?";
                var br = sv.boardRect;
                if (br != null)
                {
                    var bp = br.position; var bs = br.lossyScale;
                    board = $"{br.name}@{bp.x:0.000},{bp.y:0.000},{bp.z:0.000}s({bs.x:0.000},{bs.y:0.000},{bs.z:0.000})";
                    var ws = br.TransformPoint(new Vector3(s.x, s.y, 0f));
                    var wt = br.TransformPoint(new Vector3(t.x, t.y, 0f));
                    wS = $"({ws.x:0.000},{ws.y:0.000},{ws.z:0.000})";
                    wT = $"({wt.x:0.000},{wt.y:0.000},{wt.z:0.000})";
                }
            }
            catch { }
            CoopLog.Debug("shot.diag", () => $"[ShotDiag] init role={role} n={n} t={UnityEngine.Time.time:0.00} shell='{shell}' start=({s.x:0.000},{s.y:0.000}) target=({t.x:0.000},{t.y:0.000}) dur={dur:0.000} dist={dist:0.000} board='{board}' parent='{par}' wStart={wS} wTarget={wT}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ShotDiag init: {ex.Message}"); }
    }    private static void PostEvalReport(ImpactLocation __instance)
    {
        try
        {
            // ⚠️ 2026-08-25：落点位置同步——主机广播落点标记本地位置，客户端把标记本地位置设为主机值
            // （两端父对象一致 → 本地坐标通用；绕开 EvaluateImpact loc 坐标系不确定）
            if (OpenNestCoop.Net.AutoJoin.WantNewSync) return;
            if (__instance == null || __instance.transform == null) return;
            var net = CoopRuntime.Net;
            // 落点标记父链诊断（确认 ImpactLocation_AP 是不是战术地图落点标记）
            string pth = "?";
            try
            {
                var tr2 = __instance.transform;
                var sb = new System.Text.StringBuilder(tr2.name ?? "");
                int dep = 0;
                while (tr2.parent != null && dep < 6) { tr2 = tr2.parent; sb.Insert(0, (tr2.name ?? "") + "/"); dep++; }
                pth = sb.ToString();
            }
            catch { }
            var lpD = __instance.transform.localPosition;
            CoopLog.Info("impact.sync4", () => $"[ImpactSync] EvalReport path='{pth}' local=({lpD.x:0.00},{lpD.y:0.00}) parentW=({__instance.transform.position.x:0.00},{__instance.transform.position.z:0.00})", 0.5f);
            if (net != null)
            {
                // ⚠️ 2026-09-12 重新设计：落点/照片角度/追踪目标统一交给 ImpactSync：
                // 主机照游戏原样跑（不改自己的值），排队广播"自己的落点 + 游戏真实 roll 出的照片角度"；
                // 客机在【本地着弹报告后】与【主机包到达后】各写一次同一个权威值（幂等，无定时窗口）。
                GameSync.ImpactSync.Instance?.NotifyLocalReport(__instance);
            }
        }
        catch { }
    }
    private static void PostReportNextFrame()
    {
        _reportNextFrameCount++;
        try { CoopLog.Debug("impact.diag", () => $"[ImpactDiag] ImpactLocation.ReportLocationNextFrame n={_reportNextFrameCount} t={UnityEngine.Time.time:0.00}"); } catch { }
    }

    private static bool PreLookClick(LookAtTarget __instance)
    {
        // 必须 try/catch：prefix 抛异常会中断原方法 OnClickDown → 后续逻辑（OnChargeButtonPressed 等）不执行
        try
        {
            // 方案感知：--sync new 走 SyncV2 ButtonLayer（点击→EventLayer，Operator 权威）；默认 old 走 V1 ButtonClickSync。
            if (OpenNestCoop.Net.AutoJoin.WantNewSync)
                SyncV2.ButtonLayer.Instance.OnLocalClick(__instance);
            else
                ButtonClickSync.OnLocalClick(__instance);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony OnLocalClick: {ex.Message}"); }
        return true; // 继续原方法（点击正常执行）
    }

    /// <summary>发射台序列 slot 点击（LookAtTargetUnlockSequence5.HandleSlotClicked）→ 交互事件上报
    /// （对端执行同样解锁逻辑 + 动画）。数值对账由 SequenceSync 主机权威广播。V2 走 SequenceSyncV2（谁变化谁广播）。</summary>
    private static void PreSeqSlotClick(LookAtTargetUnlockSequence5 __instance, int __0)
    {
        // A1：脚本化模块事件——发射台序列槽点击（载荷 = slot 序号）
        try { OpenNestCoop.GameSync.OncMissionBridge.Raise("interact.slot", __0); } catch { }
        try
        {
            if (OpenNestCoop.Net.AutoJoin.WantNewSync) return; // V2 由 SequenceSyncV2 处理
            SequenceSync.OnLocalSlotClick(__instance, __0);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony seq slot click: {ex.Message}"); }
    }
    private static void PrePowderSelect(PowderChargeController __instance, int __0)
    {
        try
        {
            if (OpenNestCoop.Net.AutoJoin.WantNewSync) { SyncV2.ReloadSyncV2.Instance.OnLocalPowderSelect(__instance, __0); return; }
            ReloadSync.OnLocalPowderSelect(__instance, __0);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony powder select: {ex.Message}"); }
    }
    private static void PrePowderLoad(PowderChargeController __instance)
    {
        try
        {
            if (OpenNestCoop.Net.AutoJoin.WantNewSync) { SyncV2.ReloadSyncV2.Instance.OnLocalPowderLoad(__instance); return; }
            ReloadSync.OnLocalPowderLoad(__instance);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony powder load: {ex.Message}"); }
    }

    // ---------------- 仰角联动（GunElevationLinkCoordinator，事件驱动）----------------
    // postfix：本地切换联动后广播 isLinked（GunLinkSync.OnLocalSetLinked 变化才广播 + 去重）。
    private static void PostGunLinkSet(GunElevationLinkCoordinator __instance, bool linked, bool doInitialSync)
    {
        try { GunLinkSync.OnLocalSetLinked(__instance); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony gunlink set: {ex.Message}"); }
    }
    private static void PostGunLinkToggle(GunElevationLinkCoordinator __instance)
    {
        try { GunLinkSync.OnLocalSetLinked(__instance); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony gunlink toggle: {ex.Message}"); }
    }

    // ---------------- 预备激发（ArmedFireRelayOneShot，状态同步 2026-09-01）----------------
    // postfix：方法执行后读 IsLeftArmed/IsRightArmed 广播状态（非事件/点击）。IsApplyingArm 防环。
    // 点击实际触发 ToggleLeft/ToggleRight（切换语义），故 patch 全部 6 个方法（含 Toggle）。
    // ⚠️ postfix 抛异常不影响原方法，但仍 try/catch 吞掉防日志刷屏。

    private static void PostArmLeft(Zagreekie.Tools.ArmedFireRelayOneShot __instance)
    {
        try { if (OpenNestCoop.Net.AutoJoin.WantNewSync) return; ArmSync.OnLocalArmState(__instance); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony arm left: {ex.Message}"); }
    }
    private static void PostArmRight(Zagreekie.Tools.ArmedFireRelayOneShot __instance)
    {
        try { if (OpenNestCoop.Net.AutoJoin.WantNewSync) return; ArmSync.OnLocalArmState(__instance); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony arm right: {ex.Message}"); }
    }
    private static void PostDisarmLeft(Zagreekie.Tools.ArmedFireRelayOneShot __instance)
    {
        try { if (OpenNestCoop.Net.AutoJoin.WantNewSync) return; ArmSync.OnLocalArmState(__instance); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony disarm left: {ex.Message}"); }
    }
    private static void PostDisarmRight(Zagreekie.Tools.ArmedFireRelayOneShot __instance)
    {
        try { if (OpenNestCoop.Net.AutoJoin.WantNewSync) return; ArmSync.OnLocalArmState(__instance); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony disarm right: {ex.Message}"); }
    }
    private static void PostToggleLeft(Zagreekie.Tools.ArmedFireRelayOneShot __instance)
    {
        try { if (OpenNestCoop.Net.AutoJoin.WantNewSync) return; ArmSync.OnLocalArmState(__instance); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony toggle left: {ex.Message}"); }
    }
    private static void PostToggleRight(Zagreekie.Tools.ArmedFireRelayOneShot __instance)
    {
        try { if (OpenNestCoop.Net.AutoJoin.WantNewSync) return; ArmSync.OnLocalArmState(__instance); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony toggle right: {ex.Message}"); }
    }
    private static void PostTriggerFire(Zagreekie.Tools.ArmedFireRelayOneShot __instance)
    {
        // ⚠️ 2026-09-05 诊断：确认"拉激发拉环(Trigger chain)"是否触发 TriggerFire（炮系统开火入口）。
        // ⚠️ 状态同步由 OnLocalArmState 完成：TriggerFire 执行后读 armed 状态，变化才广播（非强制）。
        try { CoopRuntime.LogSource?.LogInfo($"[TurretSync] TriggerFire fired relay='{__instance?.name}'"); } catch { }
        try { if (OpenNestCoop.Net.AutoJoin.WantNewSync) return; ArmSync.OnLocalArmState(__instance); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony trigger fire: {ex.Message}"); }
    }

    // ---------------- 弹舱动作（CylinderShellSelector，事件解耦 P1）----------------
    // prefix：先广播再放行原方法（本地正常执行 + 对端复现）。IsApplyingCylinder 防环。

    private static void PreCylLoadShell(CylinderShellSelector __instance)
    {
        try { if (OpenNestCoop.Net.AutoJoin.WantNewSync) return; CylinderActionSync.OnLocalAction(__instance, CylinderActionSync.C_EvLoadShell); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony cyl load shell: {ex.Message}"); }
    }
    private static void PreCylMoveCylinder(CylinderShellSelector __instance)
    {
        try { if (OpenNestCoop.Net.AutoJoin.WantNewSync) return; CylinderActionSync.OnLocalAction(__instance, CylinderActionSync.C_EvMoveCylinder); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony cyl move cylinder: {ex.Message}"); }
    }

    /// <summary>拦截 PauseManager.OnApplicationFocus：联机时失焦不暂停（跳过原方法）。</summary>
    private static bool PreAppFocus(PauseManager __instance, bool __0)
    {
        var net = CoopRuntime.Net;
        bool online = net != null && (net.State == SessionState.Hosting || net.State == SessionState.Joined);
        if (!online) return true; // 非联机：正常行为
        if (__0) return true;     // 获得焦点：正常
        // 失焦且联机：阻止暂停，强制恢复时间流速
        try { PauseManager.PauseOnFocusLoss = false; } catch { }
        try { Time.timeScale = 1f; } catch { }
        return false;
    }

    private static bool PreRegisterChild(MapReconClearHandle __instance, GameObject child)
    {
        try
        {
            if (OpenNestCoop.Net.AutoJoin.WantNewSync) { SyncV2.ReconPhotoSyncV2.Instance.OnLocalPhoto(); return true; }
            // ⚠️ 2026-08-25：传 child（照片对象）→ ReconPhotoSync 主机权威同步照片位置（seed 只同步内容，位置各自算不同步）
            ReconPhotoSync.Instance?.OnLocalPhoto(child);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PreRegisterChild: {ex.Message}"); }
        return true; // 继续原方法（生成照片对象）
    }

    /// <summary>照片生成后（RegisterChild postfix）：禁用照片的 RandomUIRotation 组件 + 重置旋转——
    /// 照片带 RandomUIRotation（Awake 用 Unity 内部随机旋转，UnityEngine.Random.InitState 无法控制 →
    /// 两端照片角度不同）。prefix 时子对象可能未生成，放 postfix 处理（此时 Awake 已执行，需重置旋转）。</summary>
    private static void PostRegisterChild(MapReconClearHandle __instance, GameObject child)
    {
        try
        {
            var rrs = child != null ? child.GetComponentsInChildren<RandomUIRotation>(true) : null;
            int n = rrs != null ? rrs.Length : 0;
            // 诊断：照片对象结构 + RandomUIRotation 数量（照片方向不同步定位）——photo.angle 路由到 sync.log
            string nm = "";
            try { if (child != null && child.transform != null) nm = child.transform.name; } catch { }
            CoopLog.Info("photo.angle", () => $"[PhotoAngle] RegisterChild child='{nm}' randomUIRotation={n}", 1f);
            if (rrs != null)
                foreach (var rr in rrs)
                {
                    if (rr == null) continue;
                    try { rr.enabled = false; } catch { }
                    try { rr.transform.localRotation = Quaternion.identity; } catch { }
                }
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostRegisterChild: {ex.Message}"); }
    }

    // ---------------- 任务过渡事件（完成/失败/重载/回菜单） ----------------
    // prefix：先上报同步，再放行原方法（两端各自执行任务逻辑，事件广播保证跨端一致触发）。
    // ⚠️ 必须 try/catch：prefix 抛异常会中断原方法（如 FinishMission 后续结算流程）。

    private static bool PreMissionFinish()
    {
        NotifyMissionEvent(MissionEventSync.EvFinish, false);
        return true;
    }

    private static bool PreMissionComplete(bool __0)
    {
        NotifyMissionEvent(MissionEventSync.EvComplete, __0);
        return true;
    }

    private static bool PreMissionFailed(bool __0)
    {
        NotifyMissionEvent(MissionEventSync.EvFailed, __0);
        return true;
    }

    private static bool PreMissionReload()
    {
        NotifyMissionEvent(MissionEventSync.EvReload, false);
        return true;
    }

    private static bool PreMissionReturnMap()
    {
        NotifyMissionEvent(MissionEventSync.EvReturnMap, false);
        return true;
    }

    private static bool PreMissionEndOperation()
    {
        NotifyMissionEvent(MissionEventSync.EvEndOperation, false);
        return true;
    }

    /// <summary>任务过渡事件通知（方案感知）：--sync new 走 MissionEventSyncV2（EventLayer），默认 old 走 V1。</summary>
    private static void NotifyMissionEvent(byte ev, bool flag)
    {
        try
        {
            if (OpenNestCoop.Net.AutoJoin.WantNewSync) { SyncV2.MissionEventSyncV2.Instance.OnLocalEvent(ev, flag); return; }
            MissionEventSync.OnLocalEvent(ev, flag);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony mission event: {ex.Message}"); }
    }

    /// <summary>补给购买（Supply console）：拉征用杆 → AttemptRequisition。
    /// 主机：放行本地执行 + 广播；客机：拦截本地执行，上报主机（主机权威购买，避免两端重复扣点/双效果）。</summary>
    private static bool PreRequisition(RequisitionSlot __instance)
    {
        // A1：脚本化模块事件——补给购买/征用点消耗（脚本模块可订阅 requisition.spent）
        try { OpenNestCoop.GameSync.OncMissionBridge.Raise("requisition.spent"); } catch { }
        try
        {
            if (OpenNestCoop.Net.AutoJoin.WantNewSync) return SyncV2.PurchaseSyncV2.Instance.OnLocalPurchase(__instance);
            return PurchaseSync.OnLocalPurchase(__instance);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PreRequisition: {ex.Message}"); }
        return true;
    }

    /// <summary>卡牌入槽：ItemSlot.PlaceItem 后 → 广播（对端执行 PlaceItem，槽位 CurrentItem 两端一致）。</summary>
    private static void PostItemSlotPlace(ItemSlot __instance, DraggableItem __0)
    {
        try
        {
            if (OpenNestCoop.Net.AutoJoin.WantNewSync) { SyncV2.PunchcardSyncV2.Instance.OnLocalPlaceCard(__instance, __0); return; }
            PunchcardSync.OnLocalPlaceCard(__instance, __0);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostItemSlotPlace: {ex.Message}"); }
    }

    /// <summary>卡牌出槽：ItemSlot.RemoveItem 后 → 广播（对端执行 RemoveItem）。</summary>
    private static void PostItemSlotRemove(ItemSlot __instance, DraggableItem __0)
    {
        try
        {
            if (OpenNestCoop.Net.AutoJoin.WantNewSync) { SyncV2.PunchcardSyncV2.Instance.OnLocalRemoveCard(__instance, __0); return; }
            PunchcardSync.OnLocalRemoveCard(__instance, __0);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostItemSlotRemove: {ex.Message}"); }
    }

    /// <summary>任务随机内容一致：FireMission.GenerateMission 生成目标前，应用主机 seed。</summary>
    private static void PreFireMissionGenerate(FireMission __instance)
    {
        try
        {
            // ⚠️ 2026-08-26 诊断：GenerateMission 触发时 dump seed 应用状态 + 当前 Entities 数——
            // 定位“客机实体少（23 vs 主机 31）”：两端同 seed 应生成相同实体，若客机 GenerateMission 时
            // seed 未应用（fixedSeed=0/useFixedSeed=false）→ 随机不同 → 实体数不同。
            int entCount = -1;
            try { if (__instance != null && __instance.Entities != null) entCount = __instance.Entities.Count; } catch { }
            int fseed = -1; bool ufix = false;
            try { fseed = (int)__instance.fixedSeed; } catch { }
            try { ufix = __instance.useFixedSeed; } catch { }
            CoopRuntime.LogSource?.LogInfo($"[MissionSync] GenerateMission fired entities={entCount} useFixedSeed={ufix} fixedSeed={fseed} pending={OpenNestCoop.GameSync.MissionSync.PendingSeed}");
            if (OpenNestCoop.Net.AutoJoin.WantNewSync) { SyncV2.MissionSyncV2.ApplyPendingSeedTo(__instance); return; }
            MissionSync.ApplyPendingSeedTo(__instance);
            int fseed2 = -1;
            try { fseed2 = (int)__instance.fixedSeed; } catch { }
            CoopRuntime.LogSource?.LogInfo($"[MissionSync] GenerateMission after-apply fixedSeed={fseed2} entities={entCount}");
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PreFireMissionGenerate: {ex.Message}"); }
    }

    /// <summary>列车/移动目标（FireMission.MoveMapEntity 连续移动）：同步移动命令本身——
    /// 主机触发时广播移动参数（EntityMoveSync），客机用相同参数本地执行 → 两端原生插值轨迹一致。
    /// 客机本地任务图触发被拦截（防两端各自时间戳插值漂移）；广播驱动执行（IsApplyingRemote）放行。
    /// ⚠️ 无参 prefix 同时匹配 4参/6参重载（Harmony 按需注入 entity/worldPos 等参数）。
    /// ⚠️ 2026-09-04：任务图移动节点实际不经过 MoveMapEntity（此 patch 从未触发，保留作兜底）。</summary>
    private static bool PreMoveMapEntity(MapEntity entity, Vector3 worldPos, bool continousMovement, float timespan)
    {
        try
        {
            var net = CoopRuntime.Net;
            if (net != null && net.IsHost)
            {
                // 主机：广播移动命令（客机用相同参数执行原生移动），放行本地移动
                try { if (entity != null) EntityMoveSync.Broadcast(entity.ID, worldPos.x, worldPos.z, continousMovement, timespan, false, 0); } catch { }
                return true;
            }
            // 客机：广播驱动执行放行；本地任务图触发拦截
            if (EntityMoveSync.IsApplyingRemote) return true;
            return false;
        }
        catch { }
        return true;
    }

    /// <summary>任务打字机通知：ShowNotification 被调用后 → 主机广播（title/description/lifetime）。</summary>
    private static void PostShowNotification(string __0, string __1, float __2)
    {
        // A1：脚本化模块事件——UI 通知出现（载荷 = 标题）
        try { OpenNestCoop.GameSync.OncMissionBridge.Raise("notification.shown", __0); } catch { }
        try
        {
            if (OpenNestCoop.Net.AutoJoin.WantNewSync) { SyncV2.NotificationSyncV2.Instance.OnLocalShow(__0, __1, __2); return; }
            NotificationSync.OnLocalShow(__0, __1, __2);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostShowNotification: {ex.Message}"); }
    }

    /// <summary>客机本地 SubmitLines 抑制（V1/V2 一致，2026-08-15 修复 V1 双份打字机）：
    /// 打字机内容由主机权威广播（EvPrint/EvState），客机本地任务图/同 seed 打印会与主机内容
    /// 重复且不一致（“打字机打两份”根因——V1 此前不拦截导致客机打两遍且内容不同）。
    /// 网络复现（IsApplying=true）放行（V1=TeleprinterSync.IsApplying，V2=TeleprinterSyncV2.IsApplying）；
    /// 主机正常打印（广播权威内容）。</summary>
    private static bool PreTeleprinterPrint(Teleprinter __instance)
    {
        try
        {
            var net = CoopRuntime.Net;
            if (net == null) return true;                          // 无网络会话：正常打印
            // ⚠️ 单机（非联机状态，State=Idle 等）：必须放行本地打印——否则单人打字机无显示
            //（2026-08-15 修复：此前 net!=null 且 IsHost=false 时单机被误判为客机 → 本地打印被抑制）
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return true;
            if (net.IsHost) return true;                          // 联机主机正常打印（广播权威内容）
            // 网络复现放行（防环）：V1/V2 各自 IsApplying
            if (OpenNestCoop.Net.AutoJoin.WantNewSync)
            {
                if (SyncV2.TeleprinterSyncV2.IsApplying) return true;
            }
            else if (GameSync.TeleprinterSync.IsApplying)
            {
                return true;
            }
            return false;                                          // 联机客机本地打印：跳过（由主机 EvPrint/EvState 复现）
        }
        catch { return true; }
    }

    /// <summary>任务打字机打印：Teleprinter.SubmitLines 被调用后 → 主机广播打印文本行。</summary>
    private static void PostTeleprinterPrint(Teleprinter __instance, string __0, object __1)
    {
        // A1：脚本化模块事件——打字机打印（脚本模块可订阅 teleprinter.printed）
        try { OpenNestCoop.GameSync.OncMissionBridge.Raise("teleprinter.printed"); } catch { }
        try
        {
            // 提取打印行。⚠️ 修复（2026-08-13）：游戏传入的 __1 是 Il2CppSystem.Collections.Generic.
            // IEnumerable<string>（泛型接口，底层是 List<string>）。旧代码 `as Il2CppSystem.Collections.
            // IEnumerable`（非泛型接口）在 interop 下转换失败返回 null → 走 ToString() 分支 → 广播
            // 内容变成类型名 'Il2CppSystem.Collections.Generic.IEnumerable`1[System.String]'
            // → 客机打字机显示类型名。正确做法（闭源参考）：TryCast<List<string>>() 转回底层 List 遍历。
            var lines = new System.Collections.Generic.List<string>();
            try
            {
                if (__1 != null)
                {
                    // 尝试把 interop 对象转回底层 List<string>（游戏传入的 IEnumerable 底层是 List）
                    var backList = (__1 as Il2CppObjectBase)?.TryCast<Il2CppSystem.Collections.Generic.List<string>>();
                    if (backList != null)
                    {
                        for (int i = 0; i < backList.Count; i++)
                            lines.Add(backList[i] ?? "");
                    }
                    else
                    {
                        // 兜底：非泛型 IEnumerable 枚举（老版本/非 List 集合）
                        var e = __1 as Il2CppSystem.Collections.IEnumerable;
                        if (e != null)
                        {
                            var en = e.GetEnumerator();
                            while (true)
                            {
                                bool more;
                                try { more = en.MoveNext(); } catch { break; }
                                if (!more) break;
                                object obj;
                                try { obj = en.Current; } catch { obj = null; }
                                string s = obj == null ? "" : obj.ToString();
                                lines.Add(s);
                            }
                        }
                        else
                        {
                            // 非枚举：尝试 ToString
                            lines.Add(__1.ToString() ?? "");
                        }
                    }
                }
            }
            catch
            {
                // 枚举失败：退化为无内容打印（至少广播"有打印动作"）
                lines.Add("");
            }
            // 方案感知：--sync new 走 TeleprinterSyncV2（228），默认 old 走 V1 TeleprinterSync（134）
            if (OpenNestCoop.Net.AutoJoin.WantNewSync)
                SyncV2.TeleprinterSyncV2.Instance.OnLocalPrint(__instance, lines);
            else
                TeleprinterSync.OnLocalPrint(__instance, lines);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostTeleprinterPrint: {ex.Message}"); }
    }

    /// <summary>任务打字机追加：Teleprinter.AppendInstant 被调用后 → 广播追加文本块。</summary>
    private static void PostTeleprinterAppend(Teleprinter __instance, string __0, bool __1)
    {
        try { TeleprinterSync.OnLocalAppend(__instance, __0 ?? "", __1); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostTeleprinterAppend: {ex.Message}"); }
    }

    /// <summary>任务打字机清除：Teleprinter.ClearAll 被调用后 → 广播清除。</summary>
    private static void PostTeleprinterClearAll(Teleprinter __instance)
    {
        try { TeleprinterSync.OnLocalClearAll(__instance); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostTeleprinterClearAll: {ex.Message}"); }
    }

    /// <summary>任务打字机清报警：Teleprinter.ClearAlarm 被调用后 → 广播清报警。</summary>
    private static void PostTeleprinterClearAlarm(Teleprinter __instance)
    {
        try { TeleprinterSync.OnLocalClearAlarm(__instance); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostTeleprinterClearAlarm: {ex.Message}"); }
    }

    /// <summary>
    /// ⚠️ 自定义任务诊断：State_TeleprinterText.OnEnter 执行现场（prefix，不拦截）。
    /// ⚠️ 只用 __instance（State_TeleprinterText 具体类型），不带 __1（NodeExecutionState 嵌套参数
    /// 会让 Harmony IL 编译失败/崩溃——已证）。确认节点被调、Text、打字机、EntityIDToReplace。
    /// </summary>
    private static void PreTeleprinterNodeEnter(SleepyNodes.State_TeleprinterText __instance)
    {
        try
        {
            string nid = "?";
            try { nid = __instance?.NodeID ?? "?"; } catch { }
            string textInfo = "?";
            try
            {
                var tv = __instance.Text;
                textInfo = tv == null ? "(null)" : $"{tv.GetType().Name}/raw='{Truncate(tv.Raw)}'";
            }
            catch (System.Exception ex) { textInfo = "err(" + ex.Message + ")"; }
            string printer = "?";
            string printerFound = "?";
            try
            {
                var p = __instance.Printer;
                printer = p.ToString();
                try { printerFound = Teleprinter.GetTeleprinter(p) != null ? "FOUND" : "null"; }
                catch (System.Exception pe) { printerFound = "err(" + pe.Message + ")"; }
            }
            catch (System.Exception ex) { printer = "err(" + ex.Message + ")"; }
            string alarm = "?";
            try { alarm = __instance.AlarmState.ToString(); } catch { }
            // ⚠️ 关键：EntityIDToReplace 是否为 null（OnEnter 遍历它做实体名替换 → null NRE）
            string entityInfo = "?";
            try
            {
                var el = __instance.EntityIDToReplace;
                entityInfo = el == null ? "(null)" : $"List[{el.Count}]";
            }
            catch (System.Exception ee) { entityInfo = "err(" + ee.Message + ")"; }
            CoopLog.Debug("mission.diag", () => $"[MissionDiag] State_TeleprinterText.OnEnter node='{nid}' text={textInfo} printer='{printer}' getPrinter={printerFound} alarm='{alarm}' onlyQueue={__instance.OnlyQueue} wait={__instance.WaitUntilComplete} entityIDs={entityInfo}");
            // ⚠️ 自定义任务 token 替换：原生 OnEnter 对 ImportMission 图可能不调 ProcessBlock（token 不替换）。
            // 对【自定义图】的 TeleprinterText，若 Text 含 '<' token，手动 ProcessBlock 替换（[GRID <turret>] → 铁巢坐标、
            // [POINT <实体>] → 实体位置），设回 Text → OnEnter 打印替换后的文本。原生任务不受影响（IsNativeCustomGraph 守卫）。
            try
            {
                var mm = MissionManager.Instance;
                var cur = mm?.CurrentMission;
                if (cur != null && OpenNestCoop.GameSync.OncMissionBridge.IsNativeCustomGraph(cur))
                {
                    // ⚠️ 2026-08-26 客机 NRE 修复：原生 OnEnter 的 ProcessBlock 会对 EntityIDToReplace 里的
                    // 实体 ID 做替换（TryResolveParameterToEntity），客机端实体可能未生成 → 找不到实体 → NRE
                    // （`State_TeleprinterText.OnEnter → ProcessBlock → TryResolveParameterToEntity` NRE →
                    // 打字机打印中断 → 打字针/文本偏移错位）。我们已手动 ProcessBlock 替换 Text（下面），
                    // 清空 EntityIDToReplace 让原生 ProcessBlock 不再做实体替换 → 不 NRE。自定义图专用，
                    // 原生任务不受影响（IsNativeCustomGraph 守卫）。
                    try
                    {
                        var el = __instance?.EntityIDToReplace;
                        if (el != null && el.Count > 0)
                        {
                            el.Clear();
                            CoopLog.Debug("mission.diag", () => $"[MissionDiag] cleared EntityIDToReplace node='{nid}'");
                        }
                    }
                    catch { }
                    if (__instance?.Text != null && __instance.Text.Raw != null && __instance.Text.Raw.IndexOf('<') >= 0)
                    {
                        var raw = __instance.Text.Raw;
                        var results = FireMissionTokenProcessor.ProcessBlock(raw);
                        if (results != null && results.Count > 0)
                        {
                            var sb = new System.Text.StringBuilder();
                            for (int li = 0; li < results.Count; li++)
                            {
                                if (sb.Length > 0) sb.Append('\n');
                                try { sb.Append(results[li] ?? ""); } catch { }
                            }
                            var joined = sb.ToString();
                            if (joined != raw)
                            {
                                try
                                {
                                    __instance.Text = new Localisation.TextIdentifier(joined);
                                    CoopLog.Debug("mission.diag", () => $"[MissionDiag] TokenReplace node='{nid}' BEFORE='{Truncate(raw, 80)}' AFTER='{Truncate(joined, 160)}'");
                                }
                                catch (System.Exception se) { CoopLog.Debug("mission.diag", () => $"[MissionDiag] TokenReplace node='{nid}' set error: {se.Message}"); }
                            }
                        }
                    }
                }
            }
            catch { }
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MissionDiag] PreTeleprinterNodeEnter: {ex.Message}"); }
    }

    /// <summary>诊断：State_SpawnMapEntity.OnEnter 执行现场（prefix 只读，不拦截）——确认生成节点是否被图驱动。</summary>
    private static void PreSpawnNodeEnter(SleepyNodes.State_SpawnMapEntity __instance)
    {
        try
        {
            string nid = "?";
            try { nid = __instance?.NodeID ?? "?"; } catch { }
            string eid = "?";
            try { eid = __instance?.ID ?? "?"; } catch { }
            string n = "?", r = "?", hp = "?", z = "?";
            try { n = __instance?.NumberToSpawn.ToString(); } catch { }
            try { r = __instance?.Role.ToString(); } catch { }
            try { hp = __instance?.Health.ToString(); } catch { }
            try { if (__instance?.LocationToSpawn != null) z = __instance.LocationToSpawn.ZoneID ?? "?"; } catch { }
            CoopLog.Debug("mission.diag", () => $"[MissionDiag] State_SpawnMapEntity.OnEnter node='{nid}' id='{eid}' num={n} role={r} hp={hp} zone='{z}'");
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MissionDiag] PreSpawnNodeEnter: {ex.Message}"); }
    }

    /// <summary>捕获 SubmitLines 的 waitForTrigger 参数（玩家靠近才打印的开关）+ lines 内容（看 token 是否替换）。</summary>
    private static void PreTeleprinterSubmitCapture(Teleprinter __instance, string __0, object __1, object __2, bool __3)
    {
        try
        {
            string content = "?";
            try
            {
                // __1 = IEnumerable<string> lines；读首行看 token 是否已替换
                var ie = __1 as System.Collections.IEnumerable;
                if (ie != null)
                {
                    var en = ie.GetEnumerator();
                    if (en.MoveNext())
                    {
                        var first = en.Current;
                        content = first == null ? "(null)" : Truncate(first.ToString(), 100);
                    }
                    else content = "(empty lines)";
                }
                else content = "(not-enumerable)";
            }
            catch { }
            CoopLog.Debug("tp.capture", () => $"[TPCapture] SubmitLines ptype={__instance?.TeleprinterType} sourceId='{__0}' waitForTrigger={__3} first='{content}'");
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[TPCapture] SubmitLines: {ex.Message}"); }
    }

    /// <summary>捕获 TryStart 调用（启动打印动画）+ 打字机状态（IsPrinting/revealed/队列）。</summary>
    private static void PreTeleprinterTryStartCapture(Teleprinter __instance, bool __0)
    {
        try
        {
            string from = "?"; try { from = new System.Diagnostics.StackTrace().GetFrame(2)?.GetMethod()?.Name; } catch { }
            string st = "?";
            try
            {
                bool ip = __instance.IsPrinting;
                int rv = 0; try { rv = __instance._currentRevealedCharIndex; } catch { }
                string rich = ""; try { rich = __instance._currentFullRich ?? ""; } catch { }
                st = $"isPrinting={ip} revealed={rv} richLen={rich.Length}";
            }
            catch { }
            CoopLog.Debug("tp.capture", () => $"[TPCapture] TryStart ptype={__instance?.TeleprinterType} ignoreInitialDelay={__0} caller={from} {st}");
        }
        catch { }
    }

    /// <summary>捕获 RunQueue 协程启动（逐字打印循环）。</summary>
    private static void PreTeleprinterRunQueueCapture(Teleprinter __instance)
    {
        try
        {
            string from = "?"; try { from = new System.Diagnostics.StackTrace().GetFrame(2)?.GetMethod()?.Name; } catch { }
            CoopLog.Debug("tp.capture", () => $"[TPCapture] RunQueue ptype={__instance?.TeleprinterType} caller={from}");
        }
        catch { }
    }

    /// <summary>捕获 ResumeCurrentJob 协程（玩家靠近后恢复打印）。</summary>
    private static void PreTeleprinterResumeCapture(Teleprinter __instance)
    {
        try
        {
            string from = "?"; try { from = new System.Diagnostics.StackTrace().GetFrame(2)?.GetMethod()?.Name; } catch { }
            CoopLog.Debug("tp.capture", () => $"[TPCapture] ResumeCurrentJob ptype={__instance?.TeleprinterType} caller={from}");
        }
        catch { }
    }

    private static string Truncate(string s, int max = 80)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    // ---------------- 玩家-猫交互事件（软同步） ----------------

    /// <summary>拾起：CatPickUpHandler.ExecutePickUp 后 → 广播（ev=1）。</summary>
    private static void PostCatPickUp(CatController cat)
    {
        NotifyCatEvent(cat, 1);
    }

    /// <summary>放下：CatPickUpHandler.ExecuteDrop 后 → 广播（ev=2）。</summary>
    private static void PostCatDrop(CatController cat)
    {
        NotifyCatEvent(cat, 2);
    }

    /// <summary>放下（兜底）：CatController.StopCarrying 后 → 广播（ev=2）。</summary>
    private static void PostCatStopCarrying(CatController __instance)
    {
        NotifyCatEvent(__instance, 2);
    }

    /// <summary>驱赶：CatController.ShooCat 后 → 广播（ev=3，对端 ShooCat(false) 复现）。</summary>
    private static void PostCatShoo(CatController __instance)
    {
        NotifyCatEvent(__instance, 3);
    }

    /// <summary>抚摸：CatController.PetTheCat 后 → 广播（ev=4）。</summary>
    private static void PostCatPet(CatController __instance)
    {
        NotifyCatEvent(__instance, 4);
    }

    /// <summary>打断：CatController.InterruptCat 后 → 广播（ev=5）。</summary>
    private static void PostCatInterrupt(CatController __instance)
    {
        NotifyCatEvent(__instance, 5);
    }

    /// <summary>猫交互事件通知（方案感知）：--sync new 走 SyncV2 CatSyncV2（EventLayer，Operator）；默认 old 走 V1 CatSync。</summary>
    private static void NotifyCatEvent(CatController cat, byte ev)
    {
        try
        {
            if (OpenNestCoop.Net.AutoJoin.WantNewSync)
            {
                if (cat != null) SyncV2.CatSyncV2.Instance.OnLocalCatEvent(cat, ev);
                return;
            }
            CatSync.OnLocalCatEvent(cat, ev);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony cat event: {ex.Message}"); }
    }

}
