using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenNestModMenu.Loc;

/// <summary>
/// **外部语言键文件**读写器（`OpenNestModMenu.lang.ini`）。
///
/// 设计（与 OpenNestCoop 的 `LocFile` 同构，见 `docs/LOCALIZATION.md` / `docs/MOD_MENU.md` §5.4）：
/// - 位置：与配置文件同目录（BepInEx → `BepInEx/config/`；MelonLoader → `<Game>/UserData/`；兜底 `persistentDataPath`）；
/// - 格式：`[语言代码]` 段 + `键 = 文本`；`#`/`;` 注释；`{0}` 是运行期占位符；
/// - **文件是界面文案的来源**：改文案只改文件（2 秒热重载，不用重启）；用户可自加 `[ja]` 等语言段；
/// - 文件缺失/缺键 → 用 <see cref="LocDefaults"/> 生成/补齐（**保留用户已改过的文本**）；
/// - 取文案顺序：当前语言段 → `[en]` 段 → 内置默认 → 键名；
/// - **永不抛异常**（语言文件坏了不能影响游戏）。
/// </summary>
public static class LocFile
{
    /// <summary>语言文件名（与 `OpenNestModMenu.cfg` 同目录）。</summary>
    public const string FileName = "OpenNestModMenu.lang.ini";

    /// <summary>热重载轮询间隔（秒）。</summary>
    public const float PollSeconds = 2f;

    /// <summary>回退语言段。</summary>
    public const string FallbackLanguage = "en";

    /// <summary>语言文件绝对路径（<see cref="Init"/> 前为空串）。</summary>
    public static string FilePath { get; private set; } = "";

    /// <summary>当前语言代码（`zh` / `en` …）。</summary>
    public static string Language { get; private set; } = "en";

    private static readonly Dictionary<string, Dictionary<string, string>> _byLang =
        new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    private static bool _init;
    private static bool _loggedOnce;
    private static float _timer;
    private static DateTime _lastWrite = DateTime.MinValue;

    /// <summary>解析路径 + 首次加载（幂等，永不抛）。</summary>
    public static void Init(string filePath)
    {
        if (_init) return;
        _init = true;
        try
        {
            FilePath = string.IsNullOrEmpty(filePath) ? ResolvePath() : filePath;
            Load();
        }
        catch (Exception ex) { Log($"init failed: {ex.Message}"); }
    }

    /// <summary>设置当前语言（= 语言文件里的段名；大小写不敏感）。</summary>
    public static void SetLanguage(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        var c = code.Trim().ToLowerInvariant();
        if (c == Language) return;
        Language = c;
        Log($"language = {c}");
    }

    /// <summary>每帧驱动：每 <see cref="PollSeconds"/> 秒比对 mtime，变了就热重载。</summary>
    public static void Tick(float dt)
    {
        if (!_init) return;
        _timer += dt;
        if (_timer < PollSeconds) return;
        _timer = 0f;
        try
        {
            var fi = new FileInfo(FilePath);
            if (!fi.Exists) return;
            var w = fi.LastWriteTimeUtc;
            if (w == _lastWrite) return;
            _lastWrite = w;
            Load();
            Log($"reloaded ({FilePath})");
        }
        catch (Exception ex) { Log($"reload failed: {ex.Message}"); }
    }

    /// <summary>取文案：当前语言 → 回退语言 → 内置默认 → 键名。带 `{0}` 占位符时用 <paramref name="args"/> 填充。</summary>
    public static string Get(string key, params object[] args)
    {
        string text = null;
        try
        {
            if (_byLang.TryGetValue(Language, out var sec) && sec != null && sec.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                text = v;
            if (text == null && _byLang.TryGetValue(FallbackLanguage, out var sec2) && sec2 != null && sec2.TryGetValue(key, out var v2) && !string.IsNullOrEmpty(v2))
                text = v2;
            if (text == null && LocDefaults.TryGet(key, out var zh, out var en))
                text = Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;
            if (text == null) text = key;   // 兜底：显示键名（便于发现漏配）
        }
        catch { text = key; }

        if (args != null && args.Length > 0)
        {
            try { text = string.Format(CultureInfo.InvariantCulture, text, args); } catch { }
        }
        return text;
    }

    /// <summary>诊断：某键是否已存在于当前语言段。</summary>
    public static bool HasKey(string key)
    {
        try { return _byLang.TryGetValue(Language, out var sec) && sec != null && sec.ContainsKey(key); }
        catch { return false; }
    }

    // ---------------- 文件 IO ----------------

    private static string ResolvePath()
    {
        try
        {
            var dir = Path.GetDirectoryName(Core.ModMenuPaths.ConfigFile);
            if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, FileName);
        }
        catch { }
        try
        {
            var p = UnityEngine.Application.persistentDataPath;
            if (!string.IsNullOrEmpty(p)) return Path.Combine(p, FileName);
        }
        catch { }
        return Path.Combine(Environment.CurrentDirectory, FileName);
    }

    private static void Load()
    {
        _byLang.Clear();
        bool existed = false;
        try
        {
            if (!string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))
            {
                existed = true;
                Parse(File.ReadAllLines(FilePath));
            }
        }
        catch (Exception ex) { Log($"read failed: {ex.Message}"); }

        try { Save(); } catch (Exception ex) { Log($"write failed: {ex.Message}"); }

        if (!_loggedOnce)
        {
            _loggedOnce = true;
            Log($"{(existed ? "loaded" : "created (defaults)")} {FilePath}");
        }
    }

    private static void Parse(string[] lines)
    {
        if (lines == null) return;
        string section = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (raw == null) continue;
            var t = raw.Trim();
            if (t.Length == 0) continue;
            if (t[0] == '#' || t[0] == ';') continue;
            if (t[0] == '[')
            {
                int e = t.IndexOf(']');
                if (e > 1) section = t.Substring(1, e - 1).Trim().ToLowerInvariant();
                continue;
            }
            int eq = t.IndexOf('=');
            if (eq <= 0 || section == null) continue;
            string key = t.Substring(0, eq).Trim();
            string val = t.Substring(eq + 1).Trim();
            if (key.Length == 0) continue;
            if (!_byLang.TryGetValue(section, out var sec))
            {
                sec = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _byLang[section] = sec;
            }
            sec[key] = val;
        }
    }

    /// <summary>写出语言文件：内置表全部键 × [zh]/[en] + 文件里已有的其它语言段与自定义键（保留用户文本）。</summary>
    public static void Save()
    {
        if (string.IsNullOrEmpty(FilePath)) return;

        var sb = new StringBuilder();
        sb.Append("## ").Append(ModMenuInfo.Name).Append(" v").Append(ModMenuInfo.Version).Append(" language file / 语言键文件\n");
        sb.Append("## 本文件是界面文案的**来源**：改这里即可改文案（约 ").Append(PollSeconds.ToString("0")).Append(" 秒热重载，不用重启）。\n");
        sb.Append("## 格式：[语言代码] 段 + `键 = 文本`；# 或 ; 开头是注释；{0} {1} 是运行期占位符。\n");
        sb.Append("## 新增语言：自己加一段（如 [ja]），缺键的项会自动回退到 [en] → 内置默认值。\n");
        sb.Append("## 新增键：升级模组后会自动补进本文件（保留你已改过的文本）。文档：docs/MOD_MENU.md §5.4\n\n");

        var langs = new List<string> { "zh", FallbackLanguage };
        foreach (var k in _byLang.Keys)
            if (!langs.Contains(k)) langs.Add(k);

        for (int li = 0; li < langs.Count; li++)
        {
            string lang = langs[li];
            sb.Append('[').Append(lang).Append("]\n");

            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < LocDefaults.Items.Length; i++)
            {
                var it = LocDefaults.Items[i];
                string val = null;
                if (_byLang.TryGetValue(lang, out var sec) && sec != null) sec.TryGetValue(it.Key, out val);
                if (string.IsNullOrEmpty(val)) val = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? it.Zh : it.En;
                sb.Append(it.Key).Append(" = ").Append(val).Append('\n');
                written.Add(it.Key);
            }

            if (_byLang.TryGetValue(lang, out var extra) && extra != null)
            {
                foreach (var kv in extra)
                {
                    if (written.Contains(kv.Key)) continue;
                    sb.Append(kv.Key).Append(" = ").Append(kv.Value).Append('\n');
                }
            }
            sb.Append('\n');
        }

        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(true));   // 带 BOM：记事本打开中文不乱码
        try { _lastWrite = new FileInfo(FilePath).LastWriteTimeUtc; } catch { }
    }

    private static void Log(string msg)
    {
        try { Logging.CoopLog.Info("modmenu.loc", () => "lang: " + msg); } catch { }
    }
}
