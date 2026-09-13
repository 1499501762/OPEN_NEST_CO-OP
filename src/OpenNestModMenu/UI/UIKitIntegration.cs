using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenNestModMenu.API;
using OpenNestModMenu.Config;
using OpenNestModMenu.Core;
using OpenNestModMenu.Loaders;
using OpenNestModMenu.Registry;

namespace OpenNestModMenu.UI;

/// <summary>
/// 与 <b>OpenNestUIKit</b> 的集成（**软依赖**，2026-09-13）。
///
/// 环境里有 UIKit → **菜单改由 UIKit 渲染**（原生 ESC 入口也由 UIKit 注入）；没有 → 原样走自带覆盖 UI。
///
/// 布局对齐**原界面**（用户：“这两个模组接入改来的菜单层层嵌套的显示效果太差了，不如原来两个模组的布局方式”）：
/// 原界面是**一个窗口**——左边模组列表（带筛选 chip）+ 右边详情（页签 详情/设置/诊断），
/// 所以 UIKit 版同样**只有一页**，用两组页签把原来的“两栏 + 页签”映射成竖向的一列：
///   ① 筛选页签（全部 / 模组 / 依赖 / 已禁用）—— 对应原来的四个 chip；
///   ② 模组列表（点一行 = 选中，原来也是点行选中）；
///   ③ 选中项的页签（详情 / 设置 / 诊断）—— 对应右栏那三个页签。
/// 不再“每个模组一页 + 再点进去”，不会层层嵌套。
///
/// 为什么用「反射探测 + NoInlining 隔离」：UIKit 是可选依赖（契约 dll 只随它一起装），
/// 直接触碰契约类型会在 JIT 阶段抛 <c>TypeLoadException</c> → 本模组起不来。
/// </summary>
internal static class UIKitIntegration
{
    /// <summary>本模组在 UIKit 里注册的 provider Id。</summary>
    public const string ProviderId = "open-nest-mod-menu";

    /// <summary>环境里有 UIKit（宿主程序集已加载）。</summary>
    public static bool Present { get; private set; }

    /// <summary>已把菜单注册进 UIKit。</summary>
    public static bool Connected { get; private set; }

    /// <summary>UIKit 宿主已就绪。</summary>
    public static bool HostReady { get; private set; }

    private static bool _absent;
    private static int _attempts;

    /// <summary>诊断串。</summary>
    public static string Describe()
        => $"present={Present} connected={Connected} hostReady={HostReady} attempts={_attempts}";

    /// <summary>每帧驱动（<see cref="Core.ModMenuBehaviour"/> 调用）：消掉“加载顺序”这个变量。</summary>
    public static void Tick()
    {
        if (!Connected && !_absent)
        {
            if (_attempts >= 4) _absent = true;
            else { _attempts++; ProbeAndConnect(); }
        }
        FlushPendingRefresh();
    }

    private static void ProbeAndConnect()
    {
        Present = HostAssemblyLoaded();
        if (!Present)
        {
            if (_attempts >= 4)
            {
                _absent = true;
                CoopLog.Info("modmenu.uikit", () => "环境里没有 OpenNestUIKit → 沿用本模组自带界面");
            }
            return;
        }
        TryConnect();   // 触碰契约类型（单独方法，NoInlining）
    }

    /// <summary>宿主程序集名（**环境里有 UIKit 的标记**）。
    /// ⚠️ 双端名字不同：BepInEx 端 = `OpenNestUIKit`；MelonLoader 端 = `OpenNestUIKit.MelonMod`。</summary>
    private static bool IsHostAssemblyName(string n)
        => string.Equals(n, "OpenNestUIKit", StringComparison.OrdinalIgnoreCase)
        || string.Equals(n, "OpenNestUIKit.MelonMod", StringComparison.OrdinalIgnoreCase);

    /// <summary>按程序集名扫已加载程序集（不加载任何东西、不触碰任何类型）。</summary>
    private static bool HostAssemblyLoaded()
    {
        try
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                string n = "";
                try { n = asms[i].GetName().Name; } catch { continue; }
                if (IsHostAssemblyName(n)) return true;
            }
        }
        catch { }
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TryConnect()
    {
        try
        {
            OpenNestUIKit.API.UiKitHost.Register(new ModMenuUiKitProvider());
            OpenNestUIKit.API.UiKitHost.Changed += OnHostChanged;
            // 数据层变了（清单重扫 / 运行时状态）→ 菜单跟着刷新。
            // ⚠ 2026-09-13（用户：“启用禁用之后按钮和模组状态没有刷新，上移下移也一样”）：
            //   以前只在点击回调里立即刷一次 —— 而清单/注册表的重扫是**延迟 0.1s 排队的**，
            //   刷的时候数据还没变，重扫完成后又没人通知界面 ⇒ 界面永远停旧值。
            ModInventory.Changed += OnDataChanged;
            ModRuntimeState.Changed += OnDataChanged;
            Connected = true;
            HostReady = OpenNestUIKit.API.UiKitHost.IsHostAvailable;
            CoopLog.Info("modmenu.uikit", () => $"已接入 UIKit：provider='{ProviderId}'，菜单改由 UIKit 渲染（一页 + 页签）、原生 ESC 入口由 UIKit 注入（{Describe()}）");
        }
        catch (Exception ex)
        {
            Present = false;
            _absent = true;
            CoopLog.Warn("modmenu.uikit", () => "接入 UIKit 失败 → 回退自带界面: " + ex.Message);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OnHostChanged()
    {
        try { HostReady = OpenNestUIKit.API.UiKitHost.IsHostAvailable; } catch { }
    }

    /// <summary>宿主功能键：菜单是否由 UIKit 显示。</summary>
    public static bool MenuIsUIKit
    {
        get
        {
            if (!Connected) return false;
            try { return OpenNestUIKit.API.UiKitHost.CanControlMenu; } catch { return false; }
        }
    }

    /// <summary>让 UIKit 打开/关闭本模组的菜单页；UIKit 不可用 → false（调用方走自带界面）。</summary>
    public static bool TryToggleInUIKit()
    {
        if (!Connected) return false;
        try { return ToggleInUIKit(); } catch { return false; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ToggleInUIKit()
    {
        if (!OpenNestUIKit.API.UiKitHost.CanControlMenu) return false;
        OpenNestUIKit.API.UiKitHost.ToggleMenu("provider:" + ProviderId);
        return true;
    }

    /// <summary>菜单内容变化（模组清单/设置值）→ 让 UIKit 原地重建当前页。</summary>
    public static void Invalidate()
    {
        if (!Connected) return;
        try { RefreshInUIKit(); } catch { }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RefreshInUIKit()
    {
        OpenNestUIKit.API.UiKitHost.Refresh();
    }

    private static void RequestRefresh()
    {
        if (!Connected) return;
        // **不在点击回调里重建页面**（用户 2026-09-13 的“点了不刷新”之一）：
        // ① 指针路由还在处理这次点击，页面/行被销毁会踩到自己；
        // ② 数据层的重扫是 0.1s 后跑的，等它跑完再刷才能真正看到新状态。
        _refreshPending = true;
        if (_refreshAt <= 0f) _refreshAt = UnityEngine.Time.unscaledTime + RefreshDelaySec;
    }

    /// <summary>数据层变化（清单/运行时状态）→ 与用户操作同一条延迟刷新通道。</summary>
    private static void OnDataChanged() => RequestRefresh();

    /// <summary>
    /// 测试钩子（`-onnmm-selftest-tab=&lt;details|settings|debug&gt;:&lt;Id 匹配串&gt;` 用）：
    /// 把 UIKit 菜单定位到匹配条目的指定页签。参数是<b>子串匹配</b>（大小写不敏感）。
    /// </summary>
    public static void SelfTestSelect(string idMatch, int tab)
    {
        if (!Connected) return;
        try { ModMenuUiKitProvider.SelfTestSelect(idMatch, tab); } catch { }
    }

    /// <summary>到点就刷一次（节流；打字中不刷，否则会把用户草稿冲掉）。</summary>
    private static void FlushPendingRefresh()
    {
        if (!_refreshPending || !Connected) return;
        float now = UnityEngine.Time.unscaledTime;
        if (now < _refreshAt) return;
        if (now - _lastRefreshAt < 0.12f) { _refreshAt = now + 0.05f; return; }   // 节流：一帧多次只刷一次
        try { if (OpenNestUIKit.API.UiKitHost.IsTextInputFocused) { _refreshAt = now + 0.3f; return; } } catch { }
        _refreshPending = false;
        _refreshAt = 0f;
        _lastRefreshAt = now;
        try { RefreshInUIKit(); } catch { }
    }

    /// <summary>用户操作后等多久才重建（等数据层的延迟重扫先跑完）。</summary>
    private const float RefreshDelaySec = 0.18f;
    private static bool _refreshPending;
    private static float _refreshAt;
    private static float _lastRefreshAt;

    // ==================================================================================
    //  provider：**一页 + 两组页签**（对齐原界面：筛选 chip / 列表 / 详情三页签）
    // ==================================================================================

    private sealed class ModMenuUiKitProvider : OpenNestUIKit.API.UiKitProviderBase, OpenNestUIKit.API.IUiKitNativeEntry
    {
        public override string Id => ProviderId;
        public override string DisplayName => ModMenuInfo.Name;
        public override string Version => ModMenuInfo.Version;
        public override string Author => ModMenuInfo.Author;

        // ---- 原生 ESC 菜单里的那一格（由 UIKit 注入；一行多列的其中一格） ----
        public bool ShowInNativeMenu => true;
        public int NativeOrder => 60;
        public string NativeTitle => ModMenuInfo.Name;
        public string NativeTitleEn => "Mod Menu";

        // ---- 界面状态（provider 单例：筛选 / 选中项 / 右栏页签） ----
        private static int _filter;            // 0=全部 1=模组 2=依赖 3=已禁用（对应原来的四个 chip）
        private static string _selectedId = "";
        private static int _tab;               // 0=详情 1=设置 2=诊断（对应右栏三个页签）

        public override void BuildMenu(OpenNestUIKit.API.IUiMenuTree menu)
        {
            // ★ 布局照搬原模组（`ModMenuTheme` + `ModMenuUI`）：
            //   1180×700 窗口，**左栏 440**（筛选 chip + 模组列表 + 分页信息）、**右栏 700**（详情/设置/诊断页签）。
            //   原来 UIKit 版把一切都摊成一列（列表 13 行 + 详情挤在一起）→ 用户反馈“布局逻辑还是不如原来的”。
            var page = menu.Root;
            page.Size(1180f, 700f);
            var entries = SafeEntries();

            if (entries.Length == 0)
            {
                page.Label(T("（还没发现任何模组）", "(no mods discovered yet)"));
                return;
            }

            // 过滤后的可见条目（左栏列表与选中索引都用它 —— `SelectableList` 只吃字符串数组 + 索引）
            var shown = new List<ModEntryInfo>();
            var shownLabels = new List<string>();
            var shownMetas = new List<string>();
            for (int i = 0; i < entries.Length; i++)
            {
                var e = entries[i];
                if (e == null || string.IsNullOrEmpty(e.Id) || !Passes(e)) continue;
                shown.Add(e);
                shownLabels.Add(EntryLine(e, _selectedId));
                shownMetas.Add(EntryMeta(e));
            }
            int shownCount = shown.Count;

            // 没选中时默认选中第一条：左栏首行会被画成选中态（见下面 SelectableList），右栏也就不会空着
            if (string.IsNullOrEmpty(_selectedId) && shown.Count > 0) _selectedId = shown[0].Id ?? "";
            int selIndex = 0;
            for (int i = 0; i < shown.Count; i++)
                if (string.Equals(shown[i].Id, _selectedId, StringComparison.OrdinalIgnoreCase)) { selIndex = i; break; }

            // 选中项（右栏内容要用）
            var sel = Find(entries, _selectedId);

            page.Columns(440f, left =>
            {
                // ① 筛选 chip（原来的四个 chip：全部/模组/依赖/已禁用）
                left.Tabs("mm.filter", new[] { L("ChipAll"), L("ChipMods"), L("ChipDeps"), L("ChipOff") }, Mathf(0, 3, _filter),
                          i => { _filter = i; RequestRefresh(); });
                left.Button(string.Format(T("共 {0} 个 · {1}", "{0} mods · {1}"), entries.Length, FilterName()), L("Rescan"), () =>
                {
                    try { ModInventory.RequestRefresh("uikit"); ModMenuRegistry.RequestRescan("uikit"); }
                    catch (Exception ex) { Log("rescan", ex); }
                    RequestRefresh();
                });
                left.Separator();

                // ② 模组列表：**可选中列表**（用户：“ModMenu 的左侧不需要额外的选中按钮了”）——
                //    以前是 `List` + 每行右侧一个「选中」按钮；现在**点行本身即选中**，
                //    选中行由宿主自己画高亮（`UiNavRow` 选中态 + 左侧强调条）。
                //    ⚠ 宿主在回调后会自己 `ReloadCurrentPage()` 重画高亮 ⇒ 第三方**不要再** RequestRefresh（会白重建一次）。
                // ⚠ 高度传 **-1f = 吃掉剩余高度**（两栏页是“桌面式”：外层不滚动，左右栏各自滚）
                //   —— 以前写死 470，内容一超过外层视口就出滚动条（用户：“外面那个块还是被撑大了”）。
                left.SelectableList("mm.list", -1f, shownLabels, shownMetas, selIndex, idx =>
                {
                    if (idx < 0 || idx >= shown.Count) return;
                    _selectedId = shown[idx].Id ?? "";
                    _tab = 0;
                });
                left.Label(string.Format(T("显示 {0} / {1} 个", "showing {0} / {1}"), shownCount, entries.Length));
            },
            right =>
            {
                // ③ 选中项（原来的右栏：名称/版本 → 三个页签 → 内容）
                if (sel == null)
                {
                    right.Header(L("TabDetails"));
                    right.Label(T("（先在左边点一个模组选中）", "(pick a mod on the left first)"));
                    return;
                }
                right.Header(Display(sel));
                // ⚠ 2026-09-13（用户：“有语言键缺失残留”）：这里原来直接读条目上的 `HostLabel/FormatLabel` ——
                //   那是**扫描时**写死的文本（中文） ⇒ 切英文后右栏标题行会冒出一截中文。
                //   展示层统一走 `ModMenuDisplay.*`（本地化，与自带界面同源）。
                right.Label($"{sel.Version} · {ModMenuDisplay.HostLabel(sel)} · {ModMenuDisplay.FormatLabel(sel)}"
                            + (sel.Managed ? T(" · 受管", " · managed") : "")
                            + (sel.Enabled ? "" : T(" · 已禁用(磁盘)", " · disabled (disk)")));
                right.Separator();
                int tab = Mathf(0, 2, _tab);
                right.Tabs("mm.tabs", new[] { L("TabDetails"), L("TabSettings"), L("TabDebug") }, tab, i => { _tab = i; RequestRefresh(); });
                // ⚠ 右栏必须是**固定最大高度的滚动区**（用户 2026-09-13：“右侧块没有固定最大尺寸，块把外面的块高度撑开了”）：
                //   诊断页有几十行，直接摊进右栏子流会把 `Columns` 的行高顶大 → 整页/窗口跟着被撑开。
                //   包进内嵌 `List`（固定 470 高，与左栏一致）→ 内容再多也只在块内滚，页面高度不被内容绑架。
                right.List("mm.right", -1f, body =>
                {
                    // 诊断页顶部/底部各一个“复制全部”（点击时 body.Rows 已填满）：把整页诊断拼成纯文本进剪贴板。
                    if (tab == 2) body.Action(T("诊断信息（可整页复制）", "diagnostics (copy the whole page)"), CopyAll, () => CopyDiag(body, sel));
                    if (tab == 0) BuildDetail(body, sel);
                    else if (tab == 1) BuildSettings(body, sel);
                    else
                    {
                        BuildDiag(body, sel);
                        body.Action(T("（以上为该模组的完整诊断）", "(that is the full diagnostic for this mod)"), CopyAll, () => CopyDiag(body, sel));
                    }
                });
            });

            // ④ 环境摘要：**只在调试模式显示**（用户 2026-09-13：“UI 右下角的 Debug 信息只在调试模式启用”）。
            //   开关在「本模组自己的设置页」里（`OpenNestModMenu.cfg` 的 Debug 键，见 Core/ModMenuPrefs.cs）。
            //   以前无条件显示，而且一长串在底栏里折成三行、溢出面板（实测截图）。
            SetFooter(DebugMode ? $"{L("DetailHost")}：{ModLoaderHostLabel()} · UIKit：{UiKitSummary()}"
                      + $"｜{L("FooterProviders")} {ModMenuRegistry.Count} ({L("BuiltActive")} {ModMenuRegistry.ActiveCount} / {L("BuiltScanned")} {ModMenuRegistry.ScannedCount})"
                      + $" · {L("DbgLastScan")} {ModMenuRegistry.LastScanWhy} @ {ModMenuRegistry.LastScanAt}"
                      : "");
        }

        /// <summary>底栏常驻信息（契约 `UiKitHost.SetFooter`；没装 UIKit 时静默忽略）。</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SetFooter(string text)
        {
            try { OpenNestUIKit.API.UiKitHost.SetFooter(text); } catch { }
        }

        /// <summary>
        /// 测试钩子：选中 Id 含 <paramref name="idMatch"/> 的条目并切到 <paramref name="tab"/> 页签。
        /// 供 `-onnmm-selftest-tab=settings:uikit.test` 这类命令行验收用（装了 UIKit 时菜单是 UIKit 渲染的，
        /// 自带界面的那套选中/切页签在这里不生效）。返回命中的 Id（空 = 没命中）。
        /// </summary>
        internal static void SelfTestSelect(string idMatch, int tab)
        {
            try
            {
                var all = SafeEntries();
                string hit = null;
                for (int i = 0; i < all.Length; i++)
                {
                    var e = all[i];
                    if (e == null || string.IsNullOrEmpty(e.Id)) continue;
                    if (idMatch != null && idMatch.Length > 0
                        && e.Id.IndexOf(idMatch, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    hit = e.Id;
                    break;
                }
                _filter = 0;                                  // 全部（保证选中项在列表里可见）
                if (!string.IsNullOrEmpty(hit)) _selectedId = hit;
                _tab = Mathf(0, 2, tab);
                CoopLog.Info("modmenu.uikit", () => $"selftest-select: match='{idMatch}' hit='{hit}' tab={_tab}");
                // 页面已建好则**立刻**重建（自检里没有下一次点击来触发刷新）
                try { if (OpenNestUIKit.API.UiKitHost.IsMenuOpen) OpenNestUIKit.API.UiKitHost.Refresh(); } catch { }
            }
            catch (Exception ex) { CoopLog.Warn("modmenu.uikit", () => "selftest-select failed: " + ex.Message); }
        }

        // ---------------- 文案（语言键 + 内联双语） ----------------

        /// <summary>语言键（简写）：这些键在 `LocDefaults` 里，用户能在 `OpenNestModMenu.lang.ini` 改。</summary>
        private static string L(string key) => ModMenuLoc.L(key);

        /// <summary>
        /// **内联双语文案**（2026-09-13 用户：“语言键不全”）。
        ///
        /// UIKit 渲染的这页以前有几十条硬编码中文 ⇒ 游戏切英文后这页还是中文。
        /// 拆两类：**有现成语言键的走 <see cref="L"/>**（可编辑、可翻译），
        /// 其余用 UIKit 契约的 `UiKitLang.T(zh, en)` —— 它读的正是 UIKit 当前语言（由宿主按游戏语言推），
        /// 与本库自己渲染的页面（Coop / 演示页）**同源**，不会出现两套语言判断不一致。
        /// </summary>
        private static string T(string zh, string en)
        {
            try { return OpenNestUIKit.API.UiKitLang.T(zh, en); }
            catch { return ModMenuLoc.IsChinese ? zh : en; }   // 契约不可用（旧版 UIKit）→ 按本模组语言兜底
        }

        /// <summary>“复制全部”按钮文案（多处用）。</summary>
        private static string CopyAll => T("复制全部", "Copy all");

        /// <summary>
        /// 调试模式：底栏显示环境摘要 / 诊断日志全开。三个来源任一为真即算开：
        /// ① 用户设置（`OpenNestModMenu.cfg` 的 `Debug`，见 <see cref="Core.ModMenuPrefs"/>）；
        /// ② 日志等级已经是 Debug（命令行开诊断，或调试版构建）；
        /// ③ 自检模式（`-onnmm-selftest-tab`）。
        /// </summary>
        private static bool DebugMode
        {
            get
            {
                try { if (Core.ModMenuPrefs.Debug) return true; } catch { }
                try { if (CoopLog.Level <= Logging.LogLevel.Debug) return true; } catch { }
                try { if (ModMenuDebugView.SelfTestMode) return true; } catch { }
                return false;
            }
        }

        // ---------------- 详情页签（对应原右栏“详情”） ----------------

        private static void BuildDetail(OpenNestUIKit.API.UiPageDef page, ModEntryInfo e)
        {
            // ⚠ 名称/版本**不在这里重复**：右栏上方已经有了（用户截图里出现过两遍）。
            // 属性表走 **Info（键值两列）**：标签与值各自对齐成一列，长路径也不会把标签挤走
            page.Info(L("DetailState"), (e.Loaded ? L("StateLoaded") : L("StateNotLoaded"))
                       + (e.Managed ? T(" · 受管（有声明式页面）", " · managed (has a page)") : T(" · 未受管", " · unmanaged"))
                       + (e.Duplicate ? T(" · ⚠ 同名程序集多路径（双初始化风险）", " · WARN duplicate assembly paths (double-init risk)") : ""));
            if (!string.IsNullOrEmpty(e.Path)) page.Info(L("DetailPath"), e.Path);
            string cfg = SafeConfigPath(e);
            page.Info(L("DetailConfig"), string.IsNullOrEmpty(cfg) ? L("DetailNone") : cfg);
            if (e.DependsOn != null && e.DependsOn.Length > 0) page.Info(L("DetailDeps"), string.Join(", ", e.DependsOn));
            if (e.IncompatibleWith != null && e.IncompatibleWith.Length > 0) page.Info(L("DetailIncompat"), string.Join(", ", e.IncompatibleWith));
            if (!string.IsNullOrEmpty(e.Note)) page.Info(L("DetailNote"), e.Note);

            page.Header(T("操作", "Actions"));
            page.Button(e.Enabled ? T("从磁盘禁用（改扩展名，重启生效）", "Disable on disk (rename, restart to apply)")
                                  : T("从磁盘启用（改扩展名，重启生效）", "Enable on disk (rename, restart to apply)"),
                        e.Enabled ? L("BtnDisable") : L("BtnEnable"), () =>
                        {
                            try
                            {
                                bool ok = ModFileState.TryToggle(e, out string msg);
                                Toast(ok ? T("已切换", "toggled") : T("切换失败", "toggle failed"), msg);
                                if (ok) { ModInventory.RequestRefresh("uikit"); ModMenuRegistry.RequestRescan("uikit"); }
                            }
                            catch (Exception ex) { Log("toggle file", ex); }
                            RequestRefresh();
                        });
            page.Button(T("顺序上移", "Order: move up"), L("OrderUp"), () => MoveOrder(e, -1));
            page.Button(T("顺序下移", "Order: move down"), L("OrderDown"), () => MoveOrder(e, +1));

            bool managed = false, runtimeDisabled = false, canRuntimeToggle = false;
            try
            {
                if (ModMenuRegistry.TryGet(e.Id, out var rec) && rec?.Provider != null)
                {
                    managed = true;
                    runtimeDisabled = rec.RuntimeDisabled;
                    canRuntimeToggle = rec.Provider.CanToggleAtRuntime;
                }
            }
            catch { }
            if (managed)
            {
                if (canRuntimeToggle)
                {
                    page.Button(runtimeDisabled ? T("运行时启用（模组自己实现的可逆启停）", "Resume at runtime (mod-implemented)")
                                                : T("运行时停用（模组自己实现的可逆启停）", "Stop at runtime (mod-implemented)"),
                                runtimeDisabled ? L("BtnEnable") : L("BtnDisable"), () =>
                                {
                                    try
                                    {
                                        bool ok = ModMenuRegistry.SetRuntimeDisabled(e.Id, !runtimeDisabled, out string msg);
                                        Toast(ok ? T("运行时状态已切换", "runtime state toggled") : T("运行时切换失败", "runtime toggle failed"), msg);
                                    }
                                    catch (Exception ex) { Log("runtime toggle", ex); }
                                    RequestRefresh();
                                });
                }
                else
                {
                    page.Label(T("运行时启停：该模组未声明支持（只能改扩展名 + 重启）",
                                 "runtime toggling: not supported by this mod (rename + restart only)"));
                }
                page.Button(T("恢复该模组的默认设置", "Reset this mod to its defaults"), T("恢复默认", "Reset"), () =>
                {
                    try
                    {
                        if (ModMenuRegistry.TryGet(e.Id, out var r2) && r2?.Provider != null) r2.Provider.ResetToDefaults();
                        Toast(T("已请求恢复默认", "reset requested"), e.Id);
                    }
                    catch (Exception ex) { Log("reset defaults", ex); }
                    RequestRefresh();
                });
                page.Button(T("重新跑受管初始化队列", "Re-run the managed init queue"), T("重跑", "Re-run"), () =>
                {
                    try { ModInitScheduler.RequestInit("uikit-reinit", 0.2f); } catch (Exception ex) { Log("reinit", ex); }
                    RequestRefresh();
                });
            }
            else
            {
                page.Label(T("受管功能：该模组没有声明式注册（没有设置页/运行时启停）",
                             "managed features: this mod has no declarative registration (no settings page / runtime toggle)"));
            }
        }

        // ---------------- 设置页签（对应原右栏“设置”；内容与自带设置页同源） ----------------

        private static void BuildSettings(OpenNestUIKit.API.UiPageDef page, ModEntryInfo e)
        {
            List<UiSetting> rows;
            try { rows = ModMenuSettingsSource.Rows(e); }
            catch (Exception ex) { page.Label(T("读取设置失败：", "failed to read settings: ") + ex.Message); return; }
            if (rows.Count == 0)
            {
                page.Label(L("SettingsNone"));
                return;
            }
            for (int i = 0; i < rows.Count; i++) AddRow(page, rows[i]);
        }

        // ---------------- 诊断页签（对应原右栏“诊断”；内容与 `ModMenuDebugView` 对齐） ----------------
        //
        // ★ 2026-09-13：原来只列了 4~5 行（用户：“诊断页的功能也不全”）。
        //   现在把原生诊断面板 `ModMenuDebugView.Collect()` 的**全部分组**搬过来：
        //   帧性能（含 top 热点） / 加载器与路径 / 模组清单 / 注册表与扫描 / 统一顺序 /
        //   重复加载与 Harmony 程序集 / 能力探测（输入·指针） / 日志与配置层。
        //   警告项用富文本红色（原生面板是黄/红字）；所有值都“读不到就显示 <无>”，不编造。

        private static void BuildDiag(OpenNestUIKit.API.UiPageDef page, ModEntryInfo e)
        {
            string None = T("（无）", "(none)");
            string warn = "<color=#e06c6c>";
            string ok = "<color=#7fd08a>";
            string dim = "<color=#6c7787>";

            // ---- 该模组 ----
            page.Header(T("该模组", "This mod"));
            page.Label($"{Display(e)}");
            page.Label($"{L("DetailFormat")} {ModMenuDisplay.FormatLabel(e)} · {L("DetailHost")} {ModMenuDisplay.HostLabel(e)} · {T("路径形态", "path kind")} {ModMenuDisplay.PillLabel(e)}"
                       + (e.ViaBridge ? T("（经 ML 桥）", " (via MLL bridge)") : ""));
            page.Label($"{L("DetailState")}: {(e.Loaded ? L("StateLoaded") : L("StateNotLoaded"))}"
                       + $" · {(e.Enabled ? T("磁盘启用", "disk enabled") : T("磁盘禁用", "disk disabled"))}"
                       + $" · {T("受管", "managed")} {(e.Managed ? T("是", "yes") : T("否", "no"))} · {L("DetailPriority")} {e.Priority}");
            if (e.Duplicate) page.Label($"{warn}⚠ {T("同名程序集多路径（双初始化风险）", "duplicate assembly paths (double-init risk)")}</color>");
            if (!string.IsNullOrEmpty(e.Path)) page.Info(L("DetailPath"), e.Path);
            if (!string.IsNullOrEmpty(e.Version)) page.Info(T("版本", "Version"), e.Version);
            if (!string.IsNullOrEmpty(e.Author)) page.Info(L("DetailAuthor"), e.Author);
            if (e.DependsOn != null && e.DependsOn.Length > 0) page.Info(L("DetailDeps"), string.Join(", ", e.DependsOn));
            if (e.IncompatibleWith != null && e.IncompatibleWith.Length > 0) page.Info(L("DetailIncompat"), string.Join(", ", e.IncompatibleWith));
            if (!string.IsNullOrEmpty(e.Note)) page.Info(L("DetailNote"), e.Note);
            try
            {
                if (ModMenuRegistry.TryGet(e.Id, out var rec) && rec != null)
                {
                    page.Label($"{T("注册", "registered")}: {(rec.FromScan ? T("被动扫描发现", "by passive scan") : T("主动注册", "explicit registration"))}"
                               + $" · {T("内置", "built-in")} {(rec.BuiltIn ? T("是", "yes") : T("否", "no"))} · {L("DetailOrder")} #{rec.Order}"
                               + $" · {T("运行时停用", "runtime stopped")} {(rec.RuntimeDisabled ? T("是", "yes") : T("否", "no"))}"
                               + $" · {T("已初始化", "initialized")} {(rec.InitApplied ? T("是", "yes") : T("否", "no"))}");
                    if (!string.IsNullOrEmpty(rec.Error)) page.Label($"{warn}{T("注册错误", "registration error")}: {rec.Error}</color>");
                }
                else page.Label($"{dim}{T("注册表里没有该模组（未声明式注册）", "not in the registry (no declarative registration)")}</color>");
            }
            catch (Exception ex) { page.Label($"{warn}{T("读取注册记录失败", "failed to read registry record")}: {ex.Message}</color>"); }

            // ---- 帧性能 ----
            page.Header(T("帧性能", "Frame performance"));
            try
            {
                var fps = SafeD(() => Diagnostics.FrameProfiler.Instance.FramesPerSec);
                var avg = SafeD(() => Diagnostics.FrameProfiler.Instance.AvgFrameMs);
                var worst = SafeD(() => Diagnostics.FrameProfiler.Instance.WorstFrameMs);
                page.Label(fps > 0 ? $"{ok}{fps:0.0} FPS</color> · {T("平均", "avg")} {avg:0.00} ms · {T("最差", "worst")} {worst:0.0} ms"
                                   : $"{dim}{T("（本秒还没结算）", "(not settled this second)")}</color>");
                var top = SafeTop(4);
                if (top.Count == 0) page.Label($"{dim}{T("热点", "hotspots")}: {None}</color>");
                for (int i = 0; i < top.Count; i++) page.Label($"  {i + 1}. {top[i]}");
            }
            catch (Exception ex) { page.Label($"{warn}{T("帧性能读取失败", "frame stats failed")}: {ex.Message}</color>"); }

            // ---- 加载器 / 路径 ----
            page.Header(T("加载器与路径", "Loader & paths"));
            try
            {
                page.Label($"{L("DetailHost")} {ModLoaderHostLabel()} · {T("桥接", "bridge")} {(ModMenuBridgeSummary())}");
                page.Label($"BepInEx {(Or(ModMenuPaths.BepInExRoot))}");
                page.Label($"plugins/config {Or(ModMenuPaths.BepInExPluginDir)} | {Or(ModMenuPaths.BepInExConfigDir)}");
                page.Label($"interop {Or(ModMenuPaths.BepInExInteropDir)}");
                page.Label($"MLL {Or(ModMenuPaths.MelonRoot)}");
                page.Label($"Mods/UserLibs {Or(ModMenuPaths.MelonModsDir)} | {Or(ModMenuPaths.MelonUserLibsDir)}");
                page.Label($"UserData {Or(ModMenuPaths.MelonUserDataDir)}");
            }
            catch (Exception ex) { page.Label($"{warn}{T("路径读取失败", "paths failed")}: {ex.Message}</color>"); }

            // ---- 资产扫描（全模组） ----
            page.Header(T("资产扫描（全模组）", "Assembly scan (all mods)"));
            page.Label($"{T("程序集", "assemblies")} {ModMenuRegistry.LastScanAssemblies} · {T("候选", "candidates")} {ModMenuRegistry.LastScanCandidates} · {T("新增", "new")} {ModMenuRegistry.LastScanNew}");
            page.Label($"{T("读取失败", "load failed")} {ModMenuRegistry.LastScanLoadFailed} · {T("类型失败", "type failed")} {ModMenuRegistry.LastScanTypeFailed} · {T("耗时", "took")} {ModMenuRegistry.LastScanMs:0.##} ms");
            page.Label($"{T("清单", "inventory")}: {T("条目", "entries")} {ModMenuRegistry.Count} ({T("主动", "active")} {ModMenuRegistry.ActiveCount} / {T("扫描", "scanned")} {ModMenuRegistry.ScannedCount}) · {T("扫描次数", "scan runs")} {ModMenuRegistry.ScanRuns}");
            page.Label($"{L("DbgLastScan")}: {ModMenuRegistry.LastScanWhy} @ {ModMenuRegistry.LastScanAt}");

            // ---- 统一顺序 ----
            page.Header(T("统一顺序", "Unified order"));
            try
            {
                page.Label($"{T("条目", "entries")} {ModInitScheduler.Count} · {T("用户指定", "user-defined")} {ModOrderTable.ExplicitCount}"
                           + $" · {T("修正", "fixups")} {ModInitScheduler.LastFixups} · {T("冲突", "conflicts")} {ModInitScheduler.LastConflicts}");
                page.Label($"{T("顺序文件", "order file")} {Or(ModOrderTable.Path)}");
                string[] order = ModInitScheduler.EffectiveOrder;
                if (order == null || order.Length == 0) page.Label($"{dim}# {T("顺序", "order")}: {None}</color>");
                else for (int i = 0; i < order.Length; i++) page.Label($"  #{i + 1}  {order[i]}");
            }
            catch (Exception ex) { page.Label($"{warn}{T("顺序读取失败", "order read failed")}: {ex.Message}</color>"); }

            // ---- 重复加载 / Harmony ----
            page.Header(T("重复加载与 Harmony", "Duplicate loads & Harmony"));
            try
            {
                var all = SafeEntries();
                int dup = 0;
                for (int i = 0; i < all.Length; i++) if (all[i] != null && all[i].Duplicate) dup++;
                if (dup == 0) page.Label($"{ok}{T("无重复加载", "no duplicate loads")}</color>");
                else page.Label($"{warn}⚠ {dup} {T("个同名程序集多路径（双初始化风险）", "duplicate assembly paths (double-init risk)")}</color>");
                page.Label($"{T("Harmony 程序集", "Harmony assemblies")}: {HarmonyAssemblies()}");
            }
            catch (Exception ex) { page.Label($"{warn}{T("重复判定失败", "duplicate check failed")}: {ex.Message}</color>"); }

            // ---- 能力探测 ----
            page.Header(T("能力探测", "Capabilities"));
            try
            {
                page.Label($"{T("输入", "input")}: {CapabilitySummary()}");
                page.Label($"{T("指针来源", "pointer source")}: {(string.IsNullOrEmpty(PointerPosition.LastSource) ? None : PointerPosition.LastSource)}");
                page.Label($"API {ModMenuHost.ApiVersion} · {T("语言", "language")} {ModMenuLoc.Current} · {T("路径形态", "path kind")} {Core.ModMenuPaths.Shape}");
            }
            catch (Exception ex) { page.Label($"{warn}{T("能力探测失败", "capability probe failed")}: {ex.Message}</color>"); }

            // ---- 日志 / 配置层 ----
            page.Header(T("日志与配置", "Logs & config"));
            try
            {
                page.Label($"{L("DbgLogLevel")} {CoopLog.Level} · {T("文件日志", "file log")} {(ModLog.Enabled ? T("开", "on") : T("关", "off"))} {ModMenuPaths.LogDir}");
                page.Label($"{L("DetailConfig")} {Or(ModMenuPaths.ConfigFile)}");
                page.Label($"{T("配置映射", "config map")} {Config.ConfigMap.Describe()}");
                page.Label($"{T("语言文件", "language file")} {Or(ModMenuLoc.FilePath)}");
            }
            catch (Exception ex) { page.Label($"{warn}{T("日志信息失败", "log info failed")}: {ex.Message}</color>"); }

            // ---- UIKit 侧 ----
            page.Header(T("UIKit 接入口", "UIKit integration"));
            page.Label($"{L("DetailHost")} {ModLoaderHostLabel()} · UIKit {UiKitSummary()}");
        }

        // ---------------- 诊断辅助（全部读不到就返回 <无>，不编造） ----------------

        private static string Or(string s) => string.IsNullOrEmpty(s) ? T("（无）", "(none)") : s;

        private static double SafeD(Func<double> f)
        {
            try { return f(); } catch { return 0; }
        }

        private static List<string> SafeTop(int n)
        {
            var list = new List<string>();
            try
            {
                var top = Diagnostics.FrameProfiler.Instance.GetTop(n);
                for (int i = 0; i < top.Count; i++) list.Add($"{top[i].Name}  {top[i].MsPerSec:0.00} ms/s");
            }
            catch { }
            return list;
        }

        private static string ModMenuBridgeSummary()
        {
            try
            {
                var li = Loaders.LoaderDetector.Current;
                string s = ModMenuDisplay.LoaderHostLabel(li);
                if (!string.IsNullOrEmpty(li.BridgeVersion)) s += " v" + li.BridgeVersion;
                if (li.DualHarmony) s += "  ⚠ " + T("双 Harmony", "dual Harmony");
                return s;
            }
            catch { return T("（无）", "(none)"); }
        }

        private static string CapabilitySummary()
        {
            try { return UiInputGuard.CapabilitySummary(); }
            catch { return T("（无）", "(none)"); }
        }

        /// <summary>进程里已加载的 Harmony 程序集（过滤 HarmonyX 生成的 DTF 工厂，否则一行全是噪声）。</summary>
        private static string HarmonyAssemblies()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
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
                    try { sb.Append(n).Append(" v").Append(asms[i].GetName().Version); } catch { sb.Append(n); }
                    shown++;
                }
                if (skipped > 0) sb.Append("  (+ ").Append(skipped).Append(" DTF)");
                return sb.Length > 0 ? sb.ToString() : T("（无）", "(none)");
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        // ---------------- 辅助 ----------------

        private static int Mathf(int min, int max, int v) => v < min ? min : (v > max ? max : v);

        private static ModEntryInfo[] SafeEntries()
        {
            try { return ModInventory.Entries ?? Array.Empty<ModEntryInfo>(); }
            catch { return Array.Empty<ModEntryInfo>(); }
        }

        private static ModEntryInfo Find(ModEntryInfo[] all, string id)
        {
            if (all == null || string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && string.Equals(all[i].Id, id, StringComparison.OrdinalIgnoreCase)) return all[i];
            return null;
        }

        private static string FilterName() => _filter switch
        {
            1 => L("ChipMods"),
            2 => L("ChipDeps"),
            3 => L("ChipOff"),
            _ => L("ChipAll"),
        };

        private static bool Passes(ModEntryInfo e)
        {
            if (e == null || _filter == 0) return true;
            try
            {
                var kind = ModMenuDisplay.KindOf(e);
                if (_filter == 1) return kind == EntryKind.Mod;
                if (_filter == 2) return kind == EntryKind.Dependency;
                if (_filter == 3) return kind == EntryKind.Disabled;
            }
            catch { }
            return true;
        }

        private static string Display(ModEntryInfo e)
            => e == null ? "?" : (string.IsNullOrEmpty(e.DisplayName) ? e.Id : e.DisplayName);

        /// <summary>
        /// 左栏列表的**主文本**：[来源 pill] 名称。
        /// ⚠ 名称有**最大字符数**（用户：“左侧列表没有最大字符数量限制”）—— 超长截断到 `MaxNameChars` 并接 ASCII 三点
        ///   （`…` U+2026 在游戏字体里可能缺字形）；再配合渲染层的单行省略，两层都有上限。
        /// ⚠ 选中标记必须 **ASCII**：`▸`(U+25B8) 在游戏字库里缺字，会渲染成方框（已踩，同 `✕`）。
        /// ⚠ 来源 pill 补空格到固定宽，且**每一行都预留** `> ` 的 2 字符前缀 —— 游戏字体是等宽 CourierPrime，
        ///   这样所有行的名称起点才能对齐（否则选中行整体右移、与其它行参差）。
        /// </summary>
        private static string EntryLine(ModEntryInfo e, string selectedId)
        {
            if (e == null) return "?";
            bool isSel = string.Equals(e.Id, selectedId, StringComparison.OrdinalIgnoreCase);
            string pill = "[" + ModMenuDisplay.PillLabel(e) + "]";
            string pillHex = ModMenuTheme.Hex(ModMenuDisplay.PillColor(e));
            return (isSel ? "> " : "  ")
                   + $"<color={pillHex}>{pill.PadRight(PillWidth)}</color> "
                   + Truncate(Display(e), MaxNameChars);
        }

        /// <summary>
        /// 左栏列表的**副文本**（右侧右对齐一列，按状态**着色**：已加载=绿 / 未加载=暗灰 / 禁用=橙 / 重复=红）。
        /// 用户：“版本号没有强制在列表右侧右对齐显示” ⇒ 直接交给行的副文本区右对齐。
        /// ⚠ 用 `ModMenuDisplay.MetaText`（它的语义就是“行右侧的短状态：已加载给版本号，否则给状态词”），
        ///   **不要再拼 `e.Version`** —— 那会拼出“2.3.2 2.3.2”。
        /// </summary>
        private static string EntryMeta(ModEntryInfo e)
        {
            if (e == null) return "";
            string t = ModMenuDisplay.MetaText(e);
            if (string.IsNullOrEmpty(t)) return "";
            return $"<color={ModMenuTheme.Hex(ModMenuDisplay.MetaColor(e))}>{t}</color>";
        }

        /// <summary>来源 pill 的显示宽（字符数；`[BepInEx]` = 9，补到 10 留一列间隔）。</summary>
        private const int PillWidth = 10;

        /// <summary>名称最大字符数（超出截断 + ASCII 三点；与渲染层的单行省略双保险）。</summary>
        private const int MaxNameChars = 22;

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || max <= 3 || s.Length <= max) return s ?? "";
            return s.Substring(0, max - 3) + "...";
        }

        private static string SafeConfigPath(ModEntryInfo e)
        {
            try { return e.ConfigFile ?? ConfigLocator.Find(e) ?? ""; }
            catch { return e != null ? (e.ConfigFile ?? "") : ""; }
        }

        private static void MoveOrder(ModEntryInfo e, int dir)
        {
            try
            {
                bool ok = ModInitScheduler.Move(e.Id, dir, out string msg);
                Toast(ok ? ModMenuLoc.L("OrderMovedShort") : ModMenuLoc.L("OrderUnchanged"), msg);
                // ⚠ 仅 `Move` 是不够的：它只改条目的 `Order` 字段，而**左栏列表的顺序来自 `ModInventory.Entries`
                //   数组的排列**（按 Order 排好并缓存）→ 不重新排序就“看着没动”（用户实测反馈）。
                //   所以这里请求一次清单重刷（0.1s 后跑）→ 重排 + 触发 Changed → 界面跟着刷。
                if (ok) ModInventory.RequestRefresh("order");
            }
            catch (Exception ex) { Log("move order", ex); }
            RequestRefresh();
        }

        /// <summary>
        /// 把当前诊断页的**全部行**拼成纯文本丢进系统剪贴板（用户 2026-09-13：“诊断没有加点击复制”）。
        ///
        /// 直接读页面行模型（`UiPageDef.Rows`）⇒ 不需要在 `BuildDiag` 里逐行收集，以后加新分组也不会漏。
        /// ⚠ 两点必须做：① 剥掉 TMP 富文本标签（粘出来才是给人看的纯文本）；
        /// ② 跳过**按钮行**（否则“复制全部”按钮自己的标签也会被复制进去）。
        /// </summary>
        private static void CopyDiag(OpenNestUIKit.API.UiPageDef body, ModEntryInfo e)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(ModMenuInfo.Name).Append(' ').Append(ModMenuInfo.Version)
                  .Append(" · ").Append(Display(e))
                  .Append(" · ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                  .Append('\n');
                sb.Append(new string('-', 48)).Append('\n');

                int n = 0;
                var rows = body != null ? body.Rows : null;
                if (rows != null)
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var r = rows[i];
                        if (r == null || r.Kind == OpenNestUIKit.API.UiRowKind.Button) continue;
                        string s = StripTags(r.Label);
                        if (string.IsNullOrEmpty(s)) continue;
                        sb.Append(s).Append('\n');
                        n++;
                    }
                }

                bool ok = Core.Clipboard.SetText(sb.ToString());
                Toast(ok ? T("已复制到剪贴板", "copied to clipboard") : T("复制失败", "copy failed"),
                      ok ? string.Format(T("{0} 行（含模组标识与时间，通道 {1}）", "{0} line(s) (mod id + timestamp, channel {1})"), n, Core.Clipboard.LastPath)
                         : T("系统剪贴板不可用", "system clipboard unavailable"));
            }
            catch (Exception ex) { Toast(T("复制失败", "copy failed"), ex.Message); }
        }

        /// <summary>剥掉 TMP 富文本标签（`&lt;color=#RRGGBB&gt;…&lt;/color&gt;`）—— 进剪贴板的必须是纯文本。</summary>
        private static string StripTags(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('<') < 0) return s ?? "";
            var sb = new System.Text.StringBuilder(s.Length);
            bool inTag = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inTag) { if (c == '>') inTag = false; continue; }
                if (c == '<') { inTag = true; continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>原生 toast（桥接了就用游戏的，没桥接就只打日志）。</summary>
        private static void Toast(string title, string msg)
        {
            try { NativeUi.Toast(title, msg); } catch { }
            CoopLog.Info("modmenu.uikit", () => $"{title}: {msg}");
        }

        private static string ModLoaderHostLabel()
        {
            try
            {
                var li = LoaderDetector.Current;
                // 本地化（`LoaderInfo.HostLabel` 是中文固定串，只适合日志/诊断）
                return li != null ? ModMenuDisplay.LoaderHostLabel(li) : ModMenuInfo.BuildPlatform;
            }
            catch { return ModMenuInfo.BuildPlatform; }
        }

        private static string UiKitSummary()
        {
            try { return $"v{OpenNestUIKit.API.UiKitHost.HostVersion} (api={OpenNestUIKit.API.UiKitHost.ApiVersion})"; }
            catch { return T("已接入", "connected"); }
        }

        /// <summary>
        /// <see cref="UiSetting"/>（本模组内部行模型）→ UIKit 行。
        ///
        /// 能写回的一律做成控件：bool → 开关；有范围的数值 → 滑条；枚举 → 下拉；
        /// **其余（文本 / 快捷键 / 无范围数值）→ 可编辑输入框**（空值也给一个空文本框，
        /// 见用户反馈 2026-09-13：“只有配置项名没有内容的应该显示空文本框而不是直接暗掉”）；
        /// 只有真只有读（配置注释里声明 ReadOnly / 第三方纯展示行）才画成灰字文字行。
        /// </summary>
        private static void AddRow(OpenNestUIKit.API.UiPageDef page, UiSetting s)
        {
            if (s == null) return;
            if (s.Header) { page.Header(s.Label); return; }
            if (s.Separator) { page.Separator(); return; }

            string key = string.IsNullOrEmpty(s.Key) ? s.Label : s.Key;
            bool writable = !s.ReadOnly && s.Write != null;

            if (writable && s.Kind == SettingKind.Bool)
            {
                page.Toggle(key, s.Label, IsTrue(s.Value), v =>
                {
                    bool ok = s.Write(v ? "true" : "false");
                    CoopLog.Debug("modmenu.uikit", () => $"设置 '{s.Label}' → {v} ({(ok ? "ok" : "fail")})");
                });
                return;
            }
            if (writable && s.Kind == SettingKind.Number && s.RangeMin.HasValue && s.RangeMax.HasValue
                && s.RangeMax.Value > s.RangeMin.Value)
            {
                page.Slider(key, s.Label, Num(s.Value), s.RangeMin.Value, s.RangeMax.Value, s.Step, d =>
                {
                    bool ok = s.Write(Num(d));
                    CoopLog.Debug("modmenu.uikit", () => $"设置 '{s.Label}' → {Num(d)} ({(ok ? "ok" : "fail")})");
                });
                return;
            }
            if (writable && s.Kind == SettingKind.Enum && s.Choices != null && s.Choices.Length > 0)
            {
                int idx = IndexOf(s.Choices, s.Value);
                page.Choice(key, s.Label, s.Choices, idx, i =>
                {
                    if (i < 0 || i >= s.Choices.Length) return;
                    bool ok = s.Write(s.Choices[i]);
                    CoopLog.Debug("modmenu.uikit", () => $"设置 '{s.Label}' → {s.Choices[i]} ({(ok ? "ok" : "fail")})");
                });
                return;
            }

            // ★ 文本/无范围数值/快捷键：**可编辑输入框**（用户：“配置项里的输入框也没正常工作”）
            //   早期这里和自带界面 v1 一样只做只读展示 → 用户看到“有值但没有输入框”。
            //   现在 UiTextInput 走的是原模组那套输入管线（物理键 + 系统输入法），中文/长文本都能改。
            if (writable)
            {
                page.Text(key, s.Label, s.Value ?? "", v =>
                {
                    bool ok = s.Write(v ?? "");
                    CoopLog.Info("modmenu.uikit", () => $"设置 '{s.Label}' = '{v}' ({(ok ? "ok" : "fail")} {s.LastMessage})");
                    if (!ok && !string.IsNullOrEmpty(s.LastMessage)) Toast("写入失败", s.LastMessage);
                });
                return;
            }

            // 其余（真只读）：只读展示
            string val = s.Value ?? "";
            string hint = "";
            try { hint = s.RangeMin.HasValue && s.RangeMax.HasValue ? $"（{s.RangeMin} ~ {s.RangeMax}）" : ""; } catch { }
            page.Label(string.IsNullOrEmpty(val) ? s.Label : $"{s.Label}：{val}{hint}");
        }

        private static bool IsTrue(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            switch (v.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "on": case "yes": return true;
                default: return false;
            }
        }

        private static double Num(string v)
        {
            double d;
            return double.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out d) ? d : 0d;
        }

        private static string Num(double d) => d.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

        private static int IndexOf(string[] arr, string v)
        {
            if (arr == null) return 0;
            for (int i = 0; i < arr.Length; i++)
                if (string.Equals(arr[i], v, StringComparison.Ordinal)) return i;
            return 0;
        }

        private static void Log(string what, Exception ex)
            => CoopLog.Warn("modmenu.uikit", () => $"{what} 失败: {ex.Message}");
    }
}
