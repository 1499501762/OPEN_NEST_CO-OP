using System;
using System.Collections.Generic;

namespace OpenNestModMenu.API;

/// <summary>
/// 注册表门面：第三方模组调用 <see cref="Register"/> 挂上自己的设置页；ModMenu 本体读取本注册表。
///
/// 关键设计（见 docs/MOD_MENU.md §4.3"双通道注册"）：
/// - 注册表**放在契约程序集内**，任何时刻可写（不要求 ModMenu 已加载）→ 解决"ModMenu 必须最先加载"的鸡生蛋问题；
/// - ModMenu 未安装时 <see cref="IsHostAvailable"/> 保持 false，注册只是进内存、无副作用；
/// - ModMenu 加载后除读取本表外，还会**被动扫描已加载程序集**发现未主动注册的实现。
/// </summary>
public static class ModMenuHost
{
    /// <summary>契约版本（第三方可据此判断兼容性）。</summary>
    public const string ApiVersion = "0.1.0";

    private static readonly Dictionary<string, IModMenuProvider> _providers =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _sync = new();

    /// <summary>ModMenu 本体是否已就绪（由本体在启动时标记）。</summary>
    public static bool IsHostAvailable { get; private set; }

    /// <summary>ModMenu 本体版本（未就绪为空串）。</summary>
    public static string HostVersion { get; private set; } = "";

    /// <summary>注册表变更（注册/移除/宿主就绪）时触发。</summary>
    public static event Action Changed;

    /// <summary>当前已注册的提供者数量。</summary>
    public static int ProviderCount
    {
        get { lock (_sync) return _providers.Count; }
    }

    /// <summary>当前已注册的提供者快照。</summary>
    public static IModMenuProvider[] Providers
    {
        get
        {
            lock (_sync)
            {
                var arr = new IModMenuProvider[_providers.Count];
                _providers.Values.CopyTo(arr, 0);
                return arr;
            }
        }
    }

    /// <summary>
    /// 注册（或按 Id 覆盖）一个提供者。
    /// 返回 true 表示本次为新注册，false 表示覆盖了同 Id 的旧项或参数非法。
    /// </summary>
    public static bool Register(IModMenuProvider provider)
    {
        if (provider == null || string.IsNullOrEmpty(provider.Id)) return false;
        bool isNew;
        lock (_sync)
        {
            isNew = !_providers.ContainsKey(provider.Id);
            _providers[provider.Id] = provider;
        }
        Raise();
        return isNew;
    }

    /// <summary>按 Id 注销。返回是否确实移除了。</summary>
    public static bool Unregister(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        bool removed;
        lock (_sync) removed = _providers.Remove(id);
        if (removed) Raise();
        return removed;
    }

    /// <summary>按 Id 查询。</summary>
    public static bool TryGet(string id, out IModMenuProvider provider)
    {
        lock (_sync) return _providers.TryGetValue(id ?? "", out provider);
    }

    /// <summary>清空注册表（模组卸载/重载时用）。</summary>
    public static void Clear()
    {
        lock (_sync)
        {
            if (_providers.Count == 0) return;
            _providers.Clear();
        }
        Raise();
    }

    /// <summary>由 OpenNestModMenu 本体在启动时调用：标记宿主就绪 + 版本。</summary>
    internal static void MarkHostAvailable(string version)
    {
        IsHostAvailable = true;
        HostVersion = version ?? "";
        Raise();
    }

    /// <summary>由 OpenNestModMenu 本体在关停时调用。</summary>
    internal static void MarkHostUnavailable()
    {
        IsHostAvailable = false;
        HostVersion = "";
        Raise();
    }

    private static void Raise()
    {
        try { Changed?.Invoke(); }
        catch { /* 订阅方异常不影响注册表 */ }
    }
}
