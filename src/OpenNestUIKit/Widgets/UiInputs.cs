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
/// 文本输入框（行样式：左标签 + 右输入框）。
///
/// 文本**由我们自己画**（`UiText` + 光标 "|" ），键盘/输入法一律走
/// <see cref="UiTextRouter"/>（**照搬 OpenNestCoop 已经实机验证过的输入管线**：
/// 物理键轮询 + 隐藏 IME 锚点 + 原生 Win32 IME + `Keyboard.OnTextInput` 兜底）。
///
/// 为什么不用 `TMP_InputField` 直接收键（早期版本）：
///   - 任务场景里游戏 `EventSystem` 常未激活，且我们为了防指针穿透还要停用它的输入模块
///     → “输入框点了却打不了字”（用户报）；
///   - 就算放行模块，IL2CPP 下 `TMP_InputField.text` 也不可靠（原模组实测 `.text` 恒空）。
/// 现在只留一个 **屏幕内、全透明、不可点** 的隐藏 `TMP_InputField` 当“IME 锚点”，
/// 它的唯一作用是 **唤起系统输入法**（中文提交靠原生 IME 读取）。
/// </summary>
public sealed class UiTextInput : UiWidget
{
    private static readonly List<UiTextInput> _all = new();

    private UiText _label, _valueText;
    private Image _bg;
    private Native.UiHotZone _zone;
    private string _value;
    private string _placeholder = "";
    private string _focusValue = "";        // 聚焦时的值（失焦时比对，只把“改过的”回传）
    private int _maxLength;                 // > 0 = 超过就截断（第三方契约的 MaxLength）

    /// <summary>文本变化回调。</summary>
    public Action<string> OnChanged;

    /// <summary>回车提交回调。</summary>
    public Action<string> OnSubmit;

    /// <summary>密码框（渲染成 `*`）。</summary>
    public bool IsPassword;

    private UiTextInput(RectTransform rt, UiText label, UiText valueText, Image bg) : base(rt)
    {
        _label = label; _valueText = valueText; _bg = bg;
    }

    /// <summary>当前文本。</summary>
    public string Value
    {
        get => _value ?? "";
        set => SetValueInternal(value, notify: false);
    }

    /// <summary>是否聚焦中（唯一真源 = 输入管线）。</summary>
    public bool Focused => ReferenceEquals(UiTextRouter.Focused, this);

    /// <summary>宿主对象还活着（输入管线用）。</summary>
    internal bool Alive { get { try { return _rt != null; } catch { return false; } } }

    /// <summary>诊断：所有存活输入框的聚焦/文本状态（实机取证“输入框点了能不能打字”）。</summary>
    public static string ProbeAll()
    {
        var sb = new System.Text.StringBuilder();
        int n = 0, focused = 0;
        for (int i = _all.Count - 1; i >= 0; i--)
        {
            var x = _all[i];
            if (x == null) { _all.RemoveAt(i); continue; }
            n++;
            if (x.Focused) focused++;
            sb.Append($"\n  [{n}] '{x.Label}' 文本='{x.Value}' 聚焦={(x.Focused ? "是" : "否")}");
            if (x.Focused) sb.Append(" 光标=").Append(_caretOn ? "|（亮）" : "空格（灭）");   // 验“Caret 会不会闪”看这个
        }
        return n == 0 ? "输入框：0 个" : $"输入框 {n} 个（聚焦 {focused}）：{sb}";
    }

    /// <summary>标签文字。</summary>
    public string Label => _label != null ? _label.Value : "";

    /// <summary>空且未聚焦时显示的灰色占位提示。</summary>
    public void SetPlaceholder(string s)
    {
        _placeholder = s ?? "";
        Refresh();
    }

    /// <summary>最大字符数（&lt;= 0 = 不限制）。超出部分在写入时被截断。</summary>
    public int MaxLength
    {
        get => _maxLength;
        set { _maxLength = value > 0 ? value : 0; if (_maxLength > 0 && Value.Length > _maxLength) SetValueInternal(Value, notify: false); }
    }

    /// <summary>创建输入框。<paramref name="zoneName"/> 可覆盖热区名（默认 <c>input:&lt;标签&gt;</c>）。</summary>
    public static UiTextInput Create(Transform parent, string label, string value, Action<string> onChanged,
        Action<string> onSubmit = null, float height = 0f, string zoneName = null, bool password = false,
        string placeholder = null, int maxLength = 0)
    {
        var rt = NewRect("input", parent);
        float h = height > 0f ? height : Theme.UiTheme.RowH;
        Theme.UiTheme.SetRect(rt, 0f, 0f, 320f, h);

        var txt = UiText.Create(rt, label, UiTextKind.Body, 0f, TextAlignmentOptions.Left);
        // ⚠️ 2026-09-13（用户：“输入框应该左对齐但是居中了”）：以前不管有没有标签都固定给标签留 110px，
        //    聊天悬浮层那种**没有标签**的输入框就被挤到了右边（看上去像居中）。
        //    现在：给了标签才留位；没标签就让输入框铺满整行（左对齐就是真的左边）。
        bool hasLabel = !string.IsNullOrEmpty(label);
        try
        {
            // 标签固定宽度（与滑条/开关一致），输入框占剩下的宽度
            txt.Rect.anchorMin = new Vector2(0f, 1f);
            txt.Rect.anchorMax = new Vector2(0f, 1f);
            txt.Rect.pivot = new Vector2(0f, 1f);
            txt.Rect.anchoredPosition = Vector2.zero;
            txt.Rect.sizeDelta = new Vector2(hasLabel ? 110f : 0f, h);
            if (!hasLabel) txt.Visible = false;
        }
        catch { }

        var box = NewRect("box", rt);
        try
        {
            float vpad = (h - Theme.UiTheme.ControlH) * 0.5f;
            box.anchorMin = new Vector2(0f, 1f);
            box.anchorMax = new Vector2(1f, 1f);
            box.pivot = new Vector2(0f, 1f);
            box.offsetMin = new Vector2(hasLabel ? 112f : 2f, -(h - vpad));
            box.offsetMax = new Vector2(-2f, -vpad);
        }
        catch { }

        var bg = UiSurface.Build(box, Theme.UiTheme.InputBg, null);

        // 文本**自绘**（原模组 CoopInputBox 同款）：不依赖 TMP_InputField 渲染
        var vtxt = UiText.Create(box, value ?? "", UiTextKind.Value, 0f, TextAlignmentOptions.Left);
        try
        {
            vtxt.Rect.anchorMin = Vector2.zero;
            vtxt.Rect.anchorMax = Vector2.one;
            vtxt.Rect.pivot = new Vector2(0.5f, 0.5f);
            vtxt.Rect.offsetMin = new Vector2(8f, 0f);
            vtxt.Rect.offsetMax = new Vector2(-8f, 0f);
        }
        catch { }

        var input = new UiTextInput(rt, txt, vtxt, bg)
        {
            _value = value ?? "",
            _placeholder = placeholder ?? "",
            _maxLength = maxLength > 0 ? maxLength : 0,
            OnChanged = onChanged,
            OnSubmit = onSubmit,
            IsPassword = password,
        };
        if (input._maxLength > 0 && input._value.Length > input._maxLength)
            input._value = input._value.Substring(0, input._maxLength);
        try { _all.Add(input); } catch { }
        input.Refresh();

        input._zone = Native.UiPointerRouter.Add(new Native.UiHotZone
        {
            Name = string.IsNullOrEmpty(zoneName) ? "input:" + label : zoneName,
            Rect = box,
            Owner = input,
            Bg = bg,
            Tint = false,
            OnClick = () => input.ToggleFocus(),
        });

        Layout.UiMeasure.Register(rt, _ => h, _ => 320f);
        return input;
    }

    /// <summary>聚焦/失焦切换。</summary>
    public void ToggleFocus()
    {
        if (Focused) Blur(); else Focus();
    }

    /// <summary>聚焦（交给输入管线）。</summary>
    public void Focus()
    {
        if (!_interactable) return;
        UiTextRouter.SetFocused(this);
    }

    /// <summary>失焦。</summary>
    public void Blur()
    {
        if (Focused) UiTextRouter.SetFocused(null);
    }

    /// <summary>失焦全部输入框（菜单关闭/切页时调用）。</summary>
    public static void BlurAllInputs() => UiTextRouter.BlurAll();

    // ---------------- 输入管线回调 ----------------

    internal void OnRouterFocus() { Native.UiPointerRouter.TextFocus = true; ResetCaret(); _focusValue = Value; Refresh(); }

    /// <summary>失焦：用户要求“失焦就自动保存” → 内容变了就把值回传（第三方页面靠这个落盘）。</summary>
    internal void OnRouterBlur()
    {
        Native.UiPointerRouter.TextFocus = UiTextRouter.Typing;
        try
        {
            if (!string.Equals(Value ?? "", _focusValue ?? "", StringComparison.Ordinal))
                OnChanged?.Invoke(Value);
        }
        catch (Exception ex) { CoopLog.Warn("uikit.widget", () => "input blur-save failed: " + ex.Message); }
        Refresh();
    }
    internal void RaiseSubmit() { try { OnSubmit?.Invoke(Value); } catch (Exception ex) { CoopLog.Warn("uikit.widget", () => "input submit failed: " + ex.Message); } }

    /// <summary>写值（输入管线与外部都走这里）。<paramref name="notify"/> = 是否触发 OnChanged。</summary>
    internal void SetValueInternal(string v, bool notify)
    {
        _value = v ?? "";        // 最大长度（第三方契约的 MaxLength）：这里是最窄的咽喉 —— 物理键/CJK/IME 提交/退格/外部赋值
        // 全走这个入口，所以只在这拦一次就够了。
        if (_maxLength > 0 && _value.Length > _maxLength) _value = _value.Substring(0, _maxLength);        ResetCaret();                       // 打字/改值 → 光标复位成常亮（停手后才开始闪）
        Refresh();
        if (notify)
        {
            try { OnChanged?.Invoke(_value); } catch (Exception ex) { CoopLog.Warn("uikit.widget", () => "input handler failed: " + ex.Message); }
        }
    }

    /// <summary>渲染：掩码/占位/光标（原模组 CoopInputBox.Render 同款）。</summary>
    internal void Refresh()
    {
        try
        {
            if (_valueText != null)
            {
                string shown = Value;
                if (IsPassword && shown.Length > 0) shown = new string('*', shown.Length);
                if (shown.Length == 0) shown = Focused ? "" : _placeholder;
                // ⚠ 2026-09-13 用户：“现有的第三方 UI 的输入框 Caret 不闪” ——
                //   以前这里直接拼一个固定的 `|`，它永远不会灭。现在由 TickCaret 每 0.5s 翻转 `_caretOn` 再 Refresh
                //   （灭的时候退成空格，宽度不跳）。
                if (Focused) shown += _caretOn ? "|" : " ";
                _valueText.Value = shown;
            }
            if (_bg != null) _bg.color = Focused ? new Color(0.086f, 0.114f, 0.153f, 1f) : Theme.UiTheme.InputBg;
        }
        catch { }
    }

    // ---- 光标闪烁（0.5s）----
    private static bool _caretOn = true;
    private static float _caretT;

    /// <summary>
    /// 每帧：让**聚焦中**的输入框光标闪烁（0.5s 一亮一灭）。由 <c>UiKitBehaviour.Update</c> 调。
    /// 没有聚焦框时把状态复位成“亮”，下一次聚焦立刻能看到光标。
    /// </summary>
    public static void TickCaret(float dt)
    {
        try
        {
            var f = UiTextRouter.Focused as UiTextInput;
            if (f == null || !f.Alive)
            {
                _caretOn = true; _caretT = 0f;
                return;
            }
            if (dt <= 0f) dt = 0.016f;
            _caretT += dt;
            if (_caretT < 0.5f) return;
            _caretT = 0f; _caretOn = !_caretOn;
            f.Refresh();
        }
        catch { }
    }

    /// <summary>聚焦/打字时把光标复位成常亮（跟手，不会打完字看不见光标）。</summary>
    internal static void ResetCaret() { _caretOn = true; _caretT = 0f; }

    /// <summary>测试用：向本框“打进”一段文本（走与真实输入同一个入口）。</summary>
    public static string TryType(string s)
    {
        var f = UiTextRouter.Focused;
        if (f == null) return "没有聚焦的输入框（先 click:input:<id>）";
        for (int i = 0; i < (s ?? "").Length; i++) UiTextRouter.Append(f, s[i]);
        return $"向内 '{f.Label}' 输入 '{s}'（现文本='{f.Value}'）";
    }

    public override void Destroy()
    {
        try { if (Focused) UiTextRouter.SetFocused(null); } catch { }
        try { _all.Remove(this); } catch { }
        Native.UiPointerRouter.Remove(_zone);
        try { Layout.UiMeasure.Unregister(_rt); } catch { }
        base.Destroy();
    }
}

/// <summary>快捷键绑定（点击进入"等待按键"态，按下任意键即绑定；ESC 取消）。</summary>
public sealed class UiKeyBind : UiWidget
{
    private static readonly List<UiKeyBind> _all = new();

    private UiText _label, _valueText;
    private Image _bg;
    private string _value;
    private bool _capturing;

    /// <summary>绑定变化回调。</summary>
    public Action<string> OnChanged;

    private UiKeyBind(RectTransform rt, UiText label, UiText valueText, Image bg) : base(rt)
    {
        _label = label; _valueText = valueText; _bg = bg;
    }

    /// <summary>当前键名。</summary>
    public string Value
    {
        get => _value;
        set { _value = value ?? ""; Refresh(); }
    }

    /// <summary>是否等待按键中。</summary>
    public bool Capturing => _capturing;

    /// <summary>创建快捷键绑定行。</summary>
    public static UiKeyBind Create(Transform parent, string label, string current, Action<string> onChanged, float height = 0f)
    {
        var rt = NewRect("keybind", parent);
        float h = height > 0f ? height : Theme.UiTheme.RowH;
        Theme.UiTheme.SetRect(rt, 0f, 0f, 300f, h);

        var txt = UiText.Create(rt, label, UiTextKind.Body, 0f, TextAlignmentOptions.Left);
        Theme.UiTheme.SetRect(txt.Rect, 0f, 0f, 150f, h);

        var box = NewRect("box", rt);
        var bg = box.gameObject.AddComponent<Image>();
        bg.color = Theme.UiTheme.InputBg;
        try
        {
            box.anchorMin = box.anchorMax = new Vector2(1f, 0.5f);
            box.pivot = new Vector2(1f, 0.5f);
            box.sizeDelta = new Vector2(130f, Theme.UiTheme.ControlH);
            box.anchoredPosition = Vector2.zero;
        }
        catch { }

        var vtxt = UiText.Create(box, current, UiTextKind.Value, 0f, TextAlignmentOptions.Center);
        try
        {
            vtxt.Rect.anchorMin = Vector2.zero;
            vtxt.Rect.anchorMax = Vector2.one;
            vtxt.Rect.pivot = new Vector2(0.5f, 0.5f);
            vtxt.Rect.offsetMin = Vector2.zero;
            vtxt.Rect.offsetMax = Vector2.zero;
        }
        catch { }

        var kb = new UiKeyBind(rt, txt, vtxt, bg) { _value = current ?? "", OnChanged = onChanged };
        try { _all.Add(kb); } catch { }

        Native.UiPointerRouter.Add(new Native.UiHotZone
        {
            Name = "keybind:" + label,
            Rect = box,
            Owner = kb,
            Bg = bg,
            Tint = false,
            OnClick = () => kb.BeginCapture(),
        });

        Layout.UiMeasure.Register(rt, _ => h, _ => 300f);
        return kb;
    }

    /// <summary>开始等待按键。</summary>
    public void BeginCapture()
    {
        if (!_interactable) return;
        for (int i = 0; i < _all.Count; i++) if (_all[i] != null && _all[i] != this) _all[i].CancelCapture();
        _capturing = true;
        if (_valueText != null) _valueText.Value = UiKitLoc.T("按任意键…", "press any key…");
        try { if (_bg != null) _bg.color = Theme.UiTheme.AccentDim; } catch { }
    }

    private void CancelCapture()
    {
        _capturing = false;
        try { if (_bg != null) _bg.color = Theme.UiTheme.InputBg; } catch { }
        Refresh();
    }

    /// <summary>每帧驱动（由菜单窗口调用）：捕获按键。</summary>
    public static void TickAll(float dt)
    {
        try
        {
            if (_all.Count == 0) return;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;
            for (int i = 0; i < _all.Count; i++)
            {
                var k = _all[i];
                if (k == null || !k._capturing) continue;
                string key = ReadFirstPressedKey(kb);
                if (key == null) continue;
                if (string.Equals(key, "escape", StringComparison.OrdinalIgnoreCase)) { k.CancelCapture(); continue; }
                k._capturing = false;
                try { if (k._bg != null) k._bg.color = Theme.UiTheme.InputBg; } catch { }
                k._value = key;
                k.Refresh();
                try { k.OnChanged?.Invoke(key); } catch { }
            }
        }
        catch { }
    }

    private static string ReadFirstPressedKey(UnityEngine.InputSystem.Keyboard kb)
    {
        try
        {
            var keys = kb.allKeys;
            if (keys == null) return null;
            for (int i = 0; i < keys.Count; i++)
            {
                var kc = keys[i];
                if (kc == null) continue;
                bool pressed;
                try { pressed = kc.wasPressedThisFrame; } catch { continue; }
                if (!pressed) continue;
                try { return Normalise(kc.name); } catch { return "key"; }
            }
        }
        catch { }
        return null;
    }

    private static string Normalise(string controlName)
    {
        if (string.IsNullOrEmpty(controlName)) return "key";
        string n = controlName;
        if (n.EndsWith("Key", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 3);
        return n.ToLowerInvariant();
    }

    private void Refresh()
    {
        if (_valueText != null) _valueText.Value = string.IsNullOrEmpty(_value) ? "-" : _value;
    }

    public override void Destroy()
    {
        try { _all.Remove(this); } catch { }
        try { Layout.UiMeasure.Unregister(_rt); } catch { }
        base.Destroy();
    }
}
