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
/// 子菜单入口行（`标签 + 说明 + ›`）：整行可点 → 由调用方 Push 到目标页面。
/// 这是"多级菜单"在界面上的表现（表现与"跳到另一个菜单"一致，不需要重建窗口）。
///
/// 也用作**可折叠分组头**（<c>arrow</c> = ▾/▸）与**可选中列表条目**（<c>selected</c> = 选中高亮）。
/// </summary>
public sealed class UiNavRow : UiWidget
{
    private Image _bg;
    private UiText _label, _hint, _arrow;
    private Image _arrowImg;                 // 贴图箭头（折叠分组用）
    private Image _selBar;                   // 选中态左侧强调条（3px）
    private bool _selected;

    /// <summary>右侧副文本区宽（版本/状态这类信息右对齐成一列）。
    /// ⚠ 不能太宽：它会从主文本那里“抢走”宽度（模组列表左栏只有 440，过宽会让名称被省略号吃掉）。</summary>
    private const float HintW = 110f;

    private UiNavRow(RectTransform rt, Image bg, UiText label, UiText hint, UiText arrow, Image arrowImg, Image selBar) : base(rt)
    {
        _bg = bg; _label = label; _hint = hint; _arrow = arrow; _arrowImg = arrowImg; _selBar = selBar;
    }

    /// <summary>当前是否选中态（可选中列表用）。</summary>
    public bool Selected => _selected;

    /// <summary>切换选中态（只改颜色，不重排）。</summary>
    public void SetSelected(bool on)
    {
        _selected = on;
        try
        {
            if (_bg != null) _bg.color = on ? Theme.UiTheme.RowSelected : Theme.UiTheme.RowBg;
            if (_label != null) _label.Color = on ? Theme.UiTheme.RowSelectedText : Theme.UiTheme.TextPrimary;
            if (_selBar != null) _selBar.enabled = on;      // 左侧强调条只在选中时显示
        }
        catch { }
    }

    /// <summary>主/副文本都改成**单行省略**（信息型列表用：超长名称不换行、不越界）。</summary>
    public void SetSingleLine(bool on)
    {
        try { if (_label != null) _label.SetSingleLine(on); } catch { }
        try { if (_hint != null) _hint.SetSingleLine(on); } catch { }
    }

    /// <summary>改箭头文字（仅文字箭头模式）。</summary>
    public void SetArrow(string arrow)
    {
        try { if (_arrow != null) _arrow.Value = arrow ?? ""; } catch { }
    }

    /// <summary>
    /// 切换折叠指示：<paramref name="expanded"/> = true 时箭头朝下（展开），false 时旋转 90° 朝右（收起）。
    /// 贴图箭头模式用旋转，文字箭头模式退成 `-`/`+`（两者都是 **ASCII 安全**的）。
    /// </summary>
    public void SetArrowExpanded(bool expanded)
    {
        try
        {
            if (_arrowImg != null)
            {
                _arrowImg.rectTransform.localRotation = expanded ? Quaternion.identity : Quaternion.Euler(0f, 0f, 90f);
                return;
            }
            SetArrow(expanded ? "-" : "+");
        }
        catch { }
    }

    /// <summary>
    /// 创建子菜单入口行。<paramref name="zoneName"/> 可覆盖热区名（默认 <c>nav:标签</c>），自动化脚本用它做语言无关寻址。
    ///
    /// <paramref name="arrowText"/>：null = 默认 `›`；空串 = 不画箭头。
    /// <paramref name="triangleArrow"/>：true = 用**程序化三角贴图**代替文字箭头（折叠分组用，
    /// 因为游戏字体没有 `\u25be`/`\u25b8` 这两个字形的字形，实测渲染成 tofu 方框）。
    /// </summary>
    public static UiNavRow Create(Transform parent, string label, string hint, Action onClick, float height = 0f, string zoneName = null,
        bool selected = false, string arrowText = null, bool triangleArrow = false)
    {
        var rt = NewRect("nav", parent);
        float h = height > 0f ? height : Theme.UiTheme.RowH + 6f;
        Theme.UiTheme.SetRect(rt, 0f, 0f, 300f, h);

        var bg = rt.gameObject.AddComponent<Image>();
        bg.color = selected ? Theme.UiTheme.RowSelected : Theme.UiTheme.RowBg;

        // 选中态左侧强调条（3px，贴在行左缘；只在选中时可见）——
        // 比“只换底色”更能一眼看出选中的是哪一行（工业拟真风用 Accent 金色）。
        var selBar = NewRect("selbar", rt);
        var selBarImg = selBar.gameObject.AddComponent<Image>();
        selBarImg.color = Theme.UiTheme.Accent;
        selBarImg.raycastTarget = false;
        selBarImg.enabled = selected;
        try
        {
            selBar.anchorMin = new Vector2(0f, 0f);
            selBar.anchorMax = new Vector2(0f, 1f);
            selBar.pivot = new Vector2(0f, 0.5f);
            selBar.sizeDelta = new Vector2(3f, 0f);
            selBar.anchoredPosition = Vector2.zero;
        }
        catch { }

        // 扁平工业风：行底**发丝线**（贴下沿 1px，不占布局高度）→ 整列看起来是“钢面板 + 网格线”。
        Theme.ModStyle.BottomRule(rt, Theme.UiTheme.Hairline, Theme.UiTheme.OutlineW);

        var txt = UiText.Create(rt, label, UiTextKind.Body, 0f, TextAlignmentOptions.Left);
        try
        {
            txt.Rect.anchorMin = new Vector2(0f, 0f);
            txt.Rect.anchorMax = new Vector2(1f, 1f);
            txt.Rect.pivot = new Vector2(0f, 0.5f);
            txt.Rect.offsetMin = new Vector2(10f, 0f);
            // ⚠ 有副文本（右侧版本/状态）时主文本必须**让出固定区**，否则长名称会盖住它
            txt.Rect.offsetMax = new Vector2(string.IsNullOrEmpty(hint) ? -24f : -(HintW + 32f), 0f);
        }
        catch { }
        txt.Color = Theme.UiTheme.TextPrimary;

        UiText hintTxt = null;
        if (!string.IsNullOrEmpty(hint))
        {
            hintTxt = UiText.Create(rt, hint, UiTextKind.Note, 0f, TextAlignmentOptions.MidlineRight);
            try
            {
                hintTxt.Rect.anchorMin = new Vector2(1f, 0f);
                hintTxt.Rect.anchorMax = new Vector2(1f, 1f);
                hintTxt.Rect.pivot = new Vector2(1f, 0.5f);
                hintTxt.Rect.offsetMin = new Vector2(-(HintW + 24f), 0f);
                hintTxt.Rect.offsetMax = new Vector2(-24f, 0f);
                hintTxt.Rect.sizeDelta = new Vector2(HintW, 0f);
            }
            catch { }
            try { hintTxt.SetSingleLine(true); } catch { }   // 副文本也单行省略
        }

        // arrowText：null = 默认 '›'（子菜单）；空串 = **不画箭头**（可选中列表那种）
        UiText arrow = null;        Image arrowImg = null;
        if (triangleArrow)
        {
            var tri = Native.NativeWidgets.TriangleSprite();
            if (tri != null)
            {
                var go = NewRect("arrow", rt);
                try
                {
                    go.anchorMin = go.anchorMax = new Vector2(1f, 0.5f);
                    go.pivot = new Vector2(1f, 0.5f);
                    go.sizeDelta = new Vector2(11f, 11f);
                    go.anchoredPosition = new Vector2(-12f, 0f);
                }
                catch { }
                arrowImg = go.gameObject.AddComponent<Image>();
                arrowImg.sprite = tri;
                arrowImg.type = Image.Type.Simple;
                arrowImg.color = Theme.UiTheme.Accent;
                arrowImg.raycastTarget = false;
            }
        }
        if (arrowImg == null)
        {
            arrow = UiText.Create(rt, arrowText ?? ">", UiTextKind.Title, 0f, TextAlignmentOptions.Center);
            try
            {
                arrow.Rect.anchorMin = new Vector2(1f, 0f);
                arrow.Rect.anchorMax = new Vector2(1f, 1f);
                arrow.Rect.pivot = new Vector2(1f, 0.5f);
                arrow.Rect.offsetMin = new Vector2(-22f, 0f);
                arrow.Rect.offsetMax = new Vector2(-6f, 0f);
                arrow.Rect.sizeDelta = new Vector2(16f, 0f);
            }
            catch { }
            arrow.Color = Theme.UiTheme.Accent;
        }

        var row = new UiNavRow(rt, bg, txt, hintTxt, arrow, arrowImg, selBarImg);
        Native.UiPointerRouter.Add(new Native.UiHotZone
        {
            Name = !string.IsNullOrEmpty(zoneName) ? zoneName : "nav:" + label,
            Rect = rt,
            Owner = row,
            Bg = bg,
            Tint = true,
            BaseColor = selected ? Theme.UiTheme.RowSelected : Theme.UiTheme.RowBg,
            HoverColor = Theme.UiTheme.RowHover,
            PressColor = Theme.UiTheme.RowSelected,
            OnClick = () => { try { onClick?.Invoke(); } catch (Exception ex) { CoopLog.Warn("uikit.widget", () => "nav click failed: " + ex.Message); } },
        });

        Layout.UiMeasure.Register(rt, _ => h, _ => 300f);
        row.SetSelected(selected);
        return row;
    }

    /// <summary>改文案。</summary>
    public void SetText(string label, string hint = null)
    {
        if (_label != null) _label.Value = label;
        if (_hint != null && hint != null) _hint.Value = hint;
    }

    public override void Destroy()
    {
        try { Layout.UiMeasure.Unregister(_rt); } catch { }
        base.Destroy();
    }
}

/// <summary>只读信息行（`标签 … 值`）。</summary>
public sealed class UiInfoRow : UiWidget
{
    private UiText _label, _value;

    private UiInfoRow(RectTransform rt, UiText label, UiText value) : base(rt) { _label = label; _value = value; }

    /// <summary>创建信息行。</summary>
    public static UiInfoRow Create(Transform parent, string label, string value, float height = 0f)
    {
        var rt = NewRect("info", parent);
        float h = height > 0f ? height : Theme.UiTheme.RowH;
        Theme.UiTheme.SetRect(rt, 0f, 0f, 300f, h);

        var l = UiText.Create(rt, label, UiTextKind.Body, 0f, TextAlignmentOptions.Left);
        try
        {
            l.Rect.anchorMin = new Vector2(0f, 0f);
            l.Rect.anchorMax = new Vector2(0.5f, 1f);
            l.Rect.pivot = new Vector2(0f, 0.5f);
            l.Rect.offsetMin = new Vector2(0f, 0f);
            l.Rect.offsetMax = new Vector2(0f, 0f);
        }
        catch { }

        var v = UiText.Create(rt, value, UiTextKind.Value, 0f, TextAlignmentOptions.MidlineRight);
        try
        {
            v.Rect.anchorMin = new Vector2(0.5f, 0f);
            v.Rect.anchorMax = new Vector2(1f, 1f);
            v.Rect.pivot = new Vector2(1f, 0.5f);
            v.Rect.offsetMin = new Vector2(0f, 0f);
            v.Rect.offsetMax = new Vector2(0f, 0f);
        }
        catch { }

        Layout.UiMeasure.Register(rt, _ => h, _ => 300f);
        return new UiInfoRow(rt, l, v);
    }

    /// <summary>改值。</summary>
    public void SetValue(string s) { if (_value != null) _value.Value = s; }

    public override void Destroy()
    {
        try { Layout.UiMeasure.Unregister(_rt); } catch { }
        base.Destroy();
    }
}

/// <summary>
/// 键值行（`标签 | 值`，详情页专用）：**标签列固定宽 + 值列左对齐**。
///
/// 扁平工业风的版式要点：多行键值下来，标签与值**各自成一列**（靠列对齐读出结构），
/// 而不是把“标签：值”挤成一行散文（长路径/长文案会把标签挤走，也扫不出列）。
/// </summary>
public sealed class UiKvRow : UiWidget
{
    private UiText _label, _value;

    private UiKvRow(RectTransform rt, UiText label, UiText value) : base(rt) { _label = label; _value = value; }

    /// <summary>创建键值行（<paramref name="height"/> = 0 → 用 <see cref="Theme.UiTheme.RowH"/>）。</summary>
    public static UiKvRow Create(Transform parent, string label, string value, float height = 0f)
    {
        var rt = NewRect("kv", parent);
        float h = height > 0f ? height : Theme.UiTheme.RowH;
        Theme.UiTheme.SetRect(rt, 0f, 0f, 300f, h);

        float lw = Theme.UiTheme.KvLabelW;
        var l = UiText.Create(rt, label, UiTextKind.Note, 0f, TextAlignmentOptions.Left);
        try
        {
            l.Rect.anchorMin = new Vector2(0f, 0f);
            l.Rect.anchorMax = new Vector2(0f, 1f);
            l.Rect.pivot = new Vector2(0f, 0.5f);
            l.Rect.offsetMin = new Vector2(0f, 0f);
            l.Rect.offsetMax = new Vector2(lw, 0f);
        }
        catch { }

        var v = UiText.Create(rt, value, UiTextKind.Value, 0f, TextAlignmentOptions.Left);
        try
        {
            v.Rect.anchorMin = new Vector2(0f, 0f);
            v.Rect.anchorMax = new Vector2(1f, 1f);
            v.Rect.pivot = new Vector2(0f, 0.5f);
            v.Rect.offsetMin = new Vector2(lw + 8f, 0f);
            v.Rect.offsetMax = new Vector2(0f, 0f);
        }
        catch { }
        try { v.SetSingleLine(true); } catch { }       // 长路径/长文本单行省略（行高固定，折行会溢出）

        Layout.UiMeasure.Register(rt, _ => h, _ => 300f);
        return new UiKvRow(rt, l, v);
    }

    /// <summary>改值。</summary>
    public void SetValue(string s) { if (_value != null) _value.Value = s; }

    public override void Destroy()
    {
        try { Layout.UiMeasure.Unregister(_rt); } catch { }
        base.Destroy();
    }
}

/// <summary>动作行（`标签 + [按钮]`）。</summary>
public sealed class UiActionRow : UiWidget
{
    private UiButton _button;
    private UiText _label;

    private UiActionRow(RectTransform rt, UiText label, UiButton button) : base(rt) { _label = label; _button = button; }

    /// <summary>
    /// 创建动作行。
    ///
    /// ⚠ 2026-09-13：**整行可点**（不只是右侧按钮）—— 原模组的列表/详情栏都是“点行即选中/执行”
    /// （用户反馈“交互还是差点”）。做法：先登记一个整行热区（*后登记的热区命中优先* →
    /// 按钮自己的热区后建，所以按钮区域仍然归按钮，其余区域归整行）。
    /// </summary>
    public static UiActionRow Create(Transform parent, string label, string buttonText, Action onClick, float height = 0f)
    {
        var rt = NewRect("action", parent);
        float h = height > 0f ? height : Theme.UiTheme.RowH + 4f;
        Theme.UiTheme.SetRect(rt, 0f, 0f, 300f, h);

        // 整行背景（悬停/按下反馈）+ 整行热区（先登记，优先级低于右侧按钮）
        var rowBg = rt.gameObject.AddComponent<Image>();
        rowBg.color = Theme.UiTheme.RowBg;
        rowBg.raycastTarget = false;
        var rowZone = Native.UiPointerRouter.Add(new Native.UiHotZone
        {
            Name = "row:" + StripTags(label),
            Rect = rt,
            Bg = rowBg,
            Tint = true,
            BaseColor = Theme.UiTheme.RowBg,
            HoverColor = Theme.UiTheme.RowHover,
            PressColor = Theme.UiTheme.RowSelected,
            OnClick = () => { try { onClick?.Invoke(); } catch (Exception ex) { CoopLog.Warn("uikit.widget", () => "row click failed: " + ex.Message); } },
        });

        var l = UiText.Create(rt, label, UiTextKind.Body, 0f, TextAlignmentOptions.Left);
        try
        {
            l.Rect.anchorMin = new Vector2(0f, 0f);
            l.Rect.anchorMax = new Vector2(1f, 1f);
            l.Rect.pivot = new Vector2(0f, 0.5f);
            l.Rect.offsetMin = new Vector2(0f, 0f);
            l.Rect.offsetMax = new Vector2(-130f, 0f);
            l.SetSingleLine(true);      // 长模组名不换行（行高固定，折行会溢出到下一行）
        }
        catch { }

        var btn = UiButton.Create(rt, buttonText, onClick, UiButtonStyle.Secondary, 120f, Theme.UiTheme.ButtonH);
        try
        {
            btn.Rect.anchorMin = btn.Rect.anchorMax = new Vector2(1f, 0.5f);
            btn.Rect.pivot = new Vector2(1f, 0.5f);
            btn.Rect.anchoredPosition = Vector2.zero;
        }
        catch { }

        Layout.UiMeasure.Register(rt, _ => h, _ => 300f);
        var actRow = new UiActionRow(rt, l, btn);
        rowZone.Owner = actRow;      // 归属登记：声明式渲染层靠它绑悬停提示（以前漏了 → 会被当成“没有热区”再补一个）
        return actRow;
    }

    public override void Destroy()
    {
        try { Layout.UiMeasure.Unregister(_rt); } catch { }
        base.Destroy();
    }

    /// <summary>去掉 TMP 富文本标签（热区名用：自动化脚本要的是纯文本，不能带 `&lt;color=…&gt;`）。</summary>
    private static string StripTags(string s)
    {
        if (string.IsNullOrEmpty(s)) return "-";
        var sb = new System.Text.StringBuilder(s.Length);
        bool inTag = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }
}
