using System;
using System.Collections.Generic;
using System.IO;
using OpenNestModMenu.Config;
using OpenNestModMenu.Core;

namespace OpenNestModMenu.Loaders;

/// <summary>
/// 统一顺序表（T8）：把"用户希望的加载/初始化顺序"持久化成一个**人能读、也能手改**的 INI 文件。
///
/// 文件：<c>&lt;游戏&gt;\BepInEx\config\OpenNestModMenu.order.ini</c>（MLL 侧 = <c>UserData\</c>），即
/// 与本模组配置文件同级；格式与 BepInEx/ML 的 cfg 一致（<c>[Order]</c> + <c>Id = 10</c>），
/// 复用 <see cref="Config.IniDocument"/> 读写，**只改值、不重排**（用户写的手工注释不会丢）。
///
/// 语义（刻意做得可预测）：
/// - **写了**编号的条目 = 用户显式指定顺序，编号小者在前；
/// - **没写**的条目 = 由加载器决定（MelonLoader 的 <c>MelonPriority</c> 等），排在所有显式条目之后，
///   彼此之间按"优先级降序 → Id 升序"稳定排序；
/// - 编号只在**同一条目被上移/下移时整表重写**（10,20,30…），因此新装模组默认落在末尾，
///   不会因为插进中间而把已有顺序整体顶歪。
///
/// 相关：<see cref="Registry.ModInitScheduler"/> 负责把这里的顺序应用成"有效顺序"（含依赖修正）。
/// </summary>
public static class ModOrderTable
{
    /// <summary>顺序表文件名（与本模组配置文件同目录）。</summary>
    public const string FileName = "OpenNestModMenu.order.ini";

    private const string Section = "Order";
    private const int Step = 10;   // 编号步长：留出手工插入空档

    private static readonly Dictionary<string, int> _map = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    private static string _path = "";

    /// <summary>顺序表文件路径（配置目录第一次解析完成前为空）。</summary>
    public static string Path
    {
        get
        {
            if (_path.Length == 0)
            {
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(ModMenuPaths.ConfigFile ?? "");
                    if (string.IsNullOrEmpty(dir)) dir = ModMenuPaths.LogDir;
                    if (string.IsNullOrEmpty(dir)) dir = ModMenuPaths.GameDir;
                    _path = System.IO.Path.Combine(dir ?? "", FileName);
                }
                catch { _path = FileName; }
            }
            return _path;
        }
    }

    /// <summary>是否已成功读表（读不到 = 空表，不是错误）。</summary>
    public static bool Loaded => _loaded;

    /// <summary>显式指定顺序的条目数。</summary>
    public static int ExplicitCount { get { EnsureLoaded(); return _map.Count; } }

    /// <summary>该条目是否由用户显式指定过顺序。</summary>
    public static bool Has(string id)
    {
        EnsureLoaded();
        return !string.IsNullOrEmpty(id) && _map.ContainsKey(id);
    }

    /// <summary>取编号（没有 = <paramref name="fallback"/>）。</summary>
    public static int Get(string id, int fallback)
    {
        EnsureLoaded();
        if (!string.IsNullOrEmpty(id) && _map.TryGetValue(id, out int n)) return n;
        return fallback;
    }

    /// <summary>重新读表（用户手工改了文件后调用）。</summary>
    public static void Reload()
    {
        _loaded = false;
        _map.Clear();
        EnsureLoaded();
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            string p = Path;
            if (!File.Exists(p)) return;
            var doc = IniDocument.Load(p);
            foreach (var ln in doc.Keys())
            {
                if (!string.Equals(ln.Section, Section, StringComparison.OrdinalIgnoreCase)) continue;
                string key = (ln.Key ?? "").Trim();
                if (key.Length == 0) continue;
                if (!int.TryParse((ln.Value ?? "").Trim(), out int n)) continue;
                _map[key] = n;
            }
            CoopLog.Info("modmenu.order", () => $"order table loaded: {_map.Count} explicit entr(ies) from '{p}'");
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.order", () => $"order table load failed ('{Path}'): {ex.Message}");
        }
    }

    /// <summary>
    /// 把 <paramref name="orderedIds"/> 的当前次序整表写成显式编号（10,20,30…）。
    /// 由"上移/下移"在移动后调用 —— 这样用户看到的顺序就**原样**成为下次启动的顺序。
    /// </summary>
    public static bool WriteExplicit(IList<string> orderedIds, out string error)
    {
        error = "";
        try
        {
            var doc = File.Exists(Path) ? IniDocument.Load(Path) : null;
            if (doc == null)
            {
                // 首次：先落一个带说明的表头，再按正常路径读回（保持"全是 IniDocument 读写"这一条路径）
                File.WriteAllText(Path,
                    "; OpenNestModMenu 统一顺序表（T8）\n" +
                    "; 编号小者先加载/初始化；删掉某行 = 该模组交回加载器决定（排在显式条目之后）\n" +
                    "; 用菜单里的“上移/下移”改，也可以直接在这里手改。\n\n[" + Section + "]\n");
                doc = IniDocument.Load(Path);
            }

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < orderedIds.Count; i++)
            {
                string id = (orderedIds[i] ?? "").Trim();
                if (id.Length == 0) continue;
                map[id] = (i + 1) * Step;
            }

            // 表里多余的行（模组已卸载/被删）保留：用户可能只是临时禁用它
            foreach (var old in doc.Keys())
            {
                if (!string.Equals(old.Section, Section, StringComparison.OrdinalIgnoreCase)) continue;
                string key = (old.Key ?? "").Trim();
                if (key.Length == 0 || map.ContainsKey(key)) continue;
                map[key] = 0;   // 0 = 无显式位置（排在末尾）——保留键但不参与前段排序
            }

            foreach (var kv in map)
                doc.SetOrAppend(Section, kv.Key, kv.Value.ToString(), null);

            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in map) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("  ");
                CoopLog.Info("modmenu.order", () => "write ranks: " + sb);
            }

            string err;
            if (!doc.Save(out err)) { error = err ?? "save failed"; return false; }

            _map.Clear();
            foreach (var kv in map) if (kv.Value > 0) _map[kv.Key] = kv.Value;
            _loaded = true;
            CoopLog.Info("modmenu.order", () => $"order table written: {_map.Count} explicit entr(ies) -> '{Path}'");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            CoopLog.Warn("modmenu.order", () => "order table write failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>诊断用摘要（证据日志 / 内置设置页）。</summary>
    public static string Describe()
    {
        EnsureLoaded();
        return $"{_map.Count} explicit / path='{Path}'";
    }
}
