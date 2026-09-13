using System;
using UnityEngine;

namespace OpenNestUIKit.Widgets;

/// <summary>
/// 组件基类：所有基础 UI 组件都是**普通 C# 类**（不是 MonoBehaviour）——
/// IL2CPP 下每注入一个自定义 MonoBehaviour 都多一分风险（`docs/MOD_MENU.md` §八），
/// 而本库已有 <see cref="Core.UiKitBehaviour"/> 做每帧驱动，组件只需持有 RectTransform 与回调。
///
/// 交互一律走 <see cref="Native.UiPointerRouter"/> 热区（不依赖游戏 EventSystem）。
/// </summary>
public abstract class UiWidget
{
    protected RectTransform _rt;

    protected UiWidget(RectTransform rt) { _rt = rt; }

    /// <summary>组件根。</summary>
    public RectTransform Rect => _rt;

    /// <summary>组件 GameObject（可能为 null，已销毁）。</summary>
    public GameObject Go => _rt != null ? _rt.gameObject : null;

    /// <summary>组件名（诊断用）。</summary>
    public string Name => _rt != null ? _rt.name : "<null>";

    /// <summary>可见性。</summary>
    public bool Visible
    {
        get { try { return _rt != null && _rt.gameObject.activeSelf; } catch { return false; } }
        set { try { if (_rt != null) _rt.gameObject.SetActive(value); } catch { } }
    }

    /// <summary>控件是否可用（禁用态：热区 Enabled=false + 视觉变暗；子类可扩展）。</summary>
    public virtual bool Interactable
    {
        get => _interactable;
        set => _interactable = value;
    }

    protected bool _interactable = true;

    /// <summary>位置与尺寸（左上锚点，y 向下）。</summary>
    public void SetRect(float x, float y, float w, float h) => Theme.UiTheme.SetRect(_rt, x, y, w, h);

    /// <summary>只改尺寸。</summary>
    public void SetSize(float w, float h)
    {
        try { if (_rt != null) _rt.sizeDelta = new Vector2(w, h); } catch { }
    }

    /// <summary>销毁（注销热区，避免热区表泄漏）。</summary>
    public virtual void Destroy()
    {
        try { Native.UiPointerRouter.RemoveOwner(this); } catch { }
        try { if (_rt != null) UnityEngine.Object.Destroy(_rt.gameObject); } catch { }
        _rt = null;
    }

    /// <summary>建一个干净 RectTransform 子物体（左上锚点）。</summary>
    protected static RectTransform NewRect(string name, Transform parent)
        => UI.UiKit.MakeRect(name, parent, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, Vector2.zero, new Vector2(0f, 1f));

    /// <summary>给子物体铺满父容器（按钮内文字等用）。</summary>
    protected static RectTransform FillRect(string name, Transform parent, float inset = 0f)
    {
        var rt = UI.UiKit.MakeRectFill(name, parent);
        if (inset != 0f)
        {
            rt.offsetMin = new Vector2(inset, inset * 0.5f);
            rt.offsetMax = new Vector2(-inset, -inset * 0.5f);
        }
        return rt;
    }
}
