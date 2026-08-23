using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 装填/弹种同步（ReloadSyncV2，MsgType=209/210/214）。M7：把 V1 <c>ReloadSync</c> 迁入分层架构。
/// - 装填状态（209 主机→全员快照 / 210 客机→主机上行）：<see cref="V2Authority.Host"/> 权威——
///   每 0.2s 轮询每门炮 ArtilleryReloadController.currentStateIndex + PowderChargeController.currentSelectedCharges，
///   变化广播；客户端本地变化上行，主机应用后广播。常规广播**不写 stateIndex**（防触发状态机自动推进），
///   装填靠事件驱动（点击/粉末事件）；中途加入（applyState=1）用 SetState(force) 安全对齐。
/// - 粉末事件（选药量/投放发射药，→ EventLayer <see cref="V2Authority.Operator"/>）：谁操作谁发，对端模拟点击
///   Button Dispencer/loadChargesButton（兜底 OnChargeButtonPressed/OnLoadChargesPressed），IsApplyingPowder 防环。
/// - 中途加入：OnLateJoin 场景就绪即发；客机进入炮台场景（ResolveGuns 非空）后一次性请求（214）补发。
/// - 开火请求由 EventLayer 处理（v2/fire），本模块不重复。
/// </summary>
public sealed class ReloadSyncV2 : ISyncedModule
{
    public static ReloadSyncV2 Instance { get; } = new ReloadSyncV2();

    private ReloadSyncV2()
    {
        // 粉末事件 → EventLayer（Operator 权威）
        EventLayer.Instance.Register(PowderEventId, V2Authority.Operator, ReproducePowderEvent);
    }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2ReloadState;

    /// <summary>粉末事件 id（EventLayer 通道）。</summary>
    public const string PowderEventId = "v2/reload/powder";

    /// <summary>应用远端粉末事件期间的防环（Harmony patch 据此不重复上报）。</summary>
    public static bool IsApplyingPowder;

    private const float Interval = 0.2f;
    private float _timer;
    private bool _forceBroadcast;
    private bool _snapshotRequested;
    private int _stateLog;

    private sealed class GunReload
    {
        public int Index;
        public ArtilleryReloadController Reload;
        public PowderChargeController Powder;
        public int HostState = -1, HostCharges = -1;
        public int KnownState = -1, KnownCharges = -1;
        public float LastStateChange;
        public bool Applying;
    }

    private List<GunReload> _guns = new();
    private IntPtr _cachedTurret;
    private readonly List<ulong> _pendingLateJoin = new();

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;

        // 补发中途加入快照（场景就绪后 ResolveGuns 有值）
        if (Store.IsHost && _pendingLateJoin.Count > 0)
        {
            for (int i = _pendingLateJoin.Count - 1; i >= 0; i--)
                if (SendFullStateToNow(_pendingLateJoin[i]))
                    _pendingLateJoin.RemoveAt(i);
        }

        var guns = ResolveGuns();
        if (guns.Count == 0) return;

        // 中途加入：客机进入炮台场景（ResolveGuns 非空）后一次性请求主机补发快照
        if (!Store.IsHost && !_snapshotRequested)
        {
            _snapshotRequested = true;
            try
            {
                var net = _net;
                if (net != null && net.HostSteamId != 0)
                {
                    var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2ReloadSnapshotReq);
                    net.Transport.Send(net.HostSteamId, NetProtocol.Snapshot(w), true);
                    CoopRuntime.LogSource?.LogInfo("[ReloadSyncV2] turret scene ready, requesting reload snapshot resend");
                }
            }
            catch { }
        }

        if (Store.IsHost) HostTick(guns);
        else ClientTick(guns);
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            byte type = r.GetByte();
            if (type == (byte)OpenNestCoop.Net.MsgType.V2ReloadCmd)
            {
                if (Store.IsHost)
                {
                    int idx = r.GetByte();
                    int st = r.GetByte();
                    int ch = r.GetByte();
                    ApplySnapshot(idx, st, ch, false);
                    _forceBroadcast = true;
                }
                return;
            }
            if (type == (byte)OpenNestCoop.Net.MsgType.V2ReloadSnapshotReq)
            {
                if (Store.IsHost && from != 0) SendFullStateToNow(from);
                return;
            }
            // V2ReloadState：状态快照（客户端应用）
            if (Store.IsHost) return;
            int n = r.GetByte();
            int applyState = r.GetByte();
            var guns = ResolveGuns();
            for (int i = 0; i < n; i++)
            {
                int idx = r.GetByte();
                int st = r.GetByte();
                int ch = r.GetByte();
                if (idx < 0 || idx >= guns.Count) continue;
                var g = guns[idx];
                g.Applying = true;
                try { ApplySnapshot(idx, st, ch, applyState != 0); }
                finally { g.Applying = false; }
            }
            if ((++_stateLog % 30) == 1)
                CoopRuntime.LogSource?.LogInfo($"[ReloadSyncV2] recv state n={n} applyState={applyState}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ReloadSyncV2] OnPacket: {ex.Message}"); }
    }

    public void OnLateJoin(ulong steamId)
    {
        if (Store.IsHost && steamId != 0)
        {
            if (!SendFullStateToNow(steamId))
                if (!_pendingLateJoin.Contains(steamId))
                    _pendingLateJoin.Add(steamId);
        }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded()
    {
        _pendingLateJoin.Clear();
        _snapshotRequested = false;
        _guns = new List<GunReload>();
        _cachedTurret = IntPtr.Zero;
    }
    public void Reset()
    {
        _pendingLateJoin.Clear();
        _snapshotRequested = false;
        _guns = new List<GunReload>();
        _cachedTurret = IntPtr.Zero;
    }

    // ---------------- 主机/客机状态 Tick ----------------

    private void HostTick(List<GunReload> guns)
    {
        var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2ReloadState);
        w.Put((byte)guns.Count);
        w.Put((byte)0); // applyState：常规广播（不写 stateIndex，防自动推进）
        bool anyChanged = _forceBroadcast;
        for (int i = 0; i < guns.Count; i++)
        {
            var g = guns[i];
            int st = g.Reload != null ? g.Reload.currentStateIndex : -1;
            int ch = g.Powder != null ? g.Powder.currentSelectedCharges : -1;
            if (st != g.HostState) g.LastStateChange = Time.realtimeSinceStartup;
            w.Put((byte)g.Index);
            w.Put((byte)Math.Max(st, 0));
            w.Put((byte)Math.Max(ch, 0));
            if (st != g.HostState || ch != g.HostCharges) anyChanged = true;
            g.HostState = st;
            g.HostCharges = ch;
        }
        _forceBroadcast = false;
        if (!anyChanged) return;
        var data = NetProtocol.Snapshot(w);
        var net = _net;
        if (net != null) net.EnqueueBatch(data, true);
        if ((++_stateLog % 30) == 1)
            CoopRuntime.LogSource?.LogInfo($"[ReloadSyncV2] host broadcast st/ch guns={guns.Count}");
    }

    private void ClientTick(List<GunReload> guns)
    {
        var net = _net;
        if (net == null) return;
        for (int i = 0; i < guns.Count; i++)
        {
            var g = guns[i];
            if (g.Applying) continue;
            int st = g.Reload != null ? g.Reload.currentStateIndex : -1;
            int ch = g.Powder != null ? g.Powder.currentSelectedCharges : -1;
            if (st != g.KnownState) g.LastStateChange = Time.realtimeSinceStartup;
            if (st == g.KnownState && ch == g.KnownCharges) continue;
            g.KnownState = st;
            g.KnownCharges = ch;
            try
            {
                var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2ReloadCmd);
                w.Put((byte)g.Index);
                w.Put((byte)Math.Max(st, 0));
                w.Put((byte)Math.Max(ch, 0));
                net.EnqueueBatch(NetProtocol.Snapshot(w), false);
                CoopRuntime.LogSource?.LogInfo($"[ReloadSyncV2] client up idx={g.Index} st={st} ch={ch}");
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ReloadSyncV2] ClientTick: {ex.Message}"); }
        }
    }

    /// <summary>真正发送装填状态快照（中途加入）。返回是否成功（场景/炮就绪）。</summary>
    private bool SendFullStateToNow(ulong steamId)
    {
        var net = _net;
        if (net == null || !Store.IsHost || steamId == 0) return false;
        var guns = ResolveGuns();
        if (guns.Count == 0) return false;
        try
        {
            var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2ReloadState);
            w.Put((byte)guns.Count);
            w.Put((byte)1); // applyState：中途加入 SetState(force) 安全对齐
            for (int i = 0; i < guns.Count; i++)
            {
                var g = guns[i];
                int st = g.Reload != null ? g.Reload.currentStateIndex : 0;
                int ch = g.Powder != null ? g.Powder.currentSelectedCharges : 0;
                w.Put((byte)g.Index);
                w.Put((byte)Math.Max(st, 0));
                w.Put((byte)Math.Max(ch, 0));
            }
            net.Transport.Send(steamId, NetProtocol.Snapshot(w), true);
            CoopRuntime.LogSource?.LogInfo($"[ReloadSyncV2] full state → {steamId} guns={guns.Count}");
            return true;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ReloadSyncV2] SendFullStateToNow: {ex.Message}"); }
        return false;
    }

    /// <summary>应用装填快照：同步药量；applyState=true 时 SetState(force) 安全设置 stateIndex（防自动推进）。</summary>
    private static void ApplySnapshot(int idx, int stateIndex, int charges, bool applyState)
    {
        var guns = Instance.ResolveGuns();
        if (idx < 0 || idx >= guns.Count) return;
        var g = guns[idx];
        try
        {
            if (g.Powder != null && g.Powder.currentSelectedCharges != charges)
            {
                try { g.Powder.currentSelectedCharges = charges; }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ReloadSyncV2] ApplySnapshot charges: {ex.Message}"); }
            }
            if (applyState && g.Reload != null)
            {
                try
                {
                    if (g.Reload.CurrentStateIndex != stateIndex)
                    {
                        g.Reload.SetState(stateIndex, true);
                        try { g.Reload.UpdateAllAdvanceButtons(); } catch { }
                        CoopRuntime.LogSource?.LogInfo($"[ReloadSyncV2] mid-join SetState idx={idx} -> {stateIndex}");
                    }
                }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ReloadSyncV2] ApplySnapshot SetState: {ex.Message}"); }
            }
        }
        finally { }
    }

    // ---------------- 粉末事件（→ EventLayer，Operator） ----------------

    /// <summary>本地选发射药量（Harmony PrePowderSelect，V2 分支）→ 广播。</summary>
    public void OnLocalPowderSelect(PowderChargeController powder, int chargeIndex)
    {
        if (IsApplyingPowder || powder == null || !Store.IsOnline) return;
        int idx = IndexOfPowder(powder);
        if (idx < 0) return;
        try
        {
            EventLayer.Instance.Raise(PowderEventId, w =>
            {
                w.Put((byte)idx);
                w.Put((byte)1); // 选药量
                w.Put((byte)Math.Max(chargeIndex, 0));
            });
            CoopRuntime.LogSource?.LogInfo($"[ReloadSyncV2] powder select idx={idx} charge={chargeIndex}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ReloadSyncV2] OnLocalPowderSelect: {ex.Message}"); }
    }

    /// <summary>本地投放发射药（Harmony PrePowderLoad，V2 分支）→ 广播。</summary>
    public void OnLocalPowderLoad(PowderChargeController powder)
    {
        if (IsApplyingPowder || powder == null || !Store.IsOnline) return;
        int idx = IndexOfPowder(powder);
        if (idx < 0) return;
        try
        {
            EventLayer.Instance.Raise(PowderEventId, w =>
            {
                w.Put((byte)idx);
                w.Put((byte)2); // 投放发射药
                w.Put((byte)0);
            });
            CoopRuntime.LogSource?.LogInfo($"[ReloadSyncV2] powder load idx={idx}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ReloadSyncV2] OnLocalPowderLoad: {ex.Message}"); }
    }

    /// <summary>EventLayer 复现：对端模拟点击粉末按钮（动画+逻辑完整复现，防环）。</summary>
    private static void ReproducePowderEvent(NetDataReader r)
    {
        try
        {
            int idx = r.GetByte();
            int ev = r.GetByte();
            int chargeIndex = r.GetByte();
            var guns = Instance.ResolveGuns();
            if (idx < 0 || idx >= guns.Count) return;
            var g = guns[idx];
            if (g.Powder == null) return;
            IsApplyingPowder = true;
            try
            {
                if (ev == 1)
                {
                    var btn = FindDispencerButton(g.Powder, chargeIndex);
                    if (btn != null && btn.isActive) { btn.OnClickDown(); btn.OnClickUp(); }
                    else g.Powder.OnChargeButtonPressed(chargeIndex);
                }
                else if (ev == 2)
                {
                    var btn = g.Powder.loadChargesButton;
                    if (btn != null && btn.isActive) { btn.OnClickDown(); btn.OnClickUp(); }
                    else g.Powder.OnLoadChargesPressed();
                }
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ReloadSyncV2] ReproducePowderEvent: {ex.Message}"); }
            finally { IsApplyingPowder = false; }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ReloadSyncV2] ReproducePowderEvent: {ex.Message}"); }
    }

    private static LookAtTarget FindDispencerButton(PowderChargeController powder, int chargeIndex)
    {
        try
        {
            string want = $"Button Dispencer ({chargeIndex + 1})";
            var pc = powder.transform;
            for (int i = 0; i < pc.childCount; i++)
            {
                var ct = pc.GetChild(i);
                if (ct == null || ct.name == null) continue;
                if (ct.name == want)
                {
                    var lat = ct.GetComponent<LookAtTarget>();
                    if (lat != null) return lat;
                }
            }
        }
        catch { }
        return null;
    }

    private int IndexOfPowder(PowderChargeController powder)
    {
        var guns = ResolveGuns();
        for (int i = 0; i < guns.Count; i++)
            if (guns[i].Powder != null && guns[i].Powder.Pointer == powder.Pointer) return i;
        return -1;
    }

    // ---------------- ResolveGuns（缓存 + 炮实例） ----------------

    private List<GunReload> ResolveGuns()
    {
        var turret = TurretController.Instance;
        if (turret == null)
        {
            _guns = new List<GunReload>();
            _cachedTurret = IntPtr.Zero;
            return _guns;
        }
        if (_guns.Count > 0 && _cachedTurret == turret.Pointer) return _guns;
        _cachedTurret = turret.Pointer;
        _guns = new List<GunReload>();
        try
        {
            var powders = UnityEngine.Resources.FindObjectsOfTypeAll<PowderChargeController>();
            for (int i = 0; i < turret.guns.Count; i++)
            {
                var gun = turret.guns[i];
                if (gun == null) continue;
                var reload = gun.artilleryReloadController;
                if (reload == null) continue;
                PowderChargeController powder = null;
                if (powders != null)
                    foreach (var pc in powders)
                    {
                        if (pc == null || pc.reloadController == null) continue;
                        if (pc.reloadController.Pointer == reload.Pointer) { powder = pc; break; }
                    }
                _guns.Add(new GunReload { Index = i, Reload = reload, Powder = powder });
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ReloadSyncV2] ResolveGuns: {ex.Message}"); }
        return _guns;
    }
}
