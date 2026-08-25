# 网络负载调控器（NetworkGovernor）

> **目的**：应对 Steam P2P 硬限制（unreliable 单包 ~1200B、高频丢包率高；reliable 大包延迟/拥塞）
> 的**分级 + 动态**网络负载控制组件。任务场景内大量周期状态（EntitySync/ControlSync/PlayerSync 等）
> 可能让单帧合包逼近硬限制 → 拥塞 → 丢包 → 重传 → 更拥塞，需要按网络状况自动降频/降流量保护。
> 2026-08-25。
>
> **关联**：`docs/ARCHITECTURE.md`（2.1/2.2 网络架构）、`docs/API.md`（消息协议——本组件**不加新 MsgType**，
> 只动态调节现有发送路径参数）。**代码**：`src/OpenNestCoop/Net/NetworkGovernor.cs` +
> `NetManager.cs`（接入）+ `AutoJoin.cs`（`--nettier`）。

---

## 更新记录

- 2026-08-25 初稿：NetworkGovernor（分级档位 + 动态评估 + NetManager 接入 + `--nettier` 手动锁定）。
- 2026-08-25 补充：**F8 网络诊断 UI**（`Debug/NetworkGovernorDebugUI`）+ **网络环境限制模拟**（`--netcap <KB/s>` 带宽令牌桶 / `--netpacket <B>` 单包上限 / `--netloss <%>` unreliable 丢包率，`NetLagSim.AllowSend` 在 FlushBatch 发送前拦截）。
- 2026-08-25 精细化：**per-module 分级**（`NetModulePriority`：Critical/High/Normal/Low/Bulk + `ISyncedModule.NetPriority` + `NetworkGovernor.ModuleFreq` 系数表——同一全局档位下关键模块几乎不降、高频容忍模块优先降；`CoopSyncRegistry.TickAll` 与 V1 硬编码模块按各自优先级缩放）；**dualtest.ps1 默认开启网络模拟**（env.ps1：`LocalTestNetCapKBps=200` / `LocalTestNetLossPercent=5` / `LocalTestNetPacketCapB=1200`，可 `-NetCap 0` 等关闭）；**官方包限制确认**（Steamworks：unreliable ≤1200B、reliable ≤1MB、>1200B 路由器丢包、队列积压时 SendP2PPacket 返回 false）。
- 2026-08-25 per-module 诊断：**独立日志 dump**（key=`net.diag`，显示时打印档位/采样/每模块带宽/模拟配置）+ **每模块带宽占用统计**（`NetworkGovernor.RecordTypeBytes` 按 MsgType 归因入队字节，窗口 1s 重置，`GetTypeBytesTop` 供屏幕/日志显示 top8）。
- 2026-08-25 按键统一：网络诊断并入 **F9 循环**（`DiagCycleController`：不显示→帧→网络→交互→不显示），不再用 F8 独立按键；三个诊断 UI 统一右上角。
- 2026-08-25 吞包修复（客机炮弹落点/不同步根因）：**Critical 档不再关闭 unreliable 通道**——高频连续状态（炮塔值/玩家位置）被吞 → 炮弹落点/化身明显不同步；负载改靠 Freq 降频控制而非吞包。**EnqueueBatch 合包丢弃保护关键类型**（`IsCriticalType`：事件/交互/装填/开火/落点等边沿触发包不丢，只丢可重发周期状态）。
- 2026-08-25 **降级误判修复（用户反馈“直接降级”网络差）**：RTT 拥塞判定改**相对基线**（`_rttBaseline` EMA：上升慢跟 0.1/下降快跟 0.5）。**根因**：原绝对阈值 `DownRttMs=180ms` 在模拟延迟（`--lag 170` → RTT ~450ms）或高延迟网络下**必然判拥塞 → 持续降到 Critical（Freq×0.25）→ 所有模块降频 → 同步明显变慢**。**延迟高 ≠ 拥塞**（真拥塞信号=丢包/队列积压/发送超限）。修复：`congested = 丢弃>0 || 发送>40KB/s || 队列峰值≥上限 || RTT > 基线+150ms`；`idle = 无丢弃 && 发送<10KB/s && RTT < 基线+60ms && 队列不紧张`。稳定高 RTT（固有/模拟延迟）不降档；RTT 相对基线明显上升（真拥塞队列积压）才降档。日志加 `base=...ms` 便于诊断。
- 2026-08-25 **多维降级评估（用户要求：按流量/延迟/丢包率/队列多维）**：把单维判定升级为**四维度组合**——①流量（>40KB/s 拥塞维度命中；>80KB/s 硬信号单维度即降；<10KB/s 空闲）；②延迟（RTT 相对基线上升 >150ms 拥塞维度；<60ms 健康）；③**丢包率（新增，Ping/Pong unreliable 探测滚动窗口 24s≈8 样本：`RecordPingSent`/`RecordPongRecv` 喂入；>10% 拥塞维度；>25% 硬信号；<3% 健康；<4 样本忽略）**；④队列（峰值≥上限）。**降级 = ≥2 维度同时拥塞 或 任一硬信号（流量爆表/高丢包/合包主动丢弃）**——防单维度误报；**升档 = 全维度健康**。日志加 `loss=..% dims=N`；F9 网络诊断 UI 加丢包率显示。
- 2026-08-25 UI 可读性：网络诊断菜单**逐行着色**（档位 High 绿/Normal 蓝/Low 橙/Critical 红；采样四维度各按健康度着色——发送 <10KB 绿/>40KB 黄/>80KB 红、丢弃 0 绿 else 红、RTT <100ms 绿/<200 黄/else 红、丢包 <3% 绿/<10% 黄/else 红、队列≥上限 红）。⚠️ IL2CPP interop 限制同 FRAME_DIAG：不创建 GUIStyle / 不用 GUILayout（`new GUIStyle()`+GUILayout 运行时导致 OnGUI 异常被吞 → F9 呼不出）——用 `GUI.contentColor` 逐行着色 + 纯 `GUI.Label` 手动定位。
- 2026-08-25 **独立日志 + 时序保护**：①诊断/联机日志路由到独立文件——`OpenNestLogs/net.log`（NetworkGovernor 调控/net.diag/NetManager）、`sync.log`（联机模块）、`frame.log`（帧诊断），`ModLog` 缓冲批量落盘（1s 间隔，性能好）；主日志只留会话/错误（控制台不刷屏 → 帧性能提升）。②**reliable/unreliable 时序**：`ValueSync` 加 **seq 时序保护**（每绑定版本序号，包带 seq，接收端 `IsNewer` 去旧保新）——unreliable 无时序/乱序/晚到**不覆盖** reliable 心跳新值（解决 reliable/unreliable 争抢导致的控件/炮塔值回跳）。心跳 reliable 与高频 unreliable 共用同一 seq 计数器 → 接收端统一去旧。

## 一、背景：Steam P2P 硬限制

| 限制 | 数值 | 后果 |
|---|---|---|
| unreliable 单包上限 | ~1200B | 超限整包被拒收 |
| unreliable 高频 | 丢包率高 | 高频连续状态全断 |
| reliable 大包 | 延迟/拥塞显著 | 单包越大越容易排队/重传 |

原策略（ARCHITECTURE 2.2）：所有周期状态走 reliable 合包 + 按 1000B 拆包；高频连续状态走
unreliable 单条（>1100B 降 reliable）。但**合包上限（256）/拆包（1000B）是写死常量**，无法在
负载高时自动降——本组件把这三层控制面变成**分级 + 动态**。

## 二、分级档位（NetQualityTier）

`NetworkGovernor` 单例，四档（枚举序 = Critical→High）：

| 档位 | FreqMultiplier（模块 Tick dt 缩放） | MaxBatchItems（合包上限） | MaxPacketBytes（reliable 拆包） | UnreliableMaxBytes | AllowUnreliable |
|---|---|---|---|---|---|
| **Critical**（0） | 0.25 | 64 | 400 | 600 | 开（**不吞 unreliable**——高频连续状态靠降频控负载，吞掉炮塔值/位置会不同步） |
| **Low**（1） | 0.50 | 128 | 600 | 800 | 开 |
| **Normal**（2） | 0.75 | 192 | 800 | 1000 | 开 |
| **High**（3，默认） | 1.00 | 256 | 1000 | 1100 | 开 |

- **流量上限**：`EnqueueBatch` 合包上限用 `MaxBatchItems`（超限丢弃 + reliable 丢弃 `RecordDrop`
  喂回评估器）；`FlushBatch` 拆包用 `MaxPacketBytes`（低档更小包 → 单包延迟/重传成本更低）。
- **通道策略**：`AllowUnreliable` —— **所有档位都开**（2026-08-25 修复：Critical 不关 unreliable）。
  unreliable 承载高频连续状态（炮塔值/玩家位置），彻底关闭 = 两端明显不同步（炮弹落点错/化身卡顿）；
  负载控制靠 Freq 降频（高频状态自动变慢），不靠吞包。
- **关键类型不吞**：`NetManager.EnqueueBatch` 合包上限丢弃时，**关键类型**（`IsCriticalType`：GunFire/Impact/
  ReloadState/ReloadCmd/FireRequest/ControlState/ControlCmd/MapMarker 增删改/ReloadAdvance/PowderEvent/CatEvent/
  V2Event/V2Button/V2ReloadCmd 等事件与交互）**不参与丢弃**（合包满仍入队，FlushBatch 拆包发出）——只丢可重发
  的周期状态。避免网络分级"吞关键包"导致炮弹落点/装填/交互不同步。
- `FreqMultiplier` = Normal 优先级模块的系数（展示全局档位强度用）；实际驱动见下节 `ModuleFreq`。

## 二点五、per-module 模块优先级（精细化分级）

全局档位之上，**每个模块再按 `NetModulePriority` 单独分级**——同一全局档位下关键模块几乎不降、
高频容忍丢失模块优先降（交互保流畅、高频状态让路）。

```csharp
public enum NetModulePriority { Critical, High, Normal, Low, Bulk }
// ISyncedModule 默认属性：NetModulePriority NetPriority => NetModulePriority.Normal;
// 每模块 Tick 的 dt 乘 NetworkGovernor.ModuleFreq(module.NetPriority)
```

系数表（行=全局档位，列=模块优先级）：

| 全局档位 \ 优先级 | Critical | High | Normal | Low | Bulk |
|---|---|---|---|---|---|
| Critical | 1.00 | 0.80 | 0.55 | 0.40 | 0.25 |
| Low | 1.00 | 0.90 | 0.70 | 0.55 | 0.40 |
| Normal | 1.00 | 0.95 | 0.85 | 0.75 | 0.60 |
| High | 1.00 | 1.00 | 1.00 | 1.00 | 1.00 |

驱动：`CoopSyncRegistry.TickAll` 每模块 `m.Tick(dt × ModuleFreq(m.NetPriority))`；V1 硬编码模块由
`NetManager.UpdateCommon` 逐个指定（PlayerSync/ReloadSync/ControlSync=Critical、MapSync=Normal、
RecordPlayerSync=Low）。已设优先级的高频模块：`EntitySync`/`EntitySyncV2`/`CatSync`/`CatSyncV2`/
`ValueLayer`=**Low**（优先降），`PlayerSyncV2`=**High**，`ControlSyncV2`=**Critical**（几乎不降）。
其他模块默认 Normal。网络诊断 UI（F9 循环第 2 档）显示当前档位下 5 档优先级系数。

**管理模型**：模块**自声明** `NetPriority`（上报），系数计算与 dt 缩放由 `NetworkGovernor`/`CoopSyncRegistry`
**统一管理**（模块不直接改频率，只声明自己的重要度）。

**每模块带宽占用统计**：`NetworkGovernor.RecordTypeBytes(msgType, bytes)` 在 `NetManager.EnqueueBatch`
按 MsgType 归因入队字节（窗口 1s 重置）；`GetTypeBytesTop(n)` 取降序 top n——网络诊断 UI/日志显示
每模块产生多少带宽（`MsgType` 枚举名，如 `V2Entity=1234B`）。注：这是"模块产生的数据量"（含可能被
丢弃/限流的量）；实际发送字节见全局 `CurrentSentBytes`。

## 三、动态评估（每 1s 采样）

`Tick(dt)`（`NetManager.UpdateCommon` 每帧调，内部 1s 窗口）：手动锁定（`SetTier`）时**不自动调**。

采样喂入（NetManager 调用）：
- `RecordSent(bytes)`：FlushBatch 实际发出字节（含 peers 倍数）
- `RecordDrop()`：EnqueueBatch **reliable** 超限丢弃（unreliable 不计——本身容忍丢失，且 Critical
  主动关闭时若计入会误判拥塞 → 卡死最低档无法回升，**2026-08-25 已修**）
- `RecordQueue(count)`：FlushBatch 时合包队列长度（峰值）
- `RecordRtt(ms)`：OnPong 算的 PingMs（取窗口内最大；Ping 每 3s 一次）

评估（**多维：流量 / 延迟 / 丢包率 / 队列**，组合判定防单维度误报；`NetworkGovernor.cs` 常量）：
- **流量维度**：发送 >40KB/s（`DownBytesPerSec`）→ 拥塞维度命中；>80KB/s（`HardBytesPerSec`）→ 硬信号单维度即降；<10KB/s（`UpBytesPerSec`）→ 空闲。
- **延迟维度**：RTT 相对基线上升 >150ms（`RttRiseThresholdMs`）→ 拥塞维度命中；差 <60ms（`RttHealthyDeltaMs`）→ 健康。基线 `_rttBaseline` 用 EMA（上升慢跟 0.1 / 下降快跟 0.5）——**稳定高 RTT（固有/模拟延迟，如 `--lag 170` → ~450ms）不误判**（旧绝对阈值 RTT>180ms 会持续降级到 Critical → 同步变慢）。
- **丢包率维度（2026-08-25 新增）**：Ping/Pong unreliable 探测，滚动窗口 24s（≈8 样本），`RecordPingSent`/`RecordPongRecv` 喂入（NetManager 发 Ping/收 Pong）；>10%（`DownLossRate`）→ 拥塞维度命中；>25%（`HardLossRate`）→ 硬信号单维度即降；<3%（`UpLossRate`）→ 健康；样本 <4 忽略维度（`MinLossSamples`）。
- **队列维度**：合包队列峰值 ≥ 当前档 MaxBatchItems → 拥塞维度命中。
**降级** = ≥2 个维度同时拥塞（组合，防单维度误报）或任一硬信号（流量爆表/高丢包/合包主动丢弃 `_dropCount>0`）；**升档** = 全维度健康（无丢弃、流量<10KB/s、延迟相对健康、丢包<3%、队列<60%）。升降级后冷却 3s（升档 1.8s）——**滞回防抖动**。档位变化打
`CoopLog.Info("net.governor", ...)`：`[NetGovernor] DOWN/UP 旧→新 sent=... drop=... rtt=...ms base=...ms loss=..% peak=.. dims=N`。

## 四、接入点（代码）

| 位置 | 改动 |
|---|---|
| `NetManager.UpdateCommon` | 开头 `NetworkGovernor.Instance.Tick(dt)`；V1 模块逐个 `Tick(dt × ModuleFreq(优先级))`（PlayerSync/ReloadSync/ControlSync=Critical、MapSync=Normal、RecordPlayerSync=Low）；心跳 Ping 用**原始 dt**（RTT 测量不被缩放） |
| `CoopSyncRegistry.TickAll` | 每模块 `m.Tick(dt × ModuleFreq(m.NetPriority))`（per-module 分级，`ISyncedModule.NetPriority` 默认 Normal） |
| `NetManager.EnqueueBatch` | `maxItems = MaxBatchItems`；reliable 超限丢弃 → `RecordDrop()` |
| `NetManager.FlushBatch` | 拆包 `maxPacketBytes = MaxPacketBytes`；发送字节/队列峰值喂 `RecordSent/RecordQueue`；`AllowUnreliable=false` 时清空 unreliable 队列（不发送） |
| `NetManager.OnPong` | `RecordRtt(PingMs)` |
| `NetManager.FlushBatch` | 各发送路径发送前 `NetLagSim.AllowSend(packetLen, reliable)`——模拟 Steam P2P 带宽/单包/丢包限制，拒绝则丢弃该组/条 |
| `AutoJoin.ParseCommandLine` | `--nettier <critical\|low\|normal\|high>` → `SetTier(ParseTier(v))`（手动锁定；无参数/非法 = 自动）；`--netcap <KB/s>` / `--netpacket <B>` / `--netloss <%>` → `NetLagSim.Configure`（带宽/单包/丢包模拟） |

## 五、手动 / 自动

- **自动**（默认）：`Tick` 每 1s 评估自动升降级。
- **手动锁定**：`NetworkGovernor.Instance.SetTier(NetQualityTier.X)`（UI/脚本）或命令行
  `--nettier high` 等。`SetTier(null)` 恢复自动。手动锁定期间 `Tick` 不评估（`_manual` 守卫）。
- 查询：`Tier` / `IsManual` / `FreqMultiplier` / `ModuleFreq(NetModulePriority)` / `MaxBatchItems` /
  `MaxPacketBytes` / `UnreliableMaxBytes` / `AllowUnreliable` / `CurrentSentBytes` / `CurrentDropCount` /
  `CurrentQueuePeak` / `CurrentRttMs`（采样窗口只读，网络诊断 UI 显示用）。

## 六、网络环境限制模拟（Steam P2P 硬限制）

⚠️ **Steam 官方无公开固定带宽数值**；公开硬限制是**包级**（unreliable 单包 ~1200B、reliable 1MB），
实际瓶颈是"高频小包 + 每包固定开销 → 包率限制"。模拟器用可配置参数近似（`NetLagSim` 扩展）：

| 参数 | 说明 | 模拟机制 |
|---|---|---|
| `--netcap <KB/s>` | 发送带宽上限（0=不限） | 令牌桶：容量=1s 配额、补充=CapKBps/s，超限丢弃 + 日志节流 |
| `--netpacket <B>` | 单包上限（0=不限） | 超上限整包拒绝（近似 unreliable ~1200B 硬限制） |
| `--netloss <%>` | unreliable 丢包率（0-99） | 发送端按概率丢弃 unreliable 包 |
| `--lag/--lagjitter` | 延迟/波动（原有） | 接收路径延迟队列 |

接入：`NetLagSim.AllowSend(bytes, reliable)` 在 `FlushBatch` 各发送路径发送前调用（reliable 广播/上行 +
unreliable 单条）——拒绝则丢弃（reliable 近似拥塞丢包、unreliable 正常丢包语义）；未配置（全 0）直通。

**双端测试默认开启**：`dualtest.ps1` 已加 `-NetCap/-NetLoss/-NetPacket` 参数，默认从 env.ps1
（`LocalTestNetCapKBps=200` / `LocalTestNetLossPercent=5` / `LocalTestNetPacketCapB=1200`）取值并传给两端
`--netcap 200 --netpacket 1200 --netloss 5`——模拟 Steam P2P 社区安全范围上界的带宽 + 轻微丢包。
`-NetCap 0 -NetLoss 0 -NetPacket 0` 全部关闭（本地回环 TCP 模式同样生效，因为限制在 NetManager 层）。

## 七、网络诊断 UI

`Debug/NetworkGovernorDebugUI`（CoopRuntime 挂载，`AddComponent`）：由 `DiagCycleController` 控制显示（**F9 循环**第 2 档，右上角 IMGUI 面板——三个诊断 UI 统一右上角，同一时刻只显示一个）：
- 会话状态/成员数（含本地回环标记）
- NetworkGovernor 档位（手动/自动）+ 频率系数 + 合包上限/拆包阈值 + unreliable 状态
- 当前档位下 5 档**优先级缩放系数**（关键/高/常/低/批）——per-module 分级实时预览
- **每模块带宽占用 top5**（`MsgType=字节`，如 `V2Entity=1234B`）——定位流量大头模块
- 当前采样窗口：发送字节 / reliable 丢弃 / RTT / 队列峰值（NetworkGovernor 只读属性）
- NetLagSim 模拟配置：延迟/带宽/单包/丢包
按键检测由 `DiagCycleController` 统一（F9 循环，新 Input System `Keyboard.current.f9Key`；旧 `UnityEngine.Input`
在 IL2CPP 下禁用）。

**独立日志**：切入本档时打印一行 `[NetDiag] dump ...`（key=`net.diag`，独立可 grep）：档位/参数 + 采样 +
每模块带宽 top8 + 模拟配置。NetManager 另有 10s 汇总日志（key=`Net.stats10s`，per-MsgType **包计数** R#/S#）。
帧侧诊断（每模块 CPU 开销，F9 循环第 1 档）见 `docs/FRAME_DIAG.md`。

## 八、验证方法

1. 双端 `dualtest.ps1 -Local`（本地 TCP 不丢包，仅验证代码路径与档位切换日志）。
2. Steam 真实联机（P2P 丢包/拥塞才触发降档）：
   - 主机日志应见 `[NetGovernor] DOWN 3→2 sent=... drop=... rtt=...`（高负载时自动降档保护）；
   - 手动 `--nettier low` 启动一端 → 日志 `[NetGovernor] manual tier=Low`，该端模块 Tick 频率降一半。
3. 任务场景（大量实体移动）内观察：高负载 → 自动降到 Normal/Low → 合包丢弃减少、RTT 回落 →
   空闲后自动回升 High（`[NetGovernor] UP ...`）。
4. 按 **F9** 循环到网络诊断 UI（第 2 档），观察档位/采样/模拟配置实时变化。
5. 模拟限流验证：`--netcap 20 --netloss 10` 启动一端 → 日志 `[NetLagSim] configured ... cap=20KB/s ... loss=10%`；
   负载下 `[NetLagSim] bandwidth cap ... exceeded` / `unreliable packet dropped` 节流日志；NetworkGovernor 因
   丢弃/发送超限自动降档 → 档位回落保护。`--netpacket 200` 可强制触发单包超限（模拟小 MTU）。
