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

    public void Send(ulong steamId, byte[] data, bool reliable)
    {
        Send((CSteamID)steamId, data, reliable);
    }

    public void Send(CSteamID target, byte[] data, bool reliable)
    {
        if (!target.IsValid() || data == null || data.Length == 0) return;
        try
        {
            var buf = new Il2CppStructArray<byte>((long)data.Length);
            for (int i = 0; i < data.Length; i++) buf[i] = data[i];
            var send = reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay;
            bool ok = SteamNetworking.SendP2PPacket(target, buf, (uint)data.Length, send, NetConfig.P2PChannel);
            // 诊断：unreliable 通道单包上限约 1200B，超限会被拒收（整包丢失）
            if (!ok && (++_sendFailLog % 30) == 1)
                CoopRuntime.LogSource?.LogWarning($"P2P 发送被拒 (size={data.Length}B reliable={reliable})");
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"P2P 发送失败 ({target}): {ex.Message}");
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
            CoopRuntime.LogSource?.LogWarning($"P2P 接收出错: {ex.Message}");
            return false;
        }
    }
}
