using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using OpenNestModMenu.API;
using OpenNestModMenu.Config;
using OpenNestModMenu.Core;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestModMenu.UI;

/// <summary>
/// 设置页（T6）：右栏"设置"页签的内容 —— 控件族（开关 / 数值步进 / 枚举循环）+ 分页 + 写回结果。
///
/// 行模型统一为 <see cref="UiSetting"/>：来自配置文件的行（<see cref="ModConfigStore"/>）与
/// 第三方声明式页面（<see cref="PageCollector"/>）都走这里；写回成功/失败都会显示到底部状态行。
///
/// 交互仍然走**自管指针命中**（<c>ModMenuUI.RegisterHot</c>）——游戏 EventSystem 在任务场景里是关的（§5.2）。
/// </summary>
internal static class ModMenuSettingsView
{
    private const float RowH = 30f;

    /// <summary>每页行数（**按宿主高度算**，不是写死的 9）：右栏内容块要铺满背景块整高，
    /// 否则底部会留一片空 —— 用户实测反馈“选项卡所在的块没有填充满整个背景块的高度”。</summary>
    private static int _rowsPerPage = 9;

    private sealed class Row
    {
        public TextMeshProUGUI Label;
        public TextMeshProUGUI Value;
        public UnityEngine.UI.Button Minus, ValueBtn, Plus;
        public UiSetting Item;
    }

    private static RectTransform _host;
    private static readonly List<Row> _rows = new();
    private static TextMeshProUGUI _status, _pageText;
    private static UnityEngine.UI.Button _prev, _next;
    private static bool _built;
    private static UiSetting[] _items = Array.Empty<UiSetting>();
    private static int _page;
    private static string _ownerId = "";

    public static void Build(RectTransform host)
    {
        if (_built || host == null) return;
        _host = host;
        try
        {
            const float right = ModMenuTheme.ColRightW - ModMenuTheme.Pad;
            const float plusW = 28f, valW = 150f, minusW = 28f;
            float plusX = right - plusW;
            float valX = plusX - 4f - valW;
            float minusX = valX - 4f - minusW;
            float labelW = minusX - 8f - ModMenuTheme.Pad;

            _rowsPerPage = FitRows(host, RowH, 30f);

            for (int i = 0; i < _rowsPerPage; i++)
            {
                float y = i * RowH;
                var r = new Row();

                r.Label = UiKit.MakeText(host, "", ModMenuTheme.Pad, y + 4f, labelW, ModMenuTheme.LineH,
                    ModMenuTheme.FontLabel, ModMenuTheme.TextPrimary, TextAlignmentOptions.TopLeft);
                r.Label.overflowMode = TextOverflowModes.Ellipsis;
                r.Label.enableWordWrapping = false;   // 行高只有一行：不能折行

                r.Minus = UiKit.MakeButton(host, "-", minusX, y + 3f, minusW, 22f, Noop,
                    ModMenuTheme.ButtonBg, ModMenuTheme.TextPrimary);
                ModMenuTheme.StyleButton(r.Minus, ModMenuTheme.ButtonBg);
                r.ValueBtn = UiKit.MakeButton(host, "", valX, y + 3f, valW, 22f, Noop,
                    ModMenuTheme.ChipBg, ModMenuTheme.TextPrimary);
                ModMenuTheme.StyleButton(r.ValueBtn, ModMenuTheme.ChipBg);
                r.Plus = UiKit.MakeButton(host, "+", plusX, y + 3f, plusW, 22f, Noop,
                    ModMenuTheme.ButtonBg, ModMenuTheme.TextPrimary);
                ModMenuTheme.StyleButton(r.Plus, ModMenuTheme.ButtonBg);

                r.Value = r.ValueBtn.transform.GetChild(0).GetComponent<TextMeshProUGUI>();
                try { r.Value.fontSize = ModMenuTheme.FontValue; r.Value.overflowMode = TextOverflowModes.Ellipsis; } catch { }

                int captured = i;
                ModMenuUI.RegisterHot(r.Minus, ModMenuTheme.ButtonBg, () => Step(captured, -1));
                ModMenuUI.RegisterHot(r.Plus, ModMenuTheme.ButtonBg, () => Step(captured, +1));
                ModMenuUI.RegisterHot(r.ValueBtn, ModMenuTheme.ChipBg, () => Step(captured, 0));

                _rows.Add(r);
            }

            float fy = FooterY(_rowsPerPage, RowH);
            _prev = UiKit.MakeButton(host, "<", ModMenuTheme.Pad, fy, 40f, 22f, Noop, ModMenuTheme.ButtonBg, ModMenuTheme.TextPrimary);
            ModMenuTheme.StyleButton(_prev, ModMenuTheme.ButtonBg);
            _next = UiKit.MakeButton(host, ">", ModMenuTheme.Pad + 44f, fy, 40f, 22f, Noop, ModMenuTheme.ButtonBg, ModMenuTheme.TextPrimary);
            ModMenuTheme.StyleButton(_next, ModMenuTheme.ButtonBg);
            // ⚠️ 分页文案会“多一截” —— "page 1/1" + "8 item(s)" 超过 120px 时 TMP **会折行**，
            //    屏幕上就变成两行（用户实测反馈："Page 1/1 8" / "item(s)"）
            //    → ① 宽度给足 170；② 关掉自动换行 + 省略号（宁可截断也不折行）。
            _pageText = UiKit.MakeText(host, "", ModMenuTheme.Pad + 92f, fy + 2f, 170f, 20f,
                ModMenuTheme.FontSubtitle, ModMenuTheme.TextSecond, TextAlignmentOptions.TopLeft);
            _pageText.enableWordWrapping = false;
            _pageText.overflowMode = TextOverflowModes.Ellipsis;

            _status = UiKit.MakeText(host, "", ModMenuTheme.Pad + 272f, fy + 2f,
                ModMenuTheme.ColRightW - ModMenuTheme.Pad * 2f - 272f, 20f,
                ModMenuTheme.FontSubtitle, ModMenuTheme.TextSecond, TextAlignmentOptions.TopLeft);
            _status.enableWordWrapping = false;
            _status.overflowMode = TextOverflowModes.Ellipsis;

            ModMenuUI.RegisterHot(_prev, ModMenuTheme.ButtonBg, () => Page(-1));
            ModMenuUI.RegisterHot(_next, ModMenuTheme.ButtonBg, () => Page(+1));

            _built = true;
            SetVisible(false);
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.settings", () => "build failed: " + ex.Message);
        }
    }

    public static void SetVisible(bool v)
    {
        if (!_built) return;
        // ⚠️ 即使状态没变也要把 GameObject 状态扳正（同 ModMenuDebugView：Build 后首次隐藏不能早退，
        //    否则宿主一直可见 → 与诊断页重叠）。
        try { _host.gameObject.SetActive(v); } catch { }
    }

    /// <summary>绑定某个条目的设置（切换选择/切到设置页时调用）。</summary>
    public static void Bind(ModEntryInfo e)
    {
        if (!_built) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool sameOwner = !string.IsNullOrEmpty(e?.Id) && string.Equals(e.Id, _ownerId, StringComparison.OrdinalIgnoreCase);
        int keepPage = sameOwner ? _page : 0;   // 同一所有者重渲（清单变化等）→ 保住页码，不把用户弹回第一页
        _ownerId = e?.Id ?? "";
        // ⚠️ 行内容只有一处实现（`ModMenuSettingsSource`）：UIKit 那边渲染同一份内容，
        //    两边共用 → 迁移后不会出现"这边少一行"的漂移。
        var items = ModMenuSettingsSource.Rows(e);

        _items = items.ToArray();
        if (_items.Length == 0)
            _items = new[] { new UiSetting { Label = ModMenuLoc.L("SettingsNone"), Header = true } };
        _page = keepPage;
        SetVisible(true);
        BindPage();
        int n = _items.Length;
        try { FrameProfiler.Instance.AddMs("settings.scan", sw.Elapsed.TotalMilliseconds); } catch { }
        CoopLog.Debug("modmenu.settings", () => $"bind '{_ownerId}': {n} item(s)");
    }

    public static void Clear()
    {
        _items = Array.Empty<UiSetting>();
        _ownerId = "";
        SetVisible(false);
    }

    /// <summary>按宿主高度算“每页行数”（留出底部一行控件的高度）。</summary>
    internal static int FitRows(RectTransform host, float rowH, float footerH)
    {
        float h = 0f;
        try { h = host != null ? host.sizeDelta.y : 0f; } catch { }
        if (h <= 40f) h = ModMenuTheme.BodyH - 106f;           // 未指定时不报错，按标准右栏算
        int n = (int)((h - footerH) / rowH);
        return n < 4 ? 4 : n;
    }

    /// <summary>分页行 y（紧跟最后一行；因为行数是算出来的，所以它自然就贴着背景块底部）。</summary>
    internal static float FooterY(int rows, float rowH) => rows * rowH + 6f;

    private static void Page(int delta)
    {
        int pages = Math.Max(1, (_items.Length + _rowsPerPage - 1) / _rowsPerPage);
        int p = Math.Max(0, Math.Min(pages - 1, _page + delta));
        if (p == _page) return;
        _page = p;
        BindPage();
    }

    private static void BindPage()
    {
        int start = _page * _rowsPerPage;
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            int idx = start + i;
            var item = idx >= 0 && idx < _items.Length ? _items[idx] : null;
            r.Item = item;

            bool show = item != null;
            try { r.Label.gameObject.SetActive(show); } catch { }
            try { r.ValueBtn.gameObject.SetActive(show && !item.Header && !item.Separator); } catch { }
            try { r.Minus.gameObject.SetActive(show && item.CanEdit && item.Kind != SettingKind.Bool); } catch { }
            try { r.Plus.gameObject.SetActive(show && item.CanEdit && item.Kind != SettingKind.Bool); } catch { }

            if (!show) continue;

            try
            {
                if (item.Header)
                {
                    // ⚠️ 用**纯 ASCII** 标记：游戏 TMP 字体没有 ▸ 这类符号（U+25B8 会渲染成方框，用户实测反馈）
                    r.Label.text = "[ " + item.Label + " ]";
                    r.Label.color = ModMenuTheme.Accent;
                }
                else if (item.Separator)
                {
                    r.Label.text = new string('-', 24);   // 同理：不用制表符 ─（U+2500）
                    r.Label.color = ModMenuTheme.Border;
                }
                else
                {
                    r.Label.text = item.Label;
                    r.Label.color = RowReadOnly(item) ? ModMenuTheme.TextSecond : ModMenuTheme.TextPrimary;
                }
            }
            catch { }

            try
            {
                if (item.Header || item.Separator) continue;
                r.Value.text = Display(item);
                r.Value.color = RowReadOnly(item) ? ModMenuTheme.TextDim : ModMenuTheme.TextPrimary;
            }
            catch { }
        }

        try
        {
            int pages = Math.Max(1, (_items.Length + _rowsPerPage - 1) / _rowsPerPage);
            _pageText.text = ModMenuLoc.L("SettingsPage", _page + 1, pages) + "   " + ModMenuLoc.L("SettingsCount", _items.Length);
            _prev.gameObject.SetActive(pages > 1);
            _next.gameObject.SetActive(pages > 1);
        }
        catch { }
    }

    /// <summary>
    /// 本视图里“没有编辑器、只能看着”的行：真只读 + 文本/快捷键（这里没有输入框，所以文本项也是只读的）。
    ///
    /// ⚠ 2026-09-13：配置层不再把 Text/Key 标成 ReadOnly（UIKit 那边能编辑），所以这里自己判定，
    /// 保持自带面板原有的“文本项灰字”观感不变。
    /// </summary>
    private static bool RowReadOnly(UiSetting s)
        => s == null || s.ReadOnly || s.Kind == SettingKind.Text || s.Kind == SettingKind.Key;

    private static string Display(UiSetting s)
    {
        if (s.Kind == SettingKind.Bool)
        {
            bool b = s.Value != null && (s.Value.Trim().ToLowerInvariant() == "true" || s.Value.Trim() == "1");
            return ModMenuLoc.L(b ? "SettingsOn" : "SettingsOff");
        }
        if (s.Kind == SettingKind.Text && s.Value != null && s.Value.Length > 48) return s.Value.Substring(0, 45) + "...";
        return s.Value ?? "";
    }

    /// <summary>点击控件：<paramref name="dir"/> = -1 减 / +1 加 / 0 = 直接点击值（开关翻转）。</summary>
    private static void Step(int rowIndex, int dir)
    {
        try
        {
            if (rowIndex < 0 || rowIndex >= _rows.Count) return;
            var s = _rows[rowIndex].Item;
            if (s == null || !s.CanEdit) return;

            string next = s.Value ?? "";
            switch (s.Kind)
            {
                case SettingKind.Bool:
                    bool cur = next.Trim().Length > 0 &&
                               (next.Trim().ToLowerInvariant() == "true" || next.Trim() == "1" ||
                                next.Trim().ToLowerInvariant() == "on" || next.Trim().ToLowerInvariant() == "yes");
                    next = (!cur).ToString().ToLowerInvariant();
                    break;

                case SettingKind.Number:
                {
                    if (dir == 0) return;
                    if (!double.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) d = 0;
                    d += dir * (s.Step > 0 ? s.Step : 1);
                    if (s.RangeMin.HasValue && d < s.RangeMin.Value) d = s.RangeMin.Value;
                    if (s.RangeMax.HasValue && d > s.RangeMax.Value) d = s.RangeMax.Value;
                    next = d.ToString("0.####", CultureInfo.InvariantCulture);
                    break;
                }

                case SettingKind.Enum:
                {
                    if (s.Choices.Length == 0) return;
                    int idx = Array.IndexOf(s.Choices, next);
                    if (idx < 0) idx = 0; else idx = (idx + (dir == 0 ? 1 : dir) + s.Choices.Length) % s.Choices.Length;
                    next = s.Choices[idx];
                    break;
                }

                default:
                    return;
            }

            if (string.Equals(next, s.Value, StringComparison.Ordinal)) return;

            bool ok = false;
            string msg = "";
            try { ok = s.Write != null && s.Write(next); }
            catch (Exception ex) { msg = ex.Message; }

            if (ok)
            {
                s.Value = s.Read != null ? (s.Read() ?? next) : next;
                string src = ShortPath(s.Source);
                CoopLog.Debug("modmenu.settings", () => $"changed '{s.Label}' -> {next} ({src})");
                ModMenuUI.SetStatusText(ModMenuLoc.L("SettingsSaved", string.IsNullOrEmpty(src) ? s.Label : src));
            }
            else
            {
                string why = msg.Length > 0 ? msg : (s.LastMessage ?? "");
                ModMenuUI.SetStatusText(ModMenuLoc.L("SettingsWriteFailed", why));
                CoopLog.Warn("modmenu.settings", () => $"write failed '{s.Label}': {why}");
            }

            BindPage();
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.settings", () => "step failed: " + ex.Message);
        }
    }

    private static string ShortPath(string p) => ModMenuSettingsSource.ShortPath(p);

    private static void Noop() { }

    // ---------------- TEMP-VERIFY：自动化取证入口（-onnmm-selftest-t56） ----------------
    // 只给自测开关用；正常游玩路径不调用。目的：让"设置页读/写回"能被日志证明，而不是靠肉眼看屏幕。

    internal static int ItemCount => _items.Length;

    /// <summary>当前页所有条目的一行摘要（证据日志）。</summary>
    internal static string SelfTestSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("owner='").Append(_ownerId).Append("' items=").Append(_items.Length)
          .Append(" page=").Append(_page + 1).Append('/')
          .Append(Math.Max(1, (_items.Length + _rowsPerPage - 1) / _rowsPerPage))
          .Append(" visible=").Append(_built && _host != null && _host.gameObject.activeSelf);
        for (int i = 0; i < _rowsPerPage; i++)
        {
            var s = _items[i];
            sb.Append("\n  [").Append(i).Append(']');
            if (s.Header) { sb.Append(" (header) '").Append(s.Label).Append('\''); continue; }
            sb.Append(" '").Append(s.Label).Append("' kind=").Append(s.Kind)
              .Append(" val='").Append(s.Value).Append("' ro=").Append(s.ReadOnly)
              .Append(" edit=").Append(s.CanEdit)
              .Append(" src='").Append(ShortPath(s.Source)).Append('\'');
            if (s.Section.Length > 0 || s.Key.Length > 0) sb.Append(" key=").Append(s.Section).Append('.').Append(s.Key);
        }
        return sb.ToString();
    }

    /// <summary>对"第一个可编辑条目"做一次点击（开关翻转 / 数值 +step / 枚举下一个），返回发生了什么。</summary>
    internal static string SelfTestStepFirstEditable()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            var s = _rows[i].Item;
            if (s == null || !s.CanEdit) continue;
            string before = s.Value;
            Step(i, s.Kind == SettingKind.Number ? +1 : 0);
            return $"stepped [{i}] '{s.Label}' {s.Kind} '{before}' -> '{s.Value}' status='{_status?.text}'";
        }
        return "no editable row on this page";
    }

    /// <summary>绕过缓存，从磁盘上重新读同一个键（证明写回真的落盘了）。</summary>
    internal static string SelfTestVerifyFromDisk()
    {
        for (int i = 0; i < _items.Length; i++)
        {
            var s = _items[i];
            if (s.Header || s.Separator || s.Source.Length == 0 || s.Key.Length == 0) continue;
            try
            {
                var doc = Config.IniDocument.Load(s.Source);
                var ln = doc.Find(s.Section, s.Key);
                string disk = ln?.Value ?? "<missing>";
                string mem = FindRaw(s.Key);
                return $"disk: '{ShortPath(s.Source)}' [{s.Section}] {s.Key} = '{disk}' (item value='{s.Value}', raw='{mem}')";
            }
            catch (Exception ex) { return "verify failed: " + ex.Message; }
        }
        return "no config-backed item to verify";
    }

    private static string FindRaw(string key)
    {
        try
        {
            var all = ModConfigStore.GetSettings(ModInventory.Find(_ownerId));
            for (int i = 0; i < all.Length; i++) if (all[i].Key == key) return all[i].StringValue;
        }
        catch { }
        return "";
    }
}
