using System;
using System.Collections.Generic;
using UnityEngine;

using OpenNestCoop.Core;
using OpenNestCoop.Net;

namespace OpenNestCoop.Debug;

/// <summary>
/// 网络诊断 UI：显示 <see cref="NetworkGovernor"/> 分级/参数/当前采样窗口
/// + <see cref="NetLagSim"/> 网络环境模拟配置（延迟/带宽/单包/丢包）+ 联机会话状态。
/// 显示由 <see cref="DiagCycleController"/> 统一控制（按 F9 在 帧性能/网络/交互工具/不显示 间循环）。
/// 用 IMGUI(OnGUI) 渲染（轻量、无 UGUI 依赖）。
/// </summary>
public class NetworkGovernorDebugUI : MonoBehaviour
{
    private bool _show;
    private float _refresh;
    /// <summary>行样式枚举（颜色）。</summary>
    private enum Sk { Title, Normal, Good, Warn, Bad, Info }
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
    /// <summary>流量维度着色（对应 Governor 阈值：<10KB 绿 / >40KB 黄 / >80KB 红）。</summary>
    private static Sk BytesSk(long b) => b > 80_000 ? Sk.Bad : b > 40_000 ? Sk.Warn : b < 10_000 ? Sk.Good : Sk.Normal;
    /// <summary>延迟维度着色（<100ms 绿 / <200ms 黄 / else 红）。</summary>
    private static Sk RttSk(float ms) => ms <= 0 ? Sk.Normal : ms < 100 ? Sk.Good : ms < 200 ? Sk.Warn : Sk.Bad;
    /// <summary>丢包率维度着色（对应 Governor：<3% 绿 / <10% 黄 / else 红；-1 样本不足灰）。</summary>
    private static Sk LossSk(float l) => l < 0 ? Sk.Normal : l < 0.03f ? Sk.Good : l < 0.10f ? Sk.Warn : Sk.Bad;
    /// <summary>单例（供 DiagCycleController 切换）。</summary>
    public static NetworkGovernorDebugUI Instance { get; private set; }

    // ⚠️ 2026-08-25 折线图：1 分钟历史环形缓冲（每 1s 采样一次）。RTT/丢包率/发送字节 三条不同颜色曲线。
    private const int HistLen = 60;
    private readonly float[] _rttHist = new float[HistLen];
    private readonly float[] _lossHist = new float[HistLen];
    private readonly float[] _sentHist = new float[HistLen];
    private int _histHead;
    private float _histTimer;
    private float _rttMax = 1f, _lossMax = 1f, _sentMax = 1f; // 自适应 y 轴上限
    /// <summary>三条曲线固定颜色（图例色块用；文字用 Info 蓝——颜色标识符+文字另一色）。</summary>
    private static readonly Color C_Rtt = new Color(1f, 0.42f, 0.38f);   // 红：RTT
    private static readonly Color C_Loss = new Color(1f, 0.78f, 0.28f);  // 黄：丢包
    private static readonly Color C_Sent = new Color(0.45f, 0.92f, 0.45f); // 绿：发送字节

    public NetworkGovernorDebugUI(System.IntPtr ptr) : base(ptr) { }

    public void Awake() { Instance = this; }

    /// <summary>由 DiagCycleController（F9 循环）控制显示；打开时 dump 独立日志（key=net.diag）。</summary>
    public void SetVisible(bool v)
    {
        _show = v;
        if (v) { _refresh = 0f; LogDump(); }
    }

    public void Update()
    {
        try
        {
            // 折线图历史：常驻采样（每 1s），打开即有 1 分钟曲线
            SampleHistory(Time.unscaledDeltaTime);
            if (!_show) return;

            // 0.25s 刷新文本（避免每帧拼接字符串）
            _refresh -= Time.unscaledDeltaTime;
            if (_refresh <= 0f)
            {
                _refresh = 0.25f;
                BuildLines();
            }
        }
        catch { }
    }

    /// <summary>每 1s 采样：RTT/丢包率/发送字节写入环形历史（自适应 y 轴上限）。</summary>
    private void SampleHistory(float dt)
    {
        _histTimer += dt;
        if (_histTimer < 1f) return;
        _histTimer = 0f;
        var g = NetworkGovernor.Instance;
        _rttHist[_histHead] = g.CurrentRttMs;
        _lossHist[_histHead] = g.CurrentLossRate * 100f; // 转 %
        _sentHist[_histHead] = g.CurrentSentBytes;
        _histHead = (_histHead + 1) % HistLen;
        // 自适应上限
        float m = 1f;
        for (int i = 0; i < HistLen; i++) if (_rttHist[i] > m) m = _rttHist[i];
        _rttMax = Mathf.Max(1f, m * 1.15f);
        m = 1f;
        for (int i = 0; i < HistLen; i++) if (_lossHist[i] > m) m = _lossHist[i];
        _lossMax = Mathf.Max(1f, m * 1.15f);
        m = 1f;
        for (int i = 0; i < HistLen; i++) if (_sentHist[i] > m) m = _sentHist[i];
        _sentMax = Mathf.Max(1f, m * 1.15f);
    }

    private void OnGUI()
    {
        if (!_show || _lines.Count == 0) return;
        try
        {
            int oldDepth = GUI.depth;
            GUI.depth = -100; // 尽量渲染在其它 IMGUI 之上（诊断用；UGUI 层由 Canvas 独立控制）
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
            // ⚠️ 2026-08-25 折线图：文本面板下方画 1 分钟网络曲线——RTT(红)/丢包(黄)/发送字节(绿)。
            DrawGraphs(y + 4f);
            GUI.depth = oldDepth;
        }
        catch { }
    }

    /// <summary>画折线图（1 分钟历史）：RTT/丢包/发送字节三条不同颜色曲线，图例=色块+Info 蓝文字。</summary>
    private void DrawGraphs(float topY)
    {
        float x = Screen.width - 460;
        float w = 440;
        var oldC = GUI.contentColor;
        GUI.contentColor = ColorOf(Sk.Info);
        GUI.Label(new Rect(x, topY, 260, 18), "1分钟 RTT(红)/丢包%(黄)/发送B(绿)");
        GUI.contentColor = oldC;
        Rect area = new Rect(x, topY + 20, w, 90);
        DrawSeries(area, _rttHist, C_Rtt, _rttMax);
        DrawSeries(area, _lossHist, C_Loss, _lossMax);
        DrawSeries(area, _sentHist, C_Sent, _sentMax);
        // 图例：色块 + Info 蓝文字（颜色标识符+文字另一色）
        float ly = topY + 118;
        DrawLegend(x, ly, 0, C_Rtt, "RTT");
        DrawLegend(x + 90, ly, 0, C_Loss, "丢包");
        DrawLegend(x + 180, ly, 0, C_Sent, "发送");
        // 当前值标注（文字用各曲线色，方便对照）
        var g = NetworkGovernor.Instance;
        DrawLegend(x + 270, ly, 0, C_Rtt, $"{g.CurrentRttMs:0}ms");
        DrawLegend(x + 330, ly, 0, C_Loss, $"{g.CurrentLossRate * 100f:0}%");
    }

    /// <summary>图例项：色块标识符 + 文字（文字用 Info 蓝）。</summary>
    private void DrawLegend(float x, float y, int colorIdx, Color c, string text)
    {
        GUI.DrawTexture(new Rect(x, y, 10, 10), WhiteTex(), ScaleMode.StretchToFill, false, 1f, c, 0f, 0f);
        var oc = GUI.contentColor;
        GUI.contentColor = ColorOf(Sk.Info);
        GUI.Label(new Rect(x + 12, y - 3, 90, 18), text);
        GUI.contentColor = oc;
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

    /// <summary>独立诊断日志（key=net.diag）：F8 打开时 dump 档位/采样/每模块带宽占用/模拟配置。</summary>
    private static void LogDump()
    {
        try
        {
            var g = NetworkGovernor.Instance;
            var sb = new System.Text.StringBuilder();
            sb.Append($"[NetDiag] F9 dump tier={g.Tier}{(g.IsManual ? "(手动)" : "(自动)")} freq=×{g.FreqMultiplier:0.00} batch={g.MaxBatchItems} pkt={g.MaxPacketBytes}B unreliable={(g.AllowUnreliable ? "on" : "off")} ");
            sb.Append($"sample sent={g.CurrentSentBytes}B drop={g.CurrentDropCount} rtt={g.CurrentRttMs:0}ms loss={g.CurrentLossRate * 100f:0}% peak={g.CurrentQueuePeak} | per-module bytes:");
            var top = g.GetTypeBytesTop(8);
            for (int i = 0; i < top.Count; i++)
            {
                string name = "?";
                try { name = ((OpenNestCoop.Net.MsgType)top[i].Type).ToString(); } catch { }
                sb.Append($" {name}={top[i].Bytes}B");
            }
            if (top.Count == 0) sb.Append(" (none)");
            sb.Append($" | sim lag={NetLagSim.LagMs}±{NetLagSim.JitterMs} cap={NetLagSim.CapKBps}KB/s pktCap={NetLagSim.PacketCap}B loss={NetLagSim.LossPercent}%");
            CoopLog.Info("net.diag", () => sb.ToString());
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[NetDiag] LogDump: {ex.Message}"); }
    }

    /// <summary>构建待绘制行（每 0.25s）：档位/四维度按健康度着色。</summary>
    private void BuildLines()
    {
        var g = NetworkGovernor.Instance;
        var net = CoopRuntime.Net;
        string state = "?";
        int roster = 0;
        bool local = false;
        try { state = net != null ? net.State.ToString() : "null"; } catch { }
        try { roster = net != null && net.Roster != null ? net.Roster.Count : 0; } catch { }
        try { local = net != null && net.LocalMode; } catch { }

        _lines.Clear();
        _lines.Add(("[F9 循环] 网络诊断 (2/3)", "", Sk.Title));
        _lines.Add(("会话: " + state + "  成员: " + roster + (local ? "  [本地回环]" : ""), "", Sk.Normal));
        var tierSk = g.Tier switch
        {
            NetQualityTier.High => Sk.Good,
            NetQualityTier.Normal => Sk.Info,
            NetQualityTier.Low => Sk.Warn,
            _ => Sk.Bad,
        };
        _lines.Add(("档位: " + g.Tier + (g.IsManual ? " (手动)" : " (自动)") + "  频率: ×" + g.FreqMultiplier.ToString("0.00"), "", tierSk));
        _lines.Add(("合包上限: " + g.MaxBatchItems + "  拆包: " + g.MaxPacketBytes + "B", "", Sk.Normal));
        _lines.Add(("unreliable: " + (g.AllowUnreliable ? ("开 (单条" + g.UnreliableMaxBytes + "B)") : "关"), "", Sk.Normal));
        _lines.Add(("优先级缩放: 关键" + g.ModuleFreq(NetModulePriority.Critical).ToString("0.00")
            + " 高" + g.ModuleFreq(NetModulePriority.High).ToString("0.00")
            + " 常" + g.ModuleFreq(NetModulePriority.Normal).ToString("0.00")
            + " 低" + g.ModuleFreq(NetModulePriority.Low).ToString("0.00")
            + " 批" + g.ModuleFreq(NetModulePriority.Bulk).ToString("0.00"), "", Sk.Normal));
        // 采样四维度（每项按健康度着色）
        _lines.Add(("采样 (各维度着色):", "", Sk.Info));
        _lines.Add(("  发送 " + g.CurrentSentBytes + "B", "", BytesSk(g.CurrentSentBytes)));
        _lines.Add(("  丢弃 " + g.CurrentDropCount, "", g.CurrentDropCount > 0 ? Sk.Bad : Sk.Good));
        _lines.Add(("  RTT " + g.CurrentRttMs.ToString("0") + "ms", "", RttSk(g.CurrentRttMs)));
        _lines.Add(("  丢包 " + (g.CurrentLossRate * 100f).ToString("0") + "%", "", LossSk(g.CurrentLossRate)));
        _lines.Add(("  队列峰值 " + g.CurrentQueuePeak, "", g.CurrentQueuePeak >= g.MaxBatchItems ? Sk.Bad : Sk.Good));
        _lines.Add(("带宽占用: " + TypeBytesLine(g), "", Sk.Normal));
        _lines.Add(("模拟: 延迟 " + NetLagSim.LagMs + "ms±" + NetLagSim.JitterMs + "  带宽 " + NetLagSim.CapKBps + "KB/s  单包 " + NetLagSim.PacketCap + "B  丢包 " + NetLagSim.LossPercent + "%", "", Sk.Normal));
    }

    /// <summary>当前窗口各 MsgType 字节占用 top5（一行）。</summary>
    private static string TypeBytesLine(NetworkGovernor g)
    {
        var top = g.GetTypeBytesTop(5);
        if (top.Count == 0) return "(无)";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < top.Count; i++)
        {
            string name = "?";
            try { name = ((OpenNestCoop.Net.MsgType)top[i].Type).ToString(); } catch { }
            sb.Append(sb.Length > 0 ? "  " : "");
            sb.Append($"{name}={top[i].Bytes}B");
        }
        return sb.ToString();
    }
}
