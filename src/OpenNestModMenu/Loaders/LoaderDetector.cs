using System;
using System.Collections.Generic;
using System.IO;
using OpenNestModMenu.API;
using OpenNestModMenu.Core;

namespace OpenNestModMenu.Loaders;

/// <summary>
/// 加载器探测（宿主 / 桥 / 版本 / interop 来源）。全部反射，零编译期依赖（见 docs/MOD_MENU.md §3.1、§3.2）。
/// </summary>
public static class LoaderDetector
{
    private static LoaderInfo _current;

    /// <summary>最近一次探测结果（首次访问时自动探测）。</summary>
    public static LoaderInfo Current => _current ??= Detect();

    /// <summary>探测一次（<paramref name="force"/> = true 时强制重探）。</summary>
    public static LoaderInfo Detect(bool force = false)
    {
        if (_current != null && !force) return _current;

        var info = new LoaderInfo();
        try
        {
            // ---- 宿主证据（类型存在性）----
            var bepChain = LoaderReflect.FindType("BepInEx.Bootstrap.Chainloader")
                        ?? LoaderReflect.FindType("BepInEx.Unity.IL2CPP.IL2CPPChainloader");
            var melonBase = LoaderReflect.FindType("MelonLoader.MelonBase")
                        ?? LoaderReflect.FindType("MelonLoader.MelonEnvironment");

            info.BepInExRuntime = bepChain != null;
            info.MelonLoaderRuntime = melonBase != null;

            // ---- 桥证据：① `MelonLoader.Hosting.BepInExHost`（桥把 MelonLoader 宿主化）；② 桥插件目录 ----
            bool bridgeType = LoaderReflect.FindType("MelonLoader.Hosting.BepInExHost") != null;
            bool bridgeDir = false;
            try { bridgeDir = Directory.Exists(ModMenuPaths.BridgePluginDir); } catch { }

            if (info.BepInExRuntime && info.MelonLoaderRuntime && (bridgeType || bridgeDir))
            {
                info.ViaBridge = true;
                info.BridgeEvidence = bridgeType ? "type" : "dir";
            }

            // ---- 宿主归属 ----
            if (info.BepInExRuntime) info.Host = ModLoaderHost.BepInEx;          // 桥场景下宿主仍是 BepInEx（它驱动进程）
            else if (info.MelonLoaderRuntime) info.Host = ModLoaderHost.MelonLoader;

            // ---- 版本 ----
            if (bepChain != null) info.BepInExVersion = LoaderReflect.AssemblyVersion(bepChain.Assembly);
            if (melonBase != null) info.MelonLoaderVersion = LoaderReflect.AssemblyVersion(melonBase.Assembly);
            info.BridgeVersion = FindBridgeVersion();

            // ---- 双 Harmony（BepInEx 与 MelonLoader 各自带一套，patch 视图互相隔离）----
            // ⚠️ 只看“两个类型名能不能解析到”会误报：MelonLoader 自带的 0Harmony 也把 ns 叫 HarmonyLib
            //    → 原生 MLL 端两个名字指向**同一个程序集**，实测就被误判成 dual（已踩）
            //    所以额外要求两者来自两个**不同**程序集。
            var harmonyA = LoaderReflect.FindType("HarmonyLib.Harmony");
            var harmonyB = LoaderReflect.FindType("MelonLoader.HarmonyLib.Harmony");
            info.DualHarmony = harmonyA != null && harmonyB != null && !SameAssembly(harmonyA, harmonyB);

            // ---- 补充说明 ----
            if (info.ViaBridge)
                info.Notes.Add("MelonLoader mods run verbatim on the host's interop via Il2Cpp* alias types (§3.2)");
            if (info.ViaBridge && info.BridgeVersion.Length == 0)
                info.Notes.Add("bridge version not resolvable from plugin metadata");
        }
        catch (Exception ex)
        {
            info.Notes.Add("detect failed: " + ex.Message);
        }

        _current = info;
        return info;
    }

    /// <summary>两个类型是否来自同一个程序集（位置优先，退到程序集名；都读不到 = 当作不同，宁可多提示）。</summary>
    private static bool SameAssembly(System.Type a, System.Type b)
    {
        try { if (a == b) return true; } catch { }
        try
        {
            var pa = a.Assembly.Location;
            var pb = b.Assembly.Location;
            if (!string.IsNullOrEmpty(pa) && !string.IsNullOrEmpty(pb))
                return string.Equals(pa, pb, System.StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        try
        {
            return string.Equals(a.Assembly.GetName().Name, b.Assembly.GetName().Name, System.StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>找桥插件版本（优先插件元数据里的 GUID/名称匹配；读不到返回空串——不猜）。</summary>
    private static string FindBridgeVersion()
    {
        try
        {
            var plugins = LoaderReflect.ReadBepInExPlugins();
            for (int i = 0; i < plugins.Count; i++)
            {
                var p = plugins[i];
                string hay = (p.Guid + " " + p.Name).ToLowerInvariant();
                if (hay.Contains("melonloader") || hay.Contains("melon loader"))
                    return p.Version ?? "";
            }
        }
        catch { }
        return "";
    }

    /// <summary>把探测结果写进日志。摘要用 key `loader`（**不命中任何路由前缀 → 进主日志**，方便一眼确认宿主/桥），备注走 loader.log。</summary>
    public static void Log(LoaderInfo info)
    {
        if (info == null) return;
        CoopLog.Info("loader", () => "loader: " + info.Describe());
        for (int i = 0; i < info.Notes.Count; i++)
        {
            string note = info.Notes[i];
            CoopLog.Info("loader.note", () => "note: " + note);
        }
    }
}
