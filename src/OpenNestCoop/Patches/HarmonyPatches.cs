using System;
using HarmonyLib;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;
using UnityEngine;

using OpenNestCoop.Core;
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
        TryPatch(typeof(GunController), "RequestFire", prefix: nameof(PreRequestFire));
        TryPatch(typeof(CounterBatteryCinematicImpactSpawner), "SpawnOne", prefix: nameof(PreSpawnOne));
        TryPatch(typeof(MapReconClearHandle), "RegisterChild", prefix: nameof(PreRegisterChild));
        // 按钮/拉杆点击统一入口：LookAtTarget.OnClickDown（所有交互按钮/拉杆的点击效果都在这里触发：
        // 拉杆动画 AnimatorBoolToggler + onClickDown UnityEvent 绑定逻辑（OnLoadChargesPressed/OnChargeButtonPressed
        // 等）+ 状态推进）。转发到对端 OnClickDown+OnClickUp → 完整复现（视觉+逻辑）。
        // 注意：不再 patch OnUserInput_Advance/Regress——点击转发后对端 OnClickDown 内部会自然推进，
        // 若再转发推进事件会造成“对端点击推进 + 事件推进”双推进跳步/回退。
        TryPatch(typeof(LookAtTarget), "OnClickDown", prefix: nameof(PreLookClick));
        // 选药量/投放发射药：Button Dispencer / Charge Rammer 点击不走 OnClickDown（走 isClicked + 方法调用），
        // 必须 patch 方法：OnChargeButtonPressed（选药量）+ OnLoadChargesPressed（投放）→ 广播 → 对端执行（含按钮动画+逻辑）
        TryPatch(typeof(PowderChargeController), "OnChargeButtonPressed", prefix: nameof(PrePowderSelect));
        TryPatch(typeof(PowderChargeController), "OnLoadChargesPressed", prefix: nameof(PrePowderLoad));
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
        // 任务打字机通知同步：UINotificationManager.ShowNotification 事件 → 主机广播 → 客机复现
        TryPatch(typeof(UINotificationManager), "ShowNotification", postfix: nameof(PostShowNotification));
        // 任务打字机打印同步：Teleprinter.SubmitLines/ClearAll/ClearAlarm → 主机广播 → 客机复现
        TryPatch(typeof(Teleprinter), "SubmitLines", postfix: nameof(PostTeleprinterPrint));
        TryPatch(typeof(Teleprinter), "AppendInstant", postfix: nameof(PostTeleprinterAppend));
        TryPatch(typeof(Teleprinter), "ClearAll", postfix: nameof(PostTeleprinterClearAll));
        TryPatch(typeof(Teleprinter), "ClearAlarm", postfix: nameof(PostTeleprinterClearAlarm));
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
        // 中文输入法（IME）：
        // - 英文/数字/符号：走 PollInput 物理按键（可靠，无 native 调用风险）
        // - 中文：patch Keyboard.OnIMECompositionChanged——IME 组合提交时携带最终中文字符串，
        //   转发给输入框。不再 patch OnTextInput/不注册 onTextInput 监听器——
        //   监听器 native 调用会乱码/崩溃（字母前方框、闪退），且与 PollInput 双通道重复。
        TryPatch(typeof(UnityEngine.InputSystem.Keyboard), "OnIMECompositionChanged", postfix: nameof(PostImeComposition));
    }

    /// <summary>IME 组合变化（中文输入法提交）：组合串含 CJK 字符时转发给输入框（英文走 PollInput）。</summary>
    private static void PostImeComposition(UnityEngine.InputSystem.Keyboard __instance,
        UnityEngine.InputSystem.LowLevel.IMECompositionString __0)
    {
        try
        {
            if (__instance != UnityEngine.InputSystem.Keyboard.current) return;
            int n = 0;
            try { n = __0.Count; } catch { return; }
            if (n <= 0) return;
            System.Text.StringBuilder sb = new();
            for (int i = 0; i < n && i < 64; i++)
            {
                char ch = '\0';
                try { ch = __0[i]; } catch { break; }
                sb.Append(ch);
            }
            string s = sb.ToString();
            // 只处理含 CJK 的提交（组合过程中间状态是拼音字母，不转发；候选字确认后才含中文）
            bool hasCjk = false;
            for (int i = 0; i < s.Length; i++)
                if (s[i] > 0x2E7F) { hasCjk = true; break; }
            if (!hasCjk) return;
            CoopRuntime.LogSource?.LogInfo($"[UI] IME composition CJK '{s}' len={s.Length}");
            for (int i = 0; i < s.Length; i++)
                if (s[i] > 0x2E7F)
                    OpenNestCoop.UI.CoopUIManager.OnImeText(s[i]);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony OnIMEComposition: {ex.Message}"); }
    }

    private static void TryPatch(Type target, string method, string prefix = null, string postfix = null)
    {
        try
        {
            var mi = AccessTools.Method(target, method);
            if (mi == null)
            {
                CoopRuntime.LogSource?.LogWarning($"Harmony: 找不到 {target.Name}.{method}");
                return;
            }
            _harmony.Patch(mi,
                prefix: prefix != null ? new HarmonyMethod(AccessTools.Method(typeof(HarmonyPatches), prefix)) : null,
                postfix: postfix != null ? new HarmonyMethod(AccessTools.Method(typeof(HarmonyPatches), postfix)) : null);
            CoopRuntime.LogSource?.LogInfo($"Harmony: patched {target.Name}.{method}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony: {target.Name}.{method} patch 失败: {ex.Message}"); }
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
        TurretSync.OnLocalGunFired(__instance);
    }

    private static bool PreRequestFire(GunController __instance)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return true;          // 主机正常开火
        if (ReloadSync.IsApplyingFire) return true;          // 网络复现（OnGunFire）放行
        ReloadSync.SendFireRequest(__instance);              // 客户端本地开火：上行请求，拦截本地执行
        return false;
    }

    private static void PreSpawnOne(CounterBatteryCinematicImpactSpawner __instance)
    {
        CounterBatterySync.Instance?.OnLocalSpawn();
    }

    private static bool PreLookClick(LookAtTarget __instance)
    {
        // 必须 try/catch：prefix 抛异常会中断原方法 OnClickDown → 后续逻辑（OnChargeButtonPressed 等）不执行
        try { ButtonClickSync.OnLocalClick(__instance); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony OnLocalClick: {ex.Message}"); }
        return true; // 继续原方法（点击正常执行）
    }
    private static void PrePowderSelect(PowderChargeController __instance, int __0) => ReloadSync.OnLocalPowderSelect(__instance, __0);
    private static void PrePowderLoad(PowderChargeController __instance) => ReloadSync.OnLocalPowderLoad(__instance);

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
        ReconPhotoSync.Instance?.OnLocalPhoto();
        return true; // 继续原方法（生成照片对象）
    }

    // ---------------- 任务过渡事件（完成/失败/重载/回菜单） ----------------
    // prefix：先上报同步，再放行原方法（两端各自执行任务逻辑，事件广播保证跨端一致触发）。
    // ⚠️ 必须 try/catch：prefix 抛异常会中断原方法（如 FinishMission 后续结算流程）。

    private static bool PreMissionFinish()
    {
        try { MissionEventSync.OnLocalEvent(MissionEventSync.EvFinish, false); } catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony MissionFinish: {ex.Message}"); }
        return true;
    }

    private static bool PreMissionComplete(bool __0)
    {
        try { MissionEventSync.OnLocalEvent(MissionEventSync.EvComplete, __0); } catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony MissionComplete: {ex.Message}"); }
        return true;
    }

    private static bool PreMissionFailed(bool __0)
    {
        try { MissionEventSync.OnLocalEvent(MissionEventSync.EvFailed, __0); } catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony MissionFailed: {ex.Message}"); }
        return true;
    }

    private static bool PreMissionReload()
    {
        try { MissionEventSync.OnLocalEvent(MissionEventSync.EvReload, false); } catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony MissionReload: {ex.Message}"); }
        return true;
    }

    private static bool PreMissionReturnMap()
    {
        try { MissionEventSync.OnLocalEvent(MissionEventSync.EvReturnMap, false); } catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony MissionReturnMap: {ex.Message}"); }
        return true;
    }

    private static bool PreMissionEndOperation()
    {
        try { MissionEventSync.OnLocalEvent(MissionEventSync.EvEndOperation, false); } catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony MissionEndOperation: {ex.Message}"); }
        return true;
    }

    /// <summary>补给购买（Supply console）：拉征用杆 → AttemptRequisition。
    /// 主机：放行本地执行 + 广播；客机：拦截本地执行，上报主机（主机权威购买，避免两端重复扣点/双效果）。</summary>
    private static bool PreRequisition(RequisitionSlot __instance)
    {
        try { return PurchaseSync.OnLocalPurchase(__instance); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PreRequisition: {ex.Message}"); }
        return true;
    }

    /// <summary>卡牌入槽：ItemSlot.PlaceItem 后 → 广播（对端执行 PlaceItem，槽位 CurrentItem 两端一致）。</summary>
    private static void PostItemSlotPlace(ItemSlot __instance, DraggableItem __0)
    {
        try { PunchcardSync.OnLocalPlaceCard(__instance, __0); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostItemSlotPlace: {ex.Message}"); }
    }

    /// <summary>卡牌出槽：ItemSlot.RemoveItem 后 → 广播（对端执行 RemoveItem）。</summary>
    private static void PostItemSlotRemove(ItemSlot __instance, DraggableItem __0)
    {
        try { PunchcardSync.OnLocalRemoveCard(__instance, __0); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostItemSlotRemove: {ex.Message}"); }
    }

    /// <summary>任务随机内容一致：FireMission.GenerateMission 生成目标前，应用主机 seed。</summary>
    private static void PreFireMissionGenerate(FireMission __instance)
    {
        try { MissionSync.ApplyPendingSeedTo(__instance); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PreFireMissionGenerate: {ex.Message}"); }
    }

    /// <summary>任务打字机通知：ShowNotification 被调用后 → 主机广播（title/description/lifetime）。</summary>
    private static void PostShowNotification(string __0, string __1, float __2)
    {
        try { NotificationSync.OnLocalShow(__0, __1, __2); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostShowNotification: {ex.Message}"); }
    }

    /// <summary>任务打字机打印：Teleprinter.SubmitLines 被调用后 → 主机广播打印文本行。</summary>
    private static void PostTeleprinterPrint(Teleprinter __instance, string __0, object __1)
    {
        try
        {
            // 提取打印行（IL2CPP IEnumerable<string>，兼容 BepInEx/ML 不同 interop 表示）
            var lines = new System.Collections.Generic.List<string>();
            try
            {
                if (__1 != null)
                {
                    // IL2CPP 集合实现 Il2CppSystem.Collections.IEnumerable（BepInEx/ML 均如此）
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
            catch
            {
                // 枚举失败：退化为无内容打印（至少广播"有打印动作"）
                lines.Add("");
            }
            TeleprinterSync.OnLocalPrint(__instance, lines);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostTeleprinterPrint: {ex.Message}"); }
    }

    /// <summary>任务打字机追加：Teleprinter.AppendInstant 被调用后 → 主机广播追加文本块。</summary>
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

    // ---------------- 玩家-猫交互事件（软同步） ----------------

    /// <summary>拾起：CatPickUpHandler.ExecutePickUp 后 → 广播（ev=1）。</summary>
    private static void PostCatPickUp(CatController cat)
    {
        try { CatSync.OnLocalCatEvent(cat, 1); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostCatPickUp: {ex.Message}"); }
    }

    /// <summary>放下：CatPickUpHandler.ExecuteDrop 后 → 广播（ev=2）。</summary>
    private static void PostCatDrop(CatController cat)
    {
        try { CatSync.OnLocalCatEvent(cat, 2); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostCatDrop: {ex.Message}"); }
    }

    /// <summary>放下（兜底）：CatController.StopCarrying 后 → 广播（ev=2）。
    /// 游戏放下最终调 StopCarrying（ExecuteDrop/ExecuteExternalDrop 均汇入），此 patch 可靠捕获放下。</summary>
    private static void PostCatStopCarrying(CatController __instance)
    {
        try { CatSync.OnLocalCatEvent(__instance, 2); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostCatStopCarrying: {ex.Message}"); }
    }

    /// <summary>驱赶：CatController.ShooCat 后 → 广播（ev=3，对端 ShooCat(false) 复现）。</summary>
    private static void PostCatShoo(CatController __instance)
    {
        try { CatSync.OnLocalCatEvent(__instance, 3); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostCatShoo: {ex.Message}"); }
    }

    /// <summary>抚摸：CatController.PetTheCat 后 → 广播（ev=4）。</summary>
    private static void PostCatPet(CatController __instance)
    {
        try { CatSync.OnLocalCatEvent(__instance, 4); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostCatPet: {ex.Message}"); }
    }

    /// <summary>打断：CatController.InterruptCat 后 → 广播（ev=5）。</summary>
    private static void PostCatInterrupt(CatController __instance)
    {
        try { CatSync.OnLocalCatEvent(__instance, 5); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"Harmony PostCatInterrupt: {ex.Message}"); }
    }

}
