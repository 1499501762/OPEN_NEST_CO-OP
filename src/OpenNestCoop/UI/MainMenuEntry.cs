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
            int escCount = 0;
            var allTf = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            foreach (var t in allTf)
            {
                if (t == null || t.name != "ESC Menu Buttons") continue;
                if (HasEntryChild(t)) continue; // 已注入过
                InjectEscEntry(t);
                escCount++;
            }
            CoopLog.Info("MainMenuEntry", () => $"注入完成: Apply 右侧 {applyCount} 处, ESC 菜单 {escCount} 个");
        }
        catch (Exception ex) { CoopLog.Warn("MainMenuEntry", () => $"create entry: {ex.Message}"); }
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

            var img = go.AddComponent<Image>();
            img.color = new Color(0.16f, 0.20f, 0.26f, 0.96f);
            var btn = go.AddComponent<Button>();
            btn.onClick.AddListener(new Action(() => { try { CoopUIManager.ToggleMenu(); } catch { } }));

            // 抄模板主按钮 Image（跳过阴影）
            UnityEngine.UI.Image tImg = null;
            try
            {
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
                    img.sprite = tImg.sprite; img.type = tImg.type; img.color = tImg.color;
                    try { img.pixelsPerUnitMultiplier = tImg.pixelsPerUnitMultiplier; img.fillCenter = tImg.fillCenter; } catch { }
                }
                CoopLog.Debug("MainMenuEntry", () => $"ESC 模板 '{template.name}' sprite={(tImg != null && tImg.sprite != null ? tImg.sprite.name : "null")} btnScale={template.transform.localScale} rootScale={esc.transform.root.localScale}");
            }
            catch { }
            try { btn.transition = template.transition; btn.colors = template.colors; } catch { }

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

            var img = go.AddComponent<Image>();
            img.color = new Color(0.16f, 0.20f, 0.26f, 0.96f);
            var btn = go.AddComponent<Button>();
            btn.onClick.AddListener(new Action(() => { try { CoopUIManager.ToggleMenu(); } catch { } }));

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
            // 抄模板 Button 过渡（悬停/点击变色）
            try { btn.transition = template.transition; btn.colors = template.colors; } catch { }

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
}
