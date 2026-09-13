// ⚠️ Vendor 源码副本：拷自 src/OpenNestCore/Logging/CoopLog.cs，唯一改动 = namespace。见 Vendor/README.md
using System;
using System.Collections.Generic;

namespace OpenNestUIKit.Logging;

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
    /// <summary>独立文件路由：key 前缀 → ModLog 类别文件（frame/net/sync）。匹配的日志写独立文件（不写主日志/控制台，避免刷屏卡帧）。</summary>
    private static readonly List<(string Prefix, string File)> _fileRoutes = new();

    /// <summary>注入平台日志后端（入口壳启动时调用一次）。传入 null 可禁用日志。</summary>
    public static void SetLogSource(ILogger logger) => _logSource = logger;

    /// <summary>注册独立文件路由：key 以 prefix 开头（忽略大小写）的日志 → 写 ModLog 独立文件 file（不写主日志）。
    /// 诊断/联机高频日志用独立文件（frame.log/net.log/sync.log），主日志只保留会话/错误——控制台不刷屏 → 帧性能提升。</summary>
    public static void RouteToFile(string keyPrefix, string file)
    {
        lock (_fileRoutes) _fileRoutes.Add((keyPrefix, file));
    }

    /// <summary>查 key 是否命中独立文件路由（返回文件名；null = 走主日志）。</summary>
    private static string MatchFile(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        lock (_fileRoutes)
            foreach (var (p, f) in _fileRoutes)
                if (key.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

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
        // ⚠️ 独立文件路由：匹配的 key → ModLog 独立文件（不受全局 Level 限制——诊断文件总是记录；不写主日志/控制台）
        string file = MatchFile(key);
        if (file != null)
        {
            if (intervalSec > 0f && !Throttle(key, intervalSec)) return;
            ModLog.Write(file, message); // 惰性：ModLog 未启用时不拼接（零开销）
            return;
        }
        // 主日志：等级过滤（低于当前等级直接返回 → Func 不执行，字符串不拼接）
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
