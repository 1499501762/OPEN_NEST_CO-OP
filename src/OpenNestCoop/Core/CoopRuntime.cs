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

    /// <summary>入口壳在 Load/OnInitializeMelon 时调用：注入平台日志（同时供 OpenNestCore.CoopLog 使用）。</summary>
    public static void Initialize(ILogger logger)
    {
        LogSource = logger;
        OpenNestCore.Logging.CoopLog.SetLogSource(logger);
    }

    /// <summary>入口壳在 Unity 场景就绪后调用：建立网络 + 类型注入 + 模块注册 + Harmony。</summary>
    public static void Startup()
    {
        if (_started) return;
        _started = true;

        LogSource?.Info($"{NetConfig.Name} v{NetConfig.Version} started (platform-agnostic core). Steam running: {Steamworks.SteamAPI.IsSteamRunning()}");

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

        // Harmony 补丁（M2：炮塔输入/开火同步）
        HarmonyPatches.Apply();

        // 同步方案：--sync new 走 SyncV2 分层（测试版，不注册旧模块）；默认 old 走 V1 稳定线。
        // 双端需同方案（Hello/Welcome 握手校验，见 NetManager）。
        CoopLog.Info("Coop.syncScheme", () => $"sync scheme: new={OpenNestCoop.Net.AutoJoin.WantNewSync}");
        if (OpenNestCoop.Net.AutoJoin.WantNewSync)
            SyncV2.SyncV2Bootstrap.RegisterAll();
        else
            RegisterLegacyModules();

        // 提前触发 Animator 化身 AssetBundle 异步加载（本地 file:// 帧内完成，玩家加入时通常已就绪）
        AnimatorAvatarVisualProvider.Instance.TryLoad();

        LogSource?.Info($"local player: {Net.Local?.Name} (SteamID {Net.Local?.SteamId})");
    }

    /// <summary>注册 V1（旧方案）全部同步模块——默认方案。--sync new 时不调用。</summary>
    private static void RegisterLegacyModules()
    {
        CoopSyncRegistry.RegisterModule(new CoffeeSync());
        CoopSyncRegistry.RegisterModule(new MissionSync());
        // 中途加入快照容器（MsgType=30）：收集各模块快照打包，新成员收到后分发应用
        CoopSyncRegistry.RegisterModule(new StateSnapshotSync());
        // 注册各模块快照构建/应用（方案 B：状态注册表）
        StateSnapshotSync.Register("mission", MissionSync.BuildMissionSnapshot, MissionSync.ApplyMissionSnapshot);
        StateSnapshotSync.Register("hatch", HatchSync.BuildHatchSnapshot, HatchSync.ApplyHatchSnapshot);
        StateSnapshotSync.Register("sequence", SequenceSync.BuildSequenceSnapshot, SequenceSync.ApplySequenceSnapshot);
        StateSnapshotSync.Register("mapmarker", MapMarkerSync.BuildMapMarkerSnapshot, MapMarkerSync.ApplyMapMarkerSnapshot);
        StateSnapshotSync.Register("maptoken", MapTokenSync.BuildMapTokenSnapshot, MapTokenSync.ApplyMapTokenSnapshot);
        StateSnapshotSync.Register("recordplayer", RecordPlayerSync.BuildRecordPlayerSnapshot, RecordPlayerSync.ApplyRecordPlayerSnapshot);
        // 按钮 toggle 状态快照（指示灯/楼梯盖板等多 toggler 按钮中途加入对齐）
        StateSnapshotSync.Register("button", ButtonClickSync.BuildButtonSnapshot, ButtonClickSync.ApplyButtonSnapshot);
        CoopSyncRegistry.RegisterModule(new MissionEventSync());
        // 任务打字机通知同步（UINotificationManager.ShowNotification 事件，MsgType=131）
        CoopSyncRegistry.RegisterModule(new NotificationSync());
        // 任务打字机打印同步（Teleprinter.SubmitLines/ClearAll/ClearAlarm 事件，MsgType=134）
        CoopSyncRegistry.RegisterModule(new TeleprinterSync());
        CoopSyncRegistry.RegisterModule(new CounterBatterySync());
        CoopSyncRegistry.RegisterModule(new EntitySync());
        CoopSyncRegistry.RegisterModule(new ReconPhotoSync());
        CoopSyncRegistry.RegisterModule(new CatSync(), CatSync.CatEventMsgType);
        CoopSyncRegistry.RegisterModule(new MapMarkerSync());
        CoopSyncRegistry.RegisterModule(new RecordItemSync());
        CoopSyncRegistry.RegisterModule(new ShellSync());
        CoopSyncRegistry.RegisterModule(new SequenceSync());
        CoopSyncRegistry.RegisterModule(new HatchSync());
        CoopSyncRegistry.RegisterModule(new ButtonClickSync(), ButtonClickSync.ToggleStateMsgType);
        CoopSyncRegistry.RegisterModule(new MapTokenSync());
        // 征信点卡牌位置同步（卡牌拖到卡槽插入 → 拉杆购买；卡牌位置/插入状态两端一致）
        CoopSyncRegistry.RegisterModule(new PunchcardSync(), PunchcardSync.CardSlotEventMsgType);
        M3EnvSync.Register();
        RequisitionSync.Register();
        CoopSyncRegistry.RegisterModule(new PurchaseSync());
    }

    /// <summary>会话结束/卸载时释放网络 + 清理 AssetBundle 生命周期。</summary>
    public static void Shutdown()
    {
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
