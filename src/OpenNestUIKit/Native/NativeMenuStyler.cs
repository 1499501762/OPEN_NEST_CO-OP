using System;
using UnityEngine;
using UnityEngine.UI;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestUIKit.Native;

/// <summary>
/// 原生菜单样式抄写器：把**原生按钮**的外观（Image sprite / 过渡 / 字体 / 字号 / 内边距）
/// 抄到我们自建的按钮上，使注入项在原生菜单里"看不出是模组加的"。
///
/// 全部要点来自 `docs/NATIVE_UI.md` §7 的 11 条踩坑记录（`MainMenuEntry` 实证），**照抄不重犯**：
/// 1. **不克隆原生按钮**：克隆会带原生链接脚本 / `onClick` 持久绑定（点击跳愿望单等副作用）→ 自建 + 抄样式；
/// 2. **跳过阴影层**：模板按钮下常有 `SUGShadowLite` 之类的阴影 Image，要取正文背景；
/// 3. **字号必须固定 + 强制关 autoSizing**：抄某按钮 `enableAutoSizing=true` 会被按钮尺寸缩小文字（"忽大忽小"的根因）；
/// 4. **抄 `fontStyle`**（原生按钮文字是粗体）与**内边距**（否则文字贴满按钮，视觉显大）；
/// 5. 模板按钮必须**按名精确选**（`GetComponentsInChildren` 返回顺序不可依赖）。
/// </summary>
public static class NativeMenuStyler
{
    /// <summary>原生 ESC 菜单按钮的统一字号（实测结论：25 偏大、20 正常）。</summary>
    public const float FontSize = 20f;

    /// <summary>按名字优先级选模板按钮（精确 → 含关键字 → 第一个）。</summary>
    public static Button FindTemplate(Transform container, params string[] namePriority)
    {
        if (container == null) return null;
        Button[] all = null;
        try { all = container.GetComponentsInChildren<Button>(true); } catch { }
        if (all == null || all.Length == 0) return null;

        if (namePriority != null)
        {
            for (int p = 0; p < namePriority.Length; p++)
            {
                string want = namePriority[p];
                if (string.IsNullOrEmpty(want)) continue;
                for (int i = 0; i < all.Length; i++)
                {
                    var b = all[i];
                    if (b == null) continue;
                    if (string.Equals(b.name, want, StringComparison.OrdinalIgnoreCase)) return b;
                }
            }
            for (int p = 0; p < namePriority.Length; p++)
            {
                string want = namePriority[p];
                if (string.IsNullOrEmpty(want)) continue;
                for (int i = 0; i < all.Length; i++)
                {
                    var b = all[i];
                    if (b == null) continue;
                    if (b.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) return b;
                }
            }
        }
        for (int i = 0; i < all.Length; i++) if (all[i] != null) return all[i];
        return null;
    }

    /// <summary>取模板按钮的正文背景 Image（跳过名字含 Shadow 的阴影层）。</summary>
    public static Image FindTemplateBackground(Button template)
    {
        if (template == null) return null;
        try
        {
            Image first = null;
            var imgs = template.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < imgs.Length; i++)
            {
                var im = imgs[i];
                if (im == null) continue;
                if (first == null) first = im;
                string sn = im.sprite != null ? im.sprite.name : "";
                if (sn.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) < 0) return im;
            }
            return first;
        }
        catch { return null; }
    }

    /// <summary>把模板的外观抄到目标按钮（Image + Button 过渡）。<paramref name="templateBg"/> 为 null 时自动找。</summary>
    public static void CopyButtonVisual(Button template, Button target, Image targetImage, Image templateBg = null)
    {
        if (target == null) return;
        try
        {
            var bg = templateBg ?? FindTemplateBackground(template);
            if (targetImage != null && bg != null)
            {
                targetImage.sprite = bg.sprite;
                targetImage.type = bg.type;
                if (bg.sprite != null) targetImage.color = bg.color;
                try
                {
                    targetImage.pixelsPerUnitMultiplier = bg.pixelsPerUnitMultiplier;
                    targetImage.fillCenter = bg.fillCenter;
                }
                catch { }
            }
            CopyTint(template, target, targetImage);
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.native", () => "CopyButtonVisual: " + ex.Message);
        }
    }

    /// <summary>
    /// 抄模板的 `Button` 过渡（悬停/点击变色）：**颜色原样照抄，绝不改成白的**（用户要求：
    /// “颜色改为抄颜色而不是瞎改成白的”）。
    ///
    /// 历史（2026-09-13）：我们一度把原生 tint 里的**纯黑** `normalColor` 当成“会把底图染黑”的元凶，
    /// 于是自作主张整块换成“白底 + 轻微高亮”。结果：**底色跟原生不一样了**（原生纸面本来是有自己的
    /// 颜色/花纹的），而“ESC 注入项没有字”的真正原因是文字框只有 81x18 高（见 `NativeMenuInjector.StyleGridCell`）。
    ///
    /// 正确做法 = **结构也照抄**：
    /// · 原生那边 tint 打在**子节点**上（本游戏里是一个悬停用的覆盖层 `Bg`），按钮自己身上的底图**不吃 tint**，
    ///   所以原生看起来永远是纸面本色；
    /// · 我们原来是 `targetGraphic = 自己身上的底图` → 黑 tint 真的生效 → 底图被染黑（这才是“白框”/看不见的来源）；
    /// · 现在：`colors`/`transition` 原样抄，然后**镜像一个同款子节点**（同名、同 sprite/颜色/锚点，不可点）当 tint 目标
    ///   → 静止时底图保持本色（与原生一致），悬停也还有原生那点反馈。
    /// </summary>
    public static void CopyTint(Button template, Button target, Image rootImg)
    {
        if (target == null) return;
        try
        {
            if (template == null)
            {
                if (rootImg != null) { try { target.targetGraphic = rootImg; } catch { } }
                return;
            }
            try { target.transition = template.transition; } catch { }
            try { target.colors = template.colors; } catch { }        // ← 原样照抄（含纯黑 normalColor）

            Graphic tTg = null;
            try { tTg = template.targetGraphic; } catch { }
            if (tTg == null)
            {
                // 模板压根没有 tint 目标 → 我们也不设（否则抄来的颜色会落到我们的底图上，把它染了）
                try { target.targetGraphic = null; } catch { }
                CoopLog.Debug("uikit.native", () => "模板无 targetGraphic → 我们的按钮也不设（颜色照抄但不生效）");
                return;
            }
            if (tTg.transform == template.transform)
            {
                // 原生就是拿“自己身上的底图”当 tint 目标 → 我们照做（此时原生底图颜色=抄来的底色，表现一致）
                if (rootImg != null) { try { target.targetGraphic = rootImg; } catch { } }
                return;
            }

            // 原生 tint 打在**子节点**上（本游戏是子物体 `Bg`）→ 镜像那**一层**可见底图，并把 tint 目标指到它。
            //
            // ⚠ 2026-09-13（用户：“颜色正常了…原生只有一种背景”）：**只镜像一层**。
            //   实测模板 `OpenSettingsBtn` 的子图里 `BgShadow` 是关着的阴影，可画的有 `Bg` 与 `Selected`
            //   （后者在 `SelectionUGUI` 下、盖在 `Bg` 上面）——**玩家看到的只有最上面那层**。
            //   我们以前“自己在根上再画一张白底图” = 原生没有的那一层（用户：“有个白色的叠层”）。
            //   现在：照抄**最上面那一层**（= 玩家真正看到的），我们自己不再加任何底图。
            try
            {
                Image top = null;
                var srcs = template.GetComponentsInChildren<Image>(true);   // depth-first = 也是绘制顺序
                for (int i = 0; srcs != null && i < srcs.Length; i++)
                {
                    var s = srcs[i];
                    if (s == null) continue;
                    if (s.transform == template.transform) continue;         // 根节点（本游戏原生按钮根没有底图）
                    if (!s.enabled) continue;                                 // 关着的层不抄（如 `BgShadow`）
                    try { if (!s.gameObject.activeSelf) continue; } catch { }
                    string sn = ""; try { sn = s.sprite != null ? s.sprite.name : ""; } catch { }
                    if (sn.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) >= 0) continue;   // 阴影层
                    top = s;                                                  // 越往后越在上层 → 取最后一个
                }
                if (top != null)
                {
                    // ★ 这层也是**点击的射线目标**（UGUI 靠 Graphic.raycastTarget 命中）——
                    //   ⚠ 2026-09-13 踩坑：去掉根底图后忘了这一点 → “注入的三个按钮点不了”。
                    //   原生 `Bg` 本来就是可点的（模板按钮靠它收点击），所以这里照抄它的 raycastTarget。
                    var made = MirrorImage(top, target.transform, raycast: true);
                    if (made != null)
                    {
                        // 模板的可见层就是 tint 目标 → 把 tint 也接上（如原生）；否则不设（避免把抄来的颜色落到它身上）
                        try
                        {
                            if (tTg is Image && top == (Image)tTg) target.targetGraphic = made;
                            else target.targetGraphic = null;
                        }
                        catch { }
                        CoopLog.Debug("uikit.native", () => $"底图照抄原生**单层**：镜像子节点 '{made.gameObject.name}'（sprite={(made.sprite != null ? made.sprite.name : "null")} 色={made.color} ppuMul={made.pixelsPerUnitMultiplier:0.##}）");
                    }
                }
            }
            catch (Exception ex) { CoopLog.Warn("uikit.native", () => "镜像底图失败: " + ex.Message); }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.native", () => "CopyTint: " + ex.Message);
        }
    }

    /// <summary>把模板里的一张底图**逐项照抄**成我们按钮下的子节点（同名、同 sprite/类型/倍率/颜色/矩形）。
    /// <paramref name="raycast"/> = 是否让它当**点击射线目标**（照抄模板时给“最上层那层底图”用 true）。</summary>
    private static Image MirrorImage(Image src, Transform parent, bool raycast = false)
    {
        if (src == null || parent == null) return null;
        try
        {
            string childName = string.IsNullOrEmpty(src.gameObject.name) ? "Bg" : src.gameObject.name;
            var exist = parent.Find(childName);
            if (exist != null)
            {
                var ei = exist.GetComponent<Image>();
                if (ei != null && raycast) { try { ei.raycastTarget = true; } catch { } }
                return ei;
            }

            var go = new GameObject(childName);
            go.transform.SetParent(parent, false);          // 追加在末尾 → 与模板同级顺序 + 我们的文字最后建 = 画在最上
            var rt = go.AddComponent<RectTransform>();
            var srt = src.rectTransform;
            if (srt != null)
            {
                rt.anchorMin = srt.anchorMin; rt.anchorMax = srt.anchorMax;
                rt.pivot = srt.pivot; rt.anchoredPosition = srt.anchoredPosition; rt.sizeDelta = srt.sizeDelta;
                rt.offsetMin = srt.offsetMin; rt.offsetMax = srt.offsetMax;
            }
            else { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero; }

            var img = go.AddComponent<Image>();
            img.sprite = src.sprite;
            img.type = src.type;
            img.color = src.color;
            try { img.pixelsPerUnitMultiplier = src.pixelsPerUnitMultiplier; img.fillCenter = src.fillCenter; } catch { }
            img.raycastTarget = raycast;        // 只有“当点击目标”的那层才吃射线（其余层不吃，避免挡住）
            return img;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.native", () => "MirrorImage: " + ex.Message);
            return null;
        }
    }

    /// <summary>原生观感文字：抄模板字体/颜色/fontStyle/内边距，**字号固定 + 强制关 autoSizing**。
    /// 模板取不到时退回本地化字体（<see cref="UI.UiKit.EnsureFont"/>）。
    ///
    /// ⚠ 取模板文字时要**挑“正文”那一颗**（跳过名字含 shadow/outline/glow 的、alpha≈0 的）。
    ///
    /// ⚠⚠ 2026-09-13 又修了一次（用户：“ESC菜单里的注入项还是没有字”）：
    ///   上一版抄完颜色后又加了一条“亮度太低就当看不清 → 退成白色”的规则——**这条规则是错的**：
    ///   实测本游戏原生 ESC 按钮的正文就是 **纯黑**（`颜色=RGBA(0,0,0,1)`），因为按钮底图
    ///   （`UI Box Castile` 那张纸）是**浅色**的；我们把黑字改成白 → 白字白底 → 用户什么都看不到。
    ///   结论：**模板正文什么颜色就抄什么颜色**（只有 alpha≈0 这种真看不见的才跳过）。
    ///   诊断证据：`esctext` 命令打印的“原生模板 vs 我们”的字体/材质/字号/颜色/字形数（两边应一致）。
    /// </summary>
    public static TextMeshProUGUI CopyTextVisual(Button template, TextMeshProUGUI txt)
    {
        if (txt == null) return null;
        bool haveFont = false;
        try
        {
            TMP_Text[] tTxts = null;
            try { tTxts = template != null ? template.GetComponentsInChildren<TMP_Text>(true) : null; } catch { }
            var t = PickBodyText(tTxts);
            if (t != null)
            {
                try { if (t.font != null) { txt.font = t.font; haveFont = true; } } catch { }
                try { txt.color = t.color; } catch { }        // 原样照抄（含黑色——原生就是黑的）
                try { txt.fontStyle = t.fontStyle; } catch { }
                try
                {
                    var tRt = t.rectTransform;
                    var rt = txt.rectTransform;
                    if (tRt != null && rt != null)
                    {
                        rt.anchorMin = tRt.anchorMin;
                        rt.anchorMax = tRt.anchorMax;
                        rt.offsetMin = tRt.offsetMin;   // 内边距照抄（原生 20/10），否则文字贴满按钮显大
                        rt.offsetMax = tRt.offsetMax;
                    }
                }
                catch { }
            }
        }
        catch { }

        try
        {
            txt.fontSize = FontSize;
            txt.enableAutoSizing = false;   // ⚠️ 必须关：否则按按钮尺寸自动缩放 → 字号"忽大忽小"
        }
        catch { }
        if (!haveFont) UI.UiKit.EnsureFont(txt);
        return txt;
    }

    /// <summary>从模板按钮的子节点里挑“正文”那颗文字：跳过阴影/描边副本与 alpha≈0 的，
    /// 优先“开着且真的能显示”的那一颗（**保留原顺序**，与旧版 `tTxts[0]` 行为一致）。</summary>
    private static TMP_Text PickBodyText(TMP_Text[] all)
    {
        if (all == null || all.Length == 0) return null;
        TMP_Text best = null; int bestScore = int.MinValue;
        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i];
            if (t == null) continue;
            string n = "";
            try { n = t.gameObject.name ?? ""; } catch { }
            int score = 0;
            if (n.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0) score -= 100;
            if (n.IndexOf("outline", StringComparison.OrdinalIgnoreCase) >= 0) score -= 100;
            if (n.IndexOf("glow", StringComparison.OrdinalIgnoreCase) >= 0) score -= 100;
            try { var c = t.color; if (c.a < 0.1f) score -= 50; } catch { }
            try { if (t.font != null) score += 10; } catch { }
            try { if (t.enabled) score++; } catch { }
            try { if (t.gameObject.activeInHierarchy) score++; } catch { }
            score -= i;                                   // 同分时取靠前的（旧版就是第一个）
            if (score > bestScore) { bestScore = score; best = t; }
        }
        return best ?? all[0];
    }

    /// <summary>
    /// 创建"原生观感"按钮（自建 + 抄样式，不克隆）：
    /// 背景 Image + Button + 居中文字，文字走 <see cref="CopyTextVisual"/>。
    ///
    /// ⚠⚠⚠ 2026-09-13 **“ESC 菜单注入项没有字”的真因（已实验证实，见 `TestDriver` 的 `nativebtn` X/Y 对照）**：
    ///   抄完模板后**文字框只有 81x18**（格子 121x38 减掉模板内边距 20/10×2），而 CourierPrime 字号 18 的
    ///   **行高 ~26.4px > 18** → TMP 在 `Ellipsis`（截断）模式下“这一行放不下 → **整行丢掉**” →
    ///   实测 `行数=0 字符=0 可见字符=0`（底图 Image 照画 → 用户看到“白框没字”）；把文字框**垂直铺满**后
    ///   `行数=1 字符=6`，字出来了（`NativeMenuInjector.StyleGridCell` 做这件事）。
    ///   注：之前怀疑的“黑色 tint 把底图染黑”与“inactive 父物体下 TMP 不 Awake”都不是本次“没字”的原因
    ///   （tint 确实会把底图弄黑，但那是“框黑”不是“没字”，已按用户要求改成照抄原生颜色）。
    /// </summary>
    public static Button CreateNativeButton(Transform parent, string name, string text, Button template,
        Action onClick, float width, float height)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(width, height);

        // ⚠⚠ 2026-09-13 **“注入的三行有个白色的叠层”（用户）**：
        //   我们原来无条件给按钮根挂一张自己的底图（`Image`），**再**镜像模板的 tint 子节点 `Bg` → 一层变两层。
        //   实测模板 `OpenSettingsBtn` 的**根节点根本没有 Graphic**（底图在子物体 `Bg` 上）：
        //   我们那张白底图 = 原生没有的额外一层 → 看起来就是“白色叠了个框”。
        //   现在按模板结构照搬：**模板根有 Graphic 才建根底图**；没有（本游戏原生按钮）就让镜像出来的 `Bg` 当底。
        bool tplRootGraphic = false;
        try { tplRootGraphic = template != null && template.GetComponent<Graphic>() != null; } catch { }
        Image img = null;
        if (tplRootGraphic || template == null)
        {
            img = go.AddComponent<Image>();
            img.color = new Color(0.16f, 0.20f, 0.26f, 0.96f);
        }
        var btn = go.AddComponent<Button>();
        try { btn.onClick.AddListener(new Action(() => { try { onClick?.Invoke(); } catch (Exception ex) { CoopLog.Warn("uikit.native", () => $"entry click failed: {ex.Message}"); } })); }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "AddListener failed: " + ex.Message); }

        CopyButtonVisual(template, btn, img);

        var txtGo = new GameObject("Text");
        txtGo.transform.SetParent(go.transform, false);
        var trt = txtGo.AddComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;
        var txt = txtGo.AddComponent<TextMeshProUGUI>();
        txt.text = text ?? "";
        txt.alignment = TextAlignmentOptions.Center;
        txt.color = Color.white;
        txt.raycastTarget = false;
        CopyTextVisual(template, txt);
        // 底图／文字颜色都**照抄原生**（用户：“颜色正常了，不需要兜底换色”）→ 不再做对比度兜底换色。

        return btn;
    }

    /// <summary>
    /// ⛔ 已停用（2026-09-13，用户：“颜色正常了，不需要兜底换色”）：保留体但不调用。
    /// 历史上它用来把“对比度不足”的文字翻成黑/白 —— 但**抄原生就该原样**，任何自作聪明的换色都可能
    /// 造成“跟原生不一样”（白字白底 / 黑字黑底都踩过）。诊断/排查时仍可手动调用看计算值。
    /// </summary>
    public static void EnsureTextContrast(Image bg, TMP_Text txt)
    {
        CoopLog.Debug("uikit.native", () => "EnsureTextContrast 已停用（文字/底图一律照抄原生）");
        return;
#pragma warning disable CS0162
        try
        {
            if (bg == null || txt == null) return;
            var b = bg.color;
            if (b.a < 0.5f) return;                    // 半透明底 → 背后是什么不知道，保持原样
            float bl = Lum(b), tl = Lum(txt.color);
            if (Mathf.Abs(bl - tl) >= 0.35f) return;    // 对比度够 → 保持原生颜色
            txt.color = bl > 0.5f ? Color.black : Color.white;
            CoopLog.Debug("uikit.native", () => $"注入行文字对比度不足 → 改为 {(bl > 0.5f ? "黑" : "白")}字（底色亮度 {bl:0.##}）");
        }
        catch { }
#pragma warning restore CS0162
    }

    private static float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

    /// <summary>取容器里原生按钮的最大 y（"最高按钮"，作为重排基准；排除我们自己的按钮）。</summary>
    public static float TopY(Transform container, string ourPrefix)
    {
        float top = 0f;
        try
        {
            var all = container.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var b = all[i];
                if (b == null) continue;
                if (!string.IsNullOrEmpty(ourPrefix) && b.name.StartsWith(ourPrefix, StringComparison.Ordinal)) continue;
                var r = b.GetComponent<RectTransform>();
                if (r == null) continue;
                if ((string.IsNullOrEmpty(ourPrefix) || true) && r.parent != container && !IsDirectChildOf(b.transform, container)) continue;
                if (r.anchoredPosition.y > top) top = r.anchoredPosition.y;
            }
        }
        catch { }
        return top;
    }

    /// <summary>是否容器的直接子物体（用父链判断，`foreach(Transform)` 在 IL2CPP 下不可靠）。</summary>
    public static bool IsDirectChildOf(Transform t, Transform parent)
    {
        try { return t != null && parent != null && ReferenceEquals(t.parent, parent); }
        catch { return false; }
    }
}
