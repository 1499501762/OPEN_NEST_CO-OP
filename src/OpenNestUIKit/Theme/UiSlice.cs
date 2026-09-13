using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace OpenNestUIKit.Theme;

/// <summary>切片渲染模式（对应 <c>Image.Type</c>；<see cref="None"/> = 不用这张图）。</summary>
public enum UiSliceMode
{
    Sliced = 0,
    Simple = 1,
    Tiled = 2,
    None = 3,
}

/// <summary>
/// 一条切片定义（**用户/工具给出的"正确切片"**）。
///
/// 语义（与 `docs/UI_KIT_SLICE.md` 一致）：
/// - <see cref="Left"/>.. <see cref="Bottom"/> = 基于**该 sprite 原图尺寸**的九宫格切割线（像素）；
/// - <see cref="Scale"/> = 把 border 再 **除以** 它后烤进副本（1 = 原样；想更细的边框就调大）；
/// - <see cref="Mode"/> = Sliced / Tiled / Simple / None；
/// - <see cref="FillCenter"/> = Sliced 时是否绘制中心（中心透明的装饰框保持 true，由底层纯色填充兜住）；
/// - <see cref="Tag"/> = **用途标记**（哪种组件的背景），如 `panel` / `button` / `button.primary` / `button.danger`
///   / `dialog` / `separator` / `input` / `tab` / `row` / `frame`；库按 tag 优先挑素材（见 `UiSkin.RefForTag`）。
///
/// ⚠️ 一旦某张图在本文件里有定义，库就**无条件采用**（不再自动缩放、不再退回纯色）——
/// 切片是人的判断，工具的自动规则只在"没有定义"时兜底。
/// ⚠️ 本文件**只影响 OpenNestUIKit 自己画的界面**（我们的菜单/注入项）；**不会修改游戏原生 UI 的任何东西**：
/// 游戏原有 Sprite 不会被改 border，也不会被换成我们的副本（我们只用 `Sprite.Create` 出的副本）。
/// </summary>
public struct UiSlice
{
    public bool HasValue;
    public float Left, Top, Right, Bottom;
    public float Scale;
    public UiSliceMode Mode;
    public bool FillCenter;
    /// <summary>用途标记（哪种组件的背景）；空 = 未标记。</summary>
    public string Tag;

    /// <summary>作者原值（尚未人工调整）。</summary>
    public static UiSlice FromAuthor(Vector4 border)
        => new UiSlice { HasValue = true, Left = border.x, Top = border.y, Right = border.z, Bottom = border.w, Scale = 1f, Mode = UiSliceMode.Sliced, FillCenter = true };

    /// <summary>border（原图坐标）。</summary>
    public Vector4 Border => new Vector4(Left, Top, Right, Bottom);

    /// <summary>烤进副本的 border（= border ÷ scale，向下取整）。</summary>
    public Vector4 BakedBorder
    {
        get
        {
            float s = Scale <= 0f ? 1f : Scale;
            return new Vector4(
                Mathf.Floor(Left / s), Mathf.Floor(Top / s),
                Mathf.Floor(Right / s), Mathf.Floor(Bottom / s));
        }
    }

    public string ModeName
    {
        get
        {
            switch (Mode)
            {
                case UiSliceMode.Simple: return "simple";
                case UiSliceMode.Tiled: return "tiled";
                case UiSliceMode.None: return "none";
                default: return "sliced";
            }
        }
    }

    public static UiSliceMode ParseMode(string s)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "simple": return UiSliceMode.Simple;
            case "tiled": return UiSliceMode.Tiled;
            case "none": return UiSliceMode.None;
            default: return UiSliceMode.Sliced;
        }
    }

    public static float ParseFloat(string s, float fallback)
        => float.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

    /// <summary>该 border 在给定尺寸下是否"放得下"（九宫格不被压缩）。</summary>
    public bool Fits(float w, float h)
    {
        var b = BakedBorder;
        return b.x + b.z < w && b.y + b.w < h;
    }

    public override string ToString()
        => $"border={BakedBorder} (raw {Border}, scale {Scale:0.##}) mode={ModeName} center={(FillCenter ? 1 : 0)}" +
           (string.IsNullOrEmpty(Tag) ? "" : $" tag={Tag}");
}

/// <summary>
/// 切片定义表（**切片真相来源**）：文件 → 内存表 + 热重载。
///
/// 文件：`&lt;Game&gt;\BepInEx\config\OpenNestUIKit.slices.ini`（BepInEx 端）/
/// `&lt;Game&gt;\UserData\OpenNestUIKit.slices.ini`（MelonLoader 端），INI 格式，可手改，
/// 也可由**离线切片工具** `tools/slice_tool.py` 导出/部署（格式与用法见 `docs/UI_KIT_SLICE.md`）。
/// </summary>
public static class UiSliceStore
{
    private static readonly Dictionary<string, UiSlice> _map = new(StringComparer.OrdinalIgnoreCase);
    private static string _path = "";
    private static bool _inited;
    private static long _mtime;
    private static float _pollT;

    /// <summary>定义变化（加载/手工修改/工具导出）→ 界面可重建 / 素材缓存失效。</summary>
    public static event Action Changed;

    /// <summary>文件路径（未 Init 时为空）。</summary>
    public static string FilePath => _path;

    /// <summary>已加载的定义条数。</summary>
    public static int Count { get { lock (_map) return _map.Count; } }

    /// <summary>是否已初始化。</summary>
    public static bool Loaded => _inited;

    /// <summary>已定义的 sprite 名快照。</summary>
    public static string[] Names { get { lock (_map) return new List<string>(_map.Keys).ToArray(); } }

    /// <summary>初始化（指定文件 + 立即加载；文件不存在则建一份带注释的空模板，方便手改）。</summary>
    public static void Init(string path)
    {
        if (_inited) return;
        _inited = true;
        _path = path ?? "";
        try
        {
            if (_path.Length > 0 && !File.Exists(_path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, TemplateHeader(), new UTF8Encoding(false));
                CoopLog.Info("uikit.slice", () => $"切片定义文件已创建模板：'{_path}'（用 tools/slice_tool.py 导出，或直接编辑本文件）");
            }
        }
        catch (Exception ex) { CoopLog.Warn("uikit.slice", () => $"创建切片定义文件失败：{ex.Message}"); }
        Load();
    }

    /// <summary>从文件加载（覆盖内存表）。</summary>
    public static bool Load()
    {
        if (!_inited || _path.Length == 0) return false;
        try
        {
            lock (_map)
            {
                _map.Clear();
                if (File.Exists(_path))
                {
                    _mtime = File.GetLastWriteTimeUtc(_path).Ticks;
                    string current = null;
                    foreach (var raw in File.ReadAllLines(_path))
                    {
                        string line = (raw ?? "").Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                        if (line[0] == '[' && line[line.Length - 1] == ']')
                        {
                            current = line.Substring(1, line.Length - 2).Trim();
                            continue;
                        }
                        int eq = line.IndexOf('=');
                        if (eq <= 0 || current == null) continue;
                        string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string val = line.Substring(eq + 1).Trim();

                        if (!_map.TryGetValue(current, out var s)) s = new UiSlice { HasValue = true, Scale = 1f, Mode = UiSliceMode.Sliced, FillCenter = true };
                        switch (key)
                        {
                            case "border":
                                {
                                    var parts = val.Split(',');
                                    if (parts.Length == 4)
                                    {
                                        s.Left = UiSlice.ParseFloat(parts[0], s.Left);
                                        s.Top = UiSlice.ParseFloat(parts[1], s.Top);
                                        s.Right = UiSlice.ParseFloat(parts[2], s.Right);
                                        s.Bottom = UiSlice.ParseFloat(parts[3], s.Bottom);
                                    }
                                    break;
                                }
                            case "scale": s.Scale = Mathf.Max(0.01f, UiSlice.ParseFloat(val, s.Scale)); break;
                            case "mode": s.Mode = UiSlice.ParseMode(val); break;
                            case "center": s.FillCenter = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                            case "tag": s.Tag = string.Equals(val, "none", StringComparison.OrdinalIgnoreCase) ? "" : val.Trim(); break;
                            case "all":   // 简写：border + scale + mode + center 一行给全（工具导出用不到，手写方便）
                                break;
                        }
                        s.HasValue = true;
                        _map[current] = s;
                    }
                }
            }
            CoopLog.Info("uikit.slice", () => $"切片定义已加载：{Count} 条 ← '{_path}'");
            Raise();
            return true;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.slice", () => $"加载切片定义失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>写回文件（**导出**：`tools/slice_tool.py` 或其它外部工具写入；库侧不再自带编辑器）。</summary>
    public static bool Save()
    {
        if (_path.Length == 0) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(_path, Snapshot(), new UTF8Encoding(false));
            _mtime = File.GetLastWriteTimeUtc(_path).Ticks;
            CoopLog.Info("uikit.slice", () => $"切片定义已写盘：{Count} 条 → '{_path}'");
            Raise();
            return true;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.slice", () => $"写盘失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>取某张图的定义。</summary>
    public static UiSlice Get(string spriteName)
    {
        if (string.IsNullOrEmpty(spriteName)) return default;
        lock (_map) return _map.TryGetValue(spriteName, out var s) ? s : default;
    }

    /// <summary>是否已有定义。</summary>
    public static bool Has(string spriteName)
    {
        if (string.IsNullOrEmpty(spriteName)) return false;
        lock (_map) return _map.ContainsKey(spriteName);
    }

    /// <summary>设/改定义（只改内存；<see cref="Save"/> 才落盘）。</summary>
    public static void Set(string spriteName, UiSlice slice)
    {
        if (string.IsNullOrEmpty(spriteName)) return;
        slice.HasValue = true;
        if (slice.Scale <= 0f) slice.Scale = 1f;
        lock (_map) _map[spriteName] = slice;
        Raise();
    }

    /// <summary>删掉某条定义（回到"自动选材"）。</summary>
    public static bool Remove(string spriteName)
    {
        if (string.IsNullOrEmpty(spriteName)) return false;
        bool removed;
        lock (_map) removed = _map.Remove(spriteName);
        if (removed) Raise();
        return removed;
    }

    /// <summary>清空全部定义（内存）。</summary>
    public static void ClearAll()
    {
        lock (_map) _map.Clear();
        Raise();
    }

    /// <summary>导出文本（文件内容 = 头部注释 + 全部条目，按名字排序）。</summary>
    public static string Snapshot()
    {
        var sb = new StringBuilder();
        sb.Append(TemplateHeader());
        var names = Names;
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        lock (_map)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (!_map.TryGetValue(names[i], out var s)) continue;
                sb.Append('[').Append(names[i]).Append("]\n");
                var b = s.Border;
                sb.Append("border=").Append(Num(b.x)).Append(',').Append(Num(b.y)).Append(',')
                  .Append(Num(b.z)).Append(',').Append(Num(b.w)).Append('\n');
                sb.Append("scale=").Append(Num(s.Scale)).Append('\n');
                sb.Append("mode=").Append(s.ModeName).Append('\n');
                sb.Append("center=").Append(s.FillCenter ? '1' : '0').Append('\n');
                if (!string.IsNullOrEmpty(s.Tag)) sb.Append("tag=").Append(s.Tag).Append('\n');
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>诊断一行（当前全部定义）。</summary>
    public static string Dump()
    {
        var names = Names;
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.Append("切片定义 ").Append(names.Length).Append(" 条 ← ").Append(_path);
        lock (_map)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (!_map.TryGetValue(names[i], out var s)) continue;
                sb.Append("\n  ").Append(names[i]).Append(": ").Append(s);
            }
        }
        return sb.ToString();
    }

    /// <summary>按 tag 找素材名（精确匹配优先，其次前缀匹配如 `button` → `button.primary`）。<paramref name="tagExactOnly"/> = 只精确匹配。</summary>
    public static string[] ByTag(string tag, bool tagExactOnly = false)
    {
        if (string.IsNullOrEmpty(tag)) return Array.Empty<string>();
        var exact = new List<string>();
        var prefix = new List<string>();
        lock (_map)
        {
            foreach (var kv in _map)
            {
                string t = kv.Value.Tag;
                if (string.IsNullOrEmpty(t)) continue;
                if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) exact.Add(kv.Key);
                else if (!tagExactOnly && t.StartsWith(tag + ".", StringComparison.OrdinalIgnoreCase)) prefix.Add(kv.Key);
            }
        }
        exact.Sort(StringComparer.OrdinalIgnoreCase);
        prefix.Sort(StringComparer.OrdinalIgnoreCase);
        exact.AddRange(prefix);
        return exact.ToArray();
    }

    /// <summary>已用到的 tag 及各自条数（诊断/工具用）。</summary>
    public static string[] Tags()
    {
        var set = new List<string>();
        lock (_map)
        {
            foreach (var kv in _map)
                if (!string.IsNullOrEmpty(kv.Value.Tag) && !set.Contains(kv.Value.Tag)) set.Add(kv.Value.Tag);
        }
        set.Sort(StringComparer.OrdinalIgnoreCase);
        return set.ToArray();
    }

    /// <summary>热重载：每帧节流检查文件 mtime（手改 ini 后自动生效）。</summary>
    public static void Tick(float dt)
    {
        if (!_inited || _path.Length == 0) return;
        _pollT += dt;
        if (_pollT < 2f) return;
        _pollT = 0f;
        try
        {
            if (!File.Exists(_path)) return;
            long m = File.GetLastWriteTimeUtc(_path).Ticks;
            if (m == _mtime) return;
            CoopLog.Info("uikit.slice", () => "切片定义文件被外部修改 → 热重载");
            Load();
        }
        catch { }
    }

    private static string Num(float f)
        => f.ToString("0.###", CultureInfo.InvariantCulture);

    private static string TemplateHeader()
        => "# OpenNestUIKit 九宫格切片定义（**切片真相**：本文件里有定义的图，库无条件采用，不再自动缩放/退纯色）\n" +
           "#\n" +
           "# 字段（每个 [图片名] 段下均可写）：\n" +
           "#   border = 左,上,右,下   基于该图原图尺寸的九宫格切割线（像素）\n" +
           "#   scale  = 1             把 border 再除以它后烤进副本（>1 = 更细的边框；1 = 原样）\n" +
           "#   mode   = sliced|tiled|simple|none   （none = 不使用这张图，退回纯色）\n" +
           "#   center = 1|0           sliced 时是否绘制中心（中心透明的装饰框保持 1，底层纯色会兜住）\n" +
           "#   tag    = panel|button|button.primary|button.danger|dialog|separator|input|tab|row|frame|other\n" +
           "#                        用途标记（哪种组件的背景）；库按 tag 优先为对应控件挑素材\n" +
           "#\n" +
           "# 本文件只影响 **OpenNestUIKit 自己画的界面**（我们的菜单/注入项），\n" +
           "# **不会修改游戏原生 UI**：游戏原有 Sprite 不会被改 border，也不会被换成我们的副本。\n" +
           "#\n" +
           "# 编辑方式：跑离线切片工具（可拖线 + 多尺寸预览 + 一键部署）：\n" +
           "#   python tools/slice_tool.py --gui            详见 docs/UI_KIT_SLICE.md\n" +
           "# 也可手改本文件（保存后 2 秒内自动热重载）。\n" +
           "#\n\n";

    private static void Raise()
    {
        try { Changed?.Invoke(); } catch { }
    }
}
