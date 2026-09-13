using System.Collections.Generic;

namespace OpenNestUIKit.API;

/// <summary>页面里的行种类（宿主按此渲染成真实控件）。</summary>
public enum UiRowKind
{
    /// <summary>分组标题（不可交互）。</summary>
    Header = 0,
    /// <summary>普通说明文字。</summary>
    Label = 1,
    /// <summary>分隔线。</summary>
    Separator = 2,
    /// <summary>动作按钮（点一下就执行）。</summary>
    Button = 3,
    /// <summary>子菜单入口（点击 Push 到另一个页面）。</summary>
    Nav = 4,
    /// <summary>布尔开关。</summary>
    Toggle = 5,
    /// <summary>数值（滑条 / 步进）。</summary>
    Slider = 6,
    /// <summary>枚举（循环选择 / 下拉）。</summary>
    Choice = 7,
    /// <summary>文本输入。</summary>
    Text = 8,
    /// <summary>快捷键绑定。</summary>
    KeyBind = 9,
    /// <summary>只读进度条（0..1）。</summary>
    Progress = 10,
    /// <summary>页签栏（一行多列；<see cref="UiRow.Choices"/> = 页签名，<see cref="UiRow.Value"/> = 当前索引）。
    /// 宿主渲染成可点的页签，点哪列就把索引写回 <see cref="UiRow.Write"/>。</summary>
    Tabs = 11,

    /// <summary>
    /// **两栏容器**（左边固定宽 + 右边占剩余）：<see cref="UiRow.LeftRows"/> / <see cref="UiRow.RightRows"/> 各是一组子行，
    /// 子行种类与本枚举完全相同（可嵌套，但不要再套 Columns）。
    ///
    /// 用途：把原模组的“左边列表 + 右边详情”布局搬回来（ModMenu 的模组列表 / 详情栏）。
    /// </summary>
    Columns = 12,

    /// <summary>
    /// **内嵌可滚动列表**（固定高度，自带滚动条）：<see cref="UiRow.ListRows"/> 是列表里的行（通常是 Button），
    /// <see cref="UiRow.ListHeight"/> 是列表高度。
    ///
    /// 用途：模组列表 / 房间列表这类“行数不确定、应该自己滞动而不是把整页撑长”的区域。
    /// </summary>
    List = 13,

    /// <summary>
    /// **± 步进数值**（`− 12 +`）：与 <see cref="Slider"/> 同一个 <see cref="UiRow.Min"/>/<see cref="UiRow.Max"/>/<see cref="UiRow.Step"/> 语义，
    /// 宿主渲染成原生“数值行”（游戏设置页那种左右箭头 + 中间数值）。
    /// 用途：精确小范围数值（速度/音量档位），比滑条好点。
    /// </summary>
    Stepper = 14,

    /// <summary>
    /// **可折叠分组**（`▾ 分组名`）：<see cref="UiRow.Value"/> = "true"/"false" 表示展开/收起，
    /// <see cref="UiRow.ListRows"/> 是组内子行（展开时才渲染，展开的行直接进入页面流，不另开滚动区）。
    ///
    /// 用途：长设置页分组收纳（ImGui TreeNode / UIElements Foldout / ConfigManager 分区）。
    /// </summary>
    Foldout = 15,

    /// <summary>
    /// **可选中的列表**（固定高 + 自带滚动条，每项是一行带选中高亮的条目）：
    /// <see cref="UiRow.Choices"/> = 条目文本，<see cref="UiRow.Value"/> = 当前选中索引，
    /// <see cref="UiRow.Selected"/> 在**手搓行**（<c>Add</c>）时用来单行标记选中。
    ///
    /// 用途：列表+详情布局（选一个才能做下一步），比“每行一个按钮”语义清楚。
    /// </summary>
    SelectableList = 16,

    /// <summary>
    /// **键值行**（`标签 | 值`，只读）：<see cref="Label"/> = 标签列，<see cref="Value"/> = 值列。
    ///
    /// 用途：详情/诊断页的“属性表”（状态 / 文件 / 配置 / 版本 / 作者 …）。与 <see cref="Label"/> 的区别：
    /// 宿主把多行的**标签、值各自对齐成一列**（标签列固定宽 + 值列左对齐），扫读性比“标签：值”一行到底好得多。
    /// </summary>
    Info = 17,
}

/// <summary>
/// 声明式行模型：第三方用「动词方法」描述界面（见 <see cref="UiPageDef"/>），
/// 宿主把它渲染成真实控件（<c>Widgets/UiRow.cs</c>）。**不含任何 Unity 类型**，双端通用。
///
/// 写回约定（与 <c>OpenNestModMenu.API.IModMenuPage</c> 一致）：<c>Write</c> 直接回调给第三方，
/// 由第三方自己决定往哪写（配置文件/内存），宿主只负责把用户操作转成 <c>Write</c> 调用。
/// </summary>
public sealed class UiRow
{
    public UiRowKind Kind { get; set; }
    /// <summary>稳定键（第三方自己用来定位设置项；可空）。</summary>
    public string Key { get; set; }
    /// <summary>显示标签。</summary>
    public string Label { get; set; }
    /// <summary>当前值（字符串形式；Toggle = "true"/"false"）。</summary>
    public string Value { get; set; }
    /// <summary>副说明（灰色小字，可空）。</summary>
    public string Hint { get; set; }
    /// <summary>
    /// **动态副说明 / 悬停提示**（可空）：设了它，鼠标悬停在该行上时显示一行实时文本
    /// （如“当前值 = 12”“按住 Shift 可批量”），比固定的 <see cref="Hint"/> 更适合会变的状态。
    /// 优先级高于 <see cref="Hint"/>（有动态就不再画静态那行）。
    /// </summary>
    public System.Func<string> HintFunc { get; set; }
    /// <summary>只读（界面不可改）。</summary>
    public bool ReadOnly { get; set; }
    /// <summary>选中态（仅 <see cref="UiRowKind.SelectableList"/> 与手搓的 Button/Nav 行）：宿主画成选中高亮。</summary>
    public bool Selected { get; set; }
    /// <summary>空文本时的灰色占位提示（仅 <see cref="UiRowKind.Text"/>，可空）。</summary>
    public string Placeholder { get; set; }
    /// <summary>最大字符数（仅 <see cref="UiRowKind.Text"/>；&lt;= 0 = 不限制）。超出部分输入时就被截断。</summary>
    public int MaxLength { get; set; }
    /// <summary>子菜单目标页 id（仅 <see cref="UiRowKind.Nav"/>）。</summary>
    public string PageId { get; set; }
    /// <summary>选项列表（仅 <see cref="UiRowKind.Choice"/>）。</summary>
    public IReadOnlyList<string> Choices { get; set; }
    /// <summary>
    /// 内嵌列表每行的**右对齐副文本**（与 <see cref="Choices"/> 一一对应；`List`/`SelectableList` 用）。
    /// 信息型列表的标准排版：左边主文本（超长省略）+ 右边版本/状态**对齐成一列**。
    /// </summary>
    public IReadOnlyList<string> ListHints { get; set; }
    /// <summary>数值范围（仅 <see cref="UiRowKind.Slider"/> 与 <see cref="UiRowKind.Progress"/>）。</summary>
    public double Min { get; set; }
    public double Max { get; set; } = 1;
    public double Step { get; set; } = 1;
    /// <summary>动作按钮回调（仅 <see cref="UiRowKind.Button"/>）。</summary>
    public System.Action OnClick { get; set; }
    /// <summary>写回回调（Toggle/Slider/Choice/Text/KeyBind）。返回是否接受。</summary>
    public System.Func<string, bool> Write { get; set; }

    // ---------------- 两栏容器（仅 <see cref="UiRowKind.Columns"/>） ----------------

    /// <summary>左栏宽度（像素；<see cref="UiRowKind.Columns"/> 用）。</summary>
    public float LeftWidth { get; set; } = 440f;

    /// <summary>两栏之间的水平间隙（像素）。</summary>
    public float ColumnGap { get; set; } = 12f;

    /// <summary>左栏子行。</summary>
    public IReadOnlyList<UiRow> LeftRows { get; set; }

    /// <summary>右栏子行。</summary>
    public IReadOnlyList<UiRow> RightRows { get; set; }

    // ---------------- 内嵌列表（仅 <see cref="UiRowKind.List"/> 与 <see cref="UiRowKind.SelectableList"/>） ----------------

    /// <summary>列表高度（像素；0 = 取一个默认值）。</summary>
    public float ListHeight { get; set; }

    /// <summary>列表里的行（<see cref="UiRowKind.List"/> / <see cref="UiRowKind.Foldout"/> 的子行）。</summary>
    public IReadOnlyList<UiRow> ListRows { get; set; }
}
