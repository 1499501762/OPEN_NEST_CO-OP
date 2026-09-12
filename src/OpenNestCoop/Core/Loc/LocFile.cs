using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenNestCoop.Core.Loc;

/// <summary>
/// **外部语言键文件**的读取器（2026-09-12 新增，见 `docs/LOCALIZATION.md`）。
///
/// 设计（用户要求：“硬编码文本改为语言键；语言键保存在外部语言键文件中，然后读取”）：
/// - 文件：与配置文件同目录（BepInEx → `BepInEx/config/OpenNestCoop.lang.ini`；
///   MelonLoader → `<游戏目录>/UserData/OpenNestCoop.lang.ini`；都不可用 → `persistentDataPath`），名为 `OpenNestCoop.lang.ini`。
/// - 格式：`[语言代码]` 段 + `键 = 文本` 行；`#`/`;` 注释；`{0}`/`{1}` 为运行期占位符。
/// - **文件是 UI 文本的来源**：新增/修改文案改文件即可（2s 热重载）；代码里只剩键。
/// - 文件缺失/某键缺失 → 用 <see cref="LocDefaults"/> 的内置表**生成/补齐**（首启自动写出），
///   所以永远不会出现“界面显示键名”。
/// - 缺语言段（如只有 [zh] 但游戏是英文）→ 回退内置表 → 再回退键名。
/// - 永不抛异常（语言文件坏了不能影响游戏）。
/// </summary>
public static class LocFile
{
    /// <summary>语言文件名（与 `OpenNestCoop.cfg` 同目录）。</summary>
    public const string FileName = "OpenNestCoop.lang.ini";

    /// <summary>热重载轮询间隔（秒）。</summary>
    public const float PollSeconds = 2f;

    /// <summary>语言文件绝对路径（Init 前为空串）。</summary>
    public static string FilePath { get; private set; } = "";

    /// <summary>当前语言代码（"zh" / "en" …；由 <see cref="SetLanguage"/> 设置）。</summary>
    public static string Language { get; private set; } = "zh";

    /// <summary>回退语言代码（当前语言段缺键时用它）。</summary>
    public const string FallbackLanguage = "en";

    private static readonly Dictionary<string, Dictionary<string, string>> _byLang = new(StringComparer.OrdinalIgnoreCase);
    private static bool _init;
    private static float _timer;
    private static DateTime _lastWrite = DateTime.MinValue;
    private static bool _loggedOnce;

    /// <summary>解析路径 + 首次加载（由 <see cref="CoopRuntime.Startup"/> 调用）。幂等，永不抛。</summary>
    public static void Init()
    {
        if (_init) return;
        _init = true;
        try
        {
            FilePath = ResolvePath();
            Load();
        }
        catch (Exception ex) { Log($"[LocFile] init failed: {ex.Message}"); }
    }

    /// <summary>设置当前语言（语言键文件里的段名；大小写不敏感）。</summary>
    public static void SetLanguage(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        var c = code.Trim().ToLowerInvariant();
        if (c == Language) return;
        Language = c;
        Log($"[LocFile] language = {c}");
    }

    /// <summary>每帧调用（CoopBehaviour.Update 驱动）：mtime 变化即热重载（改文案不用重启）。</summary>
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
            Log($"[LocFile] reloaded ({FilePath})");
        }
        catch (Exception ex) { Log($"[LocFile] reload failed: {ex.Message}"); }
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
            if (text == null)
            {
                var d = LocDefaults.Find(key);
                if (d != null) text = Language == "en" ? d.Value.En : d.Value.Zh;
            }
            if (text == null) text = key;   // 兜底：显示键名（便于发现漏配）
        }
        catch { text = key; }

        if (args != null && args.Length > 0)
        {
            try { text = string.Format(CultureInfo.InvariantCulture, text, args); } catch { }
        }
        return text;
    }

    /// <summary>某键是否在文件里存在（诊断用）。</summary>
    public static bool HasKey(string key)
    {
        try
        {
            return _byLang.TryGetValue(Language, out var sec) && sec != null && sec.ContainsKey(key);
        }
        catch { return false; }
    }

    // ================== 文件 IO ==================

    private static string ResolvePath()
    {
        // 与配置文件同目录（CoopConfig.FilePath 已按宿主解析好；Init 顺序保证它先跑）
        try
        {
            var dir = Path.GetDirectoryName(CoopConfig.FilePath);
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

    /// <summary>读文件 → 解析 → 回写（补齐新键/新语言段）。永不抛。</summary>
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
        catch (Exception ex) { Log($"[LocFile] read failed: {ex.Message}"); }

        try { Save(); } catch (Exception ex) { Log($"[LocFile] write failed: {ex.Message}"); }

        if (!_loggedOnce)
        {
            _loggedOnce = true;
            Log($"[LocFile] {(existed ? "loaded" : "created (defaults)")} {FilePath}");
        }
    }

    private static void Parse(string[] lines)
    {
        if (lines == null) return;
        string section = null;
        foreach (var raw in lines)
        {
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
            if (!_byLang.TryGetValue(section, out var sec)) { sec = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); _byLang[section] = sec; }
            sec[key] = val;
        }
    }

    /// <summary>写出语言文件：内置表全部键 × 语言段（zh/en）+ 文件里已有的其它语言段/额外键（保留用户在文件里的自定义文本）。</summary>
    public static void Save()
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        var sb = new StringBuilder();
        sb.Append("## ").Append(NetConfig.Name).Append(" v").Append(NetConfig.Version).Append(" language file / 语言键文件\n");
        sb.Append("## 本文件是界面文案的**来源**：改这里即可改文案（约 ").Append(PollSeconds.ToString("0")).Append(" 秒热重载，不用重启）。\n");
        sb.Append("## 格式：[语言代码] 段 + `键 = 文本`；# 或 ; 开头是注释；{0} {1} 是运行期占位符。\n");
        sb.Append("## 新增语言：自己加一段（如 [ja]），缺键的项会自动回退到 [en] → 内置默认值。\n");
        sb.Append("## 新增键：升级模组后会自动补进本文件（保留你已改过的文本）。文档：docs/LOCALIZATION.md\n\n");

        // 语言段顺序：zh、en、然后文件里出现的其它段
        var langs = new List<string> { "zh", "en" };
        foreach (var k in _byLang.Keys) if (!langs.Contains(k)) langs.Add(k);

        foreach (var lang in langs)
        {
            sb.Append('[').Append(lang).Append("]\n");
            // 1) 内置表的全部键（值优先取文件里已有的文本）
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in LocDefaults.Items)
            {
                string val = null;
                if (_byLang.TryGetValue(lang, out var sec) && sec != null) sec.TryGetValue(it.Key, out val);
                if (string.IsNullOrEmpty(val)) val = lang.StartsWith("zh") ? it.Zh : it.En;
                sb.Append(it.Key).Append(" = ").Append(val).Append('\n');
                written.Add(it.Key);
            }
            // 2) 用户在文件里自加的键（内置表里没有的，原样保留）
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
        try { CoopRuntime.LogSource?.LogInfo(msg); } catch { }
    }
}
