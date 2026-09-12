using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime;

using OpenNestCoop.Core;
using OpenNestCore.Tasks;
#if MELONLOADER
using SleepyNodes = Il2CppSleepyNodes;
using Localisation = Il2CppLocalisation;
#endif
namespace OpenNestCoop.GameSync;

/// <summary>
/// 自定义任务桥接（游戏侧）：把 OpenNestCore 的 <see cref="OncMissionRuntime"/> 挂到真实游戏。
///
/// 分层：
/// - **Core（OpenNestCore.Tasks）**：平台无关任务图引擎（节点 + 运行时 + JSON 导入导出 + 同步序号）。
/// - **本类**：注册/启动/驱动 + 默认宿主（<see cref="IOncMissionHost"/>）把节点动作落到游戏
///   （场景加载 / 打字机 / UI 通知 / 地图实体生成移动伤害 / 前置后置解锁 / 随机种子）。
///
/// 使用：
/// <code>
/// // 脚本定义
/// OncMissionBridge.Register(OncMissionBuilder.Create("custom.x").Named("X").End().Build());
/// // 或 JSON 定义
/// OncMissionBridge.Register(OncMissionIO.Load(File.ReadAllText("Tasks/custom_x.json")));
/// OncMissionBridge.Start("custom.x");   // 启动（检查前置后置）
/// </code>
/// 每帧由 CoopBehaviour 调 <see cref="Update"/>。模组可用 <see cref="RegisterHost"/> 覆写默认宿主。
/// 联机"同步序号"：<see cref="OncMissionBridge.Current"/>.<see cref="OncMissionRuntime.BuildSyncState"/>。
/// </summary>
public static class OncMissionBridge
{
    private static readonly Dictionary<string, OncMission> _missions = new Dictionary<string, OncMission>();
    private static readonly Dictionary<string, OncOperation> _operations = new Dictionary<string, OncOperation>();
    private static readonly Dictionary<string, OncMission> _overrides = new Dictionary<string, OncMission>(); // nativeKey(MissionID/场景名) → 自定义任务（覆盖原生任务）
    private static readonly List<OncMission> _ordered = new List<OncMission>();

    /// <summary>已完成的（原生或自定义）任务 id 集合——前置/后置任务解锁依据。</summary>
    private static readonly HashSet<string> _completedMissions = new HashSet<string>();

    private static IOncMissionHost _host;
    private static OncMissionRuntime _current;
    private static readonly OncMissionHostAdapter _defaultHost = new DefaultHost();
    private static float _diagTimer; // 任务驱动诊断（临时）
    private static bool _nativeDiagPending; // 原生任务进场景后延迟扫描炮台
    private static float _nativeDiagTimer;
    private static bool _nativeDiagSecond; // 二次（更晚）查任务状态
    private static bool _nativeDiagThird;  // 三次：手动 Run 后复查
    private static bool _nativeDiagRunTried; // 1s 图启动兜底已试（CurrentState null → graph.Run）
    private static float _diagSeqTimer;      // 图执行节点序列观测定时器
    private static float _entPosLast;        // 敌人实体位置 dump 节流时间戳
    private static float _flowTimer;       // 驱动链追踪采样定时器
    private static string _flowLastSig;    // 上次采样签名（变化才打印）
    private static int _flowStay;          // 当前节点停留采样数（识别卡住/等待驱动）
    private static string _flowLastNodeKey; // 上次节点标识（停留计时用）
    // ⚠️ 引擎开局状态控制：JSON 可选字段 "EngineStart"（"on"/"off"）→ 场景加载后设置 DieselEngineController。
    // 机制：原生开局引擎状态 = 场景组件 forceEngineOn/forceEngineOff（Inspector 配置）+ RestoreMissionState
    // (DieselEngineSaveData.EnginesRunning) 存档恢复。我们的 StartNative 用 2-arg StartOperation（无
    // MissionSaveData）→ 不恢复存档 → 引擎按场景默认。此字段让自定义任务能覆盖开局引擎状态。
    private static string _nativeEngineStart;      // null=不干预（用场景默认）；"on"/"off"=覆盖
    private static bool _nativeEngineStartApplied; // 已应用
    private static float _engineApplyTimer;        // 应用尝试定时器（引擎对象需场景加载后才存在）
    // ⚠️ 引擎供电修复分步状态机（2026-08-25）：ForceEngineOn 只在引擎 stopped→running 边沿触发
    // EnginePowerController 升功率。若引擎已 running（场景默认）ForceEngineOn 无效 → Power 卡 0.285。
    // 稳健方案：ForceEngineOff → 等停机 → ForceEngineOn（强制完整重启触发启动边沿）。
    private static int _enginePowerStep;           // 0=未开始 1=已ForceOff 2=已ForceOn 3=完成/放弃
    private static float _enginePowerStepTimer;    // 步骤内计时
    private static int _enginePowerRetries;        // 供电重试轮数（最多 3）
    // ⚠️ 原生任务引擎参考捕获（2026-08-25）：用户要求"捕获原生的来参考"——进原生任务（教程4）后延迟
    // 轮询 dump 引擎/电源/主电源开关状态，对比自定义任务（引擎 running 但"断电"）定位差异。
    private static bool _nativeRefPending; // 原生任务进场景后延迟捕获引擎参考
    private static float _nativeRefTimer;
    private static int _nativeRefCount;    // 已捕获次数（多次采样看状态变化）
    // ⚠️ 任务完成后延迟原生结算（2026-08-30）：Core 任务完成时场景可能还在加载（SceneManager.LoadScene 异步/
    // 初始化延迟），MissionManager.Instance 可能为 null → 不能立即结算。改为 pending + Update 里每帧重试，
    // 等 MissionManager 就绪再 MarkMissionComplete/MarkMissionFailed（原生结算界面/参数传递才完整）。
    private static OncMissionRuntime _pendingReturnMission; // 待结算任务（完成/失败后设置）
    private static bool _pendingReturnFailed;              // 是否失败结算
    private static float _pendingReturnTimer;              // 等待超时计时
    // ⚠️ 原生结算等待（2026-08-30 docs §D2 定案）：设好原生上下文后驱动原生图推进，再 MarkMissionComplete
    // /MarkMissionFailed → 原生结算界面（EndOfMissionUIController）弹出 + dismiss 返回。
    private static bool _nativeReturnPending;
    private static float _nativeReturnWaitTimer;
    private static bool _nativeReturnMarkDone;  // 是否已调 MarkMissionComplete/Failed（只触发一次）
    private static bool _nativeReturnFailed;    // 成功(false) / 失败(true)

    /// <summary>当前运行中的任务运行时（未运行时为 null）。</summary>
    public static OncMissionRuntime Current => _current;
    public static bool IsRunning => _current != null && _current.IsRunning;

    // ---------------- 注册 ----------------

    public static void Register(OncMission mission)
    {
        if (mission == null || string.IsNullOrEmpty(mission.Id)) return;
        if (!_missions.ContainsKey(mission.Id)) _ordered.Add(mission);
        _missions[mission.Id] = mission;
        CoopLog.Info("onc.mission.reg", () => $"OncMission registered: '{mission.Id}' ({mission.DisplayName}) nodes={mission.Nodes?.Count ?? 0} obj={mission.Objectives?.Count ?? 0} requires={mission.Requires?.Count ?? 0}");
    }

    /// <summary>原生格式任务 JSON（MissionImporter 格式）→ 注册占位（卡片显示）+ 存 raw JSON。</summary>
    private static readonly Dictionary<string, string> _nativeRaw = new Dictionary<string, string>(StringComparer.Ordinal);

    // ---------------- 脚本化模块（"在原生的 Node 任务引擎上引入脚本化模块"） ----------------
    /// <summary>脚本化模块注册项（B3 生命周期：可带 OnMissionStarted/OnMissionEnded 钩子）。</summary>
    private sealed class ScriptedModuleEntry
    {
        public Action<OncScriptContext> Fn;
        public Action<OncMissionRuntime> OnMissionStarted;
        public Action<OncMissionRuntime> OnMissionEnded;
    }

    /// <summary>脚本化模块注册表：模块名 → 注册项。Core Scripted 节点 / 原生脚本锚点节点进入时按名分派。</summary>
    private static readonly Dictionary<string, ScriptedModuleEntry> _scriptedModules =
        new Dictionary<string, ScriptedModuleEntry>(StringComparer.Ordinal);

    /// <summary>原生图脚本锚点节点表：nodeId → 模块名（RegisterNativeFromJson 扫描 State_CustomTrackingVariable 载体）。</summary>
    private static readonly Dictionary<string, string> _nativeScriptedNodes = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>脚本模块事件订阅表（B 方案）：事件 id → 模块名列表。游戏事件（A 桥接）触发时分派。</summary>
    private static readonly Dictionary<string, List<string>> _scriptedHooks = new Dictionary<string, List<string>>(StringComparer.Ordinal);

    /// <summary>实体摧毁事件轮询状态（FireMission.Entities 死亡检测）。</summary>
    private static readonly HashSet<string> _knownEntities = new HashSet<string>(StringComparer.Ordinal);
    private static float _entityEventTimer;

    /// <summary>上次原生图脚本锚点检测的节点 id（防同一节点重复触发）。</summary>
    private static string _lastScriptedNativeNode;

    /// <summary>注册脚本化模块（C# 回调）。被 <see cref="OncNodeKind.Scripted"/> 节点 / 原生脚本锚点触发。</summary>
    public static void RegisterScriptedModule(string name, Action<OncScriptContext> fn)
        => RegisterScriptedModule(name, fn, null, null);

    /// <summary>注册脚本化模块（B3：可选生命周期钩子 OnMissionStarted/OnMissionEnded，任务启动/结束时调用）。</summary>
    public static void RegisterScriptedModule(string name, Action<OncScriptContext> fn,
        Action<OncMissionRuntime> onMissionStarted, Action<OncMissionRuntime> onMissionEnded)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (fn == null) { _scriptedModules.Remove(name); return; }
        if (!_scriptedModules.TryGetValue(name, out var entry))
        {
            entry = new ScriptedModuleEntry();
            _scriptedModules[name] = entry;
        }
        entry.Fn = fn;
        if (onMissionStarted != null) entry.OnMissionStarted = onMissionStarted;
        if (onMissionEnded != null) entry.OnMissionEnded = onMissionEnded;
        CoopLog.Info("onc.mission.script", () => $"OncMission scripted module registered: '{name}'");
    }

    /// <summary>执行脚本化模块（分派到注册表；未注册 → 日志提示，不中断任务）。</summary>
    public static void RunScriptedModule(OncScriptContext ctx)
    {
        if (ctx == null || string.IsNullOrEmpty(ctx.ModuleName)) return;
        if (_scriptedModules.TryGetValue(ctx.ModuleName, out var entry) && entry != null && entry.Fn != null)
        {
            try { entry.Fn(ctx); CoopLog.Debug("onc.mission.script", () => $"OncMission scripted module ran: '{ctx.ModuleName}'"); }
            catch (Exception ex) { CoopLog.Warn("onc.mission.script", () => $"OncMission scripted module '{ctx.ModuleName}' error: {ex.Message}"); }
            return;
        }
        CoopLog.Warn("onc.mission.script", () => $"OncMission scripted module not registered: '{ctx.ModuleName}'");
    }

    /// <summary>任务启动/结束时通知脚本模块生命周期钩子（B3）。</summary>
    private static void NotifyScriptedLifecycle(bool started, OncMissionRuntime rt)
    {
        if (_scriptedModules.Count == 0) return;
        foreach (var kv in _scriptedModules)
        {
            var e = kv.Value;
            if (e == null) continue;
            try
            {
                if (started) e.OnMissionStarted?.Invoke(rt);
                else e.OnMissionEnded?.Invoke(rt);
            }
            catch (Exception ex) { CoopLog.Warn("onc.mission.script", () => $"OncMission module lifecycle '{kv.Key}' error: {ex.Message}"); }
        }
    }

    /// <summary>挂接脚本模块生命周期（创建 runtime 后调用）：启动钩子 + 结束事件订阅。</summary>
    private static void AttachScriptedLifecycle(OncMissionRuntime rt)
    {
        if (rt == null) return;
        try
        {
            rt.OnCompleted += rt2 => NotifyScriptedLifecycle(false, rt2);
            rt.OnFailed += rt2 => NotifyScriptedLifecycle(false, rt2);
            rt.OnCanceled += rt2 => NotifyScriptedLifecycle(false, rt2);
            NotifyScriptedLifecycle(true, rt);
        }
        catch { }
    }

    /// <summary>
    /// 注册脚本模块事件订阅（B 方案）：当游戏事件 <paramref name="eventId"/> 触发时，分派脚本模块
    /// <paramref name="moduleName"/>（可多次、可异步、可持续）。事件来源见 A 方案
    /// （OncMissionEventHooks：mission.* / shell.landed / interact.click / entity.destroyed.*）。
    /// 事件 id 取精确匹配；模块须已 <see cref="RegisterScriptedModule"/> 注册。
    /// </summary>
    public static void RegisterScriptedHook(string eventId, string moduleName)
    {
        if (string.IsNullOrEmpty(eventId) || string.IsNullOrEmpty(moduleName)) return;
        if (!_scriptedHooks.TryGetValue(eventId, out var list))
        {
            list = new List<string>();
            _scriptedHooks[eventId] = list;
        }
        if (!list.Contains(moduleName)) list.Add(moduleName);
        CoopLog.Info("onc.mission.script", () => $"OncMission scripted hook: '{eventId}' -> module '{moduleName}'");
    }

    /// <summary>是否有订阅 <paramref name="prefix"/> 前缀的事件（如 "entity.destroyed"）——实体摧毁轮询的节流门。</summary>
    private static bool HasScriptedEventPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || _scriptedHooks.Count == 0) return false;
        foreach (var kv in _scriptedHooks)
            if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// 事件分发（A 桥接入口）：游戏事件（OncMissionEventHooks）→ ①喂 Core 图（WaitForEvent/Branch）
    /// ②分派脚本模块事件订阅（B）。
    /// </summary>
    public static void Raise(string eventId, object payload = null)
    {
        // ① Core 图：驱动 WaitForEvent/Branch
        if (_current != null) { try { _current.Raise(eventId, payload); } catch { } }
        // ② 脚本模块事件订阅（B）
        DispatchScriptedHooks(eventId, payload);
    }

    private static void DispatchScriptedHooks(string eventId, object payload)
    {
        if (string.IsNullOrEmpty(eventId) || _scriptedHooks.Count == 0) return;
        if (!_scriptedHooks.TryGetValue(eventId, out var modules)) return;
        for (int i = 0; i < modules.Count; i++)
        {
            string m = modules[i];
            if (string.IsNullOrEmpty(m)) continue;
            RunScriptedModule(new OncScriptContext
            {
                ModuleName = m,
                Event = new OncScriptEvent { EventId = eventId, Payload = payload },
            });
        }
    }

    /// <summary>
    /// 实体摧毁事件轮询（A 方案事件源之一）：订阅了 "entity.destroyed" 前缀事件时，每 0.5s 查
    /// FireMission.Entities，检测 alive→dead 转换 → Raise("entity.destroyed.&lt;id&gt;") + Raise("entity.destroyed")。
    /// 纯读取零 Harmony；只在有人订阅时工作（无订阅零开销）。
    /// </summary>
    private static void PollEntityDestroyed(float dt)
    {
        if (!HasScriptedEventPrefix("entity.destroyed")) return;
        _entityEventTimer += dt;
        if (_entityEventTimer < 0.5f) return;
        _entityEventTimer = 0f;
        try
        {
            var fm = FireMission.Instance;
            if (fm == null || fm.Entities == null) return;
            // 新增/存活实体记入
            try
            {
                var en = fm.Entities.GetEnumerator();
                while (en.MoveNext())
                {
                    var kv = en.Current;
                    if (kv == null) continue;
                    string id = null; MapEntity e = null;
                    try { id = kv.Key; e = kv.Value; } catch { }
                    if (!string.IsNullOrEmpty(id) && e != null && e.IsAlive && e.Health > 0)
                        _knownEntities.Add(id);
                }
            }
            catch { }
            // 已知实体死亡检测（alive→dead 或已移除）
            if (_knownEntities.Count == 0) return;
            var dead = new List<string>();
            foreach (var id in _knownEntities)
                if (IsEntityDead(id)) dead.Add(id);
            for (int i = 0; i < dead.Count; i++)
            {
                _knownEntities.Remove(dead[i]);
                CoopLog.Debug("onc.mission.script", () => $"OncMission entity destroyed detected: '{dead[i]}'");
                Raise("entity.destroyed." + dead[i], dead[i]);
                Raise("entity.destroyed", dead[i]);
            }
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.script", () => $"OncMission poll entity destroyed error: {ex.Message}"); }
    }

    /// <summary>实体是否已摧毁（FireMission.Entities 查 IsAlive/Health；找不到 = 已摧毁，防卡）。</summary>
    private static bool IsEntityDead(string entityId)
    {
        try
        {
            var fm = FireMission.Instance;
            if (fm == null || fm.Entities == null) return false;
            MapEntity ent = null;
            try { if (fm.Entities.TryGetValue(entityId ?? "", out var e)) ent = e; } catch { }
            if (ent == null) return true; // 找不到 = 已摧毁（防卡）
            return !ent.IsAlive || ent.Health <= 0;
        }
        catch { return false; }
    }

    /// <summary>A3：原生图变量桥接——把当前原生 MissionGraph.Variables 快照进 ctx.Variables（脚本模块可读）。
    /// 写回：模块调 <see cref="SetNativeGraphVariable"/>（原生图变量写，经 interop，需游戏实测）。</summary>
    private static void BridgeNativeVariables(OncScriptContext ctx, SleepyNodes.MissionGraph graph)
    {
        try
        {
            if (ctx == null || graph == null) return;
            var vars = graph.Variables;
            if (vars == null) return;
            var en = vars.GetEnumerator();
            while (en.MoveNext())
            {
                var kv = en.Current;
                if (kv == null) continue;
                string k = null; object v = null;
                try { k = kv.Key; v = kv.Value; } catch { }
                if (!string.IsNullOrEmpty(k)) ctx.Variables[k] = v;
            }
        }
        catch { }
    }

    /// <summary>A3：写原生图变量（当前原生任务 MissionGraph.Variables）。⚠️ 类型经 interop，需游戏实测。</summary>
    public static void SetNativeGraphVariable(string name, object value)
    {
        try
        {
            if (string.IsNullOrEmpty(name)) return;
            var mm = MissionManager.Instance;
            if (mm == null || mm.CurrentMission == null) return;
            var vars = mm.CurrentMission.Variables;
            if (vars == null) return;
            // 值转 Il2CppSystem.Object（interop 索引器 setter 期望 Il2Cpp 对象；托管 string 隐式转 Il2CppString）
            if (value is Il2CppSystem.Object io) vars[name] = io;
            else { Il2CppSystem.String ilstr = value?.ToString() ?? ""; vars[name] = ilstr; }
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.script", () => $"OncMission set native var error: {ex.Message}"); }
    }

    /// <summary>脚本化模块名 → 原生 State_CustomTrackingVariable 载体键（variableName 前缀约定）。</summary>
    private const string ScriptedPrefix = "onc.script.";

    /// <summary>脚本化锚点节点 ID 前缀（节点 ID "onc_script_&lt;name&gt;" → 分派模块 &lt;name&gt;）。</summary>
    private const string ScriptedIdPrefix = "onc_script_";

    /// <summary>注册内置脚本化模块（示例/通用工具，启动时调用）：
    /// - "announce"：从 ModuleArgs JSON 读 {"title","text","duration"} 弹通知；
    /// - "ping"：日志。
    /// 模组/任务作者可用 <see cref="RegisterScriptedModule"/> 注册自己的模块。</summary>
    public static void RegisterBuiltinScriptedModules()
    {
        RegisterScriptedModule("announce", ctx =>
        {
            string title = null, text = null; float duration = 4f;
            try
            {
                if (!string.IsNullOrEmpty(ctx.Args))
                {
                    var o = OncJson.ParseObject(ctx.Args);
                    if (o != null)
                    {
                        title = OncJson.GetString(o, "title");
                        text = OncJson.GetString(o, "text");
                        duration = OncJson.GetFloat(o, "duration", 4f);
                    }
                }
            }
            catch { }
            ctx.Host?.ShowNotification(title ?? "脚本化模块", text ?? "announce 已执行", duration);
        });
        RegisterScriptedModule("ping", ctx =>
            CoopLog.Info("onc.mission.script", () => $"OncMission scripted ping (args='{ctx.Args}')"));

        // ---- B6：声明式 JSON 内置模块库（JSON ModuleName 直接可用，无需 C# 注册）----
        // 参数：ModuleArgs 为 JSON 对象字符串，字段见各模块。
        RegisterScriptedModule("print", ctx =>
        {
            string text = null;
            try { if (!string.IsNullOrEmpty(ctx.Args)) { var o = OncJson.ParseObject(ctx.Args); if (o != null) text = OncJson.GetString(o, "text"); } } catch { }
            ctx.Host?.PrintTeleprinter(text ?? "（脚本模块 print：缺 text 参数）");
        });
        RegisterScriptedModule("requisition", ctx =>
        {
            int amount = 0;
            try { if (!string.IsNullOrEmpty(ctx.Args)) { var o = OncJson.ParseObject(ctx.Args); if (o != null) amount = OncJson.GetInt(o, "amount"); } } catch { }
            ctx.Host?.AddRequisitionPoints(amount);
        });
        RegisterScriptedModule("shell", ctx =>
        {
            string shellId = null; int amount = 0;
            try
            {
                if (!string.IsNullOrEmpty(ctx.Args))
                {
                    var o = OncJson.ParseObject(ctx.Args);
                    if (o != null) { shellId = OncJson.GetString(o, "shell"); amount = OncJson.GetInt(o, "amount"); }
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(shellId)) ctx.Host?.AddShell(shellId, amount, -1);
        });
        RegisterScriptedModule("powder", ctx =>
        {
            int amount = 0;
            try { if (!string.IsNullOrEmpty(ctx.Args)) { var o = OncJson.ParseObject(ctx.Args); if (o != null) amount = OncJson.GetInt(o, "amount"); } } catch { }
            ctx.Host?.AddPowderCharge(amount);
        });
        RegisterScriptedModule("log", ctx =>
            CoopLog.Info("onc.mission.script", () => $"[script:{ctx.ModuleName}] args='{ctx.Args}'"));
    }

    /// <summary>
    /// 扫描原生任务 JSON 的脚本锚点节点（State_CustomTrackingVariable 载体，variableName="onc.script.&lt;name&gt;"
    /// 或节点 ID "onc_script_&lt;name&gt;"）→ 注册进 <see cref="_nativeScriptedNodes"/>。
    /// 由 RegisterNativeFromJson 调用（Import 前从 raw JSON 扫描，不依赖 ImportMission 字段还原）。
    /// </summary>
    private static void ScanNativeScriptedNodes(string json)
    {
        try
        {
            var root = OncJson.ParseObject(json);
            if (root == null) return;
            var inner = UnwrapMissionJson(root);
            var src = inner ?? root;
            if (!src.TryGetValue("Nodes", out var nodesObj) || !(nodesObj is Dictionary<string, object> nodes)) return;
            foreach (var kv in nodes)
            {
                var node = kv.Value as Dictionary<string, object>;
                if (node == null) continue;
                string nodeId = OncJson.GetString(node, "ID");
                if (string.IsNullOrEmpty(nodeId)) nodeId = kv.Key;
                string module = null;
                var nd = OncJson.GetObject(node, "NodeData");
                if (nd != null)
                {
                    string vn = OncJson.GetString(nd, "variableName");
                    if (!string.IsNullOrEmpty(vn) && vn.StartsWith(ScriptedPrefix, StringComparison.Ordinal))
                        module = vn.Substring(ScriptedPrefix.Length);
                }
                if (module == null && nodeId.StartsWith(ScriptedIdPrefix, StringComparison.Ordinal))
                    module = nodeId.Substring(ScriptedIdPrefix.Length);
                if (!string.IsNullOrEmpty(module))
                {
                    _nativeScriptedNodes[nodeId] = module;
                    CoopLog.Debug("onc.mission.script", () => $"OncMission scripted anchor node '{nodeId}' -> module '{module}'");
                }
            }
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.script", () => $"OncMission scan scripted nodes error: {ex.Message}"); }
    }

    /// <summary>
    /// 检测原生图脚本锚点节点进入（每帧轮询 CurrentState，纯读取零 Harmony 风险——不碰状态机内部）。
    /// 约定：进入 variableName="onc.script.&lt;name&gt;" 的 State_CustomTrackingVariable 节点，
    /// 或节点 ID "onc_script_&lt;name&gt;" 时，分派脚本模块。只对【我们的原生格式自定义图】生效。
    /// </summary>
    private static void PollNativeScripted()
    {
        try
        {
            var mm = MissionManager.Instance;
            if (mm == null || mm.CurrentMission == null) return;
            if (!IsNativeCustomGraph(mm.CurrentMission)) return;
            string nodeId = null;
            try { nodeId = mm.CurrentMission.CurrentState?.Node?.NodeID; } catch { }
            if (string.IsNullOrEmpty(nodeId)) return;
            if (nodeId == _lastScriptedNativeNode) return; // 同节点不重复触发
            _lastScriptedNativeNode = nodeId;
            string module = null;
            if (_nativeScriptedNodes.TryGetValue(nodeId, out var m0)) module = m0;
            else if (nodeId.StartsWith(ScriptedIdPrefix, StringComparison.Ordinal)) module = nodeId.Substring(ScriptedIdPrefix.Length);
            if (string.IsNullOrEmpty(module)) return;
            CoopLog.Info("onc.mission.script", () => $"OncMission native scripted anchor entered node='{nodeId}' -> module='{module}'");
            var sctx = new OncScriptContext { ModuleName = module };
            BridgeNativeVariables(sctx, mm.CurrentMission); // A3：原生图变量桥接（读快照）
            RunScriptedModule(sctx);
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.script", () => $"OncMission poll scripted error: {ex.Message}"); }
    }

    public static bool RegisterNativeFromJson(string json)
    {
        try
        {
            var o = OncJson.ParseObject(json);
            if (o == null) return false;
            // ⚠️ 支持 ExportPackage 包装格式（{MissionName, MissionJson: 字符串, Files: []}）：
            // MissionID/MissionName 在 MissionJson 字符串内部（原生标准格式，用户确认）。
            string inner = OncJson.GetString(o, "MissionJson");
            if (!string.IsNullOrEmpty(inner))
            {
                var io = OncJson.ParseObject(inner);
                if (io != null)
                {
                    string imid = OncJson.GetString(io, "MissionID");
                    if (!string.IsNullOrEmpty(imid))
                    {
                        string imname = OncJson.GetString(io, "MissionName") ?? imid;
                        string imtype = OncJson.GetString(io, "MissionType");
                        var im = new OncMission { Id = imid, DisplayName = imname, MissionType = imtype };
                        Register(im);
                        _nativeRaw[imid] = json; // 存完整 ExportPackage（Import 时原样传，保留 Files/package 上下文）
                        ScanNativeScriptedNodes(json);
                        CoopLog.Info("onc.mission.native", () => $"OncMission native (export pkg) registered: '{imid}' ({imname})");
                        return true;
                    }
                }
                CoopLog.Warn("onc.mission.native", () => "OncMission ExportPackage has no valid MissionJson.MissionID");
                return false;
            }
            string mid = OncJson.GetString(o, "MissionID");
            if (string.IsNullOrEmpty(mid)) return false;
            string mname = OncJson.GetString(o, "MissionName") ?? mid;
            string mtype = OncJson.GetString(o, "MissionType");
            var m = new OncMission { Id = mid, DisplayName = mname, MissionType = mtype };
            Register(m); // 占位（卡片显示用；节点/目标由原生图驱动）
            _nativeRaw[mid] = json;
            ScanNativeScriptedNodes(json);
            CoopLog.Info("onc.mission.native", () => $"OncMission native registered: '{mid}' ({mname})");
            return true;
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.native", () => $"OncMission RegisterNativeFromJson error: {ex.Message}"); return false; }
    }

    public static string GetNativeJson(string id)
        => id != null && _nativeRaw.TryGetValue(id, out var j) ? j : null;

    /// <summary>判断图是否是我们注册的**原生格式自定义任务**（按 MissionID 匹配 _nativeRaw）。
    /// 用于区分"我们的 ImportMission 图" vs "游戏原生任务图"——postfix 等干扰性逻辑只对前者生效，
    /// 绝不碰原生任务（否则破坏原生行为：打字机触发/引擎启动等）。</summary>
    public static bool IsNativeCustomGraph(SleepyNodes.MissionGraph graph)
    {
        try
        {
            if (graph == null) return false;
            string id = null;
            try { id = graph.MissionID; } catch { }
            if (id != null && _nativeRaw.ContainsKey(id)) return true;
            return false;
        }
        catch { return false; }
    }

    /// <summary>从原生 JSON 读取任务场景名（支持 "SceneName" 键 或 "#sym" 符号表）。</summary>
    private static string ReadNativeSceneName(string json)
    {
        try
        {
            var o = OncJson.ParseObject(json);
            if (o == null) return null;
            // ExportPackage 包装：场景名在 MissionJson 字符串内部
            var inner = UnwrapMissionJson(o);
            var src = inner ?? o;
            var s = OncJson.GetString(src, "SceneName");
            if (!string.IsNullOrEmpty(s)) return s;
            if (src.TryGetValue("#sym", out var sym) && sym is Dictionary<string, object> symTab)
                return OncJson.GetString(symTab, "SceneName");
            return null;
        }
        catch { return null; }
    }

    private static string ReadNativeString(string json, string key)
    {
        try
        {
            var o = OncJson.ParseObject(json);
            if (o == null) return null;
            var inner = UnwrapMissionJson(o);
            var src = inner ?? o;
            return OncJson.GetString(src, key);
        }
        catch { return null; }
    }

    /// <summary>读取 JSON 的可选引擎开局状态字段（"EngineStart"："on"/"off"/true/false）。无字段返回 null（不干预）。</summary>
    private static string ReadNativeEngineStart(string json)
    {
        try
        {
            var o = OncJson.ParseObject(json);
            if (o == null) return null;
            var inner = UnwrapMissionJson(o);
            var src = inner ?? o;
            if (src == null || !src.TryGetValue("EngineStart", out var v)) return null;
            if (v is string s)
            {
                string t = s.Trim().ToLowerInvariant();
                if (t == "on" || t == "true" || t == "1" || t == "start" || t == "running") return "on";
                if (t == "off" || t == "false" || t == "0" || t == "stop" || t == "stopped") return "off";
                return null;
            }
            if (v is bool b) return b ? "on" : "off";
            if (v is long l) return l != 0 ? "on" : "off";
            return null;
        }
        catch { return null; }
    }

    /// <summary>设置自定义任务的开局引擎状态意图（从 JSON EngineStart 字段；场景加载后应用）。</summary>
    public static void ScheduleEngineStart(string json)
    {
        _nativeEngineStart = null;
        _nativeEngineStartApplied = false;
        _engineApplyTimer = 0f;
        // ⚠️ 重置保电状态机（自定义任务重新启动时清旧状态，防残留干扰）
        _enginePowerRestartPhase = 0;
        _enginePowerRestartTimer = 0f;
        _engineKeepPowerTimer = 0f;
        _engineRestartRetries = 0;
        try
        {
            string want = ReadNativeEngineStart(json);
            if (want == null) return; // 无配置 → 不干预（用场景默认）
            _nativeEngineStart = want;
            CoopLog.Info("onc.mission.native", () => $"OncMission engine start intent='{want}' (apply after scene load)");
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.native", () => $"OncMission engine start intent error: {ex.Message}"); }
    }

    /// <summary>场景加载后应用开局引擎状态（引擎对象就绪后设置；最多尝试 ~12s 直到成功或放弃）。</summary>
    private static void TryApplyEngineStart(float dt)
    {
        if (_nativeEngineStart == null || _nativeEngineStartApplied) return;
        _engineApplyTimer += dt;
        if (_engineApplyTimer < 1.5f) return; // 等场景加载 + 引擎对象生成
        try
        {
            var e = UnityEngine.Object.FindFirstObjectByType<DieselEngineController>();
            if (e == null)
            {
                if (_engineApplyTimer >= 12f)
                {
                    CoopLog.Warn("onc.mission.native", () => "OncMission engine start: no DieselEngineController found in scene, giving up");
                    _nativeEngineStart = null;
                }
                return; // 引擎对象还没生成，下帧再试
            }
            string want = _nativeEngineStart;
            if (want == "off")
            {
                // 停机（简单直接）
                _nativeEngineStartApplied = true;
                _nativeEngineStart = null;
                bool running = false;
                try { running = e.EnginesRunning; } catch { }
                if (running)
                {
                    try { e.ShutdownEngine(); CoopLog.Info("onc.mission.native", () => "OncMission engine start: ShutdownEngine (stop) applied"); }
                    catch (Exception ex) { CoopLog.Warn("onc.mission.native", () => $"OncMission engine start: ShutdownEngine error: {ex.Message}"); }
                }
                else CoopLog.Info("onc.mission.native", () => "OncMission engine start: already stopped (want=off)");
                return;
            }
            // ⚠️ want == "on"：供电恢复（2026-08-25）。
            // 根因：原生 Power=0.490（抬起位有电），自定义 Power=0.284（断电）——引擎 running 是场景默认值，
            // 没走完整启动流程（EnginesRunning false→true 边沿才触发 EnginePowerController 升功率）。
            // 修复：ForceEngineOff→ForceEngineOn（DieselEngineStateRelay，public）触发完整重启 → Power 升满；
            // 之后 RestorePowerLeverUp 把 Power Lever 拨回抬起位（relay 联动会拨到拉下位，需复位）。
            // 保电监控（UpdateEnginePowerRestart phase3）每 3s 查 Power，掉回 <0.4 则再 ForceEngineOn。
            // ❌ 证伪：设 Dial 到 target 熄火；点击 Power Lever 拨到拉下位；直接设 Power 字段被 Update 覆盖。
            _nativeEngineStartApplied = true;
            _nativeEngineStart = null;
            try
            {
                var relays = UnityEngine.Object.FindObjectsOfType<DieselEngineStateRelay>(true);
                if (relays != null && relays.Length > 0)
                {
                    for (int ri = 0; ri < relays.Length; ri++)
                    {
                        var relay = relays[ri];
                        if (relay == null) continue;
                        string rn = "?"; try { rn = relay.gameObject.name; } catch { }
                        try { relay.ForceEngineOff(); CoopLog.Info("onc.mission.native", () => $"OncMission engine start: ForceEngineOff '{rn}' (pre-restart)"); }
                        catch (Exception rex) { CoopLog.Warn("onc.mission.native", () => $"OncMission engine start: '{rn}' ForceEngineOff error: {rex.Message}"); }
                    }
                    _enginePowerRestartTimer = 0f;
                    _enginePowerRestartPhase = 1; // 等待停机后 ForceEngineOn
                }
                else CoopLog.Warn("onc.mission.native", () => "OncMission engine start: no DieselEngineStateRelay, cannot restart engine");
            }
            catch (Exception re2) { CoopLog.Warn("onc.mission.native", () => $"OncMission engine start: restart error: {re2.Message}"); }
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.native", () => $"OncMission engine start apply error: {ex.Message}"); }
    }

    // ⚠️ 引擎供电重启状态机（ForceEngineOff→等→ForceEngineOn→等→Power 复查→持续保电）
    private static float _enginePowerRestartTimer;
    private static int _enginePowerRestartPhase; // 0=空闲 1=已ForceOff等停机 2=已ForceOn等升功率 3=保电监控
    private static float _engineKeepPowerTimer;
    private static int _engineRestartRetries;

    /// <summary>每帧驱动引擎供电重启状态机 + 持续保电（Power 掉回则再次 ForceEngineOn）。
    /// ⚠️ 守卫：只在【我们的自定义任务】场景生效——原生任务一律不干预（否则 ForceEngineOn 拨动 Power Lever
    /// /重启引擎会破坏原生任务：用户反馈"原生任务场景被破坏，4图任务没电拉杆抬起"= 状态泄漏 + 无守卫干预）。</summary>
    private static void UpdateEnginePowerRestart(float dt)
    {
        try
        {
            if (_enginePowerRestartPhase == 0) return;
            // ⚠️ 守卫：当前 MissionManager 的图必须是我们的自定义图；原生任务一律重置并放行
            bool isCustom = false;
            try
            {
                var mm = MissionManager.Instance;
                if (mm != null && mm.CurrentMission != null)
                    isCustom = IsNativeCustomGraph(mm.CurrentMission);
            }
            catch { }
            if (!isCustom)
            {
                // 原生任务/主菜单：重置保电状态机（防状态泄漏干扰原生任务引擎）
                _enginePowerRestartPhase = 0;
                _enginePowerRestartTimer = 0f;
                _engineKeepPowerTimer = 0f;
                return;
            }
            _enginePowerRestartTimer += dt;
            var e = UnityEngine.Object.FindFirstObjectByType<DieselEngineController>();
            var relays = UnityEngine.Object.FindObjectsOfType<DieselEngineStateRelay>(true);
            switch (_enginePowerRestartPhase)
            {
                case 1: // 已 ForceEngineOff，等 1.5s 停机 → ForceEngineOn
                    if (_enginePowerRestartTimer >= 1.5f && relays != null)
                    {
                        foreach (var relay in relays)
                        {
                            if (relay == null) continue;
                            string rn = "?"; try { rn = relay.gameObject.name; } catch { }
                            try { relay.ForceEngineOn(); CoopLog.Info("onc.mission.native", () => $"OncMission engine start: ForceEngineOn '{rn}' (restart)"); }
                            catch (Exception rex) { CoopLog.Warn("onc.mission.native", () => $"OncMission engine start: '{rn}' ForceEngineOn error: {rex.Message}"); }
                        }
                        _enginePowerRestartPhase = 2;
                        _enginePowerRestartTimer = 0f;
                    }
                    break;
                case 2: // 已 ForceEngineOn，等 5s Power 上升 → 复查
                    if (_enginePowerRestartTimer >= 5f)
                    {
                        float power = ReadEnginePower();
                        bool running = false; if (e != null) { try { running = e.EnginesRunning; } catch { } }
                        CoopLog.Info("onc.mission.native", () => $"OncMission engine start: restart check power={power:0.000} running={running}");
                        if (power >= 0.4f)
                        {
                            // ⚠️ 2026-08-25 用户：Power Lever（拉杆）应抬起位 + 有电。ForceEngineOn 把拉杆拨到
                            // 拉下位（relay 联动）。Power 恢复后把 Power Lever 的 SwitchControler 拨回抬起位
                            // （SetInterpolantImmediate(0)，直接设位置不触发供电变化——供电由引擎保电监控维持）。
                            try { RestorePowerLeverUp(); } catch (Exception rpl) { CoopLog.Warn("onc.mission.native", () => $"OncMission engine start: restore lever up error: {rpl.Message}"); }
                            _enginePowerRestartPhase = 3; // 进入保电监控
                            _engineKeepPowerTimer = 0f;
                            CoopLog.Info("onc.mission.native", () => $"OncMission engine start: power restored ({power:0.000}), lever-up + keeping alive");
                        }
                        else if (_engineRestartRetries < 3)
                        {
                            _engineRestartRetries++;
                            _enginePowerRestartPhase = 1;
                            _enginePowerRestartTimer = 0f;
                            CoopLog.Info("onc.mission.native", () => $"OncMission engine start: power low ({power:0.000}), restart retry #{_engineRestartRetries}");
                            // 先 ForceOff 再等下轮
                            if (relays != null)
                                foreach (var relay in relays)
                                {
                                    if (relay == null) continue;
                                    try { relay.ForceEngineOff(); } catch { }
                                }
                        }
                        else
                        {
                            _enginePowerRestartPhase = 0;
                            CoopLog.Warn("onc.mission.native", () => $"OncMission engine start: give up, power={power:0.000}");
                        }
                    }
                    break;
                case 3: // 保电监控：每 3s 查 Power，掉回 <0.4 则再次 ForceEngineOn
                    _engineKeepPowerTimer += dt;
                    if (_engineKeepPowerTimer >= 3f)
                    {
                        _engineKeepPowerTimer = 0f;
                        float power = ReadEnginePower();
                        if (power < 0.4f)
                        {
                            CoopLog.Info("onc.mission.native", () => $"OncMission engine start: power dropped to {power:0.000}, ForceEngineOn again");
                            if (relays != null)
                                foreach (var relay in relays)
                                {
                                    if (relay == null) continue;
                                    try { relay.ForceEngineOn(); } catch { }
                                }
                        }
                    }
                    break;
            }
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.native", () => $"OncMission engine power restart error: {ex.Message}"); }
    }

    /// <summary>读 EnginePowerController.Power（0 失败）。</summary>
    private static float ReadEnginePower()
    {
        try
        {
            var epc = UnityEngine.Object.FindFirstObjectByType<EnginePowerController>();
            if (epc != null) return epc.Power;
        }
        catch { }
        return 0f;
    }

    /// <summary>
    /// ⚠️ 2026-08-25 把 Power Lever（拉杆）拨回**抬起位**（视觉正常），不触发供电变化。
    /// 机制：Power Lever 下有 `SwitchControler`（InterpolatedTransformController，target='Power Lever'），
    /// interp=0（抬起位）/ interp=1（拉下位）。ForceEngineOn 联动拨到拉下位。这里用
    /// `SetInterpolantImmediate(0)` 直接设回抬起位——不动 LookAtTarget/animator（不触发点击/供电逻辑）。
    /// 供电由引擎保电监控（UpdateEnginePowerRestart phase3）维持，不依赖 Power Lever 位置。
    /// </summary>
    private static void RestorePowerLeverUp()
    {
        try
        {
            var itcs = UnityEngine.Object.FindObjectsOfType<InterpolatedTransformController>(true);
            if (itcs == null || itcs.Length == 0) { CoopLog.Warn("onc.mission.native", () => "OncMission restore lever up: no InterpolatedTransformController"); return; }
            int found = 0;
            for (int i = 0; i < itcs.Length; i++)
            {
                var itc = itcs[i];
                if (itc == null || itc.gameObject == null) continue;
                // 只处理 Power Lever 下的 SwitchControler（targetObject 名含 Power Lever 或父链含 Power Box）
                string itcPath = ""; try { itcPath = PathOf(itc.transform); } catch { }
                string targetName = ""; try { if (itc.targetObject != null) targetName = itc.targetObject.name; } catch { }
                bool isPowerLever = itcPath.IndexOf("Power Lever", StringComparison.OrdinalIgnoreCase) >= 0
                    || itcPath.IndexOf("Power Box", StringComparison.OrdinalIgnoreCase) >= 0
                    || targetName.IndexOf("Power Lever", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isPowerLever) continue;
                found++;
                float interp = -1f; try { interp = itc.interpolant; } catch { }
                if (interp <= 0.1f) { CoopLog.Info("onc.mission.native", () => $"OncMission restore lever up: '{itcPath}' already up (interp={interp:0.000})"); continue; }
                try { itc.SetInterpolantImmediate(0f); CoopLog.Info("onc.mission.native", () => $"OncMission restore lever up: '{itcPath}' interp {interp:0.000}→0 (raised)"); }
                catch (Exception se) { CoopLog.Warn("onc.mission.native", () => $"OncMission restore lever up: '{itcPath}' set error: {se.Message}"); }
            }
            if (found == 0) CoopLog.Warn("onc.mission.native", () => "OncMission restore lever up: no Power Lever SwitchControler found");
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.native", () => $"OncMission restore lever up error: {ex.Message}"); }
    }

    /// <summary>Transform → 完整路径（与 ButtonClickSync.PathOf 同款）。</summary>
    private static string PathOf(UnityEngine.Transform t)
    {
        if (t == null) return "";
        string path = t.name ?? "";
        var p = t.parent;
        while (p != null) { path = (p.name ?? "") + "/" + path; p = p.parent; }
        return path;
    }

    /// <summary>若 JSON 是 ExportPackage（含 MissionJson 字符串）→ 解析返回内部 MissionDefinition；否则返回 null。</summary>
    private static Dictionary<string, object> UnwrapMissionJson(Dictionary<string, object> o)
    {
        try
        {
            if (o == null) return null;
            string inner = OncJson.GetString(o, "MissionJson");
            if (string.IsNullOrEmpty(inner)) return null;
            return OncJson.ParseObject(inner);
        }
        catch { return null; }
    }

    /// <summary>
    /// ⚠️ ImportMission 对 TextIdentifier 类型字段（TeleprinterText.Text / SendUINotification 的
    /// Text_Title/Text_Description）反序列化有缺陷：JSON 里的 {"Raw":..,"Key":..} 被还原成普通 Object
    /// （运行时类型 = Object，非 Localisation.TextIdentifier）→ OnEnter 读不到文本 → 简报/通知静默不显示。
    /// 修复：Import 后遍历图节点，从 JSON 读原始文本，手动构造 TextIdentifier 回填。
    /// </summary>
    public static void RepairImportedTexts(SleepyNodes.MissionGraph graph, string json)
    {
        if (graph == null || string.IsNullOrEmpty(json)) return;
        try
        {
            var root = OncJson.ParseObject(json);
            if (root == null) return;
            // ExportPackage 包装：Nodes 在 MissionJson 字符串内部
            var inner = UnwrapMissionJson(root);
            var src = inner ?? root;
            if (!src.TryGetValue("Nodes", out var nodesObj) || !(nodesObj is Dictionary<string, object> nodes)) return;
            var list = graph.nodes;
            if (list == null) return;
            int repaired = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i];
                if (n == null) continue;
                string nid = null;
                try { var sn = n.TryCast<SleepyNodes.StateNode>(); if (sn != null) nid = sn.NodeID; } catch { }
                if (nid == null || !nodes.TryGetValue(nid, out var nodeObj) || !(nodeObj is Dictionary<string, object> node)) continue;
                if (!node.TryGetValue("NodeData", out var ndObj) || !(ndObj is Dictionary<string, object> nd)) continue;
                // TeleprinterText.Text + EntityIDToReplace（OnEnter 遍历它做实体名替换 → null NRE）
                var tp = n.TryCast<SleepyNodes.State_TeleprinterText>();
                if (tp != null)
                {
                    string raw = ReadNodeTextRaw(nd, "Text");
                    if (!string.IsNullOrEmpty(raw))
                    {
                        try
                        {
                            // ⚠️ setter 前读（ImportMission 生成的字段状态）
                            string before = "?";
                            try { var bv = tp.Text; before = bv == null ? "(null)" : bv.GetType().Name + "/raw=" + (bv.Raw ?? "(null)"); } catch (Exception be) { before = "err(" + be.Message + ")"; }
                            tp.Text = new Localisation.TextIdentifier(raw); repaired++;
                            // ⚠️ setter 后用【属性】读回（IL2CPP get_Text；之前用 GetField 反射读 Object 可能是反射拿错字段）
                            string back = "?";
                            try { var av = tp.Text; back = av == null ? "(null)" : av.GetType().Name + "/raw=" + (av.Raw ?? "(null)"); } catch (Exception ae) { back = "err(" + ae.Message + ")"; }
                            CoopLog.Info("onc.mission.native", () => $"OncMission repair TeleprinterText '{nid}' before='{before}' after='{back}' raw='{Truncate(raw, 60)}'");
                        }
                        catch (Exception te) { CoopLog.Warn("onc.mission.native", () => $"OncMission repair TeleprinterText '{nid}' set error: {te.Message}"); }
                    }
                    // ⚠️ EntityIDToReplace null → 补空 List（OnEnter 遍历 null NRE 是打字机不驱动的直接根因）
                    try
                    {
                        var el = tp.EntityIDToReplace;
                        if (el == null)
                        {
                            tp.EntityIDToReplace = new Il2CppSystem.Collections.Generic.List<SleepyNodes.State_TeleprinterText.StringReplacement>();
                            CoopLog.Info("onc.mission.native", () => $"OncMission repair TeleprinterText '{nid}' EntityIDToReplace null → empty List");
                            repaired++;
                        }
                    }
                    catch (Exception ee) { CoopLog.Warn("onc.mission.native", () => $"OncMission repair TeleprinterText '{nid}' EntityIDToReplace set error: {ee.Message}"); }
                    continue;
                }
                // SendUINotification.Text_Title/Text_Description
                var ui = n.TryCast<SleepyNodes.State_SendUINotification>();
                if (ui != null)
                {
                    string t = ReadNodeTextRaw(nd, "Text_Title");
                    string d = ReadNodeTextRaw(nd, "Text_Description");
                    if (!string.IsNullOrEmpty(t)) { try { ui.Text_Title = new Localisation.TextIdentifier(t); repaired++; } catch { } }
                    if (!string.IsNullOrEmpty(d)) { try { ui.Text_Description = new Localisation.TextIdentifier(d); repaired++; } catch { } }
                }
            }
            CoopLog.Info("onc.mission.native", () => $"OncMission native repair texts: {repaired} fields set (TextIdentifier backfill)");
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.native", () => $"OncMission repair texts error: {ex.Message}"); }
    }

    /// <summary>从 NodeData 读文本字段：优先 {"Raw":..,"Key":..} 对象，回退 string。</summary>
    private static string ReadNodeTextRaw(Dictionary<string, object> nd, string field)
    {
        try
        {
            if (nd != null && nd.TryGetValue(field, out var v))
            {
                if (v is Dictionary<string, object> ti) return OncJson.GetString(ti, "Raw");
                if (v is string s) return s;
            }
        }
        catch { }
        return null;
    }

    // 手动简报延迟打印（场景加载后等待打字机就绪）
    private static string _manualBriefText;
    private static float _manualBriefTimer;

    /// <summary>从原生 JSON 提取首个 TeleprinterText 节点文本并延迟打印（场景加载后打字机就绪）。</summary>
    public static void ScheduleManualBriefing(string json)
    {
        try
        {
            if (string.IsNullOrEmpty(json)) return;
            var root = OncJson.ParseObject(json);
            if (root == null) return;
            var inner = UnwrapMissionJson(root);
            var src = inner ?? root;
            if (!src.TryGetValue("Nodes", out var nodesObj) || !(nodesObj is Dictionary<string, object> nodes)) return;
            foreach (var kv in nodes)
            {
                if (!(kv.Value is Dictionary<string, object> node)) continue;
                if (!node.TryGetValue("NodeType", out var nt) || nt as string != "State_TeleprinterText") continue;
                if (!node.TryGetValue("NodeData", out var ndObj) || !(ndObj is Dictionary<string, object> nd)) continue;
                string raw = ReadNodeTextRaw(nd, "Text");
                if (!string.IsNullOrEmpty(raw))
                {
                    _manualBriefText = raw;
                    _manualBriefTimer = 1.5f;
                    CoopLog.Info("onc.mission.diag", () => $"ScheduleManualBriefing: '{Truncate(raw, 60)}' (print in 1.5s)");
                }
                return; // 只取第一个简报
            }
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"ScheduleManualBriefing error: {ex.Message}"); }
    }

    /// <summary>
    /// ⚠️ 验证/兜底：手动对 Primary 打字机打印简报（绕过 State_TeleprinterText 节点——其 OnEnter
    /// 内部调 SubmitLines 但可能因缺实体上下文传空内容 → 打字机响但没字）。用 TeleprinterSync 已验证的
    /// `SubmitLines + TryStart(true)` 路径（网络复现同款）。场景加载完成后延迟调用。
    /// </summary>
    public static void ManualPrintBriefing(string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return;
            var tp = Teleprinter.GetTeleprinter(Teleprinter.Teleprinters.Primary);
            if (tp == null) { CoopLog.Warn("onc.mission.diag", () => "ManualPrintBriefing: Primary printer null"); return; }
            var il2cppLines = new Il2CppSystem.Collections.Generic.List<string>();
            il2cppLines.Add((Il2CppSystem.String)text);
            var val = ((Il2CppObjectBase)il2cppLines)
                .TryCast<Il2CppSystem.Collections.Generic.IEnumerable<string>>();
            if (val == null) { CoopLog.Warn("onc.mission.diag", () => "ManualPrintBriefing: TryCast IEnumerable failed"); return; }
            var job = tp.SubmitLines("", val, null, false);
            try { tp.TryStart(true); } catch { }
            CoopLog.Info("onc.mission.diag", () => $"ManualPrintBriefing: submitted '{Truncate(text, 60)}' job={(job == null ? "null" : "ok")} isPrinting={tp.IsPrinting}");
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"ManualPrintBriefing error: {ex.Message}"); }
    }

    /// <summary>调度原生任务引擎参考捕获（PostMissionLoaded 对原生任务调用；场景加载后延迟 3s 起，
    /// 每 5s 采样一次共 ~3 次，看引擎/电源/主电源开关状态的稳定值）。</summary>
    public static void ScheduleNativeEngineRef()
    {
        _nativeRefPending = true;
        _nativeRefTimer = 0f;
        _nativeRefCount = 0;
        CoopLog.Info("onc.mission.diag", () => "OncMission diag: native engine ref capture scheduled (dump at 3s/8s/13s)");
    }

    /// <summary>原生任务引擎参考捕获：dump 引擎 + 电源 + 主电源开关 + 引擎面板灯完整状态（只读）。</summary>
    private static void CaptureNativeEngineRef()
    {
        try
        {
            CoopLog.Info("onc.mission.diag", () => $"OncMission diag[native-ref #{_nativeRefCount}] ============ 引擎参考捕获 ============");
            DumpEngineDiag("native-ref");
            // 主电源开关（Power Lever）状态
            try
            {
                var targets = UnityEngine.Object.FindObjectsOfType<LookAtTarget>(true);
                if (targets != null)
                {
                    for (int i = 0; i < targets.Length; i++)
                    {
                        var t = targets[i];
                        if (t == null || t.gameObject == null) continue;
                        string path = "";
                        try { path = PathOf(t.transform); } catch { }
                        if (path.IndexOf("Power Lever", StringComparison.OrdinalIgnoreCase) < 0
                            && path.IndexOf("Power Box", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string isC = "?", act = "?", tgState = "?";
                        try { isC = t.isClicked.ToString(); } catch { }
                        try { act = t.isActive.ToString(); } catch { }
                        try
                        {
                            var tg = t.GetComponents<AnimatorBoolToggler>();
                            if (tg != null && tg.Length > 0)
                            {
                                var sb = new System.Text.StringBuilder();
                                for (int ti = 0; ti < tg.Length; ti++)
                                {
                                    bool b = false; try { b = tg[ti].GetBool(); } catch { }
                                    sb.Append(b ? "1" : "0");
                                }
                                tgState = sb.ToString();
                            }
                        }
                        catch { }
                        CoopLog.Info("onc.mission.diag", () => $"OncMission diag[native-ref] power lever '{t.gameObject.name}' path='{path}' isClicked={isC} active={act} toggler=[{tgState}]");
                        // ⚠️ dump Power Lever 下的 SwitchControler interp（供电开关位置：0=抬起/断电 1=拉下/来电）
                        try
                        {
                            var itcs = t.GetComponentsInChildren<InterpolatedTransformController>(true);
                            if (itcs != null)
                            {
                                for (int it = 0; it < itcs.Length; it++)
                                {
                                    var itc = itcs[it];
                                    if (itc == null) continue;
                                    string interp = "?", mode = "?", tgt = "?";
                                    try { interp = itc.interpolant.ToString("0.000"); } catch { }
                                    try { mode = itc.triggerMode.ToString(); } catch { }
                                    try { if (itc.targetObject != null) tgt = itc.targetObject.name; } catch { }
                                    CoopLog.Info("onc.mission.diag", () => $"OncMission diag[native-ref] SwitchControler interp={interp} mode={mode} target='{tgt}'");
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            // 引擎面板灯（EngineControlsLightsController 的 fuel/injection 灯）
            try
            {
                var ecls = UnityEngine.Object.FindObjectsOfType<EngineControlsLightsController>(true);
                if (ecls != null)
                {
                    for (int i = 0; i < ecls.Length; i++)
                    {
                        var ecl = ecls[i];
                        if (ecl == null) continue;
                        string en = "?"; try { en = ecl.gameObject.name; } catch { }
                        // 读各灯 active（红/黄/绿）
                        string fr = "?", fy = "?", fg = "?", ir = "?", iy = "?", ig = "?";
                        try { if (ecl._fuelLightRed != null) fr = ecl._fuelLightRed.activeSelf.ToString(); } catch { }
                        try { if (ecl._fuelLightYellow != null) fy = ecl._fuelLightYellow.activeSelf.ToString(); } catch { }
                        try { if (ecl._fuelLightGreen != null) fg = ecl._fuelLightGreen.activeSelf.ToString(); } catch { }
                        try { if (ecl._injectionLightRed != null) ir = ecl._injectionLightRed.activeSelf.ToString(); } catch { }
                        try { if (ecl._injectionLightYellow != null) iy = ecl._injectionLightYellow.activeSelf.ToString(); } catch { }
                        try { if (ecl._injectionLightGreen != null) ig = ecl._injectionLightGreen.activeSelf.ToString(); } catch { }
                        CoopLog.Info("onc.mission.diag", () => $"OncMission diag[native-ref] panelLights '{en}' fuel[R={fr} Y={fy} G={fg}] inj[R={ir} Y={iy} G={ig}]");
                    }
                }
            }
            catch { }
            // ⚠️ 原生任务生成节点取证：dump CurrentMission 所有 State_SpawnMapEntity 完整字段
            // （Role/Health/NumberToSpawn/LocationToSpawn.ZoneID/GridLocation/LocationType 等）——
            // 这是原生真实配置，确认自定义任务生成节点怎么写才有效。
            DumpSpawnNodes("native-ref");
            CoopLog.Info("onc.mission.diag", () => $"OncMission diag[native-ref #{_nativeRefCount}] ============ 结束 ============");
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag[native-ref] capture error: {ex.Message}"); }
    }

    /// <summary>把 interop 包装器/结构安全转字符串（优先 ToString()；失败则反射读 value/Value/InlineValue/Key 字段；
    /// Vector2Int 读 x/y）。用于 dump LocationToSpawn 的 DistanceMin/Max/Bearing/Offset 等字段。</summary>
    private static string SafeWrapToString(object obj)
    {
        if (obj == null) return "(null)";
        try { return obj.ToString(); }
        catch { }
        try
        {
            // Vector2Int（值类型 boxed）
            var t = obj.GetType();
            var fx = t.GetProperty("x"); var fy = t.GetProperty("y");
            if (fx != null && fy != null) return $"({fx.GetValue(obj)},{fy.GetValue(obj)})";
        }
        catch { }
        // 统一用 .NET 反射读字段（interop 对象/值类型都行；属性优先字段兜底）
        try
        {
            var t = obj.GetType();
            foreach (var fn in new[] { "x", "y", "X", "Y", "value", "Value", "InlineValue", "Key" })
            {
                try
                {
                    var f = t.GetField(fn);
                    if (f == null) continue;
                    var v = f.GetValue(obj);
                    if (v != null) return fn + "=" + v;
                }
                catch { }
            }
            return "(?)";
        }
        catch { return "(?)"; }
    }

    /// <summary>诊断：dump 当前任务图里所有 State_SpawnMapEntity 生成节点的完整字段（只读）。
    /// 用于取证原生任务真实生成配置（Role/Health/NumberToSpawn/LocationToSpawn 等）。</summary>
    public static void DumpSpawnNodes(string tag)
    {
        try
        {
            SleepyNodes.MissionGraph cur = null;
            try { var mm = MissionManager.Instance; if (mm != null) cur = mm.CurrentMission; } catch { }
            var graph = cur;
            if (graph == null)
            {
                CoopLog.Info("onc.mission.diag", () => $"OncMission diag[{tag}] spawn dump: CurrentMission null (skip)");
                return;
            }
            var nodes = graph.nodes;
            if (nodes == null || nodes.Count == 0)
            {
                CoopLog.Info("onc.mission.diag", () => $"OncMission diag[{tag}] spawn dump: no nodes in CurrentMission '{graph.MissionID}'");
                return;
            }
            int spawnCount = 0;
            CoopLog.Info("onc.mission.diag", () => $"OncMission diag[{tag}] spawn dump: mission='{graph.MissionID}' totalNodes={nodes.Count}");
            for (int ni = 0; ni < nodes.Count; ni++)
            {
                var n = nodes[ni];
                if (n == null) continue;
                string tname = "?";
                try { tname = n.GetIl2CppType().Name; } catch { try { tname = n.GetType().Name; } catch { } }
                if (tname.IndexOf("Spawn", StringComparison.OrdinalIgnoreCase) < 0) continue;
                spawnCount++;
                var sp = n.TryCast<SleepyNodes.State_SpawnMapEntity>();
                if (sp == null)
                {
                    CoopLog.Info("onc.mission.diag", () => $"OncMission diag[{tag}]   spawn#{spawnCount} type={tname} (not State_SpawnMapEntity)");
                    continue;
                }
                string eid = "?", eRole = "?", eHp = "?", eArmour = "?", eStars = "?", eScale = "?", eNum = "?", eState = "?", eImmune = "?";
                try { eid = sp.ID ?? "?"; } catch { }
                try { eRole = sp.Role.ToString(); } catch { }
                try { eHp = sp.Health.ToString(); } catch { }
                try { eArmour = sp.Armour.ToString(); } catch { }
                try { eStars = sp.Stars.ToString(); } catch { }
                try { eScale = sp.Scale.ToString(); } catch { }
                try { eNum = sp.NumberToSpawn.ToString(); } catch { }
                try { eState = sp.StartingState.ToString(); } catch { }
                try { eImmune = (sp.ImmuneShells != null ? sp.ImmuneShells.Count.ToString() : "null"); } catch { }
                string eZone = "?", eLType = "?", eFuzzy = "?", eRand = "?", eLoc = "?", eBearing = "?", eDMin = "?", eDMax = "?", eRelTo = "?", eCtx = "?", eTgtFilter = "?", eOMin = "?", eOMax = "?";
                var lt = sp.LocationToSpawn;
                if (lt != null)
                {
                    try { eZone = lt.ZoneID ?? "?"; } catch { }
                    try { eLType = lt.LocationType.ToString(); } catch { }
                    try { eFuzzy = lt.FuzzyLocation.ToString(); } catch { }
                    try { eRand = lt.RandomiseSubgrid.ToString(); } catch { }
                    try { eRelTo = lt.RelativeTo.ToString(); } catch { }
                    try { eTgtFilter = lt.TargetFilter.ToString(); } catch { }
                    try { eCtx = (lt.ContextEntityKey ?? "?") + "/" + (lt.ContextLocationKey ?? "?"); } catch { }
                    // 包装器字段（ContextVariableOrInline_Float / Vector2Int）安全读：ToString() 或反射
                    try { eBearing = SafeWrapToString(lt.Bearing); } catch { }
                    try { eDMin = SafeWrapToString(lt.DistanceMin); } catch { }
                    try { eDMax = SafeWrapToString(lt.DistanceMax); } catch { }
                    try { eOMin = SafeWrapToString(lt.OffsetMin); } catch { }
                    try { eOMax = SafeWrapToString(lt.OffsetMax); } catch { }
                    try
                    {
                        // GridLocation 是 ContextVariableOrInline<GridReference> 包装器（无 Location/X/Y 直接属性）→ 反射读 ToString/值
                        var gl = lt.GridLocation;
                        if (gl == null) eLoc = "(null)";
                        else
                        {
                            try { eLoc = gl.ToString(); }
                            catch
                            {
                                // 反射读字段（value/Value/InlineValue/Key）
                                var t2 = gl.GetIl2CppType();
                                foreach (var fn in new[] { "value", "Value", "InlineValue", "Key" })
                                {
                                    try
                                    {
                                        var f = t2.GetField(fn);
                                        if (f == null) continue;
                                        var v = f.GetValue(gl);
                                        if (v != null) { eLoc = fn + "=" + v; break; }
                                    }
                                    catch { }
                                }
                                if (eLoc == "?") eLoc = "(no-value-field)";
                            }
                        }
                    }
                    catch { }
                }
                string eSetCtx = "?", eLastEnt = "?";
                try { eSetCtx = sp.SetContextVariable.ToString(); } catch { }
                try { eLastEnt = sp.LastSpawnedEntity.ToString(); } catch { }
                CoopLog.Info("onc.mission.diag", () => $"OncMission diag[{tag}]   spawn#{spawnCount} id='{eid}' role={eRole} hp={eHp} armour={eArmour} stars={eStars} scale={eScale} num={eNum} state={eState} immune={eImmune} setCtx={eSetCtx} lastEnt={eLastEnt}");
                CoopLog.Info("onc.mission.diag", () => $"OncMission diag[{tag}]     loc: zone='{eZone}' locType={eLType} grid={eLoc} bearing={eBearing} dist=[{eDMin},{eDMax}] off=[{eOMin},{eOMax}] relTo={eRelTo} tgtFilter={eTgtFilter} ctx='{eCtx}' fuzzy={eFuzzy} rand={eRand}");
            }
            CoopLog.Info("onc.mission.diag", () => $"OncMission diag[{tag}] spawn dump: done ({spawnCount} spawn nodes)");
            // ⚠️ State_WaitEntityDestroyed 节点 dump（Entites/TargetSelection 配置——等待哪些实体被摧毁）
            try
            {
                int waitCount = 0;
                for (int ni = 0; ni < nodes.Count; ni++)
                {
                    var n = nodes[ni];
                    if (n == null) continue;
                    string tname = "?";
                    try { tname = n.GetIl2CppType().Name; } catch { try { tname = n.GetType().Name; } catch { } }
                    if (tname.IndexOf("WaitEntityDestroyed", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    waitCount++;
                    var we = n.TryCast<SleepyNodes.State_WaitEntityDestroyed>();
                    if (we == null) continue;
                    string wnid = "?";
                    try { wnid = we.NodeID ?? "?"; } catch { }
                    string wSrc = "?", wCtx = "?", wFilter = "?", wCountType = "?", wCount = "?", wSort = "?", wDEnt = "?", wDLoc = "?";
                    try
                    {
                        var ts = we.Entites;
                        if (ts == null) CoopLog.Info("onc.mission.diag", () => $"OncMission diag[{tag}]   wait#{waitCount} '{wnid}' Entites=NULL");
                        else
                        {
                            try { wSrc = ts.SourceType.ToString(); } catch { }
                            try { wCtx = ts.ContextKey.ToString(); } catch { }
                            try { wFilter = ts.Filter.ToString(); } catch { }
                            try { wCountType = ts.CountType.ToString(); } catch { }
                            try { wCount = ts.Count.ToString(); } catch { }
                            try { wSort = ts.SortType.ToString(); } catch { }
                            try { wDEnt = ts.DistanceEntityKey.ToString(); } catch { }
                            try { wDLoc = ts.DistanceLocationKey.ToString(); } catch { }
                            CoopLog.Info("onc.mission.diag", () => $"OncMission diag[{tag}]   wait#{waitCount} '{wnid}' src={wSrc} ctx={wCtx} filter={wFilter} countType={wCountType} count={wCount} sort={wSort} distEnt={wDEnt} distLoc={wDLoc}");
                        }
                    }
                    catch (Exception wex) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag[{tag}] wait dump: {wex.Message}"); }
                }
                if (waitCount == 0) CoopLog.Info("onc.mission.diag", () => $"OncMission diag[{tag}] wait dump: 0 wait nodes");
            }
            catch (Exception wx) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag[{tag}] wait dump err: {wx.Message}"); }
            // ⚠️ 当前任务图 Zones 列表（MissionGraph.Zones，ImportMission importZones=true 导入 JSON Zones）——
            // 判断生成节点 ZoneID 是否匹配图上真实区名
            try
            {
                var zl = graph.Zones;
                if (zl != null)
                {
                    CoopLog.Info("onc.mission.diag", () => $"OncMission diag[{tag}] graph zones count={zl.Count}");
                    for (int zi = 0; zi < zl.Count; zi++)
                    {
                        var z = zl[zi];
                        if (z == null) continue;
                        string zid = "?", zn = "?", zr = "?", zx = "?", zy = "?", zw = "?", zh = "?", zrgns = "?";
                        try { zid = z.ID ?? "?"; } catch { }
                        try { zn = z.Name ?? "?"; } catch { }
                        try { zr = z.Role.ToString(); } catch { }
                        try { var bl = z.BottomLeft; zx = bl.X.ToString(); zy = bl.Y.ToString(); } catch { }
                        try { zw = z.Width.ToString(); zh = z.Height.ToString(); } catch { }
                        try { zrgns = z.Regions != null ? z.Regions.Count.ToString() : "null"; } catch { }
                        CoopLog.Info("onc.mission.diag", () => $"OncMission diag[{tag}]   zone '{zid}' name='{zn}' role={zr} bl=({zx},{zy}) size={zw}x{zh} regions={zrgns}");
                    }
                }
                else CoopLog.Info("onc.mission.diag", () => $"OncMission diag[{tag}] graph zones: none");
            }
            catch (Exception ze) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag[{tag}] zone scan error: {ze.Message}"); }
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag[{tag}] spawn dump error: {ex.Message}"); }
    }

    /// <summary>诊断：dump 场景引擎 + 电源状态（只读）。tag 用于区分来源（native=原生任务 / custom=自定义任务）。
    /// 2026-08-25：对比原生（正常）vs 自定义（引擎 running 但 Power≈0.28 断电）的 Dial/Power 差异。</summary>
    public static void DumpEngineDiag(string tag)
    {
        try
        {
            // 引擎
            var decs = UnityEngine.Object.FindObjectsOfType<DieselEngineController>(true);
            CoopLog.Info("onc.mission.diag", () => $"OncMission diag[{tag}] engine count={decs?.Length ?? 0}");
            for (int e = 0; e < (decs?.Length ?? 0); e++)
            {
                var dec = decs[e];
                if (dec == null) continue;
                string en = "?"; try { en = dec.gameObject.name; } catch { }
                string run = "?", warn = "?", fuel = "?", inj = "?", bal = "?", fok = "?", iok = "?", frun = "?", irun = "?";
                try { run = dec.EnginesRunning.ToString(); } catch { }
                try { warn = dec.InWarningState.ToString(); } catch { }
                try { fuel = dec.FuelMixtureSystemValue.ToString("0.000"); } catch { }
                try { inj = dec.InjectionTimingSystemValue.ToString("0.000"); } catch { }
                try { bal = dec.BothInBalance.ToString(); } catch { }
                try { fok = dec.IsFuelBalancedForStart().ToString(); } catch { }
                try { iok = dec.IsInjectionTimingBalancedForStart().ToString(); } catch { }
                try { frun = dec.IsFuelInRangeForRunning().ToString(); } catch { }
                try { irun = dec.IsInjectionTimingInRangeForRunning().ToString(); } catch { }
                CoopLog.Info("onc.mission.diag", () => $"OncMission diag[{tag}] engine '{en}' running={run} warning={warn} fuel={fuel} inj={inj} bothBal={bal} fuelStartOk={fok} injStartOk={iok} fuelRun={frun} injRun={irun}");
            }
            // 电源
            var epcs = UnityEngine.Object.FindObjectsOfType<EnginePowerController>(true);
            for (int p = 0; p < (epcs?.Length ?? 0); p++)
            {
                var epc = epcs[p];
                if (epc == null) continue;
                string en = "?"; try { en = epc.gameObject.name; } catch { }
                string power = "?", engRef = "?", engRun = "?";
                try { power = epc.Power.ToString("0.000"); } catch { }
                try { engRef = epc.dieselEngine == null ? "(null)" : "OK"; } catch { }
                try { if (epc.dieselEngine != null) engRun = epc.dieselEngine.EnginesRunning.ToString(); } catch { }
                // 调试目标/当前功率（Update 插值用；确认 Power=0.286 是 target 还是插值卡住）
                string dtgt = "?", dcur = "?", rise = "?", fall = "?", enabled = "?", goActive = "?";
                try { dtgt = epc._debugTargetPower.ToString("0.000"); } catch { }
                try { dcur = epc._debugCurrentPower.ToString("0.000"); } catch { }
                try { rise = epc.riseSpeed.ToString("0.000"); } catch { }
                try { fall = epc.fallSpeed.ToString("0.000"); } catch { }
                try { enabled = epc.enabled.ToString(); } catch { }
                try { goActive = epc.gameObject.activeInHierarchy.ToString(); } catch { }
                CoopLog.Info("onc.mission.diag", () => $"OncMission diag[{tag}] power '{en}' Power={power} tgt={dtgt} cur={dcur} rise={rise} fall={fall} enabled={enabled} goActive={goActive} engineRef={engRef} engineRun={engRun}");
            }
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag[{tag}] engine dump error: {ex.Message}"); }
    }

    /// <summary>诊断：遍历所有已加载场景，找炮台相关根对象（Turret/Cannon/Artillery/Gun），确认炮在哪个场景、能否交互。</summary>
    private static void LogNativeSceneTurrets()
    {
        try
        {
            int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
            string activeName = "?";
            try { activeName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
            CoopLog.Info("onc.mission.diag", () => $"OncMission diag sceneCount={sceneCount} active='{activeName}'");
            for (int s = 0; s < sceneCount; s++)
            {
                var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
                string sn = "?";
                try { sn = sc.name; } catch { }
                CoopLog.Info("onc.mission.diag", () => $"OncMission diag scene[{s}]='{sn}' loaded={sc.isLoaded}");
                if (!sc.isLoaded) continue;
                int rootCount = 0, turretHits = 0;
                try
                {
                    var roots = sc.GetRootGameObjects();
                    if (roots != null) rootCount = roots.Length;
                    CoopLog.Info("onc.mission.diag", () => $"OncMission diag scene[{s}] rootObjs={rootCount}");
                    for (int i = 0; i < rootCount; i++)
                    {
                        var go = roots[i];
                        if (go == null || go.name == null) continue;
                        string n = go.name;
                        CoopLog.Info("onc.mission.diag", () => $"OncMission diag   root[{i}] '{n}'");
                        if (n.IndexOf("Turret", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Cannon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Artillery", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Gun", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            turretHits++;
                            CoopLog.Info("onc.mission.diag", () => $"OncMission diag   turret: '{n}' active={go.activeSelf}");
                            WalkTurretInteractables(go);
                        }
                    }
                }
                catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag scene[{sn}] scan error: {ex.Message}"); }
                CoopLog.Info("onc.mission.diag", () => $"OncMission diag   rootObjs={rootCount} turretRoots={turretHits}");
                // ⚠️ FireMission 诊断：确认场景是否有 FireMission 组件（实体生成宿主；缺失则 State_SpawnMapEntity 无法生成）
                try
                {
                    var fms = UnityEngine.Object.FindObjectsOfType<FireMission>(true);
                    CoopLog.Info("onc.mission.diag", () => $"OncMission diag   FireMission count={fms?.Length ?? 0} instance={(FireMission.Instance == null ? "NULL" : "OK")}");
                    if (fms != null)
                    {
                        for (int fi = 0; fi < fms.Length; fi++)
                        {
                            var f = fms[fi];
                            if (f == null) continue;
                            string fp = "?";
                            try { fp = PathOf(f.transform); } catch { }
                            CoopLog.Info("onc.mission.diag", () => $"OncMission diag   FireMission[{fi}] path='{fp}' active={f.gameObject.activeSelf} ent={(f.Entities != null ? f.Entities.Count.ToString() : "null")}");
                        }
                    }
                }
                catch (Exception fe) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag FireMission scan: {fe.Message}"); }
                // ⚠️ 引擎开局状态诊断：dump DieselEngineController 的 forceEngineOn/EnginesRunning/InWarningState
                try
                {
                    var decs = UnityEngine.Object.FindObjectsOfType<DieselEngineController>(true);
                    CoopLog.Info("onc.mission.diag", () => $"OncMission diag   engine count={decs?.Length ?? 0}");
                    if (decs != null && decs.Length > 0)
                    {
                        for (int e = 0; e < decs.Length; e++)
                        {
                            var dec = decs[e];
                            if (dec == null) continue;
                            string en = "?"; try { en = dec.gameObject.name; } catch { }
                            string fo = "?", foff = "?", run = "?", warn = "?";
                            try { fo = dec.forceEngineOn.ToString(); } catch { }
                            try { foff = dec.forceEngineOff.ToString(); } catch { }
                            try { run = dec.EnginesRunning.ToString(); } catch { }
                            try { warn = dec.InWarningState.ToString(); } catch { }
                            // ⚠️ 燃料/点火正时值（引擎 running 但功率低/警告 → 燃料或点火不在平衡区）
                            string fuel = "?", inj = "?", bal = "?", fuelOk = "?", injOk = "?", fuelRun = "?", injRun = "?";
                            try { fuel = dec.FuelMixtureSystemValue.ToString("0.000"); } catch { }
                            try { inj = dec.InjectionTimingSystemValue.ToString("0.000"); } catch { }
                            try { bal = dec.BothInBalance.ToString(); } catch { }
                            try { fuelOk = dec.IsFuelBalancedForStart().ToString(); } catch { }
                            try { injOk = dec.IsInjectionTimingBalancedForStart().ToString(); } catch { }
                            try { fuelRun = dec.IsFuelInRangeForRunning().ToString(); } catch { }
                            try { injRun = dec.IsInjectionTimingInRangeForRunning().ToString(); } catch { }
                            // 目标值 + 容差（确认 target 与当前值的差距 → 为什么 bothBal=False）
                            string ft = "?", it = "?", ftol = "?", itol = "?", fd = "?", id = "?";
                            try { ft = dec.fuelMixtureTarget.ToString("0.000"); } catch { }
                            try { it = dec.injectionTimingTarget.ToString("0.000"); } catch { }
                            try { ftol = dec.fuelMixtureTolerance.ToString("0.000"); } catch { }
                            try { itol = dec.injectionTimingTolerance.ToString("0.000"); } catch { }
                            try { if (dec.fuelMixtureDial != null) fd = dec.fuelMixtureDial.AccumulatedValue.ToString("0.000"); } catch { }
                            try { if (dec.injectionTimingDial != null) id = dec.injectionTimingDial.AccumulatedValue.ToString("0.000"); } catch { }
                            // 运行范围 + 硬关断（fuelRun=False → 设 target 后超运行范围）
                            string fomin = "?", fomax = "?", tomin = "?", tomax = "?", hso = "?", drift = "?", maxOff = "?", coup = "?";
                            try { fomin = dec.fuelOperatingMin.ToString("0.000"); } catch { }
                            try { fomax = dec.fuelOperatingMax.ToString("0.000"); } catch { }
                            try { tomin = dec.timingOperatingMin.ToString("0.000"); } catch { }
                            try { tomax = dec.timingOperatingMax.ToString("0.000"); } catch { }
                            try { hso = dec.fuelHardShutoffFloor.ToString("0.000"); } catch { }
                            try { drift = dec.driftDecaySeconds.ToString("0.000"); } catch { }
                            try { maxOff = dec.maxDriftOffset.ToString("0.000"); } catch { }
                            try { coup = dec.couplingStrength.ToString("0.000"); } catch { }
                            CoopLog.Info("onc.mission.diag", () => $"OncMission diag   engine '{en}' forceOn={fo} forceOff={foff} running={run} warning={warn} fuel={fuel} inj={inj} bothBal={bal} fuelStartOk={fuelOk} injStartOk={injOk} fuelRun={fuelRun} injRun={injRun} tgtFuel={ft}(tol{ftol}) dial={fd} tgtInj={it}(tol{itol}) dial={id} opFuel=[{fomin},{fomax}] opTiming=[{tomin},{tomax}] hardShutoff={hso} drift={drift}s maxOff={maxOff} coup={coup}");
                        }
                    }
                    else CoopLog.Info("onc.mission.diag", () => "OncMission diag   no DieselEngineController in scene");
                }
                catch (Exception ee) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag engine scan: {ee.Message}"); }
                // ⚠️ 电源供电诊断：EnginePowerController.Power（引擎 running 但电源可能是 0=断电）。
                // 用户反馈"引擎在运行，但电源没更新/断电"——看 Power 实际值 + dieselEngine 引用是否绑定。
                try
                {
                    var epcs = UnityEngine.Object.FindObjectsOfType<EnginePowerController>(true);
                    if (epcs != null && epcs.Length > 0)
                    {
                        for (int p = 0; p < epcs.Length; p++)
                        {
                            var epc = epcs[p];
                            if (epc == null) continue;
                            string en = "?"; try { en = epc.gameObject.name; } catch { }
                            string power = "?", clamped = "?", prov = "?", engRef = "?", engRun = "?";
                            try { power = epc.Power.ToString("0.000"); } catch { }
                            try { clamped = epc.ClampedPower.ToString("0.000"); } catch { }
                            try { prov = epc.ProviderName; } catch { }
                            try { engRef = epc.dieselEngine == null ? "(null)" : "OK"; } catch { engRef = "(err)"; }
                            try { if (epc.dieselEngine != null) engRun = epc.dieselEngine.EnginesRunning.ToString(); } catch { }
                            // ⚠️ 对比原生（Power 0.297→0.490 上升，tgt=0.490=fuel）vs 自定义（Power=0.287 恒定）：
                            // target/current/rise/fall 差异定位根因。
                            string dtgt = "?", dcur = "?", rise = "?", fall = "?", engFuel = "?", enabled = "?", goActive = "?";
                            try { dtgt = epc._debugTargetPower.ToString("0.000"); } catch { }
                            try { dcur = epc._debugCurrentPower.ToString("0.000"); } catch { }
                            try { rise = epc.riseSpeed.ToString("0.000"); } catch { }
                            try { fall = epc.fallSpeed.ToString("0.000"); } catch { }
                            try { if (epc.dieselEngine != null) engFuel = epc.dieselEngine.FuelMixtureSystemValue.ToString("0.000"); } catch { }
                            try { enabled = epc.enabled.ToString(); } catch { }
                            try { goActive = epc.gameObject.activeInHierarchy.ToString(); } catch { }
                            CoopLog.Info("onc.mission.diag", () => $"OncMission diag   power '{en}' Power={power} Clamped={clamped} tgt={dtgt} cur={dcur} rise={rise} fall={fall} engFuel={engFuel} enabled={enabled} goActive={goActive} provider='{prov}' engineRef={engRef} engineRun={engRun}");
                        }
                    }
                    else CoopLog.Info("onc.mission.diag", () => "OncMission diag   no EnginePowerController in scene");
                }
                catch (Exception ep) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag power scan: {ep.Message}"); }
                // ⚠️ 引擎面板灯 dump（对比原生 fuel[G=True] inj[G=True] vs 自定义）——用户"断电"可能指面板灯
                try
                {
                    var ecls = UnityEngine.Object.FindObjectsOfType<EngineControlsLightsController>(true);
                    if (ecls != null)
                    {
                        for (int ei = 0; ei < ecls.Length; ei++)
                        {
                            var ecl = ecls[ei];
                            if (ecl == null) continue;
                            string en2 = "?"; try { en2 = ecl.gameObject.name; } catch { }
                            string fr = "?", fy = "?", fg = "?", ir = "?", iy = "?", ig = "?";
                            try { if (ecl._fuelLightRed != null) fr = ecl._fuelLightRed.activeSelf.ToString(); } catch { }
                            try { if (ecl._fuelLightYellow != null) fy = ecl._fuelLightYellow.activeSelf.ToString(); } catch { }
                            try { if (ecl._fuelLightGreen != null) fg = ecl._fuelLightGreen.activeSelf.ToString(); } catch { }
                            try { if (ecl._injectionLightRed != null) ir = ecl._injectionLightRed.activeSelf.ToString(); } catch { }
                            try { if (ecl._injectionLightYellow != null) iy = ecl._injectionLightYellow.activeSelf.ToString(); } catch { }
                            try { if (ecl._injectionLightGreen != null) ig = ecl._injectionLightGreen.activeSelf.ToString(); } catch { }
                            CoopLog.Info("onc.mission.diag", () => $"OncMission diag   panelLights '{en2}' fuel[R={fr} Y={fy} G={fg}] inj[R={ir} Y={iy} G={ig}]");
                        }
                    }
                }
                catch (Exception el) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag panelLights scan: {el.Message}"); }
                // ⚠️ 总电源开关定位：场景里名字含 Power/Switch/Master/Isolated 的对象（简报提示 "Restore Master
                // Power Switch to ON"——引擎 running 但电源断电可能因总电源开关 OFF）。只扫根对象 + EngineControls 子树。
                try
                {
                    var pwrRoots = sc.GetRootGameObjects();
                    int pwrCount = pwrRoots != null ? pwrRoots.Length : 0;
                    for (int pi = 0; pi < pwrCount; pi++)
                    {
                        var pgo = pwrRoots[pi];
                        if (pgo == null || pgo.name == null) continue;
                        if (pgo.name.IndexOf("Power", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            pgo.name.IndexOf("Switch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            pgo.name.IndexOf("Master", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            pgo.name.IndexOf("Isolated", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            pgo.name.IndexOf("Engine", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            CoopLog.Info("onc.mission.diag", () => $"OncMission diag   pwrRoot '{pgo.name}' active={pgo.activeSelf} parent={(pgo.transform.parent != null ? pgo.transform.parent.name : "-")}");
                            // 递归扫该根下子对象名（Power/Switch 相关）
                            try { WalkPowerObjects(pgo, 0); } catch { }
                        }
                    }
                }
                catch (Exception pw) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag power-obj scan: {pw.Message}"); }
            }
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag error: {ex.Message}"); }
        // 任务状态诊断：CurrentMission / 完成状态（床交互依赖任务完成）
        try
        {
            var mm = MissionManager.Instance;
            if (mm != null)
            {
                string phase = "?";
                try { phase = mm.CurrentPhase.ToString(); } catch { }
                string cmName = "?";
                try { if (mm.CurrentMission != null) cmName = mm.CurrentMission.MissionID ?? "?"; } catch { }
                string cs = "?";
                try
                {
                    var st = mm.CurrentMissionState;
                    if (st != null) cs = $"complete={st.Complete} failed={st.Failed}";
                }
                catch (Exception e2) { cs = "n/a(" + e2.Message + ")"; }
                CoopLog.Info("onc.mission.diag", () => $"OncMission diag MissionManager phase={phase} current='{cmName}' state={cs}");
            }
            else CoopLog.Info("onc.mission.diag", () => "OncMission diag MissionManager=null");
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag mission state error: {ex.Message}"); }
        // 实体 ID 诊断：遍历原生模板图（第一张 MapCard 的 Mission）节点，dump State_SpawnMapEntity 的实体 ID
        try
        {
            var cards = UnityEngine.Object.FindObjectsOfType<MapCard>();
            int cardCount = cards != null ? cards.Length : 0;
            CoopLog.Info("onc.mission.diag", () => $"OncMission diag MapCard count={cardCount}");
            for (int ci = 0; ci < cardCount; ci++)
            {
                var card = cards[ci];
                if (card == null || card.Mission == null) continue;
                if (card.name.IndexOf("OncCustom", StringComparison.OrdinalIgnoreCase) >= 0) continue; // 跳过自定义卡片
                string mid = "?";
                try { mid = card.Mission.MissionID; } catch { }
                // ⚠️ 场景列表：dump 每个原生任务卡片的真实 SceneName（SceneReference.sceneName）——
                // 自定义任务 SceneName 填它能进对应场景（用户要换 Chill 场景）。
                string ssc = "?";
                try { if (card.Mission.SceneReference != null) ssc = card.Mission.SceneReference.sceneName; } catch { ssc = "?"; }
                CoopLog.Info("onc.mission.diag", () => $"OncMission diag template '{card.name}' mission='{mid}' scene='{ssc}'");
                try
                {
                    var nodes = card.Mission.nodes;
                    if (nodes != null)
                    {
                        for (int ni = 0; ni < nodes.Count; ni++)
                        {
                            var n = nodes[ni];
                            if (n == null) continue;
                            // ⚠️ interop GetType() 返回静态声明类型（Node）→ 用 IL2CPP 原生 API 取真实运行时类型名
                            string tname = "?";
                            try { tname = n.GetIl2CppType().Name; } catch { try { tname = n.GetType().Name; } catch { } }
                            string nid = "?";
                            try { var sn = n.TryCast<SleepyNodes.StateNode>(); if (sn != null) nid = sn.NodeID; } catch { }
                            string extra = "";
                            if (tname.IndexOf("Spawn", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                string eid = "?", eRole = "?", eHp = "?", eNum = "?", eZone = "?", eLoc = "?", eLType = "?", eState = "?", eArmour = "?", eStars = "?", eScale = "?", eFuzzy = "?", eRand = "?";
                                try
                                {
                                    var sp = n.TryCast<SleepyNodes.State_SpawnMapEntity>();
                                    if (sp != null)
                                    {
                                        eid = sp.ID ?? "?";
                                        try { eRole = sp.Role.ToString(); } catch { }
                                        try { eHp = sp.Health.ToString(); } catch { }
                                        try { eArmour = sp.Armour.ToString(); } catch { }
                                        try { eStars = sp.Stars.ToString(); } catch { }
                                        try { eScale = sp.Scale.ToString(); } catch { }
                                        try { eNum = sp.NumberToSpawn.ToString(); } catch { }
                                        try { eState = sp.StartingState.ToString(); } catch { }
                                        // LocationToSpawn 反射读字段（ZoneID/GridLocation/LocationType/Fuzzy/RandomiseSubgrid）
                                        var lt = sp.LocationToSpawn;
                                        if (lt != null)
                                        {
                                            try { eZone = lt.ZoneID ?? "?"; } catch { }
                                            try { eLType = lt.LocationType.ToString(); } catch { }
                                            try { eFuzzy = lt.FuzzyLocation.ToString(); } catch { }
                                            try { eRand = lt.RandomiseSubgrid.ToString(); } catch { }
                                            try
                                            {
                                                // GridLocation 是 ContextVariableOrInline<GridReference> 包装器 → ToString/反射安全读
                                                var gl = lt.GridLocation;
                                                if (gl != null)
                                                {
                                                    try { eLoc = gl.ToString(); }
                                                    catch
                                                    {
                                                        var t2 = gl.GetIl2CppType();
                                                        foreach (var fn in new[] { "value", "Value", "InlineValue", "Key" })
                                                        {
                                                            try
                                                            {
                                                                var f = t2.GetField(fn);
                                                                if (f == null) continue;
                                                                var v = f.GetValue(gl);
                                                                if (v != null) { eLoc = fn + "=" + v; break; }
                                                            }
                                                            catch { }
                                                        }
                                                        if (eLoc == "?") eLoc = "(no-value-field)";
                                                    }
                                                }
                                                else eLoc = "(null)";
                                            }
                                            catch { }
                                        }
                                    }
                                }
                                catch { }
                                extra = $" entityID='{eid}' role={eRole} hp={eHp} armour={eArmour} stars={eStars} scale={eScale} num={eNum} state={eState} zone='{eZone}' locType={eLType} grid={eLoc} fuzzy={eFuzzy} rand={eRand}";
                            }
                            else if (tname.IndexOf("WaitSeconds", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                float sec = 0f;
                                try { var ws = n.TryCast<SleepyNodes.State_WaitSeconds>(); if (ws != null) sec = ws.Seconds; } catch { }
                                extra = $" seconds={sec:0.#}";
                            }
                            else if (tname.IndexOf("End", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                string to = "?";
                                try { var en = n.TryCast<SleepyNodes.State_End>(); if (en != null && en.To != null) to = en.To.NodeID; else if (en != null) to = "(null)"; } catch { }
                                extra = $" to='{to}'";
                            }
                            else if (tname.IndexOf("Objective", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                string oname = "?";
                                try { var ob = n.TryCast<SleepyNodes.State_Objective>(); if (ob != null && ob.Objective != null) { try { oname = ob.Objective.name; } catch { } } } catch { }
                                extra = $" objective='{oname}'";
                            }
                            else if (tname.IndexOf("Teleprinter", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                // dump 原生 TeleprinterText 字段（Printer/Text/OnlyQueue/WaitUntilComplete），确定缺什么
                                string fp = "?", ft = "?", fq = "?", fw = "?", fa = "?";
                                try
                                {
                                    var tn2 = n.GetIl2CppType();
                                    var fPrinter = tn2.GetField("Printer");
                                    if (fPrinter != null) { try { var v = fPrinter.GetValue(n); fp = v != null ? v.ToString() : "(null)"; } catch { } }
                                    var fText = tn2.GetField("Text");
                                    if (fText != null) { try { var v = fText.GetValue(n); ft = v != null ? v.ToString() : "(null)"; } catch { } }
                                    var fOnly = tn2.GetField("OnlyQueue");
                                    if (fOnly != null) { try { var v = fOnly.GetValue(n); fq = v != null ? v.ToString() : "(null)"; } catch { } }
                                    var fWait = tn2.GetField("WaitUntilComplete");
                                    if (fWait != null) { try { var v = fWait.GetValue(n); fw = v != null ? v.ToString() : "(null)"; } catch { } }
                                    var fAlarm = tn2.GetField("AlarmState");
                                    if (fAlarm != null) { try { var v = fAlarm.GetValue(n); fa = v != null ? v.ToString() : "(null)"; } catch { } }
                                    // ⚠️ 属性读法对照：IL2CPP 属性 get_Text 读真实 TextIdentifier（反射 GetField 可能拿到不同字段/假象）
                                    string propText = "?", propRaw = "?";
                                    try
                                    {
                                        var tpn = n.TryCast<SleepyNodes.State_TeleprinterText>();
                                        if (tpn != null)
                                        {
                                            var tv = tpn.Text;
                                            propText = tv == null ? "(null)" : tv.GetType().Name;
                                            if (tv != null) { try { propRaw = tv.Raw ?? "(null)"; } catch { } }
                                        }
                                        else propText = "(notTeleprinter)";
                                    }
                                    catch (Exception pe) { propText = "err(" + pe.Message + ")"; }
                                    extra = $" printer='{fp}' text='{ft}' propText='{propText}' raw='{propRaw}' onlyQueue='{fq}' wait='{fw}' alarm='{fa}'";
                                }
                                catch { }
                            }
                            CoopLog.Info("onc.mission.diag", () => $"OncMission diag   node '{nid}' type={tname}{extra}");
                        }
                    }
                }
                catch (Exception e3) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag node walk: {e3.Message}"); }
            }
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag template scan error: {ex.Message}"); }
    }

    /// <summary>
    /// 帧级任务驱动链追踪：0.2s 采样 MissionManager 当前执行状态（纯读取，零 Harmony 风险）。
    /// 打印主执行线 CurrentState.Node + 并行线 SideExecutionPaths + 已触发事件 + 敌人生成计数。
    /// 变化才打印（去重），完整还原节点推进序列（含敌人生成/中途生成/打字机/结束）。
    /// ⚠️ IL2CPP 限制：节点 OnEnter 无法 Harmony patch（虚方法 override 无限递归崩），故用帧级轮询。
    /// </summary>
    private static void TraceNativeFlow()
    {
        // ⚠️ 帧性能（2026-08-25）：flow 采样/打印是任务调试诊断（每 0.2s 大字符串 + 遍历）——
        // Debug 关闭（发布默认）时整体跳过，避免拖帧。需要时把 CoopLog.Level 调到 Debug 即可。
        if (CoopLog.Level > LogLevel.Debug) return;
        var mm = MissionManager.Instance;
        if (mm == null || mm.CurrentMission == null) return;
        var g = mm.CurrentMission;
        string main = "?";
        string mainType = "?";
        try
        {
            var cs = g.CurrentState;
            if (cs != null)
            {
                var n = cs.Node;
                if (n != null)
                {
                    try { mainType = n.GetIl2CppType().Name; } catch { try { mainType = n.GetType().Name; } catch { } }
                    try { main = n.TryCast<SleepyNodes.StateNode>()?.NodeID ?? "?"; } catch { }
                }
                else main = "(no node)";
            }
            else main = "(no state)";
        }
        catch { }
        // 并行线
        string side = "";
        try
        {
            var sp = g.SideExecutionPaths;
            if (sp != null && sp.Count > 0)
            {
                var en = sp.GetEnumerator();
                while (true)
                {
                    bool more; try { more = en.MoveNext(); } catch { break; }
                    if (!more) break;
                    string k = "?"; try { k = en.Current.Key ?? "?"; } catch { }
                    if (side.Length > 0) side += ", ";
                    side += k;
                }
            }
        }
        catch { }
        // 事件节点：已启用 + 已触发（AlreadyTriggered 变化 = 事件触发瞬间）
        string events = "";
        string triggered = "";
        int enabledEvents = 0;
        int triggeredEvents = 0;
        try
        {
            var evs = g.EventNodes;
            if (evs != null)
            {
                for (int i = 0; i < evs.Count; i++)
                {
                    var e = evs[i];
                    if (e == null) continue;
                    bool en = false;
                    try { en = e.EventEnabled; } catch { }
                    bool tr = false;
                    try { tr = e.AlreadyTriggered; } catch { }
                    if (en) { enabledEvents++; if (events.Length > 0) events += ", "; try { events += e.NodeID ?? "?"; } catch { } }
                    if (tr) { triggeredEvents++; if (triggered.Length > 0) triggered += ", "; try { triggered += e.NodeID ?? "?"; } catch { } }
                }
            }
        }
        catch { }
        // 敌人生成计数（FireMission.Entities 字典）
        string ent = "?";
        try
        {
            var fm = FireMission.Instance;
            if (fm != null && fm.Entities != null) ent = $"entities={fm.Entities.Count}";
            else if (fm != null) ent = "(fm no entities)";
            else ent = "(no fm)";
            // ⚠️ dump EnemyWave 实体实际世界位置（节流 2s）——确认敌人生成位置 vs 炮击命中位置
            if (fm != null && fm.Entities != null && fm.Entities.Count > 0)
            {
                float now = UnityEngine.Time.time;
                if (now - _entPosLast >= 2f)
                {
                    _entPosLast = now;
                    try
                    {
                        var en = fm.Entities.GetEnumerator();
                        int dumped = 0;
                        while (dumped < 20)
                        {
                            bool more; try { more = en.MoveNext(); } catch { break; }
                            if (!more) break;
                            string k = "?"; try { k = en.Current.Key ?? "?"; } catch { }
                            if (k.IndexOf("EnemyWave", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var me = en.Current.Value;
                                if (me != null)
                                {
                                    string hp = "?", pos = "?", alive = "?", est = "?";
                                    try { hp = me.Health.ToString(); } catch { }
                                    try { var p = me.Position; pos = $"({p.x:0.#},{p.y:0.#})"; } catch { }
                                    try { alive = me.IsAlive.ToString(); } catch { }
                                    try { est = me.State.ToString(); } catch { }
                                    CoopLog.Info("onc.mission.diag", () => $"OncMission diag EnemyWave entity '{k}' hp={hp} pos={pos} alive={alive} state={est}");
                                }
                            }
                            dumped++;
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
        // 当前节点停留帧数（识别卡住的节点：持续停留 = WaitUntilComplete 等驱动）
        _flowStay++;
        string stayInfo = "";
        string stayKey = main + "|" + mainType;
        if (stayKey != _flowLastNodeKey) { _flowLastNodeKey = stayKey; _flowStay = 0; }
        if (_flowStay > 0) stayInfo = $" stay={_flowStay * 0.2f:0.0}s";
        string sig = $"{main}|{mainType}|{side}|{enabledEvents}|{events}|{triggeredEvents}|{triggered}|{ent}|{_flowStay >= 10}";
        if (sig == _flowLastSig) return;
        _flowLastSig = sig;
        string st = "?";
        try { var s = mm.CurrentMissionState; if (s != null) st = $"complete={s.Complete} failed={s.Failed}"; } catch { }
        CoopLog.Info("onc.mission.flow", () => $"OncMission flow: main='{main}'({mainType}) side=[{side}] evEnabled({enabledEvents})=[{events}] evTriggered({triggeredEvents})=[{triggered}] {ent}{stayInfo} {st}");
    }

    /// <summary>实验：任务未完成时手动调用 CurrentMission.Run()，判断原生状态机是否被自动启动（StartMissionRuntime 的虚表调用是否生效）。</summary>
    private static void TryManualRun()
    {
        try
        {
            var mm = MissionManager.Instance;
            if (mm == null || mm.CurrentMission == null) return;
            bool done = false;
            try { var st = mm.CurrentMissionState; if (st != null) done = st.Complete; } catch { }
            if (done) return;
            CoopLog.Info("onc.mission.diag", () => "OncMission diag MANUAL Run() (state not complete) -> calling CurrentMission.Run()");
            try { mm.CurrentMission.Run(); } catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag manual run error: {ex}"); }
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag manual run wrap: {ex.Message}"); }
    }

    /// <summary>二次诊断（进场景 25s 后）：只查任务完成状态 + 交互锁（确认计时结束后任务是否完成、锁是否释放）。</summary>
    private static void LogMissionState()
    {
        try
        {
            var mm = MissionManager.Instance;
            if (mm != null)
            {
                string phase = "?";
                try { phase = mm.CurrentPhase.ToString(); } catch { }
                string cmName = "?";
                try { if (mm.CurrentMission != null) cmName = mm.CurrentMission.MissionID ?? "?"; } catch { }
                string cs = "?";
                try { var st = mm.CurrentMissionState; if (st != null) cs = $"complete={st.Complete} failed={st.Failed}"; } catch { }
                CoopLog.Info("onc.mission.diag", () => $"OncMission diag(25s) MissionManager phase={phase} current='{cmName}' state={cs}");
            }
            else CoopLog.Info("onc.mission.diag", () => "OncMission diag(25s) MissionManager=null");
            // dump 我们的任务图（CurrentMission）节点结构，对比原生（找"少东西"）
            try
            {
                var cm = mm != null ? mm.CurrentMission : null;
                if (cm != null)
                {
                    int nc = 0;
                    try { nc = cm.nodes != null ? cm.nodes.Count : 0; } catch { }
                    // ⚠️ dump 图级 Variables 字典是否为 null（State_TeleprinterText.OnEnter 遍历它做实体名替换）
                string varsInfo = "?";
                try
                {
                    var vars = cm.Variables;
                    varsInfo = vars == null ? "(null)" : $"count={vars.Count}";
                }
                catch (Exception ve) { varsInfo = $"err({ve.Message})"; }
                CoopLog.Info("onc.mission.diag", () => $"OncMission diag CURRENT graph variables={varsInfo}");
                // ⚠️ dump 图级 CurrentState（NodeExecutionState）的 State 字典是否 null + key 列表
                string csInfo = "?";
                try
                {
                    var st = cm.CurrentState;
                    if (st == null) csInfo = "(no state)";
                    else
                    {
                        var sd = st.State;
                        csInfo = sd == null ? "stateDict=(null)" : $"stateDict=count({sd.Count})";
                        if (sd != null && sd.Count > 0)
                        {
                            // IL2CPP Dictionary 枚举：TryCast + 显式枚举器（foreach 不可靠）
                            string keys = "";
                            try
                            {
                                var en = sd.GetEnumerator();
                                while (true)
                                {
                                    bool more;
                                    try { more = en.MoveNext(); } catch { break; }
                                    if (!more) break;
                                    string k = "?";
                                    try { k = en.Current.Key ?? "?"; } catch { }
                                    if (keys.Length > 0) keys += ", ";
                                    keys += k;
                                }
                            }
                            catch { }
                            csInfo += $" keys=[{keys}]";
                        }
                    }
                }
                catch (Exception ce) { csInfo = $"err({ce.Message})"; }
                CoopLog.Info("onc.mission.diag", () => $"OncMission diag CURRENT graph currentState={csInfo}");
                CoopLog.Info("onc.mission.diag", () => $"OncMission diag CURRENT graph nodes={nc}");
                    for (int i = 0; i < nc; i++)
                    {
                        var n = cm.nodes[i];
                        if (n == null) continue;
                        string tname = "?";
                        try { tname = n.GetIl2CppType().Name; } catch { try { tname = n.GetType().Name; } catch { } }
                        string nid = "?";
                        try { var sn = n.TryCast<SleepyNodes.StateNode>(); if (sn != null) nid = sn.NodeID; } catch { }
                        string xtra = "";
                        if (tname.IndexOf("Teleprinter", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try
                            {
                                var tn2 = n.GetIl2CppType();
                                string fp = "?", ft = "?", fr = "?", fk = "?", fe = "?", fq = "?", fw = "?", fa = "?";
                                var fPrinter = tn2.GetField("Printer"); if (fPrinter != null) { try { var v = fPrinter.GetValue(n); fp = v != null ? v.ToString() : "(null)"; } catch { } }
                                var fText = tn2.GetField("Text"); if (fText != null) { try { var v = fText.GetValue(n); ft = v != null ? v.GetType().Name : "(null)"; if (v != null) { try { var ti = (Localisation.TextIdentifier)v; fr = ti.Raw ?? "(null)"; fk = ti.Key ?? "(null)"; } catch { } } } catch { } }
                                var fEntity = tn2.GetField("EntityIDToReplace"); if (fEntity != null) { try { var v = fEntity.GetValue(n); fe = v == null ? "(null)" : $"{v.GetType().Name}[{TryListCount(v)}]"; } catch (Exception ee) { fe = "err(" + ee.Message + ")"; } }
                                var fOnly = tn2.GetField("OnlyQueue"); if (fOnly != null) { try { var v = fOnly.GetValue(n); fq = v != null ? v.ToString() : "(null)"; } catch { } }
                                var fWait = tn2.GetField("WaitUntilComplete"); if (fWait != null) { try { var v = fWait.GetValue(n); fw = v != null ? v.ToString() : "(null)"; } catch { } }
                                var fAlarm = tn2.GetField("AlarmState"); if (fAlarm != null) { try { var v = fAlarm.GetValue(n); fa = v != null ? v.ToString() : "(null)"; } catch { } }
                                // ⚠️ 属性读法对照（IL2CPP 属性 get_Text 读真实 TextIdentifier）
                                string propText = "?", propRaw = "?", propKey = "?";
                                try
                                {
                                    var tpn = n.TryCast<SleepyNodes.State_TeleprinterText>();
                                    if (tpn != null)
                                    {
                                        var tv = tpn.Text;
                                        propText = tv == null ? "(null)" : tv.GetType().Name;
                                        if (tv != null) { try { propRaw = tv.Raw ?? "(null)"; propKey = tv.Key ?? "(null)"; } catch { } }
                                    }
                                    else propText = "(notTeleprinter)";
                                }
                                catch (Exception pe) { propText = "err(" + pe.Message + ")"; }
                                xtra = $" printer='{fp}' text='{ft}' propText='{propText}' propRaw='{propRaw}' key='{fk}' entityIDs={fe} onlyQueue='{fq}' wait='{fw}' alarm='{fa}'";
                            }
                            catch { }
                        }
                        CoopLog.Info("onc.mission.diag", () => $"OncMission diag   cur '{nid}' type={tname}{xtra}");
                    }
                }
                else CoopLog.Info("onc.mission.diag", () => "OncMission diag CURRENT graph=null");
            }
            catch (Exception e4) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag CURRENT walk: {e4.Message}"); }
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag(25s) error: {ex.Message}"); }
    }

    /// <summary>递归找炮对象下的 Interactable 组件，打印交互状态（定位"亮了但无法交互"）。</summary>
    private static void WalkTurretInteractables(GameObject root)
    {
        try
        {
            WalkTransformInteractables(root.transform, 0);
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag walk error: {ex.Message}"); }
    }

    private static void WalkTransformInteractables(Transform t, int depth)
    {
        if (t == null || depth > 12) return;
        var ia = t.GetComponent<Interactable>();
        if (ia != null)
        {
            bool isI = false, isP = false;
            try { isI = ia.IsInteractable; } catch { }
            try { isP = ia.IsPassive; } catch { }
            CoopLog.Info("onc.mission.diag", () => $"OncMission diag   Interactable '{t.name}' interactable={isI} passive={isP}");
        }
        for (int c = 0; c < t.childCount; c++)
        {
            try { WalkTransformInteractables(t.GetChild(c), depth + 1); } catch { }
        }
    }

    /// <summary>递归扫电源/开关相关对象（名字含 Power/Switch/Master/Engine）+ 其上 Interactable/开关组件，定位"总电源开关"。</summary>
    private static void WalkPowerObjects(GameObject go, int depth)
    {
        if (go == null || go.name == null || depth > 8) return;
        try
        {
            if (go.name.IndexOf("Power", StringComparison.OrdinalIgnoreCase) >= 0 ||
                go.name.IndexOf("Switch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                go.name.IndexOf("Master", StringComparison.OrdinalIgnoreCase) >= 0 ||
                go.name.IndexOf("Isolated", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string iaInfo = "";
                try
                {
                    var ia = go.GetComponent<Interactable>();
                    if (ia != null) iaInfo = $" interactable={ia.IsInteractable} passive={ia.IsPassive}";
                }
                catch { }
                // ⚠️ 找开关状态组件：LookAtTarget（点击开关通用入口）→ 读 isClicked / isActive
                string lookInfo = "";
                try
                {
                    var lat = go.GetComponent<LookAtTarget>();
                    if (lat != null) lookInfo = $" lookAtTarget isClicked={lat.isClicked} isActive={lat.isActive}";
                }
                catch { }
                // DieselEngineStateRelay（总电源继电器）
                string relayInfo = "";
                try
                {
                    var relay = go.GetComponent<DieselEngineStateRelay>();
                    if (relay != null) relayInfo = " [HAS DieselEngineStateRelay]";
                }
                catch { }
                // ⚠️ 组件 dump：列出对象上所有 MonoBehaviour 类型名（定位 SwitchControler 是什么组件）
                string comps = "";
                try
                {
                    var mbs = go.GetComponents<MonoBehaviour>();
                    if (mbs != null && mbs.Length > 0)
                    {
                        var names = new System.Collections.Generic.List<string>();
                        for (int ci = 0; ci < mbs.Length; ci++)
                        {
                            var mb = mbs[ci];
                            if (mb == null) continue;
                            string tn = "?";
                            try { tn = mb.GetIl2CppType().Name; } catch { try { tn = mb.GetType().Name; } catch { } }
                            names.Add(tn);
                        }
                        comps = " comps=[" + string.Join(",", names) + "]";
                    }
                }
                catch { }
                CoopLog.Info("onc.mission.diag", () => $"OncMission diag     pwrObj '{go.name}' active={go.activeSelf}{iaInfo}{lookInfo}{relayInfo}{comps}");
            }
            for (int c = 0; c < go.transform.childCount; c++)
            {
                try { WalkPowerObjects(go.transform.GetChild(c).gameObject, depth + 1); } catch { }
            }
        }
        catch { }
    }

    /// <summary>启动原生格式任务：ImportMission(json) → 原生图 → LoadMission（原生完整链路：场景/相机/HUD）。</summary>
    public static bool StartNative(string id)
    {
        if (!_nativeRaw.TryGetValue(id, out var json)) return false;
        try
        {
            var graph = OncMissionImporter.Import(json);
            if (graph == null)
            {
                CoopLog.Warn("onc.mission.native", () => $"OncMission native import failed: '{id}'");
                return false;
            }
            int nodeCount = 0;
            try { nodeCount = graph.nodes != null ? graph.nodes.Count : 0; } catch { }
            CoopLog.Info("onc.mission.native", () => $"OncMission native imported '{id}' nodes={nodeCount} → StartOperation");
            // ⚠️ ImportMission 把 TextIdentifier 字段还原成 Object → 手动回填（简报/通知文本才会显示）
            try { RepairImportedTexts(graph, json); }
            catch (Exception re) { CoopLog.Warn("onc.mission.native", () => $"OncMission native repair error: {re.Message}"); }
            // ⚠️ ImportMission 硬编码场景为 MissionBase（F3 调试空场景）→ 从 JSON 读任务场景并覆盖，
            // 否则 LoadMission 会进 MissionBase（无任务内容）。
            try
            {
                string scene = ReadNativeSceneName(json);
                if (!string.IsNullOrEmpty(scene))
                {
                    var sr = new MissionSceneReference { sceneName = scene };
                    graph.SceneReference = sr;
                    CoopLog.Info("onc.mission.native", () => $"OncMission native scene='{scene}' set");
                }
                else
                {
                    CoopLog.Warn("onc.mission.native", () => $"OncMission native no SceneName for '{id}' (will load MissionBase)");
                }
            }
            catch (Exception ex) { CoopLog.Warn("onc.mission.native", () => $"OncMission native scene set error: {ex.Message}"); }
            // ImportMission 不设置 MissionDescription（MissionDefinition 无此字段）→ 手动设置（卡片详情面板用）
            try
            {
                string desc = ReadNativeString(json, "MissionDescription");
                if (!string.IsNullOrEmpty(desc))
                {
                    graph.MissionDescription = new Localisation.TextIdentifier(desc); // 属性是 TextIdentifier 非 string（与 OncMissionCardInjector 同法）
                    CoopLog.Info("onc.mission.native", () => $"OncMission native description set ({desc.Length} chars)");
                }
            }
            catch (Exception ex) { CoopLog.Warn("onc.mission.native", () => $"OncMission native description set error: {ex.Message}"); }
            // ⚠️ 引擎开局状态：JSON 可选 "EngineStart"（on/off）→ 场景加载后应用（覆盖场景默认 forceEngineOn）
            try { ScheduleEngineStart(json); }
            catch (Exception ex) { CoopLog.Warn("onc.mission.native", () => $"OncMission engine start schedule error: {ex.Message}"); }
            var mm = MissionManager.Instance;
            if (mm == null) { CoopLog.Warn("onc.mission.native", () => "MissionManager.Instance null"); return false; }
            // F3 原生流程：构造 OperationGraph（含 MissionNode.Mission=图）→ StartOperation(operation, mission)
            var op = new SleepyNodes.OperationGraph();
            try
            {
                var mn = new SleepyNodes.MissionNode();
                try { mn.Mission = graph; } catch { }
                if (op.nodes != null) op.nodes.Add(mn);
            }
            catch (Exception ex) { CoopLog.Warn("onc.mission.native", () => $"OncMission StartNative op build: {ex.Message}"); }
            // 启动原生图：优先 3-arg StartOperation + 非空 checkpoint（触发 StartMissionRuntime → 原生任务引擎
            // 完整启动，每帧驱动图推进到 State_End：打字机/通知/床交互全原生行为——用户实测 0.2.0 正常结束）。
            // 2-arg 无 checkpoint 不启动 StartMissionRuntime → 图不被原生引擎驱动，graph.Run() 建的执行线
            // 在下一帧被重置为 no state（实测 `current=(no state)` 持续、任务卡住）。3-arg 是修复关键。
            // ⚠️ 兜底：若 3-arg 后仍无执行线，PostMissionLoaded 会对 native 自定义图调 graph.Run() 建立。
            try
            {
#if MELONLOADER
                // MelonLoader Il2CppAssemblies 缺 MissionSaveData 类型（旧 interop）→ 直接 2-arg
                // （PostMissionLoaded 对 native 自定义图调 graph.Run() 兜底建执行线）
                mm.StartOperation(op, graph);
                CoopLog.Info("onc.mission.native", () => $"OncMission native StartOperation('{id}') 2-arg (ML interop lacks MissionSaveData)");
#else
                var checkpoint = new MissionSaveData();
                try
                {
                    checkpoint.MissionId = id;
                    checkpoint.OperationId = "onc." + id;
                    checkpoint.MissionElapsedTime = 0.0;
                }
                catch { }
                mm.StartOperation(op, graph, checkpoint);
                CoopLog.Info("onc.mission.native", () => $"OncMission native StartOperation('{id}') 3-arg + checkpoint (full task engine runtime)");
#endif
            }
            catch (Exception sox)
            {
                // 3-arg 失败 → 回退 2-arg（尽力启动；PostMissionLoaded graph.Run() 兜底）
                CoopLog.Warn("onc.mission.native", () => $"OncMission native StartOperation 3-arg error ({sox.Message}), fallback 2-arg");
                try { mm.StartOperation(op, graph); } catch { }
            }
            // ⚠️ 诊断：StartOperation 后图 EntryPoint 是否被识别
            try
            {
                var ep = graph.EntryPoint;
                CoopLog.Info("onc.mission.native", () => $"OncMission native entrypoint={(ep == null ? "NULL" : "OK:" + ep.GetType().Name)}");
            }
            catch (Exception rxe) { CoopLog.Warn("onc.mission.native", () => $"OncMission native entrypoint err: {rxe.Message}"); }
            _nativeDiagPending = true; // 场景加载后延迟扫描炮台对象（确认场景内容）
            _nativeDiagTimer = 0f;
            _nativeDiagSecond = false;
            _nativeDiagThird = false;
            _nativeDiagRunTried = false;
            return true;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("onc.mission.native", () => $"OncMission StartNative error: {ex.Message}");
            return false;
        }
    }

    public static void Register(OncOperation operation)
    {        if (operation == null || string.IsNullOrEmpty(operation.Id)) return;
        _operations[operation.Id] = operation;
        if (operation.Missions != null)
            for (int i = 0; i < operation.Missions.Count; i++)
            {
                var mr = operation.Missions[i];
                if (mr == null || string.IsNullOrEmpty(mr.MissionId)) continue;
                var m = FindMission(mr.MissionId);
                if (m != null)
                {
                    // 战役前置 → 任务前置（如果任务本身没写）
                    if (mr.Requires != null && mr.Requires.Count > 0 && (m.Requires == null || m.Requires.Count == 0))
                        for (int r = 0; r < mr.Requires.Count; r++)
                            m.Requires.Add(mr.Requires[r]);
                }
                else
                {
                    CoopLog.Warn("onc.op.reg", () => $"OncOperation '{operation.Id}': mission '{mr.MissionId}' not registered yet");
                }
            }
        CoopLog.Info("onc.op.reg", () => $"OncOperation registered: '{operation.Id}' missions={operation.Missions?.Count ?? 0}");
    }

    /// <summary>从 JSON 字符串注册任务。</summary>
    public static bool RegisterFromJson(string json)
    {
        var m = OncMissionIO.Load(json);
        if (m == null) return false;
        Register(m);
        return true;
    }

    /// <summary>从外部文件夹**按文件名序**读取全部 JSON 任务/战役文件并注册（自动识别战役 vs 任务）。
    /// 返回成功注册的文件数；文件夹不存在/无 json 返回 0。单文件解析失败只跳过该文件不中断。</summary>
    public static int LoadFromFolder(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return 0;
        string[] files;
        try { files = Directory.GetFiles(folderPath, "*.json", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) { CoopLog.Warn("onc.mission.folder", () => $"OncMission LoadFromFolder scan error: {ex.Message}"); return 0; }
        if (files == null || files.Length == 0) return 0;
        Array.Sort(files, StringComparer.OrdinalIgnoreCase); // 按文件名序
        int count = 0;
        for (int i = 0; i < files.Length; i++)
        {
            try
            {
                string json = File.ReadAllText(files[i]);
                if (string.IsNullOrWhiteSpace(json)) continue;
                // 自动识别：原生 MissionImporter 格式（节点含 "NodeType"）→ 直接导入原生图；
                // "Missions" 数组 → 战役（OncOperation）；否则自定义任务（OncMission）
                bool isNative = json.IndexOf("\"NodeType\"", StringComparison.Ordinal) >= 0;
                if (isNative)
                {
                    bool okNative = RegisterNativeFromJson(json);
                    if (okNative)
                    {
                        count++;
                        CoopLog.Info("onc.mission.folder", () => $"OncMission loaded '{Path.GetFileName(files[i])}' (native)");
                    }
                    else
                        CoopLog.Warn("onc.mission.folder", () => $"OncMission skip invalid native json '{Path.GetFileName(files[i])}'");
                    continue;
                }
                bool isOperation = json.IndexOf("\"Missions\"", StringComparison.Ordinal) >= 0;
                bool ok = isOperation ? RegisterOperationFromJson(json) : RegisterFromJson(json);
                if (ok)
                {
                    count++;
                    CoopLog.Info("onc.mission.folder", () => $"OncMission loaded '{Path.GetFileName(files[i])}' ({(isOperation ? "operation" : "mission")})");
                }
                else
                    CoopLog.Warn("onc.mission.folder", () => $"OncMission skip invalid json '{Path.GetFileName(files[i])}'");
            }
            catch (Exception ex) { CoopLog.Warn("onc.mission.folder", () => $"OncMission load '{Path.GetFileName(files[i])}' error: {ex.Message}"); }
        }
        if (count > 0)
            CoopLog.Info("onc.mission.folder", () => $"OncMission folder '{folderPath}': loaded {count}/{files.Length}");
        return count;
    }

    /// <summary>从游戏目录下的子文件夹（默认 "CSM" = Custom Missions）按文件名序读取 JSON 任务并注册。
    /// 自动定位游戏根目录（Application.dataPath 的上一级），并同时检查常用插件目录位置
    /// （根目录/CSM、BepInEx/CSM、MelonLoader/CSM、BepInEx/plugins/OpenNestCoop/CSM）。
    /// 全部找不到返回 0（静默）。</summary>
    public static int LoadFromGameFolder(string subFolder = "CSM")
    {
        try
        {
            string root = null;
            try
            {
                var dp = UnityEngine.Application.dataPath;
                if (!string.IsNullOrEmpty(dp))
                {
                    var di = Directory.GetParent(dp);
                    if (di != null) root = di.FullName;
                }
            }
            catch { }
            if (string.IsNullOrEmpty(root))
            {
                CoopLog.Warn("onc.mission.folder", () => "OncMission LoadFromGameFolder: cannot resolve game root");
                return 0;
            }

            var candidates = new List<string>();
            candidates.Add(Path.Combine(root, subFolder));
            candidates.Add(Path.Combine(root, "BepInEx", subFolder));
            candidates.Add(Path.Combine(root, "MelonLoader", subFolder));
            candidates.Add(Path.Combine(root, "BepInEx", "plugins", "OpenNestCoop", subFolder));

            int total = 0;
            foreach (var folder in candidates)
            {
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    int n = LoadFromFolder(folder);
                    if (n > 0) CoopLog.Info("onc.mission.folder", () => $"OncMission loaded {n} from '{folder}'");
                    total += n;
                }
                catch { }
            }
            if (total == 0)
                CoopLog.Debug("onc.mission.folder", () => $"OncMission folder not found in {candidates.Count} candidate location(s) (skip)");
            return total;
        }
        catch { return 0; }
    }

    /// <summary>从 JSON 字符串注册战役（其下任务也注册）。</summary>
    public static bool RegisterOperationFromJson(string json)
    {
        var op = OncMissionIO.LoadOperation(json);
        if (op == null) return false;
        Register(op);
        return true;
    }

    /// <summary>替换默认宿主（null 恢复默认）。</summary>
    public static void RegisterHost(IOncMissionHost host) => _host = host;

    public static OncMission FindMission(string missionId)
        => missionId != null && _missions.TryGetValue(missionId, out var m) ? m : null;

    public static OncOperation FindOperation(string operationId)
        => operationId != null && _operations.TryGetValue(operationId, out var o) ? o : null;

    /// <summary>注册「原生任务覆盖」：nativeKey 为原生 MissionID 或场景名；该原生任务启动（点卡片/加载）时
    /// 改为启动自定义任务。统一任务 ID：nativeKey 与自定义任务 Id/SceneName 统一匹配。
    /// 由 <see cref="OncMissionHooks"/> 拦截生效（只 patch 流程方法，不碰任务状态机）。</summary>
    public static void RegisterOverride(string nativeKey, OncMission custom)
    {
        if (string.IsNullOrEmpty(nativeKey) || custom == null) return;
        _overrides[nativeKey] = custom;
        Register(custom);
        CoopLog.Info("onc.mission.override", () => $"OncMission override: '{nativeKey}' -> '{custom.Id}'");
    }

    /// <summary>统一任务解析：精确 override 映射 → 注册表 id → 场景名匹配。找不到返回 null。</summary>
    public static OncMission ResolveMission(string keyOrId)
    {
        if (keyOrId == null) return null;
        if (_overrides.TryGetValue(keyOrId, out var ov)) return ov;
        if (_missions.TryGetValue(keyOrId, out var m)) return m;
        for (int i = 0; i < _ordered.Count; i++)
            if (_ordered[i] != null && _ordered[i].SceneName == keyOrId) return _ordered[i];
        return null;
    }

    /// <summary>以原生任务为范本生成自定义任务骨架（统一 ID + 场景 + 显示名），模组在此基础上用 Builder 加节点。</summary>
    public static OncMission CreateTemplateFromNative(string nativeKey)
    {
        try
        {
            var graph = ResolveNativeGraph(nativeKey);
            if (graph == null)
            {
                CoopLog.Warn("onc.mission.tpl", () => $"OncMission template: no native graph for '{nativeKey}'");
                return null;
            }
            var m = new OncMission { Id = nativeKey, SceneName = SafeSceneName(graph) };
            m.DisplayName = SafeGraphName(graph);
            if (string.IsNullOrEmpty(m.DisplayName)) m.DisplayName = nativeKey;
            CoopLog.Info("onc.mission.tpl", () => $"OncMission template created from native '{nativeKey}' scene='{m.SceneName}'");
            return m;
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.tpl", () => $"OncMission template error: {ex.Message}"); return null; }
    }

    public static IReadOnlyList<OncMission> RegisteredMissions => _ordered;

    /// <summary>标记任务已完成（前置后置解锁；原生任务完成也走这里）。</summary>
    public static void MarkMissionCompleted(string missionId)
    {
        if (string.IsNullOrEmpty(missionId)) return;
        _completedMissions.Add(missionId);
    }

    /// <summary>查询任务是否已完成（前置后置判定）。</summary>
    public static bool IsMissionCompleted(string missionId) => _completedMissions.Contains(missionId);

    // ---------------- 启动 / 停止 ----------------

    public static bool Start(string missionId)
    {
        var m = FindMission(missionId);
        if (m == null)
        {
            CoopLog.Warn("onc.mission.start", () => $"OncMission not found: '{missionId}'");
            return false;
        }
        return Start(m);
    }

    /// <summary>场景已就绪后启动自定义引擎（原生 LoadMission 已加载场景+初始化，跳过 LoadMissionScene）。
    /// 点自定义卡片 → 原生 MissionManager.LoadMission → OnMissionLoaded → 本方法。</summary>
    public static bool StartRuntimeInScene(OncMission mission)
    {
        try
        {
            if (mission == null) return false;
            var host = _host ?? (IOncMissionHost)_defaultHost;
            if (_current != null && _current.IsRunning && _current.Mission == mission) return true; // 已在跑
            Stop();
            var runtime = new OncMissionRuntime(mission, host);
            _current = runtime;
            AttachScriptedLifecycle(runtime); // B3：脚本模块生命周期（启动钩子 + 结束事件订阅）
            try { host.ApplySeed(mission); } catch (Exception ex) { CoopLog.Warn("onc.mission.seed", () => $"OncMission seed apply error: {ex.Message}"); }
            runtime.Start();
            CoopLog.Info("onc.mission.start", () => $"OncMission started(in scene): '{mission.Id}' nodes={mission.Nodes?.Count ?? 0}");
            OncMissionHud.Show(mission);
            return true;
        }
        catch (Exception ex)
        {
            CoopLog.Error("onc.mission.start", () => $"OncMission StartRuntimeInScene error: {ex}");
            _current = null;
            return false;
        }
    }

    public static bool Start(OncMission mission)
    {
        try
        {
            if (mission == null) return false;
            var host = _host ?? (IOncMissionHost)_defaultHost;

            // 前置后置：未解锁则拒绝启动
            try
            {
                if (!host.IsMissionUnlocked(mission))
                {
                    CoopLog.Warn("onc.mission.lock", () => $"OncMission locked (requires not met): '{mission.Id}'");
                    try { host.ShowNotification("任务未解锁", $"需先完成前置任务：{mission.DisplayName}", 5f); } catch { }
                    return false;
                }
            }
            catch { }

            Stop();
            var runtime = new OncMissionRuntime(mission, host);
            _current = runtime;
            AttachScriptedLifecycle(runtime); // B3：脚本模块生命周期（启动钩子 + 结束事件订阅）

            try
            {
                if (!host.LoadMissionScene(mission))
                    CoopLog.Warn("onc.mission.scene", () => $"OncMission scene load failed/ignored: '{mission.Id}' scene='{mission.SceneName}'");
            }
            catch (Exception ex) { CoopLog.Warn("onc.mission.scene", () => $"OncMission scene load error: {ex.Message}"); }

            try { host.ApplySeed(mission); } catch (Exception ex) { CoopLog.Warn("onc.mission.seed", () => $"OncMission seed apply error: {ex.Message}"); }

            runtime.Start();
            CoopLog.Info("onc.mission.start", () => $"OncMission started: '{mission.Id}' nodes={mission.Nodes?.Count ?? 0}");
            OncMissionHud.Show(mission); // 任务 HUD（任务名/目标/简报）
            return true;
        }
        catch (Exception ex)
        {
            CoopLog.Error("onc.mission.start", () => $"OncMission Start error: {ex}");
            _current = null;
            return false;
        }
    }

    public static void Stop()
    {
        _pendingReturnMission = null; // 取消待结算（新任务启动/手动停止）
        _nativeReturnPending = false; // 取消原生结算等待
        if (_current == null) return;
        try
        {
            if (_current.IsRunning) _current.Cancel();
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.stop", () => $"OncMission Stop error: {ex.Message}"); }
        try { NotifyScriptedLifecycle(false, _current); } catch { } // B3：脚本模块生命周期结束
        _current = null;
        OncMissionHud.Hide();
    }

    /// <summary>任务完成后走原生返回（2026-08-30）：构造原生上下文（CurrentOperation/CurrentMission =
    /// 导出的原生图）再调 MarkMissionComplete/MarkMissionFailed → 原生结算界面正常显示 + dismiss 返回，
    /// 原生引擎的结算/统计参数传递完整（直接 LoadMainMenu 会跳过原生结算，参数失效）。
    /// Core 任务没走 StartOperation 时 CurrentOperation 为 null，结算界面 dismiss 回调断裂会卡死——
    /// 这里手动补上原生上下文让原生结算流程完整走通。
    /// ⚠️ 2026-08-30：不能立即执行——任务完成时场景可能还在加载（SceneManager.LoadScene 异步/初始化延迟），
    /// MissionManager.Instance 可能为 null。改为 pending 调度，Update 里每帧检查场景就绪后执行（带超时兜底）。</summary>
    private static void TryNativeReturn(OncMissionRuntime runtime, bool failed)
    {
        _pendingReturnMission = runtime;
        _pendingReturnFailed = failed;
        _pendingReturnTimer = 0f;
        CoopLog.Info("onc.mission.return", () => "OncMission native return scheduled (wait for scene/MissionManager ready)");
        // 立即尝试一次（若场景已就绪则马上结算；否则交给 Update 重试）
        TryPendingNativeReturn();
    }

    /// <summary>每帧尝试待结算任务：场景/ MissionManager 就绪才执行原生结算。由 <see cref="Update"/> 调用。</summary>
    private static void TryPendingNativeReturn()
    {
        if (_pendingReturnMission == null) return;
        try
        {
            // 1) 等 MissionManager 就绪（Instance 或场景组件）
            var mm = MissionManager.Instance;
            if (mm == null)
            {
                try { mm = UnityEngine.Object.FindFirstObjectByType<MissionManager>(); }
                catch { }
            }
            if (mm == null)
            {
                _pendingReturnTimer += 0f; // 计时由 Update 累计
                return; // 还没就绪，等下一帧
            }
            var m = _pendingReturnMission;
            bool failed = _pendingReturnFailed;
            _pendingReturnMission = null; // 先清再执行（防重入）
            CoopLog.Info("onc.mission.return", () => $"OncMission MissionManager ready (t={_pendingReturnTimer:0.0}s) → native return");
            TryNativeReturnWith(mm, m, failed);
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.return", () => $"OncMission pending return error: {ex.Message}"); }
    }

    /// <summary>执行原生结算：设原生上下文 + MarkMissionComplete/Failed。需 MissionManager 已就绪。</summary>
    private static void TryNativeReturnWith(MissionManager mm, OncMissionRuntime runtime, bool failed)
    {
        try
        {
            if (runtime?.Mission == null) return;
            var mission = runtime.Mission;
            // ⚠️ 2026-08-30：优先复用 StartOperation 3-arg 建立、正被原生任务引擎驱动的 CurrentMission（同一图，
            // 任务引擎驱动它推进到 State_End → 床可交互）。重新 Import 会新建不同图 → CurrentMission.Update()
            // 驱动的是另一张图，结算上下文断裂。
            SleepyNodes.MissionGraph graph = null;
            try
            {
                var cm = mm.CurrentMission;
                if (cm != null)
                {
                    string cmId = "?"; try { cmId = cm.MissionID; } catch { }
                    if (cmId == mission.Id) graph = cm;
                    else CoopLog.Warn("onc.mission.return", () => $"OncMission return: CurrentMission id='{cmId}' != '{mission.Id}' (reuse skipped)");
                }
            }
            catch { }
            // 复用失败 → 导出 Core 任务重新 Import（兜底）
            if (graph == null)
            {
                string json = OncMissionImporter.ExportMission(mission);
                if (!string.IsNullOrEmpty(json))
                {
                    try { graph = OncMissionImporter.Import(json); } catch (Exception ex) { CoopLog.Warn("onc.mission.return", () => $"OncMission return import: {ex.Message}"); }
                }
            }
            // 2) 构造 OperationGraph（含 MissionNode.Mission=图）→ 设 CurrentOperation（结算 dismiss 回调需要）
            try
            {
                var op = new SleepyNodes.OperationGraph();
                var mn = new SleepyNodes.MissionNode();
                try { mn.Mission = graph; } catch { }
                if (op.nodes != null) op.nodes.Add(mn);
                mm.CurrentOperation = op;
            }
            catch (Exception ex) { CoopLog.Warn("onc.mission.return", () => $"OncMission return op: {ex.Message}"); }
            // 3) 设 CurrentMission（结算界面读 mission/state；若已复用则同一引用）
            if (graph != null)
            {
                try { mm.CurrentMission = graph; } catch (Exception ex) { CoopLog.Warn("onc.mission.return", () => $"OncMission return mission: {ex.Message}"); }
                // ⚠️ 2026-08-30（用户类比引擎：缺完整启动流程）：原生结算界面/床交互需要原生任务状态机
                // "在运行中"——像引擎 running 是默认值没走启动边沿一样，Core 任务没 graph.Run() 时
                // CurrentState=(no state)，MarkMissionComplete(true) 只强制标记 MissionState.Complete，
                // 结算界面/统计（TrackingValues）不完整 → "原生结算没活"。需手动 graph.Run() 建立执行线，
                // 让原生引擎沿 To 推进到 State_End（完整结算：奖章/统计/床交互可用）。
                // ⚠️ 关键：graph.Run() 后**不要立即 MarkMissionComplete**——原生引擎每帧 Update 会自动驱动
                // CurrentMission 推进到 State_End，由原生流程触发完整结算（像 StartNative 那样）。立即
                // MarkMissionComplete 会跳过原生推进（state 才 n2 就标记完成 → 结算数据不完整）。
                try
                {
                    var cs = graph.CurrentState;
                    if (cs == null)
                    {
                        // ⚠️ 2026-08-30 FIX：graph.Run() 只用于"建立执行线"，**不要提前 return**。
                        // 之前在这里 return 导致下面 CurrentMissionState.Complete=true（床交互激活）永远不执行
                        // → "问题依旧"（床不可交互）。现在 Run() 后继续走 Complete=true 双保险：
                        // 原生引擎若推进则走 State_End；若停在 n2，床仍因 Complete=true 而可交互。
                        graph.Run();
                        CoopLog.Info("onc.mission.return", () => "OncMission return: graph.Run() to establish native execution line (settlement alive)");
                        string after = "?";
                        try { after = graph.CurrentState == null ? "(still null)" : (graph.CurrentState.Node?.NodeID ?? "?"); } catch { }
                        CoopLog.Info("onc.mission.return", () => $"OncMission return: after Run() state={after} → continue to set CurrentMissionState.Complete (bed interactive)");
                    }
                    else
                    {
                        CoopLog.Info("onc.mission.return", () => "OncMission return: graph already has execution line");
                    }
                }
                catch (Exception rex) { CoopLog.Warn("onc.mission.return", () => $"OncMission return run err: {rex.Message}"); }
            }
            // 4) ⚠️ 2026-08-30 实测定案（docs §D2）：设好原生上下文（CurrentOperation/CurrentMission）后
            // **调 MarkMissionComplete/MarkMissionFailed 触发原生结算界面**（EndOfMissionUIController 显示 +
            // dismiss 返回，原生引擎结算/统计参数传递完整）。
            // ⚠️ 不依赖"设 CurrentMissionState.Complete 让床可交互"——Core 导出图的原生引擎驱动不可靠
            // （停在 n2），床交互是**原生格式任务**（StartNative 完整引擎）专属。Core 任务正确结算 = MarkMissionComplete。
            // 停止 Core runtime（避免 Core 与原生图双驱动节点——打字机打两遍）
            try { if (_current != null && _current.IsRunning) _current.Cancel(); } catch { }
            // 设等待：等原生图推进（打字机打完）→ MarkMissionComplete 触发结算界面。Update 里驱动 + 触发。
            _nativeReturnPending = true;
            _nativeReturnWaitTimer = 0f;
            _nativeReturnMarkDone = false; // 是否已调 MarkMissionComplete/MarkMissionFailed
            _nativeReturnFailed = failed;
            CoopLog.Info("onc.mission.return", () => $"OncMission return: native settlement pending (failed={failed}) → graph advance → MarkMissionComplete → EndOfMissionUI");
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.return", () => $"OncMission return error: {ex.Message}"); }
    }

    // ---------------- 驱动 / 事件 ----------------

    public static void Update(float dt)
    {
        // ⚠️ 任务完成后延迟原生结算（2026-08-30）：等场景/MissionManager 就绪再执行；超时兜底不再尝试
        if (_pendingReturnMission != null)
        {
            _pendingReturnTimer += dt;
            if (_pendingReturnTimer >= 10f)
            {
                CoopLog.Warn("onc.mission.return", () => $"OncMission native return timed out (t={_pendingReturnTimer:0.0}s) — MissionManager never ready, skip native settlement");
                // ⚠️ 2026-08-30 诊断：超时说明 MissionBase 场景可能没有 MissionManager 组件（自定义任务直接
                // LoadScene 跳过了 StartOperation 的原生初始化）。dump 场景状态确认根因（场景名/根物体/MM 组件数）。
                try
                {
                    var sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    int mmAll = 0;
                    string rootNames = "";
                    try
                    {
                        var all = UnityEngine.Object.FindObjectsOfType<MissionManager>(true);
                        mmAll = all != null ? all.Length : 0;
                        var roots = sc.GetRootGameObjects();
                        for (int i = 0; i < roots.Length && i < 25; i++)
                            rootNames += (rootNames.Length > 0 ? "," : "") + roots[i].name;
                    }
                    catch (Exception de) { rootNames = "(err " + de.Message + ")"; }
                    CoopLog.Warn("onc.mission.return", () => $"OncMission return diag: scene='{sc.name}' loaded={sc.isLoaded} rootCount={sc.rootCount} MissionManagerObjs={mmAll} roots=[{rootNames}]");
                }
                catch (Exception dx) { CoopLog.Warn("onc.mission.return", () => $"OncMission return diag err: {dx.Message}"); }
                _pendingReturnMission = null;
            }
            else
            {
                TryPendingNativeReturn();
            }
        }
        // ⚠️ 原生结算等待（2026-08-30 实测定案 docs §D2）：设好原生上下文后，驱动原生图推进（打字机打完），
        // 再调 MarkMissionComplete/MarkMissionFailed → 原生结算界面（EndOfMissionUIController）弹出 + dismiss 返回。
        // done = 原生流程已接管（phase 离开 MissionActive / 已回主菜单或选任务）。
        if (_nativeReturnPending)
        {
            _nativeReturnWaitTimer += dt;
            // ⚠️ 手动驱动原生图推进：MissionGraph.Update() 沿 To 推进 CurrentState（StartMissionRuntime 未调用时
            // 原生引擎不自动驱动——Core 任务走 2-arg StartOperation 没启动 runtime）。每帧调它推进到 State_End。
            try
            {
                var mm = MissionManager.Instance;
                if (mm != null && mm.CurrentMission != null)
                    mm.CurrentMission.Update();
            }
            catch { }
            // ⚠️ 触发原生结算：给原生图一点时间推进（打字机出字），再 MarkMissionComplete/MarkMissionFailed。
            // 延迟 0.8s：让 n2/n8 打字机文本有机会打出（用户反馈"没时间看打印机"→ 任务完成太快被中断）。
            if (!_nativeReturnMarkDone && _nativeReturnWaitTimer >= 0.8f)
            {
                _nativeReturnMarkDone = true;
                try
                {
                    var mm = MissionManager.Instance;
                    if (mm != null)
                    {
                        if (_nativeReturnFailed) { mm.MarkMissionFailed(true); CoopLog.Info("onc.mission.return", () => "OncMission return: MarkMissionFailed(true) → native settlement UI"); }
                        else { mm.MarkMissionComplete(true); CoopLog.Info("onc.mission.return", () => "OncMission return: MarkMissionComplete(true) → native settlement UI"); }
                    }
                }
                catch (Exception mex) { CoopLog.Warn("onc.mission.return", () => $"OncMission return mark err: {mex.Message}"); }
            }
            bool done = false;
            try
            {
                var mm = MissionManager.Instance;
                if (mm != null)
                {
                    string phase = "?";
                    try { phase = mm.CurrentPhase.ToString(); } catch { }
                    // 原生流程已接管返回（结算界面弹出后 dismiss 会切 phase / 回主菜单/选任务）
                    if (phase == "MainMenu" || phase == "BrowsingMap")
                        done = true;
                    else
                    {
                        // 触发结算后给原生流程几秒处理（EndOfMissionUI 显示）；若 phase 仍是 MissionActive 则继续等
                        if (_nativeReturnMarkDone && _nativeReturnWaitTimer >= 6f) done = true; // 兜底
                    }
                }
            }
            catch { }
            if (done)
            {
                _nativeReturnPending = false;
                CoopLog.Info("onc.mission.return", () => $"OncMission native settlement reached (t={_nativeReturnWaitTimer:0.0}s) → native flow handles return");
            }
            // ⚠️ 兜底只防极端卡死：超时 120s 仅记录。
            else if (_nativeReturnWaitTimer >= 120f)
            {
                _nativeReturnPending = false;
                CoopLog.Warn("onc.mission.return", () => "OncMission native settlement wait exceeded 120s — stop waiting");
            }
        }
        // ⚠️ 帧级任务驱动链追踪（纯读取 MissionManager，零 Harmony 风险）：0.2s 采样当前执行节点，
        // 记录节点推进序列（主执行线 CurrentState.Node + 并行线 SideExecutionPaths + 事件线 EventNodes）
        _flowTimer += dt;
        if (_flowTimer >= 0.2f)
        {
            _flowTimer = 0f;
            try { TraceNativeFlow(); }
            catch (Exception fx) { CoopLog.Warn("onc.mission.diag", () => $"OncMission flow trace error: {fx.Message}"); }
        }
        // ⚠️ 手动简报延迟打印（场景加载后打字机就绪再打）
        if (_manualBriefText != null)
        {
            _manualBriefTimer -= dt;
            if (_manualBriefTimer <= 0f)
            {
                string t = _manualBriefText;
                _manualBriefText = null;
                ManualPrintBriefing(t);
            }
        }
        // ⚠️ 引擎开局状态应用（JSON EngineStart="on"/"off"；场景加载后引擎对象就绪才设置）
        try { TryApplyEngineStart(dt); }
        catch (Exception ex) { CoopLog.Warn("onc.mission.native", () => $"OncMission engine start update error: {ex.Message}"); }
        // ⚠️ 脚本化模块：检测原生图脚本锚点节点进入（State_CustomTrackingVariable 载体 / onc_script_ 节点 id）
        try { PollNativeScripted(); }
        catch (Exception ex) { CoopLog.Warn("onc.mission.script", () => $"OncMission poll scripted update error: {ex.Message}"); }
        // ⚠️ 脚本化模块：实体摧毁事件轮询（订阅了 entity.destroyed 前缀才工作）
        try { PollEntityDestroyed(dt); }
        catch (Exception ex) { CoopLog.Warn("onc.mission.script", () => $"OncMission poll entity update error: {ex.Message}"); }
        // ⚠️ 引擎供电重启状态机 + 持续保电（ForceEngineOff→ForceEngineOn 触发启动边沿升 Power，掉回则补 ForceEngineOn）
        try { UpdateEnginePowerRestart(dt); }
        catch (Exception ex) { CoopLog.Warn("onc.mission.native", () => $"OncMission engine power restart update error: {ex.Message}"); }
        // 原生任务进场景后 3s 扫描（完整诊断），25s 再查一次任务状态（确认 15s 计时后是否完成）
        if (_nativeDiagPending)
        {
            _nativeDiagTimer += dt;
            if (_nativeDiagTimer >= 3f && !_nativeDiagSecond)
            {
                _nativeDiagSecond = true;
                try { LogNativeSceneTurrets(); }
                catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag error: {ex.Message}"); }
            }
            if (_nativeDiagTimer >= 25f && !_nativeDiagThird)
            {
                _nativeDiagThird = true;
                try
                {
                    LogMissionState();
                    TryManualRun(); // 实验：若未完成，手动调用 CurrentMission.Run() 判断状态机是否被自动启动
                }
                catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag2 error: {ex.Message}"); }
            }
            if (_nativeDiagTimer >= 45f)
            {
                _nativeDiagPending = false;
                try { LogMissionState(); }
                catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag3 error: {ex.Message}"); }
            }
        }
        // ⚠️ 原生任务引擎参考捕获（3s/8s/13s 采样 3 次，看引擎/电源/主电源开关稳定值）
        if (_nativeRefPending)
        {
            _nativeRefTimer += dt;
            int sample = -1;
            if (_nativeRefCount == 0 && _nativeRefTimer >= 3f) sample = 0;
            else if (_nativeRefCount == 1 && _nativeRefTimer >= 8f) sample = 1;
            else if (_nativeRefCount == 2 && _nativeRefTimer >= 13f) sample = 2;
            if (sample >= 0)
            {
                _nativeRefCount = sample + 1;
                try { CaptureNativeEngineRef(); }
                catch (Exception ex) { CoopLog.Warn("onc.mission.diag", () => $"OncMission diag native-ref error: {ex.Message}"); }
                if (_nativeRefCount >= 3) _nativeRefPending = false;
            }
            // 超时兜底（15s 后强制结束，防残留）
            if (_nativeRefTimer >= 20f) _nativeRefPending = false;
        }
        if (_current == null || !_current.IsRunning)
        {
            // 未运行：若 HUD 残留则隐藏（完成/失败/停止后）
            OncMissionHud.Hide();
            return;
        }
        // ⚠️ 原生/离开任务守卫（2026-08-25）：自定义任务 HUD 只在自定义任务运行时显示。
        // 游戏已不在任务中（主菜单/选任务，phase≠MissionActive）→ 自定义引擎任务已退出但 runtime 未 Stop
        // → 强制停止 + 隐藏 HUD（否则自定义 HUD 残留，在之后进入的原生任务里错误弹出）。
        try
        {
            var mm = MissionManager.Instance;
            if (mm != null && (int)mm.CurrentPhase != 2)
            {
                CoopLog.Debug("onc.mission.hud", () => $"OncMission HUD guard: phase={(int)mm.CurrentPhase} != MissionActive → stop custom runtime + hide HUD");
                Stop();
                return;
            }
        }
        catch { }
        try { _current.Update(dt); }
        catch (Exception ex) { CoopLog.Warn("onc.mission.update", () => $"OncMission Update error: {ex.Message}"); }
        OncMissionHud.Refresh(_current); // 每帧刷新目标状态
        // 临时诊断：每 2s 报告任务驱动状态（确认 runtime 真在推进）
        _diagTimer += dt;
        if (_diagTimer >= 2f)
        {
            _diagTimer = 0f;
            CoopLog.Info("onc.mission.update", () => $"[OncMission] running '{_current.Mission?.Id}' elapsed={_current.Elapsed:0.0}s");
        }
    }

    // ⚠️ Raise(string, object) 定义在脚本化模块区（事件分发 A 桥接入口：喂 Core 图 + 分派脚本模块事件订阅）——
    // 旧的"只喂 _current"版本已移除，统一走扩展版。

    /// <summary>异步加载已注册任务的地图图（走默认/自定义宿主）。onLoaded 参数为 UnityEngine.Sprite（失败为 null）。</summary>
    public static void LoadMapSprite(string missionId, bool topography, Action<object> onLoaded)
    {
        var m = FindMission(missionId);
        if (m == null) { onLoaded?.Invoke(null); return; }
        var host = _host ?? (IOncMissionHost)_defaultHost;
        try { host.LoadMapSprite(m, topography, onLoaded); }
        catch (Exception ex) { CoopLog.Warn("onc.mission.map", () => $"OncMission LoadMapSprite error: {ex.Message}"); onLoaded?.Invoke(null); }
    }

    // ---------------- 默认宿主（游戏适配） ----------------

    /// <summary>默认宿主：把 Core 引擎输出接到真实游戏。精细逻辑（坐标换算/资源发放）建议模组覆写。</summary>
    private sealed class DefaultHost : OncMissionHostAdapter
    {
        public override void OnMissionStarted(OncMissionRuntime runtime)
        {
            CoopLog.Info("onc.host.start", () => $"OncMission host started '{runtime.Mission?.Id}'");
            try { ShowNotification("任务开始", runtime.Mission?.DisplayName ?? runtime.Mission?.Id ?? "", 4f); } catch { }
        }

        public override void OnNodeEntered(OncMissionRuntime runtime, OncNode node)
            => CoopLog.Debug("onc.host.node", () => $"OncMission node '{node.Id}' kind={node.Kind}");

        public override void OnMissionCompleted(OncMissionRuntime runtime)
        {
            CoopLog.Info("onc.host.done", () => $"OncMission completed '{runtime.Mission?.Id}' (elapsed={runtime.Elapsed:0.0}s)");
            if (runtime.Mission != null) MarkMissionCompleted(runtime.Mission.Id); // 前置/后置解锁
            OncMissionHud.Hide();
            ShowNotification("任务完成", (runtime.Mission?.DisplayName ?? "") + " 已完成", 6f);
            // ⚠️ 2026-08-30：走原生返回——构造原生上下文（CurrentOperation/CurrentMission）再 MarkMissionComplete，
            // 原生结算界面正常显示并 dismiss 返回（原生引擎结算/统计参数传递完整；直接 LoadMainMenu 会跳过原生结算）。
            TryNativeReturn(runtime, false);
        }

        public override void OnMissionFailed(OncMissionRuntime runtime)
        {
            CoopLog.Info("onc.host.fail", () => $"OncMission failed '{runtime.Mission?.Id}'");
            OncMissionHud.Hide();
            ShowNotification("任务失败", (runtime.Mission?.DisplayName ?? "") + " 失败", 6f);
            // ⚠️ 2026-08-30：走原生返回（MarkMissionFailed 原生结算界面 + dismiss 返回）。
            TryNativeReturn(runtime, true);
        }

        public override void OnMissionCanceled(OncMissionRuntime runtime)
            => CoopLog.Info("onc.host.cancel", () => $"OncMission canceled '{runtime.Mission?.Id}'");

        public override bool IsMissionUnlocked(OncMission mission)
        {
            if (mission == null) return false;
            if (mission.Requires == null || mission.Requires.Count == 0) return true;
            for (int i = 0; i < mission.Requires.Count; i++)
                if (!_completedMissions.Contains(mission.Requires[i]))
                {
                    CoopLog.Debug("onc.host.lock", () => $"OncMission '{mission.Id}' needs '{mission.Requires[i]}'");
                    return false;
                }
            return true;
        }

        public override bool LoadMissionScene(OncMission mission)
        {
            try
            {
                if (mission == null || string.IsNullOrEmpty(mission.SceneName)) return true; // 当前场景

                // ⚠️ 2026-08-30（实测修正）：Core 任务**必须走原生 StartOperation 流程加载场景**，不能裸
                // SceneManager.LoadScene——MissionBase 等任务场景是空壳（仅 TempMapRoot/PlayerSpawnTrigger 等
                // 框架对象），完整内容（炮台/地图/实体，~5800 renderer）由原生 StartOperation→LoadMission 动态生成；
                // 裸 LoadScene 会：① 场景内容不生成 → 黑屏；② 绕过 MissionManager 生命周期 → 结算时 MissionManager
                // 不可用（"卡结算"）。因此：导出 Core 任务为原生图 → 构造 OperationGraph → mm.StartOperation。
                var mm = MissionManager.Instance;
                if (mm == null)
                {
                    // ⚠️ MissionManager 不存在（未走原生主菜单流程）→ 回退裸 LoadScene（尽力加载场景，结算不可用）
                    CoopLog.Warn("onc.host.scene", () => "OncMission scene: MissionManager null, fallback SceneManager.LoadScene (settlement unavailable)");
                    UnityEngine.SceneManagement.SceneManager.LoadScene(mission.SceneName,
                        UnityEngine.SceneManagement.LoadSceneMode.Single);
                    return true;
                }
                // 1) 导出 Core 任务 → 原生 MissionGraph（StartOperation 需要真图；含 SceneName 引用）
                string json = OncMissionImporter.ExportMission(mission);
                var graph = OncMissionImporter.Import(json);
                if (graph == null)
                {
                    CoopLog.Warn("onc.host.scene", () => "OncMission scene: import failed, fallback SceneManager.LoadScene");
                    UnityEngine.SceneManagement.SceneManager.LoadScene(mission.SceneName,
                        UnityEngine.SceneManagement.LoadSceneMode.Single);
                    return true;
                }
                // 场景引用（Import 后默认 MissionBase；确保用任务声明的场景）
                try
                {
                    var sr = new MissionSceneReference { sceneName = mission.SceneName };
                    graph.SceneReference = sr;
                }
                catch (Exception ex) { CoopLog.Warn("onc.host.scene", () => $"OncMission scene: set SceneReference err: {ex.Message}"); }
                // 2) 构造 OperationGraph（含 MissionNode.Mission=图）→ StartOperation 原生加载场景+初始化。
                // ⚠️ 2026-08-30（用户澄清："和修补原生引擎一样——原生任务引擎"）：必须让**原生任务引擎完整启动**
                // （StartMissionRuntime 每帧驱动 graph 推进），否则图停在 Start 不推进、床交互/完整结算不活。
                // 2-arg StartOperation 不建立执行线（CurrentState null）→ 图不驱动。**3-arg 带非空 MissionSaveData**
                // 走完整启动链（StartMissionRuntime）→ 任务引擎驱动到 State_End → 床可交互 → 上床睡觉 → 原生结算返回。
                try
                {
                    var op = new SleepyNodes.OperationGraph();
                    var mn = new SleepyNodes.MissionNode();
                    try { mn.Mission = graph; } catch { }
                    if (op.nodes != null) op.nodes.Add(mn);
#if MELONLOADER
                    // MelonLoader interop 缺 MissionSaveData → 2-arg（尽力加载场景）
                    mm.StartOperation(op, graph);
                    CoopLog.Info("onc.host.scene", () => $"OncMission scene: StartOperation 2-arg (ML interop lacks MissionSaveData)");
                    return true;
#else
                    // 非空 checkpoint：触发完整 StartMissionRuntime（任务引擎启动，像引擎 ForceEngineOn 触发完整启动）
                    var checkpoint = new MissionSaveData();
                    try
                    {
                        checkpoint.MissionId = mission.Id;
                        checkpoint.OperationId = "onc." + mission.Id;
                        checkpoint.MissionElapsedTime = 0.0;
                    }
                    catch { }
                    mm.StartOperation(op, graph, checkpoint);
                    CoopLog.Info("onc.host.scene", () => $"OncMission scene: StartOperation(3-arg + checkpoint) via native flow (full task engine runtime start)");
                    return true;
#endif
                }
                catch (Exception ex)
                {
                    // 3-arg 失败 → 回退 2-arg（尽力加载场景）
                    CoopLog.Warn("onc.host.scene", () => $"OncMission scene: StartOperation 3-arg error ({ex.Message}), fallback 2-arg");
                    try
                    {
                        var op2 = new SleepyNodes.OperationGraph();
                        var mn2 = new SleepyNodes.MissionNode();
                        try { mn2.Mission = graph; } catch { }
                        if (op2.nodes != null) op2.nodes.Add(mn2);
                        mm.StartOperation(op2, graph);
                        return true;
                    }
                    catch (Exception ex2)
                    {
                        CoopLog.Warn("onc.host.scene", () => $"OncMission scene: StartOperation 2-arg error, fallback LoadScene: {ex2.Message}");
                        UnityEngine.SceneManagement.SceneManager.LoadScene(mission.SceneName,
                            UnityEngine.SceneManagement.LoadSceneMode.Single);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                CoopLog.Warn("onc.host.scene", () => $"OncMission LoadMissionScene error: {ex.Message}");
                return false;
            }
        }

        public override int ApplySeed(OncMission mission)
        {
            if (mission == null || mission.Seed <= 0) return mission == null ? -1 : mission.Seed;
            try
            {
                var fm = FireMission.Instance;
                if (fm != null)
                {
                    fm.useFixedSeed = true;
                    fm.fixedSeed = mission.Seed;
                    CoopLog.Info("onc.host.seed", () => $"OncMission seed={mission.Seed} applied to FireMission");
                }
            }
            catch (Exception ex) { CoopLog.Warn("onc.host.seed", () => $"OncMission ApplySeed error: {ex.Message}"); }
            return mission.Seed;
        }

        public override void ShowNotification(string title, string description, float duration)
        {
            try
            {
                var mgr = UINotificationManager.Instance;
                if (mgr == null) return;
                var noBorder = new Il2CppSystem.Nullable<UnityEngine.Color>();
                UINotificationManager.ShowNotification(title ?? "", description ?? "", duration > 0 ? duration : 4f, noBorder);
            }
            catch (Exception ex) { CoopLog.Warn("onc.host.notify", () => $"OncMission ShowNotification error: {ex.Message}"); }
        }

        public override void PrintTeleprinter(string text)
        {
            try
            {
                var tp = FindAnyPrinter();
                if (tp == null) return;
                var il2cppLines = new Il2CppSystem.Collections.Generic.List<string>();
                Il2CppSystem.String ilstr = text ?? "";
                il2cppLines.Add(ilstr);
                var val = ((Il2CppObjectBase)il2cppLines)
                    .TryCast<Il2CppSystem.Collections.Generic.IEnumerable<string>>();
                if (val != null)
                {
                    tp.SubmitLines("", val, null, false);
                    try { tp.TryStart(true); } catch { }
                    CoopLog.Debug("onc.host.print", () => $"OncMission printed: '{Truncate(text, 120)}'");
                }
            }
            catch (Exception ex) { CoopLog.Warn("onc.host.print", () => $"OncMission PrintTeleprinter error: {ex.Message}"); }
        }

        // ---- 地图实体（Fire 目标）----

        public override void SpawnEntity(OncNode n)
        {
            try
            {
                var fm = FireMission.Instance;
                if (fm == null)
                {
                    CoopLog.Warn("onc.host.spawn", () => "OncMission SpawnEntity: FireMission null");
                    return;
                }
                EntityRoles role = ParseRole(n.Role);
                var world = new UnityEngine.Vector3(n.X, 0f, n.Y);
                var ent = fm.CreateMapEntity(n.EntityId ?? "ent-" + n.Id,
                    null, 0, world, role, n.Health > 0 ? n.Health : 100, n.Armour, n.Stars, default, null);
                CoopLog.Info("onc.host.spawn", () => $"OncMission spawned '{n.EntityId}' at ({n.X:0.#},{n.Y:0.#}) role={role} hp={n.Health}");
            }
            catch (Exception ex) { CoopLog.Warn("onc.host.spawn", () => $"OncMission SpawnEntity error: {ex.Message}"); }
        }

        public override void MoveEntity(OncNode n)
        {
            try
            {
                var fm = FireMission.Instance;
                if (fm == null || fm.Entities == null) return;
                MapEntity ent = null;
                try
                {
                    if (fm.Entities.TryGetValue(n.EntityId ?? "", out var e)) ent = e;
                }
                catch { }
                if (ent == null)
                {
                    CoopLog.Warn("onc.host.move", () => $"OncMission MoveEntity: '{n.EntityId}' not found");
                    return;
                }
                var world = new UnityEngine.Vector3(n.X, 0f, n.Y);
                fm.MoveMapEntity(ent, world, !n.Smooth, n.Seconds > 0 ? n.Seconds : 3f);
                CoopLog.Debug("onc.host.move", () => $"OncMission moved '{n.EntityId}' -> ({n.X:0.#},{n.Y:0.#})");
            }
            catch (Exception ex) { CoopLog.Warn("onc.host.move", () => $"OncMission MoveEntity error: {ex.Message}"); }
        }

        public override void DamageEntity(string entityId, int damage)
        {
            try
            {
                var fm = FireMission.Instance;
                if (fm == null || fm.Entities == null) return;
                MapEntity ent = null;
                try { if (fm.Entities.TryGetValue(entityId ?? "", out var e)) ent = e; } catch { }
                if (ent == null) { CoopLog.Warn("onc.host.dmg", () => $"OncMission DamageEntity: '{entityId}' not found"); return; }
                int hp = ent.Health - damage;
                ent.Health = hp < 0 ? 0 : hp;
                CoopLog.Debug("onc.host.dmg", () => $"OncMission damaged '{entityId}' -{damage} -> hp={ent.Health}");
            }
            catch (Exception ex) { CoopLog.Warn("onc.host.dmg", () => $"OncMission DamageEntity error: {ex.Message}"); }
        }

        public override bool IsEntityDestroyed(string entityId)
        {
            try
            {
                var fm = FireMission.Instance;
                if (fm == null || fm.Entities == null) return false;
                MapEntity ent = null;
                try { if (fm.Entities.TryGetValue(entityId ?? "", out var e)) ent = e; } catch { }
                if (ent == null) return true; // 找不到 = 视为已摧毁（防卡）
                return !ent.IsAlive || ent.Health <= 0;
            }
            catch { return false; }
        }

        // ---- 资源奖励（尽力而为；坐标换算/精确发放建议模组覆写）----

        public override void AddRequisitionPoints(int amount)
        {
            try
            {
                var mgr = MissionManager.Instance;
                if (mgr == null) return;
                // MissionManager 没有公开加补给点方法；用 MissionStatsTracker 写会触发 AntiTamper。
                // 预留：宿主覆写接入 RequisitionSync / 购买系统。
                CoopLog.Debug("onc.host.rp", () => $"OncMission +{amount} requisition points (override host for exact)");
            }
            catch (Exception ex) { CoopLog.Warn("onc.host.rp", () => $"OncMission AddRequisitionPoints error: {ex.Message}"); }
        }

        public override void AddShell(string shellId, int amount, int slot)
        {
            try
            {
                CoopLog.Debug("onc.host.shell", () => $"OncMission +{amount} shell '{shellId}' slot={slot} (override host for exact)");
            }
            catch (Exception ex) { CoopLog.Warn("onc.host.shell", () => $"OncMission AddShell error: {ex.Message}"); }
        }

        public override void AddPowderCharge(int amount)
        {
            try
            {
                CoopLog.Debug("onc.host.powder", () => $"OncMission +{amount} powder charge (override host for exact)");
            }
            catch (Exception ex) { CoopLog.Warn("onc.host.powder", () => $"OncMission AddPowderCharge error: {ex.Message}"); }
        }

        public override void UnlockSceneObject(string objectId)
        {
            try
            {
                CoopLog.Debug("onc.host.unlock", () => $"OncMission unlock scene object '{objectId}' (override host for exact)");
            }
            catch (Exception ex) { CoopLog.Warn("onc.host.unlock", () => $"OncMission UnlockSceneObject error: {ex.Message}"); }
        }

        public override void SetEntityState(string entityId, string state, int value)
        {
            try
            {
                CoopLog.Debug("onc.host.state", () => $"OncMission set entity '{entityId}' state={state} value={value} (override host)");
            }
            catch (Exception ex) { CoopLog.Warn("onc.host.state", () => $"OncMission SetEntityState error: {ex.Message}"); }
        }

        public override void TriggerImpact(float x, float y)
        {
            try
            {
                CoopLog.Debug("onc.host.impact", () => $"OncMission trigger impact at ({x:0.#},{y:0.#}) (override host)");
            }
            catch (Exception ex) { CoopLog.Warn("onc.host.impact", () => $"OncMission TriggerImpact error: {ex.Message}"); }
        }

        public override void RunScriptedModule(OncScriptContext ctx)
            => OncMissionBridge.RunScriptedModule(ctx); // 分派到 OncMissionBridge 脚本模块注册表

        /// <summary>B1 脚本化条件：分派模块读 BoolResult（true → To[0]，false → To[1]）。</summary>
        public override bool RunScriptedCondition(OncScriptContext ctx)
        {
            if (ctx == null) return true;
            ctx.BoolResult = false;
            OncMissionBridge.RunScriptedModule(ctx); // 模块设 ctx.BoolResult
            return ctx.BoolResult;
        }

        /// <summary>B2 脚本化挂起：每帧问模块"好了没"（BoolResult=true 才完成）。</summary>
        public override bool RunScriptedWait(OncScriptContext ctx)
        {
            if (ctx == null) return true;
            ctx.BoolResult = false;
            OncMissionBridge.RunScriptedModule(ctx);
            return ctx.BoolResult;
        }

        /// <summary>A4 脚本事件广播：主机权威广播 + 本地触发（本端图 + 订阅）。</summary>
        public override void BroadcastScriptEvent(string eventId, object payload)
        {
            MissionScriptSync.Broadcast(eventId);     // 主机权威广播（客机调用 → 上报主机转发）
            OncMissionBridge.Raise(eventId, payload); // 本地也触发（本端图 + 订阅）
        }

        /// <summary>A2 计时器到期：Core runtime 计时器归零 → 转发全局事件（脚本模块可订阅 timer.expired.<id>）。</summary>
        public override void OnTimerExpired(string timerId)
        {
            OncMissionBridge.Raise("timer.expired." + timerId, timerId);
            OncMissionBridge.Raise("timer.expired", timerId);
        }

        /// <summary>异步加载任务地图图（IronRoadMap 地图/地形图，走游戏 MissionMapLoader.Acquire）。
        /// 按 scene/MissionID 解析 MissionGraph；onLoaded 回调参数为 UnityEngine.Sprite（失败为 null）。</summary>
        public override void LoadMapSprite(OncMission mission, bool topography, Action<object> onLoaded)
        {
            try
            {
                if (mission == null) { onLoaded?.Invoke(null); return; }
                var graph = ResolveMissionGraph(mission);
                if (graph == null)
                {
                    CoopLog.Warn("onc.host.map", () => $"OncMission LoadMapSprite: no MissionGraph for '{mission.Id}' (scene='{mission.SceneName}')");
                    onLoaded?.Invoke(null);
                    return;
                }
                // ⚠️ interop 签名是 Il2CppSystem.Action<Sprite>（il2cpp 委托），不能用 lambda 直传
                // → DelegateSupport.ConvertDelegate 把托管回调桥接成 il2cpp 委托。
                var cb = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UnityEngine.Sprite>>(
                    new System.Action<UnityEngine.Sprite>(sprite =>
                    {
                        try { onLoaded?.Invoke(sprite); } catch { }
                    }));
                MissionMapLoader.Acquire(graph, topography, cb);
                CoopLog.Info("onc.host.map", () => $"OncMission LoadMapSprite requested '{mission.Id}' topography={topography}");
            }
            catch (Exception ex)
            {
                CoopLog.Warn("onc.host.map", () => $"OncMission LoadMapSprite error: {ex.Message}");
                onLoaded?.Invoke(null);
            }
        }

        /// <summary>把 OncMission 解析成游戏 MissionGraph（地图图加载用）：当前任务图匹配 &gt; 场景 MapCard 匹配。</summary>
        private static SleepyNodes.MissionGraph ResolveMissionGraph(OncMission mission)
        {
            try
            {
                var mgr = MissionManager.Instance;
                if (mgr != null && mgr.CurrentMission != null)
                {
                    var cm = mgr.CurrentMission;
                    if (cm.MissionID == mission.Id || MatchMission(mission.SceneName, cm))
                        return cm;
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(mission.SceneName))
            {
                try
                {
                    var cards = UnityEngine.Resources.FindObjectsOfTypeAll<MapCard>();
                    if (cards != null)
                    {
                        for (int i = 0; i < cards.Length; i++)
                        {
                            var card = cards[i];
                            if (card == null || card.Mission == null) continue;
                            if (MatchMission(mission.SceneName, card.Mission)) return card.Mission;
                        }
                    }
                }
                catch { }
            }
            return null;
        }
    }

    // ---------------- 辅助 ----------------

    /// <summary>Harmony 钩子调用：原生任务（MissionGraph）启动时若被覆盖 → 启动自定义任务并返回 true（应阻止原生启动）。
    /// ⚠️ 只在注册了 override 且匹配时拦截；其余原生任务一律放行。防环：自定义任务已在跑则不重复启动。</summary>
    internal static bool TryStartOverride(SleepyNodes.MissionGraph native)
    {
        try
        {
            if (native == null || _overrides.Count == 0) return false;
            OncMission custom = null;
            string nativeId = null;
            try { nativeId = native.MissionID; } catch { }
            if (nativeId != null && _overrides.TryGetValue(nativeId, out custom)) { }
            else
            {
                string scene = SafeSceneName(native);
                foreach (var kv in _overrides)
                    if (kv.Value.SceneName == scene) { custom = kv.Value; break; }
            }
            if (custom == null) return false;
            if (_current != null && _current.IsRunning && _current.Mission == custom) return true; // 防环：已在跑
            CoopLog.Info("onc.mission.override", () => $"OncMission overriding native '{nativeId}' -> '{custom.Id}'");
            Start(custom);
            return true;
        }
        catch { return false; }
    }

    /// <summary>按原生任务 key（MissionID / 场景名）解析 MissionGraph（当前任务图 > 场景 MapCard）。</summary>
    private static SleepyNodes.MissionGraph ResolveNativeGraph(string key)
    {
        try
        {
            var mgr = MissionManager.Instance;
            if (mgr != null && mgr.CurrentMission != null)
            {
                var cm = mgr.CurrentMission;
                if (cm.MissionID == key || SafeSceneName(cm) == key) return cm;
            }
        }
        catch { }
        if (!string.IsNullOrEmpty(key))
        {
            try
            {
                var cards = UnityEngine.Resources.FindObjectsOfTypeAll<MapCard>();
                if (cards != null)
                    for (int i = 0; i < cards.Length; i++)
                    {
                        var card = cards[i];
                        if (card == null || card.Mission == null) continue;
                        if (card.Mission.MissionID == key || SafeSceneName(card.Mission) == key) return card.Mission;
                    }
            }
            catch { }
        }
        return null;
    }

    public static string SafeSceneName(SleepyNodes.MissionGraph graph)
    {
        try { var sr = graph?.SceneReference; if (sr != null) return sr.sceneName ?? ""; } catch { }
        return "";
    }

    private static string SafeGraphName(SleepyNodes.MissionGraph graph)
    {
        try { return graph?.MissionID ?? ""; } catch { return ""; }
    }

    private static bool MatchMission(string scene, SleepyNodes.MissionGraph graph)
    {
        try
        {
            if (graph == null) return false;
            var sr = graph.SceneReference;
            if (sr != null && sr.sceneName == scene) return true;
            if (graph.MissionID == scene) return true;
        }
        catch { }
        return false;
    }

    private static EntityRoles ParseRole(string role)
    {
        if (string.IsNullOrEmpty(role)) return EntityRoles.Enemy;
        if (Enum.TryParse(role, true, out EntityRoles r)) return r;
        return EntityRoles.Enemy;
    }

    private static Teleprinter FindAnyPrinter()
    {
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<Teleprinter>();
            if (all != null && all.Length > 0) return all[0];
        }
        catch { }
        return null;
    }

    private static string Truncate(string s, int max = 240)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    /// <summary>反射读任意对象（含 IL2CPP 泛型 List）的 Count（null/非 List 返回 -1）。</summary>
    private static int TryListCount(object o)
    {
        if (o == null) return -1;
        try
        {
            var p = o.GetType().GetProperty("Count");
            if (p != null)
            {
                var v = p.GetValue(o);
                if (v is int c) return c;
            }
        }
        catch { }
        return -2; // 非 List
    }
}
