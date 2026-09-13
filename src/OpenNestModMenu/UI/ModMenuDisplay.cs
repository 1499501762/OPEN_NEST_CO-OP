using OpenNestModMenu.API;
using OpenNestModMenu.Core;
using UnityEngine;

namespace OpenNestModMenu.UI;

/// <summary>条目性质：用来分组/筛选（**不是**加载器语义，纯展示分类）。</summary>
public enum EntryKind
{
    /// <summary>真正的模组（有元数据或已加载）。</summary>
    Mod = 0,

    /// <summary>磁盘上存在但既没加载、也无元数据 → 多为依赖库。</summary>
    Dependency = 1,

    /// <summary>已禁用（`*.dll.disabled`）。</summary>
    Disabled = 2,
}

/// <summary>
/// 条目 → 界面文案/颜色 的集中映射（列表行与右栏详情共用，避免两处规则漂移）。
///
/// ⚠️ 来源标记语义（docs/MOD_MENU.md §3.1，本轮修正）：
/// - <see cref="ModEntryInfo.Host"/> = **这个模组跑在谁的运行时里**（进程宿主）；
/// - <see cref="ModEntryInfo.ViaBridge"/> = **这个模组是否经 ML 桥加载**（仅对 MelonLoader 模组有意义）；
/// - 早先的 bug 是把"进程里有桥"当成"每个条目都经桥" → 全都显示 [桥]，已修：判定下沉到每个条目。
/// </summary>
public static class ModMenuDisplay
{
    public static EntryKind KindOf(ModEntryInfo e)
    {
        if (e == null) return EntryKind.Dependency;
        if (!e.Enabled) return EntryKind.Disabled;
        // 是模组 = 已加载，或**元数据里有模组基类**（磁盘只读探测）；两者都没有 = 依赖库/非模组。
        if (e.Loaded || e.Format != ModAssemblyFormat.Unknown) return EntryKind.Mod;
        return EntryKind.Dependency;
    }

    /// <summary>列表行的紧凑来源/格式标签（完整两枚标签在右栏详情）。</summary>
    public static string PillLabel(ModEntryInfo e)
    {
        if (e == null) return ModMenuLoc.L("PillUnknown");
        switch (e.Format)
        {
            case ModAssemblyFormat.BepInExPlugin:
                return "BepInEx";
            case ModAssemblyFormat.MelonMod:
                return "MLL";   // MLL = MelonLoader（据用户要求：原“ML bridge”统一叫 MLL）
            default:
                return ModMenuLoc.L("PillDependency");
        }
    }

    public static Color PillColor(ModEntryInfo e)
    {
        if (e == null) return ModMenuTheme.PillUnknown;
        switch (e.Format)
        {
            case ModAssemblyFormat.BepInExPlugin:
                return ModMenuTheme.PillBepInEx;
            case ModAssemblyFormat.MelonMod:
                return ModMenuTheme.PillMelon;
            default:
                return ModMenuTheme.PillUnknown;
        }
    }

    /// <summary>行右侧的短状态（已加载给版本号；否则给状态词）。运行中停用优先显示。</summary>
    public static string MetaText(ModEntryInfo e)
    {
        if (e == null) return "";
        if (e.Duplicate) return ModMenuLoc.L("MetaDup");
        if (!e.Enabled) return ModMenuLoc.L("StateDisabled");
        if (!e.Loaded) return ModMenuLoc.L("StateNotLoaded");
        if (ModRuntimeState.IsStopped(e.Id)) return ModMenuLoc.L("StateStoppedNow");
        return string.IsNullOrEmpty(e.Version) ? ModMenuLoc.L("StateLoaded") : e.Version;
    }

    public static Color MetaColor(ModEntryInfo e)
    {
        if (e == null) return ModMenuTheme.TextDim;
        if (e.Duplicate) return ModMenuTheme.Err;
        if (!e.Enabled) return ModMenuTheme.Warn;
        if (!e.Loaded) return ModMenuTheme.TextDim;
        if (ModRuntimeState.IsStopped(e.Id)) return ModMenuTheme.Warn;
        return ModMenuTheme.Ok;
    }

    /// <summary>状态词（详情用，含重复/禁用/未加载三态）。</summary>
    public static string StateText(ModEntryInfo e)
    {
        if (e == null) return "";
        string sep = ModMenuLoc.L("Separator");
        var sb = new System.Text.StringBuilder();
        sb.Append(e.Loaded ? ModMenuLoc.L("StateLoaded") : ModMenuLoc.L("StateNotLoaded"));
        sb.Append(sep).Append(e.Enabled ? ModMenuLoc.L("StateEnabled") : ModMenuLoc.L("StateDisabled"));
        if (e.Duplicate) sb.Append(sep).Append(ModMenuLoc.L("StateDup"));
        return sb.ToString();
    }

    /// <summary>
    /// **加载器**（不是条目）的宿主标签 —— 本地化。
    /// ⚠ 别直接用 `LoaderInfo.HostLabel`：那是给日志/诊断的**中文**固定串（用户：“有语言键缺失残留”）。
    /// </summary>
    public static string LoaderHostLabel(LoaderInfo li)
    {
        if (li == null) return ModMenuLoc.L("HostUnknown");
        string host = li.Host switch
        {
            ModLoaderHost.BepInEx => ModMenuLoc.L("HostBepInEx"),
            ModLoaderHost.MelonLoader => ModMenuLoc.L("HostMelonLoader"),
            _ => ModMenuLoc.L("HostUnknown"),
        };
        return li.ViaBridge ? host + " + MLL" : host;
    }

    /// <summary>宿主标签（本地化；不用契约里的 HostLabel，后者是给第三方直读的固定英文）。</summary>
    public static string HostLabel(ModEntryInfo e)
    {
        if (e == null) return ModMenuLoc.L("HostUnknown");
        if (e.ViaBridge) return ModMenuLoc.L("HostMllBridge");   // BepInEx + MLL
        switch (e.Host)
        {
            case ModLoaderHost.BepInEx: return ModMenuLoc.L("HostBepInEx");
            case ModLoaderHost.MelonLoader: return ModMenuLoc.L("HostMelonLoader");
            default: return ModMenuLoc.L("HostUnknown");
        }
    }

    /// <summary>格式标签（本地化；磁盘条目为按目录推断，文案里已说明）。</summary>
    public static string FormatLabel(ModEntryInfo e)
    {
        if (e == null) return ModMenuLoc.L("FmtUnknown");
        switch (e.Format)
        {
            case ModAssemblyFormat.BepInExPlugin: return ModMenuLoc.L("FmtBepInEx");
            case ModAssemblyFormat.MelonMod: return ModMenuLoc.L("FmtMelon");
            default: return ModMenuLoc.L("FmtUnknown");
        }
    }

    public static Color StateColor(ModEntryInfo e)
    {
        if (e == null) return ModMenuTheme.TextDim;
        if (e.Duplicate) return ModMenuTheme.Err;
        if (!e.Enabled) return ModMenuTheme.Warn;
        if (!e.Loaded) return ModMenuTheme.TextDim;
        return ModMenuTheme.Ok;
    }
}
