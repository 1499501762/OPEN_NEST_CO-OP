using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace OpenNestUIKit;

/// <summary>
/// BepInEx 入口壳。真正的逻辑在 <see cref="Core.UiKitRuntime"/>（平台无关骨架）。
/// 本壳只做两件事：注入 BepInEx 日志后端 → 启动运行时。
/// MelonLoader 版见 <c>MelonModEntry.cs</c>（同目录，BepInEx 工程排除、ML 工程编译）。
/// </summary>
[BepInPlugin(UiKitInfo.Guid, UiKitInfo.Name, UiKitInfo.Version)]
public class Plugin : BasePlugin
{
    private sealed class BepLogger : Logging.ILogger
    {
        private readonly ManualLogSource _log;
        public BepLogger(ManualLogSource log) { _log = log; }
        public void Info(string m) => _log.LogInfo(m);
        public void Warn(string m) => _log.LogWarning(m);
        public void Error(string m) => _log.LogError(m);
        public void Debug(string m) => _log.LogDebug(m);
    }

    public override void Load()
    {
        Core.UiKitRuntime.Initialize(new BepLogger(Log));
        try
        {
            Core.UiKitRuntime.Startup();
        }
        catch (System.Exception ex)
        {
            Log.LogError($"OpenNestUIKit init failed: {ex}");
        }
    }

    public override bool Unload()
    {
        try { Core.UiKitRuntime.Shutdown(); }
        catch (System.Exception ex) { Log.LogError($"OpenNestUIKit unload failed: {ex}"); }
        return true;
    }
}
