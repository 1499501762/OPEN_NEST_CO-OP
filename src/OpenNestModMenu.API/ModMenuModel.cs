using System;

namespace OpenNestModMenu.API;

/// <summary>
/// 模组实际运行在哪个加载器宿主里（进程级事实，非"模组格式"）。
/// 桥 <c>BepInEx.MelonLoader.Loader</c> 存在时：宿主 = <see cref="BepInEx"/>，
/// 但其中一部分模组的格式是 <see cref="ModAssemblyFormat.MelonMod"/>（经桥加载）。
/// </summary>
public enum ModLoaderHost
{
    Unknown = 0,
    BepInEx = 1,
    MelonLoader = 2,
}

/// <summary>模组程序集格式（由程序集内是否存在 <c>BaseUnityPlugin</c> / <c>MelonMod</c> 子类判定）。</summary>
public enum ModAssemblyFormat
{
    Unknown = 0,
    BepInExPlugin = 1,
    MelonMod = 2,
}

/// <summary>设置项控件类型（由配置文件注释元数据推断，推断不出用 <see cref="ReadOnly"/>）。</summary>
public enum SettingKind
{
    ReadOnly = 0,
    Bool = 1,
    Number = 2,
    Enum = 3,
    Text = 4,
    Key = 5,
    Action = 6,
}

/// <summary>
/// 一个模组条目的运行时信息（由 ModMenu 采集；第三方只读、不构造）。
/// 字段故意用公开字段：与 docs/MOD_MENU.md 的"双维来源标记"一一对应，便于调试直读。
/// </summary>
public sealed class ModEntryInfo
{
    /// <summary>稳定唯一键（约定 = 程序集名，忽略大小写）。</summary>
    public string Id;

    /// <summary>
    /// 加载器侧的唯一 ID：BepInEx = <c>BepInPlugin</c> 的 **GUID**（如 <c>open.nest.uikit</c>），
    /// MelonLoader = 程序集名。
    ///
    /// ⚠ 2026-09-13（用户：“依赖需要加载顺序吗？有跟随加载顺序吗？”）：
    /// 依赖/不兼容声明用的是**这个**（`BepInDependency` 写 GUID），而 <see cref="Id"/> 是**文件名**（如 `OpenNestUIKit`）
    /// ⇒ 只比 Id 会**静默匹配失败**（实测 `fixups` 恒为 0）。匹配必须 Id / Guid / DisplayName 三个都看。
    /// </summary>
    public string Guid;

    public string DisplayName;
    public string Version;
    public string Author;

    /// <summary>宿主（BepInEx / MelonLoader）。</summary>
    public ModLoaderHost Host;

    /// <summary>程序集格式（BepInExPlugin / MelonMod）。</summary>
    public ModAssemblyFormat Format;

    /// <summary>是否经桥（BepInEx 宿主 + MelonLoader 运行时）加载。</summary>
    public bool ViaBridge;

    /// <summary>模组文件路径（启用 = <c>*.dll</c>；禁用 = <c>*.dll.disabled</c>）。</summary>
    public string Path;

    /// <summary>磁盘启用状态（由扩展名决定）。</summary>
    public bool Enabled = true;

    /// <summary>是否已在内存中加载。</summary>
    public bool Loaded;

    /// <summary>是否有声明式注册（<see cref="IModMenuProvider"/>）。</summary>
    public bool Managed;

    /// <summary>统一展示/受管调度顺序（由 ModMenu 维护）。</summary>
    public int Order;

    /// <summary>模组自声明的优先级（MelonLoader <c>MelonPriorityAttribute</c>；BepInEx 侧无此语义 → 0）。</summary>
    public int Priority;

    /// <summary>依赖的程序集名（MelonLoader <c>MelonAdditionalDependencies</c> 等）。</summary>
    public string[] DependsOn = Array.Empty<string>();

    /// <summary>声明不兼容的程序集名（MelonLoader <c>MelonIncompatibleAssemblies</c>）。</summary>
    public string[] IncompatibleWith = Array.Empty<string>();

    /// <summary>同一程序集在多个已启用路径出现（双初始化风险，见 docs/MOD_MENU.md §3.4）。</summary>
    public bool Duplicate;

    /// <summary>备注（如"依赖/非模组"、加载失败原因）。</summary>
    public string Note;

    /// <summary>
    /// 该模组自己的配置文件路径（**可解析时才非空**）：
    /// BepInEx 插件用 <c>PluginInfo.Metadata.GUID</c> → <c>BepInEx\config\&lt;GUID&gt;.cfg</c>；
    /// MelonLoader 模组按惯例找 <c>UserData\&lt;程序集名&gt;.cfg</c>。空串 = 未找到（设置页会回退到按名字探测）。
    /// </summary>
    public string ConfigFile;

    /// <summary>加载器来源标记文案（UI 直接显示）。</summary>
    public string HostLabel
    {
        get
        {
            string h = Host switch
            {
                ModLoaderHost.BepInEx => "BepInEx",
                ModLoaderHost.MelonLoader => "MelonLoader",
                _ => "未知",
            };
            if (ViaBridge) h += " + MLL";   // MLL = MelonLoader（经 BepInEx.MelonLoader.Loader 桥加载）
            return h;
        }
    }

    /// <summary>格式标记文案（UI 直接显示）。</summary>
    public string FormatLabel => Format switch
    {
        ModAssemblyFormat.BepInExPlugin => "BepInEx 插件",
        ModAssemblyFormat.MelonMod => "MelonLoader 模组",
        _ => "未知",
    };
}

/// <summary>
/// 一条设置项（来自配置文件或第三方声明）。
/// 值统一用字符串承载 + 类型解析方法：避免在契约程序集里引入序列化/类型转换依赖。
/// </summary>
public sealed class SettingItem
{
    /// <summary>唯一键（约定 <c>Section.Key</c>，如 <c>Sync.CatSync</c>）。</summary>
    public string Key;

    /// <summary>归属模组 Id（空 = ModMenu 自身）。</summary>
    public string Owner;

    /// <summary>配置节名（INI section；可为空）。</summary>
    public string Section;

    public string Label;
    public string Description;

    public SettingKind Kind = SettingKind.ReadOnly;

    /// <summary>是否只读（无法安全写回的格式一律只读，见 docs/MOD_MENU.md §6.2）。</summary>
    public bool ReadOnly;

    /// <summary>当前值（原样文本）。</summary>
    public string StringValue;

    /// <summary>默认值（原样文本；未知为空）。</summary>
    public string DefaultValue;

    /// <summary>枚举可选值（<see cref="SettingKind.Enum"/> 时有效）。</summary>
    public string[] Choices = Array.Empty<string>();

    /// <summary>数值范围（<see cref="SettingKind.Number"/> 时有效；0/0 表示未声明）。</summary>
    public double Min;
    public double Max;
    public double Step;

    /// <summary>来源配置文件路径（可空）。</summary>
    public string SourceFile;

    /// <summary>键所在行号（1 起；0 = 未知）。</summary>
    public int SourceLine;

    /// <summary>解析布尔值（true/false、1/0、on/off、yes/no）。</summary>
    public bool TryGetBool(out bool value)
    {
        value = false;
        if (string.IsNullOrEmpty(StringValue)) return false;
        switch (StringValue.Trim().ToLowerInvariant())
        {
            case "true": case "1": case "on": case "yes": value = true; return true;
            case "false": case "0": case "off": case "no": value = false; return true;
            default: return false;
        }
    }

    /// <summary>解析数值（不变文化）。</summary>
    public bool TryGetNumber(out double value)
        => double.TryParse(StringValue, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out value);
}
