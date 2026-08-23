using System;
using System.Collections.Generic;

namespace OpenNestCore.Logging;

/// <summary>日志等级（值越大越严重）。Debug &lt; Info &lt; Warn &lt; Error。</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// 模组统一日志门面：等级过滤 + 按 key 节流。
///
/// 目的（解决 FPS 刷屏与字符串拼接开销）：
/// - **等级过滤**：诊断/调试日志统一降为 <see cref="LogLevel.Debug"/>，默认关闭；
///   关闭时消息用 <c>Func&lt;string&gt;</c> 惰性求值，字符串**根本不拼接**，零开销。
/// - **节流**：按 key 记录上次打印时刻，<paramref name="intervalSec"/> 秒内同 key 只打 1 条，
///   替代各模块分散的 <c>_logTimer % N</c> 计数器，行为可集中调整。
///
/// 后端是平台注入的 <see cref="ILogger"/>（见 <see cref="SetLogSource"/>），平台无关。
/// </summary>
public static class CoopLog
{
    // 编译常量控制发布默认等级：Debug 构建全开诊断，Release 构建默认 Info（关闭 Debug）。
#if DEBUG
    private const LogLevel DefaultLevel = LogLevel.Debug;
#else
    private const LogLevel DefaultLevel = LogLevel.Info;
#endif

    /// <summary>全局日志等级（发布默认 Info；运行时可改，如命令行/调试器）。</summary>
    public static LogLevel Level = DefaultLevel;

    private static ILogger _logSource;
    private static readonly Dictionary<string, long> _lastSent = new();
    private static readonly object _sync = new();

    /// <summary>注入平台日志后端（入口壳启动时调用一次）。传入 null 可禁用日志。</summary>
    public static void SetLogSource(ILogger logger) => _logSource = logger;

    /// <summary>Debug 级（诊断，默认关闭）。</summary>
    public static void Debug(string key, Func<string> message, float intervalSec = 0f)
        => Write(LogLevel.Debug, key, message, intervalSec);

    /// <summary>Info 级（运行摘要，默认开启）。</summary>
    public static void Info(string key, Func<string> message, float intervalSec = 0f)
        => Write(LogLevel.Info, key, message, intervalSec);

    /// <summary>Warn 级。</summary>
    public static void Warn(string key, Func<string> message, float intervalSec = 0f)
        => Write(LogLevel.Warn, key, message, intervalSec);

    /// <summary>Error 级。</summary>
    public static void Error(string key, Func<string> message, float intervalSec = 0f)
        => Write(LogLevel.Error, key, message, intervalSec);

    private static void Write(LogLevel level, string key, Func<string> message, float intervalSec)
    {
        // 等级过滤：低于当前等级直接返回 → Func 不执行，字符串不拼接
        if (level < Level) return;
        if (intervalSec > 0f && !Throttle(key, intervalSec)) return;
        var log = _logSource;
        if (log == null) return;
        string m;
        try { m = message?.Invoke() ?? ""; }
        catch (Exception ex) { m = $"<log-fmt-ex>{ex.Message}</log-fmt-ex>"; }
        switch (level)
        {
            case LogLevel.Debug: log.Debug(m); break;
            case LogLevel.Info: log.Info(m); break;
            case LogLevel.Warn: log.Warn(m); break;
            case LogLevel.Error: log.Error(m); break;
        }
    }

    /// <summary>节流：<paramref name="intervalSec"/> 秒内同 key 只允许打 1 条。</summary>
    private static bool Throttle(string key, float intervalSec)
    {
        long now = Environment.TickCount64;
        long minGap = (long)(intervalSec * 1000);
        lock (_sync)
        {
            if (_lastSent.TryGetValue(key, out long last) && now - last < minGap)
                return false;
            _lastSent[key] = now;
            return true;
        }
    }

    /// <summary>清空节流状态（长时间无活动 / 会话切换时可调用）。</summary>
    public static void Reset()
    {
        lock (_sync) _lastSent.Clear();
    }
}
