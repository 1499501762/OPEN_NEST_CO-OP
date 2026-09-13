using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using OpenNestModMenu.API;
using OpenNestModMenu.Core;

namespace OpenNestModMenu.Config;

/// <summary>
/// 模组配置文件的**定位**（§6.2"通用格式兼容"）。
///
/// 规则（**按名字匹配，不对文件名做任何“改写/前缀猜”**）：
/// ① 加载器真值：BepInEx `PluginInfo.Metadata.GUID` → <c>BepInEx\config\&lt;GUID&gt;.cfg</c>；
/// ② 字面同名：<c>&lt;目录&gt;\&lt;Id / 显示名 / 磁盘文件名&gt;.cfg</c>；
/// ③ **按模组名匹配**：文件名与**模组名**（`ModEntryInfo.DisplayName` = 模组自己声明的名字）
///    归一化（小写 + 去空格/点/下划线/连字符）后**完全相等**即命中
///    —— 实测：原生 MLL 端模组名 “Open Nest Co-op” ↔ `UserData\OpenNestCoop.cfg`
///    （而程序集名是 `OpenNestCoop.MelonMod`，字面同名对不上，这才是之前读不到的真正原因）；
/// ④ `UserData\MelonPreferences.cfg` 里有没有同名 `[分类]`。
/// 命中③/④（推断得到的）会**存盘**到 `ConfigMap`，下次直接用（用户要求“匹配过一次之后要保存”）。
/// 全部只读文件系统，不加载任何模组代码。
/// </summary>
public static class ConfigLocator
{
    /// <summary>解析该条目"用户可见的设置文件"；找不到返回空串。</summary>
    public static string Find(ModEntryInfo e)
    {
        if (e == null) return "";
        try
        {
            if (!string.IsNullOrEmpty(e.ConfigFile) && File.Exists(e.ConfigFile)) return e.ConfigFile;

            // ⓪ 上次“按模组名匹配”出来的结果已经存过盘 → 直接用，不再匹配（见 ConfigMap；猜错了用户可手改那个文件）
            if (ConfigMap.TryGet(e.Id, out string saved)) return LogHit(saved, e, "saved");

            var names = BaseNames(e);
            var dirs = new List<string>(CandidateDirs(e));

            // ① 字面同名（id / 显示名 / 磁盘文件名）
            foreach (var dir in dirs)
            {
                string hit = TryExact(dir, names);
                if (hit.Length > 0) return LogHit(hit, e, "exact");
            }

            // ② **按模组名匹配**（归一化后完全相等；不做文件名变形/前缀猜）
            foreach (var dir in dirs)
            {
                string hit = TryByName(dir, names, out string matchedName);
                if (hit.Length > 0) return Remember(hit, e, "模组名 '" + matchedName + "'");
            }

            // ③ MelonPreferences.cfg：按 [分类] 名字匹配（同样用模组名）
            if (e.Format == ModAssemblyFormat.MelonMod || e.Host == ModLoaderHost.MelonLoader)
            {
                string prefs = Path.Combine(ModMenuPaths.MelonUserDataDir ?? "", "MelonPreferences.cfg");
                if (File.Exists(prefs) && HasSection(prefs, names)) return Remember(prefs, e, "melonprefs");
            }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.config", () => "locate failed: " + ex.Message);
        }
        return "";
    }

    private static string LogHit(string path, ModEntryInfo e, string how)
    {
        var id = e?.Id ?? "";
        CoopLog.Info("modmenu.config", () => $"config for '{id}': {path}  (matched by {how})");
        return path;
    }

    /// <summary>
    /// 推断命中（按模组名 / MelonPreferences）→ **把结果存盘**（用户要求：“匹配过一次之后要保存”）。
    /// 字面同名不进表：那是确定的，存下来只会把表写咸噪声。
    /// </summary>
    private static string Remember(string path, ModEntryInfo e, string how)
    {
        LogHit(path, e, how);
        try { if (!string.IsNullOrEmpty(e?.Id)) ConfigMap.Set(e.Id, path); } catch { }
        return path;
    }

    /// <summary>候选基名（不含扩展名）：Id / 显示名 / 磁盘文件名。</summary>
    private static List<string> BaseNames(ModEntryInfo e)
    {
        var names = new List<string>();
        if (!string.IsNullOrEmpty(e.Id)) names.Add(e.Id);
        if (!string.IsNullOrEmpty(e.DisplayName) && !names.Contains(e.DisplayName)) names.Add(e.DisplayName);
        try
        {
            var an = Path.GetFileNameWithoutExtension(e.Path ?? "");
            if (!string.IsNullOrEmpty(an) && !names.Contains(an)) names.Add(an);
        }
        catch { }
        return names;
    }

    /// <summary>
    /// **按模组名匹配**：目录下哪个 <c>*.cfg</c> 的文件名（归一化后）与某个候选名（模组名/Id/文件名）**完全相等**。
    /// 不做后缀去除、不做前缀模糊 —— 只认“名字一样”。
    /// 命中时给出实际相等的那个名字（写日志/存映射用）。
    /// </summary>
    private static string TryByName(string dir, List<string> names, out string matchedName)
    {
        matchedName = "";
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "";
        try
        {
            foreach (var f in Directory.GetFiles(dir, "*.cfg", SearchOption.TopDirectoryOnly))
            {
                string baseName = Path.GetFileNameWithoutExtension(f);
                string looseBase = Loose(baseName);
                if (looseBase.Length < 3) continue;
                if (OwnedByOtherEntry(baseName)) continue;      // 已被别的条目“字面同名”占着 → 不抢
                foreach (var n in names)
                {
                    string looseName = Loose(n);
                    if (looseName.Length < 3) continue;
                    if (looseBase != looseName) continue;
                    matchedName = n;
                    return f;
                }
            }
        }
        catch { }
        return "";
    }

    private static string TryExact(string dir, List<string> names)
    {
        if (names == null || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "";
        foreach (var n in names)
        {
            if (string.IsNullOrEmpty(n)) continue;
            try
            {
                string p = Path.Combine(dir, n + ".cfg");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return "";
    }

    /// <summary>该文件基名是否被*别的*条目“字面同名”占用（那一条自己能精确命中，不该被这里抢走）。</summary>
    private static bool OwnedByOtherEntry(string fileBaseName)
    {
        try
        {
            var all = ModInventory.Entries;
            string loose = Loose(fileBaseName);
            for (int i = 0; i < all.Length; i++)
            {
                var e = all[i];
                if (e == null) continue;
                bool sameId = Loose(e.Id ?? "") == loose;
                bool sameFile = false;
                try { sameFile = Loose(Path.GetFileNameWithoutExtension(e.Path ?? "")) == loose; } catch { }
                if (sameId || sameFile) return true;
            }
        }
        catch { }
        return false;
    }

    private static IEnumerable<string> CandidateDirs(ModEntryInfo e)
    {
        if (e.Host == ModLoaderHost.BepInEx || e.Format == ModAssemblyFormat.BepInExPlugin)
            yield return ModMenuPaths.BepInExConfigDir;
        if (e.Host == ModLoaderHost.MelonLoader || e.Format == ModAssemblyFormat.MelonMod)
        {
            yield return ModMenuPaths.MelonUserDataDir;
            yield return ModMenuPaths.GameDir;                 // 少数模组把 cfg 放在游戏根
        }
        yield return ModMenuPaths.BepInExConfigDir;            // 兜底：都找一遍
        yield return ModMenuPaths.MelonUserDataDir;
    }

    /// <summary>文件里有没有名字匹配的 <c>[Section]</c>（大小写不敏感、忽略空格与下划线差异）。</summary>
    private static bool HasSection(string path, List<string> names)
    {
        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                string t = raw.Trim();
                if (t.Length < 3 || t[0] != '[' || t[t.Length - 1] != ']') continue;
                string sec = t.Substring(1, t.Length - 2).Trim();
                foreach (var n in names)
                    if (Loose(sec) == Loose(n)) return true;
            }
        }
        catch { }
        return false;
    }

    private static string Loose(string s)
        => (s ?? "").Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
}
