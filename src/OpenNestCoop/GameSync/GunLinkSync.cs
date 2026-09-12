using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 仰角联动（GunElevationLink）同步（MsgType=121）。
/// 同步 GunElevationLinkCoordinator.isLinked（仰角锁止/两炮联动开关——`.Elevation Lever Locking Bolt`）。
/// 任意端切换联动 → 广播 → 对端应用（isLinked set，触发游戏联动动画/逻辑）。
/// ⚠️ 2026-08-22 恢复：此前被误删（#if false 死代码）。GunElevationLinkCoordinator 为场景单例，
/// 定位不依赖 Transform 路径（动态实例路径可能不同），FindObjectsOfType 取唯一实例即可。
/// </summary>
public sealed class GunLinkSync : ISyncedModule
{
    public int MsgType => 121;

    // ⚠️ 模块自注册：程序集加载时入队（V1 方案），Startup FlushPending 统一注册
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SelfRegister() => CoopSyncRegistry.PendingRegister(false, () => new GunLinkSync());

    /// <summary>场景单例缓存（GunElevationLinkCoordinator）：场景切换/丢失时刷新，不再周期 FindObjectsOfType。</summary>
    private static GunElevationLinkCoordinator[] _coordCache;
    private static int _cacheScene = -1;
    /// <summary>最后同步的联动状态（场景单例只有一个 coordinator）。变化才广播，防 SetLinked/ToggleLinked 双触发重复。</summary>
    private static bool? _lastLinked;

    private static GunElevationLinkCoordinator GetCoord()
    {
        int sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
        if (_coordCache == null || sc != _cacheScene)
        {
            _cacheScene = sc;
            _coordCache = UnityEngine.Object.FindObjectsOfType<GunElevationLinkCoordinator>();
        }
        return (_coordCache != null && _coordCache.Length > 0) ? _coordCache[0] : null;
    }

    /// <summary>事件驱动：不再 Tick 轮询。SetLinked/ToggleLinked 的 Harmony postfix 调
    /// <see cref="OnLocalSetLinked"/> 广播，收到远端包 OnPacket 应用。</summary>
    public void Tick(float dt) { }

    /// <summary>本地联动状态变化（GunElevationLinkCoordinator.SetLinked/ToggleLinked postfix）→ 变化才广播。
    /// 任意端操作权威：主机广播给全员；客机上报主机（主机中继）。</summary>
    public static void OnLocalSetLinked(GunElevationLinkCoordinator c)
    {
        if (c == null) return;
        var net = CoopRuntime.Net;
        if (net == null) return;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        bool linked;
        try { linked = c.isLinked; } catch { return; }
        // 变化才广播：SetLinked 与 ToggleLinked 可能都触发 postfix（ToggleLinked 内部调 SetLinked）→ 去重
        if (_lastLinked.HasValue && _lastLinked.Value == linked) return;
        _lastLinked = linked;
        Broadcast(c, linked, net);
    }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte();
            bool linked = r.GetByte() == 1;
            if (net.IsHost) net.EnqueueBatch(data, true);
            Apply(linked);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"GunLinkSync OnPacket: {ex.Message}"); }
    }

    private static void Apply(bool linked)
    {
        var c = GetCoord();
        if (c == null) return;
        try
        {
            if (c.isLinked != linked)
            {
                c.isLinked = linked; // set pub，驱动游戏联动状态/动画
                CoopRuntime.LogSource?.LogInfo($"[GunLinkSync] applied linked={linked}");
            }
            _lastLinked = linked; // 更新（防环：下次本地变化检测基于最新已同步状态）
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"GunLinkSync Apply: {ex.Message}"); }
    }

    private static void Broadcast(GunElevationLinkCoordinator c, bool linked, NetManager net)
    {
        var w = NetProtocol.Begin((MsgType)121);
        w.Put(linked ? (byte)1 : (byte)0);
        var data = NetProtocol.Snapshot(w);
        if (net.IsHost) net.EnqueueBatch(data, true);
        else net.EnqueueBatch(data, false);
        CoopRuntime.LogSource?.LogInfo($"[GunLinkSync] send linked={linked} host={net.IsHost}");
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _lastLinked = null; _coordCache = null; _cacheScene = -1; }
}
