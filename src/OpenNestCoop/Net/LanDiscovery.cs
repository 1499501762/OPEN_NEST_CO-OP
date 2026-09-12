using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using OpenNestCoop.Core;

namespace OpenNestCoop.Net;

/// <summary>
/// **局域网房间发现**（UDP 广播，2026-09-12 新增，见 `docs/LAN.md`）。
///
/// 协议（纯文本、一行，无依赖）：
///   查询：`ONCQ`（客户端 → 广播地址:<c>port</c>）
///   应答：`ONCR|房间名|人数|上限|模组版本|密码hash|TCP端口`（主机 → 单播回查询方）
///
/// 主机与客户端都用 <see cref="CoopConfig.LanPort"/>（可在配置里改）。发现是**尽力而为**：
/// 广播被防火墙拦掉也只影响“列表里看不到”，手动填 IP 直连照样能用。
/// 所有异常都吞掉（局域网发现绝不能影响联机本身）。
/// </summary>
public static class LanDiscovery
{
    /// <summary>发现到的局域网房间。</summary>
    public sealed class LanRoom
    {
        public string Host = "";        // 主机 IP（回给列表用于直连）
        public int Port;                // 主机 TCP 端口
        public string Name = "";
        public int Players;
        public int MaxPlayers;
        public string Version = "";
        public string PasswordHash = "";
        public float SeenAt;            // Time.realtimeSinceStartup（过期清理用）
        public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);
    }

    private const string Query = "ONCQ";
    private const string ReplyPrefix = "ONCR|";
    private const string Sep = "|";
    /// <summary>房间条目有效期（秒）：超过则从列表移除（主机关了/网络断了）。</summary>
    private const float EntryTtl = 10f;

    private static readonly List<LanRoom> _rooms = new();
    private static readonly object _lock = new();
    private static UdpClient _announce;          // 主机应答器
    private static Thread _announceThread;
    private static volatile bool _announceRunning;
    private static volatile bool _scanning;
    private static int _announcePort;

    /// <summary>当前发现到的房间（快照，UI 用；已过期条目由 <see cref="Tick"/> 清理）。</summary>
    public static List<LanRoom> Rooms { get { lock (_lock) return new List<LanRoom>(_rooms); } }

    /// <summary>是否正在扫描。</summary>
    public static bool Scanning => _scanning;

    // ==================== 主机：广播应答 ====================

    /// <summary>主机开始应答局域网查询（建房时调用）。<paramref name="info"/> 返回当前房间信息。</summary>
    public static void StartAnnounce(int port, Func<(string name, int players, int max, string pwdHash)> info)
    {
        StopAnnounce();
        try
        {
            _announce = new UdpClient();
            _announce.EnableBroadcast = true;
            _announce.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _announce.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _announcePort = port;
            _announceRunning = true;
            _announceThread = new Thread(() => AnnounceLoop(info)) { IsBackground = true, Name = "onc-lan-announce" };
            _announceThread.Start();
            CoopLog.Info("lan.announce", () => $"[LanDiscovery] announce on UDP {port}");
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"[LanDiscovery] StartAnnounce failed (port {port}): {ex.Message}");
        }
    }

    private static void AnnounceLoop(Func<(string name, int players, int max, string pwdHash)> info)
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (_announceRunning)
        {
            try
            {
                var data = _announce.Receive(ref remote);
                if (data == null || data.Length == 0) continue;
                string q = Encoding.UTF8.GetString(data).Trim();
                if (!q.Equals(Query, StringComparison.Ordinal)) continue;
                var (name, players, max, pwdHash) = info != null ? info() : ("", 0, 0, "");
                string reply = ReplyPrefix
                    + Sanitize(name) + Sep
                    + players + Sep + max + Sep
                    + NetConfig.Version + Sep
                    + Sanitize(pwdHash) + Sep
                    + _announcePort;
                var bytes = Encoding.UTF8.GetBytes(reply);
                try { _announce.Send(bytes, bytes.Length, remote); } catch { }
            }
            catch { if (!_announceRunning) return; }
        }
    }

    /// <summary>停止应答（离开会话/关停时调用）。</summary>
    public static void StopAnnounce()
    {
        _announceRunning = false;
        try { _announce?.Close(); } catch { }
        _announce = null;
        _announceThread = null;
    }

    // ==================== 客户端：扫描 ====================

    /// <summary>扫描局域网房间（后台线程，不阻塞 UI）。已有扫描在跑 → 忽略本次请求。</summary>
    public static void Scan(int port, int timeoutMs = 900) => Scan(new[] { port }, timeoutMs);

    /// <summary>多端口扫描（同时探当前端口 + 默认端口：主机改了端口也能被发现）。</summary>
    public static void Scan(int[] ports, int timeoutMs = 900)
    {
        if (_scanning) return;
        if (ports == null || ports.Length == 0) return;
        _scanning = true;
        var t = new Thread(() => ScanLoop(ports, timeoutMs)) { IsBackground = true, Name = "onc-lan-scan" };
        t.Start();
    }

    private static void ScanLoop(int[] ports, int timeoutMs)
    {
        UdpClient udp = null;
        try
        {
            udp = new UdpClient();
            udp.EnableBroadcast = true;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));   // 随机本地端口收应答
            udp.Client.ReceiveTimeout = 200;

            var bytes = Encoding.UTF8.GetBytes(Query);
            // 广播地址 + 本机回环（同一台机器双开时 255.255.255.255 未必回环到自己）
            foreach (var port in ports)
            {
                if (port <= 0) continue;
                try { udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, port)); } catch { }
                try { udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port)); } catch { }
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(200, timeoutMs));
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var data = udp.Receive(ref remote);
                    if (data == null) continue;
                    string s = Encoding.UTF8.GetString(data);
                    if (!s.StartsWith(ReplyPrefix, StringComparison.Ordinal)) continue;
                    var parts = s.Substring(ReplyPrefix.Length).Split('|');
                    if (parts.Length < 6) continue;
                    var room = new LanRoom
                    {
                        Host = remote.Address.ToString(),
                        Name = parts[0],
                        Players = ParseInt(parts[1]),
                        MaxPlayers = ParseInt(parts[2]),
                        Version = parts[3],
                        PasswordHash = parts[4],
                        Port = ParseInt(parts[5]),
                        SeenAt = UnityEngine.Time.realtimeSinceStartup,
                    };
                    if (room.Port <= 0) room.Port = ports[0];
                    Upsert(room);
                }
                catch (SocketException) { /* 200ms 超时：继续等到 deadline */ }
                catch { break; }
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[LanDiscovery] scan failed: {ex.Message}"); }
        finally
        {
            try { udp?.Close(); } catch { }
            _scanning = false;
        }
    }

    private static void Upsert(LanRoom room)
    {
        lock (_lock)
        {
            for (int i = 0; i < _rooms.Count; i++)
            {
                if (_rooms[i].Host == room.Host && _rooms[i].Port == room.Port)
                {
                    _rooms[i] = room;
                    return;
                }
            }
            _rooms.Add(room);
            CoopLog.Info("lan.found", () => $"[LanDiscovery] found room '{room.Name}' at {room.Host}:{room.Port} ({room.Players}/{room.MaxPlayers}) v{room.Version}");
        }
    }

    /// <summary>过期清理（UI 每帧/定期调用）：太久没应答的房间从列表移除。</summary>
    public static void Tick()
    {
        float now;
        try { now = UnityEngine.Time.realtimeSinceStartup; } catch { return; }
        lock (_lock)
        {
            for (int i = _rooms.Count - 1; i >= 0; i--)
                if (now - _rooms[i].SeenAt > EntryTtl) _rooms.RemoveAt(i);
        }
    }

    /// <summary>清空列表（关闭面板/离开时）。</summary>
    public static void Clear() { lock (_lock) _rooms.Clear(); }

    private static int ParseInt(string s) => int.TryParse(s, out var v) ? v : 0;

    /// <summary>去掉分隔符与换行，防止应答串被注入破坏解析。</summary>
    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("|", "/").Replace("\r", "").Replace("\n", "").Trim();
    }
}
