# 开发文档（Development Guide）

本文件整理原 README 中的开发/技术细节，供开发者参考。

---

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
| `$ClientGame` | 第二个游戏安装（本地双开客户端 / MelonLoader 打包源，D 盘） |
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
# 单机 BepInEx 快速构建 + 复制到游戏 BepInEx\plugins\OpenNestCoop\（开发迭代用）
.\scripts\deploy.ps1
# 或
cd src\OpenNestCoop
dotnet build -c Release -p:DeployToGame=true

# 双平台发布打包：构建 BepInEx + MelonLoader 两版，生成 release/ 下 4 个 zip
#   OpenNestCoop-<ver>-BepInEx-Mod.zip / -MelonLoader-Mod.zip
#   OpenNestCoop-<ver>-BepInEx-Standalone.zip / -MelonLoader-Standalone.zip
.\scripts\package.ps1
```

- `deploy.ps1`：产物目录 `$GamePluginsDir`（默认 `游戏目录\BepInEx\plugins\OpenNestCoop\`），并同步 `model\player.bundle` 到游戏 `Models\`。
- `package.ps1`：双平台构建（`src\OpenNestCoop` + `src\OpenNestCoop.MelonMod`）→ staging → 4 个 zip；版本号默认 0.1.8（`-Version` 可覆盖，注意与 5 处版本号同步）。
- 覆盖 DLL 时需先关闭游戏，否则文件被占用。

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

