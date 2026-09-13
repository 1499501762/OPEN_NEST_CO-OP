using System;
using System.Collections.Generic;
using UnityEngine;

namespace OpenNestUIKit.Theme;

/// <summary>
/// 过渡动画（淡入/缩放），由 <see cref="Core.UiKitBehaviour.Update"/> 每帧推进（**不用协程**——
/// IL2CPP 下自定义 MonoBehaviour 的协程有额外风险）。
///
/// 只做最必要的两件事：打开时淡入 + 轻微放大，关闭时淡出。所有动画都是"打断即覆盖"（同一目标后发优先）。
/// </summary>
public static class UiKitTween
{
    private sealed class Item
    {
        public object Target;
        public int Kind;              // 0 = CanvasGroup.alpha, 1 = RectTransform.localScale
        public float From, To, Dur, T;
        public Action Done;
    }

    private static readonly List<Item> _items = new();
    private static readonly List<Item> _done = new();

    /// <summary>淡入/淡出 CanvasGroup.alpha。</summary>
    public static void Fade(CanvasGroup cg, float from, float to, float dur, Action done = null)
        => Add(cg, 0, from, to, dur, done);

    /// <summary>缩放 RectTransform.localScale（等比）。</summary>
    public static void Scale(RectTransform rt, float from, float to, float dur, Action done = null)
        => Add(rt, 1, from, to, dur, done);

    /// <summary>目标是否已有进行中的动画（用于"打断"判断，诊断用）。</summary>
    public static bool IsAnimating(object target)
    {
        for (int i = 0; i < _items.Count; i++) if (ReferenceEquals(_items[i].Target, target)) return true;
        return false;
    }

    /// <summary>立刻把目标置为终值并清掉动画。</summary>
    public static void Complete(object target, float value)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_items[i].Target, target)) _items.RemoveAt(i);
        Apply(target, value);
    }

    /// <summary>清空全部动画（菜单关闭/销毁时调用）。</summary>
    public static void Clear()
    {
        _items.Clear();
        _done.Clear();
    }

    /// <summary>每帧驱动。</summary>
    public static void Tick(float dt)
    {
        if (_items.Count == 0) return;
        try
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var it = _items[i];
                it.T += dt;
                float p = it.Dur <= 0f ? 1f : Mathf.Clamp01(it.T / it.Dur);
                // 缓出（ease-out cubic）：UI 过渡更"跟手"
                float e = 1f - Mathf.Pow(1f - p, 3f);
                Apply(it.Target, Mathf.Lerp(it.From, it.To, e));
                if (p >= 1f)
                {
                    _items.RemoveAt(i);
                    if (it.Done != null) _done.Add(it);
                }
            }
            // 回调在遍历之后统一执行（回调里可能再次 Add/Remove，避免边遍历边改集合）
            for (int i = 0; i < _done.Count; i++)
            {
                try { _done[i].Done?.Invoke(); } catch { }
            }
            _done.Clear();
        }
        catch { /* 每帧驱动绝不抛 */ }
    }

    private static void Add(object target, int kind, float from, float to, float dur, Action done)
    {
        if (target == null) return;
        for (int i = _items.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_items[i].Target, target)) _items.RemoveAt(i);
        Apply(target, from);
        if (dur <= 0f)
        {
            Apply(target, to);
            try { done?.Invoke(); } catch { }
            return;
        }
        _items.Add(new Item { Target = target, Kind = kind, From = from, To = to, Dur = dur, T = 0f, Done = done });
    }

    private static void Apply(object target, float v)
    {
        try
        {
            if (target is CanvasGroup cg) { if (cg != null) cg.alpha = v; }
            else if (target is RectTransform rt) { if (rt != null) rt.localScale = new Vector3(v, v, 1f); }
        }
        catch { }
    }
}
