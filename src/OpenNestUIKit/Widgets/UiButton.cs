using System;
using UnityEngine;
using UnityEngine.UI;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestUIKit.Widgets;

/// <summary>按钮风格。</summary>
public enum UiButtonStyle
{
    /// <summary>普通按钮（深灰）。</summary>
    Secondary = 0,
    /// <summary>主按钮（琥珀强调色）。</summary>
    Primary = 1,
    /// <summary>危险按钮（红）。</summary>
    Danger = 2,
    /// <summary>无底色按钮（文字按钮/列表项）。</summary>
    Ghost = 3,
}

/// <summary>
/// 表面（带原生观感的底）：**纯色填充层 + 原生 sprite 装饰层**双层结构。
///
/// 为什么要双层：原生 `UI Box Castile` 等装饰框**中心是透明的**（`docs/NATIVE_UI.md` §6 结论），
/// 直接把它当背景会"看不见面板"；所以底色永远由纯色层提供，sprite 只负责边框/花纹。
/// 这也是 `native-ui-sprites.md` 里"必须保留实心填充底色"那条教训的落地方式。
/// </summary>
public static class UiSurface
{
    /// <summary>在 <paramref name="parent"/> 下建"填充 + 装饰"两层（都铺满父容器）。返回填充层 Image。</summary>
    ///
    /// ⚠️ 装饰层必须**按面板色着色**，不能用白色：原生 `SGRounded`/`SUGGradientRounded` 等是**不透明底板**
    /// （实测截图：白色装饰层把深色填充盖成了浅灰面板 → 文字发飘）。
    /// 着色后两种素材都对：不透明底板 → 变成深色面板；`UI Box Castile`（中心透明）→ 底层填充从中心透出来。
    public static Image Build(Transform parent, Color fillColor, Sprite sprite, bool raycast = false)
        => Build(parent, fillColor, UiSpriteRef.From(sprite), raycast);

    /// <summary>同 <see cref="Build(Transform, Color, Sprite, bool)"/>，但用素材引用（携带绘制模式与是否填中心）。</summary>
    public static Image Build(Transform parent, Color fillColor, UiSpriteRef r, bool raycast = false)
    {
        var fillRt = UI.UiKit.MakeRectFill("fill", parent);
        var fill = fillRt.gameObject.AddComponent<Image>();
        fill.color = fillColor;
        fill.raycastTarget = raycast;

        if (!r.IsEmpty)
        {
            var frameRt = UI.UiKit.MakeRectFill("frame", parent);
            var frame = frameRt.gameObject.AddComponent<Image>();
            frame.sprite = r.Sprite;
            frame.type = r.Type;               // Sliced / Tiled / Simple 由切片定义决定
            try { frame.fillCenter = r.FillCenter; } catch { }
            frame.color = FrameTint(fillColor);
            frame.raycastTarget = false;
            try { frame.pixelsPerUnitMultiplier = Theme.UiTheme.SpritePpuMul; } catch { }   // 照抄原生 2.5（见 UiTheme.SpritePpuMul）
        }
        return fill;
    }

    /// <summary>装饰层着色：比底色亮一档（让边框/花纹看得见），但远低于白色（不遮没底色）。</summary>
    public static Color FrameTint(Color fillColor)
        => new Color(
            Mathf.Min(fillColor.r * 1.6f + 0.08f, 1f),
            Mathf.Min(fillColor.g * 1.6f + 0.08f, 1f),
            Mathf.Min(fillColor.b * 1.6f + 0.08f, 1f),
            Mathf.Max(0.55f, fillColor.a));

    /// <summary>取面板底图（原生可用则用，否则 null = 纯色）。</summary>
    public static Sprite PanelSprite => Theme.UiSkin.PanelSprite;

    /// <summary>面板素材引用（切片定义优先）。</summary>
    public static UiSpriteRef PanelRef() => Theme.UiSkin.PanelRef();

    /// <summary>分隔线素材引用（切片定义优先）。</summary>
    public static UiSpriteRef LineRef() => Theme.UiSkin.LineRef();

    /// <summary>弹框/提示框素材引用（切片定义优先）。</summary>
    public static UiSpriteRef DialogRef() => Theme.UiSkin.DialogRef();

    /// <summary>取按钮底图（默认尺寸；控件实际建时请用 <see cref="ButtonSpriteFor"/>，见其注释）。</summary>
    public static Sprite ButtonSprite => Theme.UiSkin.ButtonSprite;

    /// <summary>按风格 + 控件尺寸挑按钮素材（切片定义优先；否则自动选材：九宫格放不下时烤入 border 缩放或退纯色）。</summary>
    public static Sprite ButtonSpriteFor(UiButtonStyle style, float w, float h)
    {
        if (style == UiButtonStyle.Ghost) return null;
        return Theme.UiSkin.ButtonSpriteFor(style == UiButtonStyle.Primary, style == UiButtonStyle.Danger, w, h);
    }

    /// <summary>按风格 + 控件尺寸取素材引用（**切片定义优先**，携带绘制模式/是否填中心）。</summary>
    public static UiSpriteRef ButtonRefFor(UiButtonStyle style, float w, float h)
    {
        if (style == UiButtonStyle.Ghost) return default;
        return Theme.UiSkin.ButtonRefFor(style == UiButtonStyle.Primary, style == UiButtonStyle.Danger, w, h);
    }

    /// <summary>取分隔线底图。</summary>
    public static Sprite LineSprite => Theme.UiSkin.LineSprite;

    /// <summary>取提示框/弹框底图。</summary>
    public static Sprite DialogSprite => Theme.UiSkin.DialogSprite;
}

/// <summary>
/// 按钮：底色 + 居中文字 + 热区（悬停提亮 / 按下压暗 / 禁用变暗）。
/// 交互走 <see cref="Native.UiPointerRouter"/>（不依赖游戏 EventSystem）。
/// </summary>
public sealed class UiButton : UiWidget
{
    private Image _fill;
    private UiText _text;
    private Native.UiHotZone _zone;
    private Color _color;
    private UiButtonStyle _style;
    private string _zoneName;
    private float _width;
    private float _height = Theme.UiTheme.ButtonH;

    private UiButton(RectTransform rt, Image fill, UiText text) : base(rt) { _fill = fill; _text = text; }

    /// <summary>按钮文字（改文案用）。</summary>
    public UiText Text => _text;

    /// <summary>当前底图（null = 纯色）——测试/诊断用：能如实报告"这颗按钮到底用了哪张原生图"。</summary>
    public Sprite Sprite { get; private set; }

    /// <summary>当前素材引用（含绘制模式与是否填中心）。</summary>
    public UiSpriteRef SpriteRef { get; private set; }

    /// <summary>按钮风格。</summary>
    public UiButtonStyle Style => _style;

    /// <summary>底色。</summary>
    public Color Color => _color;

    /// <summary>点击回调（可后改）。</summary>
    public Action OnClick;

    /// <summary>禁用态（视觉 + 热区一起变）。</summary>
    public override bool Interactable
    {
        get => _interactable;
        set
        {
            _interactable = value;
            try
            {
                if (_zone != null) _zone.Enabled = value;
                if (_fill != null) _fill.color = value ? _color : Theme.UiTheme.ButtonDisabled;
            }
            catch { }
        }
    }

    /// <summary>改文案。</summary>
    public void SetText(string s)
    {
        if (_text != null) _text.Value = s;
    }

    /// <summary>换底图（null = 纯色）。用于按实际尺寸重挑素材 / 测试对照。</summary>
    public void SetSprite(Sprite sprite)
        => SetSpriteRef(UiSpriteRef.From(sprite));

    /// <summary>
    /// **运行期改尺寸**（标题栏的“返回/关闭”两态切换、图标按钮等用）。
    /// 除了改矩形，还要写回 `_width/_height`（<see cref="Layout.UiMeasure"/> 量尺寸读的就是它们）
    /// 并按新尺寸重挑素材 —— 所以这里用 `new` 盖掉基类那个“只改矩形”的版本。
    /// </summary>
    public new void SetSize(float w, float h = 0f)
    {
        try
        {
            if (w > 0f) _width = w;
            if (h > 0f) _height = h;
            base.SetSize(_width > 0f ? _width : 120f, _height);
            RefitSprite();
        }
        catch { }
    }

    /// <summary>当前宽高（诊断/测试用）。</summary>
    public Vector2 Size => new Vector2(_width, _height);

    /// <summary>换素材引用（携带绘制模式/是否填中心；切片工具预览用）。</summary>
    public void SetSpriteRef(UiSpriteRef r)
    {
        try
        {
            SpriteRef = r;
            Sprite = r.Sprite;
            if (_fill == null) return;
            var parent = _fill.transform.parent;
            if (parent == null) return;

            // 已有的装饰层：删掉重建（装饰层是 _fill 的兄弟节点）
            var old = parent.Find("frame");
            if (old != null) UnityEngine.Object.Destroy(old.gameObject);

            if (r.IsEmpty) return;
            var frameRt = UI.UiKit.MakeRectFill("frame", parent);
            var frame = frameRt.gameObject.AddComponent<Image>();
            frame.sprite = r.Sprite;
            frame.type = r.Type;
            try { frame.fillCenter = r.FillCenter; } catch { }
            frame.color = UiSurface.FrameTint(_color);
            frame.raycastTarget = false;
            try { frame.pixelsPerUnitMultiplier = Theme.UiTheme.SpritePpuMul; } catch { }   // 照抄原生 2.5
        }
        catch (Exception ex) { CoopLog.Warn("uikit.widget", () => "SetSpriteRef failed: " + ex.Message); }
    }

    /// <summary>按当前尺寸自动重挑素材（尺寸变了以后调用）。</summary>
    public void RefitSprite()
    {
        try
        {
            var sd = _rt != null ? _rt.sizeDelta : new Vector2(120f, _height);
            SetSpriteRef(UiSurface.ButtonRefFor(_style, sd.x > 1f ? sd.x : 120f, sd.y > 1f ? sd.y : _height));
        }
        catch { }
    }

    /// <summary>设置底色（悬停/按下色 + 装饰层着色自动派生）。</summary>
    public void SetColor(Color c)
    {
        _color = c;
        try
        {
            if (_zone != null) _zone.SetBaseColor(c);
            else if (_fill != null) _fill.color = c;
            // 装饰层同步着色（否则换色后边框还是旧色调）
            var parent = _fill != null ? _fill.transform.parent : null;
            if (parent != null)
            {
                var f = parent.Find("frame");
                if (f != null)
                {
                    var fi = f.GetComponent<Image>();
                    if (fi != null) fi.color = UiSurface.FrameTint(c);
                }
            }
        }
        catch { }
    }

    /// <summary>创建按钮。<paramref name="width"/> &lt;= 0 = 由文字决定（Auto）。
    /// <paramref name="zoneName"/> 可覆盖默认热区名（默认 <c>"btn:"+文字</c>）——自动化/测试用来精确寻址无空格名字。</summary>
    public static UiButton Create(Transform parent, string text, Action onClick,
        UiButtonStyle style = UiButtonStyle.Secondary, float width = 0f, float height = 0f, Color? color = null,
        string zoneName = null)
    {
        var rt = NewRect("btn" + (string.IsNullOrEmpty(text) ? "" : ":" + text), parent);
        var h = height > 0f ? height : Theme.UiTheme.ButtonH;
        var w = width > 0f ? width : 120f;
        Theme.UiTheme.SetRect(rt, 0f, 0f, w, h);

        var r = UiSurface.ButtonRefFor(style, width > 0f ? width : 120f, h);
        var baseColor = color ?? DefaultColor(style);
        var fill = UiSurface.Build(rt, baseColor, r);

        // 扁平工业风：按钮也要有一圈 **1px 描边**（与窗口/面板同一套线），否则一块纯色块看着没“壳”。
        // 主/危险按钮用各自的强调色描边（琥珀/红），次按钮用冷灰 Border。
        if (style != UiButtonStyle.Ghost)
        {
            Color edge = style == UiButtonStyle.Primary ? Theme.UiTheme.Alpha(Theme.UiTheme.Accent, 0.55f)
                       : style == UiButtonStyle.Danger ? Theme.UiTheme.Alpha(Theme.UiTheme.Err, 0.55f)
                       : Theme.UiTheme.Border;
            Theme.ModStyle.Outline(rt, edge, Theme.UiTheme.OutlineW);
        }

        var txt = UiText.Create(rt, text, UiTextKind.Body, 0f, TextAlignmentOptions.Center);
        txt.Rect.offsetMin = Vector2.zero;
        txt.Rect.offsetMax = Vector2.zero;
        txt.Color = style == UiButtonStyle.Ghost ? Theme.UiTheme.TextPrimary : Color.white;
        txt.FontSize = Theme.UiTheme.FontButton;
        try
        {
            txt.Rect.anchorMin = Vector2.zero;
            txt.Rect.anchorMax = Vector2.one;
            txt.Rect.pivot = new Vector2(0.5f, 0.5f);
        }
        catch { }

        var btn = new UiButton(rt, fill, txt) { _width = width, _height = h, _style = style, _color = baseColor, OnClick = onClick };
        btn.SpriteRef = r;
        btn.Sprite = r.Sprite;
        btn._zoneName = zoneName;
        btn.SetColor(baseColor);
        btn.RegisterZone();

        // 尺寸测量：高度固定；宽度 Auto 时按文字宽度 + 内边距
        Layout.UiMeasure.Register(rt, _ => btn._height, _ => btn.PreferredWidth());
        return btn;
    }

    private float PreferredWidth()
    {
        if (_width > 0f) return _width;
        try
        {
            float tw = _text != null && _text.Text != null ? _text.Text.preferredWidth : 0f;
            return Mathf.Max(64f, tw + 28f);
        }
        catch { return 120f; }
    }

    private void RegisterZone()
    {
        _zone = Native.UiPointerRouter.Add(new Native.UiHotZone
        {
            Name = !string.IsNullOrEmpty(_zoneName) ? _zoneName : "btn:" + (_text != null ? _text.Value : ""),
            Rect = _rt,
            Bg = _fill,
            Tint = _style != UiButtonStyle.Ghost,
            Owner = this,
            BaseColor = _color,
            HoverColor = Theme.UiTheme.Hover(_color),
            PressColor = Theme.UiTheme.Pressed(_color),
            OnClick = () => { if (_interactable) { try { OnClick?.Invoke(); } catch (Exception ex) { CoopLog.Warn("uikit.widget", () => $"button click failed: {ex.Message}"); } } },
        });
    }

    private static Color DefaultColor(UiButtonStyle style)
    {
        switch (style)
        {
            case UiButtonStyle.Primary: return Theme.UiTheme.ButtonPrimary;
            case UiButtonStyle.Danger: return Theme.UiTheme.ButtonDanger;
            case UiButtonStyle.Ghost: return new Color(0f, 0f, 0f, 0f);
            default: return Theme.UiTheme.ButtonBg;
        }
    }

    /// <summary>销毁（顺带注销热区；<see cref="UiWidget.Destroy"/> 已做）。</summary>
    public override void Destroy()
    {
        try { Layout.UiMeasure.Unregister(_rt); } catch { }
        base.Destroy();
    }
}
