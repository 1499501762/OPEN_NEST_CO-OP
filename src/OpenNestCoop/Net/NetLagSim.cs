using System;
using System.Collections.Generic;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.Net;

/// <summary>
/// 网络延迟/波动模拟器（本地双端测试用）。
/// 命令行：--lag &lt;ms&gt;（基础单向延迟，默认 0）、--lagjitter &lt;ms&gt;（波动范围 ±，默认 0）。
/// 机制：NetManager 从 Transport.Poll 收到的包不直接分发，先进延迟队列（到期时间 = now + lag + rand(0..jitter)），
/// Update 里到期才交给 OnPacket → 模拟真实网络 RTT/抖动（发到本端是单向，双端各设一半即模拟 RTT）。
/// 日志节流：只打印配置/每 500 包一次（避免刷屏）。
/// </summary>
public static class NetLagSim
{
    private sealed class Pending { public ulong From; public byte[] Data; public float At; }
    private static readonly List<Pending> _queue = new();
    private static int _injectedLog;
    private static int _deliveredLog;
    private static bool _configured;

    /// <summary>基础单向延迟（毫秒）。</summary>
    public static int LagMs;
    /// <summary>延迟波动范围 ±（毫秒）。</summary>
    public static int JitterMs;

    /// <summary>是否启用（lag 或 jitter &gt; 0）。</summary>
    public static bool Enabled => LagMs > 0 || JitterMs > 0;

    /// <summary>配置（AutoJoin 解析命令行后调用）。</summary>
    public static void Configure(int lagMs, int jitterMs)
    {
        LagMs = Math.Max(0, lagMs);
        JitterMs = Math.Max(0, jitterMs);
        _configured = true;
        if (Enabled)
            CoopRuntime.LogSource?.LogInfo($"[NetLagSim] enabled lag={LagMs}ms jitter=±{JitterMs}ms (simulated one-way delay)");
    }

    /// <summary>包入队：返回 true 表示本包被延迟（调用方不应立即分发）；false = 直通（未启用）。</summary>
    public static bool Enqueue(ulong from, byte[] data)
    {
        if (!_configured || !Enabled) return false;
        try
        {
            float delay = LagMs / 1000f;
            if (JitterMs > 0) delay += UnityEngine.Random.Range(0f, JitterMs) / 1000f;
            _queue.Add(new Pending { From = from, Data = data, At = Time.realtimeSinceStartup + delay });
            if ((++_injectedLog % 500) == 1)
                CoopRuntime.LogSource?.LogInfo($"[NetLagSim] injected {_queue.Count} pending (from {from}, delay {delay * 1000f:0}ms)");
            return true;
        }
        catch { return false; }
    }

    /// <summary>到期包分发（NetManager.Update 调用）。返回是否有待处理。调用方把返回的包交给 OnPacket。</summary>
    public static void Flush(Action<ulong, byte[]> deliver)
    {
        if (!Enabled || _queue.Count == 0) return;
        float now = Time.realtimeSinceStartup;
        for (int i = _queue.Count - 1; i >= 0; i--)
        {
            var p = _queue[i];
            if (now < p.At) continue;
            _queue.RemoveAt(i);
            try { deliver(p.From, p.Data); }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"NetLagSim deliver: {ex.Message}"); }
            if ((++_deliveredLog % 500) == 1)
                CoopRuntime.LogSource?.LogInfo($"[NetLagSim] delivered, {_queue.Count} pending");
        }
    }

    /// <summary>重置（会话结束/模块清理）。</summary>
    public static void Reset()
    {
        _queue.Clear();
        _injectedLog = 0;
        _deliveredLog = 0;
    }
}
