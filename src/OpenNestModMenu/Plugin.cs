using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using OpenNestModMenu.Core;

namespace OpenNestModMenu;

/// <summary>
/// BepInEx 入口壳。真正的逻辑在 <see cref="ModMenuRuntime"/>（平台无关骨架）。
/// 本壳只做两件事：注入 BepInEx 日志后端 → 启动运行时。
/// MelonLoader 版见 <c>MelonModEntry.cs</c>（同目录，BepInEx 工程排除、ML 工程编译）。
/// </summary>
[BepInPlugin(ModMenuInfo.Guid, ModMenuInfo.Name, ModMenuInfo.Version)]
public class Plugin : BasePlugin
{
    private sealed class BepLogger : ILogger
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
        ModMenuRuntime.Initialize(new BepLogger(Log));
        try
        {
            ModMenuRuntime.Startup();
        }
        catch (System.Exception ex)
        {
            Log.LogError($"OpenNestModMenu init failed: {ex}");
        }
    }

    public override bool Unload()
    {
        try { ModMenuRuntime.Shutdown(); }
        catch (System.Exception ex) { Log.LogError($"OpenNestModMenu unload failed: {ex}"); }
        return true;
    }
}
