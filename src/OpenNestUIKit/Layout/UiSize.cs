using UnityEngine;

namespace OpenNestUIKit.Layout;

/// <summary>主轴方向。</summary>
public enum UiAxis
{
    Vertical = 0,
    Horizontal = 1,
}

/// <summary>尺寸模式：内容决定 / 固定 / 分享剩余 / 百分比。</summary>
public enum UiSizeMode
{
    /// <summary>内容决定（文本按换行后的高度、容器按子项总和）。</summary>
    Auto = 0,
    /// <summary>固定值（像素，参考分辨率 1920×1080 下的逻辑像素）。</summary>
    Fixed = 1,
    /// <summary>分享主轴剩余空间（按权重）。仅当容器主轴尺寸已知（非 Auto）时有效，否则退化为 Auto。</summary>
    Grow = 2,
    /// <summary>主轴尺寸的百分比（0..1）。</summary>
    Percent = 3,
}

/// <summary>尺寸约束。</summary>
public struct UiSize
{
    public UiSizeMode Mode;
    public float Value;

    /// <summary>内容决定。</summary>
    public static UiSize Auto => new UiSize { Mode = UiSizeMode.Auto, Value = 0f };

    /// <summary>固定尺寸。</summary>
    public static UiSize Fixed(float v) => new UiSize { Mode = UiSizeMode.Fixed, Value = v };

    /// <summary>分享剩余（权重默认 1）。</summary>
    public static UiSize Grow(float weight = 1f) => new UiSize { Mode = UiSizeMode.Grow, Value = weight };

    /// <summary>百分比（0..1）。</summary>
    public static UiSize Percent(float p) => new UiSize { Mode = UiSizeMode.Percent, Value = Mathf.Clamp01(p) };

    public override string ToString() => Mode == UiSizeMode.Fixed ? $"Fixed({Value:0.#})"
        : Mode == UiSizeMode.Grow ? $"Grow({Value:0.##})"
        : Mode == UiSizeMode.Percent ? $"Percent({Value:0.##})" : "Auto";
}

/// <summary>内边距（上下左右）。</summary>
public struct UiPadding
{
    public float Left, Top, Right, Bottom;

    public static UiPadding All(float v) => new UiPadding { Left = v, Top = v, Right = v, Bottom = v };
    public static UiPadding XY(float x, float y) => new UiPadding { Left = x, Top = y, Right = x, Bottom = y };

    public float Horizontal => Left + Right;
    public float Vertical => Top + Bottom;

    public override string ToString() => $"(L{Left:0.#} T{Top:0.#} R{Right:0.#} B{Bottom:0.#})";
}
