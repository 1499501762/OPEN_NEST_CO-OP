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

/// <summary>原生页里一行/一个控件的类型（= 游戏自己 Settings 页里出现的那几种）。</summary>
public enum NativeRowKind
{
    /// <summary>大标题（游戏：`Title Settings`）。</summary>
    Title,
    /// <summary>小标题 / 分组标题（游戏：`HeadlineUGUI` → 大字 + 下划线）。</summary>
    SubTitle,
    /// <summary>纯文本说明行。</summary>
    Text,
    /// <summary>普通行按钮（原生 ESC 列表那种纸面行）。</summary>
    Button,
    /// <summary>主按钮（游戏：`SGButtonPrimaryUGUI`，APPLY 那种）。</summary>
    PrimaryButton,
    /// <summary>次要按钮（游戏：`ButtonSecondaryUGUI`，RESET ALL / CLOSE 那种）。</summary>
    SecondaryButton,
    /// <summary>检查框（游戏：`ToggleConsoleUGUI` → 20x20 方框 + `SGCheckMark`）。</summary>
    Checkbox,
    /// <summary>拖拽条（游戏：`SliderConsoleUGUI` → `SGRounded` 轨道 + 黑色填充 + 手柄 + 右侧数值）。</summary>
    Slider,
    /// <summary>下拉框（游戏：`DropdownUGUIWithLabel` → 深色圆角框 + 标题 + `SGDownArrow`）。</summary>
    Dropdown,
    /// <summary>选项卡（游戏：`TabsCtn` + `TabButtonUGUI`，选中项高亮变色）。</summary>
    Tabs,
    /// <summary>输入框行的类型（原生 `TextfieldConsoleUGUI`；驱动是我们自己的）。</summary>
    Input,
    /// <summary>键位行（原生 `InputBindingConsoleUGUI`；点一下进入监听态，下一个按键就绑上）。</summary>
    Keybind,
    /// <summary>文本滚动选择器（原生 `OptionsButtonConsoleUGUI`：左侧标签 + 右侧深色值框 **◄ 值 ►**）。</summary>
    TextScroller,
}

/// <summary>
/// 原生页的一行（声明式）：由 <see cref="NativeWidgets"/> 渲染成原生观感的控件。
/// 回调按类型取用（`OnClick` / `OnBool` / `OnNum` / `OnIndex` / `OnText`）。
/// </summary>
public sealed class NativeRow
{
    public NativeRowKind Kind = NativeRowKind.Button;

    /// <summary>标题 / 标签 / 按钮文案 / 输入框标签。</summary>
    public string Label = "";

    /// <summary>输入框当前文本。</summary>
    public string Value = "";

    public float Min = 0f, Max = 100f, Num = 0f;

    public bool Bool;

    /// <summary>下拉/选项卡的选项。</summary>
    public string[] Options;

    public int Index;

    /// <summary>滑条数值显示后缀（如 " %"）。</summary>
    public string Suffix = "";

    public Action OnClick;
    public Action<bool> OnBool;
    public Action<float> OnNum;
    public Action<int> OnIndex;
    public Action<string> OnText;

    public static NativeRow Title(string text) => new NativeRow { Kind = NativeRowKind.Title, Label = text };
    public static NativeRow SubTitle(string text) => new NativeRow { Kind = NativeRowKind.SubTitle, Label = text };
    public static NativeRow Text(string text) => new NativeRow { Kind = NativeRowKind.Text, Label = text };

    public static NativeRow Button(string label, Action onClick)
        => new NativeRow { Kind = NativeRowKind.Button, Label = label, OnClick = onClick };

    public static NativeRow Primary(string label, Action onClick)
        => new NativeRow { Kind = NativeRowKind.PrimaryButton, Label = label, OnClick = onClick };

    public static NativeRow Secondary(string label, Action onClick)
        => new NativeRow { Kind = NativeRowKind.SecondaryButton, Label = label, OnClick = onClick };

    public static NativeRow Check(string label, bool value, Action<bool> onBool)
        => new NativeRow { Kind = NativeRowKind.Checkbox, Label = label, Bool = value, OnBool = onBool };

    public static NativeRow SliderRow(string label, float min, float max, float value, string suffix, Action<float> onNum)
        => new NativeRow { Kind = NativeRowKind.Slider, Label = label, Min = min, Max = max, Num = value, Suffix = suffix, OnNum = onNum };

    public static NativeRow Dropdown(string label, string[] options, int index, Action<int> onIndex)
        => new NativeRow { Kind = NativeRowKind.Dropdown, Label = label, Options = options, Index = index, OnIndex = onIndex };

    public static NativeRow Tabs(string[] options, int index, Action<int> onIndex)
        => new NativeRow { Kind = NativeRowKind.Tabs, Options = options, Index = index, OnIndex = onIndex };

    public static NativeRow InputRow(string label, string value, Action<string> onText)
        => new NativeRow { Kind = NativeRowKind.Input, Label = label, Value = value, OnText = onText };

    /// <summary>键位行（点一下之后按一个键就绑上；回调在 <see cref="OnText"/>）。</summary>
    public static NativeRow KeybindRow(string label, string key, Action<string> onBind)
        => new NativeRow { Kind = NativeRowKind.Keybind, Label = label, Value = key, OnText = onBind };

    /// <summary>文本滚动选择器（◄ 值 ►；点左右箭头切上一项/下一项）。</summary>
    public static NativeRow ScrollerRow(string label, string[] options, int index, Action<int> onIndex)
        => new NativeRow { Kind = NativeRowKind.TextScroller, Label = label, Options = options, Index = index, OnIndex = onIndex };
}

/// <summary>
/// 原生控件工厂：把游戏 Settings 子页里的那几种控件（大标题/小标题/拖拽条/检查框/下拉框/选项卡/主·次按钮/输入框）
/// **照抄外观**地复刻到我们自己的原生页里。
///
/// 素材来源：<see cref="Capture"/> 扫一遍原生 Settings 页，把用到的 sprite / 字体存下来（按名字索引），
/// 之后即使那页被关掉也能建控件。实测（`pagedump`）抄到的清单：
/// · 标题：`Title Settings`（TMP，CourierPrime，黑 0.9）
/// · 小标题：`HeadlineUGUI/*/TextTf`（大字）+ `RawImage` 下划线（742x4）
/// · 选项卡：`TabsCtn`(`SUGGradientRounded_Top` 深色) + `TabButtonUGUI`（选中项 `Active` 黑底 + 强调色文字）
/// · 检查框：`ToggleConsoleUGUI` → 20x20 `SUGGradientRounded`(黑) + `Checkmark`(`SGCheckMark` 白)
/// · 拖拽条：`Slider` → `Background`(`SGRounded` 黑 0.47) + `Fill`(黑 0.86) + `Handle` + 右侧 `ValueTf`
/// · 下拉框：`DropdownUGUIWithLabel` → `Bg`(`SUGGradientRounded` 黑 0.902) + `ButtonLabel` + `Arrow`(`SGDownArrow`)
/// · 输入框：`InputField (TMP)` → `SUGGradientRounded` 黑 0.902 + `Text Area/Text`
/// · 按钮：`SGButtonPrimaryUGUI`(`UI Box Castile` + `SUGShadowLite`) / `ButtonSecondaryUGUI`(`UI box partial star`)
///
/// ⚠ 尺寸说明：原生 Settings 页的“整宽行”是 742 单位，而我们注入的原生页在**小面板**里（整宽行 250 单位），
/// 两边的画布比例不同 ⇒ **不能直接搬数值**。这里按“同样的相对观感”取尺寸（行宽 250、行高 30~38），
/// 字号按可用宽度自适应（<see cref="FitFont"/>），避免中文/长词被挤爆。
/// </summary>
public static class NativeWidgets
{
    // ---------------- 抄下来的素材 ----------------

    private static readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
    private static TMP_FontAsset _font;
    private static bool _captured;
    private static float _lastScanTime;

    /// <summary>原生 ESC 行的模板按钮（普通行按钮用它抄样式；由 <see cref="NativeMenuPage"/> 在开页时设）。</summary>
    public static Button EscTemplate;

    /// <summary>是否已抄到原生素材（没抄到也能建控件，只是底图退化成纯色块）。</summary>
    public static bool Captured => _captured;

    /// <summary>抄到的素材名清单（诊断）。</summary>
    public static string SpriteNames
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _sprites) { if (sb.Length > 0) sb.Append(','); sb.Append(kv.Key); }
            return sb.Length == 0 ? "-" : sb.ToString();
        }
    }

    /// <summary>控件要用到的素材名（诊断用）。</summary>
    private static readonly string[] Required = new[]
    {
        "SUGGradientRounded_Top", "SUGGradientRounded", "SUGGradientRounded_Bottom",
        "SGRounded", "SGRounded_Bottom", "SGCheckMark", "SGDownArrow",
        "SUGShadowLite", "UI Box Castile", "UI box partial star",
    };

    /// <summary>字体名（null = 没找到，字会不显示）。</summary>
    public static string FontName => _font != null ? (_font.name ?? "?") : "null";

    /// <summary>缺失素材清单（诊断：“空样式”时看这个）。</summary>
    public static string MissingReport
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Required.Length; i++)
                if (Sprite(Required[i]) == null) { if (sb.Length > 0) sb.Append(','); sb.Append(Required[i]); }
            return sb.Length == 0 ? "（无）" : sb.ToString();
        }
    }

    public static Sprite Sprite(string name)
    {        Sprite s = null;
        if (_sprites.TryGetValue(name, out s) && s != null) return s;

        // ⚠ 2026-09-13 用户实测：“应该是设置菜单没加载，超过来的直接空样式了”
        //   原来只在开页那一刻扫**原生 Settings 页**；那页是玩家打开设置时才建的 → 没打开过就一个素材都抄不到：
        //   字体=null（字全部不显示）+ 底图全没（退化成纯色块，阴影那层还是白的 → 一大块白）。
        //   现在做成**懒加载 + 三级兜底**：① 重试 Settings 页；② 全场景扫一遍同名 sprite；③ 我们的素材库 UiSkin。
        try
        {
            if (!_captured) Capture();
            if (_sprites.TryGetValue(name, out s) && s != null) return s;

            GlobalScan();                        // 内部有限流（10s）；不会每查一个名字就扫一遍
            if (_sprites.TryGetValue(name, out s) && s != null) return s;

            var u = Theme.UiSkin.NativeGet(name);          // UIKit 自己捕获/烘焙过的原生切片
            if (u != null) { _sprites[name] = u; return u; }
        }
        catch { }
        return null;
    }

    /// <summary>全场景扫一遍 Image 的 sprite（按名字收，先到先得）——素材是**共享资源**，
    /// 不止 Settings 页在用（ESC 菜单/剪贴板上也有同一批），所以这样能兜住“设置页还没打开”的情况。
    ///
    /// ⚠ 必须**严格限流**：`FindObjectsOfType` 在大场景里一次就要 7~21ms（本项目在拦截层那边就为它专门优化过）。
    /// 开页会连续查十几个素材名，若不限流就是十几次全场景扫描 → 卡顿（用户报“重新聚焦后巨卡”的嫌疑之一）。</summary>
    private static float _nextScanAt;

    private static void GlobalScan(bool force = false)
    {
        float now = Time.realtimeSinceStartup;
        if (!force && now < _nextScanAt) return;
        _nextScanAt = now + 10f;                 // 10 秒内不再扫
        _lastScanTime = now;
        try
        {
            var imgs = UnityEngine.Object.FindObjectsOfType<Image>(true);
            int added = 0;
            for (int i = 0; imgs != null && i < imgs.Length; i++)
            {
                var im = imgs[i];
                if (im == null || im.sprite == null) continue;
                string sn = "";
                try { sn = im.sprite.name ?? ""; } catch { }
                if (sn.Length == 0 || _sprites.ContainsKey(sn)) continue;
                _sprites[sn] = im.sprite;
                added++;
            }
            if (added > 0)
                CoopLog.Info("uikit.native", () => $"控件事才懒扫描：新增 {added} 个 sprite（共 {_sprites.Count}）");
        }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "NativeWidgets.GlobalScan: " + ex.Message); }
    }

    /// <summary>
    /// 扫一遍原生 Settings 页，抄下素材与字体（幂等，采到就返回）。
    /// 找不到那页（不在菜单场景 / 页没建）时返回 false，调用方应稍后再试。
    /// </summary>
    public static bool Capture(string pageName = "Settings menu")
    {
        if (_captured) return true;
        try
        {
            Transform root = null;
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = ""; try { nm = t.name ?? ""; } catch { }
                if (!string.Equals(nm, pageName, StringComparison.Ordinal)) continue;
                root = t; break;
            }
            if (root == null) return false;

            // ① 素材：整页扫一遍，按 sprite 名收（同名只留第一个）
            var imgs = root.GetComponentsInChildren<Image>(true);
            for (int i = 0; imgs != null && i < imgs.Length; i++)
            {
                var im = imgs[i];
                if (im == null || im.sprite == null) continue;
                string sn = ""; try { sn = im.sprite.name ?? ""; } catch { }
                if (sn.Length == 0 || _sprites.ContainsKey(sn)) continue;
                _sprites[sn] = im.sprite;
            }
            // ② 字体：优先标题那颗（CourierPrime），退而求其次随便一颗 TMP 的字体
            try
            {
                var texts = root.GetComponentsInChildren<TMP_Text>(true);
                for (int i = 0; texts != null && i < texts.Length; i++)
                {
                    var t = texts[i];
                    if (t == null || t.font == null) continue;
                    string tn = ""; try { tn = t.gameObject.name ?? ""; } catch { }
                    if (_font == null) _font = t.font;
                    if (tn.IndexOf("Title", StringComparison.OrdinalIgnoreCase) >= 0) { _font = t.font; break; }
                }
            }
            catch { }

            _captured = _sprites.Count > 0;
            CoopLog.Info("uikit.native", () => $"原生控件素材已抄（{_sprites.Count} 个 sprite，字体={(_font != null ? _font.name : "null")}）：{SpriteNames}");
            return _captured;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.native", () => "NativeWidgets.Capture: " + ex.Message);
            return false;
        }
    }

    // ---------------- 度量（**原生本地数值**，一律照抄游戏自己的 Setting 页）----------------
    //
    // ⚠ 2026-09-13 用户：“各个控件尺寸和游戏原生尺寸完全不一样” —— 我把原因查清楚了（`pagespec` 实测）：
    //   原生 `Settings menu/Settings` 节点本地尺寸是 **871.7×1012.5**，而它所在的剪贴板画布（WorldSpace）
    //   只有 **300×400** —— 游戏是**把整页缩小塞进去**的（实测缩放 **0.3591**：1012.5 × 0.3591 ≈ 364 ≈ 400 − 标题位）。
    //   所以原生的“40 高行 / 32 号字 / ppuMul 2.5”都是**本地数值**，屏上实际只有 ~0.36 倍大：
    //   行 ≈ 267×14.4 单位、字号 ≈ 11.5 单位、9-slice 圆角也只有 ~1/2.8。
    //
    // 因此正确做法不是“把数值缩小”，而是：**控件全部按原生本地数值建，再由调用方给容器加 Scale 缩放**
    //   （见 NativeMenuPage 的 `nw_content.localScale`）。这样宽度、字号、行距、9-slice 倍率、
    //   阴影尺寸全部自动与原生一致 —— 连圆角粗细都对。

    /// <summary>原生行宽（本地单位：游戏 Settings 页的行是 742.6）。</summary>
    public const float RowW = 742.6f;

    /// <summary>
    /// 原生 Settings 页的显示缩放：**实测 0.3591**（`pagespec` + 自校准实测于 2026-09-13）。
    ///
    /// 拿它把本地值缩到面板单位：行 742.6→267、高 40→14.4、字 32→11.5。
    /// 起初我按宽度推导（300/871.7 = 0.3442），实测证明游戏是**按高度适配**：
    /// 1012.5 × 0.3591 ≈ 363.6 ≈ 面板 400 − 标题那一行（~36）。
    /// 反正 <see cref="PollNativeScale"/> 会在原生页显示过之后自动纠正，这里只是个尽量准的初值。
    /// </summary>
    public const float Scale = 0.3591f;

    /// <summary>
    /// **实际采用的显示缩放**：默认 = <see cref="Scale"/>（推导值：整页塞进 300 宽的剪贴板画布）。
    /// 一旦玩家的原生 Settings 页真的显示过（`localScale > 0.05`），<see cref="PollNativeScale"/> 会实测并改成真值。
    /// </summary>
    public static float DisplayScale = Scale;

    private static float _nextScaleProbeAt;

    /// <summary>
    /// 自校准：找**正在显示**的原生 `Settings` 页（那页平时被缩放成 0 = 隐藏），把它的 `localScale` 当作显示缩放。
    /// 返回 true = 缩放变了，调用方应重排控件页。内部限流（10 秒一次），且只在本库自己的页**没显示**时调。
    /// </summary>
    public static bool PollNativeScale()
    {
        float now = Time.realtimeSinceStartup;
        if (now < _nextScaleProbeAt) return false;
        _nextScaleProbeAt = now + 10f;
        try
        {
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = ""; try { nm = t.name ?? ""; } catch { }
                if (!string.Equals(nm, "Settings", StringComparison.Ordinal)) continue;
                var rt = t.GetComponent<RectTransform>();
                if (rt == null) continue;
                float w = 0f, s = 0f;
                try { w = rt.sizeDelta.x; s = rt.localScale.x; } catch { }
                if (w < 500f || s < 0.05f) continue;                  // 认“页”那个大节点，且必须正在显示
                if (Mathf.Abs(s - DisplayScale) > 0.005f)
                {
                    float old = DisplayScale;
                    DisplayScale = s;
                    CoopLog.Info("uikit.native", () => $"实测原生 Settings 页显示缩放：{old:0.####} → {s:0.####}（控件页将按新缩放重排）");
                    return true;
                }
                return false;
            }
        }
        catch { }
        return false;
    }

    /// <summary>原生字号（本地）：行标签 32、按钮字 30、下拉标签 28、输入框 26、选项卡 28。
    /// 大标题特殊：原生 `Title Settings` 是 16.85 × **局部缩放 5.1476** = 86.7 本地（屏上 ≈31）——见 <see cref="FTitle"/>。</summary>
    public const float FLabel = 32f, FButton = 30f, FDropdown = 28f, FInput = 26f, FTab = 30f;

    /// <summary>大标题字号（本地）：原生 = 16.85 × 5.1476 ≈ 86.7（屏上 ≈ 31 单位）。
    /// ⚠ 2026-09-13 用户：“小标题正确但是大标题不够大” —— 我当时取 32，比原生小了一半多。
    /// 原生那颗 TMP 自己带 5.1476 的局部缩放，只看 `fontSize` 会漏掉这一层。</summary>
    public const float FTitle = 86.7f;

    // ---- 页面块几何（照抄原生 `Settings menu/Settings` 那一块；**本地单位**）----
    /// <summary>整页块尺寸（原生 `Settings` = 871.7×1012.5）。</summary>
    public const float PageW = 871.7f, PageH = 1012.5f;
    /// <summary>整页块在面板（画布）里的位置（原生 `Settings.anchoredPosition` = -0.9,-13.1）。</summary>
    public const float PageX = -0.9f, PageY = -13.1f;
    /// <summary>内容区（原生 `ContentCtn` = 871.7×844.2，顶边在块顶下方 72）。</summary>
    public const float PageContentW = 871.7f, PageContentH = 844.2f, PageContentTop = 72f;
    /// <summary>顶部**固定标题条**高度（原生 `TabsCtn` = 871.7×72，锚 0,1~1,1；大标题 `Title Settings` 是
    /// `Settings` 的**直接子物体** ⇒ **不参与滚动**）。用户：“顶部还是空出来一块，这一块应该是实际原生的标题块，因为不参与滚动”。</summary>
    public const float PageHeaderH = 72f;
    /// <summary>底部按钮（原生 `Save changes Button`：面板 y=-171.6，尺寸 277.6×40 本地 × 它自己的 0.7702 缩放）。</summary>
    public const float PageButtonY = -171.6f, PageButtonW = 213.8f, PageButtonH = 30.8f;

    /// <summary>原生行高（本地）：普通行 40、下拉 80（标签+框两行）、选项卡 72、大标题 87。
    /// 大标题行高 = 原生 `Title Settings` 的屏上高度（134.5×17 本地 × 5.1476 = 692×87.5 本地 → 屏上 31.4）。</summary>
    public static float HeightOf(NativeRowKind k)
    {
        switch (k)
        {
            case NativeRowKind.Title: return 87f;
            case NativeRowKind.SubTitle: return 42f;      // 原生 HeadlineUGUI = 438.2 x 41.9
            case NativeRowKind.Text: return 40f;
            case NativeRowKind.Dropdown: return 80f;      // 原生 = 742.6 x 80（上半标签、下半框 45.7 高）
            case NativeRowKind.Tabs: return 72f;          // 原生 TabsCtn = 871.7 x 72
            default: return 40f;                          // 按钮/检查框/拖拽条/输入/键位/选择器
        }
    }

    /// <summary>按可用宽度与字符数自适应字号（中文按 1.0em、ASCII 按 0.62em 估宽）。</summary>
    public static float FitFont(string text, float width, float want, float min = 9f)
    {
        if (string.IsNullOrEmpty(text)) return want;
        float em = 0f;
        for (int i = 0; i < text.Length; i++) em += text[i] > 0x2E80 ? 1f : 0.62f;
        if (em < 0.01f) return want;
        float f = width / em;
        return Mathf.Clamp(Mathf.Min(want, f), min, want);
    }

    // ---------------- 小工具 ----------------

    private static GameObject Node(Transform parent, string name, float w, float h, float x, float y)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x, y);
        return go;
    }

    private static Image AddImg(GameObject go, string spriteName, Color c, bool ray, Image.Type type = Image.Type.Sliced, float ppuMul = 4f,
        bool skipIfMissing = false, Color? fallback = null)
    {
        var sp = Sprite(spriteName);
        if (sp == null && skipIfMissing) return null;          // 装饰层（阴影…）没素材就干脆不画，免得变成白块
        var img = go.AddComponent<Image>();
        if (sp != null)
        {
            img.sprite = sp;
            img.type = type;
            try { img.pixelsPerUnitMultiplier = ppuMul; } catch { }
            img.color = c;
        }
        else
        {
            img.color = fallback ?? c;                          // 没素材：退化成纯色（调用方给合理的底色）
            img.type = Image.Type.Simple;
        }
        img.raycastTarget = ray;
        return img;
    }

    /// <summary>
    /// 程序化圆片（64×64、带 1px 抗锯齿）—— 给拖拽条的**手柄圆块**用。
    ///
    /// 为什么不直接用原生 `SGRounded`（2026-09-13 用户：“拖动条的 Handle 是方的，圆角不够”）：那张贴图是
    /// **256×256 而圆角只有 48px**（≈15%），缩到 30×30 时圆角只剩 ~5.6 本地单位（屏上 ≈2px）= 看着就是方块；
    /// 改成 `Sliced` + 按 `border/ppu` 反算 `pixelsPerUnitMultiplier`（算出来的倍率 31.25）**实测仍是方块**
    /// ⇒ 干脆自己画一个圆（确定性最好，也不依赖素材的导入设置）。凹贴图只建一次并缓存。
    /// </summary>
    private static UnityEngine.Sprite CircleSprite()
    {
        if (_circleSprite != null) return _circleSprite;
        try
        {
            const int N = 64;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var px = new Color32[N * N];
            float r = N * 0.5f - 1f;
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float dx = x + 0.5f - N * 0.5f, dy = y + 0.5f - N * 0.5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(r - d + 0.5f);                 // 边缘 1px 渐变 = 抗锯齿
                px[y * N + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            _circleSprite = UnityEngine.Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            _circleSprite.name = "nw_circle";
        }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "CircleSprite: " + ex.Message); }
        return _circleSprite;
    }

    private static UnityEngine.Sprite _circleSprite;

    /// <summary>
    /// 程序化三角箭头（32×32、带 1px 抗锯齿，**默认朝下**）—— 给折叠分组的展开指示用。
    ///
    /// 为什么不用文字箭头（2026-09-13 实机截图取证）：`\u25be`(U+25BE)/`\u25b8`(U+25B8) 在游戏字体里**没有字形**，
    /// 屏上渲染成一个描边方框（tofu）—— 和“拖拽条手柄是方的”同一类问题：**字体/素材给不了的形状就自己画**。
    /// 用法：`Image` + 旋转（0° = 展开、90° = 收起），不依赖字体也不依赖图集。
    /// </summary>
    public static UnityEngine.Sprite TriangleSprite()
    {
        if (_triangleSprite != null) return _triangleSprite;
        try
        {
            const int N = 32;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var px = new Color32[N * N];
            float wMax = N - 4f;                      // 两侧各留 2px
            float cx = N * 0.5f;
            for (int y = 0; y < N; y++)
            {
                float t = (float)y / (N - 1);         // 0 = 贴图底（顶点）、1 = 贴图顶（底边）
                float half = 0.5f * wMax * t;
                for (int x = 0; x < N; x++)
                {
                    float dx = Mathf.Abs(x + 0.5f - cx);
                    float a = Mathf.Clamp01(half - dx + 0.5f);   // 边缘 1px 渐变 = 抗锯齿
                    px[y * N + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            _triangleSprite = UnityEngine.Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            _triangleSprite.name = "nw_triangle";
        }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "TriangleSprite: " + ex.Message); }
        return _triangleSprite;
    }

    private static UnityEngine.Sprite _triangleSprite;

    /// <summary>手柄圆块：优先用程序化圆片（圆）；万一贴图建不出来就退回原生 `SGRounded`（至少有个块）。</summary>
    private static Image AddCircleKnob(GameObject go)
    {
        var knobColor = new Color(0.545f, 0.565f, 0.604f, 1f);
        var sp = CircleSprite();
        if (sp == null) return AddImg(go, "SGRounded", knobColor, true, Image.Type.Simple, 1f);
        var img = go.AddComponent<Image>();
        img.sprite = sp;
        img.type = Image.Type.Simple;
        img.color = knobColor;
        img.raycastTarget = true;
        return img;
    }

    /// <summary>
    /// 设置**实际渲染色**（`CanvasRenderer`）—— 这是"颜色抄对"的关键一步。
    ///
    /// ⚠ 2026-09-13 用户：“主按钮和次按钮还是白色背景的，应该是和原生一样的颜色的” —— 根因就是这个：
    ///   原生按钮的 `Bg` 是 `UI Box Castile`（纯白 9-slice），`Image.color` 也是白的，但
    ///   **Selectable 的 ColorTint 把黑色(0,0,0,1)设在 `CanvasRenderer` 上**（实测 `渲染色=(0,0,0,1)`），
    ///   所以原生屏上是**黑底**；我们建的是裸 `Image`，没有那层渲染色 ⇒ 一直是白底。
    /// </summary>
    private static void Rc(Image img, Color c)
    {
        try { if (img != null) img.canvasRenderer.SetColor(c); } catch { }
    }

    /// <summary>原生按钮的过渡色（实测 `OpenSettingsBtn` / `SGButtonPrimaryUGUI` 的 Selectable）。</summary>
    private static readonly Color TintNormal = new Color(0f, 0f, 0f, 1f);
    private static readonly Color TintHighlight = new Color(0.453f, 0.453f, 0.453f, 1f);
    private static readonly Color TintPressed = new Color(0f, 0f, 0f, 1f);

    /// <summary>按原生那种**锚点比例**建子节点（原生几乎全是 `锚=0,0~1,1 (+sizeDelta)` 的写法，照抄最省事）。</summary>
    private static GameObject Anch(Transform parent, string name, float axMin, float ayMin, float axMax, float ayMax,
        float dW, float dH, float px = 0f, float py = 0f)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(axMin, ayMin);
        rt.anchorMax = new Vector2(axMax, ayMax);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(dW, dH);
        rt.anchoredPosition = new Vector2(px, py);
        return go;
    }

    private static TextMeshProUGUI AddTxt(GameObject go, string text, float size, Color c, TextAlignmentOptions align, bool ray = false, string fontName = null)
    {
        var t = go.AddComponent<TextMeshProUGUI>();
        var font = string.IsNullOrEmpty(fontName) ? null : Font(fontName);
        if (font == null) font = _font;
        if (font == null) font = PickFallbackFont();            // 字体兜底（否则字全不显示）
        try { if (font != null) t.font = font; } catch { }
        t.text = text ?? "";
        t.fontSize = size;
        t.color = c;
        t.alignment = align;
        t.raycastTarget = ray;
        t.enableAutoSizing = false;
        // ⚠ 2026-09-13 用户：“字没了” —— 根因就是这里：
        //   原生的字都是 **32 号（本地）**，而行的文字矩形只有 20~30 高（行高 40），
        //   一行字高 = 字号×1.2 ≈ 38 > 30。在 `Ellipsis` + 不换行 的模式下 **TMP 会把整行直接丢掉**（无字形），
        //   于是整页“只剩框、没有字”；多行文本也会只留一行。
        //   原生 TMP 的默认是 `Overflow` + 允许换行 → 照它办（文本宽度已由 `FitFont` 控住，不会溢出）。
        try { t.enableWordWrapping = true; t.overflowMode = TextOverflowModes.Overflow; } catch { }
        return t;
    }

    /// <summary>字体按名找（先看已缓存的，再全场景找 TMP_Text 的同名字体）；没找到返回 null。
    /// ⚠ 同样限流：一次全场景 `FindObjectsOfType<TMP_Text>` 也会卡帧，所以最多 10 秒找一次。</summary>
    public static TMP_FontAsset Font(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        TMP_FontAsset f;
        if (_fonts.TryGetValue(name, out f) && f != null) return f;
        float now = Time.realtimeSinceStartup;
        if (now < _nextFontScanAt) return null;
        _nextFontScanAt = now + 10f;
        try
        {
            var texts = UnityEngine.Object.FindObjectsOfType<TMP_Text>(true);
            for (int i = 0; texts != null && i < texts.Length; i++)
            {
                var t = texts[i];
                if (t == null || t.font == null) continue;
                string fn = "";
                try { fn = t.font.name ?? ""; } catch { }
                if (fn.Length == 0 || _fonts.ContainsKey(fn)) continue;
                _fonts[fn] = t.font;
            }
        }
        catch { }
        return _fonts.TryGetValue(name, out f) ? f : null;
    }

    private static readonly Dictionary<string, TMP_FontAsset> _fonts = new Dictionary<string, TMP_FontAsset>(StringComparer.Ordinal);
    private static float _nextFontScanAt;

    /// <summary>
    /// 字体兜底（⚠ 2026-09-13：用户实测“设置菜单没加载 → 整页空样式”，根因之一就是字体为 null → 字全不显示）：
    /// ① 原生 Settings 页的字体；② **原生 ESC 模板按钮上的字体**（我们这一页就在 ESC 菜单里，模板一定在）；
    /// ③ 我们自己的本地化字体（<see cref="UI.UiKit.EnsureFont"/>）。
    /// </summary>
    private static TMP_FontAsset PickFallbackFont()
    {
        try
        {
            if (_font != null) return _font;
            if (EscTemplate != null)
            {
                var t = EscTemplate.GetComponentInChildren<TMP_Text>(true);
                if (t != null && t.font != null) { _font = t.font; return _font; }
            }
            try
            {
                var probe = new GameObject("nw_fontprobe");
                var tmp = probe.AddComponent<TextMeshProUGUI>();
                UI.UiKit.EnsureFont(tmp);
                if (tmp.font != null) _font = tmp.font;
                UnityEngine.Object.Destroy(probe);
            }
            catch { }
        }
        catch { }
        return _font;
    }

    /// <summary>
    /// ⚠ 颜色铁律（证据：`ref/ui_sprites_all` 里这些贴图**是空心的**）——
    ///   `UI Box Castile` 中线 alpha 全 0（只有描边/装饰）、`UI Box partial star` 中心 A=0、
    ///   `SUGGradientRounded(_Top)` 中心 A=41（16% 极淡填充）、只有 `SGRounded` 是实心（A=255）。
    ///   所以原生的“深色框”其实是**深色描边 + 薄底**，框里的字/箭头原生给的就是**黑**（实测：
    ///   下拉 `ButtonLabel`/`Arrow` = (0,0,0,1)、输入框文字 = (0.255,0.255,0.255,1)）。
    ///   用户报“组件内应该是黑色的文本也变白色了”就是这个 —— 别再按“深色底”推白字。
    /// </summary>
    private static readonly Color Ink = new Color(0f, 0f, 0f, 1f);                       // 纸面上的正文字色
    private static readonly Color InkSoft = new Color(0.255f, 0.255f, 0.255f, 1f);      // 原生输入框文字色（实测）
    private static readonly Color InkFaint = new Color(0.2f, 0.2f, 0.2f, 0.5f);         // 占位符
    private static readonly Color Accent = new Color(0.55f, 0.65f, 0.83f, 1f);          // 原生选项卡选中强调色

    /// <summary>
    /// 带**悬停/按下变色**的点击热区（原生每个 `Selectable` 都有高亮态；用户报“下拉框里的选项没有聚焦变色”）。
    ///
    /// ⚠ `bg` 必须是**单独一个 Graphic**：同一个 GameObject 上不能有两颗 Graphic
    ///   （`Graphic` 带 `[DisallowMultipleComponent]`；表现在 IL2CPP 下就是 `AddComponent&lt;Image&gt;()` **返回 null**）。
    /// </summary>
    private static void ZoneTint(string name, RectTransform rect, Image bg, object owner, Action onClick,
                                Color baseC, Color hoverC, Color pressC, Action onEnter = null, Action onExit = null)
    {
        try
        {
            if (rect == null) { CoopLog.Warn("uikit.native", () => "ZoneTint: rect 为空 '" + name + "'"); return; }
            if (bg == null) { CoopLog.Warn("uikit.native", () => "ZoneTint: 高亮图缺失 '" + name + "'（命中仍会注册）"); }
            else { bg.color = baseC; bg.raycastTarget = false; }   // 命中走本库自管指针，不需要 UGUI 射线
            UiPointerRouter.Add(new UiHotZone
            {
                Name = name,
                Rect = rect,
                Owner = owner,
                Bg = bg,
                Tint = true,
                BaseColor = baseC,
                HoverColor = hoverC,
                PressColor = pressC,
                OnClick = onClick,
                OnEnter = onEnter,
                OnExit = onExit,
            });
        }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "NativeWidgets.ZoneTint: " + ex); }
    }

    /// <summary>给一行注册点击热区（名字不带空格，测试脚本用 `tap:<名>` 能点到）。</summary>
    private static void Zone(string name, RectTransform rect, object owner, Action onClick,
                             Func<Vector2, bool> pressLocal = null, Action<Vector2> onDrag = null, Action<Vector2> onDragEnd = null, Action onRelease = null,
                             Action onEnter = null, Action onExit = null)
    {
        try
        {
            var z = UiPointerRouter.Add(new UiHotZone
            {
                Name = name,
                Rect = rect,
                Owner = owner,
                OnClick = onClick,
                OnPressLocal = pressLocal,
                OnDrag = onDrag,
                OnDragEnd = onDragEnd,
                OnRelease = onRelease,
                OnEnter = onEnter,
                OnExit = onExit,
            });
            _ = z;
        }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "NativeWidgets.Zone: " + ex.Message); }
    }

    // ---------------- 各控件 ----------------

    /// <summary>建一行；返回该行的 GameObject（已按 <paramref name="y"/> 放在父节点中）。</summary>
    public static GameObject Build(Transform parent, NativeRow row, float y, object owner, string zonePrefix = "nw")
    {
        if (parent == null || row == null) return null;
        switch (row.Kind)
        {
            case NativeRowKind.Title: return BuildTitle(parent, row, y);
            case NativeRowKind.SubTitle: return BuildSubTitle(parent, row, y);
            case NativeRowKind.Text: return BuildText(parent, row, y);
            case NativeRowKind.Checkbox: return BuildCheckbox(parent, row, y, owner, zonePrefix);
            case NativeRowKind.Slider: return BuildSlider(parent, row, y, owner, zonePrefix);
            case NativeRowKind.Dropdown: return BuildDropdown(parent, row, y, owner, zonePrefix);
            case NativeRowKind.Tabs: return BuildTabs(parent, row, y, owner, zonePrefix);
            case NativeRowKind.Input: return BuildInput(parent, row, y, owner, zonePrefix);
            case NativeRowKind.Keybind: return BuildKeybind(parent, row, y, owner, zonePrefix);
            case NativeRowKind.TextScroller: return BuildTextScroller(parent, row, y, owner, zonePrefix);
            case NativeRowKind.Button: return BuildButton(parent, row, y, owner, zonePrefix, EscTemplate);
            default: return BuildButton(parent, row, y, owner, zonePrefix, null);
        }
    }

    /// <summary>大标题（原生 `Title Settings`：CourierPrime、黑 0.902、居中）。
    /// 字号来源见 <see cref="FTitle"/>（16.85 × 5.1476 = 86.7 本地 ⇒ 屏上 31 —— 比行标签大一半）。</summary>
    public static GameObject BuildTitle(Transform parent, NativeRow row, float y)
    {
        float h = HeightOf(row.Kind);
        var go = Node(parent, "nw_title", RowW, h, 0f, y);
        AddTxt(go, row.Label, FitFont(row.Label, RowW, FTitle), new Color(0f, 0f, 0f, 0.902f), TextAlignmentOptions.Center);
        return go;
    }

    /// <summary>小标题（原生 `HeadlineUGUI` 438.2x41.9：大字 + 底部 4 单位黑线）。</summary>
    public static GameObject BuildSubTitle(Transform parent, NativeRow row, float y)
    {
        float h = HeightOf(row.Kind);
        var go = Node(parent, "nw_subtitle", RowW, h, 0f, y);
        var txtGo = Anch(go.transform, "TextTf", 0f, 0f, 1f, 1f, -8f, -4f, 0f, 2f);
        AddTxt(txtGo, row.Label, FitFont(row.Label, RowW - 10f, FLabel), Color.black, TextAlignmentOptions.Left);
        // 下划线：原生是 `RawImage`（**无 sprite / 无贴图**，纯色 4 单位高）
        // ⚠ 2026-09-13 用户：“标题下面那个线在闪烁（像素不精确）” —— 我原来用了 `SGRounded` + Sliced：
        //   这张图边框 48px，而线只有 4 本地单位高（屏上 ≈1.4）→ Unity 强制压缩九宫格边框，
        //   加上亚像素定位就抖。纯色方块（无 sprite）才是原生的做法。
        var lineGo = Anch(go.transform, "RawImage", 0f, 0f, 1f, 0f, -4f, 4f, 0f, 2f);
        var lineImg = lineGo.AddComponent<Image>();
        lineImg.sprite = null;
        lineImg.color = Color.black;
        lineImg.type = Image.Type.Simple;
        lineImg.raycastTarget = false;
        return go;
    }

    /// <summary>纯文本说明行（原生行标签样式：CourierPrime 32、黑）。</summary>
    public static GameObject BuildText(Transform parent, NativeRow row, float y)
    {
        float h = HeightOf(row.Kind);
        var go = Node(parent, "nw_text", RowW, h, 0f, y);
        var t = Anch(go.transform, "Label", 0f, 0f, 1f, 1f, -20f, -10f, 5f, 0f);
        AddTxt(t, row.Label, FitFont(row.Label, RowW - 20f, FLabel), Color.black, TextAlignmentOptions.Left);
        return go;
    }

    /// <summary>
    /// 按钮行。三种都照抄原生（`SGButtonPrimaryUGUI` / `ButtonSecondaryUGUI` / ESC 行 `OpenSettingsBtn`）：
    /// · 底图：主 `UI Box Castile`、次 `UI box partial star`，都是白图 + **黑渲染色**（原生 ColorTint normal=(0,0,0,1)）；
    /// · 文字：CourierPrime 30、**黑**、`Midline`（原生就是左对齐中途）；文字矩形 = 行内缩 40x20；
    /// · 原生那层 `BgShadow`（`SUGShadowLite`）**组件是 disabled 的** ⇒ 我们也不画
    ///   （⚠ 之前我们画了它 → 一圈白色光晕 = 用户报的“白色背景”）。
    /// </summary>
    public static GameObject BuildButton(Transform parent, NativeRow row, float y, object owner, string zonePrefix, Button escTemplate = null)
    {
        float h = HeightOf(row.Kind);

        if (row.Kind == NativeRowKind.Button && escTemplate != null)
        {
            // 普通行：直接用“注入原生行”的那套（纸面 + 原生字体，与原生列表一致）
            var b = NativeMenuStyler.CreateNativeButton(parent, zonePrefix + "_" + Sanitize(row.Label), row.Label, escTemplate, row.OnClick, RowW, h);
            if (b == null) return null;
            var brt = b.GetComponent<RectTransform>();
            if (brt != null) brt.anchoredPosition = new Vector2(0f, y);
            Zone(zonePrefix + ":" + Sanitize(row.Label), brt, owner, () => { try { b.onClick.Invoke(); } catch { } });
            return b.gameObject;
        }

        bool primary = row.Kind != NativeRowKind.SecondaryButton;
        var go = Node(parent, zonePrefix + "_" + Sanitize(row.Label), RowW, h, 0f, y);

        var bgGo = Anch(go.transform, "Bg", 0f, 0f, 1f, 1f, 0f, 0f, 0f, 0f);
        // 主=`UI Box Castile`(ppuMul 2.5) / 次=`UI box partial star`：色都抄原生的白，真颜色靠**渲染色**黑
        var bg = AddImg(bgGo, primary ? "UI Box Castile" : "UI box partial star", Color.white, true, Image.Type.Sliced, 2.5f);
        Rc(bg, TintNormal);

        var txtGo = Anch(go.transform, "Text (TMP)", 0f, 0f, 1f, 1f, -40f, -20f, 0f, 0f);
        AddTxt(txtGo, row.Label, FitFont(row.Label, RowW - 44f, FButton), Color.black, TextAlignmentOptions.Midline);

        Zone(zonePrefix + ":" + Sanitize(row.Label), go.GetComponent<RectTransform>(), owner, row.OnClick,
            onEnter: () => Rc(bg, TintHighlight), onExit: () => Rc(bg, TintNormal));
        return go;
    }

    /// <summary>检查框（照抄原生 `ToggleConsoleUGUI`：**标签在左（占 95% 宽、黑 32 号字）、20×20 方框贴在右边缘内缩 16**，
    /// 方框 `SUGGradientRounded` 黑 ppuMul 7；原生那层 `ToggleShadow` 80×80 是真在画的，也照抄）。</summary>
    public static GameObject BuildCheckbox(Transform parent, NativeRow row, float y, object owner, string zonePrefix)
    {
        float h = HeightOf(row.Kind);
        var go = Node(parent, "nw_check_" + Sanitize(row.Label), RowW, h, 0f, y);

        // 原生 ToggleShadow：80×80、锚在右边缘、pos(-16, 5.1)
        var shGo = Anch(go.transform, "ToggleShadow", 1f, 0.5f, 1f, 0.5f, 80f, 80f, -16f, 5.1f);
        AddImg(shGo, "SUGShadowLite", Color.white, false, Image.Type.Sliced, 1f, skipIfMissing: true);

        // 标签：锚 0,0~0.95,1（原生）
        var txtGo = Anch(go.transform, "Label", 0f, 0f, 0.95f, 1f, -10f, -10f, 0f, 0f);
        AddTxt(txtGo, row.Label, FitFont(row.Label, RowW * 0.95f - 10f, FLabel), Color.black, TextAlignmentOptions.Left);

        // 方框：原生 `尺寸=20x-20 锚=1,0~1,1` → 20×20（⚠ 若写成 +20 就会变成 20×60 的竖条，用户报“被挤扁”就是这个）
        var boxGo = Anch(go.transform, "Toggle", 1f, 0f, 1f, 1f, 20f, -20f, -16f, 0f);
        AddImg(boxGo, "SUGGradientRounded", new Color(0f, 0f, 0f, 1f), false, Image.Type.Sliced, 7f,
            fallback: new Color(0f, 0f, 0f, 1f));

        // 勾（原样照抄原生 `ToggleConsoleUGUI/Checkmark`，2026-09-13 用户：“CheckBox 里的勾和原生的勾不一样”）：
        //   原生 = `SGCheckMark` / **色=白(1,1,1,1)** / type=Simple ppuMul=1 / **rect=27.9×27.9**（比 20×20 的框还大、
        //   锚 0,0~1,1 + `sizeDelta=(7.9,7.9)`）/ pos=(2.6,3.1) ⇒ 勾是**素材自带的绿色**（`SGCheckMark.png` 实测像素
        //   (140,150,115)/(159,175,126) 就是绿勾+白描边），而且**会溢出方框**。
        //   我们之前写成 `Ink`(黑) + 14×14 居中 ⇒ 绿勾被乘成黑勾、还只有一半大 ✗（这就是“不一样”）。
        var checkGo = Anch(boxGo.transform, "Checkmark", 0f, 0f, 1f, 1f, 7.9f, 7.9f, 2.6f, 3.1f);
        var check = AddImg(checkGo, "SGCheckMark", Color.white, true, Image.Type.Simple, 1f, fallback: Color.white);
        if (check != null) check.enabled = row.Bool;

        Zone(zonePrefix + ":check:" + Sanitize(row.Label), go.GetComponent<RectTransform>(), owner, () =>
        {
            row.Bool = !row.Bool;
            try { if (check != null) check.enabled = row.Bool; } catch { }
            try { row.OnBool?.Invoke(row.Bool); } catch (Exception ex) { CoopLog.Warn("uikit.native", () => "checkbox callback: " + ex.Message); }
        });
        return go;
    }

    /// <summary>
    /// 键位行（照抄原生 `InputBindingConsoleUGUI`）：**左侧动作名 + 右侧圆角框显示当前键名**；
    /// 点一下进入“监听按键”态（原生那态是**蓝底** 0.259,0.443,0.753 + 阴影），下一个按键就绑上去。
    ///
    /// ⚠ 原生这个控件**不是输入框**（没法自由打字），只是“按键显示 + 按下即绑” —— 用户：“还有个 Keybind 漏了
    /// （原生有 Keybind 但是没有输入框）”。
    /// </summary>
    public static GameObject BuildKeybind(Transform parent, NativeRow row, float y, object owner, string zonePrefix)
    {
        float h = HeightOf(row.Kind);
        var go = Node(parent, "nw_keybind_" + Sanitize(row.Label), RowW, h, 0f, y);

        // 左 51%：动作名（原生 `Label` 锚 0,0~0.51,1、32 号黑字）
        var labelGo = Anch(go.transform, "Label", 0f, 0f, 0.51f, 1f, -10f, -10f, 5f, -5f);
        AddTxt(labelGo, row.Label, FitFont(row.Label, RowW * 0.51f - 20f, FLabel), Color.black, TextAlignmentOptions.Left);

        // 右 49%：键位框（原生 `KeyWithTextNormal`：深色 `SUGGradientRounded`，监听态换**蓝**(0.259,0.443,0.753)）
        var boxGo = Anch(go.transform, "KeyWithTextNormal", 0.51f, 0f, 1f, 1f, 0f, 0f, 0f, 0f);
        var boxBg = AddImg(boxGo, "SUGGradientRounded", new Color(0f, 0f, 0f, 0.902f), true, Image.Type.Sliced, 7f,
            fallback: new Color(0f, 0f, 0f, 0.902f));

        var keyGo = Anch(boxGo.transform, "KeyNameTf", 0f, 0f, 1f, 1f, -20f, -14f, 0f, 0f);
        var keyTxt = AddTxt(keyGo, string.IsNullOrEmpty(row.Value) ? "-" : row.Value,
            FitFont(row.Value, RowW * 0.49f - 24f, FLabel), Ink, TextAlignmentOptions.Center);

        var keybind = new KeybindState { Row = row, Box = boxBg, Text = keyTxt, Listening = false };
        _keybinds[go] = keybind;

        Zone(zonePrefix + ":key:" + Sanitize(row.Label), go.GetComponent<RectTransform>(), owner, () =>
        {
            foreach (var kv in _keybinds) if (kv.Value != keybind) SetListening(kv.Value, false);
            SetListening(keybind, true);
            row.Value = "";
            try { keyTxt.text = "按一个键…"; keyTxt.fontSize = FitFont("按一个键…", RowW * 0.49f - 24f, FLabel); } catch { }
        });
        return go;
    }

    private sealed class KeybindState
    {
        public NativeRow Row;
        public Image Box;
        public TextMeshProUGUI Text;
        public bool Listening;
    }

    private static readonly Dictionary<GameObject, KeybindState> _keybinds = new Dictionary<GameObject, KeybindState>();

    private static void SetListening(KeybindState kb, bool on)
    {
        if (kb == null) return;
        kb.Listening = on;
        try
        {
            // ⚠ 这里要改 `Image.color` 而不是渲染色：帧底图本来就是**深色** (0,0,0,0.902)，
            //   渲染色是乘上去的，乘什么都是黑的。
            if (kb.Box != null)
                kb.Box.color = on ? new Color(0.259f, 0.443f, 0.753f, 1f) : new Color(0f, 0f, 0f, 0.902f);   // 监听态=原生那个蓝
        }
        catch { }
    }

    /// <summary>
    /// 文本滚动选择器（照抄原生 `OptionsButtonConsoleUGUI`）：
    /// 左侧标签（0..51%）+ 右侧深色值框（`SUGGradientRounded` 黑）+ 值文字（居中）+ **两个 `SGDownArrow` 箭头**
    /// （左箭头转 +90° / 右箭头转 -90°，原生就是拿向下箭头转的）+ 左右两个透明可点区（原生 `PreviousOnPointerClick` /
    /// `NextOnPointerClick`，各占值框的四分之一）。
    ///
    /// 用户：“Text Scroller 和 TabBar 也没有” —— 这就是 Graphics 页那个 `◄ UltraQuality ►`。
    /// </summary>
    public static GameObject BuildTextScroller(Transform parent, NativeRow row, float y, object owner, string zonePrefix)
    {
        float h = HeightOf(row.Kind);
        var go = Node(parent, "nw_scroller_" + Sanitize(row.Label), RowW, h, 0f, y);

        // 左 51%：标签（原生 `Label` 锚 0,0~0.51,1、32 号黑字）
        var labelGo = Anch(go.transform, "Label", 0f, 0f, 0.51f, 1f, -10f, -10f, 5f, -5f);
        AddTxt(labelGo, row.Label, FitFont(row.Label, RowW * 0.51f - 20f, FLabel), Color.black, TextAlignmentOptions.Left);

        // 原生那层 Shadow（60×60、锚 0.51~1、10 倍大）是真在画的，照抄
        var shGo = Anch(go.transform, "Shadow", 0.51f, 0f, 1f, 1f, 60f, 60f, 0f, 0f);
        AddImg(shGo, "SUGShadowLite", Color.white, false, Image.Type.Sliced, 1f, skipIfMissing: true);

        // 右 49%：value 框（原生 `ValueBg`：`SUGGradientRounded` 黑 ppuMul 5，锚 0.51~1）
        var boxGo = Anch(go.transform, "ValueBg", 0.51f, 0f, 1f, 1f, 0f, 0f, 0f, 0f);
        AddImg(boxGo, "SUGGradientRounded", new Color(0f, 0f, 0f, 1f), true, Image.Type.Sliced, 5f,
            fallback: new Color(0f, 0f, 0f, 1f));
        float boxW = RowW * 0.49f;

        string[] opts = row.Options ?? new string[0];
        string cur = opts.Length > 0 ? opts[Mathf.Clamp(row.Index, 0, opts.Length - 1)] : "";
        var valueGo = Anch(boxGo.transform, "ValueTf", 0f, 0f, 1f, 1f, -40f, -14f, 0f, 0f);
        var valueTxt = AddTxt(valueGo, cur, FitFont(cur, boxW - 44f, FLabel), Ink, TextAlignmentOptions.Center);

        // 箭头：原生拿 `SGDownArrow` 转出来的（17×17、贴值框两侧内缩 17）
        // ⚠ 2026-09-13 用户：“Text Scroller 的左右箭头反了” —— 旋转方向上把事情搞反了：
        //   向下箭头 `(0,-1)` 逆时针转 **+90°** 后指 **右**（（x,y）→(-y,x)）⇒ 右侧/下一项用 +90°，
        //   左侧/上一项用 **-90°**。（之前左右正好写反了。）
        var arrowL = Anch(boxGo.transform, "Arrow Left", 0f, 0.5f, 0f, 0.5f, 17f, 17f, 17f, 0f);
        var imgL = AddImg(arrowL, "SGDownArrow", Ink, false, Image.Type.Simple, 1f, fallback: Ink);
        try { arrowL.transform.localRotation = Quaternion.Euler(0f, 0f, -90f); } catch { }
        var arrowR = Anch(boxGo.transform, "Arrow Right", 1f, 0.5f, 1f, 0.5f, 17f, 17f, -17f, 0f);
        var imgR = AddImg(arrowR, "SGDownArrow", Ink, false, Image.Type.Simple, 1f, fallback: Ink);
        try { arrowR.transform.localRotation = Quaternion.Euler(0f, 0f, 90f); } catch { }
        _ = imgL; _ = imgR;

        Action<int> step = dir =>
        {
            try
            {
                if (opts.Length == 0) return;
                int idx = (Mathf.Clamp(row.Index, 0, opts.Length - 1) + dir + opts.Length) % opts.Length;
                row.Index = idx;
                valueTxt.text = opts[idx];
                valueTxt.fontSize = FitFont(opts[idx], boxW - 44f, FLabel);
                row.OnIndex?.Invoke(idx);
            }
            catch (Exception ex) { CoopLog.Warn("uikit.native", () => "scroller callback: " + ex.Message); }
        };

        // 原生两个透明可点区：`PreviousOnPointerClick` 锚 0.51~0.76、`NextOnPointerClick` 锚 0.76~1（= 值框左右各一半）
        var prevGo = Anch(boxGo.transform, "PreviousOnPointerClick", 0f, 0f, 0.5f, 1f, 0f, 0f, 0f, 0f);
        var prevImg = prevGo.AddComponent<Image>();
        prevImg.color = new Color(1f, 1f, 1f, 0f);
        prevImg.raycastTarget = true;
        Zone(zonePrefix + ":scprev:" + Sanitize(row.Label), prevGo.GetComponent<RectTransform>(), owner, () => step(-1));

        var nextGo = Anch(boxGo.transform, "NextOnPointerClick", 0.5f, 0f, 1f, 1f, 0f, 0f, 0f, 0f);
        var nextImg = nextGo.AddComponent<Image>();
        nextImg.color = new Color(1f, 1f, 1f, 0f);
        nextImg.raycastTarget = true;
        Zone(zonePrefix + ":scnext:" + Sanitize(row.Label), nextGo.GetComponent<RectTransform>(), owner, () => step(1));

        return go;
    }

    /// <summary>拖拽条（严格照抄游戏 `SliderConsoleUGUI`）：
    /// 轨道 `SGRounded` 黑 0.471 **高 15** + 填充 `SGRounded` 黑 0.863（左右各缩 10）、
    /// 手柄 = `Handle`（20×30 定位盒，无图）+ `Bg`（**30×30 `SGRounded` 灰 `Simple`，看起来是个圆**）+ `Shadow`（`SUGShadowLite`）；
    /// 右侧 `ValueTf` 数值。</summary>
    public static GameObject BuildSlider(Transform parent, NativeRow row, float y, object owner, string zonePrefix)
    {
        float h = HeightOf(row.Kind);
        var go = Node(parent, "nw_slider_" + Sanitize(row.Label), RowW, h, 0f, y);

        // 原生布局（实测 `SliderConsoleUGUI`）：**左 51% 是标签**（32 号黑字，pos(5,-5)），
        // 右 49% 里 Slider 本体再内缩 71.1（那条缝正好放数值），轨道高 15、手柄 30×30。
        var labelGo = Anch(go.transform, "Label", 0f, 0f, 0.51f, 1f, -10f, -10f, 5f, -5f);
        AddTxt(labelGo, row.Label, FitFont(row.Label, RowW * 0.51f - 20f, FLabel), Color.black, TextAlignmentOptions.Left);

        const float valueW = 71.1f;                  // 原生右侧给数值留的缝
        // 轨道高 15 / 手柄盒 20×30 都由 `Anch` 的锚点+sizeDelta 直接算（原生值写在锚点里，不再另留常量）

        var sliderGo = Anch(go.transform, "Slider", 0.51f, 0f, 1f, 1f, -valueW, -10f, 0f, -5f);
        var valueGo = Anch(go.transform, "ValueTf", 1f, 0f, 1f, 1f, valueW, -10f, -valueW * 0.5f, -5f);
        var valueTxt = AddTxt(valueGo, "", FLabel, Color.black, TextAlignmentOptions.Right);

        // 轨道（`Shadow` 在原生里就是 `SUGShadowLite` 白色 + 锚 0,0.25~1,0.75）
        var trackGo = Anch(sliderGo.transform, "Background", 0f, 0.25f, 1f, 0.75f, 0f, 0f, 0f, 0f);
        AddImg(trackGo, "SGRounded", new Color(0f, 0f, 0f, 0.471f), false, Image.Type.Sliced, 7f);
        var trackShadow = Anch(sliderGo.transform, "Shadow", 0f, 0.25f, 1f, 0.75f, 60f, 60f, 0f, 0f);
        AddImg(trackShadow, "SUGShadowLite", Color.white, false, Image.Type.Sliced, 1f, skipIfMissing: true);
        try { trackShadow.transform.SetAsFirstSibling(); } catch { }

        // 填充（⚠ 2026-09-13 用户：“拖拽条起始位置空一块”——原生 `Fill Area` 是 `尺寸=-20x0 pos=(-5,0)`
        //   （左右各缩 10 再整体左移 5）⇒ 填充在**最左端**留下 5 单位、最右端留下 15 单位不涂。
        //   现在把 Fill Area 直接设成**整条轨道**（`Slider` 只重写 `Fill` 自己的锚点，不动这个容器）：
        //   fill 从轨道最左端开始涂、到底就是整条轨道，两端不再有缺口。）
        var slider = sliderGo.AddComponent<Slider>();
        var fillAreaGo = Anch(sliderGo.transform, "Fill Area", 0f, 0.25f, 1f, 0.75f, 0f, 0f, 0f, 0f);
        var fillGo = Anch(fillAreaGo.transform, "Fill", 0f, 0f, 1f, 1f, 0f, 0f, 0f, 0f);
        AddImg(fillGo, "SGRounded", new Color(0f, 0f, 0f, 0.863f), false, Image.Type.Sliced, 7f);

        // 手柄（**完整照抄原生的三层**：`Handle` 本体无图（20×30 定位盒，锚点由 `Slider` 改写），
        //   下面挂 `Bg` = **30×30 `SGRounded` 灰(0.545,0.565,0.604) `Simple` ppuMul=1** ← 这就是玩家看到的
        //   **圆形手柄**；`Shadow` = `SUGShadowLite`（rect 80×90、pos (0,5.5)）。
        // ⚠ 2026-09-13 血泪教训：上轮我用 `pagespec` 的**按名字** dump 看到 `/Handle 尺寸=20x0 rect=20x30` 没底图，
        //   就断定“原生没有可见手柄”把它删了 —— **_那是因为 dump 只到固定深度，没下钻到 Handle 的子树_**。
        //   用户第三次指出“原生的有可见手柄，是个圆的” → 用 `pagedump:SliderConsoleUGUI_(…)/Slider` 深 dump 才看到 `Handle/Bg`。
        //   ⇒ 结论：**判断“有没有子物体”必须下钻到足够深度**（`pagedump` 默认 14 层，别用只到 3~5 层的摘要）。
        var handleAreaGo = Anch(sliderGo.transform, "Handle Slide Area", 0f, 0f, 1f, 1f, -20f, 0f, 0f, 0f);
        var handleGo = Anch(handleAreaGo.transform, "Handle", 0f, 0f, 0f, 0f, 20f, 0f, 0f, 0f);
        // 手柄本体只负责定位（高 30 由 `Slider` 给），可见的圆方块是**点锚子节点** —— 否则会被 `Slider` 拉成竖条。
        var knobShadow = Anch(handleGo.transform, "Shadow", 0.5f, 0.5f, 0.5f, 0.5f, 80f, 90f, 0f, 5.5f);
        AddImg(knobShadow, "SUGShadowLite", Color.white, false, Image.Type.Sliced, 1f, skipIfMissing: true);
        var knobGo = Anch(handleGo.transform, "Bg", 0.5f, 0.5f, 0.5f, 0.5f, 30f, 30f, 0f, 0f);
        var knobImg = AddCircleKnob(knobGo);
        // ⚠ 2026-09-13 用户：“Handle 应该是和原生一样颜色的” —— 原生 `Handle/Bg` 里写的是
        //   色=(0.545,0.565,0.604) + 渲=白，但**实机渲染出来是深蓝灰**：同一张剪贴板上量到原生圆块 ≈ (31,35,48)，
        //   而我们同样的色值渲出来 ≈ (110,122,146)（原生那个 `Bg` 上还挂了个 MonoBehaviour，运行时另行压暗）。
        //   ⇒ 按“实机渲染色”反推：再乘一层 tint (0.28,0.29,0.33)，两边圆块就同色了（量法见 docs/UI_KIT.md 更新记录三十二）。
        Rc(knobImg, new Color(0.28f, 0.29f, 0.33f, 1f));
        // ⚠ 2026-09-13 用户：“拖动条的 Handle 是方的，圆角不够”（截图 12 倍放大确认仍是方块）——
        //   原因：原生素材 `SGRounded` 是 **256×256 但圆角只有 48px**；原生用 `Simple`（整图缩放），
        //   30×30 时圆角只剩 ~5.6 本地单位（屏上再乘 0.3591 ≈ **2px**）= 就是方的；
        //   改用 `Sliced` 把 9-slice 圆角撑到半径 15（= 半个控件）后**实测仍是方块**（`pixelsPerUnitMultiplier`
        //   在这个 sprite/Unity 版本下没把角撑开）⇒ 不再和素材较劲，直接**程序化圆片**（见 `CircleSprite()`）。

        try
        {
            slider.fillRect = fillGo.GetComponent<RectTransform>();
            slider.handleRect = handleGo.GetComponent<RectTransform>();
            slider.direction = Slider.Direction.LeftToRight;
            slider.wholeNumbers = Mathf.Approximately(row.Min, Mathf.Round(row.Min)) && Mathf.Approximately(row.Max, Mathf.Round(row.Max));
            slider.minValue = row.Min;
            slider.maxValue = row.Max;
            slider.value = row.Num;
            slider.transition = Selectable.Transition.None;
        }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "slider 初始化失败: " + ex.Message); }

        Action refresh = () =>
        {
            try
            {
                float v = slider.value;
                string txt = slider.wholeNumbers ? Mathf.RoundToInt(v).ToString() : v.ToString("0.##");
                valueTxt.text = txt + row.Suffix;
            }
            catch { }
        };
        refresh();   // 值变化时不需要监听：拖动/点击路径里已经手动调 refresh（IL2CPP 下 onValueChanged 的委托不好绑）

        // 拖拽：按下即开始拖（返回 true），拖动时把局部坐标换算成值
        // ⚠ 2026-09-13 用户：“拖动条和滚动条最大和最小的位置都滚不到（值能达到）” ——
        //   根因：`Slider` 把 `handleRect` 摆到 **手柄滑动区**（轨道左右各内缩 10，271 宽）的坐标里，
        //   而我却按**轨道**（292.8 宽）做值映射 ⇒ 拖到轨道两端时值已到 min/max，但手柄中心还差 10 单位才到端头。
        //   现在按“滑动区宽度”映射（两者中心重合，只需换宽度）：手柄中心与指针一致，两端能真到。
        var trackRt = trackGo.GetComponent<RectTransform>();
        var handleAreaRt = handleAreaGo.GetComponent<RectTransform>();
        // ⚠ 2026-09-13 用户：“只拖到 Handle 的时候似乎不更新值” ——
        //   热区原来挂在**轨道**上（高 15），而手柄盒高 **30**（上下各露出 7.5）、圆块更宽（两端各越出 5）:
        //   指针落在手柄露出部分时命中不到热区 ⇒ 拖不动。现在热区改成 **`Slider` 本身**
        //   （高 30、水平铺满＝轨道中心与它同心），`local` 基准不变（仍相对同一个中心），归一化仍用滑动区宽度。
        var hitRt = sliderGo.GetComponent<RectTransform>();
        Action<Vector2> setFromLocal = local =>
        {
            ApplySliderLocal(slider, trackRt, handleAreaRt, local);
            refresh();
            try { row.OnNum?.Invoke(slider.value); } catch { }
        };
        Zone(zonePrefix + ":slider:" + Sanitize(row.Label), hitRt, owner,
            onClick: null,
            pressLocal: local => { setFromLocal(local); return true; },
            onDrag: setFromLocal,
            onDragEnd: _ => { refresh(); try { row.OnNum?.Invoke(slider.value); } catch { } });

        return go;
    }

    /// <summary>
    /// 原生滚动条（抄游戏 `Scrollbar Vertical` 的结构）：**宽 20** 的 `SGRounded` 黑 0.471 槽 +
    /// `Sliding Area`（结构上仍内缩 10）+ `SGRounded` 灰(0.545,0.565,0.604) 手柄；手柄可拖，拖动时回调 0~1。
    ///
    /// ⚠ 2026-09-13 行程定稿：**手柄行程 = 整根槽**（`Pad = 0`，两端与槽端对齐）。
    ///   用户三次报“滚动条手柄到不了极值/外观到不了极致” —— 原生虽然上下各内缩 10，但那是 9-slice 装饰的留白，
    ///   玩家感知是“到底了”。要改回原生内缩：把下面 `Pad` 改成 `10f` 即可（拖动映射与摆手柄用同一套数）。
    ///</summary>
    public sealed class NativeScrollbar
    {
        public GameObject Root;
        public RectTransform Handle;
        public float BarH;
        public float Pad;
        public Action<float> OnDrag01;

        /// <summary>按“可视 / 内容”比例设置手柄高度（native：手柄高随内容比例变）。</summary>
        public void SetRatio(float viewRatio)
        {
            try
            {
                viewRatio = Mathf.Clamp(viewRatio, 0.05f, 1f);
                float usable = Mathf.Max(1f, BarH - Pad * 2f);
                float hh = Mathf.Max(24f, usable * viewRatio);
                if (Handle != null) Handle.sizeDelta = new Vector2(Handle.sizeDelta.x, hh);
            }
            catch { }
        }

        /// <summary>设置 0~1 位置（0 = 顶部）。</summary>
        public void SetValue(float v01)
        {
            try
            {
                v01 = Mathf.Clamp01(v01);
                float usable = Mathf.Max(1f, BarH - Pad * 2f);
                float hh = Handle != null ? Handle.sizeDelta.y : 24f;
                float y = (BarH * 0.5f - Pad) - hh * 0.5f - (usable - hh) * v01;
                if (Handle != null) Handle.anchoredPosition = new Vector2(Handle.anchoredPosition.x, y);
            }
            catch { }
        }
    }

    /// <summary>建一条原生滚动条（竖，锚在给定位置，高 <paramref name="barH"/> 用**调用方单位**）。
    /// 内部几何（宽 20、内缩 10）用**原生本地单位**，靠 <paramref name="uiScale"/> 缩到调用方单位 ——
    /// 这样 `ppuMul=7` 的 9-slice 圆角粗细也和原生一致。</summary>
    public static NativeScrollbar BuildScrollbar(Transform parent, string name, float x, float y, float barH,
        float viewRatio, float value01, object owner, string zonePrefix, Action<float> onDrag01, float uiScale = 1f)
    {
        if (uiScale <= 0.0001f) uiScale = 1f;
        float localH = barH / uiScale;
        var go = Node(parent, name, 20f, localH, x, y);
        if (Mathf.Abs(uiScale - 1f) > 0.0001f) go.transform.localScale = new Vector3(uiScale, uiScale, 1f);
        return BuildScrollbarInto(go, go.GetComponent<RectTransform>(), viewRatio, value01, owner, zonePrefix, onDrag01);
    }

    /// <summary>在**已经定位好**的物体里建滚动条（槽/滑动区/手柄全用原生本地单位，不缩放也不移位）——
    /// 调用方负责把 <paramref name="root"/> 摆到原生 `Scrollbar Vertical` 的位置（内容区右缘、上下内缩 8.5）。</summary>
    public static NativeScrollbar BuildScrollbarInto(GameObject root, RectTransform rootRt, float viewRatio, float value01,
        object owner, string zonePrefix, Action<float> onDrag01)
    {
        var sb = new NativeScrollbar { BarH = 0f, Pad = 0f, OnDrag01 = onDrag01 };   // Pad=0：手柄行程 = 整槽（见类注释）
        try
        {
            if (root == null || rootRt == null) return sb;
            sb.Root = root;
            float barH = 0f;
            try { barH = rootRt.rect.height; } catch { }
            if (barH <= 1f) { try { barH = -rootRt.sizeDelta.y - 17f; } catch { } }     // 尚未布局时按 sizeDelta 估
            if (barH <= 1f) barH = PageContentH - 17f;
            sb.BarH = barH;
            AddImg(root, "SGRounded", new Color(0f, 0f, 0f, 0.471f), true, Image.Type.Sliced, 7f);

            // 原生 `Sliding Area`：四边内缩 10（`尺寸=-20x-20`）
            var area = Node(root.transform, "Sliding Area", 0f, 0f, 0f, 0f);
            var art = area.GetComponent<RectTransform>();
            art.anchorMin = Vector2.zero;
            art.anchorMax = Vector2.one;
            art.offsetMin = new Vector2(10f, 10f);
            art.offsetMax = new Vector2(-10f, -10f);

            var handle = Node(area.transform, "Handle", 20f, 24f, 0f, 0f);
            sb.Handle = handle.GetComponent<RectTransform>();
            var handleImg = AddImg(handle, "SGRounded", new Color(0.545f, 0.565f, 0.604f, 1f), true, Image.Type.Sliced, 7f);
            // ⚠ 2026-09-13 用户：“滚动条（的手柄）也要和原生一样颜色” —— 原生 `Scrollbar Vertical` 的 `Selectable`
            //   过渡是 `ColorTint target=Handle`，**normalColor=(0.388,0.282,0.176,1)**（实机 dump 的 `渲=`）
            //   ⇒ 原生手柄是**深棕色**（灰底×棕 tint），而不是我们之前的浅灰。这里照抄那层 tint。
            Rc(handleImg, new Color(0.388f, 0.282f, 0.176f, 1f));

            sb.SetRatio(viewRatio);
            sb.SetValue(value01);

            var areaRt = art;
            Func<Vector2, bool> press = local => { DragScrollbar(sb, areaRt, local); return true; };
            Action<Vector2> drag = local => DragScrollbar(sb, areaRt, local);
            Zone(zonePrefix + ":scroll:" + Sanitize(root.name), rootRt, owner,
                onClick: null, pressLocal: press, onDrag: drag, onDragEnd: _ => { });
        }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "BuildScrollbarInto: " + ex.Message); }
        return sb;
    }

    private static void DragScrollbar(NativeScrollbar sb, RectTransform area, Vector2 local)
    {
        try
        {
            float usable = Mathf.Max(1f, sb.BarH - sb.Pad * 2f);
            float hh = sb.Handle != null ? sb.Handle.sizeDelta.y : 24f;
            float span = Mathf.Max(1f, usable - hh);
            float top = usable * 0.5f - hh * 0.5f;
            float v = Mathf.Clamp01((top - local.y) / span);
            sb.SetValue(v);
            sb.OnDrag01?.Invoke(v);
        }
        catch { }
    }

    /// <summary>把指针局部坐标换算成值。
    /// ⚠ `local` 是相对**传入热区那个矩形**（轨道）的中心；而 `Slider` 是按**手柄滑动区**摆手柄的 ——
    ///   两者中心重合、只有宽度不同，所以用滑动区的宽度做归一化，手柄就会跟着指针走到两端（用户报“到不了最大/最小位置”）。</summary>
    private static void ApplySliderLocal(Slider slider, RectTransform track, RectTransform handleArea, Vector2 local)
    {
        try
        {
            float w = handleArea != null ? handleArea.rect.width : 0f;
            if (w < 1f) w = track != null ? track.rect.width : 0f;
            if (w < 1f) return;
            float t = Mathf.Clamp01((local.x + w * 0.5f) / w);
            float v = Mathf.Lerp(slider.minValue, slider.maxValue, t);
            if (slider.wholeNumbers) v = Mathf.Round(v);
            slider.value = Mathf.Clamp(v, slider.minValue, slider.maxValue);
        }
        catch { }
    }

    /// <summary>下拉框（照抄原生 `DropdownUGUIWithLabel`：**上方标签 + 下方深色圆角框（当前项 + `SGDownArrow`）**）。
    /// 点一下在**下方展开选项列表**（后登记的热区在上层 → 天然盖住其它行）。</summary>
    /// <summary>下拉框（照抄原生 `DropdownUGUIWithLabel`：**上半 40 是标签（28 号黑字），下半 45.7 是深色框**
    /// （`SUGGradientRounded` 黑 0.902、ppuMul 7），框里 `ButtonLabel`（28 号）+ 右侧 `SGDownArrow` 17×17）。
    /// 点一下在**下方展开选项列表**（后登记的热区在上层 → 天然盖住其它行）。</summary>
    public static GameObject BuildDropdown(Transform parent, NativeRow row, float y, object owner, string zonePrefix)
    {
        float h = HeightOf(row.Kind);
        var go = Node(parent, "nw_dropdown_" + Sanitize(row.Label), RowW, h, 0f, y);

        // 原生 `Label`：锚 0,0.5~1,1（上半行）
        var capGo = Anch(go.transform, "Label", 0f, 0.5f, 1f, 1f, 0f, 0f, 0f, 0f);
        AddTxt(capGo, row.Label, FitFont(row.Label, RowW - 20f, FDropdown), Color.black, TextAlignmentOptions.Left);

        // 原生 `Shadow`（`SUGShadowLite` 白，锚 0,0~1,0.5）
        var shGo = Anch(go.transform, "Shadow", 0f, 0f, 1f, 0.5f, 60f, 60f, 0f, 0f);
        AddImg(shGo, "SUGShadowLite", Color.white, false, Image.Type.Sliced, 1f, skipIfMissing: true);

        // 原生 `Bg`：锚 0,0~1,0.57（=45.7 高）
        var bgGo = Anch(go.transform, "Bg", 0f, 0f, 1f, 0.57f, 0f, 0f, 0f, 0f);
        AddImg(bgGo, "SUGGradientRounded", new Color(0f, 0f, 0f, 0.902f), true, Image.Type.Sliced, 7f,
            fallback: new Color(0f, 0f, 0f, 0.902f));

        string cur = row.Options != null && row.Index >= 0 && row.Index < row.Options.Length ? row.Options[row.Index] : "";
        // 原生 `ButtonLabel`：锚 0,0~1,0.5、pos(-10.4,2)、28 号、**黑字**（框是空心描边，纸面透出来）
        var captionGo = Anch(go.transform, "ButtonLabel", 0f, 0f, 1f, 0.5f, -40.7f, -8f, -10.4f, 2f);
        var caption = AddTxt(captionGo, cur, FitFont(cur, RowW - 46f, FDropdown), Ink, TextAlignmentOptions.Left);

        // 原生 `Arrow`：锚 1,0.25~1,0.25、pos(-17,2)、17×17、黑
        var arrowGo = Anch(go.transform, "Arrow", 1f, 0.25f, 1f, 0.25f, 17f, 17f, -17f, 2f);
        AddImg(arrowGo, "SGDownArrow", Ink, false, Image.Type.Simple, 1f);

        UiHotZone zone = null;
        Action closeList = null;
        Zone(zonePrefix + ":dd:" + Sanitize(row.Label), go.GetComponent<RectTransform>(), owner, () =>
        {
            if (closeList != null) { closeList(); return; }                 // 已展开 → 再点收起
            try
            {
                var listGo = new GameObject("nw_dropdown_list");
                listGo.transform.SetParent(parent, false);
                var lrt = listGo.AddComponent<RectTransform>();
                lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 0.5f);
                lrt.pivot = new Vector2(0.5f, 0.5f);
                int n = row.Options != null ? row.Options.Length : 0;
                float ih = 30f;                                              // 原生选项项高 35
                lrt.sizeDelta = new Vector2(RowW, n * ih);
                lrt.anchoredPosition = new Vector2(0f, y - h * 0.5f - n * ih * 0.5f - 2f);
                var lBg = Node(listGo.transform, "Bg", RowW, n * ih, 0f, 0f);
                AddImg(lBg, "SGRounded_Bottom", new Color(0.94f, 0.94f, 0.94f, 1f), true, Image.Type.Sliced, 7f);

                for (int i = 0; i < n; i++)
                {
                    int idx = i;
                    string opt = row.Options[i];
                    // 原生 `Item`：容器 + `Item Background`(Image，高亮就染它) + `Item Label`(尺寸 -20,-3、字号 28)
                    var itemGo = Node(listGo.transform, "opt_" + Sanitize(opt), RowW - 6f, ih - 2f, 0f, (n * ih * 0.5f) - ih * 0.5f - i * ih);
                    bool sel = i == row.Index;
                    var baseC = sel ? new Color(0f, 0f, 0f, 0.08f) : new Color(0f, 0f, 0f, 0f);   // 当前项常驻淡暗带（原生 selectedColor）

                    var hlGo = Anch(itemGo.transform, "Item Background", 0f, 0f, 1f, 1f, 0f, 0f, 0f, 0f);
                    var hl = hlGo.AddComponent<Image>();
                    hl.sprite = null;
                    hl.color = baseC;
                    hl.type = Image.Type.Simple;
                    hl.raycastTarget = false;

                    var txtGo = Anch(itemGo.transform, "Item Label", 0f, 0f, 1f, 1f, -20f, -3f, 0f, -0.5f);
                    var t = AddTxt(txtGo, opt, FitFont(opt, RowW - 20f, FDropdown),
                        sel ? new Color(0.2f, 0.3f, 0.6f, 1f) : Ink, TextAlignmentOptions.Left);
                    try { t.raycastTarget = false; } catch { }

                    // ★ 悬停/按下变色（原生 `Item` 就是 Selectable + ColorTint：normal 白 → highlight 0.961 → pressed 0.784；
                    //   我们的列表底是浅色面板，所以用**半透明暗带**当高亮（与原生“比纸面稍暗”同一观感）。
                    ZoneTint(zonePrefix + ":ddopt:" + Sanitize(opt), itemGo.GetComponent<RectTransform>(), hl, owner, () =>
                    {
                        row.Index = idx;
                        try { caption.text = opt; } catch { }
                        closeList?.Invoke();
                        try { row.OnIndex?.Invoke(idx); } catch (Exception ex) { CoopLog.Warn("uikit.native", () => "dropdown callback: " + ex.Message); }
                    },
                    baseC: baseC,
                    hoverC: new Color(0f, 0f, 0f, 0.16f),
                    pressC: new Color(0f, 0f, 0f, 0.26f));
                }

                closeList = () =>
                {
                    try { UiPointerRouter.RemoveOwner(listGo); } catch { }
                    try { UnityEngine.Object.Destroy(listGo); } catch { }
                    closeList = null;
                };
                CoopLog.Debug("uikit.native", () => $"下拉框 '{row.Label}' 展开 {n} 项");
            }
            catch (Exception ex) { CoopLog.Warn("uikit.native", () => "dropdown 展开失败: " + ex.Message); }
        });
        _ = zone;
        return go;
    }

    /// <summary>
    /// 选项卡（照抄原生 `TabsCtn` + `TabButtonUGUI`）：
    /// 整条 = `SUGGradientRounded_Top` 深藏青 (0.014,0.039,0.066,0.961) ppuMul 4；
    /// 每格 160×72；**选中 = `Active` 图（原生无 sprite，纯色 (0,0,0,0.8)、ppuMul 5）+ 强调色文字
    /// (0.55,0.65,0.83) + 下方 15×15 菱形 (0.549,0.651,0.824) 转 45°**；未选中 = 直接画在条上（浅字）。
    /// </summary>
    public static GameObject BuildTabs(Transform parent, NativeRow row, float y, object owner, string zonePrefix)
    {
        float h = HeightOf(row.Kind);
        var go = Node(parent, "nw_tabs", RowW, h, 0f, y);

        var opts = row.Options ?? new string[0];
        if (opts.Length == 0) return go;

        // 原生 `TabsCtn` 的**整条底图 `组件enabled=False`**（实测！）——不画整条底，未选中就是纸面黑字
        float tw = RowW / opts.Length;
        var texts = new List<TextMeshProUGUI>();
        var bgs = new List<Image>();
        var marks = new List<GameObject>();

        for (int i = 0; i < opts.Length; i++)
        {
            int idx = i;
            string opt = opts[i];
            float x = -RowW * 0.5f + tw * (i + 0.5f);
            var tabGo = Node(go.transform, "TabButtonUGUI (" + Sanitize(opt) + ")", tw, h, x, 0f);

            // 选中态底：原生 `Active` **没有 sprite**，就是一块纯色 (0,0,0,0.8)
            var bgGo = Anch(tabGo.transform, "Active", 0f, 0f, 1f, 1f, 0f, 0f, 0f, 0f);
            var bgImg = bgGo.AddComponent<Image>();
            bgImg.color = new Color(0f, 0f, 0f, 0.8f);
            bgImg.type = Image.Type.Sliced;
            try { bgImg.pixelsPerUnitMultiplier = 5f; } catch { }
            bgImg.raycastTarget = false;
            bgs.Add(bgImg);

            var txtGo = Anch(tabGo.transform, "Normal", 0f, 0f, 1f, 1f, -12f, -8f, 0f, 0f);
            var t = AddTxt(txtGo, opt, FitFont(opt, tw - 16f, FTab, 9f), Color.black, TextAlignmentOptions.Center);
            texts.Add(t);

            // 下方小菱形（原生：15×15 纯色块转 45°）
            var markGo = Node(tabGo.transform, "Mark", 15f, 15f, 0f, -h * 0.5f + 8f);
            var markImg = markGo.AddComponent<Image>();
            markImg.color = new Color(0.549f, 0.651f, 0.824f, 1f);
            markImg.raycastTarget = false;
            try { markGo.transform.localRotation = Quaternion.Euler(0f, 0f, 45f); } catch { }
            marks.Add(markGo);

            Zone(zonePrefix + ":tab:" + Sanitize(opt), tabGo.GetComponent<RectTransform>(), owner, () =>
            {
                row.Index = idx;
                ApplyTabState(texts, bgs, marks, idx);
                try { row.OnIndex?.Invoke(idx); } catch (Exception ex) { CoopLog.Warn("uikit.native", () => "tabs callback: " + ex.Message); }
            });
        }
        ApplyTabState(texts, bgs, marks, row.Index);
        return go;
    }

    private static void ApplyTabState(List<TextMeshProUGUI> texts, List<Image> bgs, List<GameObject> marks, int sel)
    {
        for (int k = 0; k < texts.Count; k++)
        {
            bool on = k == sel;
            try
            {
                // 整条是纸面（原生整条底图是关的）→ 未选中用**黑字**、选中用原生强调青
                texts[k].color = on ? Accent : Ink;
                if (k < bgs.Count && bgs[k] != null) bgs[k].enabled = on;
                if (k < marks.Count && marks[k] != null) marks[k].SetActive(on);
            }
            catch { }
        }
    }

    /// <summary>
    /// 输入框（照抄原生 `TextfieldConsoleUGUI`：**左侧标签(0~51%) + 右侧输入框(51%~100%)**）。
    /// 输入框底 = `SUGGradientRounded` **(0,0,0,0.902) 深色**、Sliced、ppuMul **7**；文字 = `JMH Typewriter` 26 号右对齐。
    ///
    /// ⚠ 2026-09-13 用户：“输入框是白色背景，没有套用原生样式” —— 原来我用了游戏自己的 `InputFieldBackground` + 白色底，
    ///   那根本不是这一页的样式；原生的输入框就是**深色框**（`pagespec` 实测）。
    ///
    /// 驱动是**我们自己的**（用户：“输入框用一样的样式但是该用我们自己的驱动实现”）：
    /// 点击聚焦 → `TickInputs` 读键写入 → 回车提交（v1 只收 ASCII/退格；中文走我们窗口页的 IME 管线）。
    /// </summary>
    public static GameObject BuildInput(Transform parent, NativeRow row, float y, object owner, string zonePrefix)
    {
        float h = HeightOf(row.Kind);
        var go = Node(parent, "nw_input_" + Sanitize(row.Label), RowW, h, 0f, y);

        var labelGo = Anch(go.transform, "Label", 0f, 0f, 0.51f, 1f, -10f, -10f, 5f, -5f);
        AddTxt(labelGo, row.Label, FitFont(row.Label, RowW * 0.51f - 20f, FLabel), Color.black, TextAlignmentOptions.Left);

        // 原生 `InputField (TMP)`：锚 0.51,0~1,1、`Text Area` 内缩 (-20,-13)
        var boxGo = Anch(go.transform, "InputField (TMP)", 0.51f, 0f, 1f, 1f, 0f, 0f, 0f, 0f);
        var bg = AddImg(boxGo, "SUGGradientRounded", new Color(0f, 0f, 0f, 0.902f), true, Image.Type.Sliced, 7f,
            fallback: new Color(0f, 0f, 0f, 0.902f));

        var areaGo = Anch(boxGo.transform, "Text Area", 0f, 0f, 1f, 1f, -20f, -13f, 0f, -0.5f);
        var txt = AddTxt(areaGo, string.IsNullOrEmpty(row.Value) ? (row.Label ?? "") : row.Value,
            FitFont(row.Value, RowW * 0.49f - 26f, FInput),
            string.IsNullOrEmpty(row.Value) ? InkFaint : InkSoft,
            TextAlignmentOptions.Right, fontName: "JMH Typewriter SDF");

        var state = new InputState { Row = row, Text = txt, Placeholder = row.Label ?? "", Focused = false, Buffer = row.Value ?? "", Box = bg };
        _inputs[go] = state;

        Zone(zonePrefix + ":input:" + Sanitize(row.Label), go.GetComponent<RectTransform>(), owner, () =>
        {
            // 先把别的框“失焦 + 提交”（用户：“输入没有自动保存，应该失焦就自动保存”）
            // ⚠ 先收集再动：提交会调第三方回调，回调可能重建页面（同步改 `_inputs`）
            List<InputState> others = null;
            foreach (var kv in _inputs)
            {
                var o = kv.Value;
                if (o == null || o == state || !o.Focused) continue;
                (others ??= new List<InputState>()).Add(o);
            }
            if (others != null)
                for (int i = 0; i < others.Count; i++)
                {
                    others[i].Focused = false;
                    SetInputFocus(others[i], false);
                    CommitInput(others[i]);
                    ApplyInputText(others[i], others[i].Buffer);
                }
            state.Focused = true;
            _caretOn = true; _caretT = 0f;          // 刚聚焦 → 光标立刻亮（用户要求“输入框要有 Caret”）
            SetInputFocus(state, true);
            ApplyInputText(state, state.Buffer);
            try { UiPointerRouter.TextFocus = true; } catch { }
            CoopLog.Debug("uikit.native", () => $"原生输入框聚焦 '{row.Label}'");
        });
        return go;
    }

    /// <summary>提交一个输入框：把缓冲回写 <see cref="NativeRow.Value"/>，**变了才**回调。
    /// 失焦（点别的框 / 页面重建 / 关页）时调 —— 用户要求“失焦就自动保存”。</summary>
    private static void CommitInput(InputState st)
    {
        if (st == null || st.Row == null) return;
        try
        {
            string v = st.Buffer ?? "";
            if (string.Equals(v, st.Row.Value ?? "", StringComparison.Ordinal)) return;
            st.Row.Value = v;
            CoopLog.Debug("uikit.native", () => $"输入框失焦提交 '{st.Row.Label}' = '{v}'");
            try { st.Row.OnText?.Invoke(v); } catch (Exception ex) { CoopLog.Warn("uikit.native", () => "input callback (blur): " + ex.Message); }
        }
        catch { }
    }

    /// <summary>把所有缓冲与 <see cref="NativeRow.Value"/> 不一致的输入框提交（页面重建/关闭前调）。</summary>
    private static void CommitAllInputs()
    {
        try
        {
            List<InputState> all = null;
            foreach (var kv in _inputs) if (kv.Value != null) (all ??= new List<InputState>()).Add(kv.Value);
            if (all == null) return;
            for (int i = 0; i < all.Count; i++) CommitInput(all[i]);
        }
        catch { }
    }

    // ---- 光标闪烁（0.5s 一亮一灭；文本末尾的 `|`）----
    private static bool _caretOn = true;
    private static float _caretT;

    /// <summary>重绘输入框文本（含占位符与光标）。光标用“末尾追加 `|` / 空格”实现（右对齐下位置自然在末尾）。</summary>
    private static void ApplyInputText(InputState st, string buffer)
    {
        if (st == null || st.Text == null) return;
        try
        {
            buffer = buffer ?? "";
            bool has = buffer.Length > 0;
            string shown = has ? buffer : (st.Placeholder ?? "");
            if (st.Focused) shown += _caretOn ? "|" : " ";     // 空格而不是空串 → 宽度稳定，字不会左右跳
            st.Text.text = shown;
            st.Text.color = has ? InkSoft : InkFaint;
            st.Text.fontSize = FitFont(buffer, RowW * 0.49f - 26f, FInput);
        }
        catch { }
    }

    private static void SetInputFocus(InputState st, bool on)
    {
        try
        {
            if (st == null || st.Box == null) return;
            // ⚠ 不能改 `Image.color`：框底是“极淡填充 + 深色描边”的空心素材（中心 A=41），
            //   把它染深就变成一个黑框了。用**渲染色**（乘法）把描边压暗一点表示聚焦。
            Rc(st.Box, on ? new Color(0.55f, 0.55f, 0.55f, 1f) : Color.white);
        }
        catch { }
    }

    private sealed class InputState
    {
        public NativeRow Row;
        public TextMeshProUGUI Text;
        public Image Box;
        public string Placeholder;
        public bool Focused;
        public string Buffer;
    }

    private static readonly Dictionary<GameObject, InputState> _inputs = new Dictionary<GameObject, InputState>();

    /// <summary>清空输入框/键位状态表（页面重建/关页时调）。
    /// ⚠ 清之前先**把还在编辑的内容提交**：用户要求“失焦就自动保存”（重建/关页也算失焦）。</summary>
    public static void ClearInputs()
    {
        try { CommitAllInputs(); } catch { }
        _inputs.Clear(); _keybinds.Clear();
    }

    /// <summary>每帧驱动输入框与键位行（键位监听优先吃按键）。由 `NativeMenuPage.Tick` 调。</summary>
    public static void TickInputs(float dt)
    {
        if (_inputs.Count == 0 && _keybinds.Count == 0) return;
        try
        {
            if (dt <= 0f) { try { dt = Time.unscaledDeltaTime; } catch { dt = 0.016f; } }
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;

            // ① 键位行：正在监听的下一个按键就绑上去
            foreach (var kv in _keybinds)
            {
                var bind = kv.Value;
                if (bind == null || !bind.Listening) continue;
                string key = ReadFirstPressedKey(kb);
                if (key == null) continue;
                bind.Row.Value = key;
                try { bind.Text.text = key; bind.Text.fontSize = FitFont(key, RowW * 0.49f - 16f, 18f); } catch { }
                SetListening(bind, false);
                try { bind.Row.OnText?.Invoke(key); } catch (Exception ex) { CoopLog.Warn("uikit.native", () => "keybind callback: " + ex.Message); }
                return;
            }

            // ② 输入框
            InputState cur = null;
            foreach (var kv in _inputs) { if (kv.Value.Focused) { cur = kv.Value; break; } }
            if (cur == null) return;

            bool changed = false;
            // 退格
            if (kb.backspaceKey.wasPressedThisFrame && cur.Buffer.Length > 0) { cur.Buffer = cur.Buffer.Substring(0, cur.Buffer.Length - 1); changed = true; }
            // 回车 = 提交
            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
            {
                cur.Row.Value = cur.Buffer;
                cur.Focused = false;
                SetInputFocus(cur, false);
                ApplyInputText(cur, cur.Buffer);
                try { UiPointerRouter.TextFocus = false; } catch { }
                try { cur.Row.OnText?.Invoke(cur.Buffer); } catch (Exception ex) { CoopLog.Warn("uikit.native", () => "input callback: " + ex.Message); }
                return;
            }
            // 基本字符（ASCII；中文走我们的窗口页）
            for (int i = 0; i < _letterKeys.Length; i++)
            {
                if (!kb[_letterKeys[i]].wasPressedThisFrame) continue;
                char c = _letters[i];
                if (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed) c = char.ToUpperInvariant(c);
                cur.Buffer += c; changed = true;
            }
            for (int i = 0; i < _digitKeys.Length; i++)
            {
                if (!kb[_digitKeys[i]].wasPressedThisFrame) continue;
                cur.Buffer += (char)('0' + i); changed = true;
            }
            if (changed)
            {
                ApplyInputText(cur, cur.Buffer);
                _caretOn = true; _caretT = 0f;          // 打字时光标常亮，停手后才开始闪
            }
            else if (cur.Buffer.Length == 0)
            {
                ApplyInputText(cur, cur.Buffer);
            }

            // 光标闪烁（0.5s 一亮一灭）
            _caretT += dt;
            if (_caretT >= 0.5f) { _caretT = 0f; _caretOn = !_caretOn; ApplyInputText(cur, cur.Buffer); }
        }
        catch { }
    }

    private static readonly UnityEngine.InputSystem.Key[] _letterKeys = BuildLetterKeys();
    private static readonly char[] _letters = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
    private static readonly UnityEngine.InputSystem.Key[] _digitKeys = BuildDigitKeys();

    private static UnityEngine.InputSystem.Key[] BuildLetterKeys()
    {
        var a = new UnityEngine.InputSystem.Key[26];
        for (int i = 0; i < 26; i++) a[i] = (UnityEngine.InputSystem.Key)((int)UnityEngine.InputSystem.Key.A + i);
        return a;
    }

    /// <summary>本帧按下的第一个键（用于键位绑定）；没有则返回 null。</summary>
    private static string ReadFirstPressedKey(UnityEngine.InputSystem.Keyboard kb)
    {
        try
        {
            var keys = kb.allKeys;
            for (int i = 0; keys != null && i < keys.Count; i++)
            {
                var k = keys[i];
                if (k == null) continue;
                if (!k.wasPressedThisFrame) continue;
                string dn = "";
                try { dn = k.displayName ?? ""; } catch { }
                if (string.IsNullOrEmpty(dn)) { try { dn = k.name ?? ""; } catch { } }
                if (string.IsNullOrEmpty(dn)) continue;
                if (dn.Length == 1) dn = dn.ToUpperInvariant();
                return dn;
            }
        }
        catch { }
        return null;
    }

    private static UnityEngine.InputSystem.Key[] BuildDigitKeys()
    {
        var a = new UnityEngine.InputSystem.Key[10];
        for (int i = 0; i < 10; i++) a[i] = (UnityEngine.InputSystem.Key)((int)UnityEngine.InputSystem.Key.Digit1 + i);
        return a;
    }

    /// <summary>热区名用的安全片段（去空格/特殊字符，测试脚本 `tap:` 才好写）。</summary>
    public static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "x";
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length && sb.Length < 18; i++)
        {
            char c = s[i];
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c == '_' || c == '-') sb.Append(c);
            else if (c == ' ') sb.Append('_');
        }
        return sb.Length == 0 ? "x" : sb.ToString();
    }
}
