using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace OpenNestCoop.Core;

/// <summary>
/// 帧性能剖析器（FrameProfiler，单例）：统计各测量点（每个同步模块 Tick / FlushBatch / NetworkGovernor / V1 模块）
/// 的**帧内 CPU 开销**（ms，累计到 1s 窗口），每秒结算快照——供 **F7 帧性能诊断菜单**（<c>Debug/FrameDiagUI</c>）
/// 屏幕显示 + 独立日志 dump（key=frame.diag），排查"哪个模块占帧/掉帧元凶"。
///
/// 测量点：<c>CoopSyncRegistry.TickAll</c>（每注册模块，按类型名归因）+ <c>NetManager.UpdateCommon</c>
/// （NetworkGovernor.Tick / V1 硬编码模块 / FlushBatch）。用 <see cref="Stopwatch.GetTimestamp"/>（高精度
/// QueryPerformanceCounter，IL2CPP 可用，无托管分配），逐模块/逐段计时。
///
/// 帧时间：外部（F7 UI Update）每帧喂 <see cref="RecordFrameMs"/>（帧耗时），窗口结算出 FPS/平均/最差帧。
/// </summary>
public sealed class FrameProfiler
{
    public static FrameProfiler Instance { get; } = new();
    private FrameProfiler() { }

    private readonly Dictionary<string, double> _cur = new();            // 当前窗口累计 ms（测量点 → 累计）
    private readonly List<(string Name, double MsPerSec)> _snap = new(); // 上次结算快照（降序）
    private float _window;        // 结算窗口累计（≈1s）
    private double _frameMsSum;   // 帧时间累计
    private int _frameCount;
    private double _worstFrameMs;

    // ---- 查询（F7 UI/日志）----

    /// <summary>每秒帧数（上次结算窗口）。</summary>
    public double FramesPerSec => _frameCount > 0 ? _frameCount / Math.Max(0.001f, _window) : 0;
    /// <summary>平均帧耗时（ms）。</summary>
    public double AvgFrameMs => _frameCount > 0 ? _frameMsSum / _frameCount : 0;
    /// <summary>最差单帧耗时（ms）。</summary>
    public double WorstFrameMs => _worstFrameMs;

    /// <summary>上次结算各测量点每秒耗时 top n（降序）。</summary>
    public IReadOnlyList<(string Name, double MsPerSec)> GetTop(int n)
    {
        if (_snap.Count <= n) return _snap;
        return _snap.GetRange(0, n);
    }

    /// <summary>记录某测量点耗时（ms，累计到当前窗口）。</summary>
    public void AddMs(string name, double ms)
    {
        if (string.IsNullOrEmpty(name) || ms < 0) return;
        _cur.TryGetValue(name, out double v);
        _cur[name] = v + ms;
    }

    /// <summary>每帧喂帧耗时（ms；F7 UI Update 每帧调，即使不显示也累计——打开时已有 1s 历史）。</summary>
    public void RecordFrameMs(double frameMs)
    {
        _frameMsSum += frameMs;
        _frameCount++;
        if (frameMs > _worstFrameMs) _worstFrameMs = frameMs;
    }

    /// <summary>每帧结算（F7 UI Update 调；内部 1s 窗口——刷新快照 + 复位）。</summary>
    public void Tick(float dt)
    {
        _window += dt;
        if (_window < 1f) return;
        _snap.Clear();
        foreach (var kv in _cur)
            if (kv.Value > 0) _snap.Add((kv.Key, kv.Value));
        _snap.Sort((a, b) => b.MsPerSec.CompareTo(a.MsPerSec));
        _window = 0f;
        _frameMsSum = 0; _frameCount = 0; _worstFrameMs = 0;
        _cur.Clear();
    }
}
