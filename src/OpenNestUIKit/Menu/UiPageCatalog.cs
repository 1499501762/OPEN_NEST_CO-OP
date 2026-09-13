using System;
using System.Collections.Generic;
using UnityEngine;
using OpenNestUIKit.API;

namespace OpenNestUIKit.Menu;

/// <summary>
/// 页面目录：内置页 + 第三方声明页的注册表（按 id 取页面）。
/// 菜单窗口只认这里；页面来源与渲染方式解耦（内置演示页与第三方 <see cref="IUiKitProvider"/> 都进这里）。
/// </summary>
public static class UiPageCatalog
{
    private static readonly List<UiPage> _pages = new();
    private static readonly Dictionary<string, UiPage> _byId = new(StringComparer.OrdinalIgnoreCase);
    private static bool _providersCollected;

    /// <summary>目录变化（新增页面/第三方注册变化）→ 菜单需要刷新入口列表。</summary>
    public static event Action Changed;

    /// <summary>主页 id（导航返回时兜底）。</summary>
    public const string HomeId = "home";

    /// <summary>已注册页面数。</summary>
    public static int Count { get { lock (_pages) return _pages.Count; } }

    /// <summary>注册页面（同 id 替换；<c>home</c> = 主页）。</summary>
    public static void Register(UiPage page)
    {
        if (page == null || string.IsNullOrEmpty(page.Id)) return;
        bool changed = false;
        lock (_pages)
        {
            for (int i = 0; i < _pages.Count; i++)
            {
                if (!string.Equals(_pages[i].Id, page.Id, StringComparison.OrdinalIgnoreCase)) continue;
                var old = _pages[i];
                if (ReferenceEquals(old, page)) return;
                try { old.Destroy(); } catch { }
                _pages[i] = page;
                _byId[page.Id] = page;
                changed = true;
                break;
            }
            if (!changed)
            {
                _pages.Add(page);
                _byId[page.Id] = page;
                changed = true;
            }
        }
        if (changed) Raise();
    }

    /// <summary>按 id 取页面（找不到返回主页）。</summary>
    public static UiPage Get(string id)
    {
        EnsureProviders();
        if (!string.IsNullOrEmpty(id))
        {
            lock (_pages)
                if (_byId.TryGetValue(id, out var p) && p != null) return p;
        }
        return Home;
    }

    /// <summary>主页（没有注册过主页时返回一个占位页）。</summary>
    public static UiPage Home
    {
        get
        {
            lock (_pages)
                if (_byId.TryGetValue(HomeId, out var p) && p != null) return p;
            return null;
        }
    }

    /// <summary>已注册页面快照（按注册顺序）。</summary>
    public static UiPage[] All
    {
        get { lock (_pages) return _pages.ToArray(); }
    }

    /// <summary>清空（宿主关闭时调用）。</summary>
    public static void Clear()
    {
        lock (_pages)
        {
            for (int i = 0; i < _pages.Count; i++)
            {
                try { _pages[i]?.Destroy(); } catch { }
            }
            _pages.Clear();
            _byId.Clear();
            _providersCollected = false;
        }
        Raise();
    }

    /// <summary>语言变化：通知各页刷新（默认实现重建）。</summary>
    public static void NotifyLanguageChanged()
    {
        var all = All;
        for (int i = 0; i < all.Length; i++)
        {
            try { all[i]?.OnLanguageChanged(); } catch { }
        }
    }

    /// <summary>把 <see cref="UiKitHost"/> 里第三方注册的 provider 收成页面（幂等，注册表变化后会重收）。</summary>
    public static void EnsureProviders()
    {
        if (_providersCollected) return;
        _providersCollected = true;
        try
        {
            int n = ProviderPages.CollectInto(Register);
            if (n > 0) CoopLog.Info("uikit.ui", () => $"已收编第三方菜单页 {n} 个（providers={UiKitHost.ProviderCount}）");
        }
        catch (Exception ex) { CoopLog.Warn("uikit.ui", () => "collect providers failed: " + ex.Message); }
        // 原生 ESC 菜单里的入口也由**本库统一注入**（第三方声明后什么都不用做）
        try
        {
            int e = ProviderPages.SyncNativeEntries();
            if (e > 0) CoopLog.Info("uikit.native", () => $"已为 {e} 个第三方菜单注入原生 ESC 入口（由 UIKit 负责）");
        }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "sync provider native entries failed: " + ex.Message); }
    }

    /// <summary>第三方注册表变化 → 重新收编（下次取页面时生效）。</summary>
    public static void InvalidateProviders()
    {
        _providersCollected = false;
        Raise();
    }

    private static void Raise()
    {
        try { Changed?.Invoke(); } catch { }
    }
}

/// <summary>把第三方声明式契约（<see cref="IUiKitProvider"/> + <see cref="IUiMenuTree"/>）渲染成真实页面。</summary>
public static class ProviderPages
{
    /// <summary>遍历所有 provider，构建页面并交给 <paramref name="register"/>；返回收编的页面数。</summary>
    public static int CollectInto(Action<UiPage> register)
    {
        if (register == null) return 0;
        var providers = UiKitHost.Providers;
        int total = 0;
        for (int i = 0; i < providers.Length; i++)
        {
            var p = providers[i];
            if (p == null) continue;
            try
            {
                string pid = SafeId(p);
                if (pid.Length == 0) continue;

                var tree = new UiMenuTree(SafeName(p));
                p.BuildMenu(tree);

                // 1) provider 根页（列出它声明的所有子页入口 + 根页自己的行）
                string rootId = "provider:" + pid;
                var rootDef = tree.Root;
                var navTargets = new List<UiPageDef>();
                for (int k = 0; k < tree.Pages.Count; k++)
                {
                    var d = tree.Pages[k];
                    if (d == null || d.Id == rootDef.Id) continue;
                    navTargets.Add(d);
                }

                var rootRows = new List<UiRow>();
                if (rootDef.Rows != null) rootRows.AddRange(rootDef.Rows);

                // ⚠️ 根页自己已经用 `Nav` 指过的子页**不再自动加一行**（否则同一个页面在根页出现两次：
                //    上面是 provider 自己排的带提示的入口，下面又一串同名入口 —— 用户看到就是"重复列表"）。
                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int k = 0; k < rootRows.Count; k++)
                {
                    var r = rootRows[k];
                    if (r == null || r.Kind != UiRowKind.Nav || string.IsNullOrEmpty(r.PageId)) continue;
                    string rid = r.PageId;
                    int colon = rid.LastIndexOf(':');
                    if (colon >= 0) referenced.Add(rid.Substring(colon + 1));   // provider:x:page → page
                    referenced.Add(rid);
                }

                for (int k = 0; k < navTargets.Count; k++)
                {
                    var d = navTargets[k];
                    if (referenced.Contains(d.Id)) continue;                    // 已经显式指过
                    rootRows.Add(new UiRow
                    {
                        Kind = UiRowKind.Nav,
                        Label = d.Title,
                        PageId = "provider:" + pid + ":" + d.Id,
                        ReadOnly = true,
                    });
                }

                // 3) 可选接口 IUiKitDefaults：替第三方在根页底部放一个「恢复默认」（带确认框）。
                //    与 ModMenu 的 IModMenuProvider.ResetToDefaults 同名同义 —— 第三方不用自己拼这一行，
                //    也保证“重置前一定先问一句”是全家族一致的交互。
                if (p is IUiKitDefaults defaults)
                {
                    string who = SafeName(p);
                    rootRows.Add(new UiRow { Kind = UiRowKind.Separator, ReadOnly = true });
                    rootRows.Add(new UiRow
                    {
                        Kind = UiRowKind.Button,
                        Key = "uikit.reset",
                        Label = UiKitLoc.T("恢复默认设置", "Reset to defaults"),
                        Value = UiKitLoc.T("重置", "Reset"),
                        ReadOnly = true,
                        OnClick = () => UiKitHost.Confirm(
                            UiKitLoc.T("恢复默认设置", "Reset to defaults"),
                            UiKitLoc.T("确定要把「" + who + "」的设置恢复成默认值吗？",
                                       "Reset " + who + " to its default settings?"),
                            ok =>
                            {
                                if (!ok) return;
                                try { defaults.ResetToDefaults(); } catch (Exception ex) { CoopLog.Warn("uikit.ui", () => "ResetToDefaults failed: " + ex.Message); }
                                try { UiKitHost.Refresh(); } catch { }
                            }),
                    });
                }

                register(new DeclarativePage(rootId, SafeName(p), rootRows, id => UiPageCatalog.Get(id), rootDef));

                // 2) 每个声明页
                for (int k = 0; k < navTargets.Count; k++)
                {
                    var d = navTargets[k];
                    register(new DeclarativePage("provider:" + pid + ":" + d.Id, d.Title, d.Rows, id => UiPageCatalog.Get(id), d));
                    total++;
                }
                total++;
            }
            catch (Exception ex)
            {
                CoopLog.Warn("uikit.ui", () => $"provider build failed: {ex.Message}");
            }
        }
        return total;
    }

    private static string SafeId(IUiKitProvider p)
    {
        try { return p.Id ?? ""; } catch { return ""; }
    }

    private static string SafeName(IUiKitProvider p)
    {
        try { return string.IsNullOrEmpty(p.DisplayName) ? p.Id : p.DisplayName; } catch { return ""; }
    }

    /// <summary>宿主替第三方注入的**原生 ESC 入口** Id 前缀（用来区分“别人自己注册的条目”）。</summary>
    public const string NativeEntryPrefix = "provider.";

    /// <summary>
    /// 把每个 provider 同步成**游戏原生 ESC 菜单里的一行** —— 即"注入这件事由 UIKit 做"。
    ///
    /// 幂等、可反复调用：
    /// - 已存在的同 Id 条目 → 更新（标题/顺序/页面）；
    /// - provider 已注销 → 移除对应行；
    /// - **只动本库托管的前缀**（<see cref="NativeEntryPrefix"/>），绝不碰别人自己注册的条目。
    ///
    /// 行为定制见 <see cref="IUiKitNativeEntry"/>（不实现 = 默认注入一行）。
    /// </summary>
    public static int SyncNativeEntries()
    {
        var providers = UiKitHost.Providers;
        var wanted = new List<NativeMenuEntry>();
        for (int i = 0; i < providers.Length; i++)
        {
            var p = providers[i];
            if (p == null) continue;
            string pid = SafeId(p);
            if (pid.Length == 0) continue;

            bool show = true;
            int order = 100 + i;
            string title = null, titleEn = null;
            try
            {
                if (p is IUiKitNativeEntry opt)
                {
                    show = opt.ShowInNativeMenu;
                    order = opt.NativeOrder;
                    title = opt.NativeTitle;
                    titleEn = opt.NativeTitleEn;
                }
            }
            catch { /* 第三方实现抛异常 → 退回默认行为 */ }
            if (!show) continue;

            string name = SafeName(p);
            wanted.Add(new NativeMenuEntry
            {
                Id = NativeEntryPrefix + pid,
                // 标题走本地化：`UiKitLoc.T` 在注入时按当前语言选（TitleEn 空则用 Title）
                Title = string.IsNullOrEmpty(title) ? name : title,
                TitleEn = string.IsNullOrEmpty(titleEn) ? name : titleEn,
                PageId = "provider:" + pid,
                Order = order,
                ShowInNative = true,
            });
        }

        // ⚠️ 只有**真的变了**才 Add：`NativeMenuBridge.Add` 会 Change → 触发原生重注入 + 页面重建。
        //    而 `UiKitHost.Refresh()`（第三方刷动态内容）会走这条路 —— 每次都 Change 会把刚建好的页冲掉（死循环/闪屏）。
        var existing = NativeMenuBridge.Entries;
        int added = 0;
        for (int i = 0; i < wanted.Count; i++)
        {
            var w = wanted[i];
            NativeMenuEntry old = null;
            for (int k = 0; k < existing.Length; k++)
            {
                if (string.Equals(existing[k]?.Id, w.Id, StringComparison.OrdinalIgnoreCase)) { old = existing[k]; break; }
            }
            bool same = old != null
                && string.Equals(old.Title, w.Title, StringComparison.Ordinal) && string.Equals(old.TitleEn, w.TitleEn, StringComparison.Ordinal)
                && string.Equals(old.PageId, w.PageId, StringComparison.Ordinal) && old.Order == w.Order && old.ShowInNative == w.ShowInNative;
            if (same) continue;
            NativeMenuBridge.Add(w);
            added++;
        }
        // 清理：provider 消失（模组卸载/禁用）后，它的行不该留在原生菜单里
        var wantedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < wanted.Count; i++) wantedIds.Add(wanted[i].Id);
        var all = NativeMenuBridge.Entries;
        int removed = 0;
        for (int i = 0; i < all.Length; i++)
        {
            var e = all[i];
            if (e == null || string.IsNullOrEmpty(e.Id)) continue;
            if (!e.Id.StartsWith(NativeEntryPrefix, StringComparison.OrdinalIgnoreCase)) continue;   // 不是我们托管的
            if (wantedIds.Contains(e.Id)) continue;
            NativeMenuBridge.Remove(e.Id);
            removed++;
        }
        if (removed > 0) CoopLog.Info("uikit.native", () => $"移除已注销 provider 的原生入口 {removed} 个");
        if (added > 0) CoopLog.Info("uikit.native", () => $"已为 {wanted.Count} 个第三方菜单同步原生 ESC 入口（本次新增/变化 {added} 个，由 UIKit 负责）");
        return added;
    }
}
