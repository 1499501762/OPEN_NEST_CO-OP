using System;
using UnityEngine;

namespace OpenNestUIKit.Core;

/// <summary>
/// 极轻量的**自测计时器**（诊断用，不是 profiler）：统计 <see cref="UiKitBehaviour"/> 的
/// `Update` / `LateUpdate` 自身耗时（滚动窗口：平均/最大 + 采样帧数），
/// 以及拦截层（<see cref="Native.UiInputGuard"/>）的重活花了多少毫秒。
///
/// 为什么要有它：用户反馈“防止指针穿透的拦截机制很耗性能”——优化前后得**有数字对比**，
/// 否则只能靠猜。测试模组的 `perf` 命令直接读这里的值。
///
/// 成本：每帧两次 `Time.realtimeSinceStartup`（≈0.1µs），无分配。
/// </summary>
public static class Perf
{
    private const int Window = 240;               // 约 4 秒（60fps）滚动窗口

    private static readonly float[] _update = new float[Window];
    private static readonly float[] _late = new float[Window];
    private static int _ui, _li, _un, _ln;
    private static float _uSum, _lSum;

    /// <summary>最近窗口内 `Update` 平均耗时（ms）。</summary>
    public static float UpdateAvgMs => _un > 0 ? _uSum / _un : 0f;
    /// <summary>最近窗口内 `Update` 最大耗时（ms）。</summary>
    public static float UpdateMaxMs { get; private set; }
    /// <summary>最近窗口内 `LateUpdate` 平均耗时（ms）。</summary>
    public static float LateAvgMs => _ln > 0 ? _lSum / _ln : 0f;
    /// <summary>最近窗口内 `LateUpdate` 最大耗时（ms）。</summary>
    public static float LateMaxMs { get; private set; }
    /// <summary>已采样帧数。</summary>
    public static int Frames { get; private set; }

    public static void Sample(float t0)
    {
        try
        {
            float ms = (Time.realtimeSinceStartup - t0) * 1000f;
            Push(_update, ref _ui, ref _un, ref _uSum, ms);
            if (ms > UpdateMaxMs) UpdateMaxMs = ms;
            Frames++;
        }
        catch { }
    }

    public static void SampleLate(float t0)
    {
        try
        {
            float ms = (Time.realtimeSinceStartup - t0) * 1000f;
            Push(_late, ref _li, ref _ln, ref _lSum, ms);
            if (ms > LateMaxMs) LateMaxMs = ms;
        }
        catch { }
    }

    /// <summary>清空统计（`perf:reset`）。</summary>
    public static void Reset()
    {
        Array.Clear(_update, 0, _update.Length);
        Array.Clear(_late, 0, _late.Length);
        _ui = _li = _un = _ln = 0;
        _uSum = _lSum = 0f;
        UpdateMaxMs = LateMaxMs = 0f;
        Frames = 0;
    }

    private static void Push(float[] buf, ref int idx, ref int n, ref float sum, float v)
    {
        if (n >= buf.Length) sum -= buf[idx];
        else n++;
        buf[idx] = v;
        sum += v;
        idx = (idx + 1) % buf.Length;
    }

    /// <summary>一行式快照（含拦截层的关键计数与耗时）。</summary>
    public static string Snapshot()
    {
        try
        {
            return $"uikit 帧耗时：Update 平均 {UpdateAvgMs:F3}ms / 最大 {UpdateMaxMs:F3}ms；"
                 + $"LateUpdate 平均 {LateAvgMs:F3}ms / 最大 {LateMaxMs:F3}ms（{Frames} 帧）"
                 + $"｜拦截层：Apply 上次 {Native.UiInputGuard.LastApplyMs:F2}ms，"
                 + $"交互锁上次 {Native.UiInputGuard.LastLockMs:F2}ms（看进 {Native.UiInputGuard.LastScanned} 个，全场景遍历={Native.UiInputGuard.LastScanWasFull}）"
                 + $"｜其中：原生搜索 {Native.UiInputGuard.LastSearchMs:F2}ms / 入队 {Native.UiInputGuard.LastIterMs:F2}ms";
        }
        catch { return "uikit 帧耗时：<读取失败>"; }
    }
}
