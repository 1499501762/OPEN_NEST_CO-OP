using System;
using System.IO;
using OpenNestModMenu.API;
using OpenNestModMenu.Core;
using OpenNestModMenu.Registry;

namespace OpenNestModMenu.Loaders;

/// <summary>
/// 启停（T7 文件层 + T13 运行中层）：把模组文件在 `<c>X.dll</c>` ↔ `<c>X.dll.disabled</c>` 之间改名，
/// 并（能做到的宿主）**当场停用/恢复**运行中的模组。
///
/// 为何用改名作基底：这是加载器的**通用约定**（BepInEx/MelonLoader 都只看扩展名），
/// 不需要任何加载器 API，也不影响其它模组（docs/MOD_MENU.md §E/§7.2）。
///
/// 三层能力（详见 docs/MOD_MENU.md §E.2）：
/// - **受管模组**（契约 <c>CanToggleAtRuntime=true</c>）：调 `OnDisabled()`/`OnEnabled()` → 完全可逆，立即生效；
/// - **MelonLoader 模组**（含经桥）：`MelonBase.Unregister()`（撤 Harmony patch + 退订回调 + 移出注册表）→ 立即停；
///   本次会话停过的可以 `MelonAssembly.LoadMelons()` 重新注册 → 也能立即恢复；
/// - **BepInEx 插件**：加载器**没有卸载 API** → 只能改名 + 重启（不做"半禁"冻结，以免会话中途进入怪状态）。
///
/// ⚠️ 无论如何，程序集一旦加载就**无法卸载**（.NET 限制）。
/// </summary>
public static class ModFileState
{
    private const string DisabledSuffix = ".disabled";

    /// <summary>能否对该条目执行启停（不能时 <paramref name="reason"/> 给出原因）。</summary>
    public static bool CanToggle(ModEntryInfo e, out string reason)
    {
        reason = "";
        if (e == null)
        {
            reason = ModMenuLoc.L("ToggleNoEntry");
            return false;
        }
        if (string.IsNullOrEmpty(e.Path))
        {
            reason = ModMenuLoc.L("ToggleNoFile");
            return false;
        }
        if (IsSelf(e))
        {
            reason = ModMenuLoc.L("ToggleSelf");
            return false;
        }
        try
        {
            if (!File.Exists(e.Path))
            {
                reason = ModMenuLoc.L("ToggleMissing");
                return false;
            }
        }
        catch (Exception ex)
        {
            reason = ModMenuLoc.L("ToggleFailed", ex.Message);
            return false;
        }
        return true;
    }

    /// <summary>
    /// 切换启用状态。
    ///
    /// ⚠️ **顺序有讲究**（本轮实测修正）：
    /// - **启用**：先改文件（`.disabled` → `.dll`），**再**尝试热加载 —— 两个加载器都按 `*.dll` 发现文件，
    ///   文件还叫 `.disabled` 时热加载根本找不到它。（旧实现是"先热恢复、后改名"，于是**文件被禁用的模组永远只能靠重启启用**，
    ///   这正是用户反馈的"BepInEx 模组能禁用但没法启用，要重启"）
    /// - **停用**：先尝试停运行中的（改文件名不影响已在内存里的模组），再改文件。
    ///
    /// 返回文件操作是否成功；<paramref name="message"/> 给 UI 直接显示（含“立即/重启”语义）。
    /// </summary>
    public static bool TryToggle(ModEntryInfo e, out string message)
    {
        message = "";
        if (!CanToggle(e, out message)) return false;

        string id = e.Id ?? "";
        bool currentlyEnabled = !e.Path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
        bool wantEnabled = !currentlyEnabled;

        string target = currentlyEnabled
            ? e.Path + DisabledSuffix
            : e.Path.Substring(0, e.Path.Length - DisabledSuffix.Length);

        // ① 停用：先停运行中（立即生效部分）
        string runtimeNote = wantEnabled ? "" : StopRuntime(e);

        // ② 文件改名
        try
        {
            if (File.Exists(target))
            {
                message = ModMenuLoc.L("ToggleConflict", target);
                return false;
            }

            File.Move(e.Path, target);
            CoopLog.Info("modmenu.toggle", () => $"'{id}': file {(currentlyEnabled ? "disabled" : "enabled")} -> {target}");
        }
        catch (Exception ex)
        {
            message = ModMenuLoc.L("ToggleFailed", ex.Message);
            CoopLog.Warn("modmenu.toggle", () => $"file toggle failed for '{id}': {ex.Message}");
            return false;
        }

        AssemblyFormatProbe.Invalidate();   // 文件改名 → 格式探测缓存按路径 key，清掉避免旧结果残留

        // ③ 启用：此时文件已是 *.dll，再尝试热加载（BepInEx 链加载器 / MLL 重新装载注册）
        if (wantEnabled) runtimeNote = ResumeRuntime(e, target);

        string name = string.IsNullOrEmpty(e.DisplayName) ? id : e.DisplayName;
        message = (wantEnabled ? ModMenuLoc.L("ToggleEnabled", name) : ModMenuLoc.L("ToggleDisabled", name))
                + (string.IsNullOrEmpty(runtimeNote) ? "" : "   " + runtimeNote);

        ModInventory.RequestRefresh("toggle");
        ModMenuRegistry.RequestRescan("toggle");
        return true;
    }

    /// <summary>运行中停用（尽力立即生效）：受管模组 → 契约；ML 模组 → MelonLoader Unregister。</summary>
    private static string StopRuntime(ModEntryInfo e)
    {
        string id = e.Id ?? "";

        // ① 受管模组（契约 CanToggleAtRuntime）→ 模组自己实现的可逆停用（最干净）
        if (ModMenuRegistry.TryGet(id, out var rec) && rec?.Provider != null)
        {
            string msg;
            if (ModMenuRegistry.SetRuntimeDisabled(id, true, out msg))
            {
                ModRuntimeState.Mark(id, RuntimeStop.StoppedProvider, msg);
                return ModMenuLoc.L("RuntimeStoppedProvider");
            }
        }

        // ② ML 模组（含经桥）→ MelonLoader 官方 Unregister（撤 patch + 退订 + 移出注册表）
        if (e.Format == ModAssemblyFormat.MelonMod)
        {
            var inst = FindMelonInstance(e);
            if (inst == null)
            {
                ModRuntimeState.Mark(id, RuntimeStop.Unsupported, "not running");
                return ModMenuLoc.L("RuntimeUnsupported");
            }
            if (LoaderReflect.TryMelonUnregister(inst.Instance, "disabled via OpenNestModMenu"))
            {
                ModRuntimeState.Mark(id, RuntimeStop.StoppedMelon, inst.Location);
                return ModMenuLoc.L("RuntimeStoppedMelon");
            }
            ModRuntimeState.Mark(id, RuntimeStop.Failed, "unregister failed");
            return ModMenuLoc.L("RuntimeFailed");
        }

        // ③ BepInEx 插件：加载器无卸载 API → 做**实验性软停用**
        //   （撒它的 Harmony patch + 销毁它加的组件 + 尽量调它的 Unload()）；
        //   ⚠️ 静态状态/后台线程/游戏侧订阅可能残留 —— 但因为文件同时改了名，重启后彻底干净。
        if (e.Format == ModAssemblyFormat.BepInExPlugin && e.Loaded)
        {
            if (LoaderReflect.TryBepInExSoftStop(e.Path, out string detail))
            {
                ModRuntimeState.Mark(id, RuntimeStop.StoppedSoft, detail);
                return ModMenuLoc.L("RuntimeStoppedSoft");
            }
            ModRuntimeState.Mark(id, RuntimeStop.Unsupported, detail);
            return ModMenuLoc.L("RuntimeUnsupported");
        }

        // ④ 其它：加载器无卸载 API
        ModRuntimeState.Mark(id, RuntimeStop.Unsupported, e.Format.ToString());
        return ModMenuLoc.L("RuntimeUnsupported");
    }

    /// <summary>
    /// 运行中恢复/启用（仅"本次会话被我们停过"、或**文件刚被重新启用但本次没加载**的模组）。
    /// <paramref name="newPath"/> = 改名后的真实路径（`*.dll`），热加载必须用它。
    /// </summary>
    private static string ResumeRuntime(ModEntryInfo e, string newPath)
    {
        string id = e.Id ?? "";

        // ① 受管模组 → 契约恢复
        if (ModMenuRegistry.TryGet(id, out var rec) && rec?.Provider != null)
        {
            string msg;
            if (ModMenuRegistry.SetRuntimeDisabled(id, false, out msg))
            {
                ModRuntimeState.Clear(id);
                return ModMenuLoc.L("RuntimeResumedProvider");
            }
        }

        // ② ML 模组（含经桥）：本次被 Unregister 过的 → 重新注册；**文件刚启用、本次压根没加载**的 → 直接装载注册
        if (e.Format == ModAssemblyFormat.MelonMod && (ModRuntimeState.IsStopped(id) || !e.Loaded))
        {
            if (LoaderReflect.TryMelonReload(newPath) || LoaderReflect.TryMelonReload(id))
            {
                ModRuntimeState.Clear(id);
                return ModMenuLoc.L("RuntimeResumedMelon");
            }
            if (ModRuntimeState.IsStopped(id)) ModRuntimeState.Mark(id, RuntimeStop.Failed, "reload failed");
            return ModMenuLoc.L("RuntimeReloadFailed");
        }

        // ③ BepInEx 插件：本次没加载（文件曾被禁用）→ 走链加载器热加载；已加载的无需处理（改名只影响下次启动）
        if (e.Format == ModAssemblyFormat.BepInExPlugin && !e.Loaded)
        {
            if (LoaderReflect.TryBepInExHotLoad(newPath, out string why))
            {
                CoopLog.Info("modmenu.toggle", () => $"'{id}': hot-loaded BepInEx plugin (guid={why})");
                return ModMenuLoc.L("RuntimeLoadedBepInEx");
            }
            string w = why;
            CoopLog.Warn("modmenu.toggle", () => $"'{id}': BepInEx hot-load failed: {w}");
            return ModMenuLoc.L("RuntimeHotLoadFailed", w);
        }

        return "";   // 其余情况只靠文件改名（下次启动）
    }

    /// <summary>按路径优先、程序集名/显示名兜底，找当前运行中的 ML 模组实例。</summary>
    private static LoaderReflect.MelonInstance FindMelonInstance(ModEntryInfo e)
    {
        var list = LoaderReflect.ReadMelonInstances();
        for (int i = 0; i < list.Count; i++)
        {
            if (!string.IsNullOrEmpty(e.Path) &&
                string.Equals(list[i].Location, e.Path, StringComparison.OrdinalIgnoreCase)) return list[i];
        }
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].AssemblyName, e.Id, StringComparison.OrdinalIgnoreCase)) return list[i];
            if (!string.IsNullOrEmpty(e.DisplayName) &&
                string.Equals(list[i].Name, e.DisplayName, StringComparison.OrdinalIgnoreCase)) return list[i];
        }
        return null;
    }

    /// <summary>
    /// 仅做**运行中**停用/恢复（不动文件）。
    /// 用途：自测钩子（`-onnmm-selftest-runtime=`），以及将来“不改文件的热停用”复用。返回补充说明（空 = 未做任何事）。
    /// ⚠️ 恢复分支要求文件已是 `*.dll`（热加载按 `*.dll` 发现）→ 这里用条目路径去掉 `.disabled` 后的值。
    /// </summary>
    public static string TrySetRuntime(ModEntryInfo e, bool stop)
    {
        if (stop) return StopRuntime(e);
        string path = e?.Path ?? "";
        if (path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            path = path.Substring(0, path.Length - DisabledSuffix.Length);
        return ResumeRuntime(e, path);
    }

    /// <summary>运行中能否立即停用（UI 用它决定按钮文案）。</summary>
    public static bool CanStopAtRuntime(ModEntryInfo e)
    {
        if (e == null || !e.Enabled || !e.Loaded) return false;
        try
        {
            if (ModMenuRegistry.TryGet(e.Id ?? "", out var rec) && rec?.Provider != null && rec.Provider.CanToggleAtRuntime) return true;
            if (e.Format == ModAssemblyFormat.MelonMod && FindMelonInstance(e) != null) return true;
            return IsSoftStopOnly(e);
        }
        catch { return false; }
    }

    /// <summary>
    /// 只能"软停用"（实验性）的情形：**BepInEx 插件**——加载器没有卸载 API，我们只能撒 patch + 销毁组件，
    /// 不可能真卸载。UI 用它把按钮标成“停用（实验性）”。
    /// </summary>
    public static bool IsSoftStopOnly(ModEntryInfo e)
    {
        if (e == null || !e.Enabled || !e.Loaded) return false;
        try { return e.Format == ModAssemblyFormat.BepInExPlugin && LoaderReflect.CanSoftStopBepInEx(); }
        catch { return false; }
    }

    /// <summary>
    /// 运行中能否**立即启用**（UI 用它决定按钮文案）。三种情形：
    /// ① 本次会话被我们停过的（受管模组 / MLL 模组）→ 恢复；
    /// ② MLL 模组且文件刚被启用 → 直接装载 + 注册；
    /// ③ **BepInEx 插件**且已找到链加载器实例 → 走 `Instance.LoadPlugins(dir)` 热加载（见 `LoaderReflect.TryBepInExHotLoad`）。
    /// </summary>
    public static bool CanStartAtRuntime(ModEntryInfo e)
    {
        if (e == null || e.Enabled) return false;
        try
        {
            if (ModRuntimeState.IsStopped(e.Id)) return true;
            if (e.Format == ModAssemblyFormat.MelonMod) return true;
            if (e.Format == ModAssemblyFormat.BepInExPlugin) return LoaderReflect.CanHotLoadBepInEx();
            return false;
        }
        catch { return false; }
    }

    /// <summary>是否本模组自身（避免用户在菜单里把菜单自己禁用掉 → 重启后找不到菜单）。</summary>
    private static bool IsSelf(ModEntryInfo e)
    {
        string id = e.Id ?? "";
        return id.StartsWith("OpenNestModMenu", StringComparison.OrdinalIgnoreCase);
    }
}
