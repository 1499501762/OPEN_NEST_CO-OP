# Open Nest Co-op — 同步方案 V2（分层重构）开发文档

> 本文档是给**新同步模式（SyncV2）开发 AI** 的完整开发指南。
> 项目：Iron Nest: Heavy Turret Simulator 联机模组（Unity 6000.3.21f1 / IL2CPP / BepInEx+MelonLoader 双平台）。
> 目标：把现有"单机事件 + 交互按钮 + 数值"绑在一起的同步方案，重构为**分层架构**，
> 用启动参数 `--sync old|new` 切换，旧方案保稳定并行优化，新方案独立开发互不阻塞。

---

## 1. 项目结构与构建

```
D:\Dev\Open Nest co-op\
  src\OpenNestCoop\            ← 平台无关核心（BepInEx 壳的插件主体）
    Core\CoopRuntime.cs        入口：Startup() 建立网络+注册模块+Harmony
    Core\CoopBehaviour.cs      唯一帧驱动（Update → net.Update）
    Core\NetConfig.cs          常量配置
    Core\CoopLog.cs            统一日志门面（Info/Warn/Debug + 惰性求值 + 节流）
    Net\NetManager.cs          会话状态机 + 收发泵 + 批量合包
    Net\NetProtocol.cs         消息协议（MsgType + NetDataWriter/Reader）
    Net\ITransport.cs          传输抽象
    Net\SteamTransport.cs      Steamworks P2P
    Net\LocalTransport.cs      TCP 回环（双开测试）
    Net\AutoJoin.cs            命令行解析（--local/--autohost/--localport）
    Net\PlayerSession.cs       玩家模型
    GameSync\CoopSyncRegistry.cs   ISyncedModule 注册表/路由
    GameSync\*.cs              ~30 个同步模块（PlayerSync/ValueSync/ControlSync/...）
  src\OpenNestCoop.MelonMod\   ← MelonLoader 壳（引用核心）
  docs\ARCHITECTURE.md         现有架构与优化空间分析（务必先读）
  scripts\dualtest.ps1         双开测试（-Local 免 Steam）
```

**构建（必读）**：
```powershell
# G 端（BepInEx 插件）
dotnet build src\OpenNestCoop\OpenNestCoop.csproj -c Release -p:DeployToGame=true
# D 端（MelonLoader）
dotnet build src\OpenNestCoop.MelonMod\OpenNestCoop.MelonMod.csproj -c Release -p:DeployToMods=true
# 注意：MelonMod 的 MLBase 指向 G 端 MLLoader，D 端需手动复制 dll 到 D:\...\Mods\
Copy-Item src\OpenNestCoop.MelonMod\bin\Release\net6.0\OpenNestCoop.MelonMod.dll "D:\SteamLibrary\...\Mods\"
# 双开测试（必须用这个，它会等 host 端口再启 client）
.\scripts\dualtest.ps1 -Local
```

**平台无关核心**：逻辑都在 `CoopRuntime`，BepInEx/MelonLoader 壳只做日志注入 + 启动。

---

## 2. 现有同步架构（V1，你要重构的基线）

### 2.1 核心扩展点：`ISyncedModule`（GameSync/CoopSyncRegistry.cs）

```csharp
public interface ISyncedModule
{
    byte MsgType { get; }                 // 主消息类型（路由 key）
    void Tick(float dt);                  // 每帧/周期驱动（NetManager.Update 调用）
    void OnPacket(ulong from, byte[] data); // 收到本模块消息
    void OnSessionStarted(); / OnSessionEnded(); / Reset();
    void OnLateJoin(ulong steamId) { }    // 中途加入快照（默认空）
}
```

注册：`CoopSyncRegistry.RegisterModule(module, params byte[] extraTypes)`（主类型 + 附加类型）。
路由：`NetManager.OnPacket` 优先 `CoopSyncRegistry.TryRoute` → 命中交模块，未命中走内建 switch。

### 2.2 消息协议（Net/NetProtocol.cs）

- 首字节 `MsgType`。现有分配：**会话 1-6、内建同步 10-32、Batch=120、自定义模块 100+/131/133/134**。
- 序列化：LiteNetLib `NetDataWriter`（`NetProtocol.Begin(type)` 写类型字节）→ `NetProtocol.Snapshot(w)` 取副本 → `Transport.Send(peer, data, reliable)`。
- 周期状态：各模块 `EnqueueBatch` → `NetManager.FlushBatch` 帧末合包成 Batch(120) reliable 发出。
- ⚠️ **新方案 MsgType 必须用独立段（≥200）**，绝不与现有重叠（1-32/100+/120/131/133/134）。

### 2.3 会话状态机（NetManager.cs）

`SessionState { Idle, Hosting, Joined }`。host 权威 + 星型。`Update(dt)` 帧内：接收 Poll → 逐包 OnPacket；发送帧末 FlushBatch。
传输：`ITransport.Send(ulong peerId, byte[] data, bool reliable)` + `Poll(out ulong, out byte[])`。

### 2.4 你要重构的三块（问题所在）

现有把"事件 + 交互按钮 + 数值"绑在一起：
- **`ValueSync.cs`**：通用值同步轮子（`Binding`：float/int/bool + Deadzone/Interpolate/IsBusy/ClientNoApply/ClientNoSend/NoHeartbeat/HighFreq 标记）。
- **`ControlSync.cs`**：ValueSync 的"发现层"——每 3s `FindObjectsOfTypeAll` 扫 TurretController/DialInteractable/Slider 等，按场景路径注册 Binding。**扫描 + 值绑定耦合在一起**。
- **`ButtonClickSync.cs`**：直接 Harmony 挂钩 `LookAtTarget.OnClickDown` → 广播点击事件 + 0.8s toggle 状态轮询。

**V1 的问题**：单机逻辑（控件读取/按钮点击）与网络同步（值绑定/事件广播）**直接绑定在同一模块**，
主机权威逻辑散在各模块里，无法单独复用/测试/替换。

---

## 3. 新方案 V2：分层架构设计

### 3.1 目标分层

```
┌─────────────────────────────────────────────────────┐
│  SyncV2（新方案，独立命名空间 OpenNestCoop.SyncV2）  │
│                                                     │
│  HostDataLayer  主机权威数据层                       │
│    - 主机持有的状态权威（读/写/授权/广播）           │
│    - 抽象主机能力：IRoleAuthority / IHostStore        │
│                                                     │
│  EventLayer     事件层                               │
│    - 纯事件（开火/点击/交互），事件广播 + 对端复现    │
│    - 不持有状态，只转发                              │
│                                                     │
│  ValueLayer     数值层                               │
│    - 数值同步（deadzone/心跳/插值/settle）           │
│    - 通过 HostDataLayer 读写，不直接碰控件           │
│                                                     │
│  ButtonLayer    交互按钮层                           │
│    - 按钮/交互控件状态（toggle/click）               │
│    - 事件交给 EventLayer，状态交给 ValueLayer        │
└─────────────────────────────────────────────────────┘
```

**核心原则**：三层之间**不直接绑定**，都通过 `HostDataLayer` 交互；单机读取/按钮逻辑与网络同步解耦。

### 3.2 Feature Flag（启动参数切换）

在 `AutoJoin.ParseCommandLine()` 加 `--sync old|new`（默认 old）：
```csharp
// AutoJoin.cs
public static bool WantNewSync;   // --sync new

// CoopRuntime.Startup() 里按方案注册模块：
if (OpenNestCoop.Net.AutoJoin.WantNewSync)
    SyncV2.SyncV2Bootstrap.RegisterAll();   // 注册 V2 分层模块
else
    RegisterLegacyModules();                // 现有 CoopSyncRegistry.RegisterModule(...) 全部
```
- 默认 old（稳定发布线）；显式 `--sync new` 走新方案。
- **握手校验**：Hello/Welcome 带 `syncVersion` 字段，两端方案不一致时提示（避免协议错乱）。

### 3.3 SyncV2 目录与接口建议

```
src\OpenNestCoop\SyncV2\
  SyncV2Bootstrap.cs      RegisterAll()：注册所有 V2 模块（按 MsgType ≥200）
  HostDataLayer.cs        IHostStore/IRoleAuthority + 主机数据实现
  EventLayer.cs           EventSync（泛型事件广播，MsgType=200）
  ValueLayer.cs           ValueSyncV2（值同步，MsgType=201）
  ButtonLayer.cs          ButtonSyncV2（按钮状态，MsgType=202）
```

**接口草案（可自行细化）**：
```csharp
// 主机数据层：屏蔽"谁是主机/如何授权/如何广播"
public interface IHostStore
{
    bool IsHost { get; }
    void SetFloat(string id, float v);        // 主机写
    float GetFloat(string id);                // 读（主/客）
    void Broadcast(byte msgType, Action<NetDataWriter> write, bool reliable);
}
```

每个层都实现 `ISyncedModule`（MsgType ≥200），注册进 `CoopSyncRegistry` 走同一套 Tick/路由/会话生命周期。
**目标**：迁移一个验证一个（ValueSync→ValueLayer，ControlSync→HostDataLayer 发现，ButtonClickSync→Event+Button 层），
迁移时**吸收** `docs/ARCHITECTURE.md` 里对应模块的优化项（如 ControlSync Rescan 全场景扫描 → 场景加载事件驱动）。

---

## 4. 硬性约束（违反会导致联机错乱/崩溃）

1. **MsgType ≥ 200**，绝不与现有重叠（1-32 / 100+ / 120 / 131 / 133 / 134）。
2. **主机权威不变**：最终状态以 host 为准，客机不能覆盖主机共享状态（沿用 V1 的 `ClientNoSend` 思路）。
3. **防环必须保留**：事件复现时要有"是否正在应用远端操作"的 guard（V1 的 `IsApplying` 模式），
   否则本地事件 → 广播 → 对端复现 → 再广播 → 死循环/重复。
4. **确定性**：依赖游戏本地确定性逻辑的（如炮塔 Lever 同步后炮塔自然一致）不要重复同步状态，只同步命令/事件。
5. **IL2CPP 注意**：
   - `foreach` 遍历游戏字典/集合不可靠 → 用显式枚举器或索引。
   - `FindObjectsOfTypeAll` 开销大且含未激活 → 新方案避免高频全场景扫描，用缓存/事件驱动。
   - 字符串拼接/分配会 GC STW 卡帧 → 消息构造避免每帧分配（复用 writer/buffer）。
   - Animator `SetFloat(string)` 等内部 span 被裁 → 用 int 哈希重载。
6. **双端同方案**：`--sync` 不一致要握手拒绝。
7. **验证**：每次迁移完用 `.\scripts\dualtest.ps1 -Local` 双开测试，确认数值同步/按钮点击/事件复现/防环全正常。

---

## 5. 开发里程碑（给 V2 AI 的任务分解）

| # | 任务 | 产出 | 完成标准 |
|---|------|------|---------|
| M1 | 读 `docs/ARCHITECTURE.md` + `docs/SYNC_V2_DEV.md`，搭 SyncV2 骨架 | `SyncV2/` 目录 + `SyncV2Bootstrap` + `--sync` 参数 | 双端 `--sync new` 能启动不崩、走 V2 注册 |
| M2 | 实现 `HostDataLayer`（IHostStore/授权/广播）| `HostDataLayer.cs` | 主机读写 + 广播可被其他层调用 |
| M3 | 实现 `ValueLayer`（值同步，MsgType=201）| `ValueLayer.cs` | 把 ValueSync 一个代表性 Binding 迁过来，双端值一致 |
| M4 | 实现 `EventLayer`（泛型事件，MsgType=200）| `EventLayer.cs` | 开火/点击事件跨端复现 + 防环 |
| M5 | 实现 `ButtonLayer`（按钮状态，MsgType=202）| `ButtonLayer.cs` | 按钮 toggle 状态跨端一致 |
| M6 | 迁移 ControlSync 发现逻辑到 HostDataLayer（吸收扫描优化）| 重构后 ControlSyncV2 | 控件发现改为场景加载事件驱动，无 3s 全场景扫描 |
| M7 | 迁移其余模块（按 ARCHITECTURE.md 顺序）| — | 逐模块迁移 + 双端验证 |
| M8 | 清理：V1 保留（旧方案），V2 全绿 | — | `--sync old` 与 `--sync new` 都可独立跑 |

**注意**：M3-M7 每个里程碑都要双端实测（`dualtest.ps1 -Local --sync new`），不要一次性全量迁移。
