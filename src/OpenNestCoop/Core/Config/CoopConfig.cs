using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenNestCoop.Core;

/// <summary>
/// Open Nest Co-op 配置文件（**标准 INI / BepInEx `.cfg` 文本格式**；BepInEx + MelonLoader 同一实现）。
///
/// 设计取向（2026-09-12 用户要求“加入配置文件功能，看看通用的模组配置文件的标准”）：
/// - **格式**：与 BepInEx `ConfigFile` 完全一致的 INI 文本（`[Section]` + `key = value` + `## 说明注释`）：
///   可手改、可被 BepInEx.ConfigurationManager 的“显示所有配置文件”读取、纯文本无额外依赖、双加载器一致。
/// - **位置**：按宿主取各自生态的**标准配置目录**（见 <see cref="ResolvePath"/>）——
///   BepInEx → `BepInEx/config/OpenNestCoop.cfg`；MelonLoader → `UserData/OpenNestCoop.cfg`；
///   两者都不存在（便携/非标准部署）→ `Application.persistentDataPath/OpenNestCoop.cfg`。
/// - **自愈**：首启或缺少键时，用默认值 + 注释重写文件（新增设置项后默认自动出现在文件里）；用户不认识的新键保留不丢。
/// - **热重载**：每 <see cref="PollSeconds"/> 秒比对文件 mtime，改动即重读并打印**变化项** → 改配置**不用重启游戏**。
/// - **永不抛异常**：IO/解析失败只告警并退回默认值（配置问题不能挡住联机）。
///
/// ⚠️ **双端一致性**（重要）：这些开关是**本端行为开关**，不参与握手协商。同一局两端设置不一致时，
/// 该功能会“一端发、一端不理”从而表现为不同步。排障先看两端启动日志的 `[CoopConfig] ...` 行是否一致。
/// ⚠️ 新增设置项：① 加对外只读属性；② 在 <see cref="Defs"/> 登记（段/键/说明/默认值）；③ 在 `docs/CONFIG.md` 登记。
/// </summary>
public static class CoopConfig
{
    /// <summary>配置文件名（两端同名，便于对照排障）。</summary>
    public const string FileName = "OpenNestCoop.cfg";

    /// <summary>热重载轮询间隔（秒）。</summary>
    public const float PollSeconds = 2f;

    /// <summary>正在编辑配置时可能读到半个文件 → 连续两次内容一致才接受（防抖）。</summary>
    private const bool ReloadVerboseLog = true;

    // ==================================================================
    //  设置项（对外只读；取值来自 _values，未加载时为默认值）
    // ==================================================================

    /// <summary>`[Sync] CatSync`（默认 <c>true</c>）：猫同步总开关。
    /// 关 = 不做主机 AI 状态广播、客机位置偏差硬同步、猫交互事件同步（CatSync 106/133 全静默）。</summary>
    public static bool CatSync => GetBool("Sync", "CatSync");

    /// <summary>`[Sync] RecordPlayerSync`（默认 <c>true</c>）：唱片机同步开关。
    /// 关 = 不广播/不上行唱片机状态（播放中、曲目、音量、槽内唱片视觉插入）。</summary>
    public static bool RecordPlayerSync => GetBool("Sync", "RecordPlayerSync");

    /// <summary>`[Interactables] CalculateButtonSync`（默认 <c>false</c>）：
    /// `Artillery Computer Console/Calculate Universal Button`（计算万能按钮，LookAtTarget + 4 toggler）同步开关。
    /// ⚠️ 默认关：2026-09-12 新增，未实测；开启需**双端**都设为 true。</summary>
    public static bool CalculateButtonSync => GetBool("Interactables", "CalculateButtonSync");

    // ==================================================================
    //  设置项登记表（默认值 / 段 / 键 / 说明；文件生成与解析都以此为准）
    // ==================================================================

    private sealed class Def
    {
        public string Section;
        public string Key;
        public string Type;      // bool / int / float / string
        public string Default;
        public string Desc;      // 可含 \n（写成多行 ## 注释）
    }

    private static readonly List<Def> Defs = new()
    {
        new Def
        {
            Section = "Sync", Key = "CatSync", Type = "bool", Default = "true",
            Desc = "猫同步开关。\n" +
                   "true（默认）= 同步猫：主机 AI 决策（状态/目标点/动画）+ 猫交互事件 + 客机位置偏差硬同步。\n" +
                   "false = 完全不同步猫（省带宽；若两端都关，猫的行为各自演算、互不影响）。",
        },
        new Def
        {
            Section = "Sync", Key = "RecordPlayerSync", Type = "bool", Default = "true",
            Desc = "唱片机同步开关。\n" +
                   "true（默认）= 同步唱片机播放状态（是否播放 / 曲目 / 音量 / 槽内唱片视觉插入）。\n" +
                   "false = 不同步唱片机（各自播放互不影响）。\n" +
                   "注：唱片**物品位置**（拿起/放下）由 RecordItemSync 负责，不受本开关影响。",
        },
        new Def
        {
            Section = "Interactables", Key = "CalculateButtonSync", Type = "bool", Default = "false",
            Desc = "计算万能按钮同步开关：Artillery Computer Console/Calculate Universal Button。\n" +
                   "组件 = Transform / Animator / LookAtTarget / BoxCollider / AnimatorBoolToggler x4\n" +
                   "（与普通 Universal Button 同构 → 走 ButtonClickSync 点击复现 + 多 toggler 状态轮询）。\n" +
                   "false（默认）= 不同步；true = 同步。⚠️ 需双端一致（本开关不参与握手协商）。",
        },
    };

    // ==================================================================
    //  运行时状态
    // ==================================================================

    /// <summary>键 = "section/key"（忽略大小写）→ 当前值（字符串形态）。</summary>
    private static readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>文件里本模组不认识的键（保留原样，保存时写回——同 BepInEx 对 orphaned entries 的处理）。</summary>
    private static readonly List<KeyValuePair<string, string>> _orphans = new(); // (section, "key = value")

    private static bool _init;
    private static float _timer;
    private static DateTime _lastWrite = DateTime.MinValue;

    /// <summary>配置文件绝对路径（未 Init 时为空串）。</summary>
    public static string FilePath { get; private set; } = "";

    /// <summary>配置值发生变化（热重载）时触发——供需要即时反应的模块订阅（可选）。</summary>
    public static event Action Changed;

    static CoopConfig()
    {
        // 保证 Init 之前也能安全读到默认值（模块 Tick/OnPacket 可能在 Init 前被调用）
        EnsureDefaults();
    }

    // ==================================================================
    //  生命周期
    // ==================================================================

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
        catch (Exception ex) { Log($"[CoopConfig] init failed: {ex.Message}"); }
    }

    /// <summary>每帧调用（由 CoopBehaviour.Update 驱动）：按 mtime 热重载。永不抛。</summary>
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
            string before = Summary();
            Load();
            string after = Summary();
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                Log($"[CoopConfig] reloaded ({FilePath})  {before}  ->  {after}");
                try { Changed?.Invoke(); } catch { }
            }
        }
        catch (Exception ex) { Log($"[CoopConfig] reload failed: {ex.Message}"); }
    }

    /// <summary>显式重读文件（不等轮询）。</summary>
    public static void Reload()
    {
        try { Load(); }
        catch (Exception ex) { Log($"[CoopConfig] reload failed: {ex.Message}"); }
    }

    /// <summary>设置某项并立即保存（供模组菜单/调试/自动化调用）。键不存在则忽略并告警。</summary>
    public static void Set(string section, string key, string value)
    {
        var def = FindDef(section, key);
        if (def == null) { Log($"[CoopConfig] Set: unknown key '{section}/{key}'"); return; }
        string before = Summary();
        _values[Sig(section, key)] = value ?? "";
        try { Save(); } catch { }
        string after = Summary();
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            Log($"[CoopConfig] set {section}/{key}={value}  {before} -> {after}");
            try { Changed?.Invoke(); } catch { }
        }
    }

    // ==================================================================
    //  读取辅助（缺键/格式错 → 默认值）
    // ==================================================================

    private static bool GetBool(string section, string key)
    {
        var def = FindDef(section, key);
        return ParseBool(GetRaw(section, key, def), def != null && ParseBool(def.Default, true));
    }

    private static int GetInt(string section, string key)
    {
        var def = FindDef(section, key);
        var raw = GetRaw(section, key, def);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        return def != null && int.TryParse(def.Default, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    private static float GetFloat(string section, string key)
    {
        var def = FindDef(section, key);
        var raw = GetRaw(section, key, def);
        if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        return def != null && float.TryParse(def.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0f;
    }

    private static string GetString(string section, string key)
    {
        var def = FindDef(section, key);
        return Unquote(GetRaw(section, key, def));
    }

    private static string GetRaw(string section, string key, Def def)
    {
        if (_values.TryGetValue(Sig(section, key), out var v) && v != null) return v;
        return def?.Default ?? "";
    }

    /// <summary>布尔解析：true/false、1/0、on/off、yes/no（忽略大小写与空白）。</summary>
    private static bool ParseBool(string raw, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "true": case "1": case "on": case "yes": case "y": return true;
            case "false": case "0": case "off": case "no": case "n": return false;
            default: return fallback;
        }
    }

    private static string Unquote(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 2) return s ?? "";
        if ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\''))
            return s.Substring(1, s.Length - 2);
        return s;
    }

    // ==================================================================
    //  文件 IO
    // ==================================================================

    /// <summary>按宿主解析配置路径（标准目录优先；都不在 → persistentDataPath；再不行 → CWD）。永不抛。</summary>
    private static string ResolvePath()
    {
        try
        {
            var root = System.Environment.CurrentDirectory; // 与 OpenNestLogs 同源：两端实测 = 游戏根目录
#if MELONLOADER
            var stdDir = Path.Combine(root, "UserData");            // MelonLoader 标准用户数据目录
#else
            var stdDir = Path.Combine(root, "BepInEx", "config");   // BepInEx 标准配置目录
#endif
            if (Directory.Exists(stdDir)) return Path.Combine(stdDir, FileName);
        }
        catch { }
        try
        {
            var p = UnityEngine.Application.persistentDataPath;
            if (!string.IsNullOrEmpty(p)) return Path.Combine(p, FileName);
        }
        catch { }
        return Path.Combine(System.Environment.CurrentDirectory, FileName);
    }

    /// <summary>读文件 → 解析 → 回写规范文件（自愈：补齐缺失键 + 注释）。永不抛。</summary>
    private static void Load()
    {
        EnsureDefaults();
        bool existed = false;
        try
        {
            if (!string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))
            {
                existed = true;
                Parse(File.ReadAllLines(FilePath));
            }
        }
        catch (Exception ex) { Log($"[CoopConfig] read failed: {ex.Message}"); }

        try { Save(); } catch (Exception ex) { Log($"[CoopConfig] write failed: {ex.Message}"); }

        if (ReloadVerboseLog && !_loggedOnce)
        {
            _loggedOnce = true;
            Log($"[CoopConfig] {(existed ? "loaded" : "created (defaults)")} {FilePath}");
            Log($"[CoopConfig] values {Summary()}");
        }
    }

    private static bool _loggedOnce;

    /// <summary>解析 INI 文本（先回到默认值，再套用文件值 → 删掉的键自动恢复默认）。</summary>
    private static void Parse(string[] lines)
    {
        EnsureDefaults();
        // 先全部回默认值：文件里**被删掉的键**应恢复默认（而不是保留上轮内存值）
        foreach (var d in Defs) _values[Sig(d.Section, d.Key)] = d.Default;
        _orphans.Clear();
        if (lines == null) return;
        string section = "";
        foreach (var raw in lines)
        {
            if (raw == null) continue;
            var t = raw.Trim();
            if (t.Length == 0) continue;
            if (t[0] == '#' || t[0] == ';') continue;                 // 注释（含 BepInEx 的 `##`）
            if (t[0] == '[')
            {
                int e = t.IndexOf(']');
                if (e > 1) section = t.Substring(1, e - 1).Trim();
                continue;
            }
            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            string key = t.Substring(0, eq).Trim();
            string val = t.Substring(eq + 1).Trim();
            if (key.Length == 0) continue;
            if (FindDef(section, key) == null)
            {
                _orphans.Add(new KeyValuePair<string, string>(section, $"{key} = {val}"));
                continue;
            }
            _values[Sig(section, key)] = val;
        }
    }

    /// <summary>写出规范配置文件（含注释 + 全部登记项 + 未知键保留）。</summary>
    public static void Save()
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        var sb = new StringBuilder();
        sb.Append("## ").Append(NetConfig.Name).Append(" v").Append(NetConfig.Version).Append(" 配置文件\n");
        sb.Append("## 本文件由模组自动生成：新增设置项会自动补进来；改动**无需重启**（约 ").Append(PollSeconds.ToString("0")).Append(" 秒内热重载）。\n");
        sb.Append("## 布尔写法：true / false（也接受 1/0、on/off、yes/no）。\n");
        sb.Append("## ⚠️ 这些开关是本端行为开关，不参与握手协商——同一局两端请保持一致，否则该功能会表现为不同步。\n");
        sb.Append("## 文档：docs/CONFIG.md\n\n");

        var sections = new List<string>();
        foreach (var d in Defs) if (!sections.Contains(d.Section)) sections.Add(d.Section);
        foreach (var o in _orphans) if (!sections.Contains(o.Key)) sections.Add(o.Key);

        foreach (var section in sections)
        {
            sb.Append('[').Append(section).Append("]\n");
            foreach (var d in Defs)
            {
                if (d.Section != section) continue;
                if (!string.IsNullOrEmpty(d.Desc))
                    foreach (var line in d.Desc.Split('\n'))
                        sb.Append("## ").Append(line).Append('\n');
                sb.Append(d.Key).Append(" = ").Append(GetRaw(section, d.Key, d)).Append('\n');
                sb.Append("## 类型: ").Append(d.Type)
                  .Append(" | 默认: ").Append(d.Default).Append('\n');
                sb.Append('\n');
            }
            foreach (var o in _orphans)
                if (o.Key == section) sb.Append(o.Value).Append('\n');
            sb.Append('\n');
        }

        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        try { _lastWrite = new FileInfo(FilePath).LastWriteTimeUtc; } catch { }
    }

    // ==================================================================
    //  内部工具
    // ==================================================================

    private static void EnsureDefaults()
    {
        foreach (var d in Defs)
        {
            var k = Sig(d.Section, d.Key);
            if (!_values.ContainsKey(k)) _values[k] = d.Default;
        }
    }

    private static string Sig(string section, string key) => (section ?? "") + "/" + (key ?? "");

    private static Def FindDef(string section, string key)
    {
        foreach (var d in Defs)
            if (string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Section, section ?? "", StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    /// <summary>当前全部设置（"键=值" 汇总，用于日志/排障对比两端）。</summary>
    public static string Summary()
    {
        var sb = new StringBuilder();
        foreach (var d in Defs)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(d.Key).Append('=').Append(GetRaw(d.Section, d.Key, d));
        }
        return sb.ToString();
    }

    private static void Log(string msg)
    {
        try { CoopRuntime.LogSource?.LogInfo(msg); } catch { }
    }
}
