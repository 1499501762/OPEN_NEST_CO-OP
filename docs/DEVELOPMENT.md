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
| 补丁 | Harmony | 游戏方法挂钩（HandleInput/FireShell/RequestFire） |

> 说明：IL2CPP 游戏无法注入 Mirror/Netcode 等需编译进工程的网络库；
> Steamworks 已是游戏的一部分，是最优"轮子"。

---

## 目录结构

```
src/OpenNestCoop/            BepInEx 插件源码
  Plugin.cs                  插件入口（BasePlugin）
  Core/                      CoopBehaviour(Update)、NetConfig
  Net/                       NetManager(状态机)、SteamLobby、SteamTransport、NetProtocol
  GameSync/                  TurretSync / PlayerSync / RecordPlayerSync / ReloadSync /
                             MapSync / ControlSync / ValueSync / CoopSyncRegistry /
                             CoffeeSync / IPlayerVisualProvider / DefaultPlayerVisualProvider
  Patches/                   HarmonyPatches（炮塔输入/开火/装填挂钩）
  UI/                        CoopUIManager（UGUI 联机菜单）、CoopLoc（本地化）
tools/AsmDump/               程序集侦察工具（游戏类型 / Steam API 结构）
scripts/                     deploy.ps1（构建+部署）、env.ps1（本机环境）、env.example.ps1（模板）
docs/                        API.md（扩展 API 文档）、本文件
```

> 构建产物与本地私有文件**不随仓库分发**（见 `.gitignore`）。

---

## 构建与部署

### 环境变量（全局变量表）

所有开发环境参数集中在 `scripts/env.ps1`（由 `env.example.ps1` 复制并填写本机路径）：

| 变量 | 含义 |
|------|------|
| `$GameDir` | 游戏安装目录 |
| `$SteamAppId` | 2950790 |
| `$BuildConfig` | Release / Debug |
| `$PluginName` | 部署目录名（OpenNestCoop） |
| `$BepInExDir` / `$GamePluginsDir` | 派生路径（一般无需改） |

`deploy.ps1` 会自动点源 `env.ps1`，并把 `$GameDir` 导出为 MSBuild 环境变量，
供 `OpenNestCoop.csproj` 的 `$(GameDir)`/`$(BepInExDir)` 引用（interop 路径）。

> 若直接 `dotnet build`（不经 deploy.ps1），csproj 会回退到默认本机路径；
> 其他机器请修改 csproj 默认值或设置 `GameDir` 环境变量。

### 构建

```powershell
# 一键构建 + 复制到游戏 BepInEx\plugins\OpenNestCoop\
.\scripts\deploy.ps1
# 或
cd src\OpenNestCoop
dotnet build -c Release -p:DeployToGame=true
```

产物目录：`$GamePluginsDir`（默认 `游戏目录\BepInEx\plugins\OpenNestCoop\`）

> 注意：覆盖 DLL 时需先关闭游戏，否则文件被占用。

---

## 测试步骤

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
