using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using OpenNestUIKit;
using OpenNestUIKit.Layout;
using OpenNestUIKit.Menu;
using OpenNestUIKit.Theme;
using OpenNestUIKit.Widgets;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestUIKit.Test;

/// <summary>
/// 测试模组的页面（**直接调用 UIKit 的组件**构建，不是走契约）：
/// - `test.home`：入口列表 + 实时状态；
/// - `test.buttons`：**按钮素材实验室**（4 风格 × 4 高度 + 窄按钮），每颗按钮都如实报告用了哪张图；
/// - `test.widgets`：全部组件各来一个（由外部模组构建，验证外部调用方可用）；
/// - `test.zones`：热区清单 + 点击探针（CLI 模拟点击的靶子）；
/// - `test.api`：契约/宿主/原生注入状态。
/// </summary>
internal static class TestPages
{
    private static string T(string zh, string en) => Core.UiKitLoc.T(zh, en);

    public static void Register()
    {
        UiPageCatalog.Register(new UiSimplePage("test.home", T("UIKit 测试台", "UIKit Test Bench"), BuildHome));
        UiPageCatalog.Register(new UiSimplePage("test.buttons", T("按钮素材实验室", "Button Sprite Lab"), BuildButtons, fillHeight: true));
        UiPageCatalog.Register(new UiSimplePage("test.widgets", T("组件全览（外部构建）", "Widgets (built externally)"), BuildWidgets, fillHeight: true));
        UiPageCatalog.Register(new UiSimplePage("test.zones", T("热区 / 点击探针", "Hot zones / Click probe"), BuildZones, fillHeight: true));
        UiPageCatalog.Register(new UiSimplePage("test.api", T("契约 / 原生注入状态", "Contract / Native injection"), BuildApi, fillHeight: true));
        TestLog.Info($"测试页面已注册（catalog={UiPageCatalog.Count}）");
    }

    // ---------------- 主页 ----------------

    private static void BuildHome(Transform host)
    {
        var list = UiList.Create(host, 0f);
        var f = list.Flow;

        Line(f, UiSeparator.Create(f.Rect, T("测试页面", "Test pages")));
        Line(f, UiText.Create(f.Rect,
            T("这些页面由测试模组直接调用 UIKit 组件构建（不是契约路径）。CLI 可用 -onuktest-run=... 脚本自动点这些控件。",
              "Built by the test mod calling UIKit widgets directly. Drive them from CLI: -onuktest-run=..."), UiTextKind.Note));

        Nav(list, "test.buttons", T("按钮素材实验室", "Button Sprite Lab"), T("9 切片/素材选择", "9-slice / sprite pick"));
        Nav(list, "test.widgets", T("组件全览", "Widgets"), T("全部基础组件", "all widgets"));
        Nav(list, "test.zones", T("热区 / 点击探针", "Hot zones"), T("CLI 点击靶子", "CLI click targets"));
        Nav(list, "test.api", T("契约 / 原生注入状态", "Contract / Native"), T("宿主与注入", "host & injection"));
        Nav(list, "provider:" + TestProvider.ProviderId, T("契约注册页（第三方路径）", "Contract-registered page"), T("纯 .NET 契约", "pure .NET contract"));

        Line(f, UiSeparator.Create(f.Rect, T("实时状态", "Live status")));
        var status = UiText.Create(f.Rect, StatusText(), UiTextKind.Note);
        Line(f, status);
        Line(f, UiActionRow.Create(f.Rect, T("刷新状态", "Refresh"), T("刷新", "Refresh"), () => status.Value = StatusText()));

        Line(f, UiSeparator.Create(f.Rect, null));
        Line(f, UiText.Create(f.Rect, T(
            "CLI：-onuktest-autoopen 自动开菜单；-onuktest-run=脚本（; 分隔）自动跑流程；-onuktest-zones 打印热区清单。",
            "CLI: -onuktest-autoopen / -onuktest-run=<script> / -onuktest-zones"), UiTextKind.Note));

        list.ApplyLayout();
    }

    private static string StatusText()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(T("UIKit 契约宿主：", "UIKit host: ")).Append(API.UiKitHost.IsHostAvailable ? "online " + API.UiKitHost.HostVersion : "offline").Append('\n');
        sb.Append(T("画布层级：", "canvas order: ")).Append(UiMenuWindow.SortingOrder).Append(T("　当前页：", "  page: ")).Append(UiMenuWindow.CurrentPage).Append('\n');
        sb.Append(T("页面数：", "pages: ")).Append(UiPageCatalog.Count)
          .Append(T("　热区：", "  zones: ")).Append(Native.UiPointerRouter.ZoneCount)
          .Append(T("　探针命中：", "  probe hits: ")).Append(TestProbe.Total).Append('\n');
        sb.Append(T("原生素材：", "native sprites: ")).Append(UiSkin.Count)
          .Append(T("　启用：", "  enabled: ")).Append(UiSkin.UseNative)
          .Append(T("　原生注入：", "  native injected: ")).Append(Native.NativeMenuInjector.IsInjected);
        return sb.ToString();
    }

    // ---------------- 按钮素材实验室 ----------------

    private static void BuildButtons(Transform host)
    {
        var list = UiList.Create(host, 0f);
        var f = list.Flow;

        Line(f, UiSeparator.Create(f.Rect, T("按钮素材（九宫格）实验室", "Button sprite (9-slice) lab")));
        Line(f, UiText.Create(f.Rect, T(
            "每颗按钮下方日志会打印**实际使用的 sprite 与 border**。九宫格要求 border 合计 + 余量 ≤ 控件尺寸；" +
            "放不下时库会把 border 烤入缩放副本，仍放不下则退纯色（避免 Unity 把整张图案压进按钮 → 看起来像平铺）。",
            "Each button logs the sprite/border actually used. If borders don't fit, the lib bakes a scaled copy or falls back to a solid color."), UiTextKind.Note));

        var report = UiText.Create(f.Rect, "", UiTextKind.Note);
        Line(f, report);

        var styles = new[]
        {
            UiButtonStyle.Secondary, UiButtonStyle.Primary, UiButtonStyle.Danger, UiButtonStyle.Ghost,
        };
        var sizes = new[] { 24f, 28f, 32f, 40f, 56f };

        int bx = 10;
        foreach (var style in styles)
        {
            Line(f, UiText.Create(f.Rect, StyleName(style), UiTextKind.Header));
            foreach (var h in sizes)
            {
                float hh = h;
                string token = "btn:" + StyleToken(style) + hh.ToString("0");
                string label = $"{StyleName(style)}  h={hh:0}";
                var btn = UiButton.Create(f.Rect, label, () => TestProbe.Hit(token), style, 0f, hh, null, token);
                if (btn == null) continue;
                UiStretch.StretchTop(btn.Rect, hh);
                Line(f, btn, hh);
                // 如实报告本颗按钮用了哪张图（这是"看起来像平铺"问题的直接取证）
                string used = btn.Sprite != null ? $"{btn.Sprite.name} border={btn.Sprite.border}" : "纯色";
                report.Value += $"{token} ({label}) → {used}\n";
                bx++;
            }
        }

        Line(f, UiText.Create(f.Rect, T("窄按钮（横向边框压力测试）", "Narrow buttons (horizontal border stress)"), UiTextKind.Header));
        var row = UiPanel.Create(f.Rect, 0f, new Color(0f, 0f, 0f, 0f), card: false, frame: false);
        UiStretch.StretchTop(row.Rect, 36f);
        row.Flow.Axis = UiAxis.Horizontal;
        row.Flow.Gap = 8f;
        row.Flow.AutoHeight = false;
        float[] widths = { 64f, 96f, 140f, 220f };
        foreach (var w in widths)
        {
            float ww = w;
            string token = "btn:N" + ww.ToString("0");
            var b = UiButton.Create(row.Content, $"w={ww:0}", () => TestProbe.Hit(token), UiButtonStyle.Secondary, ww, 32f, null, token);
            row.Flow.Child(new RectElement(b.Rect), UiSize.Fixed(ww));
            report.Value += $"{token} (w={ww:0} h=32) → {(b.Sprite != null ? b.Sprite.name + " border=" + b.Sprite.border : "纯色")}\n";
        }
        row.Flow.Apply();
        Line(f, row, 36f);

        Line(f, UiSeparator.Create(f.Rect, T("开关对照", "Toggle comparison")));
        Line(f, UiToggle.Create(f.Rect, T("启用原生素材", "Use native sprites"), UiSkin.UseNative, v =>
        {
            UiSkin.UseNative = v;
            TestLog.Note("UiSkin.UseNative", v.ToString());
            UiMenuWindow.SetStatus("UseNative=" + v + T("（重开菜单生效于新控件）", " (reopen to apply)"));
        }));
        Line(f, UiActionRow.Create(f.Rect, T("重新捕获素材", "Recapture sprites"), T("捕获", "Capture"), () =>
        {
            int n = UiSkin.CaptureAll();
            TestLog.Note("UiSkin.CaptureAll", "new=" + n + " cached=" + UiSkin.Count);
        }));
        Line(f, UiActionRow.Create(f.Rect, T("清空素材缓存", "Clear sprite cache"), T("清空", "Clear"), () =>
        {
            UiSkin.Clear();
            TestLog.Note("UiSkin.Clear", "cached=" + UiSkin.Count);
        }));
        Line(f, UiActionRow.Create(f.Rect, T("打印素材明细到日志", "Dump sprite details"), T("打印", "Dump"), () =>
        {
            foreach (var style in styles)
                foreach (var h in sizes)
                {
                    var sp = UiSurface.ButtonSpriteFor(style, 900f, h);
                    TestLog.Note("sprite pick", $"{style} h={h:0} → {(sp != null ? sp.name + " border=" + sp.border : "纯色(T纯色)")}");
                }
        }));

        list.ApplyLayout();
    }

    private static string StyleName(UiButtonStyle s)
    {
        switch (s)
        {
            case UiButtonStyle.Primary: return "Primary";
            case UiButtonStyle.Danger: return "Danger";
            case UiButtonStyle.Ghost: return "Ghost";
            default: return "Secondary";
        }
    }

    /// <summary>风格→ASCII 单字符 token（热区名/探针键用，不受语言影响）。</summary>
    private static string StyleToken(UiButtonStyle s)
    {
        switch (s)
        {
            case UiButtonStyle.Primary: return "P";
            case UiButtonStyle.Danger: return "D";
            case UiButtonStyle.Ghost: return "G";
            default: return "S";
        }
    }

    // ---------------- 组件全览（外部构建） ----------------

    private static void BuildWidgets(Transform host)
    {
        var list = UiList.Create(host, 0f);
        var f = list.Flow;

        Line(f, UiSeparator.Create(f.Rect, T("组件（由测试模组构建）", "Widgets (test mod)")));
        var status = UiText.Create(f.Rect, T("（点一下试试）", "(try)"), UiTextKind.Note);
        Line(f, status);

        Line(f, UiToggle.Create(f.Rect, T("开关", "Toggle"), true, v => { TestProbe.Hit("t:widget"); status.Value = "Toggle=" + v; }));
        Line(f, UiSlider.Create(f.Rect, T("滑条", "Slider"), 0.35, 0, 1, 0.05, v => { TestProbe.Hit("t:widget"); status.Value = "Slider=" + v.ToString("0.00"); }));
        Line(f, UiStepper.Create(f.Rect, T("步进", "Stepper"), 3, 0, 10, 1, v => { TestProbe.Hit("t:widget"); status.Value = "Stepper=" + v.ToString("0"); }));
        Line(f, UiChoice.Create(f.Rect, T("选择", "Choice"), new[] { "A", "B", "C" }, 0, i => { TestProbe.Hit("t:widget"); status.Value = "Choice=" + i; }));
        Line(f, UiTextInput.Create(f.Rect, T("输入", "Input"), "", v => { TestProbe.Hit("t:widget"); status.Value = "Text='" + v + "'"; }));
        Line(f, UiKeyBind.Create(f.Rect, T("快捷键", "KeyBind"), "f8", v => { TestProbe.Hit("t:widget"); status.Value = "Key=" + v; }));

        var prog = UiProgress.Create(f.Rect, T("进度", "Progress"), 0.25);
        Line(f, prog);
        Line(f, UiActionRow.Create(f.Rect, T("随机进度", "Random progress"), T("随机", "Random"), () =>
        {
            TestProbe.Hit("t:widget");
            if (prog != null) prog.Value = new System.Random().NextDouble();
        }));

        var tabLabel = UiText.Create(f.Rect, T("页签：1", "Tab: 1"), UiTextKind.Note);
        Line(f, tabLabel);
        var tabs = UiTabs.Create(f.Rect, new[] { T("甲", "A"), T("乙", "B"), T("丙", "C") }, 0, i =>
        {
            TestProbe.Hit("t:widget");
            tabLabel.Value = T("页签：", "Tab: ") + (i + 1);
        });
        if (tabs != null) UiStretch.StretchTop(tabs.Rect, UiTheme.TabH);
        Line(f, tabs, UiTheme.TabH);

        Line(f, UiActionRow.Create(f.Rect, T("模态框", "Modal"), T("打开", "Open"), () =>
        {
            TestProbe.Hit("t:widget");
            UiModal.Show(list.Rect, T("确认", "Confirm"), T("来自测试模组的模态框。", "Modal from the test mod."),
                T("确定", "OK"), T("取消", "Cancel"), ok => { TestProbe.Hit("t:modal"); TestLog.Note("modal result", ok.ToString()); });
        }));

        Line(f, UiActionRow.Create(f.Rect, T("列表子项", "Sub list"), T("生成 20 行", "20 rows"), () => BuildInnerList(f)));

        list.ApplyLayout();
    }

    private static void BuildInnerList(UiFlow f)
    {
        try
        {
            var inner = UiList.Create(f.Rect, 200f);
            UiStretch.StretchTop(inner.Rect, 200f);
            Line(f, inner, 200f);
            for (int i = 0; i < 20; i++)
            {
                int idx = i;
                var row = UiNavRow.Create(inner.Flow.Rect, $"#{idx + 1}", T("行", "row"), () => TestProbe.Hit("t:listrow"), 30f);
                inner.Add(row, UiSize.Fixed(30f));
            }
            inner.ApplyLayout();
        }
        catch (Exception ex) { TestLog.Warn("inner list: " + ex.Message); }
    }

    // ---------------- 热区 / 点击探针 ----------------

    private static void BuildZones(Transform host)
    {
        var list = UiList.Create(host, 0f);
        var f = list.Flow;

        Line(f, UiSeparator.Create(f.Rect, T("热区清单（CLI 点击靶子）", "Hot zones (CLI targets)")));
        Line(f, UiText.Create(f.Rect, T(
            "下列名字可用于 `click:<片段>` / `move:<片段>`；后登记（上层）优先。",
            "Use these names with click:<substr> / move:<substr>."), UiTextKind.Note));

        var zoneText = UiText.Create(f.Rect, ZonesText(), UiTextKind.Note);
        Line(f, zoneText);
        Line(f, UiActionRow.Create(f.Rect, T("刷新热区清单", "Refresh zones"), T("刷新", "Refresh"), () =>
        {
            zoneText.Value = ZonesText();
            TestLog.Note("zones", string.Join(" | ", Native.UiPointerRouter.ZoneNames));
        }));

        Line(f, UiSeparator.Create(f.Rect, T("点击探针", "Click probe")));
        var probeText = UiText.Create(f.Rect, ProbesText(), UiTextKind.Note);
        Line(f, probeText);
        Line(f, UiButton.Create(f.Rect, T("探针按钮 A（点我）", "Probe button A"), () => Hit("btn:probeA", probeText), UiButtonStyle.Primary, 220f, 32f, null, "btn:probeA"));
        Line(f, UiButton.Create(f.Rect, T("探针按钮 B（点我）", "Probe button B"), () => Hit("btn:probeB", probeText), UiButtonStyle.Secondary, 220f, 40f, null, "btn:probeB"));
        Line(f, UiButton.Create(f.Rect, T("探针按钮 C（点我）", "Probe button C"), () => Hit("btn:probeC", probeText), UiButtonStyle.Danger, 220f, 56f, null, "btn:probeC"));
        Line(f, UiActionRow.Create(f.Rect, T("跳转到 test.api", "Go to test.api"), T("跳转", "Go"), () =>
        {
            Hit("btn:gotoApi", probeText);
            UiMenuWindow.Navigate("test.api");
        }));
        Line(f, UiSlider.Create(f.Rect, T("拖拽探针（滑条）", "Drag probe (slider)"), 0.5, 0, 1, 0.01,
            v => Hit("slider:main", probeText), 0f, "slider:main"));

        list.ApplyLayout();
    }

    private static void Hit(string key, UiText probeText)
    {
        TestProbe.Hit(key);
        if (probeText != null) probeText.Value = ProbesText();
    }

    private static string ZonesText()
    {
        var names = Native.UiPointerRouter.ZoneNames;
        if (names.Length == 0) return T("（当前无热区——菜单未打开？）", "(no zones)");
        return string.Join("\n", names);
    }

    private static string ProbesText()
    {
        var lines = TestProbe.Lines();
        if (lines.Length == 0) return T("（还没有命中）", "(no hits yet)");
        return string.Join("\n", lines);
    }

    // ---------------- 契约 / 注入状态 ----------------

    private static void BuildApi(Transform host)
    {
        var list = UiList.Create(host, 0f);
        var f = list.Flow;

        Line(f, UiSeparator.Create(f.Rect, T("契约与宿主", "Contract & host")));
        Info(f, T("测试模组版本", "Test mod"), TestInfo.Version + " (" + TestInfo.BuildPlatform + ")");
        Info(f, T("UIKit 宿主在线", "UIKit host"), API.UiKitHost.IsHostAvailable.ToString());
        Info(f, T("宿主版本", "Host version"), API.UiKitHost.HostVersion);
        Info(f, T("契约 API 版本", "API version"), API.UiKitHost.ApiVersion.ToString());
        Info(f, T("第三方提供者", "Providers"), API.UiKitHost.ProviderCount.ToString());
        Info(f, T("页面总数", "Pages"), UiPageCatalog.Count.ToString());
        Info(f, T("当前页", "Current page"), UiMenuWindow.CurrentPage);
        Info(f, T("画布层级", "Canvas order"), UiMenuWindow.SortingOrder.ToString());
        Info(f, T("热区数", "Zones"), Native.UiPointerRouter.ZoneCount.ToString());
        Info(f, T("探针命中总数", "Probe hits"), TestProbe.Total.ToString());
        Info(f, T("部署形态", "Deploy shape"), Core.UiKitPaths.Shape.ToString());
        Info(f, T("日志目录", "Log dir"), Core.UiKitPaths.LogDir);

        Line(f, UiSeparator.Create(f.Rect, T("原生菜单注入", "Native menu injection")));
        Info(f, T("已挂时机 patch", "Timing patch"), Native.NativeMenuInjector.IsPatched.ToString());
        Info(f, T("已注入", "Injected"), Native.NativeMenuInjector.IsInjected.ToString());
        Info(f, T("注册条目数", "Entries"), Native.NativeMenuBridge.Count.ToString());
        Line(f, UiActionRow.Create(f.Rect, T("重新注入", "Re-inject"), T("注入", "Inject"), () =>
        {
            TestProbe.Hit("api:inject");
            Native.NativeMenuInjector.Inject(force: true);
            TestLog.Note("native inject", $"patched={Native.NativeMenuInjector.IsPatched} injected={Native.NativeMenuInjector.IsInjected}");
        }));
        Line(f, UiActionRow.Create(f.Rect, T("加一个测试入口", "Add test entry"), T("添加", "Add"), () =>
        {
            TestProbe.Hit("api:addentry");
            Native.NativeMenuBridge.Add(new Native.NativeMenuEntry
            {
                Id = "uikit.test." + DateTime.Now.Ticks,
                Title = T("测试入口", "Test entry"),
                TitleEn = "Test entry",
                PageId = "test.home",
                Order = 60,
            });
            Native.NativeMenuInjector.Inject(force: true);
        }));
        Line(f, UiActionRow.Create(f.Rect, T("移除测试入口", "Remove test entries"), T("移除", "Remove"), () =>
        {
            TestProbe.Hit("api:delentry");
            var all = Native.NativeMenuBridge.Entries;
            for (int i = 0; i < all.Length; i++)
                if (all[i].Id.StartsWith("uikit.test.", StringComparison.Ordinal)) Native.NativeMenuBridge.Remove(all[i].Id);
            Native.NativeMenuInjector.Inject(force: true);
        }));
        Line(f, UiActionRow.Create(f.Rect, T("打印输入环境快照", "Log input probe"), T("打印", "Log"), () =>
        {
            TestProbe.Hit("api:probe");
            Native.UiInputGuard.LogProbe();
        }));

        Line(f, UiSeparator.Create(f.Rect, T("报告", "Report")));
        Info(f, T("PASS / FAIL", "PASS / FAIL"), TestLog.PassCount + " / " + TestLog.FailCount);
        Line(f, UiActionRow.Create(f.Rect, T("立即写报告文件", "Flush report"), T("写入", "Flush"), () => TestLog.Flush()));

        list.ApplyLayout();
    }

    private static void Info(UiFlow f, string label, string value)
        => Line(f, UiInfoRow.Create(f.Rect, label, value ?? ""));

    // ---------------- 小工具 ----------------

    /// <summary>把控件加进纵向流（<paramref name="fixedH"/> &gt; 0 时用固定高度）。</summary>
    private static void Line(UiFlow f, UiWidget w, float fixedH = 0f)
    {
        if (w == null || f == null || w.Rect == null) return;
        f.Child(new RectElement(w.Rect), fixedH > 0f ? UiSize.Fixed(fixedH) : UiSize.Auto);
    }

    private static void Nav(UiList list, string pageId, string label, string hint)
        => Line(list.Flow, UiNavRow.Create(list.Flow.Rect, label, hint, () =>
        {
            TestProbe.Hit("nav:" + TokenOf(pageId));
            UiMenuWindow.Navigate(pageId);
        }, 40f, "nav:" + TokenOf(pageId)), 40f);

    /// <summary>页面 id → 短 ASCII token（用于热区名/探针键：<c>nav:buttons</c> / <c>nav:zones</c> …）。</summary>
    private static string TokenOf(string pageId)
    {
        string id = pageId ?? "";
        int dot = id.LastIndexOf('.');
        if (dot >= 0 && dot < id.Length - 1) id = id.Substring(dot + 1);
        return id;
    }
}
