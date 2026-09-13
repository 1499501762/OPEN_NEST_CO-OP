// ⚠️ 本文件是 ModMenu **自有** UI 代码（不来自 Vendor 副本），可自由演进。
using System;
using UnityEngine;
using UnityEngine.UI;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestModMenu.UI;

/// <summary>
/// 覆盖 UI 的视觉标准：调色板 + 尺寸常量 + 小部件（来源标签 pill）。
///
/// 集中在这里的原因：界面代码只谈布局与状态，颜色/字号改一处即全局统一；
/// 将来把纯色换成原生素材（§5.3）时也只动这里。
///
/// 配色取向：**深色玻璃面板 + 琥珀强调色**（取自游戏仪表盘指针的琥珀色），贴合 Iron Nest 的工业质感。
/// </summary>
public static class ModMenuTheme
{
    // ---------------- 调色板 ----------------

    public static readonly Color Backdrop = new Color(0f, 0f, 0f, 0.62f);
    public static readonly Color PanelBg = new Color(0.063f, 0.078f, 0.102f, 0.98f);
    public static readonly Color HeaderBg = new Color(0.090f, 0.114f, 0.149f, 1f);
    public static readonly Color CardBg = new Color(0.047f, 0.063f, 0.082f, 1f);
    public static readonly Color Border = new Color(0.165f, 0.196f, 0.239f, 1f);
    public static readonly Color Accent = new Color(0.878f, 0.635f, 0.235f, 1f);
    public static readonly Color AccentDim = new Color(0.560f, 0.400f, 0.150f, 1f);

    public static readonly Color RowBg = new Color(0.086f, 0.106f, 0.133f, 1f);
    public static readonly Color RowSelected = new Color(0.118f, 0.180f, 0.267f, 1f);
    public static readonly Color ChipBg = new Color(0.098f, 0.120f, 0.153f, 1f);
    public static readonly Color ChipOn = new Color(0.310f, 0.235f, 0.098f, 1f);
    public static readonly Color ButtonBg = new Color(0.118f, 0.145f, 0.184f, 1f);

    public static readonly Color TextPrimary = new Color(0.902f, 0.918f, 0.941f, 1f);
    public static readonly Color TextSecond = new Color(0.604f, 0.651f, 0.710f, 1f);
    public static readonly Color TextDim = new Color(0.424f, 0.467f, 0.529f, 1f);
    public static readonly Color Ok = new Color(0.498f, 0.816f, 0.541f, 1f);
    public static readonly Color Warn = new Color(0.878f, 0.635f, 0.235f, 1f);
    public static readonly Color Err = new Color(0.878f, 0.424f, 0.424f, 1f);

    // 来源标签 pill 底色（三色区分 BepInEx / 经桥 / 原生 ML）
    public static readonly Color PillBepInEx = new Color(0.169f, 0.298f, 0.435f, 1f);
    public static readonly Color PillBridge = new Color(0.420f, 0.290f, 0.141f, 1f);
    public static readonly Color PillMelon = new Color(0.153f, 0.333f, 0.227f, 1f);
    public static readonly Color PillUnknown = new Color(0.200f, 0.227f, 0.267f, 1f);

    // ---------------- 尺寸（参考分辨率 1920×1080） ----------------

    public const float PanelW = 1180f, PanelH = 700f;
    public const float PanelX = (1920f - PanelW) / 2f, PanelY = (1080f - PanelH) / 2f;
    public const float HeaderH = 50f, FooterH = 30f, Pad = 14f, Gap = 12f;
    public const float ColLeftW = 440f;
    public const float ColRightW = PanelW - Pad - ColLeftW - Gap - Pad;   // 700
    public const float BodyY = HeaderH + Gap;                             // 62
    public const float BodyH = PanelH - BodyY - FooterH - Pad;            // 594
    public const float RowH = 34f, RowGap = 4f;
    public const float PillW = 64f, PillH = 20f;
    public const float LineH = 25f;                                       // 详情单行高

    // 字号
    public const int FontTitle = 20, FontSubtitle = 12, FontChip = 12;
    public const int FontRow = 14, FontRowMeta = 11, FontPill = 11;
    public const int FontDetailName = 19, FontLabel = 13, FontValue = 14, FontStatus = 12;

    // ---------------- 小工具 ----------------

    /// <summary>颜色 → TMP 富文本用 #RRGGBB（不用 ColorUtility，避免两端 interop 差异）。</summary>
    public static string Hex(Color c)
    {
        int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
        int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
        int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
        return "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
    }

    /// <summary>按"左上角锚点 + 负 y"重新摆位（配合 <see cref="UiKit.Place"/> 建的控件做动态纵向排版）。</summary>
    public static void SetTopLeft(RectTransform rt, float x, float y)
    {
        if (rt == null) return;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
    }

    /// <summary>悬停色（比底色亮一档；自管指针命中时手动套用）。</summary>
    public static Color Hover(Color c)
        => new Color(Mathf.Min(c.r * 1.35f + 0.04f, 1f), Mathf.Min(c.g * 1.35f + 0.04f, 1f), Mathf.Min(c.b * 1.35f + 0.04f, 1f), c.a);

    /// <summary>按钮观感：悬停提亮、按下压暗（UGUI ColorTint 的倍乘色）。</summary>
    public static void StyleButton(Button b, Color bg)
    {
        if (b == null) return;
        try
        {
            var img = b.targetGraphic as Image;
            if (img != null) img.color = bg;
            var cb = b.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.22f, 1.22f, 1.22f, 1f);
            cb.pressedColor = new Color(0.78f, 0.78f, 0.78f, 1f);
            cb.selectedColor = new Color(1.06f, 1.06f, 1.06f, 1f);
            cb.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.6f);
            cb.fadeDuration = 0.08f;
            b.colors = cb;
        }
        catch { }
    }

    /// <summary>创建来源标签 pill（底色块 + 居中短文字）。</summary>
    public static UiPill MakePill(Transform parent, float x, float y, float w, float h)
    {
        var rt = UiKit.Place("pill", parent, x, y, w, h);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = PillUnknown;
        img.raycastTarget = false;

        var txtRt = UiKit.MakeRectFill("txt", rt);
        var txt = txtRt.gameObject.AddComponent<TextMeshProUGUI>();
        txt.fontSize = FontPill;
        txt.color = TextPrimary;
        txt.alignment = TextAlignmentOptions.Center;
        txt.raycastTarget = false;
        try
        {
            txt.enableWordWrapping = false;
            txt.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            txt.rectTransform.offsetMin = new Vector2(4f, 0f);
            txt.rectTransform.offsetMax = new Vector2(-4f, 0f);
        }
        catch { }
        UiKit.EnsureFont(txt);

        return new UiPill { Rt = rt, Bg = img, Text = txt };
    }
}

/// <summary>来源标签 pill（可复用的小部件：只改文字/底色/可见性，不重建对象）。</summary>
public sealed class UiPill
{
    public RectTransform Rt;
    public Image Bg;
    public TextMeshProUGUI Text;

    public void Set(string label, Color bg)
    {
        try
        {
            if (Text != null) Text.text = label ?? "";
            if (Bg != null) Bg.color = bg;
        }
        catch { }
    }

    public void SetActive(bool on)
    {
        try { if (Rt != null) Rt.gameObject.SetActive(on); } catch { }
    }
}
