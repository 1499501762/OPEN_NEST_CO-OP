#if false // ⚠️ V1 死代码：GunLinkSync 从未被注册（RegisterLegacyModules 无 new；MsgType=121 无收发方）。
           //   仰角联动已由 V2 GunLinkSyncV2（MsgType=229）替代。整类注释保留参考，删除文件需连本 #if 一起删。
using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 仰角联动（GunElevationLink）同步（MsgType=121）。
/// 同步 GunElevationLinkCoordinator.isLinked（仰角锁止/两炮联动开关注册状态）。
/// 任意端切换联动 → 广播 → 对端应用（isLinked set，触发游戏联动动画/逻辑）。
/// </summary>
public sealed class GunLinkSync : ISyncedModule
{
    public byte MsgType => 121;
    private const float Interval = 0.3f;
    private float _timer;
    private int _log;
    private readonly Dictionary<GunElevationLinkCoordinator, bool> _known = new();

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
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
                Broadcast(c, linked, net);
            }
            if ((++_log % 20) == 1)
                CoopRuntime.LogSource?.LogInfo($"[GunLinkSync] scan coords={coords.Length}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"GunLinkSync Tick: {ex.Message}"); }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte();
            string path = r.GetString();
            bool linked = r.GetByte() == 1;
            if (net.IsHost) net.EnqueueBatch(data, true);
            Apply(path, linked);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"GunLinkSync OnPacket: {ex.Message}"); }
    }

    private static void Apply(string path, bool linked)
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
                    CoopRuntime.LogSource?.LogInfo($"[GunLinkSync] applied '{path}' linked={linked}");
                }
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"GunLinkSync Apply: {ex.Message}"); }
            break;
        }
    }

    private static void Broadcast(GunElevationLinkCoordinator c, bool linked, NetManager net)
    {
        string path = PathOf(c.transform);
        var w = NetProtocol.Begin((MsgType)121);
        w.Put(path);
        w.Put(linked ? (byte)1 : (byte)0);
        var data = NetProtocol.Snapshot(w);
        if (net.IsHost) net.EnqueueBatch(data, true);
        else net.EnqueueBatch(data, false);
        CoopRuntime.LogSource?.LogInfo($"[GunLinkSync] send '{path}' linked={linked} host={net.IsHost}");
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
    public void Reset() { _timer = 0f; _known.Clear(); }
}
#endif // !GunLinkSync 死代码
