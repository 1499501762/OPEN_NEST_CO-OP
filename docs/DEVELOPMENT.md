# 开发文档（Development Guide）

本文件整理原 README 中的开发/技术细节，供开发者参考。

---

## 更新记录

- 2026-08-25：`deploy.ps1` 改为**默认双端部署**（BepInEx 版 → G 盘 `BepInEx\plugins\`；MelonLoader 版 → D 盘 `Mods\`+`UserLibs\`），`-BepOnly`/`-MllOnly` 只部署一端；修复 `HarmonyPatches.cs` MLL 端缺 `using Localisation = Il2CppLocalisation;`。
- 2026-08-25 **帧/网络性能优化（日志降级）**：用户反馈帧性能差 + 网络轻微问题。排查日志发现大量**高频诊断日志**用 `CoopRuntime.LogSource?.LogInfo` 直调（不走 CoopLog，无等级过滤/节流，Release 默认 Info 下每次执行字符串格式化+写盘 → 日志 I/O 卡帧；网络 RTT 400-500ms 与帧率低同源——Update 变慢拖长 Ping/Pong 往返）。**修复**：批量把 30+ 处高频诊断日志（`[Net] recv batch`/`[Net] flush`/`[ControlSync] dial-diag/charge-dial/registered/reg`/`[ValueSync] diag`/`[CatSync] host AI state`/`[GunLinkSync] scan`/`[RecordItemSync]/[PunchcardSync] host send`/`[ChargeButtonSync]/[ChargeInventorySync] broadcast`/`[Requisition] powder/points`/`[Teleprinter]` 全套/`[MapMarker] erase` 及 V2 对应）从 `LogInfo` 转 `CoopLog.Debug(key, Func, interval)`——**默认关闭（Release=Info），Func 惰性求值零开销**，需要时命令行开 Debug 级。保留 Info 的关键联机摘要：`MissionSync/MissionSyncV2 host broadcast`（2s 保活）、`NetGovernor UP/DOWN`（1s 分级评估）、`[Net] stats 10s`、会话事件。教训：**诊断日志一律走 CoopLog.Debug（默认关），不得用 LogInfo 直调高频路径**；`CoopLog` 已全局 using（`global using OpenNestCore.Logging`）。
- 2026-08-25 **独立日志系统（ModLog + 路由）**：新增 `OpenNestCore/Logging/ModLog.cs`——**独立文件日志**（StringBuilder 缓冲 + 1s 间隔批量落盘，性能好，不附加主日志/控制台）。`CoopLog.RouteToFile(keyPrefix, file)` 按 key 前缀路由：诊断/联机日志写到 `游戏目录/OpenNestLogs/*.log`（frame.log/net.log/sync.log），**主日志只留会话/错误 → 控制台不刷屏 → 帧性能提升**。`CoopRuntime.InitFileLogs` 注册路由（frame/net./Net./各联机模块前缀→sync）；`CoopBehaviour.Update` 每帧 `ModLog.Flush`。
- 2026-08-25 **帧性能定位与优化（frame.log 取证）**：主机 F9 帧诊断显示 22fps 时 `ControlSync` 每帧 ~21ms（`ValueSync` 每帧遍历 71 绑定调 `IsBusy()`/`GetValue()`——**IL2CPP interop 调用开销大**）+ `TickAll` ~13ms（`PunchcardSync`/`RecordItemSync` 每 0.2s 全场景 `FindObjectsOfType`、`TeleprinterSync` 打印中 0.1s 扫描）+ 其他 ≈46ms/帧 → 帧率 10-30。**修复**：①`ValueSync` 拖拽检测**降频 0.05s** + **跳过无 `IsBusy` 语义的绑定**（IsBusy IL2CPP 调用 20 倍降，释放 settle ~1 帧延迟可接受）；②`PunchcardSync`/`RecordItemSync`/`TeleprinterSync` 加**实例缓存**（`FindObjectsOfType` 从每 tick 移到 `CacheRefreshSec=3s` + 场景 buildIndex 变化刷新——全场景扫描降 ~10-20 倍）。教训：**IL2CPP 下每帧遍历大量绑定调属性/委托开销极大；`FindObjectsOfType` 全场景扫描是 TickAll 大头——一律实例缓存 + 低频刷新**。
- 2026-08-25 **帧性能二轮（ControlSync 仍 350ms/s）**：元凶 = `ValueSync` 低频 tick（0.2s）遍历 71 绑定全量 `GetValue`（IL2CPP 属性访问）。修复：①低频 tick **0.2s→0.5s**（静止/非拖拽控件 GetValue 降 2.5 倍）；②高频 tick（0.033s）用缓存的 **`PrevDragging`**（非每帧 IsBusy）同时处理**正在拖拽的普通控件**——拖拽中控件仍 30Hz 实时跟随（不牺牲交互精度），静止控件 0.5s 低频。
- 2026-08-25 **铁巢位置两端不同步修复**：`PlayerSync` 位置包（普通帧 unreliable + 2s 心跳 reliable）**无 seq**——unreliable 位置帧先发后到覆盖心跳（reliable）权威位置 → 对端铁巢回跳/不同步。修复：`PlayerPos` 加 **seq**（发送端 `_posSeq` 递增，心跳与普通帧共用；接收端按 pid 记录 `_recvSeq` + `IsNewer` 去旧）。包格式变更：`PlayerPos = type, pid, seq(ushort), hb, x, y, z, yaw, moveFwd, moveStrafe, speed`。
- 2026-08-25 **铁巢初始位置对齐**：`PlayerSync.CreateAvatar` 时 `TargetPos` 默认 0,0,0 → 新玩家加入/场景切换后化身（对方铁巢角色）初始在错误位置（直到心跳 2s 才纠正）。修复：①缓存各 pid 最后位置 `_lastKnownPos`（OnPacket 更新，Avatar 创建时用 `TargetPos=缓存` 初始对齐）；②`SyncRoster` 检测到新成员 → `_forcePosSend` 下帧立即发本地位置（不等 0.1s tick/心跳）；③场景 `buildIndex` 变化 → `_forcePosSend`（场景切换后立即广播自己的 spawn 位置）。
- 2026-08-25 **铁巢（炮台）位置同步**：铁巢可移动（大部分固定）——炮弹发射位置/战术地图基于铁巢位置，两端铁巢位置不同 → 炮弹发射位置在地图上不同（且地图实体位置也随之偏，因 `FireMission.ToLocalSpace`/`PositionInRootSpace` 换算基准=铁巢/根空间）。修复：`ControlSync.RegisterTurret` 加 **3 个 ValueSync 位置绑定**（`__turret/posX/Y/Z`，`Get=transform.position`、`Set=ApplyTurretPos`、deadzone 0.05m）——**主机权威（`ClientNoSend`，2026-08-25 修正）**：由主机 `HostTick` 检测位置变化广播，客机只接收应用（避免“谁操作谁权威”导致两端互覆盖震荡）。低频 0.5s 检测 + 2s 心跳对齐。加 **`[TurretPosDiag]` 5s 诊断**（sync.log，ControlSync.Tick）对比两端 TurretController 位置（确认铁巢=TurretController.transform 及同步是否生效）。

## 技术栈

| 组件 | 选型 | 说明 |
|------|------|------|
| 加载器 | BepInEx 6 IL2CPP | 已随游戏安装，内置 HarmonyX + Cpp2IL |
| 传输 | Steamworks `SteamNetworking` P2P | 经典 P2P API，字节数组直发，Steam 中继自动打洞 |
| 大厅 | Steamworks `SteamMatchmaking` | 创建/浏览/加入/邀请 |
| 序列化 | LiteNetLib `NetDataWriter/Reader` | 仅用其可靠二进制序列化 |
| UI | Unity UGUI + TMP（ScreenSpaceOverlay） | 高 sortingOrder（32766），走游戏自身 EventSystem |
| 补丁 | Harmony | 游戏方法挂钩（FireShell/RequestFire/MissionManager/Teleprinter/Cat 等）；`TurretController.HandleInput` 已不再拦截——炮塔控制走 ControlSync 值同步（30Hz HighFreq），谁操作谁权威 |

> 说明：IL2CPP 游戏无法注入 Mirror/Netcode 等需编译进工程的网络库；
> Steamworks 已是游戏的一部分，是最优"轮子"。

---

## 目录结构

```
src/OpenNestCoop/            BepInEx 插件（壳）+ 平台无关核心源码
  Plugin.cs                  BepInEx 入口壳（BasePlugin：注入日志 → CoopRuntime.Startup）
  MelonModEntry.cs           MelonLoader 入口壳（#if MELONLOADER 编译）
  GlobalUsings.cs / PlatformUsings.cs  平台命名空间适配（如 using SleepyNodes = Il2CppSleepyNodes）
  Core/                      CoopRuntime（平台无关核心：Net 全局管理器 + 模块注册）、CoopBehaviour(Update)、NetConfig
  Net/                       NetManager(状态机/EnqueueBatch)、SteamLobby、SteamTransport、NetProtocol(MsgType)、
                             AutoJoin(--autohost/--autojoin/--sync)、LocalTransport、NetLagSim、PlayerSession
  GameSync/                  V1 同步：CoopSyncRegistry / ValueSync / ControlSync / TurretSync / PlayerSync /
                             RecordPlayerSync / ReloadSync / MapSync / MapMarkerSync / 各 ISyncedModule
                             （CoffeeSync/MissionSync/StateSnapshotSync/MissionEventSync/NotificationSync/
                               TeleprinterSync/CounterBatterySync/EntitySync/ReconPhotoSync/CatSync/RecordItemSync/
                               ShellSync/SequenceSync/HatchSync/ButtonClickSync/ArmSync/CylinderActionSync/
                               ChargeInventorySync/ChargeButtonSync/MapTokenSync/GunLinkSync/PunchcardSync/
                               M3EnvSync/RequisitionSync/PurchaseSync）
                             + 玩家化身 provider（AnimatorAvatar/CatCrew/ExternalModel/Humanoid/Default）
  SyncV2/                    V2 分层同步（--sync new）：HostDataLayer / ValueLayer / EventLayer / ButtonLayer
                             + 各模块 V2 版（PlayerSyncV2 / ReloadSyncV2 / ShellSyncV2 / ... / SyncV2Bootstrap）
  Patches/                   HarmonyPatches（开火/装填/任务/打字机/猫等挂钩）
  UI/                        CoopUIManager / CoopMenuUI（UGUI 联机菜单）、CoopLoc（本地化）
  Debug/                     InteractableNameTool（F9 交互名调试）
src/OpenNestCoop.MelonMod/   MelonLoader 版壳（#if MELONLOADER 编译）
src/OpenNestCore/            平台无关核心库（Avatar/IPlayerVisualProvider、AvatarPose、CrewRole、Logging/CoopLog 等）
tools/AsmDump/               程序集侦察工具（游戏类型 / Steam API 结构）
scripts/                     package.ps1（打包 4 包）、dualtest.ps1（双端测试）、deploy.ps1（单机 BepInEx 部署）、env.ps1、env.example.ps1
docs/                        API.md（扩展 API 文档）、本文件、SYNC_V2_DEV.md 等
```

> 构建产物与本地私有文件**不随仓库分发**（见 `.gitignore`）。

---

## 构建与部署

### 环境变量（全局变量表）

所有开发环境参数集中在 `scripts/env.ps1`（由 `env.example.ps1` 复制并填写本机路径）：

| 变量 | 含义 |
|------|------|
| `$GameDir` | 游戏安装目录（主机/打包源，G 盘） |
| `$ClientGame` | 第二个游戏安装（本地双开客户端 / MelonLoader 打包源，D 盘）；`deploy.ps1` 的 MLL 端部署目标（`Mods\`+`UserLibs\`） |
| `$SteamAppId` | 2950790 |
| `$BuildConfig` | Release / Debug |
| `$PluginName` | 部署目录名（OpenNestCoop） |
| `$SteamExe` | 游戏可执行名（Iron Nest Heavy Turret Simulator.exe） |
| `$LocalTestPort` / `$LocalTestLagMs` / `$LocalTestLagJitterMs` | dualtest.ps1 -Local 回环端口 / 延迟 / 抖动（默认 170ms + 50ms） |
| `$LobbyFile` | dualtest Steam 模式共享大厅文件 |
| `$BepInEx6Zip` / `$MLLZip` | package.ps1 Standalone 用加载器 zip 路径 |
| `$BepInExDir` / `$GamePluginsDir` | 派生路径（一般无需改） |

`deploy.ps1` / `package.ps1` / `dualtest.ps1` 都会自动点源 `env.ps1`，并把 `$GameDir` 导出为 MSBuild 环境变量，
供 `OpenNestCoop.csproj` 的 `$(GameDir)`/`$(BepInExDir)` 引用（interop 路径）。

> 若直接 `dotnet build`（不经脚本），csproj 会回退到默认本机路径；
> 其他机器请修改 csproj 默认值或设置 `GameDir` 环境变量。

### 构建 / 部署 / 打包

```powershell
# 双端快速构建 + 部署（开发迭代用，默认 BepInEx G + MLL D）
.\scripts\deploy.ps1
# 只部署 BepInEx 端（G 盘） / 只部署 MLL 端（D 盘）
.\scripts\deploy.ps1 -BepOnly
.\scripts\deploy.ps1 -MllOnly
# 或手动只构建 BepInEx 版并部署
cd src\OpenNestCoop
dotnet build -c Release -p:DeployToGame=true

# 双平台发布打包：构建 BepInEx + MelonLoader 两版，生成 release/ 下 4 个 zip
#   OpenNestCoop-<ver>-BepInEx-Mod.zip / -MelonLoader-Mod.zip
#   OpenNestCoop-<ver>-BepInEx-Standalone.zip / -MelonLoader-Standalone.zip
.\scripts\package.ps1
```

- `deploy.ps1`（默认双端）：
  - **BepInEx 版**（`src\OpenNestCoop`）→ G 盘 `$GamePluginsDir`（`游戏目录\BepInEx\plugins\`）；
  - **MelonLoader 版**（`src\OpenNestCoop.MelonMod`）→ D 盘 `$ClientGame`：`Mods\OpenNestCoop.MelonMod.dll` + `UserLibs\LiteNetLib.dll / SharpGLTF.Core.dll / SharpGLTF.Runtime.dll`；
  - 两端都同步 `model\player.bundle` 到游戏 `Models\`；`-BepOnly`/`-MllOnly` 只部署一端。
- `package.ps1`：双平台构建（`src\OpenNestCoop` + `src\OpenNestCoop.MelonMod`）→ staging → 4 个 zip；版本号默认 0.1.9（`-Version` 可覆盖，注意与 5 处版本号同步）。
- ⚠️ MLL 版构建依赖 G 端 `MLLoader`（MelonMod.csproj 的 `MLBase`/`MLCore` 引用 interop）；覆盖 DLL 时需先关闭游戏，否则文件被占用。
- ⚠️ 双平台 `#if MELONLOADER` 坑：MLL 端需 `using Localisation = Il2CppLocalisation;` / `using SleepyNodes = Il2CppSleepyNodes;` 等 interop 别名（`HarmonyPatches.cs` 曾漏 → CS0246）。

---

## 测试步骤

### 本地双端测试（推荐，无需两台电脑/两个 Steam 账号）

```powershell
# 本地回环双开（同一台机器两个游戏安装，不经 Steam；默认模拟 170ms 延迟 + 50ms 抖动）
.\scripts\dualtest.ps1 -Local
# 关闭延迟模拟：加 -Lag 0；覆盖延迟/抖动：-Lag 100 -LagJitter 30
# 只起一端：-HostOnly / -ClientOnly；指定同步方案：-Sync new（两端都传 --sync new）
```

- 主机 G 端日志 `BepInEx\LogOutput.log`、客机 D 端日志 `MelonLoader\Latest.log`（直接读这两个，勿依赖 runlog 拷贝）。
- 本地回环模式下 Steam P2P 被绕过（TCP loopback），可单 Steam 会话双开。

### Steam 双账号测试（跨机 / 跨账号）

> 所有参与者都必须安装本 mod，并**通过 Steam 启动游戏**（否则 Steam API 不可用）。

1. **主机**：Steam 启动游戏 → 左上角「联机菜单」→ 设置房间名/人数 → 创建房间。
2. **客户端**：另一台电脑/另一个 Steam 账号 → 打开菜单 → 刷新大厅列表 → 加入。
3. 双方互相出现在成员列表；聊天互发；延迟(ms)显示。
4. 主机关闭/离开后，客户端应提示主机已离开并回到大厅。
5. 好友邀请：Steam 好友列表右键 → 邀请加入游戏。

---

## 扩展点总览（详见 docs/API.md）

| 扩展点 | 用途 |
|--------|------|
| `CoopSyncRegistry.RegisterFloat/Int/Bool` | 设备数值状态同步 |
| `CoopSyncRegistry.RegisterModule(ISyncedModule)` | 自定义组件/事件同步 |
| `PlayerVisualRegistry.Register(IPlayerVisualProvider)` | 角色模型/骨架/动画 |

---

## 玩家化身（远端玩家 3D 模型 / 动作）

游戏没有玩家模型（只有 FirstPersonController），远端玩家可视化由 `PlayerSync` + `IPlayerVisualProvider` 提供（V1/V2 都复用 `PlayerVisualRegistry`）。
提供者按优先级选择（`PlayerSync.ResolveProvider`）：

1. **注册的自定义 provider**（`PlayerVisualRegistry.Register`，其他模组注入，位于 `OpenNestCore.Avatar`）。
2. **AnimatorAvatarVisualProvider（方案 A，推荐）**：加载 `player.bundle`（AssetBundle），用 **Unity Animator
   原生引擎**驱动真骨骼动画。bundle 由 `tools/playerbundle/` 离线工程用与游戏相同的 Unity **6000.3.21f1** 打包
   （模型 Rig=Humanoid + Mixamo 动画 + AnimatorController）。运行时只调游戏已含的
   `UnityEngine.AnimationModule` / `AssetBundleModule`，IL2CPP 安全。查找位置：
   `ONC_BUNDLE` env → `<游戏>/Models/player.bundle` → `<游戏>/player.bundle` → 插件目录。
3. **ExternalModelProvider（方案 B）**：SharpGLTF 手搓 SkinnedMeshRenderer + 自采样动画（Soldier.glb = bundle 模型源）。
4. **CatCrewVisualProvider**：克隆游戏猫船员（Unity 真 Animator 动画）——外部模型/猫船员二选一，
   由环境变量 `ONC_PROVIDER` 选择（`soldier` / `cat` / `humanoid`，默认士兵优先、失败回退猫）。
5. **HumanoidVisualProvider（兜底）**：程序化胶囊人 + 程序化走路/待机动画。

调试开关（`Models/oncmodel.txt` 或环境变量 `ONC_MODEL`，优先级：配置 > 环境变量）：`1` 强制模型，`0` 强制骨架；`--local` 本地测试默认骨架。

