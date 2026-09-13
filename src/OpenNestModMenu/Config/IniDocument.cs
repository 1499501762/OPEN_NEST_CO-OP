using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenNestModMenu.Config;

/// <summary>
/// 通用 INI 文档（BepInEx <c>.cfg</c> / MelonLoader <c>.cfg</c> / <c>MelonPreferences.cfg</c> 属同一族格式）：
/// <c>[Section]</c> + <c>key = value</c> + <c>#</c>/<c>;</c> 注释 + 空行。
///
/// 设计要点（见 docs/MOD_MENU.md §6.2"最小侵入写回"）：
/// - **逐行保存原文**：注释、注释块、空行、未知键、键的书写风格全部原样保留；
/// - 写回时**只替换"值"那一段**（保留键名拼写与 <c>=</c> 前后的空格风格；原值带引号则新值也带引号）；
/// - 值没变化就**不落盘**（不触碰文件 mtime → 不会触发别人的热重载）；
/// - 首次改动前留一份 <c>*.bak</c>（只留一份，不刷屏）；
/// - 永不抛：解析/写盘失败只记日志并返回 false。
/// </summary>
public sealed class IniDocument
{
    public enum LineKind { Blank, Comment, Section, Key, Other }

    public sealed class Line
    {
        public LineKind Kind;
        /// <summary>原始整行（写回时以它为基础）。</summary>
        public string Raw = "";
        /// <summary>所属节（Section 行为自身节名；Key 行为其所在节；其余为空）。</summary>
        public string Section = "";
        public string Key = "";
        public string Value = "";
        /// <summary>1 起行号。</summary>
        public int Number;

        /// <summary>紧邻其上的连续注释块（BepInEx 风格元数据常在键之前）。</summary>
        public string CommentBefore = "";
        /// <summary>紧邻其下的连续注释块（本项目风格：<c>## 类型: bool | 默认: true</c> 在键之后）。</summary>
        public string CommentAfter = "";
    }

    private readonly List<Line> _lines = new();
    private readonly List<string> _sections = new();
    private bool _dirty;

    public string FilePath { get; private set; } = "";
    /// <summary>原始文本（用于"值没变就不落盘"判定与测试）。</summary>
    public string OriginalText { get; private set; } = "";

    public IReadOnlyList<Line> Lines => _lines;
    public IReadOnlyList<string> Sections => _sections;

    // ---------------- 解析 ----------------

    public static IniDocument Load(string path)
    {
        var doc = new IniDocument { FilePath = path ?? "" };
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return doc;
            string text = File.ReadAllText(path);
            doc.OriginalText = text;
            doc.Parse(text);
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.config", () => $"read failed '{path}': {ex.Message}");
        }
        return doc;
    }

    private void Parse(string text)
    {
        text = text.TrimStart('\uFEFF');                    // 去 BOM（有些编辑器会写）
        var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < raw.Length; i++)
        {
            var line = new Line { Raw = raw[i], Number = i + 1 };
            string t = raw[i].Trim();
            if (t.Length == 0) line.Kind = LineKind.Blank;
            else if (t[0] == '#' || t[0] == ';') line.Kind = LineKind.Comment;
            else if (t.Length > 2 && t[0] == '[' && t[t.Length - 1] == ']')
            {
                line.Kind = LineKind.Section;
                line.Section = t.Substring(1, t.Length - 2).Trim();
                if (!_sections.Contains(line.Section)) _sections.Add(line.Section);
            }
            else
            {
                int eq = raw[i].IndexOf('=');
                if (eq > 0)
                {
                    line.Kind = LineKind.Key;
                    line.Section = _sections.Count > 0 ? _sections[_sections.Count - 1] : "";
                    line.Key = raw[i].Substring(0, eq).Trim();
                    line.Value = raw[i].Substring(eq + 1).Trim();
                }
                else line.Kind = LineKind.Other;
            }
            _lines.Add(line);
        }

        // 补注释块（上下文元数据）：键上方的连续注释 + 键下方的连续注释
        for (int i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].Kind != LineKind.Key) continue;
            _lines[i].CommentBefore = CollectComments(i, -1);
            _lines[i].CommentAfter = CollectComments(i, +1);
        }
    }

    private string CollectComments(int index, int dir)
    {
        var sb = new StringBuilder();
        for (int i = index + dir; i >= 0 && i < _lines.Count; i += dir)
        {
            var l = _lines[i];
            if (l.Kind != LineKind.Comment) break;
            if (dir < 0) sb.Insert(0, l.Raw.Trim() + "\n"); else sb.Append(l.Raw.Trim() + "\n");
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ---------------- 查询 ----------------

    public Line Find(string section, string key)
    {
        var hit = FindExact(section, key);
        if (hit != null) return hit;

        // 容错：调用方可能用“唯一键”约定（Section.Key，如 Sync.CatSync），而文件里只有短键（CatSync）
        // → 去掉最后一段前缀再找一次（SettingItem.Key / ModOrderTable 都是这个约定，已踩）。
        if (!string.IsNullOrEmpty(key))
        {
            int dot = key.LastIndexOf('.');
            if (dot >= 0 && dot < key.Length - 1)
            {
                string shortKey = key.Substring(dot + 1);
                if (!string.Equals(shortKey, key, StringComparison.OrdinalIgnoreCase)) return FindExact(section, shortKey);
            }
        }
        return null;
    }

    private Line FindExact(string section, string key)
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            var l = _lines[i];
            if (l.Kind != LineKind.Key) continue;
            if (!string.Equals(l.Key, key ?? "", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(section) && !string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase)) continue;
            return l;
        }
        return null;
    }

    public IEnumerable<Line> Keys()
    {
        for (int i = 0; i < _lines.Count; i++)
            if (_lines[i].Kind == LineKind.Key) yield return _lines[i];
    }

    // ---------------- 写入（最小侵入） ----------------

    /// <summary>把某行的**值**换成 <paramref name="newValue"/>（保留键名/空格式样；原值带引号则新值也带引号）。</summary>
    public bool SetValue(Line line, string newValue)
    {
        if (line == null || line.Kind != LineKind.Key) return false;
        newValue ??= "";

        int eq = line.Raw.IndexOf('=');
        if (eq < 0) return false;

        string head = line.Raw.Substring(0, eq + 1);
        string tail = line.Raw.Substring(eq + 1);

        int i = 0;
        while (i < tail.Length && (tail[i] == ' ' || tail[i] == '\t')) i++;
        string lead = tail.Substring(0, i);
        if (lead.Length == 0) lead = " ";                    // 原本没空格 → 补一个，保持 `key = value` 可读

        bool quoted = tail.TrimStart().StartsWith("\"", StringComparison.Ordinal);
        string v = quoted ? "\"" + newValue + "\"" : newValue;

        line.Raw = head + lead + v;
        line.Value = newValue;
        _dirty = true;
        return true;
    }

    /// <summary>在指定节末尾追加一个键（节不存在则新建；已存在则改值）。</summary>
    public bool SetOrAppend(string section, string key, string value, string annotation = null)
    {
        var existing = Find(section, key);
        if (existing != null) return SetValue(existing, value);

        var block = new List<Line>();
        int lastOfSection = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            var l = _lines[i];
            if (l.Kind == LineKind.Section && string.Equals(l.Section, section ?? "", StringComparison.OrdinalIgnoreCase))
                lastOfSection = i;
            else if (lastOfSection >= 0 && l.Kind == LineKind.Section) { /* 下一个节开始 */ }
            if (lastOfSection >= 0 && l.Kind == LineKind.Key &&
                string.Equals(l.Section, section ?? "", StringComparison.OrdinalIgnoreCase)) lastOfSection = i;
        }

        if (lastOfSection < 0)
        {
            if (!string.IsNullOrEmpty(section))
            {
                block.Add(new Line { Kind = LineKind.Blank, Raw = "" });
                block.Add(new Line { Kind = LineKind.Section, Section = section, Raw = "[" + section + "]" });
                if (!_sections.Contains(section)) _sections.Add(section);
            }
            else block.Add(new Line { Kind = LineKind.Blank, Raw = "" });
            lastOfSection = _lines.Count - 1;
        }

        var keyLine = new Line { Kind = LineKind.Key, Section = section ?? "", Key = key, Value = value, Raw = key + " = " + value };
        block.Add(keyLine);
        if (!string.IsNullOrEmpty(annotation))
            block.Add(new Line { Kind = LineKind.Comment, Section = section ?? "", Raw = annotation });

        for (int i = 0; i < block.Count; i++) block[i].Number = 0;
        _lines.InsertRange(lastOfSection + 1, block);
        Renumber();
        _dirty = true;
        return true;
    }

    private void Renumber()
    {
        for (int i = 0; i < _lines.Count; i++) _lines[i].Number = i + 1;
    }

    /// <summary>落盘（原子替换；首次改动前留一份 <c>.bak</c>）。返回 false 时 <paramref name="error"/> 有原因。</summary>
    public bool Save(out string error)
    {
        error = "";
        try
        {
            if (!_dirty) return true;
            if (string.IsNullOrEmpty(FilePath)) { error = "no path"; return false; }

            var sb = new StringBuilder();
            for (int i = 0; i < _lines.Count; i++) sb.Append(_lines[i].Raw).Append('\n');
            string text = sb.ToString();

            string dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));

            if (File.Exists(FilePath))
            {
                string bak = FilePath + ".bak";
                if (!File.Exists(bak))
                {
                    try { File.Copy(FilePath, bak, false); } catch { }
                }
                File.Replace(tmp, FilePath, null);          // 原子替换（失败时回退手写）
            }
            else File.Move(tmp, FilePath);

            OriginalText = text;
            _dirty = false;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            CoopLog.Warn("modmenu.config", () => $"write failed '{FilePath}': {ex.Message}");
            return false;
        }
    }

    /// <summary>文本快照（测试/诊断用）。</summary>
    public string Text()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _lines.Count; i++) sb.Append(_lines[i].Raw).Append('\n');
        return sb.ToString();
    }
}
