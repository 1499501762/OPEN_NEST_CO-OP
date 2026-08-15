using System;
using LiteNetLib.Utils;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 咖啡机同步（CoffeeSyncV2，MsgType=216）。M7：把 V1 <c>CoffeeSync</c>（100）迁入分层架构。
/// <see cref="V2Authority.Host"/>：客户端本地冲煮状态变化上行 → 主机应用 → 广播；_applying 防环。
/// 更简单的设备值可用 ValueLayer.RegisterInt 注册（本例保持独立模块直接迁移）。
/// </summary>
public sealed class CoffeeSyncV2 : ISyncedModule
{
    public static CoffeeSyncV2 Instance { get; } = new CoffeeSyncV2();

    private CoffeeSyncV2() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2Coffee;

    private const float Interval = 0.2f;
    private float _timer;
    private EspressoBrewingController _brew;
    private bool _resolved;
    private bool _applying;
    private bool _known; private int _knownState;
    private bool _hknown; private int _hState;

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;

        var b = Resolve();
        if (b == null) return;
        int st = (byte)b.currentState;

        if (Store.IsHost)
        {
            if (_hknown && st == _hState) return;
            _hknown = true; _hState = st;
            Send(st);
        }
        else if (!_applying)
        {
            if (_known && st == _knownState) return;
            _known = true; _knownState = st;
            Send(st);
        }
    }

    public void OnPacket(ulong from, byte[] data)
    {
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
            if (Store.IsHost) _net?.EnqueueBatch(data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CoffeeSyncV2] OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset()
    {
        _known = false; _hknown = false; _applying = false;
        _resolved = false; _brew = null;
    }

    private void Send(int st)
    {
        var net = _net;
        if (net == null) return;
        try
        {
            var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2Coffee);
            w.Put((byte)st);
            var data = NetProtocol.Snapshot(w);
            net.EnqueueBatch(data, Store.IsHost);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CoffeeSyncV2] Send: {ex.Message}"); }
    }

    private EspressoBrewingController Resolve()
    {
        if (_resolved) return _brew;
        _resolved = true;
        try { _brew = UnityEngine.Object.FindFirstObjectByType<EspressoBrewingController>(); }
        catch { _brew = null; }
        if (_brew == null)
            CoopRuntime.LogSource?.LogWarning("[CoffeeSyncV2] EspressoBrewingController not found (active after entering coffee scene)");
        return _brew;
    }
}
