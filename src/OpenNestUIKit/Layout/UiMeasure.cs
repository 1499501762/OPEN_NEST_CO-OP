using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestUIKit.Layout;

/// <summary>可测量元素（容器与自定义组件实现它，供上层 <see cref="UiFlow"/> 递归汇总）。</summary>
public interface IUiElement
{
    /// <summary>该元素的根 RectTransform。</summary>
    RectTransform Rect { get; }

    /// <summary>给定可用宽度时的首选高度（Auto 尺寸用）。</summary>
    float MeasureHeight(float width);

    /// <summary>给定可用高度时的首选宽度（Auto 尺寸用）。</summary>
    float MeasureWidth(float height);
}

/// <summary>
/// 测量工具：**一切"内容驱动高度"都走这里**（动态布局的关键）。
///
/// 优先级：
/// 1) 显式登记的测量函数（<see cref="Register"/>：文本框/滑条/列表等自报高度）；
/// 2) `LayoutElement.preferredHeight/W`（&gt; 0 时生效）；
/// 3) `TMP_Text`：把宽度写进 rect 后读 `preferredHeight`（**换行后的真实高度**，中英文都准）；
/// 4) 子 <see cref="UiFlow"/>：让子项按该宽度重排后读其总高；
/// 5) 兜底：当前 `sizeDelta`。
///
/// 全部包 `try/catch`：测量失败绝不抛出（退化为兜底值），由 <see cref="UiLayoutProbe"/> 记日志。
/// </summary>
public static class UiMeasure
{
    private static readonly Dictionary<int, Func<float, float>> _heightFns = new();
    private static readonly Dictionary<int, Func<float, float>> _widthFns = new();

    /// <summary>登记测量函数（组件构造时调用；id 用 `GetInstanceID()`）。</summary>
    public static void Register(RectTransform rt, Func<float, float> heightFn, Func<float, float> widthFn = null)
    {
        if (rt == null) return;
        int id = SafeId(rt);
        if (id == 0) return;
        if (heightFn != null) _heightFns[id] = heightFn;
        if (widthFn != null) _widthFns[id] = widthFn;
    }

    /// <summary>注销（组件销毁时调用，避免表变大）。</summary>
    public static void Unregister(RectTransform rt)
    {
        if (rt == null) return;
        int id = SafeId(rt);
        if (id == 0) return;
        _heightFns.Remove(id);
        _widthFns.Remove(id);
    }

    /// <summary>给定可用宽度，求元素首选高度。</summary>
    public static float Height(RectTransform rt, float width)
    {
        if (rt == null) return 0f;
        try
        {
            int id = SafeId(rt);
            if (id != 0 && _heightFns.TryGetValue(id, out var fn))
            {
                float v = fn(width);
                if (v > 0f) return v;
            }

            var le = rt.GetComponent<LayoutElement>();
            if (le != null && le.preferredHeight > 0f) return le.preferredHeight;

            var flow = UiFlow.Find(rt);
            if (flow != null) return flow.MeasureHeight(width);

            // TMP：宽度先落到 rect，再读换行后的首选高度
            var txt = rt.GetComponent<TextMeshProUGUI>();
            if (txt == null) txt = rt.GetComponentInChildren<TextMeshProUGUI>(true);
            if (txt != null)
            {
                if (width > 0f) SetWidth(rt, width);
                float h = txt.preferredHeight;
                if (h > 0f) return h;
            }

            if (rt.sizeDelta.y > 0f) return rt.sizeDelta.y;
        }
        catch { }
        return 0f;
    }

    /// <summary>给定可用高度，求元素首选宽度。</summary>
    public static float Width(RectTransform rt, float height)
    {
        if (rt == null) return 0f;
        try
        {
            int id = SafeId(rt);
            if (id != 0 && _widthFns.TryGetValue(id, out var fn))
            {
                float v = fn(height);
                if (v > 0f) return v;
            }

            var le = rt.GetComponent<LayoutElement>();
            if (le != null && le.preferredWidth > 0f) return le.preferredWidth;

            var flow = UiFlow.Find(rt);
            if (flow != null) return flow.MeasureWidth(height);

            var txt = rt.GetComponent<TextMeshProUGUI>();
            if (txt != null)
            {
                float w = txt.preferredWidth;
                if (w > 0f) return w;
            }

            if (rt.sizeDelta.x > 0f) return rt.sizeDelta.x;
        }
        catch { }
        return 0f;
    }

    /// <summary>把宽度写进 rect（保持左上锚点 + 当前高度）。测文本前必须先做这一步。</summary>
    public static void SetWidth(RectTransform rt, float width)
    {
        if (rt == null) return;
        try
        {
            var sd = rt.sizeDelta;
            rt.sizeDelta = new Vector2(width, sd.y);
        }
        catch { }
    }

    /// <summary>安全取实例 id（IL2CPP 下代理对象异常时返回 0）。</summary>
    private static int SafeId(RectTransform rt)
    {
        try { return rt.GetInstanceID(); } catch { return 0; }
    }
}
