using System;

namespace OpenNestModMenu.Loc;

/// <summary>
/// 内置默认文案表（`键 → 中文 / English`）。见 `docs/MOD_MENU.md` §5.4（语言键）。
///
/// 规则（与 OpenNestCoop 的 `LocDefaults` 同构，见 `docs/LOCALIZATION.md`）：
/// - 代码里**只出现键**；界面文案来自外部语言键文件 `OpenNestModMenu.lang.ini`；
/// - 文件缺失 → 用本表生成；升级新增键 → 自动补进文件（保留用户改过的文本）；
/// - 取文案顺序：当前语言段 → `[en]` 段 → 本表 → 键名。
/// </summary>
public static class LocDefaults
{
    /// <summary>一条默认文案（键 + 两种语言的默认文本）。</summary>
    public readonly struct Item
    {
        public readonly string Key;
        public readonly string Zh;
        public readonly string En;

        public Item(string key, string zh, string en)
        {
            Key = key;
            Zh = zh;
            En = en;
        }
    }

    /// <summary>全部键（数组顺序 = 语言文件里的书写顺序）。</summary>
    public static readonly Item[] Items =
    {
        // ---- 菜单 / 通用 ----
        new Item("Title", "模组菜单", "Mod Menu"),
        new Item("Close", "关闭", "Close"),
        new Item("Rescan", "重新扫描", "Rescan"),
        new Item("Page", "第 {0}/{1} 页 · 共 {2} 条", "page {0}/{1} · {2} items"),
        new Item("HintSelect", "← 在左侧点选一个模组查看详情（F6 开关菜单）", "← Click a mod on the left to inspect it (F6 toggles)"),
        new Item("SubtitleHint", "左栏点选，右栏查看", "pick left, inspect right"),

        // ---- 筛选 chip ----
        new Item("ChipAll", "全部", "All"),
        new Item("ChipMods", "模组", "Mods"),
        new Item("ChipDeps", "依赖", "Deps"),
        new Item("ChipOff", "禁用", "Off"),

        // ---- 来源 / 格式标签 ----
        new Item("PillMll", "MLL", "MLL"),
        new Item("PillDependency", "依赖", "dep"),
        new Item("PillUnknown", "?", "?"),
        new Item("HostBepInEx", "BepInEx", "BepInEx"),
        new Item("HostMelonLoader", "MelonLoader", "MelonLoader"),
        // ⚠️ 键名带 Mll：文案从“ML bridge”改成“MLL”时**必须换键名**，
        //    否则旧安装的语言文件里已存在的 HostBridge=... 会优先于新默认值（实测踩过）。
        new Item("HostMllBridge", "BepInEx + MLL", "BepInEx + MLL"),
        new Item("HostUnknown", "未知", "unknown"),
        new Item("FmtBepInEx", "BepInEx 插件", "BepInEx plugin"),
        new Item("FmtMelon", "MelonLoader 模组", "MelonLoader mod"),
        new Item("FmtUnknown", "未知（按目录推断）", "unknown (inferred from folder)"),

        // ---- 状态词 ----
        new Item("StateLoaded", "已加载", "loaded"),
        new Item("StateNotLoaded", "未加载", "not loaded"),
        new Item("StateEnabled", "已启用", "enabled"),
        new Item("StateDisabled", "已禁用", "disabled"),
        new Item("StateDup", "! 重复加载", "! duplicate load"),
        new Item("StateStoppedNow", "已停用（运行中）", "stopped (runtime)"),
        new Item("MetaDup", "! 重复", "! dup"),
        new Item("Separator", " · ", " · "),

        // ---- 详情字段名 ----
        new Item("DetailId", "Id", "Id"),
        new Item("DetailAuthor", "作者", "Author"),
        new Item("DetailState", "状态", "State"),
        new Item("DetailHost", "宿主", "Host"),
        new Item("DetailFormat", "格式", "Format"),
        new Item("DetailPriority", "优先级", "Priority"),
        new Item("DetailPriorityNote", "（加载器声明）", " (loader-declared)"),
        new Item("DetailDeps", "依赖", "Depends on"),
        new Item("DetailIncompat", "不兼容", "Incompatible"),
        new Item("DetailSettings", "设置页", "Settings"),
        new Item("DetailSettingsOn", "已接入（ModMenuHost）", "registered (ModMenuHost)"),
        new Item("DetailSettingsOff", "无（未接入 ModMenuHost）", "none"),
        new Item("DetailPath", "路径", "Path"),
        new Item("DetailNote", "备注", "Note"),
        new Item("DetailNone", "（无）", "(none)"),

        // ---- 底栏 ----
        new Item("FooterLoaded", "已加载", "loaded"),
        new Item("FooterShown", "当前筛选", "shown"),
        new Item("FooterDisabled", "禁用", "disabled"),
        new Item("FooterDup", "重复", "dup"),
        new Item("FooterDeps", "依赖", "deps"),
        new Item("FooterProviders", "提供者", "providers"),
        new Item("FooterRefreshed", "刷新", "refreshed"),
        new Item("FooterToggle", "F6 开关", "F6 toggle"),

        // ---- 内置设置页 ----
        new Item("BuiltAbout", "关于", "About"),
        new Item("BuiltBuiltFor", "构建平台", "Built for"),
        new Item("BuiltShape", "部署形态", "Deploy shape"),
        new Item("BuiltApi", "契约版本", "Contract API"),
        new Item("BuiltLang", "语言", "Language"),
        new Item("BuiltLangFile", "语言键文件", "Language file"),
        new Item("BuiltPaths", "路径", "Paths"),
        new Item("BuiltGame", "游戏目录", "Game"),
        new Item("BuiltConfig", "配置", "Config"),
        new Item("BuiltLogs", "日志", "Logs"),
        new Item("BuiltMelonRoot", "MelonLoader 侧", "MelonLoader root"),
        new Item("BuiltRegistry", "注册表", "Registry"),
        new Item("BuiltProviders", "提供者总数", "Providers"),
        new Item("BuiltActive", "主动", "active"),
        new Item("BuiltScanned", "扫描发现", "scanned"),
        new Item("BuiltScans", "扫描次数", "Scans"),
        new Item("BuiltLast", "最近", "last"),
        new Item("BuiltLastResult", "最近结果", "Last result"),
        new Item("BuiltRescan", "立即重新扫描", "Rescan now"),

        // ---- 本模组自己的开关（`OpenNestModMenu.cfg`） ----
        new Item("BuiltOwnSettings", "本模组设置", "Mod menu settings"),
        new Item("BuiltDebugMode", "调试模式（底栏显示环境摘要 + 诊断日志全开）", "Debug mode (footer summary + verbose diagnostics)"),
        new Item("DbgLastScan", "上次扫描", "last scan"),
        new Item("OrderMovedShort", "顺序已调整", "order updated"),
        new Item("OrderUnchanged", "顺序未变", "order unchanged"),

        // ---- 启用 / 禁用（T7） ----
        new Item("BtnDisable", "禁用", "Disable"),
        new Item("BtnEnable", "启用", "Enable"),
        new Item("BtnRestartHint", "（重启生效）", "(restart to apply)"),
        new Item("ToggleNoEntry", "没有选中的模组", "no mod selected"),
        new Item("ToggleNoFile", "该条目没有可操作的文件（不是磁盘上的模组）", "this entry has no file on disk"),
        new Item("ToggleSelf", "不能从这里禁用模组菜单自身（请直接删除/改名文件）", "cannot disable the mod menu itself (rename the file instead)"),
        new Item("ToggleMissing", "文件不存在（可能刚被移动过）", "file not found (it may have just been moved)"),
        new Item("ToggleConflict", "目标文件名已存在：{0}", "target file already exists: {0}"),
        new Item("ToggleDisabled", "已禁用 {0} —— 重启游戏后生效", "disabled {0} — takes effect after restart"),
        new Item("ToggleEnabled", "已启用 {0} —— 重启游戏后生效", "enabled {0} — takes effect after restart"),
        new Item("ToggleFailed", "操作失败：{0}", "failed: {0}"),

        // ---- 运行中启停（T13） ----
        new Item("DetailRuntime", "运行中", "Runtime"),
        new Item("RuntimeRunning", "正常", "running"),
        new Item("RuntimeStoppedMelon", "已停用（立即生效；撤 patch + 退订回调）", "stopped now (Harmony patches removed, callbacks unsubscribed)"),
        new Item("RuntimeStoppedProvider", "已停用（立即生效；模组自行停用）", "stopped now (mod-implemented)"),
        new Item("RuntimeUnsupported", "本宿主不支持运行中停用（需重启生效）", "this host cannot stop mods at runtime (restart required)"),
        new Item("RuntimeFailed", "运行中停用失败（需重启生效）", "runtime stop failed (restart required)"),
        new Item("RuntimeResumedMelon", "已恢复（立即生效；重新注册模组）", "resumed now (re-registered)"),
        new Item("RuntimeResumedProvider", "已恢复（立即生效）", "resumed now"),
        new Item("RuntimeReloadFailed", "恢复失败（需重启游戏）", "resume failed (restart the game)"),
        // BepInEx 插件热加载（运行中启用，不需要重启）：卸载依然做不到（BepInEx 没有卸载 API）
        new Item("RuntimeLoadedBepInEx", "已立即加载（本会话生效；卸载仍需重启）", "loaded now (this session; unloading still needs a restart)"),
        new Item("RuntimeHotLoadFailed", "立即加载失败（{0}）—— 重启后生效", "hot load failed ({0}) — will apply after restart"),
        // BepInEx 插件“软停用”（实验性：能撒 patch + 销毁组件，但静态状态/线程/订阅可能残留）
        new Item("RuntimeStoppedSoft", "已软停用（实验性：撤 Harmony patch + 销毁其组件；静态状态/线程可能残留）", "soft-stopped (experimental: patches removed + components destroyed; static state may remain)"),
        new Item("BtnDisableExperimental", "停用（实验性）", "Stop (experimental)"),
        new Item("RuntimeNoProvider", "该模组未接入设置契约", "mod does not implement the settings contract"),
        new Item("RuntimeNotSupported", "该模组未声明支持热切换", "mod does not support runtime toggling"),
        new Item("BtnDisableNow", "停用（立即）", "Stop now"),
        new Item("BtnEnableNow", "启用（立即）", "Resume now"),

        // ---- 设置中心（T6） ----
        new Item("TabDetails", "详情", "Details"),
        new Item("TabSettings", "设置", "Settings"),
        new Item("SettingsProviderPage", "本模组自带的设置页", "Mod-provided settings"),
        new Item("SettingsConfigFile", "配置文件：{0}", "Config file: {0}"),
        new Item("SettingsSaved", "已写入 {0}", "saved to {0}"),
        new Item("SettingsWriteFailed", "写入失败：{0}", "write failed: {0}"),
        new Item("SettingsNone", "没有可显示的设置（既未接入设置契约，也没找到配置文件）",
                 "no settings to show (no settings contract, no config file found)"),
        new Item("SettingsReadOnly", "只读（文本/密钥请直接改配置文件）", "read-only (edit the config file for text/keys)"),
        new Item("SettingsPage", "第 {0}/{1} 页", "page {0}/{1}"),
        new Item("SettingsCount", "共 {0} 项", "{0} item(s)"),
        new Item("SettingsOn", "开", "ON"),
        new Item("SettingsOff", "关", "OFF"),
        new Item("DetailConfig", "配置文件", "Config"),

        // ---- 统一加载/初始化顺序（T8） ----
        new Item("DetailOrder", "顺序", "Order"),        new Item("OrderNone", "不在顺序里", "not in order"),
        new Item("OrderUser", "用户指定", "user"),
        new Item("OrderLoader", "加载器决定", "loader"),
        new Item("OrderMoved", "已把 {0} 移到第 {1}/{2} 位", "moved {0} to #{1}/{2}"),
        new Item("OrderFirst", "已在首位", "already first"),
        new Item("OrderLast", "已在末位", "already last"),
        new Item("OrderUp", "上移", "Move up"),
        new Item("OrderDown", "下移", "Move down"),

        // ---- 诊断面板（T10 / T11）----
        new Item("TabDebug", "诊断", "Debug"),
        new Item("DbgNow", "立即刷新", "Refresh"),
        new Item("DbgRefreshed", "刷新 {0}", "refreshed {0}"),
        new Item("DbgLive", "每秒自动刷新", "auto 1s"),
        new Item("DbgSelfTest", "自动化会话", "automated session"),
        new Item("DbgNone", "<无>", "<none>"),
        new Item("DbgOn", "开", "on"),
        new Item("DbgOff", "关", "off"),
        new Item("DbgNo", "否", "no"),
        new Item("DbgNote", "说明", "Note"),
        new Item("DbgFrame", "帧性能", "Frame"),
        new Item("DbgFps", "帧率", "FPS"),
        new Item("DbgAvg", "平均", "avg"),
        new Item("DbgWorst", "最差帧", "worst"),
        new Item("DbgSpots", "测量点", "Spots"),
        new Item("DbgPending", "待结算（帧率按 1 秒窗口统计）", "pending (1s window)"),
        new Item("DbgLoader", "加载器", "Loader"),
        new Item("DbgHost", "宿主", "Host"),
        new Item("DbgBridge", "桥", "Bridge"),
        new Item("DbgInterop", "interop", "interop"),
        new Item("DbgHarmony", "双 Harmony", "Dual Harmony"),
        new Item("DbgDualHarmonyYes", "两份不同的 Harmony（patch 视图互相隔离）", "two distinct Harmony copies (patch views isolated)"),
        new Item("DbgDualHarmonyNote", "本模组只反射使用宿主侧那一份，不做跨侧 patch 归因", "uses the host-side copy only (no cross-side attribution)"),
        new Item("DbgPaths", "路径", "Paths"),
        new Item("DbgInventory", "模组清单", "Inventory"),
        new Item("DbgEntries", "条目", "Entries"),
        new Item("DbgRefresh", "清单刷新", "Inventory refresh"),
        new Item("DbgRegistry", "注册表", "Registry"),
        new Item("DbgLastScan", "最近扫描", "Last scan"),
        new Item("DbgOrder", "统一顺序", "Load order"),
        new Item("DbgOrderFile", "顺序表", "Order file"),
        new Item("DbgFixups", "依赖修正", "Fixups"),
        new Item("DbgConflicts", "不兼容", "Conflicts"),
        new Item("DbgDup", "重复加载", "Duplicates"),
        new Item("DbgDupNone", "无（同一程序集只出现在一个启用路径）", "none (each assembly in one enabled path)"),
        new Item("DbgDupRisk", "条重复（同一程序集在多个启用路径 → 会双初始化，见 docs/MOD_MENU.md § 3.4）",
                 "duplicate entries (same assembly on multiple enabled paths -> double init, see docs/MOD_MENU.md §3.4)"),
        new Item("DbgHarmonyAsm", "Harmony 程序集", "Harmony assemblies"),
        new Item("DbgCapability", "能力探测", "Capabilities"),
        new Item("DbgInput", "输入/层级", "Input/z-order"),
        new Item("DbgPointer", "指针来源", "Pointer source"),
        new Item("DbgNativeUi", "原生 UI", "Native UI"),
        new Item("DbgNativeUiOff", "未接入（v1 用覆盖 UI）", "not wired (v1 overlay UI)"),
        new Item("DbgLogs", "日志/配置", "Logs/config"),
        new Item("DbgLogLevel", "日志等级", "Log level"),
        new Item("DbgFileLog", "独立文件日志", "File log"),
        new Item("DbgConfigMap", "配置映射", "Config map"),

        // ---- 清单备注（数据层生成，UI 直接显示） ----
        new Item("NoteDisabled", "已禁用（重启生效）", "disabled (restart to apply)"),
        new Item("NoteLibraryOrUnloaded", "未加载：无模组类型（依赖库或非模组）", "not loaded: no mod types (library / non-mod)"),
        new Item("NoteModNotLoaded", "是模组但未加载（依赖缺失或被加载器跳过）", "is a mod but not loaded (missing dependency / skipped by loader)"),
        new Item("NoteMelonSkip", " · MelonLoader 会跳过此前缀（~/.）", " · MelonLoader skips this prefix (~/.)"),
        new Item("NoteMelonPlugin", "MelonLoader Plugin（非 Mod）", "MelonLoader Plugin (not a Mod)"),
    };

    /// <summary>按键查默认文案（找不到返回 false）。</summary>
    public static bool TryGet(string key, out string zh, out string en)
    {
        zh = null;
        en = null;
        if (string.IsNullOrEmpty(key)) return false;
        for (int i = 0; i < Items.Length; i++)
        {
            if (string.Equals(Items[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                zh = Items[i].Zh;
                en = Items[i].En;
                return true;
            }
        }
        return false;
    }
}
