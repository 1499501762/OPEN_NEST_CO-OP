using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using OpenNestCoop.Core;

namespace OpenNestCoop.Net;

/// <summary>
/// 本地 TCP 回环传输（双开测试用，不经 Steam）。
/// - host：StartHost(port) 监听 127.0.0.1，接受 client 连接，peerId=1。
/// - client：Connect(port) 连 127.0.0.1，peerId=2。
/// - 每条消息：[4B 大端长度][payload]。
/// - 后台线程读 TCP → 入队；Poll 从队列取（主线程）。
/// - TCP 天然可靠（reliable 标志忽略，仅接口兼容）。
/// </summary>
public class LocalTransport : ITransport, IDisposable
{
    private TcpListener _listener;
    private TcpClient _client;
    private NetworkStream _stream;
    private Thread _readThread;
    private volatile bool _running;

    /// <summary>本端 peerId（host=1, client=2）。</summary>
    public ulong LocalPeerId = 1;

    // 接收队列：peerId → 数据
    private readonly ConcurrentQueue<(ulong, byte[])> _incoming = new();

    /// <summary>是否有客户端连上（host 用）。</summary>
    public event Action ClientConnected;

    /// <summary>启动 TCP 服务器（host）。</summary>
    public bool StartHost(int port)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            LocalPeerId = 1;
            _running = true;
            var t = new Thread(AcceptLoop);
            t.IsBackground = true;
            t.Start();
            CoopRuntime.LogSource?.LogInfo($"[LocalTransport] host 监听 127.0.0.1:{port}");
            return true;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[LocalTransport] StartHost 失败: {ex.Message}"); return false; }
    }

    private void AcceptLoop()
    {
        try
        {
            while (_running)
            {
                var client = _listener.AcceptTcpClient();
                client.NoDelay = true;
                // 只接受一个 client（双开测试）
                _client = client;
                _stream = client.GetStream();
                _readThread = new Thread(ReadLoop);
                _readThread.IsBackground = true;
                _readThread.Start();
                CoopRuntime.LogSource?.LogInfo("[LocalTransport] client 已连接");
                try { ClientConnected?.Invoke(); } catch { }
            }
        }
        catch { }
    }

    /// <summary>连接 host（client）。失败返回 false（可重试）。</summary>
    public bool Connect(int port)
    {
        try
        {
            _client = new TcpClient();
            _client.NoDelay = true;
            var iar = _client.BeginConnect(IPAddress.Loopback, port, null, null);
            if (!iar.AsyncWaitHandle.WaitOne(300)) // 300ms 超时，避免 host 未就绪时长时间卡住
            {
                try { _client.Close(); } catch { }
                return false;
            }
            _client.EndConnect(iar);
            _stream = _client.GetStream();
            LocalPeerId = 2;
            _running = true;
            _readThread = new Thread(ReadLoop);
            _readThread.IsBackground = true;
            _readThread.Start();
            CoopRuntime.LogSource?.LogInfo($"[LocalTransport] client 已连接 host 127.0.0.1:{port}");
            return true;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[LocalTransport] Connect 失败: {ex.Message}"); return false; }
    }

    private void ReadLoop()
    {
        try
        {
            var lenBuf = new byte[4];
            while (_running && _stream != null)
            {
                if (!ReadExactly(lenBuf, 4)) break;
                int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                if (len <= 0 || len > 1024 * 1024) break;
                var payload = new byte[len];
                if (!ReadExactly(payload, len)) break;
                _incoming.Enqueue((LocalPeerId == 1 ? 2UL : 1UL, payload)); // 对端 peerId
            }
        }
        catch { }
    }

    private bool ReadExactly(byte[] buf, int count)
    {
        int off = 0;
        while (off < count)
        {
            int n = _stream.Read(buf, off, count - off);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }

    public void Send(ulong peerId, byte[] data, bool reliable)
    {
        if (data == null || data.Length == 0 || _stream == null) return;
        try
        {
            var lenBuf = new byte[4];
            lenBuf[0] = (byte)(data.Length >> 24);
            lenBuf[1] = (byte)(data.Length >> 16);
            lenBuf[2] = (byte)(data.Length >> 8);
            lenBuf[3] = (byte)data.Length;
            lock (_stream)
            {
                _stream.Write(lenBuf, 0, 4);
                _stream.Write(data, 0, data.Length);
                _stream.Flush();
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[LocalTransport] Send: {ex.Message}"); }
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
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
    }
}
