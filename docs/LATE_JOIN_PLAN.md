# 中途加入（Late Join）功能规划

## 目标
新玩家在游戏进行中加入主机，能在不重启房间的情况下拿到**当前完整游戏状态**，与现有成员看到一致的世界。

## 现状分析

### 加入流程（现有）
```
新玩家 JoinLobby → 主机 OnLobbyEntered(Joined) → 发 Hello(昵称)
  → 主机 OnHello: 分配 PlayerId + 发 Welcome(roster 名单) → 广播新名单
```
**问题**：Welcome 只含 roster 名单，**没有任何游戏状态**。中途加入者看不到当前任务/装填/控件/实体等状态。

### 各模块同步机制分类（决定快照策略）

| 模块 | 机制 | 中途加入现状 |
|---|---|---|
| **ValueSync/ControlSync** | 每 2s 全量心跳 + 0.2s 变化检测 | ✅ 心跳已覆盖（自动对齐） |
| **CatSync** | 每 0.2s 广播所有猫 | ✅ 周期全量（自动对齐） |
| **EntitySync** | 每 0.5s 广播实体列表 | ✅ 周期全量（自动对齐） |
| **ReloadSync** | 0.2s 变化检测 + `_forceBroadcast` | ⚠️ 变化检测无心跳 → 中途加入缺失 |
| **RecordPlayerSync** | 0.2s 变化检测广播 | ⚠️ 变化检测 → 中途加入缺失 |
| **MissionSync** | 0.5s 变化检测 + 2s 保活 | ⚠️ 保活只在 scene 非空时 → 任务中缺 phase/seed 初始 |
| **HatchSync** | 变化检测 | ⚠️ 缺失 |
| **SequenceSync** | 变化检测 | ⚠️ 缺失 |
| **ShellSync** | 变化检测 | ⚠️ 缺失 |
| **ButtonClickSync** | 纯事件（点击） | ⚠️ 无状态可快照（瞬时动作） |
| **MapMarkerSync** | 事件（增/删/清） | ⚠️ 无全量 → 中途加入缺已有标记 |
| **MapTokenSync** | 变化检测广播 | ⚠️ 缺失（令牌位置） |
| **RecordItemSync** | 0.2s 广播位置 | ✅ 周期全量（自动对齐） |
| **CoffeeSync** | 0.2s 值同步 | ✅ 周期全量 |
| **MissionEventSync** | 纯事件 | ⚠️ 无状态（任务过渡已完成的不需要） |
| **PurchaseSync/RequisitionSync** | 值绑定+事件 | ⚠️ 值绑定走 ValueSync 心跳，事件无需 |
| **GunLinkSync** | 0.3s | ✅ 周期 |

## 设计方案

### 核心：新增 `StateSnapshotSync`（全量状态快照，主机 → 新加入者）

在 `OnHello` 收到新成员时，主机**立即**给该成员发一份"全量状态包"，包含所有需要初始化的模块状态。

**触发时机**：新成员加入（`OnHello` 里，发 Welcome 之后）。

### 方案 A：逐模块全量发送（推荐，精准控制）
给需要快照的模块增加 `SendFullStateTo(ulong steamId)` 方法，主机收到新成员时逐个调用。

需要新增全量发送的模块：
1. **ReloadSync**：每炮 stateIndex + selectedCharges（当前 0.2s 变化检测，加全量接口）
2. **MissionSync**：当前 scene + phase + seed（任务中需让新成员直接进任务场景）
3. **ValueSync**：手动触发一次全量心跳（已有机制，加公开入口）
4. **HatchSync**：所有舱门/盖板状态
5. **SequenceSync**：发射台开关序列状态
6. **ShellSync**：弹舱/弹种状态
7. **MapMarkerSync**：已有标记全量（增发全量标记列表）
8. **MapTokenSync**：所有令牌位置
9. **RecordPlayerSync**：唱片机状态
10. **GunLinkSync**（如启用）

### 方案 B：通用"状态注册表"（已采用 ✅）
定义 `StateSnapshotSync` 注册表：`StateSnapshotSync.Register(name, BuildSnapshot, ApplySnapshot)`。
- 主机：新成员加入（`NetManager.OnHello` → `OnLateJoin`）→ 遍历所有 provider → 拼一个大快照包（MsgType=30）单播
- 新成员：收到 → 按模块名分发 `ApplySnapshot`（写状态，不广播防环）
- 优点：新增模块只要 `Register` 一对回调自动纳入，无需改 NetManager/接口
- 已注册模块：
  - `mission` → `MissionSync.BuildMissionSnapshot` / `ApplyMissionSnapshot`（scene/phase/seed，走正常 OnPacket 加载流程进任务）
  - `hatch` → `HatchSync.BuildHatchSnapshot` / `ApplyHatchSnapshot`（所有舱门/盖板 id+open）
  - `sequence` → `SequenceSync.BuildSequenceSnapshot` / `ApplySequenceSnapshot`（发射台开关序列 idx+count+mask）
  - `mapmarker` → `MapMarkerSync.BuildMapMarkerSnapshot` / `ApplyMapMarkerSnapshot`（已放置标记 kind/origin/target/id 全量）
  - `maptoken` → `MapTokenSync.BuildMapTokenSnapshot` / `ApplyMapTokenSnapshot`（所有令牌 id+pos+rot 全量）
  - `recordplayer` → `RecordPlayerSync.BuildRecordPlayerSnapshot` / `ApplyRecordPlayerSnapshot`（playing/track/vol/recordName）
- **ReloadSync 特殊**（时序：需场景就绪 + SetState 安全对齐按钮）：保留独立 pending 单播
  - `ReloadSync.SendFullStateTo(steamId)`：场景未就绪入 `_pendingLateJoin` 队列，Tick 每轮重试直到发出
  - 应用端 `OnState` 用 `applyState=1` 标志 → `g.Reload.SetState(stateIndex, true)` + `UpdateAllAdvanceButtons()` 安全对齐
- **不需要快照**：ValueSync/CatSync/EntitySync/RecordItemSync/CoffeeSync/GunLinkSync（周期全量自动对齐）、ShellSync（1.5s 心跳）、ButtonClickSync/MissionEventSync/PurchaseSync（纯事件，无状态可回放）

## 实现步骤（方案 B，已完成）

### Step 1: 消息类型
- `MsgType` 新增：`StateSnapshot = 30`（主机 → 新加入者，全量状态容器）

### Step 2: 快照容器
`GameSync/StateSnapshotSync.cs`（ISyncedModule，MsgType=30）：
- `Register(string name, Func<byte[]> build, Action<byte[]> apply)` 静态注册表
- `OnLateJoin(ulong)`：主机收集所有 provider 的 build 快照 → `[count][name][PutBytesWithLength(data)]...` 单播
- `OnPacket`：`GetBytesWithLength()` 取子包 → 按 name 分发 Apply

### Step 3: 触发点
`NetManager.OnHello` 发 Welcome 后 `SendLateJoinSnapshot(from)`：
- `foreach (CoopSyncRegistry.Modules) m.OnLateJoin(steamId)` → 含 StateSnapshotSync.OnLateJoin（统一容器，收集所有已注册模块快照）
- **ReloadSync 不在首次发**：新成员此时可能还在主菜单/加载任务场景，过早发会被应用端 `ResolveGuns()` 空而丢弃
- 装填快照由**新成员场景就绪后请求补发**：客机进入炮台场景（`ResolveGuns` 非空）→ `StateSnapshotSync.RequestSnapshot()`（MsgType=31）→ 主机 `OnLateJoin` 重发容器 + `ReloadSync.SendFullStateTo`

### Step 4: 任务场景处理
- 任务快照（scene/phase/seed）先发 → 新成员 `TryLoadMissionScene` 进任务（异步）
- 装填快照靠 ReloadSync pending 重试：等场景就绪后再发（避免引用失效）
- 地图/令牌/唱片机快照由容器一次发出（这些模块的场景对象在进任务后由各自 Tick 自然补齐，快照主要补静止状态）

### Step 5: 应用端
新成员收到 `StateSnapshot` → 解包 → 各模块 `ApplySnapshot`（写状态，不广播防环）

### Step 6: 时序/竞态
- 全量状态用可靠通道（reliable）
- 任务快照驱动场景加载；其余快照在对象未就绪时由 Apply 内的 null 检查安全跳过

## 风险与注意事项
1. **任务场景加载是异步的**：新成员中途加入任务，必须等场景加载完再应用任务相关快照（否则引用失效）——ReloadSync 用 pending 重试解决
2. **按钮/事件类**（ButtonClickSync、MissionEventSync、PurchaseSync）：瞬时动作无需快照（不可回放的历史动作）
3. **带宽**：全量状态只在加入时发一次，影响小
4. **角色分配**：新成员加入需分配岗位（Role），现有 `SetRole` 已支持
5. **ValueSync 心跳已覆盖**：手动触发一次心跳即可，不需额外快照

## 验收标准
- 中途加入后：能看到当前任务（若在任务中）、炮塔/控件状态、装填状态、地图标记、实体、猫、唱片机状态，与主机一致
- 加入不打断现有成员的同步
- 双平台（BepInEx + MelonLoader）均可
