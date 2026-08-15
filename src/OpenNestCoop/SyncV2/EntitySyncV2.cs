using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 任务实体同步（EntitySyncV2，MsgType=206）。M7：把 V1 <c>EntitySync</c> 迁入分层架构。
/// - <see cref="V2Authority.Host"/> 语义：实体状态是全局共享（任务场景实体双方有本地副本），
///   客户端聚合变化上行 → 主机应用并广播 → 各端一致（单真相在主机合并后分发）。
/// - 主数据源 FireMission.Entities 字典（只含已激活实体，Position 才有效；不用 FindObjectsOfTypeAll
///   避免未初始化实体污染坐标）。IL2CPP 显式枚举器遍历。
/// - 聚合发送（n&gt;1 合 1 包）：任务场景 30+ 实体持续移动时每实体一包会打爆收包/反序列化（FPS 200→120 根因）。
/// - 应用：写 State/Health + PositionInRootSpace 强制摆战术图标（绕开对端投影差异）+ OnEntityMoved 刷新视觉。
/// </summary>
public sealed class EntitySyncV2 : ISyncedModule
{
    public static EntitySyncV2 Instance { get; } = new EntitySyncV2();

    private EntitySyncV2() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2Entity;

    private const float Interval = 0.5f;
    private const float PosTolerance = 0.05f;
    private float _timer;
    private bool _applying;
    private float _logTimer;

    private sealed class Ent
    {
        public string Id;
        public float X, Y;   // 世界位置（Position.x / Position.z）
        public float LX, LY; // 战术地图图标位置（Location.LocalPosition）——主机权威，图标摆位靠它
        public int State;
        public int Hp;
        public bool SameAs(Ent o) =>
            Mathf.Abs(X - o.X) < PosTolerance && Mathf.Abs(Y - o.Y) < PosTolerance
            && Mathf.Abs(LX - o.LX) < 0.1f && Mathf.Abs(LY - o.LY) < 0.1f
            && State == o.State && Hp == o.Hp;
    }

    private readonly Dictionary<string, Ent> _known = new();   // 客机：上次上行
    private readonly Dictionary<string, Ent> _hknown = new();  // 主机：上次广播

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;

        var list = Collect();
        _logTimer += Interval;
        if (_logTimer >= 5f)
        {
            _logTimer = 0f;
            CoopLog.Info("SyncV2.entityCollect", () => $"[SyncV2] EntitySyncV2 collect={list.Count} known={_known.Count} hknown={_hknown.Count}", 5f);
        }
        if (list.Count == 0) return;

        if (Store.IsHost)
        {
            var changed = new List<Ent>();
            foreach (var e in list)
                if (!_hknown.TryGetValue(e.Id, out var k) || !k.SameAs(e))
                { _hknown[e.Id] = e; changed.Add(e); }
            if (changed.Count > 0) Send(changed);
        }
        else if (!_applying)
        {
            // 聚合发送（n>1）：主机权威——客机只上行变化，主机应用后广播
            var changed = new List<Ent>();
            foreach (var e in list)
                if (!_known.TryGetValue(e.Id, out var k) || !k.SameAs(e))
                { _known[e.Id] = e; changed.Add(e); }
            if (changed.Count > 0) Send(changed);
        }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int n = r.GetByte();
            _applying = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var e = new Ent
                    {
                        Id = r.GetString(), X = r.GetFloat(), Y = r.GetFloat(),
                        LX = r.GetFloat(), LY = r.GetFloat(),
                        State = r.GetByte(), Hp = r.GetInt()
                    };
                    ApplyEntity(e);
                    _known[e.Id] = e;
                    _hknown[e.Id] = e;
                }
            }
            finally { _applying = false; }
            CoopLog.Debug("SyncV2.entityRecv", () => $"[SyncV2] EntitySyncV2 recv n={n} host={Store.IsHost}", 1f);
            // 主机中继：收到客机聚合上行 → 转发其他客机（星型拓扑，合包）
            if (Store.IsHost) _net?.EnqueueBatch(data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[EntitySyncV2] OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _known.Clear(); _hknown.Clear(); _applying = false; }

    // ---------------- 内部 ----------------

    private void Send(List<Ent> list)
    {
        // 会话广播（Host 权威语义下的发送路径）：主机→全员 / 客机→主机上行（聚合 n>1）
        Store.Broadcast((byte)OpenNestCoop.Net.MsgType.V2Entity, w =>
        {
            w.Put((byte)list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                w.Put(e.Id ?? "");
                w.Put(e.X); w.Put(e.Y);
                w.Put(e.LX); w.Put(e.LY);
                w.Put((byte)e.State);
                w.Put(e.Hp);
            }
        }, reliable: false);
        CoopLog.Debug("SyncV2.entitySend", () => $"[SyncV2] EntitySyncV2 send {list.Count}", 1f);
    }

    private static List<Ent> Collect()
    {
        var list = new List<Ent>();
        try
        {
            var fm = FireMission.Instance;
            if (fm == null || fm.Entities == null)
                return CollectFallback();
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
                        if (e == null || string.IsNullOrEmpty(e.ID)) continue;
                        var p = e.Position;
                        Vector2 lp = Vector2.zero;
                        bool lpOk = false;
                        try { var loc = e.Location; if (loc != null) { lp = loc.LocalPosition; lpOk = true; } } catch { }
                        if (!lpOk) { try { lp = fm.ToLocalSpace(p); lpOk = true; } catch { } }
                        list.Add(new Ent
                        {
                            Id = e.ID, X = p.x, Y = p.z,
                            LX = lp.x, LY = lp.y,
                            State = (byte)(int)e.State, Hp = e.Health
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
                list.Add(new Ent
                {
                    Id = e.ID, X = p.x, Y = p.z,
                    LX = lp.x, LY = lp.y,
                    State = (byte)(int)e.State, Hp = e.Health
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
            var fm = FireMission.Instance;
            if (fm == null || fm.Entities == null) return;
            MapEntity ent = null;
            try { if (fm.Entities.ContainsKey(e.Id)) ent = fm.Entities[e.Id]; } catch { }
            if (ent == null) return;
            try
            {
                loc = ent.Location;
                if (loc != null && loc.gameObject != null)
                {
                    var lp = new Vector2(e.LX, e.LY);
                    try { fm.PositionInRootSpace(loc.gameObject, lp); } catch { }
                }
            }
            catch { }
            ent.State = (MapEntityStates)e.State;
            ent.Health = e.Hp;
            try { if (loc != null) loc.OnEntityMoved(); } catch { }
        }
        catch { }
    }
}
