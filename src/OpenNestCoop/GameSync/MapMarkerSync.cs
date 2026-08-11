using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 战术地图标记/画线同步（MapMarkerPlacer / MapMarkerLineUI，MsgType=107）。
/// 实现思路：
/// - 订阅 MapMarkerPlacer.OnMarkerFinalized（static 事件）捕获本地放置完成（含画线）。
/// - marker 类型 = prefab GameObject 名（去 "(Clone)"）；prefab 从 ClipboardToolSlot.markerPrefab 反查。
/// - 收到远端 → Instantiate prefab 到 mapRect + Initialize/UpdateLine/FinalizePlacement 完整创建。
/// 事件驱动 + 可靠直发（不依赖 Batch 队列）。
/// </summary>
public sealed class MapMarkerSync : ISyncedModule
{
    public byte MsgType => 107;
    private int _recvLog;
    private static bool _hooked;
    private static float _lastDragSend;
    private static readonly Dictionary<string, GameObject> _markers = new();
    // 远端拖拽中的临时 marker（kind -> GameObject）
    private static readonly Dictionary<string, GameObject> _dragMarkers = new();

    public static void EnsureHook()
    {
        if (_hooked) return;
        _hooked = true;
        try
        {
            MapMarkerPlacer.OnMarkerFinalized += new Action<MapMarkerLineUI>(OnMarkerFinalizedLocal);
            CoopRuntime.LogSource?.LogInfo("[MapMarkerSync] 已订阅 MapMarkerPlacer.OnMarkerFinalized");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapMarkerSync hook: {ex.Message}"); }
        try
        {
            new HarmonyLib.Harmony("open-nest-mapmarker").PatchAll(typeof(MapMarkerSync));
            CoopRuntime.LogSource?.LogInfo("[MapMarkerSync] 已 patch MapMarkerLineUI.UpdateLine（拖拽实时同步）");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapMarkerSync patch: {ex.Message}"); }
    }

    [HarmonyLib.HarmonyPatch(typeof(MapMarkerLineUI), "UpdateLine")]
    private static class UpdateLinePatch
    {
        private static void Postfix(MapMarkerLineUI __instance) => OnMarkerLineUpdate(__instance);
    }

    /// <summary>拖拽中每帧调用（节流 0.1s 广播实时画线）。</summary>
    private static void OnMarkerLineUpdate(MapMarkerLineUI marker)
    {
        var net = CoopRuntime.Net;
        if (net == null || marker == null) return;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        try
        {
            var placer = UnityEngine.Object.FindFirstObjectByType<MapMarkerPlacer>();
            if (placer == null || placer.currentMarkerUI == null || placer.currentMarkerUI != marker) return; // 只在拖拽中
            float now = Time.unscaledTime;
            if (now - _lastDragSend < 0.1f) return;
            _lastDragSend = now;
            string kind = CleanName(marker.gameObject.name);
            var origin = marker.OriginLocal;
            var tip = marker.TipLocalPosition;
            Vector2 target = new Vector2(origin.x + tip.x, origin.y + tip.y);
            SendMarker(kind, origin, target, "d_" + kind); // drag 标志
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapMarkerSync OnMarkerLineUpdate: {ex.Message}"); }
    }

    /// <summary>本端在地图桌上放置/画线完成 → 广播。</summary>
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
            // tip 是相对 origin 的偏移；远端 UpdateLine 需要绝对 target = origin + tip
            Vector2 target = new Vector2(origin.x + tip.x, origin.y + tip.y);
            SendMarker(kind, origin, target, id);
            _markers[id] = marker.gameObject;
            CoopRuntime.LogSource?.LogInfo($"[MapMarkerSync] placed kind={kind} id={id}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapMarkerSync OnMarkerFinalized: {ex.Message}"); }
    }

    private static void SendMarker(string kind, Vector2 origin, Vector2 target, string id)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        var w = NetProtocol.Begin((MsgType)107);
        w.Put((byte)0); // 标志：0=添加/拖拽
        w.Put(kind);
        w.Put(origin.x); w.Put(origin.y);
        w.Put(target.x); w.Put(target.y);
        w.Put(id);
        var data = NetProtocol.Snapshot(w);
        if (net.State == SessionState.Hosting)
        {
            foreach (var p in net.Roster)
                if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
        }
        else if (net.HostSteamId != 0)
        {
            net.Transport.Send(net.HostSteamId, data, true);
        }
    }

    public void Tick(float dt) { /* 事件驱动，但检测本地擦除（marker 被 Destroy）→ 广播删除 */ DetectLocalErase(dt); }

    /// <summary>检测本地标记被擦除（marker GameObject 被销毁）→ 广播删除事件给全员。
    /// 轮询 _markers：被本端玩家/游戏擦除的 marker（UnityEngine 假 null）从字典消失 → 发删除。
    /// 防环：应用远端删除时 _applyingRemove=true，此时自身 Destroy 不再上报。</summary>
    private static bool _applyingRemove;
    private static float _eraseTimer;
    private const float EraseInterval = 0.3f;

    private static void DetectLocalErase(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        _eraseTimer += dt;
        if (_eraseTimer < EraseInterval) return;
        _eraseTimer = 0f;
        if (_markers.Count == 0) return;
        List<string> gone = null;
        foreach (var kv in _markers)
        {
            var go = kv.Value;
            // Unity 假 null：GameObject 被销毁后 == null 为 true
            if (go == null) { (gone ??= new List<string>()).Add(kv.Key); continue; }
            // 兜底：marker 还在但已不在 placedMarkers（游戏用 SetActive(false)/移除方式擦除）→ 也视为擦除
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
            if (!_applyingRemove) // 应用远端删除导致的 Destroy 不再转发（防环）
                SendRemove(id);
        }
    }

    private static void SendRemove(string id)
    {
        var net = CoopRuntime.Net;
        if (net == null || string.IsNullOrEmpty(id)) return;
        var w = NetProtocol.Begin((MsgType)107);
        w.Put((byte)1); // 标志：1=删除
        w.Put(id);
        var data = NetProtocol.Snapshot(w);
        if (net.State == SessionState.Hosting)
        {
            foreach (var p in net.Roster)
                if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
        }
        else if (net.HostSteamId != 0)
        {
            net.Transport.Send(net.HostSteamId, data, true);
        }
        CoopRuntime.LogSource?.LogInfo($"[MapMarkerSync] erase broadcast id={id}");
    }

    /// <summary>对端应用删除：销毁 marker + 从 _markers/placedMarkers 移除（防环）。</summary>
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
            CoopRuntime.LogSource?.LogInfo($"[MapMarkerSync] applied erase id={id}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapMarkerSync RemoveMarker: {ex.Message}"); }
        finally { _applyingRemove = false; }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            byte flag = r.GetByte(); // 0=添加/拖拽，1=删除
            if (flag == 1)
            {
                // 删除：主机转发给其他客机 + 本端应用
                string id = r.GetString();
                if (net.IsHost)
                    foreach (var p in net.Roster)
                        if (!p.IsLocal && (ulong)p.SteamId != from)
                            net.Transport.Send(p.SteamId, data, true);
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
                // 拖拽过程实时更新
                if (net.IsHost)
                    foreach (var p in net.Roster)
                        if (!p.IsLocal && (ulong)p.SteamId != from)
                            net.Transport.Send(p.SteamId, data, true);
                ApplyDragMarker(kind, origin, target);
                return;
            }

            if (net.IsHost)
                foreach (var p in net.Roster)
                    if (!p.IsLocal && (ulong)p.SteamId != from)
                        net.Transport.Send(p.SteamId, data, true);

            ApplyMarker(id2, kind, origin, target);
            if ((++_recvLog % 5) == 1)
                CoopRuntime.LogSource?.LogInfo($"[MapMarkerSync] recv kind={kind} id={id2}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapMarkerSync OnPacket: {ex.Message}"); }
    }

    /// <summary>远端拖拽中：创建临时 marker（未 Finalize）或更新其线。</summary>
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
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapMarkerSync ApplyDragMarker: {ex.Message}"); }
    }

    private static void ApplyMarker(string id, string kind, Vector2 origin, Vector2 target)
    {
        if (string.IsNullOrEmpty(id) || _markers.ContainsKey(id)) return;
        try
        {
            var placer = UnityEngine.Object.FindFirstObjectByType<MapMarkerPlacer>();
            if (placer == null || placer.mapRect == null)
            {
                CoopRuntime.LogSource?.LogWarning("[MapMarkerSync] 地图桌未就绪");
                return;
            }
            var prefab = ResolvePrefab(kind, placer);
            if (prefab == null)
            {
                CoopRuntime.LogSource?.LogWarning($"[MapMarkerSync] 无匹配 prefab: {kind}");
                return;
            }
            var go = UnityEngine.Object.Instantiate(prefab, placer.mapRect);
            if (go == null) return;
            go.SetActive(true);
            var ui = go.GetComponent<MapMarkerLineUI>();
            var rt = go.GetComponent<RectTransform>();
            if (ui == null || rt == null)
            {
                CoopRuntime.LogSource?.LogWarning($"[MapMarkerSync] prefab 不是 MapMarkerLineUI: {kind}");
                UnityEngine.Object.Destroy(go);
                return;
            }
            rt.anchoredPosition = origin;
            rt.localRotation = Quaternion.identity;
            try { ui.Initialize(origin, placer.mapRect); } catch { }
            try { ui.UpdateLine(origin, target, placer.mapRect); } catch { }
            try { ui.FinalizePlacement(); } catch { }
            _markers[id] = go;
            // 该 kind 的拖拽临时 marker 已被正式 marker 替换，移除
            if (_dragMarkers.TryGetValue(kind, out var tmp) && tmp != null)
            {
                try { UnityEngine.Object.Destroy(tmp); } catch { }
                _dragMarkers.Remove(kind);
            }
            if (placer.placedMarkers != null)
            {
                try { placer.placedMarkers.Add(ui); } catch { }
            }
            CoopRuntime.LogSource?.LogInfo($"[MapMarkerSync] applied kind={kind} id={id}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapMarkerSync ApplyMarker: {ex.Message}"); }
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

    private static string CleanName(string name) =>
        string.IsNullOrEmpty(name) ? "" : name.Replace("(Clone)", "").Trim();

    /// <summary>中途加入：主机构建当前已放置的地图标记快照（供 StateSnapshotSync 打包）。</summary>
    public static byte[] BuildMapMarkerSnapshot()
    {
        try
        {
            if (_markers.Count == 0) return null;
            var w = NetProtocol.Begin((MsgType)107);
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
                Vector2 target = new Vector2(origin.x + tip.x, origin.y + tip.y);
                w.Put(kind);
                w.Put(origin.x); w.Put(origin.y);
                w.Put(target.x); w.Put(target.y);
                w.Put(kv.Key);
            }
            return NetProtocol.Snapshot(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapMarkerSync BuildMapMarkerSnapshot: {ex.Message}"); }
        return null;
    }

    /// <summary>中途加入：新成员应用已放置的地图标记快照。</summary>
    public static void ApplyMapMarkerSnapshot(byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int n = r.GetByte();
            for (int i = 0; i < n; i++)
            {
                string kind = r.GetString();
                float ox = r.GetFloat(); float oy = r.GetFloat();
                float tx = r.GetFloat(); float ty = r.GetFloat();
                string id = r.GetString();
                ApplyMarker(id, kind, new Vector2(ox, oy), new Vector2(tx, ty));
            }
            CoopRuntime.LogSource?.LogInfo($"[MapMarkerSync] apply snapshot n={n}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapMarkerSync ApplyMapMarkerSnapshot: {ex.Message}"); }
    }

    public void OnSessionStarted() { EnsureHook(); }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _markers.Clear(); }
}
