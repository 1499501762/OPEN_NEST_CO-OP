using System;
using Il2CppInterop.Runtime.Injection;

namespace OpenNestUIKit.Core;

/// <summary>
/// 平台无关运行时骨架（BepInEx / MelonLoader 两个入口壳共用）：
///   1) <see cref="Initialize"/> —— 注入平台日志后端；
///   2) <see cref="Startup"/> —— 路径解析 + 独立文件日志 + 游戏桥接 + Unity 行为挂载 + 契约宿主上线 + 菜单/原生注入；
///   3) <see cref="Shutdown"/> —— 清理。
///
/// ⚠️ **幂等守卫**：经桥 BepInEx.MelonLoader.Loader 加载时，同一模组可能被两个加载器各初始化一次
/// （历史事故：`xxx already injected` / TypeLoadException，见 docs/MOD_MENU.md §3.3）。
/// 因此 Initialize/Startup 都做重复调用保护，且挂载失败只降级不抛出。
/// </summary>
public static class UiKitRuntime
{
    /// <summary>平台日志（由入口壳注入）。</summary>
    public static ILogger LogSource;

    public static bool IsInitialized => _initialized;
    public static bool IsStarted => _started;

    private static bool _initialized;
    private static bool _started;
    private static UnityEngine.GameObject _behaviourGo;

    /// <summary>入口壳调用一次：注入平台日志（同时供 <see cref="CoopLog"/> 使用）。</summary>
    public static void Initialize(ILogger logger)
    {
        if (_initialized)
        {
            try { CoopLog.Warn("uikit.init", () => "Initialize 重复调用（已忽略，桥环境双加载）"); } catch { }
            return;
        }
        _initialized = true;
        LogSource = logger;
        CoopLog.SetLogSource(logger);
    }

    /// <summary>入口壳调用：建立运行时。</summary>
    public static void Startup()
    {
        if (_started)
        {
            LogSource?.Warn("[OpenNestUIKit] Startup 重复调用（已忽略）");
            return;
        }
        _started = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        UiKitPaths.Resolve();
        InitFileLogs();

        CoopLog.Info("uikit.start", () => $"=== {UiKitInfo.Name} v{UiKitInfo.Version} ({UiKitInfo.BuildPlatform} 构建) ===");
        CoopLog.Info("uikit.start", () => $"game='{UiKitPaths.GameDir}' shape={UiKitPaths.Shape}");
        CoopLog.Info("uikit.start", () => $"log='{UiKitPaths.LogDir}' config='{UiKitPaths.ConfigFile}'");

        // 语言（桥接优先；桥接未就绪时退化到系统语言）
        CoopLog.Info("uikit.loc", () => $"lang='{UiKitLoc.Current}' (bridge={(NativeUi.Available ? "ready" : "pending")})");

        // 九宫格切片定义（**切片真相**）：加载 + 热重载；定义变化 → 素材烘焙缓存失效
        try
        {
            UiSliceStore.Changed += OnSlicesChanged;
            UiSliceStore.Init(UiKitPaths.SlicesFile);
            CoopLog.Info("uikit.slice", () => $"切片定义：{UiSliceStore.Count} 条 ← '{UiKitPaths.SlicesFile}'");
        }
        catch (Exception ex) { CoopLog.Warn("uikit.slice", () => $"切片定义初始化失败（退化为自动选材）：{ex.Message}"); }

        MountBehaviour();

        // 游戏侧桥接（原生 UI 能力）：Hook 内部自己重试（游戏对象可能要等首帧/主菜单）
        try { UiGameBridge.Hook(); }
        catch (Exception ex) { CoopLog.Warn("uikit.bridge", () => $"hook failed (degraded): {ex.Message}"); }

        // 宿主启动：把菜单控制挂进契约（第三方据此打开/关闭本库菜单，而不必引宿主程序集）
        try
        {
            UiKitHost.MarkHostAvailable(UiKitInfo.Version);
            UiKitHost.Changed += OnProvidersChanged;
            UiKitHost.SetMenuControls(
                () => Menu.UiMenuWindow.IsOpen,
                id => Menu.UiMenuWindow.Open(id),
                () => Menu.UiMenuWindow.Close(),
                () => Menu.UiMenuWindow.ReloadCurrentPage(),
                () => Menu.UiMenuWindow.CurrentPage,
                () => Native.UiPointerRouter.TextFocus);
            // v0.0.1-Alpha-3：确认框 / 滚动定位（第三方契约的 Confirm / ScrollToKey / ScrollToTop）
            UiKitHost.SetViewControls(
                (payload, onResult) => Menu.UiMenuWindow.ShowConfirm(payload, onResult),
                key => Menu.UiMenuWindow.ScrollToKey(key),
                () => Menu.UiMenuWindow.ScrollTop());
            // 语言：把当前语言推给契约（第三方用 API.UiKitLang.T 写双语）；切换时由 UiMenuWindow.Tick 再推
            try { API.UiKitLang.SetChinese(Core.UiKitLoc.IsChinese); } catch { }
            CoopLog.Info("uikit.start", () => $"contract host ready (api={UiKitHost.ApiVersion}, providers={UiKitHost.ProviderCount})");
        }
        catch (Exception ex) { CoopLog.Error("uikit.start", () => $"MarkHostAvailable failed: {ex.Message}"); }

        // 内置菜单（演示 + 自检）与原生菜单注入
        try { DemoStarter.RegisterBuiltIns(); }
        catch (Exception ex) { CoopLog.Warn("uikit.demo", () => $"built-in menu register failed: {ex.Message}"); }

        // 第三方 provider（含 Coop / ModMenu）：启动就收编一次 ——
        // 这样它们的**原生 ESC 入口**不依赖“用户先手动打开过我们的窗口”（以前是懒收编，不打开就永远不会注入）。
        try { Menu.UiPageCatalog.EnsureProviders(); }
        catch (Exception ex) { CoopLog.Warn("uikit.registry", () => $"provider collect failed: {ex.Message}"); }

        try { NativeMenuInjector.Attach(); }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => $"native injector attach failed (degraded): {ex.Message}"); }

        // 文本输入管线：patch `Keyboard.OnTextInput`（转发 CJK；英文/数字走物理键轮询）
        try { Widgets.UiTextRouter.Install(); }
        catch (Exception ex) { CoopLog.Warn("uikit.start", () => $"UiTextRouter.Install failed: {ex.Message}"); }

        // 悬浮聊天层（第三方注册 → 宿主渲染；回车唤入 / 回车发送 / ESC 收起）
        try { UiKitHost.SetChatControls(Widgets.UiChatOverlay.SetFromHost, Widgets.UiChatOverlay.Clear, Widgets.UiChatOverlay.Focus, Widgets.UiChatOverlay.Close); }
        catch (Exception ex) { CoopLog.Warn("uikit.start", () => $"SetChatControls failed: {ex.Message}"); }
        try { UiKitHost.SetFooterControl(Menu.UiMenuWindow.SetFooter); }
        catch (Exception ex) { CoopLog.Warn("uikit.start", () => $"SetFooterControl failed: {ex.Message}"); }

        CoopLog.Info("uikit.start", () => $"started in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>入口壳调用：清理。</summary>
    public static void Shutdown()
    {
        if (!_started) return;
        _started = false;

        try { UiKitHost.Changed -= OnProvidersChanged; } catch { }
        try { UiKitHost.SetMenuControls(null, null, null); } catch { }
        try { UiKitHost.SetViewControls(null, null, null); } catch { }

        try { UiEscapeLevels.Clear(); } catch { }
        try { UiSliceStore.Changed -= OnSlicesChanged; } catch { }
        try { NativeMenuInjector.Detach(); } catch { }
        try { Menu.UiMenuWindow.Destroy(); } catch { }
        try { UiGameBridge.Unhook(); } catch { }
        try { UiKitHost.MarkHostUnavailable(); } catch { }
        try
        {
            if (_behaviourGo != null)
            {
                UnityEngine.Object.Destroy(_behaviourGo);
                _behaviourGo = null;
            }
        }
        catch { }
        CoopLog.Info("uikit.stop", () => "stopped");
    }

    private static void OnProvidersChanged()
    {
        try { CoopLog.Info("uikit.registry", () => $"providers changed (count={UiKitHost.ProviderCount}, host={(UiKitHost.IsHostAvailable ? "online" : "offline")})"); } catch { }
        try { Menu.UiMenuWindow.Invalidate(); } catch { }
        // 页面 + **原生 ESC 入口**都要跟着注册表变（新模组注册 → 它的行出现在原生菜单里）
        try
        {
            Menu.UiPageCatalog.InvalidateProviders();
            Menu.UiPageCatalog.EnsureProviders();
        }
        catch (Exception ex) { CoopLog.Warn("uikit.registry", () => $"provider re-collect failed: {ex.Message}"); }
    }

    /// <summary>切片定义变化 → 素材烘焙缓存失效（下次取用按新定义重建）。</summary>
    private static void OnSlicesChanged()
    {
        try { UiSkin.InvalidateBaked(); } catch { }
    }

    /// <summary>诊断日志走独立文件（<c>OpenNestUIKitLogs\*.log</c>），主日志只留会话/错误。</summary>
    private static void InitFileLogs()
    {
        try { ModLog.Init(UiKitPaths.LogDir); }
        catch { }
        CoopLog.RouteToFile("uikit.ui", "uikit");
        CoopLog.RouteToFile("uikit.layout", "uikit");
        CoopLog.RouteToFile("uikit.widget", "uikit");
        CoopLog.RouteToFile("uikit.pointer", "uikit");
        CoopLog.RouteToFile("uikit.native", "native");
        CoopLog.RouteToFile("uikit.skin", "uikit");
        CoopLog.RouteToFile("uikit.slice", "uikit");
        CoopLog.RouteToFile("uikit.probe", "uikit");
    }

    /// <summary>
    /// 挂载 Unity 行为（每帧驱动）。IL2CPP 下自定义 MonoBehaviour 必须先
    /// <see cref="ClassInjector.RegisterTypeInIl2Cpp{T}"/> 再挂到常驻 GameObject；失败只降级（不影响加载）。
    /// </summary>
    private static void MountBehaviour()
    {
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<UiKitBehaviour>();
            var go = new UnityEngine.GameObject("[OpenNestUIKit]Behaviour");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<UiKitBehaviour>();
            _behaviourGo = go;
            CoopLog.Info("uikit.start", () => "behaviour mounted");
        }
        catch (Exception ex)
        {
            CoopLog.Error("uikit.start", () => $"mount behaviour failed (degraded): {ex.Message}");
        }
    }
}
