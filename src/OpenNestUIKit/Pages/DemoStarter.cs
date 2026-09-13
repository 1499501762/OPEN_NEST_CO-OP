using System;
using UnityEngine;
using OpenNestUIKit.Menu;
using OpenNestUIKit.Widgets;

namespace OpenNestUIKit.Pages;

/// <summary>
/// 内置菜单注册（"几个菜单"）：把本库自带的页面（主页/组件总览/动态布局/列表/原生观感/原生注入/关于）
/// 注册进 <see cref="UiPageCatalog"/>，并在**游戏原生菜单**里注册入口项（<see cref="Native.NativeMenuBridge"/>）。
///
/// 这些页面既是**演示**（展示组件与动态布局），也是**自检**（进游戏一眼能看出哪一环坏了）。
/// </summary>
public static class DemoStarter
{
    private static bool _registered;

    /// <summary>注册内置页面与原生菜单入口（幂等）。</summary>
    public static void RegisterBuiltIns()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            UiPageCatalog.Register(new UiSimplePage(UiPageCatalog.HomeId, UiKitLoc.T("原生 UI 库", "Native UI Kit"), HomePage.Build));

            UiPageCatalog.Register(new UiSimplePage("widgets", UiKitLoc.T("组件总览", "Components"), DemoPages.BuildComponents, fillHeight: true));
            UiPageCatalog.Register(new UiSimplePage("layout", UiKitLoc.T("动态布局", "Dynamic Layout"), DemoPages.BuildLayout, fillHeight: true));
            UiPageCatalog.Register(new UiSimplePage("list", UiKitLoc.T("列表 / 滚动", "List / Scroll"), DemoPages.BuildList, fillHeight: true));
            UiPageCatalog.Register(new UiSimplePage("theme", UiKitLoc.T("原生观感", "Native Look"), DemoPages.BuildTheme, fillHeight: true));
            UiPageCatalog.Register(new UiSimplePage("native", UiKitLoc.T("原生菜单注入", "Native Menu"), DemoPages.BuildNative, fillHeight: true));
            UiPageCatalog.Register(new UiSimplePage("about", UiKitLoc.T("关于", "About"), DemoPages.BuildAbout, fillHeight: true));

            // **原生组件画廊**（自有风格，三页覆盖全部组件；见 NativeGallery）
            UiPageCatalog.Register(new UiSimplePage("gallery.buttons", UiKitLoc.T("画廊 · 按钮 / 行", "Gallery · Buttons / Rows"), NativeGallery.BuildButtons, fillHeight: true));
            UiPageCatalog.Register(new UiSimplePage("gallery.inputs", UiKitLoc.T("画廊 · 输入控件", "Gallery · Inputs"), NativeGallery.BuildInputs, fillHeight: true));
            UiPageCatalog.Register(new UiSimplePage("gallery.containers", UiKitLoc.T("画廊 · 容器与浮层", "Gallery · Containers"), NativeGallery.BuildContainers, fillHeight: true));

            // ---------------- 原生菜单入口（**多级**）----------------
            // 结构（`NativeMenuPage` 就是照这个树逐级展开的）：
            //   原生菜单里只占 **一行**（ShowInNative=true）：「模组 UI 库」
            //     └─ 全部页面 ›            （分组：把 6 个原页面列出来）
            //     └─ 原生组件画廊 ›        （分组：3 个画廊页）
            //     └─ 关于
            // 其余条目都只在**我们的原生页**里出现（ShowInNative=false），
            // 这样不会把原生 ESC 列表挤爆（实测原生 8 行已经占满 400 高的面板）。
            Native.NativeMenuBridge.Add(new Native.NativeMenuEntry
            {
                Id = "uikit.home",
                Title = UiKitLoc.T("模组 UI 库", "Mod UI Kit"),
                TitleEn = "Mod UI Kit",
                PageId = UiPageCatalog.HomeId,
                Order = 0,
                ShowInNative = true,        // ★ 只有它进原生 ESC 列表
            });

            Native.NativeMenuBridge.Add(new Native.NativeMenuEntry
            {
                Id = "uikit.pages",
                Title = UiKitLoc.T("全部页面", "All pages"),
                Order = 1,
                Parent = "uikit.home",
                ShowInNative = false,
            });

            string[][] pages =
            {
                new[] { "page.widgets",    "组件总览",     "Components",        "widgets" },
                new[] { "page.layout",     "动态布局",     "Dynamic Layout",    "layout" },
                new[] { "page.list",       "列表 / 滚动",  "List / Scroll",     "list" },
                new[] { "page.theme",      "原生观感",     "Native Look",       "theme" },
                new[] { "page.native",     "原生菜单注入", "Native Menu",       "native" },
                new[] { "page.about",      "关于",         "About",             "about" },
            };
            for (int i = 0; i < pages.Length; i++)
            {
                Native.NativeMenuBridge.Add(new Native.NativeMenuEntry
                {
                    Id = pages[i][0],
                    Title = UiKitLoc.T(pages[i][1], pages[i][2]),
                    TitleEn = pages[i][2],
                    PageId = pages[i][3],
                    Parent = "uikit.pages",
                    Order = i + 1,
                    ShowInNative = false,
                });
            }

            Native.NativeMenuBridge.Add(new Native.NativeMenuEntry
            {
                Id = "uikit.gallery",
                Title = UiKitLoc.T("原生组件画廊", "Native Gallery"),
                Order = 2,
                Parent = "uikit.home",
                ShowInNative = false,
            });

            string[][] gallery =
            {
                new[] { "page.gallery.buttons",    "按钮 / 行组件",  "Buttons / Rows",  "gallery.buttons" },
                new[] { "page.gallery.inputs",     "输入控件",       "Inputs",          "gallery.inputs" },
                new[] { "page.gallery.containers", "容器与浮层",     "Containers",      "gallery.containers" },
            };
            for (int i = 0; i < gallery.Length; i++)
            {
                Native.NativeMenuBridge.Add(new Native.NativeMenuEntry
                {
                    Id = gallery[i][0],
                    Title = UiKitLoc.T(gallery[i][1], gallery[i][2]),
                    TitleEn = gallery[i][2],
                    PageId = gallery[i][3],
                    Parent = "uikit.gallery",
                    Order = i + 1,
                    ShowInNative = false,
                });
            }

            Native.NativeMenuBridge.Add(new Native.NativeMenuEntry
            {
                Id = "uikit.about",
                Title = UiKitLoc.T("关于", "About"),
                TitleEn = "About",
                PageId = "about",
                Parent = "uikit.home",
                Order = 9,
                ShowInNative = false,
            });

            // 第三方声明式契约示例（演示"别的模组怎么挂自己的菜单"）
            API.UiKitHost.Register(new DemoProvider());

            CoopLog.Info("uikit.demo", () => $"内置页面已注册（{UiPageCatalog.Count} 页）+ 原生菜单入口 {Native.NativeMenuBridge.Count} 项");
        }
        catch (Exception ex)
        {
            CoopLog.Error("uikit.demo", () => "RegisterBuiltIns failed: " + ex.Message);
        }
    }
}

/// <summary>主页：各子菜单入口（演示页面栈 + 面包屑 + 返回）。</summary>
internal static class HomePage
{
    public static void Build(Transform host)
    {
        var list = UiList.Create(host, 0f);
        var flow = list.Flow;

        Add(flow, UiSeparator.Create(flow.Rect, UiKitLoc.T("菜单", "Menus")));
        Add(flow, UiText.Create(flow.Rect, UiKitLoc.T(
            "这是原生 UI 库自带的多级菜单：点下面的入口进入子菜单（同一窗口内切页，带面包屑与返回），行为与「跳到另一个菜单」一致。",
            "Multi-level menus: pick an entry below to enter a sub-page (same window, with breadcrumb + back) — behaves like jumping to another menu."), UiTextKind.Note));

        Add(flow, UiNavRow.Create(flow.Rect, UiKitLoc.T("组件总览", "Components"),
            UiKitLoc.T("按钮/开关/滑条/输入…", "buttons, toggles, sliders…"), () => UiMenuWindow.Navigate("widgets"), 44f));
        Add(flow, UiNavRow.Create(flow.Rect, UiKitLoc.T("动态布局", "Dynamic Layout"),
            UiKitLoc.T("内容变化后自动重排", "auto reflow on content change"), () => UiMenuWindow.Navigate("layout"), 44f));
        Add(flow, UiNavRow.Create(flow.Rect, UiKitLoc.T("列表 / 滚动", "List / Scroll"),
            UiKitLoc.T("行池 + 视口裁剪", "row pool + viewport culling"), () => UiMenuWindow.Navigate("list"), 44f));
        Add(flow, UiNavRow.Create(flow.Rect, UiKitLoc.T("原生观感", "Native Look"),
            UiKitLoc.T("UI Box 素材 + 过渡", "UI Box sprites + transitions"), () => UiMenuWindow.Navigate("theme"), 44f));
        Add(flow, UiNavRow.Create(flow.Rect, UiKitLoc.T("原生菜单注入", "Native Menu"),
            UiKitLoc.T("注入到游戏 ESC 菜单", "injected into the game's ESC menu"), () => UiMenuWindow.Navigate("native"), 44f));
        Add(flow, UiNavRow.Create(flow.Rect, UiKitLoc.T("关于", "About"),
            UiKitLoc.T("版本 / 宿主 / 契约", "version / host / contract"), () => UiMenuWindow.Navigate("about"), 44f));

        // 第三方注册页（如果已收编）
        UiPageCatalog.EnsureProviders();
        var providers = API.UiKitHost.Providers;
        if (providers != null && providers.Length > 0)
        {
            Add(flow, UiSeparator.Create(flow.Rect, UiKitLoc.T("第三方模组页面", "Third-party pages")));
            for (int i = 0; i < providers.Length; i++)
            {
                var p = providers[i];
                if (p == null) continue;
                string id = "provider:" + (p.Id ?? "");
                string name = string.IsNullOrEmpty(p.DisplayName) ? p.Id : p.DisplayName;
                Add(flow, UiNavRow.Create(flow.Rect, name,
                    UiKitLoc.T("由模组注册", "registered by a mod"), () => UiMenuWindow.Navigate(id), 40f));
            }
        }

        Add(flow, UiSeparator.Create(flow.Rect, null));
        Add(flow, UiText.Create(flow.Rect, UiKitLoc.T(
            "快捷键：F6 打开/关闭菜单，ESC 返回上一级（栈底时关闭）。",
            "Hotkeys: F6 toggles the menu, ESC goes back (closes at the top level)."), UiTextKind.Note));

        list.ApplyLayout();
    }

    internal static UiWidget Add(Layout.UiFlow flow, UiWidget w, float fixedH = 0f)
    {
        if (w == null) return null;
        flow.Child(new Layout.RectElement(w.Rect), fixedH > 0f ? Layout.UiSize.Fixed(fixedH) : Layout.UiSize.Auto);
        return w;
    }
}
