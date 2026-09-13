using UnityEngine;

namespace OpenNestUIKit.Layout;

/// <summary>
/// 摆位小工具：把控件贴到宿主矩形上（页面/面板里最常用）。
/// 坐标约定与全库一致：**左上锚点 + y 向下为正**。
/// </summary>
public static class UiStretch
{
    /// <summary>铺满宿主（四边到边）。</summary>
    public static void Fill(RectTransform rt, float inset = 0f)
    {
        if (rt == null) return;
        try
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
        }
        catch { }
    }

    /// <summary>横向铺满宿主、固定高度（贴顶）。</summary>
    public static void StretchTop(RectTransform rt, float height, float inset = 0f)
    {
        if (rt == null) return;
        try
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(inset, -(inset + height));
            rt.offsetMax = new Vector2(-inset, -inset);
        }
        catch { }
    }

    /// <summary>显式左上角 + 尺寸。</summary>
    public static void Size(RectTransform rt, float x, float y, float w, float h)
    {
        if (rt == null) return;
        try
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }
        catch { }
    }

    /// <summary>取宿主可用尺寸（优先显式 sizeDelta —— 布局还没跑过时 rect 可能还是 0）。</summary>
    public static Vector2 HostSize(RectTransform host)
    {
        if (host == null) return Vector2.zero;
        try
        {
            var sd = host.sizeDelta;
            if (sd.x > 1f && sd.y > 1f) return sd;
            var r = host.rect;
            return new Vector2(r.width > 1f ? r.width : sd.x, r.height > 1f ? r.height : sd.y);
        }
        catch { return Vector2.zero; }
    }

    /// <summary>取宿主可用尺寸（传 Transform 时自动取 RectTransform；拿不到返回零）。</summary>
    public static Vector2 HostSize(Transform host)
    {
        if (host == null) return Vector2.zero;
        try
        {
            var rt = host as RectTransform;
            if (rt == null) rt = host.GetComponent<RectTransform>();
            return HostSize(rt);
        }
        catch { return Vector2.zero; }
    }
}
