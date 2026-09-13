using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenNestUIKit.Test;

/// <summary>
/// 测试模组自带的最小日志/报告器（**不依赖 UIKit 的日志**，UIKit 没起来也能记录失败原因）：
/// - <see cref="Info"/>/<see cref="Warn"/> → 平台日志（BepInEx/MelonLoader）；
/// - <see cref="Pass"/>/<see cref="Fail"/> → 计数 + 写报告行；
/// - <see cref="Flush"/> → 把报告写到 <c>&lt;Game&gt;\OpenNestUIKitLogs\test.log</c>（每 1s 批量落盘）。
/// </summary>
public static class TestLog
{
    private static Action<string> _sink;
    private static readonly List<string> _report = new();
    private static readonly StringBuilder _buf = new();
    private static string _path;
    private static int _pass, _fail;

    public static int PassCount => _pass;
    public static int FailCount => _fail;

    /// <summary>注入平台日志后端。</summary>
    public static void Init(Action<string> sink, string logDir)
    {
        _sink = sink;
        try
        {
            if (!string.IsNullOrEmpty(logDir))
            {
                Directory.CreateDirectory(logDir);
                _path = Path.Combine(logDir, "test.log");
                if (File.Exists(_path)) File.Delete(_path);
            }
        }
        catch { }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    public static void Pass(string what, string detail = "")
    {
        _pass++;
        string line = $"[PASS] {what}{(detail.Length > 0 ? " — " + detail : "")}";
        _report.Add(line);
        Write("PASS", line);
    }

    public static void Fail(string what, string detail = "")
    {
        _fail++;
        string line = $"[FAIL] {what}{(detail.Length > 0 ? " — " + detail : "")}";
        _report.Add(line);
        Write("FAIL", line);
    }

    /// <summary>无条件记录一行（脚本/状态 dump）。</summary>
    public static void Note(string what, string detail = "")
    {
        string line = $"[NOTE] {what}{(detail.Length > 0 ? " — " + detail : "")}";
        _report.Add(line);
        Write("NOTE", line);
    }

    public static string Summary() => $"PASS={_pass} FAIL={_fail}";

    /// <summary>每帧驱动（每 1s 落盘一次）。</summary>
    public static void Tick(float dt)
    {
        _tim += dt;
        if (_tim < 1f) return;
        _tim = 0f;
        Flush();
    }

    private static float _tim;

    /// <summary>立即落盘报告。</summary>
    public static void Flush()
    {
        if (_path == null || _buf.Length == 0) return;
        try
        {
            string text;
            lock (_buf) { text = _buf.ToString(); _buf.Clear(); }
            using (var sw = new StreamWriter(_path, true, Encoding.UTF8)) sw.Write(text);
        }
        catch { }
    }

    private static void Write(string level, string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        try { _sink?.Invoke("[" + level + "] " + msg); } catch { }
        lock (_buf) _buf.Append(line).Append('\n');
    }
}
