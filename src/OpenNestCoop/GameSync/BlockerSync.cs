using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;

namespace OpenNestCoop.GameSync;

/// <summary>
/// Handle Blocker（装填盖阻挡器）active 状态同步（MsgType=147）。
/// 根因（2026-09-05 用户报告）：Handle Blocker（Turret/Elevation Console/.Elevation Lever Baseplate/
/// .Loading Cover Left/Handle Blocker，组件 Transform/MeshFilter/MeshRenderer/BoxCollider/Interactable）
/// 是装填盖的阻挡器——装填盖打开时游戏 SetActive(false) 让它消失（解锁装填）；客机端没同步 →
/// Handle Blocker 还在 → 客机左炮一直被锁。主机权威轮询 activeSelf，变化广播，客机应用 SetActive。
/// </summary>
public sealed class BlockerSync : ISyncedModule
{
    /// <summary>自注册通道键（稳定字符串，双端一致；前导字节由 NetManager 注册管理器动态分配，
    /// 不再硬编码 MsgType——避免"新模块忘加枚举常量 → 动态分配撞车"的重复错误）。</summary>
    public const string ChannelKey = "blocker";

    /// <summary>本模块前导字节：由注册管理器为 ChannelKey 分配（动态注册）。</summary>
    public int MsgType => CoopRuntime.Net?.ChannelType(ChannelKey) ?? 0;

    // ⚠️ 模块自注册：程序集加载时入队（V1 方案，动态通道），Startup FlushPending 统一注册
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SelfRegister() => CoopSyncRegistry.PendingRegister(false, () => new BlockerSync(), ChannelKey);

    private const float Interval = 0.5f;
    // ⚠️ 2026-09-12：单次变化事件丢了（客机当时通道未就绪/对象未找到/被本地逻辑覆盖）→ 永久不同步。
    // 心跳补发：自上一条广播超过 HeartbeatSec 且当前无变化时，把全部当前状态幂等重发一遍。
    private const float HeartbeatSec = 3f;
    private float _lastBroadcast;
    private float _timer;
    private Transform[] _cache;
    private float _cacheTimer;
    private int _cacheScene = -1;
    private const float CacheRefreshSec = 3f;
    /// <summary>Handle Blocker id（父链路径）→ 上次广播的 activeSelf（变化检测）。</summary>
    private readonly Dictionary<string, bool> _lastActive = new();
    private bool _applying;
    private int _log;

    /// <summary>查找场景中所有 Handle Blocker（名字精确匹配，含 inactive——FindObjectsOfType(Transform, true)）。</summary>
    private Transform[] GetBlockers(float dt)
    {
        int sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
        _cacheTimer -= dt;
        if (_cache == null || sc != _cacheScene || _cacheTimer <= 0f)
        {
            _cacheTimer = CacheRefreshSec;
            _cacheScene = sc;
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
                var list = new List<Transform>();
                if (all != null)
                    foreach (var t in all)
                        if (t != null && t.name == "Handle Blocker")
                            list.Add(t);
                _cache = list.ToArray();
            }
            catch { _cache = new Transform[0]; }
        }
        return _cache;
    }

    /// <summary>稳定 id：父链路径（限深度 8）。左右装填盖下各一个 Handle Blocker，路径区分。</summary>
    private static string IdOf(Transform t)
    {
        string path = t != null && t.name != null ? t.name : "";
        try
        {
            var p = t != null ? t.parent : null;
            int dep = 0;
            while (p != null && dep < 8)
            {
                path = (p.name ?? "") + "/" + path;
                p = p.parent;
                dep++;
            }
        }
        catch { }
        return path;
    }

    private static Transform Find(Transform[] arr, string id)
    {
        if (arr == null) return null;
        foreach (var t in arr)
            if (t != null && IdOf(t) == id) return t;
        return null;
    }

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        if (_applying) return;
        if (!net.IsHost) return; // 主机权威：客机只接收
        try
        {
            var blockers = GetBlockers(dt);
            if (blockers == null || blockers.Length == 0) return;
            List<string> changed = null;
            foreach (var b in blockers)
            {
                if (b == null) continue;
                string id = IdOf(b);
                bool act = false;
                try { act = b.gameObject.activeSelf; } catch { }
                if (_lastActive.TryGetValue(id, out var last) && last == act) continue;
                _lastActive[id] = act;
                (changed ??= new List<string>()).Add(id);
            }
            if (changed == null || changed.Count == 0)
            {
                if (Time.time - _lastBroadcast < HeartbeatSec) return;
                changed = new List<string>();
                foreach (var b in blockers) if (b != null) changed.Add(IdOf(b));
                if (changed.Count == 0) return;
                _log = 0; // 心跳也打一行，便于日志确认
            }
            _lastBroadcast = Time.time;
            var w = NetProtocol.Begin((MsgType)MsgType);
            w.Put((byte)changed.Count);
            foreach (var id in changed)
            {
                bool act = false;
                var b = Find(blockers, id);
                if (b != null) try { act = b.gameObject.activeSelf; } catch { }
                w.Put(id);
                w.Put(act ? (byte)1 : (byte)0);
            }
            var data = NetProtocol.Snapshot(w);
            net.EnqueueBatch(data, true, true);
            if ((++_log % 10) == 1)
                CoopLog.Info("blocker.host", () => $"[BlockerSync] host broadcast n={changed.Count}", 1f);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"BlockerSync Tick: {ex.Message}"); }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        if (net.IsHost)
        {
            // 主机转发给其他客户端（星型拓扑）
            foreach (var p in net.Roster)
                if (!p.IsLocal && (ulong)p.SteamId != from)
                    net.Transport.Send(p.SteamId, data, true);
            return;
        }
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int n = r.GetByte();
            var blockers = GetBlockers(0f);
            _applying = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    string id = r.GetString();
                    bool act = r.GetByte() != 0;
                    var b = Find(blockers, id);
                    if (b == null) continue;
                    try { if (b.gameObject.activeSelf != act) b.gameObject.SetActive(act); } catch { }
                }
            }
            finally { _applying = false; }
            CoopLog.Info("blocker.recv", () => $"[BlockerSync] recv n={n}", 1f);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"BlockerSync OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }

    public void OnSessionEnded() { Reset(); }

    public void Reset()
    {
        _lastActive.Clear();
        _cache = null;
        _applying = false;
    }
}
