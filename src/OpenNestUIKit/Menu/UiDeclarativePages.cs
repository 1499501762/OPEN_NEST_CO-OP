using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using OpenNestUIKit.API;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestUIKit.Menu;

/// <summary>
/// 声明式页面：把 <see cref="UiRow"/> 行模型渲染成真实控件（第三方 provider 的页面走这里）。
/// 内容区用 <see cref="Widgets.UiList"/>（可滚动），每行按 <see cref="UiRowKind"/> 分派到对应组件。
/// </summary>
public sealed class DeclarativePage : UiPage
{
    private readonly string _id, _title;
    private readonly List<UiRow> _rows;
    private readonly Func<string, UiPage> _resolveNav;
    private readonly IUiPageDef _def;
    private readonly List<Widgets.UiWidget> _widgets = new();
    private readonly List<RectTransform> _extras = new();       // 自建容器（两栏等；不属 UiWidget）
    private readonly Dictionary<string, Widgets.UiWidget> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Native.UiHotZone> _hintBound = new();     // 已绑过悬停提示的热区（防重复串联）
    private Widgets.UiList _list;
    /// <summary>本页是否是**两栏（桌面式）**：含 `Columns` 行时，外层不滚动、左右栏各自滚。构建期决定。</summary>
    private bool _twoPane;

    private static bool HasColumns(List<UiRow> rows)
    {
        if (rows == null) return false;
        for (int i = 0; i < rows.Count; i++)
            if (rows[i] != null && rows[i].Kind == UiRowKind.Columns) return true;
        return false;
    }

    public DeclarativePage(string id, string title, IReadOnlyList<UiRow> rows, Func<string, UiPage> resolveNav, IUiPageDef def = null)
    {
        _id = id;
        _title = title;
        _rows = new List<UiRow>();
        if (rows != null) for (int i = 0; i < rows.Count; i++) if (rows[i] != null) _rows.Add(rows[i]);
        _resolveNav = resolveNav;
        _def = def;
    }

    public override string Id => _id;
    public override string Title => _title;
    public override bool FillHeight => true;
    public override float PreferredWidth => _def != null ? _def.PreferredWidth : 0f;
    public override float PreferredHeight => _def != null ? _def.PreferredHeight : 0f;
    public override bool Compact => _def != null && _def.Compact;

    public override void Build(Transform parent)
    {
        _widgets.Clear();
        _extras.Clear();
        _byKey.Clear();
        _hintBound.Clear();
        // ⚠ 外层列表必须**铺满宿主**（`height <= 0` ⇒ `UiStretch.Fill`）。
        //   以前写死 `520f`：既用不满内容区（窗口 700 时可用 ~600），又让内容一超过 520 就冒出垂直滚动条。
        _list = Widgets.UiList.Create(parent, 0f);
        Root = _list.Rect;

        // **两栏（含 `Columns`）页 = 桌面式**：外层不滚动（内容高 = 视口高），左右栏各自内部滚动。
        //   用户：“左右两块外面那个块还是被撑大了，而且有滚动条” —— 真因就是内容高超过外层视口。
        _twoPane = HasColumns(_rows);
        if (_twoPane) { try { _list.Flow.AutoHeight = false; } catch { } }
        Render();
    }

    /// <summary>把某一项滚到可见位置（<c>UiKitHost.ScrollToKey</c> 最终走到这里）。</summary>
    public override bool ScrollToKey(string key)
    {
        if (string.IsNullOrEmpty(key) || _list == null) return false;
        if (!_byKey.TryGetValue(key, out var w) || w == null) return false;
        return _list.ScrollToWidget(w);
    }

    /// <summary>滚回顶部。</summary>
    public override void ScrollTop()
    {
        try { _list?.ScrollTop(); } catch { }
    }

    private void Render()
    {
        if (_list == null) return;
        try
        {
            var flow = _list.Flow;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                var w = BuildRow(row, flow);
                if (w != null)
                {
                    _widgets.Add(w);
                    Index(w, row);
                }
            }
            _list.ApplyLayout();
        }
        catch (Exception ex) { CoopLog.Warn("uikit.ui", () => $"declarative render failed: {ex.Message}"); }
    }

    private Widgets.UiWidget BuildRow(UiRow row, Layout.UiFlow flow)
    {
        switch (row.Kind)
        {
            case UiRowKind.Header:
                return Add(flow, Widgets.UiSeparator.Create(flow.Rect, row.Label), Layout.UiSize.Auto);

            case UiRowKind.Separator:
                return Add(flow, Widgets.UiSeparator.Create(flow.Rect, null), Layout.UiSize.Auto);

            case UiRowKind.Label:
                return Add(flow, Widgets.UiText.Create(flow.Rect, row.Label, Widgets.UiTextKind.Note), Layout.UiSize.Auto);

            case UiRowKind.Info:
                // 键值行：标签列固定宽 + 值列左对齐（扁平工业风的属性表版式）
                return Add(flow, Widgets.UiKvRow.Create(flow.Rect, row.Label, row.Value), Layout.UiSize.Auto);

            case UiRowKind.Nav:
                {
                    string target = row.PageId;
                    var nav = Widgets.UiNavRow.Create(flow.Rect, row.Label, row.Hint, () =>
                    {
                        var page = _resolveNav != null ? _resolveNav(target) : null;
                        UiMenuWindow.Navigate(target);
                    }, selected: row.Selected);
                    return Add(flow, nav, Layout.UiSize.Auto);
                }

            case UiRowKind.Button:
                return Add(flow, Widgets.UiActionRow.Create(flow.Rect, row.Label, row.Value, row.OnClick), Layout.UiSize.Auto);

            case UiRowKind.Toggle:
                {
                    bool cur = string.Equals(row.Value, "true", StringComparison.OrdinalIgnoreCase);
                    return Add(flow, Widgets.UiToggle.Create(flow.Rect, row.Label, cur, v => Write(row, v ? "true" : "false"),
                        zoneName: "toggle:" + row.Key), Layout.UiSize.Auto);
                }

            case UiRowKind.Slider:
                {
                    double cur = ParseNum(row.Value, row.Min);
                    return Add(flow, Widgets.UiSlider.Create(flow.Rect, row.Label, cur, row.Min, row.Max, row.Step,
                        v => Write(row, v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)),
                        zoneName: "slider:" + row.Key), Layout.UiSize.Auto);
                }

            case UiRowKind.Choice:
                {
                    var arr = new List<string>();
                    if (row.Choices != null) for (int k = 0; k < row.Choices.Count; k++) arr.Add(row.Choices[k]);
                    int sel = arr.IndexOf(row.Value ?? "");
                    return Add(flow, Widgets.UiChoice.Create(flow.Rect, row.Label, arr.ToArray(), sel,
                        i => Write(row, i >= 0 && i < arr.Count ? arr[i] : ""), zoneName: "choice:" + row.Key), Layout.UiSize.Auto);
                }

            case UiRowKind.Text:
                return Add(flow, Widgets.UiTextInput.Create(flow.Rect, row.Label, row.Value, v => Write(row, v),
                    height: 0f, zoneName: "input:" + row.Key, placeholder: row.Placeholder, maxLength: row.MaxLength), Layout.UiSize.Auto);

            case UiRowKind.KeyBind:
                return Add(flow, Widgets.UiKeyBind.Create(flow.Rect, row.Label, row.Value, v => Write(row, v)), Layout.UiSize.Auto);

            case UiRowKind.Tabs:
                {
                    var arr = new List<string>();
                    if (row.Choices != null) for (int k = 0; k < row.Choices.Count; k++) arr.Add(row.Choices[k]);
                    int sel = 0;
                    try { int.TryParse(row.Value, out sel); } catch { }
                    return Add(flow, Widgets.UiTabs.Create(flow.Rect, arr.ToArray(), sel,
                        i => Write(row, i.ToString()), zonePrefix: row.Key), Layout.UiSize.Auto);
                }

            case UiRowKind.Progress:
                return Add(flow, Widgets.UiProgress.Create(flow.Rect, row.Label, ParseNum(row.Value, 0)), Layout.UiSize.Auto);

            case UiRowKind.Columns:
                return BuildColumns(row, flow);

            case UiRowKind.List:
                return BuildNestedList(row, flow);

            case UiRowKind.Stepper:
                {
                    double cur = ParseNum(row.Value, row.Min);
                    return Add(flow, Widgets.UiStepper.Create(flow.Rect, row.Label, cur, row.Min, row.Max, row.Step,
                        v => Write(row, v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)),
                        zoneName: "stepper:" + row.Key), Layout.UiSize.Auto);
                }

            case UiRowKind.Foldout:
                return BuildFoldout(row, flow);

            case UiRowKind.SelectableList:
                return BuildSelectableList(row, flow);

            default:
                return null;
        }
    }

    /// <summary>
    /// 可折叠分组：头行（▾/▸ + 标题）可点 → 把新状态写回 → 调用方 Refresh 后重建页面；
    /// 展开时子行**直接进入页面流**（不另开滚动区，也不会跟外层滚动打架）。
    /// </summary>
    private Widgets.UiWidget BuildFoldout(UiRow row, Layout.UiFlow flow)
    {
        try
        {
            bool open = string.Equals(row.Value, "true", StringComparison.OrdinalIgnoreCase);
            var head = Widgets.UiNavRow.Create(flow.Rect, row.Label, null,
                () =>
                {
                    Write(row, open ? "false" : "true");
                    // 展开/收起改变了页面的行组成 ⇒ 必须重建（第三方只需在回调里**保存**状态，不用自己 Refresh）。
                    UiMenuWindow.ReloadCurrentPage();
                }, zoneName: "fold:" + row.Key, selected: open, triangleArrow: true);
            head.SetArrowExpanded(open);
            Add(flow, head, Layout.UiSize.Auto);
            if (open) BuildRows(row.ListRows, flow);
            return head;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.ui", () => "foldout build failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 可选中的列表：条目用导航行但**不上页面栈** —— 点击只把索引写回（选中高亮由调用方重建时带回来）。
    /// </summary>
    private Widgets.UiWidget BuildSelectableList(UiRow row, Layout.UiFlow flow)
    {
        try
        {
            var items = new List<string>();
            if (row.Choices != null) for (int i = 0; i < row.Choices.Count; i++) items.Add(row.Choices[i]);
            var hints = new List<string>();
            if (row.ListHints != null) for (int i = 0; i < row.ListHints.Count; i++) hints.Add(row.ListHints[i]);

            float h = row.ListHeight < 0f ? 0f : (row.ListHeight > 40f ? row.ListHeight : 240f);
            bool grow = row.ListHeight < 0f;      // 负值 = 吃掉剩余高度
            var list = Widgets.UiList.Create(flow.Rect, h, !string.IsNullOrEmpty(row.Key) ? row.Key : "select");
            _widgets.Add(list);

            int sel = 0;
            try { int.TryParse(row.Value, out sel); } catch { }

            for (int i = 0; i < items.Count; i++)
            {
                int index = i;
                // 副文本（右对齐一列，如“2.3.2 · 已加载”）：信息型列表的核心排版
                string hint = i < hints.Count ? hints[i] : null;
                var nav = Widgets.UiNavRow.Create(list.Flow.Rect, items[i], hint,
                    () =>
                    {
                        Write(row, index.ToString());
                        UiMenuWindow.ReloadCurrentPage();      // 选中高亮要重画（第三方只需保存索引）
                    }, zoneName: "pick:" + (row.Key ?? "") + ":" + index,
                    selected: index == sel, arrowText: "");
                // 主文本/副文本都单行省略（用户：“左侧列表没有最大字符数量限制”）
                try { nav.SetSingleLine(true); } catch { }
                list.Add(nav, Layout.UiSize.Auto);
            }
            list.ApplyLayout();
            flow.Child(new Layout.RectElement(list.Rect), grow ? Layout.UiSize.Grow(1f) : Layout.UiSize.Fixed(h));
            return list;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.ui", () => "selectable list build failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 内嵌可滚动列表（固定高 + 自带滚动条）：行数不确定的区域用它，
    /// 列表内部自己滚动，不会把整页撑长。
    /// </summary>
    private Widgets.UiWidget BuildNestedList(UiRow row, Layout.UiFlow flow)
    {
        try
        {
            float h = row.ListHeight < 0f ? 0f : (row.ListHeight > 40f ? row.ListHeight : 240f);
            bool grow = row.ListHeight < 0f;      // 负值 = **吃掉剩余高度**（两栏页的栏内列表用）
            var list = Widgets.UiList.Create(flow.Rect, h, !string.IsNullOrEmpty(row.Key) ? row.Key : "list");
            _widgets.Add(list);
            BuildRows(row.ListRows, list.Flow);
            list.ApplyLayout();
            flow.Child(new Layout.RectElement(list.Rect), grow ? Layout.UiSize.Grow(1f) : Layout.UiSize.Fixed(h));
            return list;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.ui", () => "nested list build failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 两栏容器（原模组“左列表 + 右详情”布局）：左栏固定宽、右栏占剩余，两栏各自一个纵向流。
    /// 高度 = 两栏内容高的较大者（构建后量一次，注册给上层流）。
    /// </summary>
    private Widgets.UiWidget BuildColumns(UiRow row, Layout.UiFlow flow)
    {
        try
        {
            var host = UI.UiKit.MakeRect("columns", flow.Rect, new Vector2(0f, 1f), new Vector2(1f, 1f),
                Vector2.zero, Vector2.zero, new Vector2(0f, 1f));
            try
            {
                host.offsetMin = new Vector2(0f, 0f);
                host.offsetMax = new Vector2(0f, 0f);
            }
            catch { }
            _extras.Add(host);

            float leftW = row.LeftWidth > 40f ? row.LeftWidth : 260f;
            float gap = row.ColumnGap > 0f ? row.ColumnGap : Theme.UiTheme.Gap;

            // 左 / 右栏：宽度**全交给 host 的横向 flow**（左固定宽 + 右吃剩余），并在末尾把这个 flow 挂进页面流。
            //
            // ⚠ 2026-09-13 真因（用户：“ModMenu 右侧块宽度没有撑满宽度”）：以前用 anchor + sizeDelta 手算宽度，
            //   而 **首次** `lf/rf.Apply()` 会把 `colL/colR` 改成**点锚**并把“当时的宽”钉死（= 页面构建时
            //   宿主还是兜底宽 ⇒ `colR` 只有 340）；之后容器变宽也不会再跟着变（点锚不再随父）。
            //   改成 flow 之后：宽度由 `columns` 的**实际宽**分配，而且页面流 `Apply()` 会递归 Apply 它
            //   （`IsFlow`）⇒ 尺寸后到 / 窗口尺寸变化时右栏宽度自动跟上。
            var left = UI.UiKit.MakeRect("colL", host, new Vector2(0f, 1f), new Vector2(0f, 1f),
                Vector2.zero, Vector2.zero, new Vector2(0f, 1f));
            var right = UI.UiKit.MakeRect("colR", host, new Vector2(0f, 1f), new Vector2(0f, 1f),
                Vector2.zero, Vector2.zero, new Vector2(0f, 1f));

            var lf = new Layout.UiFlow(left) { Axis = Layout.UiAxis.Vertical, Gap = Theme.UiTheme.RowGap, CrossStretch = true, AutoHeight = !_twoPane };
            var rf = new Layout.UiFlow(right) { Axis = Layout.UiAxis.Vertical, Gap = Theme.UiTheme.RowGap, CrossStretch = true, AutoHeight = !_twoPane };
            // 横向容器：先定左右栏宽度，再让各自的竖向流排行（递归顺序就是这里给的）
            var hf = new Layout.UiFlow(host)
            {
                Axis = Layout.UiAxis.Horizontal,
                Padding = new Layout.UiPadding(),
                Gap = gap,
                CrossStretch = true,
                AutoHeight = true,
            };
            hf.Child(new Layout.RectElement(left), Layout.UiSize.Fixed(leftW));
            hf.Child(new Layout.RectElement(right), Layout.UiSize.Grow(1f));

            BuildRows(row.LeftRows, lf);
            BuildRows(row.RightRows, rf);

            try { hf.Apply(); } catch { }        // ① 先给左右栏定宽（否则行宽会按旧宽排）
            try { lf.Apply(); } catch { }        // ② 再排各自的竖向内容
            try { rf.Apply(); } catch { }
            float lh = lf.ContentHeight, rh = rf.ContentHeight;
            Layout.UiFlow.Find(left)?.Apply();
            var leftRef = left; var rightRef = right;
            Layout.UiMeasure.Register(host, _ =>
            {
                float a = 0f, b = 0f;
                try { a = Layout.UiFlow.Find(leftRef)?.ContentHeight ?? 0f; } catch { }
                try { b = Layout.UiFlow.Find(rightRef)?.ContentHeight ?? 0f; } catch { }
                return Mathf.Max(a, b);
            }, _ => leftW + 120f);

            // ⚠ 必须把容器**挂进上层流**：不挂的话它不会被测量/排列，
            //   高度停在初值（实测 64）→ 页面内容高算错（滚动条/裁剪跟着错），
            //   只是子节点仍按各自列宽画出来，看起来“好像没问题”。
            //   ⚠ 这里挂的是 **flow 本身**（不是 `RectElement(host)`）：`UiFlow.IsFlow` 才会让上层流递归 Apply 它
            //   ⇒ 容器尺寸后到（构建时还是兜底宽）时右栏宽度能自己跟上。
            // 两栏页还得**吃掉剩余高度**（Grow）：外层不滚动时，这就是“左右栏填满内容区”的关键。
            flow.Child(hf, _twoPane ? Layout.UiSize.Grow(1f) : Layout.UiSize.Auto);
            return null;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.ui", () => "columns build failed: " + ex.Message);
            return null;
        }
    }

    private void BuildRows(IReadOnlyList<UiRow> rows, Layout.UiFlow flow)
    {
        if (rows == null) return;
        for (int i = 0; i < rows.Count; i++)
        {
            var w = BuildRow(rows[i], flow);
            if (w != null)
            {
                _widgets.Add(w);
                Index(w, rows[i]);
            }
        }
    }

    /// <summary>
    /// 登记一行：① 按 <see cref="UiRow.Key"/> 建索引（供 <c>UiKitHost.ScrollToKey</c>）；
    /// ② 没热区的行补一个行热区，并把 <see cref="UiRow.HintFunc"/>/<see cref="UiRow.Hint"/> 绑成悬停提示。
    ///
    /// 为什么在这里做而不是逐个控件工厂做：控件工厂有 10+ 个，且只有“第三方声明式页面”需要这两个能力；
    /// 统一在渲染管线里补，改动面最小（开关/滑条/输入框/导航行本来就有热区，直接复用）。
    /// </summary>
    private void Index(Widgets.UiWidget w, UiRow row)
    {
        if (w == null || row == null) return;
        try
        {
            if (!string.IsNullOrEmpty(row.Key)) _byKey[row.Key] = w;

            bool wantsHint = row.HintFunc != null || !string.IsNullOrEmpty(row.Hint);
            var zones = Native.UiPointerRouter.ZonesOf(w);
            if (zones.Length == 0)
            {
                // ⚠️ 只给**只读行**补兜底热区（信息行/进度条）。可交互行绝不能补：
                //    后登记优先 ⇒ 补上去的整行热区会盖住控件内部的 `<`/`>`、`+`/`-`、页签等子热区，
                //    结果是“行能看到、点不动”。可交互控件的整行热区应该由控件工厂**先**登记（见 UiStepper/UiChoice）。
                if (!row.ReadOnly)
                {
                    CoopLog.Debug("uikit.ui", () => $"row without zone (no tooltip): {row.Kind}/{row.Key}");
                    return;
                }
                var fb = Native.UiPointerRouter.Add(new Native.UiHotZone
                {
                    Name = RowZoneName(row),
                    Rect = w.Rect,
                    Owner = w,
                    Tint = false,
                });
                if (fb != null) zones = new[] { fb };
            }

            if (!wantsHint) return;
            var func = row.HintFunc;
            var text = row.Hint;
            for (int i = 0; i < zones.Length; i++)
            {
                var z = zones[i];
                if (z == null || !_hintBound.Add(z)) continue;      // 已绑过就不重复串联
                Widgets.UiTooltip.Bind(z, () => (func != null ? func() : text) ?? "");
            }
        }
        catch (Exception ex) { CoopLog.Warn("uikit.ui", () => "row index failed: " + ex.Message); }
    }

    private static string RowZoneName(UiRow row)
        => "row:" + (row.Kind.ToString().ToLowerInvariant()) + ":" + (!string.IsNullOrEmpty(row.Key) ? row.Key : (row.Label ?? "-"));

    private static Widgets.UiWidget Add(Layout.UiFlow flow, Widgets.UiWidget w, Layout.UiSize size)
    {
        if (w == null) return null;
        flow.Child(new Layout.RectElement(w.Rect), size);
        return w;
    }

    private static double ParseNum(string s, double fallback)
    {
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d)) return d;
        return fallback;
    }

    private static void Write(UiRow row, string value)
    {
        try { row?.Write?.Invoke(value); }
        catch (Exception ex) { CoopLog.Warn("uikit.ui", () => $"row write failed: {ex.Message}"); }
    }

    public override void Destroy()
    {
        try
        {
            for (int i = 0; i < _widgets.Count; i++) _widgets[i]?.Destroy();
            _widgets.Clear();
            for (int i = 0; i < _extras.Count; i++)
            {
                try { Layout.UiMeasure.Unregister(_extras[i]); } catch { }
                try { if (_extras[i] != null) UnityEngine.Object.Destroy(_extras[i].gameObject); } catch { }
            }
            _extras.Clear();
        }
        catch { }
        _list = null;
        base.Destroy();
    }
}
