# Steam 联机大厅功能（Lobby：房间密码 + 模组版本核对）

> **目的**：记录 2026-08-23 完善后的 Steam 联机大厅能力——**房间密码**（防止随便进）与
> **模组版本号核对/标识**（防止跨版本混联导致异常），含协议、LobbyData、UI、测试方法。
>
> **信息可信度**：来自 `src/OpenNestCoop/Net/NetManager.cs`、`Net/SteamLobby.cs`、`Core/NetConfig.cs`、
> `UI/CoopUIManager.cs`、`UI/CoopMenuUI.cs` 当前实现。
>
> **关联文档**：`docs/ARCHITECTURE.md`（架构）、`docs/API.md`（网络协议）、`docs/DEVELOPMENT.md`（开发）。

**更新记录**

- 2026-08-23 修复大厅 UI 输入体验 4 项：① 光标 `|` 只显示在当前聚焦的输入框（房间名/密码不再同时显示）；② 房间名/密码框支持中文输入（新增隐藏 IME 锚点 TMP_InputField 唤起系统输入法，IME 组合中跳过物理键防冲突）；③ 点击输入框即清空占位提示（聚焦时空内容只显示光标）；④ 房间内版本/加载器标识移到独立一行右对齐，不再被邀请/离开按钮遮挡。
- 2026-08-23（二修）Emoji `🔒` 在游戏 TMP 字体中无字形显示为方框 → 换字体安全的 `[锁]`（房间列表）/去 emoji（弹窗标题、密码状态）；IME 输入加**双通道兜底**（`OnTextInput→OnImeText→Append` + `_imeAnchor.text` 增量轮询）+ 持续保焦（防 EventSystem 抢焦点），并加 `[UI] IME`/`[UI] poll imeAnchor` 诊断日志。
- 2026-08-23（三修）中文输入仍不可用 → 根因：`_imeAnchor` 简化版（无 placeholder/textViewport + 离屏 4×4）虽能唤起输入法，但 TMP_InputField 无法真正处理 IME 组合（提交字符既不进 `.text` 也不触发 `OnTextInput`）。改为与聊天 `_chatInput` **同构**（Image + 子对象 Text/Placeholder + textViewport，屏幕内全透明、隐藏光标）→ IME 组合正常，提交字符进 `.text` 由轮询增量同步。
- 2026-08-23（四修，接手 IME 组合工作）① 组合捕获用 `compositionString`（`compositionLength` 在本环境无效恒 0 → 拼音经 PollInput 泄漏成 `AppendKey` 重复）；② 组合判定改用 `(compositionString?.Length ?? 0) > 0`（Idle+聊天一致）；③ 密码掩码 `●`（U+25CF 字体内无字形 → 方框）换 `*`；④ 退格长按自动重复（`wasPressedThisFrame` 只触发一次，改 `isPressed` 手动计时 0.45s 起每 0.06s 删一次，Idle+聊天一致）；⑤ `SetIMECursorPosition` 把 IME 候选窗定位到当前聚焦输入框旁（点击时捕获 `BoxScreenPos` 屏幕坐标）。测试确认：拼音输入可接受。
- 2026-08-23（五修）点击 IME 候选词出现**两个方框 + 拼音前后重复一次** → 根因：候选词点击提交时 `OnTextInput` 收到被 IL2CPP 交互层破坏的 **U+FFFD（� 替换字符）**（日志 `OnTextInput CJK c='�' U+FFFD`）；追加 U+FFFD 后目标 `EndsWith(拼音)` 去重被破坏 → 拼音再追加一次。修复：`Append` 入口 + 组合捕获/文本轮询的追加路径统一 `SanitizeIme`（过滤 U+FFFD/空字符）→ 消除方框 + 恢复去重（拼音不再重复）。
- 2026-08-23（六修）① 拼音**首字母重复**（如 `zzhong'wen...`）：组合启动瞬间 `compositionString` 未更新，PollInput 先把首键英文追加 → 组合开始后捕获整段拼音。修复：组合刚启动（上帧空→本帧非空）时若目标末尾 == 组合首字母则移除泄漏字母。② **空格确认候选词误触发创建房间**：空格确认时 OS 发 `'\r'/' \n'` 文本输入，`OnImeText` 误当回车 `Submit()`。修复：`OnImeText` 不再处理 `'\r'/' \n'` 提交，真实回车由 `PollInput` 物理键 `enterKey.wasPressedThisFrame` 统一处理（建房/聊天/弹窗确认一致）。
- 2026-08-23（七修，真汉字输入）用户要求真汉字：Unity 输入管线取不到（`OnTextInput` CJK 被 IL2CPP 破坏成 U+FFFD、`_imeAnchor.text` 恒空）→ **P/Invoke 原生 Win32 IME**：`ImmGetCompositionStringW(GCS_RESULTSTR)` 直接读 OS 输入法最近提交的中文字（仅 Windows）。原生为主通道（`_lastNative` 去重、组合启动重置），`compositionString` 拼音降为兜底（原生未取到才追加）。诊断：`[UI] 原生中文提交 added='…'`。
- 2026-08-23（八修）① 拼音**首字母闪烁**（<1ms）：首键被 PollInput 追加一帧、下帧才被泄漏移除。修复：组合判定优先用**原生 `GCS_COMPSTR`**（比 Unity `compositionString` 更及时，首键按下当帧即判 → 首字母根本不追加）；`compositionString` 兜底。② **聊天框接入真汉字**：聊天路径同样加原生 `GCS_RESULTSTR` 捕获（主通道），`compositionString` 拼音兜底（`_nativeHandled` 时跳过），与 Idle 一致。
- 2026-08-23（九修）**通用输入框组件 `UI/CoopInputBox.cs`**：封装自制 Button+Text 视觉 + 值/密码掩码/占位/光标/聚焦/提交回调 + AppendText/BackspaceChar；输入/IME 逻辑统一路由到 `CoopInputBox.Active`（当前聚焦输入框），移除 `_inputPassword`/`_focusBoxScreenPos`/`MakeInputBox`/`ToggleTyping`。房间名(1)/密码(2)/弹窗密码(3) 复用；Rebuild 后 `RestoreActiveBox` 恢复聚焦。聊天仍用真实 TMP_InputField（机制不同未迁移）。
  ⚠️ **IL2CPP 坑**：mod 自定义类型用泛型 `AddComponent<T>()` 崩（`MethodInfoStoreGeneric_AddComponent` NRE）→ `CoopInputBox` 用**普通类**（只 AddComponent Unity 类型，按钮回调驱动），否则 UI 元素消失（Rebuild 异常）。
- 2026-08-23（十修）排查「输入框消失」：`CoopInputBox.Create` 加创建诊断日志（`[UI] InputBox kind=… created pos=(…) active=… parent=…`）→ 确认房间名/密码框均创建成功且 `active=True`、位置正确、记住的房间名仍在（`value='…'`）。根因：**非 bug**——当时游戏处于会话中（`RebuildChat show=True`，`BuildLobby` 状态无输入框），退出回大厅（Idle）即正常显示。保留创建诊断日志便于将来排查。
- 2026-09-13 **房主 SteamID 缓存**（“创建大厅后主机掉帧”修复其一，见 `KNOWN_ISSUES.md`）：`SteamLobby.HostSteamId` 原先是**每次读都调 `SteamMatchmaking.GetLobbyOwner`** 的属性，而它在所有同步模块的“发给房主”分支里被反复读（每个发送点至少 2 次）→ 与 Steam IPC 叠加占帧。改为缓存字段 `_hostSteamId`：**建房**（`OnLobbyCreated`）/ **进房**（`OnLobbyEntered`）/ **退房**（`LeaveLobby`）清 0；另在 **成员变更**（`OnLobbyChatUpdate`）中，若变更者正是缓存房主（离开/掉线/被踢）也清 0——Steam 会把 owner 转给其他成员，不清缓存会继续往已离开的人发包。下次读时重新问 Steam。

---

## 一、功能总览

> ℹ️ **另见 `docs/LAN.md`（局域网联机，2026-09-12 新增）**：不经 Steam 大厅的 TCP 直连 + UDP 广播发现，
> 身份规则“SteamID 优先，否则配置里的 FakeID”，菜单里是独立选项卡。本文档只讲 **Steam 大厅**这条路径。

| 能力 | 说明 | 实现位置 |
|---|---|---|
| **房间密码** | 创建房间可选密码；列表显示 🔒；加入有密码房间需输入；主机握手权威校验（错误 → Kick + 原因） | `SteamLobby` / `NetManager.OnHello` / 两套 UI |
| **版本号标识** | 房间 LobbyData 写模组版本；列表显示 `vX.Y.Z`（匹配=绿/不符=红）；房间内显示本端版本 | `SteamLobby.SetLobbyData(ver)` / UI |
| **加载器标识（仅显示）** | 房间 LobbyData 写宿主加载器（`BepInEx`/`MelonLoader`）；列表显示 `[BE]`/`[ML]`；房间内显示本端加载器。**不核对**（两端加载器可不同） | `SteamLobby.SetLobbyData(loader)` / UI |
| **主机名显示** | 列表每行显示房间创建者 Steam 名（`by <名>`） | `LobbyInfo.OwnerName` / UI |
| **记住房间设置** | 建房时把房间名/人数/密码持久化到本地文件，下次启动建房 UI 自动预填 | `Core/LobbySettings` / `NetManager.Init/CreateLobby` |
| **最近房间快速重连** | 加入/创建房间后记录 Steam lobby id（持久化）；大厅显示"重连上次房间"按钮一键重连 | `LobbySettings.RecentLobbyId` / `NetManager.JoinRecentLobby` |
| **房间密码状态显示** | 房间内显示"房间密码: 🔒有/无"（主机看 PendingPassword、成员看 LobbyData）；大厅列表已有 🔒 | UI |
| **版本号核对** | 握手 Hello/Welcome 带版本号，两端不一致 → 拒绝/离开并提示 | `NetManager.OnHello` / `OnWelcome` |
| **旧客户端拒绝** | Hello 握手协议号（`HandshakeVersion=2`）不匹配 → 拒绝（旧版无此字段） | `NetManager.OnHello` |
| **拒绝原因** | Kick 消息带原因字符串，被拒端显示具体原因（版本不符/密码错误/已封禁等）。⚠️ 2026-09-12 起原因以**语言键**下发（`@Key` / `@Key|arg1|arg2`），由**接收端按本端语言**显示；见 `docs/LOCALIZATION.md` §4 | `KickPlayer` / `OnKicked` |

---

## 二、协议扩展（MsgType）

### Hello（客户端 → 主机，`MsgType.Hello=1`）

```
[syncScheme:byte] [headerWidth:byte] [handshakeVer:byte=4] [version:string] [password:string] [name:string] [channelTable...]
```

| 字段 | 含义 |
|---|---|
| `syncScheme` | 同步方案：0=V1（默认）1=V2（--sync new） |
| `headerWidth` | **前导字节宽度沟通**（1/2，`NetProtocol.HeaderWidth`）：1 字节前导用尽自动扩展为 2 字节，双端必须一致，不符 → 拒绝/离开 |
| `handshakeVer` | 握手协议版本（`NetConfig.HandshakeVersion=4`）。旧客户端 Hello 无此字节 → 读到 ASCII 值 ≠ 4 → 拒绝 |
| `version` | 模组版本号（`NetConfig.Version`），与主机不符 → 拒绝 |
| `password` | 房间密码（明文，主机按 hash 校验；非加密，仅防随便进） |
| `name` | 玩家昵称 |
| `channelTable` | **注册通道表**（`NetManager.WriteChannelTable`）：`[byte 条目数]` 每项 `[string key][ushort type]`。key 前缀 `D:`=动态通道（模块自注册，前导字节由注册管理器分配）、`S:`=静态采用（硬编码 MsgType）。type 用 ushort（兼容 2 字节扩展 ≥256）。主机校验（`NetManager.VerifyHostChannelTable`）：客户端带主机不认识的通道 → 拒绝（build 不兼容） |

### Welcome（主机 → 客户端，`MsgType.Welcome=2`）

```
[syncScheme:byte] [headerWidth:byte] [handshakeVer:byte=4] [version:string] [playerId:byte] [roster...] [channelTable...]
```

主机版本号随 Welcome 下发，客户端核对不符 → 离开并提示。**前导字节宽度（`headerWidth`）**随 Welcome 沟通（主机权威，与本端一致）。**注册通道表（前导字节 2 下发）**：主机把**权威注册表**附加在 Welcome 尾部（`NetManager.WriteChannelTable`），客户端 `NetManager.ApplyHostChannelTable` 采纳——动态通道前导字节以主机为准（重映射），保证双端路由表一致（`NetManager.TryRoute` 按前导字节统一分发）。注册管理器逻辑已并入 `NetManager`（优先级随注册提供、缺省默认，`RegisterChannel(key, ChannelCallbacks, priority)` 支持函数式注册；`MsgType` 为 int，1 字节空间用尽由 `AllocateType()` 自动扩展为 2 字节前导）。

### Kick（主机 → 被拒/被踢端，`MsgType.Kick=32`）

```
[reason:string]
```

`reason` 为拒绝/踢出原因（`Wrong room password` / `Mod version mismatch: ...` / `Outdated mod version - please update` / `Banned` / `Kicked by host` 等）。被拒端 `OnKicked` 读原因 → 显示 `LastError` + 离开大厅。

> ⚠️ 旧客户端收新版 Kick 会忽略多余字节（旧 `OnKicked` 不读数据），安全。

---

## 三、LobbyData（Steam 大厅元数据）

| Key | 常量 | 值 | 用途 |
|---|---|---|---|
| `OpenNestCoop` | `LobbyTagKey` | `1` | 大厅发现标记（只列本 mod 房间，已有） |
| `name` | `LobbyNameKey` | 房间名 | 已有 |
| `max` | `LobbyMaxKey` | 人数上限 | 已有 |
| `ver` | `LobbyVersionKey` | `NetConfig.Version`（如 `0.1.9`） | 房间模组版本标识 + 列表显示 |
| `pwd` | `LobbyPasswordKey` | 密码 FNV-1a hash（hex） | 房间是否有密码 + 客户端预校验 + 主机权威校验 |
| `loader` | `LobbyLoaderKey` | `NetConfig.LoaderName`（`BepInEx`/`MelonLoader`） | 房间宿主加载器标识（仅显示，不核对） |

**列表排序**（`SteamLobby.PollPendingLobbyList`）：同版本房间（`Version == NetConfig.Version`）排前面，其余按名称排序。

密码 hash：`NetConfig.HashPassword(string)`（FNV-1a 64 → `x16`，纯 C# 跨 BepInEx/MelonLoader 一致）。
仅用于"防随便进"，**非安全加密**（LobbyData 对所有人可见）。

---

## 四、房间密码流程

```
创建：
  建房 UI 输入密码（可选） → net.PendingPassword
  → SteamLobby.CreateLobby(name, max, password) → OnLobbyCreated
    SetLobbyData("pwd", HashPassword(password))（空密码不设）→ PasswordHash 缓存

浏览列表：
  LobbyInfo.HasPassword = GetLobbyData("pwd") 非空 → UI 显示 🔒

加入：
  点"加入"：
    - 无密码 → net.PendingPassword="" → JoinLobby
    - 有密码 → 弹输入框 → 输密码 → **本地预校验**（`net.VerifyRoomPassword`：HashPassword(pwd) 比对 LobbyInfo.PasswordHash）
       不匹配 → 提示"密码错误"，不加入（避免先加入再被踢）；匹配 → net.PendingPassword=密码 → JoinLobby
  （NetManager.JoinLobby 检查：有密码房间必须已设 PendingPassword，否则拒绝并提示）

握手校验（主机权威，防绕过）：
  客户端 Hello 带 password
  主机 OnHello：roomHash = Lobby.PasswordHash（Steam）/ HashPassword(PendingPassword)（本地模式）
    HashPassword(password) != roomHash → RejectJoin("Wrong room password") → 发 Kick(reason)
```

**客户端预校验**（`NetManager.VerifyRoomPassword`）：用 `LobbyInfo.PasswordHash`（LobbyData "pwd"）本地比对，
错误密码不发起加入；主机仍做权威校验（防客户端伪造跳过预校验）。

---

## 五、版本核对流程

```
创建：OnLobbyCreated → SetLobbyData("ver", NetConfig.Version)

列表：LobbyInfo.Version = GetLobbyData("ver")
  - 空 → 显示"旧版?"（红）
  - == 本端版本 → 绿色 vX.Y.Z
  - != 本端版本 → 红色 vX.Y.Z

握手（主机侧 OnHello）：
  handshakeVer != HandshakeVersion → RejectJoin("Outdated mod version - please update")
  remoteVer != NetConfig.Version     → RejectJoin("Mod version mismatch: room=X you=Y")

握手（客户端侧 OnWelcome）：
  handshakeVer != 2 → LastError="Outdated..." + LeaveLobby
  hostVer != NetConfig.Version → LastError="Mod version mismatch: host=X you=Y" + LeaveLobby
```

---

## 六、UI 改动

### CoopUIManager（UGUI，游戏内菜单）

| 位置 | 内容 |
|---|---|
| 创建区 | 房间名/密码输入框：点击即聚焦，**光标 `|` 只显示在聚焦框**、聚焦空内容即清空占位提示；支持**中文输入**（隐藏 IME 锚点唤起系统输入法） |
| 房间列表 | 每行：`[锁]`（有密码）+ 加载器徽标 `[BE]`/`[ML]` + 版本标签（绿/红）+ 玩家数 + `by 主机名` |
| 加入有密码房间 | 弹窗：房间名 + 密码输入框 + 确定/取消；确定 → 设 PendingPassword → JoinLobby |
| 房间内 | 第一行：房间名（左）+ 邀请/离开（右）；**第二行单独一行**：本端 `vX.Y.Z` + 加载器名（右对齐，不被按钮遮挡） |

### CoopMenuUI（IMGUI，旧主菜单，M1 原型无调用点）

同步支持：创建密码输入框（✎ 切换输入目标）、列表 `[锁]`/版本标识、加入密码弹窗、确定/取消。

### 输入目标

`Append/Backspace/Submit` 在 Idle 状态按优先级路由：**密码弹窗**（`_joinPassword`）→ **密码框**（`_inputPassword`）→ **房间名**（`_roomName`）。`_roomPassword` 存创建密码（● 掩码显示）。

### 中文输入（IME）

**通用输入框组件**（`UI/CoopInputBox.cs`）：封装视觉 + 值/掩码/占位/光标/聚焦 + 提交回调；输入与 IME 逻辑统一路由到 `CoopInputBox.Active`。房间名/密码/弹窗密码复用（Kind 1/2/3）；聊天用真实 TMP_InputField（未迁移）。

房间名/密码框点击聚焦 → `CoopInputBox.Focus()` → `CoopUIManager.OnInputFocused(kind)` 激活隐藏 TMP_InputField（`_imeAnchor`，挂在常驻 `_root` Canvas 下，**与聊天 `_chatInput` 同构**：Image + 子对象 Text/Placeholder + textViewport，屏幕内全透明、隐藏光标）→ 唤起系统中文输入法。Rebuild 后 `RestoreActiveBox` 恢复聚焦。

**组合捕获**：原生 IME（`ImmGetCompositionStringW(GCS_RESULTSTR)`）读 OS 提交的真汉字（主通道，仅 Windows）；`compositionString` 拼音兜底（原生未取到时）。
**组合判定**：`(compositionString?.Length ?? 0) > 0` 判定 IME 组合中 → `PollInput` 跳过物理键（防拼音泄漏；`compositionLength` 本环境无效恒 0）。
**双通道兜底**：① `_imeAnchor.text` 轮询增量同步（`t.StartsWith(当前目标)` 只取追加部分）；② `Keyboard.OnTextInput`（Harmony `PostKeyTextInput`）→ `OnImeText` → `Append`（按 `_inputPassword` 分流）。
**退格**：长按自动重复（首次即删，按住 0.45s 后每 0.06s 删一次，Idle+聊天一致）。
**候选窗定位**：点击输入框捕获屏幕坐标（`BoxScreenPos`），`ActivateImeAnchor` 时 `SetIMECursorPosition` 把 IME 候选窗定位到输入框旁。
**密码掩码**：`*`（`●` U+25CF 字体内无字形显示为方框）。

---

## 七、实现文件

| 文件 | 改动 |
|---|---|
| `Core/NetConfig.cs` | `LobbyVersionKey`/`LobbyPasswordKey`/`LobbyLoaderKey`/`HandshakeVersion`/`HashPassword()`/`LoaderName` |
| `Core/LobbySettings.cs` | 记住房间设置 + `RecentLobbyId` 持久化 |
| `Net/SteamLobby.cs` | `LobbyInfo.Version/HasPassword/PasswordHash/Loader`；`CreateLobby(name,max,password)`；SetLobbyData ver/pwd/loader；`PasswordHash`；列表排序 |
| `Net/NetManager.cs` | `PendingPassword`；CreateLobby/JoinLobby 带密码；Hello/Welcome 扩展；OnHello 版本+密码校验；`RejectJoin`；`RoomPasswordHash`；`VerifyRoomPassword`；Kick 带原因；OnKicked 读原因；`JoinRecentLobby`/`LastLobbyId` |
| `UI/CoopUIManager.cs` | 创建密码框、列表标识、加入密码弹窗、房间内版本、输入目标路由 |
| `UI/CoopMenuUI.cs` | 同 UGUI 的能力（IMGUI 版） |
| `UI/CoopLoc.cs` | `RoomPassword`/`PasswordRequired`/`Confirm`/`Cancel`/`VersionMismatch` 等本地化 |

---

## 八、测试方法

1. **本地双端**：`.\scripts\dualtest.ps1 -Local`（不经 Steam，走 LocalTransport）——密码/版本握手逻辑同样生效（主机 `RoomPasswordHash` 用本地 PendingPassword）。
2. **版本核对**：一端改 `NetConfig.Version` 为不同值 → 握手应拒绝并提示"Mod version mismatch"。
3. **密码**：
   - 主机建房设密码 → 列表显示 🔒
   - 客机点加入 → 弹窗 → 错误密码 → 本地预校验提示"密码错误"（不加入）；正确密码 → 加入成功
   - 无密码房间直接加入
4. **旧客户端**：旧版本客户端 Hello（无 handshakeVer）→ 主机拒绝"Outdated mod version"。
5. **记住房间设置**：建房一次（设房间名/密码/人数）→ 重启游戏 → 建房 UI 应预填上次设置（文件 `Application.persistentDataPath/open_nest_lobby_settings.txt`）。
6. **最近房间重连**：加入某个房间后返回大厅 → 大厅显示"重连上次房间"按钮 → 点击重进（lobby id 持久化）。
7. **密码输入刷新**：Idle 大厅输入密码时，密码框 `●` 掩码应实时更新（`UiKey` 已含 `_roomPassword`/`_joinPassword`/`_inputPassword`/`_passwordDialog`，变化即重建）。
8. **光标单框**：点击房间名框 → 只有房间名框显示光标 `|`，密码框不显示；点密码框反之。
9. **占位清空**：聚焦房间名/密码框时，"点击此处输入"/"留空=无密码"立即消失（显示光标）；失焦后空框恢复占位。
10. **中文输入**：聚焦房间名框输入中文 → 进房间名；聚焦密码框输入中文 → 进密码（● 掩码）。IME 候选窗选字/回车确认不误触发创建。
11. **房间内不遮挡**：进入房间后，版本/加载器显示在独立一行（右对齐），邀请/离开按钮不遮挡。

---

## 九、已知限制

- 密码 hash 存 LobbyData，对所有人可见；仅"防随便进"，不做安全加密。
- 记住的房间密码以**明文**存本机文件（`open_nest_lobby_settings.txt`）——仅本机、非加密；介意可留空密码。
- 版本核对只在握手时做；运行中改版本不影响已建立的会话。
