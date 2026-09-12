using System;
using System.Collections.Generic;
using UnityEngine;

using OpenNestCoop.Core;

namespace OpenNestCoop.Debug;

/// <summary>
/// 帧性能诊断菜单：显示 <see cref="FrameProfiler"/> 统计——FPS/平均/最差帧耗时 +
/// 每个同步模块（按测量点：各 ISyncedModule.Tick / V1 硬编码模块 / NetworkGovernor / TickAll / FlushBatch）
/// 的**每秒 CPU 开销**（ms/s，降序 top），排查"哪个模块占帧/掉帧元凶"。
/// 显示由 <see cref="DiagCycleController"/> 统一控制（按 F9 在 帧性能/网络/交互工具/不显示 间循环）。
/// 用 IMGUI(OnGUI) 渲染（轻量、无 UGUI 依赖）。
/// 帧时间常驻记录（即使不显示也累计）——打开时已有 1s 历史。独立日志：显示时 dump 一次（key=frame.diag）。
/// </summary>
public class FrameDiagUI : MonoBehaviour
{
    private bool _show;
    private float _refresh;
    private float _dumpTimer;
    /// <summary>行样式枚举（颜色）。</summary>
    private enum Sk { Title, Normal, Good, Warn, Bad, Info }
    /// <summary>待绘制行：(文本, 右对齐数值(空=单行), 样式)。模块耗时用 名字+右对齐数值 两段。</summary>
    private readonly List<(string Text, string Num, Sk Style)> _lines = new();
    /// <summary>样式→颜色。⚠️ 不用 GUIStyle 创建（IL2CPP interop 运行时 GUIStyle 构造可能抛异常导致 OnGUI 渲染失效）——用 GUI.contentColor 逐行着色。</summary>
    private static Color ColorOf(Sk s) => s switch
    {
        Sk.Title => new Color(1f, 0.92f, 0.42f),
        Sk.Good => new Color(0.45f, 0.92f, 0.45f),
        Sk.Warn => new Color(1f, 0.78f, 0.28f),
        Sk.Bad => new Color(1f, 0.42f, 0.38f),
        Sk.Info => new Color(0.45f, 0.72f, 1f),
        _ => new Color(0.88f, 0.88f, 0.88f),
    };
    /// <summary>单例（供 DiagCycleController 切换）。</summary>
    public static FrameDiagUI Instance { get; private set; }

    // ⚠️ 2026-08-25 折线图：1 分钟历史环形缓冲（每 1s 采样一次）。FPS 一条线 + 各模块 ms/s 各一条线（不同颜色）。
    private const int HistLen = 60; // 60s 历史
    private readonly float[] _fpsHist = new float[HistLen];
    private readonly List<string> _modNames = new();      // 当前跟踪的模块名（稳定顺序，最多 6 个）
    private readonly Dictionary<string, float[]> _modHist = new();
    private int _histHead;      // 环形写入头（下一个写入位置）
    private float _histTimer;
    private float _fpsMax = 1f;  // 自适应 y 轴上限（FPS）
    private float _modMax = 1f;  // 自适应 y 轴上限（模块 ms/s）
    /// <summary>模块固定调色板（折线/图例色块用；文字用 Info 蓝——颜色标识符+文字另一色）。</summary>
    private static readonly Color[] ModColors =
    {
        new Color(0.30f, 0.80f, 1.00f), // 蓝
        new Color(1.00f, 0.60f, 0.20f), // 橙
        new Color(0.60f, 1.00f, 0.30f), // 绿
        new Color(1.00f, 0.40f, 0.60f), // 粉
        new Color(0.90f, 0.35f, 1.00f), // 紫
        new Color(1.00f, 0.90f, 0.30f), // 黄
    };
    private static Color ModColor(int i) => ModColors[((i % ModColors.Length) + ModColors.Length) % ModColors.Length];

    public FrameDiagUI(System.IntPtr ptr) : base(ptr) { }

    public void Awake() { Instance = this; }

    /// <summary>由 DiagCycleController（F9 循环）控制显示；显示时 dump 独立日志（key=frame.diag）。</summary>
    public void SetVisible(bool v)
    {
        _show = v;
        if (v) { _refresh = 0f; LogDump(); }
    }

    public void Update()
    {
        try
        {
            // 帧时间常驻记录 + 1s 结算（即使不显示也累计，打开即有历史）
            float dt = Time.unscaledDeltaTime;
            FrameProfiler.Instance.RecordFrameMs(dt * 1000f);
            FrameProfiler.Instance.Tick(dt);
            // 折线图历史：常驻采样（每 1s），打开即有 1 分钟曲线
            SampleHistory(dt);

            if (!_show) return;

            _refresh -= dt;
            if (_refresh <= 0f)
            {
                _refresh = 0.25f;
                BuildLines();
            }
            // 周期性 dump 帧数据（显示时每 5s 打一次 frame.diag 日志）——便于抓帧性能热点模块
            _dumpTimer += dt;
            if (_dumpTimer >= 5f)
            {
                _dumpTimer = 0f;
                LogDump();
            }
        }
        catch { }
    }

    /// <summary>每 1s 采样：FPS + top 模块 ms/s 写入环形历史（自适应 y 轴上限）。</summary>
    private void SampleHistory(float dt)
    {
        _histTimer += dt;
        if (_histTimer < 1f) return;
        _histTimer = 0f;
        var p = FrameProfiler.Instance;
        _fpsHist[_histHead] = (float)p.FramesPerSec;
        var top = p.GetTop(6);
        // 确保跟踪当前 top 模块（新增；超过 6 个则只跟踪已跟踪的，避免集合无限增长）
        foreach (var t in top)
        {
            if (_modNames.Count >= 6) break;
            if (!_modHist.ContainsKey(t.Name))
            {
                _modHist[t.Name] = new float[HistLen];
                _modNames.Add(t.Name);
            }
        }
        foreach (var name in _modNames)
        {
            float v = 0f;
            foreach (var t in top)
                if (t.Name == name) { v = (float)t.MsPerSec; break; }
            _modHist[name][_histHead] = v;
        }
        _histHead = (_histHead + 1) % HistLen;
        // 自适应上限（最小 1，避免除以 0）
        float fpsMax = 1f;
        for (int i = 0; i < HistLen; i++) if (_fpsHist[i] > fpsMax) fpsMax = _fpsHist[i];
        _fpsMax = Mathf.Max(1f, fpsMax * 1.15f);
        float modMax = 1f;
        foreach (var arr in _modHist.Values)
            for (int i = 0; i < HistLen; i++) if (arr[i] > modMax) modMax = arr[i];
        _modMax = Mathf.Max(1f, modMax * 1.15f);
    }

    private void OnGUI()
    {
        if (!_show || _lines.Count == 0) return;
        try
        {
            int oldDepth = GUI.depth;
            GUI.depth = -100; // 尽量渲染在其它 IMGUI 之上（诊断用）
            // 右上角面板：纯 GUI.Label 手动定位（不用 GUILayout/GUIStyle，IL2CPP 最稳）
            float x = Screen.width - 460;
            float y = 12f;
            for (int i = 0; i < _lines.Count; i++)
            {
                var ln = _lines[i];
                var oldC = GUI.contentColor;
                GUI.contentColor = ColorOf(ln.Style);
                GUI.Label(new Rect(x, y, 330, 20), ln.Text);
                if (ln.Num.Length > 0)
                    GUI.Label(new Rect(x + 340, y, 100, 20), ln.Num); // 右侧数值段
                GUI.contentColor = oldC;
                y += 20f;
            }
            // ⚠️ 2026-08-25 折线图：文本面板下方画 1 分钟曲线——FPS（白色）+ 各模块 ms/s（不同颜色）。
            DrawGraphs(y + 4f);
            GUI.depth = oldDepth;
        }
        catch { }
    }

    /// <summary>画折线图（1 分钟历史）：FPS 一条 + 各模块 ms/s 各一条（不同颜色），图例=色块+Info 蓝文字。
    /// 用 GUI.DrawTexture 逐点画小方块（IL2CPP 最稳，无 GL/GUIStyle 依赖）。</summary>
    private void DrawGraphs(float topY)
    {
        float x = Screen.width - 460;
        float w = 440;
        // FPS 图（标题 + 曲线 + 图例色块）
        var oldC = GUI.contentColor;
        GUI.contentColor = ColorOf(Sk.Info);
        GUI.Label(new Rect(x, topY, 200, 18), "1分钟帧率(白) / 模块ms/s(彩色)");
        GUI.contentColor = oldC;
        // FPS 曲线
        Rect fpsArea = new Rect(x, topY + 20, w, 70);
        DrawSeries(fpsArea, _fpsHist, Color.white, _fpsMax);
        // 模块曲线（各色叠加）
        Rect modArea = new Rect(x, topY + 100, w, 70);
        for (int i = 0; i < _modNames.Count; i++)
            DrawSeries(modArea, _modHist[_modNames[i]], ModColor(i), _modMax);
        // 图例：每模块 = 色块 + 文字（文字用 Info 蓝——颜色标识符+文字另一色）
        float ly = topY + 182;
        for (int i = 0; i < _modNames.Count; i++)
        {
            // 色块标识符
            var lc = ModColor(i);
            GUI.DrawTexture(new Rect(x + i * 74, ly, 10, 10), WhiteTex(), ScaleMode.StretchToFill, false, 1f, lc, 0f, 0f);
            // 文字用另一色（Info 蓝）
            var oc = GUI.contentColor;
            GUI.contentColor = ColorOf(Sk.Info);
            GUI.Label(new Rect(x + i * 74 + 12, ly - 3, 70, 18), _modNames[i]);
            GUI.contentColor = oc;
        }
    }

    /// <summary>画一条折线（环形缓冲，最新在最右）。逐点画 3px 方块。maxV 用于 y 归一化。</summary>
    private void DrawSeries(Rect area, float[] hist, Color c, float maxV)
    {
        var tex = WhiteTex();
        for (int k = 0; k < HistLen; k++)
        {
            int idx = (k + _histHead) % HistLen; // k=0 最旧 → 从左到右
            float v = hist[idx];
            float nx = area.x + (HistLen > 1 ? (float)k / (HistLen - 1) : 0f) * area.width;
            float ny = area.y + area.height - (maxV > 0f ? Mathf.Clamp01(v / maxV) : 0f) * area.height;
            GUI.DrawTexture(new Rect(nx - 1.5f, ny - 1.5f, 3f, 3f), tex, ScaleMode.StretchToFill, false, 1f, c, 0f, 0f);
        }
    }

    /// <summary>白色 1x1 纹理（GUI.DrawTexture 着色用；懒创建，IL2CPP 安全）。</summary>
    private static Texture2D _whiteTex;
    private static Texture2D WhiteTex()
    {
        if (_whiteTex == null)
        {
            try
            {
                _whiteTex = new Texture2D(1, 1);
                _whiteTex.SetPixel(0, 0, Color.white);
                _whiteTex.Apply();
            }
            catch { }
        }
        return _whiteTex;
    }

    /// <summary>独立诊断日志（key=frame.diag）：F7 打开时 dump FPS/帧时间 + 每模块帧开销 top。</summary>
    private static void LogDump()
    {
        try
        {
            var p = FrameProfiler.Instance;
            var sb = new System.Text.StringBuilder();
            sb.Append($"[FrameDiag] F7 dump fps={p.FramesPerSec:0} avg={p.AvgFrameMs:0.0}ms worst={p.WorstFrameMs:0.0}ms | per-module ms/s:");
            var top = p.GetTop(10);
            for (int i = 0; i < top.Count; i++)
                sb.Append($" {top[i].Name}={top[i].MsPerSec:0.00}");
            if (top.Count == 0) sb.Append(" (none)");
            CoopLog.Info("frame.diag", () => sb.ToString());
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[FrameDiag] LogDump: {ex.Message}"); }
    }

    /// <summary>构建待绘制行（每 0.25s）：FPS/帧时按健康度着色，模块耗时按大小分级 + 右对齐数值。</summary>
    private void BuildLines()
    {
        var p = FrameProfiler.Instance;
        _lines.Clear();
        _lines.Add(("[F9 循环] 帧性能诊断 (1/4)", "", Sk.Title));
        double fps = p.FramesPerSec;
        double avg = p.AvgFrameMs;
        double worst = p.WorstFrameMs;
        _lines.Add(("FPS: " + fps.ToString("0"), "", fps >= 55 ? Sk.Good : fps >= 30 ? Sk.Warn : Sk.Bad));
        _lines.Add(("平均帧: " + avg.ToString("0.0") + "ms", "", avg <= 16.7 ? Sk.Good : avg <= 33 ? Sk.Warn : Sk.Bad));
        _lines.Add(("最差帧: " + worst.ToString("0.0") + "ms", "", worst <= 33 ? Sk.Good : worst <= 50 ? Sk.Warn : Sk.Bad));
        _lines.Add(("模块帧开销(每秒ms, top):", "", Sk.Info));
        var top = p.GetTop(10);
        if (top.Count == 0)
        {
            _lines.Add(("  (无数据——联机/模块驱动中才有测量)", "", Sk.Normal));
        }
        else
        {
            for (int i = 0; i < top.Count; i++)
            {
                double ms = top[i].MsPerSec;
                var sk = ms >= 5 ? Sk.Bad : ms >= 2 ? Sk.Warn : Sk.Normal;
                _lines.Add(("  " + top[i].Name, ms.ToString("0.00") + "ms", sk));
            }
        }
    }
}
