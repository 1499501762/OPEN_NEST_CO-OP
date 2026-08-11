using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using OpenNestCoop.Core;

namespace OpenNestCoop;

/// <summary>
/// BepInEx 入口壳。真正的联机逻辑在 <see cref="CoopRuntime"/>（平台无关核心）。
/// 本壳只做：注入 BepInEx 日志 → 让核心在场景就绪后 Startup。
/// MelonLoader 版本见 OpenNestCoop.MelonMod 壳。
/// </summary>
[BepInPlugin(NetConfig.Guid, NetConfig.Name, NetConfig.Version)]
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
        CoopRuntime.Initialize(new BepLogger(Log));
        CoopRuntime.HostInstance = this;
        try
        {
            CoopRuntime.Startup();
        }
        catch (System.Exception ex)
        {
            Log.LogError($"OpenNestCoop 初始化失败: {ex}");
        }
    }

    public override bool Unload()
    {
        try { CoopRuntime.Shutdown(); }
        catch (System.Exception ex) { Log.LogError($"卸载失败: {ex}"); }
        return true;
    }
}
