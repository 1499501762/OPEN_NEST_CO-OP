using MelonLoader;

// ⚠️ 程序集特性在命名空间之外，必须全限定引用 UiKitInfo（OpenNestUIKit.UiKitInfo）。
[assembly: MelonInfo(typeof(OpenNestUIKit.MelonModEntry),
                     OpenNestUIKit.UiKitInfo.Name,
                     OpenNestUIKit.UiKitInfo.Version,
                     OpenNestUIKit.UiKitInfo.Author)]
[assembly: MelonGame("Iron Nest", "Iron Nest Heavy Turret Simulator")]

namespace OpenNestUIKit;

/// <summary>
/// MelonLoader 入口壳（MelonLoader 工程编译；BepInEx 工程排除本文件）。
/// 注意 MelonGame 用「无冒号」写法：游戏的 Application.productName 是
/// "Iron Nest Heavy Turret Simulator"（无冒号），精确匹配。
/// 部署：放入 MelonLoader 的 <c>Mods/</c> 目录（原生 ML 或经桥 BepInEx.MelonLoader.Loader 都适用）。
/// </summary>
public class MelonModEntry : MelonMod
{
    private sealed class MlLogger : Logging.ILogger
    {
        public void Info(string m) => MelonLogger.Msg(m);
        public void Warn(string m) => MelonLogger.Warning(m);
        public void Error(string m) => MelonLogger.Error(m);
        public void Debug(string m) => MelonLogger.Msg(m);
    }

    public override void OnInitializeMelon()
    {
        Core.UiKitRuntime.Initialize(new MlLogger());
        try
        {
            Core.UiKitRuntime.Startup();
        }
        catch (System.Exception ex)
        {
            MelonLogger.Error($"OpenNestUIKit init failed: {ex}");
        }
    }

    public override void OnApplicationQuit()
    {
        try { Core.UiKitRuntime.Shutdown(); }
        catch { /* 退出路径不阻塞 */ }
    }
}
