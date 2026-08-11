using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 咖啡机同步——用 ISyncedModule 自定义模块接入框架的参考实现（MsgType=100）。
/// 同步 EspressoBrewingController 的 BrewState（冲煮状态机）。
/// 机制：主机权威状态同步——客户端本地状态变化上行 → 主机应用 → 广播；防环。
/// 更简单的设备值（药包/表盘等）可用 CoopSyncRegistry.RegisterInt/Float/Bool 直接注册。
/// </summary>
public sealed class CoffeeSync : ISyncedModule
{
    public byte MsgType => 100;

    private const float Interval = 0.2f;
    private float _timer;
    private EspressoBrewingController _brew;
    private bool _resolved;
    private bool _applying;
    private bool _known; private int _knownState; // 客户端本地已知
    private bool _hknown; private int _hState;    // 主机已知

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;

        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;

        var b = Resolve();
        if (b == null) return;
        int st = (byte)b.currentState;

        if (net.IsHost)
        {
            if (_hknown && st == _hState) return;
            _hknown = true; _hState = st;
            Broadcast(net, st);
        }
        else if (!_applying)
        {
            if (_known && st == _knownState) return;
            _known = true; _knownState = st;
            SendToHost(net, st);
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
            int st = r.GetByte();
            var b = Resolve();
            if (b == null) return;
            _applying = true;
            try { b.SetState((EspressoBrewingController.BrewState)st); }
            finally { _applying = false; }
            _known = true; _knownState = st;
            _hknown = true; _hState = st;
            if (net.IsHost)
                net.EnqueueBatch(data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CoffeeSync OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }

    public void Reset()
    {
        _known = false; _hknown = false; _applying = false;
        _resolved = false; _brew = null;
    }

    private EspressoBrewingController Resolve()
    {
        if (_resolved) return _brew;
        _resolved = true;
        try { _brew = UnityEngine.Object.FindFirstObjectByType<EspressoBrewingController>(); }
        catch { _brew = null; }
        if (_brew == null)
            CoopRuntime.LogSource?.LogWarning("CoffeeSync: 未找到 EspressoBrewingController（进入咖啡场景后生效）");
        return _brew;
    }

    private void Broadcast(NetManager net, int st)
    {
        var w = NetProtocol.Begin((MsgType)MsgType);
        w.Put((byte)st);
        var data = NetProtocol.Snapshot(w);
        net.EnqueueBatch(data, true);
    }

    private void SendToHost(NetManager net, int st)
    {
        var w = NetProtocol.Begin((MsgType)MsgType);
        w.Put((byte)st);
        net.EnqueueBatch(NetProtocol.Snapshot(w), false);
    }
}
