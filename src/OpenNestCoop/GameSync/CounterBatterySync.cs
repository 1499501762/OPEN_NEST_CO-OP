using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 反炮兵落点 seed 同步（最小版：主机播种 seed，客户端按相同 seed 复现一致落点）。
/// - 主机：每次 CounterBatteryCinematicImpactSpawner.SpawnOne 前生成新 seed，
///   Random.InitState(seed) 并广播给全员（reliable）。
/// - 客户端：SpawnOne 前用最近收到的 seed InitState —— 游戏内部随机（角度/半径）
///   在所有端产生一致落点序列。
/// 反炮兵计时已由 M3EnvSync 同步，两端 spawner 调度节奏对齐，seed 序列可对齐。
/// </summary>
public sealed class CounterBatterySync : ISyncedModule
{
    public static CounterBatterySync Instance;

    public byte MsgType => 103;

    private int _seed;          // 主机：递增种子
    private int _pendingSeed;   // 客户端：最近收到的种子
    private bool _havePending;
    private int _localSeq;      // 客户端：本地拍照序号（收到新主机 seed 时重置，对齐主机递增）

    public CounterBatterySync() { Instance = this; }

    /// <summary>SpawnOne 前调用（Harmony prefix）：统一随机种子。
    /// ⚠️ 2026-08-15：客户端 seed 可靠性修复（同 ReconPhotoSync）——保留最新 seed + 本地序号递增近似主机递增，
    /// 避免连续触发或 seed 延迟时用本地 Random 导致落点/方向不同。</summary>
    public void OnLocalSpawn()
    {
        var net = CoopRuntime.Net;
        if (net == null || (net.State != SessionState.Hosting && net.State != SessionState.Joined)) return;
        try
        {
            if (net.IsHost)
            {
                _seed++;
                UnityEngine.Random.InitState(_seed);
                CoopRuntime.LogSource?.LogInfo($"[CounterBattery] host seed={_seed} broadcast");
                BroadcastSeed(net, _seed);
            }
            else
            {
                if (_havePending)
                {
                    UnityEngine.Random.InitState(_pendingSeed + _localSeq);
                    CoopRuntime.LogSource?.LogInfo($"[CounterBattery] client seed={_pendingSeed}+{_localSeq}={_pendingSeed + _localSeq}");
                    _localSeq++; // 保留 _havePending：客户端可能连续触发，本地序号递增近似主机递增
                }
                else
                {
                    CoopRuntime.LogSource?.LogInfo("[CounterBattery] client NO PENDING SEED (local random)");
                }
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CounterBatterySync OnLocalSpawn: {ex.Message}"); }
    }

    public void Tick(float dt) { /* 由 Harmony patch 事件驱动 */ }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return; // 仅客户端处理
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            _pendingSeed = r.GetInt();
            _havePending = true;
            _localSeq = 0; // 收到新主机 seed：本地序号重置（对齐主机当前拍照序号）
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CounterBatterySync OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }

    public void Reset()
    {
        _seed = 0; _pendingSeed = 0; _havePending = false; _localSeq = 0;
    }

    private void BroadcastSeed(NetManager net, int seed)
    {
        var w = NetProtocol.Begin((MsgType)MsgType);
        w.Put(seed);
        var data = NetProtocol.Snapshot(w);
        foreach (var p in net.Roster)
            if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
    }
}
