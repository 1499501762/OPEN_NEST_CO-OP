using System;
using System.Collections.Generic;
using OpenNestCoop.Net;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 自定义同步模块接口——其他模组实现此接口可接入本联机框架的路由/Tick 循环，
/// 同步任意游戏组件（设备、物品、事件等）。
/// MsgType 建议使用 100+（现有内建类型 1-27）。
/// </summary>
public interface ISyncedModule
{
    /// <summary>本模块处理的消息类型（OnPacket 路由 key）。</summary>
    byte MsgType { get; }

    /// <summary>每帧/周期驱动（由 NetManager 在 Update 中调用）。</summary>
    void Tick(float dt);

    /// <summary>收到本模块消息类型的数据包。</summary>
    void OnPacket(ulong from, byte[] data);

    /// <summary>会话开始（主机或已加入房间）。</summary>
    void OnSessionStarted();

    /// <summary>会话结束（离开/被踢）。</summary>
    void OnSessionEnded();

    /// <summary>重置模块状态。</summary>
    void Reset();

    /// <summary>中途加入：主机把本模块当前状态单播给新加入/重连的成员（steamId）。
    /// 默认空实现——只有需要初始对齐的模块（任务/装填等）重写。</summary>
    void OnLateJoin(ulong steamId) { }
}

/// <summary>
/// 同步组件注册 API（开放扩展点）：
/// - RegisterFloat/RegisterInt/RegisterBool：注册"设备状态值"同步（委托给 ValueSync 统一框架）。
/// - RegisterModule：注册自定义同步模块（实现 ISyncedModule，处理自己的消息类型）。
/// 别的模组在加载时调用即可接入联机同步。
/// </summary>
public static class CoopSyncRegistry
{
    private static readonly List<ISyncedModule> _modules = new List<ISyncedModule>();
    private static readonly Dictionary<byte, ISyncedModule> _byType = new Dictionary<byte, ISyncedModule>();
    private static SessionState _lastState = SessionState.Idle;

    /// <summary>已注册模块（只读，供主机中途加入遍历 OnLateJoin）。</summary>
    public static IReadOnlyList<ISyncedModule> Modules => _modules;

    /// <summary>按类型查找已注册模块（如 MissionSync 快照应用时定位实例）。</summary>
    public static T FindModule<T>() where T : class
    {
        foreach (var m in _modules)
            if (m is T t) return t;
        return null;
    }

    // ---------------- 值同步注册（设备状态） ----------------

    public static ValueSync.Binding RegisterFloat(string id, Func<float> get, Action<float> set,
        float deadzone = 0.001f, bool interp = false, Func<bool> busy = null)
        => ValueSync.AddFloat(id, get, set, deadzone, interp, busy);

    public static ValueSync.Binding RegisterInt(string id, Func<int> get, Action<int> set,
        float deadzone = 1f, bool interp = false, Func<bool> busy = null)
        => ValueSync.AddInt(id, get, set, deadzone, interp, busy);

    public static ValueSync.Binding RegisterBool(string id, Func<bool> get, Action<bool> set,
        Func<bool> busy = null)
        => ValueSync.AddBool(id, get, set, busy);

    // ---------------- 自定义同步模块注册 ----------------

    /// <summary>注册自定义同步模块（MsgType 需唯一；重复注册被忽略）。</summary>
    public static void RegisterModule(ISyncedModule module)
    {
        if (module == null || _byType.ContainsKey(module.MsgType)) return;
        _modules.Add(module);
        _byType[module.MsgType] = module;
    }

    /// <summary>注册自定义同步模块 + 附加消息类型（同模块处理多个 MsgType，如 CatSync 处理 106+133）。</summary>
    public static void RegisterModule(ISyncedModule module, params byte[] extraTypes)
    {
        RegisterModule(module);
        if (module == null || extraTypes == null) return;
        foreach (var t in extraTypes)
            if (!_byType.ContainsKey(t))
                _byType[t] = module;
    }

    public static void ResetAll()
    {
        foreach (var m in _modules)
        {
            try { m.Reset(); }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CoopSyncRegistry Reset: {ex.Message}"); }
        }
    }

    // ---------------- 框架内部驱动（NetManager 调用） ----------------

    /// <summary>NetManager.Update 调用：会话状态事件 + 各模块 Tick。</summary>
    public static void TickAll(float dt)
    {
        var net = CoopRuntime.Net;
        var st = net?.State ?? SessionState.Idle;
        if (st != _lastState)
        {
            bool wasIn = _lastState == SessionState.Hosting || _lastState == SessionState.Joined;
            bool nowIn = st == SessionState.Hosting || st == SessionState.Joined;
            _lastState = st;
            if (nowIn && !wasIn)
                foreach (var m in _modules) { try { m.OnSessionStarted(); } catch { } }
            else if (!nowIn && wasIn)
                foreach (var m in _modules) { try { m.OnSessionEnded(); } catch { } }
        }

        foreach (var m in _modules)
        {
            try { m.Tick(dt); }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CoopSyncRegistry Tick: {ex.Message}"); }
        }
    }

    /// <summary>NetManager.OnPacket 调用：若消息类型被某模块注册则路由给它并返回 true。</summary>
    public static bool TryRoute(byte type, ulong from, byte[] data)
    {
        if (_byType.TryGetValue(type, out var m))
        {
            try { m.OnPacket(from, data); }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CoopSyncRegistry Route: {ex.Message}"); }
            return true;
        }
        return false;
    }
}
