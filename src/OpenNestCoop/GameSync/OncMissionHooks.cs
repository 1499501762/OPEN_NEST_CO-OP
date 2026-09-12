using System;
using HarmonyLib;
using OpenNestCoop.Core;
#if MELONLOADER
using SleepyNodes = Il2CppSleepyNodes;
#endif
namespace OpenNestCoop.GameSync;

/// <summary>
/// 原生任务脚本「流程级」挂钩（自定义任务覆盖原生任务 + 协同事件）。
///
/// ⚠️ 设计边界（记忆教训：任务状态机 patch 曾导致进任务崩溃，已回退）：
/// - **只 patch 流程方法**：MissionManager.StartOperation / LoadMission（任务启动）+ MissionGraph.OnMissionLoaded
///   （任务图加载完成），**不碰 StateNode/EventNode/StateGraph 状态机内部**。
/// - prefix 全 try-catch；**未覆盖的原生任务一律放行**（return true），只有被
///   <see cref="OncMissionBridge.RegisterOverride"/> 明确覆盖的任务才拦截转自定义任务。
///
/// 覆盖语义：原生任务（按 MissionID / 场景名）被自定义任务覆盖后，点任务卡片（MapCard.ActivateMission
/// → StartOperation）或 LoadMission 时改为启动自定义任务（统一任务 ID + 自定义任务范本）。
/// 协同事件：原生任务图加载完成 → OncMissionBridge.Raise("native.mission.loaded[.<id>]")，
/// 自定义任务可用 WaitFor 等待原生任务加载（叠加模式）。
/// </summary>
public static class OncMissionHooks
{
    private static HarmonyLib.Harmony _harmony;
    private static bool _applied;

    public static void Apply()
    {
        if (_applied) return;
        _applied = true;
        _harmony = new HarmonyLib.Harmony("dev.open-nest.coop.missionhooks");
        // 拦截原生任务启动（StartOperation 是 MapCard.ActivateMission 内部入口；LoadMission 是直接加载入口）
        TryPatch(typeof(MissionManager), "StartOperation", prefix: nameof(PreStartOperation));
        TryPatch(typeof(MissionManager), "LoadMission", prefix: nameof(PreLoadMission));
        // 原生任务图加载完成 → 转发协同事件（自定义任务可 WaitFor）
        TryPatch(typeof(SleepyNodes.MissionGraph), "OnMissionLoaded", postfix: nameof(PostMissionLoaded));
        // 自定义任务卡片注入选任务面板（MapCardManager.UpdateMapCards postfix + MapCard.ActivateMission prefix）
        OncMissionCardInjector.Apply();
        // ⚠️ 脚本化模块事件桥接（A 方案）：mission.* / shell.landed / interact.click → OncMissionBridge.Raise
        // （entity.destroyed 走 OncMissionBridge.PollEntityDestroyed 轮询，无需 Harmony）
        try { OncMissionEventHooks.Apply(); }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"OncMissionEventHooks apply: {ex.Message}"); }
    }

    private static void TryPatch(Type target, string method, string prefix = null, string postfix = null)
    {
        try
        {
            var mi = AccessTools.Method(target, method);
            if (mi == null)
            {
                CoopRuntime.LogSource?.LogWarning($"OncMissionHooks: cannot find {target.Name}.{method}");
                return;
            }
            _harmony.Patch(mi,
                prefix: prefix != null ? new HarmonyMethod(AccessTools.Method(typeof(OncMissionHooks), prefix)) : null,
                postfix: postfix != null ? new HarmonyMethod(AccessTools.Method(typeof(OncMissionHooks), postfix)) : null);
            CoopRuntime.LogSource?.LogInfo($"OncMissionHooks: patched {target.Name}.{method}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"OncMissionHooks: patch {target.Name}.{method} failed: {ex.Message}"); }
    }

    /// <summary>拦截 StartOperation：被覆盖 → 启动自定义任务并阻止原生启动（返回 false）。</summary>
    private static bool PreStartOperation(SleepyNodes.OperationGraph __0, SleepyNodes.MissionGraph __1)
    {
        try { return !OncMissionBridge.TryStartOverride(__1); }
        catch { return true; }
    }

    /// <summary>拦截 LoadMission：被覆盖 → 启动自定义任务并阻止原生加载（返回 false）。</summary>
    private static bool PreLoadMission(SleepyNodes.MissionGraph __0, bool __1)
    {
        try { return !OncMissionBridge.TryStartOverride(__0); }
        catch { return true; }
    }

    /// <summary>原生任务图加载完成（场景已就绪）→ 自定义任务则启动自定义引擎 + 转发协同事件。</summary>
    private static void PostMissionLoaded(SleepyNodes.MissionGraph __instance)
    {
        try
        {
            string id = __instance?.MissionID;
            string csInfo = "?";
            try
            {
                var cs = __instance?.CurrentState;
                csInfo = cs == null ? "(no state)" : (cs.Node?.NodeID ?? "(state no node)");
            }
            catch { }
            string phase = "?";
            try { phase = MissionManager.Instance?.CurrentPhase.ToString(); } catch { }
            CoopLog.Info("onc.mission.diag", () => $"OncMission diag OnMissionLoaded fired: id='{id}' nodes={(__instance != null && __instance.nodes != null ? __instance.nodes.Count.ToString() : "null")} scene='{OncMissionBridge.SafeSceneName(__instance)}' state={csInfo} phase={phase}");
            if (string.IsNullOrEmpty(id)) return;
            // ⚠️ 只对【我们注册的原生格式自定义任务】处理；原生游戏任务一律放行（不 repair/不启动自定义引擎，
            // 否则破坏原生行为——打字机触发/引擎启动等）。
            if (!OncMissionBridge.IsNativeCustomGraph(__instance))
            {
                // ⚠️ 2026-08-25 引擎电源参考捕获：原生任务也 dump 引擎/电源/主电源开关状态（只读不干预）——
                // 原生引擎正常（抬起位有电），对比自定义任务（引擎 running 但"断电"）立即看出差异。
                // 延迟 3s/8s/13s 采样 3 次（场景加载完成后引擎对象才就绪）。
                try { OncMissionBridge.ScheduleNativeEngineRef(); }
                catch { }
                return;
            }
            // ⚠️ 场景加载完成后再次修复 ImportMission 的 TextIdentifier 字段（若 StartOperation/LoadMission
            // 内部重建/重置了节点字段，StartNative 里的首轮 repair 会丢失 → OnEnter 仍读不到简报文本）。
            string json = OncMissionBridge.GetNativeJson(id);
            if (json != null) OncMissionBridge.RepairImportedTexts(__instance, json);
            // ⚠️ 原生格式自定义任务（ImportMission 原生图）：图启动由 StartOperation 驱动，但 2-arg/3-arg
            // StartOperation 带空 checkpoint 时 StartMissionRuntime 可能不建立执行线（CurrentState=(no state)）→
            // 手动 graph.Run() 建立主执行线（此时 OnMissionLoaded 已完成、场景就绪——正确时机）。
            // 注意：不调 StartRuntimeInScene（那是 Core 抽象引擎，对原生格式占位 mission 无节点、会干扰原生图）。
            try
            {
                var cs = __instance?.CurrentState;
                if (cs == null)
                {
                    CoopLog.Info("onc.mission.diag", () => $"OncMission diag OnMissionLoaded: state null → graph.Run() (native custom '{id}')");
                    __instance.Run();
                    string after = "?";
                    try { after = __instance.CurrentState == null ? "(still null)" : (__instance.CurrentState.Node?.NodeID ?? "?"); } catch { }
                    CoopLog.Info("onc.mission.diag", () => $"OncMission diag OnMissionLoaded: after Run() state={after}");
                }
                else CoopLog.Info("onc.mission.diag", () => $"OncMission diag OnMissionLoaded: state={csInfo} (no run needed)");
            }
            catch (Exception rxe) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag OnMissionLoaded run: {rxe.Message}"); }
            // 协同事件（自定义任务可 WaitFor("native.mission.loaded.<id>")）
            OncMissionBridge.Raise("native.mission.loaded." + id);
            OncMissionBridge.Raise("native.mission.loaded");
            // 任务启动事件（脚本模块事件订阅 B 方案可用：OncMissionBridge.RegisterScriptedHook("mission.started", ...)）
            OncMissionBridge.Raise("mission.started", id);
        }
        catch { }
    }
}
