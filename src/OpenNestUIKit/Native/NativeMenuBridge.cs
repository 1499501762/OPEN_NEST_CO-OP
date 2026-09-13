using System;
using System.Collections.Generic;

namespace OpenNestUIKit.Native;

/// <summary>
/// 原生菜单项（"多级页面项"）：注册进 <see cref="NativeMenuBridge"/> 后，
/// 本库会在**游戏原生菜单**（ESC 菜单容器）里注入一个按钮，点击 = 打开本库菜单并**直达**
/// <see cref="PageId"/> 页面；关闭本库菜单即回到原生菜单（覆盖式，不改原生菜单树）。
///
/// 多级：<see cref="Parent"/> 指向另一个条目的 <see cref="Id"/> 时，本条目是"子菜单项"——
/// 默认不单独占原生菜单格子，而是出现在**父条目的页面里**（父页自动多一行 Nav 入口）。
/// 需要"原生菜单里直接出现子项"时把 <see cref="ShowInNative"/> 置 true 即可（会带缩进前缀）。
/// </summary>
public sealed class NativeMenuEntry
{
    /// <summary>唯一键（稳定；用于去重/移除）。</summary>
    public string Id = "";

    /// <summary>显示文案（原生按钮文字 + 本库页面标题）。中文。</summary>
    public string Title = "";

    /// <summary>显示文案（英文；空则用 <see cref="Title"/>）。</summary>
    public string TitleEn = "";

    /// <summary>点击后在本库菜单里打开的页面 id（可空 = 打开默认页）。</summary>
    public string PageId;

    /// <summary>自定义点击动作（优先于 <see cref="PageId"/>）。</summary>
    public Action OnClick;

    /// <summary>父条目 id（多级；null/空 = 顶层）。</summary>
    public string Parent;

    /// <summary>
    /// 点击后在**原生面板里的控件页**展开的内容（优先级最高）：返回一组 <see cref="NativeRow"/>，
    /// 会用 <see cref="NativeWidgets"/> 照抄游戏 Settings 页那套控件（大标题/小标题/拖拽条/检查框/下拉框/
    /// 选项卡/主·次按钮/输入框）渲染。
    /// 与 <see cref="OnClick"/>/<see cref="Children"/> 的优先级：<c>RowPage &gt; OnClick &gt; PageId</c>（子条目仍优先于控件页）。
    /// </summary>
    public Func<List<NativeRow>> RowPage;

    /// <summary>插入位置：某个原生按钮名之后（默认 = 设置按钮 `OpenSettingsBtn`）。</summary>
    public string InsertAfter = "OpenSettingsBtn";

    /// <summary>同位置的排序（小的在前）。</summary>
    public int Order;

    /// <summary>是否在原生菜单里单独占一格（子条目默认 false = 只在父页面里出现）。</summary>
    public bool ShowInNative = true;

    /// <summary>说明（本库页面里的灰色小字）。</summary>
    public string Hint;

    public override string ToString() => $"{Id}('{Title}' → {PageId ?? "default"}{(string.IsNullOrEmpty(Parent) ? "" : ", parent=" + Parent)})";
}

/// <summary>
/// 原生菜单项注册表。第三方/内置页都可以注册；本库负责把它们渲染进**游戏原生菜单**。
/// 不抛异常：任何异常只影响该条目自己。
/// </summary>
public static class NativeMenuBridge
{
    private static readonly List<NativeMenuEntry> _entries = new();
    private static readonly object _sync = new();

    /// <summary>注册表变化（新增/移除）→ 原生菜单需要重新注入。</summary>
    public static event Action Changed;

    /// <summary>已注册条目数。</summary>
    public static int Count { get { lock (_sync) return _entries.Count; } }

    /// <summary>已注册条目快照（拷贝）。</summary>
    public static NativeMenuEntry[] Entries
    {
        get { lock (_sync) return _entries.ToArray(); }
    }

    /// <summary>注册（同 Id 替换；null/空 Id 忽略）。</summary>
    public static void Add(NativeMenuEntry entry)
    {
        if (entry == null) return;
        if (string.IsNullOrEmpty(entry.Id)) entry.Id = "entry." + (_entries.Count + 1);
        bool changed = false;
        lock (_sync)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (!string.Equals(_entries[i].Id, entry.Id, StringComparison.OrdinalIgnoreCase)) continue;
                if (ReferenceEquals(_entries[i], entry)) return;
                _entries[i] = entry;
                changed = true;
                break;
            }
            if (!changed) { _entries.Add(entry); changed = true; }
        }
        if (changed) RaiseChanged();
    }

    /// <summary>按 Id 移除。</summary>
    public static void Remove(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        bool removed = false;
        lock (_sync)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
                if (string.Equals(_entries[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    _entries.RemoveAt(i);
                    removed = true;
                }
        }
        if (removed) RaiseChanged();
    }

    /// <summary>清空（宿主关闭时调用）。</summary>
    public static void Clear()
    {
        lock (_sync) _entries.Clear();
        RaiseChanged();
    }

    /// <summary>取某父条目下的子条目（按 Order 排序）。</summary>
    public static NativeMenuEntry[] ChildrenOf(string parentId)
    {
        var list = new List<NativeMenuEntry>();
        var all = Entries;
        for (int i = 0; i < all.Length; i++)
            if (string.Equals(all[i].Parent ?? "", parentId ?? "", StringComparison.OrdinalIgnoreCase)) list.Add(all[i]);
        list.Sort((a, b) => a.Order.CompareTo(b.Order));
        return list.ToArray();
    }

    /// <summary>稳定排序后的顶层条目（Order → Id）。</summary>
    public static NativeMenuEntry[] TopLevel()
    {
        var list = new List<NativeMenuEntry>();
        var all = Entries;
        for (int i = 0; i < all.Length; i++)
            if (string.IsNullOrEmpty(all[i].Parent)) list.Add(all[i]);
        list.Sort((a, b) =>
        {
            int c = a.Order.CompareTo(b.Order);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });
        return list.ToArray();
    }

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }
}
