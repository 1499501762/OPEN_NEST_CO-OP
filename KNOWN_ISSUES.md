# 已知问题（Known Issues）

> 未修复/待验证问题清单，按优先级分级。更新于各轮测试后。
> **2026-08-23（v0.1.9）**：绝大多数历史问题已解决（见文末「已解决归档」），
> 本节只保留**当前仍待复测/待验证**的项。状态权威来源：`docs/INTERACTABLES.md` 第九节问题表 + 各主题文档。

## 优先级定义
- **T0 严重**：影响核心玩法（装填/发射/开火），必须优先
- **T1 高**：电源/灯光/动力系统
- **T2 中**：战术地图桌 / 标记 / Token
- **T3 低**：其他（猫、唱片等次要交互）

---

## 一、当前待验证 / 待复测（🔄）

### T0 — 装填 / 发射

| 问题 | 归属 | 分析 | 状态 |
|---|---|---|---|
| 方向角拉杆→Gear 动量保持（停下后 0.1° 差异） | 方向角（拉杆控制 Gear 转速） | 已改转速双通道（rotVel 高频 + 角度静止兜底）+ 停止后 `ValueSync.ForceSend("__turret/rotation")` 强制广播 + 角度 set 加 busy 检查 | 🔄 已加停止后强制同步，待复测 |
| 装药实体/拉杆不同步 + 开局误激活 | Charge Rammer / Button Dispencer / ChargeButtonSync | 已移除 Tick 补激活链（激活完全交给 ChargeButtonSync 权威掩码 143）+ 掩码 3s 周期补发 | 🔄 已改，待复测 |
| 着弹/侦察照片偶发多触发 | 着弹（ImpactTracker/ShellVisual）/ 侦察照片 | 开火层正常（2 发）；偶发 `EvaluateImpact` 3 次（第 3 次 5s 后、位置不同）。已建 `docs/IMPACT_ASSESSMENT.md` + ImpactDiag 堆栈诊断 | 🔄 偶发，待继续观察 |

### T1 — 电源 / 灯光 / 动力

| 问题 | 归属 | 分析 | 状态 |
|---|---|---|---|
| 怀表时间不同步 | GunStopwatch / GenericTimerSceneSync | 无同步模块，纯本地计算 | 🔄 待 F9 确认怀表实际组件 |

### T2 — 战术地图桌 / 标记 / Token

| 问题 | 归属 | 分析 | 状态 |
|---|---|---|---|
| 地图桌三按钮（重置击杀令牌 / 删除所有测量 / 重置标记令牌） | ButtonClickSync | 已改 `OnClickDown` patch（关键词 Reset/Delete/Measure/Kill） | 🔄 待验证是否被捕获 |

### T3 — 其他

| 问题 | 归属 | 分析 | 状态 |
|---|---|---|---|
| 仰角锁止（Wheel/Handle Blocker）同步归属 | Interactable | 非 LookAtTarget，不走点击同步，需确认用 Interactable 事件 | 🔄 2026-08-22 新发现 |
| 引擎扳手轮 `.Dial core` 同步归属 | .Dial core（DialInteractable） | 可能已有命中，需确认走哪个同步 | 🔄 2026-08-22 新发现 |
| Starter Chain 事件待查 | .Starter chain parent.001 | 与 Trigger Chain 相同非必须同步，但需找引擎启动/重启事件 | 🔄 2026-08-22 新发现 |
| 任务内容随机种子两端一致 | MissionSync | 主机广播 seed（`_hostSeed` 稳定不漂移），客机 `useFixedSeed` | 🔄 待验证目标位置/数量/照片不再漂移 |
| 任务过渡事件（完成/失败/重载/回菜单） | MissionEventSync（MsgType=130） | Harmony patch MissionManager 6 方法 → 主机权威广播 → 对端执行（防环） | 🔄 待验证两端一致 |
| 发射台 Switch 事件同步被吞 | ButtonClickSync | pending 队列同 id 去重合并 + 超时丢弃日志 + toggle 型不合并丢弃 + 单 toggler SetBool 对齐 | 🔄 待验证 |
| SaftySwitch 安全开关同步 | SaftySwitch (N) | `ShouldTrack` 移除 GetActive 检查 + Keywords 加 `Safty` + 单 toggler SetBool 对齐 | 🔄 待验证 |
| 玩家化身朝向（body vs 名字标签） | PlayerSync / DefaultPlayerVisualProvider | 化身用 `pose.Yaw`，名字标签独立 billboard | 🔄 待验证 |
| 自动加入时暂停游戏 | AutoJoin / CoopBehaviour | 加入期间 `RequestGlobalPause`，成功/失败 `ReleaseGlobalPause` | 🔄 待验证 |
| 补给/征用点显示同步 | RequisitionSync | 弹药/发射药/库存/购买已同步；**征用点 interop 只读**（`req/points` 主机只读广播，客机界面点数不更新） | 🔄 需专门设计（写回 AntiTamper 或客机本地执行购买） |

> ⚠️ **部署提醒**：游戏运行中无法覆盖 DLL——需关闭游戏后部署双端。
> `ReloadSync` 消息格式（idx + stateIndex + charges），**两端必须同版本 dll**。

---

## 二、已解决归档（✅，2026-08-23 前累计）

> 以下问题均已确认解决/用户确认正常，保留一行归档；详细根因与方案见 `docs/` 各主题文档。

### 联机菜单 / 聊天 / 输入（T3）
- ✅ **中文输入（真汉字）**：原生 Win32 IME P/Invoke（`ImmGetCompositionStringW` GCS_RESULTSTR 读 OS 提交真汉字，主通道）+ `GCS_COMPSTR` 组合判定 + 首字符缓冲（消闪烁/重复）—— 不再依赖被 IL2CPP 破坏的 Unity 输入管线。→ `docs/LOBBY.md`
- ✅ **通用输入框 `CoopInputBox`**（普通类，非 MonoBehaviour——IL2CPP 泛型 AddComponent 崩）：值/密码掩码/占位/光标/聚焦/提交回调，房间名(1)/密码(2)/弹窗(3) 复用。→ `docs/LOBBY.md`
- ✅ 聊天框接入真汉字 + 首字母不重复 + 空格确认不误触回车 + 退格长按自动重复（0.45s 起每 0.06s）+ 候选窗定位到输入框旁
- ✅ 踢人/封禁（MsgType=32 + `_banned`）、Steam 邀请 overlay、回车呼出聊天、聊天面板独立驱动（不受 `_menuOpen` 门控）
- ✅ 角色徽章/本地化走语言键；版本+加载器标识独立行右对齐（不挡邀请/离开按钮）

### T0 — 装填 / 发射
- ✅ 装填状态跳步/不同步：纯事件驱动（`LookAtTarget.OnClickDown` + `OnChargeButtonPressed`/`OnLoadChargesPressed` patch），不写 currentStateIndex
- ✅ 装药拉杆/塞装药：事件链转发 + 对端模拟点击按钮
- ✅ 仰角锁止拉杆（Arm Left/Right）：`ArmSync`（MsgType=140 事件解耦，不依赖按钮 active）
- ✅ 切换弹药种类：`ShellSync`（MsgType=109，主机权威弹舱 csv + 1.5s 心跳）
- ✅ 第一发直接装填、推弹头字节错位、装填状态回拉（事件驱动）、选药量差 1（删强制覆盖 currentSelectedCharges）

### T1 — 电源 / 灯光 / 动力
- ✅ 主电源拉杆：`ButtonClickSync`（Switch 关键词）+ `M3EnvSync` engine/running
- ✅ 开局停电：`env/*` 全部 `ClientNoSend=true`（主机权威，client 只接收不上行）
- ✅ 多人失焦暂停：patch `PauseManager.OnApplicationFocus` + `PauseOnFocusLoss=false` + `runInBackground=true`

### T2 — 地图桌 / 标记 / Token
- ✅ MapToken 拖动 / T/F/S1-10 / 位置同步（编号+路径组合区分同名、首全量对齐+变化广播）
- ✅ 铁巢 Token / 杀伤范围标盘、Token 初始位置错位（首全量对齐）
- ✅ 地图标记擦除同步（MapMarkerSync 107 标志字节 + `DetectLocalErase` + `_applyingRemove` 防环）
- ✅ 免虚拟机双开自动联机（`AutoJoin` + 本地回环 `--local` + `dualtest.ps1 -Local`）

### T3 — 其他
- ✅ 猫同步 v2/v3（主机 AI 权威决策软同步 + 交互事件 133 + 活动类型/动画同步 + `NavMeshAgent.Warp` 硬同步 + 抱起放下）—— 用户 2026-08-11 确认全部正常
- ✅ 仰角拉杆被 GunElevationLink 锁定（T3-2）、弹道计算机响声（`NoHeartbeat` 跳过 Charge/Pressure 心跳）
- ✅ 方向角/仰角曲柄：只同步 Lever/Gear 值 + 事件（用户拍板抛弃状态插值，T3-5）
- ✅ 方向角不同步：改同步 `DesiredRotation`（有界角度）+ 补 `current.Add(rotId)`（T3-6）
- ✅ 楼梯盖板手柄卡住（HatchSync 双向查找 + toggle 状态轮询 135 校正回弹）
- ✅ 打字机通知灯（即时 `BroadcastNotificationLights` + 主机权威单向，T3-8b）
- ✅ 打字机内容/位置/打字针/指示灯（TeleprinterSync 134 + reveal mask + 反射兜底）—— 用户确认全部正常
- ✅ 主机卡顿（日志刷屏降频 + 扫描周期放宽，T3-11）
- ✅ 人物临时模型朝向（移除 180 翻转，T4-3）
- ✅ 任务内容随机种子漂移（MissionSync `_hostSeed` 稳定，T4-2）
- ✅ 补给/征用（T4-1）：弹药（ShellSync）、发射药选择（ReloadSync）、发射药库存（RequisitionSync）、购买事件（PurchaseSync 132 主机权威 + 幂等去重）—— 征用点显示仍待验证（见上）
- ✅ 发射台 Switch 状态跳变（主机权威 + 事件/数值分离，消息 110 ev 字节）

### 性能 / 架构（2026-08-13~15）
- ✅ 主机卡顿/FPS 下降：`CoopLog` 日志门面（等级过滤 + 按 key 节流）+ ControlSync Rescan 守卫/降频 + ValueSync Dictionary + EntitySync 聚合发送（56 包/s→2 包/s）+ 节流日志
- ✅ EntitySync 缺实体补齐（客户端 FireMission.CreateMapEntity + Register + 节流 5s/实体）
- ✅ V1 死代码清理（该注释的注释保留）；V1 默认方案回归通过

---

## 诊断日志 / 部署提醒
- `ButtonClickSync` 检测 = **`LookAtTarget.OnClickDown` patch**（统一点击/拉杆）+ `OnChargeButtonPressed`/`OnLoadChargesPressed` patch；`OnLocalClick`/`BroadcastClick` 已加 try/catch
- 转盘（Dial）读取源：**小写 `accumulatedValue`**（大写 `AccumulatedValue` IL2CPP 读 0）
- 装填：**纯事件驱动**（OnClickDown + powder），**不写 currentStateIndex**（写索引触发游戏状态机自动推进）
- 名字调试工具：**默认开启**（准星对准可交互物品显示名字+路径+组件）
- `ReloadSync` 消息格式（idx + stateIndex + charges），**两端必须同版本 dll**
- 反编译工具 `scripts/interopdump/` 与 `ref/` 下闭源模组**不进 git 仓库**（含代码注释中的相关提及，已中性化）
- 版本号发版 5 处同步 bump（`scripts/package.ps1` / 两个 csproj / `NetConfig.cs` / `MelonModEntry.cs`）
