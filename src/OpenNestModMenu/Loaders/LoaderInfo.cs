using System.Collections.Generic;
using OpenNestModMenu.API;

namespace OpenNestModMenu.Loaders;

/// <summary>
/// 加载器探测结果（进程级事实）。见 docs/MOD_MENU.md §3.1/§3.2。
///
/// 注意"双维"：<see cref="Host"/> 是**谁在跑这个进程**，而每个模组的格式是另一回事
/// （经桥加载时宿主是 BepInEx，但模组格式仍是 MelonLoader 模组）。
/// </summary>
public sealed class LoaderInfo
{
    /// <summary>宿主：谁在驱动这个进程。</summary>
    public ModLoaderHost Host = ModLoaderHost.Unknown;

    /// <summary>检测到 BepInEx 运行时类型。</summary>
    public bool BepInExRuntime;

    /// <summary>检测到 MelonLoader 运行时类型。</summary>
    public bool MelonLoaderRuntime;

    /// <summary>宿主是 BepInEx 且同时存在 MelonLoader 运行时 → 经桥 `BepInEx.MelonLoader.Loader` 加载。</summary>
    public bool ViaBridge;

    /// <summary>桥被检出的依据：`type`（发现 `MelonLoader.Hosting.BepInExHost`）/ `dir`（发现桥插件目录）/ 空。</summary>
    public string BridgeEvidence = "";

    /// <summary>桥插件版本（读不到为空串 —— 不猜）。</summary>
    public string BridgeVersion = "";

    /// <summary>BepInEx 版本（读不到为空串）。</summary>
    public string BepInExVersion = "";

    /// <summary>MelonLoader 版本（读不到为空串）。</summary>
    public string MelonLoaderVersion = "";

    /// <summary>两侧 Harmony 程序集都存在（patch 视图互相隔离，见 §3.3）。</summary>
    public bool DualHarmony;

    /// <summary>补充说明（供调试面板显示）。</summary>
    public readonly List<string> Notes = new();

    /// <summary>实际使用的 interop 来源。桥 = 别名注入到宿主 interop（§3.2 已核实）。</summary>
    public string InteropSource =>
        ViaBridge ? "宿主(BepInEx，经 Il2Cpp* 别名)"
        : Host == ModLoaderHost.MelonLoader ? "MelonLoader"
        : Host == ModLoaderHost.BepInEx ? "BepInEx"
        : "未知";

    /// <summary>宿主显示标记。</summary>
    public string HostLabel => Host switch
    {
        ModLoaderHost.BepInEx => ViaBridge ? "BepInEx + MLL" : "BepInEx",
        ModLoaderHost.MelonLoader => "MelonLoader",
        _ => "未知",
    };

    /// <summary>一行摘要（写主日志）。</summary>
    public string Describe()
        => $"host={HostLabel} viaBridge={ViaBridge}{(BridgeEvidence.Length > 0 ? "(" + BridgeEvidence + ")" : "")}"
         + $" bridgeVer={(BridgeVersion.Length > 0 ? BridgeVersion : "?")}"
         + $" bepinEx={(BepInExVersion.Length > 0 ? BepInExVersion : "?")}"
         + $" melonLoader={(MelonLoaderVersion.Length > 0 ? MelonLoaderVersion : "?")}"
         + $" interop={InteropSource}"
         + (DualHarmony ? " dualHarmony=yes" : "");
}
