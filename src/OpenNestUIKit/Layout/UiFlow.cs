using System;
using System.Collections.Generic;
using UnityEngine;

namespace OpenNestUIKit.Layout;

/// <summary>
/// 动态布局容器（纵向/横向流式排版）：**显式测量 → 分配 → 排列**，不依赖 UGUI 布局组。
///
/// 为什么不用 `VerticalLayoutGroup` + `ContentSizeFitter`：
/// ① 本游戏最常见的需求是"**文本换行后的高度未知**"（TMP 的 preferredHeight 只有宽度定下来才有意义），
///    布局组与 ContentSizeFitter 的求解顺序在 IL2CPP 下不可控、容易出重排时序问题；
/// ② 自研流程完全可预测、可日志取证（<see cref="UiLayoutProbe"/>），也避免与滚动视口互相打架。
///
/// 坐标约定：**左上锚点 + y 向下为正**（与 <c>UiKit.Place</c> 一致），容器尺寸写 `sizeDelta`。
/// 用法：
/// <code>
/// var flow = new UiFlow(contentRect) { Padding = UiPadding.All(16), Gap = 8 };
/// flow.Child(label, UiSize.Auto);
/// flow.Child(button, UiSize.Fixed(32));
/// flow.Child(spacer, UiSize.Grow());
/// flow.Apply();   // 内容/文字/语言变化后再次调用即可重排
/// </code>
/// </summary>
public sealed class UiFlow : IUiElement
{
    private sealed class Item
    {
        public IUiElement El;
        public RectTransform Rt;
        public UiSize Size;
        public float Main;      // 主轴尺寸（测量/分配结果）
        public float Cross;     // 交叉轴尺寸
        public bool IsFlow;     // 该子项本身是布局容器 → 排列后递归 Apply
    }

    private readonly List<Item> _items = new();
    private static readonly Dictionary<int, UiFlow> _byHost = new();

    /// <summary>构造：<paramref name="host"/> 是容器自身（其尺寸/子项由本对象管理）。</summary>
    public UiFlow(RectTransform host, UiAxis axis = UiAxis.Vertical)
    {
        _host = host;
        Axis = axis;
        int id = SafeId(host);
        if (id != 0) _byHost[id] = this;
    }

    private readonly RectTransform _host;

    /// <summary>主轴方向。</summary>
    public UiAxis Axis;

    /// <summary>内边距。</summary>
    public UiPadding Padding;

    /// <summary>子项间距。</summary>
    public float Gap;

    /// <summary>交叉轴拉伸（纵向时子项宽度 = 容器内容宽）。</summary>
    public bool CrossStretch = true;

    /// <summary>主轴尺寸由内容决定（纵向时容器高度 = 内容总高）。</summary>
    public bool AutoHeight = true;

    /// <summary>容器根。</summary>
    public RectTransform Rect => _host;

    /// <summary>子项数。</summary>
    public int Count => _items.Count;

    /// <summary>
    /// 最近一次 <see cref="Apply"/> 得到的**内容总高**（纵向流 = 内边距 + 子项高 + 间距；横向流 = 子项最大高）。
    /// ⚠ 给 <c>UiList</c> 算滚动范围用：页面的行是直接挂到 flow 上的（**不走 `UiList.Add`**），
    /// 早期 UiList 用“自己的行列表”累加高 → 恒为 0 → 滚动上限 0 → “没有滚动条、拖拽不动”。
    /// </summary>
    public float ContentHeight { get; private set; }

    /// <summary>取第 <paramref name="i"/> 个子项的矩形（裁剪/诊断用）。</summary>
    public RectTransform ChildRectAt(int i)
        => (i >= 0 && i < _items.Count) ? _items[i].Rt : null;

    /// <summary>取第 <paramref name="i"/> 个子项的主轴尺寸（排列后的高/宽）。</summary>
    public float ChildMainAt(int i) => (i >= 0 && i < _items.Count) ? _items[i].Main : 0f;

    // ---------------- 构造 API ----------------

    /// <summary>加一个可测量元素。</summary>
    public UiFlow Child(IUiElement el, UiSize size)
    {
        if (el == null || el.Rect == null) return this;
        _items.Add(new Item
        {
            El = el,
            Rt = el.Rect,
            Size = size,
            IsFlow = Find(el.Rect) != null,
        });
        return this;
    }

    /// <summary>加一个裸 RectTransform（用 <see cref="UiMeasure"/> 推断高度）。</summary>
    public UiFlow ChildRect(RectTransform rt, UiSize size)
        => rt == null ? this : Child(new RectElement(rt), size);

    /// <summary>加固定高度的空白间隔。</summary>
    public UiFlow Space(float height)
    {
        if (height <= 0f) return this;
        var rt = UI.UiKit.MakeRect("space", _host, new Vector2(0f, 1f), new Vector2(0f, 1f),
            Vector2.zero, Vector2.zero, new Vector2(0f, 1f));
        _items.Add(new Item
        {
            El = new RectElement(rt),
            Rt = rt,
            Size = UiSize.Fixed(height),
            Main = height,
            Cross = 0f,
        });
        return this;
    }

    /// <summary>清空子项（不销毁 GameObject；由调用方决定是否销毁）。</summary>
    public UiFlow Clear()
    {
        _items.Clear();
        return this;
    }

    /// <summary>取某个容器已建的 flow（没有则 null）。</summary>
    public static UiFlow Find(RectTransform host)
    {
        if (host == null) return null;
        int id = SafeId(host);
        if (id == 0) return null;
        return _byHost.TryGetValue(id, out var f) ? f : null;
    }

    /// <summary>注销（容器销毁时调用）。</summary>
    public static void Forget(RectTransform host)
    {
        if (host == null) return;
        int id = SafeId(host);
        if (id != 0) _byHost.Remove(id);
    }

    // ---------------- 测量 / 排列 ----------------

    /// <summary>测量本容器在给定宽度下的总高（供上层容器递归汇总）。</summary>
    public float MeasureHeight(float width)
    {
        if (Axis == UiAxis.Horizontal) return HeightOfHorizontal();   // 横向流的高度 = 子项最大高
        float inner = Mathf.Max(0f, width - Padding.Horizontal);
        Measure(inner, inner, 0f);
        return Padding.Vertical + SumMain() + GapTotal();
    }

    /// <summary>测量本容器在给定高度下的总宽（供上层容器递归汇总）。</summary>
    public float MeasureWidth(float height)
    {
        if (Axis == UiAxis.Vertical) return WidthOfVertical();
        float inner = Mathf.Max(0f, height - Padding.Vertical);
        Measure(inner, inner, 0f);
        return Padding.Horizontal + SumMain() + GapTotal();
    }

    /// <summary>
    /// 排列全部子项（测量 → 分配 → 写 anchoredPosition/sizeDelta）。
    /// **内容/文案/语言变化后再次调用即可动态重排**。
    /// </summary>
    public void Apply()
    {
        try
        {
            float hostW = HostWidth(), hostH = HostHeight();
            if (Axis == UiAxis.Vertical)
            {
                float innerW = Mathf.Max(0f, hostW - Padding.Horizontal);
                Measure(innerW, innerW, hostH - Padding.Vertical);
                float known = AutoHeight ? 0f : Mathf.Max(0f, hostH - Padding.Vertical);
                Distribute(known);
                ArrangeVertical(innerW);

                if (AutoHeight)
                {
                    float total = Padding.Vertical + SumMain() + GapTotal();
                    SetSize(hostW > 0f ? hostW : _host.sizeDelta.x, total);
                }
                ContentHeight = Padding.Vertical + SumMain() + GapTotal();
            }
            else
            {
                float innerH = Mathf.Max(0f, hostH - Padding.Vertical);
                Measure(innerH, hostW - Padding.Horizontal, innerH);
                float known = Mathf.Max(0f, hostW - Padding.Horizontal);
                Distribute(known);
                ArrangeHorizontal(innerH);
            }

            // 递归：子容器按新尺寸重排（其高度已在测量阶段算准，不会反复变化）
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (!it.IsFlow) continue;
                var f = Find(it.Rt);
                if (f != null) f.Apply();
            }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.layout", () => $"UiFlow.Apply failed: {ex.Message}");
        }
    }

    // ---------------- 内部实现 ----------------

    private void Measure(float crossAvail, float mainAvail, float knownMain)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            var s = it.Size;
            switch (s.Mode)
            {
                case UiSizeMode.Fixed:
                    it.Main = s.Value;
                    break;
                case UiSizeMode.Percent:
                    it.Main = knownMain > 0f ? knownMain * s.Value : 0f;
                    break;
                case UiSizeMode.Grow:
                    it.Main = 0f;      // 分配阶段解决
                    break;
                default:
                    it.Main = Axis == UiAxis.Vertical
                        ? UiMeasure.Height(it.Rt, crossAvail)
                        : UiMeasure.Width(it.Rt, crossAvail);
                    break;
            }
            it.Cross = CrossStretch ? crossAvail : UiMeasure.Width(it.Rt, it.Main);
        }
    }

    /// <summary>把容器主轴可用高度分给 Grow 子项（按权重）。</summary>
    private void Distribute(float knownMain)
    {
        if (knownMain <= 0f) return;
        float used = GapTotal();
        float weight = 0f;
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            if (it.Size.Mode == UiSizeMode.Grow) weight += Mathf.Max(0.01f, it.Size.Value);
            else used += it.Main;
        }
        float remain = knownMain - used;
        if (remain <= 0f || weight <= 0f) return;
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            if (it.Size.Mode != UiSizeMode.Grow) continue;
            it.Main = remain * (Mathf.Max(0.01f, it.Size.Value) / weight);
        }
    }

    private void ArrangeVertical(float innerW)
    {
        float y = Padding.Top;
        float x = Padding.Left;
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            if (it.Rt != null)
            {
                float w = CrossStretch ? innerW : Mathf.Min(it.Cross, innerW);
                SetRect(it.Rt, x, y, w, it.Main);
            }
            y += it.Main;
            if (i < _items.Count - 1) y += Gap;
        }
    }

    private void ArrangeHorizontal(float innerH)
    {
        float x = Padding.Left;
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            if (it.Rt != null)
            {
                float h = CrossStretch ? innerH : Mathf.Min(it.Cross, innerH);
                SetRect(it.Rt, x, Padding.Top, it.Main, h);
            }
            x += it.Main;
            if (i < _items.Count - 1) x += Gap;
        }
    }

    private float SumMain()
    {
        float s = 0f;
        for (int i = 0; i < _items.Count; i++) s += _items[i].Main;
        return s;
    }

    private float GapTotal() => _items.Count > 1 ? Gap * (_items.Count - 1) : 0f;

    private float HeightOfHorizontal()
    {
        float max = 0f;
        for (int i = 0; i < _items.Count; i++) max = Mathf.Max(max, _items[i].Cross > 0f ? _items[i].Cross : _items[i].Main);
        return Padding.Vertical + max;
    }

    private float WidthOfVertical()
    {
        float max = 0f;
        for (int i = 0; i < _items.Count; i++) max = Mathf.Max(max, _items[i].Cross);
        return Padding.Horizontal + max;
    }

    private float HostWidth()
    {
        try
        {
            float w = _host.rect.width;
            if (w > 0f) return w;
        }
        catch { }
        return _host != null ? _host.sizeDelta.x : 0f;
    }

    private float HostHeight()
    {
        try
        {
            float h = _host.rect.height;
            if (h > 0f) return h;
        }
        catch { }
        return _host != null ? _host.sizeDelta.y : 0f;
    }

    private void SetSize(float w, float h)
    {
        try { _host.sizeDelta = new Vector2(Mathf.Max(0f, w), Mathf.Max(0f, h)); }
        catch { }
    }

    private static void SetRect(RectTransform rt, float x, float y, float w, float h)
    {
        try
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(Mathf.Max(0f, w), Mathf.Max(0f, h));
        }
        catch { }
    }

    private static int SafeId(RectTransform rt)
    {
        try { return rt == null ? 0 : rt.GetInstanceID(); } catch { return 0; }
    }
}

/// <summary>把裸 RectTransform 包装成 <see cref="IUiElement"/>（测量走 <see cref="UiMeasure"/>）。</summary>
public sealed class RectElement : IUiElement
{
    public RectElement(RectTransform rt) { Rect = rt; }
    public RectTransform Rect { get; }
    public float MeasureHeight(float width) => UiMeasure.Height(Rect, width);
    public float MeasureWidth(float height) => UiMeasure.Width(Rect, height);
}
