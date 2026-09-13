using System;
using System.Collections.Generic;
using System.IO;
using OpenNestModMenu.API;
using OpenNestModMenu.Core;

namespace OpenNestModMenu.Loaders;

/// <summary>
/// 模组清单：把「**加载器内存清单**（权威：谁真的加载了什么）」与「**磁盘清单**（谁存在但没加载/被禁用/只是依赖）」
/// 合成一份去重后的条目表，并给每条打上**来源标记**（宿主 / 格式 / 桥），见 docs/MOD_MENU.md §3.1、§D。
///
/// ⚠️ 时机会影响结果：经桥加载的 MelonLoader 模组要到**首帧**才注册，因此清单需要**多次刷新**
/// （启动 + 1s/5s/15s/30s），这与注册表的扫描调度是同一个道理。
/// </summary>
public static class ModInventory
{
    /// <summary>清单变化（条目数/加载数等摘要变化时触发）。UI 订阅它。</summary>
    public static event Action Changed;

    public static ModEntryInfo[] Entries { get; private set; } = Array.Empty<ModEntryInfo>();

    /// <summary>按 Id 取条目（不存在返回 null）。</summary>
    public static ModEntryInfo Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var all = Entries;
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null && string.Equals(all[i].Id, id, StringComparison.OrdinalIgnoreCase)) return all[i];
        return null;
    }

    public static int LoadedCount;
    public static int DisabledCount;
    public static int DependencyCount;   // 磁盘上存在但既非已加载模组、也非已知模组（多为依赖库）
    public static int DuplicateCount;    // 同一程序集出现在多个"已启用"路径（双初始化风险，§3.4）
    public static int FileCount;         // 磁盘上扫到的文件数（含依赖）

    public static string LastRefreshAt = "";
    public static string LastRefreshWhy = "";
    public static int RefreshRuns;

    private static int _bepPlugins, _melonMods, _skippedBundleFiles;
    private static readonly List<(float At, string Why)> _pending = new();
    private static float _clock;
    private static string _lastSig = "";
    private static string _lastDepSig = "";

    // ---------------- 调度（与 ModMenuRegistry 同构） ----------------

    /// <summary>安排若干次延迟刷新（秒）。</summary>
    public static void Schedule(params float[] delaysSec)
    {
        if (delaysSec == null) return;
        lock (_pending)
        {
            for (int i = 0; i < delaysSec.Length; i++)
                _pending.Add((_clock + Math.Max(0f, delaysSec[i]), "scheduled"));
        }
    }

    /// <summary>请求尽快刷新（用户操作/清单变化后）。</summary>
    public static void RequestRefresh(string why)
    {
        lock (_pending) _pending.Add((_clock + 0.1f, why ?? "manual"));
    }

    /// <summary>每帧驱动（ModMenuBehaviour 调用）。</summary>
    public static void Tick(float dt)
    {
        _clock += dt;
        while (true)
        {
            string why = null;
            lock (_pending)
            {
                for (int i = 0; i < _pending.Count; i++)
                {
                    if (_pending[i].At <= _clock) { why = _pending[i].Why; _pending.RemoveAt(i); break; }
                }
            }
            if (why == null) return;
            Refresh(why);
        }
    }

    // ---------------- 刷新 ----------------

    /// <summary>重建清单。</summary>
    public static void Refresh(string why)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var info = LoaderDetector.Current;
        var map = new Dictionary<string, ModEntryInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // ---- 1) 内存清单（加载器元数据 = 权威来源）----
            _bepPlugins = 0;
            _melonMods = 0;
            if (info.BepInExRuntime)
            {
                var plugins = LoaderReflect.ReadBepInExPlugins();
                _bepPlugins = plugins.Count;
                for (int i = 0; i < plugins.Count; i++) AddBepInPlugin(map, plugins[i]);
            }
            if (info.MelonLoaderRuntime)
            {
                var melons = LoaderReflect.ReadMelons();
                _melonMods = melons.Count;
                for (int i = 0; i < melons.Count; i++) AddMelon(map, melons[i]);
            }

            // ---- 2) 磁盘清单（未加载 / 已禁用 / 依赖）----
            FileCount = 0;
            _skippedBundleFiles = 0;
            ScanDisk(map, ModMenuPaths.BepInExPluginDir, info, isBepInExSide: true);
            ScanDisk(map, ModMenuPaths.MelonModsDir, info, isBepInExSide: false);
        }
        catch (Exception ex)
        {
            CoopLog.Error("loader.scan", () => $"inventory refresh failed: {ex.Message}");
        }

        // ---- 3) 汇总 ----
        var list = new List<ModEntryInfo>(map.Values);
        int loaded = 0, disabled = 0, deps = 0, dups = 0;
        var seenEnabled = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (e.Loaded) loaded++;
            else if (!e.Enabled) disabled++;
            else deps++;
            if (e.Path != null && e.Path.Length > 0 && e.Enabled)
            {
                seenEnabled.TryGetValue(e.Id ?? "", out int n);
                seenEnabled[e.Id ?? ""] = n + 1;
            }
        }
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            seenEnabled.TryGetValue(e.Id ?? "", out int n);
            e.Duplicate = n > 1 && e.Path != null && e.Path.Length > 0 && e.Enabled;
            if (e.Duplicate) dups++;
        }
        dups /= 2;   // 每条重复项都置了 Duplicate，计数取一半（成对）

        list.Sort((a, b) => string.CompareOrdinal(a.Id ?? "", b.Id ?? ""));
        Entries = list.ToArray();

        // T8：清单变了就重算统一顺序（回填每条目的 Order；依赖修正/冲突只在变化时记日志）
        try { Registry.ModInitScheduler.Apply(Entries); }
        catch (Exception ex) { CoopLog.Warn("modmenu.order", () => "apply after inventory failed: " + ex.Message); }

        // ⚠️ 列表展示顺序 = **统一加载顺序**（不是字母序）：用户实测反馈“左侧列表没有按加载顺序排”。
        //    Apply 已把 Order 回填好（1 起；0 = 不在顺序里）→ 这里按 Order 重排；
        //    未进顺序的排最后（再按 Id 兜底，保证多次刷新顺序稳定）。
        try { Array.Sort(Entries, CompareByLoadOrder); }
        catch (Exception ex) { CoopLog.Warn("modmenu.order", () => "sort by load order failed: " + ex.Message); }

        // 配置文件定位：加载器真值（BepInEx GUID → <GUID>.cfg / MLL 惯例名）拿不到时，
        // 用 ConfigLocator 的“去模组后缀 + 模糊”匹配补上，让 ModEntryInfo.ConfigFile 与界面一致。
        // 实测（用户反馈）：原生 MelonLoader 端程序集叫 `OpenNestCoop.MelonMod`，配置却是 `UserData\OpenNestCoop.cfg`。
        try
        {
            Config.ConfigMap.Invalidate();   // 清内存缓存（保留已存盘的映射）
            for (int i = 0; i < Entries.Length; i++)
            {
                var e = Entries[i];
                if (e == null) continue;
                if (!string.IsNullOrEmpty(e.ConfigFile) && File.Exists(e.ConfigFile)) continue;
                string cfg = Config.ConfigLocator.Find(e);
                if (cfg.Length > 0) e.ConfigFile = cfg;
            }
        }
        catch (Exception ex) { CoopLog.Warn("modmenu.config", () => "config locate pass failed: " + ex.Message); }

        LoadedCount = loaded;
        DisabledCount = disabled;
        DependencyCount = deps;
        DuplicateCount = dups;
        LastRefreshAt = DateTime.Now.ToString("HH:mm:ss");
        LastRefreshWhy = why ?? "";
        RefreshRuns++;
        sw.Stop();
        try { FrameProfiler.Instance.AddMs("inventory", sw.Elapsed.TotalMilliseconds); } catch { }

        // ---- 4) 日志 ----
        string sig = $"{Entries.Length}|{loaded}|{disabled}|{deps}|{dups}|{_bepPlugins}|{_melonMods}";
        bool changed = sig != _lastSig;
        _lastSig = sig;

        CoopLog.Info("inventory", () => $"inventory('{why}'): entries={Entries.Length} loaded={loaded} disabled={disabled} deps={deps} dup={dups}"
            + $" | sources: bepPlugins={_bepPlugins} melonMods={_melonMods} files={FileCount} in {sw.ElapsedMilliseconds} ms");

        // 依赖声明摘要（一次性）：调度器的“依赖前移”完全基于这份数据 —— 为 0 就说明**没有任何模组声明依赖**，
        // 而不是“调度器没做”（用户 2026-09-13 问“依赖需要加载顺序吗？有跟随加载顺序吗？”时就靠这行分辨）。
        try
        {
            var withDeps = new List<string>();
            for (int i = 0; i < Entries.Length; i++)
            {
                var e2 = Entries[i];
                if (e2?.DependsOn == null || e2.DependsOn.Length == 0) continue;
                withDeps.Add((e2.Id ?? "") + " -> [" + string.Join(",", e2.DependsOn) + "]");
            }
            string depSig = string.Join(" | ", withDeps);
            if (depSig != _lastDepSig)
            {
                _lastDepSig = depSig;
                CoopLog.Info("modmenu.order", () => withDeps.Count == 0
                    ? "declared dependencies: 无（所有条目都没声明依赖 ⇒ 顺序只看加载器/用户表）"
                    : $"declared dependencies ({withDeps.Count}): {depSig}");
            }
        }
        catch { }

        if (dups > 0)
        {
            CoopLog.Warn("inventory", () => $"duplicate assemblies across enabled paths: {dups} — 同一模组被两个加载器各加载一次会有双初始化风险（§3.4）");
            for (int i = 0; i < Entries.Length; i++)
                if (Entries[i].Duplicate) CoopLog.Warn("loader.dup", () => $"DUPLICATE '{Entries[i].Id}' -> {Entries[i].Path}");
        }

        if (changed || why == "startup")
        {
            for (int i = 0; i < Entries.Length; i++)
            {
                var e = Entries[i];
                CoopLog.Info("loader.entry", () => Describe(e));
            }
            if (_skippedBundleFiles > 0)
            {
                int skipped = _skippedBundleFiles;
                CoopLog.Info("loader.entry", () => $"... 另一个 {skipped} 个文件属于已知插件包（桥运行时等），未逐条列出");
            }
        }

        if (changed) Raise();
    }

    // ---------------- 内存清单 ----------------

    /// <summary>
    /// 列表顺序 = 统一加载顺序（T8）：<see cref="ModEntryInfo.Order"/> 小的在前；
    /// 不在顺序里的（Order = 0）排最后，同分时按 Id 稳定兜底。
    /// </summary>
    private static int CompareByLoadOrder(ModEntryInfo a, ModEntryInfo b)
    {
        int oa = a != null && a.Order > 0 ? a.Order : int.MaxValue;
        int ob = b != null && b.Order > 0 ? b.Order : int.MaxValue;
        if (oa != ob) return oa < ob ? -1 : 1;
        return string.CompareOrdinal(a?.Id ?? "", b?.Id ?? "");
    }

    private static void AddBepInPlugin(Dictionary<string, ModEntryInfo> map, LoaderReflect.PluginLite p)
    {
        string id = IdOfFile(p.Location);
        if (id.Length == 0) id = Clean(p.Name);
        if (id.Length == 0) return;

        var e = GetOrAdd(map, id);
        e.DisplayName = p.Name.Length > 0 ? p.Name : id;
        e.Version = p.Version;
        e.Format = ModAssemblyFormat.BepInExPlugin;
        e.Host = ModLoaderHost.BepInEx;   // BepInEx 插件必然跑在 BepInEx 宿主
        e.ViaBridge = false;              // 与桥无关（桥只负责 MelonLoader 模组）
        e.Loaded = true;
        if (p.Location.Length > 0) e.Path = p.Location;
        if (e.Note == null) e.Note = "";
        // 加载器侧唯一 ID（GUID）：依赖声明用的就是它（见 ModEntryInfo.Guid 的注释）
        if (!string.IsNullOrEmpty(p.Guid)) e.Guid = p.Guid;
        // 依赖声明（BepInDependency）：调度器据此做“依赖前移”（见 ModInitScheduler.Apply ③）
        if (p.DependsOn != null && p.DependsOn.Length > 0) e.DependsOn = p.DependsOn;

        // 配置真值：BepInEx 用 PluginInfo.Metadata.GUID → BepInEx\config\<GUID>.cfg（T6 设置页读它）
        try
        {
            string guid = (p.Guid ?? "").Trim();
            if (guid.Length > 0)
            {
                string cfg = System.IO.Path.Combine(Core.ModMenuPaths.BepInExConfigDir ?? "", guid + ".cfg");
                if (System.IO.File.Exists(cfg)) e.ConfigFile = cfg;
            }
        }
        catch { }
    }

    private static void AddMelon(Dictionary<string, ModEntryInfo> map, LoaderReflect.MelonLite m)
    {
        string id = Clean(m.AssemblyName);
        if (id.Length == 0) id = Clean(m.Name);
        if (id.Length == 0) return;

        var e = GetOrAdd(map, id);
        var info = LoaderDetector.Current;
        e.DisplayName = m.Name.Length > 0 ? m.Name : id;
        e.Version = m.Version;
        e.Author = m.Author;
        e.Format = ModAssemblyFormat.MelonMod;
        e.Host = info.Host;
        // ⚠️ 2026-09-12 修正（实测反馈：模组全被识别成 bridge）：
        //   只有「BepInEx 宿主 + 进程里存在 ML 桥」时，MelonLoader 模组才是**经桥加载**。
        //   原先把**进程级** info.ViaBridge 直接套到每个条目 → 列表内全部条目都标 [桥]。
        e.ViaBridge = info.Host == ModLoaderHost.BepInEx && info.ViaBridge;
        e.Loaded = true;
        e.Priority = m.Priority;
        if (!string.IsNullOrEmpty(m.AssemblyName)) e.Guid = m.AssemblyName;   // MLL 侧依赖声明就是程序集名
        if (m.DependsOn.Length > 0) e.DependsOn = m.DependsOn;
        // 配置推测：MelonLoader 惯例 = UserData\<程序集名>.cfg（存在才认，否则留给 ConfigLocator 猜）
        try
        {
            string cfg = System.IO.Path.Combine(Core.ModMenuPaths.MelonUserDataDir ?? "", m.AssemblyName + ".cfg");
            if (System.IO.File.Exists(cfg)) e.ConfigFile = cfg;
        }
        catch { }
        if (m.IncompatibleWith.Length > 0) e.IncompatibleWith = m.IncompatibleWith;
        if (m.Location.Length > 0) e.Path = m.Location;
        e.Note = m.IsPlugin ? ModMenuLoc.L("NoteMelonPlugin") : "";
    }

    // ---------------- 磁盘清单 ----------------

    private static void ScanDisk(Dictionary<string, ModEntryInfo> map, string dir, LoaderInfo info, bool isBepInExSide)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        List<string> files = new List<string>();
        for (int pass = 0; pass < 2; pass++)
        {
            string pattern = pass == 0 ? "*.dll" : "*.dll.disabled";
            try { files.AddRange(Directory.GetFiles(dir, pattern, SearchOption.AllDirectories)); }
            catch { }
        }

        for (int i = 0; i < files.Count; i++)
        {
            string path = files[i];
            try
            {
                // 已知插件包（如桥自身的运行时，46 个 dll）不逐条进清单，只计数
                if (ModMenuPaths.BridgePluginDir.Length > 0 &&
                    path.StartsWith(ModMenuPaths.BridgePluginDir, StringComparison.OrdinalIgnoreCase))
                {
                    _skippedBundleFiles++;
                    FileCount++;
                    continue;
                }

                bool enabled = !path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                string id = IdOfFile(path);
                if (id.Length == 0) continue;
                FileCount++;

                string version = "";
                try
                {
                    var an = System.Reflection.AssemblyName.GetAssemblyName(path);
                    version = an?.Version?.ToString() ?? "";
                }
                catch { }

                if (map.TryGetValue(id, out var existing))
                {
                    // 已由加载器元数据建立 → 只补充磁盘信息
                    existing.Enabled = enabled;
                    if (existing.Path == null || existing.Path.Length == 0) existing.Path = path;
                    if (existing.Version.Length == 0) existing.Version = version;
                    continue;
                }

                // 格式**读元数据**判定（不加载程序集、也不按目录猜）：有模组基类 = 真模组；没有 = 依赖库/非模组。
                // 修正自实测反馈：“按目录推断格式”不对 —— BepInEx\plugins 里同样放着依赖库。
                var fmt = AssemblyFormatProbe.Probe(path, out _);
                var e = new ModEntryInfo
                {
                    Id = id,
                    DisplayName = id,
                    Version = version,
                    Path = path,
                    Enabled = enabled,
                    Loaded = false,
                    Format = fmt,
                    Host = info.Host,
                    ViaBridge = fmt == ModAssemblyFormat.MelonMod && info.Host == ModLoaderHost.BepInEx && info.ViaBridge,
                };
                e.Note = !enabled
                    ? ModMenuLoc.L("NoteDisabled")
                    : (fmt == ModAssemblyFormat.Unknown ? ModMenuLoc.L("NoteLibraryOrUnloaded") : ModMenuLoc.L("NoteModNotLoaded"));
                if (IsMelonSkipped(id, path) && !isBepInExSide) e.Note += ModMenuLoc.L("NoteMelonSkip");
                map[id] = e;
            }
            catch { }
        }
    }

    /// <summary>MelonLoader 的原生排除语义（只对 MLL 生效，见 §E）。</summary>
    private static bool IsMelonSkipped(string id, string path)
    {
        try
        {
            string name = Path.GetFileName(path) ?? "";
            if (name.StartsWith("~") || name.StartsWith(".")) return true;
            string stem = Path.GetFileNameWithoutExtension(name) ?? "";
            return stem.Equals("Broken", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("Retired", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ---------------- 辅助 ----------------

    private static ModEntryInfo GetOrAdd(Dictionary<string, ModEntryInfo> map, string id)
    {
        if (map.TryGetValue(id, out var e)) return e;
        var info = LoaderDetector.Current;
        e = new ModEntryInfo { Id = id, Host = info.Host, ViaBridge = info.ViaBridge };
        map[id] = e;
        return e;
    }

    private static string IdOfFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try
        {
            string name = Path.GetFileName(path) ?? "";
            if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ".disabled".Length);
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ".dll".Length);
            return Clean(name);
        }
        catch { return ""; }
    }

    private static string Clean(string s) => string.IsNullOrEmpty(s) ? "" : s.Trim();

    private static string Describe(ModEntryInfo e)
    {
        string flags = "";
        if (e.Loaded) flags += " loaded";
        if (!e.Enabled) flags += " disabled";
        if (e.Duplicate) flags += " DUPLICATE";
        if (e.Priority != 0) flags += " pri=" + e.Priority;
        if (e.DependsOn.Length > 0) flags += " deps=[" + string.Join(",", e.DependsOn) + "]";
        if (e.IncompatibleWith.Length > 0) flags += " incompat=[" + string.Join(",", e.IncompatibleWith) + "]";
        string note = string.IsNullOrEmpty(e.Note) ? "" : " — " + e.Note;
        return $"[{e.HostLabel}] {e.DisplayName} {e.Version} | {e.FormatLabel} | id={e.Id}{flags}{note} | {e.Path}";
    }

    private static void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { CoopLog.Warn("loader.changed", () => $"subscriber threw: {ex.Message}"); }
    }
}
