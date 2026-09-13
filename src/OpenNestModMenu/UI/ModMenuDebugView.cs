using System;
using System.Collections.Generic;
using System.Text;
using OpenNestModMenu.API;
using OpenNestModMenu.Core;
using OpenNestModMenu.Loaders;
using OpenNestModMenu.Logging;
using OpenNestModMenu.Registry;
using UnityEngine;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestModMenu.UI;

/// <summary>
/// 诊断面板（T10 + T11）：右栏第三个页签，把"现在到底发生了什么"一次摊开 ——
/// 帧性能（<see cref="FrameProfiler"/>）/ 加载器与路径 / 模组清单 / 注册表与扫描 /
/// 统一顺序（T8）/ **重复加载与双 Harmony 隔离**（T11）/ 输入与指针能力探测 / 日志。
///
/// 设计：
/// - 与设置页同一套行池 + 分页（12 行/页），**可见时每秒重建**（帧率与测量点本来就是一秒一结算）；
/// - **只显示能读到的事实**：读不到就显示 `<无>`，不编造（诊断页最忌讳猜）；
/// - 纯 ASCII 标记（`[ 分组 ]`）——游戏 TMP 字体缺 ▸ 之类的符号，会渲染成方框（已踩）。
/// </summary>
internal static class ModMenuDebugView
{
    private const float RowH = 26f;
    private const float RefreshSec = 1f;

    /// <summary>每页行数（按宿主高度算，铺满右栏内容块；见 `ModMenuSettingsView.FitRows`）。</summary>
    private static int _rowsPerPage = 12;

    private struct Item
    {
        public string Label;
        public string Value;
        public bool Warn;
        public bool Header;
    }

    private sealed class Row
    {
        public TextMeshProUGUI Label;
        public TextMeshProUGUI Value;
    }

    private static readonly List<Row> _rows = new();
    private static readonly List<Item> _items = new();
    private static RectTransform _host;
    private static TextMeshProUGUI _pageText, _footText;
    private static UnityEngine.UI.Button _prev, _next, _now;
    private static bool _built, _visible;
    private static int _page;
    private static float _clock, _nextRefresh, _snapDue = -1f;
    private static double _lastFps, _lastAvg, _lastWorst;

    // ---------------- 构建 ----------------

    public static void Build(RectTransform host)
    {
        if (_built || host == null) return;
        _host = host;
        try
        {
            const float labelW = 120f;
            float valueW = ModMenuTheme.ColRightW - ModMenuTheme.Pad * 2f - labelW;

            _rowsPerPage = ModMenuSettingsView.FitRows(host, RowH, 34f);

            for (int i = 0; i < _rowsPerPage; i++)
            {
                float y = i * RowH;
                var r = new Row();
                r.Label = UiKit.MakeText(host, "", ModMenuTheme.Pad, y + 3f, labelW, ModMenuTheme.LineH,
                    ModMenuTheme.FontLabel, ModMenuTheme.TextDim, TextAlignmentOptions.TopLeft);
                r.Value = UiKit.MakeText(host, "", ModMenuTheme.Pad + labelW, y + 3f, valueW, ModMenuTheme.LineH,
                    ModMenuTheme.FontValue, ModMenuTheme.TextPrimary, TextAlignmentOptions.TopLeft);
                try { r.Label.overflowMode = TextOverflowModes.Ellipsis; r.Value.overflowMode = TextOverflowModes.Ellipsis; } catch { }
                // 行高只有一行：必须关掉自动换行，否则长值（路径）会折到下一行、和下一行叠在一起
                try { r.Label.enableWordWrapping = false; r.Value.enableWordWrapping = false; } catch { }
                _rows.Add(r);
            }

            float fy = ModMenuSettingsView.FooterY(_rowsPerPage, RowH);
            _prev = UiKit.MakeButton(host, "<", ModMenuTheme.Pad, fy, 40f, 22f, Noop, ModMenuTheme.ButtonBg, ModMenuTheme.TextPrimary);
            ModMenuTheme.StyleButton(_prev, ModMenuTheme.ButtonBg);
            _next = UiKit.MakeButton(host, ">", ModMenuTheme.Pad + 44f, fy, 40f, 22f, Noop, ModMenuTheme.ButtonBg, ModMenuTheme.TextPrimary);
            ModMenuTheme.StyleButton(_next, ModMenuTheme.ButtonBg);
            _now = UiKit.MakeButton(host, ModMenuLoc.L("DbgNow"), ModMenuTheme.Pad + 92f, fy, 96f, 22f, Noop,
                ModMenuTheme.ButtonBg, ModMenuTheme.TextPrimary);
            ModMenuTheme.StyleButton(_now, ModMenuTheme.ButtonBg);

            // ⚠️ 分页文案要够宽 + 关自动换行：\"page 1/1\" + \"50 item(s)\" 一旦超宽 TMP 会折成两行（用户实测反馈）
            _pageText = UiKit.MakeText(host, "", ModMenuTheme.Pad + 196f, fy + 2f, 170f, 20f,
                ModMenuTheme.FontSubtitle, ModMenuTheme.TextSecond, TextAlignmentOptions.TopLeft);
            _pageText.enableWordWrapping = false;
            _pageText.overflowMode = TextOverflowModes.Ellipsis;

            _footText = UiKit.MakeText(host, "", ModMenuTheme.Pad + 374f, fy + 2f,
                ModMenuTheme.ColRightW - ModMenuTheme.Pad * 2f - 374f, 20f,
                ModMenuTheme.FontSubtitle, ModMenuTheme.TextSecond, TextAlignmentOptions.TopLeft);
            _footText.enableWordWrapping = false;
            _footText.overflowMode = TextOverflowModes.Ellipsis;

            ModMenuUI.RegisterHot(_prev, ModMenuTheme.ButtonBg, () => Page(-1));
            ModMenuUI.RegisterHot(_next, ModMenuTheme.ButtonBg, () => Page(+1));
            ModMenuUI.RegisterHot(_now, ModMenuTheme.ButtonBg, () => { Collect(); BindPage(); LogSnapshot("manual"); });

            _built = true;
            SetVisible(false);
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.debug", () => "build failed: " + ex.Message);
        }
    }

    public static void SetVisible(bool v)
    {
        if (!_built) return;
        if (_visible == v)
        {
            // 已经是该状态：不重复采集/不重置页码（RenderDetail 会在每次重渲时调到这里；
            // 否则会把用户翻到的页码弹回去、并每秒往日志里刷整屏快照）
            // ⚠️ 但仍然要把 GameObject 状态扳正：Build 后首次 `SetVisible(false)` 若直接早退，
            //    宿主会一直可见 → 设置页/诊断页重叠（实测两个分页行同时出现，已踩）。
            try { _host.gameObject.SetActive(v); } catch { }
            if (v) BindPage();
            return;
        }
        _visible = v;
        try { _host.gameObject.SetActive(v); } catch { }
        if (!v) return;

        Collect();
        _page = 0;
        BindPage();
        _nextRefresh = _clock + RefreshSec;
        _snapDue = _clock + 2.5f;   // 快照延后再打：FrameProfiler 是 1s 窗口结算，刚打开时还是上一窗的空值（实测会读到 0 FPS）
    }

    /// <summary>每帧驱动（只有可见时干活）：每秒重建一次（帧率/测量点本身就是 1s 结算）。</summary>
    public static void Tick(float dt)
    {
        if (!_built || !_visible) return;
        _clock += dt;

        // 打开后一秒打一次整屏快照（此时帧率/测量点已有数据）
        if (_snapDue > 0f && _clock >= _snapDue)
        {
            _snapDue = -1f;
            Collect();
            BindPage();
            LogSnapshot("open");
            return;
        }

        if (_clock < _nextRefresh) return;
        _nextRefresh = _clock + RefreshSec;
        try { Collect(); BindPage(); }
        catch (Exception ex) { CoopLog.Warn("modmenu.debug", () => "refresh failed: " + ex.Message); }
    }

    // ---------------- 分页 ----------------

    private static void Page(int delta)
    {
        int pages = Math.Max(1, (_items.Count + _rowsPerPage - 1) / _rowsPerPage);
        int p = Math.Max(0, Math.Min(pages - 1, _page + delta));
        if (p == _page) return;
        _page = p;
        BindPage();
    }

    private static void BindPage()
    {
        int start = _page * _rowsPerPage;
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            int idx = start + i;
            bool show = idx >= 0 && idx < _items.Count;
            try { r.Label.gameObject.SetActive(show); r.Value.gameObject.SetActive(show); } catch { }
            if (!show) continue;
            var it = _items[idx];
            try
            {
                r.Label.text = it.Header ? "[ " + it.Label + " ]" : it.Label;
                r.Label.color = it.Header ? ModMenuTheme.Accent : ModMenuTheme.TextDim;
                r.Value.text = it.Value ?? "";
                r.Value.color = it.Warn ? ModMenuTheme.Warn : (it.Header ? ModMenuTheme.TextSecond : ModMenuTheme.TextPrimary);
            }
            catch { }
        }

        try
        {
            int pages = Math.Max(1, (_items.Count + _rowsPerPage - 1) / _rowsPerPage);
            _pageText.text = ModMenuLoc.L("SettingsPage", _page + 1, pages) + "   " + ModMenuLoc.L("SettingsCount", _items.Count);
            _prev.gameObject.SetActive(pages > 1);
            _next.gameObject.SetActive(pages > 1);
            _footText.text = ModMenuLoc.L("DbgRefreshed", DateTime.Now.ToString("HH:mm:ss"))
                           + "   " + ModMenuLoc.L(SelfTestMode ? "DbgSelfTest" : "DbgLive");
        }
        catch { }
    }

    // ---------------- 采集（全部来自实测状态） ----------------

    private static void Add(string label, string value) => _items.Add(new Item { Label = label, Value = value ?? "" });
    private static void AddWarn(string label, string value) => _items.Add(new Item { Label = label, Value = value ?? "", Warn = true });
    private static void Head(string label) => _items.Add(new Item { Label = label, Header = true });

    private static string Or(string s) => string.IsNullOrEmpty(s) ? ModMenuLoc.L("DbgNone") : s;

    private static void Collect()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _items.Clear();

        // ---- 帧性能（FrameProfiler：1s 窗口结算）----
        Head(ModMenuLoc.L("DbgFrame"));
        try
        {
            var fp = FrameProfiler.Instance;
            // ⚠️ Vendor 的 FrameProfiler 在每个 1s 窗口刚结算那一刻会把计数器归零（FramesPerSec 会瞬时为 0）
            //    → 这里缓存"上次非零值"，避免面板/快照刚好撞上那一帧而显示 0（已踩）。
            if (fp.FramesPerSec > 0)
            {
                _lastFps = fp.FramesPerSec;
                _lastAvg = fp.AvgFrameMs;
                _lastWorst = fp.WorstFrameMs;
            }
            Add(ModMenuLoc.L("DbgFps"), _lastFps > 0
                ? $"{_lastFps:F1} FPS   {ModMenuLoc.L("DbgAvg")} {_lastAvg:F2} ms   {ModMenuLoc.L("DbgWorst")} {_lastWorst:F1} ms"
                : ModMenuLoc.L("DbgPending"));
            var top = fp.GetTop(4);
            if (top.Count == 0) Add(ModMenuLoc.L("DbgSpots"), ModMenuLoc.L("DbgNone"));
            for (int i = 0; i < top.Count; i++)
                Add("  " + (i + 1) + ". " + top[i].Name, $"{top[i].MsPerSec:F2} ms/s");
        }
        catch (Exception ex) { AddWarn(ModMenuLoc.L("DbgFrame"), ex.Message); }

        // ---- 加载器 / 路径 ----
        Head(ModMenuLoc.L("DbgLoader"));
        try
        {
            var li = LoaderDetector.Current;
            Add(ModMenuLoc.L("DbgHost"), ModMenuDisplay.LoaderHostLabel(li) + "   " + ModMenuLoc.L("DbgInterop") + "=" + li.InteropSource);
            Add(ModMenuLoc.L("DbgBridge"), (li.ViaBridge ? li.BridgeEvidence : ModMenuLoc.L("DbgNone"))
                + (li.BridgeVersion.Length > 0 ? "  v" + li.BridgeVersion : ""));
            Add("BepInEx / MLL", $"{(li.BepInExVersion.Length > 0 ? li.BepInExVersion : ModMenuLoc.L("DbgNone"))}"
                + $" / {(li.MelonLoaderVersion.Length > 0 ? li.MelonLoaderVersion : ModMenuLoc.L("DbgNone"))}");
            AddWarn(ModMenuLoc.L("DbgHarmony"), li.DualHarmony ? ModMenuLoc.L("DbgDualHarmonyYes") : ModMenuLoc.L("DbgNo"));
            if (li.DualHarmony) AddWarn(ModMenuLoc.L("DbgNote"), ModMenuLoc.L("DbgDualHarmonyNote"));
            Add(ModMenuLoc.L("DbgPaths"), "");
            Add("  BepInEx", Or(ModMenuPaths.BepInExRoot));
            Add("  plugins/config", Or(ModMenuPaths.BepInExPluginDir) + "  |  " + Or(ModMenuPaths.BepInExConfigDir));
            Add("  interop", Or(ModMenuPaths.BepInExInteropDir));
            Add("  MLL root", Or(ModMenuPaths.MelonRoot));
            Add("  Mods/UserLibs", Or(ModMenuPaths.MelonModsDir) + "  |  " + Or(ModMenuPaths.MelonUserLibsDir));
            Add("  UserData", Or(ModMenuPaths.MelonUserDataDir));
        }
        catch (Exception ex) { AddWarn(ModMenuLoc.L("DbgLoader"), ex.Message); }

        // ---- 模组清单 ----
        Head(ModMenuLoc.L("DbgInventory"));
        try
        {
            Add(ModMenuLoc.L("DbgEntries"), $"{ModInventory.Entries.Length}   {ModMenuLoc.L("FooterLoaded")}={ModInventory.LoadedCount}"
                + $"   {ModMenuLoc.L("FooterDisabled")}={ModInventory.DisabledCount}   {ModMenuLoc.L("FooterDeps")}={ModInventory.DependencyCount}"
                + $"   files={ModInventory.FileCount}");
            Add(ModMenuLoc.L("DbgRefresh"), $"{ModInventory.RefreshRuns}x   {Or(ModInventory.LastRefreshAt)} '{Or(ModInventory.LastRefreshWhy)}'");
        }
        catch (Exception ex) { AddWarn(ModMenuLoc.L("DbgInventory"), ex.Message); }

        // ---- 注册表 / 扫描 ----
        Head(ModMenuLoc.L("DbgRegistry"));
        try
        {
            Add(ModMenuLoc.L("BuiltProviders"), $"{ModMenuRegistry.Count}   {ModMenuLoc.L("BuiltActive")}={ModMenuRegistry.ActiveCount}"
                + $"   {ModMenuLoc.L("BuiltScanned")}={ModMenuRegistry.ScannedCount}");
            Add(ModMenuLoc.L("BuiltScans"), $"{ModMenuRegistry.ScanRuns}x   {Or(ModMenuRegistry.LastScanAt)} '{Or(ModMenuRegistry.LastScanWhy)}'");
            if (ModMenuRegistry.ScanRuns > 0)
                Add(ModMenuLoc.L("DbgLastScan"), $"assemblies={ModMenuRegistry.LastScanAssemblies} candidates={ModMenuRegistry.LastScanCandidates}"
                    + $" new={ModMenuRegistry.LastScanNew} loadFail={ModMenuRegistry.LastScanLoadFailed} typeFail={ModMenuRegistry.LastScanTypeFailed}"
                    + $"  {ModMenuRegistry.LastScanMs:F0} ms");
        }
        catch (Exception ex) { AddWarn(ModMenuLoc.L("DbgRegistry"), ex.Message); }

        // ---- 统一顺序（T8）----
        Head(ModMenuLoc.L("DbgOrder"));
        try
        {
            Add(ModMenuLoc.L("DbgOrder"), $"{ModInitScheduler.Count}   {ModMenuLoc.L("OrderUser")}={ModOrderTable.ExplicitCount}"
                + $"   {ModMenuLoc.L("DbgFixups")}={ModInitScheduler.LastFixups}   {ModMenuLoc.L("DbgConflicts")}={ModInitScheduler.LastConflicts}");
            Add("  " + ModMenuLoc.L("DbgOrderFile"), Or(ModOrderTable.Path));
            string[] order = ModInitScheduler.EffectiveOrder;
            if (order.Length == 0) Add("  #", ModMenuLoc.L("DbgNone"));
            for (int i = 0; i < order.Length; i++)
                Add("  #" + (i + 1), order[i]);
        }
        catch (Exception ex) { AddWarn(ModMenuLoc.L("DbgOrder"), ex.Message); }

        // ---- 重复加载 + 双 Harmony 隔离（T11）----
        Head(ModMenuLoc.L("DbgDup"));
        try
        {
            var all = ModInventory.Entries;
            int dup = 0;
            var lines = new List<string>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || !all[i].Duplicate) continue;
                dup++;
                if (lines.Count < 6) lines.Add((all[i].Id ?? "?") + "  ->  " + (all[i].Path ?? ""));
            }
            if (dup == 0) Add(ModMenuLoc.L("DbgDup"), ModMenuLoc.L("DbgDupNone"));
            else
            {
                AddWarn(ModMenuLoc.L("DbgDup"), dup + "  " + ModMenuLoc.L("DbgDupRisk"));
                for (int i = 0; i < lines.Count; i++) AddWarn("  " + (i + 1), lines[i]);
            }
            Add("  " + ModMenuLoc.L("DbgHarmonyAsm"), HarmonyAssemblies());
        }
        catch (Exception ex) { AddWarn(ModMenuLoc.L("DbgDup"), ex.Message); }

        // ---- 能力探测（输入 / 指针 / 覆盖 UI）----
        Head(ModMenuLoc.L("DbgCapability"));
        try
        {
            Add(ModMenuLoc.L("DbgInput"), UiInputGuard.CapabilitySummary());
            Add(ModMenuLoc.L("DbgPointer"), string.IsNullOrEmpty(PointerPosition.LastSource)
                ? ModMenuLoc.L("DbgNone") : PointerPosition.LastSource);
            Add(ModMenuLoc.L("DbgNativeUi"), ModMenuLoc.L("DbgNativeUiOff"));
            Add("API / " + ModMenuLoc.L("BuiltLang"), $"{ModMenuHost.ApiVersion} / {ModMenuLoc.Current}");
            Add(ModMenuLoc.L("BuiltShape"), ModMenuPaths.Shape.ToString());
        }
        catch (Exception ex) { AddWarn(ModMenuLoc.L("DbgCapability"), ex.Message); }

        // ---- 日志 / 配置层 ----
        Head(ModMenuLoc.L("DbgLogs"));
        try
        {
            Add(ModMenuLoc.L("DbgLogLevel"), CoopLog.Level.ToString());
            Add(ModMenuLoc.L("DbgFileLog"), ModLog.Enabled ? ModMenuLoc.L("DbgOn") + "  " + ModMenuPaths.LogDir : ModMenuLoc.L("DbgOff"));
            Add(ModMenuLoc.L("BuiltConfig"), Or(ModMenuPaths.ConfigFile));
            Add(ModMenuLoc.L("DbgConfigMap"), Config.ConfigMap.Describe());
            Add(ModMenuLoc.L("BuiltLangFile"), Or(ModMenuLoc.FilePath));
        }
        catch (Exception ex) { AddWarn(ModMenuLoc.L("DbgLogs"), ex.Message); }

        try { FrameProfiler.Instance.AddMs("debug.collect", sw.Elapsed.TotalMilliseconds); } catch { }
    }

    /// <summary>
    /// 进程里已加载的 Harmony 程序集（双 Harmony 判定的直接证据：名字 + 版本）。
    /// ⚠️ 必须过滤 HarmonyX 为每个被 patch 的方法生成的 `HarmonyDTFAssemblyN` 工厂程序集（实测能到 85 个）→ 否则这一行全是噪声。
    /// </summary>
    private static string HarmonyAssemblies()
    {
        try
        {
            var sb = new StringBuilder();
            int shown = 0, skipped = 0;
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                string n;
                try { n = asms[i].GetName().Name ?? ""; } catch { continue; }
                if (n.IndexOf("Harmony", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (n.StartsWith("HarmonyDTFAssembly", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                if (shown >= 6) { skipped++; continue; }
                if (sb.Length > 0) sb.Append(" | ");
                try { sb.Append(n).Append(" v").Append(asms[i].GetName().Version); }
                catch { sb.Append(n); }
                shown++;
            }
            if (skipped > 0) sb.Append("  (+ ").Append(skipped).Append(" DTF)");
            return sb.Length > 0 ? sb.ToString() : ModMenuLoc.L("DbgNone");
        }
        catch (Exception ex) { return "err:" + ex.Message; }
    }

    /// <summary>打开/手动刷新时把整屏快照写进日志（离线排查用；诊断页内容不必靠肉眼看）。</summary>
    private static void LogSnapshot(string why)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("debug snapshot ('").Append(why).Append("'): items=").Append(_items.Count);
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                sb.Append("\n  ").Append(it.Header ? "[ " + it.Label + " ]" : it.Label).Append(": ").Append(it.Value);
            }
            CoopLog.Info("modmenu.debug", () => sb.ToString());
        }
        catch { }
    }

    /// <summary>自测开关是否在跑（只为在页脚标注"这是自动化会话"）。</summary>
    internal static bool SelfTestMode;

    private static void Noop() { }
}
