using System;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// M3 环境同步（M3EnvSyncV2）。M7：把 V1 <c>M3EnvSync</c> 迁入分层架构——
/// 引擎 / 高压压力 / 反炮兵倒计时全部注册为 <see cref="ValueLayer"/> **Host 权威**值绑定
/// （等价 V1 ClientNoSend：客机只接收应用，不上行——防客机开局读 0 关引擎）。
/// </summary>
public static class M3EnvSyncV2
{
    private static bool _registered;
    private static DieselEngineController _engine;
    private static HighPressureSystemManager _hps;
    private static float _engineSeenAt = -1f;
    private const float StartupGuardSeconds = 8f;
    private static bool _engineInitLogged;
    private static int _engineApplyLog;
    private static float _lastEngineStateLog;
    private static bool _lastEngineState = true;

    /// <summary>注册环境值绑定到 ValueLayer（--sync new 时 bootstrap 调用）。</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            // 引擎（Host 权威，客机只接收——防开局读 false 上行关主机引擎）
            ValueLayer.Instance.RegisterBool("env/engine/running",
                () =>
                {
                    var e = GetEngine();
                    bool running = e != null && e.EnginesRunning;
                    if (running != _lastEngineState && Time.unscaledTime - _lastEngineStateLog > 1f)
                    {
                        _lastEngineStateLog = Time.unscaledTime;
                        _lastEngineState = running;
                        CoopRuntime.LogSource?.LogInfo($"[M3EnvV2] getter engine={(e == null ? "NULL" : "ok")} running={running} seen={_engineSeenAt:0.0}");
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
                        if (v == currently) return;
                        bool protectedNoShutdown = _engineSeenAt >= 0f && Time.unscaledTime - _engineSeenAt < StartupGuardSeconds;
                        if (v) e.AttemptIgnition();
                        else
                        {
                            if (protectedNoShutdown) return; // 开局保护：场景加载时序引擎短暂读 false 不关机
                            e.ShutdownEngine();
                        }
                    }
                    catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[M3EnvV2] engine: {ex.Message}"); }
                },
                null, V2Authority.Host);

            // 高压系统压力健康度（Host 权威）
            ValueLayer.Instance.RegisterFloat("env/pressure",
                () => GetHps()?.Health01 ?? 0f,
                v =>
                {
                    var h = GetHps();
                    if (h == null) return;
                    try { h.currentHealth01 = v; }
                    catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[M3EnvV2] pressure: {ex.Message}"); }
                },
                0.005f, true, null, V2Authority.Host);

            // 反炮兵倒计时剩余秒（Host 权威）
            ValueLayer.Instance.RegisterFloat("env/cbattery/time",
                () => GetTimer()?.TimeRemaining ?? 0f,
                v =>
                {
                    var t = GetTimer();
                    if (t == null) return;
                    try { t.SetTime(v); }
                    catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[M3EnvV2] cbtime: {ex.Message}"); }
                },
                0.1f, true, null, V2Authority.Host);

            // 反炮兵倒计时运行中（Host 权威）
            ValueLayer.Instance.RegisterBool("env/cbattery/running",
                () => GetTimer()?.IsRunning ?? false,
                v =>
                {
                    var t = GetTimer();
                    if (t == null) return;
                    try { if (v) t.StartTimer(); else t.PauseTimer(); }
                    catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[M3EnvV2] cbrun: {ex.Message}"); }
                },
                null, V2Authority.Host);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[M3EnvV2] Register: {ex.Message}"); }
    }

    private static DieselEngineController GetEngine()
    {
        try
        {
            if (_engine == null || _engine.gameObject == null)
            {
                _engine = UnityEngine.Object.FindFirstObjectByType<DieselEngineController>();
                if (_engine != null)
                {
                    _engineSeenAt = Time.unscaledTime;
                    if (!_engineInitLogged)
                    {
                        _engineInitLogged = true;
                        bool r = false;
                        try { r = _engine.EnginesRunning; } catch { }
                        CoopRuntime.LogSource?.LogInfo($"[M3EnvV2] engine FOUND at t={_engineSeenAt:0.0} initialRunning={r}");
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
