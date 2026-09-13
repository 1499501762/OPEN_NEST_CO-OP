using System.Collections.Generic;

namespace OpenNestUIKit.API;

/// <summary>
/// 声明式菜单树：第三方在 <see cref="IUiKitProvider.BuildMenu"/> 里用它声明页面与子菜单结构。
/// <see cref="Root"/> 是隐式根页（若 provider 不往 Root 加东西，宿主会退化为"把所有页按声明顺序列成入口"）。
/// </summary>
public interface IUiMenuTree
{
    /// <summary>隐式根页（列表页：通常只放若干 <c>Nav</c> 子菜单入口）。</summary>
    UiPageDef Root { get; }

    /// <summary>声明一个新页面（同 id 重复调用返回同一个实例，便于多次补充行）。</summary>
    UiPageDef Page(string id, string title);

    /// <summary>所有已声明页面（含 <see cref="Root"/>）。</summary>
    IReadOnlyList<UiPageDef> Pages { get; }
}

/// <summary><see cref="IUiMenuTree"/> 的默认实现（宿主实例化并传给 provider）。</summary>
public sealed class UiMenuTree : IUiMenuTree
{
    private readonly List<UiPageDef> _pages = new();
    private readonly Dictionary<string, UiPageDef> _byId = new();

    public UiMenuTree(string rootTitle = "菜单")
    {
        Root = new UiPageDef("root", rootTitle);
        _pages.Add(Root);
        _byId[Root.Id] = Root;
    }

    public UiPageDef Root { get; }
    public IReadOnlyList<UiPageDef> Pages => _pages;

    public UiPageDef Page(string id, string title)
    {
        string key = string.IsNullOrEmpty(id) ? "page" : id;
        if (_byId.TryGetValue(key, out var existing))
        {
            if (!string.IsNullOrEmpty(title) && string.IsNullOrEmpty(existing.Title)) existing.Title = title;
            return existing;
        }
        var p = new UiPageDef(key, title);
        _pages.Add(p);
        _byId[key] = p;
        return p;
    }

    /// <summary>按 id 找页面（找不到返回 null）。</summary>
    public UiPageDef Find(string id)
        => (!string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out var p)) ? p : null;
}
