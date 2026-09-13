namespace OpenNestUIKit.Test;

/// <summary>测试模组身份常量（双端共用）。</summary>
public static class TestInfo
{
    public const string Guid = "open.nest.uikit.test";
    public const string Name = "Open Nest UIKit Test";
    public const string Version = "0.0.1-Alpha-4";
    public const string Author = "OpenNestUIKit";

    public static string BuildPlatform =>
#if MELONLOADER
        "MelonLoader";
#else
        "BepInEx";
#endif
}
