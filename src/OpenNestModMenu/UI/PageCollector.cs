using System;
using System.Collections.Generic;
using OpenNestModMenu.API;

namespace OpenNestModMenu.UI;

/// <summary>
/// 把第三方声明式页面（<see cref="IModMenuPage"/>）**收集**成 <see cref="UiSetting"/> 行（T6）。
///
/// 为什么用收集而不是直接渲染：控件族（<see cref="ModMenuSettingsView"/>）只认一种行模型，
/// 于是"配置文件里的设置"和"第三方声明的页面"走同一条渲染/交互路径；
/// 第三方也不需要引用 Unity（契约程序集无 Unity 依赖，docs/API.md）。
///
/// 写回：<c>onChanged</c> 直接回调给第三方（它自己负责写配置）——契约里就是这么约定的。
/// </summary>
internal sealed class PageCollector : IModMenuPage
{
    public readonly List<UiSetting> Items = new();

    public static List<UiSetting> Collect(IModMenuProvider provider)
    {
        var c = new PageCollector();
        try { provider?.BuildPage(c); }
        catch (Exception ex)
        {
            c.Items.Add(new UiSetting { Label = "BuildPage failed: " + ex.Message, ReadOnly = true });
        }
        return c.Items;
    }

    private void Add(UiSetting s) => Items.Add(s);

    public void Header(string text) => Add(new UiSetting { Label = text ?? "", Header = true });

    public void Label(string text) => Add(new UiSetting { Label = text ?? "", ReadOnly = true });

    public void Separator() => Add(new UiSetting { Separator = true });

    public void Bool(string key, string label, bool value, Action<bool> onChanged)
        => Add(new UiSetting
        {
            Label = label ?? key ?? "",
            Source = "provider",
            Kind = SettingKind.Bool,
            Value = value ? "true" : "false",
            Write = v =>
            {
                string t = (v ?? "").Trim().ToLowerInvariant();
                bool b = t == "true" || t == "1" || t == "on" || t == "yes";
                onChanged?.Invoke(b);
                return true;
            },
        });

    public void Number(string key, string label, double value, double min, double max, double step, Action<double> onChanged)
    {
        var s = new UiSetting
        {
            Label = label ?? key ?? "",
            Source = "provider",
            Kind = SettingKind.Number,
            Value = Num(value),
            Min = min,
            Max = max,
            Step = step > 0 ? step : 1,
            RangeMin = min,
            RangeMax = max > min ? max : (double?)null,
            Write = v =>
            {
                if (!double.TryParse(v, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d)) return false;
                onChanged?.Invoke(d);
                return true;
            },
        };
        Add(s);
    }

    public void Choice(string key, string label, IReadOnlyList<string> choices, int selected, Action<int> onChanged)
    {
        var arr = new List<string>();
        if (choices != null) for (int i = 0; i < choices.Count; i++) arr.Add(choices[i]);
        var s = new UiSetting
        {
            Label = label ?? key ?? "",
            Source = "provider",
            Kind = SettingKind.Enum,
            Choices = arr.ToArray(),
            Value = arr.Count > 0 ? arr[Math.Max(0, Math.Min(arr.Count - 1, selected))] : "",
            Write = v =>
            {
                int idx = arr.IndexOf(v ?? "");
                if (idx < 0) return false;
                onChanged?.Invoke(idx);
                return true;
            },
        };
        Add(s);
    }

    // ⚠️ 2026-09-13（用户：“只有配置项名没有内容的应该显示空文本框而不是直接暗掉”）：
    //    这两个方法以前**忽略**了契约里传进来的 `onChanged`、一律标 `ReadOnly = true`
    //    → 第三方声明“可以编辑的文本框”却只得到一行灰字（同一个病根）。
    //    现在：给了回调就是可编辑输入框（UIKit 侧画 `page.Text`），没给才是只读展示。
    //    自带回退面板没有输入框控件，仍按“文本项只读”画（`ModMenuSettingsView.RowReadOnly`）。
    public void Text(string key, string label, string value, Action<string> onChanged)
        => Add(new UiSetting
        {
            Label = label ?? key ?? "",
            Source = "provider",
            Kind = SettingKind.Text,
            Value = value ?? "",
            ReadOnly = onChanged == null,
            Write = onChanged == null ? null : v => { try { onChanged(v ?? ""); return true; } catch { return false; } },
        });

    public void Action(string label, string buttonText, Action onClick)
        => Add(new UiSetting { Label = (label ?? "") + " · " + (buttonText ?? ""), Source = "provider", Kind = SettingKind.Action, ReadOnly = true });

    public void KeyBind(string key, string label, string current, Action<string> onChanged)
        => Add(new UiSetting
        {
            Label = label ?? key ?? "",
            Source = "provider",
            Kind = SettingKind.Key,
            Value = current ?? "",
            ReadOnly = onChanged == null,
            Write = onChanged == null ? null : v => { try { onChanged(v ?? ""); return true; } catch { return false; } },
        });

    private static string Num(double d)
        => d.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}
