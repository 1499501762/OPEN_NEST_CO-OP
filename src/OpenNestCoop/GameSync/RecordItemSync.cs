using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 唱片位置同步（RecordItem 世界坐标，MsgType=108）。
/// 唱片是可拾取物品（拿在手里/放桌上/放入唱机），两端物理/交互独立会分叉。
/// 主机权威：主机广播所有唱片位置；客机本地变化（拿起/放下）上行 → 主机应用后广播。
/// 按 FindObjectsOfType 顺序逐张发送（两端同场景实例顺序一致）。
/// </summary>
public sealed class RecordItemSync : ISyncedModule
{
    public byte MsgType => 108;
    private const float Interval = 0.2f;
    private float _timer;
    private bool _applying;
    private int _sendLog;
    private int _recvLog;
    // ⚠️ 实例缓存（2026-08-25 帧性能）：FindObjectsOfType 全场景扫描很贵（曾占 ~66ms/s）——低频刷新（3s + 场景切换）
    private RecordItem[] _cache;
    private float _cacheTimer;
    private int _cacheScene = -1;
    private const float CacheRefreshSec = 3f;
    // ⚠️ 2026-08-25 帧性能：RecordPlayerController 播放器数组缓存——IsInRecordSlot 原每张唱片每 0.2s 全场景
    // FindObjectsOfType<RecordPlayerController>()（frame.log 曾占 ~50ms/s）→ 低频刷新（3s + 场景切换）
    private static RecordPlayerController[] _playerCache;
    private static float _playerCacheTimer;
    private static int _playerCacheScene = -1;
    // 主机：每张唱片上次位置签名（变化检测广播，避免每帧全量覆盖客机）
    private readonly System.Collections.Generic.Dictionary<string, string> _hostSig = new();
    // 客机：拖拽状态跟踪（放下瞬间上行一次位置，主机权威）
    private readonly System.Collections.Generic.Dictionary<RecordItem, bool> _dragState = new();

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;

        try
        {
            EnsureCache(dt);
            var items = _cache;
            if (items == null || items.Length == 0) return;
            if (net.IsHost)
                HostSendChanges(net, items);
            else if (!_applying)
                ClientSendDropEvents(net, items);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"RecordItemSync Tick: {ex.Message}"); }
    }

    /// <summary>场景实例缓存刷新：FindObjectsOfType 每 CacheRefreshSec 或场景切换才扫一次（省全场景扫描）。</summary>
    private void EnsureCache(float dt)
    {
        int sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
        _cacheTimer -= dt;
        if (_cache == null || sc != _cacheScene || _cacheTimer <= 0f)
        {
            _cacheTimer = CacheRefreshSec;
            _cacheScene = sc;
            _cache = UnityEngine.Object.FindObjectsOfType<RecordItem>();
        }
    }

    /// <summary>主机：变化检测广播所有唱片位置（首次全量，之后只发变化的）。</summary>
    private void HostSendChanges(NetManager net, RecordItem[] items)
    {
        int n = Math.Min(items.Length, 16);
        var w = NetProtocol.Begin((MsgType)MsgType);
        w.Put((byte)n);
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            var it = items[i];
            bool inSlot = it != null && IsInRecordSlot(it);
            if (it == null) { w.Put(""); w.Put((byte)2); w.Put(0f); w.Put(0f); w.Put(0f); continue; }
            string nm = it.name ?? "";
            var p = it.transform.position;
            string sig = $"{(inSlot ? 1 : 0)}|{p.x:0.###}|{p.y:0.###}|{p.z:0.###}";
            if (_hostSig.TryGetValue(nm, out var last) && last == sig) { w.Put(nm); w.Put((byte)2); w.Put(0f); w.Put(0f); w.Put(0f); continue; }
            _hostSig[nm] = sig;
            any = true;
            w.Put(nm);
            w.Put(inSlot ? (byte)1 : (byte)0);
            w.Put(p.x); w.Put(p.y); w.Put(p.z);
        }
        if (!any && _hostSig.Count > 0) return; // 无变化不发（避免每 0.2s 全量覆盖客机）
        var data = NetProtocol.Snapshot(w);
        net.EnqueueBatch(data, true);
        if ((++_sendLog % 15) == 1)
            CoopLog.Debug("RecordItemSync.hostSend", () => $"[RecordItemSync] host send n={n} sig={_hostSig.Count}");
    }

    /// <summary>客机：检测拖放事件（IsBeingDragged true→false）→ 放下瞬间上行该唱片位置一次（主机权威）。</summary>
    private void ClientSendDropEvents(NetManager net, RecordItem[] items)
    {
        int n = Math.Min(items.Length, 16);
        var w = NetProtocol.Begin((MsgType)MsgType);
        w.Put((byte)1);
        bool any = false;
        foreach (var it in items)
        {
            if (it == null) continue;
            bool dragging = IsBeingDragged(it);
            if (!_dragState.TryGetValue(it, out var prev)) { _dragState[it] = dragging; continue; }
            if (prev && !dragging) // 刚放下
            {
                _dragState[it] = false;
                var p = it.transform.position;
                w.Put(it.name ?? "");
                w.Put((byte)0);
                w.Put(p.x); w.Put(p.y); w.Put(p.z);
                any = true;
            }
            else _dragState[it] = dragging;
        }
        if (!any) return;
        net.EnqueueBatch(NetProtocol.Snapshot(w), false);
        CoopRuntime.LogSource?.LogInfo("[RecordItemSync] client drop event up");
    }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int n = r.GetByte();
            var items = UnityEngine.Object.FindObjectsOfType<RecordItem>();
            _applying = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    string name = r.GetString();
                    byte inSlot = r.GetByte();
                    float x = r.GetFloat();
                    float y = r.GetFloat();
                    float z = r.GetFloat();
                    if (inSlot != 0) continue; // 该唱片在远端槽里或正在被远端拖动，本地不覆盖位置
                    if (items == null) continue;
                    // 按名字匹配本地唱片（解决两端数量/顺序不一致导致的错位抽搐）
                    RecordItem target = null;
                    foreach (var it in items)
                        if (it != null && it.name == name) { target = it; break; }
                    if (target != null)
                        target.transform.position = new Vector3(x, y, z);
                }
            }
            finally { _applying = false; }
            if (net.IsHost) net.EnqueueBatch(data, true); // 转发给其他客户端
            if ((++_recvLog % 15) == 1)
                CoopRuntime.LogSource?.LogInfo($"[RecordItemSync] recv n={n} local={(items?.Length ?? 0)}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"RecordItemSync OnPacket: {ex.Message}"); }
    }

    /// <summary>唱片是否正在被本端玩家拖动（拖动中位置由本地 DraggableItem 控制，快照不覆盖）。</summary>
    private static bool IsBeingDragged(RecordItem it)
    {
        try
        {
            var d = it.GetComponent<DraggableItem>();
            return d != null && d.IsBeingDragged;
        }
        catch { return false; }
    }

    /// <summary>唱片是否在某台唱片机的槽里（在槽里时位置由槽控制，RecordItemSync 不覆盖）。
    /// ⚠️ 2026-08-25 帧性能：改用播放器数组缓存（GetPlayerCache）——原每唱片每 0.2s 全场景 FindObjectsOfType 是 50ms/s 大头。</summary>
    private static bool IsInRecordSlot(RecordItem it)
    {
        try
        {
            var players = GetPlayerCache();
            if (players == null) return false;
            foreach (var p in players)
            {
                if (p == null) continue;
                if (p.slot != null && p.slot.CurrentItem != null
                    && p.slot.CurrentItem.gameObject != null
                    && p.slot.CurrentItem.gameObject == it.gameObject)
                    return true;
                if (p._currentRecord != null && p._currentRecord.gameObject == it.gameObject)
                    return true;
            }
        }
        catch { }
        return false;
    }
/// <summary>RecordPlayerController 播放器数组缓存（低频 3s 刷新 + 场景切换）——省 IsInRecordSlot 高频全场景扫描。
    /// ⚠️ static：IsInRecordSlot 是 static（HostSendChanges 每唱片调用），播放器数组跨实例共享。</summary>
    private static RecordPlayerController[] GetPlayerCache()
    {
        int sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
        _playerCacheTimer -= UnityEngine.Time.unscaledDeltaTime;
        if (_playerCache == null || sc != _playerCacheScene || _playerCacheTimer <= 0f)
        {
            _playerCacheTimer = CacheRefreshSec;
            _playerCacheScene = sc;
            _playerCache = UnityEngine.Object.FindObjectsOfType<RecordPlayerController>();
        }
        return _playerCache;
    }

    
    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _timer = 0f; _applying = false; _hostSig.Clear(); _dragState.Clear(); }
}
