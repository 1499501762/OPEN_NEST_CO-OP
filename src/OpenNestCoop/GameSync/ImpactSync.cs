using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 着弹结果同步（MsgType=13）：**落点标记位置 + 照片角度 + 追踪目标**，主机权威、事件驱动、幂等。
/// <para>背景：两端各自本地评估着弹（同一发炮弹本地坐标会差 ~0.7，如 HOST (18.18,5.43) / CLIENT (18.88,5.73)）；
/// 照片角度由游戏 `RandomUIRotation.Awake` 在 `ImpactLocation/.../Parent` 上**各自随机 roll**
/// （实测 min=5 max=85 axis=Z，HOST 43.9° / CLIENT 77.9°）；追踪器终点（`TrajectoryTarget.defaultLocalPosition`）
/// 也由本地着弹点决定。</para>
/// <para>⚠️ 2026-09-12 **重新设计**（旧版"定时断言窗口 + 用落点哈希算角度 + 一个坐标套所有标记"作废）：</para>
/// <list type="number">
/// <item>**主机权威 = 游戏真实值**：主机照游戏原样跑（**不改**自己的落点与照片角度），只把游戏真实 roll 出来的
/// 角度 + 自己的落点广播出去。（旧版用"由落点哈希算角度"——自造算法，还把主机的随机也覆盖了。）</item>
/// <item>**只在明确事件点写入**：客机在「本地着弹报告（`ImpactLocation.EvaluateAndReport` postfix）」与
/// 「主机包到达」两处各写一次**同一个权威值** → 天然幂等 → **不需要任何定时窗口**
/// （旧版 3s × 0.15s 反复断言：费帧、且与客机本地后续逻辑互相打架）。</item>
/// <item>**按标记匹配**：主机事件带 `(x, y, 角度)`；客机用「本地已报告未匹配标记 FIFO + 就近容差」双匹配，
/// 逐个标记写入 → 多发炮弹不会互相覆盖（旧版 `ApplyTo` 会把场景里**所有** `ImpactLocation` 设成同一坐标）。</item>
/// <item>**帧开销**：每个事件最多 1 次 `FindObjectsOfType`（追踪目标，0.5s 缓存）；无周期扫描、无 Tick 轮询
/// （`Tick` 仅在主机有待发事件时工作一两帧）。</item>
/// </list>
/// <para>协议（主机 → 客机）：`[MsgType][x:float][y:float][tilt:float]`；`tilt` = 该标记照片的实际角度（度），
/// 该标记没有 `RandomUIRotation` 时为 `NoTilt`（-1）。客户端兼容旧 2 字段包（读不到 tilt 就视为 NoTilt）。</para>
/// </summary>
public sealed class ImpactSync : ISyncedModule
{
    public int MsgType => (byte)OpenNestCoop.Net.MsgType.Impact; // 13

    // ⚠️ 模块自注册：程序集加载时入队（V1 方案），Startup FlushPending 统一注册
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SelfRegister() => CoopSyncRegistry.PendingRegister(false, () => new ImpactSync());
    public static ImpactSync Instance;

    private const float NoTilt = -1f;          // "该标记没有照片角度"哨兵（游戏角度恒在 [0,360) 之内无此值）
    private const float MatchTolerance = 2f;   // 本地报告 → 事件的配对容差（地图单位；两端本地落点差实测 ~0.8）
    private const float NearTolerance = 1.5f;  // 包先到时只认"很近"的标记（紧容差，防止误挪另一发炮弹的标记）
    private const int MaxQueue = 8;            // 主机事件队列上限
    private const float EventTtl = 15f;        // 事件/待匹配标记存活时间（超时丢弃，防无界堆积）
    private const float PendingSendTimeout = 0.5f; // 主机等待"照片角度可读"的上限
    private const float TrackCacheSec = 0.5f;  // 追踪目标数组缓存

    /// <summary>主机待发事件（着弹当下照片子对象可能还没 roll，延迟到下一帧读真实角度再发）。</summary>
    private sealed class PendingSend { public ImpactLocation Marker; public Vector2 Loc; public float At; }
    private readonly System.Collections.Generic.List<PendingSend> _pendingSend = new();

    /// <summary>客机：主机发来的着弹事件队列（按到达顺序；每条只匹配一次）。</summary>
    private sealed class HostEvent { public Vector2 Loc; public float Tilt; public float At; }
    private readonly System.Collections.Generic.List<HostEvent> _hostEvents = new();

    /// <summary>客机：本地已报告但还没等到对应主机事件的标记（FIFO；包到达时优先配对最早的）。</summary>
    private readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Transform, float>> _pendingLocal = new();

    private TrajectorySystem.TrajectoryTarget[] _trackCache;
    private float _trackCacheAt = -999f;
    private int _sendDiag, _recvDiag;
    // 最近一次着弹（落点 + 照片角度）：只用于**中途加入**时给新成员强制对齐追踪器。
    private Vector2 _lastLoc;
    private float _lastTilt = NoTilt;
    private float _lastAt = -999f;

    public ImpactSync() { Instance = this; }

    // ---------------- 两端入口（Harmony：ImpactLocation.EvaluateAndReport postfix） ----------------

    /// <summary>本地着弹报告之后调用（两端）。主机：排队待广播（下一帧读到游戏真实照片角度后发）；
    /// 客机：用主机权威值把这个标记摆正。</summary>
    public void NotifyLocalReport(ImpactLocation il)
    {
        if (il == null || il.transform == null) return;
        var net = CoopRuntime.Net;
        if (net == null) return;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        try
        {
            var lp = il.transform.localPosition;
            var loc = new Vector2(lp.x, lp.y);
            if (net.IsHost)
            {
                // 主机保留游戏原始行为（不改自己的落点/照片角度），只排队广播
                _pendingSend.Add(new PendingSend { Marker = il, Loc = loc, At = Time.time });
                if (_pendingSend.Count > 4) _pendingSend.RemoveAt(0);
                return;
            }
            ApplyAuthoritative(il, loc);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ImpactSync NotifyLocalReport: {ex.Message}"); }
    }

    /// <summary>客机：把主机权威值（落点 + 照片角度）写到这个标记上。若对应事件还没到（客机本地着弹评估
    /// 可能先跑：日志实测包 17:26:39.8 / 本地评估 17:26:41.1）→ 把这个标记排进 FIFO，等包到达时优先配对它。</summary>
    private void ApplyAuthoritative(ImpactLocation il, Vector2 local)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < _hostEvents.Count; i++)
        {
            float d = Vector2.Distance(_hostEvents[i].Loc, local);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best >= 0 && bestD <= MatchTolerance)
        {
            // ⚠️ 不消费事件：客机的本地着弹可能**重复报告**同一个标记（日志实测：主机包 17:26:39.8 /
            // 客机本地评估 17:26:41.1，本地那次会把标记改回本地值）——事件保留 TTL 秒，
            // 重复报告时会再次匹配同一事件并把标记拉正（幂等，不需要定时窗口）。
            var e = _hostEvents[best];
            RemovePendingLocal(il.transform);
            WriteMarkerTransform(il.transform, e.Loc, e.Tilt);
            // ⚠️ 2026-09-12 用户确认：**追踪器不再单独同步**——铁巢地图图标（弹道起点）已同步 → 两端弹道一致，
            // 追踪器由落点自然驱动（写 defaultLocalPosition 反而与跟随器争抢）。只在**中途加入**时强制对齐一次。
            return;
        }
        try
        {
            foreach (var kv in _pendingLocal)
                if (kv.Key == il.transform) { PruneQueues(); return; }
            _pendingLocal.Add(new System.Collections.Generic.KeyValuePair<Transform, float>(il.transform, Time.time));
        }
        catch { }
        PruneQueues();
    }

    /// <summary>客机：主机着弹事件到达（`force=true` 仅用于中途加入的强制对齐）。</summary>
    private void OnHostEvent(Vector2 loc, float tilt, bool force)
    {
        _hostEvents.Add(new HostEvent { Loc = loc, Tilt = tilt, At = Time.time });
        if (_hostEvents.Count > MaxQueue) _hostEvents.RemoveAt(0);
        // ① 优先配对"本地已报告但还没等到事件"的标记（FIFO：与着弹顺序一致）
        Transform target = null;
        for (int i = 0; i < _pendingLocal.Count; i++)
        {
            var kv = _pendingLocal[i];
            _pendingLocal.RemoveAt(i);
            if (kv.Key != null) { target = kv.Key; break; }
            i--;
        }
        // ② 包先到（客机本地评估还没跑）→ 只认"很近"的标记（紧容差），否则不碰任何标记：
        //    避免把另一发炮弹的标记误挪到本发落点（客机随后的本地报告会用本事件匹配并拉正）。
        if (target == null)
        {
            float d;
            var near = FindNearestMarker(loc, out d);
            if (near != null && d <= NearTolerance) target = near;
        }
        if (target != null) WriteMarkerTransform(target, loc, tilt);
        if (force) WriteTrackTarget(loc); // 仅中途加入强制对齐追踪器（正常着弹不碰追踪器）
        PruneQueues();
        if ((++_recvDiag % 5) == 1)
            CoopLog.Info("impact.sync2", () => $"[ImpactSync] client recv loc=({loc.x:0.00},{loc.y:0.00}) tilt={tilt:0.0} matched={(target == null ? "none" : target.name)}", 0.5f);
    }

    // ---------------- 主机发送（无周期轮询：仅有待发事件时工作） ----------------

    public void Tick(float dt)
    {
        if (_pendingSend.Count == 0) return;
        var net = CoopRuntime.Net;
        if (net == null || net.State != SessionState.Hosting || !net.IsHost) { _pendingSend.Clear(); return; }
        try { SendReady(); }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ImpactSync Tick: {ex.Message}"); }
    }

    /// <summary>主机：把"照片角度已可读（照片子对象已建好并 roll 完）或已超时"的事件发出去。</summary>
    private void SendReady()
    {
        for (int i = 0; i < _pendingSend.Count; i++)
        {
            var p = _pendingSend[i];
            float tilt = ReadPhotoTilt(p.Marker);
            bool angleReady = tilt != NoTilt;
            bool timedOut = Time.time - p.At > PendingSendTimeout;
            if (!angleReady && !timedOut) continue; // 下一帧再看（保证广播的是游戏真实角度）
            _pendingSend.RemoveAt(i--);
            var net = CoopRuntime.Net;
            if (net == null) return;
            var w = NetProtocol.Begin((OpenNestCoop.Net.MsgType)MsgType);
            w.Put(p.Loc.x);
            w.Put(p.Loc.y);
            w.Put(tilt);
            w.Put((byte)0); // force=0：正常着弹（追踪器不再单独同步）；force=1 仅中途加入用
            var data = NetProtocol.Snapshot(w);
            net.EnqueueBatch(data, true, true); // reliable（落点/照片角度丢失不会自愈）
            _lastLoc = p.Loc; _lastTilt = tilt; _lastAt = Time.time; // 供中途加入强制对齐
            // 逐张照片角度日志（每发一条、不节流）：定位"所有照片同一个角度"——看主机实际 roll/读到的值
            try
            {
                int rc = 0; bool en = false; string ax = "?"; float mn = 0f, mx = 0f; string nm = "?";
                if (p.Marker != null && p.Marker.transform != null)
                {
                    nm = p.Marker.name ?? "?";
                    var rs = p.Marker.GetComponentsInChildren<RandomUIRotation>(true);
                    if (rs != null && rs.Length > 0)
                    {
                        rc = rs.Length; en = rs[0] != null && rs[0].enabled;
                        try { ax = rs[0].rotationAxis.ToString(); } catch { }
                        try { mn = rs[0].minAngle; mx = rs[0].maxAngle; } catch { }
                    }
                }
                CoopLog.Info("impact.photo", () => $"[ImpactSync] photo HOST marker='{nm}' rurs={rc} en={en} axis={ax} min={mn:0.#} max={mx:0.#} tilt={tilt:0.0}");
            }
            catch { }
            if ((++_sendDiag % 5) == 1)
                CoopLog.Info("impact.sync0", () => $"[ImpactSync] host send loc=({p.Loc.x:0.00},{p.Loc.y:0.00}) tilt={tilt:0.0}", 0.5f);
        }
    }

    // ---------------- 写入 ----------------

    private static void WriteMarkerTransform(Transform t, Vector2 loc, float tilt)
    {
        if (t == null) return;
        try
        {
            var lp = t.localPosition;
            lp.x = loc.x; lp.y = loc.y;
            t.localPosition = lp;
        }
        catch { }
        ApplyTilt(t, tilt);
        // 逐张照片角度日志（每发一条、不节流）：定位"所有照片同一个角度"——看客机实际写进去的值
        try
        {
            int rc = 0; bool en = false; string ax = "?"; float mn = 0f, mx = 0f;
            var rs = t.GetComponentsInChildren<RandomUIRotation>(true);
            if (rs != null && rs.Length > 0)
            {
                rc = rs.Length; en = rs[0] != null && rs[0].enabled;
                try { ax = rs[0].rotationAxis.ToString(); } catch { }
                try { mn = rs[0].minAngle; mx = rs[0].maxAngle; } catch { }
            }
            string nm = t.name ?? "?";
            CoopLog.Info("impact.photo", () => $"[ImpactSync] photo CLIENT marker='{nm}' rurs={rc} en={en} axis={ax} min={mn:0.#} max={mx:0.#} tilt={tilt:0.0}");
        }
        catch { }
    }

    /// <summary>把主机照片角度写到该标记下所有 `RandomUIRotation`（轴按组件自身配置；保留其它两轴）。</summary>
    private static void ApplyTilt(Transform root, float tilt)
    {
        if (root == null || tilt == NoTilt) return;
        try
        {
            var rurs = root.GetComponentsInChildren<RandomUIRotation>(true);
            if (rurs == null) return;
            foreach (var rur in rurs)
            {
                if (rur == null || rur.transform == null) continue;
                try
                {
                    var t = rur.transform;
                    var e = t.localEulerAngles;
                    int axis = AxisOf(rur);
                    if (axis == 0) t.localEulerAngles = new Vector3(tilt, e.y, e.z);
                    else if (axis == 1) t.localEulerAngles = new Vector3(e.x, tilt, e.z);
                    else t.localEulerAngles = new Vector3(e.x, e.y, tilt);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>读主机该标记照片的**真实随机角度**（游戏 `RandomUIRotation` 实际 roll 出来的值）。
    /// 该标记没有此组件（无照片角度概念）→ 返回 NoTilt。</summary>
    private static float ReadPhotoTilt(ImpactLocation il)
    {
        if (il == null || il.transform == null) return NoTilt;
        try
        {
            var rurs = il.GetComponentsInChildren<RandomUIRotation>(true);
            if (rurs == null || rurs.Length == 0) return NoTilt;
            var rur = rurs[0];
            if (rur == null || rur.transform == null) return NoTilt;
            var e = rur.transform.localEulerAngles;
            int axis = AxisOf(rur);
            return axis == 0 ? e.x : axis == 1 ? e.y : e.z;
        }
        catch { return NoTilt; }
    }

    /// <summary>`RandomUIRotation.rotationAxis` → 0=X / 1=Y / 2=Z（默认 Z，与实测 axis=Z 一致）。</summary>
    private static int AxisOf(RandomUIRotation rur)
    {
        try
        {
            var ax = rur.rotationAxis.ToString();
            if (ax.StartsWith("X", StringComparison.Ordinal)) return 0;
            if (ax.StartsWith("Y", StringComparison.Ordinal)) return 1;
        }
        catch { }
        return 2;
    }

    // ---------------- 照片角度：**同步随机种子**（游戏自己 roll，两端同值） ----------------
    // ⚠️ 2026-09-12 用户建议"看随机器怎么写，再决定预缓存结果 or 同步随机种子"：
    // 实测 `RandomUIRotation` 是 `Awake` 里用 **`UnityEngine.Random`** 在 [minAngle,maxAngle] 之间 roll
    // （`[RurSeed] match=YES` 验证：我们在 Awake 前 InitState(seed) 后，游戏 roll 出的值与我们预测的**完全相等**）。
    // `UnityEngine.Random` 是**全局可播种**伪随机 → 两端在 Awake 前用**同一个种子** InitState，游戏自己 roll
    // 出的角度就完全相同：**不用把角度传过去、不用事后覆盖、没有 1 帧闪烁**（旧方案两个缺点都消掉）。
    // 种子来源 = **两端一致的权威落点**（主机 = 自己标记的落点；客机 = 与本地标记最近的"主机事件"落点）；
    // 拿不到权威落点时（包还没到）就不干预，让游戏正常 roll，随后由包里的 `tilt` 兜底。
#if false // ⚠️ 2026-09-12 停用：实测未生效（双端 [RurSeed] 零日志 = seed 分支从未进入）+ 与"照片角度异常"排查相关。
    private bool _rurActive;
    private bool _rurHasSaved;
    private UnityEngine.Random.State _rurSaved;
    private float _rurPredicted, _rurMin, _rurMax;
    private int _rurSeed, _rurDiag;

    /// <summary>Harmony prefix：若该 `RandomUIRotation` 属于着弹照片，就把本次 roll 用的全局随机状态
    /// 设成"两端一致的种子" → 游戏自己的 `Awake` 会 roll 出同一个角度。非照片对象 / 不知道权威落点时不动。</summary>
    public void PrePhotoRotationAwake(RandomUIRotation rur)
    {
        _rurActive = false;
        try
        {
            if (rur == null || rur.transform == null) return;
            var net = CoopRuntime.Net;
            if (net == null) return;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
            Vector2 seedLoc;
            if (!TryGetSeedLoc(rur.transform, out seedLoc)) return;
            float min = 5f, max = 85f;
            try { min = rur.minAngle; } catch { }
            try { max = rur.maxAngle; } catch { }
            if (max - min <= 0.0001f) return;
            int seed = SeedOf(seedLoc);
            try { _rurSaved = UnityEngine.Random.state; _rurHasSaved = true; } catch { _rurHasSaved = false; }
            UnityEngine.Random.InitState(seed);
            _rurPredicted = UnityEngine.Random.Range(min, max); // 预测游戏将 roll 出的值（供验证/日志）
            UnityEngine.Random.InitState(seed);                 // 复位到同一状态 → 游戏自己的 roll 与预测同值
            _rurMin = min; _rurMax = max; _rurSeed = seed;
            _rurActive = true;
        }
        catch { _rurActive = false; }
    }

    /// <summary>Harmony postfix：恢复全局随机状态（不影响游戏其它随机序列），并打一行验证日志
    /// （每 3 次一条）：`predicted` = 我们按种子算的值，`actual` = 游戏实际 roll 的值；
    /// `match=YES` ⇒ 确认游戏用的就是 `UnityEngine.Random`，且种子同步已生效。</summary>
    public void PostPhotoRotationAwake(RandomUIRotation rur)
    {
        if (!_rurActive) return;
        _rurActive = false;
        try
        {
            if (_rurHasSaved) { UnityEngine.Random.state = _rurSaved; _rurHasSaved = false; }
            if (rur == null || rur.transform == null) return;
            float actual = 0f;
            try
            {
                var e = rur.transform.localEulerAngles;
                int axis = AxisOf(rur);
                actual = axis == 0 ? e.x : axis == 1 ? e.y : e.z;
            }
            catch { }
            if ((++_rurDiag % 3) == 1)
            {
                var net = CoopRuntime.Net;
                string role = (net == null || !net.IsHost) ? "CLIENT" : "HOST";
                bool same = Math.Abs(actual - _rurPredicted) < 0.01f;
                CoopRuntime.LogSource?.LogInfo($"[RurSeed] {role} min={_rurMin:0.#} max={_rurMax:0.#} seed={_rurSeed} predicted={_rurPredicted:0.00} actual={actual:0.00} match={(same ? "YES" : "NO")}");
            }
        }
        catch { }
    }

    /// <summary>取"种子用的权威落点"：主机 = 本端标记落点（本端就是权威）；客机 = 与该标记本地落点
    /// **最近的未过期主机事件**落点（容差 `MatchTolerance`）。取不到（包还没到）→ false。</summary>
    private bool TryGetSeedLoc(Transform photoT, out Vector2 loc)
    {
        loc = default(Vector2);
        var il = FindAncestorImpactLocation(photoT);
        if (il == null || il.transform == null) return false;
        var net = CoopRuntime.Net;
        if (net == null) return false;
        var lp = il.transform.localPosition;
        var local = new Vector2(lp.x, lp.y);
        if (net.IsHost)
        {
            loc = local; // 主机不改自己的值：它的落点就是权威值
            return true;
        }
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < _hostEvents.Count; i++)
        {
            float d = Vector2.Distance(_hostEvents[i].Loc, local);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best >= 0 && bestD <= MatchTolerance)
        {
            loc = _hostEvents[best].Loc;
            return true;
        }
        return false;
    }

    /// <summary>向上找 `ImpactLocation` 祖先（照片对象挂在落点标记下：`ImpactLocation/.../Parent`）。</summary>
    private static ImpactLocation FindAncestorImpactLocation(Transform t)
    {
        try
        {
            var p = t;
            for (int i = 0; i < 6 && p != null; i++)
            {
                var il = p.GetComponent<ImpactLocation>();
                if (il != null) return il;
                p = p.parent;
            }
        }
        catch { }
        return null;
    }

    /// <summary>落点 → 确定性种子（float 位 → FNV 混合）。两端落点相同 ⇒ 种子相同 ⇒ roll 结果相同。</summary>
    private static int SeedOf(Vector2 loc)
    {
        unchecked
        {
            uint hx = (uint)BitConverter.SingleToInt32Bits(loc.x);
            uint hy = (uint)BitConverter.SingleToInt32Bits(loc.y);
            uint h = (hx * 73856093u) ^ (hy * 19349663u);
            h ^= h >> 13; h *= 0x85EBCA6Bu; h ^= h >> 16;
            return (int)(h & 0x7FFFFFFF);
        }
    }

#endif

    private static Transform FindNearestMarker(Vector2 loc, out float dist)
    {
        dist = float.MaxValue;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<ImpactLocation>(true);
            Transform best = null;
            float bestD = float.MaxValue;
            if (all != null)
                foreach (var il in all)
                {
                    if (il == null || il.transform == null) continue;
                    var lp = il.transform.localPosition;
                    float d = Vector2.Distance(new Vector2(lp.x, lp.y), loc);
                    if (d < bestD) { bestD = d; best = il.transform; }
                }
            dist = bestD;
            return best;
        }
        catch { return null; }
    }

    /// <summary>把某标记从"本地已报告未配对"队列中移除（配对成功后调用）。</summary>
    private void RemovePendingLocal(Transform t)
    {
        if (t == null) return;
        try
        {
            for (int i = _pendingLocal.Count - 1; i >= 0; i--)
                if (_pendingLocal[i].Key == t) _pendingLocal.RemoveAt(i);
        }
        catch { }
    }

    /// <summary>追踪器（Shell Trajectory display：经 LocalTwoAxisTrajectoryTargetFollower 跟随 `TrajectoryTarget`）
    /// 的语义 = **跟踪最新着弹点** → 写主机权威落点（客机本地着弹评估晚于包到达时，本方法会在本地那一次
    /// 报告后再次被调用 → 拉正，无需定时窗口）。</summary>
    private void WriteTrackTarget(Vector2 loc)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return;
        try
        {
            float now = Time.time;
            if (_trackCache == null || now - _trackCacheAt > TrackCacheSec)
            {
                _trackCache = UnityEngine.Object.FindObjectsOfType<TrajectorySystem.TrajectoryTarget>(true);
                _trackCacheAt = now;
            }
            var tg = _trackCache;
            if (tg == null) return;
            foreach (var t in tg)
            {
                if (t == null || t.transform == null) continue;
                try { t.defaultLocalPosition = new Vector3(loc.x, loc.y, t.defaultLocalPosition.z); }
                catch { }
            }
        }
        catch { }
    }

    private void PruneQueues()
    {
        try
        {
            float now = Time.time;
            for (int i = _hostEvents.Count - 1; i >= 0; i--)
                if (now - _hostEvents[i].At > EventTtl) _hostEvents.RemoveAt(i);
            for (int i = _pendingLocal.Count - 1; i >= 0; i--)
                if (_pendingLocal[i].Key == null || now - _pendingLocal[i].Value > EventTtl) _pendingLocal.RemoveAt(i);
        }
        catch { }
    }

    // ---------------- 收包 ----------------

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return; // 仅客户端处理（主机只发）
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型（与旧版一致的读取方式）
            float x = r.GetFloat();
            float y = r.GetFloat();
            float tilt = NoTilt;
            if (r.AvailableBytes >= 4) tilt = r.GetFloat(); // 兼容旧版 2 字段包
            bool force = false;
            if (r.AvailableBytes >= 1) { try { force = r.GetByte() != 0; } catch { force = false; } }
            OnHostEvent(new Vector2(x, y), tilt, force);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ImpactSync OnPacket: {ex.Message}"); }
    }

    /// <summary>中途加入：主机把**最近一次着弹**（落点 + 照片角度）单播给新成员，并带 force=1
    /// → 客机收到后**强制对齐追踪器**（新成员本地没有着弹历史，追踪器停在默认位置）。
    /// 正常着弹已不再单独同步追踪器（铁巢地图图标/弹道起点已同步 → 两端弹道一致，追踪器由落点自然驱动）。</summary>
    public void OnLateJoin(ulong steamId)
    {
        try
        {
            var net = CoopRuntime.Net;
            if (net == null || !net.IsHost) return;
            if (Time.time - _lastAt > 300f) return; // 太久没有着弹（新任务）：不强制对齐
            var w = NetProtocol.Begin((OpenNestCoop.Net.MsgType)MsgType);
            w.Put(_lastLoc.x);
            w.Put(_lastLoc.y);
            w.Put(_lastTilt);
            w.Put((byte)1); // force：强制对齐追踪器
            net.Transport.Send(steamId, NetProtocol.Snapshot(w), true);
            CoopLog.Info("impact.latejoin", () => $"[ImpactSync] host late-join force-align loc=({_lastLoc.x:0.00},{_lastLoc.y:0.00}) tilt={_lastTilt:0.0} to {steamId}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ImpactSync OnLateJoin: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }

    public void Reset()
    {
        _pendingSend.Clear();
        _hostEvents.Clear();
        _pendingLocal.Clear();
        _trackCache = null;
        _trackCacheAt = -999f;
    }
}
