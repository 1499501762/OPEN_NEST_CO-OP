using System;
using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 战术地图标记/画线同步（MapMarkerSyncV2，MsgType=226）。M7：把 V1 <c>MapMarkerSync</c>（107）迁入分层架构。
/// 事件驱动：订阅 MapMarkerPlacer.OnMarkerFinalized 捕获放置完成 + Harmony patch MapMarkerLineUI.UpdateLine
/// 同步实时拖拽画线（0.1s 节流）；主机中继；检测本地擦除（marker 被 Destroy/移出 placedMarkers）广播删除；
/// OnLateJoin 主机全量快照（替代 V1 StateSnapshotSync "mapmarker"）。
/// </summary>
public sealed class MapMarkerSyncV2 : ISyncedModule
{
    public static MapMarkerSyncV2 Instance { get; } = new MapMarkerSyncV2();

    private MapMarkerSyncV2() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2MapMarker;

    private int _recvLog;
    private static bool _hooked;
    private static float _lastDragSend;
    private static readonly Dictionary<string, GameObject> _markers = new();
    private static readonly Dictionary<string, GameObject> _dragMarkers = new();
    private static bool _applyingRemove;
    private static float _eraseTimer;
    private const float EraseInterval = 0.3f;

    public static void EnsureHook()
    {
        if (_hooked) return;
        _hooked = true;
        try
        {
            MapMarkerPlacer.OnMarkerFinalized += new Action<MapMarkerLineUI>(OnMarkerFinalizedLocal);
            CoopRuntime.LogSource?.LogInfo("[MapMarkerSyncV2] subscribed MapMarkerPlacer.OnMarkerFinalized");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MapMarkerSyncV2] hook: {ex.Message}"); }
        try
        {
            // ML 的 Il2Cpp 程序集里也有 Harmony 命名空间（HarmonyX 兼容别名）会把裸 'Harmony' 遮蔽，
            // 故用完全限定 HarmonyLib.Harmony（两平台均有）。
            new HarmonyLib.Harmony("open-nest-mapmarker-v2").PatchAll(typeof(MapMarkerSyncV2));
            CoopRuntime.LogSource?.LogInfo("[MapMarkerSyncV2] patched MapMarkerLineUI.UpdateLine (live drag sync)");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MapMarkerSyncV2] patch: {ex.Message}"); }
    }

    [HarmonyPatch(typeof(MapMarkerLineUI), "UpdateLine")]
    private static class UpdateLinePatch
    {
        private static void Postfix(MapMarkerLineUI __instance) => OnMarkerLineUpdate(__instance);
    }

    private static void OnMarkerLineUpdate(MapMarkerLineUI marker)
    {
        var net = CoopRuntime.Net;
        if (net == null || marker == null) return;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        try
        {
            var placer = UnityEngine.Object.FindFirstObjectByType<MapMarkerPlacer>();
            if (placer == null || placer.currentMarkerUI == null || placer.currentMarkerUI != marker) return;
            float now = Time.unscaledTime;
            if (now - _lastDragSend < 0.1f) return;
            _lastDragSend = now;
            string kind = CleanName(marker.gameObject.name);
            var origin = marker.OriginLocal;
            var tip = marker.TipLocalPosition;
            var target = new Vector2(origin.x + tip.x, origin.y + tip.y);
            SendMarker(kind, origin, target, "d_" + kind);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MapMarkerSyncV2] OnMarkerLineUpdate: {ex.Message}"); }
    }

    private static void OnMarkerFinalizedLocal(MapMarkerLineUI marker)
    {
        var net = CoopRuntime.Net;
        if (net == null || marker == null) return;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        try
        {
            string kind = CleanName(marker.gameObject.name);
            string id = (net.Local?.SteamId ?? 0).ToString() + ":" + UnityEngine.Random.Range(0, 100000).ToString();
            var origin = marker.OriginLocal;
            var tip = marker.TipLocalPosition;
            var target = new Vector2(origin.x + tip.x, origin.y + tip.y);
            SendMarker(kind, origin, target, id);
            _markers[id] = marker.gameObject;
            CoopRuntime.LogSource?.LogInfo($"[MapMarkerSyncV2] placed kind={kind} id={id}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MapMarkerSyncV2] OnMarkerFinalized: {ex.Message}"); }
    }

    private static void SendMarker(string kind, Vector2 origin, Vector2 target, string id)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2MapMarker);
        w.Put((byte)0); // 0=添加/拖拽
        w.Put(kind);
        w.Put(origin.x); w.Put(origin.y);
        w.Put(target.x); w.Put(target.y);
        w.Put(id);
        var data = NetProtocol.Snapshot(w);
        if (net.State == SessionState.Hosting)
        {
            for (int i = 0; i < net.Roster.Count; i++)
            {
                var p = net.Roster[i];
                if (p != null && !p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
        }
        else if (net.HostSteamId != 0)
            net.Transport.Send(net.HostSteamId, data, true);
    }

    public void Tick(float dt) { DetectLocalErase(dt); }

    private static void DetectLocalErase(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null || _markers.Count == 0) return;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        _eraseTimer += dt;
        if (_eraseTimer < EraseInterval) return;
        _eraseTimer = 0f;
        List<string> gone = null;
        foreach (var kv in _markers)
        {
            var go = kv.Value;
            if (go == null) { (gone ??= new List<string>()).Add(kv.Key); continue; }
            var ui = go.GetComponent<MapMarkerLineUI>();
            if (ui == null) { (gone ??= new List<string>()).Add(kv.Key); continue; }
            var placer = UnityEngine.Object.FindFirstObjectByType<MapMarkerPlacer>();
            if (placer != null && placer.placedMarkers != null && !placer.placedMarkers.Contains(ui))
                (gone ??= new List<string>()).Add(kv.Key);
        }
        if (gone == null) return;
        foreach (var id in gone)
        {
            _markers.Remove(id);
            if (!_applyingRemove) SendRemove(id);
        }
    }

    private static void SendRemove(string id)
    {
        var net = CoopRuntime.Net;
        if (net == null || string.IsNullOrEmpty(id)) return;
        var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2MapMarker);
        w.Put((byte)1); // 1=删除
        w.Put(id);
        var data = NetProtocol.Snapshot(w);
        if (net.State == SessionState.Hosting)
        {
            for (int i = 0; i < net.Roster.Count; i++)
            {
                var p = net.Roster[i];
                if (p != null && !p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
        }
        else if (net.HostSteamId != 0)
            net.Transport.Send(net.HostSteamId, data, true);
        CoopRuntime.LogSource?.LogInfo($"[MapMarkerSyncV2] erase broadcast id={id}");
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            byte flag = r.GetByte(); // 0=添加/拖拽，1=删除
            if (flag == 1)
            {
                string id = r.GetString();
                RelayIfHost(from, data);
                RemoveMarker(id);
                return;
            }
            string kind = r.GetString();
            float ox = r.GetFloat(); float oy = r.GetFloat();
            float tx = r.GetFloat(); float ty = r.GetFloat();
            string id2 = r.GetString();
            var origin = new Vector2(ox, oy);
            var target = new Vector2(tx, ty);
            if (id2.StartsWith("d_"))
            {
                RelayIfHost(from, data);
                ApplyDragMarker(kind, origin, target);
                return;
            }
            RelayIfHost(from, data);
            ApplyMarker(id2, kind, origin, target);
            if ((++_recvLog % 5) == 1) CoopRuntime.LogSource?.LogInfo($"[MapMarkerSyncV2] recv kind={kind} id={id2}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MapMarkerSyncV2] OnPacket: {ex.Message}"); }
    }

    public void OnLateJoin(ulong steamId)
    {
        if (Store.IsHost && steamId != 0)
        {
            var net = _net;
            if (net == null || _markers.Count == 0) return;
            try
            {
                var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2MapMarker);
                w.Put((byte)Math.Min(_markers.Count, 255));
                int written = 0;
                foreach (var kv in _markers)
                {
                    if (written >= 255) break;
                    var go = kv.Value;
                    if (go == null) continue;
                    var ui = go.GetComponent<MapMarkerLineUI>();
                    if (ui == null) continue;
                    written++;
                    string kind = CleanName(go.name);
                    var origin = ui.OriginLocal;
                    var tip = ui.TipLocalPosition;
                    var target = new Vector2(origin.x + tip.x, origin.y + tip.y);
                    w.Put(kind);
                    w.Put(origin.x); w.Put(origin.y);
                    w.Put(target.x); w.Put(target.y);
                    w.Put(kv.Key);
                }
                net.Transport.Send(steamId, NetProtocol.Snapshot(w), true);
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MapMarkerSyncV2] OnLateJoin: {ex.Message}"); }
        }
    }

    private void RelayIfHost(ulong from, byte[] data)
    {
        if (!Store.IsHost) return;
        var net = _net;
        if (net == null) return;
        for (int i = 0; i < net.Roster.Count; i++)
        {
            var p = net.Roster[i];
            if (p != null && !p.IsLocal && (ulong)p.SteamId != from)
                net.Transport.Send(p.SteamId, data, true);
        }
    }

    private static void ApplyDragMarker(string kind, Vector2 origin, Vector2 target)
    {
        try
        {
            var placer = UnityEngine.Object.FindFirstObjectByType<MapMarkerPlacer>();
            if (placer == null || placer.mapRect == null) return;
            if (!_dragMarkers.TryGetValue(kind, out var go) || go == null)
            {
                var prefab = ResolvePrefab(kind, placer);
                if (prefab == null) return;
                go = UnityEngine.Object.Instantiate(prefab, placer.mapRect);
                if (go == null) return;
                go.SetActive(true);
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) { UnityEngine.Object.Destroy(go); return; }
                rt.anchoredPosition = origin;
                rt.localRotation = Quaternion.identity;
                var ui0 = go.GetComponent<MapMarkerLineUI>();
                if (ui0 != null) { try { ui0.Initialize(origin, placer.mapRect); } catch { } }
                _dragMarkers[kind] = go;
            }
            var ui = go.GetComponent<MapMarkerLineUI>();
            if (ui != null) { try { ui.UpdateLine(origin, target, placer.mapRect); } catch { } }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MapMarkerSyncV2] ApplyDragMarker: {ex.Message}"); }
    }

    private static void ApplyMarker(string id, string kind, Vector2 origin, Vector2 target)
    {
        if (string.IsNullOrEmpty(id) || _markers.ContainsKey(id)) return;
        try
        {
            var placer = UnityEngine.Object.FindFirstObjectByType<MapMarkerPlacer>();
            if (placer == null || placer.mapRect == null) return;
            var prefab = ResolvePrefab(kind, placer);
            if (prefab == null) return;
            var go = UnityEngine.Object.Instantiate(prefab, placer.mapRect);
            if (go == null) return;
            go.SetActive(true);
            var ui = go.GetComponent<MapMarkerLineUI>();
            var rt = go.GetComponent<RectTransform>();
            if (ui == null || rt == null) { UnityEngine.Object.Destroy(go); return; }
            rt.anchoredPosition = origin;
            rt.localRotation = Quaternion.identity;
            try { ui.Initialize(origin, placer.mapRect); } catch { }
            try { ui.UpdateLine(origin, target, placer.mapRect); } catch { }
            try { ui.FinalizePlacement(); } catch { }
            _markers[id] = go;
            if (_dragMarkers.TryGetValue(kind, out var tmp) && tmp != null)
            {
                try { UnityEngine.Object.Destroy(tmp); } catch { }
                _dragMarkers.Remove(kind);
            }
            if (placer.placedMarkers != null) { try { placer.placedMarkers.Add(ui); } catch { } }
            CoopRuntime.LogSource?.LogInfo($"[MapMarkerSyncV2] applied kind={kind} id={id}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MapMarkerSyncV2] ApplyMarker: {ex.Message}"); }
    }

    private static GameObject ResolvePrefab(string kind, MapMarkerPlacer placer)
    {
        try
        {
            var selectors = UnityEngine.Object.FindObjectsOfType<ClipboardToolSelector>();
            if (selectors != null)
                foreach (var sel in selectors)
                {
                    if (sel == null || sel.slots == null) continue;
                    for (int i = 0; i < sel.slots.Count; i++)
                    {
                        var slot = sel.slots[i];
                        if (slot == null || slot.markerPrefab == null) continue;
                        if (CleanName(slot.markerPrefab.name) == kind) return slot.markerPrefab;
                    }
                }
            var slots = UnityEngine.Object.FindObjectsOfType<ClipboardToolSlot>();
            if (slots != null)
                foreach (var slot in slots)
                {
                    if (slot == null || slot.markerPrefab == null) continue;
                    if (CleanName(slot.markerPrefab.name) == kind) return slot.markerPrefab;
                }
            if (placer != null && placer.activeMarkerPrefab != null && CleanName(placer.activeMarkerPrefab.name) == kind)
                return placer.activeMarkerPrefab;
        }
        catch { }
        return null;
    }

    private static void RemoveMarker(string id)
    {
        if (string.IsNullOrEmpty(id) || !_markers.TryGetValue(id, out var go)) return;
        _applyingRemove = true;
        try
        {
            if (go != null)
            {
                var ui = go.GetComponent<MapMarkerLineUI>();
                var placer = UnityEngine.Object.FindFirstObjectByType<MapMarkerPlacer>();
                if (placer != null && placer.placedMarkers != null && ui != null)
                {
                    try { placer.placedMarkers.Remove(ui); } catch { }
                }
                try { UnityEngine.Object.Destroy(go); } catch { }
            }
            _markers.Remove(id);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MapMarkerSyncV2] RemoveMarker: {ex.Message}"); }
        finally { _applyingRemove = false; }
    }

    private static string CleanName(string name) =>
        string.IsNullOrEmpty(name) ? "" : name.Replace("(Clone)", "").Trim();

    public void OnSessionStarted() { EnsureHook(); }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _markers.Clear(); _dragMarkers.Clear(); }
}
