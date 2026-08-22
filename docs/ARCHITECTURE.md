# Open Nest Co-op 架构与技术分析（2026-08-23）

> 本文档梳理模组的核心代码结构、网络同步架构、数据同步模式，
> 以及基于实际代码（文件:行号）的优化空间清单，供后续维护与优化参考。
> 基于对 `src/OpenNestCoop/`（BepInEx 壳）+ `OpenNestCoop.MelonMod/`（MelonLoader 壳）的实际通读。
>
> **2026-08-23 修订**（同步至 **0.1.8** 代码基线）：V1 模块清单补全（新增 ArmSync / CylinderActionSync /
> ChargeInventorySync / ChargeButtonSync）、StateSnapshotSync 已注册快照补全为 8 个、TurretSync 精简说明、
> 装填事件驱动与方向角双通道、SyncV2（`--sync new`，MsgType 200-229 分层）现状。

---

## 1. 架构总览

**"一套核心，双平台"**：平台无关核心在 `CoopRuntime`（`Core/CoopRuntime.cs`），
BepInEx 壳（`Plugin.cs`）与 MelonLoader 壳（`MelonModEntry.cs`）只做日志注入 + 启动/卸载。

```
Plugin.cs / MelonModEntry.cs        ← 平台壳（仅初始化 + 卸载）
  └─ CoopRuntime.Initialize/Startup/Shutdown
       ├─ NetManager          会话状态机（Hosting/Joined/Idle），单线程帧驱动
       ├─ ITransport         传输抽象：SteamTransport(P2P) / LocalTransport(TCP回环)
       ├─ NetProtocol        消息协议（LiteNetLib NetDataWriter/Reader）
       ├─ PlayerSession      玩家管理（PlayerId/Roster/Role/踢封禁）
       ├─ CoopSyncRegistry   ISyncedModule 注册表（V1 ~26 模块 + 附加类型路由；--sync new 走 SyncV2 分层）
       ├─ StateSnapshotSync  中途加入全量快照（30/31，已注册 8 个快照）
       ├─ Patches/Harmony    游戏方法挂钩（开火/输入/装填/地图/预备激发/弹舱动作等）
       └─ GameSync/*         同步模块（V1：Player/Value/Control/Entity/Cat/Arm/CylinderAction/...）
```

**同步方案**：默认 **V1**（`--sync old`，`CoopRuntime.RegisterLegacyModules` 整组注册）；**V2 实验**
（`--sync new`，`SyncV2Bootstrap.RegisterAll`，MsgType ≥200 分层：HostDataLayer/ValueLayer/EventLayer/
ButtonLayer/ControlSyncV2/PlayerSyncV2/...，见 `docs/SYNC_V2_DEV.md`）。双端需同方案（Hello/Welcome 握手校验）。

**V1 模块完整清单**（`CoopRuntime.cs` `RegisterLegacyModules`）：
`CoffeeSync / MissionSync / StateSnapshotSync / MissionEventSync / NotificationSync / TeleprinterSync /
CounterBatterySync / EntitySync / ReconPhotoSync / CatSync / MapMarkerSync / RecordItemSync / ShellSync /
SequenceSync / HatchSync / ButtonClickSync / ArmSync / CylinderActionSync / ChargeInventorySync /
ChargeButtonSync / MapTokenSync / GunLinkSync / PunchcardSync / M3EnvSync / RequisitionSync / PurchaseSync`
（**0.1.8 新增/早期文档缺失**：`ArmSync`=140 预备激发、`CylinderActionSync`=141 弹舱动作、
`ChargeInventorySync`=142 装药库存、`ChargeButtonSync`=143 Button Dispencer 掩码）。

**驱动方式**：`CoopBehaviour.Update`（Core）每帧调 `net.Update(Time.unscaledDeltaTime)`
（用 unscaledDeltaTime——游戏 timeScale=0 暂停时不中断同步）。

**线程模型**：**单线程、帧内驱动**。
- 接收：Steam 回调线程只把包入队 → 主线程 `Update` 里 `Poll` 串行处理（防回调内同步 API 死锁）。
- 发送：各模块帧末 `EnqueueBatch` 聚合 → `FlushBatch` 帧末统一发（合包）。

---

## 2. 网络同步架构

### 2.1 会话状态机（`Net/NetManager.cs`）

状态：`SessionState { Idle, Hosting, Joined }`（:17）。

主循环 `Update(dt)`（:122）：
- `LocalMode` → `UpdateLocal`；否则 `EnsureSteamContext()` → `SteamAPI.RunCallbacks()` →
  `AutoJoin.TryStart` → `Lobby.PollPendingLobbyList()` → `while (Transport.Poll(...)) OnPacket(...)`。

`UpdateCommon(dt)`（:207）每帧：每 3s Ping 测 RTT → 依次 Tick `PlayerSync → RecordPlayerSync →
ReloadSync → MapSync → ControlSync(含 ValueSync) → CoopSyncRegistry.TickAll → FlushBatch()` →
每 10s 打印收发统计。

状态迁移：
- 建房/加入成功 → `OnLobbyEntered()`：host 置 Hosting（PlayerId=0、Roster=[本地]）；client 置 Joined、发 Hello。
- 离开 → `OnLobbyLeft()`：清 Roster/ChatLog/封禁，回 Idle。
- 成员变化：host 增删成员并广播 Roster；client 检测 host 离开则返回大厅（**无主机迁移**）。

### 2.2 传输层（`Net/ITransport.cs`）

```csharp
interface ITransport { void Send(ulong peerId, byte[] data, bool reliable); bool Poll(out ulong sender, out byte[] data); }
```

| | SteamTransport | LocalTransport |
|---|---|---|
| 通道 | Steamworks P2P（中继打洞） | TCP 回环 127.0.0.1:29507 |
| 可靠性 | 显式 reliable/unreliable 双通道 | TCP 天然可靠 |
| 单包上限 | **unreliable ~1200B**（超限整包拒收） | 1MB（守卫） |
| 线程 | 回调线程入队，主线程泵 | 后台读线程 + ConcurrentQueue |
| 身份 | 真实 SteamID | 自增 peerId（1=host 2=client）|
| 用途 | 正式联机 | 双开开发测试（--local host/join）|

**关键约束**：Steam unreliable 单包上限约 1200B 且高频丢包率高 →
**所有周期状态同步统一走 reliable 合包 + 按 1000B 拆包**（NetManager.cs:290）。

### 2.3 协议与消息格式（`Net/NetProtocol.cs`）

- 首字节 = `MsgType`（byte）。分类：**会话类**（Hello/Welcome/Roster/Ping/Pong/Chat/Kick）、
  **内建同步**（TurretState…ControlCmd、PlayerPos、Batch=120）、**自定义模块**（MsgType ≥ 100）。
- 序列化用 LiteNetLib `NetDataWriter/Reader`。Batch 容器：`[120][n:byte][len:ushort+子包]*`。
- 可靠/不可靠：事件/会话类直接 `Send(..., reliable:true)`；周期状态类走 `EnqueueBatch` → 帧末合包（reliable）。

### 2.4 同步注册表（`GameSync/CoopSyncRegistry.cs`）

- `ISyncedModule`：`MsgType / Tick(dt) / OnPacket(from,data) / OnSessionStarted/Ended / Reset / OnLateJoin`。
- `RegisterModule(module, params byte[] extraTypes)`：加入列表 + 按类型路由（一个模块可处理多类型）。
- 路由：`NetManager.OnPacket` 优先 `CoopSyncRegistry.TryRoute`，未命中才走内建 switch。
- `TickAll` 会话状态事件 + 遍历全部模块 Tick（各模块内部 interval 门控）。

### 2.5 玩家管理（`Net/PlayerSession.cs` + `SteamLobby.cs`）

- `PlayerSession`：SteamId、Name、PlayerId（byte，0=主机）、CrewRole、IsHost/IsLocal、PingMs。
- host `OnHello`：封禁/满员检查 → 分配 `NextFreeId` → Welcome（全量 Roster）→ 广播 Roster → 中途加入快照。
- 大厅发现：lobby 只做"发现"，游戏数据走 P2P；`OnLobbyList` 回调内不读 `m_nLobbiesMatching`
  （IL2CPP 读垃圾值）→ 逐项收集，详情在 Update 安全上下文填充。

### 2.6 中途加入快照（`StateSnapshotSync`，30/31）

各模块 `Register(name, build, apply)`；host `OnLateJoin` 收集所有 provider 的 `Build()` 快照
单播给新成员。客机进任务场景后 `RequestSnapshot`(31) → host 重发 + 装填 SetState 安全对齐。
已注册（8 个）：mission / hatch / sequence / mapmarker / maptoken / recordplayer / **button** / **entity**
（button=指示灯/楼梯盖板等多 toggler 按钮 toggle 状态；entity=反炮兵炮兵/药包等任务动态实体）。

---

## 3. 数据同步模式（代表模块）

总体：**主机权威 + 星型 + 状态广播/差值/命令混合**。

| 模块 | 模式 | 频率 | 方向 |
|---|---|---|---|
| PlayerSync | 状态上报 + 死区（Pos 0.03m/Yaw 1.5°/Pitch 1.5°）| 0.1s | 客户端→主机→其他端 |
| ValueSync | 变化检测 + 2s 心跳 + 拖拽 settle + 插值 | 0.2s / 高频 0.033s(Lever/Gear) | 双向 |
| ControlSync | OnEnable 逐控件注册（主路径）+ Rescan 保底 → 委托 ValueSync | OnEnable / Rescan 30s | — |
| TurretSync | **纯事件**（仅开火 GunFire 广播），无状态同步/无输入上行 | 事件触发 | 主机→全员 |
| EntitySync | 状态广播（聚合 n>1）+ entity 快照 | 0.5s | 双向 |
| CatSync | 主机 AI 软同步 + 交互事件 + 偏差硬同步 | 1/3s | 双向 |
| ButtonClickSync | 点击事件 + toggle 轮询（装填/弹舱/预备激发已排除） | 0.8s 轮询 | 双向 |
| **ArmSync** | 预备激发事件（ArmedFireRelayOneShot 四方法） | 事件 | 双向（谁操作谁上报 + 主机中继） |
| **CylinderActionSync** | 弹舱动作事件（推弹/切弹） | 事件 | 双向（谁操作谁上报 + 主机中继） |
| **ChargeInventorySync** | 装药库存（CurrentCharges） | 0.5s 轮询变化 | 主机→客机 |
| **ChargeButtonSync** | Button Dispencer active 掩码（6 位） | 0.3s 轮询 + 3s 补发 | 主机→客机 |

**PlayerSync**：10Hz + 死区检测；数据含位置/yaw/横移分量/姿态位/俯仰/真实速度。
远端化身指数插值 `1-exp(-12*dt)` + `DeltaAngle` 短路径朝向。化身由 `IPlayerVisualProvider`
提供（AnimatorAvatarBundle / ExternalModel / CatCrew / Humanoid），支持注册覆盖。

**ValueSync**（通用值同步轮子）：每个 `Binding` 带 Deadzone/Interpolate/IsBusy/ClientNoApply/
ClientNoSend/NoHeartbeat/HighFreq 细粒度标记。host 只在变化/edge/心跳时广播；
client 变化上行（ClientNoSend 的不上行，防客机读 0 覆盖主机）；拖拽释放瞬间 settle 精确发送。

**TurretSync**（架构最精简）：**不做炮塔状态同步/输入上行**——炮塔控制完全交给 ControlSync 的 Lever/Gear
值同步（谁操作谁权威，游戏本地确定性逻辑驱动炮塔，动画+数值天然一致）。本类只保留开火事件。

**开火链路（V1）**：客机 `RequestFire` 被 Harmony 拦截上行 `FireRequest`(21) → 主机执行 `RequestFire` →
`FireShell` postfix 广播 `GunFire`(11, gunIndex) → 客机复现开火（`ReloadSync.IsApplyingFire` 防环，
避免复现时再上行造成循环）。

**装填（ReloadSync，0.1.8 事件驱动）**：0.1.8 回退 9aca1a8（"总是 SetState"）——装填状态**不再靠常规广播
SetState**，而是**事件驱动**推进：选药量/投放（`PowderEvent`=29）、推弹/切弹（CylinderActionSync=141），
两端执行完整操作过程自然一致；常规广播不写 stateIndex（防跳推弹动画/跳过过程）；`currentSelectedCharges`
单向（只在 applyState 中途加入时同步，防双向覆盖过渡值来回跳）。Button Dispencer **差1修复**（不再强制覆盖
currentSelectedCharges）+ **开局误激活修复**（移除 Tick 补激活链；ChargeButtonSync=143 主机权威掩码 0.3s 轮询
+ 3s 周期补发，客机 SetActive 对齐）。

**方向角双通道（ValueSync）**：转速模式（拉杆控制 Gear 转速/动量，松开后动量保持）——
① `__turret/rotVel` 转速（desiredRotationVelocity）HighFreq **30Hz** 双向，对端平滑旋转、动量一致
（`SenderWhenBusy` 单向：非操作方不上行，防干扰动量）；② `__turret/rotation` 角度低频兜底
（**只在转速≈0 静止时广播**当前角度对齐；转盘停稳非0→0 时 `ForceSend` 强制广播一次角度）。
仰角 Lever 同理值同步（`Interpolate=false`，避免持续拉向远端覆盖本地操作）。

**EntitySync**：主机权威，主数据源用 `FireMission.Entities` 字典（显式枚举，IL2CPP foreach 不可靠），
避免全场景扫描（未初始化实体有垃圾坐标）。已聚合发送（n>1 合 1 包，56 小包/s → 2 包/s）。

---

## 4. 代码优化空间（文件:行号 → 问题 → 建议）

### A. 发送/接收路径堆分配（GC 热点，最高优先级）

**A1. 每发一包 3 次堆分配副本** ✅ 已实施（2026-08 轨道 A）
- `NetProtocol.Begin`（:59）→ `new NetDataWriter()`；`NetProtocol.Snapshot`（:69）→ `new byte[]` + `Array.Copy`；
  `SteamTransport.Send`（:32）→ `new Il2CppStructArray<byte>` + 逐元素拷贝。
- 任务场景发包：PlayerSync 10Hz + ValueSync 30Hz(Lever) + EntitySync 2Hz + CatSync 3Hz + 心跳，
  每秒数十包 × 3 次分配，IL2CPP 托管堆 GC STW 卡帧。
- **实施状态**：① `Send(byte[], int len)` 重载已加（ITransport + SteamTransport + LocalTransport，合包路径直接用 `writer.Data/len` 跳过 Snapshot 副本）；
  ② `NetDataWriter` 对象池已加（`Begin` 池取 + `Snapshot` 自动回池，72 处调用点零改动）；
  ③ `SteamTransport._sendBuf` 复用已加（SendP2PPacket 同步拷贝安全）；
  ④ 逐元素 for 拷贝改 `Marshal.Copy`/批量 `CopyTo` 未做（可选微优化）。双端回归验证通过。

**A2. 收包路径分配** ⏸ 暂缓（低优先级：收包频率低于发包，且 data 被 OnPacket→TryRoute/各模块分散消费，池化需全局确认同步用完，风险高）
- `SteamTransport.Poll`（:56,67）每次 `new Il2CppStructArray` + `new byte[read]`；
  Batch 解析（NetManager.cs:736）每子包 `new byte[]` + `new NetDataReader`。
- **建议**：Poll 用池化接收缓冲；Batch 子包复用单个 reader（`RawData`+位置偏移）。
  ⚠️ 保持现有 `Array.Copy(r.RawData, r.Position, ...)` + `SkipBytes(len)` 的正确写法（勿改 `GetRemainingBytes`）。

**A3. `FlushBatch` 每帧临时分配**（:290,327） ✅ 已实施（2026-08 轨道 A）
- `SplitBatches`/`BuildBatch` 每帧 `new List` + `new NetDataWriter` + Snapshot 副本。
- **实施状态**：`_batchWriter` 字段复用已加（Reset 保留内部 buffer）+ 合包直接 `Send(writer.Data, len)` 跳过 Snapshot 副本。
  `SplitBatches` 的 `List<List<byte[]>>` 分组仍未复用（每帧 1-2 个 List，收益小，可选）。

### B. 周期性全场景扫描（周期性卡顿源）

- **B1 `ControlSync.Rescan` 每 3s 4 次 `FindObjectsOfTypeAll`**（:147,150,188,225）⭐ 高 ✅ 已实施（2026-08 轨道 A）
  → **并行方案**：OnEnable 逐控件注册（主路径，Harmony patch DialInteractable/LinearSliderInteractable/SliderEnergyMomentumSpinner OnEnable → `OnControlEnabled` 即时注册单个控件，不触发全量扫描）
    + Rescan 保底（30s 周期 + 场景 buildIndex 变化初始化；`detectMiss` 区分——初始化扫描不算漏，运行期保底记录漏检）
    + 独立日志 `ControlSync.onEnableMiss`：记录"OnEnable 未覆盖、靠 Rescan 补注册"的控件（TurretController 无 OnEnable override 必然靠 Rescan）。
    移除逻辑保留在 Rescan 内（current 对比）。ValueSync 2s 心跳负责已注册控件对齐。
- **B2 `ButtonClickSync.PollToggleStates` 每 0.8s `FindObjectsOfType<LookAtTarget>(true)`**（:157）⭐ 中 ✅ 已实施
  → `GetTargets()` 实例缓存（5s 刷新 + 场景切换即时刷新）；低频事件处保留即时扫描保证正确性。
- **B3 `CatSync` 多处 `FindObjectsOfType<CatController>`**（:71,223,271,503,553）✅ 已实施
  → `GetCats()` 猫实例缓存（3s 刷新 + 场景切换即时刷新；缓存顺序 = FindObjectsOfType 顺序，保跨端索引一致）。
- **B4 `ReloadSync.Tick` 内 `ResolveGuns()` + `FindObjectsOfTypeAll<PowderChargeController>`**（:575）✅ 已实施（先前已有）
  → ResolveGuns 结果缓存 + 脏标志（`_cachedTurret == turret.Pointer`，turret 为 null 重置）。

### C. LINQ 与委托分配 ✅ 已实施（2026-08 轨道 A）

- `NetManager` 内 `Roster.Where/FirstOrDefault/Any`（:216,432,581,596,680,710,878,957,971,998）
  → 已改 `for` 循环消除迭代器+委托分配（10 处）。`Roster.RemoveAll`（成员离开事件，低频）保留。

### D. 可缓存组件/属性访问

- **D1 `ValueSync` 每帧两遍遍历 `_bindings.Values`**（`ApplyInterpolated`:278 + `TickDraggingChange`:223）
  → **评估后不做**：两遍遍历语义不同（拖拽检测 vs 远端插值），合并易破坏拖拽/插值逻辑，62 个 binding 收益极小（~微秒级）。
  ⚠️ 确认高频 `IsBusy` 不退化到 `FindObjectsOfTypeAll`（ControlSync.cs:413 兜底扫描）。
- **D2 `PlayerSync.GetBodyTransform`/`GetYaw`**（:272,278）：`_fpc` 未找到时每 0.1s 各扫一次
  `FindFirstObjectByType<FirstPersonController>` → 查找失败加退避（`_fpcRetryTimer` 1s 重查）。✅ 已实施
- **D3 `PathOf` 字符串拼接**：仅 Rescan(3s)，可接受；`ButtonClickSync` 的 `sig += b?"1":"0"`（:183）低频，低优先级。

### E. 发送频率/批量评估 ✅ 分级已实施（2026-08 轨道 A）

- **合理**：PlayerSync 10Hz+死区；ValueSync 高频仅 Lever/Gear；EntitySync 聚合；
  CatSync 3Hz；心跳 2s 全量；Batch 上限 256 + 1000B 拆包。
- **分级已实施**：`EnqueueBatch(data, toAll, reliable=true)` 新增 unreliable 通道（独立队列 + FlushBatch unreliable 发送）：
  - `ValueSync`：高频 tick（30Hz Lever/Gear）→ unreliable；低频 + 心跳（2s 全量）→ reliable（保底对齐）；OnCmd 高频转发 unreliable
  - `PlayerSync`：**状态标记与位置数据拆两包**——`PlayerPos`（连续位置/移动值 pos/yaw/move/speed → unreliable + 每 2s reliable 心跳帧防漂移）；`PlayerState`（离散状态标记 flags[空中/蹲下/冲刺] + 俯仰 → **必须 reliable**：丢失会卡在错误状态不能靠下帧纠正，变化才发频率低）
  - 其余模块默认 reliable（命令/事件/快照不变，46 处现有调用零改动）
  - **修复**：`MapSync` 的 MapMarkerAdd/Remove/ClearAll（离散状态，边沿触发无周期重发）原先误走 unreliable → 已改 reliable（丢失会永久不一致：标记缺失/残留）
- **⚠️ unreliable 单条大小约束**：unreliable 消息只要任意分片丢失 → 整条全部丢弃（不会收残缺版）。因此 **unreliable 不合并成大 Batch**（分片整条丢 + 连坐多个子包），而是**每个子包单独小包发送**（单条几十 B，互不影响）；单条 >1100B 降级 reliable（保证送达）。接收端直接路由裸状态包（首字节 MsgType），reliable 路径仍合包 Batch。
- **保底回退**：unreliable 丢包由低频 reliable 心跳校正（ValueSync 2s 心跳 / PlayerSync 2s 心跳），无需接收端反馈协议。
  ⚠️ Steam 真实丢包/乱序需 Steam 联机实测（local 双开是 TCP 不丢包，仅验证代码路径）；历史教训"unreliable 高频曾全断"，保底心跳已覆盖。

---

## 5. 架构观察

### 优点
1. **单线程帧内驱动 + 回调入队/主线程泵**：避免 IL2CPP 回调线程同步 API 死锁，架构清晰。
2. **传输抽象 + 双实现**：Steam P2P 与 LocalTransport 无缝切换，双开测试与正式联机同代码。
3. **可插拔同步模块（CoopSyncRegistry）**：新增同步功能只需实现 ISyncedModule + Register。
4. **主机权威 + 状态/事件混合**：TurretSync 纯事件（利用游戏确定性）显著减带宽；
   ValueSync 细粒度标记（deadzone/interp/edge）适配不同控件。
5. **中途加入快照注册表**：模块声明式对齐，避免全量重连逻辑重复。

### 风险/待改进
1. **发送/接收路径堆分配密集**（§4A）：IL2CPP GC STW 是主要帧卡来源，优先池化。
2. **周期性全场景扫描**（§4B）：`FindObjectsOfTypeAll` 是周期性微卡来源，改缓存/事件驱动。
3. **统一 reliable 无分级**（§4E）：数据量大时拥塞风险，建议容忍丢失通道分级。
4. **无主机迁移**：host 掉线全员回大厅（可接受但需在文档/UI 说明）。
5. **PlayerSync 10Hz 固定**：可考虑按移动速度自适应（静止降频，移动升频）进一步省带宽。

---

## 6. 关键文件索引

| 文件 | 职责 |
|---|---|
| `Core/CoopRuntime.cs` | 平台无关核心：初始化/Startup/Shutdown、模块注册 |
| `Core/CoopBehaviour.cs` | 唯一帧驱动（Update → net.Update）|
| `Net/NetManager.cs` | 会话状态机、收发泵、批量合包 |
| `Net/NetProtocol.cs` | 消息协议、序列化、Roster |
| `Net/ITransport.cs` | 传输抽象 |
| `Net/SteamTransport.cs` | Steamworks P2P 实现 |
| `Net/LocalTransport.cs` | TCP 回环双开实现 |
| `Net/PlayerSession.cs` | 玩家模型 |
| `Net/SteamLobby.cs` | 大厅发现/会话 |
| `GameSync/CoopSyncRegistry.cs` | ISyncedModule 注册表/路由 |
| `GameSync/StateSnapshotSync.cs` | 中途加入快照 |
| `GameSync/PlayerSync.cs` | 玩家位置同步 + 化身管理 |
| `GameSync/ValueSync.cs` | 通用值同步轮子 |
| `GameSync/ControlSync.cs` | 控件发现层 → ValueSync |
| `GameSync/TurretSync.cs` | 开火事件同步（纯事件）|
| `GameSync/EntitySync.cs` | 实体状态同步（聚合）|
| `GameSync/CatSync.cs` | 猫 AI 软/硬同步 |
| `GameSync/AnimatorAvatarVisualProvider.cs` | 玩家化身 AssetBundle + Animator 驱动 |
| `GameSync/ArmSync.cs` | 预备激发事件同步（MsgType=140） |
| `GameSync/CylinderActionSync.cs` | 弹舱动作事件同步（MsgType=141） |
| `GameSync/ChargeInventorySync.cs` | 装药库存同步（MsgType=142） |
| `GameSync/ChargeButtonSync.cs` | Button Dispencer active 掩码同步（MsgType=143） |
| `SyncV2/SyncV2Bootstrap.cs` | V2 分层注册入口（--sync new，MsgType 200-229） |
