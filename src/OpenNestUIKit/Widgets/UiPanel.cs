using System;
using UnityEngine;
using UnityEngine.UI;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestUIKit.Widgets;

/// <summary>
/// 面板/卡片：底色 +（可选）原生装饰框 + **内层内容流**（<see cref="Flow"/>）。
/// 高度由内容决定（Auto），也可以给固定高度（用于滚动区）。
/// </summary>
public sealed class UiPanel : UiWidget
{
    /// <summary>内容容器（子控件的父物体）。</summary>
    public RectTransform Content { get; private set; }

    /// <summary>内容流（往里 <c>Child(...)</c> 再用 <c>Apply()</c> 排出布局）。</summary>
    public Layout.UiFlow Flow { get; private set; }

    /// <summary>填充层 Image（改底色用）。</summary>
    public Image Fill { get; private set; }

    /// <summary>内边距。</summary>
    public float Padding { get; private set; }

    private bool _autoHeight = true;

    private UiPanel(RectTransform rt, Image fill, RectTransform content, Layout.UiFlow flow, float pad) : base(rt)
    {
        Fill = fill;
        Content = content;
        Flow = flow;
        Padding = pad;
    }

    /// <summary>
    /// 创建面板（Auto 高度：由内容决定；内容变化后调 <c>Flow.Apply()</c> + <c>ApplySize()</c>）。
    /// </summary>
    public static UiPanel Create(Transform parent, float padding = 12f, Color? bg = null,
        bool card = false, bool frame = true, float fixedHeight = 0f)
    {
        var rt = NewRect("panel", parent);
        Theme.UiTheme.SetRect(rt, 0f, 0f, 100f, 10f);

        var color = bg ?? (card ? Theme.UiTheme.CardBg : Theme.UiTheme.ContentBg);
        var spriteRef = frame ? UiSurface.PanelRef() : default;
        var fill = UiSurface.Build(rt, color, spriteRef);

        var content = NewRect("content", rt);
        Theme.UiTheme.SetRect(content, padding, padding, 100f, 10f);

        var flow = new Layout.UiFlow(content)
        {
            Axis = Layout.UiAxis.Vertical,
            Padding = new Layout.UiPadding(),
            Gap = Theme.UiTheme.GapSm,
            CrossStretch = true,
            AutoHeight = true,
        };

        var panel = new UiPanel(rt, fill, content, flow, padding);
        if (fixedHeight > 0f)
        {
            panel._autoHeight = false;
            Theme.UiTheme.SetRect(rt, 0f, 0f, 100f, fixedHeight);
            Theme.UiTheme.SetRect(content, padding, padding, 100f, fixedHeight - padding * 2f);
            flow.AutoHeight = false;
        }

        Layout.UiMeasure.Register(rt,
            w => panel.MeasurePanelHeight(w),
            _ => panel._rt != null ? panel._rt.sizeDelta.x : 0f);
        return panel;
    }

    /// <summary>内容高度 + 内边距（Auto 高度）。</summary>
    public float MeasurePanelHeight(float width)
    {
        if (!_autoHeight)
        {
            try { return _rt != null ? _rt.sizeDelta.y : 0f; } catch { return 0f; }
        }
        float inner = Mathf.Max(0f, width - Padding * 2f);
        return Flow.MeasureHeight(inner) + Padding * 2f;
    }

    /// <summary>重排内容并刷新自身高度（内容变化后调用）。</summary>
    public void Relayout()
    {
        try
        {
            float w = _rt != null ? _rt.rect.width : 0f;
            if (w <= 1f) w = _rt != null ? _rt.sizeDelta.x : 100f;
            Flow.Apply();
            if (_autoHeight)
            {
                float h = MeasurePanelHeight(w);
                Theme.UiTheme.SetRect(_rt, 0f, 0f, w, h);
                Theme.UiTheme.SetRect(Content, Padding, Padding, Mathf.Max(0f, w - Padding * 2f), Mathf.Max(0f, h - Padding * 2f));
                Flow.Apply();
            }
            else
            {
                float h = _rt != null ? _rt.sizeDelta.y : 0f;
                Theme.UiTheme.SetRect(Content, Padding, Padding, Mathf.Max(0f, w - Padding * 2f), Mathf.Max(0f, h - Padding * 2f));
                Flow.Apply();
            }
        }
        catch (Exception ex) { CoopLog.Warn("uikit.widget", () => "panel relayout failed: " + ex.Message); }
    }

    /// <summary>改底色。</summary>
    public void SetColor(Color c)
    {
        try { if (Fill != null) Fill.color = c; } catch { }
    }

    public override void Destroy()
    {
        try { Layout.UiMeasure.Unregister(_rt); } catch { }
        try { Layout.UiFlow.Forget(Content); } catch { }
        base.Destroy();
    }
}

/// <summary>分隔线（优先用原生细装饰线；没有则 1px 纯色）。可选带分组标题。</summary>
public sealed class UiSeparator : UiWidget
{
    private UiSeparator(RectTransform rt) : base(rt) { }

    /// <summary>创建分隔线（<paramref name="label"/> 非空 = "标题 + 线"）。</summary>
    public static UiSeparator Create(Transform parent, string label = null)
    {
        var rt = NewRect("sep", parent);
        float h = string.IsNullOrEmpty(label) ? 9f : 22f;
        Theme.UiTheme.SetRect(rt, 0f, 0f, 100f, h);

        var spriteRef = UiSurface.LineRef();
        if (!string.IsNullOrEmpty(label))
        {
            // 工业风分组：左侧 **2px 琥珀竖条** + 分组名（字距拉开 1.5）—— 一眼能认出“这是一段的开始”
            var barRt = NewRect("hbar", rt);
            var barImg = barRt.gameObject.AddComponent<Image>();
            barImg.color = Theme.UiTheme.Accent;
            barImg.raycastTarget = false;
            try
            {
                barRt.anchorMin = barRt.anchorMax = new Vector2(0f, 1f);
                barRt.pivot = new Vector2(0f, 1f);
                barRt.sizeDelta = new Vector2(2f, 12f);
                barRt.anchoredPosition = new Vector2(0f, -2f);
            }
            catch { }

            // ⚠ 标题必须**铺满整块 + 单行**：早期写死 200 宽 → 长标题在 200px 里折行，
            //   而行高固定 22 → 第二行溢出/被裁（用户报“标题宽度没有沾满整个块，错误换行了”）。
            var txt = UiText.Create(rt, label, UiTextKind.Header, 0f, TextAlignmentOptions.Left);
            try
            {
                txt.Rect.anchorMin = new Vector2(0f, 1f);
                txt.Rect.anchorMax = new Vector2(1f, 1f);
                txt.Rect.pivot = new Vector2(0f, 1f);
                txt.Rect.offsetMin = new Vector2(8f, -18f);
                txt.Rect.offsetMax = new Vector2(0f, 0f);
                txt.Rect.sizeDelta = new Vector2(0f, 18f);
                txt.SetSingleLine(true);
                txt.LetterSpacing = 1.5f;
            }
            catch { }
        }

        var line = NewRect("line", rt);
        var img = line.gameObject.AddComponent<Image>();
        float lineH;
        if (!spriteRef.IsEmpty)
        {
            img.sprite = spriteRef.Sprite;
            img.type = spriteRef.Type;
            try { img.fillCenter = spriteRef.FillCenter; } catch { }
            img.color = new Color(1f, 1f, 1f, 0.55f);
            try { img.pixelsPerUnitMultiplier = Theme.UiTheme.SpritePpuMul; } catch { }   // 照抄原生 2.5（见 UiTheme.SpritePpuMul）
            lineH = 6f;
            Theme.UiTheme.SetRect(line, 0f, h - 6f, 100f, lineH);
        }
        else
        {
            img.color = Theme.UiTheme.Hairline;
            lineH = 1f;
            Theme.UiTheme.SetRect(line, 0f, h - 1f, 100f, lineH);
        }
        img.raycastTarget = false;

        Layout.UiMeasure.Register(rt, _ => h, _ => 100f);

        // 线要随父宽拉伸 → 锚点铺满宽度
        try
        {
            line.anchorMin = new Vector2(0f, 1f);
            line.anchorMax = new Vector2(1f, 1f);
            line.pivot = new Vector2(0.5f, 1f);
            line.offsetMin = new Vector2(0f, 0f);
            line.offsetMax = new Vector2(0f, 0f);
            var sd = line.sizeDelta;
            sd.x = 0f;
            // ⚠ 高度必须写回：上面把 `offsetMin/offsetMax` 都置 0（而垂直方向是点锚），
            //   会把 `sizeDelta.y` 归零 ⇒ **分隔线高度 0、根本看不见**（实测 `sep/line rect=440x0`）。
            sd.y = lineH;
            line.sizeDelta = sd;
            line.anchoredPosition = new Vector2(0f, -(h - 6f));
        }
        catch { }

        return new UiSeparator(rt);
    }

    public override void Destroy()
    {
        try { Layout.UiMeasure.Unregister(_rt); } catch { }
        base.Destroy();
    }
}

/// <summary>进度条（0..1）：标题 + 轨道 + 填充 + 百分比。</summary>
public sealed class UiProgress : UiWidget
{
    private Image _fillImage;
    private UiText _label;
    private double _value;

    private UiProgress(RectTransform rt, Image fill, UiText label) : base(rt) { _fillImage = fill; _label = label; }

    /// <summary>进度（0..1；自动夹取并刷新）。</summary>
    public double Value
    {
        get => _value;
        set
        {
            _value = Math.Max(0.0, Math.Min(1.0, value));
            Refresh();
        }
    }

    /// <summary>创建进度条。</summary>
    public static UiProgress Create(Transform parent, string label, double value01 = 0)
    {
        var rt = NewRect("progress", parent);
        const float h = 30f;
        Theme.UiTheme.SetRect(rt, 0f, 0f, 100f, h);

        var txt = UiText.Create(rt, label, UiTextKind.Body, 0f, TextAlignmentOptions.Left);
        Theme.UiTheme.SetRect(txt.Rect, 0f, 0f, 100f, 18f);
        txt.Rect.anchorMax = new Vector2(1f, 1f);
        txt.Rect.offsetMax = new Vector2(-60f, 0f);

        var track = NewRect("track", rt);
        var trackImg = track.gameObject.AddComponent<Image>();
        trackImg.color = Theme.UiTheme.TrackBg;
        trackImg.raycastTarget = false;
        try
        {
            track.anchorMin = new Vector2(0f, 1f);
            track.anchorMax = new Vector2(1f, 1f);
            track.pivot = new Vector2(0.5f, 1f);
            track.offsetMin = new Vector2(0f, 0f);
            track.offsetMax = new Vector2(0f, 0f);
            var sd = track.sizeDelta; sd.x = 0f; sd.y = 8f; track.sizeDelta = sd;
            track.anchoredPosition = new Vector2(0f, -21f);
        }
        catch { }

        var fill = NewRect("fill", track);
        var fillImg = fill.gameObject.AddComponent<Image>();
        fillImg.color = Theme.UiTheme.Accent;
        fillImg.raycastTarget = false;
        try
        {
            fill.anchorMin = new Vector2(0f, 0f);
            fill.anchorMax = new Vector2(0f, 1f);
            fill.pivot = new Vector2(0f, 0.5f);
            fill.offsetMin = Vector2.zero;
            fill.offsetMax = Vector2.zero;
            fill.sizeDelta = new Vector2(0f, 0f);
            fill.anchoredPosition = Vector2.zero;
        }
        catch { }

        Layout.UiMeasure.Register(rt, _ => h, _ => 100f);
        var p = new UiProgress(rt, fillImg, txt) { _value = Math.Max(0, Math.Min(1, value01)) };
        p.Refresh();
        return p;
    }

    private void Refresh()
    {
        try
        {
            if (_fillImage != null)
            {
                var rt = _fillImage.rectTransform;
                if (rt != null) rt.anchorMax = new Vector2((float)_value, 1f);
            }
            if (_label != null)
            {
                // 值显示在右侧（标签文字保持不动，避免每帧拼字符串覆盖标签）
                _label.Value = _label.Value;
            }
        }
        catch { }
    }

    public override void Destroy()
    {
        try { Layout.UiMeasure.Unregister(_rt); } catch { }
        base.Destroy();
    }
}
