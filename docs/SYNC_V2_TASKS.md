# Open Nest Co-op — 同步方案并行开发任务表（2026-08-15）

> 双轨道并行：**轨道 A（优化）** 保持旧方案稳定并优化；**轨道 B（重构）** 开发 SyncV2 分层新方案。
> 通过启动参数 `--sync old|new` 切换，互不阻塞。

---

## 轨道 A：旧方案优化（当前 AI 负责）

### A0. Feature Flag 骨架（先做，V2 的挂载点）
- [ ] `AutoJoin.ParseCommandLine()` 加 `--sync old|new`（默认 old，字段 `WantNewSync`）
- [ ] `CoopRuntime.Startup()` 按方案分支：`--sync new` → `SyncV2Bootstrap.RegisterAll()`；否则现有模块注册
- [ ] Hello/Welcome 握手带 `syncVersion`，两端不一致拒绝
- [ ] 双端 `--sync old` 回归无变化；`--sync new` 能启动（V2 骨架空注册也不崩）
- 验证：`.\scripts\dualtest.ps1 -Local`（默认 old 全绿）

### A1. 发送/接收路径堆分配池化（GC 热点，最高收益）
- [x] `SteamTransport.Send` 增加 `(byte[] data, int len)` 重载，跳过 `NetProtocol.Snapshot` 中间副本（ITransport 同步加重载，合包路径已直接用 `writer.Data/len` 发送，省每帧合包 byte[] 副本）
- [x] `NetDataWriter` 对象池（`Reset()` 复用内部 buffer）—— `NetProtocol.Begin` 池取 + `Snapshot` 自动回池（72 处调用点零改动、V2 透明受益）
- [x] `SteamTransport` 复用发送 buffer 池（`SendP2PPacket` 同步拷贝可安全复用，`_sendBuf` EnsureCapacity）
- [ ] 收包 `Poll` 池化接收缓冲 + Batch 子包复用单个 reader —— **低优先级，暂缓**：data 被 `OnPacket`→`TryRoute`/各模块分散消费，需全局确认所有消费者同步用完才安全，收益中等、风险高，留作后续
- [x] `FlushBatch` 的 Writer 提为字段复用（`_batchWriter` Reset 重用）
- ⚠️ 保持 Batch 解析现有正确写法（`Array.Copy(RawData, Position,...)` + `SkipBytes`，勿改 `GetRemainingBytes`）
- 验证：✅ 双端 `dualtest.ps1 -Local` 回归通过（`state=Hosting peers=1`，`flush`/`recv batch`/`ControlState:S98` 双向正常，无 mismatch 无数据损坏）

### A2. 周期性全场景扫描缓存（周期性卡顿）
- [x] `ControlSync.Rescan`（每 3s 4 次 FindObjectsOfTypeAll）→ **并行方案**：OnEnable 逐控件注册（主路径，Harmony patch Dial/Slider/Spinner OnEnable）+ Rescan 5s 保底（detectMiss 区分初始化/漏检）+ 独立日志 `ControlSync.onEnableMiss` 记录 OnEnable 未覆盖控件
- [x] `ButtonClickSync.PollToggleStates`（0.8s FindObjectsOfType<LookAtTarget>）→ `GetTargets()` 实例缓存（5s 刷新 + 场景切换即时刷新）；低频事件处保留即时扫描保证正确性
- [x] `CatSync` 多处 FindObjectsOfType<CatController>（5 处）→ `GetCats()` 猫实例缓存（3s 刷新 + 场景切换即时刷新，顺序 = FindObjectsOfType 顺序保跨端索引一致）
- [x] `ReloadSync.ResolveGuns` 扫描 → 已有缓存（`_cachedTurret == turret.Pointer` 脏标志，turret 为 null 重置）
- 验证：✅ 双端 `dualtest.ps1 -Local` 回归通过（`ControlSync registered=56`、`ValueSync bindings=62`、`CatSync n=1`、`ReloadSync host recv cmd` 全部正常）

### A3. 其他低风险优化
- [x] `NetManager` 内 LINQ（Where/First/Any）→ for 循环（Roster ≤8，10 处机械转换）
- [ ] `ValueSync` 每帧两遍遍历 `_bindings.Values` 合并单遍 —— **评估后不做**：两遍遍历是不同语义（拖拽检测 vs 远端插值），合并易破坏拖拽/插值逻辑，62 个 binding 收益极小
- [x] `PlayerSync._fpc` 查找失败退避（1s 重查，`_fpcRetryTimer`；查找失败时不再每帧 FindFirstObjectByType）
- 验证：✅ 双端 `dualtest.ps1 -Local` 回归通过（`state=Hosting peers=1`、`ControlState:S98`、`ReloadSync recv` 正常）

---

## 轨道 B：SyncV2 分层新方案（另一个 AI 负责）

> 开发文档：`docs/SYNC_V2_DEV.md`（必读）+ `docs/ARCHITECTURE.md`。

### B0. 骨架（与 A0 对接）
- [ ] `SyncV2/` 目录 + `SyncV2Bootstrap.RegisterAll()`
- [ ] 读 `--sync` 参数，`--sync new` 时走 V2 注册
- [ ] MsgType ≥200 独立段

### B1. 分层实现（里程碑 M2-M5）
- [ ] `HostDataLayer`：IHostStore/IRoleAuthority（主机读写/授权/广播）
- [ ] `ValueLayer`（MsgType=201）：值同步（迁移 ValueSync 一个 Binding 验证）
- [ ] `EventLayer`（MsgType=200）：泛型事件广播+复现+防环
- [ ] `ButtonLayer`（MsgType=202）：按钮 toggle 状态

### B2. 模块迁移（里程碑 M6-M7，迁移即吸收优化）
- [ ] ControlSync 发现逻辑 → HostDataLayer（吸收 Rescan 扫描优化）
- [ ] 按 ARCHITECTURE.md 顺序逐模块迁移
- 每个里程碑双端 `--sync new` 验证

### B3. 收尾（里程碑 M8）
- [ ] V1 保留（old 稳定线），V2 全绿
- [ ] `--sync old` 与 `--sync new` 独立可跑

---

## 并行协调

| 事件 | 动作 |
|---|---|
| A0 完成 | 通知 B：`--sync` 骨架就绪，V2 可挂载 |
| B0 完成 | 通知 A：V2 空注册不崩，A 可继续 A1-A3 |
| A1 完成 | 双方受益（基础设施共享），无阻塞 |
| 任一轨道出问题 | 回退 `--sync old`（V1 稳定线不受影响）|

## 验证命令

```powershell
# 双开（默认 old 方案）
.\scripts\dualtest.ps1 -Local
# 双开（new 方案）—— 需 dualtest 支持传 --sync 给两端 exe（见 A0）
.\scripts\dualtest.ps1 -Local -Sync new
# 构建
dotnet build src\OpenNestCoop\OpenNestCoop.csproj -c Release -p:DeployToGame=true
dotnet build src\OpenNestCoop.MelonMod\OpenNestCoop.MelonMod.csproj -c Release -p:DeployToMods=true
```
