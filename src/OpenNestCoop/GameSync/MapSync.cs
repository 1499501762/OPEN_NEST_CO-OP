using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 地图标记同步（最小可玩版，主机权威 + 事件/增量）。
/// - 轮询 MapMarkerPlacer.placedMarkers 检测本地新放置/移除的标记。
/// - 客户端放置 → MapMarkerAdd 上行主机 → 主机分配全局唯一 id → 广播全员。
/// - 客户端应用：按 prefabIdx 实例化 markerPrefabs 并 Initialize/UpdateLine 到地图坐标。
/// - 移除同理（MapMarkerRemove）；MapMarkerClearAll 清空网络实例。
/// 实现为本项目自有轮子：同步最终放置结果 + 拖拽过程中的实时 tip 位置（MapMarkerUpdate）。
/// </summary>
public static class MapSync
{
    private const float Interval = 0.2f;
    private const float TipDeadzone = 0.001f; // 实时拖拽位置变化阈值
    private const float InterpRate = 12f;     // 远端标记 tip 插值系数
    private static float _timer;
    private static MapMarkerPlacer _placer;
    private static int _nextId = 1;

    private sealed class LocalInfo
    {
        public int Id;
        public Vector2 KnownTip;
    }

    // 本地放置的标记：实例指针 → (id, 已知 tip)
    private static readonly Dictionary<IntPtr, LocalInfo> _local = new Dictionary<IntPtr, LocalInfo>();
    // 等待主机确认的本地放置实例（FIFO）
    private static readonly Queue<IntPtr> _pending = new Queue<IntPtr>();
    // 网络实例（其它玩家放的）：id → GameObject
    private static readonly Dictionary<int, GameObject> _remote = new Dictionary<int, GameObject>();

    private sealed class TipTarget
    {
        public Vector2 Origin;
        public Vector2 Tip;
        public bool Has;
    }

    // 远端标记的插值目标：id → (origin, tip)
    private static readonly Dictionary<int, TipTarget> _remoteTips = new Dictionary<int, TipTarget>();

    public static void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;

        // 每帧：远端标记 tip 插值
        ApplyInterpolated(dt);

        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;

        var placer = GetPlacer();
        if (placer == null || placer.placedMarkers == null) return;

        // 1) 检测本地新放置：placedMarkers 里不在 _local 的实例
        var newInstances = new List<MapMarkerLineUI>();
        foreach (var m in placer.placedMarkers)
        {
            if (m == null) continue;
            if (!_local.ContainsKey(m.Pointer))
                newInstances.Add(m);
        }
        foreach (var m in newInstances)
        {
            int prefabIdx = FindPrefabIndex(placer, m);
            if (prefabIdx < 0) { _local[m.Pointer] = new LocalInfo { Id = 0 }; continue; } // 无法识别，占位忽略
            Vector2 origin = m.OriginLocal;
            Vector2 tip = m.TipLocalPosition;
            if (net.IsHost)
            {
                // 主机放置：分配 id + 本地记录 + 广播
                int id = _nextId++;
                _local[m.Pointer] = new LocalInfo { Id = id, KnownTip = tip };
                BroadcastAdd(net, id, prefabIdx, origin, tip);
            }
            else
            {
                // 客户端放置：上行主机（id=0 占位），加入 pending 等待确认
                _pending.Enqueue(m.Pointer);
                SendAdd(net, prefabIdx, origin, tip);
            }
        }

        // 1.5) 实时拖拽：本地已确认标记的 tip 位置变化 → 同步
        foreach (var kv in _local)
        {
            var info = kv.Value;
            if (info.Id == 0) continue;
            MapMarkerLineUI marker = null;
            foreach (var m in placer.placedMarkers)
                if (m != null && m.Pointer == kv.Key) { marker = m; break; }
            if (marker == null) continue;
            Vector2 tip = marker.TipLocalPosition;
            if (Vector2.Distance(tip, info.KnownTip) < TipDeadzone) continue;
            info.KnownTip = tip;
            if (net.IsHost) BroadcastUpdate(net, info.Id, marker.OriginLocal, tip);
            else SendUpdate(net, info.Id, marker.OriginLocal, tip);
        }

        // 2) 检测本地移除：_local 中实例不再在 placedMarkers
        if (_local.Count > 0)
        {
            var removed = new List<int>();
            foreach (var kv in _local)
            {
                bool stillThere = false;
                foreach (var m in placer.placedMarkers)
                    if (m != null && m.Pointer == kv.Key) { stillThere = true; break; }
                if (!stillThere && kv.Value.Id != 0)
                    removed.Add(kv.Value.Id);
            }
            if (removed.Count > 0)
            {
                // 清理 _local 中对应 id 的条目
                var removeKeys = new List<IntPtr>();
                foreach (var kv in _local)
                    if (removed.Contains(kv.Value.Id))
                        removeKeys.Add(kv.Key);
                foreach (var k in removeKeys) _local.Remove(k);

                foreach (var id in removed)
                {
                    if (net.IsHost) BroadcastRemove(net, id);
                    else SendRemove(net, id);
                }
            }
        }
    }

    // ---------------- 消息处理 ----------------

    /// <summary>收到 MapMarkerAdd：主机分配 id + 转发；任意端应用。</summary>
    public static void OnAdd(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int id = r.GetInt();
            int prefabIdx = r.GetByte();
            var origin = new Vector2(r.GetFloat(), r.GetFloat());
            var tip = new Vector2(r.GetFloat(), r.GetFloat());

            if (net.IsHost && id == 0)
            {
                // 客户端上行占位 → 分配全局 id 并广播
                id = _nextId++;
                var w = NetProtocol.Begin(MsgType.MapMarkerAdd);
                w.Put(id);
                w.Put((byte)prefabIdx);
                w.Put(origin.x); w.Put(origin.y);
                w.Put(tip.x); w.Put(tip.y);
                data = NetProtocol.Snapshot(w);
                foreach (var p in net.Roster)
                    if (!p.IsLocal) net.Transport.Send(p.SteamId, data, false);
                ApplyAdd(id, prefabIdx, origin, tip); // 主机也显示
                return;
            }

            // 应用（发起者本地已有：用 pending 识别跳过实例化）
            ApplyAdd(id, prefabIdx, origin, tip);

            // 主机：转发给其它客户端（不含来源，避免重复）
            if (net.IsHost && net.Roster != null)
                foreach (var p in net.Roster)
                    if (!p.IsLocal && p.SteamId != from)
                        net.Transport.Send(p.SteamId, data, false);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapSync OnAdd: {ex.Message}"); }
    }

    public static void OnRemove(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int id = r.GetInt();
            ApplyRemove(id);
            if (net.IsHost && net.Roster != null)
                foreach (var p in net.Roster)
                    if (!p.IsLocal && p.SteamId != from)
                        net.Transport.Send(p.SteamId, data, false);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapSync OnRemove: {ex.Message}"); }
    }

    public static void OnClearAll(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            ApplyClearAll();
            if (net.IsHost && net.Roster != null)
                foreach (var p in net.Roster)
                    if (!p.IsLocal && p.SteamId != from)
                        net.Transport.Send(p.SteamId, data, false);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapSync OnClearAll: {ex.Message}"); }
    }

    /// <summary>收到 MapMarkerUpdate：实时拖拽 tip 位置更新。</summary>
    public static void OnUpdate(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int id = r.GetInt();
            var origin = new Vector2(r.GetFloat(), r.GetFloat());
            var tip = new Vector2(r.GetFloat(), r.GetFloat());
            if (_remote.ContainsKey(id))
                _remoteTips[id] = new TipTarget { Origin = origin, Tip = tip, Has = true };
            if (net.IsHost)
                net.EnqueueBatch(data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapSync OnUpdate: {ex.Message}"); }
    }

    // ---------------- 应用 ----------------

    private static void ApplyAdd(int id, int prefabIdx, Vector2 origin, Vector2 tip)
    {
        if (_remote.ContainsKey(id)) return;
        var placer = GetPlacer();
        if (placer == null || placer.markerPrefabs == null || placer.mapRect == null) return;
        // 发起者本地已有该标记（pending 队列里的实例）→ 只记录 id，不重复实例化
        if (_pending.Count > 0)
        {
            var ptr = _pending.Dequeue();
            _local[ptr] = new LocalInfo { Id = id, KnownTip = tip };
            return;
        }
        if (prefabIdx < 0 || prefabIdx >= placer.markerPrefabs.Count) return;
        var prefab = placer.markerPrefabs[prefabIdx];
        if (prefab == null) return;
        try
        {
            var go = UnityEngine.Object.Instantiate(prefab, placer.mapRect);
            var marker = go.GetComponent<MapMarkerLineUI>();
            if (marker != null)
            {
                marker.Initialize(origin, placer.mapRect);
                marker.UpdateLine(origin, tip, placer.mapRect);
            }
            _remote[id] = go;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapSync ApplyAdd: {ex.Message}"); }
    }

    private static void ApplyRemove(int id)
    {
        if (_remote.TryGetValue(id, out var go))
        {
            if (go != null)
            {
                try { UnityEngine.Object.Destroy(go); }
                catch { }
            }
            _remote.Remove(id);
        }
    }

    private static void ApplyClearAll()
    {
        foreach (var kv in _remote)
        {
            if (kv.Value != null)
            {
                try { UnityEngine.Object.Destroy(kv.Value); }
                catch { }
            }
        }
        _remote.Clear();
    }

    private static void ApplyInterpolated(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return;
        if (_remoteTips.Count == 0) return;
        var placer = GetPlacer();
        if (placer == null || placer.mapRect == null) return;
        float t = 1f - Mathf.Exp(-InterpRate * dt);
        var done = new List<int>();
        foreach (var kv in _remoteTips)
        {
            var target = kv.Value;
            if (!target.Has || !_remote.TryGetValue(kv.Key, out var go) || go == null)
            { done.Add(kv.Key); continue; }
            var marker = go.GetComponent<MapMarkerLineUI>();
            if (marker == null) { done.Add(kv.Key); continue; }
            var cur = marker.TipLocalPosition;
            var next = Vector2.Lerp(new Vector2(cur.x, cur.y), target.Tip, t);
            if (Vector2.Distance(next, target.Tip) < 0.0005f)
            { next = target.Tip; done.Add(kv.Key); }
            try { marker.UpdateLine(target.Origin, next, placer.mapRect); }
            catch { done.Add(kv.Key); }
        }
        foreach (var k in done) _remoteTips.Remove(k);
    }

    // ---------------- 发送 ----------------

    private static void SendAdd(NetManager net, int prefabIdx, Vector2 origin, Vector2 tip)
    {
        try
        {
            var w = NetProtocol.Begin(MsgType.MapMarkerAdd);
            w.Put(0); // id 占位，主机分配
            w.Put((byte)prefabIdx);
            w.Put(origin.x); w.Put(origin.y);
            w.Put(tip.x); w.Put(tip.y);
            net.Transport.Send(net.HostSteamId, NetProtocol.Snapshot(w), true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapSync SendAdd: {ex.Message}"); }
    }

    private static void BroadcastAdd(NetManager net, int id, int prefabIdx, Vector2 origin, Vector2 tip)
    {
        try
        {
            var w = NetProtocol.Begin(MsgType.MapMarkerAdd);
            w.Put(id);
            w.Put((byte)prefabIdx);
            w.Put(origin.x); w.Put(origin.y);
            w.Put(tip.x); w.Put(tip.y);
            var data = NetProtocol.Snapshot(w);
            foreach (var p in net.Roster)
                if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapSync BroadcastAdd: {ex.Message}"); }
    }

    private static void SendRemove(NetManager net, int id)
    {
        try
        {
            var w = NetProtocol.Begin(MsgType.MapMarkerRemove);
            w.Put(id);
            net.Transport.Send(net.HostSteamId, NetProtocol.Snapshot(w), true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapSync SendRemove: {ex.Message}"); }
    }

    private static void SendUpdate(NetManager net, int id, Vector2 origin, Vector2 tip)
    {
        try
        {
            var w = NetProtocol.Begin(MsgType.MapMarkerUpdate);
            w.Put(id);
            w.Put(origin.x); w.Put(origin.y);
            w.Put(tip.x); w.Put(tip.y);
            net.EnqueueBatch(NetProtocol.Snapshot(w), false);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapSync SendUpdate: {ex.Message}"); }
    }

    private static void BroadcastUpdate(NetManager net, int id, Vector2 origin, Vector2 tip)
    {
        try
        {
            var w = NetProtocol.Begin(MsgType.MapMarkerUpdate);
            w.Put(id);
            w.Put(origin.x); w.Put(origin.y);
            w.Put(tip.x); w.Put(tip.y);
            var data = NetProtocol.Snapshot(w);
            net.EnqueueBatch(data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapSync BroadcastUpdate: {ex.Message}"); }
    }

    private static void BroadcastRemove(NetManager net, int id)
    {
        try
        {
            var w = NetProtocol.Begin(MsgType.MapMarkerRemove);
            w.Put(id);
            var data = NetProtocol.Snapshot(w);
            foreach (var p in net.Roster)
                if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapSync BroadcastRemove: {ex.Message}"); }
    }

    // ---------------- 辅助 ----------------

    private static int FindPrefabIndex(MapMarkerPlacer placer, MapMarkerLineUI marker)
    {
        if (placer == null || placer.markerPrefabs == null || marker == null) return -1;
        for (int i = 0; i < placer.markerPrefabs.Count; i++)
        {
            var pf = placer.markerPrefabs[i];
            if (pf != null && !string.IsNullOrEmpty(pf.name)
                && marker.name != null && marker.name.StartsWith(pf.name, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static MapMarkerPlacer GetPlacer()
    {
        // 场景切换后旧的 placer 会被销毁（Unity 假 null），此时重新查找，保证任务场景里可用
        if (_placer == null)
        {
            try { _placer = UnityEngine.Object.FindFirstObjectByType<MapMarkerPlacer>(); }
            catch { _placer = null; }
            if (_placer == null)
                CoopRuntime.LogSource?.LogWarning("MapSync: 未找到 MapMarkerPlacer（地图标记同步禁用）");
        }
        return _placer;
    }
}
