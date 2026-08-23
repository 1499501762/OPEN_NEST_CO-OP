using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#endif
namespace OpenNestCoop.GameSync;

/// <summary>
/// 战术令牌标记同步（MapToken_Artillery 等地图可拖拽令牌，MsgType=119）。
/// 令牌是 "Draggable Surface"（地图桌）下的子对象，名含 "MapToken"。
/// 同步 localPosition + localEulerAngles；本端拖拽中（DraggableItem.IsBeingDragged /
/// MapPiece3D.dragging）不应用远端，释放后由主机/操作方 settle。
/// 变化检测广播（防刷屏）；应用防环。
/// </summary>
public sealed class MapTokenSync : ISyncedModule
{
    public byte MsgType => 119;
    private const float Interval = 0.12f;
    private float _timer;
    private int _sendLog;
    private int _pathFrame;
    private bool _applying;
    private bool _fullOnce; // 首次全量对齐（连接后一次，之后只广播变化，避免周期覆盖静止 Token）
    private bool _debugDumped;
    private readonly System.Collections.Generic.Dictionary<string, string> _lastSig = new(); // token id -> sig
    /// <summary>Draggable Surface 下 Token 的"名字@路径"出现次数（判断是否多实例）——唯一实例不加
    /// childIndex，否则击杀标记在两端数量不同会导致后续 childIndex 偏移 → id 不同 → 单向/双向匹配失败。
    /// static：接收端 FindTokenById 也用（每帧 Tick 重建，两端场景布局一致 → 唯一性判断一致）。</summary>
    private static readonly Dictionary<string, int> _nameCount = new();

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        if (_applying) return; // 正在应用远端，不检测本地（防环）
        try
        {
            var map = GameObject.Find("Draggable Surface");
            if (map == null) return;
            // 统计名字@路径出现次数（唯一性判断，供 TokenId 决定是否加 childIndex）
            BuildNameCount(map);
            // 一次性：打印 token 结构（组件+子对象+文本，定位编号所在）
            if (!_debugDumped)
            {
                _debugDumped = true;
                int shown = 0;
                for (int i = 0; i < map.transform.childCount && shown < 6; i++)
                {
                    var t = map.transform.GetChild(i);
                    if (t == null || !IsToken(t)) continue;
                    shown++;
                    string comps = "";
                    try
                    {
                        var cs = t.GetComponents<Component>();
                        for (int ci = 0; ci < cs.Length && ci < 8; ci++)
                        {
                            string tn = "?";
                            try { tn = cs[ci].GetIl2CppType().FullName; } catch { }
                            comps += (comps.Length > 0 ? "," : "") + tn;
                        }
                    }
                    catch (Exception ex) { comps = "ERR:" + ex.Message; }
                    string kids = "";
                    for (int k = 0; k < t.childCount && k < 5; k++)
                    {
                        var kt = t.GetChild(k);
                        kids += (kids.Length > 0 ? "," : "") + (kt.name ?? "");
                        try
                        {
                            var tmp = kt.GetComponent<TMPro.TextMeshPro>();
                            if (tmp == null) tmp = kt.GetComponentInChildren<TMPro.TextMeshPro>(true);
                            if (tmp != null && !string.IsNullOrEmpty(tmp.text)) kids += "(" + tmp.text.Trim() + ")";
                        }
                        catch { }
                    }
                    CoopLog.Debug("MapTokenSync.tokenDebug", () => $"[MapTokenSync] DEBUG token='{t.name}' comps=[{comps}] kids=[{kids}]");
                }
            }
            bool forceFull = !_fullOnce;
            _fullOnce = true;
            int scanned = 0;
            // 诊断：每 60 帧打印所有 Token 的 id+名字
            if (++_pathFrame % 60 == 0)
            {
                string paths = "";
                for (int i = 0; i < map.transform.childCount; i++)
                {
                    var t = map.transform.GetChild(i);
                    if (t == null || !IsToken(t)) continue;
                    paths += (paths.Length > 0 ? " | " : "") + TokenId(t) + "@" + (t.name ?? "");
                }
                CoopLog.Debug("MapTokenSync.tokenIds", () => $"[MapTokenSync] token ids=[{paths}]", 5f);
            }
            List<string> changed = null;
            for (int i = 0; i < map.transform.childCount; i++)
            {
                var t = map.transform.GetChild(i);
                if (t == null || !IsToken(t)) continue;
                scanned++;
                string id = TokenId(t);
                if (string.IsNullOrEmpty(id)) continue;
                string sig = SigOf(t);
                if (!forceFull && _lastSig.TryGetValue(id, out var last) && last == sig) continue;
                _lastSig[id] = sig;
                (changed ??= new List<string>()).Add(id);
            }
            if (changed == null || changed.Count == 0)
            {
                if ((++_sendLog % 40) == 1)
                    CoopLog.Debug("MapTokenSync.scan", () => $"[MapTokenSync] scan tokens={scanned}");
                return;
            }
            var w = NetProtocol.Begin((MsgType)119);
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
            var data = NetProtocol.Snapshot(w);
            if (net.IsHost) net.EnqueueBatch(data, true);
            else net.EnqueueBatch(data, false);
            if ((++_sendLog % 20) == 1)
            {
                // 诊断：打印所有 Token 的 id + TMP 文本（确认编号是否读到、是否唯一）
                string ids = "";
                for (int i = 0; i < map.transform.childCount; i++)
                {
                    var t = map.transform.GetChild(i);
                    if (t == null || !IsToken(t)) continue;
                    string tmpText = "noTMP";
                    try
                    {
                        var tmp = t.GetComponent<TMPro.TextMeshPro>();
                        if (tmp == null) tmp = t.GetComponentInChildren<TMPro.TextMeshPro>(true);
                        if (tmp != null) tmpText = "'" + (tmp.text ?? "") + "'";
                    }
                    catch { tmpText = "err"; }
                    ids += (ids.Length > 0 ? " | " : "") + TokenId(t) + "@" + (t.name ?? "") + " tmp=" + tmpText;
                }
                CoopLog.Debug("MapTokenSync.ids", () => $"[MapTokenSync] ids=[{ids}]", 5f);
            }
            CoopLog.Debug("MapTokenSync.send", () => $"[MapTokenSync] send changed={changed.Count} host={net.IsHost}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapTokenSync Tick: {ex.Message}"); }
    }

    /// <summary>统计 Draggable Surface 下 Token 的"名字@路径"出现次数（唯一性判断）。</summary>
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

    /// <summary>Token 稳定 id：编号（TextMeshPro 文本）+ 完整路径 + 同路径实例序号——
    /// 同一标号不同颜色/不同对象的 Token 用路径区分；同路径多实例（如击杀标记
    /// MapToken_Killed_Enemy 可能同时存在多个，且 TMP 编号/路径完全相同）用
    /// Draggable Surface 下的 childIndex 区分，避免 id 冲突导致定位到错误实例
    /// （移除/移动不同步的根因）。两端场景布局一致，childIndex 跨端稳定。</summary>
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
        // 同路径多实例唯一化：Draggable Surface 下的 childIndex。
        // 静态上下文拿不到 map 引用，用最近父链中名为 "Draggable Surface" 的祖先下的 index。
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
            // 有 TMP 编号（普通战术标记 MapToken_Artillery 等）：编号+路径唯一，**不加 childIndex**——
            // 动态添加/移除的击杀标记会改变 Draggable Surface 下的 childIndex，若所有 Token 都加
            // childIndex，跨端 index 不一致 → 普通 Token id 匹配失败 → 不同步（"只有一个标记同步"根因）。
            baseId = "TMP:" + text + "@" + path;
        else
        {
            baseId = "NM:" + (t.name ?? "") + "@" + path;
            // 仅**同名多实例**（击杀标记 AlliedKillTokens/EnemyKillTokens 等）加 childIndex 区分；
            // **唯一实例**（Player Turret Piece 等）不加——击杀标记在两端数量不同会改变 childIndex，
            // 唯一实例 id 跨端不一致 → 客机同步给主机时 FindTokenById 匹配失败（单向不同步根因）。
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

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
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
                    // 按稳定 id（TextMeshPro 编号/名字）匹配本地 Token
                    var t = FindTokenById(map, id);
                    if (t == null) continue;
                    if (IsLocalDragging(t)) continue; // 本端拖拽中，不覆盖
                    try
                    {
                        t.localPosition = new Vector3(x, y, z);
                        t.localEulerAngles = new Vector3(rx, ry, rz);
                        // 同步 active 状态（击杀标记移除/回归初始位置）
                        try { if (t.gameObject.activeSelf != act) t.gameObject.SetActive(act); } catch { }
                    }
                    catch { }
                }
            }
            finally { _applying = false; }
            if (net.IsHost)
                foreach (var p in net.Roster)
                    if (!p.IsLocal && (ulong)p.SteamId != from)
                        net.Transport.Send(p.SteamId, data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapTokenSync OnPacket: {ex.Message}"); }
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

    // ⚠️ V1 死代码：FindToken（按路径匹配）全仓零调用，已完全被按 TokenId 匹配的 FindTokenById 取代，已注释。
    // private static Transform FindToken(string id, GameObject map)
    // {
    //     if (map == null) return null;
    //     for (int i = 0; i < map.transform.childCount; i++)
    //     {
    //         var t = map.transform.GetChild(i);
    //         if (t != null && PathOf(t) == id) return t;
    //     }
    //     return null;
    // }

    private static string SigOf(Transform t)
    {
        var p = t.localPosition;
        var r = t.localEulerAngles;
        // 加入 active 状态：击杀标记移除（回归初始位置）可能只是 SetActive(false)，
        // 位置不变时也要检测到变化并广播。
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

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _timer = 0f; _lastSig.Clear(); _applying = false; }

    // ---------------- 中途加入快照（方案 B） ----------------

    /// <summary>中途加入：主机构建当前所有战术令牌快照（供 StateSnapshotSync 打包）。</summary>
    public static byte[] BuildMapTokenSnapshot()
    {
        try
        {
            var map = GameObject.Find("Draggable Surface");
            if (map == null) return null;
            var w = NetProtocol.Begin((MsgType)119);
            int count = 0;
            for (int i = 0; i < map.transform.childCount; i++)
            {
                var t = map.transform.GetChild(i);
                if (t == null || !IsToken(t)) continue;
                string id = TokenId(t);
                if (string.IsNullOrEmpty(id)) continue;
                count++;
            }
            if (count == 0) return null;
            w.Put((byte)Math.Min(count, 255));
            int written = 0;
            for (int i = 0; i < map.transform.childCount && written < 255; i++)
            {
                var t = map.transform.GetChild(i);
                if (t == null || !IsToken(t)) continue;
                string id = TokenId(t);
                if (string.IsNullOrEmpty(id)) continue;
                written++;
                var p = t.localPosition;
                var r = t.localEulerAngles;
                bool act = false;
                try { act = t.gameObject.activeSelf; } catch { }
                w.Put(id);
                w.Put(p.x); w.Put(p.y); w.Put(p.z);
                w.Put(r.x); w.Put(r.y); w.Put(r.z);
                w.Put(act ? (byte)1 : (byte)0);
            }
            return NetProtocol.Snapshot(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapTokenSync BuildMapTokenSnapshot: {ex.Message}"); }
        return null;
    }

    /// <summary>中途加入：新成员应用战术令牌快照。</summary>
    public static void ApplyMapTokenSnapshot(byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int n = r.GetByte();
            var map = GameObject.Find("Draggable Surface");
            for (int i = 0; i < n; i++)
            {
                string id = r.GetString();
                float x = r.GetFloat(), y = r.GetFloat(), z = r.GetFloat();
                float rx = r.GetFloat(), ry = r.GetFloat(), rz = r.GetFloat();
                bool act = true;
                if (r.AvailableBytes > 0) act = r.GetByte() != 0;
                if (map == null) continue;
                var t = FindTokenById(map, id);
                if (t == null) continue;
                if (IsLocalDragging(t)) continue;
                try
                {
                    t.localPosition = new Vector3(x, y, z);
                    t.localEulerAngles = new Vector3(rx, ry, rz);
                    try { if (t.gameObject.activeSelf != act) t.gameObject.SetActive(act); } catch { }
                }
                catch { }
            }
            CoopRuntime.LogSource?.LogInfo($"[MapTokenSync] apply snapshot n={n}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"MapTokenSync ApplyMapTokenSnapshot: {ex.Message}"); }
    }
}
