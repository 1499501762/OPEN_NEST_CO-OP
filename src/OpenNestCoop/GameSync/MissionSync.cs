using System;
using Il2CppInterop.Runtime;
using OpenNestCoop.Net;
using LiteNetLib.Utils;

using OpenNestCoop.Core;
#if MELONLOADER
using SleepyNodes = Il2CppSleepyNodes;
#endif
namespace OpenNestCoop.GameSync;

/// <summary>
/// 任务同步（最小版）：同步 MissionManager.CurrentMissionSceneName（当前任务标识，string）。
/// 主机权威：客户端本地变化上行 → 主机应用 → 广播；防环。
/// 说明：MissionManager.MissionState 在 interop 中非标准枚举（无法 cast），
/// 故用任务场景名作为标识（跨端唯一、可直接字符串比较）。
/// 任务开始/结束事件、实体位置、计时器精确同步后续再补。
/// </summary>
public sealed class MissionSync : ISyncedModule
{
    public byte MsgType => 102;

    private const float Interval = 0.5f;
    private float _timer;
    private bool _applying;
    private string _knownScene = "";
    private string _hScene = "";
    private byte _knownPhase;
    private byte _hPhase;
    private int _knownSeed = -1; // ⚠️ V1 死代码：只写不读（全仓无读取点），见 158/295 写点已注释
    private int _hSeed = -1;
    private bool _known;
    private bool _hknown;
    private int _lastRosterCount;
    private float _sceneKeepalive;
    private string _lastAppliedScene = "";
    private string _hNodeId = ""; // 主机任务图当前节点（同步序号，诊断/进度显示用）
    private int _pendingSeed = -1;   // 待应用到 FireMission 的种子（场景加载后重试直到生效）
    /// <summary>主机记住的已生成种子（GetSeed 读不到 FireMission.seed 时的稳定回退——
    /// 避免主机每 0.5s 重新生成新 seed 导致广播变化、客机 fixedSeed 被反复覆盖 → 任务目标不同步）。
    /// 静态：BuildMissionSnapshot（静态）与 Tick/OnPacket（实例）共用。</summary>
    private static int _hostSeed = -1;

    /// <summary>最新收到的主机种子（供 Harmony patch FireMission.GenerateMission 在生成前应用）。</summary>
    internal static int PendingSeed = -1;

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;

        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;

        var mgr = GetManager();
        string scene = GetMissionId(mgr);
        byte phase = GetPhaseByte(mgr);
        int seed = GetSeed(mgr); // 任务随机种子（任务内容随机一致的关键）
        string nodeId = GetProgressNode(mgr); // 任务图当前节点（同步序号）
        // 客机：任务场景加载后持续尝试把待应用种子写到 FireMission（直到生效）
        TryApplySeed();
        if (net.IsHost)
        {
            // 主机任务种子：进入任务（phase==2 且 scene 有效）时若还没有 seed，
            // 生成一个固定 seed 并应用到本地 FireMission（useFixedSeed=true），随后广播给客机。
            // 记住到 _hostSeed：FireMission.seed 读不到时保持稳定（不再每 0.5s 重新生成）。
            if (phase == 2 && scene.Length > 0 && seed < 0)
            {
                seed = GenerateHostSeed();
                _hostSeed = seed;
                ApplySeedNow(seed);
                CoopRuntime.LogSource?.LogInfo($"[MissionSync] host generated mission seed={seed}");
            }
            // 任务开始是事件：scene/phase/seed 变化、新成员加入、或保活定时 → 可靠重发
            bool rosterChanged = _lastRosterCount != net.Roster.Count;
            _lastRosterCount = net.Roster.Count;
            _sceneKeepalive += dt;
            bool changed = !_hknown || scene != _hScene || phase != _hPhase || seed != _hSeed || nodeId != _hNodeId || rosterChanged;
            bool keepalive = _hknown && scene.Length > 0 && _sceneKeepalive >= 2f;
            if (_sceneKeepalive >= 2f) _sceneKeepalive = 0f;
            if (!changed && !keepalive) return;
            _hknown = true; _hScene = scene; _hPhase = phase; _hSeed = seed; _hNodeId = nodeId;
            CoopRuntime.LogSource?.LogInfo($"[MissionSync] host broadcast scene='{scene}' phase={phase} seed={seed} node='{nodeId}' changed={changed} keepalive={keepalive} roster={net.Roster.Count}");
            Broadcast(net, scene, phase, seed, nodeId);
        }
        else if (!_applying)
        {
            if (_known && scene == _knownScene && phase == _knownPhase) return;
            _known = true; _knownScene = scene; _knownPhase = phase;
            SendToHost(net, scene, phase, seed);
        }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            string scene = r.GetString();
            byte phase = r.GetByte();
            int seed = r.GetInt();
            string nodeId = r.GetString(); // 主机任务图当前节点（同步序号）
            var m = GetManager();
            if (m == null) return;
            var op = m.CurrentOperation;
            CoopRuntime.LogSource?.LogInfo($"[MissionSync] recv scene='{scene}' phase={phase} seed={seed} node='{nodeId}' isHost={net.IsHost} op={(op == null ? "null" : "ok")} curScene={m.CurrentMissionSceneName} lastApplied={_lastAppliedScene}");
            // ⚠️ 任务内容随机一致性：记录主机种子，并在任务场景加载后应用到 FireMission
            // （两端用同一随机种子生成任务内容 → 随机一致）。FireMission 可能未就绪，存 _pendingSeed 持续重试；
            // 同时更新 PendingSeed 供 Harmony patch FireMission.GenerateMission 在生成前应用。
            _pendingSeed = seed;
            PendingSeed = seed;
            if (seed > 0) _hostSeed = seed; // 记住主机种子（GetSeed 读不到时的稳定回退）
            TryApplySeed();
            _applying = true;
            try
            {
                if (net.IsHost)
                {
                    // ⚠️ 自定义任务（@c: 前缀）：主机以 CurrentMission 真实图为准（GetMissionId 已从图读），
                    // 不覆盖 CurrentMissionSceneName——避免缓存污染（自定义任务 scene 名可能与原生任务同名）。
                    if (!IsCustomTag(scene))
                        m.CurrentMissionSceneName = scene;
                }
                else
                {
                    if (phase == 2)
                    {
                        // 主机在任务中（MissionActive）→ 客机加载匹配的任务场景
                        // 注意：不要预先设置 CurrentMissionSceneName，否则 LoadMission 内部
                        // UnloadCurrentMissionSceneIfAny 会用无效场景名卸载 → Scene to unload is invalid
                        if (string.IsNullOrEmpty(scene))
                        {
                            // 主机还没拿到任务标识（CurrentMissionSceneName/MissionID 均空）：
                            // 只同步 phase 不加载，避免用空名进选任务界面反复弹窗；等主机重发有效 ID
                            CoopRuntime.LogSource?.LogInfo("[MissionSync] host mission id empty, skip load (waiting for resend)");
                        }
                        else if (scene != _lastAppliedScene)
                        {
                            string raw = UnwrapMissionTag(scene); // 去 @c:/@n: 前缀
                            if (m.CurrentMissionSceneName == raw || (m.CurrentMission != null && m.CurrentMission.MissionID == raw))
                            {
                                // 客机本地已在目标任务（玩家已手动开始）→ 直接标记已应用
                                _lastAppliedScene = scene;
                            }
                            else
                            {
                                if (TryLoadMissionScene(scene, m))
                                    _lastAppliedScene = scene;
                                // 失败则保持 _lastAppliedScene 不变，等主机 2s 保活重发再试
                            }
                        }
                    }
                    else
                    {
                        // 主菜单/选任务界面：跟随主机 GamePhase（标准枚举可 cast）
                        try { m.SetPhase((MissionManager.GamePhase)phase); } catch { }
                    }
                }
            }
            finally { _applying = false; }
            _known = true; _knownScene = scene; _knownPhase = phase; /* _knownSeed = seed; */ // V1 死代码
            _hknown = true; _hScene = scene; _hPhase = phase; _hSeed = seed; _hNodeId = nodeId;
            if (net.IsHost)
                net.EnqueueBatch(data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MissionSync OnPacket: {ex.Message}\n{ex.StackTrace}"); }
    }

    /// <summary>尝试把待应用种子写到 FireMission；成功后清除待应用标记。</summary>
    private void TryApplySeed()
    {
        if (_pendingSeed < 0) return;
        if (ApplySeedNow(_pendingSeed))
            _pendingSeed = -1;
    }

    /// <summary>按任务标识找 MissionGraph 并加载（客机跟随主机开始任务）。成功返回 true。
    /// ⚠️ 2026-08-25 修复：不再直接调 m.LoadMission（MLL interop 该重载签名不匹配 → Method not found），
    /// 改用 card.ActivateMission()/StartOperation（原生完整链路）；不再主动 LoadMainMenu/EnterBrowsingMap
    /// （避免把客机拉回选任务界面——失败保持现状等主机 2s 保活重发）。</summary>
    private static bool TryLoadMissionScene(string scene, MissionManager m)
    {
        try
        {
            CoopRuntime.LogSource?.LogInfo($"[MissionSync] TryLoad('{scene}') phase=2");
            // ⚠️ 自定义任务（@c:<MissionID> 前缀）：走 OncMissionBridge.StartNative（ImportMission 完整链路），
            // 不再遍历 MapCard——自定义任务卡片不在场景，且 scene 名可能与原生任务同名（会命中原生卡片进错任务）。
            if (IsCustomTag(scene))
            {
                string id = scene.Substring(3);
                if (OncMissionBridge.GetNativeJson(id) != null)
                {
                    CoopRuntime.LogSource?.LogInfo($"[MissionSync] custom mission '{id}' → OncMissionBridge.StartNative");
                    return OncMissionBridge.StartNative(id);
                }
                CoopRuntime.LogSource?.LogWarning($"[MissionSync] custom mission '{id}' json not found on this client (skip)");
                return false;
            }
            // 优先：场景中的 MapCard（最接近“玩家点击任务卡片”的正常流程）。
            // MapCard.Campaign / .Mission 是场景序列化引用，正是点卡片时传给
            // StartOperation 的同一实例。客机在没点过卡片前 CurrentOperation 恒为 null，
            // 所以不能依赖 CurrentOperation.Missions。
            var cards = UnityEngine.Resources.FindObjectsOfTypeAll<MapCard>();
            int cardCount = cards?.Length ?? 0;
            if (cards != null)
            {
                foreach (var card in cards)
                {
                    if (card == null || card.Mission == null) continue;
                    if (MatchMission(scene, card.Mission))
                    {
                        CoopRuntime.LogSource?.LogInfo($"MissionSync: hit mission card '{scene}', try auto-activate (cards={cardCount})");
                        // 1) 模拟点击卡片：走游戏原生流程（解锁检查 + StartOperation + 场景加载）
                        try { card.ActivateMission(); return true; }
                        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MissionSync ActivateMission: {ex.Message}"); }
                        // 2) ActivateMission 失败 → 直接 StartOperation（同一次点击的等效调用）
                        if (card.Campaign != null)
                        {
                            try { m.StartOperation(card.Campaign, card.Mission); return true; }
                            catch (Exception ex2) { CoopRuntime.LogSource?.LogWarning($"MissionSync StartOperation: {ex2.Message}"); }
                        }
                        // ⚠️ 不再调 LoadMission（MLL interop 签名不匹配）：记录并继续，等主机保活重发
                        CoopRuntime.LogSource?.LogWarning($"MissionSync: matched card '{scene}' but Activate/StartOperation failed, wait for host resend");
                    }
                }
            }
            else
            {
                CoopRuntime.LogSource?.LogInfo("MissionSync: no MapCard in scene (may still be in main menu)");
            }

            // 回退：CurrentOperation.Missions（主机正常流程 / 客机已点过一次卡片时已初始化）
            var op = m.CurrentOperation;
            if (op != null && op.Missions != null)
            {
                foreach (var node in op.Missions)
                {
                    if (node == null) continue;
                    var graph = node.Mission;
                    if (graph == null) continue;
                    if (MatchMission(scene, graph))
                    {
                        CoopRuntime.LogSource?.LogInfo($"MissionSync: loading mission '{scene}' (op:{graph.MissionID})");
                        // ⚠️ 用 StartOperation 代替 LoadMission（MLL interop LoadMission 签名不匹配）
                        try { m.StartOperation(op, graph); return true; }
                        catch (Exception ex4) { CoopRuntime.LogSource?.LogWarning($"MissionSync StartOperation(op): {ex4.Message}"); }
                    }
                }
            }

            // ⚠️ 不主动进选任务界面（避免把客机拉回选任务 Card）——保持现状，等主机 2s 保活重发再试
            CoopRuntime.LogSource?.LogInfo($"MissionSync: no loadable mission '{scene}' (cards={cardCount}), waiting for host resend");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MissionSync TryLoadMissionScene: {ex.Message}"); }
        return false;
    }

    private static bool MatchMission(string scene, SleepyNodes.MissionGraph graph)
    {
        try
        {
            var sr = graph.SceneReference;
            if (sr != null && sr.sceneName == scene) return true;
            if (graph.MissionID == scene) return true;
        }
        catch { }
        return false;
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }

    /// <summary>中途加入：主机构建当前任务状态快照（scene/phase/seed），供 StateSnapshotSync 打包。</summary>
    public static byte[] BuildMissionSnapshot()
    {
        try
        {
            var m = GetManager();
            if (m == null) return null;
            string scene = GetMissionId(m);
            byte phase = GetPhaseByte(m);
            int seed = GetSeed(m);
            if (phase == 2 && scene.Length > 0 && seed < 0)
            {
                seed = GenerateHostSeed();
                ApplySeedNow(seed);
            }
            var w = NetProtocol.Begin((MsgType)102);
            w.Put(scene ?? "");
            w.Put(phase);
            w.Put(seed);
            w.Put(GetProgressNode(m) ?? ""); // 任务图当前节点（同步序号）
            return NetProtocol.Snapshot(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MissionSync BuildMissionSnapshot: {ex.Message}"); }
        return null;
    }

    /// <summary>中途加入：新成员应用任务状态快照（走正常 OnPacket 加载流程进任务）。</summary>
    public static void ApplyMissionSnapshot(byte[] data)
    {
        try
        {
            var inst = CoopSyncRegistry.FindModule<MissionSync>();
            if (inst != null) inst.OnPacket(0, data);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MissionSync ApplyMissionSnapshot: {ex.Message}"); }
    }

    public void Reset()
    {
        _known = false; _hknown = false; _applying = false;
        _knownScene = ""; _hScene = ""; _knownPhase = 0; _hPhase = 0;
        /* _knownSeed = -1; */ _hSeed = -1; // V1 死代码（_knownSeed 只写不读）
        _hNodeId = ""; _lastAppliedScene = "";
    }

    private static MissionManager GetManager()
    {
        try { return MissionManager.Instance; }
        catch { return null; }
    }

    /// <summary>
    /// 任务标识（跨端唯一、可用于匹配任务卡片）：
    /// 优先 CurrentMissionSceneName（旧版返回 "Mission tutorial 2" 之类），
    /// 为空时回退 CurrentMission.MissionID（游戏更新后 CurrentMissionSceneName 可能不再返回任务名，
    /// 但激活任务时 CurrentMission 可用）。两者都不足则返回空（主机保活重发）。
    /// </summary>
    private static string GetMissionId(MissionManager m)
    {
        try
        {
            if (m == null) return "";
            // ⚠️ 自定义任务（CSM，IsNativeCustomGraph）：广播自定义 MissionID（@c: 前缀）而非 scene 名——
            // 自定义任务 scene 名（如 "Mission tutorial 4"）可能与原生任务同名，客机按 scene 名会命中原生卡片进错任务。
            var cm = m.CurrentMission;
            if (cm != null && OncMissionBridge.IsNativeCustomGraph(cm))
            {
                string mid = "";
                try { mid = cm.MissionID; } catch { }
                if (!string.IsNullOrEmpty(mid)) return "@c:" + mid;
            }
            var n = m.CurrentMissionSceneName;
            // ⚠️ MissionBase 是无区分度默认场景名（多数原生任务 SceneReference 未列动态场景 → 客机无法
            // 用它匹配任务卡片）→ 回退 CurrentMission.MissionID（原生任务 ID，客机可按 MissionID 匹配）。
            if (!string.IsNullOrEmpty(n) && !n.Equals("MissionBase", StringComparison.Ordinal)) return n;
            if (cm != null && !string.IsNullOrEmpty(cm.MissionID)) return cm.MissionID;
            return n ?? "";
        }
        catch { }
        return "";
    }

    /// <summary>任务标识是否带 @c: 前缀（自定义任务 MissionID）。</summary>
    private static bool IsCustomTag(string tagged)
        => tagged != null && tagged.StartsWith("@c:", StringComparison.Ordinal);

    /// <summary>解任务标识前缀（@c: 自定义 MissionID / @n: 原生 scene；无前缀按原生 scene 原样返回）。</summary>
    private static string UnwrapMissionTag(string tagged)
    {
        if (string.IsNullOrEmpty(tagged)) return tagged;
        if (tagged.StartsWith("@c:", StringComparison.Ordinal) || tagged.StartsWith("@n:", StringComparison.Ordinal))
            return tagged.Substring(3);
        return tagged;
    }

    /// <summary>任务图当前主执行线节点（同步序号）：读 MissionManager.CurrentMission.CurrentState.Node.NodeID。
    /// 原生图未运行/无状态返回 ""。供主机广播任务进度（客机诊断/显示）。</summary>
    private static string GetProgressNode(MissionManager m)
    {
        try
        {
            if (m == null || m.CurrentMission == null) return "";
            var cs = m.CurrentMission.CurrentState;
            if (cs == null) return "";
            var n = cs.Node;
            if (n == null) return "";
            try { return n.TryCast<SleepyNodes.StateNode>()?.NodeID ?? ""; } catch { return ""; }
        }
        catch { return ""; }
    }

    private static byte GetPhaseByte(MissionManager m)
    {
        try { return (byte)(int)m.CurrentPhase; }
        catch { return 0; }
    }

    /// <summary>生成主机任务种子（固定值，广播给客机后两端一致）。</summary>
    private static int GenerateHostSeed()
    {
        // 用时间+随机，避免两端巧合相同
        int s = System.Environment.TickCount & 0x7FFFFFFF;
        if (s == 0) s = 1234567;
        return s;
    }

    /// <summary>
    /// 读任务随机种子（任务地图实体生成器 FireMission.seed）。任务内容（目标位置/敌人）随机的真正源头。
    /// MissionManager 没有 seed 字段（旧实现读它恒返回 -1）。FireMission 才是 GenerateMission 的随机源。
    /// 不可访问返回 -1。
    /// </summary>
    private static int GetSeed(MissionManager m)
    {
        // 主机已生成过 seed → 稳定返回它（避免 FireMission.seed/fixedSeed 读不到或实例重建时
        // 返回 -1/变化值 → 主机每 0.5s 重新生成新 seed → 广播变化 → 任务目标不同步）
        if (_hostSeed > 0) return _hostSeed;
        try
        {
            var fm = FireMission.Instance;
            if (fm != null)
            {
                int s = (int)fm.seed;
                if (s != 0) return s;
            }
        }
        catch { }
        try
        {
            var fm = FireMission.Instance;
            if (fm != null)
            {
                int fs = (int)fm.fixedSeed;
                if (fs != 0) return fs;
            }
        }
        catch { }
        return -1;
    }

    /// <summary>
    /// 应用主机种子到任务随机源（FireMission）：useFixedSeed=true + fixedSeed=seed →
    /// 两端 GenerateMission 用同一随机序列，任务内容一致。返回是否成功（FireMission 就绪）。
    /// </summary>
    private static bool ApplySeedNow(int seed)
    {
        if (seed < 0) return false;
        try
        {
            var fm = FireMission.Instance;
            if (fm != null)
            {
                fm.useFixedSeed = true;
                fm.fixedSeed = seed;
                CoopRuntime.LogSource?.LogInfo($"[MissionSync] apply seed={seed} → FireMission (useFixedSeed=true, fixedSeed={seed})");
                return true;
            }
            return false; // FireMission 未就绪，等下次 Tick 重试
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"MissionSync ApplySeedNow: {ex.Message}");
            return false;
        }
    }

    /// <summary>供 Harmony patch FireMission.GenerateMission 在生成前调用：把 PendingSeed 应用到该实例。</summary>
    internal static void ApplyPendingSeedTo(FireMission fm)
    {
        if (fm == null) return;
        int seed = PendingSeed;
        if (seed < 0) return;
        try
        {
            // ⚠️ 2026-08-26 诊断：应用前 dump 当前 fixedSeed——确认是否已应用过（避免重复设置/被覆盖）。
            int before = -1; bool ufix = false;
            try { before = (int)fm.fixedSeed; } catch { }
            try { ufix = fm.useFixedSeed; } catch { }
            fm.useFixedSeed = true;
            fm.fixedSeed = seed;
            CoopRuntime.LogSource?.LogInfo($"[MissionSync] applying seed={seed} before GenerateMission -> FireMission (was fixedSeed={before} useFixedSeed={ufix})");
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"MissionSync ApplyPendingSeedTo: {ex.Message}");
        }
    }

    /// <summary>主机广播任务状态给所有远端（任务开始事件，可靠直发）。</summary>
    private void Broadcast(NetManager net, string scene, byte phase, int seed, string nodeId)
    {
        try
        {
            var w = NetProtocol.Begin((MsgType)MsgType);
            w.Put(scene ?? "");
            w.Put(phase);
            w.Put(seed);
            w.Put(nodeId ?? ""); // 任务图当前节点（同步序号）
            var data = NetProtocol.Snapshot(w);
            // 任务开始是事件，用可靠直发，保证客机收到
            foreach (var p in net.Roster)
                if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MissionSync Broadcast: {ex.Message}"); }
    }

    private void SendToHost(NetManager net, string scene, byte phase, int seed)
    {
        var w = NetProtocol.Begin((MsgType)MsgType);
        w.Put(scene ?? "");
        w.Put(phase);
        w.Put(seed);
        w.Put(GetProgressNode(GetManager()) ?? ""); // 客机上报当前图节点（主机诊断用）
        net.EnqueueBatch(NetProtocol.Snapshot(w), false);
    }
}
