using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 仰角联动/锁定插销同步（GunLinkSyncV2，MsgType=229）。M7：把 V1 <c>GunLinkSync</c>（121）迁入分层架构。
/// 同步 <c>GunElevationLinkCoordinator.isLinked</c>（仰角锁止/两炮联动开关注册状态）。
/// 任意端切换联动 → 广播（Operator 权威，主机中继）→ 对端应用（isLinked set，驱动游戏联动状态/动画）。
/// 路径作为跨端标识（两端同场景同名）。
/// </summary>
public sealed class GunLinkSyncV2 : ISyncedModule
{
    public static GunLinkSyncV2 Instance { get; } = new GunLinkSyncV2();

    private GunLinkSyncV2() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2GunLink;

    private const float Interval = 0.3f;
    private float _timer;
    private int _log;
    private readonly Dictionary<GunElevationLinkCoordinator, bool> _known = new();

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        try
        {
            var coords = UnityEngine.Object.FindObjectsOfType<GunElevationLinkCoordinator>();
            if (coords == null || coords.Length == 0) return;
            foreach (var c in coords)
            {
                if (c == null) continue;
                bool linked;
                try { linked = c.isLinked; } catch { continue; }
                if (_known.TryGetValue(c, out var last) && last == linked) continue;
                _known[c] = linked;
                Broadcast(c, linked);
            }
            if ((++_log % 20) == 1)
                CoopRuntime.LogSource?.LogInfo($"[GunLinkSyncV2] scan coords={coords.Length}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[GunLinkSyncV2] Tick: {ex.Message}"); }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            string path = r.GetString();
            bool linked = r.GetByte() == 1;
            // 主机中继：客机上行的联动状态转发给其他客机（星型拓扑）
            if (Store.IsHost && from != 0)
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
            Apply(path, linked);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[GunLinkSyncV2] OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _timer = 0f; _known.Clear(); }

    private void Apply(string path, bool linked)
    {
        try
        {
            var coords = UnityEngine.Object.FindObjectsOfType<GunElevationLinkCoordinator>();
            if (coords == null) return;
            foreach (var c in coords)
            {
                if (c == null || PathOf(c.transform) != path) continue;
                try
                {
                    if (c.isLinked != linked)
                    {
                        c.isLinked = linked; // set pub，驱动游戏联动状态/动画
                        CoopRuntime.LogSource?.LogInfo($"[GunLinkSyncV2] applied '{path}' linked={linked}");
                    }
                }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[GunLinkSyncV2] Apply: {ex.Message}"); }
                // 防环：应用后更新本地已知，避免下轮 Tick 把刚应用的状态当“本地变化”回广播
                _known[c] = linked;
                break;
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[GunLinkSyncV2] Apply: {ex.Message}"); }
    }

    private void Broadcast(GunElevationLinkCoordinator c, bool linked)
    {
        string path = PathOf(c.transform);
        Store.Broadcast((byte)OpenNestCoop.Net.MsgType.V2GunLink, w =>
        {
            w.Put(path);
            w.Put(linked ? (byte)1 : (byte)0);
        }, reliable: true);
        CoopRuntime.LogSource?.LogInfo($"[GunLinkSyncV2] send '{path}' linked={linked} host={Store.IsHost}");
    }

    private static string PathOf(Transform t)
    {
        if (t == null) return "";
        string path = t.name ?? "";
        var p = t.parent;
        while (p != null) { path = (p.name ?? "") + "/" + path; p = p.parent; }
        return path;
    }
}
