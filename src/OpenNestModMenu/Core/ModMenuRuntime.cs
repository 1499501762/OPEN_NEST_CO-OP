using System;
using Il2CppInterop.Runtime.Injection;
using OpenNestModMenu.API;
using OpenNestModMenu.Registry;

namespace OpenNestModMenu.Core;

/// <summary>
/// 平台无关运行时骨架（BepInEx / MelonLoader 两个入口壳共用）：
///   1) <see cref="Initialize"/> —— 注入平台日志后端；
///   2) <see cref="Startup"/> —— 路径解析 + 独立文件日志 + Unity 行为挂载 + 标记契约宿主就绪；
///   3) <see cref="Shutdown"/> —— 清理。
///
/// ⚠️ **幂等守卫**：经桥 BepInEx.MelonLoader.Loader 加载时，同一模组可能被两个加载器各初始化一次
/// （历史事故：<c>xxx already injected</c> / TypeLoadException，见 docs/MOD_MENU.md §3.3）。
/// 因此 Initialize/Startup 都做重复调用保护，且挂载失败只降级不抛出。
/// </summary>
public static class ModMenuRuntime
{
    /// <summary>平台日志（由入口壳注入）。</summary>
    public static ILogger LogSource;

    /// <summary>是否已注入日志后端。</summary>
    public static bool IsInitialized => _initialized;

    /// <summary>是否已完成启动。</summary>
    public static bool IsStarted => _started;

    private static bool _initialized;
    private static bool _started;
    private static UnityEngine.GameObject _behaviourGo;

    /// <summary>入口壳调用一次：注入平台日志（同时供 <see cref="CoopLog"/> 使用）。</summary>
    public static void Initialize(ILogger logger)
    {
        if (_initialized)
        {
            // 重复初始化（桥环境双加载）→ 只记一条，不重复挂载
            try { CoopLog.Warn("modmenu.init", () => "Initialize 重复调用（已忽略）"); } catch { }
            return;
        }
        _initialized = true;
        LogSource = logger;
        CoopLog.SetLogSource(logger);
    }

    /// <summary>入口壳调用：建立运行时（路径 → 日志 → 行为挂载 → 契约宿主标记）。</summary>
    public static void Startup()
    {
        if (_started)
        {
            LogSource?.Warn("[OpenNestModMenu] Startup 重复调用（已忽略）");
            return;
        }
        _started = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        ModMenuPaths.Resolve();
        InitFileLogs();

        CoopLog.Info("modmenu.start", () => $"=== {ModMenuInfo.Name} v{ModMenuInfo.Version} ({ModMenuInfo.BuildPlatform} 构建) ===");
        CoopLog.Info("modmenu.start", () => $"game='{ModMenuPaths.GameDir}' shape={ModMenuPaths.Shape}");
        CoopLog.Info("modmenu.start", () => $"BepInEx={(ModMenuPaths.HasBepInEx ? ModMenuPaths.BepInExRoot : "<无>")}");
        CoopLog.Info("modmenu.start", () => $"MelonLoader 侧 root='{ModMenuPaths.MelonRoot}' mods='{ModMenuPaths.MelonModsDir}'");
        CoopLog.Info("modmenu.start", () => $"config='{ModMenuPaths.ConfigFile}' logs='{ModMenuPaths.LogDir}'");

        // 语言键文件（OpenNestModMenu.lang.ini，与配置文件同目录；见 Loc/LocFile.cs）
        ModMenuLoc.Init(ModMenuPaths.LangFile);
        CoopLog.Info("modmenu.loc", () => $"lang='{ModMenuLoc.Current}' file='{ModMenuPaths.LangFile}'");

        // 本模组自己的偏好（`OpenNestModMenu.cfg`）：目前就一个「调试模式」，必须在 InitFileLogs 之后
        // （它会改写 CoopLog.Level，而等级要覆盖文件日志路由）
        ModMenuPrefs.Load();

        MountBehaviour();

        // 契约宿主就绪：第三方模组（在此之前已 Register 的）现在能被枚举
        try
        {
            ModMenuHost.MarkHostAvailable(ModMenuInfo.Version);
            CoopLog.Info("modmenu.start", () => $"contract host ready (api={ModMenuHost.ApiVersion}, providers={ModMenuHost.ProviderCount})");
        }
        catch (Exception ex)
        {
            CoopLog.Error("modmenu.start", () => $"MarkHostAvailable failed: {ex.Message}");
        }

        InitRegistry();
        InitLoaders();

        // T8：统一顺序调度器（订阅注册表变化 + 启动后按序跑受管初始化）
        try
        {
            ModInitScheduler.Attach();
            ModInitScheduler.RequestInit("startup", 1f);
        }
        catch (Exception ex) { CoopLog.Warn("modmenu.order", () => $"scheduler attach failed (degraded): {ex.Message}"); }

        CoopLog.Info("modmenu.start", () => $"started in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>入口壳调用：清理（解挂行为、标记宿主下线）。</summary>
    public static void Shutdown()
    {
        if (!_started) return;
        _started = false;

        try
        {
            if (_behaviourGo != null)
            {
                UnityEngine.Object.Destroy(_behaviourGo);
                _behaviourGo = null;
            }
        }
        catch { }

        try { ModMenuHost.MarkHostUnavailable(); } catch { }
        try { ModMenuHost.Changed -= OnRegistryChanged; } catch { }
        try { ModInitScheduler.Detach(); } catch { }
        try { ModMenuUI.Destroy(); } catch { }
        try { ModMenuRegistry.Clear(); } catch { }
        CoopLog.Info("modmenu.stop", () => "stopped");
    }

    /// <summary>
    /// 独立文件日志：诊断日志走 <c>OpenNestModMenuLogs\*.log</c>，主日志只留会话/错误
    /// （避免控制台刷屏拖帧，见 repo 经验）。
    /// </summary>
    private static void InitFileLogs()
    {
        try { ModLog.Init(ModMenuPaths.LogDir); }
        catch { }
        CoopLog.RouteToFile("modmenu.", "modmenu");   // 本模组诊断
        CoopLog.RouteToFile("registry.", "modmenu");   // 注册表 / 被动扫描（T2）
        CoopLog.RouteToFile("loader.", "loader");     // 加载器探测（T3）
        CoopLog.RouteToFile("config.", "config");     // 配置读写（T6）
        CoopLog.RouteToFile("order.", "modmenu");      // 统一顺序（T8）
    }

    /// <summary>
    /// 注册表初始化（T2）：订阅契约注册表变化 → 主动注册本模组内置设置页 → 同步记录 →
    /// 立即扫一次（发现比本模组先加载的模组）+ 安排几次延迟扫描（兜底首帧后才加载的 MLL 模组）。
    /// </summary>
    private static void InitRegistry()
    {
        try
        {
            ModMenuHost.Changed += OnRegistryChanged;

            // 内置提供者：主动注册通道（第三方模组同理调用 ModMenuHost.Register）
            ModMenuHost.Register(new BuiltInSettingsProvider());

            ModMenuRegistry.Sync();

            // 首次：扫已加载程序集（早于本模组加载的主动/被动提供者都能收齐）
            ModMenuRegistry.ScanNow("startup");

            // 延迟重扫：经桥加载的 MLL 模组在首帧才加载，晚于本模组的启动时机
            ModMenuRegistry.ScheduleScan(1f, 5f, 15f);

            CoopLog.Info("registry.init", () => $"providers={ModMenuRegistry.Count} (active={ModMenuRegistry.ActiveCount}, scanned={ModMenuRegistry.ScannedCount})");
        }
        catch (Exception ex)
        {
            CoopLog.Error("registry.init", () => $"registry init failed (degraded): {ex.Message}");
        }
    }

    private static void OnRegistryChanged()
    {
        try { ModMenuRegistry.Sync(); }
        catch (Exception ex) { CoopLog.Warn("registry.sync", () => ex.Message); }
    }

    /// <summary>
    /// 加载器探测 + 模组清单（T3）：探测宿主/桥/版本 → 建清单 → 多次延迟刷新
    /// （经桥加载的 MLL 模组要在首帧才注册，启动时它们还不存在，见 docs/MOD_MENU.md §3.2）。
    /// </summary>
    private static void InitLoaders()
    {
        try
        {
            var loader = LoaderDetector.Detect(force: true);
            LoaderDetector.Log(loader);

            ModInventory.Refresh("startup");
            ModInventory.Schedule(1f, 5f, 15f, 30f);
        }
        catch (Exception ex)
        {
            CoopLog.Error("modmenu.loader", () => $"loader detect/inventory failed (degraded): {ex.Message}");
        }
    }

    /// <summary>
    /// 挂载 Unity 行为（每帧驱动）。IL2CPP 下自定义 MonoBehaviour 必须先
    /// <see cref="ClassInjector.RegisterTypeInIl2Cpp{T}"/> 再挂到常驻 GameObject；失败只降级（不影响加载）。
    /// </summary>
    private static void MountBehaviour()
    {
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<ModMenuBehaviour>();
            var go = new UnityEngine.GameObject("[OpenNestModMenu]Behaviour");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ModMenuBehaviour>();
            _behaviourGo = go;
            CoopLog.Info("modmenu.start", () => "behaviour mounted");
        }
        catch (Exception ex)
        {
            CoopLog.Error("modmenu.start", () => $"mount behaviour failed (degraded): {ex.Message}");
        }
    }
}
