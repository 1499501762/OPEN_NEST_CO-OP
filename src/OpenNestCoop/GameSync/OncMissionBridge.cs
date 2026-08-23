using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime;

using OpenNestCoop.Core;
using OpenNestCore.Tasks;
#if MELONLOADER
using SleepyNodes = Il2CppSleepyNodes;
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
    private static readonly List<OncMission> _ordered = new List<OncMission>();

    /// <summary>已完成的（原生或自定义）任务 id 集合——前置/后置任务解锁依据。</summary>
    private static readonly HashSet<string> _completedMissions = new HashSet<string>();

    private static IOncMissionHost _host;
    private static OncMissionRuntime _current;
    private static readonly OncMissionHostAdapter _defaultHost = new DefaultHost();

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

    public static void Register(OncOperation operation)
    {
        if (operation == null || string.IsNullOrEmpty(operation.Id)) return;
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
                    return false;
                }
            }
            catch { }

            Stop();
            var runtime = new OncMissionRuntime(mission, host);
            _current = runtime;

            try
            {
                if (!host.LoadMissionScene(mission))
                    CoopLog.Warn("onc.mission.scene", () => $"OncMission scene load failed/ignored: '{mission.Id}' scene='{mission.SceneName}'");
            }
            catch (Exception ex) { CoopLog.Warn("onc.mission.scene", () => $"OncMission scene load error: {ex.Message}"); }

            try { host.ApplySeed(mission); } catch (Exception ex) { CoopLog.Warn("onc.mission.seed", () => $"OncMission seed apply error: {ex.Message}"); }

            runtime.Start();
            CoopLog.Info("onc.mission.start", () => $"OncMission started: '{mission.Id}' nodes={mission.Nodes?.Count ?? 0}");
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
        if (_current == null) return;
        try
        {
            if (_current.IsRunning) _current.Cancel();
        }
        catch (Exception ex) { CoopLog.Warn("onc.mission.stop", () => $"OncMission Stop error: {ex.Message}"); }
        _current = null;
    }

    // ---------------- 驱动 / 事件 ----------------

    public static void Update(float dt)
    {
        if (_current == null || !_current.IsRunning) return;
        try { _current.Update(dt); }
        catch (Exception ex) { CoopLog.Warn("onc.mission.update", () => $"OncMission Update error: {ex.Message}"); }
    }

    public static void Raise(string eventId, object payload = null)
    {
        if (_current == null) return;
        _current.Raise(eventId, payload);
    }

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
            => CoopLog.Info("onc.host.start", () => $"OncMission host started '{runtime.Mission?.Id}'");

        public override void OnNodeEntered(OncMissionRuntime runtime, OncNode node)
            => CoopLog.Debug("onc.host.node", () => $"OncMission node '{node.Id}' kind={node.Kind}");

        public override void OnMissionCompleted(OncMissionRuntime runtime)
        {
            CoopLog.Info("onc.host.done", () => $"OncMission completed '{runtime.Mission?.Id}' (elapsed={runtime.Elapsed:0.0}s)");
            if (runtime.Mission != null) MarkMissionCompleted(runtime.Mission.Id); // 前置/后置解锁
            ShowNotification("任务完成", (runtime.Mission?.DisplayName ?? "") + " 已完成", 6f);
        }

        public override void OnMissionFailed(OncMissionRuntime runtime)
        {
            CoopLog.Info("onc.host.fail", () => $"OncMission failed '{runtime.Mission?.Id}'");
            ShowNotification("任务失败", (runtime.Mission?.DisplayName ?? "") + " 失败", 6f);
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

                // 1) 优先：场景 MapCard 匹配 → ActivateMission（游戏原生任务流程）
                var cards = UnityEngine.Resources.FindObjectsOfTypeAll<MapCard>();
                if (cards != null)
                {
                    for (int i = 0; i < cards.Length; i++)
                    {
                        var card = cards[i];
                        if (card == null || card.Mission == null) continue;
                        if (MatchMission(mission.SceneName, card.Mission))
                        {
                            CoopLog.Info("onc.host.scene", () => $"OncMission scene: MapCard hit '{mission.SceneName}'");
                            try { card.ActivateMission(); return true; }
                            catch (Exception ex) { CoopLog.Warn("onc.host.scene", () => $"OncMission ActivateMission error: {ex.Message}"); }
                        }
                    }
                }

                // 2) 回退：Unity 场景加载（把游戏已打包的任务场景当地图用）
                UnityEngine.SceneManagement.SceneManager.LoadScene(mission.SceneName,
                    UnityEngine.SceneManagement.LoadSceneMode.Single);
                CoopLog.Info("onc.host.scene", () => $"OncMission scene: SceneManager.LoadScene('{mission.SceneName}')");
                return true;
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
}
