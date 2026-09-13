using System;
using System.Collections.Generic;
using OpenNestModMenu.API;
using OpenNestModMenu.Config;
using OpenNestModMenu.Core;
using OpenNestModMenu.Registry;

namespace OpenNestModMenu.UI;

/// <summary>
/// “某个模组有哪些设置行”的**唯一来源**：
/// ① 第三方声明式页面（<see cref="IModMenuProvider"/> → <see cref="PageCollector"/>）
/// ② 它自己配置文件里的设置（<see cref="ModConfigStore"/> → <see cref="UiSetting.FromConfig"/>）
///
/// 为什么单独抽出来（2026-09-13）：同一个内容现在有两个渲染器 ——
/// 本模组自带的右栏设置页（<see cref="ModMenuSettingsView"/>）与 **UIKit 的声明式页面**
/// （见 <see cref="UIKitIntegration"/>）。两边共用本方法，避免"迁移后的界面少一行"这类漂移。
/// </summary>
internal static class ModMenuSettingsSource
{
    /// <summary>取该模组的设置行（顺序：第三方页面 → 配置文件；各自带一个分组标题）。</summary>
    public static List<UiSetting> Rows(ModEntryInfo e)
    {
        var items = new List<UiSetting>();
        if (e == null) return items;
        string ownerId = e.Id ?? "";

        try
        {
            // ① 第三方声明式页面（若该模组注册了 provider）
            if (ModMenuRegistry.TryGet(ownerId, out var rec) && rec?.Provider != null)
            {
                var page = PageCollector.Collect(rec.Provider);
                if (page.Count > 0)
                {
                    items.Add(new UiSetting { Label = ModMenuLoc.L("SettingsProviderPage"), Header = true });
                    items.AddRange(page);
                }
            }

            // ② 配置文件里的设置（通用格式兼容）
            if (ModConfigStore.HasSettings(e))
            {
                var cfg = ModConfigStore.GetSettings(e);
                if (cfg.Length > 0)
                {
                    items.Add(new UiSetting
                    {
                        Label = ModMenuLoc.L("SettingsConfigFile", ShortPath(e.ConfigFile ?? ConfigLocator.Find(e))),
                        Header = true,
                    });
                    for (int i = 0; i < cfg.Length; i++) items.Add(UiSetting.FromConfig(cfg[i]));
                }
            }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.settings", () => "collect rows failed: " + ex.Message);
        }
        return items;
    }

    /// <summary>把长路径缩成一个文件名（分组标题用；太长会把标题挤掉）。</summary>
    internal static string ShortPath(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        try { return System.IO.Path.GetFileName(p); } catch { return p; }
    }
}
