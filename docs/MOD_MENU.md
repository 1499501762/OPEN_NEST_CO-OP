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
> **桥模组源码**：`D:\Dev\BepMl\BepInEx.MelonLoader.Loader`（`BepInEx.MelonLoader.Loader.IL2CPP/Plugin.cs`、`MelonLoader/Hosting/{BepInExHost,Il2CppInteropAliasInjector,ModCompatScanner}.cs`、`MelonLoader/Melons/MelonFolderHandler.cs`、`MelonLoader/Melons/MelonBase.cs`、`BepInEx.MelonLoader.Loader.Patcher/Patcher.cs`）；
> **本机 BepInEx 端实测**：`G:\SteamLibrary\...\BepInEx\{plugins,patchers,interop,config}` + `MLLoader\`，`D:\SteamLibrary\...\{Mods,UserLibs,MelonLoader}`。
>
> **更新记录**：
> - 2026-09-12 建档：需求与标准整理（功能清单 / 加载器来源标记 / 桥接兼容 / 统一加载顺序 / 覆盖 UI 规范 / 文件级拆解 / 任务列表）。**尚无实现代码**。
> - 2026-09-12（二）§1.1 “配置持久化”行更新：本模组已新增标准 INI 配置文件 `CoopConfig`（`docs/CONFIG.md`），
>   设置中心直接**复用**它（不再自建 key=value 文件）。
> - 2026-09-12（三）按用户决策修订：**与 Core 独立**（自带源码副本 + 独立命名空间，不再往 `OpenNestCore.ModMenu` 放契约，改放 `OpenNestModMenu.API`）；
>   补 §1.2 独立性技术后果；§3.2/§3.3 用**桥源码 + 本机环境实测**替换全部“待实测”（别名策略 / 目录真值 / 哈希证据）；
>   补 §4.2 加载器原生顺序语义（`MelonPriority` + 依赖拓扑）；§E 补 MelonLoader 原生排除语义与 `--no-mods`；§七 改为独立工程布局。

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
| `src/OpenNestCoop/Core/Config/CoopConfig.cs` | `OpenNestModMenu.Config` | 拷一份（**格式/语义对齐** `docs/CONFIG.md`），但**不引用 OpenNestCoop**；本模组配置文件独立为 `BepInEx/config/OpenNestModMenu.cfg` |

**必须自写**（Core 内没有）：

| 能力 | 说明 |
|---|---|
| 加载器探测 / 清单 / 重复加载检测 | 全新（§三） |
| 启停（文件重命名）+ 顺序表 | 全新（§四） |
| 第三方注册契约 + 初始化调度器 | 全新（`OpenNestModMenu.API`） |
| 调试面板 | 自写（用拷来的 `FrameProfiler`） |
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
- 配置读写（**标准 INI**，格式对齐 `docs/CONFIG.md`；文件 `BepInEx/config/OpenNestModMenu.cfg`），首次运行生成默认、损坏回退默认

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
| 方式 | 适用 | 机制 |
|---|---|---|
| 运行时热切换 | 仅**声明支持**的受管模组（`CanToggleAtRuntime=true`，模组自己实现可逆的 `OnEnable/OnDisable`） | `IModMenuProvider.OnEnable/OnDisable` |
| 重启生效（通用） | 所有模组（含第三方未注册） | **文件重命名 `.dll` ↔ `.dll.disabled`** —— 两个加载器都只扫 `*.dll`，故跨加载器通用 |

- 禁用后 UI 标灰 + "重启生效"角标；下次启动时状态行提示"已禁用 N 个模组"。
- 写盘失败（文件被占用/无权限）必须给出明确 toast + 日志，不静默失败。
- ⚠️ **MelonLoader 另有原生「排除」语义**（`MelonFolderHandler._nameExclusions`，已核实）：文件名以 `~` 或 `.` 开头、或精确名为 `Broken`/`Retired`/`Disabled` 的**会被 MLL 跳过**。
  该语义**只对 MLL 生效**（BepInEx 只扫 `*.dll`，`~x.dll` 仍会被加载）→ **跨加载器通用禁用仍以"改扩展名"为准**；MLL 专属排除只在 UI 提示"此文件会被 MelonLoader 跳过"。
- 桥启动参数 `--no-mods`（`LoaderConfig.Disable`）会**禁用全部 MLL 模组** → 状态行必须显示该状态（否则用户会以为启停功能失效）。
- 顺序语义差异（见 §四）：MLL 模组可用 `MelonPriority` / 依赖特性声明顺序；BepInEx 插件**没有**顺序语义。

### F. 统一加载顺序（见 §5）

### G. 统一设置中心（UI）
- 分类页签（按模组 / 按功能域）+ 左侧列表 + 右侧内容区
- 设置项控件族：布尔开关、数值（滑条或 ± 步进）、枚举（按钮循环）、动作按钮、重置默认、快捷键绑定
- 搜索过滤；滚动列表 + **实例缓存**（IL2CPP 下每帧全遍历是掉帧元凶）
- 确认弹窗（重置 / 需重启提示）；操作反馈走 `NativeUi.Toast`

### H. 调试面板
- 帧性能：`FrameProfiler`（FPS / 平均 / 最差帧 / 每模块 `ms/s` top N）
- 加载器诊断：宿主 / 格式 / 桥版本 / 双 Harmony 检测 / 重复加载 / 各加载器目录路径
- 能力探测结果：`NativeUi.Available`、本地化是否可用、光标是否可用、原生字体能力开关状态
- 日志：运行时切 `CoopLog.Level`、显示配置/日志文件路径

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
| G 端 | `MLLoader\Mods\` | `IronNestFCS.dll`、`IronNestFCS.CustomRecords.dll`、`IronNestEnemyTracker.dll`（**无** `OpenNestCoop.MelonMod.dll`） |
| D 端（原生 MLL） | `Mods\` | `OpenNestCoop.MelonMod.dll`、`IronNestEnemyTracker.dll`、`IronNestFreecam.dll` **+ 依赖混放** `LiteNetLib.dll`、`SharpGLTF.Core.dll`、`SharpGLTF.Runtime.dll` |
| D 端 | `UserLibs\` | `LiteNetLib.dll`、`SharpGLTF.Core.dll`、`SharpGLTF.Runtime.dll`（与 `Mods\` 重复一份） |

> ⚠️ **依赖 dll 与模组 dll 混放**（D 端 `Mods\` 与 G 端 `plugins\` 顶层都有）→ 清单**绝不能"目录里每个 dll 都算模组"**：以加载器元数据（`Chainloader.PluginInfos` / `MelonMod.RegisteredMelons`）为"模组"判据，其余标 `依赖/非模组`。

### 3.3 桥接兼容风险表

| 风险 | 说明 | 处置 |
|---|---|---|
| **重复加载 / 双初始化** | 史上出现过：G 端同时存在 BepInEx 与 `MLLoader`，MelonMod dll 误部署到 `MLLoader\Mods` → 同一模组被两个加载器各初始化一次（症状：`xxx already injected`、`TypeLoadException`）。 | **检测同名程序集跨目录同时存在 → UI 打红色警告 + 提示禁用其一**；模组自身必须做**幂等初始化守卫**（静态标志，重复初始化直接返回） |
| **双 Harmony 实例** | BepInEx 的 `0Harmony` 与 MelonLoader 内嵌 `0Harmony` 是**两个程序集** → 各自 patch，互相不可见（无法通过自己的 Harmony 枚举到对方的 patch；同一方法可能被双方各 patch 一次，执行顺序不确定）。 | ModMenu **不假设**能跨加载器统一管理 patch；调试面板**如实标注"patch 视图受加载器隔离"**；对已知重复 patch 风险给出提示 |
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
| **统一展示顺序** | 全部模组（受管 + 不受管） | ModMenu 维护权威顺序表（按 名称/作者/目录/手动拖拽），**仅影响展示与"受管初始化顺序"**，不改加载器行为 |
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

### 4.4 顺序表持久化

- 存 INI（§6.2），键 = 程序集名，值 = 序数。
- 表中不存在的模组（新装）→ 追加到末尾；已卸载的条目 → 保留但标灰（重装后恢复顺序）。

---

## 五、UI 规范（v1：覆盖 UI）

### 5.1 结构与尺寸

```
Canvas "OpenNestModMenu"（ScreenSpaceOverlay, sortingOrder 需与 CoopUIManager 协商, DontDestroyOnLoad）
└─ Blocker（全屏半透明, raycastTarget=true, SetAsFirstSibling）
   └─ Panel（居中，建议 1000×640，纯色）
      ├─ TitleBar（标题 + 关闭按钮）
      ├─ LeftList（分类/模组滚动列表，宽 ~300）
      ├─ RightContent（设置项/详情，宽 ~680）
      └─ StatusBar（加载器诊断摘要 + 提示）
```

- `CanvasScaler`：`ScaleWithScreenSize` / 1920×1080 / `matchWidthOrHeight=0.5`（`UiKit.CreateCanvas` 已是此默认）
- **`sortingOrder`**：`CoopUIManager` 用 32766。ModMenu 建议 **32764**（低于联机菜单）或与联机菜单协商；**须在实现时定死并写入本文档**（避免互相叠压）。
- 字号：正文 15–16 / 标题 20；**一律固定字号 + `enableAutoSizing=false`**（历史结论：autoSizing 是"忽大忽小"根因）
- 控件尺寸不按 `sprite.border` 反推（v1 无 sprite，天然规避）

### 5.2 控件与输入

- 点击走游戏 EventSystem（**不新建 `EventSystem`**）；`MakeBlocker` 防点击穿透到 3D 世界
- 列表滚动用 `ScrollRect` + **可见项实例缓存**（禁用/隐藏项不参与每帧布局）
- **v1 不做文本输入**（`CoopInputBox` 的 IME 复杂度不值得在 v1 承担）；数值用滑条/步进
- 打开菜单时**禁用游戏侧交互**（防止误操作炮台）：靠 blocker 拦截射线即可；必要时叠加 `NativeUi.SetEscapeMenuBlocked(true)`

### 5.3 原生素材：保留不激活

- `ModMenuConfig.UseNativeSkin = false`（默认）
- 为 `true` 时才：`UiSpriteBank.FromResources/CollectSprites/CaptureFromScene` + `UiKit.MakePanel(bgSprite)` / `MakeButton(bgSprite:)`
- ⚠️ 复用时的已知坑（`docs/NATIVE_UI.md` §6、repo memory）：`Resources.Load<Sprite>` 按名**恒 null**（要用 `LoadAll<T>` / `CaptureFromScene`）；
  `pixelsPerUnitMultiplier` 在 IL2CPP 下不可靠（**必须把 `border ÷ scale` 烤进 sprite 副本**，用 `CopySprite` / `MeasureActualBorder`）。
- 代码保留但**不在启动路径调用**（不产生主菜单捕获开销与场景卸载风险）

---

## 六、标准清单

### 6.1 代码标准
- **独立性**：不引用 `OpenNestCore`/`OpenNestCoop` 程序集；拷入的基建一律改 `OpenNestModMenu.*` 命名空间（§1.2）
- **名称禁忌**：程序集名/类型名**不得包含 "MelonLoader"**（桥的 `AssemblyResolve` 会把含该串的请求重定向到桥自己的 `MelonLoader.dll`）
- **契约程序集** `OpenNestModMenu.API.dll` 不含 Unity/游戏类型依赖；第三方为**软依赖**（只引用该 dll，ModMenu 缺失时静默跳过）
- 平台差异一律 `#if MELONLOADER` + `PlatformUsings.cs` 别名；引 `Assembly-CSharp` 的桥接实现只能放模组侧，拷入的 UI 基建**不得**引用 `Assembly-CSharp`
- **禁止** `foreach` 遍历 Il2Cpp 集合（用显式索引/枚举器）；`AddComponent<T>()` 仅限 Unity 内置类型（自定义 MonoBehaviour 会 `TypeInitializationException`）
- 委托桥接用显式 `(Action<T>)` 转换；**不订阅原生事件**，用轮询（`NativeUiPoll` 模式）
- `AccessTools.Method` 查带参方法**必须显式传参数类型数组**（否则 patch 静默失败）
- 反射遍历程序集/类型**逐项 try-catch**
- 幂等初始化守卫（防桥环境重复加载）

### 6.2 配置标准
- **标准 INI**（格式对齐 `docs/CONFIG.md`），文件 `BepInEx\config\OpenNestModMenu.cfg`，UTF-8，支持热重载；模组自带 `CoopConfig` 同构实现（不引用 OpenNestCoop）
- ModMenu 自身开关（热键 / `UseNativeSkin` / 顺序表 / 禁用表 / 日志等级）与**第三方设置项分节存放**（段 = 模组 Id）
- **不用 JSON/Newtonsoft**（双端依赖不一致）
- 解析失败/损坏 → 回退默认值并 `Warn`，不抛异常

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
- `sortingOrder` 不撞；`NativeUi` 桥接能力与 OpenNestCoop 保持等价
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

### 7.1 契约工程 `src/OpenNestModMenu.API/`（第三方引用；纯 .NET，无 Unity/游戏依赖）

```
src/OpenNestModMenu.API/
  OpenNestModMenu.API.csproj   net6.0；无 Unity/interop/游戏引用（双端同一份）
  IModMenuProvider.cs      ★ 第三方契约：Id/DisplayName/Version/Author/AutoInit/CanToggleAtRuntime
                             + BuildPage(IModMenuPage) / OnEnable / OnDisable / ResetToDefaults
  IModMenuPage.cs          页面构建契约（控件工厂：开关/滑条/步进/枚举/按钮/分组）
  ModMenuModel.cs          数据模型：ModEntryInfo（Host/Format/Path/Enabled/Managed/Order/优先级/依赖/冲突标记）、
                             SettingItem（Bool/Number/Enum/Action/Key）
  ModMenuHost.cs           静态入口：Register/Unregister/IsHostAvailable（ModMenu 未安装时安全降级）
```

### 7.2 模组工程 `src/OpenNestModMenu/`（BepInEx 壳 + 主体）

```
src/OpenNestModMenu/
  Plugin.cs                          BepInEx 6 入口壳（BasePlugin：日志 → ModMenuRuntime.Startup）
  MelonModEntry.cs                   MelonLoader 入口壳（#if MELONLOADER）
  GlobalUsings.cs / PlatformUsings.cs 平台命名空间适配
  OpenNestModMenu.csproj             net6.0；ProjectReference → OpenNestModMenu.API；**不引用 OpenNestCore/OpenNestCoop**
  Vendor/                            ★ 从 Core/OpenNestCoop 拷入的源码副本（独立命名空间，见 §1.1）
    UiKit.cs / INativeUiService.cs / UiSpriteBank.cs   → namespace OpenNestModMenu.UI
    CoopLog.cs / ILogger.cs                            → namespace OpenNestModMenu.Logging
    FrameProfiler.cs                                   → namespace OpenNestModMenu.Diagnostics
    CoopConfig.cs                                      → namespace OpenNestModMenu.Config
  ModMenuRuntime.cs                  运行时根：启动/关停、服务装配、幂等守卫
  Core/
    ModMenuConfig.cs                 配置读写（key=value）+ 顺序表 + 禁用表 + UseNativeSkin + 热键
    ModMenuPaths.cs                  目录解析（BepInEx\plugins、MLL根\Mods、日志目录、配置路径）
    ModMenuBehaviour.cs              MonoBehaviour（Update：热键轮询/语言变化/场景变化，低频节流）
  Loaders/
    LoaderInfo.cs                    宿主/格式/桥 探测结果模型
    LoaderDetector.cs                反射探测（无硬依赖；逐项 try-catch）
    InteropAliasStatus.cs            只读报告：interop 目录/dll 数/.melonloader-aliased 指纹（§3.2）
    ModEntry.cs                      条目（程序集名/显示名/版本/作者/格式/宿主/路径/启用态/优先级/依赖）
    ModInventory.cs                  内存清单（Chainloader.PluginInfos + MelonMod.RegisteredMelons，**首帧后**采集）+ 磁盘清单取并集
    ModFileState.cs                  启停：.dll ↔ .dll.disabled 重命名（+ MLL 排除语义提示）
    DuplicateLoadDetector.cs         跨目录重复加载检测（§3.4）
    BridgeCompatReport.cs            读取/展示桥 ModCompatScanner 结论 + --no-mods 状态
  Registry/
    ModMenuRegistry.cs               注册表实现（契约在 API 工程；含 ModMenu 未就绪时的暂存）
    AssemblyScanner.cs               被动发现 IModMenuProvider 实现（反射 + try-catch）
    ModInitScheduler.cs              受管模组的统一初始化顺序调度（尊重 MLL 优先级/依赖，§4.2/§4.3）
  UI/
    ModMenuUI.cs                     主面板（标题栏/左列表/右内容/状态行）
    ModMenuListView.cs               滚动列表 + 实例缓存
    ModMenuPageBuilder.cs            设置项 → 控件渲染（开关/滑条/步进/枚举/按钮/重置）
    ModMenuDialogs.cs                确认弹窗 + "需重启"提示
    ModMenuEntryInjector.cs          原生主菜单 / ESC 菜单注入入口按钮（含 slot 让位）
  Bridge/
    ModMenuNativeUiBridge.cs         INativeUiService 游戏侧实现（移植 IronNestNativeUi 子集 + 幂等）
    NativeCapabilityProbe.cs         能力探测 + "缺失方法"禁用表（GetFont 等默认禁用 + 配置开关）
  Debug/
    ModMenuDebugPage.cs              调试面板（FrameProfiler / 加载器诊断 / 重复加载 / 能力探测 / 日志等级）
```

### 7.3 脚本

```
scripts/deploy.ps1                  加 OpenNestModMenu 双端部署（BepInEx plugins + MLL Mods）
scripts/package.ps1                 加 OpenNestModMenu 双端包（光 MOD；Standalone 视需要）
```

---

## 八、实施任务列表（建议顺序）

| # | 任务 | 产出 |
|---|---|---|
| T1 | 骨架：`OpenNestModMenu.API` 契约工程 + 模组工程 + 双端壳 + `Vendor/` 源码副本 + 日志注入 + 幂等守卫 | 双端能构建、能加载、日志出现 |
| T2 | 注册表 + 双通道注册（主动暂存 / 被动程序集扫描） | 第三方可注册并出现在列表 |
| T3 | 加载器探测 + 清单（宿主/格式/路径/桥标记） | 调试输出正确标记 |
| T4 | 覆盖 UI 骨架（Canvas/Blocker/标题栏/列表/状态行） | 菜单可显示、可开/关 |
| T5 | 热键 + ESC blocker 成对 + 关闭清理 | 开关入口可用 |
| T6 | 设置项控件族 + 设置中心页面渲染 | 第三方设置页可展示 |
| T7 | 启停（文件重命名）+ 重启提示 + 失败提示 | 可禁用/启用并持久 |
| T8 | 统一加载顺序（展示顺序表 + `ModInitScheduler` + 双通道注册） | 顺序可调 + 受管调度 |
| T9 | 原生菜单注入入口（主菜单 + ESC 全实例 + slot 让位） | 第二入口可用 |
| T10 | 调试面板（FrameProfiler + 加载器诊断 + 重复加载 + 能力探测） | 诊断页可用 |
| T11 | 重复加载检测 + 双 Harmony 隔离说明 | 风险可见 |
| T12 | 双端部署/打包脚本 + 四组合实测 + 文档回填 | 可交付 |

---

## 九、待实测确认清单（不实测不下结论）

1. 两个加载器**实际的模组枚举顺序**（能否通过命名/子目录影响"尽早加载"）——桥已证实 `BepInExHost.Start()` 在**首帧**，但 BepInEx 插件与 MLL 模组之间的相对顺序未测。
2. 桥插件版本能否从 `Chainloader.PluginInfos` 读到（GUID/版本）——决定"桥版本"标记方式（备选：读 `BepInEx\patchers` 的程序集版本）。
3. 跨加载器 `Harmony` 是否真的完全隔离（同一模组装两版做对照实验）。
4. IL2CPP 下 `AppDomain.CurrentDomain.GetAssemblies()` 能否枚举到**全部**托管模组程序集（被动注册的前提）。
5. BepInEx 插件扫描是否跳过 `~`/`.` 前缀文件（决定 MLL 排除语义能否被 BepInEx 复用）。
6. 桥配置 `io.bepis.melonloader.loader.cfg` 为何在本机不存在（版本差异/首次启动才写？）——决定调试面板是否要显示"桥配置缺失"。

> **已核实、从本清单移除**：桥的 interop 来源（宿主 BepInEx，哈希一致）、MelonLoader 根目录（`MLLoader\`）、别名写入时机（preloader 构造函数，同启动生效）、MLL 原生顺序语义（`MelonPriority` + 依赖拓扑）、MLL 排除语义（`~`/`.`/`Broken`/`Retired`/`Disabled`）。
