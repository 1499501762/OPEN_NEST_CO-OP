// ⚠️ Vendor 源码副本：拷自 src/OpenNestCore/Logging/ModLog.cs，唯一改动 = namespace。见 Vendor/README.md
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenNestUIKit.Logging;

/// <summary>
/// 模组独立文件日志（ModLog）：把高频诊断/联机日志写到**独立 .log 文件**（不附加到 BepInEx/MelonLoader 主日志），
/// 用 StringBuilder 缓冲 + 定时/满额**批量落盘**——降低文件 I/O 次数，且主日志/控制台不再被刷屏（主日志安静 → 帧性能提升）。
///
/// 文件目录：<see cref="Init"/> 时指定（建议游戏目录/OpenNestLogs）；按"类别"分文件：frame.log / net.log / sync.log 等
/// （由 <see cref="CoopLog.RouteToFile"/> 按 key 前缀路由）。惰性：<see cref="Write(string, Func{string})"/> 只在缓冲/落盘时拼接字符串。
/// 驱动：<see cref="Flush(float)"/> 每帧调用（CoopBehaviour.Update），达到 1s 间隔或缓冲满才批量写盘。
/// </summary>
public static class ModLog
{
    private static string _dir = "";
    private static readonly Dictionary<string, StringBuilder> _buf = new();
    private static readonly Dictionary<string, double> _lastFlush = new();
    private const double FlushIntervalSec = 1.0;   // 至少 1s 落盘一次（批量化 I/O）
    private const int MaxBufChars = 64 * 1024;     // 缓冲满强制落盘

    // 时间戳缓存（每 0.2s 刷新一次字符串，避免每行 DateTime.Now 格式化开销）
    private static string _ts = "";
    private static double _tsUpdated = -1;

    /// <summary>是否启用（Init 成功创建目录）。未启用时 Write 为无操作。</summary>
    public static bool Enabled => _dir.Length > 0;

    /// <summary>初始化：创建日志目录 + 清空上次会话的旧日志文件（默认每次启动清空，便于按本次会话读日志）。
    /// 失败则禁用独立文件（回退：路由日志不落盘，主日志也不写）。</summary>
    public static void Init(string dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir)) { _dir = ""; return; }
            Directory.CreateDirectory(dir);
            _dir = dir;
            // ⚠️ 2026-08-30：每次启动清空旧日志（*.log），新会话从头记（上次会话日志不再残留混淆）。
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*.log"))
                {
                    try { File.Delete(f); } catch { }
                }
                _buf.Clear();
                _lastFlush.Clear();
            }
            catch { }
        }
        catch { _dir = ""; }
    }

    /// <summary>写一行到指定类别文件（惰性：只有缓冲/落盘时才调用 msg 拼接）。</summary>
    public static void Write(string file, Func<string> msg)
    {
        if (!Enabled || string.IsNullOrEmpty(file)) return;
        try { WriteRaw(file, msg?.Invoke() ?? ""); }
        catch { }
    }

    /// <summary>写一行到指定类别文件（直接字符串）。</summary>
    public static void Write(string file, string msg)
    {
        if (!Enabled || string.IsNullOrEmpty(file)) return;
        try { WriteRaw(file, msg); }
        catch { }
    }

    private static void WriteRaw(string file, string msg)
    {
        lock (_buf)
        {
            if (!_buf.TryGetValue(file, out var sb)) { sb = new StringBuilder(1024); _buf[file] = sb; }
            sb.Append('[').Append(Timestamp()).Append("] ").Append(msg).Append('\n');
        }
    }

    /// <summary>每帧调用（CoopBehaviour.Update）：达到 1s 间隔或缓冲满才批量落盘（性能：批量化文件 I/O）。</summary>
    public static void Flush(float dt)
    {
        if (!Enabled) return;
        double now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        lock (_buf)
        {
            foreach (var kv in _buf)
            {
                if (kv.Value.Length == 0) continue;
                double last = _lastFlush.TryGetValue(kv.Key, out var l) ? l : 0;
                bool due = (now - last) >= FlushIntervalSec || kv.Value.Length >= MaxBufChars;
                if (!due) continue;
                _lastFlush[kv.Key] = now;
                try
                {
                    var line = kv.Value.ToString();
                    kv.Value.Clear();
                    using (var sw = new StreamWriter(Path.Combine(_dir, kv.Key + ".log"), true, Encoding.UTF8))
                        sw.Write(line);
                }
                catch { }
            }
        }
    }

    /// <summary>缓存时间戳（每 0.2s 刷新一次字符串）。</summary>
    private static string Timestamp()
    {
        double now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        if (_tsUpdated < 0 || now - _tsUpdated > 0.2)
        {
            _ts = DateTime.Now.ToString("HH:mm:ss.fff");
            _tsUpdated = now;
        }
        return _ts;
    }
}
