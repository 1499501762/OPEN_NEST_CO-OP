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
    public byte MsgType => 104;

    private const float Interval = 0.5f;
    private const float PosTolerance = 0.05f;
    private float _timer;
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
        public int Hp;
        public bool Alive; // 存活标志（击杀时 IsAlive→false）——主机权威，客机端击杀标记/实体死亡表现靠它
        public bool SameAs(Ent o) =>
            Mathf.Abs(X - o.X) < PosTolerance && Mathf.Abs(Y - o.Y) < PosTolerance
            && Mathf.Abs(LX - o.LX) < 0.1f && Mathf.Abs(LY - o.LY) < 0.1f
            && State == o.State && Hp == o.Hp && Alive == o.Alive;
    }

    private readonly Dictionary<string, Ent> _known = new();
    private readonly Dictionary<string, Ent> _hknown = new();
    /// <summary>已触发击杀表现的实体 id（去重：每实体击杀只触发一次 OnDestroyed，避免每 Tick 重复）。
    /// static：ApplyEntity 是 static。EntitySync 由 CoopSyncRegistry 单例持有，static 安全。</summary>
    private static readonly HashSet<string> _killed = new();

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;

        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;

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
                if (!_hknown.TryGetValue(e.Id, out var k) || !k.SameAs(e))
                { _hknown[e.Id] = e; changed.Add(e); }
            if (changed.Count > 0) Broadcast(net, changed);
        }
        else if (!_applying)
        {
            // 聚合发送：把本次所有变化的实体合成一个包（n>1）上行，避免每实体一包
            // （任务场景 30+ 实体持续移动时，每实体一包 → 每秒 ~56 个小包，主机收包/反序列化/日志全被打爆，
            //   是 FPS 200→120 的主因）。主机 OnPacket 已支持 n>1 解析，格式完全兼容。
            var changed = new List<Ent>();
            foreach (var e in list)
                if (!_known.TryGetValue(e.Id, out var k) || !k.SameAs(e))
                { _known[e.Id] = e; changed.Add(e); }
            if (changed.Count > 0) SendToHost(net, changed);
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
                var e = new Ent
                {
                    Id = r.GetString(), X = r.GetFloat(), Y = r.GetFloat(),
                    LX = r.GetFloat(), LY = r.GetFloat(),
                    State = r.GetByte(), Hp = r.GetInt(),
                    Alive = r.GetByte() != 0
                };
                ApplyEntity(e);
                applied.Add(e);
            }
            foreach (var e in applied)
            {
                _known[e.Id] = e;
                _hknown[e.Id] = e;
            }
            CoopLog.Debug("EntitySync.recv", () => $"[EntitySync] recv n={n} isHost={net.IsHost}", 1f);
            if (net.IsHost)
                net.EnqueueBatch(data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"EntitySync OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }

    public void Reset()
    {
        _known.Clear(); _hknown.Clear(); _applying = false; _killed.Clear();
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
                        list.Add(new Ent
                        {
                            Id = e.ID,
                            X = p.x,
                            Y = p.z,
                            LX = lp.x,
                            LY = lp.y,
                            State = (byte)(int)e.State,
                            Hp = e.Health,
                            Alive = alive
                        });
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
                list.Add(new Ent
                {
                    Id = e.ID,
                    X = p.x,
                    Y = p.z,
                    LX = lp.x,
                    LY = lp.y,
                    State = (byte)(int)e.State,
                    Hp = e.Health,
                    Alive = alive
                });
            }
        }
        catch { }
        return list;
    }

    private static void ApplyEntity(Ent e)
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
            if (ent == null) return;
            try
            {
                loc = ent.Location;
                if (loc != null && loc.gameObject != null)
                {
                    var lp = new Vector2(e.LX, e.LY);
                    // PositionInRootSpace 强制摆图标到主机坐标（绕开对端投影算法差异——战术地图图标两端一致根因）
                    try { fm.PositionInRootSpace(loc.gameObject, lp); } catch { }
                }
            }
            catch { }
            try { ent.State = (MapEntityStates)e.State; } catch { }
            try { ent.Health = e.Hp; } catch { }
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
        }
        var data = NetProtocol.Snapshot(w);
        CoopLog.Debug("EntitySync.broadcast", () => $"[EntitySync] broadcast {list.Count}", 1f);
        net.EnqueueBatch(data, true);
    }

    private void SendToHost(NetManager net, List<Ent> list)
    {
        // 聚合：把所有变化实体合成一个包（n>1），避免每实体一包
        // （任务场景 30+ 实体持续移动 → 每实体一包 → 每秒 ~56 个小包，收包/反序列化/日志全被打爆）
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
        }
        net.EnqueueBatch(NetProtocol.Snapshot(w), false);
    }
}
