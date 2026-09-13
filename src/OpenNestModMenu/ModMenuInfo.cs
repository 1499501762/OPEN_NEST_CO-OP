namespace OpenNestModMenu;

/// <summary>模组身份常量（BepInEx / MelonLoader 双端共用）。</summary>
public static class ModMenuInfo
{
    public const string Guid = "open.nest.modmenu";
    public const string Name = "Open Nest Mod Menu";
    /// <summary>模组（产品）版本：0.0.1 = 首个公开版本；0.0.2 = 修复原生 MLL 端配置定位 + 匹配结果落盘；
    /// 0.0.3 = 扁平工业风界面重做（控件/表格/分隔线/按钮描边）+ 模组依赖声明与加载顺序 + 调试模式开关（OpenNestModMenu.cfg）+ 界面文案全量本地化。
    /// ⚠️ 与“契约 API 版本”（<c>ModMenuHost.ApiVersion</c>）是两个轴：前者是产品版本号，后者是第三方要面对的接口版本。</summary>
    public const string Version = "0.0.3";
    public const string Author = "OpenNestModMenu";

    /// <summary>当前编译目标平台（编译期常量；运行时宿主可能不同——经桥加载时由 LoaderDetector 探测）。</summary>
    public static string BuildPlatform =>
#if MELONLOADER
        "MelonLoader";
#else
        "BepInEx";
#endif
}
