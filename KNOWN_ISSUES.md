# 已知问题（Known Issues）

> 未修复/待验证问题清单，按优先级分级。更新于各轮测试后。

## 优先级定义
- **T0 严重**：影响核心玩法（装填/发射/开火），必须优先
- **T1 高**：电源/灯光/动力系统
- **T2 中**：战术地图桌 / 标记 / Token
- **T3 低**：其他（猫、唱片等次要交互）

---

## 联机菜单 / 聊天输入（T3）

### IME 中文输入 — 最终方案（2026-08-11，待验证）
- **现状**：移除 `onTextInput` 监听器 + `OnTextInput` patch（native 调用乱码/崩溃：字母前方框、闪退；且与物理键双通道重复跳字）。
- **最终方案（"最简可靠"）**：
  - **英文/数字/符号/空格**：`PollInput` 物理按键直接处理（`kb.aKey` 等具名属性 + `AppendKey`），带 shift 大小写。可靠、无 native 调用。
  - **中文**：`Harmony patch Keyboard.OnIMECompositionChanged`（`PostImeComposition`）——IME 组合提交时组合串含 CJK 字符（>0x2E7F）则逐字符转发给 `CoopUIManager.OnImeText`。
  - `EnsureImeRegistered` 已删除（不再 add_onTextInput）；`SetImeActive` 保留（输入态切换时 `Keyboard.SetIMEEnabled`）。
- **已知边界**：Backspace 仅删单个 UTF-16 码元，CJK 是 BMP 内单码元（1 字符=1 码元），中文退格正常；组合输入过程中的拼音字母不转发（只转发最终提交的 CJK）。
- **待用户验证**：① 英文输入无方框/无双字；② 中文 IME 可输入；③ Enter 取消聚焦。

---

## T0 — 装填 / 发射（核心玩法）

### T0-1 装填状态跳步 / 装填不同步
- **方案演进（2026-08-09 第 4 版，已稳定 ✅）**：
  1. ~~增量推进事件~~ → 两端动画时序不同跳步/错位。
  2. ~~纯快照（写 currentStateIndex）~~ → 状态数字对齐但拉杆/动画不动。
  3. ~~快照写索引（含严重偏离纠正）~~ → **写 currentStateIndex 触发游戏状态机自动推进**（自动上膛、锁膛后退膛重置）。
  4. **当前方案（纯事件驱动，已确认装弹全链路正常 ✅）**：
     - patch `LookAtTarget.OnClickDown`（拉杆/按钮点击）→ 对端 `OnClickDown+OnClickUp` 完整复现（动画+逻辑+推进）。
     - patch `PowderChargeController.OnChargeButtonPressed`（选药量）+ `OnLoadChargesPressed`（投放）→ PowderEvent 转发。
     - **完全移除快照写 currentStateIndex**（只同步药量 currentSelectedCharges）——写索引会触发游戏状态机自动推进。
     - `IsApplyingClick`/`IsApplyingPowder` 防环。
- **已确认工作 ✅**：上弹头（Load shell Rammer）→ 选药量（Button Dispencer）→ 投放（Charge Rammer）→ 上膛 → 锁膛，**客机/主机装弹全链路正常**（`click/applied click` + `powder select/load apply` 日志齐全）。

### T0-2 装药拉杆 / 塞装药不同步（已解决 ✅）
- **事件链**（IL dump 确认）：`LookAtTarget.OnClickDown` → onClickDown UnityEvent → `OnLoadChargesPressed`（投放）+ AnimatorBoolToggler（动画）。
- **已改**：`OnClickDown` 转发（拉杆视觉）+ `OnChargeButtonPressed`/`OnLoadChargesPressed` patch（选药量/投放逻辑）。`OnPowderEvent` 应用时**模拟点击按钮**（Button Dispencer(N)/loadChargesButton）→ 对端按钮动画+逻辑完整复现。
- **已确认工作 ✅**。

### T0-3 仰角锁止拉杆（待发射/挂发射拉绳）（已解决 ✅）
- **已定位**：`Universal Button Arm Left` / `Arm Right`（左/右炮仰角锁止拉杆）。`ButtonClickSync` 已加 `Arm` 关键词。
- **已做**：`GunLinkSync` 同步 `GunElevationLinkCoordinator.isLinked`。
- **待验证**：拉 Arm 拉杆对端是否跟随。
- **已确认工作 ✅**。

### T0-4 切换弹药种类拉杆物理位置不同步（已解决 ✅）
- **已确认工作 ✅**（用户确认）：切弹 `Universal Button Move Cylinder` 点击同步；`ShellSync` 主机权威弹舱同步（含全 NULL + 1.5s 心跳，client 不广播）。

### T0-5 第一发炮弹直接装填（已解决 ✅）
- **待办**：确认是弹种应用（ShellSync）触发还是游戏自身装填逻辑；必要时应用弹种后不推进装填状态。

---

## T1 — 电源 / 灯光 / 动力

### T1-1 主电源拉杆物理位置不同步（已解决 ✅）
- **已定位**：主电源拉杆 = `Universal Switch Button Variant`（路径 `Turret/Office Corner/Power Box/Power Lever Parent/Power Lever/...`，LookAtTarget + 多个 AnimatorBoolToggler）。
- **已做**：`ButtonClickSync` 加 `Switch` 关键词（点击同步）；`M3EnvSync` 同步 `engine/running`（`DieselEngineController.EnginesRunning` + `AttemptIgnition`/`ShutdownEngine`）。
- **待验证**：拉主电源拉杆 + 启动引擎，两端是否来电。
- **已确认工作 ✅**。

### T1-2 开局停电（已解决 ✅）
- **现象**：开局两端永远停电（红光）；`env/engine/running` 广播 v=0。
- **根因（日志铁证）**：host.log 持续 `cmd recv kind=2 id='env/engine/running' v=0` —— **client 端引擎 Getter 开局读到 false（场景未加载完）→ 上行 v=0 → host 应用 `ShutdownEngine()` 把主机引擎关了**。单机没有 client 上行所以来电正常。
- **修复（已改）**：`env/engine/running`/`env/pressure`/`env/cbattery/*` 全部 `ClientNoSend=true`（**主机权威，client 只接收不上行**）。引擎开局来电不再被 client 上行破坏。**撤销了此前的"开局自动启动引擎"错误方案**（有的关卡开局本就该没电，自动点火破坏关卡设计）。
- **待验证**：开局两端是否来电（engine v=1）。
- **已确认工作 ✅**。

### T1-3 多人模式「失焦暂停」（已解决 ✅）
- **现象**：游戏切出去（虚拟机/Alt-Tab）自动暂停，联机时时间冻结。
- **根因**：游戏 `PauseManager.OnApplicationFocus(false)` + `PauseOnFocusLoss` 实现失焦暂停。
- **修复（已改）**：patch `PauseManager.OnApplicationFocus`（联机失焦跳过原方法 + 恢复 timeScale=1）+ 进入会话时 `PauseOnFocusLoss=false` + `Application.runInBackground=true`。
- **已确认生效**：日志 `[CoopBehaviour] 已移除失焦暂停`。
- **补充（自动加入时暂停，2026-08-11）**：autojoin 期间 `RequestGlobalPause`（防加入干扰），成功/失败 `ReleaseGlobalPause`（见 T3-10）。

---

## T2 — 战术地图桌 / 标记 / Token

### T2-1 地图桌三个按钮不同步 
- **现象**：重置击杀令牌、删除所有战术地图测量、重置标记令牌三个按钮未同步。
- **已做**：`ButtonClickSync` 关键词含 Reset/Delete/Measure/Kill；检测方式已从 isClicked 轮询改为 **OnClickDown patch**（不再有失效对象漏检）。
- **待办**：验证这三个按钮（Reset/Delete/Measure 关键词）是否被 `OnClickDown` 捕获。

### T2-2 MapToken 拖动 / T/F/S1-10 / 位置同步
- **已确认正常 ✅**（用户确认）：Token 全系统（编号+路径组合区分同名、首全量对齐+变化广播）。

### T2-3 铁巢 Token / 杀伤范围标盘位置
- **已确认正常 ✅**（用户确认）。

### T2-4 Token 初始位置错位
- **已确认正常 ✅**（用户确认）：首全量对齐已解决。

### T2-5 地图标记擦除不同步（已修复待验证）（已解决 ✅）
- **现象（用户反馈）**：地图上的标记**擦除**时没有同步（只同步了放置，擦除后对端还留着）。
- **根因**：`MapMarkerSync`（活跃，MsgType=107）只订阅 `OnMarkerFinalized`（放置），**无擦除同步**。
  旧 `MapSync`（处理 MapMarkerRemove=23）未注册到 CoopRuntime → 不运行。
- **修复（MapMarkerSync）**：
  - 107 消息加**标志字节**：0=添加/拖拽，1=删除
  - `DetectLocalErase()`（Tick 轮询）：检测 `_markers` 中 GameObject 被销毁（Unity 假 null）
    → 广播删除 `SendRemove(id)`
  - `RemoveMarker(id)`：对端 Destroy marker + 从 `_markers`/`placedMarkers` 移除（`_applyingRemove` 防环）
- **注意**：标记快照（BuildMapMarkerSnapshot）格式独立（n + kind+...），不受标志字节影响。
- **待办**：实际擦除标记验证对端同步移除。
- **已确认工作 ✅**。

### T2-6 免虚拟机双开自动联机（已实现待验证）（已解决 ✅）
- **需求（用户反馈）**：测试太麻烦（要虚拟机），想直接**带参数启动两个客户端自动联机**。
- **实现**：
  - `Net/AutoJoin.cs`：解析命令行参数 `--autohost` / `--autojoin` / `--autolobby <file>`
  - host：Steam 就绪后自动 `CreateLobby()`，建房成功把 lobby id 写共享文件（`%TEMP%\open_nest_lobby.txt`）
  - client：读共享文件 lobby id → 自动 `JoinLobby()`
  - `CoopRuntime.Startup` 解析参数；`NetManager.Update`（SteamReady）触发；`OnLobbyEntered`（host）写文件
  - `scripts/dualtest.ps1`：一键启动 host（--autohost）+ client（--autojoin）
- **本地回环模式（新增，免 Steam）**：`--local host` / `--local join` / `--localport <n>`
  - `ITransport` 抽象：SteamTransport（P2P）+ LocalTransport（TCP 回环 127.0.0.1）
  - NetManager 支持 LocalMode：绕过 Steam 就绪/大厅，host 监听 TCP、client 连本地端口
  - 同一机器两个游戏进程可直接 TCP 通信，**无需两个 Steam 会话**（开发测试用，
    延迟/中继行为与真实 Steam P2P 不同）
  - `dualtest.ps1 -Local`：启动 host（--local host）+ client（--local join）
- **限制（Steam 模式）**：Steamworks **不允许同 AppID 两个进程共用一个 Steam 会话**
  → 需两个 Steam 客户端/账号。**本地模式（--local）无此限制**（不经 Steam P2P）。
  - **已确认工作 ✅**。

---

## T3 — 其他

### T3-1 客机小猫同步（v2 软同步 + v3 活动类型同步）
- **现象（旧）**：客机小猫卡在角落抽搐（插值问题）；客机无法与猫交互（拾取等）。
- **v2 重构（用户方案：主机 AI 权威决策 → 软同步 → 各自执行）**：
  - 主机权威 AI：每 1/3s 广播每只猫 AI 决策（CatState + NavMesh 目标点 + 权威位置，MsgType=106）
  - 客机软同步执行：收到主机运动指令 → `SetDestination(目标点)` 走同一目标 → 两端路径一致
  - 交互事件（MsgType=133）：拾起/放下/驱赶/抚摸/打断，谁操作谁发，对端执行
    （StartCarrying/StopCarrying/ShooCat(false)/PetTheCat/InterruptCat，`IsApplyingCat` 防环）
  - 硬同步兜底：客机本地猫位置 vs 主机权威位置偏差 > 1m → Teleport 对齐（每 1/3s 检测）
  - 客机拾起的猫位置上行（0xFF held 列表，玩家持有表现，非 AI 信息）
  - Harmony patch：`CatPickUpHandler.ExecutePickUp/ExecuteDrop` + `CatController.ShooCat/PetTheCat/InterruptCat`
- **v3 活动类型同步（2026-08-11，走路✅ 已确认，趴下休息待验证）**：
  - **根因**：PerformingActivity 有**多种随机活动**（趴下/梳理/玩耍），由 `PickRandomActivity()` 用全局 Random 选——两端各自选会不同（走路/位置同步了但趴下休息不同）。
  - **修复（CatSync）**：广播增加活动类型字段——`loopEndTrigger`（活动循环结束触发，活动标识）+ `_isLoopingActivity` + `_afterLoopActivityDuration`；客机 `ApplyHostAI` 在 PerformingActivity（state==2）时同步这些字段 → 状态机 `HandleActivityState` 播放主机同一活动。
  - **v3.1 活动动画同步（2026-08-11 增强）**：仅同步活动字段仍不够（客机 HandleActivityState 可能已用本地活动）——广播**主机 animator 当前状态**（`_agentAnimation._animator.GetCurrentAnimatorStateInfo(0)` 的 `shortNameHash` + `normalizedTime`），客机 PerformingActivity 时 `anim.Play(hash, 0, time)` 播放主机同一动画 → 趴下/梳理等具体动作一致。
  - 状态变化（cur != state）时强制覆盖 `_currentState`（走路同步关键，只状态变时写不闪烁）+ 同步 `_activityTimer`/`_currentActivityDuration`（切换时机一致）。
  - **v3.2 走路动画同步（2026-08-11）**：走路（state>=1）也同步 animator（Play 主机同一动画）——之前只对活动（state==2）同步动画，走路动画两端不一致（客机本地 AI 争抢）。
  - **v3.3 硬同步 Warp 修复（2026-08-11）**：硬同步从 `transform.position=` 改为 `NavMeshAgent.Warp` + 重设目标=当前权威位置——原来只改位置不改 agent 路径 → agent 瞬间拉回 → 每 1/3s 反复 teleport 抽搐。Warp 后频率从每 0.35s 降到几十秒一次。
  - **v3.4 抱起/放下修复（2026-08-11）**：放下事件曾**不触发**（`ExecuteDrop` patch 了但游戏放下不走它）→ 客机猫一直 Carried → 状态冲突抽搐。**修复**：patch `CatController.StopCarrying`（游戏放下最终都调它）→ 广播放下（ev=2）。另：state==4（Carried）时**跳过硬同步**（被抱着时位置由持有端控制，追不上会抽搐）。
  - **已确认工作 ✅**（2026-08-11 用户确认）：走路/活动/趴下/抱起放下/不抽搐 全部正常。

### T3-2 仰角拉杆（Elevation Lever）被 GunElevationLink 锁定（已解决 ✅）
- **现象**：仰角拉杆物理值同步了但视觉/逻辑被游戏高压联动系统锁定（SetSliderValue 无效）。
- **待办**：在联动激活时走联动逻辑（leader/follower）而非直接设滑块。
- **已确认工作 ✅**。

### T3-3 弹道计算机持续响声（波动装药量摇杆/压力）（已解决 ✅）
- **已确认解决 ✅**（用户确认）：Binding 加 `NoHeartbeat`，Charge/PressureSystem/Magazine/Ballistic 控件跳过心跳。

### T3-4 方向角曲柄 / 仰角曲柄不同步（根因已定位）（已解决 ✅）
- **现象**：客机操纵杆（joy）调仰角/方向角**同步 ✅**（TurretSync DesiredRotation/Elevation 通道）；但**曲柄**：仰角会同步但**松手回退**，方向角**完全无效**。
- **根因 A（仰角曲柄恒 0 / 回退，日志铁证）**：host 仰角曲柄 `av=1.68→31`（在转），client 恒 `av=0`。因为 `abf9440` 的 `ClientNoApply` 匹配 **`"Spur Gear"`**——方向角 AND 仰角曲柄**都叫 "Spur Gear 12 DRIVER"**，被**误伤**：client 收到仰角曲柄值时不应用 → 客机仰角曲柄恒 0 → 回退。
  - **修复（已改）**：`ClientNoApply` 只匹配 **`"Rotation Console"`**（方向角），仰角曲柄恢复双向值同步。
- **根因 B（方向角曲柄值同步但炮塔不转）**：`abf9440` 把方向角曲柄设 `ClientNoApply` + `TurretSync` 接管，但 `26ec3eb` 又移除 Gunner 跳过 → 曲柄→炮塔链路被打断。**待验证**：方向角曲柄是否仍需 `ClientNoApply`，或应恢复健康版本的双向 ValueSync（host 应用曲柄值 → host 炮塔跟转）。
- **根因 C（仰角拉杆回退）**：client 应用 `SetSliderValue` 被 GunElevationLink 锁定无效 → client 本地值 0 → 上行 v=0 → host 拉杆被拉回 0（见 T3-2）。
- **待验证**：改后方向角/仰角曲柄、仰角拉杆是否同步、松手不回退。
- **已确认工作 ✅**。

### T3-5 炮塔 Lever/Gear 同步重构（用户拍板：只同步 Lever/Gear 位置 + 事件，谁操作谁权威，抛弃状态插值）（已解决 ✅）
- **根因（双轨打架）**：`TurretSync` 状态插值每帧覆盖 `CurrentAngle`/`SetDesiredElevation`，与 `ValueSync` 的 Lever/Gear 值同步冲突 → 曲柄无效/回退/不同步。
- **新架构（用户 2026-08-10 拍板）**：
  1. **只同步 Lever/Gear 位置值**（谁操作谁权威，isDragging busy 本地优先）——两端位置一致 → 游戏本地逻辑驱动炮塔一致（动画+数值天然一致）
  2. **抛弃状态插值**：TurretSync 只保留开火事件（OnLocalGunFired/OnGunFire），移除 SendState/OnState/ApplyInterpolated/SendAimInput/OnAimInput/IsLocalTurretInputActive
  3. **30Hz 高频**：ValueSync per-binding HighFreq（仅炮塔 Lever/Gear + Chain 30Hz，其余 0.2s + 插值省带宽）
- **NetManager**：移除 TurretSync.Tick + TurretState/TurretInput 消息处理（不再发送）
- **已确认工作 ✅**。

### T4-2 任务内容随机（种子同步，已实现待验证）
- **现象**：每次任务内容有随机（目标位置/数量等），两端不一致。
- **根因**：任务内容由随机种子生成，种子未同步。
- **修复（MissionSync）**：主机广播 seed（MissionManager.seed，反射读）；对端 `useFixedSeed=true + fixedSeed=seed` → 两端任务内容随机一致。
- **回归修复（2026-08-11）**：主机 GetSeed 读 FireMission.seed 恒 0/不可读 → 每 0.5s 重新生成新 seed（`GenerateHostSeed` 用 TickCount）→ 广播 seed 持续变化 → 客机 fixedSeed 被反复覆盖 → 目标生成后 seed 又变 → 任务目标不同步。
  - **修复**：`GetSeed` **优先返回已记住的 `_hostSeed`**（静态字段，主机生成后记住、OnPacket 也记住）→ 一旦生成不再变化 → 任务目标稳定。
  - **待验证**：任务目标（位置/数量/照片）两端一致且不再随时间漂移。

### T4-3 人物临时模型朝向反了（已修复待验证）（已解决 ✅）
- **现象**：后脑勺对着人。
- **根因**：化身朝 pose.Yaw（玩家朝向）但 gasmask 面罩保留 180 翻转 → 脸朝 -Z 背对玩家朝向。
- **修复**：移除 180 翻转（identity），面罩正面朝 pose.Yaw（对应玩家朝向）。
- **已确认工作 ✅**。

### T4-4 发射台 Switch button 事件同步被吞（已修复待验证）
- **现象**：发射台几个按钮（Check Switch / Universal Switch Button）靠事件同步，可能被吞/状态不一致（灯开关已确认同步 ✅）。
- **修复（ButtonClickSync）**：
  - pending 队列同 id **去重合并**（保留最新，避免 Switch 状态开关被过期点击反转）
  - 超时丢弃加日志（防堆积）
  - toggle 型按钮（AnimatorBoolToggler）：**不合并丢弃**（快速连点逐一应用，丢一次状态就反）
  - **单 toggler 按钮（灯/SaftySwitch 等简单开关）**：点击复现后强制 `SetBool(权威最终值)` 对齐
  - **多 toggler 按钮（楼梯盖板手柄等瞬时拉杆）**：不做立即 SetBool（避免打断动画），改由**状态轮询**（MsgType=135，见 T3-8）校正回弹后最终状态
  - **瞬时回弹型单 toggler（发射控制台 Switch，delay>0）**：跳过立即 SetBool（避免在动画播放中强制设值 → 对端回弹），由状态轮询校正最终状态
- **说明**：Switch 是状态保持开关，事件同步 + 状态开关语义下，去重合并保证快速连点只应用最后状态。

### T4-5 SaftySwitch 安全开关同步（已增强待验证）
- **对象**：战术室 `SaftySwitch (N)`（LookAtTarget + AnimatorBoolToggler + LookAtTargetEventRelay 拨动开关）。
- **现象（用户反馈）**：这个开关也要同步（拨动后对端应看到同样状态/触发同样逻辑）。
- **分析**：名字含 "Switch" 已被 ButtonClickSync 关键词匹配、且已被 track（日志证实 tracked 列表含 SaftySwitch (2)）→ 点击同步（OnClickDown 复现 → 对端 AnimatorBoolToggler 切换 + UnityEvent 触发）理论上已覆盖。
- **增强（ButtonClickSync）**：
  - `ShouldTrack` **移除 `GetActive()` 检查**：OnLocalClick 只在真实点击时调用（能点的按钮必然激活），
    inactive 对象不会被真实点击触发 → 移除检查让"初始 inactive、激活后点按"的开关也能广播点击。
  - Keywords 加 `"Safty"`（安全开关的拼写，更精确匹配）。
  - 单 toggler 点击后 `SetBool(权威值)` 对齐最终状态（防丢事件状态反）。
- **注意**：未扩展 HatchSync 状态同步（避免点击 + 状态双触发竞态：SetBool 绝对值 vs OnClickDown toggle 可能反转）。
  若验证发现状态仍不一致（状态由逻辑直接 SetBool 驱动而非点击），再补 HatchSync 状态兜底（需先确认参数名）。
- **待办**：实际拨动 SaftySwitch 验证对端同步。

### T4 任务系统同步（研究 + 增量实现）
- **已同步**：任务开始（MissionSync 102：scene 名 + phase）；任务内实体/目标状态（EntitySync 104：位置/状态/血量）——击杀/目标移动两端一致，任务逻辑自动驱动。
- **研究结论（游戏 API 确认）**：
  - MissionManager：`StartOperation(OperationGraph, MissionGraph)`/`LoadMission`（开始）、`FinishMission()`/`MarkMissionComplete(bool)`/`MarkMissionFailed(bool)`（完成/失败）、`ReloadCurrentMission()`/`ReturnToMap()`/`EndOperationAndReturnToMenu()`（重载/回菜单）、`SetTrackingValue/ModifyTrackingValue(MedalTrackedValue, float)`（进度追踪）
  - **游戏没有玩家模型/骨架**（只有 FirstPersonController，无 PlayerModel/Skeleton/Avatar）——占位化身是正确方案
- **新实现（MissionEventSync 130）**：任务过渡事件（完成/失败/重载/回菜单）——Harmony patch 上述方法 → 主机权威广播 → 对端执行同样操作（防环）
- **待验证**：任务完成/失败/重载/回菜单两端一致

### T4-1 补给/征用同步（Supply console，已实现待验证）
- **用户需求**：弹药（每炮左右）、发射药、征用点、重新定位 Iron Nest、侦察机；购买对所有人生效（不管谁买）。
- **同步覆盖**：
  - **弹药**（CylinderShellSelector）：✅ 已由 ShellSync 同步（弹舱 csv，含全 NULL 消耗）
  - **每炮发射药选择**（PowderChargeController.currentSelectedCharges）：✅ 已由 ReloadSync 同步
  - **发射药库存**（PowderChargeInventory.CurrentCharges）：✅ 新增 RequisitionSync（`req/powder/stock`，主机权威，AddCharges 差值应用）
  - **征用点**（MissionStatsTracker.RequisitionPoints）：⚠️ interop **只读**（get only）——不能值写；只做主�机只读广播（`req/points`，ClientNoSend），点数由主机购买事件权威驱动
  - **购买事件**（Supply console 拉征用杆）：✅ 新增 PurchaseSync（MsgType=132）——patch RequisitionSlot.AttemptRequisition；主机放行+广播，客机拦截上报主机执行（避免双扣点/双效果），购买对所有人生效
  - **重新定位/侦察机**：购买效果由游戏本地逻辑响应（弹药/发射药已同步），侦察机/移动若需额外同步后续评估
- **⚠️ 部署注意**：游戏运行中无法覆盖 DLL——需关闭游戏后部署

### T3-11 主机卡顿（已解决 ✅，2026-08-11）
- **现象（用户反馈）**：主机非常卡，导致引擎状态开局也不同步。
- **根因**：日志刷屏（ControlSync `probe=` 每 0.5s 打印大段控件路径 + HatchSync `scan togglers=472` + ShellSync SnapshotCsv 等）+ 高频 `FindObjectsOfType` 扫描（HatchSync 0.4s / ControlSync Rescan 0.5s / ButtonClickSync PollToggleStates 0.4s）+ 双端同机 CPU 竞争。
- **修复**：
  - ControlSync `probe=` 降到每 20 次 Rescan（~10s）打印
  - HatchSync 扫描 0.4s → 1.0s；ControlSync Rescan 0.5s → 1.0s；PollToggleStates 0.4s → 0.8s
  - 移除/降频诊断日志（light-scan/LEVER-REG/ALL-LEVER 每 20s）
- **已确认工作 ✅**（用户确认卡顿消失）。

### T3-7 玩家化身朝向（已实现，待验证）
- **现象**：所有远端化身（头球/面罩/名字）朝向都跟着本机摄像头（`DefaultPlayerVisualProvider.Update` 设 `visual.rotation = Camera.main.rotation`）。
- **修复**：拆分——化身（body）用 `pose.Yaw`（对应玩家朝向，PlayerSync 同步），名字标签单独 billboard 正对观察者。
- **待验证**：远端化身朝向对应玩家视角方向；名字标签仍正对观察者。

### T3-8 楼梯盖板手柄卡住（已修复待验证）
- **现象（用户反馈）**：楼梯盖板手柄卡在"打开"动作上（盖板整个打开，但手柄动画没回弹）。
- **对象**：`Turret/Floor Hatch Barbet Stars`（盖板根对象，IsOpen toggler）+ 子对象 `Universal Button`（按钮，LookAtTarget + 4 个 AnimatorBoolToggler）。
- **根因（用户纠正：非 4 段折叠，是整体打开 + 手柄回弹）**：
  1. **HatchSync 误管**：IsOpen toggler 在盖板根，`GetComponentInParent<LookAtTarget>` 从根**向上找**不到按钮（按钮是**子对象**）→ HatchSync 仍对它 SetBool，把 IsOpen **钉在"开"** → 手柄卡住。
  2. **对端复现同帧 `OnClickDown()+OnClickUp()`**：楼梯手柄是"点击→动画开→回弹"的瞬时拉杆，同帧按下+抬起让动画被跳过，停在"开"。
- **修复（ButtonClickSync + HatchSync）**：
  - `HatchSync.IsHatch` 改用 `GetComponentInChildren`（向下）**和** `GetComponentInParent`（向上）双向查 LookAtTarget——只要盖板 animator 被任何按钮驱动就跳过（交给 ButtonClickSync）
  - `ButtonClickSync` 新增 **toggle 状态轮询（MsgType=135）**：每 0.4s 扫描多 toggler 按钮，读取**回弹后的最终状态**并广播；对端 `SetBool` 校正（不触发动画）——手柄无论回弹到开/关，两端最终一致
  - 多 toggler 不做立即 SetBool（避免打断动画），改由轮询兜底；单 toggler（灯/SaftySwitch）仍走点击+对齐
  - ApplyClick 加多 toggler after/want 状态诊断日志（`multi-toggler after=[...] want=[...]`）
- **已确认工作 ✅**（用户确认）。

### T3-8b 打字机通知指示灯（Notification Light）不同步（已解决 ✅）
- **现象（用户反馈）**：打字机旁的黄/红/绿通知灯两端不一致（主机亮、客机灭）。
- **根因**：灯亮灭由**按钮 Animator 动画状态**决定（`AnimatorBoolToggler.SetBool` 是协程触发动画），toggler bool 值可能都是 0 但灯亮（场景正常状态）——同步 toggler 值不足以同步灯视觉；且灯亮时间短（<轮询间隔 0.4/0.8s）轮询会错过。
- **修复（ButtonClickSync + TeleprinterSync）**：
  - **打字机打印/清除时即时广播灯状态**（`BroadcastNotificationLights`，Teleprinter 事件触发时调用）——不依赖轮询，灯亮瞬间即同步
  - 通知灯**主机权威单向**：客机只接收应用，不上行、不复现点击（打字机驱动灯触发 OnClickDown，客机复现点击会切换 toggler[0] → 状态错乱）
  - `IsNotificationLight` 只匹配 `Notification Light`/`Message Notifications`（不匹配 `hanging round lamp`——那包含普通吊灯，误匹配会破坏吊灯双向同步）
  - 中途加入快照（StateSnapshotSync "button"）含通知灯状态
- **已确认工作 ✅**（2026-08-11 用户确认）。

### T3-9 打字机（Teleprinter）任务目标指示不同步（已修复待验证）
- **现象（用户反馈）**：打字机输出信息没同步（区块编号同步了但区块内编号没同步；后续"完全不同步"）。
- **对象**：任务目标指示打字机（`[Teleportation and Notifications (1)]` 下，含 PrinterAlertDismisser 等）。
- **根因**：打字机打印由任务状态机（MissionGraph `State_TeleprinterText` 节点）在**主机**跑；客机没跑完整任务图 → 打字机文本两端不同。事件同步（SubmitLines/AppendInstant patch）因 IL2CPP 集合/反射问题不可靠。
- **修复（TeleprinterSync，MsgType=134）**：
  - **状态同步（主）**：Tick 每 0.5s 读取打字机 `_currentFullRich`（完整富文本，兜底反射读 `_tmp.text`），变化广播 `EvState`；对端 `DrainAllJobsInstant()` 清掉排队逐字打印 + 设置 `_currentFullRich` + 反射设 `_tmp.text` → 两端显示内容必然一致
  - 事件（EvPrint/EvAppend/EvClearAll/EvClearAlarm）保留为辅助
  - 反射访问 `_tmp.text`（TMP_Text 跨平台命名空间不同，避免编译差异）
  - **`_tmp` 字段查找修复（2026-08-11）**：interop 的 `_tmp` 是 public 字段但 IL2CPP 反射默认找不到（需 `BindingFlags.Public|NonPublic|Instance`），且字段名可能变化——改为显式 BindingFlags + 兜底按字段类型名 "TMP_Text" 查找。
  - **IsPrinting 时停逐字动画协程**（`_runner`）——否则协程会覆盖新设文本。
  - **视觉状态同步（2026-08-11）**：EvState 消息附加 `revealed`（揭示字符数）+ `paperTransform.localPosition`（纸张位置）+ `animTyping`（打字针敲击）——修复：打字针抽搐（revealed=0 打字机内部认为没打完反复重打）+ 文本太靠上（纸张没随行数下移，host paper=0.14 vs client 0.31）。对端设 `_currentRevealedCharIndex`/`paperTransform`/停打字针动画。
  - **`_tmp` 设置增强（2026-08-11）**：反射 `_tmp` 字段失败 → 兜底 GetComponentsInChildren<TMP_Text> 直接设 .text。
- **已确认工作 ✅**（2026-08-11 用户确认）：内容/位置/打字针/指示灯 全部正常。

### T3-10 自动加入时暂停游戏（新功能，待验证）
- **需求（用户提出）**：中途加入（autojoin）时暂停游戏，加入成功或失败后再解除暂停——防止加入过程中场景/实体生成受玩家操作干扰。
- **实现（AutoJoin + CoopBehaviour）**：
  - `AutoJoin` 首次尝试加入（本地 join / Steam join）时 `PauseManager.RequestGlobalPause()`（`_pausedForJoin` 标记，已暂停则不重复）
  - `CoopBehaviour` 状态变化检测：进入 Hosting/Joined（成功）或回到 Idle（失败放弃）→ `AutoJoin.ResumeIfPaused()`（`ReleaseGlobalPause`）
  - host 建房不暂停（即时完成）
- **待验证**：client 自动加入期间游戏暂停，加入成功/失败后恢复运行。

### T3-6 方向角不同步（已确认解决 ✅）
- **现象**：仰角好了（双向同步有效），但方向角只有**单向**（客机→主机成功，主机→客机失败），两端数据不同步。
- **根因 A（累积值无限，日志铁证）**：方向角 Gear（`Turret/Rotation Console/.Wheel Parent/.Spur Gear 12 DRIVER`）的 `accumulatedValue` 是**无限累积**（backdrive 显示炮塔转动 圈数）：host 端持续增长（-34236→-37117→…→-48901），client 端恒 -25200。**同步绝 对累积值两端基准不同必然错乱**。仰角 Gear（`.002` av=1.7→29）**有界**所以仰角正常。
- **根因 B（binding 反复删建）**：`__turret/rotation` 专门注册块缺 `current.Add(rotId)` → Rescan 尾部"移除已消失控件"每次把它移除 → 每次 Rescan 重复注册 + binding 反复删建 → 同步数据不稳定（"方向角完全不同步"根因）。
- **修复**：
  1. 方向角 Gear **不再同步累积值**（`IsRotationGear` 跳过 dials 通用注册）
  2. 方向角改同步 **`turret.DesiredRotation`**（有界角度，谁操作谁权威，isDragging busy 本地优先）——对端设置 DesiredRotation → 游戏本地逻辑驱动炮塔 → Gear backdrive 自然跟随 → 两端角度一致
  3. **移除 `PreHandleInput` 拦截**（任何端都可本地操作炮塔输入，谁操作谁权威）
  4. **补 `current.Add(rotId)`**——方向角绑定保持稳定
- **已确认解决 ✅**（用户确认）：方向角/仰角/开火/Chain 全部正常。

---

## UI 本地化 + 中文输入（2026-08-11 两轮修复，待验证）
- **部分初始文本未走语言键**：`CoopUIManager` 本地模式角色徽章 `HOST/CLIENT 待机/联机中` 硬编码中文 → 改用 `CoopLoc.HostBadge/ClientBadge/Standby/Online`。
- **中文输入法不生效根因（第 1 轮）**：`PollInput()` 只处理 InputSystem 物理按键（a-z/0-9/标点），中文 IME 组合的字符没有按键事件 → 输入框收不到。旧版 `UnityEngine.Input.inputString` 被裁剪、`InputSystem.onTextInput` 静态事件也被裁剪（dump 确认 InputSystem 静态类只剩 RuntimeInitialize/InitializeInPlayer）。
- **修复（第 1 轮）**：Harmony patch `UnityEngine.InputSystem.Keyboard.OnTextInput(char)` postfix → `CoopUIManager.OnImeText(c)`。dump 确认 `OnTextInput(Char)` 是 public 方法可 patch。
- **第 2 轮（用户反馈"还是没跳输入法"）**：根因是**从未调用 `SetIMEEnabled(true)`** 激活 IME（输入法不弹出）。且首次尝试订阅 `Keyboard.onTextInput` 事件失败——interop 把实例事件降级为 `add_onTextInput(Il2CppSystem.Action<char>)`，`new Il2CppSystem.Action<char>(managedLambda)` 需要 IntPtr（CS1503），无法直接订阅。
- **最终方案（第 2 轮）**：保留 Harmony patch `OnTextInput` 转发 IME 字符 + **输入态切换时 `Keyboard.current.SetIMEEnabled(active)`**（ToggleTyping/StopTyping 统一入口）。
- **⚠️ 部署关键**：client（D 端）是 **MelonLoader**（`Mods\OpenNestCoop.MelonMod.dll`），host（G 端）是 BepInEx（`plugins\OpenNestCoop.dll`）——**两端是不同加载器、不同 DLL**，必须分别构建部署（BepInEx.csproj + OpenNestCoop.MelonMod.csproj）！之前误以为 G 端是 client 部署错位置。
- **待验证**：中文聊天/房间名输入生效（输入法弹出 + 中文字符输入）。

## 联机菜单增强（2026-08-11，已提交 65112cd + 部署）
- **踢人/封禁（本次会话）**：主机成员行点「踢出」→ 发 Kick 消息（MsgType=32）→ 被踢端 LeaveSession + 提示「你已被主机移出房间」；主机 `_banned` 集合记录，收到其 Hello 拒绝加入（并发 Kick 明确拒绝）。会话结束清空封禁。
- **邀请**：主机点「邀请」→ `SteamFriends.ActivateGameOverlayInviteDialog(lobbyId)` 弹出 Steam 好友邀请对话框（本地回环模式不支持）。
- **聊天框拆分**：独立浮动面板（屏幕左中，`_chatRoot` 锚点左中），主面板只留提示行。联机会话时显示。
- **回车快捷键**：未聚焦 + 联机会话时按 Enter → 呼出聊天聚焦；聚焦时 Enter → 发送 + 取消聚焦（`Submit` 末尾 `StopTyping`）。
- **聊天聚焦态（2026-08-11 补充）**：未聚焦时隐藏面板背景/输入框，只留左下小提示条「按回车聊天」（`_chatHintBar`，点击也可聚焦）；回车展开完整面板（背景+记录+输入框）并聚焦；发送后收起回提示条。`_chatBg.enabled=focused` + `_chatContent.SetActive(focused)` + `_chatHintBar.SetActive(!focused)`。
- **⚠️ 聊天面板不响应回车（2026-08-11 修复，提交 171cece）**：根因 = 聊天面板之前**在 Rebuild() 末尾调用，而 Rebuild() 开头 `if (!_menuOpen) return;`**——用户关闭主菜单后 `RebuildChat` 永不执行 + `PollInput` 也 `if (!_menuOpen) return;` 回车呼出被跳过 → 面板不显示/回车无效。
  - **修复**：聊天面板**独立驱动**——Update 里 `ChatKey()` 检测（会话状态+聚焦态+聊天条数+输入文本）变化时调 `RebuildChat()`，不受 `_menuOpen` 门控；`PollInput` 回车呼出也不依赖 `_menuOpen`（联机时始终响应）。
  - **RebuildChat 改无参**（从 `CoopRuntime.Net` 取）——避免 IL2CPP 自定义类型参数（`RebuildChat(NetManager)` 报 "unsupported parameter" 警告，虽历史遗留但稳妥起见无参化）。
  - 保留诊断日志：`BuildChatPanel 完成`、`Enter 呼出聊天 (state=...)`、`RebuildChat show=...`、`FocusChat typing=true`。
- **⚠️ 呼出后打不出字（2026-08-11 修复，提交 fb0d5f3）**：根因 = **InputSystem 只有在存在 onTextInput 监听器时才产生文本事件（TextEvent）并调用 Keyboard.OnTextInput**——只 patch `OnTextInput` 方法不够（无监听器时 InputSystem 不调用它）。之前英文能打是靠 PollInput 物理按键兜底，移除物理按键后（防跳字）就没有任何字符源。
  - **修复**：**真正注册监听器**——`EnsureImeRegistered()`：managed `Action<char>` → `Marshal.GetFunctionPointerForDelegate` → `new Il2CppSystem.Action<char>(IntPtr)`（dump 确认其 ctor 接受 IntPtr）→ `kb.add_onTextInput(...)`。委托保存引用防 GC。保留 Harmony patch OnTextInput 作双保险。
  - **移除发送按钮**：回车发送即可（`MakeInputBox` 全宽 + Enter 提交）。
  - **待验证**：呼出后英文/中文能正常输入，不再跳字。
- **⚠️ 中文仍打不出（2026-08-11 提交 4e179a1，待验证）**：
  - **英文已正常**（日志 `OnImeText 'a'`——onTextInput 监听器注册成功，TextEvent 通）。
  - **中文没收到**：可能 ① `SetIMEEnabled(true)` 未真正激活 IME（IL2CPP 下无效）② 中文提交走 `OnIMECompositionChanged` 而非 `OnTextInput`。
  - **修复/诊断**：patch `Keyboard.OnIMECompositionChanged`（组合串 `Count`/`Item` 索引器遍历，含 CJK 则逐个转发 `OnImeText`）+ `SetImeActive` 加日志（`SetIMEEnabled(x) ok` / `Keyboard.current=null`）。布局：输入框固定贴背景底部（`inputY = ChatH - inputH - 8`），记录区自适应。
  - **待验证**：切中文输入法后是否有 `IME composition '...'` 日志（确认 IME 激活）；中文字符是否进入输入框。
  - ⚠️ IL2CPP 注意：`IMECompositionString` 不能 `== null`/`?.ToString()`（编译错），用 `Count`+`Item` 索引器 + try/catch。
- **⚠️ onTextInput 监听器注册失败（2026-08-11 修复，提交 72b40cf）**：
  - **根因（日志铁证）**：刷屏 `EnsureImeRegistered: The specified Type must not be a generic type. (Parameter 'delegate')` —— **`Marshal.GetFunctionPointerForDelegate` 不接受泛型委托**（`System.Action<char>` 是泛型）→ 监听器从未注册 → 中文打不出（英文之前正常是另一原因）。
  - **修复**：改用**非泛型自定义委托** `private delegate void TextInputHandler(char c)`（`_imeManagedHandler = OnImeText`）→ `Marshal.GetFunctionPointerForDelegate` → `new Il2CppSystem.Action<char>(ptr)` → `kb.add_onTextInput(...)`。委托保存引用防 GC。
  - **失败重试节流**：`_imeRetryAt` + `ImeRetryInterval=2s`，Keyboard 未就绪/注册失败都不再每帧刷屏。
  - **待验证**：日志应出现 `onTextInput 监听器已注册（文本输入激活）` 且不再刷屏；中文输入是否生效。
- **输入法修复（第 3 轮）**：根因 = 双通道重复（Harmony patch OnTextInput + PollInput 物理按键**都**追加 → 按一键跳两字）。修复：**PollInput 只处理控制键（退格/回车），所有可见字符走 OnTextInput 单通道**（含中文 IME）。Backspace 也由 PollInput 按键处理（不走文本事件）。
- **待验证**：中文输入（不重复、Backspace 有效）、踢人/封禁/邀请、回车呼出聊天。

## 诊断日志 / 部署提醒
- `ButtonClickSync` 检测为 **`LookAtTarget.OnClickDown` patch**（统一点击/拉杆）+ `OnChargeButtonPressed`/`OnLoadChargesPressed` patch（选药量/投放）；`OnLocalClick`/`BroadcastClick` 已加 try/catch（防异常中断原方法）。
- 转盘（Dial）读取源：**小写 `accumulatedValue`**（大写 `AccumulatedValue` IL2CPP 读 0）。
- 装填：**纯事件驱动**（OnClickDown + powder），**不写 currentStateIndex**（写索引触发游戏状态机自动推进）。
- 名字调试工具：**默认开启**（准星对准可交互物品显示名字+路径+组件，用于定位无名对象）。
- `ReloadSync` 消息格式（idx + stateIndex + charges），**两端必须同版本 dll**。
- 反编译工具 `scripts/interopdump/` 与 `ref/` 下闭源模组**不进 git 仓库**（含代码注释中的相关提及，已中性化）。
