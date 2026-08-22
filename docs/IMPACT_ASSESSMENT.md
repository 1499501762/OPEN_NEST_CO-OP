# 着弹评估模块（Impact Assessment）

> **目的**：记录游戏"炮弹飞行 → 着弹 → 着弹评估/报告 → 落点标记/照片/反炮兵"的完整机制，
> 以及本模组对着弹相关系统的联机同步归属（哪些同步、哪些本地模拟）、已知问题与诊断手段。
> 与 `docs/INTERACTABLES.md`（交互实体术语表）互补——那里记录**玩家可交互实体**，
> 这里记录**任务/战斗逻辑实体**（炮弹、着弹、落点、侦察照片、反炮兵）。
>
> **信息可信度**：类/方法名来自 `tools/dump_Assembly-CSharp.txt`（IL2CPP 反编译）；
> 调用链与次数来自 `[ImpactDiag]` 诊断实测（2026-08-23 双端日志）。
>
> **关联文档**：`docs/ARCHITECTURE.md`、`docs/DECOUPLING.md`、`docs/INTERACTABLES.md`。

---

## 一、游戏侧着弹链路

```
开火（GunController.RequestFire → FireShell）
  └─ 创建炮弹 ShellVisual（Initialize：startPos/targetPos/travelDuration/shell）
       └─ 每帧 Update() 沿弹道飞行（本地模拟，2D 战术地图坐标系）
            └─ 着弹（到达 targetLocalPos / 出界）
                 ├─ ImpactTracker.EvaluateImpact(shell, loc, triggerNormalEvents)   ← 着弹评估（static）
                 │    └─ 命中判定：ImpactTracker.GetNearest / EntityLocations 字典
                 ├─ ImpactLocation.EvaluateAndReport()                              ← 着弹报告
                 │    └─ ReportLocationNextFrame()（coroutine，下一帧报告落点）
                 ├─ ShellVisual.SpawnImpactEffectAt(localPos)                       ← 着弹特效
                 ├─ ImpactIndicator.HandleLocalSpaceEvent                           ← 着弹区域事件
                 └─ 落点标记（ImpactMarkerManager markerDataList）
```

### 关键类（`tools/dump_Assembly-CSharp.txt`）

| 类 | 关键成员 | 职责 |
|---|---|---|
| `ShellVisual` | `Initialize(start,target,duration,shell)`、`Update()`、`SpawnImpactEffectAt(localPos)`、`SpawnOutOfBoundsEffectAt` | 炮弹对象：2D 弹道飞行 + 着弹特效（本地模拟） |
| `ImpactTracker` | **`static EvaluateImpact(shell, loc, triggerNormalEvents)`**、`GetNearest(...)`、`OnImpact` 事件、`EntityLocations` | 着弹评估总入口（static）：命中判定/实体关联 |
| `ImpactLocation` | `Init(shell, triggerNormalEvents)`、`EvaluateAndReport()`、`ReportLocationNextFrame()`（coroutine）、`ScoutingStrips`（侦察条）、`SetScoutingStripsActive` | 着弹报告：把落点报告给战术地图（照片/标记） |
| `ImpactIndicator` | `HandleLocalSpaceEvent(EventData_Impact)`、`onImpactWithinRegion` | 着弹区域指示（落点是否在区域内 → 触发事件） |
| `ImpactMarkerManager` | `markerDataList`、`masterImpactMarkerInstance`、`UpdateAllGunMarkers` | 战术地图落点标记管理 |
| `ImpactExplosionSpawner` | `SpawnExplosionNextFrame()` | 3D 爆炸特效 |
| `MapReconClearHandle` | `RegisterChild(child)`、`DestroyAll()` | 侦察照片/标记对象注册到清除器（战术地图侦察标记） |
| `CounterBatteryCinematicImpactSpawner` | `SpawnOne()` | 反炮兵（敌方反击）电影式落点生成 |

---

## 二、联机同步归属

| 子系统 | 同步方式 | 模块 | 说明 |
|---|---|---|---|
| **开火** | 事件同步 | TurretSync（GunFire=11）/ V2 EventLayer | 客机 RequestFire 上行 → 主机执行 → GunFire 广播 → 客机复现（防环） |
| **炮弹飞行/着弹** | **本地模拟**（不跨端同步） | — | 两端用相同开火参数各自本地模拟弹道；着弹点由弹道确定，两端一致（确定性） |
| **着弹评估** | **本地模拟**（不跨端同步） | — | `ImpactTracker.EvaluateImpact` 由 `ImpactLocation.EvaluateAndReport` 本地调用；每端各自评估 |
| **侦察照片** | seed 同步（内容一致） | ReconPhotoSync（105） | `MapReconClearHandle.RegisterChild` prefix → 拍照前统一随机 seed → 两端程序化生成一致照片 |
| **反炮兵落点** | seed 同步（落点一致） | CounterBatterySync（103） | `CounterBatteryCinematicImpactSpawner.SpawnOne` prefix → 统一 seed → 两端一致落点 |
| **任务随机 seed** | seed 广播（keepalive） | MissionSync（102） | 任务生成固定 seed → 两端任务实体/目标一致 |
| **任务实体** | 状态快照同步 | EntitySync（104） | MapEntity（敌人/炮兵/任务目标）位置/状态/存活，缺实体 CreateMapEntity 补齐 |

**关键结论**：炮弹飞行 + 着弹评估是**每端本地确定性模拟**（不跨端广播位置），
两端用相同开火参数（枪口/弹道/落点由弹道计算）自然一致——这是模组的依赖假设。
侦察照片/反炮兵因为用了 `UnityEngine.Random` 程序化生成，才需要 seed 同步保证两端内容一致。

---

## 三、`[ImpactDiag]` 诊断（2026-08-23 加入）

排查"只打两发但着弹多触发"加的 Harmony postfix 计数，patch 于 `Patches/HarmonyPatches.cs`：

| 日志 | patch 方法 | 含义 |
|---|---|---|
| `[ImpactDiag] ShellVisual.Initialize n=?` | `ShellVisual.Initialize` | 炮弹对象创建次数（应 = 实际发射数） |
| `[ImpactDiag] EvaluateImpact n=? t=? loc=? call=[堆栈]` | `ImpactTracker.EvaluateImpact`（static） | 着弹评估次数 + 时间 + 落点 + **调用来源堆栈** |
| `[ImpactDiag] SpawnImpactEffectAt n=?` | `ShellVisual.SpawnImpactEffectAt` | 着弹特效生成次数 |
| `[ImpactDiag] ImpactLocation.EvaluateAndReport n=?` | `ImpactLocation.EvaluateAndReport` | 着弹报告路径次数 |
| `[ImpactDiag] ImpactLocation.ReportLocationNextFrame n=?` | `ImpactLocation.ReportLocationNextFrame` | 着弹报告 coroutine 次数 |

### 实测基准（2026-08-23 双端，2 发齐射）

```
ShellVisual.Initialize n=1,2                        ← 2 个炮弹（正确）
EvaluateImpact n=1 loc=(11.1,7.8) call=[...EvaluateAndReport...]
EvaluateImpact n=2 loc=(11.1,7.8) call=[...EvaluateAndReport...]
ImpactLocation.EvaluateAndReport n=1,2
```

- **正常**：2 发炮弹 → 2 次 EvaluateImpact（每次由 `ImpactLocation::EvaluateAndReport` 调用，堆栈确认）
- 每次 EvaluateImpact 的堆栈：`EvaluateAndReport → il2cpp_runtime_invoke → ImpactTracker::EvaluateImpact`
- 2 发齐射着弹点相同（同瞄准弹道）属正常

---

## 四、已知问题

### 只打两发但着弹/侦察照片多触发（偶发）

| 现象 | 分析 | 状态 |
|---|---|---|
| 用户拉 `.Trigger chain parent` 开火 2 发（FireRequest=2/GunFire=2 正确），但战术地图"着弹好像多触发"、"侦察照片多触发" | 开火事件层正常（2 发）；**偶发**：一次复现 `EvaluateImpact` 3 次（n=1,2 同时同位置=2发齐射 + **n=3 在 5 秒后、位置不同 = 多余一次**），另一次测试只 2 次（无多）→ 属偶发，非稳定 | 🔄 偶发，已加 ImpactDiag + 堆栈，待继续观察 |

**已确认事实**：
- 炮弹创建不重复（`Initialize` 恒 = 实际发射数）
- 着弹评估由 `ImpactLocation::EvaluateAndReport` 驱动（每发 1 次）
- 多余的那次（n=3）在 5 秒后、位置不同 → 疑似**非本次炮弹的着弹**（任务事件/其他系统触发）
- 侦察照片入口 `MapReconClearHandle.RegisterChild`（ReconPhotoSync）在两次测试中**均未触发**——用户看到的"照片"更可能是战术地图落点标记（ImpactMarkerManager），非 ReconPhotoSync

**待办**（若再次复现）：
1. 读 `[ImpactDiag] EvaluateImpact n=3 call=[...]` 堆栈，确认第 3 次调用来源
2. 若为任务/其他系统触发，评估是否需要区分"玩家炮弹着弹"与"系统着弹"（避免照片/标记计数混入）
3. 若与延迟有关（用户提示可能延迟容忍性问题），评估着弹报告是否需按开火序号去重

---

## 五、术语对照（与 INTERACTABLES.md 的关系）

| 用户说法 | 本模块实体 | INTERACTABLES.md |
|---|---|---|
| 着弹 / 落点 | `ImpactTracker.EvaluateImpact` / `ImpactLocation` / `ImpactMarkerManager` | 非交互实体（不记录） |
| 侦察照片 | `MapReconClearHandle`（RegisterChild）/ `ScoutingStrips` | `.Trigger chain parent` 是开火拉环（交互实体），**不是**照片 |
| 反炮兵 | `CounterBatteryCinematicImpactSpawner.SpawnOne` | 非交互实体 |
| 开火 | `GunController.RequestFire/FireShell` | `.Trigger chain parent`（开火拉环，点击被排除走 GunFire 事件） |

> ⚠️ 用户易混点：拉 `.Trigger chain parent` 开火后看到的"侦察照片"在战术地图上，
> 与 `Requisition Console` 的 `.Charge Dial`（补给目标弹舱拨杆）**无关**。
