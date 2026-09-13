using System;
using UnityEngine;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestUIKit.Widgets;

/// <summary>文本语义样式（字号/颜色/是否加粗）。</summary>
public enum UiTextKind
{
    /// <summary>窗口/页面主标题。</summary>
    Title = 0,
    /// <summary>分组标题（页内小节）。</summary>
    Header = 1,
    /// <summary>正文（行标签）。</summary>
    Body = 2,
    /// <summary>次要说明（灰色小字）。</summary>
    Note = 3,
    /// <summary>值/数字（右对齐常用）。</summary>
    Value = 4,
}

/// <summary>
/// 文本组件：**自动换行 + 内容驱动高度**（动态布局的基础：高度不写死，由 <see cref="Layout.UiMeasure"/> 测）。
/// </summary>
public sealed class UiText : UiWidget
{
    private TextMeshProUGUI _txt;

    private UiText(RectTransform rt, TextMeshProUGUI txt) : base(rt) { _txt = txt; }

    /// <summary>TMP 组件（高级用法）。</summary>
    public TextMeshProUGUI Text => _txt;

    /// <summary>文本内容（赋值后可调 <c>UiFlow.Apply()</c> 重排）。</summary>
    public string Value
    {
        get { try { return _txt != null ? _txt.text : ""; } catch { return ""; } }
        set { try { if (_txt != null) _txt.text = value ?? ""; } catch { } }
    }

    /// <summary>字号。</summary>
    public float FontSize
    {
        get { try { return _txt != null ? _txt.fontSize : 0f; } catch { return 0f; } }
        set { try { if (_txt != null) _txt.fontSize = value; } catch { } }
    }

    /// <summary>颜色。</summary>
    public Color Color
    {
        get { try { return _txt != null ? _txt.color : Color.white; } catch { return Color.white; } }
        set { try { if (_txt != null) _txt.color = value; } catch { } }
    }

    /// <summary>对齐（TMP 语义：Left/TopLeft…注意 `Left` 是垂直居中）。</summary>
    public TextAlignmentOptions Align
    {
        get { try { return _txt != null ? _txt.alignment : TextAlignmentOptions.TopLeft; } catch { return TextAlignmentOptions.TopLeft; } }
        set { try { if (_txt != null) _txt.alignment = value; } catch { } }
    }

    /// <summary>字距（工业风的标题/分组名拉开一点看着更“制式”；0 = 默认）。</summary>
    public float LetterSpacing
    {
        get { try { return _txt != null ? _txt.characterSpacing : 0f; } catch { return 0f; } }
        set { try { if (_txt != null) _txt.characterSpacing = value; } catch { } }
    }

    /// <summary>创建文本。</summary>
    public static UiText Create(Transform parent, string text, UiTextKind kind = UiTextKind.Body,
        float width = 0f, TextAlignmentOptions? align = null)
    {
        var rt = NewRect("text", parent);
        if (width > 0f) rt.sizeDelta = new Vector2(width, 10f);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.raycastTarget = false;
        t.richText = true;
        ApplyKind(t, kind);
        t.alignment = align ?? DefaultAlign(kind);
        UI.UiKit.EnsureFont(t);
        t.text = text ?? "";
        return new UiText(rt, t);
    }

    /// <summary>单行模式（不换行 + 超长省略号）——标题/行标签用，避免长文案折成两行溢出固定行高。</summary>
    public void SetSingleLine(bool on)
    {
        try
        {
            if (_txt == null) return;
            _txt.enableWordWrapping = !on;
            if (on) _txt.overflowMode = TextOverflowModes.Ellipsis;
        }
        catch { }
    }

    /// <summary>按语义样式设置字号/颜色/加粗。</summary>
    public static void ApplyKind(TMP_Text t, UiTextKind kind)
    {
        if (t == null) return;
        try
        {
            switch (kind)
            {
                case UiTextKind.Title:
                    t.fontSize = Theme.UiTheme.FontTitle; t.color = Theme.UiTheme.TextPrimary; t.fontStyle = FontStyles.Bold; break;
                case UiTextKind.Header:
                    t.fontSize = Theme.UiTheme.FontHeader; t.color = Theme.UiTheme.Accent; t.fontStyle = FontStyles.Bold; break;
                case UiTextKind.Note:
                    t.fontSize = Theme.UiTheme.FontSmall; t.color = Theme.UiTheme.TextDim; t.fontStyle = FontStyles.Normal; break;
                case UiTextKind.Value:
                    t.fontSize = Theme.UiTheme.FontValue; t.color = Theme.UiTheme.TextPrimary; t.fontStyle = FontStyles.Normal; break;
                default:
                    t.fontSize = Theme.UiTheme.FontBody; t.color = Theme.UiTheme.TextSecondary; t.fontStyle = FontStyles.Normal; break;
            }
            t.enableAutoSizing = false;
        }
        catch { }
    }

    private static TextAlignmentOptions DefaultAlign(UiTextKind kind)
        => kind == UiTextKind.Title || kind == UiTextKind.Header ? TextAlignmentOptions.Left
         : kind == UiTextKind.Value ? TextAlignmentOptions.MidlineRight
         : TextAlignmentOptions.TopLeft;

    /// <summary>设置文本样式（字号/颜色/对齐/换行）。</summary>
    public void Style(UiTextKind kind)
    {
        ApplyKind(_txt, kind);
        Align = DefaultAlign(kind);
    }

    /// <summary>换行开关（默认开：动态布局要靠它算高度）。</summary>
    public void SetWrap(bool wrap)
    {
        try { if (_txt != null) _txt.enableWordWrapping = wrap; } catch { }
    }
}
