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
    /// <summary>本模块处理的消息类型（OnPacket 路由 key）。int 支持 2 字节扩展类型（≥256，
    /// 1 字节空间用尽时由 NetManager 注册管理器自动分配；普通模块仍为 1-255）。</summary>
    int MsgType { get; }

    /// <summary>网络负载优先级（NetworkGovernor per-module 分级）：决定同一全局档位下本模块的频率缩放。
    /// 默认 Normal；高频/容忍丢失的模块（实体/猫/位置）设 Low，批量/低频设 Bulk，关键交互设 Critical。</summary>
    NetModulePriority NetPriority => NetModulePriority.Normal;

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
    private static readonly Dictionary<int, ISyncedModule> _byType = new Dictionary<int, ISyncedModule>();
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

    /// <summary>注册自定义同步模块（MsgType 需唯一；重复注册被忽略）。
    /// 注册后同时交给 <see cref="OpenNestCoop.Net.NetManager.AdoptModule"/> 采用（统一管理前导字节/优先级/路由）。
    /// 优先级缺省 → 取模块 <see cref="ISyncedModule.NetPriority"/>（默认 Normal）。</summary>
    public static void RegisterModule(ISyncedModule module, params byte[] extraTypes)
        => RegisterModule(module, null, extraTypes);

    /// <summary>注册自定义同步模块 + 显式优先级（缺省 null → 模块 NetPriority）+ 附加消息类型
    /// （同模块处理多个 MsgType，如 CatSync 106+133）。</summary>
    public static void RegisterModule(ISyncedModule module, NetModulePriority? priority, params byte[] extraTypes)
    {
        if (module == null) return;
        if (module.MsgType == 0)
        {
            // ⚠️ 动态注册通道（RegisterDynamicChannel）的模块 MsgType 由注册管理器分配；误走本入口时类型未就绪 → 忽略
            try { OpenNestCore.Logging.CoopLog.Warn("CoopSyncRegistry.zero", () => $"[Registry] type=0 (dynamic?) class={module.GetType().Name} — use RegisterDynamicChannel"); } catch { }
            return;
        }
        if (_byType.ContainsKey(module.MsgType))
        {
            // ⚠️ 冲突诊断：MsgType 被占用 → 该模块不注册（OnPacket 路由不命中 → 该消息不同步）
            try { OpenNestCore.Logging.CoopLog.Warn("CoopSyncRegistry.conflict", () => $"[Registry] CONFLICT type={module.MsgType} class={module.GetType().Name} (already registered)"); } catch { }
            return;
        }
        _modules.Add(module);
        _byType[module.MsgType] = module;
        foreach (var t in extraTypes)
            if (t != 0 && !_byType.ContainsKey(t))
                _byType[t] = module;
        // 注册管理器统一管理：采用硬编码前导字节（记录占用 + 优先级 + 加入统一路由表）
        CoopRuntime.Net?.AdoptModule(module, priority ?? module.NetPriority, extraTypes);
        try { OpenNestCore.Logging.CoopLog.Info("CoopSyncRegistry.reg", () => $"[Registry] register type={module.MsgType} pri={priority ?? module.NetPriority} class={module.GetType().Name}", 2f); } catch { }
    }

    /// <summary>
    /// 动态注册（模块自行注册同步通道）：用稳定 channelKey 注册，前导字节由 NetManager 统一分配
    /// （自动避开框架枚举 / 已采用模块 / V2 200-229，杜绝手工挑号冲突）。
    /// 模块发送消息时用 <see cref="OpenNestCoop.Net.NetManager.ChannelType"/> 取分配字节（MsgType 属性返回它）。
    /// <paramref name="priority"/> 缺省（null）→ 取模块 <see cref="ISyncedModule.NetPriority"/>（默认 Normal）。
    /// 返回分配的前导字节（int，支持 2 字节扩展 ≥256；0=分配失败）。
    /// </summary>
    public static int RegisterDynamicChannel(ISyncedModule module, string channelKey, NetModulePriority? priority = null)
    {
        if (module == null || string.IsNullOrEmpty(channelKey)) return 0;
        var net = CoopRuntime.Net;
        if (net == null) return 0;
        int t = net.RegisterChannel(channelKey, module, priority ?? module.NetPriority);
        if (t == 0) return 0;
        AttachModule(module, t);
        try { OpenNestCore.Logging.CoopLog.Info("CoopSyncRegistry.dyn", () => $"[Registry] dynamic channel '{channelKey}' → type={t} pri={priority ?? module.NetPriority} class={module.GetType().Name}", 2f); } catch { }
        return t;
    }

    /// <summary>把模块加入生命周期列表 + 路由表（Tick/会话事件/重置/中途加入由本注册表驱动；不做通道表登记——
    /// 供 NetManager 函数式注册 RegisterChannel(string, ChannelCallbacks, ...) 与 RegisterDynamicChannel 内部调用）。</summary>
    public static void AttachModule(ISyncedModule module, int type)
    {
        if (module == null || type == 0) return;
        if (_byType.ContainsKey(type)) return;
        _modules.Add(module);
        _byType[type] = module;
    }

    // ---------------- 模块自注册（每个模块在自己文件里 [ModuleInitializer] 入队；Startup 冲刷） ----------------

    private sealed class PendingReg
    {
        public bool NewScheme;                 // true=V2(--sync new)，false=V1(--sync old)
        public Func<ISyncedModule> Factory;    // 延迟构造（在 Startup Flush 时构造，避免程序集加载期构造异常）
        public string ChannelKey;              // 动态注册 key（null = 静态采用）
        public NetModulePriority? Priority;
        public byte[] ExtraTypes;
        public Action Deferred;                // 非 ISyncedModule 注册（快照/值绑定等）
    }

    private static readonly List<PendingReg> _pending = new();

    /// <summary>模块自注册入口：在模块文件里用 [ModuleInitializer] 调用（程序集加载时入队，
    /// Startup 解析 --sync 方案后统一冲刷，V1/V2 互斥）。<paramref name="newScheme"/>：true=仅 --sync new（V2）
    /// 注册；false=仅 --sync old（V1）注册。<paramref name="factory"/> 在 Flush 时才构造模块（安全）。</summary>
    public static void PendingRegister(bool newScheme, Func<ISyncedModule> factory,
        string channelKey = null, NetModulePriority? priority = null, params byte[] extraTypes)
    {
        if (factory == null) return;
        _pending.Add(new PendingReg { NewScheme = newScheme, Factory = factory, ChannelKey = channelKey, Priority = priority, ExtraTypes = extraTypes });
    }

    /// <summary>非 ISyncedModule 的自注册（快照注册/值绑定等）：<paramref name="newScheme"/> 命中当前方案时
    /// 在 Flush 执行 <paramref name="register"/>。同样用 [ModuleInitializer] 在模块文件里调用。</summary>
    public static void PendingRegister(bool newScheme, Action register)
    {
        if (register == null) return;
        _pending.Add(new PendingReg { NewScheme = newScheme, Deferred = register });
    }

    /// <summary>Startup 解析 --sync 方案后调用：注册当前方案对应的全部自注册模块（V1/V2 互斥）。
    /// 构造在 try/catch 内（单模块失败不阻断其余）。</summary>
    public static void FlushPending()
    {
        if (_pending.Count == 0) return;
        bool wantNew = OpenNestCoop.Net.AutoJoin.WantNewSync;
        int done = 0;
        foreach (var p in _pending)
        {
            if (p.NewScheme != wantNew) continue; // 只注册当前方案
            try
            {
                if (p.Deferred != null) { p.Deferred(); }
                else if (!string.IsNullOrEmpty(p.ChannelKey)) RegisterDynamicChannel(p.Factory(), p.ChannelKey, p.Priority);
                else RegisterModule(p.Factory(), p.Priority, p.ExtraTypes);
                done++;
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[Registry] pending register {p.Factory?.GetType().Name}: {ex.Message}"); }
        }
        _pending.Clear();
        try { OpenNestCore.Logging.CoopLog.Info("CoopSyncRegistry.flush", () => $"[Registry] flushed {done} self-registered modules (scheme={(wantNew ? "new" : "old")})", 2f); } catch { }
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
            // ⚠️ per-module 网络分级：每模块按注册管理器登记的优先级缩放 dt（NetworkGovernor.ModuleFreq）。
            // 优先级随注册一起提供（缺省 = 模块 NetPriority，默认 Normal）；未登记时回退模块 NetPriority。
            var pri = net?.ChannelPriority(m.MsgType) ?? m.NetPriority;
            float md = dt * OpenNestCoop.Net.NetworkGovernor.Instance.ModuleFreq(pri);
            try
            {
                // ⚠️ 帧性能剖析（F7 诊断）：测量每模块 Tick 耗时，按模块类型名归因
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                m.Tick(md);
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                OpenNestCoop.Core.FrameProfiler.Instance.AddMs(m.GetType().Name, (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CoopSyncRegistry Tick: {ex.Message}"); }
        }
    }

    /// <summary>NetManager.OnPacket 调用：若消息类型被某模块注册则路由给它并返回 true（int，兼容 2 字节扩展类型）。</summary>
    public static bool TryRoute(int type, ulong from, byte[] data)
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
