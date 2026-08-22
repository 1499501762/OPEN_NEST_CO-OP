using MelonLoader;
using OpenNestCoop.Core;

[assembly: MelonInfo(typeof(OpenNestCoop.MelonModEntry), "Open Nest Co-op", "0.1.8", "OpenNestCoop")]
[assembly: MelonGame("Iron Nest", "Iron Nest Heavy Turret Simulator")]

namespace OpenNestCoop;

/// <summary>
/// MelonLoader 入口壳。真正的联机逻辑在 <see cref="CoopRuntime"/>（平台无关核心）。
/// 注意 MelonGame 声明用「无冒号」：游戏更新后 Application.productName 是
/// "Iron Nest Heavy Turret Simulator"（无冒号），与 MelonGame 精确字符串匹配。
/// 部署：放入 MelonLoader 的 Mods/ 目录（脱离 BepInEx 的纯 ML 环境也适用）。
/// </summary>
public class MelonModEntry : MelonMod
{
    private sealed class MlLogger : ILogger
    {
        public void Info(string m) => MelonLogger.Msg(m);
        public void Warn(string m) => MelonLogger.Warning(m);
        public void Error(string m) => MelonLogger.Error(m);
        public void Debug(string m) => MelonLogger.Msg(m);
    }

    public override void OnInitializeMelon()
    {
        CoopRuntime.Initialize(new MlLogger());
        CoopRuntime.HostInstance = this;
        try
        {
            // 场景可能未就绪，延迟到 LateInitialize/SceneLoaded 再 Startup
            CoopRuntime.Startup();
        }
        catch (System.Exception ex)
        {
            MelonLogger.Error($"OpenNestCoop init failed: {ex}");
        }
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        // 确保核心在场景就绪后启动（Unity 对象注入需要场景）
    }
}
