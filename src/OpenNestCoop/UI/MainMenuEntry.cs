using System;
using UnityEngine;
using UnityEngine.UI;
#if !MELONLOADER
using TMPro;
#else
using TMPro = Il2CppTMPro;
#endif
using OpenNestCore.Logging;

namespace OpenNestCoop.UI;

/// <summary>主菜单联机入口（2026-08-23）：patch 原生主菜单程序化脚本
/// （MainMenuStateRelay.HandleMainMenuLoaded，注册在 Patches/HarmonyPatches.cs），
/// 原生主菜单加载完成后，在主菜单 UI 上注入"联机"入口按钮 → 打开 OpenNestCoop 联机菜单。
/// 首次注入会打印主菜单 Canvas/原生按钮结构（探测日志），便于后续调整注入位置/样式。</summary>
public static class MainMenuEntry
{
    private static bool _injected;

    /// <summary>Harmony postfix 回调：MainMenuStateRelay.HandleMainMenuLoaded（原生主菜单加载完成）。</summary>
    public static void OnMainMenuLoaded(string sceneName)
    {
        if (_injected) return;
        _injected = true;
        try
        {
            CoopLog.Info("MainMenuEntry", () => $"主菜单加载完成 scene='{sceneName}' → 注入联机入口");
            CoopLoc.Refresh(); // 注入前刷新语言（CoopLoc 只在 CoopUIManager.Rebuild 刷新，主菜单注入时可能未刷新→按钮文字恒中文）
            ProbeMainMenu();
            var canvas = FindMainMenuCanvas();
            if (canvas == null) { CoopLog.Warn("MainMenuEntry", () => "未找到主菜单 Canvas，跳过注入"); return; }
            CreateEntryButton(canvas.transform);
        }
        catch (Exception ex) { CoopLog.Warn("MainMenuEntry", () => $"OnMainMenuLoaded: {ex.Message}"); }
    }

    /// <summary>探测：打印所有激活根 Canvas + 主菜单 Canvas 的按钮布局（定位注入位置，首次日志）。</summary>
    private static void ProbeMainMenu()
    {
        try
        {
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
            foreach (var c in canvases)
            {
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                int btnCount = 0;
                string firstBtn = "";
                foreach (var b in c.GetComponentsInChildren<Button>(true))
                {
                    if (b == null) continue;
                    btnCount++;
                    if (firstBtn.Length == 0) firstBtn = b.name + " @" + PathOf(b.transform);
                }
                CoopLog.Debug("MainMenuEntry", () =>
                    $"Canvas '{c.name}' root='{c.transform.root.name}' sort={c.sortingOrder} mode={c.renderMode} overlay={c.isRootCanvas} raycaster={(c.GetComponent<GraphicRaycaster>() != null)} btns={btnCount}" +
                    (firstBtn.Length > 0 ? $" | 首个按钮: {firstBtn}" : ""));
                // 主菜单候选（按钮多的）打印按钮布局，供精确定位注入位置
                if (btnCount >= 8)
                {
                    int n = 0;
                    foreach (var b in c.GetComponentsInChildren<Button>(true))
                    {
                        if (b == null) break;
                        var rt = b.GetComponent<RectTransform>();
                        string pos = rt != null ? rt.anchoredPosition.ToString("0") : "?";
                        string sz = rt != null ? rt.sizeDelta.ToString("0") : "?";
                        CoopLog.Debug("MainMenuEntry", () => $"  btn[{n}] '{b.name}' pos={pos} size={sz} parent='{PathOf(b.transform.parent)}'");
                        n++;
                    }
                }
            }
        }
        catch (Exception ex) { CoopLog.Warn("MainMenuEntry", () => $"probe: {ex.Message}"); }
    }

    private static string PathOf(Transform t)
    {
        try
        {
            string s = t.name;
            var p = t.parent;
            while (p != null) { s = p.name + "/" + s; p = p.parent; }
            return s;
        }
        catch { return t != null ? t.name : "?"; }
    }

    /// <summary>找主菜单 UI Canvas：排除自建 OpenNestCoop_UI / 虚拟光标层（Cursor Canvas）；
    /// 主菜单 UI 是 WorldSpace Canvas（Barbet 场景，含全部主菜单按钮）→ 优先按钮多 + WorldSpace。</summary>
    private static Canvas FindMainMenuCanvas()
    {
        Canvas best = null;
        int bestScore = -1;
        try
        {
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
            foreach (var c in canvases)
            {
                if (c == null || c.transform.root == null) continue;
                string rootName = c.transform.root.name;
                if (rootName.StartsWith("OpenNestCoop", StringComparison.OrdinalIgnoreCase)) continue; // 自建 UI
                if (rootName.IndexOf("Cursor", StringComparison.OrdinalIgnoreCase) >= 0) continue;     // 虚拟光标层
                if (!c.gameObject.activeInHierarchy) continue;
                if (c.GetComponent<GraphicRaycaster>() == null) continue;
                int btns = 0;
                foreach (var b in c.GetComponentsInChildren<Button>(true)) if (b != null) btns++;
                int score = btns * 100 + (c.renderMode == RenderMode.WorldSpace ? 10 : 0);
                if (score > bestScore) { bestScore = score; best = c; }
            }
        }
        catch (Exception ex) { CoopLog.Warn("MainMenuEntry", () => $"find canvas: {ex.Message}"); }
        if (best != null) CoopLog.Info("MainMenuEntry", () => $"主菜单 Canvas 选定: '{best.name}' root='{best.transform.root.name}' sort={best.sortingOrder} mode={best.renderMode}");
        return best;
    }

    /// <summary>在主菜单注入"联机"入口：遍历**所有**"应用"按钮（SGButtonPrimaryUGUI (apply)，设置菜单可能多处实例），
    /// 在每个右边注入**自建干净按钮**（不克隆——克隆会带链接脚本/onClick 持久绑定），抄应用按钮样式
    /// （Castile 背景 + Button 过渡 + 原生字体/颜色 + 同尺寸），点击打开联机菜单。</summary>
    private static void CreateEntryButton(Transform canvas)
    {
        try
        {
            // 1) 全场景所有设置菜单 Apply 按钮右侧注入（Barbet 主菜单 + 其他实例）
            int applyCount = 0;
            var allBtns = UnityEngine.Object.FindObjectsOfType<Button>(true);
            foreach (var b in allBtns)
            {
                if (b == null) continue;
                if (b.name.IndexOf("Apply", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (HasEntryChild(b.transform.parent)) continue; // 已注入过
                InjectEntry(b);
                applyCount++;
            }
            // 2) 全场景所有 ESC Menu Buttons 容器顶部注入（Barbet 主菜单 + Main Camera 暂停菜单，多实例）
            //    ⚠️ 环境里有 OpenNestUIKit 时**不自己做**：ESC 入口由 UIKit 统一注入（用户要求：注入归 UIKit 一家做，
            //       免得两个模组各自往原生容器里塞按钮、各自重排、互相打架）。
            int escCount = 0;
            if (UIKitIntegration.EscEntryOwnedByUIKit)
            {
                CoopLog.Info("MainMenuEntry", () => "ESC 入口已交给 OpenNestUIKit 注入 → 本模组跳过 ESC 注入");
            }
            else
            {
                var allTf = UnityEngine.Object.FindObjectsOfType<Transform>(true);
                foreach (var t in allTf)
                {
                    if (t == null || t.name != "ESC Menu Buttons") continue;
                    if (HasEntryChild(t)) continue; // 已注入过
                    InjectEscEntry(t);
                    escCount++;
                }
            }
            CoopLog.Info("MainMenuEntry", () => $"注入完成: Apply 右侧 {applyCount} 处, ESC 菜单 {escCount} 个");
        }
        catch (Exception ex) { CoopLog.Warn("MainMenuEntry", () => $"create entry: {ex.Message}"); }
    }

    /// <summary>
    /// 入口点击 → 打开联机菜单。
    /// 环境里有 OpenNestUIKit → 打开**它**的联机页（菜单由 UIKit 渲染，用户要求：菜单 UI 统一走 UIKit）；
    /// 没有 UIKit（或它还没就绪）→ 回退本模组自带的大厅窗口。
    /// </summary>
    private static void OpenCoopMenu()
    {
        try
        {
            if (UIKitIntegration.TryToggleInUIKit()) return;
            CoopUIManager.ToggleMenu();
        }
        catch (Exception ex) { CoopLog.Warn("MainMenuEntry", () => $"OpenCoopMenu: {ex.Message}"); }
    }

    /// <summary>父容器下是否已有联机入口（防重复注入）。</summary>
    private static bool HasEntryChild(Transform parent)
    {
        if (parent == null) return false;
        try
        {
            var children = parent.GetComponentsInChildren<Transform>(true);
            foreach (var c in children)
            {
                if (c != null && (c.name == "OpenNest_CoopEntry" || c.name == "OpenNest_CoopEsc")) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>在 ESC 菜单里注入"联机"入口：顶部放自建干净按钮（抄 ESC 按钮样式，无链接脚本），SetActive(true)，
    /// 并把现有按钮下移 + 间距压缩（固定紧凑布局，适配多个 ESC 菜单实例）。</summary>
    private static void InjectEscEntry(Transform esc)
    {
        try
        {
            var escRt = esc.GetComponent<RectTransform>();
            if (escRt != null)
                CoopLog.Debug("MainMenuEntry", () => $"ESC 容器 root='{esc.transform.root.name}' pos={escRt.anchoredPosition} size={escRt.sizeDelta} mask={(esc.GetComponent<RectMask2D>() != null)} layoutV={(esc.GetComponent<VerticalLayoutGroup>() != null)}");
            // 模板 = ESC 菜单"设置"按钮（样式/尺寸/字体参考）：**按名字精确选**（OpenSettingsBtn → 含 Settings → 第一个）。
            // ⚠️ 用户反馈过"抄的可能不是设置按钮导致尺寸不对"——GetComponentsInChildren 返回顺序不可依赖，
            //    必须按名字精确定位，并用 Info 打印所选模板名便于核对。
            Button template = null;
            foreach (var b in esc.GetComponentsInChildren<Button>(true))
            {
                if (b == null) continue;
                if (b.name == "OpenSettingsBtn") { template = b; break; }
            }
            if (template == null)
                foreach (var b in esc.GetComponentsInChildren<Button>(true))
                {
                    if (b == null) continue;
                    if (b.name.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0) { template = b; break; }
                }
            if (template == null)
                foreach (var b in esc.GetComponentsInChildren<Button>(true)) { if (b != null) { template = b; break; } }
            if (template == null) { CoopLog.Warn("MainMenuEntry", () => "ESC 无按钮模板"); return; }
            CoopLog.Info("MainMenuEntry", () => $"ESC 模板按钮: '{template.name}'"); // 模板 = 设置按钮（外观 + 文字，字号 25——用户确认 25 正确）
            // 完整探测：ESC 菜单所有按钮 name+y（定位 y=220 的按钮/标题，布局依据）
            try
            {
                var allB = esc.GetComponentsInChildren<Button>(true);
                foreach (var b in allB)
                {
                    if (b == null) continue;
                    var r = b.GetComponent<RectTransform>();
                    CoopLog.Debug("MainMenuEntry", () => $"  ESC按钮 '{b.name}' y={(r != null ? r.anchoredPosition.y.ToString("0.#") : "?")} size={(r != null ? r.sizeDelta.ToString("0.#") : "?")}");
                }
            }
            catch { }

            // 自建按钮（抄 ESC 按钮样式，无链接）
            var go = new GameObject("OpenNest_CoopEsc");
            go.transform.SetParent(esc, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchoredPosition = new Vector2(0f, 220f); // 顶部（Wishlist y=176 之上）
            rt.sizeDelta = new Vector2(250f, 40f);       // ESC 按钮尺寸
            go.SetActive(true);

            // ⚠ 2026-09-13（用户：“Settings 里面右下角还有一个 CoopMenu 的注入，这个注入现在也有白色叠层的问题”）：
            //   模板根节点**没有** Graphic 时（本游戏原生按钮就是：底图在子物体 `Bg` 上），我们**不要**自己再挂一张白底图
            //   —— 那层原生没有，看起来就是“白色叠了个框”。这时底图/点击目标都由镜像出来的那层负责（见 CopyTintLikeNative）。
            bool tplRootGraphic = false;
            try { tplRootGraphic = template != null && template.GetComponent<Graphic>() != null; } catch { }
            Image img = null;
            if (tplRootGraphic || template == null)
            {
                img = go.AddComponent<Image>();
                img.color = new Color(0.16f, 0.20f, 0.26f, 0.96f);
            }
            var btn = go.AddComponent<Button>();
            btn.onClick.AddListener(new Action(OpenCoopMenu));

            // 抄模板主按钮 Image（跳过阴影）——**仅当我们自己有根底图时**
            try
            {
                UnityEngine.UI.Image tImg = null;
                var imgs = template.GetComponentsInChildren<UnityEngine.UI.Image>(true);
                foreach (var im in imgs)
                {
                    if (im == null) continue;
                    if (tImg == null) tImg = im;
                    var sn = im.sprite != null ? im.sprite.name : "";
                    if (sn.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) < 0) { tImg = im; break; }
                }
                if (img != null && tImg != null && tImg.sprite != null)
                {
                    img.sprite = tImg.sprite; img.type = tImg.type; img.color = tImg.color;
                    try { img.pixelsPerUnitMultiplier = tImg.pixelsPerUnitMultiplier; img.fillCenter = tImg.fillCenter; } catch { }
                }
                CoopLog.Debug("MainMenuEntry", () => $"ESC 模板 '{template.name}' 根底图={tplRootGraphic} sprite={(tImg != null && tImg.sprite != null ? tImg.sprite.name : "null")} btnScale={template.transform.localScale} rootScale={esc.transform.root.localScale}");
            }
            catch { }
            // 抄模板 Button 过渡（悬停/点击变色）——
            // ⚠️ 2026-09-13 改为**颜色原样照抄**（用户要求：“颜色改为抄颜色而不是瞎改成白的”）：
            //    以前发现原生 `normalColor` 是纯黑，就把整块颜色换成白底 tint 了事 → 底色跟原生不一样。
            //    正确做法：`colors`/`transition` 照抄，并像原生那样把 tint 打到**子节点**上（见 CopyTintLikeNative），
            //    自己身上的底图**不吃 tint** → 静止时与原生纸面一模一样，悬停也还有原生那点反馈。
            CopyTintLikeNative(template, btn, img);
            // 文字（抄模板字体/字号/颜色——与"设置"按钮完全一致）。
            // ⚠️ 模板字体优先：ApplySharedFont 会用本地化字体覆盖 font，若与模板字体不同则字形/大小不一致。
            // 只有模板字体不可用（null）才走本地化字体兜底。
            var txtGo = new GameObject("Text");
            txtGo.transform.SetParent(go.transform, false);
            var trt = txtGo.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            var txt = txtGo.AddComponent<TextMeshProUGUI>();
            txt.text = CoopLoc.MenuToggle; // 语言键（联机菜单/Coop Menu），与左上角开关同键
            txt.alignment = TextAlignmentOptions.Center;
            txt.color = Color.white;
            txt.raycastTarget = false;
            txt.fontSize = 18;
            bool haveTemplateFont = false;
            try
            {
                var tTxts = template.GetComponentsInChildren<TMPro.TMP_Text>(true);
                foreach (var tt in tTxts)
                {
                    if (tt == null) continue;
                    var ttRt = tt.rectTransform;
                    string scale = ttRt != null ? ttRt.localScale.ToString("0.###") : "?";
                    CoopLog.Debug("MainMenuEntry", () => $"  模板文字 '{tt.name}' font='{(tt.font != null ? tt.font.name : "null")}' size={tt.fontSize} autoSizing={tt.enableAutoSizing} scale={scale} color={tt.color}");
                }
                if (tTxts.Length > 0)
                {
                    var tTxt = tTxts[0];
                    try { if (tTxt.font != null) { txt.font = tTxt.font; haveTemplateFont = true; } } catch { }
                    // 字号固定 20 + 强制关 autoSizing（根因：抄某按钮 autoSizing=true 会被按钮尺寸缩小文字→"过小"；
                    // 25 又偏大。各按钮字号/autoSizing 不统一 → 固定最稳，不抄任何按钮）
                    txt.fontSize = 20f;
                    txt.enableAutoSizing = false;
                    try { txt.color = tTxt.color; } catch { }
                    try { txt.fontStyle = tTxt.fontStyle; } catch { } // 抄加粗/斜体（原生按钮文字是加粗的）
                    // ⚠️ 抄文字布局（按钮内边距 margin）：模板文字在按钮内有内边距，我们 offset 全 0
                    // 会文字贴满按钮 → 视觉显大。抄模板文字的 offset/anchor 保持一致的观感。
                    try
                    {
                        var tTxtRt = tTxt.rectTransform;
                        if (tTxtRt != null)
                        {
                            CoopLog.Debug("MainMenuEntry", () => $"  模板文字 offsetMin={tTxtRt.offsetMin} offsetMax={tTxtRt.offsetMax} anchorMin={tTxtRt.anchorMin} anchorMax={tTxtRt.anchorMax}");
                            trt.anchorMin = tTxtRt.anchorMin;
                            trt.anchorMax = tTxtRt.anchorMax;
                            // 内边距完全照抄模板（水平 20 / 垂直 10）：文字布局与原生一致
                            trt.offsetMin = tTxtRt.offsetMin;
                            trt.offsetMax = tTxtRt.offsetMax;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            if (!haveTemplateFont) CoopUIManager.ApplySharedFont(txt); // 模板字体不可用才兜底
            CoopLog.Debug("MainMenuEntry", () => $"  联机文字 font='{(txt.font != null ? txt.font.name : "null")}' size={txt.fontSize} haveTemplateFont={haveTemplateFont}");

            // 布局：整个 ESC 菜单**统一间距 38**（从最高原生按钮 Wishlist 向下连续排列，联机插在"设置"后）。
            // ⚠️ topY 必须排除联机按钮（它初始 y 高，否则误算贴标题）。
            const float escGap = 38f;
            const float escBtnH = 38f; // 按钮高 38 接近原生 40；间距 38 间隙 0，紧密排列像原生
            float topY = 0f;
            try
            {
                foreach (var b in esc.GetComponentsInChildren<Button>(true))
                {
                    if (b == null || b.name == "OpenNest_CoopEsc") continue;
                    var r = b.GetComponent<RectTransform>();
                    if (r == null) continue;
                    if (r.anchoredPosition.y > topY) topY = r.anchoredPosition.y; // Wishlist 顶部
                }
            }
            catch { }
            // 单遍重排：联机**真正占一格**（插在"设置"按钮后），且联机插入后**所有**后续原生按钮 slot +1 让位
            //（只让第一个/不整体让位都会造成两按钮 y 相同 → 重叠/间距错乱，先后踩过两版）。
            int idx = 0; int settingsIdx = -1; bool entryPlaced = false;
            float entryY = 0f;
            foreach (var b in esc.GetComponentsInChildren<Button>(true))
            {
                if (b == null || b.name == "OpenNest_CoopEsc") continue;
                var r = b.GetComponent<RectTransform>();
                if (r == null) continue;
                if (b == template) settingsIdx = idx;
                if (settingsIdx >= 0 && idx > settingsIdx && !entryPlaced)
                {
                    // 联机插在"设置"按钮后一格
                    entryY = topY - (settingsIdx + 1) * escGap;
                    rt.anchoredPosition = new Vector2(0f, entryY);
                    rt.sizeDelta = new Vector2(250f, escBtnH);
                    entryPlaced = true;
                }
                int slot = entryPlaced ? idx + 1 : idx;
                r.anchoredPosition = new Vector2(r.anchoredPosition.x, topY - slot * escGap);
                r.sizeDelta = new Vector2(250f, escBtnH);
                idx++;
            }
            CoopLog.Info("MainMenuEntry", () => $"ESC 联机入口: 统一间距={escGap} 顶部={topY:0.#} 设置Idx={settingsIdx} 联机y={entryY:0.#} 字号={txt.fontSize} 共{idx + 1}按钮");
            // 重排后打印实际布局（验证所有按钮均匀，避免再"只调一个"）
            try
            {
                foreach (var b in esc.GetComponentsInChildren<Button>(true))
                {
                    if (b == null) continue;
                    var r = b.GetComponent<RectTransform>();
                    CoopLog.Debug("MainMenuEntry", () => $"  [重排后] '{b.name}' y={(r != null ? r.anchoredPosition.y.ToString("0.#") : "?")}");
                }
            }
            catch { }
        }
        catch (Exception ex) { CoopLog.Warn("MainMenuEntry", () => $"inject esc: {ex.Message}"); }
    }

    /// <summary>在单个模板按钮右边注入一个联机入口按钮（自建 + 抄样式 + 同尺寸 + 抄文字颜色）。</summary>
    private static void InjectEntry(Button template)
    {
        try
        {
            var go = new GameObject("OpenNest_CoopEntry");
            var applyRt = template.GetComponent<RectTransform>();
            var p = template.transform.parent;
            Transform parentTf = p != null ? p : template.transform;
            Vector2 anchorMin = new Vector2(0f, 0.5f), anchorMax = new Vector2(0f, 0.5f);
            Vector2 pivot = new Vector2(0f, 0.5f);
            float targetX = 0f, targetY = 0f;
            Vector2 size = new Vector2(130f, 52f);
            if (applyRt != null)
            {
                anchorMin = applyRt.anchorMin; anchorMax = applyRt.anchorMax;
                targetX = applyRt.anchoredPosition.x + applyRt.sizeDelta.x / 2f + 12f; // 模板右边缘 + 间距
                targetY = applyRt.anchoredPosition.y;                                  // 垂直对齐
                size = applyRt.sizeDelta;                                               // 与模板同尺寸
            }
            go.transform.SetParent(parentTf, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = new Vector2(targetX, targetY);
            rt.sizeDelta = size;

            // ⚠ 2026-09-13（用户：“Settings 里面右下角还有一个 CoopMenu 的注入，这个注入现在也有白色叠层的问题”）：
            //   模板根节点**没有** Graphic 时（本游戏原生按钮就是：底图在子物体 `Bg` 上）**不要**自建根底图
            //   —— 那层原生没有，看起来就是“白色叠了个框”；这时的底图/点击目标都由镜像出来的那层负责。
            bool tplRootGraphic = false;
            try { tplRootGraphic = template.GetComponent<Graphic>() != null; } catch { }
            Image img = null;
            if (tplRootGraphic)
            {
                img = go.AddComponent<Image>();
                img.color = new Color(0.16f, 0.20f, 0.26f, 0.96f);
                // 抄模板主按钮 Image（跳过阴影 SUGShadowLite，找不到用第一个）
                try
                {
                    UnityEngine.UI.Image tImg = null;
                    var imgs = template.GetComponentsInChildren<UnityEngine.UI.Image>(true);
                    foreach (var im in imgs)
                    {
                        if (im == null) continue;
                        if (tImg == null) tImg = im;
                        var sn = im.sprite != null ? im.sprite.name : "";
                        if (sn.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) < 0) { tImg = im; break; }
                    }
                    if (tImg != null && tImg.sprite != null)
                    {
                        img.sprite = tImg.sprite;
                        img.type = tImg.type;
                        img.color = tImg.color;
                        try { img.pixelsPerUnitMultiplier = tImg.pixelsPerUnitMultiplier; img.fillCenter = tImg.fillCenter; } catch { }
                        CoopLog.Debug("MainMenuEntry", () => $"  模板 '{template.name}' 主sprite={tImg.sprite.name} mppu={tImg.pixelsPerUnitMultiplier} type={tImg.type}");
                    }
                }
                catch { }
            }
            var btn = go.AddComponent<Button>();
            btn.onClick.AddListener(new Action(OpenCoopMenu));

            // 抄模板 Button 过渡（悬停/点击变色）—— 颜色原样照抄 + 只镜像原生那**一层**可见底图
            // （用户：“颜色正常了，不需要兜底换色，原生只有一种背景”；模板根没底图时我们也不自建）
            Image bgMade = CopyTintLikeNative(template, btn, img);
            _ = bgMade;

            // 文字（抄模板字体/字号/颜色）
            var txtGo = new GameObject("Text");
            txtGo.transform.SetParent(go.transform, false);
            var trt = txtGo.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
            var txt = txtGo.AddComponent<TextMeshProUGUI>();
            txt.text = CoopLoc.MenuToggle; // 语言键（联机菜单/Coop Menu），与左上角开关同键
            txt.alignment = TextAlignmentOptions.Center;
            txt.color = Color.white;
            txt.raycastTarget = false;
            txt.fontSize = 18;
            try
            {
                var tTxt = template.GetComponentInChildren<TMPro.TMP_Text>(true);
                if (tTxt != null)
                {
                    try { txt.font = tTxt.font; } catch { }
                    txt.fontSize = tTxt.fontSize;
                    try { txt.color = tTxt.color; } catch { }  // 抄文字颜色
                    try { txt.fontStyle = tTxt.fontStyle; } catch { } // 抄加粗/斜体样式
                }
            }
            catch { }
            CoopUIManager.ApplySharedFont(txt);

            CoopLog.Info("MainMenuEntry", () => $"联机入口已注入 '{template.name}' 右侧 (size={size})");
        }
        catch (Exception ex) { CoopLog.Warn("MainMenuEntry", () => $"inject: {ex.Message}"); }
    }

    private static float _lastPoll;
    /// <summary>诊断轮询（每帧被 IronNestNativeUi.Poll 调用，内部节流 2s）：当 ESC 菜单激活时，
    /// 打印联机入口按钮的实际状态（active/位置）和父级裁剪者，定位"ESC 菜单看不到联机按钮"根因。</summary>
    public static void Poll()
    {
        try
        {
            if (Time.unscaledTime - _lastPoll < 2f) return;
            _lastPoll = Time.unscaledTime;
            var all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            foreach (var t in all)
            {
                if (t == null || t.name != "ESC Menu Buttons") continue;
                if (!t.gameObject.activeInHierarchy) continue; // 只诊断激活的 ESC 菜单
                Transform entry = null;
                var children = t.GetComponentsInChildren<Transform>(true);
                foreach (var c in children)
                {
                    if (c != null && c.name == "OpenNest_CoopEsc") { entry = c; break; }
                }
                string entryState = entry != null
                    ? $"active={entry.gameObject.activeInHierarchy} pos={((RectTransform)entry).anchoredPosition} size={((RectTransform)entry).sizeDelta}"
                    : "未注入";
                string clip = "无";
                var p = t.parent;
                while (p != null)
                {
                    if (p.GetComponent<RectMask2D>() != null || p.GetComponent<ScrollRect>() != null) { clip = p.name; break; }
                    p = p.parent;
                }
                CoopLog.Debug("MainMenuEntry", () => $"ESC 菜单激活: 联机={entryState} 裁剪者={clip} 容器root='{t.transform.root.name}'");
                return;
            }
        }
        catch { }
    }

    /// <summary>按名字关键词找按钮（如 "Apply"），用于选"应用"按钮做样式模板。</summary>
    private static Button FindButtonByKeyword(Transform root, string keyword)
    {
        try
        {
            var btns = root.GetComponentsInChildren<Button>(true);
            foreach (var b in btns)
            {
                if (b == null) continue;
                if (b.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return b;
            }
        }
        catch { }
        return null;
    }

    private static Transform FindChildRecursive(Transform root, string name)
    {
        try
        {
            // ⚠️ IL2CPP 下 foreach(Transform) 枚举子物体不可靠（项目踩过坑）→ 用 GetComponentsInChildren<Transform>（数组）匹配
            var all = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in all)
            {
                if (t != null && t.name == name) return t;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 照抄模板按钮的过渡（悬停/点击变色）：**颜色原样抄，不改白**，并像原生那样只镜像**一层**可见底图。
    ///
    /// 背景（2026-09-13）：原生按钮的 `colors.normalColor` 是**纯黑**，且 tint 目标是一个**子物体** `Bg`；
    /// 原生模板根节点**没有底图**（玩家看到的就是 `Bg` 这一层）。
    /// 我们如果自己再给根挂一张白底图，就多出一层原生没有的“白色叠层”（用户报过两次：ESC 行、Settings 里的 CoopMenu）。
    /// ⇒ 本方法负责：① `colors`/`transition` 原样照抄；② 镜像**最上面那层启用中的非阴影子底图**，
    ///  且在“我们自己没有根底图”时把它当**点击射线目标**（否则按钮点不了）。
    ///
    /// 返回：我们实际使用的底图（可能是调用方自己的 `img`，也可能是镜像出来的那一层）。
    /// 与 `OpenNestUIKit.Native.NativeMenuStyler.CopyTint` 同口径（此处不引用 UIKit 程序集：本模组要能单独跑）。
    /// </summary>
    private static Image CopyTintLikeNative(Button template, Button btn, Image img)
    {
        if (btn == null || template == null) return img;
        try { btn.transition = template.transition; } catch { }
        try { btn.colors = template.colors; } catch { }
        try
        {
            var tTg = template.targetGraphic;
            if (tTg == null) { btn.targetGraphic = img; return img; }                       // 原生没 tint 目标 → 保持我们的
            if (tTg.transform == template.transform) { btn.targetGraphic = img; return img; }  // 原生就打在自身底图上 → 照做

            // 只镜像**最上面那层**启用中的非阴影子底图（= 玩家真正看到的那层）
            Image top = null;
            var srcs = template.GetComponentsInChildren<Image>(true);
            for (int i = 0; srcs != null && i < srcs.Length; i++)
            {
                var s = srcs[i];
                if (s == null) continue;
                if (s.transform == template.transform) continue;
                if (!s.enabled) continue;
                try { if (!s.gameObject.activeSelf) continue; } catch { }
                string sn = ""; try { sn = s.sprite != null ? s.sprite.name : ""; } catch { }
                if (sn.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                top = s;
            }
            if (top == null) { btn.targetGraphic = img; return img; }

            bool weHaveRoot = img != null;
            var made = MirrorImage(top, btn.transform, raycast: !weHaveRoot);
            try
            {
                // ⚠ 2026-09-13 修正（用户：“Settings 右下角 Coop Menu 变成白色背景了”）：
                //   上一版在“我们自己没有根底图”时把 `targetGraphic` 设成了 **null**（想的是“别把抄来的颜色落到镜像层上”），
                //   结果**tint 没生效** → 镜像出来的 `Bg` 保持白色（`Graphic.color`），而原生那一层是被
                //   ColorTint 染黑的 → 我们比原生白了一大截。
                //   与 UIKit 侧 `NativeMenuStyler.CopyTint` 同口径：**模板的 tint 目标是谁，就把我们的 targetGraphic 指到镜像出的那一层**。
                if (tTg is Image && ReferenceEquals(top, (Image)tTg)) btn.targetGraphic = made;
                else if (!weHaveRoot) btn.targetGraphic = made;      // 我们只有这一层底图 → 它就是 tint 目标（与原生同构）
                else btn.targetGraphic = img;
            }
            catch { }
            CoopLog.Debug("MainMenuEntry", () => $"底图照抄原生单层：镜像 '{made?.gameObject.name}'（sprite={(made != null && made.sprite != null ? made.sprite.name : "null")} 可点={!weHaveRoot}）");
            return made ?? img;
        }
        catch { }
        return img;
    }

    /// <summary>把模板里的一张底图逐项照抄成我们按钮下的子节点（同名、同 sprite/类型/倍率/颜色/矩形）。</summary>
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
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            var srt = src.rectTransform;
            if (srt != null)
            {
                rt.anchorMin = srt.anchorMin; rt.anchorMax = srt.anchorMax;
                rt.pivot = srt.pivot; rt.anchoredPosition = srt.anchoredPosition; rt.sizeDelta = srt.sizeDelta;
                rt.offsetMin = srt.offsetMin; rt.offsetMax = srt.offsetMax;
            }
            else { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero; }

            var bimg = go.AddComponent<Image>();
            bimg.sprite = src.sprite; bimg.type = src.type; bimg.color = src.color;
            try { bimg.pixelsPerUnitMultiplier = src.pixelsPerUnitMultiplier; bimg.fillCenter = src.fillCenter; } catch { }
            bimg.raycastTarget = raycast;
            return bimg;
        }
        catch { return null; }
    }
}
