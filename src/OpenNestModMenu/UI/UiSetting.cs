using System;
using OpenNestModMenu.API;

namespace OpenNestModMenu.UI;

/// <summary>
/// 设置页里的**一行**（内部模型）：把"来自配置文件的 <see cref="SettingItem"/>"与
/// "第三方声明式页面（<see cref="IModMenuPage"/>）"统一成同一种可渲染 + 可写回的东西，
/// 于是右键的控件族只需实现一次（T6）。
///
/// 写回用委托：配置文件路径 → <c>ModConfigStore.SetValue</c>；第三方页 → 它自己的 <c>onChanged</c>。
/// </summary>
internal sealed class UiSetting
{
    public string Label = "";
    public string Description = "";

    /// <summary>来源描述（配置文件路径 / "provider"），仅用于 UI 提示与日志。</summary>
    public string Source = "";

    /// <summary>配置节名 / 键名（来自配置文件的行才有；供证据日志里"改的是哪一行"）</summary>
    public string Section = "";
    public string Key = "";

    public SettingKind Kind = SettingKind.ReadOnly;
    public string Value = "";
    public string[] Choices = Array.Empty<string>();
    public double Min, Max, Step = 1;
    public double? RangeMin, RangeMax;

    /// <summary>真只读：配置注释里声明了 ReadOnly（或第三方声明式页面里的纯展示行）。
    /// ⚠ 2026-09-13：文本/快捷键项**不再**算只读——UIKit 那边用输入框渲染它们（用户要求“空值也要给空文本框”）。</summary>
    public bool ReadOnly;

    /// <summary>分组标题行（无控件）。</summary>
    public bool Header;

    /// <summary>分隔线行。</summary>
    public bool Separator;

    /// <summary>重新读取当前值（可空；用于写回后刷新显示）。</summary>
    public Func<string> Read;

    /// <summary>写回（返回是否成功；null = 无写回能力 → 只读）。</summary>
    public Func<string, bool> Write;

    /// <summary>写回结果说明（成功/失败原因，供底栏显示）。</summary>
    public string LastMessage = "";

    /// <summary>自带面板的编辑器能力（只有布尔/数值/枚举三档：加减 + 点击翻转）。
    /// 文本/快捷键不在此列，它们在 UIKit 那边走输入框（<c>UiSetting.Write</c>）。</summary>
    public bool CanEdit => !ReadOnly && Write != null && (Kind == SettingKind.Bool || Kind == SettingKind.Number || Kind == SettingKind.Enum);

    /// <summary>由配置文件扫描出的设置项构造（写回走 <c>ModConfigStore</c>）。</summary>
    public static UiSetting FromConfig(SettingItem it)
    {
        var s = new UiSetting
        {
            Label = string.IsNullOrEmpty(it.Label) ? it.Key : it.Label,
            Description = it.Description ?? "",
            Source = it.SourceFile ?? "",
            Section = it.Section ?? "",
            Key = it.Key ?? "",
            Kind = it.Kind,
            Value = it.StringValue ?? "",
            Choices = it.Choices ?? Array.Empty<string>(),
            Min = it.Min,
            Max = it.Max,
            Step = it.Step > 0 ? it.Step : 1,
            ReadOnly = it.ReadOnly,
        };
        // ⚠️ 模型里 Min/Max = 0/0 表示“未声明范围” → 只有 Max > Min 才当范围用，
        //    否则会把无范围声明的数值（含负数）夹到 0 以上（已踩）。
        bool hasRange = it.Max > it.Min;
        s.RangeMin = hasRange ? it.Min : (double?)null;
        s.RangeMax = hasRange ? it.Max : (double?)null;
        if (!s.ReadOnly)
        {
            s.Write = v =>
            {
                bool ok = Config.ModConfigStore.SetValue(it, v, out string msg);
                s.LastMessage = ok ? "" : msg;
                return ok;
            };
            s.Read = () => it.StringValue ?? "";
        }
        return s;
    }
}
