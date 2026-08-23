using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OpenNestCoop.Core;
#if !MELONLOADER
using Steamworks;
#else
using Steamworks = Il2CppSteamworks;
#endif

namespace OpenNestCoop.Net;

/// <summary>
/// 基于 Steam 经典 P2P API（SteamNetworking.SendP2PPacket / ReadP2PPacket）的传输层。
/// 免费获得 Steam 中继打洞；可靠/不可靠通道；无需管理连接生命周期。
/// </summary>
public class SteamTransport : ITransport
{
    private readonly HashSet<ulong> _accepted = new();
    private int _sendFailLog;
    // A1: 发送缓冲池化。SendP2PPacket 同步拷贝数据（函数返回前已拷入 Steam 内部缓冲），
    // 同一 buf 可连续发多个 peer 后复用，省每包 new Il2CppStructArray。单线程发送路径无并发。
    private Il2CppStructArray<byte> _sendBuf;

    public void Send(ulong steamId, byte[] data, bool reliable)
    {
        Send((CSteamID)steamId, data, data?.Length ?? 0, reliable);
    }

    public void Send(ulong steamId, byte[] data, int len, bool reliable)
    {
        Send((CSteamID)steamId, data, len, reliable);
    }

    public void Send(CSteamID target, byte[] data, bool reliable)
    {
        Send(target, data, data?.Length ?? 0, reliable);
    }

    public void Send(CSteamID target, byte[] data, int len, bool reliable)
    {
        if (!target.IsValid() || data == null || len <= 0) return;
        try
        {
            if (_sendBuf == null || _sendBuf.Length < len)
                _sendBuf = new Il2CppStructArray<byte>((long)len);
            for (int i = 0; i < len; i++) _sendBuf[i] = data[i];
            var send = reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay;
            bool ok = SteamNetworking.SendP2PPacket(target, _sendBuf, (uint)len, send, NetConfig.P2PChannel);
            // 诊断：unreliable 通道单包上限约 1200B，超限会被拒收（整包丢失）
            if (!ok && (++_sendFailLog % 30) == 1)
                CoopRuntime.LogSource?.LogWarning($"P2P send rejected (size={len}B reliable={reliable})");
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"P2P send failed ({target}): {ex.Message}");
        }
    }

    /// <summary>取出一个待处理包。队列为空返回 false。</summary>
    public bool Poll(out ulong sender, out byte[] data)
    {
        data = Array.Empty<byte>();
        sender = 0;
        try
        {
            if (!SteamNetworking.IsP2PPacketAvailable(out uint size, NetConfig.P2PChannel))
                return false;

            var buf = new Il2CppStructArray<byte>((long)size);
            if (!SteamNetworking.ReadP2PPacket(buf, size, out uint read, out CSteamID from, NetConfig.P2PChannel))
                return false;

            var sid = (ulong)from;
            if (!_accepted.Contains(sid))
            {
                SteamNetworking.AcceptP2PSessionWithUser(from);
                _accepted.Add(sid);
            }

            var managed = new byte[read];
            for (int i = 0; i < read; i++) managed[i] = buf[i];
            sender = sid;
            data = managed;
            return true;
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"P2P receive error: {ex.Message}");
            return false;
        }
    }
}
