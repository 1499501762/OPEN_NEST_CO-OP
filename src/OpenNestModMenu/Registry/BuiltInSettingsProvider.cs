using System;
using OpenNestModMenu.API;
using OpenNestModMenu.Core;

namespace OpenNestModMenu.Registry;

/// <summary>
/// 本模组**自己的设置页**（内置提供者）。
///
/// 作用有两个：
/// 1. 让"统一设置中心"在没有任何第三方接入时也有内容 —— 展示本模组身份、部署形态、路径与注册表状态；
/// 2. 作为双通道注册的**活体验证**：它通过主动注册进入契约注册表，被动扫描应能正确识别"已存在"而不重复注册。
/// </summary>
public sealed class BuiltInSettingsProvider : ModMenuProviderBase
{
    /// <summary>内置提供者的固定 Id（同时用于 <see cref="ModMenuRegistry"/> 标记 BuiltIn）。</summary>
    public const string ProviderId = "OpenNestModMenu";

    public override string Id => ProviderId;
    public override string DisplayName => ModMenuInfo.Name;
    public override string Version => ModMenuInfo.Version;
    public override string Author => ModMenuInfo.Author;

    public override void BuildPage(IModMenuPage page)
    {
        page.Header(ModMenuLoc.L("BuiltAbout"));
        page.Label($"{ModMenuInfo.Name}  v{ModMenuInfo.Version}");
        page.Label(ModMenuLoc.L("BuiltBuiltFor") + ": " + ModMenuInfo.BuildPlatform);
        page.Label(ModMenuLoc.L("BuiltShape") + ": " + ModMenuPaths.Shape);
        page.Label(ModMenuLoc.L("BuiltApi") + ": " + ModMenuHost.ApiVersion);
        page.Label(ModMenuLoc.L("BuiltLang") + ": " + ModMenuLoc.Current);

        page.Separator();
        page.Header(ModMenuLoc.L("BuiltPaths"));
        page.Label(ModMenuLoc.L("BuiltGame") + ": " + ModMenuPaths.GameDir);
        page.Label(ModMenuLoc.L("BuiltConfig") + ": " + ModMenuPaths.ConfigFile);
        page.Label(ModMenuLoc.L("BuiltLangFile") + ": " + ModMenuLoc.FilePath);
        page.Label(ModMenuLoc.L("BuiltLogs") + ": " + ModMenuPaths.LogDir);
        if (ModMenuPaths.HasBepInEx)
            page.Label("BepInEx: " + ModMenuPaths.BepInExRoot);
        page.Label(ModMenuLoc.L("BuiltMelonRoot") + ": " + ModMenuPaths.MelonRoot);

        page.Separator();
        page.Header(ModMenuLoc.L("BuiltRegistry"));
        page.Label(ModMenuLoc.L("BuiltProviders") + ": " + ModMenuRegistry.Count
                   + $"  ({ModMenuLoc.L("BuiltActive")}={ModMenuRegistry.ActiveCount}, "
                   + $"{ModMenuLoc.L("BuiltScanned")}={ModMenuRegistry.ScannedCount})");
        page.Label(ModMenuLoc.L("BuiltScans") + ": " + ModMenuRegistry.ScanRuns
                   + (ModMenuRegistry.LastScanAt.Length > 0
                        ? $"   {ModMenuLoc.L("BuiltLast")}: {ModMenuRegistry.LastScanAt} '{ModMenuRegistry.LastScanWhy}'"
                        : ""));
        if (ModMenuRegistry.ScanRuns > 0)
        {
            page.Label(ModMenuLoc.L("BuiltLastResult")
                       + $": assemblies={ModMenuRegistry.LastScanAssemblies} candidates={ModMenuRegistry.LastScanCandidates}"
                       + $" new={ModMenuRegistry.LastScanNew} loadFail={ModMenuRegistry.LastScanLoadFailed} typeFail={ModMenuRegistry.LastScanTypeFailed}"
                       + $" ({ModMenuRegistry.LastScanMs:F0} ms)");
        }

        page.Separator();
        page.Action("", ModMenuLoc.L("BuiltRescan"), () => ModMenuRegistry.RequestRescan("builtin"));

        // 本模组自己的开关（存 `OpenNestModMenu.cfg`）：调试模式
        // ⚠ 用户 2026-09-13：“UI 右下角的 Debug 信息只在调试模式启用” —— 默认关，界面只留必要信息。
        page.Separator();
        page.Header(ModMenuLoc.L("BuiltOwnSettings"));
        page.Bool("Debug", ModMenuLoc.L("BuiltDebugMode"), ModMenuPrefs.Debug, v =>
        {
            try { ModMenuPrefs.SetDebug(v); } catch (Exception ex) { CoopLog.Warn("modmenu.prefs", () => "set debug failed: " + ex.Message); }
            ModMenuLoc.NotifyChanged();        // 自带界面：置脏重画
            UIKitIntegration.Invalidate();    // UIKit 界面：重建当前页（底栏摘要跟着出现/消失）
        });
    }
}
