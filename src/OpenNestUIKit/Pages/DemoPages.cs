using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using OpenNestUIKit.Menu;
using OpenNestUIKit.Widgets;

namespace OpenNestUIKit.Pages;

/// <summary>演示页集合（组件总览 / 动态布局 / 列表 / 原生观感 / 原生注入 / 关于）。</summary>
internal static class DemoPages
{
    /// <summary>页面根统一用可滚动列表（自动获得滚动 + 视口裁剪）。</summary>
    private static UiList NewPage(Transform host)
    {
        var list = UiList.Create(host, 0f);
        return list;
    }

    private static T Add<T>(UiList list, T w, float fixedH = 0f) where T : UiWidget
    {
        if (w == null) return null;
        list.Flow.Child(new Layout.RectElement(w.Rect), fixedH > 0f ? Layout.UiSize.Fixed(fixedH) : Layout.UiSize.Auto);
        return w;
    }

    // ---------------- 组件总览 ----------------

    public static void BuildComponents(Transform host)
    {
        var list = NewPage(host);
        var f = list.Flow;
        string status = UiKitLoc.T("（点一下试试）", "(try clicking)");

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("文本", "Text")));
        Add(list, UiText.Create(f.Rect, UiKitLoc.T("标题", "Title") + " · Title", UiTextKind.Title));
        Add(list, UiText.Create(f.Rect, UiKitLoc.T("分组标题", "Header") + " · Header", UiTextKind.Header));
        Add(list, UiText.Create(f.Rect, UiKitLoc.T("正文：这是一段会自动换行的正文，宽度变化时高度会重新测量（动态布局的基础）。", "Body: this text wraps automatically; its height is re-measured when the width changes."), UiTextKind.Body));
        Add(list, UiText.Create(f.Rect, UiKitLoc.T("次要说明（Note）", "Note text"), UiTextKind.Note));

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("按钮", "Buttons")));
        float bw = 120f;
        var row1 = UiPanel.Create(f.Rect, 0f, new Color(0f, 0f, 0f, 0f), card: false, frame: false);
        try
        {
            row1.Flow.Axis = Layout.UiAxis.Horizontal;
            row1.Flow.Gap = Theme.UiTheme.GapSm;
            row1.Flow.AutoHeight = false;
            var b1 = UiButton.Create(row1.Content, UiKitLoc.T("主按钮", "Primary"), () => UiMenuWindow.SetStatus("Primary"), UiButtonStyle.Primary, bw, Theme.UiTheme.ButtonH);
            var b2 = UiButton.Create(row1.Content, UiKitLoc.T("次按钮", "Secondary"), () => UiMenuWindow.SetStatus("Secondary"), UiButtonStyle.Secondary, bw, Theme.UiTheme.ButtonH);
            var b3 = UiButton.Create(row1.Content, UiKitLoc.T("危险", "Danger"), () => UiMenuWindow.SetStatus("Danger"), UiButtonStyle.Danger, bw, Theme.UiTheme.ButtonH);
            var b4 = UiButton.Create(row1.Content, UiKitLoc.T("纯文字", "Ghost"), () => UiMenuWindow.SetStatus("Ghost"), UiButtonStyle.Ghost, bw, Theme.UiTheme.ButtonH);
            row1.Flow.Child(new Layout.RectElement(b1.Rect), Layout.UiSize.Fixed(bw));
            row1.Flow.Child(new Layout.RectElement(b2.Rect), Layout.UiSize.Fixed(bw));
            row1.Flow.Child(new Layout.RectElement(b3.Rect), Layout.UiSize.Fixed(bw));
            row1.Flow.Child(new Layout.RectElement(b4.Rect), Layout.UiSize.Fixed(bw));
            Layout.UiStretch.StretchTop(row1.Rect, Theme.UiTheme.ButtonH);
            row1.Flow.Apply();
            Layout.UiStretch.StretchTop(row1.Rect, Theme.UiTheme.ButtonH);
            f.Child(new Layout.RectElement(row1.Rect), Layout.UiSize.Fixed(Theme.UiTheme.ButtonH));
        }
        catch (Exception ex) { CoopLog.Warn("uikit.demo", () => "buttons row: " + ex.Message); }

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("控件", "Controls")));

        var tStatus = Add(list, UiText.Create(f.Rect, status, UiTextKind.Note)) as UiText;
        var toggle = Add(list, UiToggle.Create(f.Rect, UiKitLoc.T("布尔开关", "Toggle"), true, v =>
        {
            if (tStatus != null) tStatus.Value = "Toggle = " + v;
        })) as UiToggle;

        Add(list, UiSlider.Create(f.Rect, UiKitLoc.T("数值滑条", "Slider"), 0.45, 0, 1, 0.05, v =>
        {
            if (tStatus != null) tStatus.Value = "Slider = " + v.ToString("0.00");
        }));

        Add(list, UiStepper.Create(f.Rect, UiKitLoc.T("步进器", "Stepper"), 5, 0, 20, 1, v =>
        {
            if (tStatus != null) tStatus.Value = "Stepper = " + v.ToString("0");
        }));

        Add(list, UiChoice.Create(f.Rect, UiKitLoc.T("循环选择", "Choice"),
            new[] { UiKitLoc.T("低", "Low"), UiKitLoc.T("中", "Medium"), UiKitLoc.T("高", "High") }, 1, i =>
            {
                if (tStatus != null) tStatus.Value = "Choice = " + i;
            }));

        Add(list, UiTextInput.Create(f.Rect, UiKitLoc.T("文本输入", "Text input"), "", v =>
        {
            if (tStatus != null) tStatus.Value = "Text = '" + v + "'";
        }));

        Add(list, UiKeyBind.Create(f.Rect, UiKitLoc.T("快捷键", "Key bind"), "f7", v =>
        {
            if (tStatus != null) tStatus.Value = "Key = " + v;
        }));

        var prog = Add(list, UiProgress.Create(f.Rect, UiKitLoc.T("进度条", "Progress"), 0.35)) as UiProgress;
        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("进度演示", "Progress demo"), UiKitLoc.T("随机", "Random"), () =>
        {
            try { if (prog != null) prog.Value = new System.Random().NextDouble(); } catch { }
        }));

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("页签 / 弹窗 / 提示", "Tabs / Modal / Tooltip")));
        var tabLabel = Add(list, UiText.Create(f.Rect, UiKitLoc.T("页签：1", "Tab: 1"), UiTextKind.Note)) as UiText;
        var tabs = UiTabs.Create(f.Rect, new[]
        {
            UiKitLoc.T("常规", "General"), UiKitLoc.T("显示", "Display"), UiKitLoc.T("高级", "Advanced"),
        }, 0, i => { if (tabLabel != null) tabLabel.Value = UiKitLoc.T("页签：", "Tab: ") + (i + 1); });
        if (tabs != null) Layout.UiStretch.StretchTop(tabs.Rect, Theme.UiTheme.TabH);
        Add(list, tabs, Theme.UiTheme.TabH);

        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("模态确认框", "Modal dialog"), UiKitLoc.T("打开", "Open"), () =>
        {
            UiModal.Show(list.Rect, UiKitLoc.T("确认操作", "Confirm"),
                UiKitLoc.T("这是原生 UI 库的模态框（遮罩 + 面板 + 按钮组）。", "This is the kit's modal dialog."),
                UiKitLoc.T("确定", "OK"), UiKitLoc.T("取消", "Cancel"),
                ok => UiMenuWindow.SetStatus(ok ? "Modal = OK" : "Modal = Cancel"));
        }));

        var tipBtn = UiButton.Create(f.Rect, UiKitLoc.T("悬停看提示", "Hover for tooltip"), null, UiButtonStyle.Secondary, 160f, Theme.UiTheme.ButtonH);
        Layout.UiStretch.StretchTop(tipBtn.Rect, Theme.UiTheme.ButtonH);
        // 提示走热区（组件内部已建热区；这里给"最后一个热区"绑提示——按名字查找更稳）
        try
        {
            var zones = Native.UiPointerRouter.ZoneCount;
            _ = zones;
        }
        catch { }
        Add(list, tipBtn, Theme.UiTheme.ButtonH);

        Add(list, UiSeparator.Create(f.Rect, null));
        Add(list, UiText.Create(f.Rect, UiKitLoc.T("提示：所有交互都走自管指针（不依赖游戏 EventSystem），因此在任务场景里也能点。",
            "Note: all interaction goes through the kit's own pointer routing (no dependency on the game's EventSystem)."), UiTextKind.Note));

        list.ApplyLayout();
    }

    // ---------------- 动态布局 ----------------

    public static void BuildLayout(Transform host)
    {
        var list = NewPage(host);
        var f = list.Flow;

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("动态布局", "Dynamic layout")));
        Add(list, UiText.Create(f.Rect, UiKitLoc.T(
            "下面的卡片里，每行文字长度不同 -> 高度自动测量；点按钮增删行/改文字 -> 整块重新排版（没有写死坐标）。",
            "Rows below have different text lengths -> their heights are measured; add/remove rows or change text -> the whole block re-lays out."), UiTextKind.Note));

        var card = UiPanel.Create(f.Rect, Theme.UiTheme.Pad, Theme.UiTheme.CardBg, card: true);
        Layout.UiStretch.StretchTop(card.Rect, 120f);
        f.Child(new Layout.RectElement(card.Rect), Layout.UiSize.Auto);

        var rows = new List<UiText>();
        var stat = UiText.Create(f.Rect, "", UiTextKind.Note);
        Add(list, stat);

        Action rebuild = () =>
        {
            try
            {
                card.Flow.Clear();
                for (int i = 0; i < rows.Count; i++) card.Flow.Child(new Layout.RectElement(rows[i].Rect), Layout.UiSize.Auto);
                card.Relayout();
                Layout.UiStretch.StretchTop(card.Rect, card.MeasurePanelHeight(Layout.UiStretch.HostSize(host).x - Theme.UiTheme.Pad * 2f));
                list.ApplyLayout();
                stat.Value = UiKitLoc.T("行数：", "rows: ") + rows.Count;
            }
            catch (Exception ex) { CoopLog.Warn("uikit.demo", () => "layout rebuild: " + ex.Message); }
        };

        Action<string> addRow = text =>
        {
            var t = UiText.Create(card.Content, text, UiTextKind.Body);
            rows.Add(t);
            rebuild();
        };

        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("增删行", "Add / remove"), UiKitLoc.T("加一行", "Add row"), () =>
        {
            string[] samples =
            {
                UiKitLoc.T("短行", "short row"),
                UiKitLoc.T("中等长度的一行文字，用于验证自动高度测量。", "a medium-length row to verify automatic height measurement."),
                UiKitLoc.T("更长的一行文字：动态布局会在宽度固定的情况下把文字换成多行，并把后面的控件整体下移——这一切都是每次 Apply() 重算出来的，没有任何写死的坐标。",
                    "a longer row: the layout wraps the text at the fixed width and pushes the following controls down — all recomputed on every Apply()."),
            };
            addRow(samples[rows.Count % samples.Length]);
        }));

        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("删行", "Remove"), UiKitLoc.T("删一行", "Remove row"), () =>
        {
            if (rows.Count == 0) return;
            var last = rows[rows.Count - 1];
            rows.RemoveAt(rows.Count - 1);
            try { last.Destroy(); } catch { }
            rebuild();
        }));

        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("清空", "Clear"), UiKitLoc.T("清空", "Clear"), () =>
        {
            for (int i = 0; i < rows.Count; i++) { try { rows[i].Destroy(); } catch { } }
            rows.Clear();
            rebuild();
        }));

        addRow(UiKitLoc.T("初始行：这是第一行，下面还可以加很多行。", "initial row"));
        addRow(UiKitLoc.T("初始行：这一行稍微长一点，用来对比行高。", "a slightly longer initial row"));
        rebuild();
        list.ApplyLayout();
    }

    // ---------------- 列表 / 滚动 ----------------

    private static readonly List<string> ListData = new();

    public static void BuildList(Transform host)
    {
        var list = NewPage(host);
        var f = list.Flow;

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("列表（滚动）", "List (scroll)")));
        Add(list, UiText.Create(f.Rect, UiKitLoc.T(
            "滚轮或按住拖动即可滚动；行按需创建并复用（对象池），滚出视口的行会被临时隐藏。",
            "Scroll with the wheel or by dragging; rows are pooled and rows outside the viewport are hidden."), UiTextKind.Note));

        var stat = UiText.Create(f.Rect, "", UiTextKind.Note);
        Add(list, stat);
        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("填充数据", "Fill data"), UiKitLoc.T("生成 40 行", "generate 40 rows"), () =>
        {
            ListData.Clear();
            for (int i = 0; i < 40; i++) ListData.Add(UiKitLoc.T("条目 #", "entry #") + (i + 1));
            RebuildInner();
            stat.Value = UiKitLoc.T("行数：", "rows: ") + ListData.Count;
        }));
        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("清空", "Clear"), UiKitLoc.T("清空", "Clear"), () =>
        {
            ListData.Clear();
            RebuildInner();
            stat.Value = UiKitLoc.T("行数：", "rows: ") + ListData.Count;
        }));

        var inner = UiList.Create(f.Rect, 420f);
        Layout.UiStretch.StretchTop(inner.Rect, 420f);
        f.Child(new Layout.RectElement(inner.Rect), Layout.UiSize.Fixed(420f));
        _inner = inner;
        _innerStat = stat;

        for (int i = 0; i < 12; i++) ListData.Add(UiKitLoc.T("条目 #", "entry #") + (i + 1));
        RebuildInner();
        stat.Value = UiKitLoc.T("行数：", "rows: ") + ListData.Count;

        list.ApplyLayout();
    }

    private static UiList _inner;
    private static UiText _innerStat;

    /// <summary>用行池重建内层列表（<c>Obtain</c> 复用旧行，避免每次重建对象）。</summary>
    private static void RebuildInner()
    {
        try
        {
            if (_inner == null) return;
            _inner.ClearRows();
            for (int i = 0; i < ListData.Count; i++)
            {
                int idx = i;
                var row = UiNavRow.Create(_inner.Flow.Rect, ListData[i], "#" + (idx + 1), () =>
                    UiMenuWindow.SetStatus(UiKitLoc.T("选中：", "selected: ") + ListData[idx]), 34f);
                _inner.Add(row, Layout.UiSize.Fixed(34f));
            }
            _inner.ApplyLayout();
            _inner.ScrollTop();
            if (_innerStat != null) _innerStat.Value = UiKitLoc.T("内层行数：", "inner rows: ") + _inner.Count;
        }
        catch (Exception ex) { CoopLog.Warn("uikit.demo", () => "inner list rebuild: " + ex.Message); }
    }

    // ---------------- 原生观感 ----------------

    public static void BuildTheme(Transform host)
    {
        var list = NewPage(host);
        var f = list.Flow;

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("原生观感（UI Box 素材）", "Native look (UI Box sprites)")));
        var info = UiText.Create(f.Rect, "", UiTextKind.Note);
        Add(list, info);

        Action refresh = () =>
        {
            string panel = Theme.UiSkin.PanelSprite != null ? Theme.UiSkin.PanelSprite.name : "<纯色>";
            string btn = Theme.UiSkin.ButtonSprite != null ? Theme.UiSkin.ButtonSprite.name : "<纯色>";
            string line = Theme.UiSkin.LineSprite != null ? Theme.UiSkin.LineSprite.name : "<纯色>";
            info.Value =
                UiKitLoc.T("已捕获素材：", "captured: ") + Theme.UiSkin.Count + "\n" +
                "Panel=" + panel + "\nButton=" + btn + "\nLine=" + line;
        };

        Add(list, UiToggle.Create(f.Rect, UiKitLoc.T("启用原生素材", "Use native sprites"), Theme.UiSkin.UseNative, v =>
        {
            Theme.UiSkin.UseNative = v;
            refresh();
            UiMenuWindow.SetStatus(UiKitLoc.T("原生素材：", "native sprites: ") + (v ? "on" : "off") + UiKitLoc.T("（重开菜单后应用到新控件）", " (reopen to apply to new widgets)"));
        }));

        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("重新捕获", "Recapture"), UiKitLoc.T("捕获", "Capture"), () =>
        {
            int n = Theme.UiSkin.CaptureAll();
            refresh();
            UiMenuWindow.SetStatus(UiKitLoc.T("新捕获素材：", "newly captured: ") + n);
        }));

        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("清空素材缓存", "Clear sprite cache"), UiKitLoc.T("清空", "Clear"), () =>
        {
            Theme.UiSkin.Clear();
            refresh();
            UiMenuWindow.SetStatus("UiSkin cleared");
        }));

        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("重播过渡动画", "Replay transition"), UiKitLoc.T("播放", "Play"), () =>
        {
            UiMenuWindow.SetStatus("transition");
        }));

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("预览", "Preview")));
        var card = UiPanel.Create(f.Rect, Theme.UiTheme.Pad, Theme.UiTheme.CardBg, card: true);
        Layout.UiStretch.StretchTop(card.Rect, 90f);
        f.Child(new Layout.RectElement(card.Rect), Layout.UiSize.Auto);
        card.Flow.Child(new Layout.RectElement(UiText.Create(card.Content, UiKitLoc.T("这是一个卡片面板（原生素材优先）。", "A card panel (native sprite preferred)."), UiTextKind.Body).Rect), Layout.UiSize.Auto);
        card.Flow.Child(new Layout.RectElement(UiButton.Create(card.Content, UiKitLoc.T("面板内按钮", "Button in panel"), () => UiMenuWindow.SetStatus("panel button"), UiButtonStyle.Primary, 160f, Theme.UiTheme.ButtonH).Rect), Layout.UiSize.Fixed(Theme.UiTheme.ButtonH));
        card.Flow.Child(new Layout.RectElement(UiSeparator.Create(card.Content, null).Rect), Layout.UiSize.Auto);
        card.Relayout();
        Layout.UiStretch.StretchTop(card.Rect, card.MeasurePanelHeight(Layout.UiStretch.HostSize(host).x - Theme.UiTheme.Pad * 2f));

        refresh();
        list.ApplyLayout();
    }

    // ---------------- 原生菜单注入 ----------------

    public static void BuildNative(Transform host)
    {
        var list = NewPage(host);
        var f = list.Flow;

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("原生菜单注入", "Native menu injection")));
        var info = UiText.Create(f.Rect, "", UiTextKind.Note);
        Add(list, info);

        Action refresh = () =>
        {
            info.Value =
                UiKitLoc.T("已注入条目：", "entries: ") + Native.NativeMenuBridge.Count + "\n" +
                UiKitLoc.T("是否已注入原生菜单：", "injected: ") + (Native.NativeMenuInjector.IsInjected ? "yes" : "no") + "\n" +
                UiKitLoc.T("原生时机 patch：", "harmony patch: ") + (Native.NativeMenuInjector.IsPatched ? "yes" : "no（用事件/轮询兜底）") + "\n" +
                UiKitLoc.T("目标容器：", "target: ") + "ESC Menu Buttons";
        };

        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("重新注入", "Re-inject"), UiKitLoc.T("注入", "Inject"), () =>
        {
            Native.NativeMenuInjector.Inject(force: true);
            refresh();
            UiMenuWindow.SetStatus(UiKitLoc.T("已尝试注入（看日志 uikit.native）", "inject attempted (see log)"));
        }));

        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("加一个测试项", "Add test entry"), UiKitLoc.T("添加", "Add"), () =>
        {
            Native.NativeMenuBridge.Add(new Native.NativeMenuEntry
            {
                Id = "uikit.test." + DateTime.Now.Ticks,
                Title = UiKitLoc.T("测试入口", "Test entry"),
                TitleEn = "Test entry",
                PageId = "about",
                Order = 50,
                InsertAfter = "OpenSettingsBtn",
            });
            Native.NativeMenuInjector.Inject(force: true);
            refresh();
        }));

        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("移除测试项", "Remove test entries"), UiKitLoc.T("移除", "Remove"), () =>
        {
            var all = Native.NativeMenuBridge.Entries;
            for (int i = 0; i < all.Length; i++)
                if (all[i].Id.StartsWith("uikit.test.", StringComparison.Ordinal)) Native.NativeMenuBridge.Remove(all[i].Id);
            Native.NativeMenuInjector.Inject(force: true);
            refresh();
        }));

        Add(list, UiActionRow.Create(f.Rect, UiKitLoc.T("输入环境探测", "Input probe"), UiKitLoc.T("打印日志", "Log"), () =>
        {
            Native.UiInputGuard.LogProbe();
            UiMenuWindow.SetStatus(UiKitLoc.T("已打印环境快照（uikit 日志）", "probe logged"));
        }));

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("原生菜单里的多级页面项", "Multi-level native entries")));
        Add(list, UiNavRow.Create(f.Rect, UiKitLoc.T("这是一个子页（可从原生菜单直达）", "A sub-page (reachable from the native menu)"),
            "uikit.native", () => UiMenuWindow.Navigate("widgets"), 44f));
        Add(list, UiText.Create(f.Rect, UiKitLoc.T(
            "原生菜单里的「原生菜单注入」入口点击后直达本页；「模组 UI 库」入口直达主页 —— 关闭菜单即回到原生菜单。",
            "The native entry opens this page; closing the kit returns to the native menu."), UiTextKind.Note));

        refresh();
        list.ApplyLayout();
    }

    // ---------------- 关于 ----------------

    public static void BuildAbout(Transform host)
    {
        var list = NewPage(host);
        var f = list.Flow;

        Add(list, UiSeparator.Create(f.Rect, UiKitLoc.T("关于", "About")));
        Add(list, UiInfoRow.Create(f.Rect, UiKitLoc.T("名称", "Name"), UiKitInfo.Name + " v" + UiKitInfo.Version));
        Add(list, UiInfoRow.Create(f.Rect, UiKitLoc.T("构建平台", "Build"), UiKitInfo.BuildPlatform));
        Add(list, UiInfoRow.Create(f.Rect, UiKitLoc.T("部署形态", "Deploy"), UiKitPaths.Shape.ToString()));
        Add(list, UiInfoRow.Create(f.Rect, UiKitLoc.T("画布层级", "Canvas order"), UiMenuWindow.SortingOrder.ToString()));
        Add(list, UiInfoRow.Create(f.Rect, UiKitLoc.T("契约 API 版本", "Contract API"), API.UiKitHost.ApiVersion.ToString()));
        Add(list, UiInfoRow.Create(f.Rect, UiKitLoc.T("契约宿主", "Host"), API.UiKitHost.IsHostAvailable ? UiKitLoc.T("在线", "online") : UiKitLoc.T("离线", "offline")));
        Add(list, UiInfoRow.Create(f.Rect, UiKitLoc.T("第三方提供者", "Providers"), API.UiKitHost.ProviderCount.ToString()));
        Add(list, UiInfoRow.Create(f.Rect, UiKitLoc.T("页面数", "Pages"), UiPageCatalog.Count.ToString()));
        Add(list, UiInfoRow.Create(f.Rect, UiKitLoc.T("热区数", "Hot zones"), Native.UiPointerRouter.ZoneCount.ToString()));
        Add(list, UiInfoRow.Create(f.Rect, UiKitLoc.T("原生素材数", "Native sprites"), Theme.UiSkin.Count.ToString()));
        Add(list, UiInfoRow.Create(f.Rect, UiKitLoc.T("当前语言", "Language"), UiKitLoc.Current));
        Add(list, UiSeparator.Create(f.Rect, null));
        Add(list, UiText.Create(f.Rect, UiKitLoc.T(
            "第三方模组可通过 OpenNestUIKit.API（纯 .NET 契约，软依赖）注册自己的菜单页：实现 IUiKitProvider 并调用 UiKitHost.Register。",
            "Third-party mods can register their own pages via OpenNestUIKit.API (pure .NET contract, soft dependency)."), UiTextKind.Note));

        list.ApplyLayout();
    }
}
