using System;
using System.Diagnostics;
using System.IO;

namespace OpenNestModMenu.Core;

/// <summary>
/// 路径解析：一次解析出双加载器环境的全部惯例目录（见 docs/MOD_MENU.md §3.2 的"目录真值"表）。
///
/// 覆盖两种布局：
/// - 原生 BepInEx：<c>&lt;Game&gt;\BepInEx\{plugins,config,patchers,interop}</c>
/// - 原生 MelonLoader：<c>&lt;Game&gt;\{Mods,Plugins,UserLibs,UserData}</c> + <c>&lt;Game&gt;\MelonLoader\</c>
/// - 桥（BepInEx + BepInEx.MelonLoader.Loader）：ML 侧被移到 <c>&lt;Game&gt;\MLLoader\{Mods,Plugins,UserLibs,UserData,MelonLoader}</c>
/// </summary>
public static class ModMenuPaths
{
    /// <summary>游戏根目录。</summary>
    public static string GameDir { get; private set; } = "";

    // ---- BepInEx 侧 ----
    public static string BepInExRoot { get; private set; } = "";
    public static string BepInExPluginDir { get; private set; } = "";
    public static string BepInExConfigDir { get; private set; } = "";
    public static string BepInExInteropDir { get; private set; } = "";
    public static string BridgePluginDir { get; private set; } = "";
    public static string BridgePatcherDir { get; private set; } = "";

    // ---- MelonLoader 侧 ----
    /// <summary>ML 侧根：桥环境 = <c>&lt;Game&gt;\MLLoader</c>；原生 = <c>&lt;Game&gt;</c>。</summary>
    public static string MelonRoot { get; private set; } = "";
    public static string MelonModsDir { get; private set; } = "";
    public static string MelonPluginsDir { get; private set; } = "";
    public static string MelonUserLibsDir { get; private set; } = "";
    public static string MelonUserDataDir { get; private set; } = "";

    /// <summary>本模组独立日志目录（<c>OpenNestModMenuLogs</c>，避开 OpenNestCoop 的 <c>OpenNestLogs</c>）。</summary>
    public static string LogDir { get; private set; } = "";

    /// <summary>本模组配置文件路径（按"本模组实际被谁加载"决定：BepInEx\config 或 UserData）。</summary>
    public static string ConfigFile { get; private set; } = "";
    /// <summary>本模组语言键文件路径（与配置文件同目录；读写见 Loc/LocFile.cs）。</summary>
    public static string LangFile { get; private set; } = "";
    /// <summary>探测到的本模组部署形态。</summary>
    public static ModMenuDeployShape Shape { get; private set; } = ModMenuDeployShape.Unknown;

    public static bool HasBepInEx => BepInExRoot.Length > 0 && Directory.Exists(BepInExRoot);
    public static bool HasMelonMods => MelonModsDir.Length > 0 && Directory.Exists(MelonModsDir);

    /// <summary>解析全部路径（Startup 时调用一次；重复调用安全）。</summary>
    public static void Resolve()
    {
        GameDir = ResolveGameDir();

        // BepInEx
        BepInExRoot = Path.Combine(GameDir, "BepInEx");
        BepInExPluginDir = Path.Combine(BepInExRoot, "plugins");
        BepInExConfigDir = Path.Combine(BepInExRoot, "config");
        BepInExInteropDir = Path.Combine(BepInExRoot, "interop");
        BridgePluginDir = Path.Combine(BepInExPluginDir, "BepInEx.MelonLoader.Loader");
        BridgePatcherDir = Path.Combine(BepInExRoot, "patchers");

        // MelonLoader：桥环境根在 MLLoader\，原生环境根在游戏根
        MelonRoot = Directory.Exists(Path.Combine(GameDir, "MLLoader"))
            ? Path.Combine(GameDir, "MLLoader")
            : GameDir;
        MelonModsDir = Path.Combine(MelonRoot, "Mods");
        MelonPluginsDir = Path.Combine(MelonRoot, "Plugins");
        MelonUserLibsDir = Path.Combine(MelonRoot, "UserLibs");
        MelonUserDataDir = Path.Combine(MelonRoot, "UserData");

        LogDir = Path.Combine(GameDir, "OpenNestModMenuLogs");

        Shape = DetectShape();
        var configDir = Shape == ModMenuDeployShape.BepInExPlugin ? BepInExConfigDir : MelonUserDataDir;
        ConfigFile = Path.Combine(configDir, "OpenNestModMenu.cfg");
        LangFile = Path.Combine(configDir, Loc.LocFile.FileName);
    }

    /// <summary>游戏根目录（优先进程可执行文件所在目录，失败回退当前工作目录）。</summary>
    private static string ResolveGameDir()
    {
        try
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
                string dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            }
        }
        catch { /* 取不到就用工作目录 */ }
        return Environment.CurrentDirectory;
    }

    /// <summary>
    /// 探测本模组自己是怎么被部署/加载的（**证据优先**：按磁盘上实际存在的文件判定，不靠编译常量）。
    /// 编译常量只描述"编译目标"，而经桥加载时 MLL 版 dll 跑在 BepInEx 宿主里（docs/MOD_MENU.md §3.1）。
    /// </summary>
    private static ModMenuDeployShape DetectShape()
    {
        try
        {
            if (File.Exists(Path.Combine(BepInExPluginDir, "OpenNestModMenu.dll")))
                return ModMenuDeployShape.BepInExPlugin;
            if (File.Exists(Path.Combine(MelonModsDir, "OpenNestModMenu.MelonMod.dll")))
                return ModMenuDeployShape.MelonMod;
        }
        catch { }
        return ModMenuDeployShape.Unknown;
    }
}

/// <summary>本模组自身的部署形态（不是"宿主"——宿主由 LoaderDetector 在 T3 探测）。</summary>
public enum ModMenuDeployShape
{
    Unknown = 0,
    BepInExPlugin = 1,
    MelonMod = 2,
}
