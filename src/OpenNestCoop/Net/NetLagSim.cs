using System;
using System.Collections.Generic;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.Net;

/// <summary>
/// 网络环境模拟器（本地双端测试用）：延迟/抖动 + 带宽限制 + 单包上限 + unreliable 丢包率。
/// 命令行：
///   --lag &lt;ms&gt;        基础单向延迟（默认 0）
///   --lagjitter &lt;ms&gt;  延迟波动范围 ±（默认 0）
///   --netcap &lt;KB/s&gt;   发送带宽上限（令牌桶，0=不限；模拟 Steam P2P 流量限制）
///   --netpacket &lt;B&gt;   单包上限（0=不限；模拟 Steam unreliable ~1200B 硬限制）
///   --netloss &lt;%&gt;     unreliable 丢包率（0-99；模拟高频丢包）
/// 机制：
///   - 接收路径延迟：NetManager 从 Transport.Poll 收到的包先进延迟队列（now + lag + rand(0..jitter)），
///     到期才交给 OnPacket → 模拟 RTT/抖动（发到本端是单向，双端各设一半即模拟 RTT）。
///   - 发送路径限制：NetManager.FlushBatch 发送前调 <see cref="AllowSend"/>——单包超上限/带宽超限/
///     unreliable 命中丢包率 → 丢弃（日志节流），近似 Steam P2P 硬限制下的丢包/拥塞。
/// ⚠️ Steam 官方**无公开固定带宽数值**；公开硬限制是包级（unreliable 单包 ~1200B、reliable 1MB），
/// 实际瓶颈是"高频小包 + 每包固定开销 → 包率限制"。--netcap 用可配置 KB/s 近似。
/// 日志节流：配置/每 200-500 包一次（避免刷屏）。
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
    /// <summary>发送带宽上限（KB/s，--netcap；0=不限）。令牌桶：容量=1s 配额，补充率=CapKBps/s。</summary>
    public static int CapKBps;
    /// <summary>单包上限（字节，--netpacket；0=不限，模拟 Steam unreliable ~1200B 硬限制）。</summary>
    public static int PacketCap;
    /// <summary>reliable 单包上限（Steam reliable 真实硬限制 1MB；--netpacket 只约束 unreliable）。</summary>
    private const int ReliableCapBytes = 1048576;
    /// <summary>unreliable 丢包率（%，--netloss；0-99，模拟高频丢包）。</summary>
    public static int LossPercent;

    // 带宽令牌桶
    private static float _tokens;
    private static float _lastRefill;
    private static float _bucketCapacity;
    private static int _rejectCapLog, _rejectPktLog, _rejectLossLog;

    /// <summary>是否启用延迟模拟（lag 或 jitter &gt; 0）。带宽/单包/丢包独立生效（见 AllowSend）。</summary>
    public static bool Enabled => LagMs > 0 || JitterMs > 0;

    /// <summary>配置（AutoJoin 解析命令行后调用）。</summary>
    public static void Configure(int lagMs, int jitterMs, int capKBps = 0, int packetCap = 0, int lossPercent = 0)
    {
        LagMs = Math.Max(0, lagMs);
        JitterMs = Math.Max(0, jitterMs);
        CapKBps = Math.Max(0, capKBps);
        PacketCap = Math.Max(0, packetCap);
        LossPercent = Math.Clamp(lossPercent, 0, 99);
        _configured = true;
        _bucketCapacity = CapKBps > 0 ? CapKBps * 1024f : 0f;
        _tokens = _bucketCapacity;
        _lastRefill = UnityEngine.Time.realtimeSinceStartup;
        if (Enabled || CapKBps > 0 || PacketCap > 0 || LossPercent > 0)
            CoopRuntime.LogSource?.LogInfo($"[NetLagSim] configured lag={LagMs}ms jitter=±{JitterMs}ms cap={CapKBps}KB/s packetCap={PacketCap}B loss={LossPercent}%");
    }

    /// <summary>发送前网络环境限制检查（NetManager.FlushBatch 调用）：返回 true=允许发送，false=应丢弃。
    /// 模拟 Steam P2P 限制：单包上限 + 带宽上限（--netcap，令牌桶）+ unreliable 丢包率（--netloss）。
    /// ⚠️ 2026-08-26：单包上限**区分通道**——unreliable 用 PacketCap（--netpacket，模拟 Steam ~1200B 硬限制）；
    /// reliable 用 1MB（Steam reliable 真实上限）。原来 reliable 也套 1200B cap → 全量广播/中途加入快照
    /// （31 实体 ~1256B）被误丢 → 客机缺实体/中途加入实体没类型（"客机同步到的实体会少"根因）。
    /// 未配置直通。</summary>
    public static bool AllowSend(int bytes, bool reliable)
    {
        if (!_configured || bytes <= 0) return true;
        try
        {
            // reliable 上限 = Steam reliable 真实限制（1MB）；unreliable 用 PacketCap（~1200B 硬限制）
            int cap = reliable ? ReliableCapBytes : PacketCap;
            if (cap > 0 && bytes > cap)
            {
                if ((++_rejectPktLog % 200) == 1)
                    CoopRuntime.LogSource?.LogWarning($"[NetLagSim] packet {bytes}B > cap {cap}B ({(reliable ? "reliable" : "unreliable")}) → dropped");
                return false;
            }
            if (!reliable && LossPercent > 0 && UnityEngine.Random.Range(0, 100) < LossPercent)
            {
                if ((++_rejectLossLog % 200) == 1)
                    CoopRuntime.LogSource?.LogWarning($"[NetLagSim] unreliable packet dropped ({LossPercent}% loss sim)");
                return false;
            }
            if (CapKBps > 0)
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                float elapsed = now - _lastRefill;
                if (elapsed > 0)
                {
                    _tokens = Math.Min(_bucketCapacity, _tokens + CapKBps * 1024f * elapsed);
                    _lastRefill = now;
                }
                if (bytes > _tokens)
                {
                    if ((++_rejectCapLog % 200) == 1)
                        CoopRuntime.LogSource?.LogWarning($"[NetLagSim] bandwidth cap {CapKBps}KB/s exceeded ({bytes}B, tokens={_tokens:0}) → dropped");
                    return false;
                }
                _tokens -= bytes;
            }
            return true;
        }
        catch { return true; }
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
