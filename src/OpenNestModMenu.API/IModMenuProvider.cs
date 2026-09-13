using System;

namespace OpenNestModMenu.API;

/// <summary>
/// 第三方模组接入 OpenNestModMenu 的契约。
///
/// 推荐用法（软依赖，缺 ModMenu 也不影响自身加载）：
/// <code>
/// using OpenNestModMenu.API;
///
/// public sealed class MySettings : ModMenuProviderBase
/// {
///     public override string Id =&gt; "MyMod";
///     public override string DisplayName =&gt; "My Mod";
///     public override void BuildPage(IModMenuPage page)
///     {
///         page.Header("常规");
///         page.Bool("MyMod.Enabled", "启用", true, v =&gt; { /* 写配置 */ });
///     }
/// }
///
/// // 初始化时（不依赖 ModMenu 是否安装）：
/// ModMenuHost.Register(new MySettings());
/// </code>
/// </summary>
public interface IModMenuProvider
{
    /// <summary>稳定唯一键（建议 = 程序集名）。</summary>
    string Id { get; }

    /// <summary>界面显示名。</summary>
    string DisplayName { get; }

    /// <summary>版本（可空）。</summary>
    string Version { get; }

    /// <summary>作者（可空）。</summary>
    string Author { get; }

    /// <summary>
    /// true = 把初始化交给 ModMenu 的调度器（受管顺序：按统一顺序表依次初始化）。
    /// false = 模组自己初始化，ModMenu 只展示设置页（不受管）。
    /// </summary>
    bool AutoInit { get; }

    /// <summary>
    /// true = 支持运行时热切换（模组自己实现可逆的 <see cref="OnEnabled"/>/<see cref="OnDisabled"/>）。
    /// false（默认）= 启停走"改扩展名 + 重启生效"。
    /// </summary>
    bool CanToggleAtRuntime { get; }

    /// <summary>构建设置页（ModMenu 打开菜单时调用；不要在里做重活）。</summary>
    void BuildPage(IModMenuPage page);

    /// <summary>运行时启用（仅 <see cref="CanToggleAtRuntime"/> = true 时被调用）。</summary>
    void OnEnabled();

    /// <summary>运行时禁用（仅 <see cref="CanToggleAtRuntime"/> = true 时被调用）。</summary>
    void OnDisabled();

    /// <summary>恢复默认设置。</summary>
    void ResetToDefaults();
}

/// <summary>
/// 契约的默认实现：除 <see cref="Id"/>/<see cref="DisplayName"/> 外全部给安全默认值，
/// 第三方只需 override 关心的成员。
/// </summary>
public abstract class ModMenuProviderBase : IModMenuProvider
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }

    public virtual string Version => "";
    public virtual string Author => "";
    public virtual bool AutoInit => false;
    public virtual bool CanToggleAtRuntime => false;

    public virtual void BuildPage(IModMenuPage page) { }
    public virtual void OnEnabled() { }
    public virtual void OnDisabled() { }
    public virtual void ResetToDefaults() { }
}
