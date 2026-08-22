# 联机化同步方案：实体耦合 vs 事件解耦 评估（Decoupling Status）

> **目的**：评估当前模组"按钮/交互同步"的两种方案——**实体耦合**（按路径找实体复现点击）与
> **事件解耦**（广播业务方法调用，不依赖按钮实体状态），记录已解耦/待解耦/保留耦合的实体清单，
> 作为后续"是否全解耦"的决策依据。2026-08-22 评估，**2026-08-23 修订**（预备激发/弹舱动作已事件解耦）。
>
> **关联文档**：`docs/INTERACTABLES.md`（实体术语表）、`docs/ARCHITECTURE.md`（架构）。

---

## 一、两种同步方案对比

| 维度 | 实体耦合（当前 ButtonClickSync/ButtonLayer） | 事件解耦（PowderEvent/SequenceSync/Requisition） |
|---|---|---|
| 同步内容 | 广播按钮路径 id + toggler 状态 → 对端按路径找 `LookAtTarget` → 复现 `OnClickDown()+OnClickUp()` | 广播业务方法调用（如 `OnChargeButtonPressed(chargeIndex)`）→ 对端直接调方法 |
| 依赖按钮实体 | ⚠️ **必须找到同路径实体且 active** | ✅ 不依赖按钮 active/存在 |
| 对端按钮 inactive | ❌ 排队 3s 超时丢弃（点击失效） | ✅ 直接执行逻辑，不受影响 |
| 动画复现 | ✅ 完整复现（拉杆动画 + 事件链） | ⚠️ 需额外处理动画（或方法内部自带） |
| 典型问题 | "客机拉 Arm 没用"（Arm inactive 排队丢弃） | 需防环（IsApplying 标志），方法参数化 |

**结论**：**事件解耦是更可靠的联机化方案**（用户确认"这看起来是正确的联机化方案"）。
但**不是全解耦**——纯视觉/动画按钮（无独立业务方法，或点击效果全在 UnityEvent 绑定里）应保留实体耦合。

---

## 二、已事件解耦 ✅（V1 已实现）

| 交互 | 游戏方法 | 同步模块 | 事件类型 |
|---|---|---|---|
| 选药量 | `PowderChargeController.OnChargeButtonPressed(chargeIndex)` | ReloadSync（PowderEvent） | EvSelect |
| 投放发射药 | `PowderChargeController.OnLoadChargesPressed()` | ReloadSync（PowderEvent） | EvLoad |
| 发射台序列开关 | `LookAtTargetUnlockSequence5.HandleSlotClicked(slotIndex)` | SequenceSync（EvClick=2） | 事件 + 数值对账 |
| 开火 | `GunController.FireShell` → GunFire 事件 | TurretSync / EventLayer(V2) | 事件 |
| 征用购买 | `RequisitionSlot.CR_SpendRequisitionPoints` | RequisitionSync | 事件 |
| 任务过渡 | `MissionManager.FinishMission/MarkMissionComplete/MarkMissionFailed/ReloadCurrentMission/ReturnToMap/EndOperationAndReturnToMenu` | MissionEventSync（MsgType=130） | 6 种事件 |
| 打字机打印/清除 | `Teleprinter.SubmitLines/AppendInstant/ClearAll/ClearAlarm` | TeleprinterSync（MsgType=134） | 5 种事件 + 状态 |
| 通知灯 | `LookAtTarget` 指示灯 | ButtonClickSync（MsgType=135 状态广播，**不点击复现**） | 状态广播 |
| 反炮兵 | `CounterBatteryCinematicImpactSpawner.SpawnOne` | CounterBatterySync | 事件 |
| 任务随机 seed | `FireMission.GenerateMission`（prefix 应用固定 seed） | MissionSync（MsgType=102） | seed 广播 |
| 预备激发（Arm Left/Right / Disarm Left/Right） | `ArmedFireRelayOneShot.ArmLeft()/ArmRight()/DisarmLeft()/DisarmRight()` | ArmSync（MsgType=140） | 4 种事件 |
| 弹舱动作（推弹头 / 切弹舱） | `CylinderShellSelector.OnLoadButtonClicked()/OnMoveButtonClicked()` | CylinderActionSync（MsgType=141） | 2 种事件 |

**补充（2026-08-23）**：Button Dispencer（选药量）与 Charge Rammer（投放发射药）已从
`ButtonClickSync.ShouldTrack` **显式排除**（走 ReloadSync PowderEvent 事件解耦）——避免点击复现 + 事件双重触发。

**共同点**：对端**直接调用业务方法**，不要求按钮实体 active/存在。

---

## 三、仍实体耦合 ❌（按路径复现点击）

> **已移出（2026-08-23）**：预备激发（`Universal Button Arm Left/Right`，ArmedFireRelayOneShot 四方法）与
> 弹舱动作（`Universal Button Load shell Rammer` 推弹头 / `Universal Button Move Cylinder` 切弹舱）已由
> `ArmSync`（MsgType=140）/ `CylinderActionSync`（MsgType=141）**事件解耦**，见 §二。

| 实体 | 游戏方法（若存在） | 现状 | 建议 |
|---|---|---|---|
| `Requisition Console/Universal Button`（征用台拉杆） | 购买已事件化（CR_SpendRequisitionPoints） | 拉杆动画仍点击复现（视觉） | 🟢 保留（购买已解耦，动画复现足够） |
| `SaftySwitch (4)`（重置征用卡位置） | 无独立方法 | 实体耦合 | 🟢 保留（无方法可调） |
| `War Horn` 汽笛 `universal button`（×2） | 无独立类（UnityEvent 绑定） | 实体耦合 | 🟢 保留（无方法可调） |
| `Floor Hatch` 舱门 `Universal Button`（×2） | `AnimatorBoolToggler`（IsOpen） | HatchSync 状态同步 + 点击复现完整动画 | 🟢 保留（动画复杂，点击复现 + 状态对账正确） |
| `Locking Lever` / `Wheel Blocker` / `Handle Blocker`（仰角锁止） | `Interactable`（非 LookAtTarget） | ⚠️ 实测是 `Interactable` 类型 → **不走点击同步**，当前可能未同步 | 🔴 需确认用 Interactable 事件同步 |

---

## 四、解耦判断标准

**该解耦**（满足任一）：
1. 有独立业务方法（`ArmLeft()` 等），且实体耦合已导致问题（inactive 排队/状态不同步/回跳）
2. 按钮 active 由状态机驱动（装填/解锁），对端状态不同步时按钮 inactive → 点击必然失效
3. 方法参数化明确（如 `HandleSlotClicked(slotIndex)`、`OnChargeButtonPressed(chargeIndex)`）

**不该解耦**（保留实体点击复现）：
1. 纯视觉/动画按钮，无独立业务方法（点击效果全在 `OnClickDown` 的 UnityEvent 绑定里）
2. 点击效果 = 完整动画过程（楼梯盖板手柄、锁止拉杆倾斜），解耦会丢失动画过程
3. 无参数、无副作用可广播（如汽笛短鸣，需采样点击动作而非方法）

---

## 五、推荐行动（按优先级）

| 优先级 | 动作 | 状态 |
|---|---|---|
| 🔴 **P0** | `ArmedFireRelayOneShot` 事件解耦（Arm Left/Right/Disarm） | ✅ **已完成**（ArmSync=140，2026-08-23） |
| 🟡 **P1** | 推弹头/切弹舱方法级事件解耦 | ✅ **已完成**（CylinderActionSync=141，2026-08-23） |
| 🟡 **P1** | 仰角锁止（Wheel/Handle Blocker）`Interactable` 事件同步 | ⏳ 待做 |
| 🟢 **P2** | 其余保留实体点击复现 | — |

> **关键洞察**：V1 已走通事件解耦范式（Powder/Sequence/Requisition/Arm/Cylinder 都成功）。
> P0/P1 两项高风险实体（预备激发 Arm、推弹/切弹舱）已于 2026-08-23 事件解耦——
> "有独立方法却还靠点击复现"的高风险实体已基本清零。
