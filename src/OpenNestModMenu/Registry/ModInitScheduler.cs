using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using OpenNestModMenu.API;
using OpenNestModMenu.Core;
using OpenNestModMenu.Loaders;

namespace OpenNestModMenu.Registry;

/// <summary>
/// 统一加载/初始化顺序调度器（T8）。
///
/// 解决的问题：两个加载器各自为政 —— BepInEx 插件按目录扫描先后、MelonLoader 模组按
/// <c>MelonPriority</c> 再按名字，**没有任何跨加载器的统一顺序**；而"谁先初始化"对
/// 依赖型模组（A 要给 B 打补丁）是硬约束。这里给出**一份**顺序：
///
/// 1. **基线**：加载器自带的顺序（MelonLoader: <c>Priority</c> 降序 → Id 升序；BepInEx: Id 升序）；
/// 2. **用户覆盖**：用 <see cref="ModOrderTable"/>（可在界面上"上移/下移"）显式指定的条目按编号排到前面；
/// 3. **依赖修正**：被声明为 <see cref="ModEntryInfo.DependsOn"/> 的模组若排在使用者之后 → 前移（拓扑修正，记日志）；
///    同时检测 <see cref="ModEntryInfo.IncompatibleWith"/> 的成对冲突（只告警，不擅自禁用）；
/// 4. **受管初始化**：对声明 <see cref="IModMenuProvider.AutoInit"/> = true 的提供者，按最终顺序调用
///    其 <see cref="IModMenuInit.OnInitialize"/>（每项一次；记录 <see cref="ProviderRecord.InitApplied"/>）。
///
/// 未写入顺序表的条目 = "顺序由加载器决定"（界面上会这么标）；需要确定性顺序的模组请把它写进表里。
/// </summary>
public static class ModInitScheduler
{
    private static readonly List<string> _order = new();          // 有效顺序（Id）
    private static readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
    private static string _sig = "";
    private static float _clock;
    private static float _initDue = float.MaxValue;
    private static string _initWhy = "";
    private static bool _inited;

    /// <summary>有效顺序快照（Id 数组，按加载/初始化先后）。</summary>
    public static string[] EffectiveOrder { get { lock (_order) return _order.ToArray(); } }

    public static int Count { get { lock (_order) return _order.Count; } }
    public static string LastAppliedAt = "";
    public static int LastFixups;      // 上一次应用时做了多少条依赖前移
    public static int LastConflicts;   // 上一次应用时发现的成对不兼容数

    // ---------------- 调度入口 ----------------

    /// <summary>启动时接合同步（注册表变化 → 安排一次初始化尝试）。</summary>
    public static void Attach()
    {
        try
        {
            ModMenuRegistry.Changed += OnRegistryChanged;
            CoopLog.Info("modmenu.order", () => $"scheduler attached ({ModOrderTable.Describe()})");
        }
        catch (Exception ex) { CoopLog.Warn("modmenu.order", () => "attach failed: " + ex.Message); }
    }

    public static void Detach()
    {
        try { ModMenuRegistry.Changed -= OnRegistryChanged; } catch { }
    }

    private static void OnRegistryChanged()
    {
        // 新发现的提供者可能声明了 AutoInit → 稍后统一按顺序初始化（去抖，避免扫描期间反复跑）
        RequestInit("registry-changed", 0.5f);
    }

    /// <summary>请求在 <paramref name="delaySec"/> 秒后跑一次"应用顺序 + 受管初始化"。</summary>
    public static void RequestInit(string why, float delaySec = 0.2f)
    {
        float due = _clock + (delaySec < 0f ? 0f : delaySec);
        if (due < _initDue) { _initDue = due; _initWhy = why ?? ""; }
    }

    /// <summary>每帧驱动（ModMenuBehaviour 调用）。</summary>
    public static void Tick(float dt)
    {
        _clock += dt;
        if (!_inited && _clock > 0.5f) { _inited = true; RequestInit("startup", 0f); }
        if (_initDue == float.MaxValue || _clock < _initDue) return;
        string why = _initWhy.Length > 0 ? _initWhy : "tick";
        _initDue = float.MaxValue;
        _initWhy = "";
        try
        {
            Apply(ModInventory.Entries);
            InitManaged(why);
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.order", () => $"{why}: scheduler failed: {ex.Message}");
        }
    }

    // ---------------- 顺序计算 ----------------

    /// <summary>
    /// 计算有效顺序并回填每个条目的 <see cref="ModEntryInfo.Order"/>（1 起；0 = 不在顺序里）。
    /// 清单刷新后调用（<see cref="ModInventory.Refresh"/>）。
    /// </summary>
    public static void Apply(ModEntryInfo[] entries)
    {
        if (entries == null) return;

        var list = new List<ModEntryInfo>(entries.Length);
        for (int i = 0; i < entries.Length; i++) if (entries[i] != null) list.Add(entries[i]);

        // ① 基线：加载器自带顺序（MLL 优先级降序 → Id 升序）
        list.Sort((a, b) =>
        {
            if (a.Priority != b.Priority) return b.Priority - a.Priority;
            return string.CompareOrdinal(a.Id ?? "", b.Id ?? "");
        });

        // ② 用户显式条目 → 前置（按编号）
        var explicitIds = new List<string>();
        for (int i = 0; i < list.Count; i++)
            if (ModOrderTable.Has(list[i].Id)) explicitIds.Add(list[i].Id);
        explicitIds.Sort((x, y) => ModOrderTable.Get(x, int.MaxValue).CompareTo(ModOrderTable.Get(y, int.MaxValue)));

        var ordered = new List<ModEntryInfo>(list.Count);
        for (int i = 0; i < explicitIds.Count; i++)
        {
            var e = Find(list, explicitIds[i]);
            if (e != null) ordered.Add(e);
        }
        for (int i = 0; i < list.Count; i++)
            if (!ModOrderTable.Has(list[i].Id)) ordered.Add(list[i]);

        // ③ 依赖修正 + 不兼容检测
        int fixups = 0, conflicts = 0;
        for (int pass = 0; pass < ordered.Count; pass++)
        {
            bool moved = false;
            for (int i = 0; i < ordered.Count; i++)
            {
                var e = ordered[i];
                var deps = e.DependsOn;
                if (deps == null || deps.Length == 0) continue;
                for (int d = 0; d < deps.Length; d++)
                {
                    int di = IndexOfDep(ordered, deps[d]);
                    if (di < 0 || di < i) continue;            // 不存在 / 已在前面 → 无需修正
                    var dep = ordered[di];
                    ordered.RemoveAt(di);
                    ordered.Insert(i, dep);
                    i++;                                        // 插入后当前位置已是 dep，跳过
                    fixups++;
                    moved = true;
                    CoopLog.Warn("modmenu.order",
                        () => $"dependency fixup: '{dep.Id}' moved before '{e.Id}' (declared by '{e.Id}')");
                }
            }
            if (!moved) break;
        }

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            e.Order = i + 1;
            if (e.Id != null && e.Id.Length > 0) index[e.Id] = i;
        }
        for (int i = 0; i < entries.Length; i++)
            if (entries[i] != null && !index.ContainsKey(entries[i].Id ?? "")) entries[i].Order = 0;

        // 不兼容：成对出现 → 告警（不擅自禁用；用户自己决定）
        for (int i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            var inc = e.IncompatibleWith;
            if (inc == null || inc.Length == 0) continue;
            for (int k = 0; k < inc.Length; k++)
            {
                if (IndexOfDep(ordered, inc[k]) < 0) continue;
                conflicts++;
                CoopLog.Warn("modmenu.order", () => $"declared incompatibility present: '{e.Id}' <-> '{inc[k]}'");
            }
        }

        lock (_order)
        {
            _order.Clear();
            for (int i = 0; i < ordered.Count; i++) _order.Add(ordered[i].Id ?? "");
            _index.Clear();
            foreach (var kv in index) _index[kv.Key] = kv.Value;
        }
        LastAppliedAt = DateTime.Now.ToString("HH:mm:ss");
        LastFixups = fixups;
        LastConflicts = conflicts;

        // ④ 仅在顺序真的变了时打一条可核对的证据行
        var sb = new StringBuilder();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i > 0) sb.Append(" < ");
            sb.Append('#').Append(i + 1).Append(' ').Append(ordered[i].Id);
        }
        string sig = fixups + "|" + conflicts + "|" + sb;
        if (sig != _sig)
        {
            _sig = sig;
            CoopLog.Info("modmenu.order",
                () => $"effective order ({ordered.Count}): {sb} | fixups={fixups} conflicts={conflicts} explicit={ModOrderTable.ExplicitCount}");
        }
    }

    // ---------------- 受管初始化 ----------------

    /// <summary>按有效顺序执行声明 <c>AutoInit</c> 的提供者初始化（每项一次）。</summary>
    public static int InitManaged(string why)
    {
        int ran = 0;
        string[] ids;
        lock (_order) ids = _order.ToArray();

        for (int i = 0; i < ids.Length; i++)
        {
            if (!ModMenuRegistry.TryGet(ids[i], out var rec) || rec?.Provider == null) continue;

            bool auto;
            try { auto = rec.Provider.AutoInit; } catch { auto = false; }
            if (!auto || rec.InitApplied) continue;

            var sw = Stopwatch.StartNew();
            try
            {
                if (rec.Provider is IModMenuInit init) init.OnInitialize();
                else
                {
                    CoopLog.Warn("modmenu.init",
                        () => $"'{ids[i]}' declares AutoInit but does not implement IModMenuInit — skipped (按序初始化需要实现该接口)");
                }
                rec.InitApplied = true;
                ran++;
                sw.Stop();
                CoopLog.Info("modmenu.init", () => $"initialized '{ids[i]}' (#{i + 1}, why={why}, {sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                rec.InitApplied = true;   // 不重试，避免每帧反复抛
                ModMenuRegistry.SetError(ids[i], ex.Message);
                CoopLog.Error("modmenu.init", () => $"'{ids[i]}' OnInitialize threw: {ex.Message}");
            }
        }

        if (ran > 0) CoopLog.Info("modmenu.init", () => $"managed init pass '{why}': {ran} provider(s) initialized");
        return ran;
    }

    // ---------------- 查询 / 界面支持 ----------------

    /// <summary>该条目在有效顺序里的位置（1 起；0 = 不在）。</summary>
    public static int OrderOf(string id)
    {
        if (string.IsNullOrEmpty(id)) return 0;
        lock (_order) return _index.TryGetValue(id, out int i) ? i + 1 : 0;
    }

    /// <summary>该条目的顺序是用户指定的吗（false = 由加载器决定）。</summary>
    public static bool IsUserOrdered(string id) => ModOrderTable.Has(id);

    /// <summary>界面上"顺序"一行要显示的文案。</summary>
    public static string Describe(ModEntryInfo e)
    {
        if (e == null) return "";
        int n = e.Order > 0 ? e.Order : OrderOf(e.Id);
        if (n <= 0) return ModMenuLoc.L("OrderNone");
        int total;
        lock (_order) total = _order.Count;
        string how = IsUserOrdered(e.Id) ? ModMenuLoc.L("OrderUser") : ModMenuLoc.L("OrderLoader");
        return "#" + n + "/" + total + " · " + how;
    }

    /// <summary>该条目能否上下移动（必须已经在有效顺序里）。</summary>
    public static bool CanReorder(string id) => OrderOf(id) > 0;

    /// <summary>
    /// 上移/下移一个条目（<paramref name="dir"/> = -1 上移 / +1 下移），成功后把**整表**写成显式顺序。
    /// 返回 false 时 <paramref name="message"/> 说明原因（已到首/末位、写盘失败…）。
    /// </summary>
    public static bool Move(string id, int dir, out string message)
    {
        message = "";
        if (string.IsNullOrEmpty(id)) { message = ModMenuLoc.L("ToggleNoEntry"); return false; }

        string[] ids;
        lock (_order) ids = _order.ToArray();
        int idx = Array.FindIndex(ids, s => string.Equals(s, id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) { message = ModMenuLoc.L("OrderNone"); return false; }

        int target = idx + (dir < 0 ? -1 : +1);
        if (target < 0) { message = ModMenuLoc.L("OrderFirst"); return false; }
        if (target >= ids.Length) { message = ModMenuLoc.L("OrderLast"); return false; }

        (ids[idx], ids[target]) = (ids[target], ids[idx]);

        CoopLog.Info("modmenu.order", () => $"swap: idx={idx} target={target} (dir={dir}) a='{ids[target]}' b='{ids[idx]}' -> [{string.Join(" < ", ids)}]");

        if (!ModOrderTable.WriteExplicit(ids, out string err))
        {
            message = ModMenuLoc.L("SettingsWriteFailed", err);
            return false;
        }

        // 立刻按新表重算（让界面上的 #N 与依赖修正马上反映出来）
        try { Apply(ModInventory.Entries); } catch { }

        int now = OrderOf(id);
        message = ModMenuLoc.L("OrderMoved", id, now, ids.Length);
        CoopLog.Info("modmenu.order", () => $"user moved '{id}' -> #{now}/{ids.Length}");
        return true;
    }

    private static ModEntryInfo Find(List<ModEntryInfo> list, string id)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Id, id, StringComparison.OrdinalIgnoreCase)) return list[i];
        return null;
    }

    private static int IndexOfId(List<ModEntryInfo> list, string id)
    {
        if (string.IsNullOrEmpty(id)) return -1;
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// 解析**依赖/不兼容声明里的名字** → 条目下标（找不到 = -1）。三种都比：
    /// ① <see cref="ModEntryInfo.Id"/>（文件名/程序集名 —— MLL 的 `MelonAdditionalDependencies` 是这个）；
    /// ② <see cref="ModEntryInfo.Guid"/>（BepInEx `BepInDependency` 写的是 **GUID**，而 Id 是文件名）；
    /// ③ <see cref="ModEntryInfo.DisplayName"/>（少数模组按显示名互相引用）。
    /// ⚠ 只比 Id 会**静默匹配失败**：实测测试模组声明 `open.nest.uikit`、条目 Id 却是 `OpenNestUIKit`
    /// ⇒ 调度器“依赖前移”形同虚设（用户 2026-09-13：“依赖需要加载顺序吗？有跟随加载顺序吗？”）。
    /// </summary>
    private static int IndexOfDep(List<ModEntryInfo> list, string name)
    {
        if (string.IsNullOrEmpty(name) || list == null) return -1;
        for (int i = 0; i < list.Count; i++) if (SameKey(list[i].Id, name)) return i;
        for (int i = 0; i < list.Count; i++) if (SameKey(list[i].Guid, name)) return i;
        for (int i = 0; i < list.Count; i++) if (SameKey(list[i].DisplayName, name)) return i;
        return -1;
    }

    private static bool SameKey(string a, string b)
        => !string.IsNullOrEmpty(a) && string.Equals(a.Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}
