using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenNestModMenu.Logging;

namespace OpenNestModMenu.Core;

/// <summary>
/// 本模组**自己的偏好设置**（`BepInEx\config\OpenNestModMenu.cfg`，即 <see cref="ModMenuPaths.ConfigFile"/>）。
///
/// 为什么是"cfg + BepInEx 风格元数据注释"（2026-09-13）：
/// ① 用户要求“UI 右下角的 Debug 信息只在调试模式启用” —— 调试模式得**可开关且能存住**，
///    而本模组此前没有任何自己的配置落盘；
/// ② 写成带 `## Setting type:` 注释的 cfg ⇒ **本模组自己的「设置」页也能自动把它解析成控件**
///    （`ModConfigStore` → `ModMenuSettingsSource`），一份文件同时喂给两套界面，不用另写一套设置 UI；
/// ③ 纯文本、手改也生效（和家族里其它模组的配置一个风格）。
/// </summary>
public static class ModMenuPrefs
{
    /// <summary>节名（给配置文件用；保持通用名，方便以后加更多键）。</summary>
    private const string Section = "General";

    /// <summary>调试模式键名（同时也用作设置页的稳定键）。</summary>
    public const string DebugKey = "Debug";

    private static bool _loaded;

    /// <summary>
    /// 调试模式：底栏显示环境摘要（宿主/UIKit/提供者/扫描）、诊断日志全开。
    /// 默认关闭 —— 发布版界面上不该出现这一类信息（用户 2026-09-13）。
    /// </summary>
    public static bool Debug { get; private set; }

    /// <summary>配置文件路径。</summary>
    public static string FilePath => ModMenuPaths.ConfigFile;

    /// <summary>启动时读一次（没有文件就写一份模板出来，让用户能在设置页里看到开关）。</summary>
    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            string path = FilePath;
            if (string.IsNullOrEmpty(path)) return;

            if (!File.Exists(path))
            {
                WriteTemplate(path);
                Debug = false;
                ApplyLogLevel();
                return;
            }

            Debug = ReadBool(path, DebugKey, false);
            ApplyLogLevel();
            CoopLog.Info("modmenu.prefs", () => $"prefs loaded: debug={Debug} ({path})");
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.prefs", () => "load failed: " + ex.Message);
        }
    }

    /// <summary>改调试模式（写盘 + 立刻作用于日志等级）。返回是否写盘成功。</summary>
    public static bool SetDebug(bool on)
    {
        Debug = on;
        ApplyLogLevel();
        bool ok = true;
        try { ok = WriteBool(FilePath, DebugKey, on); }
        catch (Exception ex) { ok = false; CoopLog.Warn("modmenu.prefs", () => "save failed: " + ex.Message); }
        CoopLog.Info("modmenu.prefs", () => $"debug={on} (saved={ok})");
        return ok;
    }

    /// <summary>把调试模式折算成日志等级（Debug 级日志量大，只在调试模式开）。</summary>
    public static void ApplyLogLevel()
    {
        try { CoopLog.Level = Debug ? LogLevel.Debug : LogLevel.Info; } catch { }
    }

    // ---------------- 极简 ini 读写（只碰我们自己的这一个键，其余原样保留） ----------------

    private static void WriteTemplate(string path)
    {
        var sb = new StringBuilder();
        sb.Append("## OpenNestModMenu 自己的设置（由模组菜单维护；手改也生效）\n");
        sb.Append("## 位置: ").Append(path).Append("\n\n");
        sb.Append('[').Append(Section).Append("]\n\n");
        sb.Append("## 调试模式：界面底栏显示环境摘要（宿主 / UIKit / 提供者 / 扫描），并把诊断日志全开。\n");
        sb.Append("## Setting type: System.Boolean\n");
        sb.Append("## Default value: false\n");
        AppendKey(sb, DebugKey, false);
        WriteText(path, sb.ToString());
        CoopLog.Info("modmenu.prefs", () => $"prefs created: {path}");
    }

    private static void AppendKey(StringBuilder sb, string key, bool value)
        => sb.Append(key).Append(" = ").Append(value ? "true" : "false").Append('\n');

    private static bool ReadBool(string path, string key, bool fallback)
    {
        try
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                string t = (lines[i] ?? "").Trim();
                if (t.Length == 0 || t[0] == '#' || t[0] == ';') continue;
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(t.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
                string v = t.Substring(eq + 1).Trim();
                if (bool.TryParse(v, out bool b)) return b;
                return v == "1" || string.Equals(v, "on", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) { CoopLog.Warn("modmenu.prefs", () => "read failed: " + ex.Message); }
        return fallback;
    }

    /// <summary>改一个键：有就替换那一行，没有就在 `[General]` 段里补一行（段不存在则建段）。</summary>
    private static bool WriteBool(string path, string key, bool value)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var lines = new List<string>(File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8) : Array.Empty<string>());

        int sectionAt = -1, insertAt = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = (lines[i] ?? "").Trim();
            if (t.Length == 0 || t[0] == '#' || t[0] == ';') continue;
            if (t.Length > 2 && t[0] == '[' && t[t.Length - 1] == ']')
            {
                if (sectionAt >= 0 && insertAt < 0) insertAt = i;                  // 上一段结束位置
                if (string.Equals(t.Substring(1, t.Length - 2).Trim(), Section, StringComparison.OrdinalIgnoreCase)) sectionAt = i;
                continue;
            }
            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            if (!string.Equals(t.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
            lines[i] = key + " = " + (value ? "true" : "false");
            WriteText(path, string.Join("\n", lines) + "\n");
            return true;
        }

        // 没找到这个键 → 补进（优先补在 [General] 段尾，其次文件尾）
        string newLine = key + " = " + (value ? "true" : "false");
        if (sectionAt >= 0)
        {
            int at = insertAt > sectionAt ? insertAt : lines.Count;
            lines.Insert(at, newLine);
        }
        else
        {
            if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
            lines.Add("[" + Section + "]");
            lines.Add(newLine);
        }
        WriteText(path, string.Join("\n", lines) + "\n");
        return true;
    }

    /// <summary>写文件（UTF-8 **无 BOM**，与本仓其它配置一致）。</summary>
    private static void WriteText(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
}
