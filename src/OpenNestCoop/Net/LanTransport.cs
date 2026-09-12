using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using OpenNestCoop.Core;

namespace OpenNestCoop.Net;

/// <summary>
/// **局域网联机传输**（TCP，不经 Steam；2026-09-12 新增，见 `docs/LAN.md`）。
///
/// 与 <see cref="LocalTransport"/>（双开测试用，只支持 1 个客户端、只绑 127.0.0.1）的区别：
///   - 主机监听 `IPAddress.Any`（任意网卡）→ 局域网内其它机器可直连；
///   - 支持**多个**客户端（每个一条 TCP 连接 + 一条读线程）；
///   - peerId 由主机按连接顺序分配（host=1，客户端=2,3,4…），并支持
///     <see cref="RebindPeer"/> —— 客户端在游戏层 Hello 里声明真实身份（SteamID 或 FakeID）后，
///     主机把临时连接 id 重绑到该身份 → 名单/踢人/路由都用这个稳定 id。
///   - 断线事件 <see cref="PeerDisconnected"/>（主机据此把离开者从名单移除；LocalTransport 无此能力）。
///
/// 帧格式与 LocalTransport 一致：`[4B 大端长度][payload]`。TCP 天然有序可靠，reliable 标志忽略（接口兼容）。
/// 线程模型：后台线程只做「读 + 入队」，主线程 <see cref="Poll"/> 取包 + <see cref="Send"/> 发（写有 per-stream 锁）。
/// </summary>
public sealed class LanTransport : ITransport, IDisposable
{
    /// <summary>本端 peerId：主机 = 1；客户端先记 1（主机），真实 id 由 Welcome 告知。</summary>
    public ulong LocalPeerId = 1;

    /// <summary>连接 id 起始值（主机用；1 保留给主机自己）。</summary>
    private const ulong FirstPeerId = 2;

    private sealed class Peer
    {
        public ulong Id;
        public TcpClient Client;
        public NetworkStream Stream;
        public readonly object WriteLock = new();
    }

    private TcpListener _listener;
    private Thread _acceptThread;
    private volatile bool _running;

    /// <summary>主机：peerId → 连接（重绑会改 key）。读写都在 <see cref="_peersLock"/> 内。</summary>
    private readonly Dictionary<ulong, Peer> _peers = new();
    private readonly object _peersLock = new();
    private ulong _nextPeerId = FirstPeerId;

    /// <summary>客户端模式：本端连上主机的那条连接。</summary>
    private Peer _hostPeer;

    /// <summary>接收队列：(来自 peerId, 数据)。</summary>
    private readonly ConcurrentQueue<(ulong, byte[])> _incoming = new();

    /// <summary>主机：新连接接入（尚未 Hello，peerId 是临时连接 id）。</summary>
    public event Action<ulong> PeerConnected;
    /// <summary>主机：连接断开（peerId 是重绑后的最终 id）。⚠️ 在**后台线程**触发——
    /// 订阅方只能置标志/入队，真正处理（名单/UI）必须回主线程。</summary>
    public event Action<ulong> PeerDisconnected;
    /// <summary>客机：与主机的连接断开（后台线程触发，同上）。</summary>
    public event Action HostDisconnected;

    /// <summary>主机监听的端口（0 = 未监听）。</summary>
    public int Port { get; private set; }

    /// <summary>当前连接数（主机：客户端数；客户端：连上主机时 1）。</summary>
    public int PeerCount { get { lock (_peersLock) return _peers.Count + (_hostPeer != null ? 1 : 0); } }

    // ==================== 主机 ====================

    /// <summary>主机：监听任意网卡的 <paramref name="port"/>，接受多个客户端。</summary>
    public bool StartHost(int port)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Port = port;
            LocalPeerId = 1;
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "onc-lan-accept" };
            _acceptThread.Start();
            CoopLog.Info("lan.listen", () => $"[LanTransport] host listening on 0.0.0.0:{port}");
            return true;
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"[LanTransport] StartHost failed (port {port}): {ex.Message}");
            return false;
        }
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client = null;
            try { client = _listener.AcceptTcpClient(); }
            catch { if (!_running) return; continue; }
            try
            {
                client.NoDelay = true;
                var peer = new Peer { Client = client, Stream = client.GetStream() };
                lock (_peersLock)
                {
                    if (_nextPeerId == 0) _nextPeerId = FirstPeerId;   // 极端溢出兜底
                    peer.Id = _nextPeerId++;
                    _peers[peer.Id] = peer;
                }
                var t = new Thread(() => ReadLoop(peer)) { IsBackground = true, Name = "onc-lan-read" };
                t.Start();
                CoopLog.Info("lan.accept", () => $"[LanTransport] client connected as peer {peer.Id} ({client.Client?.RemoteEndPoint})");
                try { PeerConnected?.Invoke(peer.Id); } catch { }
            }
            catch (Exception ex)
            {
                CoopRuntime.LogSource?.LogWarning($"[LanTransport] accept: {ex.Message}");
                try { client?.Close(); } catch { }
            }
        }
    }

    /// <summary>把临时连接 id 重绑到客户端声明的身份（SteamID / FakeID）。
    /// 返回 false = 该身份已被别的连接占用（重复/冒用）→ 调用方应拒绝该客户端。</summary>
    public bool RebindPeer(ulong oldId, ulong newId)
    {
        if (oldId == newId) return true;
        if (newId == 0) return false;
        lock (_peersLock)
        {
            if (!_peers.TryGetValue(oldId, out var peer)) return false;
            if (_peers.ContainsKey(newId)) return false;      // 身份冲突（同一身份重复连接）
            _peers.Remove(oldId);
            peer.Id = newId;
            _peers[newId] = peer;
        }
        CoopLog.Info("lan.rebind", () => $"[LanTransport] peer {oldId} → identity {newId}");
        return true;
    }

    /// <summary>主机：该 peerId 当前是否有连接（派生唯一身份时避开已用 id）。</summary>
    public bool HasPeer(ulong peerId)
    {
        lock (_peersLock) return _peers.ContainsKey(peerId);
    }

    /// <summary>主机：按 peerId 取远端地址（诊断/日志用）。</summary>
    public string RemoteEndPointOf(ulong peerId)
    {
        lock (_peersLock)
        {
            if (_peers.TryGetValue(peerId, out var p))
                try { return p.Client?.Client?.RemoteEndPoint?.ToString() ?? ""; } catch { }
        }
        return "";
    }

    // ==================== 客户端 ====================

    /// <summary>客户端：连接主机（IP/主机名 + 端口）。超时返回 false（可重试）。
    /// ⚠️ **阻塞调用**（等待上限 <paramref name="timeoutMs"/>）——只能在用户点击/节流后的重试里调，不要每帧调。</summary>
    public bool Connect(string host, int port, int timeoutMs = 1500)
    {
        try
        {
            IPAddress addr = null;
            if (!IPAddress.TryParse(host ?? "", out addr))
            {
                // 主机名 → 解析（局域网内也支持写机器名）
                var ips = Dns.GetHostAddresses(host ?? "");
                foreach (var ip in ips)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork) { addr = ip; break; }
                }
                if (addr == null && ips.Length > 0) addr = ips[0];
            }
            if (addr == null) { CoopRuntime.LogSource?.LogWarning($"[LanTransport] cannot resolve host '{host}'"); return false; }

            var client = new TcpClient { NoDelay = true };
            var iar = client.BeginConnect(addr, port, null, null);
            if (!iar.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                try { client.Close(); } catch { }
                CoopRuntime.LogSource?.LogWarning($"[LanTransport] connect timeout {addr}:{port}");
                return false;
            }
            client.EndConnect(iar);
            _hostPeer = new Peer { Id = 1, Client = client, Stream = client.GetStream() };  // 主机 peerId 恒 1
            LocalPeerId = 1;
            _running = true;
            var t = new Thread(() => ReadLoop(_hostPeer)) { IsBackground = true, Name = "onc-lan-read" };
            t.Start();
            CoopLog.Info("lan.connected", () => $"[LanTransport] client connected to {addr}:{port}");
            return true;
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"[LanTransport] Connect {host}:{port} failed: {ex.Message}");
            return false;
        }
    }

    // ==================== 收发 ====================

    private void ReadLoop(Peer peer)
    {
        var lenBuf = new byte[4];
        try
        {
            while (_running && peer.Stream != null)
            {
                if (!ReadExactly(peer.Stream, lenBuf, 4)) break;
                int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                if (len <= 0 || len > 1024 * 1024) break;
                var payload = new byte[len];
                if (!ReadExactly(peer.Stream, payload, len)) break;
                _incoming.Enqueue((peer.Id, payload));
            }
        }
        catch { }
        // 断开：通知主机移除成员（客户端侧只记日志）
        if (!_running) return;
        ulong gone = peer.Id;
        bool wasHost = peer == _hostPeer;
        try
        {
            lock (_peersLock) { if (!wasHost) _peers.Remove(gone); }
        }
        catch { }
        CoopLog.Info("lan.disconnect", () => $"[LanTransport] peer {gone} disconnected");
        if (wasHost) { try { HostDisconnected?.Invoke(); } catch { } }
        else { try { PeerDisconnected?.Invoke(gone); } catch { } }
    }

    private static bool ReadExactly(NetworkStream s, byte[] buf, int count)
    {
        int off = 0;
        while (off < count)
        {
            int n = s.Read(buf, off, count - off);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }

    public void Send(ulong peerId, byte[] data, bool reliable) => Send(peerId, data, data?.Length ?? 0, reliable);

    public void Send(ulong peerId, byte[] data, int len, bool reliable)
    {
        if (data == null || len <= 0) return;
        Peer peer = null;
        lock (_peersLock) { _peers.TryGetValue(peerId, out peer); }
        if (peer == null) peer = _hostPeer != null && peerId == 1 ? _hostPeer : null;
        if (peer == null)
        {
            CoopLog.Warn("lan.sendUnknown", () => $"[LanTransport] Send to unknown peer {peerId} ({len}B) — dropped");
            return;
        }
        try
        {
            var lenBuf = new byte[4];
            lenBuf[0] = (byte)(len >> 24); lenBuf[1] = (byte)(len >> 16);
            lenBuf[2] = (byte)(len >> 8); lenBuf[3] = (byte)len;
            lock (peer.WriteLock)
            {
                peer.Stream.Write(lenBuf, 0, 4);
                peer.Stream.Write(data, 0, len);
                peer.Stream.Flush();
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[LanTransport] Send({peerId}): {ex.Message}"); }
    }

    public bool Poll(out ulong sender, out byte[] data)
    {
        if (_incoming.TryDequeue(out var item))
        {
            sender = item.Item1;
            data = item.Item2;
            return true;
        }
        sender = 0;
        data = Array.Empty<byte>();
        return false;
    }

    public void Dispose()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        lock (_peersLock)
        {
            foreach (var p in _peers.Values)
            {
                try { p.Stream?.Close(); } catch { }
                try { p.Client?.Close(); } catch { }
            }
            _peers.Clear();
        }
        try { _hostPeer?.Stream?.Close(); } catch { }
        try { _hostPeer?.Client?.Close(); } catch { }
        _hostPeer = null;
        Port = 0;
    }

    // ==================== 诊断 ====================

    /// <summary>本机所有 IPv4 地址（主机界面显示，方便告诉队友连哪个）。排除回环/虚拟网卡常见项。</summary>
    public static List<string> LocalIPv4()
    {
        var list = new List<string>();
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ip)) continue;
                list.Add(ip.ToString());
            }
        }
        catch { }
        return list;
    }
}
