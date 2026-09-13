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
    public int MsgType => 119;

    // ⚠️ 模块自注册：程序集加载时入队（V1 方案），Startup FlushPending 统一注册；同时注册中途加入快照
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SelfRegister()
    {
        CoopSyncRegistry.PendingRegister(false, () => new MapTokenSync());
        CoopSyncRegistry.PendingRegister(false, () => StateSnapshotSync.Register("maptoken", BuildMapTokenSnapshot, ApplyMapTokenSnapshot));
    }
    // ⚠️ 2026-08-26 帧性能：0.12s→0.3s（每 tick GameObject.Find + 遍历全部 token 读 TMP/路径/签名，
    // 8.3Hz 全遍历是 frame.log MapTokenSync 22-37ms/s 大头；0.3s 仍够拖拽 token 实时跟随）
    private const float Interval = 0.3f;
    private float _timer;
    private int _sendLog;
    private int _pathFrame;
    private int _photoDiag; // 照片位置/方向诊断降频
    private bool _applying;
    private bool _fullOnce; // 首次全量对齐（连接后一次，之后只广播变化，避免周期覆盖静止 Token）
    private bool _debugDumped;
    private readonly System.Collections.Generic.Dictionary<string, string> _lastSig = new(); // token id -> sig
    /// <summary>⚠️ 2026-08-26 争抢修复（对齐 官方联机 DraggableBridge remoteControlledTokens）：远端控制中的
    /// token id。应用远端广播后记录 → 发送循环跳过（本地不广播远端控制的 token，避免"主机↔客机互推"争抢）——
    /// 游戏本地 DraggableItem/MapPiece3D 物理会把应用后的位置改回，若我们不跳过，下一轮 SigOf 读到改回位置
    /// ≠ _lastSig（应用后位置）→ 判定变化 → 广播 → 对方又应用 → 又改回 → **无限双向互推**（"地图 Token 至少
    /// 两处同步争抢"根因）。本端重新拖拽该 token 时移除（重新本地权威）。</summary>
    private readonly System.Collections.Generic.HashSet<string> _remote = new();
    /// <summary>上一轮每个 token 是否本地拖拽中（拖拽边界检测：false→true 开始广播，true→false 发一次最终态）。</summary>
    private readonly System.Collections.Generic.Dictionary<string, bool> _wasDragging = new();
    /// <summary>⚠️ 2026-08-26 帧性能：Draggable Surface 引用缓存（避免每 0.3s 全场景 GameObject.Find）——
    /// 场景 buildIndex 变化 / 找不到（null）时刷新。</summary>
    private GameObject _mapCache;
    private int _mapScene = -1;
    /// <summary>Draggable Surface 下 Token 的"名字@路径"出现次数（判断是否多实例）——唯一实例不加
    /// childIndex，否则击杀标记在两端数量不同会导致后续 childIndex 偏移 → id 不同 → 单向/双向匹配失败。
    /// static：接收端 FindTokenById 也用（每帧 Tick 重建，两端场景布局一致 → 唯一性判断一致）。</summary>
    private static readonly Dictionary<string, int> _nameCount = new();

    /// <summary>⚠️ 2026-08-26 帧性能：Draggable Surface 缓存（场景切换/丢失刷新，避免每 tick 全场景 Find）。</summary>
    private GameObject GetMap()
    {
        int sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
        if (_mapCache == null || sc != _mapScene)
        {
            _mapCache = GameObject.Find("Draggable Surface");
            _mapScene = sc;
        }
        return _mapCache;
    }

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
            var map = GetMap();
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
                // ⚠️ 2026-08-26 照片诊断：打印照片 token（MapToken_Recon）位置+方向（降频）——对比两端
                // 照片位置/方向（"拍照不同步"：照片位置或方向两端不同，或多张重合）。
                if (IsPhoto(t) && (++_photoDiag % 7) == 1)
                {
                    try
                    {
                        var pp = t.localPosition;
                        var pr = t.localEulerAngles;
                        var wp = t.position; // 世界坐标（判断 Draggable Surface 朝向/位置两端是否不同）
                        // ⚠️ 2026-09-05 照片"消失"诊断：加 activeSelf + activeInHierarchy——照片反复 change 的
                        // 根因可能是 active 反复切换（任务图/本地逻辑 SetActive），导致广播 act 交替 → 客机照片
                        // 反复隐藏/显示（"照片消失"）。
                        bool actSelf = false, actHier = false;
                        try { actSelf = t.gameObject.activeSelf; } catch { }
                        try { actHier = t.gameObject.activeInHierarchy; } catch { }
                        // 照片 token 组件列表（确认是否有 TrajectoryTarget/RandomUIRotation 等会改视觉的组件）
                        string comps = "";
                        try
                        {
                            var cs = t.GetComponents<Component>();
                            if (cs != null)
                                for (int ci2 = 0; ci2 < cs.Length && ci2 < 10; ci2++)
                                {
                                    string tn = "?";
                                    try { tn = cs[ci2].GetIl2CppType().FullName; } catch { }
                                    comps += (comps.Length > 0 ? "," : "") + tn;
                                }
                        }
                        catch { }
                        // 子对象诊断：照片实际显示子对象（Image/Mesh 等）的**位置 + 旋转 + RandomUIRotation**——
                        // 照片 token localPosition 正常但视觉被挪到落点 → 需确认视觉子对象（Mesh/Image）位置是否与 token 分离。
                        string childInfo = "";
                        try
                        {
                            for (int ci = 0; ci < t.childCount && ci < 6; ci++)
                            {
                                var c = t.GetChild(ci);
                                if (c == null) continue;
                                bool hasRur = false;
                                try { hasRur = c.GetComponent<RandomUIRotation>() != null; } catch { }
                                var clp = c.localPosition;
                                var cwp = c.position;
                                var cr = c.localEulerAngles;
                                childInfo += $" | {c.name}(lp={clp.x:0.###},{clp.y:0.###},{clp.z:0.###} w=({cwp.x:0.##},{cwp.y:0.##},{cwp.z:0.##}) r={cr.x:0.#},{cr.y:0.#},{cr.z:0.#},rur={hasRur})";
                            }
                        }
                        catch { }
                        CoopLog.Info("MapTokenSync.photo", () => $"[MapTokenSync] photo id='{id}' comps=[{comps}] pos=({pp.x:0.###},{pp.y:0.###},{pp.z:0.###}) rot=({pr.x:0.#},{pr.y:0.#},{pr.z:0.#}) world=({wp.x:0.##},{wp.y:0.##},{wp.z:0.##}) actSelf={actSelf} actHier={actHier}{childInfo}", 1f);
                    }
                    catch { }
                }
                // ⚠️ 2026-08-26 争抢修复：远端控制中的 token 本端不广播（除非本端重新拖拽）。游戏本地物理
                // （DraggableItem/MapPiece3D）会把应用后的位置改回，跳过才不互推。
                bool dragging = IsLocalDragging(t);
                if (!dragging && _remote.Contains(id))
                {
                    _wasDragging[id] = false;
                    continue;
                }
                // 拖拽边界：刚释放（上一轮拖 → 本轮不拖）→ 发一次最终态（settle 后位置）
                bool wasDrag = _wasDragging.TryGetValue(id, out bool wd) && wd;
                _wasDragging[id] = dragging;
                if (wasDrag && !dragging)
                {
                    _remote.Remove(id);
                    _lastSig[id] = SigOf(t);
                    (changed ??= new List<string>()).Add(id);
                    continue;
                }
                // ⚠️ 2026-08-26：铁巢 token（Player Turret Piece）是**可拖拽标记**，需同步（拖拽后两端一致）；
                // 但**开局 forceFull 全量广播跳过它**——开局默认位置由游戏摆位（两端同 seed 天然一致，战术地图
                // 场景加载后才摆到默认格）。广播"未摆位/摆位中"的初始位置 → 客机被同步到错误位置（"开局铁巢
                // Token 直接出现在战术地图桌上"根因）。跳过时**必须记录 _lastSig[id]（当前签名）**——否则下一次
                // Tick（非 forceFull）因 _lastSig 无记录而把铁巢 token 当前（摆位中间）位置判定为"变化"广播 →
                // 对端初始化被移动。记录后只有**真正变化（玩家拖拽）**才广播。
                if (forceFull && IsTurretPiece(t))
                {
                    _lastSig[id] = SigOf(t);
                    continue;
                }
                // ⚠️ 2026-08-26 争抢修复（二）：**非拖拽 token 不广播**（对齐 官方联机“拖拽才同步”的原则）。
                // MapToken_Artillery 等是**游戏本地动态对象**（本地炮击/部署动画持续更新位置），玩家没拖它时
                // 两端本地逻辑各自跑 → 位置天然不同 → 每 0.3s 判定"变化" → 持续广播（CLIENT changed=1 持续）。
                // 只更新 _lastSig（作 anchor）不广播；forceFull（首次全量）例外（开局对齐）。玩家拖拽中/刚释放
                // 由上方拖拽分支处理，仍实时同步。
                // ⚠️ 2026-08-26 照片例外：照片 token（MapToken_Recon）**非拖拽也广播**——游戏自动生成（非玩家
                // 拖），生成时方向/位置需同步（"照片方向不同步"根因：非拖拽不广播跳过照片 → 方向两端不同）。
                // 照片静止后变化检测不触发 → 不持续广播（区别于 MapToken_Artillery 持续动态）。
                if (!forceFull && !dragging && !IsPhoto(t))
                {
                    _lastSig[id] = SigOf(t);
                    continue;
                }
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
            string changedNames = "";
            foreach (var id in changed)
            {
                var t = FindTokenById(map, id);
                if (t == null) continue;
                if (changedNames.Length > 0) changedNames += " | ";
                changedNames += id + " name=" + (t.name ?? "?");
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
            CoopLog.Debug("MapTokenSync.send", () => $"[MapTokenSync] send changed={changed.Count} host={net.IsHost} tokens=[{changedNames}]");
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
        {
            // ⚠️ 2026-08-31 照片（MapToken_Recon）：TMP 编号是照片内容序号（两端同 seed 一致且唯一），
            // 而实例名后缀 (N) 是 Unity 实例化顺序（两端可能不同，尤其两落点同时拍照时竞争）。
            // 照片 id 只用 TMP 编号 → 跨端稳定匹配（"照片消失/双落点位置错误"根因）。
            if (IsPhoto(t))
                baseId = "TMP:" + text;
            else
                // 有 TMP 编号（普通战术标记 MapToken_Artillery 等）：编号+路径唯一，**不加 childIndex**——
                // 动态添加/移除的击杀标记会改变 Draggable Surface 下的 childIndex，若所有 Token 都加
                // childIndex，跨端 index 不一致 → 普通 Token id 匹配失败 → 不同步（"只有一个标记同步"根因）。
                baseId = "TMP:" + text + "@" + path;
        }
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
            var map = GetMap();
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
                        // ⚠️ 2026-08-26 争抢修复：应用成功后记录远端控制——发送循环跳过该 token
                        // （避免游戏本地物理改回 → 本端判定变化 → 互推）。本端重新拖拽时移除。
                        _remote.Add(id);
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

    /// <summary>是否照片 token（MapToken_Recon / 名字含 "Recon"）——⚠️ 2026-08-26 照片方向同步：照片由游戏
    /// 自动生成（非玩家拖拽），生成时位置/方向确定。**非拖拽也需广播**（生成时同步方向——"照片方向不同步"
    /// 根因：非拖拽不广播跳过照片 → 方向两端不同）。照片静止后变化检测不触发 → 不会持续广播。</summary>
    private static bool IsPhoto(Transform t)
    {
        try
        {
            string nm = t != null && t.name != null ? t.name : "";
            if (nm.IndexOf("Recon", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string p = t != null && t.transform != null ? PathOf(t.transform) : "";
            if (p.IndexOf("Recon", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        catch { }
        return false;
    }

    /// <summary>是否铁巢 token（Player Turret Piece / 含 "Turret Piece"）——开局默认位置由游戏摆位，forceFull 跳过。</summary>
    private static bool IsTurretPiece(Transform t)
    {
        try
        {
            string nm = t != null && t.name != null ? t.name : "";
            if (nm.IndexOf("Turret Piece", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // 兜底：路径含 Draggable Surface/Player Turret Piece
            string p = t != null && t.transform != null ? PathOf(t.transform) : "";
            if (p.IndexOf("Turret Piece", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        catch { }
        return false;
    }

    private static bool IsToken(Transform t)
    {
        string nm = t.name ?? "";
        // ⚠️ 2026-08-26：铁巢 token（Player Turret Piece）**是** MapTokenSync 同步对象——它是可拖拽战术标记，
        // 玩家拖拽后两端需一致。但**开局不广播**（见 Tick 的 forceFull 跳过）：开局默认位置由游戏摆位（两端
        // 同 seed 天然一致），广播未摆位/摆位中位置会把对端开局状态覆盖掉（"开局铁巢 token 被错误移动"）。
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
    public void Reset() { _timer = 0f; _lastSig.Clear(); _applying = false; _remote.Clear(); _wasDragging.Clear(); _fullOnce = false; }

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
