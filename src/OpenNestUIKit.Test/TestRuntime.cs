using System;
using Il2CppInterop.Runtime.Injection;
using OpenNestUIKit.API;

namespace OpenNestUIKit.Test;

/// <summary>
/// 测试模组运行时（平台无关骨架，两个入口壳共用）：
///   1) <see cref="Initialize"/> 注入平台日志；
///   2) <see cref="Startup"/> 建日志/报告文件 + **注册契约提供者**（软依赖：UIKit 没装也安全） +
///      注册直接调用 UIKit 的测试页 + 挂每帧驱动（CLI 驱动）；
///   3) <see cref="Shutdown"/> 清理。
///
/// ⚠️ 与 UIKit 一样做幂等守卫（桥环境可能双加载）。
/// </summary>
public static class TestRuntime
{
    private static bool _init, _started;
    private static UnityEngine.GameObject _go;
    private static TestDriver _driver;
    private static Action<string> _sink;

    internal static TestDriver Driver => _driver;

    public static void Initialize(Action<string> sink)
    {
        if (_init) return;
        _init = true;
        _sink = sink;
    }

    public static void Startup()
    {
        if (_started) return;
        _started = true;
        try
        {
            TestLog.Init(_sink, ResolveLogDir());
            TestLog.Info($"=== OpenNestUIKit 测试模组 v{TestInfo.Version}（{TestInfo.BuildPlatform} 构建）===");
            TestLog.Note("env", $"gameDir='{SafeGameDir()}' uikitLogDir='{ResolveLogDir()}'");

            // 1) 契约路径（纯 .NET API）：即使 UIKit 没装也不会抛
            try
            {
                UiKitHost.Register(new TestProvider());
                TestLog.Note("contract", $"registered TestProvider; hostAvailable={UiKitHost.IsHostAvailable} hostVer='{UiKitHost.HostVersion}' api={UiKitHost.ApiVersion} providers={UiKitHost.ProviderCount}");
                if (UiKitHost.IsHostAvailable) TestLog.Pass("契约注册", "宿主在线（UIKit 已加载）");
                else TestLog.Note("契约注册", "宿主离线（UIKit 未加载——注册已进内存表，这是设计允许的软依赖行为）");
            }
            catch (Exception ex) { TestLog.Fail("契约注册", ex.Message); }

            // 2) 直接调用 UIKit 的测试页
            try
            {
                TestPages.Register();
                TestLog.Pass("测试页注册", $"pages={Menu.UiPageCatalog.Count}");
            }
            catch (Exception ex) { TestLog.Fail("测试页注册", ex.Message); }

            // 2b) 模拟配置文件（只补不覆盖）：让「设置」页有全类型的自动生成控件可验收
            try { MockConfig.Ensure(force: false); }
            catch (Exception ex) { TestLog.Note("mockcfg", "跳过：" + ex.Message); }

            // 3) CLI 驱动
            _driver = new TestDriver(Environment.GetCommandLineArgs());
            Mount();

            // 4) 原生控件 Gallery：注册成原生 ESC 菜单里的一个独立条目（用户要求：用测试模组注入一个单独的原生菜单 Gallery）
            try { NativeGallery.Register(); TestLog.Pass("gallery 注册", NativeGallery.State); }
            catch (Exception ex) { TestLog.Fail("gallery 注册", ex.Message); }

            TestLog.Note("startup", _driver != null && _driver.Active ? "CLI 驱动已启用" : "无自测参数（静默待命：F6 打开菜单可见测试页）");
        }
        catch (Exception ex)
        {
            TestLog.Error("Startup 失败：" + ex);
        }
    }

    public static void Shutdown()
    {
        if (!_started) return;
        _started = false;
        try { Native.UiPointerRouter.PointerOverride = null; Native.UiPointerRouter.ButtonOverride = false; } catch { }
        try { if (_go != null) UnityEngine.Object.Destroy(_go); } catch { }
        _go = null;
        TestLog.Flush();
    }

    private static void Mount()
    {
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<TestBehaviour>();
            var go = new UnityEngine.GameObject("[OpenNestUIKit.Test]Behaviour");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<TestBehaviour>();
            _go = go;
            TestLog.Info("behaviour mounted");
        }
        catch (Exception ex) { TestLog.Error("挂载行为失败：" + ex.Message); }
    }

    /// <summary>每帧（由 <see cref="TestBehaviour"/> 调用）。</summary>
    public static void Tick(float dt)
    {
        TestLog.Tick(dt);
        _driver?.Tick(dt);
    }

    private static string SafeGameDir()
    {
        try { return Core.UiKitPaths.GameDir; } catch { return "<unknown>"; }
    }

    /// <summary>日志/报告目录：优先用 UIKit 的日志目录（同一处便于对照），拿不到则自己算。</summary>
    private static string ResolveLogDir()
    {
        try
        {
            string d = Core.UiKitPaths.LogDir;
            if (!string.IsNullOrEmpty(d)) return d;
        }
        catch { }
        try
        {
            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
                string dir = System.IO.Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir)) return System.IO.Path.Combine(dir, "OpenNestUIKitLogs");
            }
        }
        catch { }
        return System.IO.Path.Combine(Environment.CurrentDirectory, "OpenNestUIKitLogs");
    }
}

/// <summary>每帧驱动（IL2CPP 注入的 MonoBehaviour：方法必须 public）。</summary>
public sealed class TestBehaviour : UnityEngine.MonoBehaviour
{
    public void Update()
    {
        try { TestRuntime.Tick(UnityEngine.Time.unscaledDeltaTime); }
        catch { }
    }
}
