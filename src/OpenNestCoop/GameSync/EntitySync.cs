using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 任务实体同步（最小版）：同步场景内实体的世界坐标 + 状态/血量。
/// 主机权威：客户端本地实体变化上行 → 主机应用 → 广播；防环。
/// 实体按 MapEntity.ID 匹配，应用时写 MapEntity.Position（世界坐标）并触发 OnEntityMoved() 刷新视觉。
/// </summary>
public sealed class EntitySync : ISyncedModule
{
    /// <summary>高频任务实体状态 → 容忍丢失，全局降频时优先降（per-module 分级）。</summary>
    public NetModulePriority NetPriority => NetModulePriority.Low;
    public int MsgType => 104;

    // ⚠️ 模块自注册：程序集加载时入队（V1 方案），Startup FlushPending 统一注册；同时注册中途加入快照
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SelfRegister()
    {
        CoopSyncRegistry.PendingRegister(false, () => new EntitySync());
        CoopSyncRegistry.PendingRegister(false, () => StateSnapshotSync.Register("entity", BuildEntitySnapshot, ApplyEntitySnapshot));
    }

    /// <summary>主机检测/广播间隔。2026-08-31：0.1s 全量 Collect 开销过大（用户“高频太夸张”）→ 恢复 0.5s。
    /// 列车平滑改由客机端每帧插值（TickSmooth）实现，不靠高频广播。只有变化实体才广播。</summary>
    private const float Interval = 0.5f;
    private const float PosTolerance = 0.05f;
    /// <summary>主机心跳全量广播间隔（2026-08-26）：客机缺失实体靠心跳补齐——主机只在“变化时”广播会让
    /// 开局未就绪/补齐失败的实体永远补不上（日志：客机 collect=23 主机 35，missing-alive 触发补齐才涨）。
    /// 5s 全量（~35 实体×30B≈1KB/5s）带宽可接受（同 ReloadSync 心跳）。</summary>
    private const float HeartbeatInterval = 5f;
    private float _timer;
    private float _heartbeatTimer;
    private bool _applying;
    private float _logTimer;

    private sealed class Ent
    {
        public string Id;
        public float X;
        public float Y;   // 世界位置（Position.x / Position.z）
        public float LX;
        public float LY;  // 战术地图图标位置（Location.LocalPosition）——主机权威，图标实际摆位靠它
        public int State;
        public int Hp;     // 当前血量（MapEntity.Health）
        public int MaxHp;  // 最大血量（MapEntity.MaxHealth）——击杀判定必需，缺它补建实体不能击杀
        public int Armour; // 装甲
        public int Stars;  // 星级
        public bool Alive; // 存活标志（击杀时 IsAlive→false）——主机权威，客机端击杀标记/实体死亡表现靠它
        /// <summary>实体种类（EntityRoles 枚举 int）——⚠️ 2026-08-26：补传种类（客机补齐/创建用真实 Role，
        /// 修复“客机同步到的实体种类全是 Enemy”）。MapEntity.Role 可读写（interop）。</summary>
        public int Kind;
        /// <summary>地图图标（MapEntity.Icon，string）——⚠️ 2026-08-26：补传图标。视觉图标由 Icon 决定（非 Role）：
        /// created missing 补齐时 CreateMapEntity 第 10 参传 Icon，否则客机补建的实体 Icon 空 → 地图类型图标丢失
        /// （“客机实体丢部分类型”根因——日志 kind 枚举对，但 Icon 空 → 视觉图标缺）。</summary>
        public string Icon;
        /// <summary>⚠️ 2026-09-05 位置变化检测阈值降到 0.003：列车速度 ~0.01/s，0.5s 只动 0.005，
        /// 原阈值 0.1 导致列车位置只在 5s 心跳广播（每 5s 跳 0.05，客机插值后"一窜一窜"）。
        /// 降到 0.003 让列车 0.5s 变化广播（跳 0.005，几乎不可见），配合 LerpRate=5 平滑跟随。</summary>
        public bool SameAs(Ent o) =>
            Mathf.Abs(X - o.X) < PosTolerance && Mathf.Abs(Y - o.Y) < PosTolerance
            && Mathf.Abs(LX - o.LX) < 0.003f && Mathf.Abs(LY - o.LY) < 0.003f
            && State == o.State && Hp == o.Hp && MaxHp == o.MaxHp && Armour == o.Armour && Stars == o.Stars
            && Alive == o.Alive && Kind == o.Kind
            && string.Equals(Icon ?? "", o.Icon ?? "", StringComparison.Ordinal);
    }

    private readonly Dictionary<string, Ent> _known = new();
    private readonly Dictionary<string, Ent> _hknown = new();
    /// <summary>已触发击杀表现的实体 id（去重：每实体击杀只触发一次 OnDestroyed，避免每 Tick 重复）。
    /// static：ApplyEntity 是 static。EntitySync 由 CoopSyncRegistry 单例持有，static 安全。</summary>
    private static readonly HashSet<string> _killed = new();
    /// <summary>客机端插值目标：实体 id → 位置组件（EntityLocation）+ 目标本地坐标（LX/LY）。
    /// 列车视觉由 EntityLocation 驱动（ent.Position 是无效字段），故插值 PositionInRootSpace 而非 ent.Position。</summary>
    private static readonly Dictionary<string, EntityLocation> _lerpLocs = new();
    private static readonly Dictionary<string, Vector2> _lerpTargets = new();
    /// <summary>⚠️ 2026-09-05 指数平滑速率：30（快收敛）时列车位置广播每 5s 跳一次（0.04），cur 几帧内
    /// 追上 → 视觉"一窜一窜"。降到 5（约 0.6s 收敛 95%），cur 平滑滑动到跳变目标，列车不再"窜"。</summary>
    private const float LerpRate = 5f;
    private static int _smoothDiag;
    /// <summary>回退扫描节流：找不到的实体 ID 上次全场景扫描时间（5s 内不重复 FindObjectsOfTypeAll<EntityLocation>
    /// ——客户端本地缺实体（如动态炮兵）时每 0.5s 广播触发回退扫描全场景 → massive FPS loss 主因）。</summary>
    private static readonly Dictionary<string, float> _missingScan = new();
    private const float MissingScanInterval = 5f;
    private static int _missingLog;

    private static int _kindAppLog;
    private static int _kindSendLog;
    /// <summary>漂移诊断计数（applypos 日志降频）。</summary>
    private static int _posDiagLog;
    /// <summary>图标漂移诊断计数（collectpos 日志降频）。</summary>
    private static int _collectDiag;
    /// <summary>主机变化实体诊断计数（hostchg 日志降频）。</summary>
    private static int _hostChgLog;

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;

        // ⚠️ 2026-09-04 列车平滑：每帧把实体位置指数逼近目标（替代 ApplyEntity 直接瞬移 → “一跳一跳”）。
        TickSmooth(dt);

        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;

        // ⚠️ 2026-08-26：主机心跳全量广播（5s）——客机缺失实体靠心跳补齐（“客机同步到的实体会少”根因：
        // 主机只在变化时广播，开局未就绪/补齐失败的实体永远收不到）。心跳强制全量所有实体（含静态）。
        _heartbeatTimer += Interval;
        bool heartbeat = net.IsHost && _heartbeatTimer >= HeartbeatInterval;
        if (heartbeat) _heartbeatTimer = 0f;

        var list = Collect();
        // 轻量诊断：只打实体数量（每 ~5s 一次），不做 FindObjectsOfTypeAll 全场景扫描（会掉帧）
        _logTimer += Interval;
        if (_logTimer >= 5f)
        {
            _logTimer = 0f;
            CoopLog.Info("EntitySync.collect", () => $"[EntitySync] collect={list.Count} known={_known.Count} hknown={_hknown.Count}", 5f);
        }
        if (list.Count == 0) return;

        if (net.IsHost)
        {
            var changed = new List<Ent>();
            foreach (var e in list)
                if (heartbeat || !_hknown.TryGetValue(e.Id, out var k) || !k.SameAs(e))
                { _hknown[e.Id] = e; changed.Add(e); }
            if (changed.Count > 0) Broadcast(net, changed);
            // ⚠️ 2026-08-26 漂移诊断：打印变化实体 ID + 位置（确认 broadcast N 是谁在动——列车/机械化单位
            // 移动实体，位置每 0.5s 变化）。对比客机 collectpos 确认两端移动实体位置差（漂移量）。
            if (changed.Count > 0 && (++_hostChgLog % 20) == 1)
            {
                try
                {
                    string ids = "";
                    for (int i = 0; i < changed.Count && i < 8; i++)
                        ids += (ids.Length > 0 ? "," : "") + changed[i].Id + "=(" + changed[i].X.ToString("0.###") + "," + changed[i].Y.ToString("0.###") + ")";
                    CoopLog.Info("EntitySync.hostchg", () => $"[EntitySync] hostchg n={changed.Count} [{ids}]", 3f);
                }
                catch { }
            }
        }
        // ⚠️ 2026-08-26 主机权威：客机**不上行实体位置**。位置全由主机权威广播（心跳全量 5s 兜底对齐）。
        // 原实现客机本地 Collect 位置变化 → SendToHost 上行 → 主机 ApplyEntity 应用（覆盖主机权威位置）→
        // 主机广播 → 客机收到又变 → 再上行 → **双向互推乱飘**（"移动实体在乱飘"根因）。客机本地实体位置
        // 与主机基准可能不同（铁巢/任务脚本本地计算），上行覆盖主机权威必然争抢。实体位置主机权威单向。
        // 客机玩家拖拽实体（战术地图 DraggableItem）走 MapTokenSync 等专用模块，不经 EntitySync 上行。
        else
        {
            // 客机仍记录本地 Collect（供诊断/未来需要），但不上行位置
            foreach (var e in list)
            {
                try { if (_known.TryGetValue(e.Id, out var k)) { if (k.SameAs(e)) continue; } } catch { }
                _known[e.Id] = e;
            }
        }
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
            var applied = new List<Ent>(n);
            for (int i = 0; i < n; i++)
            {
                // ⚠️ 2026-08-26：Role 主机权威——客机上行**不带 Kind**（客机本地实体 Role 可能未初始化=0，
                // 上行 0 会覆盖主机正确 Role → 任务中途生成的新实体类型丢失）。只有主机广播带 Kind。
                // 主机收到客机上行时 Kind=0（不读取字段）；客机收到主机广播时读 Kind 应用。
                int kind = 0;
                string icon = "";
                var e = new Ent
                {
                    Id = r.GetString(), X = r.GetFloat(), Y = r.GetFloat(),
                    LX = r.GetFloat(), LY = r.GetFloat(),
                    State = r.GetByte(), Hp = r.GetInt(),
                    Alive = r.GetByte() != 0,
                    Kind = kind,
                    Icon = icon
                };
                if (!net.IsHost)
                {
                    e.Kind = r.GetInt();    // 仅广播包（客机接收）带 Kind
                    e.Icon = r.GetString(); // 仅广播包带 Icon（视觉图标）
                    e.MaxHp = r.GetInt();   // 最大血量
                    e.Armour = r.GetInt();
                    e.Stars = r.GetInt();
                }
                ApplyEntity(e, applyKind: !net.IsHost);
                applied.Add(e);
            }
            foreach (var e in applied)
            {
                _known[e.Id] = e;
                // ⚠️ 2026-08-26：主机不把客机上行写入 _hknown（客机上行 Kind=0 会污染 _hknown → HostTick 用
                // Collect 真实 Kind 对比恒不同 → 每 0.5s 持续广播）。_hknown 留给 HostTick 用主机真实 Kind 更新。
                if (!net.IsHost) _hknown[e.Id] = e;
            }
            CoopLog.Debug("EntitySync.recv", () => $"[EntitySync] recv n={n} isHost={net.IsHost}", 1f);
            // ⚠️ 2026-08-26：不再原样中继客机上行包（它无 Kind 且 Kind=0，转发会让其他客机按广播格式读越界
            // + 覆盖类型）。主机改用 HostTick 自己的 Collect（读主机本地 Role）检测到变化后广播正确 Kind。
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"EntitySync OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }

    public void Reset()
    {
        _known.Clear(); _hknown.Clear(); _applying = false; _killed.Clear(); _missingScan.Clear();
        _lerpTargets.Clear(); _lerpLocs.Clear();
    }

    /// <summary>客机端每帧插值：实体图标位置（EntityLocation，PositionInRootSpace）指数逼近主机广播目标
    /// （列车平滑，不瞬移）。指数平滑（同 PlayerSync）：t = 1 - exp(-LerpRate*dt)。
    /// ⚠️ 2026-09-04 持续锁定：收敛后**不移除**目标——否则本地任务图立即重新接管列车（推前），插值又拉回
    /// → 前进-后退抖动（"不平滑"）。持续把列车锁定到主机位置，覆盖本地任务图移动。只在实体销毁时清理。
    /// ⚠️ 列车视觉由 EntityLocation 驱动，ent.Position 是无效字段——插值必须对 EntityLocation 做。</summary>
    private static void TickSmooth(float dt)
    {
        if (_lerpTargets.Count == 0) return;
        var fm = FireMission.Instance;
        if (fm == null) return;
        float t = 1f - Mathf.Exp(-LerpRate * dt);
        var gone = new List<string>();
        foreach (var kv in _lerpTargets)
        {
            EntityLocation loc = null;
            try { if (_lerpLocs.TryGetValue(kv.Key, out var l)) loc = l; } catch { }
            if (loc == null || loc.gameObject == null) { gone.Add(kv.Key); continue; }
            try
            {
                var cur = loc.LocalPosition;
                var next = Vector2.Lerp(cur, kv.Value, t);
                fm.PositionInRootSpace(loc.gameObject, next);
                // 插值诊断（降频 ~1s）：当前值 vs 目标值——cur 逐渐逼近 target = 插值生效（平滑）；
                // cur 直接跳到 target = 插值未生效（瞬移）。
                if ((++_smoothDiag % 60) == 1)
                    CoopLog.Info("EntitySync.smooth", () => $"[EntitySync] smooth {kv.Key} cur=({cur.x:0.###},{cur.y:0.###}) target=({kv.Value.x:0.###},{kv.Value.y:0.###}) t={t:0.###}", 1f);
            }
            catch { }
        }
        foreach (var id in gone) { _lerpTargets.Remove(id); _lerpLocs.Remove(id); }
    }

    // ---------------- 内部 ----------------

    private static List<Ent> Collect()
    {
        var list = new List<Ent>();
        try
        {
            // 主数据源：FireMission.Entities 字典（只含已激活/已放置实体，Position 才是有效世界坐标）。
            // 不用 FindObjectsOfTypeAll<EntityLocation>——未初始化实体的 Entity.Position 是默认值
            // （实测全 (-66.5,0)），会污染坐标（打字机坐标/地图实体同源，主机客机都错）。
            var fm = FireMission.Instance;
            if (fm == null || fm.Entities == null)
                return CollectFallback(); // FireMission 未就绪时回退 EntityLocation 扫描
            try
            {
                // IL2CPP Dictionary 遍历用显式枚举器（foreach 在 IL2CPP 集合上不可靠）
                var en = fm.Entities.GetEnumerator();
                while (true)
                {
                    bool more;
                    try { more = en.MoveNext(); } catch { break; }
                    if (!more) break;
                    try
                    {
                        var kv = en.Current;
                        var e = kv.Value;
                        if (e == null) continue;
                        if (string.IsNullOrEmpty(e.ID)) continue;
                        var p = e.Position;
                        Vector2 lp = Vector2.zero;
                        bool lpOk = false;
                        try
                        {
                            var loc = e.Location;
                            if (loc != null) { lp = loc.LocalPosition; lpOk = true; }
                        }
                        catch { }
                        if (!lpOk)
                        {
                            try { lp = fm.ToLocalSpace(p); lpOk = true; } catch { }
                        }
                        bool alive = false;
                        try { alive = e.IsAlive; } catch { }
                        int kind = 0;
                        try { kind = (int)e.Role; } catch { }
                        string icon = "";
                        try { icon = e.Icon ?? ""; } catch { }
                        int maxHp = 0; try { maxHp = e.MaxHealth; } catch { }
                        int armour = 0; try { armour = e.Armour; } catch { }
                        int stars = 0; try { stars = e.Stars; } catch { }
                        list.Add(new Ent
                        {
                            Id = e.ID,
                            X = p.x,
                            Y = p.z,
                            LX = lp.x,
                            LY = lp.y,
                            State = (byte)(int)e.State,
                            Hp = e.Health,
                            MaxHp = maxHp,
                            Armour = armour,
                            Stars = stars,
                            Alive = alive,
                            Kind = kind,
                            Icon = icon
                        });
                        // ⚠️ 2026-08-26 图标漂移诊断：读 LX/LY（图标本地坐标）降频打印——对比两端战术地图
                        // 实体图标位置（世界坐标已确认一致，图标可能因 ToLocalSpace 基准/插值不同而漂移）。
                        if ((++_collectDiag % 120) == 1)
                        {
                            try
                            {
                                CoopLog.Info("EntitySync.collectpos", () => $"[EntitySync] collectpos id='{e.ID}' world=({p.x:0.###},{p.z:0.###}) local=({lp.x:0.###},{lp.y:0.###})", 5f);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
        catch { }
        return list;
    }

    /// <summary>回退：FireMission 未就绪时用 EntityLocation 扫描（坐标可能不完整，但保底收集 ID）。</summary>
    private static List<Ent> CollectFallback()
    {
        var list = new List<Ent>();
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<EntityLocation>();
            if (all == null) return list;
            foreach (var el in all)
            {
                if (el == null || el.Entity == null) continue;
                var e = el.Entity;
                if (string.IsNullOrEmpty(e.ID)) continue;
                var p = e.Position;
                Vector2 lp = Vector2.zero;
                bool lpOk = false;
                try { lp = el.LocalPosition; lpOk = true; } catch { }
                if (!lpOk)
                {
                    try { var fm = FireMission.Instance; if (fm != null) { lp = fm.ToLocalSpace(p); lpOk = true; } } catch { }
                }
                bool alive = false;
                try { alive = e.IsAlive; } catch { }
                int kind = 0;
                try { kind = (int)e.Role; } catch { }
                string icon = "";
                try { icon = e.Icon ?? ""; } catch { }
                int maxHp2 = 0; try { maxHp2 = e.MaxHealth; } catch { }
                int armour2 = 0; try { armour2 = e.Armour; } catch { }
                int stars2 = 0; try { stars2 = e.Stars; } catch { }
                list.Add(new Ent
                {
                    Id = e.ID,
                    X = p.x,
                    Y = p.z,
                    LX = lp.x,
                    LY = lp.y,
                    State = (byte)(int)e.State,
                    Hp = e.Health,
                    MaxHp = maxHp2,
                    Armour = armour2,
                    Stars = stars2,
                    Alive = alive,
                    Kind = kind,
                    Icon = icon
                });
            }
        }
        catch { }
        return list;
    }

    private static void ApplyEntity(Ent e, bool applyKind = false)
    {
        EntityLocation loc = null;
        try
        {
            // 用 FireMission.Entities 字典定位实体（避免 FindObjectsOfTypeAll<EntityLocation> 全场景扫描掉帧）
            var fm = FireMission.Instance;
            if (fm == null || fm.Entities == null) return;
            MapEntity ent = null;
            try
            {
                if (fm.Entities.ContainsKey(e.Id)) ent = fm.Entities[e.Id];
            }
            catch { }
            // ⚠️ 2026-08-15 反炮兵持续刷新的炮兵实体：动态生成实体可能未注册进 fm.Entities（或两端 ID 不一致）
            // → ApplyEntity 定位失败 → 客机端看不到新炮兵。回退：场景 EntityLocation 扫描按 ID 匹配，
            // 再按位置就近匹配（炮兵阵地位置稳定，两端同 seed 落点一致）。
            // ⚠️ 2026-08-17 节流：客户端本地缺实体时每 0.5s 广播都触发回退全场景扫描 → massive FPS loss。
            // 找不到的实体 ID 5s 内不重复扫描（5s 后重试，实体可能晚生成）。
            if (ent == null)
            {
                float now2 = UnityEngine.Time.realtimeSinceStartup;
                if (_missingScan.TryGetValue(e.Id, out var lastScan) && now2 - lastScan < MissingScanInterval)
                    return;
                _missingScan[e.Id] = now2;
                if (_missingScan.Count > 128)
                {
                    try { _missingScan.Clear(); } catch { } // 防无限增长
                }
                // ⚠️ 2026-08-17 缺实体补齐：客户端本地没有该实体（反炮兵动态炮兵等生成不同步）。
                // Alive=true（存活）→ 用 FireMission.CreateMapEntity 创建补齐（否则地图实体/任务目标缺失不同步）；
                // Alive=false（已销毁）→ 客户端已移除，无需创建。记录日志确认缺哪些实体。
                if (e.Alive)
                {
                    if ((++_missingLog % 3) == 1)
                        CoopRuntime.LogSource?.LogInfo($"[EntitySync] missing-alive entity id='{e.Id}' hp={e.Hp} st={e.State} pos=({e.X:0.0},{e.Y:0.0}) -> try create");
                    try
                    {
                        var fm2 = FireMission.Instance;
                        if (fm2 != null && fm2.Entities != null && !fm2.Entities.ContainsKey(e.Id))
                        {
                            // ⚠️ 2026-08-26：补传种类——用真实 Role（e.Kind）创建补齐，修复“客机同步到的实体种类全是 Enemy”。
                            // ⚠️ 2026-08-26：补传图标（e.Icon）——CreateMapEntity 第 10 参是 Icon，传空 → 补建实体图标缺失
                            // （视觉类型靠 Icon 非 Role，修复“客机实体丢部分类型”）。
                            var created = fm2.CreateMapEntity(e.Id, null, 0, new UnityEngine.Vector3(e.X, 0, e.Y),
                                (EntityRoles)e.Kind, e.MaxHp > 0 ? e.MaxHp : e.Hp, e.Armour, e.Stars, (MapEntityStates)e.State, e.Icon ?? "");
                            if (created != null)
                            {
                                try { fm2.RegisterMapEntity(created); } catch { }
                                CoopRuntime.LogSource?.LogInfo($"[EntitySync] created missing entity id='{e.Id}' hp={e.Hp} kind={(EntityRoles)e.Kind} icon='{e.Icon}'");
                            }
                        }
                    }
                    catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"EntitySync create missing entity: {ex.Message}"); }
                }
                else if ((++_missingLog % 5) == 1)
                {
                    CoopRuntime.LogSource?.LogInfo($"[EntitySync] missing-destroyed entity id='{e.Id}' (client already removed, skip)");
                }
                try
                {
                    var all = UnityEngine.Resources.FindObjectsOfTypeAll<EntityLocation>();
                    if (all != null)
                    {
                        foreach (var el in all)
                        {
                            if (el == null || el.Entity == null) continue;
                            if (el.Entity.ID == e.Id) { ent = el.Entity; loc = el; break; }
                        }
                        if (ent == null)
                        {
                            foreach (var el in all)
                            {
                                if (el == null || el.Entity == null) continue;
                                try
                                {
                                    var pp = el.Entity.Position;
                                    if (Mathf.Abs(pp.x - e.X) < 4f && Mathf.Abs(pp.z - e.Y) < 4f) { ent = el.Entity; loc = el; break; }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            }
            if (ent == null) return;
            // ⚠️ 2026-09-04 列车平滑：不直接瞬移，记录插值目标（TickSmooth 每帧指数逼近 → 列车平滑不跳）。
            // 列车视觉由 EntityLocation 驱动（ent.Position 是无效字段），故插值 PositionInRootSpace。
            try { if (loc == null) loc = ent.Location; } catch { }
            if (loc != null && loc.gameObject != null)
            {
                _lerpLocs[e.Id] = loc;
                _lerpTargets[e.Id] = new Vector2(e.LX, e.LY);
            }
            // ent.Position 仍设（世界坐标，无害；列车视觉靠 EntityLocation 插值）
            try { ent.Position = new UnityEngine.Vector3(e.X, 0, e.Y); } catch { }
            try { ent.State = (MapEntityStates)e.State; } catch { }
            try { ent.Health = e.Hp; } catch { }
            // ⚠️ 2026-09-04 击杀修复：补全最大血量/装甲/星级（缺 MaxHealth 补建实体不能击杀）
            try { if (e.MaxHp > 0) ent.MaxHealth = e.MaxHp; } catch { }
            try { ent.Armour = e.Armour; } catch { }
            try { ent.Stars = e.Stars; } catch { }
            // ⚠️ 2026-08-26 漂移诊断：应用后读实体实际位置（世界 + 图标本地），对比主机广播值——
            // 用户"实体还是漂移"。确认漂移量（PositionInRootSpace 后图标 transform 是否与主机值一致）。
            if ((++_posDiagLog % 60) == 1)
            {
                try
                {
                    var ap = ent.Position;
                    CoopRuntime.LogSource?.LogInfo($"[EntitySync] applypos id='{e.Id}' host=({e.X:0.###},0,{e.Y:0.###}) ent=({ap.x:0.###},{ap.y:0.###},{ap.z:0.###}) loc={loc?.gameObject?.name}");
                }
                catch { }
            }
            // ⚠️ 2026-08-26：应用真实种类（MapEntity.Role 可读写 interop）——修复“客机同步到的实体种类全是 Enemy”。
            // Role 主机权威：只有主机广播/快照（applyKind=true）才设 Role；客机上行（主机端 applyKind=false）不覆盖。
            if (applyKind)
            {
                try { ent.Role = (EntityRoles)e.Kind; } catch { }
                // ⚠️ 2026-08-26：应用图标（MapEntity.Icon）——视觉类型靠 Icon 非 Role（修复“客机实体丢部分类型”）。
                // 仅 applyKind（主机广播/快照）设 Icon，避免客机本地实体被上行覆盖。
                if (!string.IsNullOrEmpty(e.Icon))
                {
                    try { ent.Icon = e.Icon; } catch { }
                }
                // 诊断：客机应用种类（受限）——确认任务中途生成的新实体 Kind 是否正确传递（没类型=Kind 0/错）
                if ((++_kindAppLog % 20) == 1)
                    CoopRuntime.LogSource?.LogInfo($"[EntitySync] apply id='{e.Id}' kind={(EntityRoles)e.Kind} icon='{e.Icon}'");
            }
            // ⚠️ 2026-08-15 击杀同步：MapEntity.IsAlive interop **只读**（无 setter）无法直接设 →
            // 主机 IsAlive=false（Alive=false）时触发客机端 EntityLocation.OnDestroyed / OnStateUpdated
            // 事件驱动击杀标记/死亡表现（战术地图桌“主机跳击杀客机不跳”根因）。_killed 去重：每实体只触发一次。
            if (!e.Alive && !_killed.Contains(e.Id))
            {
                _killed.Add(e.Id);
                try
                {
                    if (loc != null)
                    {
                        try { loc.OnDestroyed?.Invoke(loc); } catch { }
                        try { loc.OnStateUpdated?.Invoke(); } catch { }
                    }
                }
                catch { }
            }
            try { if (loc != null) loc.OnEntityMoved(); } catch { }
        }
        catch { }
    }

    private void Broadcast(NetManager net, List<Ent> list)
    {
        var w = NetProtocol.Begin((MsgType)MsgType);
        w.Put((byte)list.Count);
        foreach (var e in list)
        {
            w.Put(e.Id ?? "");
            w.Put(e.X); w.Put(e.Y);
            w.Put(e.LX); w.Put(e.LY);
            w.Put((byte)e.State);
            w.Put(e.Hp);
            w.Put(e.Alive ? (byte)1 : (byte)0);
            w.Put(e.Kind); // ⚠️ 2026-08-26：补传种类（客机补齐/创建用真实 Role）
            w.Put(e.Icon ?? ""); // ⚠️ 2026-08-26：补传图标（视觉类型靠 Icon，非 Role——修复客机补齐实体图标缺失）
            w.Put(e.MaxHp); // ⚠️ 2026-09-04：补传最大血量（击杀判定必需）
            w.Put(e.Armour);
            w.Put(e.Stars);
        }
        var data = NetProtocol.Snapshot(w);
        CoopLog.Debug("EntitySync.broadcast", () => $"[EntitySync] broadcast {list.Count}", 1f);
        // 诊断：主机广播种类（受限）——确认新实体 Kind 是否从主机本地实体 Role 正确读出
        if ((++_kindSendLog % 20) == 1)
        {
            string k = "";
            for (int i = 0; i < list.Count && i < 8; i++)
                k += (k.Length > 0 ? "," : "") + list[i].Id + "=" + (EntityRoles)list[i].Kind;
            CoopRuntime.LogSource?.LogInfo($"[EntitySync] host broadcast kinds=[{k}]");
        }
        net.EnqueueBatch(data, true);
    }

    // ---------------- 中途加入快照（StateSnapshotSync "entity"）----------------

    /// <summary>中途加入：主机构建当前所有实体状态快照（供 StateSnapshotSync 打包）。
    /// ⚠️ 2026-08-15：中途加入未同步实体（反炮兵炮兵/药包等动态实体）→ 新成员缺实体/状态。
    /// 注册后新成员收到快照即应用所有实体状态对齐。</summary>
    public static byte[] BuildEntitySnapshot()
    {
        try
        {
            var list = Collect();
            if (list == null || list.Count == 0) return null;
            var w = NetProtocol.Begin((MsgType)104); // 104=EntitySync
            w.Put((byte)list.Count);
            foreach (var e in list)
            {
                w.Put(e.Id ?? "");
                w.Put(e.X); w.Put(e.Y);
                w.Put(e.LX); w.Put(e.LY);
                w.Put((byte)e.State);
                w.Put(e.Hp);
                w.Put(e.Alive ? (byte)1 : (byte)0);
                w.Put(e.Kind); // ⚠️ 2026-08-26：补传种类
                w.Put(e.Icon ?? ""); // ⚠️ 2026-08-26：补传图标
                w.Put(e.MaxHp); // ⚠️ 2026-09-04：补传最大血量/装甲/星级
                w.Put(e.Armour);
                w.Put(e.Stars);
            }
            return NetProtocol.Snapshot(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"EntitySync BuildSnapshot: {ex.Message}"); return null; }
    }

    /// <summary>中途加入：客机应用实体状态快照（按 ID → 场景 EntityLocation 回退）。
    /// ⚠️ 2026-08-26：快照是主机权威广播（带 Kind），客机应用 Role（applyKind=true）。</summary>
    public static void ApplyEntitySnapshot(byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int n = r.GetByte();
            for (int i = 0; i < n; i++)
            {
                var e = new Ent
                {
                    Id = r.GetString(), X = r.GetFloat(), Y = r.GetFloat(),
                    LX = r.GetFloat(), LY = r.GetFloat(),
                    State = r.GetByte(), Hp = r.GetInt(),
                    Alive = r.GetByte() != 0,
                    Kind = r.GetInt(),
                    Icon = r.GetString(), // ⚠️ 2026-08-26：快照带图标
                    MaxHp = r.GetInt(),
                    Armour = r.GetInt(),
                    Stars = r.GetInt()
                };
                ApplyEntity(e, applyKind: true);
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"EntitySync ApplySnapshot: {ex.Message}"); }
    }
}
