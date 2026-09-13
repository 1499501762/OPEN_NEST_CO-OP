using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace OpenNestUIKit.Test;

/// <summary>
/// BepInEx 入口壳（MelonLoader 版见 <c>TestMelonEntry.cs</c>，本文件在 ML 工程里被排除）。
///
/// ⚠ 2026-09-13：这里**真实声明**对 UIKit 的硬依赖（测试模组编译期就引用 UIKit 程序集）。
/// 除了这本来就是事实，它还让“依赖 → 加载/初始化顺序”这条链路在实机上**可验证**：
/// 若用户把测试模组在顺序表里排到 UIKit 前面，调度器会把它拉回来（`dependency fixup`）。
/// </summary>
[BepInPlugin(TestInfo.Guid, TestInfo.Name, TestInfo.Version)]
[BepInDependency(OpenNestUIKit.UiKitInfo.Guid)]
public class TestPlugin : BasePlugin
{
    public override void Load()
    {
        TestRuntime.Initialize(m => Log.LogInfo(m));
        try { TestRuntime.Startup(); }
        catch (System.Exception ex) { Log.LogError("OpenNestUIKit.Test 初始化失败：" + ex); }
    }

    public override bool Unload()
    {
        try { TestRuntime.Shutdown(); } catch { }
        return true;
    }
}
