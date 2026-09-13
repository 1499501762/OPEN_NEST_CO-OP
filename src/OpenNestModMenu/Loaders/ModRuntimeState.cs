using System;
using System.Collections.Generic;

namespace OpenNestModMenu.Loaders;

/// <summary>运行中状态（与"文件是否禁用"是两个维度）。</summary>
public enum RuntimeStop
{
    /// <summary>正常运行中。</summary>
    Running = 0,

    /// <summary>已通过 MelonLoader 官方 API 停用（撤 Harmony patch + 退订回调 + 移出注册表）。</summary>
    StoppedMelon = 1,

    /// <summary>已通过契约（<c>IModMenuProvider.OnDisabled</c>）停用（模组自己实现的可逆停用）。</summary>
    StoppedProvider = 2,

    /// <summary>该宿主不支持运行中停用（如 BepInEx 插件：没有卸载 API）→ 只能重启生效。</summary>
    Unsupported = 3,

    /// <summary>尝试了但失败（详见 <see cref="Info.Detail"/>）→ 只能重启生效。</summary>
    Failed = 4,

    /// <summary>
    /// **软停用**（实验性，BepInEx 插件专用）：撒掉该插件打的所有 Harmony patch + 销毁它加在场景里的组件，
    /// 并尽量调它的 <c>Unload()</c>。⚠️ 不是真卸载：静态状态 / 后台线程 / 已注册的游戏侧订阅**可能残留**。
    /// 文件已改名，所以重启后是彻底干净的。
    /// </summary>
    StoppedSoft = 5,
}

/// <summary>
/// 运行中启停状态表（T13）。
///
/// 为什么需要单独一张表："文件禁用（改扩展名，重启生效）"与"运行中停用（本进程内立即生效）"是**两个维度**：
/// 停用后文件也改了名（下次启动也不加载），但**当前进程**里它到底是活着还是停了，只有这张表知道。
/// UI 把两者分开显示，避免"点了禁用但还在跑"的误解。
/// </summary>
public static class ModRuntimeState
{
    public sealed class Info
    {
        public RuntimeStop Kind = RuntimeStop.Running;
        public string Detail = "";
        public string At = "";
    }

    private static readonly Dictionary<string, Info> _map =
        new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);

    /// <summary>状态变化（UI 订阅 → 置脏重刷）。</summary>
    public static event Action Changed;

    public static bool IsStopped(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_map)
        {
            return _map.TryGetValue(id, out var i) && i.Kind != RuntimeStop.Running;
        }
    }

    public static bool TryGet(string id, out Info info)
    {
        info = null;
        if (string.IsNullOrEmpty(id)) return false;
        lock (_map) return _map.TryGetValue(id, out info);
    }

    /// <summary>记录某条目的运行中状态。</summary>
    public static void Mark(string id, RuntimeStop kind, string detail)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_map)
        {
            _map[id] = new Info
            {
                Kind = kind,
                Detail = detail ?? "",
                At = DateTime.Now.ToString("HH:mm:ss"),
            };
        }
        try { Changed?.Invoke(); } catch { }
    }

    public static void Clear(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        bool removed;
        lock (_map) removed = _map.Remove(id);
        if (removed) { try { Changed?.Invoke(); } catch { } }
    }

    public static void ClearAll()
    {
        lock (_map) _map.Clear();
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>运行中状态 → 语言键（UI 直接取文案）。</summary>
    public static string LabelKey(RuntimeStop kind) => kind switch
    {
        RuntimeStop.StoppedMelon => "RuntimeStoppedMelon",
        RuntimeStop.StoppedProvider => "RuntimeStoppedProvider",
        RuntimeStop.StoppedSoft => "RuntimeStoppedSoft",
        RuntimeStop.Unsupported => "RuntimeUnsupported",
        RuntimeStop.Failed => "RuntimeFailed",
        _ => "RuntimeRunning",
    };

    /// <summary>运行中状态的提示色（绿=在跑 / 橙=已停 / 灰=只能重启 / 红=失败）。</summary>
    public static UnityEngine.Color ColorOf(RuntimeStop kind) => kind switch
    {
        RuntimeStop.StoppedMelon => UI.ModMenuTheme.Ok,
        RuntimeStop.StoppedProvider => UI.ModMenuTheme.Ok,
        RuntimeStop.StoppedSoft => UI.ModMenuTheme.Warn,
        RuntimeStop.Unsupported => UI.ModMenuTheme.TextDim,
        RuntimeStop.Failed => UI.ModMenuTheme.Err,
        _ => UI.ModMenuTheme.Ok,
    };
}
