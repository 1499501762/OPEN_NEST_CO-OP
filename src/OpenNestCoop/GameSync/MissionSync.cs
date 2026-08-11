using System;
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
    private int _knownSeed = -1;
    private int _hSeed = -1;
    private bool _known;
    private bool _hknown;
    private int _lastRosterCount;
    private float _sceneKeepalive;
    private string _lastAppliedScene = "";
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
                CoopRuntime.LogSource?.LogInfo($"[MissionSync] host 生成任务种子 seed={seed}");
            }
            // 任务开始是事件：scene/phase/seed 变化、新成员加入、或保活定时 → 可靠重发
            bool rosterChanged = _lastRosterCount != net.Roster.Count;
            _lastRosterCount = net.Roster.Count;
            _sceneKeepalive += dt;
            bool changed = !_hknown || scene != _hScene || phase != _hPhase || seed != _hSeed || rosterChanged;
            bool keepalive = _hknown && scene.Length > 0 && _sceneKeepalive >= 2f;
            if (_sceneKeepalive >= 2f) _sceneKeepalive = 0f;
            if (!changed && !keepalive) return;
            _hknown = true; _hScene = scene; _hPhase = phase; _hSeed = seed;
            CoopRuntime.LogSource?.LogInfo($"[MissionSync] host broadcast scene='{scene}' phase={phase} seed={seed} changed={changed} keepalive={keepalive} roster={net.Roster.Count}");
            Broadcast(net, scene, phase, seed);
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
            var m = GetManager();
            if (m == null) return;
            var op = m.CurrentOperation;
            CoopRuntime.LogSource?.LogInfo($"[MissionSync] recv scene='{scene}' phase={phase} seed={seed} isHost={net.IsHost} op={(op == null ? "null" : "ok")} curScene={m.CurrentMissionSceneName} lastApplied={_lastAppliedScene}");
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
                            CoopRuntime.LogSource?.LogInfo("[MissionSync] 主机任务标识为空，跳过加载（等待重发）");
                        }
                        else if (scene != _lastAppliedScene)
                        {
                            if (m.CurrentMissionSceneName == scene || (m.CurrentMission != null && m.CurrentMission.MissionID == scene))
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
            _known = true; _knownScene = scene; _knownPhase = phase; _knownSeed = seed;
            _hknown = true; _hScene = scene; _hPhase = phase; _hSeed = seed;
            if (net.IsHost)
                net.EnqueueBatch(data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MissionSync OnPacket: {ex.Message}"); }
    }

    /// <summary>尝试把待应用种子写到 FireMission；成功后清除待应用标记。</summary>
    private void TryApplySeed()
    {
        if (_pendingSeed < 0) return;
        if (ApplySeedNow(_pendingSeed))
            _pendingSeed = -1;
    }

    /// <summary>按任务场景名找 MissionGraph 并加载（客机跟随主机开始任务）。成功返回 true。</summary>
    private static bool TryLoadMissionScene(string scene, MissionManager m)
    {
        try
        {
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
                        CoopRuntime.LogSource?.LogInfo($"MissionSync: 命中任务卡片 '{scene}'，尝试自动激活 (cards={cardCount})");
                        // 1) 模拟点击卡片：走游戏原生流程（解锁检查 + StartOperation + 场景加载）
                        try { card.ActivateMission(); return true; }
                        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MissionSync ActivateMission: {ex.Message}"); }
                        // 2) ActivateMission 失败 → 直接 StartOperation（同一次点击的等效调用）
                        if (card.Campaign != null)
                        {
                            try { m.StartOperation(card.Campaign, card.Mission); return true; }
                            catch (Exception ex2) { CoopRuntime.LogSource?.LogWarning($"MissionSync StartOperation: {ex2.Message}"); }
                        }
                        // 3) 最后回退：仅加载任务场景
                        try { m.LoadMission(card.Mission, false); return true; }
                        catch (Exception ex3) { CoopRuntime.LogSource?.LogWarning($"MissionSync LoadMission: {ex3.Message}"); }
                    }
                }
            }
            else
            {
                CoopRuntime.LogSource?.LogInfo("MissionSync: 场景中无 MapCard（可能还在主菜单）");
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
                        CoopRuntime.LogSource?.LogInfo($"MissionSync: 加载任务 '{scene}' (op:{graph.MissionID})");
                        m.LoadMission(graph, false);
                        return true;
                    }
                }
            }

            // 都不行 → 初始化选任务界面（让 MapCard 出现），等待主机保活重发再试
            CoopRuntime.LogSource?.LogInfo($"MissionSync: 未找到可加载任务 '{scene}' (cards={cardCount})，进入选任务界面等待重试");
            try { m.LoadMainMenu(); } catch { }
            try { m.EnterBrowsingMap(); } catch { }
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
        _knownSeed = -1; _hSeed = -1;
        _lastAppliedScene = "";
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
            var n = m.CurrentMissionSceneName;
            if (!string.IsNullOrEmpty(n)) return n;
            var cm = m.CurrentMission;
            if (cm != null && !string.IsNullOrEmpty(cm.MissionID)) return cm.MissionID;
        }
        catch { }
        return "";
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
            fm.useFixedSeed = true;
            fm.fixedSeed = seed;
            CoopRuntime.LogSource?.LogInfo($"[MissionSync] GenerateMission 前应用 seed={seed} → FireMission");
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"MissionSync ApplyPendingSeedTo: {ex.Message}");
        }
    }

    /// <summary>主机广播任务状态给所有远端（任务开始事件，可靠直发）。</summary>
    private void Broadcast(NetManager net, string scene, byte phase, int seed)
    {
        try
        {
            var w = NetProtocol.Begin((MsgType)MsgType);
            w.Put(scene ?? "");
            w.Put(phase);
            w.Put(seed);
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
        net.EnqueueBatch(NetProtocol.Snapshot(w), false);
    }
}
