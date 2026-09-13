using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using OpenNestModMenu.API;

namespace OpenNestModMenu.Loaders;

/// <summary>
/// 只读元数据探测：**不加载程序集**，判断磁盘上的 dll 到底是什么格式（docs/MOD_MENU.md §3.1）。
///
/// 为什么需要它：盘上的 dll 既可能是模组、也可能是依赖库。之前"按目录推断格式"是错的
/// （`BepInEx\plugins\` 里同样放着依赖库，`MLLoader\Mods\` 里也可能有元数据缺失的 dll）。
/// 正确做法是**读元数据**判断有没有模组基类：
/// - 有 `MelonLoader.MelonMod` / `MelonPlugin` 基类 → MelonLoader 模组；
/// - 有 `BepInEx...BaseUnityPlugin` / `BasePlugin` 基类 → BepInEx 插件；
/// - 都没有 → 依赖库/非模组（UI 标 Dependency）。
///
/// 实现用 `System.Reflection.Metadata`（.NET 共享框架自带，双端 net6 都可用）：
/// **只读 PE 元数据，不执行任何模组代码**，零副作用（比 `Assembly.LoadFrom` 安全得多）。
/// </summary>
public static class AssemblyFormatProbe
{
    private const int MaxBaseDepth = 8;

    private sealed class Cached
    {
        public ModAssemblyFormat Format;
        public string Evidence;
        public long Size;
        public DateTime Mtime;
    }

    private static readonly Dictionary<string, Cached> _cache =
        new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);

    /// <summary>探测格式（带 mtime/size 缓存，避免每次刷新都重读全部 dll）。</summary>
    public static ModAssemblyFormat Probe(string path, out string evidence)
    {
        evidence = "";
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return ModAssemblyFormat.Unknown;

            var fi = new FileInfo(path);
            if (_cache.TryGetValue(path, out var c) && c != null && c.Size == fi.Length && c.Mtime == fi.LastWriteTimeUtc)
            {
                evidence = c.Evidence;
                return c.Format;
            }

            var fmt = ProbeCore(path, out evidence);
            _cache[path] = new Cached { Format = fmt, Evidence = evidence, Size = fi.Length, Mtime = fi.LastWriteTimeUtc };
            return fmt;
        }
        catch (Exception ex)
        {
            evidence = "probe failed: " + ex.Message;
            return ModAssemblyFormat.Unknown;
        }
    }

    private static ModAssemblyFormat ProbeCore(string path, out string evidence)
    {
        evidence = "";
        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata) { evidence = "no metadata"; return ModAssemblyFormat.Unknown; }

        var md = pe.GetMetadataReader();

        // 1) 收集全部类型定义：自身名字 + 基类（可能是本程序集内的 typedef，也可能是外部 typeref）
        var ownName = new Dictionary<int, string>();                 // rowId → "ns.name"
        var baseOfTd = new Dictionary<int, int>();                   // rowId → 本程序集内基类 rowId
        var baseNameOf = new Dictionary<int, string>();              // rowId → 外部基类 "ns.name"

        foreach (var h in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(h);
            int row = MetadataTokens.GetRowNumber(h);
            ownName[row] = Full(md.GetString(td.Namespace), md.GetString(td.Name));

            var bt = td.BaseType;
            if (bt.IsNil) continue;
            switch (bt.Kind)
            {
                case HandleKind.TypeDefinition:
                    baseOfTd[row] = MetadataTokens.GetRowNumber((TypeDefinitionHandle)bt);
                    break;
                case HandleKind.TypeReference:
                {
                    var tr = md.GetTypeReference((TypeReferenceHandle)bt);
                    baseNameOf[row] = Full(md.GetString(tr.Namespace), md.GetString(tr.Name));
                    break;
                }
            }
        }

        // 2) 逐类型沿基类链往上走（最多 MaxBaseDepth 层），命中已知基类即定格式
        bool melon = false, bep = false;
        string melonVia = null, bepVia = null;

        foreach (var kv in ownName)
        {
            int row = kv.Key;
            string cur = kv.Value;
            for (int depth = 0; depth < MaxBaseDepth; depth++)
            {
                string bn = baseOfTd.ContainsKey(row) ? ownName.TryGetValue(baseOfTd[row], out var n) ? n : null : null;
                int nextRow = -1;
                if (bn == null && baseNameOf.TryGetValue(row, out var ext)) bn = ext;
                if (baseOfTd.TryGetValue(row, out var nxt)) nextRow = nxt;

                if (bn == null) break;

                if (bn == "MelonLoader.MelonMod" || bn == "MelonLoader.MelonPlugin" || bn == "MelonLoader.MelonBase")
                {
                    melon = true; melonVia = cur + " : " + bn; break;
                }
                if (IsBepInExPluginBase(bn))
                {
                    bep = true; bepVia = cur + " : " + bn; break;
                }

                if (nextRow < 0) break;
                row = nextRow;
            }
            if (melon && bep) break;
        }

        if (melon && !bep) { evidence = melonVia; return ModAssemblyFormat.MelonMod; }
        if (bep && !melon) { evidence = bepVia; return ModAssemblyFormat.BepInExPlugin; }
        if (melon && bep) { evidence = "both: " + melonVia + " | " + bepVia; return ModAssemblyFormat.MelonMod; }

        evidence = "no mod types (library / non-mod)";
        return ModAssemblyFormat.Unknown;
    }

    /// <summary>
    /// 是不是 BepInEx 的插件基类。
    ///
    /// ⚠️ **实测踩过的坑（用户反馈“禁用的 BepInEx 模组被识别成 dep”）**：这里原来只列了
    /// `BepInEx.BasePlugin` / `BepInEx.BaseUnityPlugin` / `BepInEx.Unity.Mono.BaseUnityPlugin`，
    /// 而**本游戏的 BepInEx 6 是 IL2CPP 运行时**，插件基类是 **`BepInEx.Unity.IL2CPP.BasePlugin`**
    /// （`src/OpenNestCoop/Plugin.cs`：`using BepInEx.Unity.IL2CPP; public class Plugin : BasePlugin`；
    /// 二进制里只有 `BepInEx.Unity.IL2CPP` + `BasePlugin` 两个字符串，没有 `BepInEx.BasePlugin`）
    /// → 探不到模组基类 → 标成 `dep`（依赖库）。已加载的插件走加载器元数据所以看不出问题，
    /// **只有"文件被禁用（`.disabled`）→ 本次没加载 → 只能靠元数据探测"时才暴露**。
    ///
    /// 现改为按"命名空间是 BepInEx + 类名是已知插件基类名"判定（比枚举全名更耐版本变化）：
    /// IL2CPP 运行时 `BepInEx.Unity.IL2CPP.BasePlugin`、Mono 运行时 `BepInEx.Unity.Mono.BaseUnityPlugin` /
    /// `BepInEx.Unity.Mono.BepInExPlugin`（BepInEx 5 风格）、以及 `BepInEx.BasePlugin` / `BepInEx.BaseUnityPlugin`。
    /// </summary>
    private static bool IsBepInExPluginBase(string bn)
    {
        if (string.IsNullOrEmpty(bn)) return false;

        int dot = bn.LastIndexOf('.');
        string ns = dot > 0 ? bn.Substring(0, dot) : "";
        string nm = dot > 0 ? bn.Substring(dot + 1) : bn;

        if (nm != "BasePlugin" && nm != "BaseUnityPlugin" && nm != "BepInExPlugin") return false;
        return ns.StartsWith("BepInEx", StringComparison.Ordinal);
    }

    private static string Full(string ns, string name)
        => string.IsNullOrEmpty(ns) ? (name ?? "") : ns + "." + name;

    /// <summary>清缓存（文件被改名/移动后调用；也可不调——缓存按 mtime+size 自动失效）。</summary>
    public static void Invalidate() => _cache.Clear();
}
