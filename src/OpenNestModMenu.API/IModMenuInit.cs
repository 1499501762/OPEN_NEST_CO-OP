namespace OpenNestModMenu.API;

/// <summary>
/// 可选契约：**受管初始化入口**（T8）。
///
/// 当 <see cref="IModMenuProvider.AutoInit"/> = <c>true</c> 时，ModMenu 的顺序调度器会
/// 按"统一顺序表"依次调用本方法 —— 也就是把"我什么时候初始化"这件事交给菜单，
/// 这样多个模组之间的相对顺序（依赖方先于被依赖方）才有保证。
///
/// 为什么不把 <c>OnInitialize()</c> 直接写进 <see cref="IModMenuProvider"/>：
/// 那会让**所有已实现的第三方代码**在升级契约后编译不过；单独一个接口 = 纯增量、可选实现。
///
/// 约定：
/// - 只调用一次（同一个提供者实例；停用后重新启用会再调一次）；
/// - **不要在构造函数里做重活**：构造函数在扫描阶段就会被执行，那时顺序还没定；
/// - 抛异常只影响该模组自己（会记到该条目的错误里，界面上能看到），不会中断其它模组的初始化。
/// </summary>
public interface IModMenuInit
{
    /// <summary>受管初始化（按统一顺序调用一次）。</summary>
    void OnInitialize();
}
