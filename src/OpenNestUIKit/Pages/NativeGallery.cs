using System;
using UnityEngine;
using UnityEngine.UI;
using OpenNestUIKit.Menu;
using OpenNestUIKit.Widgets;

namespace OpenNestUIKit.Pages;

/// <summary>
/// **原生组件画廊**：把本库所有组件按原生 ESC 菜单的口径排成三页
/// （按钮/行 · 输入控件 · 容器与浮层），每节都标出"这一颗控件实际用了哪张原生图"，
/// 便于和原生菜单逐项对照「功能 + 外观」。
///
/// 三页在**原生多级菜单**里挂成一层分组（顶层「模组 UI 库」→「原生组件画廊」→ 三页），
/// 所以从游戏 ESC 菜单一路点下来就能看到，不需要别的入口。
/// </summary>
internal static class NativeGallery
{
    private static UiList NewPage(Transform host) => UiList.Create(host, 0f);

    private static T Add<T>(UiList list, T w, float fixedH = 0f) where T : UiWidget
    {
        if (w == null) return null;
        list.Flow.Child(new Layout.RectElement(w.Rect), fixedH > 0f ? Layout.UiSize.Fixed(fixedH) : Layout.UiSize.Auto);
        return w;
    }

    /// <summary>当前这颗按钮实际用的原生底图名（null = 纯色）。</summary>
    private static string SpriteNameOf(UiButtonStyle style, float w, float h)
    {
        try
        {
            var s = Theme.UiSkin.ButtonSpriteFor(style == UiButtonStyle.Primary, style == UiButtonStyle.Danger, w, h);
            return s != null ? s.name : UiKitLoc.T("<纯色>", "<solid>");
        }
        catch { return "?"; }
    }

    // ---------------- 1) 按钮 / 行 ----------------

    public static void BuildButtons(Transform host)
    {
        var list = NewPage(host);
        var f = list.Flow;

        var status = Add(list, UiText.Create(f.Rect, UiKitLoc.T("（点一下任意按钮，这里会显示结果）", "(click a button; result shows here)"), UiTextKind.Note)) as UiText;
        Action<string> set = s => { if (status != null) status.Value = s; };

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("按钮四种风格（原生底图按尺寸自动挑）", "Button styles (native sprite auto-picked by size)")));

        // 固定宽 + 全宽两种尺寸，验证九宫格在不同尺寸下的观感
        var sizes = new[] { 120f, 200f, 0f };
        for (int si = 0; si < sizes.Length; si++)
        {
            float w = sizes[si];
            float ww = w > 0f ? w : Layout.UiStretch.HostSize(host).x;
            var row = UiPanel.Create(f.Rect, 0f, new Color(0f, 0f, 0f, 0f), card: false, frame: false);
            try
            {
                row.Flow.Axis = Layout.UiAxis.Horizontal;
                row.Flow.Gap = Theme.UiTheme.GapSm;
                row.Flow.AutoHeight = false;
                var styles = new[] { UiButtonStyle.Primary, UiButtonStyle.Secondary, UiButtonStyle.Danger, UiButtonStyle.Ghost };
                foreach (var st in styles)
                {
                    var cap = st + " · " + SpriteNameOf(st, ww, Theme.UiTheme.ButtonH);
                    var b = UiButton.Create(row.Content, cap, () => set(st + " ← " + cap), st, ww, Theme.UiTheme.ButtonH);
                    row.Flow.Child(new Layout.RectElement(b.Rect), Layout.UiSize.Fixed(ww));
                }
                Layout.UiStretch.StretchTop(row.Rect, Theme.UiTheme.ButtonH);
                row.Flow.Apply();
                Layout.UiStretch.StretchTop(row.Rect, Theme.UiTheme.ButtonH);
                f.Child(new Layout.RectElement(row.Rect), Layout.UiSize.Fixed(Theme.UiTheme.ButtonH));
            }
            catch (Exception ex) { CoopLog.Warn("uikit.gallery", () => "button row: " + ex.Message); }
        }

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("禁用态 / 行组件", "Disabled / rows")));
        var dis = UiButton.Create(f.Rect, UiKitLoc.T("禁用按钮", "Disabled"), null, UiButtonStyle.Secondary, 160f, Theme.UiTheme.ButtonH);
        try { dis.Interactable = false; } catch { }
        Add(list, dis, Theme.UiTheme.ButtonH);

        Add(list, UiNavRow.Create(f.Rect, UiKitLoc.T("导航行（点我）", "Nav row (click)"), UiKitLoc.T("-> 打开组件总览", "-> open Components"), () => UiMenuWindow.Navigate("widgets"), 44f));
        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("动作行", "Action row"), UiKitLoc.T("执行", "Run"), () => set("ActionRow ← Run")));
        Add(list, UiInfoRow.Create(f.Rect, UiKitLoc.T("信息行", "Info row"), SpriteNameOf(UiButtonStyle.Primary, 200f, Theme.UiTheme.ButtonH)));

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("文本层级", "Text kinds")));
        Add(list, UiText.Create(f.Rect, "Title", UiTextKind.Title));
        Add(list, UiText.Create(f.Rect, "Header", UiTextKind.Header));
        Add(list, UiText.Create(f.Rect, UiKitLoc.T("正文（自动换行，宽度变化会重算高度）。", "Body text (wraps automatically)."), UiTextKind.Body));
        Add(list, UiText.Create(f.Rect, UiKitLoc.T("次要说明（Note）", "Note text"), UiTextKind.Note));

        list.ApplyLayout();
    }

    // ---------------- 2) 输入控件 ----------------

    public static void BuildInputs(Transform host)
    {
        var list = NewPage(host);
        var f = list.Flow;

        var status = Add(list, UiText.Create(f.Rect, UiKitLoc.T("（操作任意控件，这里显示最新值）", "(interact; latest value shows here)"), UiTextKind.Note)) as UiText;
        Action<string> set = s => { if (status != null) status.Value = s; };

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("开关 / 滑条 / 步进", "Toggle / Slider / Stepper")));
        Add(list, UiToggle.Create(f.Rect, UiKitLoc.T("布尔开关", "Toggle"), true, v => set("Toggle = " + v)));

        var slider = UiSlider.Create(f.Rect, UiKitLoc.T("数值滑条", "Slider"), 0.45, 0, 1, 0.05, v => set("Slider = " + v.ToString("0.00")));
        Add(list, slider);
        var stepper = UiStepper.Create(f.Rect, UiKitLoc.T("步进器", "Stepper"), 5, 0, 20, 1, v => set("Stepper = " + v.ToString("0")));
        Add(list, stepper);

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("选择 / 输入 / 快捷键", "Choice / Input / Key bind")));
        Add(list, UiChoice.Create(f.Rect, UiKitLoc.T("循环选择", "Choice"),
            new[] { UiKitLoc.T("低", "Low"), UiKitLoc.T("中", "Medium"), UiKitLoc.T("高", "High") }, 1,
            i => set("Choice = " + i)));

        var input = UiTextInput.Create(f.Rect, UiKitLoc.T("文本输入", "Text input"), "", v => set("Text = '" + v + "'"));
        Add(list, input);

        Add(list, UiKeyBind.Create(f.Rect, UiKitLoc.T("快捷键", "Key bind"), "f7", v => set("Key = " + v)));

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("进度", "Progress")));
        var prog = Add(list, UiProgress.Create(f.Rect, UiKitLoc.T("进度条", "Progress"), 0.35)) as UiProgress;
        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("进度演示", "Progress demo"), UiKitLoc.T("随机", "Random"), () =>
        {
            try { if (prog != null) prog.Value = new System.Random().NextDouble(); } catch { }
        }));

        list.ApplyLayout();
    }

    // ---------------- 3) 容器与浮层 ----------------

    public static void BuildContainers(Transform host)
    {
        var list = NewPage(host);
        var f = list.Flow;
        string hostW = Layout.UiStretch.HostSize(host).x.ToString("0");

        var status = Add(list, UiText.Create(f.Rect, UiKitLoc.T("（页签 / 弹窗都点一下）", "(try tabs / modal)"), UiTextKind.Note)) as UiText;
        Action<string> set = s => { if (status != null) status.Value = s; };

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("面板 / 卡片", "Panel / Card")));
        var panelSprite = Theme.UiSkin.PanelRef();
        Add(list, UiText.Create(f.Rect, UiKitLoc.T("面板底图：", "panel sprite: ")
            + (panelSprite.Sprite != null ? panelSprite.Sprite.name : UiKitLoc.T("<纯色>", "<solid>"))
            + UiKitLoc.T("（窗口宽 ", " (window w ") + hostW + UiKitLoc.T("）", ")"), UiTextKind.Note));

        var card = UiPanel.Create(f.Rect, Theme.UiTheme.Pad, Theme.UiTheme.CardBg, card: true);
        Layout.UiStretch.StretchTop(card.Rect, 96f);
        f.Child(new Layout.RectElement(card.Rect), Layout.UiSize.Auto);
        card.Flow.Child(new Layout.RectElement(UiText.Create(card.Content, UiKitLoc.T("卡片面板（原生素材优先，纯色层兜底）。", "Card panel (native sprite preferred)."), UiTextKind.Body).Rect), Layout.UiSize.Auto);
        card.Flow.Child(new Layout.RectElement(UiButton.Create(card.Content, UiKitLoc.T("面板内按钮", "Button in panel"), () => set("panel button"), UiButtonStyle.Primary, 160f, Theme.UiTheme.ButtonH).Rect), Layout.UiSize.Fixed(Theme.UiTheme.ButtonH));
        card.Flow.Child(new Layout.RectElement(UiSeparator.Create(card.Content, null).Rect), Layout.UiSize.Auto);
        card.Relayout();
        Layout.UiStretch.StretchTop(card.Rect, card.MeasurePanelHeight(Layout.UiStretch.HostSize(host).x - Theme.UiTheme.Pad * 2f));

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("页签", "Tabs")));
        var tabInfo = Add(list, UiText.Create(f.Rect, UiKitLoc.T("页签：1", "Tab: 1"), UiTextKind.Note)) as UiText;
        var tabs = UiTabs.Create(f.Rect, new[]
        {
            UiKitLoc.T("常规", "General"), UiKitLoc.T("显示", "Display"), UiKitLoc.T("高级", "Advanced"),
        }, 0, i => { if (tabInfo != null) tabInfo.Value = UiKitLoc.T("页签：", "Tab: ") + (i + 1); set("Tab = " + (i + 1)); });
        if (tabs != null) Layout.UiStretch.StretchTop(tabs.Rect, Theme.UiTheme.TabH);
        Add(list, tabs, Theme.UiTheme.TabH);

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("浮层：模态框 / 提示", "Overlays: modal / tooltip")));
        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("模态确认框", "Modal dialog"), UiKitLoc.T("打开", "Open"), () =>
        {
            UiModal.Show(list.Rect, UiKitLoc.T("确认操作", "Confirm"),
                UiKitLoc.T("遮罩 + 面板 + 按钮组，走自管指针（不依赖游戏 EventSystem）。", "Overlay + panel + buttons, routed by the kit's own pointer."),
                UiKitLoc.T("确定", "OK"), UiKitLoc.T("取消", "Cancel"),
                ok => set(ok ? "Modal = OK" : "Modal = Cancel"));
        }));

        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("提示条", "Toast / status"), UiKitLoc.T("弹一下", "Show"), () => set("Toast")));

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("列表 / 滚动", "List / scroll")));
        Add(list, UiText.Create(f.Rect, UiKitLoc.T("整页就是一个滚动列表（滚轮 / 拖拽都可），视口外自动裁剪。", "This whole page is a scroll list (wheel/drag), clipped to the viewport."), UiTextKind.Note));

        list.ApplyLayout();
    }
}
