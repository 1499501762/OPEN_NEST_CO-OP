using System;

namespace OpenNestUIKit;

/// <summary>
/// **运行在环境里的标记**：只要 OpenNestUIKit 这个模组被加载，本类型就存在。
///
/// 用途：其它模组（Coop / ModMenu / 第三方）需要判断"环境里到底有没有 UIKit"，
/// 而 **不想** 硬依赖任何 dll 时，用反射探测本类型即可（纯字符串，不引任何程序集）：
/// <code>
/// var t = Type.GetType("OpenNestUIKit.UiKitMarker, OpenNestUIKit");
/// bool hasUIKit = t != null;
/// </code>
/// 完整的"能不能真接上"判断以契约注册表为准：<c>OpenNestUIKit.API.UiKitHost.IsHostAvailable</c>
/// （宿主启动时把它置 true；在此之前注册的 provider 会被保留，宿主上线后再收编）。
///
/// ⚠️ 只在**宿主程序集**里放这个标记，契约程序集（<c>OpenNestUIKit.API</c>）里不放 ——
/// 因为契约 dll 是"可以被第三方一起引用"的东西，而"环境里有没有 UIKit"必须由宿主程序集回答。
/// </summary>
public static class UiKitMarker
{
    /// <summary>稳定标识（程序集名 / 契约里的 Id 一致）。</summary>
    public const string Id = "OpenNestUIKit";

    /// <summary>契约程序集名（第三方注册 provider 用的那个 dll）。</summary>
    public const string ContractAssembly = "OpenNestUIKit.API";

    /// <summary>本类型的完整名（反射探测用；与 <see cref="ContractAssembly"/> 配合）。
    /// ⚠️ **双端程序集名不同**：BepInEx 端 = `OpenNestUIKit`，MelonLoader 端 = `OpenNestUIKit.MelonMod`
    /// （MLL 壳工程只能把主体源码编进自己的程序集）。所以探测要**两个都试**：
    /// <code>
    /// bool hasUIKit = Type.GetType("OpenNestUIKit.UiKitMarker, OpenNestUIKit") != null
    ///              || Type.GetType("OpenNestUIKit.UiKitMarker, OpenNestUIKit.MelonMod") != null;
    /// </code>
    /// 或者更省事：扫 `AppDomain.CurrentDomain.GetAssemblies()` 的程序集名（见本库的 Coop/ModMenu 集成）。</summary>
    public const string TypeName = "OpenNestUIKit.UiKitMarker, OpenNestUIKit";

    /// <summary>环境里存在（能得到本类型 = 已经加载）。</summary>
    public static bool IsRunning => true;

    /// <summary>宿主版本（写进日志/原生菜单提示用）。</summary>
    public static string Version => UiKitInfo.Version;

    /// <summary>宿主构建平台（BepInEx / MelonLoader）。</summary>
    public static string Platform => UiKitInfo.BuildPlatform;

    /// <summary>一行式环境描述。</summary>
    public static string Describe() => $"{Id} v{Version} ({Platform})";

    /// <summary>
    /// 契约宿主是否已经就绪（= 可以安全地注册/调用 <c>UiKitHost</c> 与 <c>NativeMenuBridge</c>）。
    /// 未就绪不代表"没装"（可能只是加载顺序），第三方注册不受影响。
    /// </summary>
    public static bool IsHostReady
    {
        get
        {
            try { return API.UiKitHost.IsHostAvailable; } catch { return false; }
        }
    }
}
