using MelonLoader;

// ⚠️ 程序集特性在命名空间之外，必须全限定引用 TestInfo。
[assembly: MelonInfo(typeof(OpenNestUIKit.Test.TestMelonEntry),
                     OpenNestUIKit.Test.TestInfo.Name,
                     OpenNestUIKit.Test.TestInfo.Version,
                     OpenNestUIKit.Test.TestInfo.Author)]
[assembly: MelonGame("Iron Nest", "Iron Nest Heavy Turret Simulator")]

namespace OpenNestUIKit.Test;

/// <summary>MelonLoader 入口壳（MelonLoader 工程编译；BepInEx 工程排除本文件）。</summary>
public class TestMelonEntry : MelonMod
{
    public override void OnInitializeMelon()
    {
        TestRuntime.Initialize(m => MelonLogger.Msg(m));
        try { TestRuntime.Startup(); }
        catch (System.Exception ex) { MelonLogger.Error("OpenNestUIKit.Test 初始化失败：" + ex); }
    }

    public override void OnApplicationQuit()
    {
        try { TestRuntime.Shutdown(); } catch { }
    }
}
