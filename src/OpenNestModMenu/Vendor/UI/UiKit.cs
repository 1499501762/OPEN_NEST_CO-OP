// ⚠️ Vendor 源码副本：拷自 src/OpenNestCore/UI/UiKit.cs，唯一改动 = namespace。见 Vendor/README.md
using System;
using UnityEngine;
using UnityEngine.UI;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestModMenu.UI;

/// <summary>
/// UGUI 构建工具集（中英双语注释）。
/// 该游戏是 IL2CPP 裁剪构建，IMGUI(OnGUI) 渲染在游戏 UGUI 主菜单**之下**（被盖住/点击被拦截），
/// 因此任何模组 UI 都必须用 UGUI Canvas 实现。本工具把 UGUI 常见构建封装成零依赖的静态方法，
/// 并自动接入 <see cref="NativeUi"/> 的本地化字体 + 文案（游戏侧已注册桥接时）。
/// 用法：Canvas 根 → Place/Stretch 摆位 → MakeText/MakeButton/MakeImage 构建子控件。
/// </summary>
public static class UiKit
{
    // ---------------- 画布 ----------------

    /// <summary>创建全屏 ScreenSpaceOverlay 画布（CanvasScaler + GraphicRaycaster）。
    /// sortingOrder 默认 32765（盖过游戏 UGUI；错开 32767=游戏虚拟光标 / 32766=联机菜单）；
    /// dontDestroy 默认 true（跨场景常驻）。</summary>
    public static Canvas CreateCanvas(string name, int sortingOrder = 32765, bool dontDestroy = true)
    {
        var go = new GameObject(name);
        if (dontDestroy) UnityEngine.Object.DontDestroyOnLoad(go);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        go.AddComponent<GraphicRaycaster>();
        return canvas;
    }

    /// <summary>全屏半透明射线拦截层（屏蔽下方游戏 UI / 3D 物品点击穿透）。返回其 RectTransform。</summary>
    public static RectTransform MakeBlocker(Transform parent, Color color)
    {
        var rt = MakeRect("Blocker", parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f));
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color; // 全透明 + raycastTarget=true 拦截射线
        rt.SetAsFirstSibling(); // 置于最底层，按钮/面板在上层仍可点击
        return rt;
    }

    // ---------------- Rect 布局 ----------------

    /// <summary>创建 RectTransform，锚点 anchorMin/Max、pivot、offsetMin/Max 全自定义。</summary>
    public static RectTransform MakeRect(string name, Transform parent,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Vector2 pivot)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
        return rt;
    }

    /// <summary>填充父容器（anchorMin/Max=0..1，offset 全 0）。</summary>
    public static RectTransform MakeRectFill(string name, Transform parent)
        => MakeRect(name, parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f));

    /// <summary>左上角锚点、手动摆位（x 向右 / y 向下为正）。</summary>
    public static RectTransform Place(string name, Transform parent, float x, float y, float w, float h)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    // ---------------- 控件 ----------------

    /// <summary>背景色块（Image），默认不挡射线。</summary>
    public static Image MakeImage(Transform parent, float x, float y, float w, float h, Color color, bool raycast = false)
    {
        var rt = Place("img", parent, x, y, w, h);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = raycast;
        return img;
    }

    /// <summary>原生观感面板（9-slice Sliced）：用原生素材 sprite 构建圆角/边框/贴图面板
    /// （sprite.border 决定四角不拉伸区域，中间拉伸）。sprite 为 null 时退回纯色。
    /// ⚠️ v1 覆盖 UI 阶段一律传 null（原生素材**保留但不激活**，见 docs/MOD_MENU.md §5.3）。</summary>
    public static Image MakePanel(Transform parent, float x, float y, float w, float h, Sprite sprite, Color tint, bool raycast = false)
    {
        var rt = Place("panel", parent, x, y, w, h);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sprite;
        img.color = tint;
        img.raycastTarget = raycast;
        if (sprite != null)
        {
            img.type = Image.Type.Sliced;
            // 9-slice：四角固定、中间拉伸。pixelsPerUnitMultiplier 调四角随尺寸缩放的比例
            // （按 sprite.border 非零才走 Sliced）。
            try { img.pixelsPerUnitMultiplier = 1f; } catch { }
        }
        return img;
    }

    /// <summary>文本（左上角摆位），自动应用本地化字体。</summary>
    public static TextMeshProUGUI MakeText(Transform parent, string text,
        float x, float y, float w, float h, int size, Color color, TextAlignmentOptions align)
    {
        var rt = Place("txt", parent, x, y, w, h);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.raycastTarget = false;
        EnsureFont(t);
        return t;
    }

    /// <summary>填满父容器的文本（按钮文字等用）。</summary>
    public static TextMeshProUGUI MakeTextFill(Transform parent, string text, int size, TextAlignmentOptions align)
    {
        var rt = MakeRectFill("txt", parent);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = size;
        t.color = Color.white;
        t.alignment = align;
        t.raycastTarget = false;
        EnsureFont(t);
        return t;
    }

    /// <summary>按钮（背景 + 居中文字 + onClick），自动本地化字体。
    /// bgSprite 提供时用 9-slice Sliced 原生观感（四角不拉伸），否则纯色。
    /// ⚠️ 字号固定 + 强制关 autoSizing（历史结论：autoSizing 是"字号忽大忽小"根因）。</summary>
    public static Button MakeButton(Transform parent, string text,
        float x, float y, float w, float h, Action onClick,
        Color? bgColor = null, Color? textColor = null, Sprite bgSprite = null)
    {
        var go = new GameObject("btn");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x + w / 2f, -(y + h / 2f));
        rt.sizeDelta = new Vector2(w, h);

        var img = go.AddComponent<Image>();
        if (bgSprite != null)
        {
            img.sprite = bgSprite;
            img.type = Image.Type.Sliced;
            try { img.pixelsPerUnitMultiplier = 1f; } catch { }
        }
        img.color = bgColor ?? new Color(0.20f, 0.24f, 0.30f, 1f);

        var btn = go.AddComponent<Button>();
        var txt = MakeTextFill(go.transform, text, 14, TextAlignmentOptions.Center);
        txt.color = textColor ?? Color.white;
        btn.onClick.AddListener(onClick);
        return btn;
    }

    /// <summary>可点击输入框（点击切换输入态，显示当前文本 + 光标）。
    /// 注意：这只唤起"点击回调"，不接管输入法；真正唤起 IME 需用 <see cref="MakeInputField"/>。</summary>
    public static Button MakeInputBox(Transform parent, string text, string placeholder,
        float x, float y, float w, float h, Action onTap, bool showCursor)
    {
        var go = new GameObject("input");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x + w / 2f, -(y + h / 2f));
        rt.sizeDelta = new Vector2(w, h);

        var img = go.AddComponent<Image>();
        img.color = new Color(0.10f, 0.12f, 0.16f, 1f);
        var btn = go.AddComponent<Button>();

        // 聚焦框：空内容不显示 placeholder（点击即清空），只显示光标；非聚焦空内容才显示占位提示。
        var shown = text.Length > 0 ? text : (showCursor ? "" : placeholder);
        if (showCursor) shown += "|";
        var txt = MakeTextFill(go.transform, shown, 15, TextAlignmentOptions.Left);
        txt.richText = true;
        txt.color = new Color(0.92f, 0.95f, 1f);
        txt.rectTransform.offsetMin = new Vector2(8f, 0f);
        txt.rectTransform.offsetMax = Vector2.zero;

        btn.onClick.AddListener(onTap);
        return btn;
    }

    /// <summary>真实 TMP_InputField（聚焦会唤起系统 IME，中文输入必需；游戏 InputFieldHelper 同款）。
    /// 返回输入框；placeholder 由其子文本承担。⚠️ 需游戏 EventSystem/InputSystemUIInputModule 正常，才弹出 IME。</summary>
    public static TMP_InputField MakeInputField(Transform parent, string text,
        float x, float y, float w, float h, int fontSize, TMPro.TextOverflowModes overflow = TMPro.TextOverflowModes.Overflow)
    {
        var rt = Place("input", parent, x, y, w, h);

        // 背景
        var img = rt.gameObject.AddComponent<Image>();
        img.color = new Color(0.10f, 0.12f, 0.16f, 1f);

        var input = rt.gameObject.AddComponent<TMP_InputField>();

        // 文本子物体
        var txtRt = MakeRectFill("Text", rt);
        var txt = txtRt.gameObject.AddComponent<TextMeshProUGUI>();
        txt.fontSize = fontSize;
        txt.color = new Color(0.92f, 0.95f, 1f);
        txt.raycastTarget = false;
        txt.overflowMode = overflow;
        EnsureFont(txt);

        // 占位符子物体
        var phRt = MakeRectFill("Placeholder", rt);
        var ph = phRt.gameObject.AddComponent<TextMeshProUGUI>();
        ph.fontSize = fontSize;
        ph.color = new Color(0.45f, 0.50f, 0.60f);
        ph.raycastTarget = false;
        EnsureFont(ph);

        input.textComponent = txt;
        input.placeholder = ph;
        input.text = text;
        input.lineType = TMP_InputField.LineType.SingleLine;
        return input;
    }

    // ---------------- 本地化 / 字体 ----------------

    /// <summary>取本地化文案：优先 NativeUi 桥接，未注册时返回 key 本身。</summary>
    public static string L(string key) => NativeUi.Localise(key);

    /// <summary>应用本地化字体到 TMP 文本：优先 NativeUi 桥接的 GetLocalisedFont，
    /// 失败/未注册时运行时扫描含字形字体（中文避免方框 □）兜底。</summary>
    public static void EnsureFont(TextMeshProUGUI t)
    {
        try
        {
            // 1) 已注册桥接：按当前语言取本地化字体（中文需要 fallback 字体）
            var f = NativeUi.Available ? NativeUi.GetLocalisedFont(TMP_Settings.defaultFontAsset) : null;
            if (f != null && f.name != (TMP_Settings.defaultFontAsset?.name ?? "")) { t.font = f; return; }

            // 2) 兜底：运行时找所有 TMP_FontAsset，挑非默认的（含中文字形）
            try
            {
                string defName = TMP_Settings.defaultFontAsset != null ? TMP_Settings.defaultFontAsset.name ?? "" : "";
                var all = UnityEngine.Object.FindObjectsOfType<TMP_FontAsset>(true);
                if (all != null)
                {
                    foreach (var cand in all)
                    {
                        if (cand == null) continue;
                        string fn = "";
                        try { fn = cand.name ?? ""; } catch { }
                        if (fn.Length > 0 && fn != defName && fn.IndexOf("Default", StringComparison.OrdinalIgnoreCase) < 0)
                        { t.font = cand; return; }
                    }
                }
            }
            catch { }
            if (TMP_Settings.defaultFontAsset != null) t.font = TMP_Settings.defaultFontAsset;
        }
        catch { }
    }
}
