# 帧性能诊断（FrameProfiler）

> **目的**：统计**每个同步模块/网络核心路径占有的帧 CPU 开销**（每秒 ms），排查"哪个模块占帧/掉帧元凶"
> 的性能问题。诊断菜单经 **F9 循环切换**（不显示→帧→网络→交互→不显示，见 `DiagCycleController`）。2026-08-25。
>
> **关联**：`docs/NETWORK_GOVERNOR.md`（网络负载诊断，网络侧）、`docs/DEVELOPMENT.md`（调试工具）。
> **代码**：`src/OpenNestCore/FrameProfiler.cs`（统计）+ `src/OpenNestCoop/Debug/FrameDiagUI.cs`（UI）+
> 测量点（`CoopSyncRegistry.TickAll` / `NetManager.UpdateCommon`）。

---

## 更新记录

- 2026-08-25 初稿：FrameProfiler（按测量点统计每秒 CPU 耗时）+ 帧性能诊断菜单（屏幕 + 独立日志 dump）。
- 2026-08-25 按键统一：F7/F8/F9 独立按键废弃，三个诊断（帧/网络/交互工具）由 `DiagCycleController` 按 **F9 循环切换**（不显示→帧→网络→交互→不显示）；F10 复制剪贴板保留。
- 2026-08-25 UI 可读性：帧诊断菜单**逐行着色**（标题亮黄大字号；FPS/平均帧/最差帧按健康度 绿/黄/红；模块耗时 ≥5ms 红、≥2ms 黄、其余灰，数值行右对齐）。⚠️ IL2CPP interop 限制：`GUIStyle` 拷贝构造编译不过（interop 只生成 IntPtr 构造）；`TextAnchor`/`FontStyle` 需引用未引用的 `UnityEngine.TextRenderingModule`；**且 `new GUIStyle()` 无参构造 + `GUILayout` 在 IL2CPP 运行时导致 OnGUI 异常被吞 → 整个诊断 UI 不渲染（用户“F9 呼不出来”）**。**最终稳妥方案**：不创建 GUIStyle / 不用 GUILayout——`GUI.contentColor` 逐行着色 + 纯 `GUI.Label(new Rect(...))` 手动定位；右对齐数值放固定右侧 rect。
- 2026-08-25 **独立日志文件**：帧诊断改写到**独立 `OpenNestLogs/frame.log`**（`ModLog` 缓冲批量落盘，1s 间隔；`CoopLog.RouteToFile("frame","frame")` 路由），不再打主日志/控制台（主日志安静 → 帧性能提升）。按 F9 开帧诊断档后每 5s 追加一条（key=frame.diag），帧性能数据查 `frame.log` 即可（不必翻主日志）。
- 2026-09-13 **掉帧排查增强（Steam 主机建房后掉帧）**：① 补上 **Steam 专属测量段**（`Steam.Probe`/`Steam.Callbacks`/`Steam.AutoJoin`/`Steam.LobbyList`/`Steam.Poll`）——此前测量点只盖到模块 Tick 与 `UpdateCommon`，而“本地模式不掉帧、只有 Steam 掉帧”的路径全在盲区，等于没有归因数据；② 联机菜单整页刷新单独归因 `UiKitRefresh`（`UIKitIntegration.AutoRefresh`）；③ 加 **`--framediag` 启动参数**（免按 F9，启动即进帧诊断档；`DiagCycleController` 只扫一次命令行，不每帧扫）；④ **尖峰即时落盘**：结算窗口内出现 >60ms 的帧就立刻追加一条带 `[SPIKE]` 的 dump（≥1.5s 间隔防刷屏）——抓间歇性掉帧不再靠碰运气；⑤ `Steam.Probe`（`SteamAPI.IsSteamRunning` + `SteamUser.GetSteamID`）**降频到 2Hz**——实测每帧 ~0.12ms（8–14 ms/s）纯浪费，`SteamReady` 半秒延迟无影响。

> **本项排查的另一半（代码侧）**见 `docs/LOBBY.md`（`HostSteamId` 由每读一次调 Steam API 改为缓存）与 `KNOWN_ISSUES.md`（“创建大厅后主机个位数 FPS”条目）。

## 一、测量点（归因）

用 `System.Diagnostics.Stopwatch.GetTimestamp()`（高精度 QueryPerformanceCounter，IL2CPP 可用，无托管分配）
逐段计时，按名归因累计到 1s 窗口：

| 测量点 | 位置 | 归因名 |
|---|---|---|
| 每个注册模块 `Tick` | `CoopSyncRegistry.TickAll`（每模块） | 模块类型名（如 `EntitySync`/`EntitySyncV2`/`MissionSyncV2`） |
| V1 硬编码模块 `Tick` | `NetManager.UpdateCommon` | `PlayerSync`/`RecordPlayerSync`/`ReloadSync`/`MapSync`/`ControlSync` |
| 网络负载调控器 | `NetManager.UpdateCommon` | `NetworkGovernor` |
| 注册模块整体 Tick | `NetManager.UpdateCommon` | `TickAll` |
| 帧末合包发送 | `NetManager.UpdateCommon` | `FlushBatch` |
| Steam 就绪探测（2Hz 节流） | `NetManager.Update`（Steam 专属路径） | `Steam.Probe` |
| Steam 回调泵 | `NetManager.Update` | `Steam.Callbacks` |
| 自动加入 | `NetManager.Update` | `Steam.AutoJoin` |
| 大厅列表刷新 | `NetManager.Update` | `Steam.LobbyList` |
| P2P 收发轮询 | `NetManager.Update` | `Steam.Poll` |
| 联机菜单整页刷新 | `UI/UIKitIntegration.AutoRefresh`（0.6s 节流 + 状态指纹 + 非当前页跳过） | `UiKitRefresh` |

帧时间（FPS/平均/最差）由帧诊断 UI（F9 循环第 1 档）每帧喂 `FrameProfiler.RecordFrameMs`（常驻累计，即使不显示）。

## 二、诊断菜单

`Debug/FrameDiagUI`（CoopRuntime 挂载）：由 `DiagCycleController` 控制显示（F9 循环第 1 档，右上角 IMGUI 面板）：
- **FPS / 平均帧 / 最差帧**（ms）
- **模块帧开销 top8**（每秒 ms，降序）——如 `EntitySync: 12.30ms`，一眼定位流量大头/占帧模块
- 显示时打印一行独立日志 `[FrameDiag] dump ...`（key=`frame.diag`）：FPS/帧时间 + 每模块 top10；
  结算窗口内出现 >60ms 帧时额外追加 `[SPIKE]` 行（`[FrameDiag] F7 dump[SPIKE] ...`）。
  注意：**只有处于帧诊断档时**才有周期 dump，所以抓间歇性掉帧建议用 `--framediag` 启动（见 §三.5）。

三个诊断 UI **统一右上角**（同一时刻只显示一个，F9 循环不重叠）。
按键统一：**F9** 循环切换；**F10** 复制当前交互信息到剪贴板（仅交互工具档）。

## 三、用法

1. 启动游戏进入联机（测量点在联机驱动路径——主菜单 Idle 时模块不跑，无数据）。
2. 按 **F9** 循环到帧性能菜单 → 看 FPS 与各模块 ms/s（每按一次在 帧→网络→交互→不显示 间切）。
3. 排查掉帧：按 ms/s 降序找占帧大头（如某模块 FindObjectsOfType 全场景扫描、高频轮询）。
4. 再按 F9 切到下一档/不显示（每次切入都会 dump 一次 `frame.diag` 日志）。
5. **`--framediag` 启动参数**（2026-09-13）：启动即进帧诊断档（不必按 F9、不必手动开），全程产出 `frame.log`，且每次 >60ms 的尖峰都会自动落一条 `[SPIKE]`。
   适合抓“偶发/间歇”的掉帧：
   - BepInEx（G 端）：给 `BepInEx\config\BepInEx.cfg` 的启动参数或 Steam 启动项加 `--framediag`；
   - 直接用 Steam 启动：`steam.exe -applaunch <AppID> --autohost --framediag`。
   事后只读 `<游戏目录>\OpenNestLogs\frame.log`：`fps` 掉到个位数的那几行，`per-module ms/s` 降序就是当时的占帧元凶（模块名或 Steam 段）。
   ⚠️ 判读要点：**启动前 20s 的 `[SPIKE]`（数百 ms）是加载期正常现象**（注册表/回调/资源），
   要与“建房后持续低 fps”区分开——后者会在稳态窗口里反复出现 `[SPIKE]` 且 fps 一直个位数。
