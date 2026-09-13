using System;
using UnityEngine;

namespace OpenNestUIKit.Menu;

/// <summary>
/// 菜单页面：一个页面 = 内容区里的一屏。
/// **子菜单 = 页面栈里的下一屏**（表现与"跳到另一个菜单"一致，不需要重建窗口，见 docs/UI_KIT.md §五）。
///
/// 页面实例由 <see cref="UiMenuWindow"/> 缓存（按 id），切页只做 SetActive 显隐 → 页码/输入内容等状态不丢。
/// </summary>
public abstract class UiPage
{
    /// <summary>页面 id（导航/原生菜单项用它定位）。</summary>
    public abstract string Id { get; }

    /// <summary>页面标题（窗口标题 + 面包屑）。</summary>
    public abstract string Title { get; }

    /// <summary>true = 根元素占满内容区高度（列表/滚动页）；false = 由内容决定高度。</summary>
    public virtual bool FillHeight => false;

    /// <summary>首选窗口宽度（0 = 用默认）。不同页可以有不同窗口尺寸（如 ModMenu 双栏要 1180、Coop 紧凑单栏只要 520）。</summary>
    public virtual float PreferredWidth => 0f;

    /// <summary>首选窗口高度（0 = 用默认）。</summary>
    public virtual float PreferredHeight => 0f;

    /// <summary>紧凑密度（行高/间隙/字号小一档；构建期生效）。</summary>
    public virtual bool Compact => false;

    /// <summary>页面根（<see cref="Build"/> 后有效）。</summary>
    public RectTransform Root { get; protected set; }

    /// <summary>
    /// 宿主（<see cref="UiMenuWindow"/>）在 <see cref="Build"/> 后调用：
    /// 页面自己没设 <see cref="Root"/>（如 <see cref="UiSimplePage"/>——构建器只拿到 parent）时，
    /// 就把建给它的宿主当作页面根。
    /// ⚠ 没这一步的后果（2026-09-13 实测）：`IsBuilt` 永远 false → **每次显示都再建一份**，
    /// 而且<see cref="SetVisible"/> 什么都没隐藏 → 旧页全叠在新页上（看起来“垂直方向挤在一起”）。
    /// </summary>
    internal void AttachRoot(RectTransform host)
    {
        if (Root == null) Root = host;
    }

    /// <summary>是否已构建。</summary>
    public bool IsBuilt => Root != null;

    /// <summary>构建页面：在 <paramref name="parent"/> 下建根元素并存到 <see cref="Root"/>。</summary>
    public abstract void Build(Transform parent);

    /// <summary>进入页面（每次切入都会调用）。</summary>
    public virtual void OnEnter() { }

    /// <summary>离开页面（切走时调用）。</summary>
    public virtual void OnExit() { }

    /// <summary>语言变化 → 刷新文案（默认重建：清掉旧根再 <see cref="Build"/>）。</summary>
    public virtual void OnLanguageChanged() { }

    /// <summary>把某一项（按第三方声明的 <c>UiRow.Key</c>）滚到可见位置；不支持则返回 false。</summary>
    public virtual bool ScrollToKey(string key) => false;

    /// <summary>滚回顶部（不支持则什么都不做）。</summary>
    public virtual void ScrollTop() { }

    /// <summary>显隐（切页用）。</summary>
    public void SetVisible(bool on)
    {
        try { if (Root != null) Root.gameObject.SetActive(on); } catch { }
    }

    /// <summary>销毁（菜单关闭时调用）。</summary>
    public virtual void Destroy()
    {
        try { Widgets.UiTextInput.BlurAllInputs(); } catch { }
        try { if (Root != null) UnityEngine.Object.Destroy(Root.gameObject); } catch { }
        Root = null;
    }
}

/// <summary>用委托构建的简单页面（内置演示页与第三方声明页都用它承载）。</summary>
public sealed class UiSimplePage : UiPage
{
    private readonly string _id, _title;
    private readonly Action<Transform> _build;
    private readonly bool _fill;

    public UiSimplePage(string id, string title, Action<Transform> build, bool fillHeight = false)
    {
        _id = id; _title = title; _build = build; _fill = fillHeight;
    }

    public override string Id => _id;
    public override string Title => _title;
    public override bool FillHeight => _fill;

    public override void Build(Transform parent)
    {
        try { _build?.Invoke(parent); }
        catch (Exception ex) { CoopLog.Warn("uikit.ui", () => $"page '{_id}' build failed: {ex.Message}"); }
    }
}
