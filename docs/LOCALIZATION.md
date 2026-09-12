# 语言键 / 本地化（Localization）

> **目的**（用户 2026-09-12 要求）：**硬编码文本改为语言键**；**语言键保存在外部语言键文件中，运行时读取** ——
> 改文案只改文件（2 秒热重载，不用重新编译），也能自己加语言段。
>
> **信息可信度 / 来源**：实现 = `src/OpenNestCoop/Core/Loc/LocFile.cs`（读取/生成/热重载）、
> `Core/Loc/LocDefaults.cs`（内置默认表）、`UI/CoopLoc.cs`（属性 → 键）、
> 调用方：`UI/CoopUIManager.cs`、`UI/MainMenuEntry.cs`、`Debug/InteractableNameTool.cs`、`Net/NetManager.cs`；
> 接线：`Core/CoopRuntime.Startup`（Init + 语言检测）、`Core/CoopBehaviour.Update`（热重载）。
>
> **更新记录**：
> - 2026-09-12 建档：文件位置/格式/键清单/语言检测/热重载/加键加语言/错误键约定/未纳入范围/实测记录。

---

## 一、文件位置与格式

| 宿主 | 路径 |
|---|---|
| BepInEx | `<游戏目录>/BepInEx/config/OpenNestCoop.lang.ini` |
| MelonLoader | `<游戏目录>/UserData/OpenNestCoop.lang.ini` |
| 兜底 | `<persistentDataPath>/OpenNestCoop.lang.ini` |

与配置文件 `OpenNestCoop.cfg` **同目录**（路径直接取自 `CoopConfig.FilePath` 的目录）。

```ini
## Open Nest Co-op v0.2.1-Alpha-2 language file / 语言键文件
## 本文件是界面文案的**来源**：改这里即可改文案（约 2 秒热重载，不用重启）。
## 格式：[语言代码] 段 + `键 = 文本`；# 或 ; 开头是注释；{0} {1} 是运行期占位符。
## 新增语言：自己加一段（如 [ja]），缺键的项会自动回退到 [en] → 内置默认值。
## 新增键：升级模组后会自动补进本文件（保留你已改过的文本）。文档：docs/LOCALIZATION.md

[zh]
MenuToggle = 联机菜单
Title = Open Nest 联机
LanCreate = 创建局域网房间
ErrLanPortInUse = 端口 {0} 被占用（可在配置文件 [LAN] Port 改一个）
...

[en]
MenuToggle = Coop Menu
Title = Open Nest Co-op
LanCreate = Host LAN Room
ErrLanPortInUse = Port {0} in use - change [LAN] Port in OpenNestCoop.cfg
...
```

**规则**
- 段名 = 语言代码（`zh` / `en`，大小写不敏感；可自行加 `[ja]`、`[ru]`…）。
- `键 = 文本`；`#`/`;` 开头是注释；空行忽略；`{0}`/`{1}` 是运行期占位符（`string.Format` 填充）。
- 文件**缺失/缺键** → 用内置表（`LocDefaults`）**生成/补齐**（首启自动写出；升级后新增键自动补进文件，**保留你已改过的文本**）。
- 取文案顺序：`当前语言段` → `[en]` 段 → 内置默认 → 键名（最后这层只是兜底，正常不会看到键名）。
- 文件带 UTF-8 BOM（记事本打开中文不乱码）；**永不抛异常**（语言文件损坏不影响游戏）。

## 二、语言检测

跟随游戏语言（`CoopLoc.Detect()`，顺序）：

1. 原生 UI 桥接 `NativeUi.CurrentLanguage`（经 `IronNestNativeUi` 读游戏 `LocalisationManager`）
2. 兜底直读游戏 `LocalisationManager.Instance.CurrentLanguage`
3. 再兜底 `Application.systemLanguage`（中文 → `zh`，其它 → `en`）

→ `CoopLoc.Current`（`Lang.Zh`/`Lang.En`）→ `LocFile.SetLanguage("zh"/"en")`。
`CoopUIManager.Update` 每秒刷新一次（语言切换即时生效）。日志：`[LocFile] language = en`。

## 三、键清单（按分组）

> 完整文案（中文/English 对照）见语言文件本身，或内置表 `Core/Loc/LocDefaults.cs`。键名 = `CoopLoc` 的属性名。

| 分组 | 键 |
|---|---|
| 联机菜单 | `MenuToggle` `Title` `DefaultRoomName` `State` `SteamReady` `SteamInit` |
| 建房 / 大厅 | `RoomNameLabel` `RoomNamePlaceholder` `RoomPassword` `RoomPasswordPlaceholder` `PasswordRequired` `PasswordError` `Confirm` `Cancel` `VersionMismatch` `RejoinLast` `RoomPwd` `PwdHas` `PwdNone` `MaxPlayers` `CreateLobby` `RefreshLobbies` `Refreshing` `NoLobbies` `Join` `Full` `InviteHint` `Locked` `OldVersion` |
| 房间内 / 聊天 | `Room` `Leave` `Members` `MyRole` `HostTag` `YouTag` `Chat` `ChatPlaceholder` `Send` `NoChat` `Kick` `Invite` `KickedHint` `ChatHint` `ChatEnterHint` |
| 局域网 | `TabSteam` `TabLan` `LanCreate` `LanScan` `LanScanning` `LanNoRooms` `LanIpLabel` `IpPlaceholder` `LanPortLabel` `LanMyIp` `LanHint` `LanIdentity` `LanName` `LanNamePlaceholder` `Copy` `Paste` `Copied` `LanJoining` `LanMismatch` |
| 状态 / 角色 | `StatusIdle` `StatusHosting` `StatusJoined` `RoleNone` `RoleCommander` `RoleGunner` `RoleLoader` `RoleFireControl` |
| F10 交互工具 | `ToolHeader` `ToolName` `ToolPath` `ToolComponents` `ToolHit` `ToolError` `ToolNoCamera` `ToolNoHit` `ToolNoCollider` `ToolSyncNA` `ToolSyncOn` `ToolSyncOff` `ToolSyncUnknown` |
| 错误 / 拒绝 | `ErrSteamNotReady` `ErrPasswordProtected` `ErrInviteUnsupportedLocal` `ErrInviteUnsupportedLan` `ErrLanPortInUse` `ErrLanHostFailed` `ErrLanNoIp` `ErrLanUnreachable` `ErrLanJoinFailed` `ErrHostLeft` `ErrHostDisconnected` `ErrHeaderWidthMismatch` `ErrModVersionMismatch` `RejectBanned` `RejectSchemeMismatch` `RejectOutdated` `RejectWrongPassword` `RejectDuplicateIdentity` `RejectRegistrationMismatch` |

## 四、错误 / 踢出原因的“语言键”约定（协议小扩展）

主机拒绝加入或踢人时，原因**以键的形式发给对端**，由**对端按自己的语言**显示（否则英文客户端会看到中文原因）：

```
Kick 载荷 = "@Key"            例：@RejectWrongPassword
           "@Key|arg1|arg2"   例：@ErrModVersionMismatch|0.2.1-Alpha-1|0.2.0
```

- 接收端（`NetManager.OnKicked` → `LocalizeReason`）：`@` 开头 → 拆键/参数 → 取本端语言文案；
  不是 `@` 开头（旧端发的普通文本）→ 原样显示（兼容）。
- 例：`@RejectWrongPassword` 在中文端显示“房间密码错误”、英文端显示“Wrong room password”。

## 五、加键 / 加语言的步骤

**加一个语言键**（3 步）：
1. `Core/Loc/LocDefaults.cs` 的内置表加一行 `("MyKey", "中文", "English"),`
2. 若要在 UI 里用：`UI/CoopLoc.cs` 加属性 `public static string MyKey => LocFile.Get("MyKey");`
3. 用法处调 `CoopLoc.MyKey`（或 `LocFile.Get("MyKey")`；带参数用 `LocFile.Get("MyKey", a, b)`）
   → 首启/升级后该键自动出现在语言文件里，玩家可直接改文案。

**加一种语言**：语言文件里自己加一段（如 `[ja]`），把需要的键填上；
缺的键自动回退 `[en]` → 内置默认。**不需要改代码**。

## 六、热重载

- `CoopBehaviour.Update` → `LocFile.Tick(dt)`：每 **2 秒**比对文件 mtime，变了就重读并回写（补齐新键）。
- 日志：`[LocFile] reloaded (<路径>)`。
- ⚠️ 回写会**保留**你在文件里改过的文本（用文件里的值优先），只补内置表里新增/缺失的键。

## 七、未纳入本地化的文本（有意保留）

| 范围 | 原因 |
|---|---|
| F9 诊断覆盖层（`Debug/MissionGraphViewUI` `MissionNodeDiagUI` `NetworkGovernorDebugUI` `FrameDiagUI`） | 开发者自用调试面板（中文为主、信息密度高），非玩家界面；如需本地化可后续按同一机制加键 |
| `UI/CoopMenuUI.cs`（IMGUI，M1 原型） | **无调用点**（已被 `CoopUIManager` 取代），属遗留代码 |
| 日志（`CoopLog`/`LogSource` 的 `[XSync] …` 行） | 面向开发者/排障，不是 UI 文案 |
| 配置文件说明（`CoopConfig.Defs` 的 `Desc`） | 生成在**配置文件中**（本身就是外部文本，玩家可直接改） |
| 游戏侧原生文案 | 由游戏本地化系统负责 |

## 八、实测记录（2026-09-12）

| 检查 | 结果 |
|---|---|
| 首启生成 | 启动游戏 3 秒后出现 `BepInEx/config/OpenNestCoop.lang.ini`（216 行；`[zh]` 段 206 键，`[en]` 段同） |
| 语言检测 | 主机系统为英文 → 日志 `[LocFile] language = en` → UI 取 `[en]` 段 |
| 日志 | `[LocFile] created (defaults) <路径>` |
| **热重载** | 手改 `Title` 后 **4 秒内**日志出现 `[LocFile] reloaded (<路径>)` ✓ |
