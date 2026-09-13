using System;
using System.Collections.Generic;
using UnityEngine;
using OpenNestUIKit.Native;

namespace OpenNestUIKit.Test;

/// <summary>
/// **原生控件 Gallery**（测试模组提供）：在游戏原生 ESC 菜单里注入一个独立条目「控件 Gallery」，
/// 点进去是一页**全部原生控件**（大标题/小标题/纯文本/检查框/拖拽条/下拉框/选项卡/主·次按钮/输入框 + 返回），
/// 用来肉眼检查「显示效果 + 交互效果」。
///
/// 与 `nativew` 命令的区别：`nativew` 是直接开页（不占 ESC 菜单格子）；本类走**正规入口**——
/// 注册 <see cref="NativeMenuBridge"/> 条目（<see cref="NativeMenuEntry.RowPage"/>）→
/// UIKit 的注入器把它当一个 provider 一样注入原生 ESC 列表 → 点它进控件页（和第三方模组的用法完全一致）。
///
/// 交互反馈：任何会“改状态”的操作（勾选框/下拉/选项卡/按钮/输入框回车）都会**重建这一页**，
/// 页首那行 `最近操作` 会写出刚发生的事 —— 一眼能看出回调真的跑了。
/// </summary>
public static class NativeGallery
{
    /// <summary>条目 id（稳定；去重用）。</summary>
    public const string EntryId = "test.uitest.gallery";

    /// <summary>标题（原生 ESC 格子里显示）。</summary>
    public const string Title = "控件 Gallery";

    private static bool _registered;

    // 控件状态（重建页面时保留）
    private static bool _check = true;
    private static float _num = 67f;
    private static int _dd;
    private static int _tab;
    private static string _input = "";
    private static string _last = "(还没操作过)";

    /// <summary>注册进原生 ESC 菜单（幂等）。测试模组加载时自动调一次，也可用 `gallery` 命令重注册。</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            NativeMenuBridge.Add(new NativeMenuEntry
            {
                Id = EntryId,
                Title = Title,
                TitleEn = "Widget Gallery",
                ShowInNative = true,          // 在原生 ESC 菜单里单独占一格（和 provider 一样）
                Order = 90,
                RowPage = BuildRows,
            });
            TestLog.Note("gallery", $"已注册原生控件 Gallery 条目 `{EntryId}`（ESC 菜单里应出现「{Title}」格子）");
        }
        catch (Exception ex)
        {
            _registered = false;
            TestLog.Warn("NativeGallery.Register: " + ex.Message);
        }
    }

    /// <summary>从原生 ESC 菜单里撤掉（`galleryoff` 命令）。</summary>
    public static void Unregister()
    {
        if (!_registered) return;
        _registered = false;
        try { NativeMenuBridge.Remove(EntryId); } catch { }
        TestLog.Info("已撤下原生控件 Gallery 条目");
    }

    public static bool Registered => _registered;

    /// <summary>当前状态一句话（日志用）。</summary>
    public static string State =>
        $"勾选={_check} 滑条={_num:0.#} 下拉={_dd} 页签={_tab} 输入='{_input}' 最近='{_last}'";

    /// <summary>控件页内容（每次开页/重建都重新生成，读的是上面的静态状态）。</summary>
    public static List<NativeRow> BuildRows()
    {
        var rows = new List<NativeRow>
        {
            NativeRow.Title(T("控件 Gallery", "Widget Gallery")),
            NativeRow.SubTitle(T("原生控件（照抄 Settings 子页）", "Native widgets (copied from Settings)")),
            NativeRow.Text(T("最近操作：", "Last action: ") + T2(_last)),

            NativeRow.Check(T("检查框 / Checkbox", "Checkbox"), _check, v => { _check = v; _last = T("勾选框 → ", "checkbox → ") + T(v ? "开" : "off", v ? "on" : "off"); Rebuild(); }),
            NativeRow.SliderRow(T("拖拽条 / Slider", "Slider"), 0f, 100f, _num, " %",
                v => { _num = v; _last = T("滑条 → ", "slider → ") + v.ToString("0.#"); }),   // 拖动中不重建（避免重建打断拖拽）
            NativeRow.Dropdown(T("下拉框 / Dropdown", "Dropdown"), new[] { "English", "简体中文", "日本語" }, _dd,
                i => { _dd = i; _last = T("下拉 → 第 ", "dropdown → item ") + (i + 1); Rebuild(); }),
            NativeRow.Tabs(new[] { T("详情", "Details"), T("设置", "Settings"), T("诊断", "Diagnostics") }, _tab,
                i => { _tab = i; _last = T("页签 → ", "tab → ") + new[] { T("详情", "Details"), T("设置", "Settings"), T("诊断", "Diagnostics") }[Mathf.Clamp(i, 0, 2)]; Rebuild(); }),

            NativeRow.Primary(T("主按钮 / Primary", "Primary"), () => { _last = T("点了主按钮", "primary clicked"); Rebuild(); }),
            NativeRow.Secondary(T("次按钮 / Secondary", "Secondary"), () => { _last = T("点了次按钮", "secondary clicked"); Rebuild(); }),
            NativeRow.InputRow(T("输入框 / InputField", "Input field"), _input,
                s => { _input = s; _last = T("输入框 → '", "input → '") + s + "'"; Rebuild(); }),
            NativeRow.KeybindRow(T("键位 / Keybind", "Keybind"), _key,
                k => { _key = k; _last = T("键位 → ", "keybind → ") + k; Rebuild(); }),
            NativeRow.ScrollerRow(T("画质 / Scroller", "Scroller"), new[] { "Ultra", "High", "Medium", "Low" }, _quality,
                i => { _quality = i; _last = T("选择器 → 第 ", "scroller → item ") + (i + 1); Rebuild(); }),
        };

        // 填充区：内容超过一屏 → 右侧出现**原生滚动条**（用户：“滚动条没有在 Gallery 里”）。
        // 顺便验证“超出屏幕的行也会渲染”与“点击组件不会跳回顶部”。
        rows.Add(NativeRow.SubTitle(T("滚动验证区（超出一屏 → 右侧滚动条）", "Scroll test (overflow → scrollbar)")));
        for (int i = 1; i <= 10; i++)
        {
            rows.Add(NativeRow.Text(T("填充行 ", "filler row ") + i));
        }
        return rows;
    }

    /// <summary>语言键：中文/英文两套（用户：“没加语言键”）。</summary>
    private static string T(string zh, string en) => OpenNestUIKit.Core.UiKitLoc.T(zh, en);

    /// <summary>“最近操作”这种带状态的句子没法逐字翻译 —— 英文下用固定串占位（不至于满屏中文）。</summary>
    private static string T2(string s) => OpenNestUIKit.Core.UiKitLoc.IsChinese ? (s ?? "") : "(see log)";

    private static string _key = "SPACE";
    private static int _quality = 2;

    /// <summary>原地重建这一页（保持页打开状态；只用于离散操作，拖拽中不要调）。</summary>
    private static void Rebuild()
    {
        try
        {
            TestLog.Info("Gallery 交互：" + State);
            if (!NativeMenuPage.IsShown) return;                       // 页已经关了 → 只更新状态
            NativeMenuPage.ReloadRows();
        }
        catch (Exception ex) { TestLog.Warn("Gallery.Rebuild: " + ex.Message); }
    }

    /// <summary>打开控件页（`gallery` 命令用）：自己找 ESC 容器与模板。</summary>
    public static bool Open()
    {
        try
        {
            Register();
            Transform esc = null;
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = "";
                try { nm = t.name ?? ""; } catch { }
                if (string.Equals(nm, "ESC Menu Buttons", StringComparison.Ordinal)) { esc = t; break; }
            }
            if (esc == null) return false;
            var tpl = NativeMenuStyler.FindTemplate(esc, "OpenSettingsBtn", "Settings");
            // ⚠ 必须把 BuildRows 也当**数据源**传进去：交互后 NativeMenuPage 靠它重新取行；
            //    只传一次 List 的话，重建拿到的还是那份旧列表（页首「最近操作」会一直停在初始值）。
            return NativeMenuPage.ShowRows(esc, tpl, "控件 Gallery", BuildRows(), BuildRows);
        }
        catch (Exception ex)
        {
            TestLog.Warn("NativeGallery.Open: " + ex.Message);
            return false;
        }
    }
}


