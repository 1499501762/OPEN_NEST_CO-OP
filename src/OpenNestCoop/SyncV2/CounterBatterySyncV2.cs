using System;
using LiteNetLib.Utils;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 反炮兵落点 seed 同步（CounterBatterySyncV2，MsgType=217）。M7：把 V1 <c>CounterBatterySync</c>（103）
/// 迁入分层架构。<see cref="V2Authority.Host"/>：主机 SpawnOne 前生成递增 seed + InitState + 广播；
/// 客机用最近收到的 seed InitState → 两端一致落点序列（seed 对齐依赖 M3EnvSync 计时同步）。
/// </summary>
public sealed class CounterBatterySyncV2 : ISyncedModule
{
    public static CounterBatterySyncV2 Instance { get; } = new CounterBatterySyncV2();

    private CounterBatterySyncV2() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2CounterBattery;

    private int _seed;
    private int _pendingSeed;
    private bool _havePending;

    /// <summary>SpawnOne 前调用（Harmony PreSpawnOne，V2 分支）：统一随机种子。</summary>
    public void OnLocalSpawn()
    {
        if (!Store.IsOnline) return;
        try
        {
            if (Store.IsHost)
            {
                _seed++;
                UnityEngine.Random.InitState(_seed);
                BroadcastSeed(_seed);
            }
            else if (_havePending)
            {
                UnityEngine.Random.InitState(_pendingSeed);
                _havePending = false;
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CounterBatterySyncV2] OnLocalSpawn: {ex.Message}"); }
    }

    public void Tick(float dt) { /* Harmony patch 事件驱动 */ }

    public void OnPacket(ulong from, byte[] data)
    {
        if (Store.IsHost) return; // 仅客户端处理
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            _pendingSeed = r.GetInt();
            _havePending = true;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CounterBatterySyncV2] OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _seed = 0; _pendingSeed = 0; _havePending = false; }

    private void BroadcastSeed(int seed)
    {
        var net = _net;
        if (net == null) return;
        try
        {
            var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2CounterBattery);
            w.Put(seed);
            var data = NetProtocol.Snapshot(w);
            for (int i = 0; i < net.Roster.Count; i++)
            {
                var p = net.Roster[i];
                if (p != null && !p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CounterBatterySyncV2] BroadcastSeed: {ex.Message}"); }
    }
}
