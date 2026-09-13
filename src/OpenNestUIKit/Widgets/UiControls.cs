using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestUIKit.Widgets;

/// <summary>
/// 开关（行样式：左标签 + 右滑块）。整行可点。
/// </summary>
public sealed class UiToggle : UiWidget
{
    private Image _track, _knob;
    private UiText _label;
    private bool _value;
    private Native.UiHotZone _zone;

    /// <summary>值变化回调。</summary>
    public Action<bool> OnChanged;

    private UiToggle(RectTransform rt, Image track, Image knob, UiText label) : base(rt)
    {
        _track = track; _knob = knob; _label = label;
    }

    /// <summary>当前值（赋值会刷新视觉但**不**触发回调）。</summary>
    public bool Value
    {
        get => _value;
        set { _value = value; Refresh(); }
    }

    /// <summary>创建开关行。</summary>
    public static UiToggle Create(Transform parent, string label, bool value, Action<bool> onChanged, float height = 0f,
        string zoneName = null)
    {
        var rt = NewRect("toggle", parent);
        float h = height > 0f ? height : Theme.UiTheme.RowH;
        Theme.UiTheme.SetRect(rt, 0f, 0f, 200f, h);

        var txt = UiText.Create(rt, label, UiTextKind.Body, 0f, TextAlignmentOptions.Left);
        Theme.UiTheme.SetRect(txt.Rect, 0f, 0f, 120f, h);
        txt.Rect.anchorMax = new Vector2(1f, 1f);
        txt.Rect.offsetMax = new Vector2(-(Theme.UiTheme.ToggleW + 8f), 0f);

        var track = NewRect("track", rt);
        var trackImg = track.gameObject.AddComponent<Image>();
        trackImg.raycastTarget = false;
        try { track.anchorMin = track.anchorMax = new Vector2(1f, 0.5f); track.pivot = new Vector2(1f, 0.5f); } catch { }
        Theme.UiTheme.SetTopLeft(track, 0f, 0f);
        try
        {
            track.anchorMin = track.anchorMax = new Vector2(1f, 1f);
            track.pivot = new Vector2(1f, 1f);
            track.anchoredPosition = new Vector2(0f, -(h - Theme.UiTheme.ToggleH) * 0.5f);
        }
        catch { }
        track.sizeDelta = new Vector2(Theme.UiTheme.ToggleW, Theme.UiTheme.ToggleH);

        var knob = NewRect("knob", track);
        var knobImg = knob.gameObject.AddComponent<Image>();
        knobImg.color = Color.white;
        knobImg.raycastTarget = false;
        try
        {
            knob.anchorMin = knob.anchorMax = new Vector2(0f, 0.5f);
            knob.pivot = new Vector2(0f, 0.5f);
            knob.sizeDelta = new Vector2(Theme.UiTheme.ToggleH - 6f, Theme.UiTheme.ToggleH - 6f);
            knob.anchoredPosition = new Vector2(3f, 0f);
        }
        catch { }

        var t = new UiToggle(rt, trackImg, knobImg, txt) { _value = value, OnChanged = onChanged };
        t.Refresh();
        t._zone = Native.UiPointerRouter.Add(new Native.UiHotZone
        {
            Name = string.IsNullOrEmpty(zoneName) ? "toggle:" + label : zoneName,
            Rect = rt,
            Owner = t,
            OnClick = () => t.ToggleValue(),
        });
        Layout.UiMeasure.Register(rt, _ => h, _ => 200f);
        return t;
    }

    /// <summary>翻转并触发回调。</summary>
    public void ToggleValue()
    {
        if (!_interactable) return;
        _value = !_value;
        Refresh();
        try { OnChanged?.Invoke(_value); } catch (Exception ex) { CoopLog.Warn("uikit.widget", () => "toggle handler failed: " + ex.Message); }
    }

    private void Refresh()
    {
        try
        {
            if (_track != null) _track.color = _value ? Theme.UiTheme.AccentDim : Theme.UiTheme.TrackBg;
            if (_knob != null)
            {
                var rt = _knob.rectTransform;
                if (rt != null)
                {
                    float w = Theme.UiTheme.ToggleW - (Theme.UiTheme.ToggleH - 6f) - 6f;
                    rt.anchoredPosition = new Vector2(_value ? 3f + w : 3f, 0f);
                    _knob.color = _value ? Theme.UiTheme.Accent : Theme.UiTheme.TextDim;
                }
            }
        }
        catch { }
    }

    /// <summary>改标签。</summary>
    public void SetLabel(string s) { if (_label != null) _label.Value = s; }

    public override void Destroy()
    {
        try { Layout.UiMeasure.Unregister(_rt); } catch { }
        base.Destroy();
    }
}

/// <summary>滑条（行样式：左标签 + 轨道 + 右侧数值）。拖动由自管指针驱动。</summary>
public sealed class UiSlider : UiWidget
{
    private static readonly List<UiSlider> _live = new();      // 诊断用（ProbeAll）
    private Image _fill;
    private RectTransform _track;
    private UiText _label, _valueText;
    private double _value, _min, _max, _step;
    private string _format = "0.##";

    /// <summary>值变化回调。</summary>
    public Action<double> OnChanged;

    private UiSlider(RectTransform rt, RectTransform track, Image fill, UiText label, UiText valueText) : base(rt)
    {
        _track = track; _fill = fill; _label = label; _valueText = valueText;
    }

    /// <summary>当前值。</summary>
    public double Value
    {
        get => _value;
        set { SetValue(value, notify: false); }
    }

    /// <summary>创建滑条。<paramref name="zoneName"/> 可覆盖热区名（默认 <c>slider</c>）。</summary>
    public static UiSlider Create(Transform parent, string label, double value, double min, double max, double step,
        Action<double> onChanged, float height = 0f, string zoneName = null)
    {
        var rt = NewRect("slider", parent);
        float h = height > 0f ? height : Theme.UiTheme.RowH;
        Theme.UiTheme.SetRect(rt, 0f, 0f, 240f, h);

        float valueW = Theme.UiTheme.SliderLabelW;

        var txt = UiText.Create(rt, label, UiTextKind.Body, 0f, TextAlignmentOptions.Left);
        Theme.UiTheme.SetRect(txt.Rect, 0f, 0f, 100f, h);
        txt.Rect.anchorMax = new Vector2(0f, 1f);
        txt.Rect.anchorMin = new Vector2(0f, 1f);
        txt.Rect.sizeDelta = new Vector2(96f, h);

        var vtxt = UiText.Create(rt, "0", UiTextKind.Value, valueW, TextAlignmentOptions.MidlineRight);
        try
        {
            vtxt.Rect.anchorMin = vtxt.Rect.anchorMax = new Vector2(1f, 1f);
            vtxt.Rect.pivot = new Vector2(1f, 1f);
            vtxt.Rect.anchoredPosition = new Vector2(0f, 0f);
        }
        catch { }
        vtxt.Rect.sizeDelta = new Vector2(valueW, h);

        var track = NewRect("track", rt);
        var trackImg = track.gameObject.AddComponent<Image>();
        trackImg.color = Theme.UiTheme.TrackBg;
        trackImg.raycastTarget = false;
        try
        {
            track.anchorMin = new Vector2(0f, 0.5f);
            track.anchorMax = new Vector2(1f, 0.5f);
            track.pivot = new Vector2(0f, 0.5f);
            track.offsetMin = new Vector2(100f, 0f);
            track.offsetMax = new Vector2(-(valueW + 8f), 0f);
            var sd = track.sizeDelta; sd.y = 6f; track.sizeDelta = sd;
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
            fill.sizeDelta = Vector2.zero;
            fill.anchoredPosition = Vector2.zero;
        }
        catch { }

        var knob = NewRect("knob", track);
        var knobImg = knob.gameObject.AddComponent<Image>();
        knobImg.color = Theme.UiTheme.TextPrimary;
        knobImg.raycastTarget = false;
        try
        {
            // ⚠️ 把手位置用**锚点比例**表达（不是像素）——早期版本在 Refresh() 里算 `track.rect.width * p`，
            //    而创建时布局还没跑：rect.width=0 → 退化成 `sizeDelta.x`（拉伸锚点下是**负值**）
            //    → 把手被摆到轨道左边外面（用户反馈“拖动点位置初始化有问题”）。
            //    用锚点后无论何时/何尺寸都落在 p 处。
            knob.anchorMin = knob.anchorMax = new Vector2(0f, 0.5f);
            knob.pivot = new Vector2(0.5f, 0.5f);
            knob.sizeDelta = new Vector2(10f, 16f);
            knob.anchoredPosition = Vector2.zero;
        }
        catch { }

        var sl = new UiSlider(rt, track, fillImg, txt, vtxt)
        {
            _min = min,
            _max = max > min ? max : min + 1,
            _step = step > 0 ? step : (max - min) / 100.0,
            OnChanged = onChanged,
        };
        sl.RegisterZone(track, knob, trackImg, zoneName);
        sl.SetValue(value, notify: false);
        try { _live.Add(sl); } catch { }
        Layout.UiMeasure.Register(rt, _ => h, _ => 240f);
        return sl;
    }

    private void RegisterZone(RectTransform track, RectTransform knob, Image trackImg, string zoneName)
    {
        Native.UiPointerRouter.Add(new Native.UiHotZone
        {
            Name = string.IsNullOrEmpty(zoneName) ? "slider" : zoneName,
            Rect = track,
            Owner = this,
            Bg = trackImg,
            OnPressLocal = local =>
            {
                if (!_interactable) return false;
                ApplyLocal(local.x);
                return true;    // 开始拖拽
            },
            OnDrag = local => ApplyLocal(local.x),
            OnClick = () => { try { OnChanged?.Invoke(_value); } catch { } },
        });
        _knob = knob;
    }

    private RectTransform _knob;

    private void ApplyLocal(float localX)
    {
        try
        {
            float w = _track != null ? _track.rect.width : 0f;
            if (w <= 1f) return;
            double p = Mathf.Clamp01(localX / w);
            double raw = _min + p * (_max - _min);
            if (_step > 0) raw = _min + Math.Round((raw - _min) / _step) * _step;
            SetValue(raw, notify: true);
        }
        catch { }
    }

    /// <summary>设置值（<paramref name="notify"/> = 是否触发回调）。</summary>
    public void SetValue(double v, bool notify)
    {
        _value = Math.Max(_min, Math.Min(_max, v));
        Refresh();
        if (notify)
        {
            try { OnChanged?.Invoke(_value); } catch (Exception ex) { CoopLog.Warn("uikit.widget", () => "slider handler failed: " + ex.Message); }
        }
    }

    /// <summary>小数位格式（默认 "0.##"）。</summary>
    public void SetFormat(string fmt) { _format = string.IsNullOrEmpty(fmt) ? "0.##" : fmt; Refresh(); }

    /// <summary>
    /// 诊断（实机取证用）：值 / 比例 / 把手锚点 / 轨道宽。
    /// 用户反馈“拖动条上面那个拖动点位置初始化有问题，不在对应的位置上” → 这里直接比对
    /// **把手锚点比例** 与 **值比例** 是否一致（一致就是对的：锚点定位不依赖布局时序）。
    /// </summary>
    public static string ProbeAll()
    {
        var sb = new System.Text.StringBuilder();
        int n = 0;
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var s = _live[i];
            if (s == null) { _live.RemoveAt(i); continue; }
            n++;
            try
            {
                double p = s._max > s._min ? (s._value - s._min) / (s._max - s._min) : 0.0;
                float trackW = 0f, knobAnchor = -1f;
                try { trackW = s._track != null ? s._track.rect.width : 0f; } catch { }
                try { knobAnchor = s._knob != null ? s._knob.anchorMin.x : -1f; } catch { }
                bool ok = Mathf.Abs(knobAnchor - (float)p) <= 0.01f;
                // 实机位置换算：把手中心在屏幕上的实际比例（对照 `p`；比看截图靠谱）
                float actual = -1f;
                try
                {
                    if (s._knob != null && s._track != null)
                    {
                        Vector3 k = s._knob.position;
                        Vector3 l = s._track.TransformPoint(new Vector3(s._track.rect.xMin, 0f, 0f));
                        Vector3 r = s._track.TransformPoint(new Vector3(s._track.rect.xMax, 0f, 0f));
                        float span = r.x - l.x;
                        if (Mathf.Abs(span) > 1f) actual = (k.x - l.x) / span;
                    }
                }
                catch { }
                sb.Append($"\n  [{n}] '{s._label?.Value}' 值={s._value:0.##} 范围={s._min:0.##}..{s._max:0.##} 比例={p:0.###}"
                          + $" 轨道宽={trackW:0.#} 把手锚点x={knobAnchor:0.###} 实测屏幕比例={actual:0.###}"
                          + (ok ? " ✓ 位置正确" : " ❌ 锚点≠比例")
                          // ★ 光看锚点不够（旧版“半宽内缩”时锚点是对的、外观却到不了端）：直接比**实测屏幕比例**
                          + (actual < 0f ? "" : (Mathf.Abs(actual - (float)p) <= 0.02f ? " ✓ 到端" : " ❌ 外观未到端（把手中心 ≠ 比例）")));
            }
            catch (Exception ex) { sb.Append("\n  probe err: ").Append(ex.Message); }
        }
        return n == 0 ? "滑块：0 个" : $"滑块 {n} 个：{sb}";
    }

    private void Refresh()
    {
        try
        {
            double p = _max > _min ? (_value - _min) / (_max - _min) : 0.0;
            if (_fill != null)
            {
                var rt = _fill.rectTransform;
                if (rt != null) rt.anchorMax = new Vector2((float)p, 1f);
            }
            if (_knob != null)
            {
                // 锚点比例定位（与 _fill 同一套写法）：不依赖轨道当时的像素宽/布局是否已跑完。
                // ⚠ 2026-09-13 用户：“滚动条和拖拽条还是有外观到不了极致的问题（值能到，外观异常）” ——
                //   真因就是旧版这里额外做了**半宽内缩**（`(0.5-p)*knobW`，为的是“把手完全落在轨道内”）：
                //   于是 p=0/1 时把手**中心**只到 `knobW/2` 与 `trackW-knobW/2`，而填充（`_fill`）已经铺到底
                //   ⇒ 用户看到的“值到了、外观（把手）没到底”。
                //   现在把手中心 = p·trackW（与填充末端、原生页的圆块一致；两端时把手自然越出轨道半格）。
                _knob.anchorMin = _knob.anchorMax = new Vector2((float)p, 0.5f);
                _knob.anchoredPosition = new Vector2(0f, 0f);
            }
            if (_valueText != null) _valueText.Value = _value.ToString(_format);
        }
        catch { }
    }

    /// <summary>改标签。</summary>
    public void SetLabel(string s) { if (_label != null) _label.Value = s; }

    public override void Destroy()
    {
        try { Layout.UiMeasure.Unregister(_rt); } catch { }
        base.Destroy();
    }
}

/// <summary>步进器（行样式：左标签 + `-` 值 `+`）。</summary>
public sealed class UiStepper : UiWidget
{
    private UiText _label, _valueText;
    private double _value, _min, _max, _step;
    private string _format = "0.##";

    /// <summary>值变化回调。</summary>
    public Action<double> OnChanged;

    private UiStepper(RectTransform rt, UiText label, UiText valueText) : base(rt) { _label = label; _valueText = valueText; }

    /// <summary>当前值。</summary>
    public double Value
    {
        get => _value;
        set { _value = Clamp(value); Refresh(); }
    }

    /// <summary>创建步进器。</summary>
    public static UiStepper Create(Transform parent, string label, double value, double min, double max, double step,
        Action<double> onChanged, float height = 0f, string zoneName = null)
    {
        var rt = NewRect("stepper", parent);
        float h = height > 0f ? height : Theme.UiTheme.RowH;
        Theme.UiTheme.SetRect(rt, 0f, 0f, 240f, h);

        var txt = UiText.Create(rt, label, UiTextKind.Body, 0f, TextAlignmentOptions.Left);
        Theme.UiTheme.SetRect(txt.Rect, 0f, 0f, 100f, h);
        txt.Rect.sizeDelta = new Vector2(120f, h);

        var st = new UiStepper(rt, txt, null)
        {
            _min = min,
            _max = max > min ? max : min + 1,
            _step = step > 0 ? step : 1,
            OnChanged = onChanged,
        };

        // 整行热区：**先登记 = 优先级最低**，所以左右按钮仍然是它们自己接管点击。
        // 存在的意义：① 第三方的动态悬停提示（HintFunc）能绑到这一行；② 探针/自动化可按名字定位。
        Native.UiPointerRouter.Add(new Native.UiHotZone
        {
            Name = string.IsNullOrEmpty(zoneName) ? "stepper:" + label : zoneName,
            Rect = rt,
            Owner = st,
            Tint = false,
        });

        const float bw = Theme.UiTheme.StepperBtnW;
        var minus = UiButton.Create(rt, "-", () => st.Add(-st._step), UiButtonStyle.Secondary, bw, Theme.UiTheme.ControlH);
        try
        {
            minus.Rect.anchorMin = minus.Rect.anchorMax = new Vector2(1f, 0.5f);
            minus.Rect.pivot = new Vector2(1f, 0.5f);
            minus.Rect.anchoredPosition = new Vector2(-(bw * 2f + 56f), 0f);
        }
        catch { }

        var vtxt = UiText.Create(rt, "", UiTextKind.Value, 56f, TextAlignmentOptions.Center);
        st._valueText = vtxt;
        try
        {
            vtxt.Rect.anchorMin = vtxt.Rect.anchorMax = new Vector2(1f, 0.5f);
            vtxt.Rect.pivot = new Vector2(1f, 0.5f);
            vtxt.Rect.anchoredPosition = new Vector2(-(bw + 56f), 0f);
        }
        catch { }
        vtxt.Rect.sizeDelta = new Vector2(56f, h);

        var plus = UiButton.Create(rt, "+", () => st.Add(st._step), UiButtonStyle.Secondary, bw, Theme.UiTheme.ControlH);
        try
        {
            plus.Rect.anchorMin = plus.Rect.anchorMax = new Vector2(1f, 0.5f);
            plus.Rect.pivot = new Vector2(1f, 0.5f);
            plus.Rect.anchoredPosition = new Vector2(0f, 0f);
        }
        catch { }

        st._value = st.Clamp(value);
        st.Refresh();
        Layout.UiMeasure.Register(rt, _ => h, _ => 240f);
        return st;
    }

    private double Clamp(double v) => Math.Max(_min, Math.Min(_max, v));

    /// <summary>加减一步。</summary>
    public void Add(double delta)
    {
        if (!_interactable) return;
        _value = Clamp(_value + delta);
        Refresh();
        try { OnChanged?.Invoke(_value); } catch (Exception ex) { CoopLog.Warn("uikit.widget", () => "stepper handler failed: " + ex.Message); }
    }

    /// <summary>小数位格式。</summary>
    public void SetFormat(string fmt) { _format = string.IsNullOrEmpty(fmt) ? "0.##" : fmt; Refresh(); }

    private void Refresh()
    {
        if (_valueText != null) _valueText.Value = _value.ToString(_format);
    }

    public override void Destroy()
    {
        try { Layout.UiMeasure.Unregister(_rt); } catch { }
        base.Destroy();
    }
}

/// <summary>循环选择（行样式：左标签 + `‹ 值 ›`）。</summary>
public sealed class UiChoice : UiWidget
{
    private UiText _label, _valueText;
    private string[] _choices;
    private int _index;

    /// <summary>选择变化回调。</summary>
    public Action<int> OnChanged;

    private UiChoice(RectTransform rt, UiText label, UiText valueText) : base(rt) { _label = label; _valueText = valueText; }

    /// <summary>当前索引。</summary>
    public int Index
    {
        get => _index;
        set { _index = Clamp(value); Refresh(); }
    }

    /// <summary>当前值文本。</summary>
    public string Selected => (_choices != null && _index >= 0 && _index < _choices.Length) ? _choices[_index] : "";

    /// <summary>创建循环选择。</summary>
    public static UiChoice Create(Transform parent, string label, string[] choices, int selected, Action<int> onChanged, float height = 0f, string zoneName = null)
    {
        var rt = NewRect("choice", parent);
        float h = height > 0f ? height : Theme.UiTheme.RowH;
        Theme.UiTheme.SetRect(rt, 0f, 0f, 240f, h);

        var txt = UiText.Create(rt, label, UiTextKind.Body, 0f, TextAlignmentOptions.Left);
        Theme.UiTheme.SetRect(txt.Rect, 0f, 0f, 100f, h);
        txt.Rect.sizeDelta = new Vector2(120f, h);

        var ch = new UiChoice(rt, txt, null) { _choices = choices ?? new string[0], OnChanged = onChanged };

        // 整行热区：先登记 = 优先级最低 ⇒ 左右 `<`/`>` 按钮（后建）仍然优先命中。
        Native.UiPointerRouter.Add(new Native.UiHotZone
        {
            Name = string.IsNullOrEmpty(zoneName) ? "choice:" + label : zoneName,
            Rect = rt,
            Owner = ch,
            Tint = false,
        });

        const float bw = 22f, vw = 150f;
        var prev = UiButton.Create(rt, "<", () => ch.Move(-1), UiButtonStyle.Secondary, bw, Theme.UiTheme.ControlH);
        try
        {
            prev.Rect.anchorMin = prev.Rect.anchorMax = new Vector2(1f, 0.5f);
            prev.Rect.pivot = new Vector2(1f, 0.5f);
            prev.Rect.anchoredPosition = new Vector2(-(bw + vw), 0f);
        }
        catch { }

        var vtxt = UiText.Create(rt, "", UiTextKind.Value, vw, TextAlignmentOptions.Center);
        ch._valueText = vtxt;
        try
        {
            vtxt.Rect.anchorMin = vtxt.Rect.anchorMax = new Vector2(1f, 0.5f);
            vtxt.Rect.pivot = new Vector2(1f, 0.5f);
            vtxt.Rect.anchoredPosition = new Vector2(-bw, 0f);
        }
        catch { }
        vtxt.Rect.sizeDelta = new Vector2(vw, h);

        var next = UiButton.Create(rt, ">", () => ch.Move(1), UiButtonStyle.Secondary, bw, Theme.UiTheme.ControlH);
        try
        {
            next.Rect.anchorMin = next.Rect.anchorMax = new Vector2(1f, 0.5f);
            next.Rect.pivot = new Vector2(1f, 0.5f);
            next.Rect.anchoredPosition = new Vector2(0f, 0f);
        }
        catch { }

        ch._index = ch.Clamp(selected);
        ch.Refresh();
        Layout.UiMeasure.Register(rt, _ => h, _ => 240f);
        return ch;
    }

    private int Clamp(int i)
    {
        int n = _choices != null ? _choices.Length : 0;
        if (n == 0) return 0;
        return Math.Max(0, Math.Min(n - 1, i));
    }

    /// <summary>上下切换（带环绕）。</summary>
    public void Move(int delta)
    {
        if (!_interactable) return;
        int n = _choices != null ? _choices.Length : 0;
        if (n == 0) return;
        _index = ((_index + delta) % n + n) % n;
        Refresh();
        try { OnChanged?.Invoke(_index); } catch (Exception ex) { CoopLog.Warn("uikit.widget", () => "choice handler failed: " + ex.Message); }
    }

    private void Refresh()
    {
        if (_valueText != null) _valueText.Value = Selected;
    }

    public override void Destroy()
    {
        try { Layout.UiMeasure.Unregister(_rt); } catch { }
        base.Destroy();
    }
}
