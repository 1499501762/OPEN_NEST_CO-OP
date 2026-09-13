using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestUIKit.Native;

/// <summary>
/// 原生**次级菜单页**：照抄游戏自己的“切页”做法（`ESC Menu Buttons` 的父层 `Canvas`(300x400) 下
/// 本来就有 `Main Menu Cover` / `ESC Menu Buttons` / `Settings menu` 三个**同级页**，游戏靠切 active 换页）。
///
/// 我们同样在面板下建一个 `OpenNestUIKit_Page` 同级页：
/// - 点击原生 ESC 列表里我们的条目 → **隐藏原生按钮列、显示我们的页**（不是弹自建画布窗口）；
/// - 页内用**原生样式按钮**（照抄 `OpenSettingsBtn` 模板：250x38、间距 38、字号/字体/底图全抄）逐级展开
///   <see cref="NativeMenuBridge"/> 里的条目树 → 即“次级菜单”，每级首行是「‹ 返回」；
/// - 叶子条目再进才打开我们的 UIKit 页面（那类页面是复杂控件，没法用一行按钮表达）；
/// - 剪贴板被游戏关掉时自动复位（下次打开仍是原生列表）。
/// </summary>
public static class NativeMenuPage
{
    public const string PageName = "OpenNestUIKit_Page";

    /// <summary>一页最多几行（面板 400 高：标题 1 行 + 8 行左右刚好）。</summary>
    private const int MaxRows = 8;

    private const float RowW = 250f, RowH = 38f, RowGap = 38f;   // 原生行尺寸/节距（这一页要“看起来就是原生菜单”）
    private const float TopY = 138f;        // 第一行 y（原生第一行按钮在 176，其上还有标题；我们留一行给标题）
    private const float TitleY = 176f;

    private static GameObject _root;
    private static Transform _rows;
    private static TextMeshProUGUI _title;
    private static Button _template;
    private static Transform _esc;                 // 原生按钮列（切页时隐藏/恢复）
    private static Transform _panel;               // 面板 Canvas（300x400）—— 用它判断剪贴板开关
    private static readonly List<GameObject> _made = new();
    private static readonly Stack<string> _stack = new();   // 多级：父条目 Id（空 = 顶层）
    private static bool _escDown;                            // ESC 边沿检测（ESC = 返回上一级/回到原生列表）

    /// <summary>
    /// 热区所有者（注销用；静态类不能自己当 owner，用一个占位对象）。
    /// 为什么行要走**本库自管指针**：剪贴板画布是 **WorldSpace**（`renderMode=WorldSpace`），
    /// 而且我们还会压制外部射线器 —— 把这些行当成普通 UGUI 按钮靠游戏的 EventSystem 收点击，
    /// 实测就是“点 `X Close` / `< Back` 没反应、层级不降”。所以与窗口里的控件一致：
    /// 注册 <see cref="UiPointerRouter"/> 热区，命中和点击都走本库自己的数学。
    /// </summary>
    private static readonly object _zoneOwner = new object();

    /// <summary>测试用：剪贴板没真打开（测试环境按不了 ESC）时，允许我们的页保留显示以便用 `pageprobe` 看行。</summary>
    public static bool DebugKeepAlive;

    /// <summary>我们的页当前是否显示。</summary>
    public static bool IsShown { get { try { return _root != null && _root.activeSelf; } catch { return false; } } }

    /// <summary>当前层级（日志用）：空 = 顶层。</summary>
    public static string CurrentLevel { get { return _stack.Count == 0 ? "<顶层>" : _stack.Peek(); } }

    /// <summary>测试/兜底：自己找第一个 `ESC Menu Buttons` 与其模板并打开我们的页。</summary>
    public static bool ShowFirst(string titleText)
    {
        try
        {
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
            if (esc == null) { CoopLog.Warn("uikit.native", () => "ShowFirst：没找到 ESC Menu Buttons"); return false; }
            var tpl = NativeMenuStyler.FindTemplate(esc, "OpenSettingsBtn", "Settings");
            return Show(esc, tpl, string.IsNullOrEmpty(titleText) ? UiKitInfo.Name : titleText);
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.native", () => "NativeMenuPage.ShowFirst: " + ex.Message);
            return false;
        }
    }

    /// <summary>在原生面板里打开我们的页（隐藏原生按钮列）。<paramref name="esc"/> = `ESC Menu Buttons`（切页对象），
    /// <paramref name="template"/> = 原生按钮模板（抄样式用）。
    /// </summary>
    public static bool Show(Transform esc, Button template, string titleText)
    {
        try
        {
            if (esc == null || esc.parent == null) return false;
            _esc = esc;
            _panel = esc.parent;
            _template = template;

            if (_root == null || _root.transform.parent != _panel)
                Build(titleText);

            if (_root == null) return false;
            _stack.Clear();
            BuildLevel(null);
            SetTitle(titleText);
            SwitchToOurs(true);
            // 我们的页显示期间**挡住游戏自己的 ESC**（否则 ESC 会把剪贴板整个关掉，
            // 而不是像原生 Settings 页那样“先退一级”）——ESC 由 NativeMenuPage.Tick 自己处理。
            // 层级：原生页也算我们占用 ESC 的一层（层级归 0 时守卫会自动放行）。
            try { UiEscapeLevels.Push(UiEscapeLevels.NativePage); } catch { }
            try { UiEscapeGuard.Block(true); } catch { }
            // 行点击走本库自管指针（剪贴板画布是 WorldSpace，而且我们压制了外部射线器）
            try { UiPointerRouter.Activate(); } catch { }
            // ★ 阻挡游戏自己的输入：用户报“点击现在是穿透了的，应该阻挡”
            try { UiInputGuard.SuppressForNativePage(true); } catch { }
            CoopLog.Info("uikit.native", () => $"原生次级菜单页已打开：'{_panel.name}/{PageName}' 条目={NativeMenuBridge.Count}");
            return true;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.native", () => "NativeMenuPage.Show: " + ex.Message);
            return false;
        }
    }

    /// <summary>返回上一级；已在顶层则关闭我们的页、恢复原生列表。</summary>
    /// <param name="fromEsc">true = 这一次关闭是 ESC 自己触发的（要等按键抬起再放行游戏 ESC，
    /// 否则同一次按键会把剪贴板也关掉）；false = 点“X 关闭”/返回钮触发的，**立即放行**。</param>
    public static void Back(bool fromEsc = false)
    {
        try
        {
            if (_stack.Count > 0)
            {
                _stack.Pop();
                BuildLevel(_stack.Count == 0 ? null : _stack.Peek());
                return;
            }
            Close(fromEsc);
        }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "NativeMenuPage.Back: " + ex.Message); }
    }

    /// <summary>关闭我们的页并恢复原生按钮列。</summary>
    public static void Close(bool fromEsc = false)
    {
        try
        {
            SwitchToOurs(false);
            _stack.Clear();
            _widgetRows = null;
            _widgetSource = null;
            _reloadPending = false;
            _scrollOffset = 0f;
            try { if (_title != null) _title.gameObject.SetActive(true); } catch { }   // 控件页可能把标题隐了 → 还原
            // 注销本页热区；自管指针只在本页或我们窗口需要时才开着
            try { UiPointerRouter.RemoveOwner(_zoneOwner); } catch { }
            try { UiPointerRouter.TextFocus = false; } catch { }
            try { if (!Menu.UiMenuWindow.IsOpen) UiPointerRouter.Deactivate(); } catch { }
            try { UiInputGuard.SuppressForNativePage(false); } catch { }   // ★ 还原游戏输入（不再穿透）
            // 弹层级 + 放行游戏 ESC：若这次是 ESC 触发的，等按键抬起再放行（防同一次按键把剪贴板也关了）
            try { UiEscapeLevels.Pop(UiEscapeLevels.NativePage); } catch { }
            if (fromEsc) { try { UiEscapeGuard.ReleaseWhenKeyUp(); } catch { } }
            else { try { UiEscapeGuard.Block(false); } catch { } }   // 点击关闭 → 立即放行（别让 ESC 一直占着）
            CoopLog.Info("uikit.native", () => "原生次级菜单页已关闭（恢复原生列表）");
        }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "NativeMenuPage.Close: " + ex.Message); }
    }

    /// <summary>每帧：剪贴板被游戏关掉时复位；我们的页显示时 ESC = 返回上一级（顶层 = 回原生列表）。</summary>
    public static void Tick()
    {
        try
        {
            if (IsShown)
            {
                try { NativeWidgets.TickInputs(0f); } catch { }        // 控件页：驱动输入框
                DoPendingReload();                                      // 交互后的“下一帧重建”
                bool esc = false;
                try { var kb = UnityEngine.InputSystem.Keyboard.current; esc = kb != null && kb.escapeKey.isPressed; } catch { }
                if (esc && !_escDown) Back(fromEsc: true);
                _escDown = esc;
            }
            else
            {
                _escDown = false;
                // 自校准：本库的页没显示时才去量原生 Settings 页的显示缩放（限流 10 秒），量到了就按新值重排
                try { if (NativeWidgets.PollNativeScale() && _widgetRows != null) ReloadRows(); } catch { }
            }

            if (_root == null) return;
            bool panelAlive = _panel != null && _panel.gameObject != null && _panel.gameObject.activeInHierarchy;
            if (!panelAlive && !DebugKeepAlive)
            {
                if (IsShown || (_esc != null && _esc.gameObject != null && !_esc.gameObject.activeSelf))
                {
                    _root.SetActive(false);
                    if (_esc != null && _esc.gameObject != null) _esc.gameObject.SetActive(true);
                    _stack.Clear();
                    try { UiPointerRouter.RemoveOwner(_zoneOwner); } catch { }
                    try { if (!Menu.UiMenuWindow.IsOpen) UiPointerRouter.Deactivate(); } catch { }
                    try { UiInputGuard.SuppressForNativePage(false); } catch { }   // ★ 剪贴板被游戏关掉 → 同步还原输入
                    try { UiEscapeLevels.Pop(UiEscapeLevels.NativePage); } catch { }
                    try { UiEscapeGuard.ReleaseWhenKeyUp(); } catch { }   // 剪贴板被游戏关了 → 同步放行 ESC
                    CoopLog.Debug("uikit.native", () => "剪贴板已关闭 → 我们的页复位");
                }
            }
        }
        catch { }
    }

    /// <summary>行探针（测试/排查）：当前层级每行的名字/文案/位置/尺寸/字号/底图 + 面板页状态。</summary>
    public static string DumpRows()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            sb.Append("原生页 shown=").Append(IsShown).Append(" 层级='").Append(CurrentLevel).Append("' 栈深=").Append(_stack.Count)
              .Append(" 面板='").Append(_panel != null ? _panel.name : "-").Append("' 按钮列active=").Append(_esc != null && _esc.gameObject != null && _esc.gameObject.activeSelf);
            sb.Append(" 标题='").Append(_title != null ? Trim(_title.text) : "-").Append('\'');
            // ★ 滚动状态（用户：“拖动条和滚动条最大和最小的位置都滚不到” → 得能一眼看出偏移与可用量）
            sb.Append(" 滚动偏移=").Append(_scrollOffset.ToString("0.#")).Append('/').Append(_scrollUsable.ToString("0.#"));
            try
            {
                var crt0 = _widgetContent != null ? _widgetContent.GetComponent<RectTransform>() : null;
                if (crt0 != null) sb.Append(" 内容y=").Append(crt0.anchoredPosition.y.ToString("0.#"))
                                         .Append(" 内容高=").Append(crt0.rect.height.ToString("0.#"))
                                         .Append(" 内容宽=").Append(crt0.rect.width.ToString("0.#"));
                var vrt0 = _widgetView != null ? _widgetView.GetComponent<RectTransform>() : null;
                if (vrt0 != null) sb.Append(" 视口=").Append(vrt0.rect.width.ToString("0.#")).Append('x').Append(vrt0.rect.height.ToString("0.#"));
                // ★ 极值外观取证：(手柄相对轨道/槽的**实际**起止边) —— 用户：“值能到但是外观到不了两个极值”
                sb.Append(GeometryText());
                // ★ 输入拦截状态（用户：“鼠标还是有穿透”）—— 直接看游戏输入模块是启用还是停用
                sb.Append("\n    ").Append(Native.UiInputGuard.ProbeTextCapture());
                // ★ 缺素材清单（某个控件“只剩色块”时先看这里）+ 检查框的勾
                sb.Append("\n    缺素材=").Append(NativeWidgets.MissingReport);
                var chk = FindByName(_widgetContent != null ? _widgetContent : _rows, "Checkmark", 5) as RectTransform;
                if (chk != null)
                {
                    var ci = chk.GetComponent<Image>();
                    sb.Append("\n    勾=").Append(ci != null ? (ci.sprite != null ? ci.sprite.name : "<无图>") : "<无>")
                      .Append(" enabled=").Append(ci != null && ci.enabled)
                      .Append(" 色=(").Append(ci != null ? $"{ci.color.r:0.##},{ci.color.g:0.##},{ci.color.b:0.##}" : "-").Append(')')
                      .Append(" 尺寸=").Append($"{chk.rect.width:0.#}x{chk.rect.height:0.#}");
                    try
                    {
                        var rc2 = ci.canvasRenderer.GetColor();
                        sb.Append(" 渲=(").Append($"{rc2.r:0.##},{rc2.g:0.##},{rc2.b:0.##}").Append(')');
                    }
                    catch { }
                }
                else sb.Append("\n    勾=<没找到 Checkmark 节点>");
            }
            catch { }
            if (_rows != null)
            {
                sb.Append("\n  行数=").Append(_rows.childCount);
                for (int i = 0; i < _rows.childCount; i++)
                {
                    var ch = _rows.GetChild(i);
                    if (ch == null) continue;
                    var rt = ch.GetComponent<RectTransform>();
                    string txt = "", sprite = "-"; float fs = 0f; float ppu = 0f;
                    try
                    {
                        var t = ch.GetComponentInChildren<TMP_Text>(true);
                        if (t != null) { txt = t.text ?? ""; fs = t.fontSize; }
                    }
                    catch { }
                    try
                    {
                        var img = ch.GetComponentInChildren<Image>(true);
                        if (img != null && img.sprite != null) { sprite = img.sprite.name; ppu = img.pixelsPerUnitMultiplier; }
                    }
                    catch { }
                    sb.Append("\n    [").Append(i).Append("] '").Append(ch.name).Append("' 文案='").Append(txt)
                      .Append("' y=").Append(rt != null ? rt.anchoredPosition.y.ToString("0.#") : "?")
                      .Append(" 尺寸=").Append(rt != null ? $"{rt.sizeDelta.x:0}x{rt.sizeDelta.y:0}" : "?")
                      .Append(" 字号=").Append(fs.ToString("0.##"))
                      .Append(" 底图=").Append(sprite).Append('@').Append(ppu.ToString("0.##"));

                    // 内容层：把真正的控件行也列出来（否则永远看不到“越界的行到底建没建”——`pagedump` 只看直接子节点）
                    if (ch.childCount > 0)
                    {
                        for (int j = 0; j < ch.childCount && j < 4; j++)
                        {
                            var lv2 = ch.GetChild(j);
                            if (lv2 == null) continue;
                            var r2 = lv2.GetComponent<RectTransform>();
                            sb.Append("\n      └'").Append(lv2.name).Append("' 尺寸=").Append(r2 != null ? $"{r2.sizeDelta.x:0}x{r2.sizeDelta.y:0}" : "?")
                              .Append(" y=").Append(r2 != null ? r2.anchoredPosition.y.ToString("0.#") : "?")
                              .Append(" 局部缩放=").Append(r2 != null ? r2.localScale.x.ToString("0.###") : "?")
                              .Append(" 子=").Append(lv2.childCount);
                            if (lv2.name != null && lv2.name.IndexOf("header", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                // 固定标题条（不滚动）：把里面的标题节点也打出来（含 Brief ⇒ 字号/顶点/图）
                                for (int k = 0; k < lv2.childCount && k < 3; k++)
                                {
                                    var hc = lv2.GetChild(k);
                                    sb.Append("\n        ★固定条 '").Append(hc.name).Append("' ").Append(Brief(hc));
                                }
                            }
                            if (lv2.name != null
                                && (lv2.name.IndexOf("content", StringComparison.OrdinalIgnoreCase) >= 0
                                    || lv2.name.IndexOf("view", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                sb.Append("：");
                                int max = Mathf.Min(lv2.childCount, 24);
                                for (int k = 0; k < max; k++)
                                {
                                    var rc = lv2.GetChild(k);
                                    var rrt = rc != null ? rc.GetComponent<RectTransform>() : null;
                                    sb.Append(k > 0 ? " | " : "").Append(rc.name).Append('@')
                                      .Append(rrt != null ? rrt.anchoredPosition.y.ToString("0.#") : "?")
                                      .Append('/').Append(rrt != null ? rrt.rect.height.ToString("0.#") : "?").Append("高")
                                      .Append(' ').Append(Brief(rc));
                                }
                                // ★ 再下钻一层：内容层的子物体**就是控件行**（每行带 `Brief` ⇒ 一眼看到
                                //   滑条的手柄 `Bg` 到底多大、字体有没有顶点、标题是否已被提到固定条里）
                                var cnt = _widgetContent;
                                if (cnt != null && cnt.childCount > 0)
                                {
                                    sb.Append("\n      ★行（内容层 ").Append(cnt.childCount).Append(" 个）：");
                                    int rmax = Mathf.Min(cnt.childCount, 14);
                                    for (int k = 0; k < rmax; k++)
                                    {
                                        var rw = cnt.GetChild(k);
                                        var rwrt = rw != null ? rw.GetComponent<RectTransform>() : null;
                                        sb.Append("\n        · '").Append(rw.name).Append("' y=")
                                          .Append(rwrt != null ? rwrt.anchoredPosition.y.ToString("0.#") : "?")
                                          .Append(" 高=").Append(rwrt != null ? rwrt.rect.height.ToString("0.#") : "?")
                                          .Append(' ').Append(Brief(rw));
                                    }
                                    if (cnt.childCount > rmax) sb.Append("\n        …共 ").Append(cnt.childCount).Append(" 行");
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) { sb.Append("DumpRows: ").Append(ex.Message); }
        return sb.ToString();
    }

    private static string Trim(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length > 24 ? s.Substring(0, 24) + "…" : s;
    }

    private static string FontName(TMP_Text t)
    {
        try { return t.font != null ? (t.font.name ?? "?") : "无"; } catch { return "?"; }
    }

    /// <summary>把一个 RectTransform 的四角换算到 <paramref name="space"/> 的**局部单位**并输出 x/y 范围。
    /// 用途：用户反复报“滚动条/拖拽条的外观到不了极值” —— 必须能直接看到**手柄边沿 vs 轨道边沿**的差值，
    /// 而不是只看“值到没到”。所有页面内几何都是同一套本地单位，所以能直接比大小。</summary>
    private static string Edges(RectTransform rt, Transform space)
    {
        try
        {
            if (rt == null || space == null) return "?";
            var a = space.InverseTransformPoint(rt.TransformPoint(new Vector3(rt.rect.xMin, rt.rect.yMin, 0f)));
            var b = space.InverseTransformPoint(rt.TransformPoint(new Vector3(rt.rect.xMax, rt.rect.yMax, 0f)));
            return $"{rt.name} x[{Mathf.Min(a.x, b.x):0.#}..{Mathf.Max(a.x, b.x):0.#}]"
                 + $" y[{Mathf.Min(a.y, b.y):0.#}..{Mathf.Max(a.y, b.y):0.#}]";
        }
        catch { return "?"; }
    }

    /// <summary>按名字递归找子节点（**不用** `Transform.Find`：先前实测它对刚建的节点会返回 null）。</summary>
    private static Transform FindByName(Transform root, string name, int maxDepth)
    {
        try
        {
            if (root == null) return null;
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c == null) continue;
                if (string.Equals(c.name, name, StringComparison.Ordinal)) return c;
                if (maxDepth > 0)
                {
                    var r = FindByName(c, name, maxDepth - 1);
                    if (r != null) return r;
                }
            }
        }
        catch { }
        return null;
    }

    private static Transform FindByPrefix(Transform root, string prefix)
    {
        try
        {
            if (root == null) return null;
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c == null) continue;
                if (c.name != null && c.name.StartsWith(prefix, StringComparison.Ordinal)) return c;
            }
        }
        catch { }
        return null;
    }

    /// <summary>极值外观取证：拖拽条（轨道/Fill/手柄盒/圆块）与滚动条（槽/滑动区/手柄）的边沿。</summary>
    private static string GeometryText()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            if (_widgetPage == null) return "（没有页面块）";
            var row = FindByPrefix(_widgetContent, "nw_slider_");
            if (row != null)
            {
                var slider = row.GetComponentInChildren<Slider>(true);
                sb.Append("\n    拖拽条 值=").Append(slider != null ? slider.value.ToString("0.##") : "?");
                // ⚠ 优先用 `Slider` 自己的 `fillRect/handleRect` 引用（名字查找在重建帧可能拿到已销毁对象 → 打出 "?"）
                var fillRt = slider != null ? slider.fillRect : null;
                var handleRt = slider != null ? slider.handleRect : null;
                if (fillRt == null) fillRt = FindByName(row, "Fill", 3) as RectTransform;
                if (handleRt == null) handleRt = FindByName(row, "Handle", 3) as RectTransform;
                var knobRt = handleRt != null ? FindByName(handleRt, "Bg", 2) as RectTransform : null;
                sb.Append(" 轨道[").Append(Edges(FindByName(row, "Background", 3) as RectTransform, _widgetPage)).Append(']');
                sb.Append(" 填充[").Append(Edges(fillRt, _widgetPage)).Append(']');
                sb.Append(" 手柄盒[").Append(Edges(handleRt, _widgetPage)).Append(']');
                sb.Append(" 圆块[").Append(Edges(knobRt, _widgetPage)).Append(']');
                // 圆角够不够：把圆块的素材/type/ppuMul/border 一并打出来（`Simple` 下圆角是按整图比例缩的）
                try
                {
                    var kimg = knobRt != null ? knobRt.GetComponent<Image>() : null;
                    if (kimg == null && FindByName(row, "Bg", 4) != null) kimg = FindByName(row, "Bg", 4).GetComponent<Image>();
                    if (kimg != null)
                    {
                        var b = kimg.sprite != null ? kimg.sprite.border : Vector4.zero;
                        sb.Append(" 圆块素材=").Append(kimg.sprite != null ? kimg.sprite.name : "<无>")
                          .Append('/').Append(kimg.type)
                          .Append(" 色=(").Append($"{kimg.color.r:0.##},{kimg.color.g:0.##},{kimg.color.b:0.##},{kimg.color.a:0.##}").Append(')');
                        try { sb.Append(" 渲=(").Append($"{kimg.canvasRenderer.GetColor().r:0.##},{kimg.canvasRenderer.GetColor().g:0.##},{kimg.canvasRenderer.GetColor().b:0.##}").Append(')'); } catch { }
                    }
                    // 滚动条手柄的真色（用户：“滚动条的也一样”—— 原生是棕色 tint）
                    var hImg = _scrollBar != null && _scrollBar.Handle != null ? _scrollBar.Handle.GetComponent<Image>() : null;
                    if (hImg != null)
                    {
                        sb.Append(" 滚动条手柄 色=(").Append($"{hImg.color.r:0.##},{hImg.color.g:0.##},{hImg.color.b:0.##}").Append(')');
                        try { sb.Append(" 渲=(").Append($"{hImg.canvasRenderer.GetColor().r:0.##},{hImg.canvasRenderer.GetColor().g:0.##},{hImg.canvasRenderer.GetColor().b:0.##}").Append(')'); } catch { }
                    }
                }
                catch { }
            }
            if (_scrollBar != null && _scrollBar.Root != null)
            {
                var root = _scrollBar.Root.transform;
                sb.Append("\n    滚动条 比例=").Append(_scrollUsable > 0.5f ? (_scrollOffset / _scrollUsable).ToString("0.##") : "-");
                sb.Append(" 槽[").Append(Edges(root as RectTransform, _widgetPage)).Append(']');
                sb.Append(" 滑动区[").Append(Edges(FindByName(root, "Sliding Area", 2) as RectTransform, _widgetPage)).Append(']');
                sb.Append(" 手柄[").Append(Edges(_scrollBar.Handle, _widgetPage)).Append(']');
            }
        }
        catch (Exception ex) { sb.Append("GeometryText: ").Append(ex.Message); }
        return sb.ToString();
    }

    /// <summary>文本内容（英文/中文）—— 只给探针用。</summary>

    private static string TextRect(TMP_Text t)
    {
        try
        {
            var rt = t.rectTransform;
            return rt != null ? $"{rt.rect.width:0.#}x{rt.rect.height:0.#}" : "?";
        }
        catch { return "?"; }
    }

    private static bool ActiveOf(TMP_Text t)
    {
        try { return t.gameObject != null && t.gameObject.activeInHierarchy; } catch { return false; }
    }

    /// <summary>一行的关键视觉摘要（第一张非阴影底图的 sprite/色/**渲染色** + 第一颗 TMP 的字号/色/字体）。
    /// 为什么要看**渲染色**：原生按钮的 `Image.color` 是白的，真颜色在 `CanvasRenderer` 上（见 NativeWidgets.Rc）。</summary>
    private static string Brief(Transform row)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var imgs = row.GetComponentsInChildren<Image>(true);
            for (int i = 0; imgs != null && i < imgs.Length; i++)
            {
                var im = imgs[i];
                if (im == null) continue;
                string n = ""; try { n = im.gameObject.name ?? ""; } catch { }
                if (n.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string sp = "<无>"; try { if (im.sprite != null) sp = im.sprite.name; } catch { }
                var c = Color.clear; try { c = im.color; } catch { }
                var rc = Color.clear; try { rc = im.canvasRenderer.GetColor(); } catch { }
                sb.Append("底[").Append(n).Append(']').Append(sp)
                  .Append(" 色=(").Append($"{c.r:0.##},{c.g:0.##},{c.b:0.##},{c.a:0.##}").Append(')')
                  .Append(" 渲=(").Append($"{rc.r:0.##},{rc.g:0.##},{rc.b:0.##},{rc.a:0.##}").Append(')');
                try { sb.Append(" type=").Append(im.type).Append(" ppuMul=").Append(im.pixelsPerUnitMultiplier.ToString("0.##")); } catch { }
                sb.Append(' ');
                // 尺寸核对（拖拽条：轨道 290x15 / 方块 30x30；“方块被拉成竖条”这类问题只看这里就能发现）
                try { sb.Append($"尺寸={im.rectTransform.rect.width:0.#}x{im.rectTransform.rect.height:0.#} "); } catch { }
                try
                {
                    int shown2 = 0;
                    for (int k = 0; k < imgs.Length && shown2 < 4; k++)
                    {
                        var im2 = imgs[k];
                        if (im2 == null || ReferenceEquals(im2, im)) continue;
                        string n2 = ""; try { n2 = im2.gameObject.name ?? ""; } catch { }
                        shown2++;
                        sb.Append(n2).Append('=')
                          .Append($"{im2.rectTransform.rect.width:0.#}x{im2.rectTransform.rect.height:0.#} ");
                    }
                }
                catch { }
                break;
            }
            var ts = row.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; ts != null && i < ts.Length; i++)
            {
                var t = ts[i];
                if (t == null) continue;
                // ★ 字符数/顶点数：顶点 0 = 屏幕上什么都不会有（“字没了”必须看这个 ——
                //   字号大过文字矩形高时，Ellipsis+不换行 会让 TMP 把整行丢掉）
                int chars = 0, verts = 0;
                try { chars = t.textInfo != null ? t.textInfo.characterCount : 0; } catch { }
                try { verts = t.mesh != null ? t.mesh.vertexCount : 0; } catch { }
                // ★ 再补两项（诊断“行里没字”到底是被遮罩 cull 了、还是根本没建网格）：
                //   `cull` = CanvasRenderer 被 RectMask2D 判为“裁剪到看不见”；`强制` = ForceMeshUpdate 后的字符/顶点。
                bool cull = false; int fchars = 0, fverts = 0;
                try { cull = t.canvasRenderer != null && t.canvasRenderer.cull; } catch { }
                try
                {
                    t.ForceMeshUpdate();
                    fchars = t.textInfo != null ? t.textInfo.characterCount : 0;
                    fverts = t.mesh != null ? t.mesh.vertexCount : 0;
                }
                catch { }
                sb.Append(i == 0 ? "字=" : "值=").Append(t.fontSize.ToString("0.##"))
                  .Append(" 色=(").Append($"{t.color.r:0.##},{t.color.g:0.##},{t.color.b:0.##},{t.color.a:0.##}").Append(')')
                  .Append(" 字符=").Append(chars).Append(" 顶点=").Append(verts)
                  .Append(" cull=").Append(cull)
                  .Append(" 强制=").Append(fchars).Append('/').Append(fverts)
                  // ★ 字体/矩形/激活：TMP 字符=0 的三种真因（没字体 / 文字矩形为 0 / 节点没激活）一眼区分
                  .Append(" 字体=").Append(FontName(t))
                  .Append(" rect=").Append(TextRect(t))
                  .Append(" act=").Append(ActiveOf(t))
                  .Append(" 文='").Append(Trim(t.text ?? "")).Append('\'');
                if (i == 0) continue;
                break;
            }
        }
        catch { }
        return sb.ToString();
    }

    // ---------------- 内部 ----------------

    private static void Build(string titleText)
    {
        _root = new GameObject(PageName);
        _root.transform.SetParent(_panel, false);
        var rt = _root.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;      // 铺满面板（300x400），坐标原点=面板中心（与原生按钮列一致）

        var titleGo = new GameObject("title");
        titleGo.transform.SetParent(_root.transform, false);
        var trt = titleGo.AddComponent<RectTransform>();
        trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 0.5f);
        trt.pivot = new Vector2(0.5f, 0.5f);
        trt.sizeDelta = new Vector2(260f, 22f);
        trt.anchoredPosition = new Vector2(0f, TitleY);
        _title = titleGo.AddComponent<TextMeshProUGUI>();
        NativeMenuStyler.CopyTextVisual(_template, _title);      // 原生字体/字号/颜色（这一页是“原生页”）
        try { _title.alignment = TextAlignmentOptions.Center; } catch { }
        try { _title.text = titleText ?? ""; } catch { }

        var rowsGo = new GameObject("rows");
        rowsGo.transform.SetParent(_root.transform, false);
        var rrt = rowsGo.AddComponent<RectTransform>();
        rrt.anchorMin = Vector2.zero;
        rrt.anchorMax = Vector2.one;
        rrt.offsetMin = Vector2.zero;
        rrt.offsetMax = Vector2.zero;
        _rows = rowsGo.transform;

        _root.SetActive(false);
    }

    private static void SetTitle(string s)
    {
        try { if (_title != null) _title.text = s ?? ""; } catch { }
    }

    /// <summary>按层级建行：<paramref name="parentId"/> 为 null = 顶层；每级首行是「‹ 返回」（顶层则为「✕ 关闭」）。</summary>
    private static void BuildLevel(string parentId)
    {
        ClearRows();
        if (_rows == null) return;

        // 控件页：这一层是用 NativeWidgets 声明的一组原生控件（大标题/小标题/拖拽条/…）
        if (_widgetRows != null)
        {
            BuildWidgetLevel();
            return;
        }

        var items = parentId == null ? NativeMenuBridge.TopLevel() : NativeMenuBridge.ChildrenOf(parentId);

        int row = 0;
        // 返回/关闭行
        string backLabel = parentId == null
            ? UiKitLoc.T("X 关闭", "X Close")            // 用 ASCII：`✕`/`‹`/`›` 在游戏字体里会渲染成方块
            : UiKitLoc.T("< 返回", "< Back");
        var back = NativeMenuStyler.CreateNativeButton(_rows, "OpenNestUIKit_back", backLabel, _template,
            () => Back(), RowW, RowH);
        PlaceRow(back, row++);

        for (int i = 0; i < items.Length; i++)
        {
            var e = items[i];
            if (e == null) continue;                             // 原生页就是“展开所有条目”的地方，不再按 ShowInNative 过滤
            string label = UiKitLoc.T(e.Title, string.IsNullOrEmpty(e.TitleEn) ? e.Title : e.TitleEn);
            var kids = NativeMenuBridge.ChildrenOf(e.Id);
            bool hasKids = kids != null && kids.Length > 0;
            string caption = hasKids ? label + "  >" : label;

            var entry = e;
            var btn = NativeMenuStyler.CreateNativeButton(_rows, "OpenNestUIKit_row_" + Sanitize(e.Id), caption, _template,
                () => OnRowClicked(entry, hasKids), RowW, RowH);
            PlaceRow(btn, row++);
            if (row >= MaxRows) break;                           // 面板就 400 高，超了的条目下一页显示
        }

        CoopLog.Info("uikit.native", () => $"原生页层级='{CurrentLevel}'：行数={row}（含返回行）");
    }

    // ==================================================================
    //  控件页（NativeRow 列表）：大标题 / 小标题 / 拖拽条 / 检查框 / 下拉框 / 选项卡 / 按钮 / 输入框
    // ==================================================================

    /// <summary>当前显示的控件行（非 null = 控件页模式）。</summary>
    private static List<NativeRow> _widgetRows;

    /// <summary>控件行的数据源（有它才能在交互后原地重建；见 <see cref="ReloadRows"/>）。</summary>
    private static Func<List<NativeRow>> _widgetSource;

    // ---- 控件页滚动（内容超过一屏时）----
    private static Transform _widgetView;                     // 裁剪视口（固定；滚轮热区挂在它上面）
    private static Transform _widgetContent;                  // 被平移的内容层（行挂在它下面）
    private static NativeWidgets.NativeScrollbar _scrollBar;  // 右侧滚动条
    private static float _scrollOffset;                       // 当前偏移（**正 = 内容上移 = 看下面的内容**）
    private static float _scrollUsable;                       // 可滚动距离 = 内容高 - 视口高
    private static float _scrollBaseY;
    private static float _scrollVel;                          // 弹性回位用的速度（Mathf.SmoothDamp）
    private static Transform _widgetPage;                     // 页面块（几何探针用：所有本地单位都以它为参考）

    /// <summary>越界（Bounce）的衰减系数：超出边界的部分只按这个比例反映到偏移上，形成“橡皮筋”。
    /// 原生页是 `ScrollRect.movementType = Elastic`（用户：“原生有的页有 Bounce 效果，可以拖拽/滚动 超出页面的位置然后弹性弹回来”）。</summary>
    private const float OverscrollFactor = 0.4f;
    /// <summary>越界上限（相对视口高）：防止一次性狂滚（99 格）把内容飞到屏幕外。</summary>
    private const float MaxOverscrollRatio = 0.22f;
    /// <summary>弹性回位时间（秒，越大越慢越“弹”）。</summary>
    private const float BounceSmooth = 0.12f;
    private static bool _draggingContent;                      // 正按住空区拖内容（拖拽中不回弹）
    private static float _dragLastLocalY;

    /// <summary>把**原始**偏移（可越界）压成“橡皮筋”偏移：越界部分按 <see cref="OverscrollFactor"/> 衰减，且有上限。</summary>
    private static float SoftLimit(float raw)
    {
        if (_scrollUsable <= 0.5f) return 0f;
        float cap = NativeWidgets.PageContentH * MaxOverscrollRatio;
        if (raw < 0f) return Mathf.Max(-cap, raw * OverscrollFactor);
        if (raw > _scrollUsable) return Mathf.Min(_scrollUsable + cap, _scrollUsable + (raw - _scrollUsable) * OverscrollFactor);
        return raw;
    }

    /// <summary>松手/滚完后把越界偏移**弹性**拉回边界（由 <c>UiKitBehaviour.Update</c> 每帧驱动）。</summary>
    public static void TickScroll(float dt)
    {
        try
        {
            if (!IsShown || _widgetContent == null || _scrollUsable <= 0.5f) return;
            if (_draggingContent) return;                // 拖拽中不回弹（像 ScrollRect：松手才回）
            float target = Mathf.Clamp(_scrollOffset, 0f, _scrollUsable);
            if (Mathf.Abs(_scrollOffset - target) <= 0.05f)
            {
                if (_scrollOffset != target) { _scrollOffset = target; ApplyScroll(); }
                _scrollVel = 0f;
                return;
            }
            _scrollOffset = Mathf.SmoothDamp(_scrollOffset, target, ref _scrollVel, BounceSmooth, float.PositiveInfinity, dt);
            ApplyScroll();
        }
        catch { }
    }

    /// <summary>把滚动偏移应用到内容，并同步滚动条（0 = 顶部）。
    /// ⚠ 内容层的 pivot 是 (0.5,1)（顶对齐视口顶边）⇒ **看下面的内容 = 内容整体上移 = `anchoredPosition.y` 变大（正）**。
    ///   2026-09-13 用户：“滚动条滚动方向和实际页面滚动方向反了（实际页面滚动方向反了）” ——
    ///   旧代码写成 `_scrollBaseY - _scrollOffset`（偏移越大内容越**下**移）⇒ 往下滚时内容反而往下跑，
    ///   而且顶部露出一块空白（用户把它认成“应该放标题的那块”）。现改为 **加**。
    /// </summary>
    private static void ApplyScroll()
    {
        try
        {
            if (_widgetContent == null) return;
            // ⚠ 这里**不再 clamp**：越界（Bounce）时偏移可以超出 [0,usable]，由 <see cref="TickScroll"/> 弹性拉回。
            //   滚动条的**手柄**仍然只用 clamp 后的比例（手柄不允许越出槽）。
            var rt = _widgetContent.GetComponent<RectTransform>();
            if (rt != null) rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, _scrollBaseY + _scrollOffset);
            if (_scrollBar != null && _scrollUsable > 0.5f) _scrollBar.SetValue(Mathf.Clamp01(_scrollOffset / _scrollUsable));
        }
        catch { }
    }

    /// <summary>控件页最多几行（超出的部分靠**滚动**看；内容超过视口时右侧自动挂原生滚动条）。</summary>
    private const int MaxWidgetRows = 32;

    /// <summary>控件页行间距（**原生本地单位**，随内容层一起缩小）。</summary>
    private const float RowGapLocal = 4f;

    /// <summary>
    /// 在原生面板里打开一个**控件页**：把 <paramref name="rows"/> 里的原生控件（照抄游戏 Settings 页那套）
    /// 从上往下铺开。返回值 = 是否成功。
    ///
    /// 为什么不用我们自己的窗口：用户要求“用在注入的原生菜单点击分出的子页的 UI 上” ——
    /// 这一页跟原生 Settings 页**同一个面板、同一套控件**，看起来就是游戏自己的页。
    /// </summary>
    public static bool ShowRows(Transform esc, Button template, string titleText, List<NativeRow> rows, Func<List<NativeRow>> source = null)
    {
        try
        {
            NativeWidgets.Capture();                    // 尽量先把原生控件素材拄下来
            _widgetRows = rows ?? new List<NativeRow>();
            _widgetSource = source;
            _scrollOffset = 0f;                          // 新开一页 → 从顶部开始（交互重建**不**归零）
            _scrollVel = 0f;
            bool ok = Show(esc, template, titleText);
            if (!ok) { _widgetRows = null; _widgetSource = null; }
            return ok;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.native", () => "NativeMenuPage.ShowRows: " + ex.Message);
            _widgetRows = null;
            _widgetSource = null;
            return false;
        }
    }

    /// <summary>
    /// 请求**下一帧**重建当前控件页（交互后刷新显示用）。
    ///
    /// ⚠ 为什么不当场重建：回调是在 <see cref="UiPointerRouter"/> 处理某个热区的点击过程中调的，
    ///   当场 `Destroy` 掉热区自己的 RectTransform/GameObject → 路由器还在引用刚销毁的对象，之后可能点击错乱。
    ///   延迟一帧重建（在 <see cref="Tick"/> 里做）最稳。拖拽中不要调（会打断拖拽）。
    /// </summary>
    public static void ReloadRows() => _reloadPending = true;

    private static bool _reloadPending;

    private static void DoPendingReload()
    {
        if (!_reloadPending) return;
        _reloadPending = false;
        try
        {
            if (_widgetRows == null || !IsShown) return;
            if (_widgetSource != null)
            {
                var r = _widgetSource();
                if (r != null && r.Count > 0) _widgetRows = r;
            }
            BuildLevel(null);
        }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "NativeMenuPage.DoPendingReload: " + ex.Message); }
    }

    /// <summary>
    /// 建控件页（自上而下逐行；行高由控件类型决定）。
    ///
    /// ⚠ 2026-09-13 尺寸对齐（用户：“各个控件尺寸和游戏原生尺寸完全不一样”）：`pagespec` 实测原生
    ///   `Settings` 页本地是 **871.7×1012.5**，而它所在的剪贴板画布只有 **300×400** —— 游戏把整页**缩小**塞进去的
    ///   （实测缩放 **0.3591**）。所以原生的 “40 高行 / 32 号字 / ppuMul 2.5” 都是本地数值，屏上只有 ~0.36 倍。
    ///   ⇒ 我们把控件按**原生本地数值**建，整体挂在一个 `localScale = NativeWidgets.DisplayScale` 的 `nw_content` 上，
    ///     宽度/字号/行高/9-slice 圆角就与原生**完全一致**（且不再需要滚动条：12 行只有 ~216 面板单位高）。
    ///
    /// 滚动（内容仍可能超屏时）：`nw_view`（带 `RectMask2D` 裁剪、固定不动）+ `nw_content`（平移 = 滚动）。
    /// </summary>
    private static void BuildWidgetLevel()
    {
        NativeWidgets.ClearInputs();
        NativeWidgets.EscTemplate = _template;
        float S = NativeWidgets.DisplayScale;      // 显示缩放（默认推导值，原生页显示过之后会自动变成实测值）

        // ① 先算**滚动内容**总高（**原生本地单位**）——大标题不参与滚动（见 ②b），所以不计入内容高
        float contentH = 0f;
        int count = 0;
        NativeRow titleRow = null;
        for (int i = 0; i < _widgetRows.Count && count < MaxWidgetRows; i++)
        {
            var row = _widgetRows[i];
            if (row == null) continue;
            // 第一条大标题提到**固定标题条**（不滚动）；若一页有多条，后面的仍当普通行
            if (row.Kind == NativeRowKind.Title && titleRow == null) { titleRow = row; count++; continue; }
            contentH += NativeWidgets.HeightOf(row.Kind) + RowGapLocal;
            count++;
        }
        if (contentH > 0f) contentH -= RowGapLocal;
        bool hasTitle = titleRow != null;

        bool scroll = contentH > NativeWidgets.PageContentH + 0.5f;

        // ② **页面块**（照抄原生 `Settings` 节点：871.7×1012.5 本地、显示缩放 0.3591、位置 (-0.9,-13.1)）
        //    ⚠ 2026-09-13 用户：“块尺寸也不对，上下都缺了一块，右侧也缺了一块或者没有居中（可能是 Canvas 的尺寸问题）”
        //      —— 以前我按“面板 300 宽”自己估了个视口（743×288 本地），而原生整块是 **871.7×1012.5 本地**
        //      （屏上 313×363.6），内容区 `ContentCtn` 是 **871.7×844.2、顶边在块顶下方 72**。
        //      现在整块按原生数值建，块内所有东西都是原生本地单位（行宽 742.6 居中 ⇒ 两侧各 64.55 的留白，与原生一致）。
        float contentPx = contentH * S;                 // 仅用于日志
        var pageGo = new GameObject("nw_page");
        pageGo.transform.SetParent(_rows, false);
        var prt = pageGo.AddComponent<RectTransform>();
        prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
        prt.pivot = new Vector2(0.5f, 0.5f);
        prt.sizeDelta = new Vector2(NativeWidgets.PageW, NativeWidgets.PageH);
        prt.anchoredPosition = new Vector2(NativeWidgets.PageX, NativeWidgets.PageY);
        try { pageGo.transform.localScale = new Vector3(S, S, 1f); } catch { }
        _made.Add(pageGo);

        Transform host = pageGo.transform;
        float contentTopLocal = contentH * 0.5f;       // 内容层中心为原点 → 上沿 = +contentH/2

        // ②b **固定标题条**（原生 `TabsCtn`：871.7×72、锚 0,1~1,1、Image `enabled=false`）——
        //   原生大标题 `Title Settings` 是 `Settings` 的**直接子物体**（不在 `ContentCtn/Scroll View` 里）⇒ **不参与滚动**。
        //   用户：“顶部还是空出来一块，这一块应该是实际原生的标题块，因为不参与滚动”。
        if (titleRow != null)
        {
            var headGo = new GameObject("nw_header");
            headGo.transform.SetParent(pageGo.transform, false);
            var hrt = headGo.AddComponent<RectTransform>();
            hrt.anchorMin = new Vector2(0f, 1f);
            hrt.anchorMax = new Vector2(1f, 1f);
            hrt.pivot = new Vector2(0.5f, 1f);
            hrt.sizeDelta = new Vector2(0f, NativeWidgets.PageHeaderH);   // y 轴非 stretch ⇒ 正数 = 实际高度
            hrt.anchoredPosition = Vector2.zero;                          // 顶对齐页块顶边
            _made.Add(headGo);
            // 标题在条内居中（行高 87 > 条高 72，和原生一样会稍微溢出一点）
            NativeWidgets.Build(headGo.transform, titleRow, 0f, _zoneOwner, "nw");
        }
        {
            // 视口 = 原生 `ContentCtn`（871.7×844.2、顶边在块顶下方 72）——**固定不动**，带 RectMask2D
            var viewGo = new GameObject("nw_view");
            viewGo.transform.SetParent(pageGo.transform, false);
            var vrt = viewGo.AddComponent<RectTransform>();
            vrt.anchorMin = new Vector2(0f, 1f);
            vrt.anchorMax = new Vector2(1f, 1f);
            vrt.pivot = new Vector2(0.5f, 1f);
            // ⚠ 这里 y 轴**不是 stretch**（上下锚点都在 1）⇒ `sizeDelta.y` 就是**实际高度**，必须给正数。
            //   （写 -PageContentH 会让 rect 高度变负 → RectMask2D 把整页内容剪没。）
            vrt.sizeDelta = new Vector2(0f, NativeWidgets.PageContentH);
            vrt.anchoredPosition = new Vector2(0f, -NativeWidgets.PageContentTop);
            viewGo.AddComponent<RectMask2D>();
            _made.Add(viewGo);

            // 内容层（行挂在它下面；宽 742.6 居中、顶对齐；滚动只平移它）
            var contentGo = new GameObject("nw_content");
            contentGo.transform.SetParent(viewGo.transform, false);
            var crt = contentGo.AddComponent<RectTransform>();
            crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.sizeDelta = new Vector2(NativeWidgets.RowW, contentH);
            crt.anchoredPosition = Vector2.zero;

            host = contentGo.transform;
            _widgetContent = contentGo.transform;  // ★ 滚动平移的是**它**（不是 mask 那层）
            _widgetView = viewGo.transform;        // ★ 滚轮/滚动条用它
            _widgetPage = pageGo.transform;        // ★ 几何探针用它当坐标系（页面块本地单位）
            _scrollBaseY = 0f;
        }

        // ③ 建行（先把“滚轮热区”登记好 —— 它铺满整个视口，若建在行之后就会盖住所有行，
        //    导致“除了拖拽条/滚动条，其它控件点不动”；本库命中是“后登记在上层”，所以它必须在最底层）
        if (scroll)
        {
            try
            {
                var viewRt = _widgetView != null ? _widgetView.GetComponent<RectTransform>() : null;
                if (viewRt == null) CoopLog.Warn("uikit.native", () => "滚轮热区：视口引用为空（滚轮会不可用）");
                UiPointerRouter.Add(new UiHotZone
                {
                    Name = "nw:scrollarea",
                    Rect = viewRt,
                    Owner = _zoneOwner,
                    OnScroll = notches =>
                    {
                        // 滚轮向上（notches>0）= 看上面的内容 → 内容下移。
                        // 单位：内容层在缩放后的块里，偏移量是**原生本地单位**（一格 ≈ 30 面板单位 = 84 本地）
                        // Bounce：允许越界（超出部分按 SoftLimit 衰减 + 有上限），停下后由 TickScroll 弹性拉回。
                        _scrollOffset = SoftLimit(_scrollOffset - notches * 84f);
                        _scrollVel = 0f;
                        ApplyScroll();
                    },
                    // ★ 按住空区拖动 = 拖内容滚动（原生 `ScrollRect` 的拖拽手感）：同样允许越界 + 松手弹回。
                    //   行自带热区且在它上层，所以按住“行以外”的空白才算拖内容（不会抢走行的点击）。
                    OnPressLocal = local => { _draggingContent = true; _dragLastLocalY = local.y; return true; },
                    OnDrag = local =>
                    {
                        if (!_draggingContent) return;
                        float dy = local.y - _dragLastLocalY;      // 指针上移(+y) = 看下面的内容 → 内容上移
                        _dragLastLocalY = local.y;
                        _scrollOffset = SoftLimit(_scrollOffset + dy);
                        _scrollVel = 0f;
                        ApplyScroll();
                    },
                    OnDragEnd = _ => { _draggingContent = false; },
                });
            }
            catch { }
        }

        float y = contentTopLocal;
        int n = 0;
        for (int i = 0; i < _widgetRows.Count && n < MaxWidgetRows; i++)
        {
            var row = _widgetRows[i];
            if (row == null) continue;
            if (ReferenceEquals(row, titleRow)) continue;      // 大标题已提到固定标题条（不滚动）
            float h = NativeWidgets.HeightOf(row.Kind);
            y -= h * 0.5f;
            var go = NativeWidgets.Build(host, row, y, _zoneOwner, "nw");
            if (go == null) continue;
            _made.Add(go);
            y -= h * 0.5f + RowGapLocal;           // 行间距（本地单位）
            n++;
        }

        // ④ 滚动条（内容装不下才挂；原生 `Scrollbar Vertical` 就在内容区右侧：宽 20、上下内缩 8.5）
        // ⚠ 2026-09-13 用户：“滚动过的页面在点击组件的时候点完滚动条会跳回到初始位置” ——
        //   根因：点击控件 → `ReloadRows()` 重建整页 → 这里把 `_scrollOffset` 归零了。
        //   现在**不在重建时归零**（只在开页/切层时归零，见 ShowRows/OnRowClicked），重建后只做 clamp。
        // ⚠ 单位改动：现在整块在缩放后的 `nw_page` 里，一切用**原生本地单位**（不再需要 uiScale）。
        _scrollBar = null;
        _scrollUsable = Mathf.Max(0f, contentH - NativeWidgets.PageContentH);
        _scrollOffset = SoftLimit(_scrollOffset);      // 重建后只软限（Bounce 中不硬钳）
        _scrollVel = 0f;
        if (scroll)
        {
            float usable = _scrollUsable;
            var sbGo = new GameObject("nw_scroll");
            sbGo.transform.SetParent(_widgetView != null ? _widgetView : _rows, false);
            var sbr = sbGo.AddComponent<RectTransform>();
            sbr.anchorMin = new Vector2(1f, 0f);
            sbr.anchorMax = new Vector2(1f, 1f);
            sbr.pivot = new Vector2(1f, 0.5f);
            sbr.sizeDelta = new Vector2(20f, -17f);
            sbr.anchoredPosition = Vector2.zero;
            _made.Add(sbGo);
            _scrollBar = NativeWidgets.BuildScrollbarInto(sbGo, sbr, usable / Mathf.Max(1f, NativeWidgets.PageContentH), 0f,
                _zoneOwner, "nw", v => { _scrollOffset = usable * Mathf.Clamp01(v); ApplyScroll(); });
            ApplyScroll();
        }

        // ⑤ 底部返回/关闭（固定不滚；几何照原生底部 `Save changes Button`：面板 y=-171.6、约 214×31）
        string backLabel = UiKitLoc.T("X 关闭", "X Close");
        const float backY = NativeWidgets.PageButtonY;
        var back = NativeMenuStyler.CreateNativeButton(_rows, "OpenNestUIKit_back", backLabel, _template,
            () => Back(), NativeWidgets.PageButtonW, NativeWidgets.PageButtonH);
        if (back != null)
        {
            var brt = back.GetComponent<RectTransform>();
            if (brt != null)
            {
                brt.anchoredPosition = new Vector2(0f, backY);
                // 返回钮也要能被自管指针点到（原生页里所有可点的东西都走热区）
                AddZone("native:back", brt, () => { try { back.onClick.Invoke(); } catch { } });
            }
        }

        // 控件页自己带大标题时，把页面标题（上面那行）隐掉，免得两个标题
        try { if (_title != null) _title.gameObject.SetActive(!hasTitle); } catch { }

        CoopLog.Info("uikit.native", () => $"原生控件页：控件={n}（含标题={hasTitle}）内容高={contentH:0}（本地）"
            + $" 内容区={NativeWidgets.PageContentW:0}x{NativeWidgets.PageContentH:0} 块={NativeWidgets.PageW:0}x{NativeWidgets.PageH:0}"
            + $" 视口实际={ViewRectText()} 缩放={S:0.####} 滚动={scroll}"
            + $" 素材={(NativeWidgets.Captured ? "已抄" : "未抄（退化纯色）")}");
    }

    /// <summary>视口（`nw_view`）的**实际 rect 尺寸**（诊断用：负 sizeDelta 会让 rect 高度变负 → 内容被剪没）。</summary>
    private static string ViewRectText()
    {
        try
        {
            var rt = _widgetView != null ? _widgetView.GetComponent<RectTransform>() : null;
            if (rt == null) return "-";
            return $"{rt.rect.width:0}x{rt.rect.height:0}";
        }
        catch { return "?"; }
    }

    private static void OnRowClicked(NativeMenuEntry e, bool hasKids)
    {
        try
        {
            if (hasKids)
            {
                _stack.Push(e.Id);
                BuildLevel(e.Id);
                return;
            }
            // 叶子：① 声明了控件页（原生控件铺开）→ 进控件页；② 有回调 → 调回调；③ 有页面 id → 开我们的窗口页
            if (e.RowPage != null)
            {
                var rows = e.RowPage();
                if (rows != null && rows.Count > 0)
                {
                    _widgetRows = rows;
                    _widgetSource = e.RowPage;
                    _scrollOffset = 0f;                  // 进新控件页 → 回顶部
                    BuildLevel(null);
                    return;
                }
            }
            if (e.OnClick != null) { e.OnClick(); return; }
            if (!string.IsNullOrEmpty(e.PageId))
            {
                Close();                       // 先把原生页复位，避免盖着我们的画布窗口
                Menu.UiMenuWindow.Open(e.PageId);
            }
        }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => $"原生页行点击失败 '{e.Id}': {ex.Message}"); }
    }

    /// <summary>给任意矩形挂一个点击热区（自管指针；控件页的返回钮等用）。</summary>
    private static void AddZone(string name, RectTransform rect, Action onClick)
    {
        try
        {
            var z = UiPointerRouter.Add(new UiHotZone { Name = name, Rect = rect, Owner = _zoneOwner, OnClick = onClick });
            _ = z;
        }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "NativeMenuPage.AddZone: " + ex.Message); }
    }

    private static void PlaceRow(Button b, int index)
    {
        if (b == null) return;
        try
        {
            var rt = b.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(RowW, RowH);
            rt.anchoredPosition = new Vector2(0f, TopY - index * RowGap);

            // 注册为本库热区：名字唯一且**不带空格**（测试脚本里 `_` 会变空格），点击/悬停都走自管指针
            var btn = b;
            string zoneName = "native:" + (btn.name != null ? btn.name.Replace("OpenNestUIKit_", "") : "row" + index);
            var zone = UiPointerRouter.Add(new UiHotZone
            {
                Name = zoneName,
                Rect = rt,
                Owner = _zoneOwner,
                OnClick = () => { try { btn.onClick.Invoke(); } catch (Exception ex) { CoopLog.Warn("uikit.native", () => "native row click: " + ex.Message); } },
            });
            _ = zone;
        }
        catch { }
    }

    private static void ClearRows()
    {
        try { UiPointerRouter.RemoveOwner(_zoneOwner); } catch { }   // 旧行的热区一并注销（矩形会失效）
        try { NativeWidgets.ClearInputs(); } catch { }
        for (int i = 0; i < _made.Count; i++)
            try { if (_made[i] != null) UnityEngine.Object.Destroy(_made[i]); } catch { }
        _made.Clear();
        if (_rows == null) return;
        for (int i = _rows.childCount - 1; i >= 0; i--)
            try { UnityEngine.Object.Destroy(_rows.GetChild(i).gameObject); } catch { }
    }

    /// <summary>切页：ours=true 显示我们的页 + 隐藏原生按钮列；false 反之（照抄原生 Settings 页的切法）。</summary>
    private static void SwitchToOurs(bool ours)
    {
        try { if (_root != null) _root.SetActive(ours); } catch { }
        try { if (_esc != null && _esc.gameObject != null) _esc.gameObject.SetActive(!ours); } catch { }
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "x";
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (!char.IsLetterOrDigit(chars[i])) chars[i] = '_';
        return new string(chars);
    }
}
