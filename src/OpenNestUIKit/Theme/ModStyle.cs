using System;
using UnityEngine;
using UnityEngine.UI;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestUIKit.Theme;

/// <summary>
/// **本库自有风格**（第三方风格，故意**不模仿**游戏原生）。
///
/// 为什么单独一套：原生 ESC 菜单是"羊皮纸 + 装饰框"（`UI Box Castile` 带坦克装饰端、
/// `line` 细框、`Double line` 双线），块与块的区分**全靠素材本身**；我们要是一起抄，
/// 就会被当成"原生第 N 组"，还得跟着它的手调倍率（ppuMul 1/2/2.5/3/4）跑，永远差一点。
/// 所以这里干脆做一套**一眼看得出来是模组**的皮：
/// - 扁平深色块 + **1px 冷色描边**（程序化画的，不依赖任何游戏素材）；
/// - 左侧**强调色竖条**（模组条目/当前项）；
/// - 自己的字号与内边距（不抄原生的 20/25）。
///
/// 全部用纯色 Image 拼（1x1 白底随颜色缩放），所以**不需要任何 sprite**，
/// 也不受 `pixelsPerUnitMultiplier` / 九宫格 border 影响 —— 尺寸所见即所得。
/// </summary>
public static class ModStyle
{
    // ---- 调色板（冷调深色 + 琥珀强调，和游戏暖色羊皮纸明显区隔）----
    public static readonly Color RowBg = new Color(0.086f, 0.129f, 0.176f, 0.96f);
    public static readonly Color RowBgHot = new Color(0.149f, 0.216f, 0.290f, 1f);
    public static readonly Color RowBgPress = new Color(0.223f, 0.161f, 0.063f, 1f);
    public static readonly Color Edge = new Color(0.286f, 0.451f, 0.588f, 1f);
    public static readonly Color Accent = new Color(0.976f, 0.706f, 0.243f, 1f);
    public static readonly Color Text = new Color(0.945f, 0.960f, 0.976f, 1f);
    public static readonly Color TextDim = new Color(0.639f, 0.694f, 0.749f, 1f);
    public static readonly Color HeaderBg = new Color(0.059f, 0.086f, 0.122f, 1f);

    // ---- 尺寸 ----
    public const float OutlineW = 1f;      // 描边粗细
    public const float AccentW = 3f;       // 左侧强调条宽
    public const float RowH = 34f;         // 模组条目行高（比原生 38/40 矮一点，自成一块）
    public const float RowW = 250f;
    public const float FontSize = 20f;     // 自己的字号（不与原生"对齐"）
    public const float PadX = 14f;

    private static Sprite _white;

    /// <summary>1x1 白底（所有程序化色块都用它，避免任何游戏素材）。</summary>
    public static Sprite White
    {
        get
        {
            if (_white == null)
            {
                try
                {
                    var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    tex.SetPixel(0, 0, Color.white);
                    tex.Apply();
                    _white = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 100f);
                    _white.name = "OpenNestUIKit_White";
                }
                catch (Exception ex) { CoopLog.Warn("uikit.modstyle", () => "White sprite: " + ex.Message); }
            }
            return _white;
        }
    }

    /// <summary>铺满父节点的纯色块。</summary>
    public static Image Fill(Transform parent, string name, Color c)
    {
        var rt = UI.UiKit.MakeRectFill(name, parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = White;
        img.type = Image.Type.Simple;
        img.color = c;
        img.raycastTarget = false;
        return img;
    }

    /// <summary>四条细边拼出的**程序化描边**（不依赖九宫格素材，粗细=像素级所见即所得）。</summary>
    public static void Outline(Transform parent, Color c, float t = OutlineW)
    {
        try
        {
            Edges(parent, "edge.t", c, 0f, 1f, Anchor.Top, t);
            Edges(parent, "edge.b", c, 0f, 0f, Anchor.Bottom, t);
            Edges(parent, "edge.l", c, 0f, 0f, Anchor.Left, t);
            Edges(parent, "edge.r", c, 1f, 0f, Anchor.Right, t);
        }
        catch (Exception ex) { CoopLog.Warn("uikit.modstyle", () => "Outline: " + ex.Message); }
    }

    private enum Anchor { Top, Bottom, Left, Right }

    private static void Edges(Transform parent, string name, Color c, float ax, float ay, Anchor a, float t)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        var img = go.AddComponent<Image>();
        img.sprite = White;
        img.color = c;
        img.raycastTarget = false;

        switch (a)
        {
            case Anchor.Top:
                rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.offsetMin = new Vector2(0f, -t); rt.offsetMax = Vector2.zero;
                break;
            case Anchor.Bottom:
                rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.offsetMin = Vector2.zero; rt.offsetMax = new Vector2(0f, t);
                break;
            case Anchor.Left:
                rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 0.5f);
                rt.offsetMin = Vector2.zero; rt.offsetMax = new Vector2(t, 0f);
                break;
            default:
                rt.anchorMin = new Vector2(1f, 0f); rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 0.5f);
                rt.offsetMin = new Vector2(-t, 0f); rt.offsetMax = Vector2.zero;
                break;
        }
    }

    /// <summary>左侧强调色竖条（标"这是模组功能"）。</summary>
    public static void AccentBar(Transform parent, Color c, float w = AccentW)
    {
        var go = new GameObject("accent");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.offsetMin = new Vector2(0f, 1f);
        rt.offsetMax = new Vector2(w, -1f);
        var img = go.AddComponent<Image>();
        img.sprite = White;
        img.color = c;
        img.raycastTarget = false;
    }

    /// <summary>顶部发丝线（贴父容器上沿 1px，**不占布局高度**）—— 扁平工业风的"分格线"。</summary>
    public static void TopRule(Transform parent, Color c, float t = OutlineW)
        => Edges(parent, "rule.t", c, 0f, 0f, Anchor.Top, t);

    /// <summary>底部发丝线（贴父容器下沿 1px，**不占布局高度**）。行与行之间靠它形成网格。</summary>
    public static void BottomRule(Transform parent, Color c, float t = OutlineW)
        => Edges(parent, "rule.b", c, 0f, 0f, Anchor.Bottom, t);

    /// <summary>左侧发丝线。</summary>
    public static void LeftRule(Transform parent, Color c, float t = OutlineW)
        => Edges(parent, "rule.l", c, 0f, 0f, Anchor.Left, t);

    /// <summary>
    /// 建**模组风格**的一行（原生 ESC 菜单里的入口 / 我们的原生页行）：
    /// 扁平底 + 描边 + 左强调条 + 靠左文字，悬停/按下用自己的色。
    /// </summary>
    public static Button Row(Transform parent, string name, string text, Action onClick,
        float width = RowW, float height = RowH, bool accent = true, Color? fill = null)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(width, height);

        var bg = Fill(rt, "bg", fill ?? RowBg);
        Outline(rt, Edge);
        if (accent) AccentBar(rt, Accent);

        var txtGo = new GameObject("Text");
        txtGo.transform.SetParent(rt, false);
        var trt = txtGo.AddComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(accent ? AccentW + PadX : PadX, 2f);
        trt.offsetMax = new Vector2(-PadX, -2f);
        var txt = txtGo.AddComponent<TextMeshProUGUI>();
        UI.UiKit.EnsureFont(txt);
        txt.text = text ?? "";
        txt.fontSize = FontSize;
        txt.enableAutoSizing = false;
        txt.alignment = TextAlignmentOptions.Left;
        txt.color = Text;
        txt.raycastTarget = false;

        var btn = go.AddComponent<Button>();
        try { btn.targetGraphic = bg; } catch { }
        var cb = btn.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = new Color(1.35f, 1.45f, 1.6f, 1f);   // 乘在底色上 → 悬停亮一档
        cb.pressedColor = new Color(1.6f, 1.25f, 0.75f, 1f);        // 按下偏强调色
        cb.selectedColor = Color.white;
        cb.disabledColor = new Color(0.7f, 0.7f, 0.7f, 0.8f);
        cb.fadeDuration = 0.06f;
        try { btn.colors = cb; } catch { }
        try
        {
            btn.onClick.AddListener(new Action(() =>
            {
                try { onClick?.Invoke(); }
                catch (Exception ex) { CoopLog.Warn("uikit.modstyle", () => "row click failed: " + ex.Message); }
            }));
        }
        catch (Exception ex) { CoopLog.Warn("uikit.modstyle", () => "AddListener failed: " + ex.Message); }

        return btn;
    }
}
