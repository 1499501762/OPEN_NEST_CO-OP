# 可交互实体术语表（Interactables Glossary）

> **目的**：记录游戏内所有被本模组同步/识别/跳过的**可交互实体**（按钮、拉杆、刻度盘、滑块、曲柄、拉环、开关）的
> **真实 GameObject 名字/路径 ↔ 功能 ↔ 同步模块归属**，避免开发/调试时把名字相近的实体搞混（例如把
> `Universal Button Arm Left` 当成装药拉杆——那是**预备激发火炮**的拉杆）。
>
> **信息可信度**：路径/组件名来自 `tools/dump_Assembly-CSharp.txt`（IL2CPP 反编译）、运行日志（runlog/）
> 以及 **F9/F10 调试工具实测**（对准实体 → 复制到剪贴板，2026-08-22 采集）。
> 同步归属来自 `src/OpenNestCoop/GameSync/*.cs` 当前实现（V1 活代码）。
>
> **更新记录**：
>
> - 2026-09-12（三）新增 §六 **炮术计算台按钮**：`Artillery Computer Console/Calculate Universal Button`
>   （用户 F10 实测：组件 `Transform/Animator/LookAtTarget/BoxCollider/AnimatorBoolToggler x4`；此前**无任何同步**）。
>   归 `ButtonClickSync`（点击复现 + 多 toggler 轮询），由配置文件开关 `[Interactables] CalculateButtonSync` 控制，**默认 false**
>   → 见 `docs/CONFIG.md`（新增模组配置文件：猫同步/唱片机同步开关 + 本项）。
>
> - 2026-09-12（二）新增一条**根因已定位并改为源头修正**的问题：**客机落点与主机不一致**——
>   实测两端飞行时长/射程差均 ~4%，根因是客机复现开火用的是**自己那一刻的本地物理状态**（炮塔角/仰角）；
>   新增 `ShotSync`（动态通道 `shotparams`）主机权威下发**炮弹发射参数**。详见 `docs/IMPACT_ASSESSMENT.md`。
>
> - 2026-09-12 新增两条已修问题：**照片“拍摄角度/方向”不同步**（地图上的照片角度由游戏 `RandomUIRotation.Awake`
>   两端各自随机 roll → `ImpactSync` 改为由已同步着弹点算出的确定性角度，详见 `docs/IMPACT_ASSESSMENT.md`）；
>   **开局打字机偶发不同步**（`Apply` 在打字机未注册时丢弃事件 + 主机 `EvState` 只在变化时广播 → 永久不同步；
>   改为暂存原始包 + `Tick` 重试，详见 `docs/TELEPRINTER_MISSION.md`）。
>
> - 2026-08-26 修复"开局状态不正常"（Locking Lever Rotation/Elevation 乱转 + 引擎停电）：①`Aiming Console/` 下 Locking Lever 视觉角度由 `transform.localEulerAngles` 表示（accumulatedValue 恒 0 非视觉值）——ControlSync 值源改读 `localEulerAngles.y`、set 直接设旋转（对端 Lever 角度一致）；②`env/engine/running` 加 SkipFull 排除 ControlFull 全量广播（只走状态变化广播，避免主机开局 getter 误读 false 覆盖客机关引擎）。
> - 2026-08-22 建表
> - 2026-08-22 加入 F9/F10 实测路径；修订：不存在方向角锁止拉杆；仰角锁止 = `Wheel Blocker`/`Handle Blocker`（Interactable 非 LookAtTarget）；
>   `Starter Chain` 非必须同步；`Delete button` 归属仰角计算单；新增引擎扳手轮 `.Dial core`、`CatInterruption`、`SaftySwitch (4)`、
>   `EngineControls/Floor Hatch` 舱门、`War Horn` 汽笛、打字机通知灯 6 个等。
> - 2026-08-23 全表同步当前代码：推弹头/切弹舱 → **CylinderActionSync（141 事件解耦）**；预备激发 → **ArmSync（140 事件解耦）**；
>   Button Dispencer 激活 → **ChargeButtonSync（143 掩码同步）**；`.Charge Dial` 实测路径更新；着弹/照片问题 → `docs/IMPACT_ASSESSMENT.md`。

---

## 一、装填 / 发射药区域

路径前缀：`Gun System Left|Right/--Reloading Console/`（⚠️ `Charge Rammer`/`Load shell Rammer`/`Move Cylinder` 直接在
`--Reloading Console` 下，**不在** `PowderChargeController` 下；仅 `Button Dispencer` 在 `PowderChargeController/` 下）

| 实体名 | 实测路径（F9） | 类型 | 功能 | 同步模块 | 备注 |
|---|---|---|---|---|---|
| `Button Dispencer (N)`（N=1..6） | `PowderChargeController/Button Dispencer (N)` | LookAtTarget 按钮 +**4** toggler（2026-08-23 实测 tg=[1,0,0,0]；此前 comps 诊断只打前 6 组件误判 2） | **选药量**：选第 N 号发射药包（**逐档激活**：选 N 档才激活到 Button N，allActive 110000→111111） | ReloadSync（`OnChargeButtonPressed` → PowderEvent）+ **ChargeButtonSync（MsgType=143 主机权威 active 掩码同步）** | ⚠️ 不走 ButtonClickSync 的 OnClickDown（走 isClicked+方法调用），必须 patch `PowderChargeController.OnChargeButtonPressed`；2026-08-23 已从 ButtonClickSync.ShouldTrack 显式排除；激活链由 ChargeButtonSync 掩码同步（0.3s 轮询+3s 补发）保证两端逐档一致；开局误激活已修（移除 Tick 补激活链） |
| `Universal Button Charge Rammer (1)` | `--Reloading Console/Universal Button Charge Rammer (1)` | LookAtTarget 按钮 +4 toggler | **投放发射药**：把选好的药包推进膛 | ReloadSync（`loadChargesButton` / `OnLoadChargesPressed` → PowderEvent） | 就是 `PowderChargeController.loadChargesButton`；2026-08-23 从 ButtonClickSync.ShouldTrack 显式排除（原来被 `Rammer` 关键词误命中 → 下装药拉杆 powder load 被 IsApplyingClick block 未广播） |
| `Universal Button Load shell Rammer` | `--Reloading Console/Universal Button Load shell Rammer` | LookAtTarget 按钮 +4 toggler | **推弹头**：把炮弹推进膛（不是推装药！） | **CylinderActionSync（MsgType=141 事件解耦）**：`CylinderShellSelector.OnLoadButtonClicked` 被 patch → 广播 → 对端同名方法 | 依赖装填状态机 active；2026-08-23 从 ButtonClickSync.ShouldTrack 显式排除（走事件解耦，不再按路径复现点击） |
| `Universal Button Move Cylinder` | `--Reloading Console/Universal Button Move Cylinder` | LookAtTarget 按钮 +4 toggler | **切弹舱/换弹种** | **CylinderActionSync（MsgType=141）**：`OnMoveButtonClicked` → 广播 → 对端同名方法 | CylinderShellSelector 的 `moveButton`；同上事件解耦 |
| `Odomiter Counter Charge Invenotry` | `PowderChargeController/Odomiter Counter Charge Invenotry` | OdometerDisplay | 装药量计数器显示 | 不注册（显示/从动） | 非交互 |
| `hanging round lamp` | `PowderChargeController/hanging round lamp` | 灯 | 装药区吊灯（视觉） | ButtonClickSync（灯） | 非输入控件 |

---

## 二、瞄准 / 仰角区域（Aiming Console / Elevation Console）

> ⚠️ **实测确认（2026-08-22）**：
> - **不存在** "方向角锁止拉杆"（原 `Aiming Console/Locking Lever Rotation` 是错误记录，已删）。
> - "仰角锁止" 实际是 `Wheel Blocker` / `Handle Blocker`（`Interactable` 类型，**不是** LookAtTarget → 不走点击同步）。

| 实体名 | 实测路径（F9） | 类型 | 功能 | 同步模块 | 备注 |
|---|---|---|---|---|---|
| `Wheel Blocker` | `Turret/Elevation Console/.Elevation Lever Baseplate/.Loading Cover Left/Wheel Blocker` | Interactable（SphereCollider） | **仰角锁止（轮）**：锁定/解锁仰角调整 | ⚠️ 非 LookAtTarget → 不走 ButtonClickSync；需确认用 Interactable 事件同步 | 2026-08-22 新增 |
| `Handle Blocker` | `Turret/Elevation Console/.Elevation Lever Baseplate/.Loading Cover Left/Handle Blocker` | Interactable（BoxCollider） | **仰角锁止（手柄）**：锁定/解锁仰角调整 | 同上 | 2026-08-22 新增 |
| `.Elevation Lever Left` / `.Elevation Lever Right` | `Turret/Elevation Console/.Elevation Lever Baseplate/.Elevation Lever Left` | LinearSliderInteractable + Interactable + MeshCollider + Outline | **仰角物理拉杆**（输入源） | ControlSync/ValueSync（30Hz HighFreq，双向，谁操作谁权威） | ⚠️ 物理位置双向同步，不做插值 |
| `Elevation Desired Left/Right`、`Elevation Current Left/Right` | （推测 `Elevation Console` 下） | LinearSliderInteractable（从动） | 仰角期望/当前值（联动 follower 输出） | ControlSync/ValueSync（**ClientNoSend**：只接收 host 广播） | ⚠️ client 本地未驱动时读 0，双向同步会上行 0 覆盖 host（仰角回退根因） |
| `Medium_Valve`（方向角压力阀） | `Aiming Console/PressureSystem_ElevationRight/PressureValve (1)/Dial/Medium_Valve` | DialInteractable + Interactable + Outline + DialInteractableColliderHelper | 压力阀（仰角压力系统） | ControlSync/ValueSync（NoHeartbeat） | ⚠️ **压力系统存在多个**，分别控制不同部件：炮 / 瞄准 / 推弹 / 推药 / 旋转（2026-08-22 实测） |
| 方向角 Spur Gear | （原 `Turret/Rotation Console/.Wheel Parent/.Spur Gear 12 DRIVER`；F9 实测命中 `Turret/Cube (6)` 待复核） | DialInteractable（方向角齿轮） | 方向角齿轮（backdrive 显示炮塔圈数） | **不直接同步**（accumulatedValue 无限累积）→ 由 `TurretController.DesiredRotation` 状态同步覆盖（`__turret/rotation`，30Hz） | ⚠️ 同步累积值必然错乱（两端基准不同） |

## 三、预备激发 / 开火拉环（Trigger Console / Arm）

> ⚠️ **最容易搞混的区域**。`PowderRamLever.007/008` 名字带 "Powder"（火药），但它不是装药拉杆——
> 它下面挂的是**预备激发火炮的拉杆**。

| 实体名 | 实测路径（F9） | 类型 | 功能 | 同步模块 | 备注 |
|---|---|---|---|---|---|
| `Universal Button Arm Left` | `Trigger Console/.Trigger Rail Cart/.Trigger Core/.ArmingLeverParent Left/.PowderRamLever.007/Universal Button Arm Left` | LookAtTarget 按钮 +4 toggler | **预备激发左炮**（`ArmedFireRelayOneShot.ArmLeft()` → `_leftArmed=true`） | **ArmSync（MsgType=140 事件解耦）**：`ArmLeft` 被 patch → 广播 → 对端同名方法（不依赖按钮 active） | ⚠️ **不是装药拉杆**！2026-08-23 从 ButtonClickSync 改走 ArmSync 事件解耦（修客机按钮 inactive 排队丢弃，不再用关键词 `Arm` 点击复现） |
| `Universal Button Arm Right` | `Trigger Console/.Trigger Rail Cart/.Trigger Core/.ArmingLeverParent Right/.PowderRamLever.008/Universal Button Arm Right` | LookAtTarget 按钮 +4 toggler | **预备激发右炮**（`ArmRight()`） | ArmSync（MsgType=140） | 同上（对称） |
| `.Trigger chain parent` | `Trigger Console/.Trigger Rail Cart/.Trigger Core/.Trigger Chain Track/.Trigger chain parent`（命中 `.Trigger Handle`） | LinearSliderInteractable + LinearSliderAutoRetractor + LookAtTarget | **激发拉环/开火** | ⚠️ **不走点击同步**——开火由 `GunController.FireShell` → GunFire 事件同步，否则双重触发开火两次 | ButtonClickSync.ShouldTrack 显式排除 |
| `.Starter chain parent.001` | `EngineControls/Engine Controls/.Starter Chain Track.001/.Starter chain parent.001`（命中 `.Starter Handle.001`） | LinearSliderInteractable + LinearSliderAutoRetractor + LookAtTarget | **启动/重启引擎拉环** | ⚠️ 2026-08-22 修订：**与 Trigger Chain 相同，不是必须同步**，但要找一下引擎启动/重启的事件 | 原记录"必须同步"已更正 |

`ArmedFireRelayOneShot`（Zagreekie.Tools）关键成员：`_leftArmed`/`_rightArmed`、`ArmLeft()`/`ArmRight()`/`DisarmLeft()`/`DisarmRight()`/`ArmBoth()`、`_leftArmedEvent`/`_rightArmedEvent`。

### 引擎相关调整控件（2026-08-22 新增）

| 实体名 | 实测路径（F9） | 类型 | 功能 | 同步模块 | 备注 |
|---|---|---|---|---|---|
| `.Dial core` | `EngineControls/Engine Controls/.wrench wheel Parent/.Dial core` | DialInteractable + LookAtTarget + Interactable + DialInteractableColliderHelper | 引擎扳手轮（调整控件） | 可能已有命中（待确认走哪个同步） | 2026-08-22 新增 |
| `CatInterruption` | `CatObjects/CatInterruption` | Interactable（BoxCollider + InterruptCatOnCollision） | 引擎区猫打断碰撞 | 非按钮（猫交互走 CatSync） | 2026-08-22 新增 |

---

## 四、发射台解锁序列（SequenceSync 专门同步）

路径：`Trigger Console/.Trigger Console Floor/.Review Console Parent/.Check Switch.00X/`

| 实体名 | 实测路径（F9） | 功能 | 同步模块 | 备注 |
|---|---|---|---|---|
| `Universal Switch Button` | `.Check Switch/Universal Switch Button` | 发射台开关序列第 1 个 | **SequenceSync**（含点击复现） | ⚠️ 不走 ButtonClickSync 点击/toggle——否则与 SequenceSync 双驱动反复回跳 |
| `.Check Switch.001/Universal Switch Button Variant` | `.Check Switch.001/...` | 序列开关 2 | SequenceSync | 同上 |
| `.Check Switch.002/...` | `.Check Switch.002/...` | 序列开关 3 | SequenceSync | 同上 |
| `.Check Switch.003/...` | `.Check Switch.003/...` | 序列开关 4 | SequenceSync | 同上 |
| `.Check Switch.004/...` | `.Check Switch.004/...` | 序列开关 5 | SequenceSync | 同上 |

`LookAtTargetUnlockSequence5.HandleSlotClicked` 被 Harmony patch（PreSeqSlotClick）→ 点击上报。

### 发射台升起/降下拉杆（2026-08-22 实测）

| 实体名 | 实测路径（F9） | 功能 | 同步模块 | 备注 |
|---|---|---|---|---|
| `Universal Button` | `Trigger Console/Universal Button` | 发射台升起/降下拉杆 | 已有命中同步 | 2026-08-22 确认已同步 |

---

## 五、征用 / 补给（Requisition / Punchcard）

| 实体名 | 实测路径（F9） | 功能 | 同步模块 | 备注 |
|---|---|---|---|---|
| `Universal Button` | `Requisition Console/Universal Button` | **征用台拉杆**：插卡后拉杆购买 | ButtonClickSync（关键词 `Requisition`/`Universal`，togglers=4 对齐）+ PunchcardSync（卡槽事件） | 曾出现 `after=[1,0,0,0] want=[0,0,0,1]` 反复回跳，已做多 toggler 对齐 |
| `PunchcardRuntime_Default(Clone)` | `Requisition Console/Punchcard Deck Area/PunchcardRuntime_Default(Clone)` | 补给卡牌（DraggableItem），拖到 RequisitionSlot 插入 | PunchcardSync（MsgType 136/137/138） | ⚠️ EntitySync 跳过 `card#` 开头实体（卡牌由 PunchcardSync 管理，不创建成 Enemy） |
| `.Charge Dial`（目标弹舱拨杆） | `Requisition Console/Requisition Control Pannel/Console Parent/Console Box/ConsoleAnchor/ConsoleControl_Magazine Selection(Clone)/.Charge Dial Parent/.Charge Dial` | DialInteractable + DialValueEventWatcher | **弹头类补给卡牌的目标弹舱选择拨杆**（调整补给到左炮/右炮/弹舱） | ControlSync/ValueSync（Dial 通用注册；路径含 Requisition Control → 非 PowderChargeController，保留心跳） | 2026-08-23 F9 实测；⚠️ 补给台拉杆卡激活与此无关（用户澄清） |
| `SaftySwitch (4)` | `Requisition Console/SaftySwitch (4)` | **重置征用卡位置按钮** | ButtonClickSync（关键词 `Safty`） | 2026-08-22 新增；含 LookAtTargetEventRelay |
| `Delete button` | `Tactical Map/Draggable Surface/FireMissionCard3D(Clone)/Delete button` | **仰角计算单上的删除按钮**（与征用机构无关） | ButtonClickSync（关键词 `Delete`） | 2026-08-22 修订：不是任务卡删除，是发射目标仰角计算单的删除 |

---

## 六、其他按钮

### 地板舱门（2026-08-22 实测两个）

| 实体名 | 实测路径（F9） | 功能 | 同步模块 | 备注 |
|---|---|---|---|---|
| `Universal Button` | `EngineControls/Floor Hatch/Universal Button` | 引擎区地板舱门按钮 | ButtonClickSync | 2026-08-22 新增 |
| `Universal Button` | `Turret/Floor Hatch Barbet Stars/Universal Button` | 炮塔地板舱门按钮（驱动 4 个 AnimatorBoolToggler 楼梯折叠段） | ButtonClickSync（完整复现点击，不 SetBool） | ⚠️ 多 toggler 含长动画（delay>0）跳过立即 SetBool，防打断动画卡中间态 |

### 打字机通知灯（2026-08-22 实测共 6 个：2 组 × 3 色）

路径：`[Teleprinters]/Message Notifications(N)/Notification Light {green|yellow|red}/Swing pivot/hanging round lamp/Universal Button`

| 实体名 | 功能 | 同步模块 | 备注 |
|---|---|---|---|
| `Notification Light green/yellow/red`（×2 组） | 打字机通知指示灯（玩家可交互） | ButtonClickSync（点击 + 多 toggler 轮询；主机权威单向） | ⚠️ 客机点灯只影响本地按钮动画，避免打字机驱动 + 双向轮询争抢 |

### 战争汽笛（2026-08-22 实测 2 个）

| 实体名 | 实测路径（F9） | 功能 | 同步模块 | 备注 |
|---|---|---|---|---|
| `universal button` | `War Horn Parent/War Horn/universal button` | 汽笛按钮 | ButtonClickSync（关键词 `Horn`/`Siren`） | 2026-08-22 新增 |
| `universal button` | `War Horn Parent (1)/War Horn/universal button` | 汽笛按钮 | ButtonClickSync | 2026-08-22 新增 |

### 炮术计算台（Artillery Computer Console，2026-09-12 实测）

| 实体名 | 实测路径（F10） | 类型（组件） | 功能 | 同步模块 | 备注 |
|---|---|---|---|---|---|
| `Calculate Universal Button` | `Artillery Computer Console/Calculate Universal Button` | LookAtTarget + **4×AnimatorBoolToggler** + Animator + BoxCollider + Transform | 炮术计算台“计算”按钮（与普通 Universal Button 同构） | **ButtonClickSync**（点击复现 118 + 多 toggler 状态轮询 135） | ⚠️ **2026-09-12 新增同步，默认关**：开关 = 配置文件 `[Interactables] CalculateButtonSync`（`docs/CONFIG.md`）——此前路径不含任何关键词 → 完全不跟踪。开启后需**双端都 true** |

### 其他

| 实体名 | 功能 | 同步模块 | 备注 |
|---|---|---|---|
| `[Teleprinters]/Printers/.Scroll Lever Parent Right (1)/.Scroll Lever Right` | 打字机滚动拉杆 | ControlSync/ValueSync（Scroll Lever 30Hz） | — |

---

## 七、怀表 / 秒表（非同步，当前调查中）

| 实体名 | 类型 | 功能 | 同步模块 | 备注 |
|---|---|---|---|---|
| `GunStopwatch` | MonoBehaviour（枪上秒表/怀表） | 显示弹道预测时间/倒计时（`countdownStartTime`、`ComputeTravelTimeLocally()`） | **无同步模块** | ⚠️ 纯本地计算。用户反馈"任务内手持怀表时间不同步"——需确认是它还是 `GenericTimerSceneSync` |
| `GenericTimerSceneSync` | MonoBehaviour（游戏自带通用计时器） | `CurrentTime`/`CurrentHours/Mins/Seconds` + `TimerID`，LateUpdate 本地走表 | **无同步模块** | 名字带 SceneSync，但看实现只本地刷新；任务计时起点两端可能不同 |

**怀表同步候选方案**（待用户确认怀表实际是哪个组件后实施）：
- GunStopwatch → 主机权威广播 `currentState` + `countdownStartTime`，客户端 `ApplyHandFromSeconds` 对齐
- GenericTimerSceneSync → 主机权威广播 `CurrentTime`（或任务开始偏移），客户端 set `CurrentTime` 对齐

---

## 八、ButtonClickSync 关键词表（ShouldTrack 匹配）

以下关键词命中按钮名**或路径**即被 ButtonClickSync 跟踪（点击同步）：

```
Lever, Rammer, Hatch, Primer, Confirm, Breech,
Power, Reset, Delete, Measure, Kill, Lock, Elevation, Range, Damage,
Link, Firing, Trigger, Launch, Lanyard, Fire, Safety, Safty, Switch, Arm,
Starter, Crank,
Light, Lighting, Notification,
Horn, Siren,
Cylinder, Move, Dispenc, Dispenser, Load,
Requisition, Punchcard, 征用,
Trajectory
```

**显式排除**（命中即不跟踪）：
- `Trigger Chain`（开火走 GunFire 事件同步，避免双重开火）
- `LookAtTargetUnlockSequence5` 下任何按钮（发射台序列走 SequenceSync）
- `Load shell Rammer` / `Move Cylinder`（推弹头/切弹舱走 CylinderActionSync 事件解耦）
- `Button Dispencer` / `Charge Rammer`（选药量/投放发射药走 ReloadSync PowderEvent 事件解耦；2026-08-23 修，原来被 `Dispenc`/`Rammer` 关键词误命中 → 双路径冲突）

**关键词之外的名字级显式纳入**（不经关键词表，直接按名字判定，**由配置开关控制**）：
- `Calculate Universal Button`（炮术计算台计算按钮）→ `return CoopConfig.CalculateButtonSync;`（**默认 false**，见 `docs/CONFIG.md`）

> F10 交互工具现在会在屏幕底部多显示一行 `同步(点击复现): 开/关` + `[CoopConfig 键=值 ...]`——
> 对着实体看这行就能判断“到底有没有被跟踪”（含配置开关判定），不用猜。

**ControlSync 不注册**（显示/从动型，非玩家输入）：
- Range/Bearing Dial、Split flap display（坐标计算器显示）
- Starter Chain/Trigger Chain（链条机械动画显示）
- 方向角 Spur Gear（走 DesiredRotation 状态同步）
- ⚠️ 2026-08-22 实测：仰角锁止是 `Wheel Blocker`/`Handle Blocker`（`Interactable`，非 LookAtTarget）——
  不走 ButtonClickSync 点击同步，需确认用 Interactable 事件同步（旧记录“Locking Lever 走点击”不适用于这两个实体）

---

## 九、已确认但待修的问题（2026-08-22）

| 现象 | 归属实体 | 分析 | 状态 |
|---|---|---|---|
| 客机拉 `Universal Button Arm Left` 没用 | Arm Left（预备激发） | **已修（2026-08-23）**：客户端该按钮 inactive → 点击排队丢弃（原 ButtonClickSync 实体耦合）。改走 **ArmSync（MsgType=140 事件解耦）**：patch `ArmedFireRelayOneShot.ArmLeft/Right/DisarmLeft/DisarmRight` → 广播 → 对端同名方法（不依赖按钮 active） | ✅ 已改事件解耦，待复测 |
| 装填状态被反复拉回 0 | ArtilleryReloadController | 客户端本地推进 st=1/3/5/8 被 `host-align SetState idx=0 -> 0` 拉回（9aca1a8 改“总是 SetState”后） | ✅ 0.1.8 已回退 9aca1a8，待复测 |
| 下装药拉杆/装药数值不同步（2026-08-23） | Button Dispencer / Charge Rammer | 双路径已排除；gun 索引两端一致。**最终根因（差 1）**：游戏 `OnChargeButtonPressed` 参数是 **0-based**（Button N → N-1），方法内部 `currentSelectedCharges` = 药包数（index+1）。对端 apply 的 `currentSelectedCharges = chargeIndex` **强制覆盖**把游戏算好的药包数覆盖成 0-based → 拉 Button 1 = 0 个药（没下装药）、拉 Button 2 = 1 个药（才下 1 个）。**修复**：删除强制覆盖，让游戏自己设（`powder select apply ... ch=?` 诊断验证）；快照通道 ch 单向（仅中途加入对齐） | ✅ 已删强制覆盖，待复测 |
| 方向角拉杆→Gear 动量保持（2026-08-23） | 方向角（拉杆控制 Gear 转速） | **机制澄清**：拉杆控制 Gear 转动速度（动量），幅度→转速，逆/顺时针；松开后应保持动量。已改**转速双通道**（rotVel 高频 + 角度静止兜底）。**第二轮：来回转根因**——角度广播与转速冲突 + 转速双向覆盖。修复：角度应用时本地操作中不设 + 转速 SenderWhenBusy 单向。**第三轮：停下后角度差异（0.1°）**——deadzone 0.5 是**通量节流**（非精度），停止后需强制同步一次。**修复**：ControlSync.Tick 检测转速非0→0（停稳）→ `ValueSync.ForceSend("__turret/rotation")`（ForceNext 忽略 deadzone 强制广播一次）；角度 set 改 busy 检查（本地主动操作中不设，停止后设） | 🔄 已加停止后强制同步，待复测 |
| 装药实体/拉杆不同步 + 拉杆锁定 + 开局误激活（2026-08-23） | Charge Rammer / Button Dispencer / 状态机 | **推弹头锁定（已修）**：`AlignReloadState(cyl,4)` 延迟容忍。**Button Dispencer 逐档激活链断**：客机拉 2 档后 Button 3 不激活（allActive 停 110000）。**修复**：新建 `ChargeButtonSync`（MsgType 143）**主机权威同步按钮 active 掩码**（0.3s 轮询 + 变化即发 + **3s 周期补发**——补发解决客机首次收到时场景未就绪被丢弃、之后 sig 不变不重发 → 掩码同步失效）→ 客机 `LookAtTarget.SetActive` 对齐。**"开局就激活"元凶（2026-08-23 实测）**：`ReloadSync.Tick` 补激活链（`LastActivateCh=-1` → ch=0 变化 → 开局调 `ActivateNextChargeButtonIfValid` → 激活 Button 1）→ 主机开局 Button 1 active（掩码 `[0:1;1:1]`），掩码同步又同步给客机 → 双端开局都激活。**修复**：**移除 Tick 补激活链**（激活同步完全交给 ChargeButtonSync 权威掩码；apply 后补激活保留，仅选药量事件触发）。装填阶段指示器按次序亮（黄灭灭灭→绿黄灭灭→绿绿黄灭），第三阶段才拉装药，开局应全不激活 | 🔄 已移除 Tick 补激活链 + 掩码周期补发，待复测 |
| 补给台拉杆卡激活（2026-08-23） | `Requisition Console/Universal Button`（多 toggler 拉杆） | ⚠️ 用户澄清：**与目标弹舱拨杆（.Charge Dial）无关**，非冲突。原分析（动画中切拨杆）错误；后测**未复现** | ✅ 未复现，暂关闭 |
| `.Charge Dial` 双向同步（2026-08-23） | `Requisition Console/.../.Charge Dial` | 用户反馈：当前**双向同步正常**（两端 Requisition 拨杆 av 0→1 一致）；但提示**可能是偶发**（预计延迟容忍性问题）——本轮未改 .Charge Dial 逻辑，若再出现客机拨动不上行，需加延迟容忍/心跳处理 | ✅ 当前正常，偶发待观察 |
| 只打两发但着弹/侦察照片多触发（2026-08-23） | 着弹（ImpactTracker/ShellVisual）/ 侦察照片 | 用户反馈：拉 `.Trigger chain parent` 开火 2 发，但"着弹好像多触发"+"似乎侦察照片多触发"。**实测结论（ImpactDiag+堆栈）**：开火/炮弹层正常（2 发 = Initialize 2 次）；着弹评估由 `ImpactLocation::EvaluateAndReport` 驱动（每发 1 次，堆栈确认）；**偶发**——一次复现 EvaluateImpact 3 次（第 3 次 5 秒后、位置不同 = 多余一次），另一次只 2 次（无多）。侦察照片入口 `RegisterChild`（ReconPhotoSync）两次均未触发——用户看到的"照片"更可能是战术地图落点标记（ImpactMarkerManager）。详见 `docs/IMPACT_ASSESSMENT.md` | 🔄 偶发，已建 IMPACT_ASSESSMENT.md + ImpactDiag 堆栈诊断，待继续观察 |
| 照片"拍摄角度/方向"不同步（2026-09-12） | 着弹照片（`ImpactLocation_*(Clone)/Parent` 的 `RandomUIRotation`） | 双端实测：角度由游戏 `RandomUIRotation.Awake` **本地随机 roll**（`UnityEngine.Random`，`min=5 max=85 axis=Z`；HOST 43.9° / CLIENT 77.9°）。**方案（用户方向）**：全局随机**同步种子**——两端用同一权威落点派生种子在 Awake 前 `InitState` → 游戏自己 roll 出同值（不传角度/不覆盖/无闪烁）。详见 `docs/IMPACT_ASSESSMENT.md` | ✅ 已改，待复测 |
| 客机落点与主机不一致（2026-09-12 根因） | 炮弹弹道（`ShellVisual`）/ 着弹评估 | 用户质疑：“开火参数明明都是一致的，客机落点怎么会不一致，一直是同一个根源问题，治标不治本”。实测：HOST 落点(18.18,5.43) 飞行25.62s vs CLIENT 落点(18.88,5.73) 飞行26.84s（时长/射程差均~4%）→ **根因 = 客机是收到 GunFire 后用自己的本地物理状态（炮塔角/仰角）重算一发**，模组只同步了期望/控件值。**修法**：新增 `ShotSync`（动态通道 `shotparams`）主机权威下发这发炮弹的发射参数，客机套用到本地同发炮弹（治本）。详见 `docs/IMPACT_ASSESSMENT.md` | 🔄 已改源头，待复测 |
| 锁止组件出现/消失未同步（2026-09-12） | `Turret/Elevation Console/.Elevation Lever Baseplate/.Loading Cover {Left,Right}/Handle Blocker` | `BlockerSync`（动态通道 `blocker`）已实现（主机 0.5s 轮询 activeSelf 变化广播、客机 SetActive），实测主机 `host broadcast n=2` 但**客机无任何 recv** → 单次变化事件丢了（客机当时未注册/对象未找到/被本地逻辑覆盖）就永久不同步。**修法**：加 **3s 心跳补发**（无变化时幂等重发全部当前状态）+ 日志路由到 sync.log | 🔄 已加心跳，待复测 |
| 丢一发炮弹（2026-09-12 已查清） | 炮弹（`ShellVisual`）/ `GunController.ChamberedShellBlueprint` | 用户怀疑“模组拦截短时间多发炮弹落地”。实测：**模组没拦截**——客机 3 次 `OnGunFire`/`RequestFire` 都执行了，第三发（GunRight）`chambered=N`（膛内无弹）→ `FireShell` 不触发 → 客机少一发。修法：`ShotSync` 兜底——主机参数 1.2s 未配到本地炮弹 → 按 `ShellId` 解析 `ShellDefinition` + `shellVisualPrefab` 直接 `Instantiate` + `Initialize(主机参数)`。详见 `docs/IMPACT_ASSESSMENT.md` | 🔄 已加兜底，待复测 |
| 打字机右侧一直空白（2026-09-12 已修判据） | `[Teleprinters]/Printers`（ptype） | 上一版“本地动画期间不写状态”的判据用 `IsPrinting`——实测**本机协程已死但 `IsPrinting` 仍 true** → 永远不写 → 一直空白。新判据 = “**本机揭示数是否真的在前进**”：前进 → 不干预（平滑本地动画）；不前进 → 由主机状态驱动（每个状态包写一次文本/揭示数/遮罩/纸张），即“跟随主机逐字显示”。另：换新文本时揭示数从 0 重新跟随。详见 `docs/TELEPRINTER_MISSION.md` | 🔄 已改，待复测 |
| 打字机右侧空白/无动画 + 后续任务错乱（2026-09-12 已定位） | `[Teleprinters]/Printers`（ptype） | 双端日志：主机右侧**正常打印完**（revealed=463）；客机 `applied print ptype=1` 后 `isPrinting=True revealed=0`（**启动了但一字符未揭示**）→ 随后任务静默结束 → 被强制同步到 `revealed=463`。根因：**每 0.1s 覆写内部字段与本地协程打架**。详见 `docs/TELEPRINTER_MISSION.md` | 🔄 已改，待复测 |
| 客机漏开火（2026-09-12 根因=膛内无弹） | 炮弹（`ShellVisual`）/ 着弹评估 | 主机 4 次 FireShell / 客机只 2 次。实测客机 `RequestFire chambered=N`（膛内无弹）→ `FireShell` 不触发 → 该发在客机侧没炮弹/没落点/没照片。**用户确认：由外部测试模组（买弹药没同步）导致，非本模组问题** | ✅ 非本模组问题，不管 |
| 追踪器异常不同步（2026-09-12 ✅ 已不再需要单独同步） | `Map Table_ Shell Trajectory display` / `TrajectoryTarget` | 根因是**弹道起点不同**（铁巢地图图标 `TurretLocation` 未同步，已由 `NestSync` 修）→ 两端弹道一致后追踪器自然一致。用户确认不再需要单独同步：`ImpactSync` 已移除每次着弹写 `TrajectoryTarget.defaultLocalPosition`，只保留**中途加入**的强制对齐（协议加 `force` 字节，主机 `OnLateJoin` 单播最近着弹）。 | ✅ 已改为仅中途加入强制对齐 |
| 开局打字机偶发不同步（2026-09-12） | `[Teleprinters]/Printers`（Teleprinter） | ①`Apply` 在打字机未注册时丢弃事件（已改暂存+`Tick` 重试）；②**实测真正主因**：客机自己的 `IsPrinting` 长期为真（主机 `isPrinting=False` 而客机持续 `printing=True` 2 分钟）→ `keepLocalAnimation=true` 永远跳过主机最终态（revealed/遮罩/纸张）→ 揭示进度与纸张位置不同步；已加"揭示数 2s 未前进→强制最终态"。详见 `docs/TELEPRINTER_MISSION.md` | ✅ 已改，待复测 |
| 怀表时间不同步 | GunStopwatch / GenericTimerSceneSync | 无同步模块，纯本地计算 | 🔄 待 F9 确认怀表实际组件 || 仰角锁止（Wheel/Handle Blocker）未确认同步 | Wheel/Handle Blocker（Interactable） | 非 LookAtTarget，不走点击同步，需确认用 Interactable 事件 | 🔄 2026-08-22 新发现 |
| 引擎扳手轮 `.Dial core` 同步归属待确认 | .Dial core（DialInteractable） | 可能已有命中，需确认走哪个同步 | 🔄 2026-08-22 新发现 |
| Starter Chain 事件待查 | .Starter chain parent.001 | 与 Trigger Chain 相同非必须同步，但需找引擎启动/重启事件 | 🔄 2026-08-22 新发现 |

---

## 十、版本回归记录（重要！）

| 版本 | 状态 | 说明 |
|---|---|---|
| **2026-09-12k（未发版）** | 🔄 待复测 | ①**新增模组配置文件**（标准 INI，`BepInEx/config/OpenNestCoop.cfg` / `UserData/OpenNestCoop.cfg`，2s 热重载）——设置项：`CatSync`（猫同步开关，默认开）、`RecordPlayerSync`（唱片机同步开关，默认开）、`CalculateButtonSync`（默认**关**）。见 `docs/CONFIG.md`；②**新交互同步**：`Artillery Computer Console/Calculate Universal Button`（F10 实测：LookAtTarget + 4 toggler）纳入 `ButtonClickSync`（默认关）；③**关键包保护表加注册函数**：`NetManager.RegisterCriticalType(s)` + `IsCriticalRegistered`（动态 Critical/High 优先级模块自动登记）；④F10 交互工具显示“同步(点击复现)开/关” + 当前配置值 |
| **2026-09-12j（未发版）** | 🔄 待复测 | ①**关键包保护扩展**：动态通道模块（优先级 Critical/High，如 `shotparams`/`blocker`/铁巢图标）不再被合包上限丢弃（旧实现只保护硬编码类型且仅 <256）；②**主机返回主菜单客机跟随**：`MissionSync` 客户端在 phase=0 调 `LoadMainMenu()`、phase=1 调 `EnterBrowsingMap()`（旧实现只 `SetPhase` 不切界面）；③打字机已修好（用户确认） |
| **2026-09-12i（未发版）** | 🔄 待复测 | 打字机：“动画期间不写状态”的判据由 `IsPrinting` 改为“**本机揭示数是否真的在前进**”（协程已死但 IsPrinting 仍 true → 旧判据导致一直空白）；不前进时由主机状态驱动跟随显示（并修正换新文本时揭示数复位） |
| **2026-09-12h（未发版）** | 🔄 待复测 | 打字机：“本地打印任务一开始就死”根因 = **我们每 0.1s 覆写内部字段与本地协程打架** → 修：动画期间一律不写状态 + 卡死时用主机文本重起动画打印 + EvPrint 提交前清坏任务 + `job.lines` 无条件覆盖。另（上一版）：丢发兜底（`ShotSync` 用主机参数直接生成炮弹） |
| **2026-09-12g（未发版）** | 🔄 待复测 | ①**丢发兜底**：实测模组未拦截，是客机膛内无弹（`chambered=N`）→ `ShotSync` 用主机参数直接 `Instantiate` 炮弹复现；②**打字机**：卡死判定加严格门槛（主机揭示数>本机）+ 定向复位（只停本机协程/清队列，不用游戏强制API）+ EvState 追加主机行游标对齐 |
| **2026-09-12f（未发版）** | 🔄 待复测 | ①**撤销打字机“强制收尾”**（`ForceCompleteAll`/`DrainAllJobsInstant` 实测把后续所有打印动画弄没）→ 改纯诊断 `[Teleprinter] STUCK ...`；②**追踪器不再每次着弹同步**（弹道起点已同步→自然一致），只保留中途加入强制对齐（协议加 `force`）|
| **2026-09-12e（未发版）** | 🔄 待复测 | ①**打字机卡死“强制同步”改为完整收尾**：新增 `CompleteLocally()`（游戏自带 `ForceCompleteAll`/`DrainAllJobsInstant` + 清 `_pendingJobs` + `_isRunning=false`），用于卡死分支 / `EvPrint` 提交前清理 / `Tick` 客机侧卡死自愈——修“强制同步不完全 → 后续任务打字针起始位置/动画状态/换行全错”；②上一版：弹道起点根因（铁巢地图图标 `TurretLocation`）已由 `NestSync` 同步 |
| **2026-09-12d（未发版）** | 🔄 待复测 | **弹道起点根因锁定并修复**：炮弹起点 = `Tactical Map/Canvas/MapRoot/TurretLocation`（铁巢地图图标，= `GunController.firePoint`）；客机该图标停在场景默认值 → 弹道整体平移 → 落点不同。修：`NestSync`（146）随铁巢一并主机权威同步该图标（同包附加块 + 1.5s 心跳 + 客机补应用）。另：`ShotSync` 注册修复后已验证生效；照片角度已正常（用户确认）；漏开火=外部模组；打字机右侧偶发待定位 |
| **2026-09-12c（未发版）** | 🔄 待复测 | ①**落点差的真源入**：弹道 `start`/`target` 两端**整体平移同一个固定向量 (-1.100,-0.100)**（aim/dur/dist 完全相同）——即弹道原点 `firePoint` 本地位置不同；②修 `ShotSync` **注册漏传 ChannelKey**（主日志 `[Registry] type=0 … ShotSync` → 整轮未注册）→ 修后落点直接一致；③停用未生效的“同步随机种子”，改为逐张照片角度日志；④`BlockerSync` 加 3s 心跳补发（锁止组件出现/消失）；⑤客机漏开火根因=**膛内无弹**（已加显式日志）。详见 `docs/IMPACT_ASSESSMENT.md` |
| **2026-09-12b（未发版）** | 🔄 待复测 | **客机落点不一致根因修复（治本）**：新增 `ShotSync`（动态通道 `shotparams`）——主机权威下发**炮弹发射参数**（shellId/起点/终点/飞行时长/路径长），客机套用到本地同发炮弹 → 两端同一条弹道、落点一致（根因：客机复现开火用的是自己那一刻的本地物理瞄准状态）。诊断 `[ShotDiag] fire`/`[ShotDiag] init` 双端对照。详见 `docs/IMPACT_ASSESSMENT.md` |
| **2026-09-12（未发版）** | 🔄 待复测 | ①**照片“拍摄角度/方向”不同步**：`ImpactSync`（13）**重新设计**：主机广播自己照片的**真实随机角度** + 落点，客机在【本地报告后】+【包到达后】按标记配对写入（事件驱动、无定时窗口、不再用哈希算角度）（详见 `docs/IMPACT_ASSESSMENT.md`）；②**开局打字机偶发不同步**：`TeleprinterSync`（134）打字机未注册时暂存事件 + `Tick` 重试（详见 `docs/TELEPRINTER_MISSION.md`）|
| **0.1.6** | ✅ **一切正常**（用户确认） | 只是有性能问题 |
| **0.1.7（c6c1197 + 9aca1a8）** | ❌ 一大堆严重问题 | 修 FPS（EntitySync 节流、心跳 2s→5s、日志节流）+ 9aca1a8（ApplySnapshot 总是 SetState、Punchcard 上报去重、EntitySync 跳过 card 实体）后，出现：Arm 按钮 inactive、装填状态被拉回、卡牌交互异常等 |
| **0.1.8** | 🔄 0.1.6 基线 + 多轮回归修复（2026-08-22~23） | 回退 9aca1a8（ReloadSync/PunchcardSync/EntitySync 恢复 0.1.6）+ F9 下移 + F10 复制剪贴板。随后迭代修复：推弹头字节错位、装填状态回拉（事件驱动，不再常规 SetState）、方向角动量/停止强制同步、装药差1（删强制覆盖 currentSelectedCharges）、Button Dispencer 开局误激活（移除 Tick 补激活链 + ChargeButtonSync 掩码同步 143）、预备激发/弹舱动作事件解耦（ArmSync 140/CylinderActionSync 141）、.Charge Dial 双向回归。**装药区域已全部正常（用户 2026-08-23 确认）**；剩余：着弹偶发多触发（IMPACT_ASSESSMENT.md）、.Charge Dial 偶发、怀表时间、仰角锁止等 |

> ⚠️ **回归排查方向**：0.1.7 相对 0.1.6 的改动集中在
> `EntitySync.cs`（FPS 节流 + 跳过 card + 缺实体 CreateMapEntity）、`ReloadSync.cs`（心跳 5s + 总是 SetState）、
> `PunchcardSync.cs`（上报去重）。若 0.1.6 行为正确，需逐项对照这些改动是否引入行为变化。
