# 模组菜单（OpenNestModMenu）设计

> **目的**：新建**独立模组** `OpenNestModMenu`——自带 UI / 日志基建，**不链接、不依赖 `OpenNestCore` / `OpenNestCoop` 的程序集**，
> 在游戏内提供统一入口：**模组列表 / 启停 / 统一设置中心 / 统一加载顺序 / 调试面板**；**开放第三方注册 API**，让其它模组把自己的设置页挂进来。
> ⚠️ 「复用 Core 的原生 UI 部分」的含义 = **把 `OpenNestCore.UI` 的源码拷进本模组并改用独立命名空间**（不是引用 Core 程序集），见 §1.1 / §1.2。
>
> **已定决策（2026-09-12，用户确认）**：
> 1. 范围 = 列表 + 启停 + 统一设置中心 + 调试面板 + **统一加载顺序**（五者全做）。
> 2. 入口 = **热键 + 原生菜单注入，两者都要**。
> 3. **开放第三方注册 API**；模组另起为 `OpenNestModMenu`。
> 4. **双端**（BepInEx + MelonLoader）；且需与在维护的桥模组 **`BepInEx.MelonLoader.Loader`（在 BepInEx 上加载 MLL 模组）** 功能兼容，
>    **并标记每个模组的加载器来源**。
> 5. **UI 先用覆盖 UI**（ScreenSpaceOverlay 自制 UGUI）；原生 UI（原生素材观感 / `UiSpriteBank` 图集）**先复用/保留代码但不激活**，后续再研究。
> 6. **与 Core 独立**：`OpenNestModMenu` 作为单独模组存在，**不依赖 `OpenNestCore` / `OpenNestCoop` 的程序集**（自带源码副本 + 独立命名空间，见 §1.2）。
> 7. **桥采用「别名策略」，最终使用宿主（BepInEx）的 interop** —— 已按桥源码 + 本机环境**核实**（见 §3.2），不再是待实测项。
>
> **信息来源**：`src/OpenNestCore/UI/{INativeUiService,UiKit,UiSpriteBank}.cs`、`src/OpenNestCoop/UI/{CoopUIManager,MainMenuEntry,IronNestNativeUi}.cs`、
> `src/OpenNestCoop.MelonMod/OpenNestCoop.MelonMod.csproj`、`scripts/{package,deploy}.ps1`、`docs/NATIVE_UI.md`、`docs/DEVELOPMENT.md`、`docs/CONFIG.md`；
> **桥模组源码**：`<bridge repo>`（`BepInEx.MelonLoader.Loader.IL2CPP/Plugin.cs`、`MelonLoader/Hosting/{BepInExHost,Il2CppInteropAliasInjector,ModCompatScanner}.cs`、`MelonLoader/Melons/MelonFolderHandler.cs`、`MelonLoader/Melons/MelonBase.cs`、`BepInEx.MelonLoader.Loader.Patcher/Patcher.cs`）；
> **本机 BepInEx 端实测**：`<GameDir(BepInEx)>\...\BepInEx\{plugins,patchers,interop,config}` + `MLLoader\`，`<GameDir(MelonLoader)>\...\{Mods,UserLibs,MelonLoader}`。
>
> **更新记录**：
> - 2026-09-13（二十八）**依赖顺序我得到真数据了 + 修三处本地化残留**（用户：“依赖需要加载顺序吗？有跟随加载顺序吗？/ 有语言键缺失残留”）：
>   ① **“依赖前移”以前是空跑**：MLL 侧一直读 `MelonAdditionalDependencies`，而 **BepInEx 侧的依赖从来没读**
>      ⇒ 所有 BepInEx 插件的 `DependsOn` 都是空 ⇒ 调度器里的依赖前移形同虚设（实机 `fixups=0`）。
>      现：从 `PluginInfo.Dependencies`（`BepInDependency[]` → `DependencyGUID`）读，兜底通道从类型上的 `BepInDependency` 属性读。
>   ② **依赖匹配因此失败（真 bug）**：依赖声明写的是 **GUID**（`open.nest.uikit`），而条目 `Id` 是**文件名**（`OpenNestUIKit`）
>      ⇒ 只比 Id 静默匹配不到。新增 `ModEntryInfo.Guid`，`ModInitScheduler.IndexOfDep` 按 **Id / Guid / DisplayName** 三种比。
>   ③ **顺带修“依赖库排在依赖者后面”的观感**：依赖边一旦生效，依赖会被拉到使用者前面。
>      实测（故意把测试模组排在 UIKit 前）：`dependency fixup: 'OpenNestUIKit' moved before 'OpenNestUIKit.Test'`，
>      `effective order: #1 OpenNestUIKit < #2 OpenNestUIKit.Test | fixups=1`。
>      ⚠ 注意语义：**实际加载顺序仍由加载器决定**（BepInEx 插件按扫描序、MLL 按 Priority），
>      这份统一顺序管的是**展示顺序**与**受管初始化（`IModMenuInit`）调用顺序**；没有声明依赖的模组就只看加载器/用户表。
>   ④ 新增证据行：清单刷新时打一条 `declared dependencies (N): 模组 -> [依赖]`（为 0 就说明**没人声明依赖**，
>      而不是“调度器没做”）；测试模组现在真实声明 `[BepInDependency("open.nest.uikit")]`，让这条链路在实机上可验证。
>   ⑤ **本地化残留三处**（用户截图里右栏标题行的“BepInEx 插件”）：展示层不能再读条目上的 `HostLabel/FormatLabel`
>      （那是扫描时写死的中文）→ 一律走 `ModMenuDisplay.HostLabel/FormatLabel/PillLabel`；
>      新增 `ModMenuDisplay.LoaderHostLabel(LoaderInfo)`（`LoaderInfo.HostLabel` 只是日志用的中文串）；
>      诊断页“双 Harmony”、复制结果 Toast、`（无）` 兜底等也改成双语。实测右栏标题行已是 `0.0.1-Alpha-3 · BepInEx · BepInEx plugin`。
> - 2026-09-13（二十七）**UIKit 页全量本地化 + 刷新机制修根 + 调试模式开关 + 模拟配置文件**（用户：“启用禁用之后按钮和模组状态没有刷新，上移，下移也一样 / 语言键不全 / 给测试模组做个模拟配置文件 / UI 右下角的 Debug 信息只在调试模式启用”）：
>   ① **“点了不刷新”真因是两条**：(a) 重扫是**延迟 0.1s 排队**的，而回调里立即 `Refresh` ⇒ 刷的还是旧数据；
>      (b) 重扫完成后没有事件通知界面，而且 `Move` 只改条目 `Order` 字段——**左栏顺序来自 `Entries` 数组的排列**，
>      不重排就看着没动。现：`RequestRefresh` 改为**延迟 0.18s 节流刷新**（不在点击回调里销毁页面），
>      并订阅 `ModInventory.Changed` / `ModRuntimeState.Changed`；上/下移后再 `ModInventory.RequestRefresh("order")` 让清单重排。
>   ② **语言键不全**：UIKit 渲染的那一页原来几十条硬编码中文（游戏切英文后那页还是中文）。
>      现：有语言键的走 `ModMenuLoc.L`（新增 `BuiltOwnSettings`/`BuiltDebugMode`/`DbgLastScan`/`OrderMovedShort`/`OrderUnchanged`），
>      其余用 UIKit 契约的 `UiKitLang.T(zh,en)`（与本库自己渲染的页面同源）。实测整页英文一致（图 `p1.png`）。
>   ③ **调试模式**：新增 `Core/ModMenuPrefs.cs` → `BepEx\config\OpenNestModMenu.cfg`（BepInEx 风格元数据注释，
>      带 `## Setting type: System.Boolean` ⇒ 本模组自己的设置页也能自动把它渲染成开关）；
>      底栏环境摘要（宿主/UIKit/提供者/上次扫描）**只在调试模式显示**，调试模式 = 用户开关 ∨ 日志等级=Debug ∨ 自检模式。
>   ④ **详情页去重**：右栏上方已显示名称/版本，`BuildDetail` 不再重复 Header + 版本行。
>   ⑤ **模拟配置文件**（测试模组）：`MockConfig` 写到 `BepInEx\config\open.nest.uikit.test.cfg`，覆盖
>      bool / int+范围 / float+步进 / 枚举 / 快捷键 / 文本 / **空文本** / 只读 / 无类型声明(按值推断) / 多 [Section] /
>      项目风格“键后注释”元数据；CLI：`mockcfg`（缺失才写）/ `mockcfg!`（重建）。
>      实测：菜单里该模组设置页解析出 **13 项**并全部绑上控件（图 `p2.png`）。
>   ⑥ 新增测试钩子 `UIKitIntegration.SelfTestSelect`：`-onnmm-selftest-tab=<details|settings|debug>:<Id 匹配串>`
>      现在也驱动 **UIKit** 渲染的菜单（以前只动自带界面，装了 UIKit 等于什么都没发生）。
> - 2026-09-13（二十六）**详情/诊断页属性表改键值两列 + 跟进扁平工业风**（用户：“扁平化工业风”）：
>   ① 详情页（`BuildDetail`）与诊断页的 `状态/文件/配置/版本/作者/依赖/不兼容/备注` 从 `Label("标签：值")`
>      改为 **`Info(标签, 值)`**（UIKit 新增的 `UiRowKind.Info`）：标签列固定宽 76、值列左对齐 ——
>      多行下来两列对齐，长路径不再把标签挤走。
>   ② 观感随 UIKit 主题一起变扁平（冷灰钢底 + 1px 描边/发丝线 + 琥珀细规；行不再画卡片底）。
>   - 实测截图 `shots/flat3.png`（详情页：`状态|已加载 · 未接管` / `文件|G:\…` / `配置|（未找到）`）。
> - 2026-09-13（二十五）**观感跟进**：左栏副文本按状态着色（`MetaColor` 富文本）；配合 UIKit 侧观感精修（分隔线修复 + 配色层次 + 暗金选中态，见 `docs/UI_KIT.md` 更新记录四十三）。
> - 2026-09-13（二十四）**左栏列表排版：名称截断 + 版本右对齐**（用户：“左侧列表没有最大字符数量限制，版本号没有强制在列表右侧右对齐显示”）：
>   ① 用新契约 `SelectableList(key, height, items, hints, …)` 把**版本/状态**（`ModMenuDisplay.MetaText`）交给行的右对齐副文本区
>      —— 不再拼在名称后面；⚠ `MetaText` 已含版本，不要再拼 `e.Version`（会出 `2.3.2 2.3.2`）。
>   ② 主文本 = `[来源 pill] 名称`：pill 补空格到固定宽 10、**每行都预留** `> ` 两字符前缀 ⇒ 名称起点对齐；
>      名称超 22 字符截断为 `...`（ASCII；`…` 可能缺字形），渲染层再单行省略。
>   实测截图 `shots/mm7.png`。
> - 2026-09-13（二十三）**右栏撑满 + 环境摘要移到窗口底栏**（用户：“ModMenu 右侧块宽度没有撑满宽度，底下那个块又把块高度撑出去了，直接放底栏那个 Open Nest Mod Menu 右侧吧”）：
>   ① 右栏宽度不撑满是 **UIKit 侧 `Columns` 的 bug**（构建时容器还是兜底宽 + 子栏锚点被改点锚后钉死），
>      已在 `UiDeclarativePages.BuildColumns` 改成**横向 flow 分配**（详见 `docs/UI_KIT.md` 更新记录四十一）；ModMenu 侧无需改动。
>   ② 页面底部原来那 3 行（分隔线 + 宿主/UIKit + 提供者/扫描统计）**不再占页面高度**，
>      改用新契约 `UiKitHost.SetFooter(...)` 摆到**窗口底栏右侧**（`Open Nest Mod Menu` 面包屑的右边；宿主弹临时提示时临时盖住它）。
>   实测：`rects` → 右栏 `685x470`、底栏出现环境摘要；截图 `shots/mm5.png`；`PASS=5 FAIL=0`。
> - 2026-09-13（二十二）**右栏改固定高滚动区 + 诊断页加“点击复制”**（用户：“右侧块没有固定最大尺寸，块把外面的块高度撑开了 / 诊断没有加点击复制”）：
>   ① **撑开的真因**：右栏内容（详情/设置/诊断）之前是**直接摊进右栏子流**——诊断页 50+ 行，
>      `Columns` 的行高由内容决定 ⇒ 整页/窗口被顶高（`page.Size(1180,700)` 只定首选尺寸，拦不住）。
>      现把右栏内容包进**固定 470 高的内嵌 `List`**（`mm.right`，与左栏同高）⇒ 内容再多也只在块内滚。
>   ② **诊断页“点击复制”**：顶部与底部各一个「复制全部」按钮 → `CopyDiag` 直接把当前诊断页的**行模型**
>      （`UiPageDef.Rows`）拼成纯文本进剪贴板（不需要在 `BuildDiag` 里逐行收集，以后加分组不会漏）；
>      剥掉 TMP 富文本标签、跳过按钮行；带模组标识 + 时间戳 + 48 字符分隔线，Toast 回报行数与通道。
>      新增 `Core/Clipboard.cs`（Win32 主路径 + `GUIUtility.systemCopyBuffer` 兜底，与 Coop 侧同源——两模组程序集独立，故各持一份）。
>   - 实测：`zones` 出现 `mm.right-viewport/mm.right-scrollbar` 与 `btn:复制全部`（顶/底各一个）；
>     `tap:btn:复制全部` 后 **Windows 剪贴板**里是完整纯文本诊断（8 个分组齐全、无 `<color=…>` 残留）；
>     截图 `shots/mmdiag.png`（右栏不再撑开）、`shots/mmcopy.png`。
>   - 观察项（未处理）：切页签后 `zones` 里会多出一批 `[hidden]` 旧热区（`list-viewport` / `btn:禁用` …）——
>     `HitTop` 会跳过它们（不影响点击），但反复切页会累积；属 UIKit 页面重建时的热区回收问题，待单独处理。
> - 2026-09-13（二十一）**左栏模组列表去掉每行的「选中」按钮：改为点行本身即选中**（用户：“ModMenu的左侧不需要额外的选中按钮了”）：
>   以前左栏是 `List` + 每行右侧一个「选中」/「已选」按钮（点按钮才换选中项，点行空白处没反应，且行被按钮挤窄）。
>   现改用契约里的 **`SelectableList`**（`docs/UI_KIT.md` 契约扩容第一档新增的行动词）——
>   条目用 `UiNavRow`，**点整行即回调**，选中行由宿主画高亮（左强调条 + 选中态），行里只剩“来源 pill + 名称 + 版本/状态”三块富文本信息。
>   要点：① `SelectableList` 只吃 `IReadOnlyList<string>` + 选中索引 ⇒ 页面构建前先算好“过滤后的可见条目”列表（`shown` / `shownLabels` / `selIndex`）；
>   ② 宿主在回调后**自己** `ReloadCurrentPage()` 重画高亮 ⇒ 第三方回调里**不要**再 `RequestRefresh()`（否则白重建一次）；
>   ③ `_selectedId` 为空时**默认选中第一条**，左栏首行高亮与右栏详情才不会对不上（原来右栏显示“先在左边点一个模组选中”）。
>   - G 端实测（`-onuktest-run=…open:provider:open-nest-mod-menu…`）：`zones` 里不再有行内按钮热区，改为 `pick:mm.list:0..12`；
>     `tap:pick:mm.list:2` → 第 3 行变 `> [ML] SomeMod Smart 1.2.7` 且**右栏同步切到该模组**；
>     截图 `OpenNestUIKitLogs\shots\mmleft.png` / `mmsel.png`；`PASS=7 FAIL=0`。
> - 2026-09-13（二十）**设置页文本/快捷键项改为可编辑输入框**（用户：“Mod配置里只有配置项名没有内容的应该显示空文本框
>   而不是直接暗掉”）：真因是 `ModConfigStore.Scan` 把 `Text`/`Key` 两类也标了 `ReadOnly`（v1 没有文本编辑，见 §1.1 的 IME 坑），
>   UIKit 设置页于是按“只读行”画成灰字 —— 只有键名、值为空的项（本机实例：`OpenNestCoop.cfg` 的 `FakeId` / `Name` / `LastHost`
>   全是空串）看上去就是暗掉的一行，也**没法填值**。
>   现在 UIKit 侧已有完整文本输入管线（`docs/UI_KIT.md` §8.2），文本/快捷键项能写回 → 改 `item.ReadOnly = kind == SettingKind.ReadOnly`
>   （只注释里**显式声明** ReadOnly、或注释声明了无法识别的类型才真只读）；`ModConfigStore.SetValue` 的只读闸不变。
>   自带回退面板（无 UIKit 时）没有输入框，仍按“文本项只读”画（新增 `ModMenuSettingsView.RowReadOnly`），观感不变。
>   - G 端实测：设置页 `zones` 出现 `toggle:Sync.CatSync | toggle:Sync.RecordPlayerSync | toggle:Interactables.CalculateButtonSync |
>     input:Identity.FakeId | input:Identity.Name | input:LAN.Port | input:LAN.LastHost`；
>     `click:input:Identity.Name` + `type:ZZQA` → `聚焦='Name'（文本='ZZQA'）｜IME 锚点=已聚焦` → 磁盘复核 `Name = ZZQA`（已复原为空）。
>   - D 端（原生 MLL，配置在 `UserData\OpenNestCoop.cfg`）同样出现这 4 个 `input:`。
>   - 联机菜单配色同批恢复，见 `docs/UI_KIT.md` 更新记录十五。
> - 2026-09-12 建档：需求与标准整理（功能清单 / 加载器来源标记 / 桥接兼容 / 统一加载顺序 / 覆盖 UI 规范 / 文件级拆解 / 任务列表）。**尚无实现代码**。
> - 2026-09-12（二）§1.1 “配置持久化”行更新：本模组已新增标准 INI 配置文件 `CoopConfig`（`docs/CONFIG.md`），
>   设置中心直接**复用**它（不再自建 key=value 文件）。
> - 2026-09-12（三）按用户决策修订：**与 Core 独立**（自带源码副本 + 独立命名空间，不再往 `OpenNestCore.ModMenu` 放契约，改放 `OpenNestModMenu.API`）；
>   补 §1.2 独立性技术后果；§3.2/§3.3 用**桥源码 + 本机环境实测**替换全部“待实测”（别名策略 / 目录真值 / 哈希证据）；
>   补 §4.2 加载器原生顺序语义（`MelonPriority` + 依赖拓扑）；§E 补 MelonLoader 原生排除语义与 `--no-mods`；§七 改为独立工程布局。
> - 2026-09-12（四）澄清“三个独立模组”（OpenNestCoop / OpenNestModMenu / 桥）互不归属：配置改为**通用格式兼容层**（不拷任何具体模组的配置类，见 §6.2）；
>   本模组自身配置放到**各加载器的社区惯例位置**；§5.1 重写 `sortingOrder` 说明（为何需要 + 固定值 + 可配置，不再说“协商”）。
> - 2026-09-12（五）**T1 骨架已实现**：新建 `src/OpenNestModMenu.API/`（契约工程）+ `src/OpenNestModMenu/`（BepInEx 壳 + Vendor 副本）+ `src/OpenNestModMenu.MelonMod/`（ML 壳）；
>   双端构建 **0 警告 0 错误**、双端部署已验证（`BepInEx\plugins\` / `Mods\`+`UserLibs\`）；`scripts/deploy.ps1` 已接入；§7.1/§7.2 按实际文件更新。
> - 2026-09-12（六）**已开源到独立公开仓库**：<https://github.com/1499501762/Open-Nest-Mod-Menu>（AGPL-3.0）。
>   发布目录 `<ModMenu 公开仓目录>`（独立 git 仓库）；公开版含 `README.md` / `CONTRIBUTING.md` / `docs/MOD_MENU.md`（本文件去掉本机绝对路径后的版本）/ `docs/API.md`（第三方契约指南）+ 三个工程源码。
> - 2026-09-12（七）**T1 游戏内确认（双端）+ T2 完成**：
>   - T1：G 端（BepInEx+桥）`shape=BepInExPlugin` + 侧根识别为 `MLLoader`；D 端（原生 ML）`shape=MelonMod`；两端 `behaviour mounted` / `contract host ready` / 日志目录生成。
>   - T2：新增 `Registry/{ModMenuRegistry,AssemblyScanner,BuiltInSettingsProvider}.cs` + `Core/ModMenuLoc.cs`；**双通道实测**：
>     主动（`from=active`）+ 被动（临时验证：`candidates=1 new=1` → `active=0, scanned=1`，日志 `discovered ... (passive)`）；
>     扫描统计拆分为 `loadFail`（程序集类型加载失败，与提供者无关）/ `typeFail`；§九 待实测 #4 已实测确认。
> - 2026-09-12（八）**T4 覆盖 UI 完成（重设计版）+ 三项实测反馈修复**（本机 G 端每次重启实测）：
>   - ① **来源标记 bug 修复**：原先把**进程级** `info.ViaBridge` 直接套到每个条目 → 列表里所有条目都标 `[桥]`。
>     改为**逐条目判定**（BepInEx 插件 = `Host=BepInEx` / `ViaBridge=false`；MelonMod = 宿主是 BepInEx 且有桥 才 `ViaBridge=true`）；
>     磁盘条目**格式按目录推断**，并在备注里说明是推断（见 §3.1）。实测：BepInEx 插件显示 `[BepInEx]`、MLL 模组显示 `[bridge]`。
>   - ② **点击穿透修复**：新增 `UI/UiInputGuard.cs`——菜单打开时把所有非本模组画布的 `GraphicRaycaster.enabled=false`（实测压制 **59** 个），
>     关闭时**原样恢复**；仅靠全屏 blocker 不够，因为 blocker 只能拦住 sortingOrder 低于我们的画布（实测 Coop 画布 32766 > 我们 32765）。
>   - ③ **界面重设计**：新增 `UI/ModMenuTheme.cs`（调色板/尺寸/字号/pill 小部件）与 `UI/ModMenuDisplay.cs`（条目→文案/颜色映射）；
>     面板 1180×700；标题栏 + 筛选 chip（全部/模组/依赖/禁用，**依赖库不再混进模组列表**）+ 行（来源 pill + 名称 + 版本/状态）+ 右栏标签值详情 + 底栏状态。
>   - ④ **层级抬升**：实测画布 `Cursor Canvas=32767` / `OpenNestCoop_UI=32766` / 本模组 32765 → 右栏详情被联机面板遮住；
>     现在 `UiInputGuard.RaiseAbove` 把本模组画布抬到「其它画布最大值+1」（封顶 32767），比我们高的画布（含游戏虚拟光标）**整体 +1**，
>     保证光标仍在最上且关菜单后全部还原（实测日志 `z-order: ours=32767, raised 1 canvas(es) above us`）。
>   - ⑤ 临时截屏钩子：命令行带 `-onnmm-autoopen` 时启动 6 秒后自动打开菜单（**仅供自动化 UI 验收**，正常启动行为不变）。
>   - 证据：`runlog/modmenu_ui5.png`（列表/详情/中文全部正常，无异常日志）。
> - ✅ **T4 完成**（含上述修复）。
> - 2026-09-12（九）**实测反馈四项修复**（用户在 G 端实机提出；F6 热键确认有效）：
>   - ① **点击穿透的真因找到了**：任务场景里游戏自己的 `EventSystem` 是**未激活**的
>     （实测 `input probe: eventSystems=1 / EventSystem enabled=True module=<none> / current=<none>`）
>     → 我们的 uGUI 按钮根本收不到点击，而游戏自己的“虚拟光标”照旧处理输入 → 看起来就是“点击穿透”。
>     **对策 = 三层**：
>     (a) **自管指针命中**（`ModMenuUI.Hot` + `RectTransformUtility.RectangleContainsScreenPoint` + `Mouse.current`）：
>         不依赖游戏 EventSystem（有 EventSystem 时也不重复触发，`ClickOnce` 按帧去重）；
>     (b) **压制外部 UGUI 射线**（`UiInputGuard`，实测压制 59 个 raycaster）；
>     (c) **世界交互点击拦截**：Harmony prefix 挂 `LookAtTarget.OnClickDown`（本游戏**所有**交互按钮/拉杆的唯一点击入口，
>         结论来自 OpenNestCoop `ButtonClickSync`），菜单打开时返回 false → 跳过原方法。实测日志
>         `game click block installed: LookAtTarget.OnClickDown prefix (6 参重载)`；Harmony 用**反射**拿（不引用 HarmonyLib，双端通用）。
>   - ② **指针在 UI 下层**：游戏会把 `Cursor Canvas` 的 `sortingOrder` 改回 32767（与我们持平，同级顺序不可控，且它可能是 inactive 的）
>     → 层级抬升改为纳入 inactive 画布 + 名字含 cursor 的画布强制在菜单之上，并**每帧重申一次**（`UiInputGuard.TickCursorOrder`）。
>   - ③ **新增启用/禁用按钮（T7 核心）**：`Loaders/ModFileState.cs`（`*.dll` ↔ `*.dll.disabled` 改名，**重启生效**）；
>     详情卡片右上角的按钮（禁用 → 启用）+ 底栏操作结果提示；菜单自身不允许被禁用（避免重启后找不到菜单）；
>     实测几何日志确认按钮存在：`geometry: ... toggle=pos=(1434,1748) size=(176x20) text='Disable (restart to apply)' clickable=True`。
>   - ④ **改用语言键（不再硬编码双语）**：新增 `Loc/{LocDefaults,LocFile}.cs` + `Core/ModMenuLoc.cs` 改写为键门面，
>     外部语言键文件 `BepInEx/config/OpenNestModMenu.lang.ini`（与配置文件同目录；MLL 侧在 `UserData/`），
>     `[zh]`/`[en]` 段 + 2 秒热重载 + 新增键自动补齐（保留用户文本）。实测：`lang: created (defaults) …` → 下次 `lang: loaded …`（见 §5.4）。
>   - ⚠️ 仍待用户实机确认：真实鼠标下点击手感（自管命中）、启用/禁用是否重启后生效。
> - ⏳ **T5（ESC blocker 部分）、T6、T8–T12 未开始**。
> - 2026-09-12（十）**T13 运行时启停（热禁用 / 热恢复）已实现并双端实测**：
>   - 新增 `Loaders/ModRuntimeState.cs`（运行中状态表，与文件状态**分维度显示**）+ `Loaders/ModFileState.cs` 编排（运行中 + 文件改名）+ `ModMenuRegistry.SetRuntimeDisabled`（契约热切换）。
>   - 能力：**MLL 模组（含经桥）可当场停/起**；受管模组可自实现可逆热切换；**BepInEx 插件不做**（无卸载 API，避免半个模组）。
>   - 实测（临时心跳模组作靶，**已删**）：G 端（桥）与 D 端（原生 MLL）均通过 —— 靶模组心跳在停用后**真的断了 ~5 秒**、恢复后重新 `initialized` 并续跑；
>     `initialized` 计数 2（首次 + 恢复）。日志证据比如 `selftest(STOP): stopped now (Harmony patches removed, callbacks unsubscribed)` / `selftest(RESUME): resumed now (re-registered)`。
>   - 坑（已写入 §E.1）：`LoadMelons()` 不注册（必须 `RegisterSorted`）；桥环境下复用内存里的 `MelonAssembly` 无效 → 改走 `LoadMelonAssembly(path)` + **结果校验**。
>   - UI：按钮文案分档（`停用（立即）` / `禁用（重启生效）` / `启用（立即）`），详情新增“运行中”一行，底栏显示操作结果。
> - ⏳ **T5（ESC blocker 部分）、T6、T8–T12 未开始**。
> - 2026-09-12（十一）**据实测反馈修正 4 处**：
>   - ① **磁盘条目格式改“读元数据”**：新增 `Loaders/AssemblyFormatProbe.cs`（`System.Reflection.Metadata` 只读 PE 元数据，沿基类链找模组基类；不加载程序集、不执行代码；带 mtime+size 缓存）。
>     无模组类型 ⇒ 列表标签 `dep`（性质 = `Dependency`）；有模组类型 ⇒ 按真实格式显示，即使它没加载成功。实测：`LiteNetLib`→依赖、`SomeMod.CustomRecords`→MLL 模组但未加载。
>   - ② **命名统一**：列表标签与宿主标记里的“ML bridge / 经桥”全部改为 **MLL**（`PillBridge` / `HostBridge` / 契约 `ModEntryInfo.HostLabel` / `LoaderInfo.HostLabel`）。
>   - ③ **右栏两列对齐**：标签原来用 `TextAlignmentOptions.Left`（垂直居中）、值用 `TopLeft` → 同一行看起来错位；改为两边都 `TopLeft`。
>   - ④ **指针改自绘层**（`UI/UiCursorOverlay.cs`）：压在游戏光标之上这条路实测压不住（BepInEx 端仍复现“指针在菜单下层”），
>     改为菜单打开时在**我们画布最上层**自绘一个跟随鼠标的指针（精灵优先抄游戏光标，拿不到时纯色兜底）。实测日志 `sibling=2/2`（最上层）+ `late tick (menuOpen=True)`。
> - 2026-09-12（十二）**层级改为“抄联机模组”的定稿 + 点击穿透再补一层“组件级交互锁”**（用户实测反馈：先“层级对了”，然后“鼠标有穿透”）：
>   - ① **层级定稿（不再自绘兜底为常态）**：`UiInputGuard.DockLayers` 改为联机模组/官方联机的权威做法 ——
>     本模组画布 = **32766**（严格低于游戏光标层 32767）、**从不修改光标画布**（游戏每帧自己写回 32767 / 隐藏时 -32768），
>     只把其它 `sortingOrder ≥ 32766` 的**非光标**画布降到我们之下（保持彼此相对顺序），关菜单还原。
>     `NeedsOwnPointer()` 只在“既没有活跃光标画布、硬件光标又隐藏”时才自绘指针（否则必定双指针）。
>     依据：`OpenNestCoop.UI.CoopUIManager.BuildCanvas()` 与官方联机 `MultiplayerMenu` 都用 32766 且不碰光标画布。
>   - ② **点击穿透第 ⑤ 层 = 组件级交互锁**（`UiInputGuard.LockInteractions`，抄联机模组 `CoopUIManager.ApplyMenuState()`）：
>     菜单打开时 **禁用场景里所有交互组件**（基类链含 `Interactable` 的派生类 + 类型名 `LookAtTarget`，实测 **27** 个）、
>     调 `FirstPersonController.SetFrozen(true)`；关菜单**原样恢复**（全屏透明 blocker 本来就有 = `UiKit.MakeBlocker` 压暗层，与联机模组 `_blocker` 等价，未重复添加）。
>     为什么前四层不够：压制 raycaster 只拦 UGUI EventSystem 那条路；拦 `OnClickDown` 只拦“点了这个方法”那条路
>     （联机模组已记录部分点击**不走** `OnClickDown`）；世界交互多半由**游戏自己**从光标位置轮询/射线 ——
>     组件级禁用与“谁触发、走哪条路”**无关**。
>   - ③ **实测证据（G 端 / BepInEx+桥）**：`interaction lock: disabled 27 component(s) [LookAtTarget=27] (total locked=27, scan=9.0ms)`、
>     `player frozen (FirstPersonController.SetFrozen(true))`、probe `current=EventSystem clickBlock=True interactionLock=True`；
>     首扫（菜单比任务场景的交互物先出现）记 `interaction lock: nothing to lock in current scene(s) (scanned 13213 MonoBehaviour(s))`，5s 后补上。
>     ⚠️ 同期 `game click blocked (LookAtTarget.OnClickDown)` **一次都没出现** → 说明点击路径就在 `LookAtTarget` 组件自己内部
>     （组件被禁用后原方法根本不会被调用）→ 组件级禁用才是那条“路径无关”的层。
>   - ④ **类型解析加固**：`LoaderReflect.FindType` 增加**按简单名兜底扫描**（+ 缓存 + 未命中诊断 `type NOT found: '…' | assemblies=N [...]`）。
>     原因：D 端（原生 MLL）`FindType("LookAtTarget")` 曾返回 null → ③ 层整层**静默失效**；同时点击拦截改为失败后 5s 节流重试。
>     实测根因：**MLL 端游戏类型带 `Il2Cpp` 命名空间前缀**（`'LookAtTarget' -> Il2Cpp.LookAtTarget`、`'VirtualCursor' -> Il2Cpp.VirtualCursor`，
>     都在 `MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll`），G 端全名就是 `LookAtTarget` → 修后 D 端也装上（`game click block installed: Il2Cpp.LookAtTarget.OnClickDown`）。
>   - ⑤ 临时脚本 `%TEMP%\onnmm-shot.ps1` 增加 `-FullScreen`，并在启动时 `SetProcessDPIAware()`（否则窗口 rect/屏幕拷贝按逻辑像素 → 截出来的图整体偏移）。
>   - ⚠️ **待用户实机确认**：菜单打开时点不到背景东西（本轮新增的组件级交互锁这一层）。
> - 2026-09-12（十三）**据实测反馈修正 3 处：禁用态 BepInEx 插件被标 `dep` / 启用要重启 / BepInEx 插件能否热加载**：
>   - ① **探测基类名修复**：BepInEx 6 **IL2CPP** 的插件基类是 `BepInEx.Unity.IL2CPP.BasePlugin`，原探测表缺它 →
>     **文件被禁用的 BepInEx 插件（本次没加载 → 只能靠磁盘探测）被标成 `dep`**。改为“命名空间以 `BepInEx` 开头 + 类名 ∈ {BasePlugin,BaseUnityPlugin,BepInExPlugin}”
>     （比枚举全名耐版本变化）。实测：`[BepInEx] ONNMM Hotload Test 1.0.1 | BepInEx 插件 | 已禁用`（不再是 dep）。
>   - ② **启用不再需要重启（顺序 bug）**：旧实现“先热恢复、后改名” → 热加载时文件还叫 `.disabled`，加载器按 `*.dll` 发现 ⇒ 永远找不到
>     → 文件被禁用的模组**只能靠重启启用**。改为 **启用：先改名 → 再热加载；停用：先停运行中 → 再改名**。
>   - ③ **BepInEx 插件热加载（技术上可实现，已实测）**：`IL2CPPChainloader.Instance.LoadPlugins(new[]{目录})`（官方路径，`ModifyLoadOrder` 按 GUID 去重 ⇒ 幂等）。
>     ⚠️ 传**目录**（`TypeLoader.FindPluginTypes` 内部是 `Directory.GetFiles`）；依赖不满足会被 BepInEx 自己跳过（读回 `Plugins` 校验结果）。
>     **实测（临时测试插件，已删）**：启动时文件为 `.disabled` → 自测钩子触发启用 → `hot-loaded BepInEx plugin (guid=onnmm.hotload.test)`
>     → 插件自己打 **心跳 #1..#16**（真在跑）→ 列表变 `loaded`。**停用仍不可能**（`BasePlugin.Unload()` 默认 `false` 且无调用点；程序集不可卸载）。
>   - 新增 TEMP-VERIFY 钩子 `-onnmm-selftest-toggle=<id>`（8s 启用 / 20s 停用，走真实 `ModFileState.TryToggle`）；UI 文案：可热加载时显示 `启用（立即）`。
> - 2026-09-12（十四）**BepInEx 插件“软停用”（实验性热卸载）已实现并逐条实测**（用户追问“热卸载禁用能否实现”）：
>   - 结论：**能撒 patch + 销毁组件 + 调它的 `Unload()`；但做不到真卸载**（程序集不可卸载、静态状态/后台线程/游戏侧订阅可能残留）。
>     所以 UI 标 **`停用（实验性）`**、状态栏写明残留风险，**并仍然改文件名** → 重启后彻底干净；若插件自己实现了 `Unload()` 且清理干净，则等同真停用。
>   - 实现：`LoaderReflect.TryBepInExSoftStop` —— ①调 `Unload()`（仅当插件重写；BepInEx 默认 `=> false`）；
>     ②撒 patch：`GetAllPatchedMethods` → `GetPatchInfo` → 对「补丁方法属于该程序集」逐条 `Unpatch(目标, 补丁方法)`，之后**复量一次**（`left=0` 自检）；
>     ③销毁组件：`Resources.FindObjectsOfTypeAll<MonoBehaviour>()`（含隐藏对象）+ `Object.FindObjectsOfType` 两个来源去重。
>   - **实测（临时测试插件：2 条 patch + 1 个 `Update` 组件 + 1 个后台线程，已删）**：
>     `bep soft-stop: unloaded=1(False) unpatched=2 components=1 left=0`；时间线显示组件心跳在停用后**不再出现**、
>     插件自己的 `Unload()` 被调到（它自己收掉了线程）。0 错误。
>   - 三个踩坑（已写进 §E.1）：① HarmonyX 2.10 的 `GetPatchInfo` 返回 **`Patches`**（字段是只读集合，不是数组）→ 原先读到 0 条；
>     ② 去重必须按 **(目标方法, 补丁方法)** 成对 → 否则同一补丁方法挂多个目标时漏撒；
>     ③ 插件组件挂在 `BepInEx_Manager`（`HideFlags.HideAndDontSave`）上，`Object.FindObjectsOfType` **看不见** → 必须叠加 `Resources.FindObjectsOfTypeAll`。
> - 2026-09-12（十五）**T5（ESC 拦截）+ T6（设置中心）+ T8（统一加载顺序）完成，均在本机 G 端实测取证**：
>   - **T5** 新增 `UI/UiEscapeGuard.cs`：菜单打开时**禁用游戏自己的 ESC 触发 action**（`EscapeMenuToggleUnityEvent.toggleAction.action` / `subscribedAction`，
>     逐条记原状态、关闭时**只还原我们动过的那几个**），已打开的实例 `ForceClose(false)`；ESC 用来关我们自己的菜单（`ModMenuUI.Tick` 读 `Keyboard.current.escapeKey.isPressed` 自算边沿）。
>     **不**往场景里塞 `EscapeMenuOpenBlocker`（要赌 `escapeMenuTag` / `GetEscapeMenu()` 能解析到菜单，赌输就是静默无效）。
>     实测：`escape guard: disabled 1 game escape action(s) across 1 menu instance(s)`；菜单打开期间 `stillEnabled=0 gameEscapeOpen=0`；
>     注入真实 ESC 按键后 `t56#8 AFTER-ESC closed=True … stillEnabled=1`——我们的菜单被 ESC 关掉，**且游戏暂停菜单没有打开**，关闭时被禁的 action 已还原。
>   - **T6** 新增设置中心：`Config/{IniDocument,ConfigLocator,ModConfigStore}.cs` + `UI/{UiSetting,PageCollector,ModMenuSettingsView}.cs`；
>     右栏新增 `[详情][设置]` 两个页签（页眉共用，切页不重进菜单）；设置页合并**两个来源**（第三方 `IModMenuPage` 声明页 + 通用配置文件扫描，冲突时都列出）；
>     控件族 = 开关（点值翻转）/ 数值（± 步进，按声明范围钳制）/ 枚举（点值循环）；文本与密钥**只读**（中文 IME 的坑，§1.1）。
>     **写回最小侵入**（`IniDocument`）：只改“值”那一段、保留注释/键名/`=`空格式样、值没变就不落盘、首次改动留一份 `.bak`。
>     实测：`settings: 'OpenNestCoop' file='…\BepInEx\config\OpenNestCoop.cfg' items=7 sections=4` →
>     写回 `stepped [1] 'CatSync' Bool 'true' -> 'false'` → 磁盘复核 `disk: 'OpenNestCoop.cfg' [Sync] Sync.CatSync = 'false'`；
>     与 `.bak` 逐行比对 **只有这一行不同**；底栏提示 `已写入 OpenNestCoop.cfg`。
>   - **T8** 统一加载顺序：`Loaders/ModOrderTable.cs` + `Registry/ModInitScheduler.cs` + 契约新增**可选**接口 `IModMenuInit`（不塞进 `IModMenuProvider`，避免弄坏已有第三方实现）。
>     有效顺序 = ① 基线（MLL `Priority` 降序 → Id 升序）→ ② 用户显式条目前置（`BepInEx\config\OpenNestModMenu.order.ini`，10/20/30…）→
>     ③ **依赖修正**（`DependsOn` 声明的模组若排在使用者之后则前移，逐条记日志）+ 不兼容成对检测（只告警，不擅自禁用）→ ④ 受管初始化（`AutoInit=true` 且实现 `IModMenuInit` 的提供者按最终顺序调 `OnInitialize()`，每项一次）。
>     详情页新增“顺序”一行（`#n/总数 · 用户指定/加载器决定`）+ `上移/下移` 按钮（点一下把整表写成显式编号，**用户看到的顺序就是下次启动的顺序**）。
>     实测：`effective order (10): … #5 LiteNetLib < #6 OpenNestCoop … explicit=0` → 上移 → `swap: idx=5 target=4 (dir=-1) …` →
>     `order table written: 10 explicit entr(ies)` → `effective order (10): … #4 SomeMod.CustomRecords < #5 OpenNestCoop < #6 LiteNetLib … explicit=10`。
>   - 两个踩坑（已写进 §E.1）：① 顺序表编号是**按位置**写的（`(i+1)*10`）→ 文件里“相邻两条编号看起来没变”不代表没生效，要看 `effective order` 那行；
>     ② 设置项的 `SettingItem.Key` 是 `Section.Key` 约定而文件里只有短键 → `IniDocument.Find` 增加“去掉最后一段前缀再找一次”的容错（否则复核永远报 `<missing>`，已踩）。
>   - 视觉取证：`runlog/modmenu_t6_settings.png`（G 端，`-onnmm-selftest-t56-ui=OpenNestCoop`）——右栏 `Details | Settings` 页签、`Settings` 选中，
>     内容显示 `Config file: OpenNestCoop.cfg` 分组行 + `CatSync / RecordPlayerSync / CalculateButtonSync` 开关 chip + `Port` 的 `- 29507 +` 步进 + 文本项只读，下方分页与底栏统计均正常。
>   - 观察项（未处理，待复现）：菜单打开期间日志里每 ~1.5s 出现一次 `click: hot=-1 row=N`（列表选中项会被挑走）——与 `UiInputGuard` 的 1.5s 重申节奏同步，怀疑是**游戏虚拟光标**在写合成鼠标状态；不影响上面三项验证，但值得单独查（已记入本文件 §5.2 末的观察项）。
> - ✅ **T5 / T6 / T8 完成（本次）**；⏳ **T9–T12 未开始**（T7 / T13 已如前述完成）。
> - 2026-09-12（十六）**T10（诊断面板）+ T11（重复加载/双 Harmony 呈现）+ T12（双端部署/打包）完成；T9 暂缓**（用户：T9 已交由另一个 Agent 写 UIkit 模组，等它完成再做）：
>   - 跳过的原因同步到任务表：T9 要挂到 `OpenNestUIKit` 的入口上，先等那边定稿（避免两边各写一套注入）。
>   - **T10 新增 `UI/ModMenuDebugView.cs`**：右栏第三个页签 `[详情][设置][诊断]`（`ModMenuUI.SelectRightTab` 改索引式）、行池 12 行/页 + 分页 +`立即刷新`，可见时**每秒重建**。分组：
>     `[帧性能]`（FPS/平均/最差帧 + 测量点 ms/s top 4）、`[加载器]`（宿主/桥/双方版本/interop/双 Harmony/全部惯例路径）、
>     `[模组清单]`、`[注册表]`（含最近扫描的 assemblies/candidates/new/loadFail/typeFail/ms）、`[统一顺序]`（含顺序表路径与 `#n` 明细）、
>     `[重复加载]`、`[能力探测]`（EventSystem/输入模块/点击拦截/交互锁/层级/指针来源）、`[日志/配置]`。
>   - ⚠️ **发现并修好一个真缺口**：`FrameProfiler` 之前**没人喂帧耗时**（`RecordFrameMs` 全库无调用）→ FPS 永远是 0；
>     现在 `ModMenuBehaviour.Update` 每帧喂 `unscaledDeltaTime*1000`，并新增 5 个测量点（`inventory`/`registry.scan`/`ui.refresh`/`settings.scan`/`debug.collect`）。
>     实测（G 端桥）：`FPS: 81.9 FPS avg 12.21 ms worst 24.5 ms`；D 端（原生 MLL）：`119.9 FPS avg 8.34 ms` + `inventory: 1.31 ms/s`。
>   - ⚠️ **另一个实测到的假信号（已修正）**：双 Harmony 原本只看“两个类型名能不能解析”——但 MelonLoader 自带的 0Harmony 也把命名空间叫 `HarmonyLib`
>     → 原生 MLL 端两个名字指向**同一个程序集**，被误判成 dual。现要求两者来自**两个不同程序集**（比 Location，退到程序集名）。
>     **实测结论（与之前文档的假设不同）**：本机两端都只有**一份** Harmony（`0Harmony 2.10.2` + `Il2CppInterop.HarmonySupport`，另加 HarmonyX 的 85~87 个 `HarmonyDTFAssemblyN` 动态工厂）
>     → “双 Harmony 风险”（§3.3）在当前双端环境下**不成立**，面板会标 `no`；仍然保留“只用宿主侧那一份、不做跨侧归因”的约定。
>   - **T11**：重复加载的判定（已在 `ModInventory`）现在直接呈现在 `[重复加载]` 分组（无重复时明说）；Harmony 归属给出**进程内程序集实名 + 版本**证据行（过滤掉 DTF 工厂，只留 6 条 + `(+ N DTF)`）。
>   - **T12**：`scripts/deploy.ps1` 已含双端部署（本次实际使用：G 端 `BepInEx\plugins\`、D 端 `Mods\`+`UserLibs\` 均 `Deployed` 成功）；
>     `scripts/package.ps1` 新增两个**只装菜单本体**的包，实测产出：
>     `OpenNestModMenu-0.1.0-BepInEx.zip`（`BepInEx\plugins\OpenNestModMenu.dll` + `.API.dll` + README）、`OpenNestModMenu-0.1.0-MelonLoader.zip`（`Mods\` + `UserLibs\` + README）。
>   - **四组合实测现状（诚实口径）**：已实测 ✅ **G 端（BepInEx + 桥 + BepInEx 版菜单）** 与 ✅ **D 端（原生 MelonLoader + MLL 版菜单）**，均 0 error；
>     “纯 BepInEx（无桥）”与“桥里的 MLL 版菜单”两种组合**本机没有对应环境**（G 端带桥、D 端无 BepInEx）→ 未实测，不写成已验证。
>   - 视觉/日志取证：`runlog/modmenu_t10_debug.png`（G 端）、`runlog/modmenu_t10_debug_mll.png`（D 端）、`runlog/modmenu_t6_settings.png`（设置页）；
>     打开诊断页后 2.5s 会把整屏快照写进 `modmenu.log`（`debug snapshot ('open')`），离线也能核对。
>   - **字体缺字修复（用户反馈“ConfigFile 前面那个符号变方框”）**：`ModMenuSettingsView` 的分组行不再用 `▸`（U+25B8，TMP 字体没有）→ 改纯 ASCII `[ 分组 ]`；
>     分隔行不用制表符 `─`（U+2500）→ 改 24 个 ASCII `-`；数值 `−`（U+2212）→ 改 ASCII `-`。诊断页同样只用 `[ 分组 ]`。
>     另：用脚本核对“代码里引用的每个语言键都已定义”（本次 124 个全齐）——缺键会直接显示键名。
> - ✅ **T10 / T11 / T12 完成（本次）**；⏳ **T9 暂缓（等 OpenNestUIKit）**。
> - 2026-09-12（十七）**据用户实测反馈修四处布局/交互**（细分项见下；均已在本机 G 端截图验证）：
>   - ① **右栏内容块没铺满背景块高度**：设置页/诊断页的“每页行数”原来写死 9/12 → 底部留一大片空。
>     现改为**按宿主高度算**（`ModMenuSettingsView.FitRows`：`(host 高度 - 分页行高) / 行高`），分页行紧跟最后一行
>     → 实测设置页 **15 行/页**、诊断页 **17 行/页**，内容直达卡片底部。
>   - ② **左侧列表改为按“统一加载顺序”排**（原来按 Id 字母序）：`ModInventory.Refresh` 在 `ModInitScheduler.Apply` 回填 `Order` **之后**按 Order 重排
>     （不在顺序里的排最后、Id 做稳定兜底）。实测截图（G 端，12 条）：`#1 BepInEx.MelonLoader.Loader… < #2 SomeEnemyTracker < #3 SomeMod < #4 Open Nest Co-op < … < #11 Open Nest UIKit < #12 OpenNestUIKit.API`，
>     与诊断页 `[统一顺序]` 的 `#n` **逐条一致**。
>   - ③ **分页行折行错误**（屏幕上出现 `Page 1/1 8` / `item(s)` 两行）：分页文案宽度 120→170 并**关自动换行**（`enableWordWrapping=false` + 省略号），
>     诊断页同行、并把右侧状态/刷新文案起点右移（避免两者叠在一起）。
>   - ④ **顺手修掉一个重叠 bug**：两个页宿主 `SetVisible` 的“状态没变就早退”分支漏了把 GameObject 状态扳正
>     → Build 后首次 `SetVisible(false)` 不生效，设置页与诊断页**同时可见**（两个分页行一起出现）。现“状态没变”也会强制同步 GameObject。
>   - ⑤ **点击闸（缓解“幻影点击”）**：裸边沿检测会把游戏侧写入的**合成鼠标状态**当点击（实测菜单打开期间每 ~1.5s 一次、位置还在漂，
>     会把列表选中项自己挑走，也可能误触按钮）。现要求：按下**连续 2 帧** + **上一帧就悬停同一目标** + **≥180ms 冷却** + **必须观察到抬起**。
>     实测：自动会话 40s 内被接受的点击 **0 次**（修前约每 1.5s 一次）；注入的真实 OS 点击仍照旧被接受（`click: hot=0`）。
>     ⚠️ **但仍未根治**：D 端（原生 MLL，无任何其它 UI 模组）也偶发 `click: hot=-1 row=-1`（没有目标时无害）→ 观察项保留在 §5.2 末。
>   - 取证图：`runlog/modmenu_layout_list.png`（列表顺序=加载顺序）、`modmenu_layout_settings.png` / `modmenu_footer_settings.png`（铺满高度 + 分页单行）、
>     `modmenu_t10_debug.png` / `modmenu_footer_debug.png`（诊断页）、`modmenu_t10_debug_mll.png`（D 端）。
> - 2026-09-13（十九·再续）**改成“一页 + 两组页签”（对齐原界面，不再层层嵌套）**（用户：“层层嵌套的显示效果太差，不如原来两个模组的布局方式”）：
>   UIKit 版现在**只有一页**：筛选页签（全部 / 模组 / 依赖 / 已禁用 ↔ 原来的四个 chip）→ 模组列表（点一行 = 选中，行内带类型/状态）
>   → 选中项的页签（详情 / 设置 / 诊断 ↔ 原来的右栏三页签）→ 底部扫描统计；不再“每模组一页 + 再点进去”。
>   页签用契约新增的 `UiPageDef.Tabs(...)`（渲染成真页签栏）；叶子 ESC 条目点一下**直开这一页**。
> - 2026-09-13（十九·续）**UIKit 版模组页补齐“原有功能”**（用户：“接管后的 UI 没实现原来的所有功能，不反对重新布局但要实现原功能”）：
>   每个模组一页 = **详情**（版本/宿主/格式/路径/配置/依赖/不兼容/备注/加载与受管状态）+ **操作**（磁盘启用停用、
>   顺序上移·下移、运行时启用停用（模组声明支持时）、恢复默认设置、重跑受管初始化队列）+ **设置行**（与自带设置页同源）；
>   根页加 **筛选页**（全部 / 模组 / 依赖 / 已禁用，对应自带界面右上的四个 chip）与 **刷新清单**；
>   诊断页除了扫描统计还逐条列出模块状态。刷新走契约的 `UiKitHost.Refresh()`（原地重建当前页），
>   不再“重新注册 provider”（那会连带把原生入口重注入一遍）。
> - 2026-09-13（十九）**T9 完成：菜单入口挂到 OpenNestUIKit（软依赖，缺它则回退）**：
>   环境里装有并加载了 `OpenNestUIKit` → 本模组的**菜单改由 UIKit 渲染**（一个声明式页面：模组列表 + 每个模组的设置行），
>   **原生 ESC 菜单里的入口也由 UIKit 统一注入**（本模组不再自己往原生容器里塞按钮）；
>   没有 UIKit → 原样走自带覆盖 UI（`ModMenuUI`），行为一行不变。
>   实现：`UI/UIKitIntegration.cs`（**反射探测**已加载程序集 + `[MethodImpl(NoInlining)]` 隔离契约类型调用，
>   启动路径上不触碰 `OpenNestUIKit.API`，所以没装 UIKit 时不会 `TypeLoadException`）；
>   `UIKitIntegration.Tick()` 由 `ModMenuBehaviour.Update` 每帧驱动（前几次探完就停，接上后零开销）；
>   `ModMenuUI.Toggle()`（F6 / 开关按钮）→ 接上 UIKit 就交给它；`Open()` 保持原样（自测路径不变）。
>   行内容与自带设置页**共用一份实现**：新抽 `UI/ModMenuSettingsSource.cs`（`ModMenuSettingsView.Bind` 也改调它）——
>   避免“迁移后的界面少一行”这类漂移；映射只把**能安全写回**的行做成可交互控件（bool / 有范围的数值 / 枚举），
>   其余只读展示（与自带界面 v1 口径一致，文本编辑的中文 IME 坑见 §1.1）。
>   工程侧：csproj 对 `OpenNestUIKit.API` 加 `Private="false"` 引用（只编译期用，**不**随本模组部署）。
> - 2026-09-12（十八）**修复“原生 MelonLoader 端读不到模组配置” + 匹配结果落盘**（用户实测反馈：`D:\...\UserData` 路径下的配置没被读取）：
>   - **真因不是路径、而是文件名**：`ModMenuPaths.MelonUserDataDir` 在原生 MLL 端 = `<Game>\UserData` ✓ 正确；
>     但 D 端 Coop 的程序集叫 `OpenNestCoop.MelonMod`，而它的配置叫 `UserData\OpenNestCoop.cfg`
>     → 旧代码只找 `<id>.cfg` / `<显示名>.cfg`，**永远找不到**。
>   - **修法（最终定稿：`Config/ConfigLocator.cs` 按名字匹配，不对文件名做任何“改写/前缀猜”）**：
>     ① 加载器真值（BepInEx `GUID` → `<GUID>.cfg`）；② 字面同名（`Id` / 显示名 / 磁盘文件名 + `.cfg`）；
>     ③ **按模组名匹配**：文件名与**模组名**（`ModEntryInfo.DisplayName` = 模组自己声明的名字，如 “Open Nest Co-op”）
>     归一化（小写 + 去空格/点/下划线/连字符）后**完全相等**即命中；④ `MelonPreferences.cfg` 的同名 `[分类]`。
>     > 中途曾实现过“去掉 `.MelonMod` 后缀 + 前缀模糊”的启发式，**已按用户要求废弃**：
>     > 不靠变形文件名，只认“名字一样”（前缀模糊本来就容易误配，且需要黑名单/覆盖率等一堆护栏）。
>     仍保留一条最小护栏：文件名若被**别的条目“字面同名”**占着，则不抢（那一条自己能精确命中）。
>   - **匹配一次就存盘（用户要求）**：新增 `Config/ConfigMap.cs` —— 推断命中（按模组名 / MelonPreferences）会把
>     `模组 Id = 配置文件路径` 写进 `OpenNestModMenu.configmap.ini`（与本模组配置同级，普通 INI，可手改可删行），
>     下次启动直接 `matched by saved`，**不再重新匹配**；指向的文件不存在则自动失效重猜。
>     （字面同名不入表：那是确定的，存下来只会把表写成噪声。）
>   - 另：`ModInventory` 刷新时会用同一套逻辑把解析到的路径回填进 **`ModEntryInfo.ConfigFile`**（公开契约字段），
>     诊断页 `[ 日志/配置 ]` 新增 `配置映射` 一行（条目数 + 文件路径）。
>   - **实测**（D 端，原生 MLL，先删掉旧映射表强制重匹配）：
>     `config for 'OpenNestCoop.MelonMod': …\UserData\OpenNestCoop.cfg  (matched by 模组名 'Open Nest Co-op')`
>     → `config map saved: …` → 下一轮 `config map loaded: 1 entr(ies)` + `(matched by saved)`；
>     设置页 `items=7 sections=4` / `bind: 8 item(s)`；0 error。
>     G 端（BepInEx）：`matched by exact`（无回归）；桥条目（名字 ≠ `BepInEx`）不会再误配 `BepInEx.cfg`。

---

## 一、定位与边界

| 项 | 结论 |
|---|---|
| 产物 | **独立模组** `OpenNestModMenu`（双端）；**不并入 OpenNestCoop，也不依赖其程序集**，两者可共存 |
| 复用方式 | **源码级复用**：把 `OpenNestCore/UI/*`（`INativeUiService` / `UiKit` / `UiSpriteBank`）、`OpenNestCore/Logging/CoopLog.cs`、`OpenNestCore/FrameProfiler.cs` **拷入本模组**并改用独立命名空间（§1.1） |
| 契约归属 | 第三方注册契约放 **本模组自己的契约程序集** `OpenNestModMenu.API.dll`（纯 .NET、无 Unity/游戏依赖），**不放进 Core** |
| 第三方依赖方式 | **软依赖**：第三方只引用 `OpenNestModMenu.API.dll`；未安装 ModMenu 时自身静默跳过（见 §6.1） |
| v1 UI | 覆盖 UI（自制 UGUI + 纯色）；原生素材**代码保留但不激活**（`UseNativeSkin=false`） |
| 不引用 | 本模组**不引用** `OpenNestCoop.dll` / `OpenNestCore.dll`；游戏侧桥接自带（§3-B） |

### 1.1 自带副本清单（拷什么、剥什么）

| 来源 | 目标命名空间 | 处理 |
|---|---|---|
| `src/OpenNestCore/UI/UiKit.cs` | `OpenNestModMenu.UI` | 直拷（`using OpenNestCore.UI` → 本模组命名空间） |
| `src/OpenNestCore/UI/INativeUiService.cs`（含 `NativeUi` 门面） | `OpenNestModMenu.UI` | 直拷（契约 + 注册表） |
| `src/OpenNestCore/UI/UiSpriteBank.cs` | `OpenNestModMenu.UI` | 拷入但 **v1 不激活**（不调用 `Load/CaptureFromScene`） |
| `src/OpenNestCore/Logging/CoopLog.cs`（+ `ILogger`） | `OpenNestModMenu.Logging` | 直拷 |
| `src/OpenNestCore/FrameProfiler.cs` | `OpenNestModMenu.Diagnostics` | 直拷 |
| `src/OpenNestCoop/UI/IronNestNativeUi.cs` | `OpenNestModMenu.Bridge` | **移植精简版**（引 `Assembly-CSharp`，只能放模组侧；`#if !MELONLOADER` 排除 `GetFont`） |
| `src/OpenNestCoop/Core/Config/CoopConfig.cs` | — | **不拷**。仅作为“社区 INI 写法”的**参考样例**（`##` 注释 + `[Section]` + `Key = Value` + `## 类型: bool | 默认: true`）；本模组自研**通用格式读写层**（§6.2） |

**必须自写**（Core 内没有）：

| 能力 | 说明 |
|---|---|
| 加载器探测 / 清单 / 重复加载检测 | 全新（§三） |
| 启停（文件重命名）+ 顺序表 | 全新（§四） |
| 第三方注册契约 + 初始化调度器 | 全新（`OpenNestModMenu.API`） |
| 调试面板 | 自写（用拷来的 `FrameProfiler`） |
| **通用配置兼容层**（适配各模组的 cfg 格式与社区惯例） | 全新（§6.2）；不拷任何具体模组的配置类 |
| 文本输入 / 中文 IME | v1 **不做**（`CoopInputBox` 的 IME 复杂度不在 v1 承担）；数值用滑条/步进 |
| 独立文件日志（`ModLog`） | v1 先用 `CoopLog` + 等级；需要时再拷 `src/OpenNestCoop/Logging/ModLog.cs` |

### 1.2 独立性的技术后果（必须遵守）

1. **不共享静态状态**：本模组内拷入的 `NativeUi` 注册表与 `CoopLog` 门面，与 OpenNestCoop 内联的那一份是**两个不同的 CLR 类型**（不同程序集）→ 各管各的。因此：
   - 本模组**必须自己注册**游戏侧 `INativeUiService` 实现（不能指望 OpenNestCoop 的桥接生效）；
   - OpenNestCoop 的 `CoopLog` 等级/路由设置**不影响**本模组（反之亦然）→ 配置项各自独立。
2. **命名空间独立**：一律用 `OpenNestModMenu.*`，避免与同进程内 OpenNestCoop 内联的 `OpenNestCore.*` 同名类型造成阅读/诊断歧义。
3. **不引入联机耦合**：不引用 `CoopRuntime` / `NetConfig` / 任何网络与同步类型。
4. **契约程序集 `OpenNestModMenu.API.dll`** 不含 Unity 依赖 → 第三方可安全引用（双端同一份）。

---

## 二、功能清单

### A. 框架与生命周期
- 双端入口壳：`Plugin.cs`（BepInEx 6 `BasePlugin`）+ `MelonModEntry.cs`（MelonLoader，`#if MELONLOADER`）
- **无 Core 链接**：自带拷贝的 UI/日志源码（§1.1）；`OpenNestModMenu.csproj` 只编自身目录（`src/OpenNestModMenu/**` + `src/OpenNestModMenu.API/**`）
- 日志后端注入：`CoopLog.SetLogSource(...)`（BepInEx `ManualLogSource` / MelonLoader 适配器）
- 排序靠前加载（见 §5.1），最早建立注册表与调度器
- 关停清理：解 ESC blocker、销毁 Canvas、取消语言事件订阅；`AssetBundleIron` 未被使用（v1 不加载 bundle）

### B. 注册 / 初始化顺序 / 生命周期
- `IModMenuProvider` / `IModMenuPage` 契约 + `ModMenuRegistry` 门面（**本模组契约程序集 `OpenNestModMenu.API`**）
- 加载器来源探测（`LoaderDetector`，见 §三）
- 运行时清理 `AssetBundleIron.UnloadAll()`、销毁 Canvas、解 ESC blocker
- 配置：自研**通用格式读写层**（§6.2）；本模组自身配置写到**各加载器的社区惯例位置**（BepInEx 版 → `BepInEx\config\OpenNestModMenu.cfg`；MLL 版 → `<GameDir>\UserData\OpenNestModMenu.cfg`），首次运行生成默认、损坏回退默认

### C. 打开/关闭入口（两者都要）
- **热键**：`UnityEngine.InputSystem.Keyboard.current`。**默认 F6**，可配置。
  ⚠️ **必须避开 F7–F10**：`Debug/DiagCycleController` 占用 F9（帧/网络/交互循环）、`InteractableNameTool` 占用 F10。
- **原生菜单注入**：patch `MainMenuStateRelay.HandleMainMenuLoaded` postfix（或订阅 `MissionManager.MainMenuLoaded`）→
  注入"模组菜单"按钮到设置菜单 `Apply` 按钮右侧 + **遍历全部 `ESC Menu Buttons` 实例**（主菜单与游戏内暂停菜单是两个实例）。
  复用 `MainMenuEntry` 的已验证结论：**slot 整体让位**、**模板按钮按名精确选**、**字号固定 20 + 强制 `enableAutoSizing=false`**、
  **抄模板 `fontStyle`（加粗）**、**内边距 20/10**、**自建按钮不克隆模板**（克隆会带原生链接脚本副作用）。
- 打开时 `NativeUi.SetEscapeMenuBlocked(true)`，关闭时 `(false)`（**必须成对**）；ESC 只关闭自己的菜单。
- 全场景可用（`GamePhase` 0/1/2），Canvas `DontDestroyOnLoad`。

### D. 模组发现与清单
- 枚举已加载模组 + 枚举磁盘上的模组文件（两者取并集，磁盘有而内存无 = 未加载/已禁用/加载失败）
- 元数据：显示名 / 版本 / 作者（BepInEx `BepInPlugin` 特性 / MelonLoader `MelonInfo` 特性）
- **加载器来源标记**（§4）：宿主机 / 模组格式 / 桥 / 目录 / 双加载器标记
- 重复加载检测（同一程序集同时存在于两个加载器目录 → 双初始化风险警告，见 §4.4）

### E. 启停（开关模组）

### E.1 能力表（已实现 + 已实测）

| 档 | 适用 | 机制（源码依据） | 当场生效？ |
|---|---|---|---|
| **运行中 · 受管模组** | 接入契约且 `CanToggleAtRuntime=true` | `IModMenuProvider.OnDisabled/OnEnabled`（模组自己实现可逆停用） | ✅ 立即、**可逆** |
| **运行中 · MelonLoader 模组**（含经桥加载） | 所有 MLL 模组 | 停：`MelonBase.Unregister()` —— 源码注释原文：*“unsubscribes the Melons from all Callbacks/MelonEvents and unpatches all Methods that were patched by Harmony, but doesn't actually unload the whole Assembly”*（内部：`OnDeinitializeMelon()` → `UnregisterInternal()` → 移出 `_registeredMelons` → `HarmonyInstance.UnpatchSelf()`）<br>起：`MelonAssembly.LoadMelonAssembly(path, true)` → `MelonBase.RegisterSorted(...)`（**与加载器同一条路**） | ✅ 立即（停/起都可，**双端实测通过**） |
| **运行中 · BepInEx 插件**：热**启用** ✅ / 热**停用** = **软停用（实验性）** | 所有 BepInEx 插件（宿主有链加载器时） | 起：`IL2CPPChainloader.Instance.LoadPlugins(new[]{ 插件所在目录 })` —— 官方路径：`DiscoverPluginsFrom`（Cecil 读元数据）→ `ModifyLoadOrder`（**GUID 已在 `Plugins` 里的跳过** → 天然幂等）→ `LoadPlugin` = `Activator.CreateInstance` + `pluginInstance.Load()`<br>停（软）：调它的 `Unload()`（若重写）→ **撒它的全部 Harmony patch**（`GetAllPatchedMethods`+`GetPatchInfo`+`Unpatch(target,patchMethod)`，按**补丁方法所属程序集**归因）→ **销毁它加的组件**（`Resources.FindObjectsOfTypeAll` 含隐藏对象），见下 | ⬆️ 启用 ✅ 立即（实测：心跳 16 次）<br>⬇️ 停用 ⚠️ **立即但实验性**（实测 `unpatched=2 components=1 left=0`）<br>彻底干净仍需重启（文件已改名） |
| **重启生效（通用兵底）** | 所有模组 | **文件重命名 `.dll` ↔ `.dll.disabled`** —— 两个加载器都只扫 `*.dll`，故跨加载器通用 | ❌ 下次启动 |

**BepInEx 插件的“停用”能做到什么程度（已逐条实测）**：
- ✅ **能：撒 Harmony patch**。按「补丁方法属于该插件程序集」归因，逐条 `Harmony.Unpatch(目标方法, 补丁方法)`（不用 `UnpatchAll(owner)` 以免误伤同名 owner），
  然后**复量一次**确认剩下 0 条（实测 `matched=2 → left=0`；同时也证明“同一目标方法被多方 patch”时只撤指定那一条）。
  ⚠️ 两个实踩的坑：① HarmonyX 2.10 的 `GetPatchInfo` 返回 **`Patches`**（字段是**只读集合** `Prefixes/Postfixes/…`，**不是数组**）；
  ② 去重必须按 **(目标方法, 补丁方法) 成对**做 —— 同一插件常用同一个补丁方法挂多个目标，只按补丁方法去重会漏撤。
- ✅ **能：销毁它加进场景的组件**（连带停掉它的 `Update`/协程）。⚠️ 必须用 `Resources.FindObjectsOfTypeAll<MonoBehaviour>()`：
  插件组件通常挂在 BepInEx 的 **`BepInEx_Manager`**（`HideFlags.HideAndDontSave`）上，`Object.FindObjectsOfType` **看不见它**（实测漏掉 → 组件心跳照跑）。
- ✅ **能：调插件自己的 `Unload()`**（只有它重写才有意义；BepInEx 默认 `public virtual bool Unload() => false;`，实测返回 `False`）。
- ❌ **做不到：真卸载**。程序集不可卸载；**静态字段/单例/静态构造留下的状态还在**；`System.Threading.Timer`/后台线程若插件自己不收就一直在跑；
  插件往游戏侧注册的回调/订阅也还在（只是它的 patch 没了）→ 所以 UI 标成 **`停用（实验性）`**、状态栏写明“静态状态/线程可能残留”，
  并且**仍然改文件名** —— 重启后彻底干净。若插件自行实现了 `Unload()` 并清理干净，则等同于真停用。
- 启用（加载）本就不需要重启：走 BepInEx 自己的 `LoadPlugins`（它本来就支持被多次调用；幂等靠 GUID 去重）。
  ⚠️ **必须传目录**：`TypeLoader.FindPluginTypes` 内部是 `Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories)`，传文件路径会抛。
  ⚠️ patcher 不参与（preloader 阶段早过了）；依赖不满足/进程过滤不符/不兼容 → BepInEx 自己跳过并写 `DependencyErrors`（**不抛异常**）→
  我们读回 `Plugins`（GUID → PluginInfo）校验真实结果，失败就老实报“重启后生效”。
- ⚠️ **顺序坑（实测修正）**：**启用必须“先改文件名、再热加载”** —— 两个加载器都按 `*.dll` 发现文件，文件还叫 `.disabled` 时热加载找不到它；
  旧实现“先热恢复、后改名”导致文件被禁用的模组**永远只能重启启用**（用户反馈）。停用则相反（先停运行中，再改名）。

**T6/T8 实测踩过的坑（两个）**：
1. **顺序表编号是“按位置”写的**：`ModOrderTable.WriteExplicit` 按当前列表位置写 `(i+1)*10`，
   所以“上下移一格”在文件里看到的是**相邻两条的相对次序互换**；如果只看某一条的编号反而会以为没生效。
   取证要看 `effective order (N): …`（每次顺序变化记一行）与界面上的 `#n/总数`。
2. **`SettingItem.Key` 是 `Section.Key` 约定，而配置文件里只有短键**：写回时 `ModConfigStore` 已按短键找行，
   但“写回后从磁盘复核”如果直接拿 `Section.Key` 去 `IniDocument.Find` 会**永远返回 `<missing>`**（已踩，看着像写失败）。
   现在 `IniDocument.Find` 会在精确匹配失败后**去掉最后一段前缀再找一次**（只影响查找、不改写回语义）。

**两个实测踩过的坑（源码依据）**：
1. `MelonAssembly.LoadMelons()` **只创建实例 + 填元数据，并不注册**（源码：`loadedMelons.Add(melon)` 就结束）——
   真正的注册在 `MelonBase.RegisterSorted(...)`（`MelonFolderHandler.LoadMelons<T>()` 就是这么调的）；
   只调 `LoadMelons()` 会“看着成功但模组不跑”（已踩）。
2. 复位私有字段 `melonsLoaded` + 复用**内存里那个** `MelonAssembly` 重注：在**原生 MLL（D 端）可用**，但在**经桥（G 端）无效**（无报错、模组就是不回来）
   → 改为**优先走 `MelonAssembly.LoadMelonAssembly(path, true)`**（加载器自己的入口），并对结果**校验**
   （查 `MelonBase.RegisteredMelons` 里是否真有该程序集）——校验不过就老实报“需重启游戏”，不假称成功。

### E.2 其它规则
- 两个维度**分开显示**：文件状态（重启生效）与运行中状态（`Loaders/ModRuntimeState.cs`）；按钮文案相应分档（`停用（立即）` / `禁用（重启生效）`）。
- 写盘失败（文件被占用/无权限）必须给出明确提示 + 日志，不静默失败；失败时**不改文件**。
- ⚠️ **MelonLoader 另有原生「排除」语义**（`MelonFolderHandler._nameExclusions`，已核实）：文件名以 `~` 或 `.` 开头、或精确名为 `Broken`/`Retired`/`Disabled` 的**会被 MLL 跳过**。
  该语义**只对 MLL 生效**（BepInEx 只扫 `*.dll`，`~x.dll` 仍会被加载）→ **跨加载器通用禁用仍以“改扩展名”为准**；MLL 专属排除只在 UI 提示“此文件会被 MelonLoader 跳过”。
- 桥启动参数 `--no-mods`（`LoaderConfig.Disable`）会**禁用全部 MLL 模组** → 状态行必须显示该状态（否则用户会以为启停功能失效）。
- 顺序语义差异（见 §四）：MLL 模组可用 `MelonPriority` / 依赖特性声明顺序；BepInEx 插件**没有**顺序语义。
- 自测钩子（命令行，仅供自动化验证）：`-onnmm-selftest-runtime=<匹配串>` —— 列出 ML 模组实例，并对匹配项做“运行中停用 → 4 秒后恢复”（**不改文件**）；`list` 只列不回。
- 自测钩子（T5/T6/T8 取证）：`-onnmm-selftest-t56=<Id 匹配串>` —— 7s 开菜单 → 选中该条目 → 10s 切“设置”页并列出条目 →  11.5s 点第一个可编辑控件（写回）→ 13s **绕缓存从磁盘复核** → 14.5s 统一顺序上移一格 → 16s 注入真实 ESC 按键（**会等窗口前台**，拿不到就老实记 `SKIPPED`）→
  18.5s 读回状态（期望：我们的菜单已关、`gameEscapeOpen=0`）。`-onnmm-selftest-t56-ui=<匹配串>` = 只走到“切设置页”并停在设置页（供截图）。
- 自测钩子（T10–T12 取证）：`-onnmm-selftest-tab=<details|settings|debug>[:<Id 匹配串>]` —— 启动 6s 后开菜单 + （可选）选中匹配条目 + 切到指定页签，
  之后不再动作（供截图与 `debug snapshot` 日志），不写任何文件。
- 自测钩子（点击闸取证）：`-onnmm-selftest-click=<行号>` —— 8s 把**真实 OS 鼠标**移到该行中心并按下、8.35s 抬起、10s 读回选中项；
  用来确认“防幻影点击的闸”不会把真点击也滤掉（实测真点击被接受）。

### F. 统一加载顺序（见 §5）

✅ **已实现（T8）**：`Loaders/ModOrderTable.cs`（显式顺序表，`BepInEx\config\OpenNestModMenu.order.ini`）
+ `Registry/ModInitScheduler.cs`（有效顺序计算：基线排序 → 用户覆盖 → 依赖修正/冲突告警 → 受管初始化）+ 契约可选接口 `IModMenuInit`。
详情页有“顺序”行与 `上移/下移` 按钮；未写进表的条目标 **加载器决定**。顺序语义与实测取证见 §4.3 / §4.4。

> ⚠️ 诚实边界：**对不受管（第三方未注册、未实现 `IModMenuInit`）的模组，本表只是“权威展示顺序 + 受管初始化顺序”**，
> 改不了加载器自己的加载时刻（BepInEx 无优先级 API；MLL 自己的拓扑+优先级已经跑完了）。

### G. 统一设置中心（UI）
- ✅ **已实现（T6）**：右栏 `[详情][设置]`（另有 T10 的 `[诊断]`）页签 + `UI/ModMenuSettingsView.cs`（9 行/页 + 分页 + 底栏结果提示）
  + `UI/UiSetting.cs`（统一行模型）+ `UI/PageCollector.cs`（把第三方 `IModMenuPage` 声明转成行）
  + `Config/{IniDocument,ConfigLocator,ModConfigStore}.cs`（通用配置文件读写层）。
- 已支持：布尔开关、数值 ± 步进（按声明范围钳制）、枚举循环；**文本/快捷键 → UIKit 输入框**（空值就是空文本框，见更新记录二十）；分组标题行；分页；写回成功/失败提示。
- **自带回退面板（无 UIKit）里文本/快捷键仍为只读**（那里没有输入框控件）；搜索过滤、重置默认、确认弹窗、快捷键绑定仍待做。
- **第三方配置发现**：扫各加载器惯例位置（`BepInEx\config\*.cfg`、`UserData\*.cfg`、`MelonPreferences.cfg`）→ 解析元数据注释推断类型（`## 类型: bool | 默认: true` 与 BepInEx 官方 `## Setting type: …` 两种风格都认）→ 直接生成设置页（无需模组适配，见 §6.2）。
- **“模组 ↔ 配置文件”怎么对上的**（`Config/ConfigLocator.cs`）：① 加载器真值（BepInEx `GUID` → `<GUID>.cfg`）；② 字面同名（id / 显示名 / 磁盘文件名）；
  ③ **按模组名匹配**（文件名与模组名 `DisplayName` 归一化后完全相等 —— 实测原生 MLL 端 `Open Nest Co-op` ↔ `UserData\OpenNestCoop.cfg`，程序集名 `OpenNestCoop.MelonMod` 对不上）；
  ④ `MelonPreferences.cfg` 同名分类。（不做去后缀/前缀模糊那套。）
  **推断命中会存盘**到 `OpenNestModMenu.configmap.ini`（`[Config]` 段 `Id = 路径`），下次直接 `matched by saved`（见更新记录十八）。
- **实例缓存**：`ModConfigStore` 按文件 mtime+size 缓存解析结果；写回后失效重读（IL2CPP 下每帧全遍历是掉帧元凶）。

### H. 调试面板

✅ **已实现（T10/T11）**：右栏第三个页签 `[诊断]`（`UI/ModMenuDebugView.cs`）—— 可见时每秒重建；行池 12 行/页 + 分页 + `立即刷新`。

| 分组 | 内容 |
|---|---|
| `[帧性能]` | `FrameProfiler`：FPS / 平均帧 / 最差帧 + 测量点 `ms/s` top 4（`inventory`、`registry.scan`、`ui.refresh`、`settings.scan`、`debug.collect`） |
| `[加载器]` | 宿主 / 桥（依据 + 版本）/ BepInEx+MLL 版本 / interop 来源 / **双 Harmony**（需两个不同程序集）/ 全部惯例路径 |
| `[模组清单]` | 条目 / 已加载 / 已禁用 / 依赖 / 文件数 + 刷新次数与时间 |
| `[注册表]` | 提供者（主动/扫描） + 扫描次数 + 最近扫描明细（assemblies/candidates/new/loadFail/typeFail/ms） |
| `[统一顺序]` | 条目数 / 用户显式数 / 依赖修正 / 不兼容 + 顺序表路径 + `#1..#N` 明细 |
| `[重复加载]` | 重复条目（多启用路径 → 双初始化风险）+ Harmony 程序集实名与版本（过滤 DTF 工厂） |
| `[能力探测]` | `EventSystem`/输入模块/点击拦截/交互锁/层级（我们 vs 最高的几个画布）/ 指针来源 |
| `[日志/配置]` | 日志等级 / 独立文件日志目录 / 配置文件 / **配置映射表**（条目数 + 路径）/ 语言键文件 |

- 打开后 2.5s 自动把整屏快照写进 `modmenu.log`（`debug snapshot ('open')`）——诊断内容不靠肉眼看屏。
- **未接入（保持诚实）**：`NativeUi.Available` 这类“能力开关”在本模组里还没有具体实现（v1 用覆盖 UI）→ 面板直接写 `未接入（v1 用覆盖 UI）`，不编造。
- **运行时切日志等级**：未做（需要先定“哪些级别走文件、哪些只进主日志”的策略）；当前只**显示**等级。

### I. 本地化
- 自建 zh/en 键表（`en` 为键），订阅 `NativeUi.LanguageChanged` 刷新可见文案
- 字体走 `UiKit.EnsureFont`（桥接优先 → 运行时扫描兜底）

### J. 交付
- `scripts/deploy.ps1` 加双端部署；`scripts/package.ps1` 加双端包（光 MOD + Standalone 视需要）
- 不部署任何 bundle（v1）

---

## 三、跨加载器兼容与来源标记（重点）

### 3.1 为什么需要"双维"模型

桥模组 `BepInEx.MelonLoader.Loader` 的存在意味着：**进程宿主**与**模组格式**可以不同。
只标"MelonLoader"或"BepInEx"会歧义，必须拆成两维：

| 维度 | 取值 | 判定依据 |
|---|---|---|
| **宿主（Host）** | `BepInEx` / `MelonLoader` | 运行时探测：`BepInEx.Chainloader` 是否存在；`MelonLoader.MelonEnvironment` 是否存在 |
| **模组格式（Format）** | `BepInExPlugin` / `MelonMod` / `Unknown` | 该模组程序集内是否存在 `BaseUnityPlugin` 子类 / `MelonMod` 子类 |
| **加载路径（Path）** | `BepInEx\plugins\...` / `<MLL根>\Mods\...` / `Mods\...` | 从 `ModEntry.Path` 前缀判定 |
| **桥（Bridge）** | `None` / `BepInEx+MelonLoader` | 宿主=`BepInEx` **且** MelonLoader 运行时可用（两类证据：① `BepInEx\plugins\BepInEx.MelonLoader.Loader\` 存在；② 已加载程序集含 `MelonLoader.Hosting.BepInExHost`） |

**显示规范（UI 标记文案）**：

| 场景 | 标记 |
|---|---|
| BepInEx 插件，普通 BepInEx 环境 | `BepInEx` |
| MelonLoader 模组，普通 MelonLoader 环境 | `MelonLoader` |
| MelonLoader 模组，跑在 BepInEx 宿主（经桥） | `BepInEx + ML桥` |
| 探测不到/元数据缺失 | `未知` |

> 条目上同时显示 **宿主 + 格式** 两枚小标签（如 `[宿主: BepInEx] [格式: MelonMod]`），避免歧义。

⚠️ **实现坑（已踩，2026-09-12 修）**：`LoaderInfo.ViaBridge` 是**进程级**事实（“这个进程里有桥”），
**不能**直接当条目的桥标记用 —— 那样在桥环境里每个条目（含 BepInEx 插件与纯依赖）都会被标成“经桥”。
条目上的 `ModEntryInfo.ViaBridge` 必须**逐条目**算：

```text
BepInEx 插件              → Host=BepInEx, ViaBridge=false
MelonLoader 模组 + 桥环境 → Host=BepInEx(进程宿主), ViaBridge=true   ← 这才是“经桥”
MelonLoader 模组 + 原生 ML → Host=MelonLoader,     ViaBridge=false
```

⚠️ **磁盘条目的格式不能按目录猜**（已踩：`BepInEx\plugins\` 里同样放着依赖库）：
改为 `Loaders/AssemblyFormatProbe.cs` **只读 PE 元数据**判定（`System.Reflection.Metadata`，共享框架自带，双端 net6 都可用）：
沿基类链找 `MelonLoader.MelonMod/MelonPlugin` 与 `BepInEx..BaseUnityPlugin/BasePlugin`；**不加载程序集、不执行任何模组代码**（带 mtime+size 缓存）。
- 命中 MelonLoader 基类 → 格式 = `MelonMod`（UI 标签 **MLL**，无论是否经桥）
- 命中 BepInEx 基类 → `BepInExPlugin`（标签 `BepInEx`）
- 都没有 → **无模组类型 ⇒ 依赖库**（标签 `dep` / 中文“依赖”，性质分类 = `Dependency`）

⚠️ **BepInEx 插件基类名必须按运行时区分（本轮实测修正）**：本游戏的 BepInEx 6 是 **IL2CPP 运行时**，
插件基类是 **`BepInEx.Unity.IL2CPP.BasePlugin`**（`src/OpenNestCoop/Plugin.cs`）；探测表原来只列了
`BepInEx.BasePlugin` / `BepInEx.BaseUnityPlugin` / `BepInEx.Unity.Mono.BaseUnityPlugin` → 探不到模组基类
⇒ **禁用的 BepInEx 插件被标成 `dep`**（用户反馈）。已改为按“命名空间以 `BepInEx` 开头 + 类名 ∈ {`BasePlugin`,`BaseUnityPlugin`,`BepInExPlugin`}”判定。
> 为何只有“禁用态”暴露：已加载的插件走**加载器元数据**（`PluginInfos`/类型扫描）就有格式，不经过磁盘探测。
- 列表标签统一叫 **MLL**（不再用“ML bridge/经桥”）；是否经桥看右栏详情“宿主”（`BepInEx + MLL` / `MelonLoader`）

### 3.2 桥的实际机制（已按桥源码 + 本机环境核实）

| 机制 | 事实（含证据） | 对 ModMenu 的意义 |
|---|---|---|
| **别名策略** | 桥把 `BepInEx\interop\*.dll` 里每个游戏类型**克隆一份到 `Il2Cpp*` 前缀命名空间**（`MelonLoader/Hosting/Il2CppInteropAliasInjector.cs`：`Il2Cpp.LookAtTarget`、`Il2CppTMPro.TMP_Text` …）。MLL 模组因此**原样（verbatim）加载、从不被重写**。 | MLL 模组引用的 `Il2CppXxx` 类型 = **宿主 interop 实现**；ModMenu 做类型/成员反射时**不必区分两套 interop** |
| **写入时机** | `BepInEx.MelonLoader.Loader.Patcher`（`[PatcherPluginInfo("MelonLoader.InteropAliases", …, "2.3.8")]`）在**构造函数**里写入（早于 BepInEx 的 Cecil 读入/锁定，见其类注释的时序图）→ **同一次启动即生效**；插件阶段再写会 access denied。 | ModMenu **只读**报告别名状态，**绝不尝试改写** interop |
| **别名状态标记** | interop 目录下 `.melonloader-aliased`（含 fingerprint）+ `assembly-hash.txt`。**本机实测**：`G:\...\BepInEx\interop` 157 个 dll，标记**存在**（8921B）。 | 调试面板显示：interop 路径 / dll 数 / 别名是否就位 / 指纹是否与当前 interop 匹配 |
| **程序集解析重定向** | `Plugin.Load()` 里 `AppDomain.CurrentDomain.AssemblyResolve += …`：`args.Name.Contains("MelonLoader")` → 返回 `typeof(BepInExHost).Assembly`（= 桥目录里那份被改造的 `MelonLoader.dll`）。 | ModMenu 内**程序集名/类型名不得包含 "MelonLoader"**；查 MLL 类型以 `MelonBase`/`MelonMod` 为准 |
| **加载时机** | `BepInExHost.Initialize(MLLoader)` 在插件 `Load()`；但 `BepInExHost.Start()`（**真正加载 Mods**）延迟到 `GameLoopDriver.Update` **首帧**（否则等不到 `MelonMod.RegisteredMelons`）。 | MLL 清单采集**必须等首帧后**（否则空）；用延迟/轮询，不全信启动时刻快照 |
| **配置** | `BepInEx\config\io.bepis.melonloader.loader.cfg`（README：首次启动自动生成）。**本机实测该文件不存在**（`config\` 只有 `BepInEx.cfg`）。 | 桥配置读不到就**显示"默认"**，不报错；桥版本以插件元数据为来源 |
| **兼容自检（已有）** | `MelonLoader/Hosting/ModCompatScanner.cs` 在加载前用 dnlib 扫 `Mods/**/*.dll`，命中 `MelonUtils.NativeHookAttach/Detach`、`Imports.Hook/Unhook`（桥下为 no-op）就打警告 + 汇总 `[CompatScan] Scanned N mod assembly(ies); M call unsupported…`。 | ModMenu **不重复实现**：读取/展示其结论（或复用同一检查表），避免双重扫描开销 |

> **哈希证据（interop 来源）**：`G:\BepInEx\core\Il2CppInterop.Runtime.dll` 与 `G:\BepInEx\plugins\BepInEx.MelonLoader.Loader\Il2CppInterop.Runtime.dll` **完全相同**（SHA256 前 16 位 `65AB051A681C2C1E`，284672B）；而 D 端原生 `MelonLoader\net6\Il2CppInterop.Runtime.dll` 不同（`3878CE0B…`, 284672B）→ **桥下 MLL 模组最终用宿主（BepInEx）的 interop**。

**目录真值（本机实测，2026-09-12）**：

| 环境 | 路径 | 内容 |
|---|---|---|
| G 端（BepInEx + 桥） | `BepInEx\plugins\` | `BepInEx.MelonLoader.Loader\`（46 个 dll，含 `MelonLoader.dll` 2.0MB 与**同哈希**的 `Il2CppInterop.Runtime.dll`）、`OpenNestCoop.dll`、`LiteNetLib.dll`、`SharpGLTF.*.dll` |
| G 端 | `BepInEx\patchers\` | `BepInEx.MelonLoader.Loader.Patcher.dll`（27648B）+ `dnlib.dll` |
| G 端 | `BepInEx\interop\` | 157 个 dll + `.melonloader-aliased` + `assembly-hash.txt` |
| G 端 | `MLLoader\` | `MelonLoader\{Dependencies,Il2CppAssemblies}`、`Mods\`、`Plugins\`（空）、`UserLibs\`、`UserData\` |
| G 端 | `MLLoader\Mods\` | `SomeMod.dll`、`SomeMod.CustomRecords.dll`、`SomeEnemyTracker.dll`（**无** `OpenNestCoop.MelonMod.dll`） |
| D 端（原生 MLL） | `Mods\` | `OpenNestCoop.MelonMod.dll`、`SomeEnemyTracker.dll`、`SomeFreecam.dll` **+ 依赖混放** `LiteNetLib.dll`、`SharpGLTF.Core.dll`、`SharpGLTF.Runtime.dll` |
| D 端 | `UserLibs\` | `LiteNetLib.dll`、`SharpGLTF.Core.dll`、`SharpGLTF.Runtime.dll`（与 `Mods\` 重复一份） |

> ⚠️ **依赖 dll 与模组 dll 混放**（D 端 `Mods\` 与 G 端 `plugins\` 顶层都有）→ 清单**绝不能"目录里每个 dll 都算模组"**：以加载器元数据（`Chainloader.PluginInfos` / `MelonMod.RegisteredMelons`）为"模组"判据，其余标 `依赖/非模组`。

### 3.3 桥接兼容风险表

| 风险 | 说明 | 处置 |
|---|---|---|
| **重复加载 / 双初始化** | 史上出现过：G 端同时存在 BepInEx 与 `MLLoader`，MelonMod dll 误部署到 `MLLoader\Mods` → 同一模组被两个加载器各初始化一次（症状：`xxx already injected`、`TypeLoadException`）。 | **检测同名程序集跨目录同时存在 → UI 打红色警告 + 提示禁用其一**；模组自身必须做**幂等初始化守卫**（静态标志，重复初始化直接返回） |
| **双 Harmony 实例** | BepInEx 的 `0Harmony` 与 MelonLoader 内嵌 `0Harmony` 是**两个程序集** → 各自 patch，互相不可见（无法通过自己的 Harmony 枚举到对方的 patch；同一方法可能被双方各 patch 一次，执行顺序不确定）。 | ModMenu **不假设**能跨加载器统一管理 patch；调试面板**如实标注"patch 视图受加载器隔离"**；对已知重复 patch 风险给出提示 |

> ⚠️ **实测修正（2026-09-12 十六）**：本机 G 端（BepInEx + 桥）与 D 端（原生 MLL）实测进程内都只有**一份** Harmony
> （`0Harmony 2.10.2` + `Il2CppInterop.HarmonySupport`；另有 HarmonyX 为每个被 patch 方法生成的 `HarmonyDTFAssemblyN` 动态工厂 85~87 个），
> 即上表的“双 Harmony 实例”在当前环境中**未出现**。
> 另外：旧判定“两个类型名都能解析”会把 0Harmony（命名空间也叫 `HarmonyLib`）当成第二份 → 现在要求两者来自**不同程序集**才计。
| **`Chainloader.PluginInfos` 反射读不到（实测）** | 本机 be.785 下 `IL2CPPChainloader.PluginInfos` 反射取值为 `null`；`BepInEx.Bootstrap.Chainloader` 类型不存在。若只依赖它，**BepInEx 插件会全部被当成"未加载的依赖"**（已踩）。 | 改为**双通道**：先试 Chainloader（多个候选类型），为空则**扫描已加载程序集**里 `BasePlugin`/`BaseUnityPlugin` 子类并读其 `[BepInPlugin]` 特性（实测可拿到名称/版本，如 `Open Nest Co-op 0.2.1-Alpha-1`） |
| **interop 来源（已核实）** | 桥**不**用 MelonLoader 自己的 interop：两边 `Il2CppInterop.Runtime.dll` 哈希完全一致（见 §3.2 哈希证据），D 端原生 MLL 的那份不同 → **桥下 MLL 模组最终用宿主的 interop**。 | UI 显示 `interop: 宿主(BepInEx)`；**不要**假设 MLL 侧 interop 版本；编译期不引桥 |
| **别名必须早于 interop 加载** | 别名由 preloader patcher 在**构造函数**写入；插件阶段再改会被拒（interop 已被 Cecil 加载/锁定）。 | ModMenu **不得**改写 interop；只**只读**报告 `.melonloader-aliased` / `assembly-hash.txt` 状态 |
| **桥自带兼容自检（可复用）** | 桥已有 `ModCompatScanner`（静态扫描 MLL 模组对 no-op API 的调用并警告）。 | ModMenu 不重复实现；只**展示其结论** + 补充自己的检查表（转储/加载失败/重复加载） |
| **"缺失方法"不可 catch** | 已知事实：`LocalisationManager.GetFont` 在 MLL 下缺失，`MissingMethodException` 在 IL2CPP trampoline 抛出，**managed `try-catch` 捕不到**（会打断调用链）。 | **默认禁用**该类调用（`NativeCapabilityProbe`），仅提供**配置开关**显式启用；字体走 `UiKit.EnsureFont` 的运行时扫描兜底 |
| **配置/UI 冲突** | 多模组抢 F7–F10 / ESC / `sortingOrder` / `EventSystem`。 | 见 §7 兼容标准；ModMenu 打开时抢占 ESC 语义（成对解 blocker） |
| **`NativeUi` 桥接互相覆盖** | 若 OpenNestCoop 与 OpenNestModMenu 同时加载，各自 `NativeUi.Register(...)` → 后者覆盖前者。 | 能力等价故无害；Core 的 `Register` 已处理事件退订/重订阅。**标准：两方桥接能力必须保持等价**，否则后注册者会削弱前者 |

### 3.4 重复加载检测（`DuplicateLoadDetector`）

- 数据源：`ModEntry.Path` + 程序集名（不含扩展名，忽略大小写）。
- 判定：同一程序集名出现在 **≥2 个已启用（`.dll`）路径** → 标红 `重复加载`。
- 已知的高风险组合：BepInEx `plugins\` 与 MLL 根 `Mods\` 各放一份；或 `plugins\` 根与 `plugins\<子目录>\` 各放一份。
- 输出：UI 徽标 + 日志 `Warn`；调试面板列出具体路径。

---

## 四、统一加载顺序

### 4.1 问题

两个加载器各自扫描各自目录、顺序由加载器决定且不可注入；桥环境下更是**两套独立顺序**。ModMenu 无法改变加载器行为。

### 4.2 加载器的原生顺序语义（已核实）

| 加载器 | 语义 | ModMenu 处置 |
|---|---|---|
| **MelonLoader** | `MelonBase.SortMelons` = `DependencyGraph<T>.TopologicalSort(melons)` **然后** `OrderBy(x => x.Priority)`。可声明：`MelonPriorityAttribute(int)`（装配级）、`MelonAdditionalDependencies` / `MelonOptionalDependencies`（参与拓扑）、`MelonIncompatibleAssemblies`（声明不兼容的程序集名）。 | **读取并展示**（优先级 / 依赖 / 不兼容声明）；统一顺序表**必须尊重**已声明优先级与依赖（不得把被依赖项排到后面）；`MelonIncompatibleAssemblies` 直接进冲突警告 |
| **BepInEx** | **无**优先级/依赖语义（按目录枚举顺序加载）。 | 只展示 + 标注 `顺序由加载器决定`；不承诺可改 |

> API 提示：`MelonHandler.ModsDirectory/PluginsDirectory` 已 `[Obsolete(…, true)]`（编译报错）→ 取路径必须用 `MelonEnvironment.ModsDirectory` / `PluginsDirectory` / `UserLibsDirectory`。

### 4.3 方案：两级顺序 + 双通道注册

| 级别 | 覆盖对象 | 能力 |
|---|---|---|
| **统一展示顺序** | 全部模组（受管 + 不受管） | ModMenu 维护权威顺序表（基线排序 + 手动 `上移/下移`，已实现 T8），**仅影响展示与"受管初始化顺序"**，不改加载器行为 |
| **受管初始化调度** | 声明式注册的模组（`IModMenuProvider.AutoInit = true`） | 模组把初始化回调交给 `ModInitScheduler`，由 ModMenu 按配置顺序**依次执行**（真正的"统一加载顺序"） |
| **不受管** | 第三方未注册模组 | 只展示，标注 **`顺序由加载器决定`** |

**双通道注册**（解决"ModMenu 必须最先加载"的鸡生蛋问题）：

1. **主动**：模组在自身初始化时调用 `ModMenuRegistry.Register(provider)`。
   → 若 ModMenu 尚未就绪，调用进 Core 的静态注册表**暂存**（Core 门面不依赖游戏，任何时刻可写）。
2. **被动**：ModMenu 启动后扫描已加载程序集，发现 `IModMenuProvider` 实现 → 反射构造并注册。
   → 因此 **ModMenu 晚于其它模组加载也能发现它们**。

**硬约束**：被动发现的模组无法回退"初始化顺序"（它已经初始化完了）→ 只有主动注册 + `AutoInit=true` 才能被调度。
ModMenu 自身仍应**尽量早加载**（在探测清单/顺序上更完整）。

> ⚠️ 桥环境下"ModMenu 自身排在前面"没有保证手段（加载器顺序不可控）——**待实测**各加载器的实际枚举顺序，
> 并在文档记录结论；实测前不写"保证最先加载"。

### 4.4 顺序表持久化（✅ 已实现，T8）

- 文件：`<游戏>\BepInEx\config\OpenNestModMenu.order.ini`（MLL 侧 = `UserData\`，与本模组配置文件同目录）；
  格式与 BepInEx/ML 的 cfg 同族（`[Order]` + `Id = 10`），**人能读也能手改**（一律走 `Config/IniDocument` 最小侵入写回）。
- 存 INI（§6.2），键 = 条目 Id（= 程序集名），值 = 序数（步长 10）；**删掉某行 = 该模组交回“加载器决定”**（排在显式条目之后）。
- 表中不存在的模组（新装）→ 排到显式条目之后（不扰动已有顺序）；已卸载的条目 → 保留键但值写 0（不参与前段排序，重装后恢复）。
- 写回时机：只在界面 `上移/下移` 时**整表重写**（用户看到的顺序原样成为下次启动顺序）→ 不产生“序号跳号”这类惊喜。
- **左侧模组列表的展示顺序 = 本顺序**（`ModInventory.Refresh` 在回填 `Order` 后按 Order 重排；不在表里的排最后）
  → 界面列表与诊断页 `[统一顺序]` 的 `#n` 逐条一致（用户实测反馈后修正，原为 Id 字母序）。
- 实测：`order table written: 10 explicit entr(ies) -> '…\BepInEx\config\OpenNestModMenu.order.ini'`；
  文件内容 `[Order]` 段下 10 条 `Id = 10…100`；重启后 `effective order` 与界面一致。

---

## 五、UI 规范（v1：覆盖 UI）

### 5.1 结构与尺寸

```
Canvas "OpenNestModMenu"（ScreenSpaceOverlay, sortingOrder=运行时抬升, DontDestroyOnLoad）
└─ Blocker（全屏半透明压暗, raycastTarget=true, SetAsFirstSibling）
   └─ Panel（居中，1180×700，纯色）
      ├─ Header（50：左侧琥珀强调条 + 标题/副标题 + 关闭按钮）
      ├─ LeftCard（440×594）
      │   ├─ 筛选 chip ×4（全部/模组/依赖/禁用，带计数）
      │   ├─ 列表区（行 34 + 间距 4；每行 = 来源 pill + 名称 + 版本/状态；**顺序 = 统一加载顺序**，见 §4.4）
      │   └─ 工具条（‹ › 翻页 + 重新扫描 + 页码）
      ├─ RightCard（700×594）：名称 + 版本 + 三枚 pill（来源/格式/状态）+ 启停按钮 + **[详情][设置][诊断] 三个页签**
      │   ├─ 详情页：标签值行（**14** 行池）+ `上移/下移`（T8 顺序）
      │   ├─ 设置页：**按高度算行数**（实测 15 行/页）+ 分页 + 本页结果提示（T6，见 §G）
      │   └─ 诊断页：**按高度算行数**（实测 17 行/页）+ 分页 + `立即刷新`（T10，见 §H）
      └─ Footer（30）：加载/筛选/禁用/重复/依赖/提供者 + interop + 刷新时间 + F6 提示
      └─ Footer（30）：加载/筛选/禁用/重复/依赖/提供者 + interop + 刷新时间 + F6 提示
```

- 调色板/尺寸/字号集中在 `UI/ModMenuTheme.cs`（深色玻璃面板 + **琥珀强调色**，取自游戏仪表盘指针色）；将来换原生素材只改这一处。
- `CanvasScaler`：`ScaleWithScreenSize` / 1920×1080 / `matchWidthOrHeight=0.5`（`UiKit.CreateCanvas` 已是此默认）
- **`sortingOrder` 是什么**：UGUI `Canvas` 的绘制顺序值（**值大的画在上面**）。游戏原生 UI、联机菜单、本模组各自建独立的 `ScreenSpaceOverlay` Canvas，互相不知情，谁盖谁**只由这个值决定**。
- **本机实测的画布层级（2026-09-12，G 端）**：`Cursor Canvas`（游戏虚拟光标）**32767** > `OpenNestCoop_UI` **32766** > 本模组原定 32765。
  → 固定值必然踩错：我们比联机面板低，右栏详情会被它遮住。
- **ModMenu 取值（定稿：拄联机模组的做法，2026-09-12 十二）**：`UiInputGuard.DockLayers` 把本模组画布设为 **32766**（严格低于游戏光标层 32767），
  **从不修改光标画布**（游戏每帧自己写回 32767 / 隐藏时 -32768）；仅把其它非光标、且 `sortingOrder ≥ 32766` 的画布降位，
  所有被改动都记原值，关菜单时**全部还原**（见 §5.2）。
  > 历史：“运行时抬升到最高”的方案（`RaiseAbove`）实际会与游戏光标层抢位，已废弃。
- 字号：标题 20 / 副标题 12 / 行 14 / 行状态 11 / 详情名 19 / 标签 13 / 值 14；**一律固定字号 + `enableAutoSizing=false`**（历史结论：autoSizing 是“忽大忽小”根因）
- 行内文字**不换行** + 省略号（长模组名换行会撑破行高与下一行重叠；完整名字在右栏详情里看）
- 控件尺寸不按 `sprite.border` 反推（v1 无 sprite，天然规避）

### 5.2 控件与输入

点击穿透在本项目里不是一个原因，而是**四个**，必须逐层都上（下面每条都带实测证据）：

| 层 | 做什么 | 为何必要（实测） |
|---|---|---|
| ① 自己的指针命中 | `ModMenuUI.Hot`：指针位置（`PointerPosition`：虚拟光标优先、退回 `Mouse.current`）+ `RectTransformUtility.RectangleContainsScreenPoint` 命中热区/行，自管悬停与点击（`isPressed` 自算边沿） | 任务场景里游戏 `EventSystem` **未激活**（`current=<none>`）→ uGUI 按钮**收不到点击**（这是“点击穿透”的真因） |
| ② 压制外部 UGUI 射线 | `UiInputGuard.SuppressRaycasters`：菜单打开时把所有非本模组画布的 `GraphicRaycaster.enabled=false`（实测 **57~59** 个），关菜单原样恢复；每 1.5s 重申（场景切换会新建画布） | blocker 只能拦 `sortingOrder` 比我们低的画布（实测联机面板 32766）；挡不住高于我们的画布，而 raycaster 压制与顺序无关 |
| ③ 世界交互点击拦截 | Harmony prefix 挂 `LookAtTarget.OnClickDown`（菜单打开时返回 false），失败 5s 节流重试，命中记 `game click blocked #n` | 交互按钮/拉杆的点击效果（拉杆动画 + onClickDown UnityEvent）都在它内部；结论来自 OpenNestCoop `ButtonClickSync` |
| ④ **设备层输入吞掉** | Harmony prefix 挂 `ButtonControl.get_wasPressedThisFrame` / `get_wasReleasedThisFrame` → 菜单打开时一律返回 false | 前三条只拦“事件系统/特定入口”。⚠️ **保留但未证实对游戏原生代码有效**（patch 的是 interop 代理 getter，游戏原生调用不一定经它）；模组自己的 UI 因此改读 `isPressed` + 自算边沿 |
| ⑤ **组件级交互锁**（关键） | `UiInputGuard.LockInteractions`：菜单打开时禁用场景里所有交互组件（基类链含 `Interactable` 的派生类 + 类型名 `LookAtTarget`，实测 **27** 个）+ `FirstPersonController.SetFrozen(true)`；关菜单**原样恢复**。（全屏透明 blocker 本来就有：`UiKit.MakeBlocker` 的压暗层，与联机模组 `_blocker` 等价） | **唯一与“调用路径”无关的一层**：世界交互由游戏自己从光标位置轮询/射线，与我们的画布层级、EventSystem 都无关；实测第 ③ 层从未被调用（路径在组件内部），真正生效的是本层。**做法抄联机模组 `OpenNestCoop.UI.CoopUIManager.ApplyMenuState()`**（`_blocker` + `LockPlayer` + `DisableInteractables`） |

- 组件识别走**类型名 + 基类链**（不依赖能否把游戏类型解析成 `Type`）：实测 D 端（原生 MLL）`FindType("LookAtTarget")` 曾返回 null
  → 第 ③ 层整层静默失效；`LoaderReflect.FindType` 现已加**按简单名兜底扫描**（+ 缓存 + 未命中诊断 `type NOT found: '…' | assemblies=N [...]`）。
- 🔍 **待查观察项（T5/T6/T8 自测时发现，不影响上述验证）**：菜单打开期间日志里每 ~1.5s 出现一次 `click: hot=-1 row=N`，
  列表选中项会被挑到别的行；节奏与 `UiInputGuard` 的 1.5s 重申完全同步。已排除“设备层吞输入”（那层只是 prefix 返回 false，不写合成状态），
  怀疑是**游戏自己的虚拟光标在向 Input System 写合成鼠标状态**（我们的 `HandlePointer` 看到 `leftButton.isPressed` 边沿就当成点击）。
  风险：实际游玩时可能“未按键却点到一行/一个按钮”；待查方向：给自管命中加“指针真的动过 / 按下与释放成对”约束，或改为只信游戏虚拟光标自己的点击事件。
  **根因已实测确认**：MLL 端（MelonLoader）游戏类型带 `Il2Cpp` 命名空间前缀 ——
  `'LookAtTarget' -> Il2Cpp.LookAtTarget`、`'VirtualCursor' -> Il2Cpp.VirtualCursor`（都在 `MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll`）；
  而 G 端（BepInEx + 桥）全名就是 `LookAtTarget`。两端都能解析后，第 ③ 层在 D 端也装上了（`game click block installed: Il2Cpp.LookAtTarget.OnClickDown`）。
- 组件扫描**不是每 1.5s 一次**（主线程 `GetComponentsInChildren` 有成本，实测场景 13213 个 MonoBehaviour → 9ms）：打开时必扫，之后 **5s** 一次
  （捡打开期间新出现/被游戏重新启用的交互物 —— 实测任务场景的交互物比菜单晚出现：首次扫描 `nothing to lock (scanned 13213)`，5s 后 `disabled 27`）。
- 不新建 `EventSystem`：自己管命中比“再造一个 EventSystem”风险低得多（双 EventSystem 会互相抢 `EventSystem.current`）。
- Harmony 用**反射**拿（`HarmonyLib.Harmony` / `HarmonyMethod`），不引用 `HarmonyLib` —— 与“共享代码全反射”一致。
  ⚠️ `Harmony.Patch` 的**重载数目随版本变**（新版多个 ilmanipulator）→ 按“第1参是 MethodBase + 其余都是 HarmonyMethod”筛选，多余参数补 null。
- `ClickOnce`：同一帧只触发一个点击（自管命中与 uGUI 点击可能同时到达时防重复）。
- **层级（定稿，2026-09-12 十二）**：本模组画布固定 **32766**（严格低于游戏光标层 32767），
  其它 `sortingOrder ≥ 32766` 的**非光标**画布降到我们之下（保持彼此相对顺序），关菜单还原；
  **从不修改光标画布**（游戏自己在 `Update` 里写回 32767 / 隐藏时 -32768）；`TickLayerOrder` 在 `Update` 与 **`LateUpdate`** 各重申一次。
  依据 = 联机模组与官方联机的既有做法（都 32766、都不碰光标画布）。
- **指针**：默认**不画**自己的指针 —— 游戏虚拟光标（32767）或系统硬件光标必定在我们之上；
  只有“既没有活跃光标画布、硬件光标又隐藏”时才由 `UI/UiCursorOverlay.cs` 自绘（`sibling=2/2`，精灵优先抄游戏光标，拿不到用纯色兜底）。
- 内容多时用**翻页**（行控件池 + 页码），不用 `ScrollRect`：IL2CPP 下手建 `ScrollRect`（viewport/Mask/ContentSizeFitter/VerticalLayoutGroup）易踩坑，
  而模组数量在本项目里很小（十几~几十条）→ 翻页更稳、可预测。
- **v1 不做文本输入**（`CoopInputBox` 的 IME 复杂度不值得在 v1 承担）；数值用滑条/步进
- 打开菜单时**游戏侧交互全部禁用**（防止误操作炮台/拉杆）：靠**第 ⑤ 层组件级交互锁**（实测 27 个组件 + 玩家冻结 + blocker）；必要时叠加 `NativeUi.SetEscapeMenuBlocked(true)`（T5）

### 5.3 原生素材：保留不激活

- `ModMenuConfig.UseNativeSkin = false`（默认）
- 为 `true` 时才：`UiSpriteBank.FromResources/CollectSprites/CaptureFromScene` + `UiKit.MakePanel(bgSprite)` / `MakeButton(bgSprite:)`
- ⚠️ 复用时的已知坑（`docs/NATIVE_UI.md` §6、repo memory）：`Resources.Load<Sprite>` 按名**恒 null**（要用 `LoadAll<T>` / `CaptureFromScene`）；
  `pixelsPerUnitMultiplier` 在 IL2CPP 下不可靠（**必须把 `border ÷ scale` 烤进 sprite 副本**，用 `CopySprite` / `MeasureActualBorder`）。
- 代码保留但**不在启动路径调用**（不产生主菜单捕获开销与场景卸载风险）

### 5.4 语言键（本地化）

**代码里不写文案，只写键**；文本来自外部语言键文件（与 OpenNestCoop 同一套规范，见 `docs/LOCALIZATION.md`）：

| 宿主 | 路径 |
|---|---|
| BepInEx | `<游戏目录>/BepInEx/config/OpenNestModMenu.lang.ini` |
| MelonLoader | `<游戏目录>/UserData/OpenNestModMenu.lang.ini` |
| 兜底 | `<persistentDataPath>/OpenNestModMenu.lang.ini` |

- 格式：`[语言代码]` 段 + `键 = 文本`；`#`/`;` 注释；`{0}`/`{1}` 是运行期占位符（`string.Format`）。
- 实现：`Loc/{LocDefaults,LocFile}.cs`（内置表 + 读写/生成/热重载）+ `Core/ModMenuLoc.cs`（键门面 `L("Key")`）。
- 取文案顺序：当前语言段 → `[en]` 段 → 内置默认 → 键名（最后一层只是兜底，正常看不到键名）。
- 文件缺失/缺键 → 用内置表**生成/补齐**（首启自动写出，升级后新增键自动补进，**保留用户改过的文本**）。
- 热重载：`ModMenuBehaviour.Update` → `ModMenuLoc.Tick` → 每 **2 秒**比对 mtime，变了就重读（改文案不用重启）。
- 语言判定：桥接 `NativeUi.CurrentLanguage`（游戏内切语言即时跟随）→ `Application.systemLanguage` → `en`；每秒轮询，变化时抛 `Changed` 让 UI 重刷。
- 语言文件写 UTF-8 **BOM**（记事本打开中文不乱码）；**永不抛异常**（文件坏了不影响游戏）。
- ⚠️ 解析时会对值 `Trim()` → 默认值**不能依赖前导空格**（踩过：`" (restart to apply)"` 变 `"(restart to apply)"`，已在代码里显式拼空格）。
- 未纳入本地化：日志行（面向排障）、第三方注册的 `DisplayName/Note`（模组自带身份）。

---

## 六、标准清单

### 6.1 代码标准
- **独立性**：不引用 `OpenNestCore`/`OpenNestCoop` 程序集；拷入的基建一律改 `OpenNestModMenu.*` 命名空间（§1.2）
- **名称禁忌**：程序集名/类型名**不得包含 "MelonLoader"**（桥的 `AssemblyResolve` 会把含该串的请求重定向到桥自己的 `MelonLoader.dll`）
- **加载器探测一律走反射**：BepInEx 版与 MelonLoader 版**共用同一份源码**，各自只引用一端的加载器程序集 → 共享代码里直接写 `BepInEx.*` / `MelonLoader.*` 会导致**另一端编译失败**（同时这也满足"对桥零编译期依赖"）。
  例外：前缀黑名单要放行 **`BepInEx.MelonLoader.Loader*`**（桥本身就是个插件，否则看不到它也读不到桥版本）
- **契约程序集** `OpenNestModMenu.API.dll` 不含 Unity/游戏类型依赖；第三方为**软依赖**（只引用该 dll，ModMenu 缺失时静默跳过）
- 平台差异一律 `#if MELONLOADER` + `PlatformUsings.cs` 别名；引 `Assembly-CSharp` 的桥接实现只能放模组侧，拷入的 UI 基建**不得**引用 `Assembly-CSharp`
- **禁止** `foreach` 遍历 Il2Cpp 集合（用显式索引/枚举器）；`AddComponent<T>()` 仅限 Unity 内置类型（自定义 MonoBehaviour 会 `TypeInitializationException`）
- 委托桥接用显式 `(Action<T>)` 转换；**不订阅原生事件**，用轮询（`NativeUiPoll` 模式）
- `AccessTools.Method` 查带参方法**必须显式传参数类型数组**（否则 patch 静默失败）
- 反射遍历程序集/类型**逐项 try-catch**
- 幂等初始化守卫（防桥环境重复加载）

### 6.2 配置兼容标准（社区通用格式，不依赖任何具体模组）

前提：**OpenNestCoop / OpenNestModMenu / 桥是三个相互独立的模组**，互不归属也不互引程序集。ModMenu 只做“**兼容通用格式与社区惯例**”。

**本模组自身配置放哪**（跟加载器惯例）：

| 平台 | 路径 | 格式 |
|---|---|---|
| BepInEx 版 | `BepInEx\config\OpenNestModMenu.cfg` | BepInEx cfg 写法（见下表） |
| MelonLoader 版 | `<GameDir>\UserData\OpenNestModMenu.cfg` | MelonLoader 模组 cfg 写法（本机实例：`UserData\OpenNestCoop.cfg`） |

**要能识别 / 读写的通用格式**（本机实测取样）：

| 格式 | 本机实例 | 关键写法 | ModMenu 行为 |
|---|---|---|---|
| **BepInEx 插件 cfg** | `BepInEx\config\BepInEx.cfg` | `[Section]`（含子节 `[Harmony.Logger]`）、`## 描述`、`# Setting type: Boolean`、`# Default value: true`、`# Acceptable values: A, B`、`Key = Value` | **可读写**；注释元数据→**推断控件类型**（Boolean/枚举/数值/**文本**），写回**只替换值** |
| **MelonLoader 模组 cfg** | `UserData\OpenNestCoop.cfg` | `## 描述`、`[Section]`、`Key = Value`、`## 类型: bool \| 默认: true` | **可读写**；同一最小侵入策略 |
| **MelonPreferences** | `UserData\MelonPreferences.cfg`（本机 0 字节） | 以**模组名/ID 为 category** 的偏好表 | 识别；有内容时按 category 分组展示 |
| **MelonLoader Loader.cfg**（TOML 风格） | `UserData\Loader.cfg` | 小写 `key = "value"`、`#` 注释、双引号字符串 | **只读展示**（属加载器自身，不由 ModMenu 改写） |
| 无法识别的自定义格式 | 如 `Models\soldier.cfg` | 各种 key=value 变体 | 只读展示 + “打开文件”按钮，**绝不改写** |

**硬规则**：
1. **最小侵入写回**：只改目标键的值，保留注释、空行、顺序、未识别键（不重排、不丢注释、不重写整文件）。
2. **格式探测优先**：类型由注释标记推断；推断不出→按字符串处理（可编辑输入框，值原样写回），不猜更复杂的类型。
3. **不认识就不改**：无法可靠解析的文件一律只读（宁可少做，不可损坏别人的配置）。
4. **不改加载器自身配置**（`BepInEx.cfg` / `Loader.cfg`）：只读展示。
5. 解析失败/损坏 → 回退默认值 + `Warn`，不抛异常；**不用 JSON/Newtonsoft**（双端依赖不一致）。

### 6.3 性能标准
- 禁止每帧全场景扫描（`FindObjectsOfType`）；一律"实例缓存 + 低频刷新（3s 级）+ 场景变化刷新"
- 诊断日志只能 `CoopLog.Debug(key, ()=>..., intervalSec)`；`Info` 仅低频摘要
- UI 列表不做每帧 `Rebuild`；菜单关闭时停止刷新
- `FrameProfiler` 测量点加在 ModMenu 自身 tick 上

### 6.4 稳定性标准
- 所有 Harmony patch / 反射调用 / 桥调用 try-catch，失败降级不崩游戏
- 未注册 `NativeUi` 时一切安全（Core 已保证）
- **跨场景/跨任务存活的 UI 必须绑定"菜单是否打开"状态**，关闭即清（历史坑：HUD 泄漏到下一个场景）

### 6.5 兼容标准（与 OpenNestCoop / 桥共存）
- 不新建 `EventSystem`；不抢 F7–F10；ESC blocker 成对
- `sortingOrder` 错开已知值（32767 光标 / 32766 联机菜单）+ 可配置；**不共享静态状态**（本模组的 `NativeUi` 注册表 / `CoopLog` 与其它模组各管各的，见 §1.2）
- 与 `BepInEx.MelonLoader.Loader` **零硬依赖**（只用公开 API + 反射探测）；**不读改 `BepInEx\interop`**（只读报告别名状态）
- 不重复实现桥已有的兼容自检（`ModCompatScanner`），只展示其结论
- MLL 清单采集在 `BepInExHost.Start()`（首帧）**之后**；不重排 MLL 已声明的 `MelonPriority`/依赖拓扑
- 与 OpenNestCoop 同时加载运行 ≥10 分钟无异常

### 6.6 测试标准（准入）
- 双端构建 0 error；双端运行 0 exception
- 4 种组合各跑一遍：`BepInEx 纯` / `MelonLoader 纯` / `BepInEx + 桥（含 MLL 模组）` / `BepInEx + OpenNestCoop 共存`
- 主菜单 + 任务内均可开/关；开→关无 ESC blocker 残留
- 中文不显示方框；加载器来源标记与实际部署一致；重复加载场景能报出警告
- 禁用/启用后重启，状态与顺序表保持一致

### 6.7 文档标准
- 本文档随实现更新（更新记录逐条追加）；实测结论（加载器枚举顺序、桥的 interop 来源、桥版本读取方式）**回填 §3.2 / §4.2**
- 在 `.github/instructions/copilot-instructions.md` 的 docs 索引 + 映射表登记（已登记）

---

## 七、文件级拆解

### 7.1 契约工程 `src/OpenNestModMenu.API/`（第三方引用；纯 .NET，无 Unity/游戏依赖）— ✅ 已实现（T1）

```
src/OpenNestModMenu.API/
  OpenNestModMenu.API.csproj   net6.0；无 Unity/interop/游戏引用（双端同一份）；
                               InternalsVisibleTo = OpenNestModMenu / OpenNestModMenu.MelonMod
  IModMenuProvider.cs      ★ 第三方契约（Id/DisplayName/Version/Author/AutoInit/CanToggleAtRuntime
                             + BuildPage/OnEnabled/OnDisabled/ResetToDefaults）+ ModMenuProviderBase 默认实现
  IModMenuPage.cs          页面构建契约（Header/Label/Separator/Bool/Number/Choice/Text/Action/KeyBind）
  ModMenuModel.cs          枚举 ModLoaderHost / ModAssemblyFormat / SettingKind；
                             ModEntryInfo（双维来源标记 HostLabel/FormatLabel + 路径/启用态/优先级/依赖/冲突）、
                             SettingItem（字符串承载值 + TryGetBool/TryGetNumber）
  ModMenuHost.cs           注册表 + 门面：Register/Unregister/TryGet/Providers/ProviderCount/Changed/
                             IsHostAvailable/HostVersion；MarkHostAvailable 由本模组调用（internal）
```

### 7.2 模组工程 `src/OpenNestModMenu/` + `src/OpenNestModMenu.MelonMod/`

**✅ 已实现（T1 骨架 + T2 注册表，2026-09-12）**：`Plugin.cs` / `MelonModEntry.cs` / `ModMenuInfo.cs` / `GlobalUsings.cs` / `PlatformUsings.cs` /
`OpenNestModMenu.csproj` / `Core/{ModMenuRuntime,ModMenuPaths,ModMenuBehaviour,ModMenuLoc}.cs` /
`Registry/{ModMenuRegistry,AssemblyScanner,BuiltInSettingsProvider}.cs`（双通道注册 + 扫描调度 + 内置设置页）/
`Vendor/**`（`README.md` + `Logging/{ILogger,CoopLog,ModLog}.cs` + `UI/{INativeUiService,UiKit}.cs` + `Diagnostics/FrameProfiler.cs`）+
`src/OpenNestModMenu.MelonMod/OpenNestModMenu.MelonMod.csproj`（DeployToMods：主 dll → `Mods\`，API dll → `UserLibs\`）。

**✅ 已实现（T1 + T2 + T3 + T4，2026-09-12）**：

```
src/OpenNestModMenu/
  Plugin.cs / MelonModEntry.cs / ModMenuInfo.cs        入口壳 + 身份常量
  GlobalUsings.cs / PlatformUsings.cs / *.csproj       命名空间导入 + 工程（ProjectReference → API；不引 OpenNestCore/OpenNestCoop）
  Core/
    ModMenuRuntime.cs                运行时根：Initialize/Startup/Shutdown + 幂等守卫（注册表/加载器初始化入口）
    ModMenuPaths.cs                  双加载器路径解析 + 部署形态探测
    ModMenuBehaviour.cs              MonoBehaviour（ModLog 落盘 / FrameProfiler / 注册表与清单调度）
    ModMenuLoc.cs                    中英文案（桥接可用则跟随游戏语言，否则系统语言）
  Registry/
    ModMenuRegistry.cs               记录表 + Changed + 扫描调度（ModInitScheduler 待 T8）
    AssemblyScanner.cs               被动发现（框架前缀快筛 + 逐项 try-catch）
    BuiltInSettingsProvider.cs       本模组自己的设置页（兼作双通道活体验证）
  Loaders/
    LoaderInfo.cs                    宿主/桥/版本/interop 来源 探测结果模型
    LoaderReflect.cs                 ★ 反射工具（全反射：找类型/读静态成员/读 BepInEx 插件/读 MLL 模组与特性）
    LoaderDetector.cs                探测 + 桥证据 + 双 Harmony 判定 + 版本
    ModInventory.cs                  内存清单 + 磁盘清单 → 去重条目 + **逐条目**来源标记 + 日志
    ModFileState.cs                  启停：改名切换 `.dll` / `.dll.disabled`（重启生效）
  UI/
    ModMenuTheme.cs                  ★ 视觉标准：调色板 / 尺寸 / 字号 / pill 小部件
    ModMenuDisplay.cs                条目 → 文案/颜色 映射（来源 pill、状态色、性质分类）
    ModMenuListView.cs               列表控件（行池：来源 pill + 名称 + 版本/状态 + 选中强调条 + 翻页 + 悬停）
    ModMenuUI.cs                     主界面（标题栏 / 筛选 chip / 列表 / 详情行池 / 底栏 / 热键 / **自管指针命中**）
    UiInputGuard.cs                  ★ 输入守卫：压制外部射线 + 层级抬升/光标重申 + LookAtTarget 点击拦截 + 环境探测
  Loc/
    LocDefaults.cs                   内置默认文案表（键 → 中文/English）
    LocFile.cs                       语言键文件读写/生成/热重载（`OpenNestModMenu.lang.ini`）
  Loaders/
    ModFileState.cs                  启停：`*.dll` ↔ `*.dll.disabled` 改名（T7）
  Vendor/                            拷入的源码副本（独立命名空间；清单/同步流程见 Vendor/README.md）
    Logging/{ILogger,CoopLog,ModLog}.cs / UI/{INativeUiService,UiKit}.cs / Diagnostics/FrameProfiler.cs
src/OpenNestModMenu.MelonMod/
  OpenNestModMenu.MelonMod.csproj    MelonLoader 壳（DeployToMods：主 dll → Mods\，API dll → UserLibs\）
```

> 与初版计划的差异（以代码为准）：① `ModEntry.cs` 不再单独存在 —— 条目直接用契约程序集的 `ModEntryInfo`；
> ② 新增 `LoaderReflect.cs`（共享代码必须全反射、不能直接引用加载器类型）；
> ③ `Loaders/ModFileState.cs`（启停改名）已在 T4 后续修复里落地（原计划 T7）；
> ④ 新增 `Loc/{LocDefaults,LocFile}.cs` + `Core/ModMenuLoc.cs` 改写为**语言键**门面（原计划把文案硬编码在 `ModMenuLoc.T(zh,en)` 里）；
> ⑤ 新增 `UI/{ModMenuTheme,ModMenuDisplay,UiInputGuard}.cs`（视觉标准/展示映射/输入守卫 单独成文件，而不是塞进 `ModMenuUI`）。

**✅ 以下已落地（T5/T6/T8，文件以代码为准）**：

```
  Config/
    IniDocument.cs                   通用 INI 读写（最小侵入：只改值、保留注释/键名/空格式样；值没变不落盘；.bak 一次）
    ConfigLocator.cs                 找某个条目对应的配置文件（真值 → 字面同名 → 按模组名匹配 → MelonPreferences）
    ConfigMap.cs                     “模组 ↔ 配置文件”映射表（启发式命中后存盘，下次直接复用）
    ModConfigStore.cs                设置项扫描 + 类型推断 + 写回（按 mtime+size 缓存）
  Loaders/
    ModOrderTable.cs                 统一顺序表（显式编号持久化，见 §4.4）
  Registry/
    ModInitScheduler.cs              有效顺序计算（基线→用户覆盖→依赖修正）+ 受管初始化调度（T8）
  UI/
    UiEscapeGuard.cs                 ESC 拦截（禁用游戏 ESC 触发 action，关闭时还原）（T5）
    UiSetting.cs / PageCollector.cs   设置页统一行模型 / 第三方声明页→行（T6）
    ModMenuSettingsView.cs           设置页渲染与控件交互（T6）
    ModMenuDebugView.cs              诊断页（帧性能/加载器/清单/注册表/顺序/重复/能力/日志）（T10+T11）
```

**⏳ 以下仍为计划**：

```
  Core/
    ModMenuConfig.cs                 本模组自身配置（热键 / Ui.SortingOrder / UseNativeSkin / 日志等级）
  Loaders/
    InteropAliasStatus.cs            只读报告：interop 目录/dll 数/.melonloader-aliased 指纹（T11）
    DuplicateLoadDetector.cs         重复加载的 UI/诊断呈现（T11；判定已并入 ModInventory）
    BridgeCompatReport.cs            展示桥 ModCompatScanner 结论 + --no-mods 状态（T11）
  UI/
    ModMenuPageBuilder.cs / ModMenuDialogs.cs / ModMenuEntryInjector.cs
  Bridge/
    ModMenuNativeUiBridge.cs / NativeCapabilityProbe.cs
  Debug/
    ModMenuDebugPage.cs
```

### 7.3 脚本

```
scripts/deploy.ps1                  加 OpenNestModMenu 双端部署（BepInEx plugins + MLL Mods）
scripts/package.ps1                 加 OpenNestModMenu 双端包（光 MOD；Standalone 视需要）
```

**✅ 已落地（T12，2026-09-12）**：

```
scripts/deploy.ps1                  双端部署菜单（实测两端均 Deployed）
scripts/package.ps1                 6 个包：Coop 4 个 + 菜单 2 个（OpenNestModMenu-<ver>-{BepInEx,MelonLoader}.zip，均只装菜单本体 + README）
```

---

## 八、实施任务列表（建议顺序）

| # | 任务 | 产出 |
|---|---|---|
| T1 | 骨架：`OpenNestModMenu.API` 契约工程 + 模组工程 + 双端壳 + `Vendor/` 源码副本 + 日志注入 + 幂等守卫 | 双端能构建、能加载、日志出现 |
| T2 | 注册表 + 双通道注册（主动暂存 / 被动程序集扫描） | 第三方可注册并出现在列表 |
| T3 | 加载器探测 + 清单（宿主/格式/路径/桥标记） | 调试输出正确标记 |
| T4 | 覆盖 UI 骨架（Canvas/Blocker/标题栏/列表/状态行） | 菜单可显示、可开/关 |
| T5 | 热键 + ESC blocker 成对 + 关闭清理 | ✅ 开关入口可用（`UiEscapeGuard`，见更新记录十五） |
| T6 | 设置项控件族 + 设置中心页面渲染 | ✅ 第三方设置页/配置文件均可展示与写回（`ModMenuSettingsView`） |
| T7 | 启停（文件重命名）+ 重启提示 + 失败提示 | 可禁用/启用并持久 |
| T8 | 统一加载顺序（展示顺序表 + `ModInitScheduler` + 双通道注册） | ✅ 顺序可调 + 受管调度（`ModOrderTable` + `ModInitScheduler`） |
| T9 | 原生菜单注入入口（主菜单 + ESC 全实例 + slot 让位） | ✅ 2026-09-13 完成：改为挂到 `OpenNestUIKit`（菜单由它渲染、ESC 入口由它注入；缺 UIKit 则回退自带界面）——见更新记录十九 |
| T10 | 调试面板（FrameProfiler + 加载器诊断 + 重复加载 + 能力探测） | ✅ 诊断页可用（`ModMenuDebugView`） |
| T11 | 重复加载检测 + 双 Harmony 隔离说明 | ✅ 风险可见（`[重复加载]` 分组 + 程序集实名证据；实测本机双端均单份 Harmony） |
| T12 | 双端部署/打包脚本 + 四组合实测 + 文档回填 | ✅ 脚本可用（6 个包）+ 已实测 G/D 两端，另外两种组合本机无环境（已如实标注） |
| T13 | **运行时启停**（热禁用 / 热恢复）：MLL 官方 `Unregister` + 契约 `CanToggleAtRuntime` + 文件改名兵底 | 点禁用当场生效 |

> **进度（2026-09-12）**：
> - ✅ **T1 完成并已游戏内确认（双端）**：API 契约工程 + 模组工程 + 双端壳 + `Vendor/` 副本 + 日志注入 + 幂等守卫；
>   双端构建 **0 警告 0 错误**；`scripts/deploy.ps1` 已接入；
>   实测日志（G 端 BepInEx+桥）：`shape=BepInExPlugin`、侧根=`MLLoader`、`behaviour mounted`、`contract host ready`、`started in 258 ms`；
>   D 端（原生 ML）：`shape=MelonMod`、侧根=游戏根、同样挂载成功；两端各自生成 `<游戏目录>\OpenNestModMenuLogs\modmenu.log`。
> - ✅ **T2 完成（双通道均已实测）**：注册表 + 扫描调度 + 内置设置页；
>   主动通道日志 `provider 'OpenNestModMenu' ... from=active`；被动通道经临时验证：`candidates=1 new=1` → `active=0, scanned=1` + `discovered ... (passive)`。
>   延迟扫描已验证（1s/5s/15s：`assemblies` 112→124→125，即桥在首帧加载 MLL 模组后能被看到）。
> - ✅ **T3 完成（双端实测）**：新增 `Loaders/{LoaderInfo,LoaderReflect,LoaderDetector,ModInventory}.cs`；
>   G 端（桥）：`host=BepInEx + ML桥 viaBridge=True(type) ... interop=宿主(BepInEx，经 Il2Cpp* 别名)`，清单 `entries=10 loaded=6 deps=4`（3 个 BepInEx 插件 + 3 个 MLL 模组）；
>   D 端（原生 ML）：`host=MelonLoader viaBridge=False interop=MelonLoader`，清单 `entries=7 loaded=4 deps=3`；
>   实测发现并修复：`Chainloader.PluginInfos` 反射取值为 null（改用类型扫描兜底）、桥自身插件被前缀黑名单误跳（现可枚举，版本 2.3.2）。
> - ✅ **T4 完成（重设计版 + 三项实测反馈修复）**：
>   - 界面：`UI/{ModMenuTheme,ModMenuDisplay,ModMenuListView,ModMenuUI}.cs` —— 面板 1180×700，筛选 chip（全部/模组/依赖/禁用，带计数），
>     行内 = 来源 pill + 名称 + 版本/状态（长名省略号），右栏 = 名称 + 三枚 pill + 标签值行（12 行池），底栏 = 统计/interop/刷新时间。
>   - 修复：来源标记逐条目判定（不再全标 `[桥]`）、点击穿透（`UiInputGuard` 压制 59 个外部 raycaster + 层级抬升，实测 `z-order: ours=32767, raised 1 canvas`）。
>   - 验证：`runlog/modmenu_ui4.png` / `runlog/modmenu_ui5.png`（双端构建 0 警告 0 错误，无异常日志）。
>   - ⚠️ 待用户实机确认：**真实按键 F6**（合成按键不可靠，见上）；点击穿透在真实鼠标下的手感。
> - ✅ **T5 / T6 / T8 完成（本机 G 端实测取证，细证见更新记录十五）**：ESC 拦截 + `[详情][设置]` 页签与配置读写 + 统一顺序表与受管初始化调度。
> - ✅ **T10 / T11 / T12 完成（细证见更新记录十六）**：诊断面板（含帧性能/加载器/重复/能力）+ 双端部署与打包（6 个包）+ 字体缺字修复。
> - ✅ **布局/交互修复完成（细证见更新记录十七）**：右栏铺满高度、列表按加载顺序、分页行不折行、页宿主重叠、点击闸。
> - ✅ **T9 完成（2026-09-13）**：菜单入口不再本模组自己往原生容器里塞，而是挂到 `OpenNestUIKit`（软依赖）；
>   环境里没有 UIKit 时自动回退到本模组的自带覆盖 UI（见更新记录十九）。
> - ✅ **T13 运行时启停已实现并双端实测**（MLL 模组可当场停/起；BepInEx 插件保持重启生效，理由见 §E.1）。
> - ✅ **T4 完成**（重设计 + 三项实测反馈修复）——以下为后续进展：
>   - ✅ **T7 核心（启用/禁用）已实现**：详情卡片按钮 + `Loaders/ModFileState.cs`（`*.dll` ↔ `*.dll.disabled`，重启生效；实测几何日志确认按钮存在）。
>   - ✅ **I（本地化）第一版已实现**：语言键文件 `OpenNestModMenu.lang.ini` + 2s 热重载 + 自动补齐（见 §5.4）。
>   - ✅ **输入五层防护**（见 §5.2）：自管指针命中 + 压制外部射线 + `LookAtTarget.OnClickDown` 点击拦截 + 设备层吞输入 + **组件级交互锁**（实测 27 个组件 + 玩家冻结 + blocker）。
>   - ⏳ 待用户实机确认：真实鼠标下「菜单打开时点不到背景东西」（组件级交互锁这一层）、启用/禁用重启后生效。

---

## 九、待实测确认清单（不实测不下结论）

1. 两个加载器**实际的模组枚举顺序**（能否通过命名/子目录影响"尽早加载"）——桥已证实 `BepInExHost.Start()` 在**首帧**；
   已实测：本模组的启动发生在 **BepInEx 插件 `Load()` 阶段**（早于桥的首帧 Start，日志里 `providers=0` 即为佐证）。
2. ~~桥插件版本能否从 `Chainloader.PluginInfos` 读到~~ → **已实测：此路不通**（be.785 下 `PluginInfos` 反射为 `null`）。
   已改用类型扫描兜底并**验证成功**：读到桥插件 `BepInEx.MelonLoader.Loader.IL2CPP 2.3.2`（后续可把桥版本直接展示到 UI）。
3. 跨加载器 `Harmony` 是否真的完全隔离（同一模组装两版做对照实验）。
4. ~~IL2CPP 下 `AppDomain.CurrentDomain.GetAssemblies()` 能否枚举到全部托管模组程序集~~ → **已实测确认可用**：
   启动扫描看到 112 个程序集，1s/5s/15s 延迟扫描分别为 124/125 —— 增量即桥在首帧加载的 MelonLoader 运行时 + `MLLoader\Mods` 下的 MLL 模组。
5. BepInEx 插件扫描是否跳过 `~`/`.` 前缀文件（决定 MLL 排除语义能否被 BepInEx 复用）。
6. 桥配置 `io.bepis.melonloader.loader.cfg` 为何在本机不存在（版本差异/首次启动才写？）——决定调试面板是否要显示"桥配置缺失"。

> **已核实、从本清单移除**：桥的 interop 来源（宿主 BepInEx，哈希一致）、MelonLoader 根目录（`MLLoader\`）、别名写入时机（preloader 构造函数，同启动生效）、MLL 原生顺序语义（`MelonPriority` + 依赖拓扑）、MLL 排除语义（`~`/`.`/`Broken`/`Retired`/`Disabled`）。
