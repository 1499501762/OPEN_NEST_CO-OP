using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestUIKit.Widgets;

/// <summary>
/// 页签栏（扁平工业风：**不用填充底**，选中靠“琥珀文字 + 2px 琥珀下划线”标记）。
///
/// 为什么这样改（用户：“还能更好看吗”）：旧版选中格填暗金 + 2px 下划线，等于**双重强调**，
/// 在深色钢底上看着脏。现在：底色一律同内容底（只留悬停微亮），选中只靠线条与颜色。
/// </summary>
public sealed class UiTabs : UiWidget
{
    private readonly List<Image> _bg = new();
    private readonly List<Image> _under = new();
    private readonly List<UiText> _txt = new();
    private string[] _tabs;
    private int _index;

    /// <summary>切换回调。</summary>
    public Action<int> OnChanged;

    private UiTabs(RectTransform rt) : base(rt) { }

    /// <summary>当前页签索引。</summary>
    public int Index
    {
        get => _index;
        set { _index = Mathf.Clamp(value, 0, Mathf.Max(0, (_tabs?.Length ?? 1) - 1)); Refresh(); }
    }

    /// <summary>创建页签栏。<paramref name="zonePrefix"/> 非空时热区名为 <c>tab:&lt;前缀&gt;:&lt;i&gt;</c>
    /// （一页里可能有多条页签栏 —— 不带前缀彼此重名，自动化就只能碰运气命中）。</summary>
    public static UiTabs Create(Transform parent, string[] tabs, int index, Action<int> onChanged, string zonePrefix = null)
    {
        var rt = NewRect("tabs", parent);
        float h = Theme.UiTheme.TabH;
        Theme.UiTheme.SetRect(rt, 0f, 0f, 400f, h);

        var t = new UiTabs(rt) { _tabs = tabs ?? new string[0], OnChanged = onChanged };
        Layout.UiMeasure.Register(rt, _ => h, _ => 400f);

        try
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.offsetMin = new Vector2(0f, 0f);
            rt.offsetMax = new Vector2(0f, 0f);
            var sd = rt.sizeDelta; sd.x = 0f; sd.y = h; rt.sizeDelta = sd;
        }
        catch { }

        int n = t._tabs.Length;
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            var tabRt = NewRect("tab" + i, rt);
            var bg = tabRt.gameObject.AddComponent<Image>();
            bg.raycastTarget = false;
            try
            {
                tabRt.anchorMin = new Vector2((float)i / n, 0f);
                tabRt.anchorMax = new Vector2((float)(i + 1) / n, 1f);
                tabRt.pivot = new Vector2(0.5f, 0.5f);
                tabRt.offsetMin = new Vector2(1f, 1f);
                tabRt.offsetMax = new Vector2(-1f, -1f);
            }
            catch { }

            var txt = UiText.Create(tabRt, t._tabs[i], UiTextKind.Body, 0f, TextAlignmentOptions.Center);
            try
            {
                txt.Rect.anchorMin = Vector2.zero;
                txt.Rect.anchorMax = Vector2.one;
                txt.Rect.pivot = new Vector2(0.5f, 0.5f);
                txt.Rect.offsetMin = Vector2.zero;
                txt.Rect.offsetMax = Vector2.zero;
            }
            catch { }

            var under = NewRect("under", tabRt);
            var underImg = under.gameObject.AddComponent<Image>();
            underImg.color = Theme.UiTheme.Accent;
            underImg.raycastTarget = false;
            try
            {
                under.anchorMin = new Vector2(0f, 0f);
                under.anchorMax = new Vector2(1f, 0f);
                under.pivot = new Vector2(0.5f, 0f);
                under.offsetMin = Vector2.zero;
                under.offsetMax = Vector2.zero;
                var usd = under.sizeDelta; usd.x = -8f; usd.y = 2f; under.sizeDelta = usd;
                under.anchoredPosition = new Vector2(0f, 0f);
            }
            catch { }

            t._bg.Add(bg);
            t._under.Add(underImg);
            t._txt.Add(txt);
            Native.UiPointerRouter.Add(new Native.UiHotZone
            {
                Name = string.IsNullOrEmpty(zonePrefix) ? ("tab" + i) : ("tab:" + zonePrefix + ":" + i),
                Rect = tabRt,
                Owner = t,
                Bg = bg,
                Tint = true,
                BaseColor = Theme.UiTheme.ContentBg,
                HoverColor = Theme.UiTheme.RowHover,
                PressColor = Theme.UiTheme.RowSelected,
                OnClick = () => t.Select(idx),
            });
        }

        // 整条页签栏下沿 1px 发丝线（工业风：结构与结构之间必有线）
        Theme.ModStyle.BottomRule(rt, Theme.UiTheme.Hairline, Theme.UiTheme.OutlineW);

        t.Index = index;
        return t;
    }

    /// <summary>切到某页签（触发回调）。</summary>
    public void Select(int i)
    {
        int n = _tabs?.Length ?? 0;
        if (n == 0) return;
        int v = ((i % n) + n) % n;
        if (v == _index) return;
        _index = v;
        Refresh();
        try { OnChanged?.Invoke(v); } catch (Exception ex) { CoopLog.Warn("uikit.widget", () => "tab handler failed: " + ex.Message); }
    }

    private void Refresh()
    {
        try
        {
            for (int i = 0; i < _bg.Count; i++)
            {
                bool on = i == _index;
                // 底色不区分选中（只留内容底）；选中 = 琥珀文字 + 下划线
                if (_bg[i] != null) _bg[i].color = Theme.UiTheme.ContentBg;
                if (_under[i] != null) _under[i].enabled = on;
                if (i < _txt.Count && _txt[i] != null)
                {
                    _txt[i].Color = on ? Theme.UiTheme.Accent : Theme.UiTheme.TextSecondary;
                    _txt[i].Text.fontStyle = on ? FontStyles.Bold : FontStyles.Normal;
                }
            }
        }
        catch { }
    }

    public override void Destroy()
    {
        try { Layout.UiMeasure.Unregister(_rt); } catch { }
        base.Destroy();
    }
}
