using Il2CppInterop.Runtime.Injection;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;
using OpenNestCoop.Patches;
using OpenNestCoop.UI;
#if MELONLOADER
using Steamworks = Il2CppSteamworks;
#endif

namespace OpenNestCoop.Core;

/// <summary>
/// 平台无关的联机运行时核心。BepInEx / MelonLoader 双入口壳只负责：
///   1) 调用 <see cref="Initialize"/> 注入平台日志与（可选的）宿主实例；
///   2) 在 Unity 场景就绪后调用 <see cref="Startup"/> 完成类型注入 + 模块注册 + Harmony。
/// 核心本身不引用任何平台 API（BepInEx / MelonLoader）。
/// </summary>
public static class CoopRuntime
{
    /// <summary>平台日志（由入口壳注入）。核心代码通过 <see cref="LogSource"/> 打日志。</summary>
    public static ILogger LogSource;

    /// <summary>全局联机管理器（会话状态机 + Steam 大厅 + P2P 传输）。</summary>
    public static NetManager Net;

    /// <summary>入口壳实例（BepInEx Plugin / MelonLoader Mod）。核心不依赖其类型，仅供兼容旧引用。</summary>
    public static object HostInstance;

    private static bool _started;

    /// <summary>入口壳在 Load/OnInitializeMelon 时调用：注入平台日志（同时供 OpenNestCore.CoopLog 使用）。
    /// ⚠️ 2026-08-26：把 LogSource 包一层 <see cref="RoutingLogger"/>——同步模块大量 `LogSource?.LogInfo("[XSync] ...")`
    /// 直调绕过 CoopLog key 路由，混进主日志刷屏；RoutingLogger 按消息前缀自动路由到独立文件（sync.log）。</summary>
    public static void Initialize(ILogger logger)
    {
        LogSource = new OpenNestCore.Logging.RoutingLogger(logger);
        OpenNestCore.Logging.CoopLog.SetLogSource(logger);
    }

    /// <summary>入口壳在 Unity 场景就绪后调用：建立网络 + 类型注入 + 模块注册 + Harmony。</summary>
    public static void Startup()
    {
        if (_started) return;
        _started = true;

        LogSource?.Info($"{NetConfig.Name} v{NetConfig.Version} started (platform-agnostic core). Steam running: {Steamworks.SteamAPI.IsSteamRunning()}");

        // 配置文件（标准 INI：BepInEx/config 或 UserData/OpenNestCoop.cfg；缺失则生成默认 + 注释）
        // ⚠️ 必须在模块注册/首次 Tick 前加载——各同步模块在 Tick/OnPacket 里读 CoopConfig 决定是否同步。
        CoopConfig.Init();

        // 独立文件日志：诊断/联机日志 → frame/net/sync 独立 .log（主日志安静 → 控制台不刷屏 → 帧性能提升）
        InitFileLogs();

        Net = new NetManager();
        Net.Init();

        // 自动联机参数（--autohost / --autojoin）：免虚拟机双开测试
        OpenNestCoop.Net.AutoJoin.ParseCommandLine();

        // 挂载 Unity 行为：驱动联机 Update + UGUI 联机菜单 + 可交互物品名字调试工具（F9）
        ClassInjector.RegisterTypeInIl2Cpp<CoopBehaviour>();
        AddComponent<CoopBehaviour>();
        ClassInjector.RegisterTypeInIl2Cpp<CoopUIManager>();
        AddComponent<CoopUIManager>();
        ClassInjector.RegisterTypeInIl2Cpp<Debug.InteractableNameTool>();
        AddComponent<Debug.InteractableNameTool>();
        // 网络诊断 UI：NetworkGovernor 分级/参数/采样 + NetLagSim 模拟配置（F9 循环内显示）
        ClassInjector.RegisterTypeInIl2Cpp<Debug.NetworkGovernorDebugUI>();
        AddComponent<Debug.NetworkGovernorDebugUI>();
        // 帧性能诊断菜单：FPS/帧时间 + 每模块 CPU 开销（FrameProfiler 统计；F9 循环内显示）
        ClassInjector.RegisterTypeInIl2Cpp<Debug.FrameDiagUI>();
        AddComponent<Debug.FrameDiagUI>();
        // 自定义任务引擎节点图完整可视化（F9 循环内显示：节点逻辑连线 + 内容 + 状态着色 + 缩放/平移/选中检查器）
        ClassInjector.RegisterTypeInIl2Cpp<Debug.MissionGraphViewUI>();
        AddComponent<Debug.MissionGraphViewUI>();
        // 诊断菜单循环切换器（F9）：不显示→帧→网络→任务→交互→不显示 循环（替代 F7/F8/F9 独立按键）
        ClassInjector.RegisterTypeInIl2Cpp<Debug.DiagCycleController>();
        AddComponent<Debug.DiagCycleController>();

        // Harmony 补丁（M2：炮塔输入/开火同步）
        HarmonyPatches.Apply();

        // 同步方案：--sync new 走 SyncV2 分层（测试版，不注册旧模块）；默认 old 走 V1 稳定线。
        // 双端需同方案（Hello/Welcome 握手校验，见 NetManager）。
        // ⚠️ 模块自注册：每个模块在自己文件里用 [ModuleInitializer] 入队（CoopSyncRegistry.PendingRegister，
        // 按方案 V1/V2 标记），这里解析 --sync 方案后统一冲刷（V1/V2 互斥，只注册当前方案对应的模块）。
        CoopLog.Info("Coop.syncScheme", () => $"sync scheme: new={OpenNestCoop.Net.AutoJoin.WantNewSync}");
        CoopSyncRegistry.FlushPending();

        // 提前触发 Animator 化身 AssetBundle 异步加载（本地 file:// 帧内完成，玩家加入时通常已就绪）
        AnimatorAvatarVisualProvider.Instance.TryLoad();

        // 自定义任务：从外部游戏目录 CSM 文件夹按文件名序读取 JSON 任务/战役并注册（文件夹不存在则静默跳过）
        try { OpenNestCoop.GameSync.OncMissionBridge.LoadFromGameFolder(); }
        catch (System.Exception ex) { LogSource?.LogWarning($"[CoopRuntime] OncMission LoadFromGameFolder: {ex.Message}"); }
        // 脚本化模块：注册内置示例模块（announce/ping），模组可再用 RegisterScriptedModule 注册自己的
        try { OpenNestCoop.GameSync.OncMissionBridge.RegisterBuiltinScriptedModules(); }
        catch (System.Exception ex) { LogSource?.LogWarning($"[CoopRuntime] OncMission RegisterBuiltinScriptedModules: {ex.Message}"); }

        // 游戏原生 UI 桥接（OpenNestCore.UI.NativeUi）：本地化/通知/ESC/主菜单/光标能力抽象，供模组平台无关调用
        IronNestNativeUi.Hook();

        LogSource?.Info($"local player: {Net.Local?.Name} (SteamID {Net.Local?.SteamId})");
    }

    /// <summary>独立文件日志初始化 + 路由注册（诊断/联机日志 → frame/net/sync 独立 .log，主日志只留会话/错误）。
    /// 文件目录：游戏目录/OpenNestLogs。写入用 ModLog 缓冲批量落盘（1s 间隔），性能好。</summary>
    private static void InitFileLogs()
    {
        try
        {
            var dir = System.IO.Path.Combine(System.Environment.CurrentDirectory, "OpenNestLogs");
            OpenNestCore.Logging.ModLog.Init(dir);
        }
        catch { }
        // 帧/网络诊断 → 独立文件
        CoopLog.RouteToFile("frame", "frame");  // FrameDiagUI LogDump / 帧诊断
        CoopLog.RouteToFile("net.", "net");     // NetworkGovernor 调控（net.governor）/ net.diag
        CoopLog.RouteToFile("Net.", "net");     // NetManager 网络日志（Net.stats10s/Net.recvBatch/Net.flush/...）
        // 落点/照片诊断 → sync（HarmonyPatches ImpactDiag/PhotoDiag；key=impact.diag/photo.diag）
        CoopLog.RouteToFile("impact.", "sync");
        CoopLog.RouteToFile("photo.", "sync");
        CoopLog.RouteToFile("shot.", "sync");   // 炮弹发射参数（ShotSync）+ 发射参数诊断（[ShotDiag]）
        CoopLog.RouteToFile("blocker.", "sync"); // 锁止组件（BlockerSync）
        CoopLog.RouteToFile("BlockerSync", "sync");
        // ⚠️ 2026-08-30：自定义任务诊断 → 独立 mission.log（OncMissionBridge 全部 onc.mission.* + 节点诊断 UI mission.diag）
        CoopLog.RouteToFile("onc.mission.", "mission");
        CoopLog.RouteToFile("mission.diag", "mission");
        // ⚠️ 2026-08-26：.Charge Dial 值源诊断（HarmonyPatches PostDialValueChanged）→ sync
        CoopLog.RouteToFile("chargedial.", "sync");
        // 联机同步模块 → sync 文件（主日志/控制台不刷屏 → 帧性能提升）
        string[] sync = {
            "CatSync","SyncV2","ControlSync","ValueSync","Teleprinter","Requisition",
            "GunLinkSync","PunchcardSync","RecordItemSync","MapMarkerSync","ChargeButtonSync",
            "ChargeInventorySync","MissionSync","MissionEventSync","EntitySync","ReloadSync",
            "CounterBattery","ReconPhoto","SequenceSync","HatchSync","ShellSync","CoffeeSync",
            "ArmSync","CylinderActionSync","PurchaseSync","MapTokenSync","NotificationSync",
            "RecordPlayerSync","StateSnapshot","ButtonClickSync",
        };
        foreach (var p in sync) CoopLog.RouteToFile(p, "sync");
        // ⚠️ 2026-08-26：RoutingLogger（LogSource 直调兜底）同步前缀路由——同源 sync 数组 + 直调消息前缀补充，
        // 把 `LogSource?.LogInfo("[XSync] ...")` 直调也路由到 sync.log（主日志不再刷屏）。
        // ⚠️ 消息前缀 ≠ 模块 key：直调用 `[XSync]` 文本前缀，按实际消息补齐（Impact/MissionEvent/Purchase/Notification 等）。
        string[] syncMsgPrefix = {
            "CatSync","SyncV2","ControlSync","ValueSync","Teleprinter","Requisition",
            "GunLinkSync","PunchcardSync","RecordItemSync","MapMarkerSync","ChargeButtonSync",
            "ChargeInventorySync","MissionSync","MissionEventSync","EntitySync","ReloadSync",
            "CounterBattery","ReconPhoto","SequenceSync","HatchSync","ShellSync","CoffeeSync",
            "ArmSync","CylinderActionSync","PurchaseSync","MapTokenSync","NotificationSync",
            "RecordPlayerSync","StateSnapshot","ButtonClickSync",
            // ⚠️ 直调消息前缀补充（模块 key 可能是 XxxSync，但 LogSource 直调用 [Xxx] 短名）
            "ImpactSync","Impact","MissionEvent","Mission","Purchase","PurchaseV2","Notification",
            "ShotSync","ShotDiag",
            "MapSync","M3Env","M3EnvV2","TurretSync","PlayerSync","PlayerSyncV2","XSync",
            "CatSyncV2","CoffeeSyncV2","MissionSyncV2","PunchcardSyncV2","RecordItemSyncV2",
            "ReloadSyncV2","RecordPlayerSyncV2","MapMarkerSyncV2","HatchSyncV2","GunLinkSyncV2",
            "SequenceSyncV2","ShellSyncV2","MapTokenSyncV2","TeleprinterSyncV2",
        };
        foreach (var p in syncMsgPrefix) OpenNestCore.Logging.RoutingLogger.RouteToFile(p, "sync");
    }

    /// <summary>会话结束/卸载时释放网络 + 清理 AssetBundle 生命周期。</summary>
    public static void Shutdown()
    {
        // 游戏原生 UI 桥接卸载（清注册 + 销毁轮询 Behaviour）
        try { IronNestNativeUi.Unhook(); } catch { }
        try { Net?.Shutdown(); } catch { }
        // ⚠️ AssetBundle 生命周期（OpenNestCore.Assets.AssetBundleIron）：
        // 模组卸载 / 游戏退出时按契约清理全部 bundle（各持有方应已销毁实例；引用归零后 Unload(false) + 关闭 FileStream）。
        // 注意：联机会话结束（回大厅）**不**走这里——玩家模型 bundle 跨会话复用，不重复加载。
        try { OpenNestCore.Assets.AssetBundleIron.UnloadAll(); } catch { }
        _started = false;
    }

    /// <summary>
    /// 由入口壳提供"把 Il2Cpp 托管类型注入并挂到场景"的能力。
    /// BepInEx 用 ClassInjector.RegisterTypeInIl2Cpp + AddComponent；
    /// MelonLoader 用 ClassInjector.RegisterTypeInIl2Cpp + GameObject.AddComponent。
    /// </summary>
    internal static void AddComponent<T>() where T : UnityEngine.Component
    {
        var go = new UnityEngine.GameObject($"[OpenNestCoop]{typeof(T).Name}");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<T>();
    }
}
