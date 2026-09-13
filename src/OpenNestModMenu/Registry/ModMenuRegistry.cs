using System;
using System.Collections.Generic;
using OpenNestModMenu.API;
using OpenNestModMenu.Core;

namespace OpenNestModMenu.Registry;

/// <summary>
/// 一条提供者记录：契约注册表只存 <see cref="IModMenuProvider"/> 本身，
/// 这里补上 ModMenu 侧需要的元数据（来源、顺序、初始化状态、错误）。
/// </summary>
public sealed class ProviderRecord
{
    public IModMenuProvider Provider;

    public string Id = "";
    public string DisplayName = "";
    public string Version = "";
    public string Author = "";

    /// <summary>true = 由**被动扫描**发现（该模组没有主动调用 Register）。</summary>
    public bool FromScan;

    /// <summary>true = 本模组内置的设置页。</summary>
    public bool BuiltIn;

    /// <summary>受管初始化是否已执行（T8 的顺序调度器使用）。</summary>
    public bool InitApplied;

    /// <summary>该提供者当前是否被"运行中停用"（由 <see cref="ModMenuRegistry.SetRuntimeDisabled"/> 维护）。</summary>
    public bool RuntimeDisabled;

    /// <summary>注册/构造/初始化过程中的错误（非空 = 有问题，需在 UI 提示）。</summary>
    public string Error;

    /// <summary>统一顺序（T8 由顺序表填充；0 = 未指定）。</summary>
    public int Order;
}

/// <summary>
/// 注册表（ModMenu 侧）：在契约注册表 <see cref="ModMenuHost"/> 之上维护元数据、变更事件与扫描调度。
///
/// **双通道注册**（见 docs/MOD_MENU.md §五）：
/// - **主动**：第三方模组自己调用 <see cref="ModMenuHost.Register"/> —— 注册表在契约程序集里，任何时刻可写；
/// - **被动**：本模组（<see cref="AssemblyScanner"/>）扫描已加载程序集发现契约实现 —— 因此**晚于第三方加载也能发现它**。
/// 两者最终都汇聚到契约注册表，本类通过 <see cref="Sync"/> 把变化同步成记录。
/// </summary>
public static class ModMenuRegistry
{
    private static readonly Dictionary<string, ProviderRecord> _map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _sync = new();
    private static readonly List<(float At, string Why)> _pending = new();
    private static readonly HashSet<string> _scanPending = new(StringComparer.OrdinalIgnoreCase);
    private static float _clock;
    private static bool _scanning;

    /// <summary>记录集合发生变化（新增/移除）。UI 与调度器订阅它。</summary>
    public static event Action Changed;

    // ---------------- 扫描统计（调试面板 / 内置设置页显示） ----------------

    public static string LastScanWhy = "";
    public static string LastScanAt = "";
    public static int LastScanAssemblies;
    public static int LastScanCandidates;
    public static int LastScanNew;
    /// <summary>无法读取类型的程序集数（通常是缺依赖，与提供者无关；不算错误）</summary>
    public static int LastScanLoadFailed;
    /// <summary>提供者层面的失败数（无无参构造函数 / Id 为空 / 构造抛异常）</summary>
    public static int LastScanTypeFailed;
    public static double LastScanMs;
    public static int ScanRuns;

    // ---------------- 查询 ----------------

    public static int Count { get { lock (_sync) return _map.Count; } }

    /// <summary>全部记录（按 Order 再按 Id 排序的快照）。</summary>
    public static ProviderRecord[] All
    {
        get
        {
            lock (_sync)
            {
                var list = new List<ProviderRecord>(_map.Values);
                list.Sort((a, b) => a.Order != b.Order ? a.Order - b.Order : string.CompareOrdinal(a.Id, b.Id));
                return list.ToArray();
            }
        }
    }

    /// <summary>被动扫描发现的提供者数。</summary>
    public static int ScannedCount
    {
        get { lock (_sync) { int n = 0; foreach (var r in _map.Values) if (r.FromScan) n++; return n; } }
    }

    /// <summary>主动注册的提供者数（含内置）。</summary>
    public static int ActiveCount => Count - ScannedCount;

    public static bool TryGet(string id, out ProviderRecord record)
    {
        lock (_sync) return _map.TryGetValue(id ?? "", out record);
    }

    /// <summary>
    /// 运行中启用/停用一个**受管模组**（契约 <see cref="IModMenuProvider.CanToggleAtRuntime"/> = true）：
    /// 调用 <see cref="IModMenuProvider.OnDisabled"/>/<see cref="IModMenuProvider.OnEnabled"/>。
    ///
    /// 为何只能靠模组自己：.NET 程序集一旦加载就不可卸载（BepInEx/MelonLoader 都没有卸载 API），
    /// 所以“运行中停用”只能由模组实现可逆停用（退订事件/停自己的循环/撤自己的 patch）。
    /// </summary>
    public static bool SetRuntimeDisabled(string id, bool disabled, out string message)
    {
        message = "";
        ProviderRecord rec;
        lock (_sync) _map.TryGetValue(id ?? "", out rec);
        if (rec?.Provider == null) { message = ModMenuLoc.L("RuntimeNoProvider"); return false; }

        bool can;
        try { can = rec.Provider.CanToggleAtRuntime; } catch { can = false; }
        if (!can) { message = ModMenuLoc.L("RuntimeNotSupported"); return false; }

        try
        {
            if (disabled) rec.Provider.OnDisabled(); else rec.Provider.OnEnabled();
            rec.RuntimeDisabled = disabled;
            message = disabled ? ModMenuLoc.L("RuntimeStoppedProvider") : ModMenuLoc.L("RuntimeResumedProvider");
            CoopLog.Info("modmenu.runtime", () => $"provider '{id}' runtime {(disabled ? "disabled" : "enabled")}");
            Raise();
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            CoopLog.Warn("modmenu.runtime", () => $"provider '{id}' toggle threw: {ex.Message}");
            return false;
        }
    }

    /// <summary>按 Id 取提供者（不存在返回 false）。</summary>
    public static bool TryGetProvider(string id, out IModMenuProvider provider)
    {
        provider = null;
        lock (_sync)
        {
            if (_map.TryGetValue(id ?? "", out var rec)) { provider = rec?.Provider; return provider != null; }
        }
        return false;
    }

    public static bool IsPresent(string id)
    {
        lock (_sync) return _map.ContainsKey(id ?? "");
    }

    // ---------------- 同步（契约注册表 → 本记录表） ----------------

    /// <summary>
    /// 把契约注册表的当前内容同步成记录：新增的建记录、已消失的移除、元数据变化的重读。
    /// 由 <see cref="ModMenuHost.Changed"/> 触发（主动注册与被动发现都会走到），也可手动调用。
    /// </summary>
    public static void Sync()
    {
        var live = ModMenuHost.Providers;
        List<ProviderRecord> added = null;
        bool dirty = false;

        lock (_sync)
        {
            // 1) 移除已注销的
            if (_map.Count > 0)
            {
                var stale = new List<string>();
                foreach (var id in _map.Keys)
                {
                    bool found = false;
                    for (int i = 0; i < live.Length; i++)
                    {
                        if (live[i] != null && string.Equals(live[i].Id, id, StringComparison.OrdinalIgnoreCase))
                        { found = true; break; }
                    }
                    if (!found) stale.Add(id);
                }
                for (int i = 0; i < stale.Count; i++) { _map.Remove(stale[i]); dirty = true; }
            }

            // 2) 新增 / 刷新元数据
            for (int i = 0; i < live.Length; i++)
            {
                var p = live[i];
                if (p == null) continue;
                string id;
                try { id = p.Id; } catch (Exception ex) { CoopLog.Warn("registry.sync", () => $"provider.Id threw: {ex.Message}"); continue; }
                if (string.IsNullOrEmpty(id)) continue;

                if (_map.TryGetValue(id, out var rec))
                {
                    rec.Provider = p;
                    RefreshMeta(rec);
                }
                else
                {
                    rec = new ProviderRecord { Provider = p };
                    RefreshMeta(rec);
                    rec.BuiltIn = id.Equals("OpenNestModMenu", StringComparison.OrdinalIgnoreCase);
                    // 来源在**建记录时**就定下来：被动扫描会在 Register 之前预登记 Id
                    // （否则 Sync 由 Register 内部触发，会先于 MarkFromScan 执行，错误地标为 active）
                    rec.FromScan = _scanPending.Remove(id);
                    _map[id] = rec;
                    (added ??= new List<ProviderRecord>()).Add(rec);
                    dirty = true;
                }
            }
        }

        if (added != null)
        {
            for (int i = 0; i < added.Count; i++)
            {
                var r = added[i];
                CoopLog.Info("registry.add", () => $"provider '{r.Id}' ({r.DisplayName} v{r.Version}) from={(r.FromScan ? "scan" : "active")}");
            }
        }
        if (dirty) Raise();
    }

    /// <summary>
    /// 【被动扫描专用】在调用 <see cref="ModMenuHost.Register"/> **之前**预登记 Id，
    /// 让 <see cref="Sync"/> 在建立记录时就能把来源标为“扫描发现”（Register 会同步触发 Sync）。
    /// </summary>
    public static void MarkScanPending(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_sync) _scanPending.Add(id);
    }

    /// <summary>标记某提供者是"被动扫描发现"的（安全网；正常路径由 <see cref="MarkScanPending"/> 完成）。</summary>
    public static void MarkFromScan(string id)
    {
        bool dirty = false;
        lock (_sync)
        {
            if (_map.TryGetValue(id ?? "", out var rec) && !rec.FromScan)
            {
                rec.FromScan = true;
                dirty = true;
            }
            _scanPending.Remove(id ?? "");
        }
        if (dirty) Raise();
    }

    /// <summary>记录某提供者的错误（保留在记录里供 UI 展示，不抛出）。</summary>
    public static void SetError(string id, string error)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(id ?? "", out var rec)) rec.Error = error;
        }
    }

    /// <summary>清空全部记录（关停时用；契约注册表由 <see cref="ModMenuHost.Clear"/> 单独处理）。</summary>
    public static void Clear()
    {
        lock (_sync)
        {
            if (_map.Count == 0) return;
            _map.Clear();
        }
        Raise();
    }

    private static void RefreshMeta(ProviderRecord rec)
    {
        // 逐项 try-catch：第三方实现可能抛（本方法在锁内，异常绝不能逸出）
        try { rec.Id = rec.Provider.Id ?? ""; } catch { rec.Id = rec.Id ?? ""; }
        try { rec.DisplayName = rec.Provider.DisplayName ?? ""; } catch { }
        try { rec.Version = rec.Provider.Version ?? ""; } catch { }
        try { rec.Author = rec.Provider.Author ?? ""; } catch { }
        if (string.IsNullOrEmpty(rec.DisplayName)) rec.DisplayName = rec.Id;
    }

    private static void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { CoopLog.Warn("registry.changed", () => $"subscriber threw: {ex.Message}"); }
    }

    // ---------------- 扫描调度 ----------------

    /// <summary>安排若干次延迟扫描（秒）。用于兜底"比本模组晚加载"的模组（经桥加载的 MLL 模组在首帧才加载）。</summary>
    public static void ScheduleScan(params float[] delaysSec)
    {
        if (delaysSec == null) return;
        lock (_sync)
        {
            for (int i = 0; i < delaysSec.Length; i++)
            {
                float d = delaysSec[i];
                if (d < 0f) d = 0f;
                _pending.Add((_clock + d, "scheduled"));
            }
        }
    }

    /// <summary>请求尽快重扫一次（用户点"重新扫描"/清单变化后调用）。</summary>
    public static void RequestRescan(string why) => ScheduleScan0(0.1f, why ?? "manual");

    private static void ScheduleScan0(float delaySec, string why)
    {
        lock (_sync) _pending.Add((_clock + delaySec, why));
    }

    /// <summary>每帧驱动（<see cref="Core.ModMenuBehaviour"/> 调用）：到点执行扫描。</summary>
    public static void Tick(float dt)
    {
        _clock += dt;
        while (true)
        {
            string why = null;
            lock (_sync)
            {
                for (int i = 0; i < _pending.Count; i++)
                {
                    if (_pending[i].At <= _clock)
                    {
                        why = _pending[i].Why;
                        _pending.RemoveAt(i);
                        break;
                    }
                }
            }
            if (why == null) return;
            ScanNow(why);
        }
    }

    /// <summary>立即扫描一次（不做重入）。</summary>
    public static int ScanNow(string why)
    {
        if (_scanning) return 0;
        _scanning = true;
        try { return AssemblyScanner.ScanOnce(why ?? "manual"); }
        catch (Exception ex)
        {
            CoopLog.Error("registry.scan", () => $"scan threw: {ex.Message}");
            return 0;
        }
        finally { _scanning = false; }
    }

    /// <summary>由 <see cref="AssemblyScanner"/> 回填本次扫描统计。</summary>
    internal static void RecordScan(string why, int assemblies, int candidates, int added, int loadFailed, int typeFailed, double ms)
    {
        LastScanWhy = why ?? "";
        LastScanAt = DateTime.Now.ToString("HH:mm:ss");
        LastScanAssemblies = assemblies;
        LastScanCandidates = candidates;
        LastScanNew = added;
        LastScanLoadFailed = loadFailed;
        LastScanTypeFailed = typeFailed;
        LastScanMs = ms;
        ScanRuns++;
        try { FrameProfiler.Instance.AddMs("registry.scan", ms); } catch { }
    }
}
