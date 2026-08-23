using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 事件层（EventLayer，MsgType=200）。纯事件（开火/点击/交互）：事件广播 + 对端复现；不持有状态，只转发。
/// - 通过 <see cref="IHostStore.Broadcast"/> 收发（主机→全员 / 客机→主机中继）。
/// - 权威模型沿用 <see cref="V2Authority"/>：<see cref="V2Authority.Operator"/>（谁触发谁执行 + 广播复现，
///   默认，适用于点击/交互等）与 <see cref="V2Authority.Host"/>（仅主机广播，客机请求上行主机执行，适用于开火）。
/// - 防环（约束#3）：复现远端事件期间置位 <c>_reproducing</c>（事件 id 级），同 id 的本地 Raise 被抑制，
///   避免"本地触发→广播→对端复现→再广播"死循环/重复。
/// - M4 迁移：开火事件（V1 TurretSync GunFire）→ 两个事件 id：
///   "v2/fire/req"（客机→主机开火请求，主机执行）+"v2/fire"（主机→全员开火复现）。
/// </summary>
public sealed class EventLayer : ISyncedModule
{
    public static EventLayer Instance { get; } = new EventLayer();

    private EventLayer()
    {
        // M4 开火事件（V1 TurretSync 迁移）：host 权威——客机请求上行，主机执行后广播复现。
        Register(FireRequestEventId, V2Authority.Host, FireGunFromEvent);
        Register(FireEventId, V2Authority.Host, FireGunFromEvent);
    }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2Event;

    private const string FireEventId = "v2/fire";            // 主机 → 全员：开火复现
    private const string FireRequestEventId = "v2/fire/req"; // 客机 → 主机：开火请求（主机执行）

    /// <summary>开火复现防环标志（V1 ReloadSync.IsApplyingFire 迁移到 V2）：EventLayer 复现时置位，
    /// Harmony PreRequestFire 据此放行网络复现，避免再上行请求形成循环。</summary>
    public static bool IsApplyingFire;

    public delegate void ReproduceHandler(NetDataReader r);

    /// <summary>事件 id → 复现回调（对端/主机收到后调用，reader 已跳过类型+id，指向 payload）。</summary>
    private readonly Dictionary<string, ReproduceHandler> _handlers = new();
    private readonly Dictionary<string, V2Authority> _authority = new();

    /// <summary>防环：正在复现的事件 id 集合（约束#3）。对象/事件级：只抑制同 id 的再广播，不吞其他事件。</summary>
    private readonly HashSet<string> _reproducing = new();

    private int _diagCounter;

    /// <summary>注册事件：id + 权威模型 + 复现回调。</summary>
    public void Register(string id, V2Authority authority, ReproduceHandler reproduce)
    {
        if (string.IsNullOrEmpty(id) || reproduce == null) return;
        _handlers[id] = reproduce;
        _authority[id] = authority;
    }

    /// <summary>
    /// 本地触发（操作者已本地执行效果，或事件已由游戏本地逻辑触发）：
    /// 按权威模型会话广播（Operator / Host 主机）或上行主机（Host 客机请求）。
    /// 防环：复现远端事件期间同 id 调用被抑制。
    /// </summary>
    public void Raise(string id, Action<NetDataWriter> write)
    {
        if (string.IsNullOrEmpty(id) || !Store.IsOnline) return;
        if (_reproducing.Contains(id)) return; // 防环：复现期间不重复广播
        Store.Broadcast((byte)OpenNestCoop.Net.MsgType.V2Event, w =>
        {
            w.Put(id);
            write?.Invoke(w);
        }, reliable: true);
    }

    // ---------------- ISyncedModule ----------------

    public void Tick(float dt)
    {
        // 事件层不持有状态、无周期驱动；仅低频诊断（确认注册表规模/角色）。
        _diagCounter++;
        if (_diagCounter % 250 == 1) // ~每 5s
            CoopLog.Info("SyncV2.event", () => $"[SyncV2] EventLayer handlers={_handlers.Count} host={Store.IsHost} reproducing={_reproducing.Count}", 5f);
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            string id = r.GetString();
            if (!_handlers.TryGetValue(id, out var h)) return;
            var auth = _authority.TryGetValue(id, out var a) ? a : V2Authority.Operator;
            // 复现/执行（防环：同 id 的本地 Raise 在此期间被抑制）：
            //  - Host 权威 + 主机收到客机请求（from!=0）→ 主机执行（handler 触发本地游戏逻辑 + 对应广播复现，如开火）
            //  - 其余 → 复现远端事件（handler 执行同款效果）
            _reproducing.Add(id);
            try { h(r); }
            finally { _reproducing.Remove(id); }
            // 主机中继：Operator 权威事件（如按钮点击）收到客机上行 → 转发给其他客机（星型拓扑，客机间无直连）。
            // Host 权威事件（如开火请求）不中继——主机执行后由对应广播事件（v2/fire）覆盖所有客机，避免重复执行。
            if (auth == V2Authority.Operator && Store.IsHost && from != 0)
            {
                var net = _net;
                if (net != null)
                    for (int i = 0; i < net.Roster.Count; i++)
                    {
                        var p = net.Roster[i];
                        if (p != null && !p.IsLocal && (ulong)p.SteamId != from)
                            net.Transport.Send(p.SteamId, data, true);
                    }
            }
            CoopLog.Debug("SyncV2.eventRecv", () => $"[SyncV2] EventLayer recv id='{id}' auth={auth} from={from}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[EventLayer] OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { }
    public void Reset() { _reproducing.Clear(); }

    // ---------------- 开火事件（M4 迁移，V1 TurretSync） ----------------

    /// <summary>主机本地开火（Harmony PostFireShell，V2 分支）→ 广播开火事件（客机复现）。</summary>
    public void OnLocalShellFired(GunController gun)
    {
        if (!Store.IsHost) return; // 只有主机广播开火（客机由"v2/fire"复现）
        int idx = IndexOfGun(gun);
        if (idx < 0) return;
        Raise(FireEventId, w => w.Put((byte)idx));
    }

    /// <summary>客机本地开火请求（Harmony PreRequestFire，V2 分支）→ 上行主机（主机执行 + 广播复现）。</summary>
    public void OnLocalFireRequest(GunController gun)
    {
        if (Store.IsHost) return; // 主机正常开火（PostFireShell 路径），无需请求
        int idx = IndexOfGun(gun);
        if (idx < 0) return;
        Raise(FireRequestEventId, w => w.Put((byte)idx));
    }

    /// <summary>事件复现：找到炮并 RequestFire（带 IsApplyingFire 防环，放行 PreRequestFire 网络复现路径）。</summary>
    private static void FireGunFromEvent(NetDataReader r)
    {
        try
        {
            int idx = r.GetByte();
            var turret = TurretController.Instance;
            if (turret == null || turret.guns == null) return;
            if (idx < 0 || idx >= turret.guns.Count) return;
            var gun = turret.guns[idx];
            if (gun == null) return;
            // 防环标志（EventLayer.IsApplyingFire，static，从 V1 ReloadSync.IsApplyingFire 迁移）：
            // PreRequestFire 据此放行网络复现，避免再上行请求形成循环
            IsApplyingFire = true;
            try { gun.RequestFire(); }
            finally { IsApplyingFire = false; }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[EventLayer] FireGunFromEvent: {ex.Message}"); }
    }

    private static int IndexOfGun(GunController gun)
    {
        try
        {
            var turret = TurretController.Instance;
            if (turret == null || turret.guns == null || gun == null) return -1;
            for (int i = 0; i < turret.guns.Count; i++)
                if (turret.guns[i] == gun) return i;
        }
        catch { }
        return -1;
    }
}
