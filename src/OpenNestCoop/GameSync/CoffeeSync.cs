using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 咖啡机同步——用 ISyncedModule 自定义模块接入框架的参考实现。
/// 同步 EspressoBrewingController 的 BrewState（冲煮状态机）。
/// 机制：主机权威状态同步——客户端本地状态变化上行 → 主机应用 → 广播；防环。
/// 更简单的设备值（药包/表盘等）可用 CoopSyncRegistry.RegisterInt/Float/Bool 直接注册。
///
/// ⚠️ 自注册参考（NetManager 注册管理器）：前导字节不再硬编码（旧 100），改为稳定 channelKey 自注册，
/// 由 NetManager 统一分配空闲前导字节（见 CoopRuntime.RegisterLegacyModules 中
/// CoopSyncRegistry.RegisterDynamicChannel(new CoffeeSync(), CoffeeSync.ChannelKey)）。
/// 发送路径经 <see cref="MsgType"/>（= CoopRuntime.Net.ChannelType(ChannelKey)）取分配字节。
/// </summary>
public sealed class CoffeeSync : ISyncedModule
{
    /// <summary>自注册通道键（稳定字符串，双端一致；前导字节由 NetManager 注册管理器分配）。</summary>
    public const string ChannelKey = "coffee";

    /// <summary>本模块前导字节：由注册管理器为 ChannelKey 分配（动态注册）。</summary>
    public int MsgType => OpenNestCoop.Core.CoopRuntime.Net?.ChannelType(ChannelKey) ?? 0;

    // ⚠️ 模块自注册：程序集加载时入队（V1 方案，动态通道），Startup FlushPending 统一注册
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SelfRegister() => CoopSyncRegistry.PendingRegister(false, () => new CoffeeSync(), ChannelKey);

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
            CoopRuntime.LogSource?.LogWarning("CoffeeSync: EspressoBrewingController not found (active after entering coffee scene)");
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
