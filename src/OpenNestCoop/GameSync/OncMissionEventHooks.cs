using System;
using HarmonyLib;
using OpenNestCoop.Core;
using UnityEngine;

namespace OpenNestCoop.GameSync;

/// <summary>
/// 脚本化模块事件桥接（A 方案）：把关键**游戏事件**喂给 <see cref="OncMissionBridge.Raise"/>，
/// 让任务图（Core `WaitForEvent`/`Branch`）与脚本模块事件订阅（B 方案，`RegisterScriptedHook`）都能响应游戏事件。
///
/// 事件清单：
/// - `mission.finished` / `mission.completed` / `mission.failed` / `mission.reloaded` / `mission.map` / `mission.menu`
///   （MissionManager 任务过渡方法 postfix，与联机同步 MissionEventSync 的 patch 并存，互不干扰）
/// - `shell.landed`（ImpactTracker.EvaluateImpact postfix，载荷 = 落点 Vector2）
/// - `interact.click`（LookAtTarget.OnClickDown postfix，载荷 = 交互对象路径）
/// - `entity.destroyed[.&lt;id&gt;]`（无 Harmony——OncMissionBridge.PollEntityDestroyed 轮询 FireMission.Entities，
///   订阅了该前缀才工作）
///
/// ⚠️ 设计边界：只 postfix 喂事件，prefix 一律 return true 放行原方法（不改变游戏行为）；
/// postfix 全 try-catch，绝不让事件桥接影响游戏/联机。多 patch 同方法合法（Harmony 叠加）。
/// </summary>
public static class OncMissionEventHooks
{
    private static HarmonyLib.Harmony _harmony;
    private static bool _applied;

    public static void Apply()
    {
        if (_applied) return;
        _applied = true;
        _harmony = new HarmonyLib.Harmony("dev.open-nest.coop.missionevents");
        // 任务过渡事件（postfix 喂事件；与 MissionEventSync 的 prefix 并存）
        TryPatch(typeof(MissionManager), "FinishMission", postfix: nameof(PostMissionFinished));
        TryPatch(typeof(MissionManager), "MarkMissionComplete", postfix: nameof(PostMissionComplete));
        TryPatch(typeof(MissionManager), "MarkMissionFailed", postfix: nameof(PostMissionFailed));
        TryPatch(typeof(MissionManager), "ReloadCurrentMission", postfix: nameof(PostMissionReload));
        TryPatch(typeof(MissionManager), "ReturnToMap", postfix: nameof(PostMissionReturnMap));
        TryPatch(typeof(MissionManager), "EndOperationAndReturnToMenu", postfix: nameof(PostMissionEndOperation));
        // 着弹事件（载荷 = 落点 Vector2）
        TryPatch(typeof(ImpactTracker), "EvaluateImpact", postfix: nameof(PostShellLanded));
        // 交互点击事件（载荷 = 交互对象路径）
        TryPatch(typeof(LookAtTarget), "OnClickDown", postfix: nameof(PostInteractClick));
    }

    private static void TryPatch(Type target, string method, string postfix)
    {
        try
        {
            var mi = AccessTools.Method(target, method);
            if (mi == null)
            {
                CoopRuntime.LogSource?.LogWarning($"OncMissionEventHooks: cannot find {target.Name}.{method}");
                return;
            }
            _harmony.Patch(mi, postfix: new HarmonyMethod(AccessTools.Method(typeof(OncMissionEventHooks), postfix)));
            CoopRuntime.LogSource?.LogInfo($"OncMissionEventHooks: patched {target.Name}.{method}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"OncMissionEventHooks: patch {target.Name}.{method} failed: {ex.Message}"); }
    }

    // ---- 任务过渡 ----

    private static void PostMissionFinished() { try { OncMissionBridge.Raise("mission.finished"); } catch { } }
    private static void PostMissionComplete(bool __0) { try { OncMissionBridge.Raise("mission.completed", __0); } catch { } }
    private static void PostMissionFailed(bool __0) { try { OncMissionBridge.Raise("mission.failed", __0); } catch { } }
    private static void PostMissionReload() { try { OncMissionBridge.Raise("mission.reloaded"); } catch { } }
    private static void PostMissionReturnMap() { try { OncMissionBridge.Raise("mission.map"); } catch { } }
    private static void PostMissionEndOperation() { try { OncMissionBridge.Raise("mission.menu"); } catch { } }

    // ---- 着弹 ----

    /// <summary>着弹评估（静态方法；`__1` = 落点 Vector2，与 HarmonyPatches.PostEvaluateImpact 同签名）。</summary>
    private static void PostShellLanded(UnityEngine.Vector2 __1)
    {
        try { OncMissionBridge.Raise("shell.landed", __1); } catch { }
    }

    // ---- 交互点击 ----

    /// <summary>按钮/拉杆点击（载荷 = 交互对象完整路径）。</summary>
    private static void PostInteractClick(LookAtTarget __instance)
    {
        try
        {
            string path = __instance != null && __instance.gameObject != null ? PathOf(__instance.transform) : null;
            if (!string.IsNullOrEmpty(path)) OncMissionBridge.Raise("interact.click", path);
        }
        catch { }
    }

    /// <summary>Transform → 完整路径（限深度防爆，与 ButtonClickSync.PathOf 同款）。</summary>
    private static string PathOf(Transform t)
    {
        try
        {
            if (t == null) return "";
            string path = t.name ?? "";
            var p = t.parent;
            int depth = 0;
            while (p != null && depth < 8) { path = (p.name ?? "") + "/" + path; p = p.parent; depth++; }
            return path;
        }
        catch { return t == null ? "" : (t.name ?? ""); }
    }
}
