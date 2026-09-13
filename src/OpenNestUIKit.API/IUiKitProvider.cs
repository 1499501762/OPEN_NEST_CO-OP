namespace OpenNestUIKit.API;

/// <summary>
/// 第三方模组接入 OpenNestUIKit 的契约。推荐用法（**软依赖**，缺 UIKit 也不影响自身加载）：
/// <code>
/// using OpenNestUIKit.API;
///
/// public sealed class MyMenu : UiKitProviderBase
/// {
///     public override string Id =&gt; "MyMod";
///     public override string DisplayName =&gt; "我的模组";
///
///     public override void BuildMenu(IUiMenuTree menu)
///     {
///         menu.Root.Header("我的模组");
///         menu.Root.Nav("常规设置", "general");
///         menu.Root.Nav("快捷键", "keys");
///
///         menu.Page("general", "常规设置")
///             .Toggle("MyMod.Enabled", "启用", true, v =&gt; { /* 写配置 */ })
///             .Slider("MyMod.Volume", "音量", 0.5, 0, 1, 0.05, v =&gt; { });
///
///         menu.Page("keys", "快捷键").KeyBind("MyMod.Key", "打开菜单", "F6", v =&gt; { });
///     }
/// }
///
/// // 初始化时（不依赖 UIKit 是否已加载）：
/// UiKitHost.Register(new MyMenu());
/// </code>
/// </summary>
public interface IUiKitProvider
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
    /// 构建菜单树（宿主打开菜单时调用；**不要在这里做重活**，也不要假设它在主线程之外被调用）。
    /// </summary>
    void BuildMenu(IUiMenuTree menu);
}

/// <summary>
/// 可选接口：控制"宿主替第三方往**游戏原生 ESC 菜单**里注入的那一行"。
///
/// 设计意图（2026-09-13）：**注入由 UIKit 一家负责**。第三方只声明菜单，不碰原生 UI 内部
/// （`NativeMenuBridge` / 原生按钮重排 / 原生面板切页都封在宿主里）——于是：
/// - 多个模组同时注入时不会互相抢格子（宿主统一做 slot 让位）；
/// - 原生菜单的样子/摆法只有一处实现，升级不用各模组跟改；
/// - 没实现本接口的 provider **默认也会有一行**（显示名 = <see cref="IUiKitProvider.DisplayName"/>）。
///
/// 不想要那一行（例如自己的菜单只从热键进）→ 实现本接口并把 <see cref="ShowInNativeMenu"/> 返回 false。
/// </summary>
public interface IUiKitNativeEntry
{
    /// <summary>是否在原生 ESC 菜单里占一行（默认 true = 由 UIKit 注入）。</summary>
    bool ShowInNativeMenu { get; }

    /// <summary>原生 ESC 菜单里的排序（小的在前；默认 100 + 注册顺序）。</summary>
    int NativeOrder { get; }

    /// <summary>原生 ESC 菜单里的文案（空/未实现 = 用 <see cref="IUiKitProvider.DisplayName"/>）。</summary>
    string NativeTitle { get; }

    /// <summary>原生 ESC 菜单里的英文文案（空 = 用 <see cref="NativeTitle"/>）。</summary>
    string NativeTitleEn { get; }
}

/// <summary>契约默认实现：除 <see cref="Id"/>/<see cref="DisplayName"/> 外全部给安全默认值。</summary>
public abstract class UiKitProviderBase : IUiKitProvider
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }

    public virtual string Version => "";
    public virtual string Author => "";

    public virtual void BuildMenu(IUiMenuTree menu) { }
}
