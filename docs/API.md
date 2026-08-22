# Open Nest Co-op Mod — 同步/扩展 API 文档

本文档面向想扩展本联机 mod 的开发者（其他模组、脚本、自定义内容作者）。

框架命名空间：`OpenNestCoop.GameSync`（同步）与 `OpenNestCoop.Net`（网络）。
所有注册 API 都是**开放扩展点**：别的模组在加载时调用即可接入，无需改动本 mod 源码。

> **License**：本 mod（含本文档与全部源码）以 **GNU Affero General Public License v3.0（AGPLv3）** 授权，
> 见仓库根目录 [LICENSE](../LICENSE)。任何基于本代码的修改/再分发必须保持 AGPLv3 并公开源码；
> 在网络上提供修改版服务时（如专用服务器/中继），须向用户提供对应源码（AGPLv3 §13）。
> 扩展方仅**调用**上述注册 API（不复制本代码）时，扩展自身可选用其他协议；但修改/派生本代码须遵循 AGPLv3。

---

## 1. 扩展点总览

| 扩展点 | 用途 | 入口 |
|---|---|---|
| `CoopSyncRegistry.RegisterFloat/Int/Bool` | 同步设备/组件的**数值状态**（表盘、压力、药包、罐子等） | `CoopSyncRegistry` |
| `CoopSyncRegistry.RegisterModule(ISyncedModule)` | 同步任意**自定义组件/事件**（需要自己的消息类型） | `CoopSyncRegistry` |
| `PlayerVisualRegistry.Register(IPlayerVisualProvider)` | 替换/填充**玩家角色模型、骨架、动画** | `PlayerVisualRegistry` |

网络传输、消息封装（`NetProtocol`）、Steam P2P（`SteamTransport`）由本 mod 提供，扩展方只需提供数据读写。
运行时访问统一走平台无关核心 `CoopRuntime.Net`（`OpenNestCoop.Core`）——`Plugin` 只是 BepInEx 入口壳，**不再有 `Plugin.Net`**。

---

## 2. 设备数值状态同步（CoopSyncRegistry / ValueSync）

适合：刻度盘、旋钮、曲柄、滑块、表盘、压力、阀门、计时、药包数量、加载状态等**单个数值**。

```csharp
using OpenNestCoop.GameSync;

// 例：同步一个"咖啡机冲煮进度"(0~1 float)
CoopSyncRegistry.RegisterFloat(
    "coffee/brewProgress",            // id：跨端唯一（建议用场景路径）
    () => brewer.BrewElapsedSeconds / brewer.idealBrewSeconds, // 本地读值
    v => ApplyBrewProgress(v),        // 远端写值
    deadzone: 0.01f,                  // 变化死区（小于此不发送）
    interp: true,                     // 客户端插值平滑
    busy: () => brewer.isDragging);   // 可选：本地操作中跳过远端覆盖

// 同步一个整数（装填药包数）
CoopSyncRegistry.RegisterInt("gun/0/charges",
    () => powder.currentSelectedCharges,
    v => powder.currentSelectedCharges = v);

// 同步一个布尔（播放/停止）
CoopSyncRegistry.RegisterBool("radio/playing",
    () => player._isPlaying,
    v => ApplyPlaying(v));
```

### 参数说明

| 参数 | 类型 | 含义 |
|---|---|---|
| `id` | `string` | 跨端唯一标识。同一组件在两端必须有**相同 id**（推荐场景路径 `父/子`）。 |
| `get` | `Func<T>` | 从本地游戏对象读取当前值。 |
| `set` | `Action<T>` | 把远端值写回本地游戏对象。 |
| `deadzone` | `float` | 变化死区：变化小于此值不发送（减少流量）。float 默认 `0.001`，int 默认 `1`。 |
| `interp` | `bool` | 客户端是否插值（仅 float 有效）。连续量建议 `true`。 |
| `busy` | `Func<bool>` | 可选。返回 true 时放弃远端覆盖（本地正在操作，本地优先）。 |

> 注：`RegisterBool` 无 `deadzone`/`interp` 参数（布尔无死区/插值）；仅 `RegisterFloat/Int` 有。

### 机制

- **主机权威**：主机按周期轮询注册值，变化时广播 `ControlState`（`kind+id+value`）。低频 **0.2s**（多数控件变化检测）+ **心跳 2s 全量广播**（初始对齐/状态自愈，`ValueSync.HeartbeatInterval`）；炮塔 Lever/Gear 等 `HighFreq` 绑定走 **30Hz 高频**（`ValueSync.HighFreqInterval`）。
- **客户端**：本地值变化上行 `ControlCmd` → 主机应用后转发；远端拖拽中插值平滑追，释放瞬间精确 settle。
- 内建防环 + 插值 + **释放保护窗口**（0.35s，防松开瞬间被对端旧广播值回跳），无需扩展方处理。

> 内建 `ControlSync`（曲柄/旋钮/滑块）就是用它实现的，可作参考。

---

## 3. 自定义同步模块（ISyncedModule）

适合：无法用"单个数值"表达的对象（物品生命周期、事件、复杂状态机）。

```csharp
public sealed class MyCoffeeSync : ISyncedModule
{
    public byte MsgType => 100; // 用 100+ 避开内建消息类型

    public void Tick(float dt)
    {
        // 每帧/周期：检测本地变化 → 用 NetProtocol 发包
        // 例：主机广播 or 客户端上行
    }

    public void OnPacket(ulong from, byte[] data)
    {
        // 收包处理（从 NetDataReader 读数据）
    }

    public void OnSessionStarted() { /* 进入房间 */ }
    public void OnSessionEnded()   { /* 离开房间 */ }
    public void Reset()            { /* 重置状态 */ }

    public void OnLateJoin(ulong steamId)
    {
        // 可选：中途加入快照对齐——主机在成员加入/重连时调用本方法，
        // 把本模块当前状态单播给该 steamId（默认空实现；仅需初始对齐的模块重写，如任务/装填）。
    }
}

// 加载时注册：
CoopSyncRegistry.RegisterModule(new MyCoffeeSync());
```

> **参考实现**：本 mod 内置 `CoffeeSync`（`GameSync/CoffeeSync.cs`，MsgType=100）即用此方式同步
> `EspressoBrewingController.BrewState`（咖啡机冲煮状态机），可作为自定义模块的完整范例。

> **其他辅助 API**：`CoopSyncRegistry.Modules`（只读已注册列表）、`FindModule<T>()`（按类型找模块，快照应用用）、
> `RegisterModule(module, params byte[] extraTypes)`（一个模块处理多个 MsgType，如 `CatSync` 106+133、`ButtonClickSync` 118+135、`PunchcardSync` 136+137+138）。

### 发包示例（复用框架消息封装 + Steam P2P）

```csharp
using OpenNestCoop.Core;   // CoopRuntime
using OpenNestCoop.Net;    // NetProtocol / MsgType

// 上行给主机（单播）：
var w = NetProtocol.Begin((MsgType)MsgType);
w.Put(value);
CoopRuntime.Net.Transport.Send(CoopRuntime.Net.HostSteamId, NetProtocol.Snapshot(w), reliable: true);

// 主机广播给全员 / 转发：
var data = NetProtocol.Snapshot(w);
CoopRuntime.Net.EnqueueBatch(data, toAll: true, reliable: true);
```

`NetProtocol.Begin(type)` 写入类型头；`NetProtocol.Snapshot(w)` 取出完整字节数组；
`CoopRuntime.Net.Transport.Send(steamId, data, reliable)` 单播；`CoopRuntime.Net.EnqueueBatch(data, toAll, reliable)` 合包广播（`toAll=false` = 发给主机/上行）。

---

## 4. 角色模型 / 骨架 / 动画（IPlayerVisualProvider）

> 类型位于 `OpenNestCore.Avatar` 命名空间（`IPlayerVisualProvider` / `PlayerVisualRegistry` / `AvatarPose` / `PlayerAction` / `CrewRole`）。

游戏是第一人称、无玩家模型，因此远端队友默认显示为「头球 + 防毒面罩 + 名字」。其他模组可注入自定义模型。

```csharp
public sealed class MySoldierVisual : IPlayerVisualProvider
{
    public GameObject Create(Transform root, string playerName, Color tint)
    {
        // 实例化你的模型 prefab / FBX，挂 Animator / 骨架
        // var go = Object.Instantiate(myPrefab, root);
        // go.GetComponent<Animator>().applyRootMotion = false;
        // return go;   // 返回视觉根（Update/Destroy 用）
    }

    public void Update(GameObject visual, float dt, ref AvatarPose pose)
    {
        // pose.Position / pose.Yaw / pose.Speed / pose.Moving / pose.Role
        // 例：var anim = visual.GetComponent<Animator>();
        //     anim.SetFloat("Speed", pose.Speed);
        //     anim.SetBool("Moving", pose.Moving);
        //     visual.transform.rotation = Quaternion.Euler(0, pose.Yaw, 0);
    }

    public void Destroy(GameObject visual) { /* 清理（根由 PlayerSync 统一销毁） */ }
}

// 加载时注册（覆盖默认）：
PlayerVisualRegistry.Register(new MySoldierVisual());
```

### AvatarPose 字段

| 字段 | 类型 | 含义 |
|---|---|---|
| `Position` | `Vector3` | 世界位置（已插值） |
| `Yaw` | `float` | 朝向（度） |
| `Speed` | `float` | 估算移动速度（米/秒） |
| `Moving` | `bool` | 是否在移动（Speed > 0.05） |
| `Role` | `CrewRole` | 玩家角色分工（Commander/Gunner/Loader/FireControl） |
| `Action` | `PlayerAction` | 当前动作（Idle/Moving/Reloading/LoadingShell/AdjustingElevation/OperatingDevice/Custom） |
| `DeviceId` | `int` | 正在操作的设备/炮（0 = 无） |
| `MoveFwd` | `float` | 本地空间前进速度分量（米/秒，正=向前，供横移姿态） |
| `MoveStrafe` | `float` | 本地空间横向速度分量（米/秒，正=向右，供横移姿态） |
| `Airborne` | `bool` | 空中（跳跃/下落） |
| `Crouched` | `bool` | 蹲下 |
| `Sprinting` | `bool` | 奔跑 |
| `Pitch` | `float` | 摄像机俯仰角（度，抬头为正）——驱动头部转向 |

---

## 5. 骨架 / 动作同步（架构说明）

**职责分层：呈现（`IPlayerVisualProvider`）与传输（`CoopSyncRegistry`/`ISyncedModule`）分离。**

- **传输层**只传"意图"（`AvatarPose`：`Speed`/`Moving`/`Action`/`DeviceId`，或自定义消息），带宽极小。
- **呈现层**（provider）把意图映射到本地模型/Animator/骨架——两端有相同动画资源时可预测播放。

三种粒度（按需叠加，**无需推翻现有 API**）：

### 5.1 动作驱动（推荐，两端同动画资源）

provider 读 `pose.Action`/`pose.Speed`/`pose.DeviceId` 播对应动画：

```csharp
var anim = visual.GetComponent<Animator>();
anim.SetBool("Moving", pose.Moving);
anim.SetInteger("Action", (int)pose.Action);
anim.SetFloat("Speed", pose.Speed);
```

### 5.2 Animator 参数同步（细粒度）

把 Animator 的 float/bool 参数注册为值绑定，远端直接应用：

```csharp
CoopSyncRegistry.RegisterFloat("anim/gun/0/recoil",
    () => anim.GetFloat("recoil"), v => anim.SetFloat("recoil", v));
```

### 5.3 精确骨骼变换（带宽大，仅特殊场景）

用 `ISyncedModule` 按关节注册变换（`localPosition` 3 + `localRotation` 四元数 4），远端应用到骨架：

```csharp
// 发送端：每关节 7 个 float（位置 + 四元数）
w.Put(pos.x); w.Put(pos.y); w.Put(pos.z);
w.Put(rot.x); w.Put(rot.y); w.Put(rot.z); w.Put(rot.w);
// 应用端：joint.localPosition / joint.localRotation = 值
```

> 建议只同步少数关键骨骼 + 插值，避免全骨架带宽风暴。

### 5.4 现状说明

`AvatarPose.Action` 目前由 `PlayerSync` 推断（`Speed > 0.05` → `Moving`，否则 `Idle`）。
移动方向/姿态已随协议同步：`PlayerPos`（16）携带 `moveFwd/moveStrafe` 本地速度分量 + 真实速度（unreliable + 2s 心跳帧）；
`PlayerState`（33）reliable 携带 空中/蹲下/冲刺/俯仰（变化才发）。如需同步自定义动作（装填/操作设备），
可用独立 `ISyncedModule` 发送动作状态，或参考 `PlayerState` 扩展标记位。

---

## 6. 内建消息协议（MsgType）

| 值 | 名称 | 方向 | 内容 |
|---|---|---|---|
| 1 | `Hello` | 客户端→主机 | 昵称 |
| 2 | `Welcome` | 主机→客户端 | 玩家序号 + 名单 |
| 3 | `Roster` | 主机→全员 | 名单（含角色） |
| 4/5 | `Ping/Pong` | 双向 | 心跳延迟 |
| 6 | `Chat` | 双向 | 聊天 |
| 10 | `TurretState` | 主机→全员 | 炮塔旋转 + 各炮俯仰 |
| 11 | `GunFire` | 主机→全员 | 开火事件（gunIndex） |
| 12 | `TurretInput` | 瞄准手→主机 | 期望旋转/俯仰 |
| 13 | `Impact` | 主机→全员 | 炮弹落点 |
| 14 | `MissionState` | 主机→全员 | 任务/目标状态 |
| 15 | `CounterBattery` | 主机→全员 | 反炮兵事件（落点；seed 走 100+ 模块） |
| 16 | `PlayerPos` | 客户端→主机→其他 | 玩家位置/朝向/速度分量（unreliable + 2s 心跳帧） |
| 17/18 | `RecordState/Cmd` | 唱片机 | 播放状态/曲目/音量 |
| 19/20/21 | `ReloadState/Cmd/FireRequest` | 装填/开火 | 装填状态机/药包/开火请求 |
| 22-24 | `MapMarkerAdd/Remove/ClearAll` | 地图标记 | 放置/移除/清空 |
| 25/26 | `ControlState/Cmd` | 通用值状态 | kind+id+value（ValueSync 用） |
| 27 | `MapMarkerUpdate` | 地图拖拽 | id+origin+tip |
| 28 | `ReloadAdvance` | 任意端→全员 | 装填推进/回退事件（gunIndex+dir） |
| 29 | `PowderEvent` | 任意端→全员 | 发射药事件（gunIndex+ev+chargeIndex） |
| 30 | `StateSnapshot` | 主机→新成员 | 中途加入全量状态容器（方案 B） |
| 31 | `SnapshotRequest` | 客机→主机 | 任务场景加载后请求补发快照 |
| 32 | `Kick` | 主机→成员 | 踢出/封禁（reliable） |
| 33 | `PlayerState` | 客户端→主机→其他 | 空中/蹲下/冲刺/俯仰（reliable，变化才发） |
| 120 | `Batch` | 任意 | 外层容器：多个不可靠状态子包合并 |

**100+ 内置 `ISyncedModule` 模块（V1 `--sync old` 注册）：**

| 值 | 模块 | 内容 |
|---|---|---|
| 100 | `CoffeeSync` | 咖啡机冲煮状态（参考实现示例） |
| 102 | `MissionSync` | 任务状态 |
| 103 | `CounterBatterySync` | 反炮兵落点 seed |
| 104 | `EntitySync` | 任务实体（炮兵/药包等） |
| 105 | `ReconPhotoSync` | 侦察照片 seed |
| 106 | `CatSync` | 猫 AI 状态（附加类型 133） |
| 107 | `MapMarkerSync` | 地图标记 |
| 108 | `RecordItemSync` | 唱片物品 |
| 109 | `ShellSync` | 弹舱弹种 |
| 110 | `SequenceSync` | 发射台开关序列 |
| 117 | `HatchSync` | 舱门/楼梯盖板 |
| 118 | `ButtonClickSync` | 按钮点击（附加 135 toggle 状态） |
| 119 | `MapTokenSync` | 战术令牌 |
| 121 | `GunLinkSync` | 仰角联动/锁定插销 |
| 130 | `MissionEventSync` | 任务事件 |
| 131 | `NotificationSync` | 打字机通知（UINotificationManager.ShowNotification） |
| 132 | `PurchaseSync` | 补给购买 |
| 133 | `CatEvent` | 玩家-猫交互事件（CatSync 附加类型） |
| 134 | `TeleprinterSync` | 打字机打印（SubmitLines/ClearAll/ClearAlarm） |
| 136-138 | `PunchcardSync` | 征信点卡牌（136 状态 / 137 入槽事件 / 138 卡牌列表） |
| 140 | `ArmSync` | 预备激发火炮 |
| 141 | `CylinderActionSync` | 弹舱动作 |
| 142 | `ChargeInventorySync` | 装药库存 |
| 143 | `ChargeButtonSync` | 按钮 Dispencer active 掩码 |
| **100+** | 自定义 | 留给扩展方 `ISyncedModule` |

> **V2（`--sync new`）**：独立消息段 **200-229**（`V2Event=200`/`V2Value=201`/`V2Button=202`/`V2HostData=203`/`V2Control=204`/`V2Player=205`…`V2GunLink=229`）。
> 路由走 `CoopSyncRegistry.TryRoute`（各层实现 `ISyncedModule`），不注册上面这些 V1 模块。

---

## 7. 常见问题

- **id 用什么？** 两端同炮台场景结构一致，推荐用组件 `transform` 的场景路径（如 `炮塔/曲柄1`）。
- **消息类型冲突？** 内建基础协议占用 1-33 + `Batch=120`；内置模块用 100-143；自定义模块建议 **100+** 并避开已用值（见第 6 节）。V2（`--sync new`）用 200-229。
- **插值会不会和本地操作打架？** `busy` 返回 true（如正在拖拽）时自动放弃远端覆盖，本地优先。
- **角色没有动画素材？** 默认 `HumanoidVisualProvider`（程序化兜底）已内置；自定义实现里用 pose 驱动你自己的 Animator 即可。
- **License 是 AGPLv3，我调用 API 会被传染吗？** 仅**调用**注册 API（`CoopSyncRegistry`/`IPlayerVisualProvider`，不复制本代码）不构成派生作品，你的扩展可用任意协议。但**复制/修改**本代码并再分发时，整体须保持 AGPLv3 且公开源码。详见 [LICENSE](../LICENSE)。
