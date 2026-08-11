using System;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// M3 环境同步：引擎、高压系统（压力）、反炮兵倒计时。
/// 全部通过 CoopSyncRegistry 注册为值绑定（复用 ValueSync 统一框架，无新消息）。
/// - 引擎：EnginesRunning（点火/关机）
/// - 压力：HighPressureSystemManager.Health01（0-1，插值）
/// - 反炮兵计时：CounterBatteryTimer.TimeRemaining + IsRunning（主机权威，客户端 SetTime/StartTimer 对齐）
/// 引擎灯/压力表是这些状态的视觉表现，同步状态后自然一致。
/// 落点 seed 精确同步、任务/目标同步需另行实现（见后续）。
/// </summary>
public static class M3EnvSync
{
    private static bool _registered;
    private static DieselEngineController _engine;
    private static HighPressureSystemManager _hps;
    /// <summary>引擎最近出现/重建时间（任务场景加载起点）；开局保护：此窗口内不主动关引擎（防初始化时序误关机）。</summary>
    private static float _engineSeenAt = -1f;
    private const float StartupGuardSeconds = 8f;
    // 诊断：引擎初始状态 / 来电被破坏的定位（谁在开局把引擎关掉了）
    private static bool _engineInitLogged;
    private static int _engineApplyLog;
    private static float _lastEngineStateLog;
    private static bool _lastEngineState = true;

    /// <summary>注册环境值绑定（Plugin.Load 调用；对象在运行时懒解析）。</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            // 引擎：运行状态（点/关）——主机权威（ClientNoSend：客机只接收不上行）。
            // 客机开局引擎 Getter 读到 false（场景未加载完）会上行 v=0 关掉主机引擎 = 联机开局断电根因。
            CoopSyncRegistry.RegisterBool("env/engine/running",
                () =>
                {
                    var e = GetEngine();
                    bool running = e != null && e.EnginesRunning;
                    // 诊断：引擎状态每次变化都记录（低频节流）——定位“开局来电被破坏”的确切时刻/来源
                    if (running != _lastEngineState && Time.unscaledTime - _lastEngineStateLog > 1f)
                    {
                        _lastEngineStateLog = Time.unscaledTime;
                        _lastEngineState = running;
                        CoopRuntime.LogSource?.LogInfo($"[M3Env] getter engine={(e == null ? "NULL" : "ok")} running={running} seen={_engineSeenAt:0.0} now={Time.unscaledTime:0.0}");
                    }
                    return running;
                },
                v =>
                {
                    var e = GetEngine();
                    if (e == null) return;
                    try
                    {
                        bool currently = e.EnginesRunning;
                        if (v == currently) return; // 状态已一致，不重复操作
                        bool protectedNoShutdown = _engineSeenAt >= 0f && Time.unscaledTime - _engineSeenAt < StartupGuardSeconds;
                        if (v)
                        {
                            if ((++_engineApplyLog % 10) == 1)
                                CoopRuntime.LogSource?.LogInfo($"[M3Env] apply START engine running={currently}");
                            e.AttemptIgnition(); // 开机：同步点火
                        }
                        else
                        {
                            if (protectedNoShutdown)
                            {
                                // 开局保护：引擎出现后 StartupGuardSeconds 内不主动关机——
                                // 任务场景加载时序引擎可能短暂读 false，主动关机会导致“联机开局停电”。
                                if ((++_engineApplyLog % 10) == 1)
                                    CoopRuntime.LogSource?.LogInfo($"[M3Env] apply STOP blocked by startup guard (age={Time.unscaledTime - _engineSeenAt:0.0}s)");
                                return;
                            }
                            if ((++_engineApplyLog % 10) == 1)
                                CoopRuntime.LogSource?.LogInfo($"[M3Env] apply STOP engine running={currently}");
                            e.ShutdownEngine(); // 中途/正常关机：同步关闭
                        }
                    }
                    catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"M3EnvSync engine: {ex.Message}"); }
                }).ClientNoSend = true;

            // 高压系统：压力健康度 0-1（主机权威，客户端只接收）
            CoopSyncRegistry.RegisterFloat("env/pressure",
                () => GetHps()?.Health01 ?? 0f,
                v =>
                {
                    var h = GetHps();
                    if (h == null) return;
                    try { h.currentHealth01 = v; }
                    catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"M3EnvSync pressure: {ex.Message}"); }
                },
                0.005f, true).ClientNoSend = true;

            // 反炮兵倒计时剩余秒（主机权威，客户端对齐）
            CoopSyncRegistry.RegisterFloat("env/cbattery/time",
                () => GetTimer()?.TimeRemaining ?? 0f,
                v =>
                {
                    var t = GetTimer();
                    if (t == null) return;
                    try { t.SetTime(v); }
                    catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"M3EnvSync cbtime: {ex.Message}"); }
                },
                0.1f, true).ClientNoSend = true;

            // 反炮兵倒计时运行中（主机权威，客户端只接收）
            CoopSyncRegistry.RegisterBool("env/cbattery/running",
                () => GetTimer()?.IsRunning ?? false,
                v =>
                {
                    var t = GetTimer();
                    if (t == null) return;
                    try { if (v) t.StartTimer(); else t.PauseTimer(); }
                    catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"M3EnvSync cbrun: {ex.Message}"); }
                }).ClientNoSend = true;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"M3EnvSync Register: {ex.Message}"); }
    }

    private static DieselEngineController GetEngine()
    {
        // 不用一次性 _tried 标志：引擎/高压系统在任务场景才出现，
        // 首次查找（主菜单）为 null 后必须能重新找到（场景切换销毁后也重查）。
        try
        {
            if (_engine == null || _engine.gameObject == null)
            {
                _engine = UnityEngine.Object.FindFirstObjectByType<DieselEngineController>();
                if (_engine != null)
                {
                    _engineSeenAt = Time.unscaledTime; // 引擎出现/重建 = 场景加载起点（开局保护基准）
                    // 诊断：引擎出现时的初始状态（单机是来电的，联机若断电=被破坏的起点，从这里查）
                    if (!_engineInitLogged)
                    {
                        _engineInitLogged = true;
                        bool r = false;
                        try { r = _engine.EnginesRunning; } catch { }
                        CoopRuntime.LogSource?.LogInfo($"[M3Env] engine FOUND at t={_engineSeenAt:0.0} initialRunning={r}");
                    }
                }
            }
        }
        catch { _engine = null; }
        return _engine;
    }

    private static HighPressureSystemManager GetHps()
    {
        try { if (_hps == null) _hps = UnityEngine.Object.FindFirstObjectByType<HighPressureSystemManager>(); }
        catch { _hps = null; }
        return _hps;
    }

    private static CounterBatteryTimer GetTimer()
    {
        try { return CounterBatteryTimer.Instance; }
        catch { return null; }
    }
}
