using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;

namespace OpenNestCoop.GameSync;

/// <summary>
/// 炮弹**发射参数**同步（动态通道 key="shotparams"）——修"两端落点不一致"的**根因**（治本，不是事后纠正结果）。
///
/// <para><b>根因（2026-09-12 日志实测）</b>：炮弹飞行/着弹是"每端本地模拟"，但**开火参数并不是真的相同**：
/// 客机的开火是收到主机 GunFire 事件后在自己这边 <c>GunController.RequestFire()</c> **重新算一发**，
/// 用的是客机**自己那一刻**的炮塔/仰角/装药状态（仰角是物理量、随时间向目标值靠，两端帧时点不同 →
/// 数值不同）→ 炮弹 target 不同 → 落点不同。</para>
///
/// <para>实测证据（同一次射击）：HOST 开火 17:26:13.983 → 落点 (18.18,5.43)，飞行 25.62s；
/// CLIENT 复现开火 17:26:14.253 → 落点 (18.88,5.73)，飞行 26.84s。飞行时长差 ~1.2s（≈4%）对应射程差 ~4%
/// —— 典型的"发射参数小幅不同"，不是网络丢包/坐标系问题。</para>
///
/// <para><b>方案</b>：主机在 <c>ShellVisual.Initialize</c> 之后把**这发炮弹的发射参数**
/// （shellId / 起点 / 终点 / 飞行时长 / 路径长度）广播给客机；客机在自己创建同发炮弹时（或参数稍后到达时）
/// 把参数**套用到本地那一发**上 → 两端炮弹沿**同一条弹道**飞行 → 落点天然一致。
/// 于是着弹评估（<c>ImpactTracker.EvaluateImpact</c> 拿到的 loc）、落点标记、追踪器、照片角度种子
/// 全部**自然一致**，不再需要"把结果强行改回来"。</para>
///
/// <para><b>配对</b>：按炮弹**起点**就近配对（同一门炮两端起点几乎重合；双炮齐射用起点可区分左右）。
/// 包先到 → 入队等本地炮弹创建；本地先创建 → 等包到（两者都覆盖，均按起点配对，无顺序假设）。
/// 配对半径 <see cref="MatchRadius"/>，超出半径 / 超时（TTL）即丢弃，不会误套到别的炮弹上。</para>
///
/// <para>协议（主机 → 全员）：<c>[MsgType][shellId:string][sx:float][sy:float][tx:float][ty:float][dur:float][dist:float]</c>。
/// reliable（丢一发就会落点不一致，不能容忍丢失）。</para>
/// </summary>
public sealed class ShotSync : ISyncedModule
{
    /// <summary>自注册通道键（稳定字符串，双端一致；前导字节由 NetManager 注册管理器动态分配）。</summary>
    public const string ChannelKey = "shotparams";

    /// <summary>本模块前导字节：由注册管理器为 ChannelKey 分配（动态注册）。</summary>
    public int MsgType => CoopRuntime.Net?.ChannelType(ChannelKey) ?? 0;

    /// <summary>落点一致性是任务判定/伤害的基础 → 关键交互级（全局降频时几乎不降）。</summary>
    public NetModulePriority NetPriority => NetModulePriority.Critical;

    // ⚠️ 模块自注册：程序集加载时入队（V1 方案，动态通道），Startup FlushPending 统一注册。
    // ⚠️ 必须传 ChannelKey：不传 → 走静态采用路径 → MsgType 此刻还是 0（通道未分配）→ 注册被拒
    //   （主日志 "[Registry] type=0 (dynamic?) class=ShotSync"）→ 整轮模块不工作。
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SelfRegister() => CoopSyncRegistry.PendingRegister(false, () => new ShotSync(), ChannelKey);

    public static ShotSync Instance;

    private const float AuthTtl = 6f;       // 主机权威参数等待配对的存活时间（超时丢弃，防无界堆积）
    private const float LocalTtl = 4f;      // 客机"刚创建炮弹"候选保留窗口
    private const int MaxAuth = 8;          // 待配对权威参数上限
    private const float MatchRadius = 10f;  // 起点配对半径（地图单位；同门炮两端起点几乎重合）
    private const float SpawnDelay = 1.2f;  // 主机参数到达后等多久仍无本地炮弹 → 判定客机丢发，直接生成
    private static Transform _board;
    private int _nSpawn;

    /// <summary>一发炮弹的发射参数（两端通用）。</summary>
    private sealed class Shot
    {
        public string Shell;
        public Vector2 Start, Target;
        public float Dur, Dist, At;
        public bool Spawned; // 已尝试用本参数生成炮弹（避免反复重试）
    }

    /// <summary>客机刚创建、还没等到主机参数的炮弹。</summary>
    private sealed class LocalShot
    {
        public ShellVisual Sv;
        public Vector2 Start;
        public float At;
    }

    private readonly List<Shot> _auth = new();       // 客机：已到达、待配对的主机权威参数
    private readonly List<LocalShot> _local = new(); // 客机：已创建、待配对的本地炮弹
    private int _nSend, _nRecv, _nLate;

    public ShotSync() { Instance = this; }

    // ---------------- 入口（Harmony：ShellVisual.Initialize postfix，两端） ----------------

    /// <summary>炮弹创建之后调用（两端）。主机：广播这发炮弹的发射参数；客机：套用已到达的主机参数
    /// （若还没到 → 登记等待）。</summary>
    public void OnLocalInit(ShellVisual sv)
    {
        if (sv == null) return;
        var net = CoopRuntime.Net;
        if (net == null) return;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        try
        {
            Shot local;
            try
            {
                local = new Shot
                {
                    Shell = ShellIdOf(sv),
                    Start = sv.startLocalPos,
                    Target = sv.targetLocalPos,
                    Dur = sv.travelTime,
                    Dist = sv.totalPathDistance,
                    At = Time.time,
                };
            }
            catch { return; } // 字段读不到（异常拦截）→ 本发不同步

            if (net.IsHost)
            {
                int t = MsgType;
                if (t == 0) return; // 通道未分配（注册失败）→ 不发（防 type=0 包）
                var w = NetProtocol.Begin((MsgType)t);
                w.Put(local.Shell ?? "");
                w.Put(local.Start.x); w.Put(local.Start.y);
                w.Put(local.Target.x); w.Put(local.Target.y);
                w.Put(local.Dur); w.Put(local.Dist);
                var data = NetProtocol.Snapshot(w);
                net.EnqueueBatch(data, true, true); // reliable：丢一发就落点不一致
                _nSend++;
                CoopLog.Info("shot.host", () => $"[ShotSync] host send shell='{local.Shell}' start=({local.Start.x:0.000},{local.Start.y:0.000}) target=({local.Target.x:0.000},{local.Target.y:0.000}) dur={local.Dur:0.000} dist={local.Dist:0.000}");
                return;
            }

            var cand = new LocalShot { Sv = sv, Start = local.Start, At = Time.time };
            if (TryApplyBest(cand)) return;
            _local.Add(cand);
            Prune();
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ShotSync OnLocalInit: {ex.Message}"); }
    }

    /// <summary>客机：用已到达的权威参数配对本地这发炮弹（按起点就近）。配对成功返回 true。</summary>
    private bool TryApplyBest(LocalShot cand)
    {
        int best = -1;
        float bd = float.MaxValue;
        for (int i = 0; i < _auth.Count; i++)
        {
            float d = Vector2.Distance(_auth[i].Start, cand.Start);
            if (d < bd) { bd = d; best = i; }
        }
        if (best < 0 || bd > MatchRadius) return false;
        var s = _auth[best];
        _auth.RemoveAt(best);
        Apply(cand.Sv, s, "init");
        return true;
    }

    /// <summary>客机：主机发射参数到达。</summary>
    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return; // 主机权威：只发不收
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            var s = new Shot
            {
                Shell = r.GetString(),
                Start = new Vector2(r.GetFloat(), r.GetFloat()),
                Target = new Vector2(r.GetFloat(), r.GetFloat()),
                Dur = r.GetFloat(),
                Dist = r.GetFloat(),
                At = Time.time,
            };

            // 本地炮弹已创建（常见：本机复现开火时事件与参数几乎同时到）→ 立即配对
            int best = -1;
            float bd = float.MaxValue;
            for (int i = 0; i < _local.Count; i++)
            {
                float d = Vector2.Distance(_local[i].Start, s.Start);
                if (d < bd) { bd = d; best = i; }
            }
            if (best >= 0 && bd <= MatchRadius)
            {
                var sv = _local[best].Sv;
                _local.RemoveAt(best);
                Apply(sv, s, "recv");
                return;
            }

            _auth.Add(s);
            if (_auth.Count > MaxAuth) _auth.RemoveAt(0);
            _nLate++;
            CoopLog.Info("shot.late", () => $"[ShotSync] client auth queued (local shell not seen yet) shell='{s.Shell}' target=({s.Target.x:0.000},{s.Target.y:0.000}) start=({s.Start.x:0.000},{s.Start.y:0.000}) n={_nLate}");
            Prune();
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ShotSync OnPacket: {ex.Message}"); }
    }

    /// <summary>客机：把主机权威发射参数套用到本地这发炮弹上（等价于"用主机参数重新 Initialize"）。
    /// 只改参数、不动别的系统；此刻炮弹刚创建（飞行刚开始），重新起算飞行时钟视觉上无差别。</summary>
    private void Apply(ShellVisual sv, Shot s, string via)
    {
        if (sv == null) return;
        string own = "?";
        try
        {
            var t = sv.targetLocalPos;
            own = $"({t.x:0.000},{t.y:0.000})";
            sv.targetLocalPos = new Vector2(s.Target.x, s.Target.y);
            sv.travelTime = s.Dur;
            sv.totalPathDistance = s.Dist;
            double now = Time.time;
            sv.startedAt = now;
            sv.endsAt = now + s.Dur;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ShotSync Apply: {ex.Message}"); }
        _nRecv++;
        CoopLog.Info("shot.recv", () => $"[ShotSync] client apply via={via} shell='{s.Shell}' ownTarget={own} hostTarget=({s.Target.x:0.000},{s.Target.y:0.000}) dur={s.Dur:0.000} dist={s.Dist:0.000} n={_nRecv}");
    }

    private static string ShellIdOf(ShellVisual sv)
    {
        try
        {
            var d = sv.impactShell;
            if (d != null) return d.ShellId ?? "";
        }
        catch { }
        return "";
    }

    // ---------------- 生命周期 ----------------

    public void Tick(float dt)
    {
        if (_auth.Count == 0 && _local.Count == 0) return;
        Prune();
        // ⚠️ 2026-09-12：主机参数到达后 ~1.2s 仍未配到本地炮弹 → **这发在客机没有炮弹**
        // （实测：客机膛内无弹时 `RequestFire` 不会触发 `FireShell` → 客机丢一发，无炮弹/无落点标记/无照片）。
        // 为不再丢发，由本模块用主机参数**直接生成**这发炮弹（同弹种 + 同弹道参数）→ 客机照常出弹/着弹。
        // 门控：仅在客机（Joined）且该参数已超时；已生成的参数不再重试。
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return;
        if (net.State != SessionState.Joined) return;
        try
        {
            for (int i = _auth.Count - 1; i >= 0; i--)
            {
                var s = _auth[i];
                if (s.Spawned || s.At + SpawnDelay > Time.time) continue;
                s.Spawned = true; // 先置位：失败也不反复重试（下一轮看日志再调）
                TrySpawn(s);
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ShotSync Tick spawn: {ex.Message}"); }
    }

    /// <summary>客机：用主机参数直接生成一发炮弹（客机丢发兜底）。任一步失败则打日志跳过（不改现有行为）。</summary>
    private void TrySpawn(Shot s)
    {
        try
        {
            var def = ResolveShell(s.Shell);
            if (def == null) { CoopLog.Info("shot.spawn", () => $"[ShotSync] SPAWN skipped: shell definition not found for '{s.Shell}'"); return; }
            var prefab = ResolvePrefab(s.Shell);
            if (prefab == null) { CoopLog.Info("shot.spawn", () => "[ShotSync] SPAWN skipped: no shell visual prefab"); return; }
            var parent = ResolveBoard();
            if (parent == null) { CoopLog.Info("shot.spawn", () => "[ShotSync] SPAWN skipped: board/parent not found"); return; }
            var go = UnityEngine.Object.Instantiate(prefab, parent);
            if (go == null) return;
            var sv = go.GetComponent<ShellVisual>();
            if (sv == null) sv = go.GetComponentInChildren<ShellVisual>(true);
            if (sv == null) { CoopLog.Info("shot.spawn", () => "[ShotSync] SPAWN skipped: no ShellVisual on prefab"); return; }
            sv.Initialize(new Vector2(s.Start.x, s.Start.y), new Vector2(s.Target.x, s.Target.y), s.Dur, def);
            try { sv.totalPathDistance = s.Dist; } catch { }
            _nSpawn++;
            CoopLog.Info("shot.spawn", () => $"[ShotSync] client SPAWN shell='{s.Shell}' start=({s.Start.x:0.000},{s.Start.y:0.000}) target=({s.Target.x:0.000},{s.Target.y:0.000}) dur={s.Dur:0.000} (膛内无弹兜底) n={_nSpawn}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ShotSync TrySpawn: {ex.Message}"); }
    }

    /// <summary>按 ShellId 解析 `ShellDefinition`（与任务/弹舱同一批资源对象）。</summary>
    private static ShellDefinition ResolveShell(string sid)
    {
        try
        {
            var defs = UnityEngine.Resources.FindObjectsOfTypeAll<ShellDefinition>();
            if (defs != null)
                foreach (var d in defs)
                {
                    if (d == null) continue;
                    try { if (d.ShellId == sid) return d; } catch { }
                }
        }
        catch { }
        return null;
    }

    /// <summary>取炮弹 visual prefab（优先弹种匹配的 `ShellBlueprint.shellVisualPrefab`，否则任一）。</summary>
    private static GameObject ResolvePrefab(string sid)
    {
        try
        {
            var bps = UnityEngine.Resources.FindObjectsOfTypeAll<ShellBlueprint>();
            if (bps == null) return null;
            GameObject any = null;
            foreach (var bp in bps)
            {
                if (bp == null) continue;
                GameObject pv = null;
                try { pv = bp.shellVisualPrefab; } catch { }
                if (pv == null) continue;
                if (any == null) any = pv;
                try { if (bp.shellDefinition != null && bp.shellDefinition.ShellId == sid) return pv; } catch { }
            }
            return any;
        }
        catch { return null; }
    }

    /// <summary>弹道坐标系父对象（= `ShellVisual` 的父，实测为 `Tactical Map/Canvas/MapRoot/---ImpactMarkerManager`）。</summary>
    private static Transform ResolveBoard()
    {
        if (_board != null) return _board;
        try
        {
            var ils = UnityEngine.Object.FindObjectsOfType<ImpactLocation>(true);
            if (ils != null)
                foreach (var il in ils)
                    if (il != null && il.transform != null && il.transform.parent != null) { _board = il.transform.parent; return _board; }
        }
        catch { }
        try
        {
            var go = UnityEngine.GameObject.Find("Tactical Map/Canvas/MapRoot/---ImpactMarkerManager");
            if (go != null) _board = go.transform;
        }
        catch { }
        return _board;
    }

    private void Prune()
    {
        float now = Time.time;
        for (int i = _auth.Count - 1; i >= 0; i--)
            if (now - _auth[i].At > AuthTtl) _auth.RemoveAt(i);
        for (int i = _local.Count - 1; i >= 0; i--)
        {
            var l = _local[i];
            if (l.Sv == null || now - l.At > LocalTtl) _local.RemoveAt(i);
        }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }

    public void Reset()
    {
        _auth.Clear();
        _local.Clear();
        _nSend = _nRecv = _nLate = 0;
    }
}
