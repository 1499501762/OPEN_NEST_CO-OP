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
        public bool SameAs(Ent o) =>
            Mathf.Abs(X - o.X) < PosTolerance && Mathf.Abs(Y - o.Y) < PosTolerance
            && Mathf.Abs(LX - o.LX) < 0.1f && Mathf.Abs(LY - o.LY) < 0.1f
            && State == o.State && Hp == o.Hp;
    }

    private readonly Dictionary<string, Ent> _known = new();
    private readonly Dictionary<string, Ent> _hknown = new();

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;

        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;

        var list = Collect();
        _logTimer += dt;
        if (_logTimer >= 5f)
        {
            _logTimer = 0f;
            CoopRuntime.LogSource?.LogInfo($"[EntitySync] collect={list.Count} known={_known.Count} hknown={_hknown.Count}");
            // 诊断：打印两端实体 ID 列表（定位"后续任务目标（友军敌军）不同步"——known 差 2 的根因）
            try
            {
                string ids = "";
                string pos = "";
                foreach (var e in list)
                {
                    ids += (ids.Length > 0 ? "," : "") + e.Id;
                    pos += (pos.Length > 0 ? "," : "") + $"{e.Id}@({e.X:0.#},{e.Y:0.#})";
                }
                CoopRuntime.LogSource?.LogInfo($"[EntitySync] ids=[{ids}] isHost={net.IsHost}");
                // 位置诊断（战术地图标记位置两端不同）：只打前 8 个避免刷屏
                string[] parts = pos.Split(',');
                string first8 = "";
                for (int i = 0; i < parts.Length && i < 8; i++)
                    first8 += (first8.Length > 0 ? "," : "") + parts[i];
                CoopRuntime.LogSource?.LogInfo($"[EntitySync] pos(first8)=[{first8}] isHost={net.IsHost}");
                // 诊断：战术地图图标位置（EntityLocation.LocalPosition，Vector2）——确认图标是否随 Position 更新
                try
                {
                    string lp = "";
                    int lpCnt = 0;
                    var all = UnityEngine.Resources.FindObjectsOfTypeAll<EntityLocation>();
                    if (all != null)
                        foreach (var el in all)
                        {
                            if (el == null || el.Entity == null || string.IsNullOrEmpty(el.Entity.ID)) continue;
                            if (lpCnt >= 5) break;
                            try
                            {
                                var lp2 = el.LocalPosition;
                                lp += (lp.Length > 0 ? "," : "") + $"{el.Entity.ID}@({lp2.x:0.#},{lp2.y:0.#})";
                                lpCnt++;
                            }
                            catch { }
                        }
                    CoopRuntime.LogSource?.LogInfo($"[EntitySync] localPos(first5)=[{lp}] isHost={net.IsHost}");
                }
                catch { }
            }
            catch { }
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
            foreach (var e in list)
                if (!_known.TryGetValue(e.Id, out var k) || !k.SameAs(e))
                { _known[e.Id] = e; SendToHost(net, e); }
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
                    State = r.GetByte(), Hp = r.GetInt()
                };
                ApplyEntity(e);
                applied.Add(e);
            }
            foreach (var e in applied)
            {
                _known[e.Id] = e;
                _hknown[e.Id] = e;
            }
            CoopRuntime.LogSource?.LogInfo($"[EntitySync] recv n={n} isHost={net.IsHost}");
            if (net.IsHost)
                net.EnqueueBatch(data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"EntitySync OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }

    public void Reset()
    {
        _known.Clear(); _hknown.Clear(); _applying = false;
    }

    // ---------------- 内部 ----------------

    private static List<Ent> Collect()
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
                // 世界位置 = MapEntity.Position（游戏权威）；战术地图图标位置 = EntityLocation.LocalPosition
                // （主机权威，图标实际摆位用本地图坐标，不依赖对端投影算法——两端 canvas 差异导致的
                //  投影不同直接绕开，直接同步主机图标坐标）。
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
                    Id = e.ID,
                    X = p.x,
                    Y = p.z,
                    LX = lp.x,
                    LY = lp.y,
                    State = (byte)(int)e.State,
                    Hp = e.Health
                });
            }
        }
        catch { }
        return list;
    }

    private static void ApplyEntity(Ent e)
    {
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<EntityLocation>();
            if (all == null) return;
            foreach (var el in all)
            {
                if (el == null || el.Entity == null) continue;
                if (el.Entity.ID != e.Id) continue;
                var ent = el.Entity;
                // 同步逻辑位置（MapEntity.Position）
                var pos = ent.Position;
                ent.Position = new Vector3(e.X, pos.y, e.Y);
                try
                {
                    // Synchrony 权威摆位：MoveMapEntity 让游戏知道实体移动 + PositionInRootSpace
                    // 把图标强制摆到主机坐标（绕开对端投影算法差异——战术地图图标两端一致根因）。
                    var fm = FireMission.Instance;
                    if (fm != null)
                    {
                        try { fm.MoveMapEntity(ent, ent.Position, false, 0f); } catch { }
                        var loc = ent.Location;
                        if (loc != null && loc.gameObject != null)
                        {
                            var lp = new Vector2(e.LX, e.LY);
                            try { fm.PositionInRootSpace(loc.gameObject, lp); } catch { }
                        }
                    }
                    else
                    {
                        // 保底：设置 VisualRoot 世界位置 + 缓存投影源字段
                        try
                        {
                            var vr = el.VisualRoot;
                            if (vr != null && vr.transform != null)
                            {
                                var vp = vr.transform.position;
                                vp.x = e.X; vp.z = e.Y;
                                vr.transform.position = vp;
                            }
                        }
                        catch { }
                        try { el._visualRootWorldPosition = new Vector3(e.X, 0f, e.Y); el._hasVisualRootWorldPosition = true; } catch { }
                    }
                }
                catch { }
                ent.State = (MapEntityStates)e.State;
                ent.Health = e.Hp;
                try { el.OnEntityMoved(); }
                catch { }
                return;
            }
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
        }
        var data = NetProtocol.Snapshot(w);
        CoopRuntime.LogSource?.LogInfo($"[EntitySync] broadcast {list.Count}");
        net.EnqueueBatch(data, true);
    }

    private void SendToHost(NetManager net, Ent e)
    {
        var w = NetProtocol.Begin((MsgType)MsgType);
        w.Put((byte)1);
        w.Put(e.Id ?? "");
        w.Put(e.X); w.Put(e.Y);
        w.Put(e.LX); w.Put(e.LY);
        w.Put((byte)e.State);
        w.Put(e.Hp);
        net.EnqueueBatch(NetProtocol.Snapshot(w), false);
    }
}
