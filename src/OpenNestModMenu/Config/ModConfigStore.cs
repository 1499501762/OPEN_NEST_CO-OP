using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using OpenNestModMenu.API;
using OpenNestModMenu.Core;

namespace OpenNestModMenu.Config;

/// <summary>
/// 模组配置的**读取 + 类型推断 + 最小侵入写回**（T6）。
///
/// 类型从**真实格式**推断（两个社区格式都覆盖）：
/// - BepInEx 官方风格（键**之前**的 <c>##</c> 注释）：<c>## Setting type: System.Boolean</c> /
///   <c>## Default value: true</c> / <c>## Acceptable values: A, B</c> / <c>## Acceptable value range: 0 to 100</c>；
/// - 本模组语系风格（键**之后**的注释）：<c>## 类型: bool | 默认: true | 范围: 0..100</c>；
/// - 都没有 → 按值本身推断（true/false → 开关；数字 → 数值；其余 → 文本，v1 只读展示）。
///
/// 写回走 <see cref="IniDocument"/>（只改值那一段，注释/顺序/未知键全保留），值没变则不落盘。
/// </summary>
public static class ModConfigStore
{
    private sealed class Cache
    {
        public IniDocument Doc;
        public DateTime MtimeUtc;
        public long Size;
        public SettingItem[] Items = Array.Empty<SettingItem>();
    }

    private static readonly Dictionary<string, Cache> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>该条目有没有可展示的设置文件。</summary>
    public static bool HasSettings(ModEntryInfo e) => ConfigLocator.Find(e).Length > 0;

    /// <summary>取（并缓存）该条目的设置项列表；无配置文件返回空数组。</summary>
    public static SettingItem[] GetSettings(ModEntryInfo e)
    {
        string path = ConfigLocator.Find(e);
        if (path.Length == 0) return Array.Empty<SettingItem>();

        try
        {
            var fi = new FileInfo(path);
            if (_cache.TryGetValue(path, out var c) && c != null && c.Size == fi.Length && c.MtimeUtc == fi.LastWriteTimeUtc)
                return c.Items;

            var doc = IniDocument.Load(path);
            var items = Scan(doc, e);
            _cache[path] = new Cache { Doc = doc, MtimeUtc = fi.LastWriteTimeUtc, Size = fi.Length, Items = items };
            CoopLog.Info("modmenu.config", () => $"settings: '{e.Id}' file='{path}' items={items.Length} sections={doc.Sections.Count}");
            return items;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.config", () => $"scan failed '{path}': {ex.Message}");
            return Array.Empty<SettingItem>();
        }
    }

    /// <summary>写回一个设置项（最小侵入）。成功时 <paramref name="item"/> 的值已被更新。</summary>
    public static bool SetValue(SettingItem item, string newValue, out string message)
    {
        message = "";
        try
        {
            if (item == null) { message = "no item"; return false; }
            if (item.ReadOnly) { message = "read-only"; return false; }

            string path = item.SourceFile;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { message = "file missing"; return false; }

            if (!_cache.TryGetValue(path, out var c) || c == null || c.Doc == null)
            {
                var doc0 = IniDocument.Load(path);
                c = new Cache { Doc = doc0 };
                _cache[path] = c;
            }

            var line = c.Doc.Find(item.Section, KeyOf(item));
            if (line == null) { message = "key not found"; return false; }

            if (string.Equals(line.Value, newValue ?? "", StringComparison.Ordinal)) { message = ""; return true; }

            if (!c.Doc.SetValue(line, newValue)) { message = "set failed"; return false; }
            if (!c.Doc.Save(out string err)) { message = "write failed: " + err; return false; }

            // 写盘后刷新缓存：文件 mtime 变了，下次读取会重新扫描
            try { var fi = new FileInfo(path); c.MtimeUtc = fi.LastWriteTimeUtc; c.Size = fi.Length; } catch { }
            _cache.Remove(path);

            item.StringValue = newValue;
            CoopLog.Info("modmenu.config", () => $"write: '{Path.GetFileName(path)}' [{item.Section}] {item.Key} = {newValue}");
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private static string KeyOf(SettingItem item)
        => string.IsNullOrEmpty(item.Key) ? "" : (item.Key.Contains('.') ? item.Key.Substring(item.Key.LastIndexOf('.') + 1) : item.Key);

    // ---------------- 扫描 + 类型推断 ----------------

    private static SettingItem[] Scan(IniDocument doc, ModEntryInfo e)
    {
        var list = new List<SettingItem>();
        foreach (var line in doc.Keys())
        {
            if (string.IsNullOrEmpty(line.Key)) continue;
            var item = new SettingItem
            {
                Owner = e?.Id ?? "",
                Section = line.Section ?? "",
                Key = string.IsNullOrEmpty(line.Section) ? line.Key : line.Section + "." + line.Key,
                Label = line.Key,
                StringValue = line.Value ?? "",
                SourceFile = doc.FilePath,
                SourceLine = line.Number,
                Kind = SettingKind.Text,
            };

            var meta = Meta.Parse(line.CommentBefore, line.CommentAfter);
            item.Description = meta.Description;
            item.DefaultValue = meta.DefaultValue ?? "";

            // ① 声明类型优先
            var kind = meta.Kind;
            if (kind == SettingKind.ReadOnly && !meta.HadType)
                kind = InferFromValue(line.Value);       // ② 按值推断

            if (meta.Choices.Length > 0)
            {
                kind = SettingKind.Enum;
                item.Choices = meta.Choices;
            }
            item.Kind = kind;

            if (kind == SettingKind.Number)
            {
                item.Min = meta.Min;
                item.Max = meta.Max;
                item.Step = meta.Step > 0 ? meta.Step : GuessStep(line.Value);
            }

            // ⚠ 2026-09-13（用户：“Mod配置里只有配置项名没有内容的应该显示空文本框而不是直接暗掉”）：
            //   以前 Text / Key 一律标成 ReadOnly（v1 没有文本编辑，中文 IME 的坑见 docs/MOD_MENU.md §1.1），
            //   UIKit 设置页于是把它们画成灰字只读行 —— 只有键名、值为空的项看上去就是“暗掉的一行”，
            //   而且**没法填值**。
            //   现在 UIKit 有了完整的文本输入管线（原模组 CoopInputBox + IME，见 docs/UI_KIT.md §8.2），
            //   文本/快捷键项可以写回 → 只有注释里**声明了** ReadOnly 的项才真只读。
            //   自带 UGUI 设置页（无 UIKit 时的回退）那里没有编辑器，仍按“文本项只读”画（见 ModMenuSettingsView）。
            item.ReadOnly = kind == SettingKind.ReadOnly;

            list.Add(item);
        }
        return list.ToArray();
    }

    private static SettingKind InferFromValue(string v)
    {
        string s = (v ?? "").Trim();
        if (s.Length == 0) return SettingKind.Text;
        switch (s.ToLowerInvariant())
        {
            case "true": case "false": case "on": case "off": case "yes": case "no":
                return SettingKind.Bool;
        }
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return SettingKind.Number;
        return SettingKind.Text;
    }

    private static double GuessStep(string v)
    {
        string s = (v ?? "").Trim();
        int dot = s.IndexOf('.');
        if (dot < 0 || dot == s.Length - 1) return 1;
        int decimals = s.Length - dot - 1;
        if (decimals > 6) decimals = 6;
        return Math.Round(Math.Pow(10, -decimals), 6);
    }

    /// <summary>从注释块里抽 BepInEx / 本项目两种风格的元数据。</summary>
    internal sealed class Meta
    {
        public bool HadType;
        public SettingKind Kind = SettingKind.ReadOnly;
        public string DefaultValue = null;
        public double Min, Max, Step;
        public string[] Choices = Array.Empty<string>();
        public string Description = "";

        public static Meta Parse(string before, string after)
        {
            var m = new Meta();
            var desc = new List<string>();
            foreach (var block in new[] { before, after })
            {
                if (string.IsNullOrEmpty(block)) continue;
                foreach (var raw in block.Split('\n'))
                {
                    string t = raw.Trim().TrimStart('#', ';', ' ', '\t').Trim();
                    if (t.Length == 0) continue;
                    if (!m.TryMeta(t) && desc.Count < 6) desc.Add(t);
                }
            }
            m.Description = string.Join("\n", desc);
            if (m.Description.Length > 400) m.Description = m.Description.Substring(0, 400);
            return m;
        }

        /// <summary>尝试把一行注释吃掉（BepInEx 风格 + "类型: x | 默认: y" 风格）。返回是否已消费。</summary>
        private bool TryMeta(string t)
        {
            int colon = t.IndexOfAny(new[] { ':', '：' });
            if (colon <= 0) return false;
            string k = t.Substring(0, colon).Trim().ToLowerInvariant();
            string v = t.Substring(colon + 1).Trim();

            // "类型: bool | 默认: true | 范围: 0..100"
            if (v.Contains("|") && (k.Contains("类型") || k.Contains("type")))
            {
                foreach (var part in v.Split('|'))
                {
                    int c2 = part.IndexOfAny(new[] { ':', '：' });
                    if (c2 <= 0) continue;
                    Apply(part.Substring(0, c2).Trim().ToLowerInvariant(), part.Substring(c2 + 1).Trim());
                }
                return true;
            }

            return Apply(k, v);
        }

        private bool Apply(string key, string val)
        {
            if (key.Contains("类型") || key.Contains("type"))
            {
                HadType = true;
                Kind = KindOf(val);
                return true;
            }
            if (key.Contains("默认") || key.Contains("default"))
            {
                DefaultValue = val;
                return true;
            }
            if (key.Contains("可选") || key.Contains("acceptable values") || key.Contains("choices"))
            {
                var arr = new List<string>();
                foreach (var p in val.Split(',')) { var s = p.Trim(); if (s.Length > 0) arr.Add(s); }
                Choices = arr.ToArray();
                return true;
            }
            if (key.Contains("范围") || key.Contains("range"))
            {
                // 0..100 / 0 to 100 / 0-100（与"默认"混排时已在上面的 | 分支拆开）
                var nums = new List<double>();
                var sb = new StringBuilder();
                foreach (char ch in val)
                {
                    if (char.IsDigit(ch) || ch == '.' || ch == '-' || ch == '+') sb.Append(ch);
                    else
                    {
                        if (sb.Length > 0 && double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) nums.Add(d);
                        sb.Clear();
                    }
                }
                if (sb.Length > 0 && double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d2)) nums.Add(d2);
                if (nums.Count >= 2) { Min = nums[0]; Max = nums[1]; }
                if (nums.Count >= 3) Step = nums[2];
                return true;
            }
            return false;
        }

        private static SettingKind KindOf(string s)
        {
            string t = (s ?? "").Trim().ToLowerInvariant();
            if (t.Contains("bool")) return SettingKind.Bool;
            if (t.Contains("int") || t.Contains("float") || t.Contains("double") || t.Contains("number") ||
                t.Contains("single") || t.Contains("数值") || t.Contains("数字")) return SettingKind.Number;
            if (t.Contains("enum") || t.Contains("choice") || t.Contains("枚举")) return SettingKind.Enum;
            if (t.Contains("key") || t.Contains("shortcut") || t.Contains("快捷键")) return SettingKind.Key;
            if (t.Contains("string") || t.Contains("text") || t.Contains("文本")) return SettingKind.Text;
            return SettingKind.ReadOnly;
        }
    }
}
