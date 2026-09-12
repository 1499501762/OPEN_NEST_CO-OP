# 模组配置文件（CoopConfig）

> **目的**：让用户可以**不改代码、不用重启**地开关部分同步功能（默认值 = 原来的行为，装了配置不写也不变）。
> 当前提供：**猫同步开关**、**唱片机同步开关**、**`Calculate Universal Button` 同步开关（默认关）**。
>
> **信息可信度 / 来源**：`src/OpenNestCoop/Core/Config/CoopConfig.cs`（唯一实现）、
> `src/OpenNestCoop/Core/CoopBehaviour.cs`（驱动热重载）、
> 各被门控模块（`GameSync/CatSync.cs`、`SyncV2/CatSyncV2.cs`、`GameSync/RecordPlayerSync.cs`、`SyncV2/RecordPlayerSyncV2.cs`、
> `GameSync/ButtonClickSync.cs`）；实体路径来自 F10 交互工具实测（2026-09-12 用户提供）。
>
> **更新记录**：
> - 2026-09-12 建档：选型（标准 INI vs BepInEx `ConfigFile` vs MelonPreferences）、路径/格式、3 个设置项、热重载、扩展步骤、双端一致性。

---

## 一、选型：为什么是「标准 INI 文本 + 生态标准目录」

用户要求“看看通用的模组配置文件的标准”。三条候选路线的评估：

| 方案 | 优点 | 缺点 | 结论 |
|---|---|---|---|
| BepInEx `ConfigFile` / `ConfigEntry<T>` | BepInEx 生态事实标准；ConfigurationManager 直接列出；自动保存/文件监视 | **只有 BepInEx 有**；核心代码需引用 BepInEx 类型，MelonLoader 端要写第二套；`ConfigEntry.Value` 是引用对象，静态核心访问要传引用 | ❌ 单端专用 |
| MelonLoader `MelonPreferences` | MelonLoader 生态标准；写 `UserData/MelonPreferences.cfg` | 同上（只有 ML 有）；与 BepInEx 端两套行为/两套 bug 面 | ❌ 单端专用 |
| **自建 INI 文本（本次采用）** | **格式与 BepInEx `.cfg` 完全一致**（`[Section]` + `key = value` + `## 注释`）；双加载器**一套代码一套行为**；无平台依赖；BepInEx 的 ConfigurationManager 若开启“显示所有配置文件”也能读取/编辑它 | 需要自己写解析/保存/热重载（约 400 行，已含） | ✅ 采用 |

**目录选择**：按宿主放进**各自生态的标准位置**（用户找得到、和其它模组配置放一起）：

| 宿主 | 路径 | 说明 |
|---|---|---|
| BepInEx | `<游戏目录>/BepInEx/config/OpenNestCoop.cfg` | BepInEx 标准配置目录 |
| MelonLoader | `<游戏目录>/UserData/OpenNestCoop.cfg` | MelonLoader 标准用户数据目录 |
| 都不存在（便携/非标准部署） | `<persistentDataPath>/OpenNestCoop.cfg` | 兜底（`Application.persistentDataPath`） |

> 解析顺序见 `CoopConfig.ResolvePath()`：标准目录存在才用，否则回退 persistentDataPath，最后回退 CWD。

## 二、文件格式（首次运行自动生成）

```ini
## Open Nest Co-op v0.2.1-Alpha-1 配置文件
## 本文件由模组自动生成：新增设置项会自动补进来；改动**无需重启**（约 2 秒内热重载）。
## 布尔写法：true / false（也接受 1/0、on/off、yes/no）。
## ⚠️ 这些开关是本端行为开关，不参与握手协商——同一局两端请保持一致，否则该功能会表现为不同步。
## 文档：docs/CONFIG.md

[Sync]
## 猫同步开关。
## true（默认）= 同步猫：主机 AI 决策（状态/目标点/动画）+ 猫交互事件 + 客机位置偏差硬同步。
## false = 完全不同步猫（省带宽；若两端都关，猫的行为各自演算、互不影响）。
CatSync = true
## 类型: bool | 默认: true

[Interactables]
## 计算万能按钮同步开关：Artillery Computer Console/Calculate Universal Button。
CalculateButtonSync = false
## 类型: bool | 默认: false
```

**容错规则**（`CoopConfig.Parse`）：
- 注释行：`#` 或 `;` 开头（含 BepInEx 的 `##`）→ 忽略
- 段：`[Section]`；无段归属的键归到“当前段”（文件开头 → 空段）
- 键值：第一个 `=` 分割，两侧去空白；字符串值可加 `"`/`'` 引号
- 布尔：`true/false`、`1/0`、`on/off`、`yes/no`（忽略大小写）
- 非法/缺失/损坏 → **退回该项默认值**并告警，绝不抛异常
- **本模组不认识的键**（用户自加 / 旧版本遗留）→ 保留原样，保存时写回（同 BepInEx 对 orphaned entries 的做法）
- **被删掉的键** → 下次加载恢复默认值（不是保留内存里的旧值）

## 三、设置项清单

| 段 | 键 | 类型 | 默认 | 作用 | 门控点（代码） |
|---|---|---|---|---|---|
| `Sync` | `CatSync` | bool | `true` | 猫同步总开关（主机 AI 状态广播 / 客机位置偏差硬同步 / 猫交互事件 106+133、V2 `v2/cat/*`） | `CatSync.Tick/OnPacket/OnLocalCatEvent`；`CatSyncV2.Tick/OnPacket/OnLocalCatEvent/ReproduceCatEvent` |
| `Sync` | `RecordPlayerSync` | bool | `true` | 唱片机同步开关（播放中/曲目/音量/槽内唱片视觉插入） | `RecordPlayerSync.Tick/OnState/OnCmd/BuildRecordPlayerSnapshot/ApplyRecordPlayerSnapshot`；`RecordPlayerSyncV2.Tick/OnPacket/OnLateJoin` |
| `Interactables` | `CalculateButtonSync` | bool | `false` | `Artillery Computer Console/Calculate Universal Button` 点击同步（点击复现 + 4 toggler 状态轮询） | `ButtonClickSync.ShouldTrack` 显式名字匹配 |

> ⚠️ `CalculateButtonSync` **默认关**（2026-09-12 新增，未实测）；开启需**双端**都设为 `true`。
> 唱片**物品位置**（拿起/放下）由 `RecordItemSync` 负责，**不受** `RecordPlayerSync` 影响。

## 四、生效时机与热重载

- 加载：`CoopRuntime.Startup()` → `CoopConfig.Init()`（早于模块注册与首个 Tick；模块在 Tick/OnPacket **当次读取**配置，故不存在“注册时快照”问题）
- 热重载：`CoopBehaviour.Update()` → `CoopConfig.Tick(dt)`，每 **2 秒**比对文件 `LastWriteTimeUtc`；变化即重读+回写规范文件，
  并在主日志打印**变化项**：`[CoopConfig] reloaded (<路径>) CatSync=true ... -> CatSync=false ...`
- 生效范围：下一次 Tick / 下一个包即生效（猫 1/3s、唱片机 0.2s、按钮实时）
- ⚠️ **任务进行中改开关**会让两端短暂不一致（例如主机先改、客机未改）→ 建议在**主菜单**改，或改完等 2 秒确认两端日志行一致
- ⚠️ 开关是**本端**行为开关，**不参与握手协商**（不校验、不拒绝）。所以“两端不一致”不会报错，只会表现为该功能不同步——
  排障第一步就是比对两端启动日志的 `[CoopConfig] values ...` 行

## 五、代码位置与“新增一个设置项”的步骤

实现：`src/OpenNestCoop/Core/Config/CoopConfig.cs`（单文件；BepInEx 与 MelonLoader 共用同一份源码）。
对外 API：

| 成员 | 用途 |
|---|---|
| `CoopConfig.<名称>`（如 `CatSync`） | 读取当前值（缺文件/缺键/格式错 → 默认值） |
| `CoopConfig.FilePath` | 实际生效的配置路径（排障用） |
| `CoopConfig.Reload()` / `Save()` / `Set(段,键,值)` | 显式重读 / 立即保存 / 程序化改值（供模组菜单/自动化） |
| `CoopConfig.Changed`（event） | 热重载且值真的变了时触发 |
| `CoopConfig.Summary()` | `键=值` 汇总串（日志/两端对比；F10 交互工具也显示） |

**新增设置项三步**（以加个 `CoffeeSync` 为例）：
1. 加对外只读属性：`public static bool CoffeeSync => GetBool("Sync", "CoffeeSync");`
2. 在 `CoopConfig.Defs` 登记：段 `Sync` / 键 `CoffeeSync` / 类型 `bool` / 默认 `true` / 说明（多行用 `\n`）
3. 在被门控模块的入口加 `if (!CoopConfig.CoffeeSync) return;`（**发送端与接收端都要加**），并更新本文档表格 + `docs/` 对应模块文档

## 六、与其它机制的关系

- **关键包保护表**（合包满时不丢关键消息）是**另一个**机制，不读配置文件：见 `docs/API.md`
  （`NetManager.RegisterCriticalType` + `Critical/High` 优先级模块自动登记）。
  门控模块（猫/唱片机）用的是可重发的周期状态或事件，故不依赖关键包保护。
- **模组菜单**（`docs/MOD_MENU.md`）的统一设置中心后续可直接复用 `CoopConfig`（读 `Defs` 渲染控件、写 `Set()` 保存、订阅 `Changed`）。

## 七、待办

- [ ] 模组菜单设置中心接入（`docs/MOD_MENU.md` §1.1“配置持久化”行已改为“复用 `CoopConfig`”）
- [ ] `CalculateButtonSync` 开/关双端实测（默认关，需用户确认开关效果后决定是否改默认为开）
- [ ] 视需要增加数值/字符串项（框架已支持 `int/float/string`，当前仅有 bool 项）
- [ ] 视需要增加“仅主机/仅客机生效”的项（当前所有项两端都生效）
