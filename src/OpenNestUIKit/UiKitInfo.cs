namespace OpenNestUIKit;

/// <summary>模组身份常量（BepInEx / MelonLoader 双端共用）。</summary>
public static class UiKitInfo
{
    public const string Guid = "open.nest.uikit";
    public const string Name = "Open Nest UIKit";
    public const string Version = "0.0.1-Alpha-4";
    public const string Author = "OpenNestUIKit";

    /// <summary>当前编译目标平台（编译期常量；运行时宿主可能不同——经桥加载时由 Paths 探测）。</summary>
    public static string BuildPlatform =>
#if MELONLOADER
        "MelonLoader";
#else
        "BepInEx";
#endif
}
