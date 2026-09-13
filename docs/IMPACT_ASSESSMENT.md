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
>
> **更新记录**：
> - 2026-09-12 照片“拍摄角度/方向不同步”根因定位并修复（见第四节末）：照片角度由游戏 `RandomUIRotation.Awake`
>   在 `Tactical Map/Canvas/MapRoot/---ImpactMarkerManager/ImpactLocation_HCHE(Clone)/Parent` 上**本地随机 roll**
>   （双端实测 `min=5 max=85 axis=Z`，HOST 43.9° / CLIENT 77.9°）；`MapReconClearHandle.RegisterChild`
>   （`ReconPhotoSync` 105）**双端实测从未触发**（Harmony patch 均成功）→ 旧“seed 同步照片内容”对地图上的照片无效。
>   修法：`ImpactSync`（13）新增**由已同步着弹点决定的确定性角度** + 3s 锁定窗口（每 0.15s 断言）。
> - 2026-09-12（同日二次迭代）①照片角度确认同步成功；②诊断 patch（`RandomUIRotation.Awake` /
>   `DraggableItem.Awake`）与 MapTokenSync 照片深度转储**已移除**（根因已定位，去掉全局 patch 与扫描）；
>   ③断言窗口改**缓存式**（着弹时收集目标 + 窗口中途重建 1 次，窗口内零场景扫描）——旧实现每 0.15s
>   `FindObjectsOfType`+`GetComponentsInChildren` → 用户反馈“异常卡顿”；④窗口内**同时断言客机落点标记位置
>   + `TrajectoryTarget.defaultLocalPosition`**（修“追踪器异常不同步”：客机本地着弹评估可能晚于同步包，
>   日志实测包 17:26:39.802 / 本地评估 17:26:41.092 把两者改回了本地值）。
> - 2026-08-23 建文（`[ImpactDiag]` 诊断 + 实测基准 + “着弹多触发”已知问题）。
> - 2026-09-12（同日第三次迭代）**重新设计着弹结果同步（`ImpactSync` 13）**：旧版“定时断言窗口 + 用落点哈希
>   算角度 + 一个坐标套所有标记”作废。新版四个要点：①主机**不改自己的值**（保留游戏随机照片角度），
>   只广播**游戏真实 roll 出的角度 + 自己的落点**；②客机在【本地着弹报告后】与【包到达后】各写一次同一个
>   权威值（幂等）——**无定时窗口**；③按标记配对（本地已报告未配对标记 FIFO + 就近容差 2.0 / 紧容差 1.5），
>   多发炮弹不互相覆盖；④事件保留 TTL 15s **不消费**，客机重复报告同一标记时再次拉正（修“本地评估晚于包”竞态）。
>   协议：`[MsgType][x:float][y:float][tilt:float]`（tilt=-1 表示该标记无照片角度）。
> - 2026-09-12（同日第四次迭代，用户方向“先看随机器怎么写再定方案”）**照片角度改为同步随机种子**：
>   实测 `RandomUIRotation.Awake` 用 **`UnityEngine.Random`** 在 `[minAngle,maxAngle]` 之间 roll
>   （实测 min=5 max=85 axis=Z；`[RurSeed] predicted/actual match=YES` 为验证）。
>   `UnityEngine.Random` 全局可播种 → 在 `Awake` 前用**两端同一个种子**（由两端一致的权威落点派生）`InitState`，
>   游戏自己 roll 出的角度就相同——**不传角度、不事后覆盖、无 1 帧闪烁**；事后恢复全局随机状态。
>   包里的 `tilt` 降为“包还没到”时的兜底。
> - 2026-09-12（同日第五次迭代，用户方向“开火参数明明都是一致的，客机落点怎么会不一致——一直是同一个根源问题”）
>   **定位根因并改为源头修正（治本）**：①文档旧结论“两端用相同开火参数 → 本地模拟必然一致（确定性）”
>   **证伪**——客机开火是收到主机 `GunFire` 后在自己这边重新 `GunController.RequestFire()` **重算一发**，
>   用的是客机**自己那一刻**的本地炮塔角/仰角/装药状态（仰角是物理量、随时间向目标值靠，两端帧时点不同）→ 弹道不同；
>   ②实测（`[ImpactDiag]` + `[TurretSync]` 双端日志）：HOST 开火 17:26:13.983、落点 (18.18,5.43)、飞行 25.62s；
>   CLIENT 复现开火 17:26:14.253、落点 (18.88,5.73)、飞行 26.84s（时长差 ~1.2s≈4%，与射程差 ~4% 相称）；
>   ③**新增 `ShotSync`（动态通道 `shotparams`）**：主机在 `ShellVisual.Initialize` 后广播这发炮弹的
>   **发射参数**（shellId/起点/终点/飞行时长/路径长），客机把参数套用到本地同发炮弹 → **两端弹道一致 → 落点天然一致**；
>   ④落点标记/追踪器/照片角度种子因此自然一致，不再需要“事后把结果改回来”；
>   ⑤诊断：`[ShotDiag] fire`（开火时瞄准/装药状态）+ `[ShotDiag] init`（弹道参数）双端对照。> - 2026-09-12（同日第六次迭代，双端实测日志）**落点差的真正来源入——弹道原点不同**：
>   同一次射击双端 `[ShotDiag] init` 对比：`turretAng/desRot/elev/desElev/dur/dist` **完全相同**，
>   但 `start` 与 `target` **整体平移了同一个固定向量 (-1.100,-0.100)**（连续 4 发完全相同）→
>   即**弹道 = 起点 + 射程×方位**，射程/方位一致、**起点不一致** → 两端弹道是平移关系（不是“参数乱”）。
>   同时 `[NestSync]` 证实铁巢世界位置两端完全一致 → 差异在 `ShellVisual.boardRect`（着弹坐标是 board 本地坐标)
>   与 `GunController.firePoint` 的本地位置（新增 `board/parent/wStart/wTarget/firePointW/firePointPath` 诊断待下一轮定论）。
>   ②本轮修了 `ShotSync` **注册漏传 ChannelKey** 的 bug（主日志 `[Registry] type=0 (dynamic?) class=ShotSync` → 整轮未注册）：
>   修后客机能把主机参数套用到本地同发炮弹 → **落点直接一致**（不再靠事后改结果）。
>   ③停用“同步随机种子”方案（双端 `[RurSeed]` 零日志 = 从未生效），改为**逐张照片角度日志**（`[ImpactSync] photo HOST/CLIENT`）定位“所有照片同一个角度”。
>   ④新增已知问题：**客机漏开火 = 膛内无弹**（见第四节）。
> - 2026-09-12（同日第七次迭代）**弹道起点根因锁定 + 修复**：`[ShotDiag] init/fire` 新诊断一次命中——
>   两端 `board='---ImpactMarkerManager@-1.058,-0.816,-14.105s(0.212...)'`、
>   `turretAng/desElev/dur/dist` 全相同，唯一差的是 **`firePointPath='Tactical Map/Canvas/MapRoot/TurretLocation'`
>   的世界位置**（HOST `(-0.561,-0.772,-14.689)` vs CLIENT `(-0.730,-0.787,-14.434)`）——
>   即**炮弹起点 = 铁巢在地图上的图标 `TurretLocation`**（`GunController.firePoint`），
>   客机这个图标**两局都停在场景默认 local (1.550,1.550) 从未被摆位**（主机则跟着真实铁巢）。
>   ②修法：`NestSync`（146）**随铁巢一并主机权威同步该图标的 localPosition/localRotation**（同包附加块） →
>   两端弹道起点一致 → **整条弹道一致**。（2026-09-12 晚收敛：同步方式由“1.5s 心跳重发”改为
>   **变化检测 + 缓存**——0.5s 只做本地读值/比较，*变了才发*；因不再有周期重发兜底，146 已登记为
>   **关键包**（合包丢弃不丢）+ 中途加入 `OnLateJoin` 显式补发一次。见 `docs/INTERACTABLES.md` 版本记录）
>   ③`ShotSync` 注册 bug 修复后已验证生效（`[ShotSync] client apply via=recv/init`，客机 target 被套用为主机值）。
>   ④用户确认：**照片角度已正常**；**客机漏开火**是外部测试模组导致（买弹药没同步），非本模组问题。
> - 2026-09-12（同日第八次迭代，用户：“追踪器现在不用单独同步了，只保留强制同步的方法给中途加入用”）
>   **追踪器不再随每次着弹同步**：铁巢地图图标（弹道起点）已同步 → 两端弹道一致，追踪器由落点自然驱动
>   （写 `TrajectoryTarget.defaultLocalPosition` 反而与 follower 争抢）。
>   实现：`ApplyAuthoritative`/`OnHostEvent` 移除 `WriteTrackTarget` 调用；`ImpactSync` 协议加 `force` 字节
>   （`[x][y][tilt][force]`，缺字节=0 兼容旧包）；**仅中途加入**时主机 `OnLateJoin(steamId)` 单播最近一次着弹
>   （≤300s）且 `force=1` → 客机强制对齐追踪器一次。
>   注：落点标记位置 + 照片角度（tilt）仍照常同步（现在正常：落点两端已一致，写入是幂等的）。
> - 2026-09-12（同日第九次迭代，用户：“还是丢了一发炮弹，检查一下是不是模组直接拦截了短时间多发炮弹落地”）
>   **实测：模组没拦截，是客机膛内无弹**。日志（一次三连发）：HOST 3 发均 `chambered=Y` 出弹；CLIENT 收到 3 个
>   `OnGunFire` 且 3 次 `RequestFire(CLIENT)` 都执行了，但第三发（GunRight）`chambered=N` → `FireShell` 不触发
>   → 客机少一发（无炮弹/无落点标记/无照片），日志行 `[TurretSync] CLIENT chamber EMPTY at fire`。
>   因 `ChamberedShellBlueprint` 是**只读**（模组无法给客机“装弹”），改为主机权威**直接复现这发炮弹**：
>   `ShotSync` 新增兜底——主机发射参数到达后 `SpawnDelay=1.2s` 仍未配到本地炮弹 → 用 `Resources.FindObjectsOfTypeAll`
>   按 `ShellId` 解析 `ShellDefinition` + 取 `ShellBlueprint.shellVisualPrefab` + 父对象 `TurretLocation` 同级的
>   `---ImpactMarkerManager` → `Instantiate` + `Initialize(主机 start/target/dur)` → 客机照常出弹/着弹。
>   日志：`[ShotSync] client SPAWN shell='..' start=(..) target=(..) dur=..`（`shot.spawn`）；任一步失败则
>   `SPAWN skipped: ...` 跳过（不改变现有行为）。
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
| **炮弹发射参数**（起点/终点/飞行时长/路径长） | 主机权威（**2026-09-12 新增，治本**） | ShotSync（动态通道 `shotparams`） | 主机 `ShellVisual.Initialize` 后广播这发炮弹的参数；客机按**起点就近配对**并套用到本地同发炮弹（两端同一条弹道）|
| **落点标记位置** | 主机权威（事件同步） | ImpactSync（13） | 主机 `PostEvalReport` 排队广播，客机在【本地报告后】+【包到达后】按标记配对写入（无定时窗口） |
| **照片角度（拍摄角度/方向）** | 主机权威（**同步随机种子**） | ImpactSync（13） | 两端在 `RandomUIRotation.Awake` 前用同一权威落点派生的种子 `InitState` → 游戏自己 roll 出同值（`[RurSeed]` 验证用的是 `UnityEngine.Random`）；包里的 `tilt` 仅兜底 |
| **侦察照片** | seed 同步（**实测未生效**） | ReconPhotoSync（105） | `MapReconClearHandle.RegisterChild` prefix → 拍照前统一随机 seed；⚠️ 该入口**双端实测从未触发**，地图上的照片不由它创建 |
| **反炮兵落点** | seed 同步（落点一致） | CounterBatterySync（103） | `CounterBatteryCinematicImpactSpawner.SpawnOne` prefix → 统一 seed → 两端一致落点 |
| **任务随机 seed** | seed 广播（keepalive） | MissionSync（102） | 任务生成固定 seed → 两端任务实体/目标一致 |
| **任务实体** | 状态快照同步 | EntitySync（104） | MapEntity（敌人/炮兵/任务目标）位置/状态/存活，缺实体 CreateMapEntity 补齐 |

**关键结论（2026-09-12 修正）**：炮弹飞行 + 着弹评估是**每端本地模拟**，但**“两端开火参数相同”这个前提是错的**：
客机开火是收到主机 `GunFire` 事件后在自己这边重新 `GunController.RequestFire()` **重算一发**，用的是客机
**自己那一刻**的炮塔角/仰角/装药（仰角是物理量、随时间向目标值靠，两端帧时点不同）→ 弹道不同 → 落点不同。
故**炮弹发射参数改由 `ShotSync` 主机权威下发**（见上表第一行），**在源头**保证两端同一条弹道；
侦察照片/反炮兵因用 `UnityEngine.Random` 程序化生成，才需要 seed 同步，

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

### 客机漏开火（膛内无弹，2026-09-12 🔄 待修）

**现象**：用户“客机好像漏了一次开火”。实测一致：主机 4 次 `FireShell fired` / 客机只 2 次。

**已确认事实（双端 `[ShotDiag]`/`[TurretSync]` 日志）**：

| 时刻 | 主机 | 客机 |
|---|---|---|
| 17:55:33 | `RequestFire chambered=Y st=0` → 出弹 | `chambered=Y st=0` → 出弹 ✅ |
| 17:57:59 | `chambered=Y` → 出弹 | **`chambered=N st=0`** → `FireShell` 不触发 ❌ |
| 17:58:13 (GunRight) | `chambered=Y` → 出弹 | **`chambered=N st=0`** ❌ |
| 18:00:56 | `chambered=Y` → 出弹 | `chambered=Y st=7` ✅ |

**根因**：客机复现开火走 `gun.RequestFire()`，而 **`FireShell` 在膛内无弹时不会触发**（`ChamberedShellBlueprint == null`）→
该发在客机侧**压根没有炮弹** → 无 `ShellVisual.Initialize`、无着弹评估、无落点标记/照片。
客机装填状态机与主机**相位不同**（客机 `st=0`/`st=7` vs 主机恒 `st=0`），且 `[ReloadSync]` 日志显示客机大量
`recv state ... apply=0`（未应用主机装填状态）+ 主机 `host recv cmd idx=0 st=5 ch=0`（客机上行了一个 ch=0 的装填命令）。

**已加诊断（本轮）**：客机膛空时 `[TurretSync] CLIENT chamber EMPTY at fire → FireShell 不会触发（本发无炮弹）st=..`
（把“漏发”变成显式日志）；下一步根据该日志 + `[ReloadSync] apply`/`[CylinderActionSync]` 时序定位是哪一步没同步。

### 客机落点与主机不一致（2026-09-12 🔄 已改源头，待复测）——“一直是同一个根源问题”

用户质疑：“客机的落点怎么会不一致呢，开火参数明明都是一致的，其实一直都是同一个根源问题导致的，一直治标不治本。”

**已确认事实（2026-09-12 双端日志实测）**：

- 同一次射击：HOST `RequestFire` 17:26:13.983 → `FireShell` → 落点 `EvaluateImpact loc=(18.18,5.43)`（17:26:39.603）；
  CLIENT 收到事件后复现 `RequestFire` 17:26:14.253 → `FireShell` → 本地落点 `loc=(18.88,5.73)`（17:26:41.092）。
- 两端**飞行时长差 ~1.2s（25.62s vs 26.84s ≈ 4%）**，与落点差 ~0.8 / 射程 ~19 ≈ 4% **相称** →
  典型的“发射参数小幅不同”，不是丢包/坐标系/量化问题。
- 客机开火路径：`PreRequestFire` 上行请求 → 主机 `RequestFire`（主机权威开火）→ `GunFire` 广播 →
  客机 `EventLayer.FireGunFromEvent` → `gun.RequestFire()`（**用客机自己的状态重算**）。
- 游戏弹道由 `ShellBlueprint`（`currentPowderCharge` + `ShellDefinition` → `GetAdjustedShellSpeed()`/
  `GetRangeForCharge()`）+ 炮塔角 + **物理仰角**（`GunController.CurrentElevation`/`ElevationErrorDeg`，
  随时间向 `DesiredElevationAngle` 靠）决定；模组同步的是**期望/控件值**，不是“开火那一刻的物理仰角”。

**根因（2026-09-12 第七次迭代，已确认）**：两端各自用**自己那一刻的本地物理状态**重算弹道。
进一步实测锁定：`board`/`turretAng`/`desRot`/`elev`/`dur`/`dist` 两端**完全相同**，唯一不同的是
**炮弹起点**（`GunController.firePoint` = `Tactical Map/Canvas/MapRoot/TurretLocation` 铁巢地图图标）——
客机该图标停在**场景默认 local (1.550,1.550)**，主机则跟着真实铁巢；
`start` 与 `target` 相差**同一个固定向量**（一局 (-1.100,-0.100)，另一局 (-1.200,-0.800)）
⇒ 两端弹道是**平移关系**（不是“参数乱”）。下游（落点标记、追踪器、照片角度种子）全是这个落点生的。

**修法（治本，2026-09-12 已实现待复测）**：①`NestSync`（146）把铁巢图标 `TurretLocation` 与铁巢同位
主机权威同步（同包附加块；同步方式 = **变化检测 + 缓存，变了才发**，非心跳重发）→ 两端**起点一致**；
②`ShotSync`（动态通道 `shotparams`）作保险：主机下发该发炮弹的 `target/dur/dist`，客机套用（已验证生效）；
③验证通过后可删掉 `ImpactSync` 里“把本地落点/追踪器改回主机值”的事后纠正。

**修法（治本，2026-09-12 已实现待复测）**：`ShotSync`（动态通道 `shotparams`）——
主机在 `ShellVisual.Initialize`（postfix）后广播这发炮弹的 `shellId/start/target/dur/dist`；
客机在自己创建同发炮弹时（或参数稍后到达时）**把参数套用到本地那一发**（``targetLocalPos``/`travelTime`/
`totalPathDistance`/`startedAt`/`endsAt`），按**起点就近配对**（同一门炮两端起点几乎重合，双炮齐射靠起点区分左右）。
→ 两端炮弹沿同一条弹道 → `EvaluateImpact` 拿到的 loc 天然一致。

**复测看点**：`[ShotDiag] init` 的 `target`（两端应**完全相同**）、`[ImpactDiag] EvaluateImpact loc`（两端应相同）、
`[ShotSync] client apply via=… hostTarget=(…)`。
**验证通过后**：删除 `ImpactSync` 里“把本地落点/追踪器改回主机值”的那段事后纠正代码（只留照片角度 seed 同步）。

**诊断手段**（2026-09-12 新增，定位根因用；根因确认后可删）：

| 日志 | patch 方法 | 含义 |
|---|---|---|
| `[ShotDiag] fire role=… shell=… speed=… turretAng=… desRot=… elev=… desElev=… elevErr=… range=… powder=…` | `GunController.FireShell` postfix | 开火那一刻的**两端**瞄准/装药状态（对比“开火参数是否真的一致”）|
| `[ShotDiag] init role=… shell=… start=… target=… dur=… dist=…` | `ShellVisual.Initialize` postfix | 这发炮弹的**实际弹道参数**（两端是否相同）|

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

### 照片"拍摄角度/方向"不同步（2026-09-12 ✅ 已修）

| 现象 | 分析 | 状态 |
|---|---|---|
| 落点/追踪器同步后，地图上的照片两端"拍摄角度/方向"不同（照片卡位置一致） | 双端诊断实测同一张照片：HOST `RandomUIRotation.Awake on 'Parent' … min=5 max=85 axis=Z lr=(-0,0,43.9)`；CLIENT 同一时刻同一对象 `lr=(-0,0,77.9)` → 角度由游戏**本地随机 roll**，两端必然不同 | ✅ 已修（`ImpactSync` 确定性角度 + 3s 锁定窗口） |

**已确认事实（2026-09-12 双端日志 + 离线资源解析）**：

- 照片对象 = `Tactical Map/Canvas/MapRoot/---ImpactMarkerManager/ImpactLocation_*(Clone)`；"拍摄角度"挂在它的子对象
  `Parent` 上的 `RandomUIRotation`（实测 `min=5 / max=85 / axis=Z`）——游戏在 `Awake` 用 UnityEngine 内部随机 roll。
- 地图上的照片**令牌**是 `MapToken_Recon`（`level0` 场景里**预置 10 个**，结构与 `MapToken_Artillery` 完全相同：
  `DraggableItem`+`Interactable` + 子对象 `Text (TMP)`/`Mesh`/`SFX_grab`/`SFX_release`），由 `MapTokenSync`（119）
  同步 `localPosition`/`localEulerAngles`（逐 id 实测两端完全一致，含子对象 `Mesh` 的 `r=(0,0,90)`）。
- `MapReconClearHandle.RegisterChild`（`ReconPhotoSync` 105 / 旧 `PostRegisterChild` 关 `RandomUIRotation`）
  **双端实测 0 次触发**（Harmony patch 均成功）→ 对地图上的照片不生效。
- 相关游戏资产线索（`tools/find_prefab.py` / `list_go.py` / `dump_prefab.py`）：`SaftySwitch delete recon photos`
  （删除侦察照片的保险开关）、`resources.assets` 的 `Photo Overlay`、`sharedassets0.assets` 的
  `FMOD new photo SFX tm_map_reveal`（"new photo" 音效）。
- 参考实现：官方联机的反编译参考 `ImpactPhotoBearingBridge.cs`（patch `RandomUIRotation.Awake`
  + 按着弹格匹配主机四元数 + 12×0.1s 反复写 `localRotation`）与 `ReconArtifactBridge.cs`（传 PNG 纹理块）。

**修法（`ImpactSync`，MsgType=13）**：角度不再各自随机，改为**由已同步的着弹点 (x,y) 算出的确定性值**
（`DeterministicTilt`：float 位 → FNV 混合 → 映射到组件的 `[minAngle,maxAngle]`；主机用本地坐标、客机用广播坐标，
位相同 → 角度必然相同），并在着弹后 **3s 断言窗口**内每 0.15s 写一次。不新增网络包、不改/不禁用游戏组件。

**（旧版，已被 2026-09-12 重新设计取代）** 性能：断言目标（`RandomUIRotation` 变换 / 客机侧落点标记 / 追踪目标）在**着弹时一次性收集**，
窗口中途（≈0.6s）重建 1 次；窗口内**只写 transform**（零 `FindObjectsOfType`/`GetComponentsInChildren`）。
日志 `[ImpactAssert]` 每发一条（`loc` / `tiltTargets` / `angle` / `markers` / `trackTargets`），便于核对两端角度一致。

**（旧版，已被重新设计取代）** 追踪器（同一窗口）：客机本地着弹评估可能**晚于**同步包（日志实测：包 17:26:39.802 / 本地评估 17:26:41.092），
本地评估会把落点标记与 `TrajectoryTarget.defaultLocalPosition` 改回本地值 → 追踪器不同步；
故断言窗口内（仅客机）一并拉正这两个。

**诊断入口**（⚠️ 2026-09-12 已移除：`[PhotoAngle]`/`[PhotoDeep]`/`[PhotoClear]`/`[PhotoCreate]` 与对应 Harmony patch
——根因已定位，去掉全局 patch 与扫描；现只留 `[ImpactAssert]` 每发一条）：

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
