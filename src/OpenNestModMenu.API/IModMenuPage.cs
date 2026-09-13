using System;
using System.Collections.Generic;

namespace OpenNestModMenu.API;

/// <summary>
/// 第三方模组的设置页构建接口（由 ModMenu 传入实现，模组只调用——**不依赖 Unity**，
/// 因此同一份模组代码在 BepInEx / MelonLoader 双端都能编译）。
///
/// 用法：在 <see cref="IModMenuProvider.BuildPage"/> 里用这些方法描述界面，
/// ModMenu 负责把它们渲染成覆盖 UGUI（v1）。
/// </summary>
public interface IModMenuPage
{
    /// <summary>分组标题。</summary>
    void Header(string text);

    /// <summary>普通说明文字。</summary>
    void Label(string text);

    /// <summary>分隔线。</summary>
    void Separator();

    /// <summary>布尔开关。</summary>
    void Bool(string key, string label, bool value, Action<bool> onChanged);

    /// <summary>数值（滑条或 ± 步进）。<paramref name="step"/> &lt;= 0 时由 ModMenu 取合理默认。</summary>
    void Number(string key, string label, double value, double min, double max, double step, Action<double> onChanged);

    /// <summary>枚举（按钮循环 / 下拉）。</summary>
    void Choice(string key, string label, IReadOnlyList<string> choices, int selected, Action<int> onChanged);

    /// <summary>文本输入（v1 可能降级为只读展示，见 docs/MOD_MENU.md §1.1）。</summary>
    void Text(string key, string label, string value, Action<string> onChanged);

    /// <summary>动作按钮。</summary>
    void Action(string label, string buttonText, Action onClick);

    /// <summary>快捷键绑定。</summary>
    void KeyBind(string key, string label, string current, Action<string> onChanged);
}
