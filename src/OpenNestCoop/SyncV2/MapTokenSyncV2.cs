using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#endif

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 战术令牌标记同步（MapTokenSyncV2，MsgType=222）。M7：把 V1 <c>MapTokenSync</c>（119）迁入分层架构。
/// 双向：谁变化谁广播（localPosition + localEulerAngles + active，0.12s 变化检测，首次全量）；
/// 本端拖拽中不应用远端，释放后 settle；_applying 防环；主机中继。OnLateJoin 全量快照。
/// 稳定 id：TMP 编号+路径（同路径多实例加 childIndex），按名匹配本地令牌（两端场景布局一致）。
/// </summary>
public sealed class MapTokenSyncV2 : ISyncedModule
{
    public static MapTokenSyncV2 Instance { get; } = new MapTokenSyncV2();

    private MapTokenSyncV2() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2MapToken;

    private const float Interval = 0.12f;
    private float _timer;
    private bool _applying;
    private bool _fullOnce;
    private readonly Dictionary<string, string> _lastSig = new();
    private static readonly Dictionary<string, int> _nameCount = new();

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (_applying) return; // 防环
        try
        {
            var map = GameObject.Find("Draggable Surface");
            if (map == null) return;
            BuildNameCount(map);
            bool forceFull = !_fullOnce;
            _fullOnce = true;
            List<string> changed = null;
            for (int i = 0; i < map.transform.childCount; i++)
            {
                var t = map.transform.GetChild(i);
                if (t == null || !IsToken(t)) continue;
                string id = TokenId(t);
                if (string.IsNullOrEmpty(id)) continue;
                string sig = SigOf(t);
                if (!forceFull && _lastSig.TryGetValue(id, out var last) && last == sig) continue;
                _lastSig[id] = sig;
                (changed ??= new List<string>()).Add(id);
            }
            if (changed == null || changed.Count == 0) return;
            var net = _net;
            Store.Broadcast((byte)OpenNestCoop.Net.MsgType.V2MapToken, w =>
            {
                w.Put((byte)changed.Count);
                foreach (var id in changed)
                {
                    var t = FindTokenById(map, id);
                    if (t == null) continue;
                    var p = t.localPosition;
                    var r = t.localEulerAngles;
                    bool act = false;
                    try { act = t.gameObject.activeSelf; } catch { }
                    w.Put(id);
                    w.Put(p.x); w.Put(p.y); w.Put(p.z);
                    w.Put(r.x); w.Put(r.y); w.Put(r.z);
                    w.Put(act ? (byte)1 : (byte)0);
                }
            }, reliable: false);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MapTokenSyncV2] Tick: {ex.Message}"); }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte();
            int n = r.GetByte();
            var map = GameObject.Find("Draggable Surface");
            _applying = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    string id = r.GetString();
                    float x = r.GetFloat(), y = r.GetFloat(), z = r.GetFloat();
                    float rx = r.GetFloat(), ry = r.GetFloat(), rz = r.GetFloat();
                    bool act = true;
                    if (r.AvailableBytes > 0) act = r.GetByte() != 0;
                    if (map == null) continue;
                    var t = FindTokenById(map, id);
                    if (t == null || IsLocalDragging(t)) continue;
                    try
                    {
                        t.localPosition = new Vector3(x, y, z);
                        t.localEulerAngles = new Vector3(rx, ry, rz);
                        try { if (t.gameObject.activeSelf != act) t.gameObject.SetActive(act); } catch { }
                    }
                    catch { }
                }
            }
            finally { _applying = false; }
            if (Store.IsHost && from != 0)
            {
                var net = _net;
                if (net != null)
                    for (int k = 0; k < net.Roster.Count; k++)
                    {
                        var p = net.Roster[k];
                        if (p != null && !p.IsLocal && (ulong)p.SteamId != from)
                            net.Transport.Send(p.SteamId, data, true);
                    }
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MapTokenSyncV2] OnPacket: {ex.Message}"); }
    }

    public void OnLateJoin(ulong steamId)
    {
        if (Store.IsHost && steamId != 0)
        {
            var map = GameObject.Find("Draggable Surface");
            if (map == null) return;
            var net = _net;
            if (net == null) return;
            try
            {
                var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2MapToken);
                int count = 0;
                for (int i = 0; i < map.transform.childCount; i++)
                {
                    var t = map.transform.GetChild(i);
                    if (t == null || !IsToken(t)) continue;
                    string id = TokenId(t);
                    if (string.IsNullOrEmpty(id)) continue;
                    count++;
                }
                w.Put((byte)Math.Min(count, 255));
                int written = 0;
                for (int i = 0; i < map.transform.childCount && written < 255; i++)
                {
                    var t = map.transform.GetChild(i);
                    if (t == null || !IsToken(t)) continue;
                    string id = TokenId(t);
                    if (string.IsNullOrEmpty(id)) continue;
                    var p = t.localPosition;
                    var r = t.localEulerAngles;
                    bool act = false;
                    try { act = t.gameObject.activeSelf; } catch { }
                    written++;
                    w.Put(id);
                    w.Put(p.x); w.Put(p.y); w.Put(p.z);
                    w.Put(r.x); w.Put(r.y); w.Put(r.z);
                    w.Put(act ? (byte)1 : (byte)0);
                }
                net.Transport.Send(steamId, NetProtocol.Snapshot(w), true);
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[MapTokenSyncV2] OnLateJoin: {ex.Message}"); }
        }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _timer = 0f; _lastSig.Clear(); _applying = false; _fullOnce = false; }

    // ---------------- 稳定 id / 匹配 ----------------

    private static void BuildNameCount(GameObject map)
    {
        _nameCount.Clear();
        if (map == null) return;
        for (int i = 0; i < map.transform.childCount; i++)
        {
            var t = map.transform.GetChild(i);
            if (t == null || !IsToken(t)) continue;
            string key;
            try { key = (t.name ?? "") + "@" + PathOf(t); } catch { continue; }
            _nameCount[key] = (_nameCount.TryGetValue(key, out int c) ? c : 0) + 1;
        }
    }

    private static string TokenId(Transform t)
    {
        string text = "";
        try
        {
            var tmp = t.GetComponent<TMPro.TextMeshPro>();
            if (tmp == null) tmp = t.GetComponentInChildren<TMPro.TextMeshPro>(true);
            if (tmp != null && !string.IsNullOrEmpty(tmp.text)) text = tmp.text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                var ui = t.GetComponent<TMPro.TextMeshProUGUI>();
                if (ui == null) ui = t.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
                if (ui != null && !string.IsNullOrEmpty(ui.text)) text = ui.text.Trim();
            }
        }
        catch { }
        string path = PathOf(t);
        int idx = -1;
        try
        {
            var p = t.parent;
            while (p != null)
            {
                if (p.name == "Draggable Surface")
                {
                    for (int i = 0; i < p.childCount; i++)
                        if (p.GetChild(i) == t) { idx = i; break; }
                    break;
                }
                p = p.parent;
            }
        }
        catch { }
        string baseId;
        if (!string.IsNullOrEmpty(text))
            baseId = "TMP:" + text + "@" + path;
        else
        {
            baseId = "NM:" + (t.name ?? "") + "@" + path;
            string key = (t.name ?? "") + "@" + path;
            bool multi = _nameCount.TryGetValue(key, out int cnt) && cnt > 1;
            if (multi && idx >= 0) baseId += "#" + idx;
        }
        return baseId;
    }

    private static Transform FindTokenById(GameObject map, string id)
    {
        if (map == null) return null;
        for (int i = 0; i < map.transform.childCount; i++)
        {
            var t = map.transform.GetChild(i);
            if (t == null || !IsToken(t)) continue;
            if (TokenId(t) == id) return t;
        }
        return null;
    }

    private static bool IsToken(Transform t)
    {
        string nm = t.name ?? "";
        if (nm.IndexOf("MapToken", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (nm.IndexOf("Token", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (nm.IndexOf("Nest", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (nm.IndexOf("Disc", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (nm.IndexOf("Range", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (nm.IndexOf("Damage", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (nm.IndexOf("Marker", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        try
        {
            if (t.GetComponent<MapPiece3D>() != null) return true;
            if (t.GetComponent<DraggableItem>() != null) return true;
        }
        catch { }
        return false;
    }

    private static bool IsLocalDragging(Transform t)
    {
        try
        {
            var d = t.GetComponent<DraggableItem>();
            if (d != null && d.IsBeingDragged) return true;
            var p = t.GetComponent<MapPiece3D>();
            if (p != null && p.dragging) return true;
        }
        catch { }
        return false;
    }

    private static string SigOf(Transform t)
    {
        var p = t.localPosition;
        var r = t.localEulerAngles;
        bool act = false;
        try { act = t.gameObject.activeSelf; } catch { }
        return $"{(act ? 1 : 0)}|{p.x:0.###}|{p.y:0.###}|{p.z:0.###}|{r.x:0.#}|{r.y:0.#}|{r.z:0.#}";
    }

    private static string PathOf(Transform t)
    {
        if (t == null) return "";
        string path = t.name ?? "";
        var p = t.parent;
        while (p != null) { path = (p.name ?? "") + "/" + path; p = p.parent; }
        return path;
    }
}
