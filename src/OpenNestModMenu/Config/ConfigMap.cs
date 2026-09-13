using System;
using System.Collections.Generic;
using System.IO;
using OpenNestModMenu.Core;

namespace OpenNestModMenu.Config;

/// <summary>
/// **配置文件映射表**（保存“猜出来的”模组 ↔ 配置文件对应关系）。
///
/// 为什么要存盘：`ConfigLocator` 只能靠惯例/启发式去猜（去掉 `.MelonMod` 后缀、模糊前缀匹配…）。
/// 猜一次就够了 —— 把结果保存下来，下次启动**直接用**，既不再猜、也不再受“以后又多出一个同名 cfg”影响；
/// 用户如果发现猜错了，可以直接改这个文件（就是普通 INI，注释保留）。
///
/// 文件：与本模组配置同级 —— <c>&lt;Game&gt;\BepInEx\config\OpenNestModMenu.configmap.ini</c>（MLL 侧 = <c>UserData\</c>），
/// 形如：
/// <code>
/// [Config]
/// OpenNestCoop.MelonMod = D:\...\UserData\OpenNestCoop.cfg
/// </code>
///
/// 只保存**启发式命中**（变体/模糊/MelonPreferences）；精确命中（id 或 BepInEx GUID 直接对上）本来就是确定的，
/// 不进表，免得把整张表写成一堆噪声。记录指向的文件若已不存在，则视为失效并重新猜。
/// </summary>
internal static class ConfigMap
{
    public const string FileName = "OpenNestModMenu.configmap.ini";
    private const string Section = "Config";

    private static readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    private static string _path = "";

    /// <summary>映射表文件路径（与本模组配置文件同目录）。</summary>
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

    public static int Count { get { EnsureLoaded(); return _map.Count; } }

    /// <summary>取已保存的映射（文件已不存在 → 失效并删除该条，返回 false 以便重新猜）。</summary>
    public static bool TryGet(string id, out string path)
    {
        path = "";
        if (string.IsNullOrEmpty(id)) return false;
        EnsureLoaded();
        if (!_map.TryGetValue(id, out string p) || string.IsNullOrEmpty(p)) return false;
        if (!File.Exists(p))
        {
            CoopLog.Info("modmenu.config", () => $"config map: '{id}' -> '{p}' 已失效（文件不在了）→ 重新匹配");
            _map.Remove(id);
            Save();
            return false;
        }
        path = p;
        return true;
    }

    /// <summary>保存一条映射（立即落盘；值没变则不写）。</summary>
    public static void Set(string id, string path)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(path)) return;
        EnsureLoaded();
        if (_map.TryGetValue(id, out string old) && string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) return;
        _map[id] = path;
        Save();
        CoopLog.Info("modmenu.config", () => $"config map saved: '{id}' = '{path}'");
    }

    /// <summary>清掉内存缓存（清单刷新时调用；不动文件）。</summary>
    public static void Invalidate()
    {
        _loaded = false;
        _map.Clear();
    }

    /// <summary>供调试面板显示。</summary>
    public static string Describe()
    {
        EnsureLoaded();
        return $"{_map.Count} entr(ies)  '{Path}'";
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
            int n = 0;
            foreach (var ln in doc.Keys())
            {
                if (!string.Equals(ln.Section, Section, StringComparison.OrdinalIgnoreCase)) continue;
                string key = (ln.Key ?? "").Trim();
                string val = (ln.Value ?? "").Trim().Trim('"');
                if (key.Length == 0 || val.Length == 0) continue;
                _map[key] = val;
                n++;
            }
            if (n > 0) CoopLog.Info("modmenu.config", () => $"config map loaded: {n} entr(ies) from '{p}'");
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.config", () => "config map load failed: " + ex.Message);
        }
    }

    private static void Save()
    {
        try
        {
            string p = Path;
            IniDocument doc;
            if (File.Exists(p)) doc = IniDocument.Load(p);
            else
            {
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(p);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                }
                catch { }
                File.WriteAllText(p,
                    "; OpenNestModMenu 配置文件映射表\n" +
                    "; 作用：记住“模组 ↔ 它的配置文件”的对应关系（猜出来一次就存下来，下次直接用）。\n" +
                    "; 猜错了可以直接改这里（左边=模组 Id，右边=配置文件路径）；删掉某行 = 让它重新猜。\n\n" +
                    "[" + Section + "]\n");
                doc = IniDocument.Load(p);
            }

            foreach (var kv in _map) doc.SetOrAppend(Section, kv.Key, kv.Value, null);

            string err;
            if (!doc.Save(out err)) CoopLog.Warn("modmenu.config", () => "config map save failed: " + (err ?? ""));
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.config", () => "config map save threw: " + ex.Message);
        }
    }
}
