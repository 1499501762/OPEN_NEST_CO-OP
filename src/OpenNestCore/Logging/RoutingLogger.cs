using System;
using System.Collections.Generic;

namespace OpenNestCore.Logging;

/// <summary>
/// 自动路由日志器（RoutingLogger）：包装底层平台 ILogger，按**消息前缀**把同步/联机模块日志
/// 自动路由到独立文件（ModLog sync.log），主日志（BepInEx/MelonLoader 控制台）不刷屏 → 帧性能提升。
///
/// ⚠️ 2026-08-26：同步模块还有大量 `CoopRuntime.LogSource?.LogInfo("[XSync] ...")` 直调**绕过 CoopLog 的
/// key 路由**（CoopLog.RouteToFile 只对走 CoopLog.Info/Debug(key,...) 的日志生效；直调 LogSource 会进主日志）。
/// 用本包装器统一兜底：所有直调日志按消息开头的 `[模块名]` 前缀匹配 sync 前缀表，命中 → 写独立文件；
/// 未命中（会话/错误）→ 走底层主日志。
///
/// 使用：入口壳 Initialize 时 `LogSource = new RoutingLogger(platformLogger)`（CoopLog.SetLogSource 用原始 logger，
/// CoopLog 自己的 key 路由不受影响）。ModLog 未启用时 fallback 到底层（不丢日志）。
/// </summary>
public sealed class RoutingLogger : ILogger
{
    private readonly ILogger _inner;
    /// <summary>路由表：模块前缀 → 独立文件名（与 CoopLog.RouteToFile 的 sync 前缀一致，共享注册）。</summary>
    private static readonly List<(string Prefix, string File)> _routes = new();

    /// <summary>注册消息前缀路由（由 CoopRuntime.InitFileLogs 调用；与 CoopLog.RouteToFile 同源）。
    /// 前缀匹配消息开头 `[前缀]`（忽略大小写、可选首方括号）。</summary>
    public static void RouteToFile(string prefix, string file)
    {
        if (string.IsNullOrEmpty(prefix)) return;
        lock (_routes) _routes.Add((prefix, file));
    }

    public RoutingLogger(ILogger inner) { _inner = inner; }

    public void Info(string message) => Route(message, m => _inner?.Info(m));
    public void Warn(string message) => Route(message, m => _inner?.Warn(m));
    public void Debug(string message) => Route(message, m => _inner?.Debug(m));
    public void Error(string message) => Route(message, m => _inner?.Error(m));

    private void Route(string message, Action<string> fallback)
    {
        // ModLog 启用时按前缀路由到独立文件；否则 fallback 到底层（不丢日志）
        string file = ModLog.Enabled ? MatchFile(message) : null;
        if (file != null) { ModLog.Write(file, message); return; }
        try { fallback?.Invoke(message ?? ""); } catch { }
    }

    /// <summary>匹配消息开头的 `[前缀]`（或裸前缀）→ 返回独立文件名；null = 走主日志。</summary>
    private static string MatchFile(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        string head = message.TrimStart();
        // 取首段（去掉开头的 '[' 与结尾的 ']'/':'）：如 "[CatSync] xxx" → "CatSync"；"CatSync: xxx" → "CatSync"；"ImpactSync xxx" → "ImpactSync"
        int start = head[0] == '[' ? 1 : 0;
        char endCh = head[0] == '[' ? ']' : ' ';
        int end = head.IndexOf(endCh, start);
        string token = end > start ? head.Substring(start, end - start) : head.Substring(start);
        if (string.IsNullOrEmpty(token)) return null;
        // 去尾部分隔符（冒号/空格等）："CatSync:" → "CatSync"
        int tl = token.Length;
        while (tl > 0 && (token[tl - 1] == ':' || token[tl - 1] == ' ' || token[tl - 1] == ']'))
            tl--;
        if (tl <= 0) return null;
        token = token.Substring(0, tl);
        lock (_routes)
            foreach (var (p, f) in _routes)
                if (token.Equals(p, StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }
}
