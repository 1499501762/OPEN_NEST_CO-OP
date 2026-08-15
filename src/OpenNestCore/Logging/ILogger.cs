namespace OpenNestCore.Logging;

/// <summary>
/// 平台无关日志接口。BepInEx 壳用 ManualLogSource 实现，MelonLoader 壳用 MelonLogger 实现。
/// 核心代码只依赖此接口，不引用任何平台 API。
/// </summary>
public interface ILogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Debug(string message);
}

/// <summary>
/// BepInEx ManualLogSource 风格兼容扩展（LogInfo/LogWarning/LogError/LogDebug），
/// 让核心代码从旧的 Plugin.LogSource?.LogXxx 平滑迁移，无需逐处改写调用点。
/// </summary>
public static class LoggerExtensions
{
    public static void LogInfo(this ILogger logger, string message) => logger?.Info(message);
    public static void LogWarning(this ILogger logger, string message) => logger?.Warn(message);
    public static void LogError(this ILogger logger, string message) => logger?.Error(message);
    public static void LogDebug(this ILogger logger, string message) => logger?.Debug(message);
}
