using System;
using System.Collections.Generic;

namespace OpenNestUIKit.API;

/// <summary>页面（只读视图）：宿主渲染用。第三方通过 <see cref="UiPageDef"/> 构建。</summary>
public interface IUiPageDef
{
    /// <summary>页面 id（同一 provider 内唯一；导航/原生菜单项用它定位）。</summary>
    string Id { get; }

    /// <summary>页面标题（面包屑 + 窗口标题用）。</summary>
    string Title { get; }

    /// <summary>行列表（按声明顺序渲染）。</summary>
    IReadOnlyList<UiRow> Rows { get; }

    /// <summary>首选窗口宽度（0 = 用宿主默认；参考分辨率 1920×1080 的逻辑像素）。</summary>
    float PreferredWidth { get; }

    /// <summary>首选窗口高度（0 = 用宿主默认）。</summary>
    float PreferredHeight { get; }

    /// <summary>紧凑密度（行高/内边距/字号都小一档）——原模组那种窄面板的观感。</summary>
    bool Compact { get; }
}

/// <summary>
/// 页面构建器：第三方用这些「动词方法」声明界面，**不依赖 Unity**（因此同一份模组代码在
/// BepInEx / MelonLoader 双端都能编译）。宿主在打开菜单时调用 <see cref="IUiKitProvider.BuildMenu"/>，
/// 把这里声明的行渲染成真实控件。
///
/// 导航：<see cref="Nav"/> 声明一个子菜单入口 → 宿主点击时把目标页 Push 进页面栈
/// （表现等同"跳到另一个菜单"，见 docs/UI_KIT.md §五）。
/// </summary>
public sealed class UiPageDef : IUiPageDef
{
    private readonly List<UiRow> _rows = new();
    private UiRow _last;        // 最近加的一行（Hint/MarkSelected 修饰它）

    public UiPageDef(string id, string title)
    {
        Id = string.IsNullOrEmpty(id) ? "page" : id;
        Title = title ?? Id;
    }

    public string Id { get; }
    public string Title { get; set; }
    public IReadOnlyList<UiRow> Rows => _rows;

    /// <summary>首选窗口尺寸（0/0 = 用宿主默认）。链式：<c>new UiPageDef(...).Size(520, 660)</c>。</summary>
    public float PreferredWidth { get; private set; }
    public float PreferredHeight { get; private set; }

    /// <summary>紧凑密度（行高/间隙/字号小一档）。</summary>
    public bool Compact { get; private set; }

    /// <summary>声明首选窗口尺寸（参考分辨率逻辑像素）。</summary>
    public UiPageDef Size(float w, float h)
    {
        PreferredWidth = w > 0f ? w : 0f;
        PreferredHeight = h > 0f ? h : 0f;
        return this;
    }

    /// <summary>声明紧凑密度（原模组窄面板那种紧凑观感）。</summary>
    public UiPageDef SetCompact(bool compact = true)
    {
        Compact = compact;
        return this;
    }

    /// <summary>
    /// **两栏容器**（左栏固定宽 + 右栏占剩余）：把“左列表 + 右详情”这类布局搬回来。
    /// 两栏各自是一组子行（动作/文字/输入……与普通页一样）。
    /// </summary>
    public UiPageDef Columns(float leftWidth, Action<UiPageDef> left, Action<UiPageDef> right, float gap = 12f)
    {
        var l = new UiPageDef(Id + "#left", "left");
        var r = new UiPageDef(Id + "#right", "right");
        try { left?.Invoke(l); } catch { }
        try { right?.Invoke(r); } catch { }
        return Add(new UiRow
        {
            Kind = UiRowKind.Columns,
            LeftWidth = leftWidth > 40f ? leftWidth : 40f,
            ColumnGap = gap > 0f ? gap : 12f,
            LeftRows = l.Rows,
            RightRows = r.Rows,
            ReadOnly = true,
        });
    }

    /// <summary>分组标题。</summary>
    public UiPageDef Header(string text)
        => Add(new UiRow { Kind = UiRowKind.Header, Label = text ?? "" });

    /// <summary>说明文字。</summary>
    public UiPageDef Label(string text)
        => Add(new UiRow { Kind = UiRowKind.Label, Label = text ?? "", ReadOnly = true });

    /// <summary>
    /// 键值行（`标签 | 值`，只读）：宿主把标签列固定宽、值列左对齐 → 多行下来两列对齐。
    /// 详情/诊断页的属性表就用它（比 `Label("状态：…")` 那种一行到底好扫读）。
    /// </summary>
    public UiPageDef Info(string label, string value)
        => Add(new UiRow { Kind = UiRowKind.Info, Label = label ?? "", Value = value ?? "", ReadOnly = true });

    /// <summary>分隔线。</summary>
    public UiPageDef Separator()
        => Add(new UiRow { Kind = UiRowKind.Separator, ReadOnly = true });

    /// <summary>动作按钮。</summary>
    public UiPageDef Button(string label, string buttonText, Action onClick)
        => Add(new UiRow
        {
            Kind = UiRowKind.Button,
            Label = label ?? "",
            Value = buttonText ?? "",
            OnClick = onClick,
        });

    /// <summary>子菜单入口（点击进入 <paramref name="pageId"/> 页面）。</summary>
    public UiPageDef Nav(string label, string pageId, string hint = null)
        => Add(new UiRow
        {
            Kind = UiRowKind.Nav,
            Label = label ?? "",
            Hint = hint,
            PageId = pageId,
            ReadOnly = true,
        });

    /// <summary>
    /// 页签栏（一行多列）：<paramref name="tabs"/> 里每个名字占一列，点哪列就以该索引回调。
    ///
    /// 用途：把“原来是一个面板里切页签”的布局搬过来（Coop 的 Steam 大厅 / 局域网，ModMenu 的详情 / 设置 / 诊断）。
    /// 宿主渲染成真页签（选中高亮 + 底部指示条）；回调里通常 <c>UiKitHost.Refresh()</c> 一下让内容跟着切。
    /// </summary>
    public UiPageDef Tabs(string key, IReadOnlyList<string> tabs, int index, Action<int> onChanged)
    {
        var arr = new List<string>();
        if (tabs != null) for (int i = 0; i < tabs.Count; i++) arr.Add(tabs[i]);
        return Add(new UiRow
        {
            Kind = UiRowKind.Tabs,
            Key = key,
            Label = key ?? "",
            Choices = arr,
            Value = Math.Max(0, Math.Min(arr.Count - 1, index)).ToString(),
            ReadOnly = true,
            Write = v =>
            {
                if (!int.TryParse(v, out int i)) return false;
                onChanged?.Invoke(i);
                return true;
            },
        });
    }

    /// <summary>布尔开关。</summary>
    public UiPageDef Toggle(string key, string label, bool value, Action<bool> onChanged)
        => Add(new UiRow
        {
            Kind = UiRowKind.Toggle,
            Key = key,
            Label = label ?? key ?? "",
            Value = value ? "true" : "false",
            Write = v =>
            {
                bool b = ParseBool(v);
                onChanged?.Invoke(b);
                return true;
            },
        });

    /// <summary>数值（滑条 / 步进）。<paramref name="step"/> &lt;= 0 时由宿主取合理默认。</summary>
    public UiPageDef Slider(string key, string label, double value, double min, double max, double step, Action<double> onChanged)
        => Add(new UiRow
        {
            Kind = UiRowKind.Slider,
            Key = key,
            Label = label ?? key ?? "",
            Value = Num(value),
            Min = min,
            Max = max > min ? max : min + 1,
            Step = step > 0 ? step : (max - min) / 100.0,
            Write = v =>
            {
                if (!double.TryParse(v, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double d)) return false;
                onChanged?.Invoke(d);
                return true;
            },
        });

    /// <summary>枚举（循环选择）。</summary>
    public UiPageDef Choice(string key, string label, IReadOnlyList<string> choices, int selected, Action<int> onChanged)
    {
        var arr = new List<string>();
        if (choices != null) for (int i = 0; i < choices.Count; i++) arr.Add(choices[i]);
        return Add(new UiRow
        {
            Kind = UiRowKind.Choice,
            Key = key,
            Label = label ?? key ?? "",
            Choices = arr,
            Value = arr.Count > 0 ? arr[Math.Max(0, Math.Min(arr.Count - 1, selected))] : "",
            Write = v =>
            {
                int idx = arr.IndexOf(v ?? "");
                if (idx < 0) return false;
                onChanged?.Invoke(idx);
                return true;
            },
        });
    }

    /// <summary>文本输入。<paramref name="placeholder"/> = 空文本时的灰色占位；<paramref name="maxLength"/> &lt;= 0 = 不限制。</summary>
    public UiPageDef Text(string key, string label, string value, Action<string> onChanged)
        => Add(new UiRow
        {
            Kind = UiRowKind.Text,
            Key = key,
            Label = label ?? key ?? "",
            Value = value ?? "",
            Write = v =>
            {
                onChanged?.Invoke(v ?? "");
                return true;
            },
        });

    /// <summary>文本输入（带占位提示与最大长度）。</summary>
    public UiPageDef Text(string key, string label, string value, Action<string> onChanged, string placeholder, int maxLength = 0)
    {
        Text(key, label, value, onChanged);
        if (_last != null)
        {
            _last.Placeholder = placeholder;
            _last.MaxLength = maxLength > 0 ? maxLength : 0;
        }
        return this;
    }

    /// <summary>快捷键绑定。</summary>
    public UiPageDef KeyBind(string key, string label, string current, Action<string> onChanged)
        => Add(new UiRow
        {
            Kind = UiRowKind.KeyBind,
            Key = key,
            Label = label ?? key ?? "",
            Value = current ?? "",
            Write = v =>
            {
                onChanged?.Invoke(v ?? "");
                return true;
            },
        });

    /// <summary>只读进度条（<paramref name="value01"/> 会被夹到 0..1）。</summary>
    public UiPageDef Progress(string label, double value01)
        => Add(new UiRow
        {
            Kind = UiRowKind.Progress,
            Label = label ?? "",
            Value = Num(Math.Max(0, Math.Min(1, value01))),
            Min = 0,
            Max = 1,
            ReadOnly = true,
        });

    /// <summary>
    /// **内嵌可滚动列表**：行数不确定的区域（模组列表 / 房间列表）用它 —— 列表自己有滚动条与滞动，
    /// 不会把整页撑长（对比：直接把十儿行 Button 摊在页面上，整页都得滚）。
    /// </summary>
    public UiPageDef List(string key, float height, Action<UiPageDef> build)
    {
        var l = new UiPageDef(Id + "#list", "list");
        try { build?.Invoke(l); } catch { }
        return Add(new UiRow
        {
            Kind = UiRowKind.List,
            Key = key,
            // ⚠ 保留原值（含**负值**）：负 = 吃掉剩余高度（两栏页的栏内列表），0/小值 = 老规矩用 240 默认高。
            ListHeight = height,
            ListRows = l.Rows,
            ReadOnly = true,
        });
    }

    /// <summary>
    /// **可选中的列表**（固定高 + 自带滚动条，条目带选中高亮）：选一个才能做下一步时用它。
    /// 回调给你选中索引；宿主不做任何记忆，重建页面时你自己把 <paramref name="selected"/> 传回来。
    /// </summary>
    /// <summary>
    /// **可选中的列表（带右对齐副文本）**：<paramref name="hints"/> 与 <paramref name="items"/> 一一对应。
    /// 信息型列表（模组列表/房间列表）用它：左边主文本超长**省略**、右边版本/状态**对齐成一列**。
    /// </summary>
    public UiPageDef SelectableList(string key, float height, IReadOnlyList<string> items, IReadOnlyList<string> hints,
        int selected, Action<int> onSelected)
    {
        SelectableList(key, height, items, selected, onSelected);
        if (_last != null)
        {
            var hs = new List<string>();
            if (hints != null) for (int i = 0; i < hints.Count; i++) hs.Add(hints[i] ?? "");
            _last.ListHints = hs;
        }
        return this;
    }

    public UiPageDef SelectableList(string key, float height, IReadOnlyList<string> items, int selected, Action<int> onSelected)
    {
        var arr = new List<string>();
        if (items != null) for (int i = 0; i < items.Count; i++) arr.Add(items[i]);
        return Add(new UiRow
        {
            Kind = UiRowKind.SelectableList,
            Key = key,
            // ⚠ 同样保留原值（负 = 吃掉剩余高度）
            ListHeight = height,
            Choices = arr,
            Value = Math.Max(0, Math.Min(arr.Count, selected)).ToString(),
            Write = v =>
            {
                if (!int.TryParse(v, out int i)) return false;
                onSelected?.Invoke(i);
                return true;
            },
            ReadOnly = true,
        });
    }

    /// <summary>
    /// **± 步进数值行**（`− 12 +`）：与 <see cref="Slider"/> 同参数，但渲染成原生数值行（左右箭头 + 中间值），
    /// 适合精确小范围数值。
    /// </summary>
    public UiPageDef Stepper(string key, string label, double value, double min, double max, double step, Action<double> onChanged)
        => Add(new UiRow
        {
            Kind = UiRowKind.Stepper,
            Key = key,
            Label = label ?? key ?? "",
            Value = Num(value),
            Min = min,
            Max = max > min ? max : min + 1,
            Step = step > 0 ? step : 1,
            Write = v =>
            {
                if (!double.TryParse(v, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double d)) return false;
                onChanged?.Invoke(d);
                return true;
            },
        });

    /// <summary>
    /// **可折叠分组**（`▾ 分组名`）：展开的子行由 <paramref name="body"/> 声明，**展开时直接进入页面流**
    /// （不另开滚动区）。展开/收起状态由调用方保存（回调给你新值，通常接着 <c>UiKitHost.Refresh()</c>）。
    /// </summary>
    public UiPageDef Foldout(string key, string label, bool expanded, Action<bool> onToggle, Action<UiPageDef> body)
    {
        var inner = new UiPageDef(Id + "#fold:" + (key ?? ""), "foldout");
        if (expanded) { try { body?.Invoke(inner); } catch { } }
        return Add(new UiRow
        {
            Kind = UiRowKind.Foldout,
            Key = key,
            Label = label ?? key ?? "",
            Value = expanded ? "true" : "false",
            ListRows = inner.Rows,
            Write = v =>
            {
                onToggle?.Invoke(ParseBool(v));
                return true;
            },
        });
    }

    /// <summary>把已有行加进来（高级用法：宿主/第三方自己构造 <see cref="UiRow"/>）。</summary>
    public UiPageDef Add(UiRow row)
    {
        if (row != null)
        {
            _rows.Add(row);
            _last = row;
        }
        return this;
    }

    // ---------------- 对上一行的修饰（链式；要写在那一行之后） ----------------

    /// <summary>给**刚加的那一行**加静态副说明（灰色小字 / 悬停提示）。</summary>
    public UiPageDef Hint(string text)
    {
        if (_last != null) _last.Hint = text;
        return this;
    }

    /// <summary>
    /// 给**刚加的那一行**加**动态副说明**：鼠标悬停时取一次文本（如“当前值 = 12”）。
    /// 比静态 <see cref="Hint(string)"/> 适合会变的状态；写了它就不画静态那行。
    /// </summary>
    public UiPageDef Hint(Func<string> text)
    {
        if (_last != null) _last.HintFunc = text;
        return this;
    }

    /// <summary>标记“刚加的那一行”为选中态（手搓列表时用；<see cref="SelectableList"/> 不用）。</summary>
    public UiPageDef MarkSelected(bool selected = true)
    {
        if (_last != null) _last.Selected = selected;
        return this;
    }

    // ---------------- 与 OpenNestModMenu.API 同名的别名（一份代码两处可用） ----------------

    /// <summary><see cref="Toggle"/> 的别名（与 <c>OpenNestModMenu.API.IModMenuPage.Bool</c> 同名）。</summary>
    public UiPageDef Bool(string key, string label, bool value, Action<bool> onChanged)
        => Toggle(key, label, value, onChanged);

    /// <summary><see cref="Slider"/> 的别名（与 <c>IModMenuPage.Number</c> 同名）。</summary>
    public UiPageDef Number(string key, string label, double value, double min, double max, double step, Action<double> onChanged)
        => Slider(key, label, value, min, max, step, onChanged);

    /// <summary><see cref="Button"/> 的别名（与 <c>IModMenuPage.Action</c> 同名）。</summary>
    public UiPageDef Action(string label, string buttonText, Action onClick)
        => Button(label, buttonText, onClick);

    private static bool ParseBool(string v)
    {
        string t = (v ?? "").Trim().ToLowerInvariant();
        return t == "true" || t == "1" || t == "on" || t == "yes";
    }

    private static string Num(double d)
        => d.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}
