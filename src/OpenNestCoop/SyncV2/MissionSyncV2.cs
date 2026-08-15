using System;
using LiteNetLib.Utils;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;
#if MELONLOADER
using SleepyNodes = Il2CppSleepyNodes;
#endif

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 任务同步（MissionSyncV2，MsgType=227）。M7：把 V1 <c>MissionSync</c>（102）迁入分层架构。
/// <see cref="V2Authority.Host"/>：同步 MissionManager 当前任务标识（scene）+ GamePhase + 随机 seed。
/// - 主机：进入任务时生成固定 seed 应用到本地 FireMission，变化/新成员/2s 保活 → 可靠直发广播。
/// - 客机：任务中加载匹配任务场景（MapCard 匹配/ActivateMission/StartOperation/LoadMission 回退）；
///   主菜单跟随主机 phase；seed → PendingSeed + FireMission useFixedSeed（任务内容随机一致）。
/// - 中途加入：OnLateJoin 主机单播 scene/phase/seed（替代 V1 StateSnapshotSync "mission"）。
/// </summary>
public sealed class MissionSyncV2 : ISyncedModule
{
    public static MissionSyncV2 Instance { get; } = new MissionSyncV2();

    private MissionSyncV2() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2Mission;

    private const float Interval = 0.5f;
    private float _timer;
    private bool _applying;
    private string _knownScene = "";
    private string _hScene = "";
    private byte _knownPhase, _hPhase;
    private int _knownSeed = -1, _hSeed = -1;
    private bool _known, _hknown;
    private int _lastRosterCount;
    private float _sceneKeepalive;
    private string _lastAppliedScene = "";
    private int _pendingSeed = -1;
    private static int _hostSeed = -1;

    /// <summary>最新收到的主机种子（供 Harmony patch FireMission.GenerateMission 生成前应用）。</summary>
    internal static int PendingSeed = -1;

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;

        var mgr = GetManager();
        string scene = GetMissionId(mgr);
        byte phase = GetPhaseByte(mgr);
        int seed = GetSeed(mgr);
        TryApplySeed();
        var net = _net;
        if (net == null) return;

        if (Store.IsHost)
        {
            if (phase == 2 && scene.Length > 0 && seed < 0)
            {
                seed = GenerateHostSeed();
                _hostSeed = seed;
                ApplySeedNow(seed);
                CoopRuntime.LogSource?.LogInfo($"[MissionSyncV2] host generated mission seed={seed}");
            }
            bool rosterChanged = _lastRosterCount != net.Roster.Count;
            _lastRosterCount = net.Roster.Count;
            _sceneKeepalive += dt;
            bool changed = !_hknown || scene != _hScene || phase != _hPhase || seed != _hSeed || rosterChanged;
            bool keepalive = _hknown && scene.Length > 0 && _sceneKeepalive >= 2f;
            if (_sceneKeepalive >= 2f) _sceneKeepalive = 0f;
            if (!changed && !keepalive) return;
            _hknown = true; _hScene = scene; _hPhase = phase; _hSeed = seed;
            CoopRuntime.LogSource?.LogInfo($"[MissionSyncV2] host broadcast scene='{scene}' phase={phase} seed={seed} changed={changed} keepalive={keepalive}");
            Broadcast(scene, phase, seed);
        }
        else if (!_applying)
        {
            if (_known && scene == _knownScene && phase == _knownPhase) return;
            _known = true; _knownScene = scene; _knownPhase = phase;
            SendToHost(scene, phase, seed);
        }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            string scene = r.GetString();
            byte phase = r.GetByte();
            int seed = r.GetInt();
            var m = GetManager();
            if (m == null) return;
            _pendingSeed = seed;
            PendingSeed = seed;
            if (seed > 0) _hostSeed = seed;
            TryApplySeed();
            _applying = true;
            try
            {
                if (Store.IsHost)
                {
                    m.CurrentMissionSceneName = scene;
                }
                else
                {
                    if (phase == 2)
                    {
                        if (string.IsNullOrEmpty(scene))
                        {
                            CoopRuntime.LogSource?.LogInfo("[MissionSyncV2] host mission id empty, skip load (waiting for resend)");
                        }
                        else if (scene != _lastAppliedScene)
                        {
                            if (m.CurrentMissionSceneName == scene || (m.CurrentMission != null && m.CurrentMission.MissionID == scene))
                            {
                                _lastAppliedScene = scene;
                            }
                            else
                            {
                                if (TryLoadMissionScene(scene, m)) _lastAppliedScene = scene;
                            }
                        }
                    }
                    else
                    {
                        try { m.SetPhase((MissionManager.GamePhase)phase); } catch { }
                    }
                }
            }
            finally { _applying = false; }
            _known = true; _knownScene = scene; _knownPhase = phase; _knownSeed = seed;
            _hknown = true; _hScene = scene; _hPhase = phase; _hSeed = seed;
            if (Store.IsHost) _net?.EnqueueBatch(data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MissionSyncV2] OnPacket: {ex.Message}"); }
    }

    public void OnLateJoin(ulong steamId)
    {
        if (Store.IsHost && steamId != 0)
        {
            var m = GetManager();
            if (m == null) return;
            string scene = GetMissionId(m);
            byte phase = GetPhaseByte(m);
            int seed = GetSeed(m);
            if (phase == 2 && scene.Length > 0 && seed < 0)
            {
                seed = GenerateHostSeed();
                ApplySeedNow(seed);
            }
            var net = _net;
            if (net == null) return;
            try
            {
                var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2Mission);
                w.Put(scene ?? "");
                w.Put(phase);
                w.Put(seed);
                net.Transport.Send(steamId, NetProtocol.Snapshot(w), true);
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MissionSyncV2] OnLateJoin: {ex.Message}"); }
        }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset()
    {
        _known = false; _hknown = false; _applying = false;
        _knownScene = ""; _hScene = ""; _knownPhase = 0; _hPhase = 0;
        _knownSeed = -1; _hSeed = -1; _lastAppliedScene = "";
    }

    // ---------------- 内部 ----------------

    private void TryApplySeed()
    {
        if (_pendingSeed < 0) return;
        if (ApplySeedNow(_pendingSeed)) _pendingSeed = -1;
    }

    private static bool TryLoadMissionScene(string scene, MissionManager m)
    {
        try
        {
            var cards = UnityEngine.Resources.FindObjectsOfTypeAll<MapCard>();
            int cardCount = cards?.Length ?? 0;
            if (cards != null)
            {
                foreach (var card in cards)
                {
                    if (card == null || card.Mission == null) continue;
                    if (MatchMission(scene, card.Mission))
                    {
                        CoopRuntime.LogSource?.LogInfo($"[MissionSyncV2] hit mission card '{scene}', try auto-activate (cards={cardCount})");
                        try { card.ActivateMission(); return true; }
                        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MissionSyncV2] ActivateMission: {ex.Message}"); }
                        if (card.Campaign != null)
                        {
                            try { m.StartOperation(card.Campaign, card.Mission); return true; }
                            catch (Exception ex2) { CoopRuntime.LogSource?.LogWarning($"[MissionSyncV2] StartOperation: {ex2.Message}"); }
                        }
                        try { m.LoadMission(card.Mission, false); return true; }
                        catch (Exception ex3) { CoopRuntime.LogSource?.LogWarning($"[MissionSyncV2] LoadMission: {ex3.Message}"); }
                    }
                }
            }
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
                        m.LoadMission(graph, false);
                        return true;
                    }
                }
            }
            CoopRuntime.LogSource?.LogInfo($"[MissionSyncV2] no loadable mission '{scene}' (cards={cardCount}), entering mission-select and retrying");
            try { m.LoadMainMenu(); } catch { }
            try { m.EnterBrowsingMap(); } catch { }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MissionSyncV2] TryLoadMissionScene: {ex.Message}"); }
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

    private static MissionManager GetManager()
    {
        try { return MissionManager.Instance; }
        catch { return null; }
    }

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

    private static int GenerateHostSeed()
    {
        int s = System.Environment.TickCount & 0x7FFFFFFF;
        if (s == 0) s = 1234567;
        return s;
    }

    private static int GetSeed(MissionManager m)
    {
        if (_hostSeed > 0) return _hostSeed;
        try { var fm = FireMission.Instance; if (fm != null) { int s = (int)fm.seed; if (s != 0) return s; } } catch { }
        try { var fm = FireMission.Instance; if (fm != null) { int fs = (int)fm.fixedSeed; if (fs != 0) return fs; } } catch { }
        return -1;
    }

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
                CoopRuntime.LogSource?.LogInfo($"[MissionSyncV2] apply seed={seed} → FireMission");
                return true;
            }
            return false;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MissionSyncV2] ApplySeedNow: {ex.Message}"); return false; }
    }

    /// <summary>供 Harmony patch FireMission.GenerateMission 生成前调用：把 PendingSeed 应用到该实例。</summary>
    internal static void ApplyPendingSeedTo(FireMission fm)
    {
        if (fm == null) return;
        int seed = PendingSeed;
        if (seed < 0) return;
        try
        {
            fm.useFixedSeed = true;
            fm.fixedSeed = seed;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MissionSyncV2] ApplyPendingSeedTo: {ex.Message}"); }
    }

    private void Broadcast(string scene, byte phase, int seed)
    {
        var net = _net;
        if (net == null) return;
        try
        {
            var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2Mission);
            w.Put(scene ?? "");
            w.Put(phase);
            w.Put(seed);
            var data = NetProtocol.Snapshot(w);
            for (int i = 0; i < net.Roster.Count; i++)
            {
                var p = net.Roster[i];
                if (p != null && !p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MissionSyncV2] Broadcast: {ex.Message}"); }
    }

    private void SendToHost(string scene, byte phase, int seed)
    {
        var net = _net;
        if (net == null) return;
        var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2Mission);
        w.Put(scene ?? "");
        w.Put(phase);
        w.Put(seed);
        net.EnqueueBatch(NetProtocol.Snapshot(w), false);
    }
}
