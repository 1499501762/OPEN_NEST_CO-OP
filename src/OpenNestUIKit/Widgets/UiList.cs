using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace OpenNestUIKit.Widgets;

/// <summary>
/// 滚动列表：**行对象池 + 视口裁剪 + 自管滚轮/拖拽**。
///
/// 为什么不用 uGUI 的 `ScrollRect` 拖拽：那依赖 `EventSystem`（任务场景里常为未激活），
/// 所以滚动交互走 <see cref="Native.UiPointerRouter"/>（滚轮 + 按住拖动都自己算）。
/// 视口裁剪用 `RectMask2D`（只做裁剪，不做布局），布局由 <see cref="Layout.UiFlow"/> 负责。
///
/// 行池：<see cref="Obtain{T}"/> 复用已有行；<see cref="BeginRebuild"/> / <see cref="EndRebuild"/>
/// 包住一次重建（内部把多余的行 SetActive(false) 留作池，不销毁对象）。
/// </summary>
public sealed class UiList : UiWidget
{
    /// <summary>视口（滚动裁剪区）。</summary>
    public RectTransform Viewport { get; private set; }

    /// <summary>内容容器（行都挂在这里）。</summary>
    public RectTransform Content { get; private set; }

    /// <summary>内容流（行布局）。</summary>
    public Layout.UiFlow Flow { get; private set; }

    private const float BarW = 7f;              // 滚动条占位宽（内容右侧预留；不滚动时也保留，避免宽度跳动）

    private readonly List<UiWidget> _rows = new();
    private readonly List<float> _rowY = new();
    private readonly List<float> _rowH = new();
    private float _scroll;
    private float _viewportH;
    private float _lastW = -1f;                 // 上次布局用的宽度（用于发现“尺寸后到”与窗口尺寸变化）
    private static readonly List<UiList> _live = new();

    // ---- 滚动条（2026-09-13 新增：用户反馈“块内容超出没有自动加滚动条和拖动效果”） ----
    private RectTransform _barTrack, _barThumb;   // 轨道 / 滑块（内容不超出视口时整体隐藏）
    private Native.UiHotZone _barZone;            // 轨道热区（可点可拖 → 定位）
    private Native.UiHotZone _pendingBarZone;     // 待注册（等视口热区先注册，保证命中优先级）

    private UiList(RectTransform rt, RectTransform viewport, RectTransform content, Layout.UiFlow flow, float height) : base(rt)
    {
        Viewport = viewport; Content = content; Flow = flow; _viewportH = height;
    }

    /// <summary>当前滚动偏移（像素）。</summary>
    public float Scroll => _scroll;

    /// <summary>行数。</summary>
    public int Count => _rows.Count;

    /// <summary>内容总高。</summary>
    public float ContentHeight { get; private set; }

    /// <summary>创建滚动列表。<paramref name="height"/> &gt; 0 = 固定高；**0 = 铺满宿主**（页面最外层用）。
    /// 早期版本无论 height 都只把尺寸写成 300×h 而不铺满宿主 → 视口 300×0，
    /// `RectMask2D` 把内容整片裁掉，现象就是“窗口开着但内容看不见”。</summary>
    public static UiList Create(Transform parent, float height, string name = null)
    {
        var rt = NewRect(string.IsNullOrEmpty(name) ? "list" : name, parent);
        if (height > 0f)
        {
            Theme.UiTheme.SetRect(rt, 0f, 0f, 300f, height);
            try
            {
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.offsetMin = new Vector2(0f, -height);
                rt.offsetMax = Vector2.zero;
            }
            catch { }
        }
        else
        {
            Layout.UiStretch.Fill(rt);      // 铺满宿主（页面最外层列表的常规用法）
        }

        var viewport = NewRect("viewport", rt);
        try
        {
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.pivot = new Vector2(0.5f, 0.5f);
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = Vector2.zero;
        }
        catch { }
        try { viewport.gameObject.AddComponent<RectMask2D>(); } catch { }

        var content = NewRect("content", viewport);
        Theme.UiTheme.SetRect(content, 0f, 0f, 300f, 10f);

        // 滚动条（在列表右边缘**内侧**留 7px；内容宽度已相应缩窄，所以不会盖住行右侧的按钮）
        var barTrack = NewRect("scrollbar", rt);
        var trackImg = barTrack.gameObject.AddComponent<Image>();
        trackImg.color = new Color(1f, 1f, 1f, 0.06f);
        trackImg.raycastTarget = false;
        try
        {
            barTrack.anchorMin = new Vector2(1f, 0f);
            barTrack.anchorMax = new Vector2(1f, 1f);
            barTrack.pivot = new Vector2(1f, 0.5f);
            barTrack.offsetMin = new Vector2(-BarW + 2f, 2f);
            barTrack.offsetMax = new Vector2(-1f, -2f);
        }
        catch { }
        var barThumb = NewRect("thumb", barTrack);
        var thumbImg = barThumb.gameObject.AddComponent<Image>();
        thumbImg.color = new Color(1f, 1f, 1f, 0.38f);
        thumbImg.raycastTarget = false;
        try
        {
            barThumb.anchorMin = new Vector2(0f, 1f);
            barThumb.anchorMax = new Vector2(1f, 1f);
            barThumb.pivot = new Vector2(0.5f, 1f);
            barThumb.offsetMin = Vector2.zero;
            barThumb.offsetMax = Vector2.zero;
            barThumb.sizeDelta = new Vector2(0f, 24f);
        }
        catch { }
        /**/
        var flow = new Layout.UiFlow(content)
        {
            Axis = Layout.UiAxis.Vertical,
            Padding = new Layout.UiPadding { Left = 0f, Right = 0f, Top = 0f, Bottom = 8f },
            Gap = Theme.UiTheme.RowGap,
            CrossStretch = true,
            AutoHeight = true,
        };

        var list = new UiList(rt, viewport, content, flow, height)
        {
            _barTrack = barTrack,
            _barThumb = barThumb,
        };

        // 轨道热区：点哪里跳哪里 / 按住拖（比“拖滑块”宽容，整条都能抓）。
        // ⚠ 必须在 `list` 构造之后建（lambda 要捕获 list），且**后加 = 命中优先**（盖在视口热区之上）。
        var barZone = new Native.UiHotZone
        {
            Name = (string.IsNullOrEmpty(name) ? "list" : name) + "-scrollbar",
            Rect = barTrack,
            Owner = list,
            Enabled = false,          // 内容不超出视口时不参与命中（由 UpdateScrollbar 同步）
        };
        barZone.OnPressLocal = local => { list.ScrollToLocalY(local.y); return true; };
        barZone.OnDrag = local => list.ScrollToLocalY(local.y);
        list._barZone = barZone;
        // ⚠ 注册顺序 = 命中优先级（后注册优先）：必须在**视口热区之后**注册，
        //    否则轨道那几像素会被视口热区吃掉（点轨道不会跳转）。
        list._pendingBarZone = barZone;
        Layout.UiMeasure.Register(rt, _ => height, _ => 300f);
        _live.Add(list);

        // 滚轮 + 拖拽（不依赖 EventSystem）
        float step = 42f;                 // 一格滚轮 ≈ 一行多一点（列表行 34 + 间距 6）
        float lastLocalY = 0f;
        Native.UiPointerRouter.Add(new Native.UiHotZone
        {
            Name = (string.IsNullOrEmpty(name) ? "list" : name) + "-viewport",
            Rect = viewport,
            Owner = list,
            // 语义：`delta` = 格数，**正 = 滚轮向上/内容上移**（与 Unity 设备符号一致）。
            // 向下滚（看后面的行）= `_scroll` 变大 → 所以乘 `-WheelSign`（WheelSign 用于现场纠正反符号环境）。
            OnScroll = delta => list.ScrollBy(-delta * step * Native.UiPointerRouter.WheelSign),
            // 按住拖动：手指上移 = 看后面的行（内容跟随手指）——早期这里是个空 lambda，
            // 所以文档写的“按住拖动即可滚动”实际是不生效的（用户反馈“只有 8 行/没法向下滚”）。
            OnPressLocal = local => { lastLocalY = local.y; return true; },
            OnDrag = local =>
            {
                float dy = local.y - lastLocalY;
                lastLocalY = local.y;
                if (Mathf.Abs(dy) > 0.01f) list.ScrollBy(dy);
            },
        });

        // 滚动条热区：**最后注册**（命中优先于视口热区）
        if (list._pendingBarZone != null)
        {
            Native.UiPointerRouter.Add(list._pendingBarZone);
            list._pendingBarZone = null;
        }
        return list;
    }

    /// <summary>加一行（返回行对象，供调用方改文案）。</summary>
    public UiWidget Add(UiWidget row, Layout.UiSize size)
    {
        if (row == null || row.Rect == null) return null;
        _rows.Add(row);
        Flow.Child(new Layout.RectElement(row.Rect), size);
        return row;
    }

    /// <summary>
    /// 取一行：优先复用池中位置 <paramref name="index"/> 的行（类型不匹配则跳过）；
    /// 返回 null = 调用方需要自己建新行，并用 <see cref="Add"/> 追加。
    /// </summary>
    public T Obtain<T>(int index) where T : UiWidget
        => (index >= 0 && index < _rows.Count && _rows[index] is T t) ? t : null;

    /// <summary>结束重建：隐藏池里多余的行（不销毁）。</summary>
    public void EndRebuild(int used)
    {
        for (int i = used; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (r != null) r.Visible = false;
        }
        ApplyLayout();
    }

    /// <summary>清空所有行（销毁对象；页面重建时用）。</summary>
    public void ClearRows()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            try { _rows[i]?.Destroy(); } catch { }
        }
        _rows.Clear();
        Flow.Clear();
        _scroll = 0f;
        ContentHeight = 0f;
    }

    /// <summary>重排 + 重新计算滚动范围 + 视口裁剪。</summary>
    public void ApplyLayout()
    {
        try
        {
            // 视口高度：优先实际矩形（页面用 Fill 铺满宿主时，高度只有布局后才确定）
            float vh = 0f;
            try { vh = _rt != null ? _rt.rect.height : 0f; } catch { }
            if (vh <= 1f) { try { vh = _rt != null ? _rt.sizeDelta.y : 0f; } catch { } }
            if (vh > 1f) _viewportH = vh;

            // 视口宽度：**只信实际矩形**；两者都量不到（父链还没定尺寸）就**下一帧再算**，
            // 绝不能像早期那样回退到创建时的 300 —— 那会把整页按 300 宽落定，
            // 现象就是“窗口开着但内容看不见/挤成一团”（页面是在窗口尺寸就绪前构建的）。
            float vw = Viewport != null ? Viewport.rect.width : 0f;
            if (vw <= 1f && _rt != null) { try { vw = _rt.rect.width; } catch { } }
            if (vw <= 1f) return;                       // 尺寸未定 → 由 TickAll 下一帧重试
            _lastW = vw;

            // ⚠ 2026-09-13（用户：“左右两块外面那个块还是被撑大了，而且有滚动条” / 实测 `mm.list/viewport rect=440x0`）：
            //   宿主 flow 声明 **不自适应高度**（两栏页的桌面式布局：Grow 子项要吃剩余高度）时，内容层必须**铺满视口**。
            //   否则自引用：`flow.known` 取自内容层矩形，内容层高 = Σ子项高，子项（Grow）又依赖 known
            //   → 收敛到 0 → 内嵌列表视口高 0、内容被 RectMask2D 整片裁光。
            bool fillHost = Flow != null && !Flow.AutoHeight;

            if (_rt != null) Theme.UiTheme.SetRect(Content, 0f, 0f, Mathf.Max(40f, vw - BarW),
                                                   fillHost ? Mathf.Max(vh, 1f) : ContentHeight);
            Flow.Apply();

            // 内容高：**以 flow 的实际排列结果为准**。
            // ★ 这是“块内容超出没有自动加滚动条和拖动效果”的真因：声明式页面的行是直接挂到 flow 上的
            //   （不走 `UiList.Add`），下面那段“按 _rows 累加”得到的值是 0 → 滚动上限 0 →
            //   滚轮/拖拽都被 Clamp 成 0，滚动条也判定“内容不超出”而隐藏。
            float flowH = Flow != null ? Flow.ContentHeight : 0f;

            // 记录每行几何（内容坐标系：y 向下；Auto 行按 flow 排列结果取）
            float cursor = 0f;
            _rowY.Clear(); _rowH.Clear();
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                float h = 0f;
                try { h = r != null && r.Rect != null ? r.Rect.sizeDelta.y : 0f; } catch { }
                _rowY.Add(cursor);
                _rowH.Add(h);
                cursor += h + Theme.UiTheme.RowGap;
            }
            float rowsH = _rows.Count > 0 ? Mathf.Max(0f, cursor - Theme.UiTheme.RowGap) : 0f;
            ContentHeight = Mathf.Max(flowH, rowsH);
            // 铺满视口的宿主：滚动范围恒为 0（滚动交给内嵌列表），也就不会再冒出外层滚动条。
            if (fillHost) ContentHeight = Mathf.Max(vh, 1f);

            // ⚠ 2026-09-13（用户：“Chat 左侧框没有聊天记录”）：内容高度的**落定必须放在 ContentHeight 算完之后再补一次**。
            //   上面那次 `SetRect(Content, …, ContentHeight)` 用的是**上一轮**的值：
            //   首次布局 / `ClearRows()`（会把 ContentHeight 归零）之后，它写下去的是 **0 高**，
            //   而 `RectMask2D` 会把 0 高内容区整片裁掉 → “列表里有行、Count>0，但一条也看不见”。
            //   常规页面靠“父尺寸后到”会再排一次盖过去；但**固定尺寸**的宿主（悬浮聊天层）宽度从头到尾不变，
            //   `MaybeRelayout` 因 `_lastW` 相等而直接 return → 永远不收敛。补这一行即一次收敛。
            if (_rt != null) Theme.UiTheme.SetRect(Content, 0f, 0f, Mathf.Max(40f, vw - BarW), ContentHeight);

            float max = Mathf.Max(0f, ContentHeight - _viewportH);
            _scroll = Mathf.Clamp(_scroll, 0f, max);
            ApplyScroll();
            Cull();
        }
        catch (Exception ex) { CoopLog.Warn("uikit.widget", () => "list layout failed: " + ex.Message); }
    }

    /// <summary>按像素滚动。</summary>
    public void ScrollBy(float dy)    {
        float max = Mathf.Max(0f, ContentHeight - _viewportH);
        _scroll = Mathf.Clamp(_scroll + dy, 0f, max);
        ApplyScroll();
        Cull();
    }

    /// <summary>滚到顶部。</summary>
    public void ScrollTop()
    {
        _scroll = 0f;
        ApplyScroll();
        Cull();
    }

    /// <summary>
    /// 把某一行滚到可见位置（第三方契约的 <c>UiKitHost.ScrollToKey</c> 最终走到这里）。
    ///
    /// 行已经完整可见时**不动**（否则点一下列表就“跳一下”，体验很差）。
    /// 坐标系：流的行都是左上对齐（pivot=(0,1)），所以“行顶距内容顶” = <c>-anchoredPosition.y</c>。
    /// 返回是否真的生效（行不在本列表里 / 内容不够长 → false）。
    /// </summary>
    public bool ScrollToWidget(UiWidget w, float pad = 8f)
    {
        try
        {
            if (w == null || w.Rect == null) return false;
            float max = Mathf.Max(0f, ContentHeight - _viewportH);
            if (max <= 0f) return false;
            float y = -w.Rect.anchoredPosition.y;            // 行顶距内容顶的像素
            if (float.IsNaN(y) || float.IsInfinity(y)) return false;
            float rowH = 0f;
            try { rowH = w.Rect.rect.height; } catch { }
            if (y >= _scroll - 0.5f && y + rowH <= _scroll + _viewportH + 0.5f) return true;   // 已可见
            _scroll = Mathf.Clamp(y - Mathf.Max(0f, pad), 0f, max);
            ApplyScroll();
            Cull();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 每帧驱动（<see cref="Core.UiKitBehaviour"/> 调用）：**尺寸后到 / 窗口尺寸变化**时自动重排。
    /// 页面往往在窗口尺寸就绪前就构建了，首次 <see cref="ApplyLayout"/> 量不到宽度 → 这里补算。
    /// </summary>
    public static void TickAll()
    {
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var l = _live[i];
            if (l == null || !l.Alive) { _live.RemoveAt(i); continue; }
            l.MaybeRelayout();
        }
    }

    private bool Alive
    {
        get { try { return _rt != null; } catch { return false; } }
    }

    private void MaybeRelayout()
    {
        try
        {
            if (!Visible) return;
            float w = Viewport != null ? Viewport.rect.width : 0f;
            if (w <= 1f && _rt != null) w = _rt.rect.width;
            if (w <= 1f) return;                                  // 尺寸仍未定
            if (_lastW > 1f && Mathf.Abs(w - _lastW) <= 0.5f) return;   // 没变
            ApplyLayout();
        }
        catch { }
    }

    private void ApplyScroll()
    {
        try
        {
            if (Content != null)
            {
                // ⚠ 符号：`_scroll` = “已经从顶部往下滚了多少像素”（与 `Cull`/`_rowY` 的坐标系一致，
                // 都是 y 向下）。要让**第 _scroll 像素处**的内容贴到视口顶部，内容必须**上移** `_scroll`，
                // 也就是 `anchoredPosition.y = +_scroll`（UGUI 里 y 向上为正）。
                // 早期写成 `SetTopLeft(Content, 0, _scroll)`（= 位置 -_scroll）→ 内容**下移**，
                // 与裁剪计算相反：视觉上“滚动方向反了”，而且行被挤出/误裁 → 看着“行数变少”。
                Theme.UiTheme.SetTopLeft(Content, 0f, 0f);
                var pos = Content.anchoredPosition;
                Content.anchoredPosition = new Vector2(pos.x, _scroll);
            }
            UpdateScrollbar();
        }
        catch { }
    }

    /// <summary>滚动条轨道局部 y → 滚动位置。</summary>
    private void ScrollToLocalY(float localY)
    {
        try
        {
            float max = Mathf.Max(0f, ContentHeight - _viewportH);
            if (max <= 0f || _barTrack == null) return;
            float trackH = _barTrack.rect.height;
            if (trackH <= 1f) return;
            // ⚠ 局部坐标是**相对 pivot** 的（轨道 pivot=(1,0.5) → 原点在中间）：先把原点换算成顶边距离
            float topFromPivot = trackH * (1f - _barTrack.pivot.y);
            float p = Mathf.Clamp01((topFromPivot - localY) / trackH);   // 0=顶 1=底
            _scroll = p * max;
            ApplyScroll();
            Cull();
        }
        catch { }
    }

    /// <summary>滚动条：内容不超出视口 → 整条隐藏；超出 → 按比例算滑块高/位（1:1 跟随 `_scroll`）。</summary>
    private void UpdateScrollbar()
    {
        try
        {
            if (_barTrack == null || _barThumb == null) return;
            float content = Mathf.Max(0f, ContentHeight);
            float view = _viewportH;
            float max = Mathf.Max(0f, content - view);
            bool show = max > 1f && view > 1f;
            if (_barTrack.gameObject.activeSelf != show) _barTrack.gameObject.SetActive(show);
            if (_barZone != null) _barZone.Enabled = show;      // 隐藏时不参与命中
            if (!show) return;

            float trackH = 0f;
            try { trackH = _barTrack.rect.height; } catch { }
            if (trackH <= 1f) return;
            float thumbH = Mathf.Clamp(view * view / content, 24f, trackH);
            float travel = Mathf.Max(0f, trackH - thumbH);
            float p = max > 0f ? Mathf.Clamp01(_scroll / max) : 0f;
            try
            {
                var sd = _barThumb.sizeDelta; sd.y = thumbH; _barThumb.sizeDelta = sd;
                _barThumb.anchoredPosition = new Vector2(0f, -p * travel);   // 顶部锚定，向下为正 → 用负 y
            }
            catch { }
        }
        catch { }
    }

    /// <summary>视口裁剪（把完全在视口外的行临时隐藏，省掉 TMP 的渲染开销）。</summary>
    private void Cull()
    {
        try
        {
            float top = _scroll - 4f;
            float bottom = _scroll + _viewportH + 4f;
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                if (r == null) continue;
                float y0 = _rowY[i], y1 = y0 + _rowH[i];
                bool visible = y1 >= top && y0 <= bottom;
                if (r.Visible != visible) r.Visible = visible;
            }
        }
        catch { }
    }

    /// <summary>
    /// 诊断：所有存活列表的状态（行数 / 内容高 / 视口高 / 滚动偏移 / 能否向下滚）。
    /// 用来回答“滚动方向对不对 / 40 行到底生成了没 / 能不能向下滚”。
    /// </summary>
    public static string ProbeAll()
    {
        var sb = new System.Text.StringBuilder();
        int live = 0;
        for (int i = 0; i < _live.Count; i++)
        {
            var l = _live[i];
            if (l == null || !l.Alive) continue;
            live++;
            float vh = 0f, vw = 0f;
            try { vh = l.Viewport != null ? l.Viewport.rect.height : 0f; } catch { }
            try { vw = l.Viewport != null ? l.Viewport.rect.width : 0f; } catch { }
            sb.Append($"\n  [{live}] '").Append(l._rt.name).Append("（滚动条 ").Append(_barShown(l) ? "显示" : "隐藏").Append("）' rows=").Append(l._rows.Count)
              .Append($" 内容高={l.ContentHeight:F1} 视口={vw:F0}x{vh:F0} 可滚={Mathf.Max(0f, l.ContentHeight - l._viewportH):F1}")
              .Append($" 当前偏移={l._scroll:F1} 滚动条={l.BarProbeText()} 可见行=").Append(l.VisibleRowCount()).Append(l.VisibleRange());
        }
        return sb.Length == 0 ? "没有存活列表" : "存活列表 " + live + " 个：" + sb;
    }

    /// <summary>诊断：滚动条当前是否可见。</summary>
    private static bool _barShown(UiList l)
    {
        try { return l._barTrack != null && l._barTrack.gameObject.activeSelf; } catch { return false; }
    }

    /// <summary>诊断：滑块相对轨道的**顶端比例**（0=顶 1=底）。
    /// 用户反复报“外观到不了极值” → 这里直接给出**实测**位置，比看截图靠谱（同 `UiSlider` 的 `实测屏幕比例`）。</summary>
    public string BarProbeText()
    {
        try
        {
            if (_barTrack == null || _barThumb == null || !_barTrack.gameObject.activeSelf) return "-";
            float trackH = _barTrack.rect.height;
            float thumbH = _barThumb.rect.height;
            if (trackH <= 1f) return "-";
            // 滑块顶边距轨道顶边的距离 → 比例（0=顶 1=底）。锚是 (0,1) 顶对齐、y 向下为负 ⇒ 用 -y。
            float topGap = trackH * _barTrack.pivot.y - (thumbH * _barThumb.pivot.y - _barThumb.anchoredPosition.y);
            float travel = Mathf.Max(1f, trackH - thumbH);
            float p = Mathf.Clamp01(topGap / travel);
            return $"滑块高={thumbH:0.#} 顶距={topGap:0.#} 行程={travel:0.#} 实测比例={p:0.###}"
                 + $" 当前比例={(Mathf.Max(0f, ContentHeight - _viewportH) > 0f ? Mathf.Clamp01(_scroll / Mathf.Max(0f, ContentHeight - _viewportH)) : 0f):0.###}";
        }
        catch { return "?"; }
    }

    /// <summary>当前可见行数（诊断）。</summary>
    private int VisibleRowCount()
    {
        int n = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (r != null && r.Visible) n++;
        }
        return n;
    }

    /// <summary>可见行号区间（诊断：判断“滚动到底看没看到后面的行”）。</summary>
    private string VisibleRange()
    {
        int first = -1, last = -1;
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (r != null && r.Visible) { if (first < 0) first = i; last = i; }
        }
        return first < 0 ? "（无）" : $"#{first + 1}~#{last + 1}";
    }

    public override void Destroy()
    {
        try { ClearRows(); } catch { }
        try { Layout.UiMeasure.Unregister(_rt); } catch { }
        try { Layout.UiFlow.Forget(Content); } catch { }
        base.Destroy();
    }
}
