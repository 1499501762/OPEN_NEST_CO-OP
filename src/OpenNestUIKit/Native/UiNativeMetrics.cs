using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestUIKit.Native;

/// <summary>
/// 原生 UI 度量诊断（**只读**）：把"游戏原生组件"的实际尺寸/字号/素材倍率量出来，
/// 与我们主题（<see cref="Theme.UiTheme"/>）的常量并排打印，用于回答
/// "我们的字/图标/背景是不是比原生大了一截，倍率差在哪"。
///
/// 为什么要有它：UGUI 里一个元素最终占多少屏幕像素 = 逻辑尺寸 × <c>Canvas.scaleFactor</c>；
/// 而 9-slice 素材的"边框粗细"还额外取决于 <c>canvas.referencePixelsPerUnit / sprite.pixelsPerUnit</c>
/// （以及 <c>Image.pixelsPerUnitMultiplier</c>）。只看逻辑尺寸对不上观感，必须把这几项一起比。
/// </summary>
public static class UiNativeMetrics
{
     /// <summary>
    /// 原生菜单**结构快照**：把每个 `ESC Menu Buttons` 容器的按钮按顺序列出（名字/是否我们注入的/尺寸/
    /// anchoredPosition/间距/字号/底图+ppuMul），并附注入器状态 —— 用来看“我们对原生菜单做了什么、
    /// 现在长什么样”（有没有错位/重叠/盖住原生条目）。
    /// </summary>
    public static string DumpMenuTree(int maxButtons = 24)
    {
        var sb = new StringBuilder();
        sb.Append("注入器状态：patched=").Append(NativeMenuInjector.IsPatched)
          .Append(" injected=").Append(NativeMenuInjector.IsInjected)
          .Append(" 条目=").Append(NativeMenuBridge.Count)
          .Append("；我们的前缀='").Append(NativeMenuInjector.OurPrefix).Append("'\n");

        try
        {
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            int found = 0;
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = "";
                try { nm = t.name ?? ""; } catch { }
                if (!string.Equals(nm, "ESC Menu Buttons", StringComparison.Ordinal)) continue;
                found++;
                var rt = RectOf(t);
                bool activeInHierarchy = false;
                try { activeInHierarchy = t.gameObject != null && t.gameObject.activeInHierarchy; } catch { }
                sb.Append($"\n■ 容器 '{Path(t)}' active={activeInHierarchy} 尺寸={(rt != null ? $"{rt.rect.width:0}x{rt.rect.height:0}" : "?")} 子节点数={t.childCount}\n");

                float prevY = float.NaN;
                int shown = 0;
                for (int k = 0; k < t.childCount && shown < maxButtons; k++)
                {
                    var ch = t.GetChild(k);
                    if (ch == null) continue;
                    var crt = RectOf(ch);
                    float y = crt != null ? crt.anchoredPosition.y : 0f;
                    float x = crt != null ? crt.anchoredPosition.x : 0f;
                    string gap = float.IsNaN(prevY) ? "" : $"　Δy={prevY - y:0.#}";
                    prevY = y;
                    string childName = "";
                    try { childName = ch.name ?? ""; } catch { }
                    bool ours = childName.StartsWith(NativeMenuInjector.OurPrefix, StringComparison.Ordinal);

                    float fs = 0f; string bg = "-";
                    string label = "-", tcol = "-", tsize = "-";
                    try
                    {
                        var txt = ch.GetComponentInChildren<TMP_Text>(true);
                        if (txt != null)
                        {
                            fs = SafeF(() => txt.fontSize);
                            if (ours)
                            {
                                try { label = txt.text ?? ""; } catch { }
                                try { var c = txt.color; tcol = $"{c.r:0.##},{c.g:0.##},{c.b:0.##},{c.a:0.##}"; } catch { }
                                try { var tr = txt.rectTransform; tsize = tr != null ? $"{tr.rect.width:0}x{tr.rect.height:0}" : "?"; } catch { }
                                try { tsize += txt.gameObject.activeInHierarchy ? "/on" : "/off"; } catch { }
                            }
                        }
                    }
                    catch { }
                    try
                    {
                        var imgs2 = ch.GetComponentsInChildren<Image>(true);
                        for (int q = 0; imgs2 != null && q < imgs2.Length; q++)
                        {
                            var im2 = imgs2[q];
                            if (im2 == null || im2.sprite == null) continue;
                            string sn2 = "";
                            try { sn2 = im2.sprite.name ?? ""; } catch { }
                            if (sn2.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            bg = $"{sn2}@{SafeF(() => im2.pixelsPerUnitMultiplier):0.##}";
                            break;
                        }
                    }
                    catch { }

                    sb.Append($"  [{k:00}]{(ours ? " ★我们" : "       ")} '{childName}' 尺寸={(crt != null ? $"{crt.rect.width:0}x{crt.rect.height:0}" : "?")}"
                              + $" pos=({x:0},{y:0}){gap} 字号={fs:0.##} 底图={bg} active={(ch.gameObject != null && ch.gameObject.activeSelf)}"
                              + (ours ? $" 文字='{label}' 颜色=({tcol}) 文本区={tsize}" : "")
                              + "\n");
                    shown++;
                }
                if (t.childCount > shown) sb.Append($"  …（还有 {t.childCount - shown} 个）\n");
            }
            if (found == 0) sb.Append("没找到 'ESC Menu Buttons'（不在主菜单场景？）\n");
            else sb.Append($"\n共 {found} 个 ESC 容器（原生主菜单 + 暂停菜单各一个）\n");
        }
        catch (Exception ex) { sb.Append("(结构快照失败: ").Append(ex.Message).Append(")\n"); }

        return sb.ToString();
    }

    /// <summary>
    /// 剪贴板（ESC 菜单）**面板边界 + 层级**诊断：回答两个问题 ——
    /// ① 面板背景到底多大、我们的注入按钮有没有超出面板（用户反馈“加了个按钮直接撑出去了”）；
    /// ② 原生“设置”页是怎么切出来的（哪个节点被激活/隐藏），好照它做**原生次级菜单**。
    /// 输出：从 ESC 容器往上每层的 rect/active，以及该层下所有兄弟节点的名字/尺寸/位置/active。
    /// </summary>
    public static string DumpClipboard(int depth = 4)
    {
        var sb = new StringBuilder();
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
                if (string.Equals(nm, "ESC Menu Buttons", StringComparison.Ordinal) && t.gameObject != null && t.gameObject.activeInHierarchy)
                { esc = t; break; }
            }
            if (esc == null)
                for (int i = 0; trs != null && i < trs.Length; i++)
                {
                    var t = trs[i];
                    if (t == null) continue;
                    string nm = "";
                    try { nm = t.name ?? ""; } catch { }
                    if (string.Equals(nm, "ESC Menu Buttons", StringComparison.Ordinal)) { esc = t; break; }
                }
            if (esc == null) return "没找到 ESC Menu Buttons\n";

            // ① 从容器往上逐层看 rect / active（面板背景一般在祖先层）
            sb.Append("== 从 ESC 容器往上（看面板背景与遮罩）==\n");
            var cur = esc;
            for (int d = 0; cur != null && d <= depth; d++)
            {
                var rt = RectOf(cur);
                bool act = false;
                try { act = cur.gameObject != null && cur.gameObject.activeInHierarchy; } catch { }
                sb.Append($"  L{d} '{cur.name}' rect={(rt != null ? $"{rt.rect.width:0}x{rt.rect.height:0}" : "?")}"
                          + $" sizeDelta={(rt != null ? $"{rt.sizeDelta.x:0}x{rt.sizeDelta.y:0}" : "?")}"
                          + $" pos={(rt != null ? $"({rt.anchoredPosition.x:0},{rt.anchoredPosition.y:0})" : "?")} active={act}"
                          + $" 子={cur.childCount}\n");
                cur = cur.parent;
            }

            // ② 容器内部：子节点 y 范围（判断有没有超出面板背景）
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int k = 0; k < esc.childCount; k++)
            {
                var ch = esc.GetChild(k);
                var crt = RectOf(ch);
                if (crt == null) continue;
                float y = crt.anchoredPosition.y, h = crt.rect.height;
                minY = Mathf.Min(minY, y - h * 0.5f);
                maxY = Mathf.Max(maxY, y + h * 0.5f);
            }
            sb.Append($"\n容器内子节点纵向范围：{minY:0} ~ {maxY:0}（高 {maxY - minY:0}）\n");

            // ③ 剪贴板/面板层下的所有兄弟（含设置页等），看“切页”是怎么做的
            var hier = esc;
            for (int d = 0; d < 3 && hier.parent != null; d++) hier = hier.parent;

            // ③a 先看直接父层（通常就是 300x400 的面板 Canvas）的子节点：里面一般有 背景/标题/按钮列/设置页
            if (esc.parent != null)
            {
                var p = esc.parent;
                var prt = RectOf(p);
                sb.Append($"\n== 面板层 '{p.name}' rect={(prt != null ? $"{prt.rect.width:0}x{prt.rect.height:0}" : "?")} 子={p.childCount} ==\n");
                for (int k = 0; k < p.childCount; k++)
                {
                    var ch = p.GetChild(k);
                    if (ch == null) continue;
                    var rt = RectOf(ch);
                    bool act = false;
                    try { act = ch.gameObject != null && ch.gameObject.activeSelf; } catch { }
                    sb.Append($"  [{k:00}] '{ch.name}' rect={(rt != null ? $"{rt.rect.width:0}x{rt.rect.height:0}" : "?")}"
                              + $" pos={(rt != null ? $"({rt.anchoredPosition.x:0},{rt.anchoredPosition.y:0})" : "?")} active={act}"
                              + $" 子={ch.childCount} 组件={DescribeTypes(ch)}\n");
                }
            }

            sb.Append($"\n== 剪贴板根 '{Path(hier)}' 下的兄弟（第一层，看有哪些“页”）==\n");
            for (int k = 0; k < hier.childCount; k++)
            {
                var ch = hier.GetChild(k);
                if (ch == null) continue;
                var rt = RectOf(ch);
                bool act = false;
                try { act = ch.gameObject != null && ch.gameObject.activeSelf; } catch { }
                string cn = "";
                try { cn = ch.name ?? ""; } catch { }
                sb.Append($"  [{k:00}] '{cn}' rect={(rt != null ? $"{rt.rect.width:0}x{rt.rect.height:0}" : "?")}"
                          + $" pos={(rt != null ? $"({rt.anchoredPosition.x:0},{rt.anchoredPosition.y:0})" : "?")} active={act}"
                          + $" 子={ch.childCount} 组件={DescribeTypes(ch)}\n");
            }
        }
        catch (Exception ex) { sb.Append("(剪贴板诊断失败: ").Append(ex.Message).Append(")\n"); }
        return sb.ToString();
    }

    /// <summary>
    /// 诊断：我们注入到原生 ESC 菜单里的行**到底有没有字形**（用户报“ESC 菜单里的注入项还是没有字”，
    /// 而 `menutree` 显示文字/颜色/尺寸都正常 → 只能是**字体/材质/激活链**的问题）。
    ///
    /// 做法：把容器的**祖先链临时激活**（这样 TMP 才会建网格；之前用 `activeInHierarchy` 只能看到
    /// 容器关着 = 全是 `/off`，看不出所以然），对每个我们注入的行 + 一颗原生对照按钮打印：
    /// 文字、字体名、材质名/着色器、字号、颜色、**字符数/顶点数**（顶点 0 = 画面上什么都不会有）、
    /// 文本节点的 activeSelf、文本矩形相对按钮的位置（贴出格子也会看不见）。跑完**原样还原**激活状态。
    /// </summary>
    public static string DumpInjectedText()
    {
        var sb = new StringBuilder();
        try
        {
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            int found = 0;
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = "";
                try { nm = t.name ?? ""; } catch { }
                if (!string.Equals(nm, "ESC Menu Buttons", StringComparison.Ordinal)) continue;
                found++;
                sb.Append($"\n■ 容器 #{found} '{Path(t)}'  activeSelf=").Append(SafeAct(t)).Append('\n');

                // 1) 祖先链临时激活（记录原状态，finally 还原）
                var chain = new List<Transform>();
                for (var cur = t; cur != null; cur = cur.parent) chain.Add(cur);
                var saved = new List<bool>();
                for (int k = 0; k < chain.Count; k++) saved.Add(SafeActBool(chain[k]));
                for (int k = 0; k < chain.Count; k++) { try { chain[k].gameObject.SetActive(true); } catch { } }

                try
                {
                    // 1b) 祖先链上的“能不能被画出来”要素：Canvas/CanvasGroup/Mask/裁剪/相机/缩放
                    sb.Append("  ── 祖先链（自内向外）──\n");
                    for (int k = 0; k < chain.Count; k++)
                    {
                        var a = chain[k];
                        string line = $"    L{k} '{a.name}' activeSelf={SafeAct(a)} 缩放={a.localScale.x:0.###}";
                        try { var r = a.GetComponent<RectTransform>(); if (r != null) line += $" rect={r.rect.width:0}x{r.rect.height:0}"; } catch { }
                        try { var c = a.GetComponent<Canvas>(); if (c != null) line += $" Canvas(enabled={c.enabled},mode={c.renderMode},order={c.sortingOrder},override={c.overrideSorting},cam={(c.worldCamera != null ? c.worldCamera.name : "null")})"; } catch { }
                        try { var g = a.GetComponent<CanvasGroup>(); if (g != null) line += $" CanvasGroup(alpha={g.alpha:0.##},interactable={g.interactable},blocks={g.blocksRaycasts})"; } catch { }
                        try { var mm = a.GetComponent<Mask>(); if (mm != null) line += $" Mask(enabled={mm.enabled},showMask={mm.showMaskGraphic})"; } catch { }
                        try { var rm = a.GetComponent<RectMask2D>(); if (rm != null) line += $" RectMask2D(enabled={rm.enabled})"; } catch { }
                        try { var cr = a.GetComponent<CanvasRenderer>(); if (cr != null) line += $" CanvasRenderer(cull={cr.cull},alpha={cr.GetAlpha():0.##},depth={cr.absoluteDepth},moved={cr.hasMoved})"; } catch { }
                        sb.Append(line).Append('\n');
                    }
                    try
                    {
                        var cam = Camera.main;
                        sb.Append("    主相机=").Append(cam != null ? cam.name : "null")
                          .Append(" cullingMask=").Append(cam != null ? cam.cullingMask.ToString() : "-")
                          .Append(" 位置=").Append(cam != null ? cam.transform.position.ToString() : "-").Append('\n');
                    }
                    catch { }

                    // 2) 原生对照：设置按钮的文字 + 底图（我们抄的就是它们；两边一致 = 可见性一致）
                    try
                    {
                        var tpl = NativeMenuStyler.FindTemplate(t, "OpenSettingsBtn", "Settings");
                        sb.Append("  [原生模板 '").Append(tpl != null ? tpl.name : "-").Append("'] ").Append(BgReport(tpl != null ? tpl.transform : null)).Append('\n')                          .Append("  [原生模板] Selectable ").Append(SelectableReport(tpl != null ? tpl.transform : null)).Append('\n')                          .Append("  [原生模板] 渲染器 ").Append(RendererReport(tpl != null ? tpl.transform : null)).Append('\n');
                        var tplTxts = tpl != null ? tpl.GetComponentsInChildren<TMP_Text>(true) : null;
                        if (tplTxts != null)
                            for (int q = 0; q < tplTxts.Length; q++)
                                sb.Append("  [原生模板 '").Append(tpl.name).Append("'] ").Append(TextReport(tplTxts[q])).Append('\n');
                    }
                    catch (Exception ex) { sb.Append("  (原生模板读取失败: ").Append(ex.Message).Append(")\n"); }

                    // 3) 我们注入的行
                    int mine = 0;
                    for (int k = 0; k < t.childCount; k++)
                    {
                        var ch = t.GetChild(k);
                        if (ch == null) continue;
                        string cn = "";
                        try { cn = ch.name ?? ""; } catch { }
                        if (!cn.StartsWith(NativeMenuInjector.OurPrefix, StringComparison.Ordinal)) continue;
                        mine++;
                        var crt = RectOf(ch);
                        sb.Append("  [★我们 ").Append(cn).Append("] 按钮尺寸=")
                          .Append(crt != null ? $"{crt.rect.width:0}x{crt.rect.height:0}" : "?")
                          .Append(" 行activeSelf=").Append(SafeAct(ch))
                          .Append(' ').Append(BgReport(ch)).Append('\n')
                          .Append("  [★我们] Selectable ").Append(SelectableReport(ch)).Append('\n')
                          .Append("  [★我们] 渲染器 ").Append(RendererReport(ch)).Append('\n');
                        var txts = ch.GetComponentsInChildren<TMP_Text>(true);
                        if (txts == null || txts.Length == 0) { sb.Append("      没有文字组件！\n"); continue; }
                        for (int q = 0; q < txts.Length; q++)
                            sb.Append("      ").Append(TextReport(txts[q])).Append('\n');
                    }
                    if (mine == 0) sb.Append("  （该容器里没有我们注入的行）\n");
                }
                finally
                {
                    for (int k = 0; k < chain.Count; k++) { try { chain[k].gameObject.SetActive(saved[k]); } catch { } }
                }
            }
            if (found == 0) sb.Append("没找到 'ESC Menu Buttons'\n");
        }
        catch (Exception ex) { sb.Append("(注入文字诊断失败: ").Append(ex.Message).Append(")\n"); }
        return sb.ToString();
    }

    /// <summary>单个文字组件的“到底会不会画出来”报告（字体/材质/字形数/位置）。</summary>
    private static string TextReport(TMP_Text txt)
    {
        if (txt == null) return "（null）";
        var sb = new StringBuilder();
        try { sb.Append("文字='").Append(txt.text ?? "").Append('\''); } catch { }
        try { sb.Append(" 字体=").Append(txt.font != null ? txt.font.name : "<null>"); } catch (Exception e) { sb.Append(" 字体=!").Append(e.Message); }
        try { sb.Append(" 材质=").Append(txt.fontSharedMaterial != null ? txt.fontSharedMaterial.name : "<null>"); } catch { }
        try { sb.Append(" 着色器=").Append(txt.fontSharedMaterial != null && txt.fontSharedMaterial.shader != null ? txt.fontSharedMaterial.shader.name : "<null>"); } catch { }
        try { sb.Append(" 字号=").Append(txt.fontSize.ToString("0.##")); } catch { }
        try { sb.Append(" 颜色=").Append(txt.color.ToString()); } catch { }
        try { sb.Append(" enabled=").Append(txt.enabled); } catch { }
        try { sb.Append(" activeSelf=").Append(txt.gameObject.activeSelf).Append(" inHierarchy=").Append(txt.gameObject.activeInHierarchy); } catch { }
        // 先读**自然状态**（不碰它），再强制建一次网格对照 —— 区分“本来就没生成”与“只是没重建”
        int chars0 = -1, verts0 = -1, chars = -1, verts = -1;
        try { chars0 = txt.textInfo != null ? txt.textInfo.characterCount : -1; } catch { }
        try { verts0 = txt.mesh != null ? txt.mesh.vertexCount : -1; } catch { }
        sb.Append(" 自然[字符数=").Append(chars0).Append(" 顶点数=").Append(verts0).Append(']');
        // 关键：让 TMP 现在就建一次网格（ignoreActiveState=true 才能在容器关着时也建），再读字形数
        try { txt.ForceMeshUpdate(true, false); } catch { try { txt.ForceMeshUpdate(); } catch { } }
        try { chars = txt.textInfo != null ? txt.textInfo.characterCount : -1; } catch { }
        try { verts = txt.mesh != null ? txt.mesh.vertexCount : -1; } catch { }
        sb.Append(" 强制后[字符数=").Append(chars).Append(" 顶点数=").Append(verts).Append(']').Append(verts == 0 ? "  ← 画面上不会有任何东西" : "");
        try
        {
            var rt = txt.rectTransform;
            if (rt != null) sb.Append(" 文本rect=").Append($"{rt.rect.width:0}x{rt.rect.height:0}@({rt.anchoredPosition.x:0},{rt.anchoredPosition.y:0})");
        }
        catch { }
        // 最终渲染色（tint 会乘进来，只看 `Graphic.color` 是看不出来的）+ 它到底画在屏幕哪儿
        try { if (txt.canvasRenderer != null) sb.Append(" 渲染色=").Append(txt.canvasRenderer.GetColor()); } catch { }
        try
        {
            var rt2 = txt.rectTransform;
            var canvas = txt.GetComponentInParent<Canvas>();
            var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            var corners = new Vector3[4];
            rt2.GetWorldCorners(corners);
            var p0 = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
            var p2 = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
            sb.Append($" 屏幕=({p0.x:0},{p0.y:0})-({p2.x:0},{p2.y:0})");
            if (canvas != null) sb.Append($" 画布={canvas.renderMode}/scale{canvas.scaleFactor:0.####}");
        }
        catch { }
        return sb.ToString();
    }

    /// <summary>
    /// Selectable（Button）报告：过渡方式 / tint / tint 目标。
    ///
    /// ⚠️ 2026-09-13 结论修正：原生按钮的 `colors.normalColor` 确实是**纯黑**，但原生那边它打在**子节点**上
    /// （悬停覆盖层），所以无害；我们当初把底图 Image 放在按钮自己身上并被当成 `targetGraphic` → 黑 tint 生效
    /// → 底图被染黑（“框黑”）。**“注入项没有字”与此无关**，真正原因是文字框只有 81x18 高、
    /// 装不下 18 号字 ~26px 的行高，被 TMP 的 `Ellipsis` 整行丢掉（见 `NativeMenuInjector.StyleGridCell`）。
    /// 现在 `NativeMenuStyler.CopyTint` 会把这个子节点也镜像一份，颜色照抄原生。
    /// </summary>
    private static string SelectableReport(Transform t)
    {
        if (t == null) return "<无>";
        try
        {
            var btn = t.GetComponent<Selectable>();
            if (btn == null) return "（不是 Selectable）";
            var c = btn.colors;
            return $"过渡={btn.transition} normal={c.normalColor} highlight={c.highlightedColor} target={(btn.targetGraphic != null ? btn.targetGraphic.gameObject.name : "null")}";
        }
        catch (Exception ex) { return "(读取失败:" + ex.Message + ")"; }
    }

    /// <summary>渲染器报告：每个 Graphic 的 enabled / 颜色 / `CanvasRenderer.cull`（**true = 被遮罩裁掉，不会画**）
    /// / alpha / 深度 —— 文字“属性都对却看不见”时，这一行能直接指出是被裁掉还是被盖住。</summary>
    private static string RendererReport(Transform t)
    {        if (t == null) return "<无>";
        var sb = new StringBuilder();
        try
        {
            var gs = t.GetComponentsInChildren<Graphic>(true);
            for (int i = 0; gs != null && i < gs.Length; i++)
            {
                var g = gs[i];
                if (g == null) continue;
                if (sb.Length > 0) sb.Append(" ； ");
                string gn = "";
                try { gn = g.gameObject.name ?? ""; } catch { }
                sb.Append('[').Append(gn).Append('/').Append(g.GetType().Name).Append(']');
                try
                {
                    var grt = g.rectTransform;
                    if (grt != null)
                    {
                        string pn = "-";
                        try { pn = grt.parent != null ? (grt.parent.name ?? "-") : "-"; } catch { }
                        if (pn.Length > 16) pn = pn.Substring(0, 16);
                        sb.Append(" rect=").Append($"{grt.rect.width:0.#}x{grt.rect.height:0.#}")
                          .Append(" 缩放=").Append(grt.localScale.x.ToString("0.##"))
                          .Append(" 父='").Append(pn).Append('\'');
                    }
                }
                catch { }
                try { sb.Append(" enabled=").Append(g.enabled); } catch { }
                try { sb.Append(" 色=").Append(g.color.ToString()); } catch { }
                try
                {
                    var cr = g.canvasRenderer;
                    if (cr != null)
                        sb.Append(" cull=").Append(cr.cull)
                          .Append(" alpha=").Append(cr.GetAlpha().ToString("0.##"))
                          .Append(" 渲染色=").Append(cr.GetColor().ToString())   // ★ tint 之后的真色（Image 有效；TMP 走材质，读回是白）
                          .Append(" depth=").Append(cr.absoluteDepth);
                }
                catch { }
            }
        }
        catch (Exception ex) { sb.Append("(读取失败:").Append(ex.Message).Append(')'); }
        return sb.ToString();
    }

    /// <summary>底图报告（列出**每一张** Image 的 sprite/颜色/**绘制参数**）——
    /// “看着多出一层/底色不对”这类问题只有把 `type / fillCenter / ppuMul / preserveAspect / rect` 一起看才能定位：
    /// 同一个 sprite 在 `Simple` 与 `Sliced` 下画出来完全不同（九宫格放不下时 Unity 会把整张 128px 图压进小控件）。</summary>
    private static string BgReport(Transform t)
    {
        if (t == null) return "底图=<无按钮>";
        var sb = new StringBuilder();
        try
        {
            var imgs = t.GetComponentsInChildren<Image>(true);
            int n = 0;
            for (int i = 0; imgs != null && i < imgs.Length; i++)
            {
                var im = imgs[i];
                if (im == null) continue;
                if (n++ > 0) sb.Append(" ； ");
                string nm = "-";
                try { nm = im.gameObject.name ?? "-"; } catch { }
                string sn = "<无sprite>";
                try { if (im.sprite != null) sn = im.sprite.name; } catch { }
                sb.Append('[').Append(nm).Append(']').Append(sn).Append(" 色=").Append(im.color);
                try { sb.Append(" type=").Append(im.type).Append(" fillCenter=").Append(im.fillCenter); } catch { }
                try { sb.Append(" ppuMul=").Append(im.pixelsPerUnitMultiplier.ToString("0.##")); } catch { }
                try { sb.Append(" preserveAspect=").Append(im.preserveAspect).Append(" enabled=").Append(im.enabled); } catch { }
                try { var rt = im.rectTransform; if (rt != null) sb.Append(" rect=").Append($"{rt.rect.width:0.#}x{rt.rect.height:0.#}"); } catch { }
            }
            if (n > 0) return "底图=" + sb;
        }
        catch { }
        return "底图=<没找到>";
    }

    private static string SafeAct(Transform t)
    {
        try { return t != null && t.gameObject != null ? t.gameObject.activeSelf.ToString() : "?"; } catch { return "?"; }
    }

    private static bool SafeActBool(Transform t)
    {
        try { return t != null && t.gameObject != null && t.gameObject.activeSelf; } catch { return false; }
    }

    /// <summary>节点上挂的组件类型简写（诊断用）。</summary>
    /// <summary>
    /// 原生页**逐节点普查**（`pagedump[:页名]`，默认 `Settings menu`）：把页里每个节点的
    /// 名字/激活/尺寸/位置/组件类型 + 关键值（TMP 文案·字体·字号·颜色、Image 的 sprite·type·ppuMul·raycast、
    /// Slider 的 min/max/value·方向、Toggle 的 isOn、Dropdown 的选项数、InputField 的文本）逐行打出来。
    ///
    /// 用途：我们要在**自己的原生子页**里复刻“大标题/小标题/拖拽条/检查框/下拉框/选项卡/主按钮/输入框”，
    /// 必须先把游戏自己那一页的控件长什么样量清楚（照抄而不是猜）。
    /// </summary>
    public static string DumpPage(string pageName = "Settings menu", int maxDepth = 14, int maxNodes = 1600)
    {
        var sb = new StringBuilder();
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
                bool act = false; try { act = t.gameObject != null && t.gameObject.activeInHierarchy; } catch { }
                if (act) { root = t; break; }
                if (root == null) root = t;
            }
            if (root == null) return $"没找到页 '{pageName}'\n";

            var rt0 = RectOf(root);
            sb.Append($"== 页 '{Path(root)}' rect={(rt0 != null ? $"{rt0.rect.width:0}x{rt0.rect.height:0}" : "?")} 子={root.childCount} ==\n");
            int[] n = { 0 };
            WalkNode(root, 0, maxDepth, maxNodes, sb, n);
            sb.Append($"\n共 {n[0]} 个节点（上限 {maxNodes}）\n");
        }
        catch (Exception ex) { sb.Append("(pagedump 失败: ").Append(ex.Message).Append(")\n"); }
        return sb.ToString();
    }

    /// <summary>要量规格的原生控件名（就是我们复刻的那几种 + 页面骨架）。</summary>
    private static readonly string[] SpecTargets = new[]
    {
        "Title Settings", "HeadlineUGUI", "TabsCtn", "TabButtonUGUI",
        "SGButtonPrimaryUGUI", "ButtonSecondaryUGUI", "OpenSettingsBtn",
        "SliderConsoleUGUI", "ToggleConsoleUGUI", "DropdownUGUIWithLabel",
        "TextfieldConsoleUGUI", "InputBindingConsoleUGUI", "OptionsButtonConsoleUGUI",
        "Scrollbar Vertical",
    };

    /// <summary>
    /// 原生控件**样式规格**普查（命令 `pagespec`）：把游戏 Settings 页里那几种控件的外观规格连同
    /// **宿主几何**一起打出来 —— ① `Settings menu` 往上到 Canvas 每层的 rect/缩放/active；
    /// ② 该页直接子节点（页骨架）；③ 每个目标控件 3 层子节点的
    /// `Image(sprite·颜色·type·ppuMul·rect)` 与 `TMP(字体·字号·颜色·对齐)`。
    ///
    /// ⚠ 为什么必须量而不能猜（2026-09-13 用户报“主/次按钮还是白底、输入框白底没套原生样式”）：
    ///   同一个 sprite 配不同颜色观感完全不同（`UI Box Castile` 是**白**纸面 + 深色 `SUGShadowLite` 投影；
    ///   输入框底其实不是白的）。这些值只能从**真的那页**抄。
    /// </summary>
    public static string DumpSpecs(int childDepth = 3)
    {
        var sb = new StringBuilder();
        try
        {
            Transform page = FindByName("Settings menu");
            if (page == null) { sb.Append("（没找到 'Settings menu' —— 游戏还没建设置页，先打开一次设置）\n"); return sb.ToString(); }

            // ① 宿主链：搞清楚“大页”到底挂在哪、多大、缩放多少（决定我们能不能照抄它的几何）
            sb.Append("== 宿主链（'Settings menu' 往上）==\n");
            var cur = page;
            for (int d = 0; cur != null && d <= 4; d++)
            {
                var rt = RectOf(cur);
                sb.Append($"  L{d} '{cur.name}' rect={(rt != null ? $"{rt.rect.width:0.#}x{rt.rect.height:0.#}" : "?")}"
                          + $" sizeDelta={(rt != null ? $"{rt.sizeDelta.x:0.#}x{rt.sizeDelta.y:0.#}" : "?")}"
                          + $" pos={(rt != null ? $"({rt.anchoredPosition.x:0.#},{rt.anchoredPosition.y:0.#})" : "?")}"
                          + $" 锚={(rt != null ? $"{rt.anchorMin.x:0.##},{rt.anchorMin.y:0.##}~{rt.anchorMax.x:0.##},{rt.anchorMax.y:0.##}" : "?")}"
                          + $" piv={(rt != null ? $"{rt.pivot.x:0.##},{rt.pivot.y:0.##}" : "?")}"
                          + $" 缩放={ScaleOf(cur):0.###} act={SafeAct(cur)} 子={cur.childCount} {DescribeTypes(cur)}\n");
                var cv = cur.GetComponent<Canvas>();
                if (cv != null)
                    sb.Append($"       ↳Canvas renderMode={cv.renderMode} scaleFactor={cv.scaleFactor:0.###} 参考PPU={cv.referencePixelsPerUnit:0.#}\n");
                cur = cur.parent;
            }

            // ② 页骨架：直接子节点（原生行宽 742 就是从这些骨架上量出来的）
            sb.Append("== 'Settings menu' 直接子节点 ==\n");
            for (int i = 0; i < page.childCount; i++)
            {
                var ch = page.GetChild(i);
                var rt = RectOf(ch);
                sb.Append($"  [{i:00}] '{ch.name}' rect={(rt != null ? $"{rt.rect.width:0.#}x{rt.rect.height:0.#}" : "?")}"
                          + $" pos={(rt != null ? $"({rt.anchoredPosition.x:0.#},{rt.anchoredPosition.y:0.#})" : "?")}"
                          + $" 缩放={ScaleOf(ch):0.###} act={SafeAct(ch)} 子={ch.childCount} {DescribeTypes(ch)}\n");
            }

            // ②b 页骨架**逐层**（标题/选项卡/内容区在哪一层、多大 —— 用户：“大标题不够大，应该是不同块上的，没拷贝过来”/
            //    “块尺寸也不对，上下都缺一块” 都靠这一段定位）
            sb.Append("== 'Settings menu' 骨架（到 3 层）==\n");
            SpecNode(page, 1, 3, sb);

            // ③ 每个目标控件的完整规格
            sb.Append("\n== 控件规格（每层子节点：尺寸 / 底图 / 文字）==\n");
            for (int i = 0; i < SpecTargets.Length; i++)
            {
                var t = FindByName(SpecTargets[i]);
                if (t == null) { sb.Append($"【{SpecTargets[i]}】没找到\n"); continue; }
                sb.Append($"【{SpecTargets[i]}】路径={Path(t)} 缩放={ScaleOf(t):0.###} act={SafeAct(t)} {DescribeTypes(t)}\n");
                // 拖拽条要看到第 5 层：`Handle Slide Area/Handle/Bg`（手柄方块本体）与它背后的 Shadow
                int d = string.Equals(SpecTargets[i], "SliderConsoleUGUI", StringComparison.Ordinal) ? 5 : childDepth;
                SpecNode(t, 1, d, sb);
            }

            // ④ **按组件类型**普查：控件是按需实例化的（只有当前选项卡的行存在），
            //    所以名字找不到时改按类型找 —— 这才是真正稳的做法（InputField / Slider / Toggle / Dropdown / Button / Scrollbar）。
            sb.Append("\n== 按组件类型（'Settings menu' 子树）==\n");
            int printed = 0;
            ScanByType(page, ref printed, 40, sb);

            // ⑤ 未应用设置面板（APPLY / RESET 那排主·次按钮就在这儿）
            var un = FindByName("UnappliedSettingsPanel");
            if (un != null)
            {
                sb.Append("\n== UnappliedSettingsPanel 全量 ==\n");
                SpecNode(un, 0, 4, sb);
            }
        }
        catch (Exception ex) { sb.Append("(pagespec 失败: ").Append(ex.Message).Append(")\n"); }
        return sb.ToString();
    }

    /// <summary>按组件类型扫子树（见 <see cref="DumpSpecs"/> 第 ④ 步）。</summary>
    private static void ScanByType(Transform root, ref int printed, int max, StringBuilder sb)
    {
        if (root == null || printed >= max) return;
        try
        {
            string why = null;
            if (root.GetComponent<TMPro.TMP_InputField>() != null) why = "TMP_InputField";
            else if (root.GetComponent<TMPro.TMP_Dropdown>() != null) why = "TMP_Dropdown";
            else if (root.GetComponent<Dropdown>() != null) why = "Dropdown";
            else if (root.GetComponent<Slider>() != null) why = "Slider";
            else if (root.GetComponent<Toggle>() != null) why = "Toggle";
            else if (root.GetComponent<Scrollbar>() != null) why = "Scrollbar";
            else if (root.GetComponent<Button>() != null) why = "Button";
            if (why != null)
            {
                printed++;
                sb.Append($"【{why}】路径={Path(root)} act={SafeAct(root)} {DescribeTypes(root)}\n");
                // 深度 3：拖拽条的 `Fill Area/Fill`、`Handle Slide Area/Handle`、选项卡的字都在这一层
                SpecNode(root, 1, 3, sb);
                // 输入框：文字/占位符的**具体规格**（底是深色的话，字必须是浅色 —— 这一项必须照抄）
                if (why == "TMP_InputField")
                {
                    try
                    {
                        var inp = root.GetComponent<TMPro.TMP_InputField>();
                        sb.Append("        ↳输入框 文字组件=" + TmpBrief(inp != null ? inp.textComponent as TMP_Text : null)
                                  + " 占位=" + TmpBrief(inp != null ? inp.placeholder as TMP_Text : null)
                                  + $" 内容类型={(inp != null ? inp.contentType.ToString() : "?")} 行类型={(inp != null ? inp.lineType.ToString() : "?")}\n");
                    }
                    catch { }
                }
                else if (why == "Scrollbar")
                {
                    try
                    {
                        var sb2 = root.GetComponent<Scrollbar>();
                        sb.Append("        ↳滚动条 handle=" + (sb2 != null && sb2.handleRect != null ? sb2.handleRect.name : "null") + "\n");
                    }
                    catch { }
                }
            }
            for (int i = 0; i < root.childCount; i++) ScanByType(root.GetChild(i), ref printed, max, sb);
        }
        catch { }
    }

    /// <summary>递归打印一个节点及其子节点的尺寸与绘制参数（`pagespec` 用）。</summary>
    private static void SpecNode(Transform t, int depth, int maxDepth, StringBuilder sb)
    {
        if (t == null || depth > maxDepth) return;
        try
        {
            var rt = RectOf(t);
            sb.Append("    ").Append(new string(' ', depth * 2)).Append("/").Append(t.name)
              .Append(" 尺寸=").Append(rt != null ? $"{rt.sizeDelta.x:0.#}x{rt.sizeDelta.y:0.#}" : "?")
              .Append(" rect=").Append(rt != null ? $"{rt.rect.width:0.#}x{rt.rect.height:0.#}" : "?")
              .Append(" pos=").Append(rt != null ? $"({rt.anchoredPosition.x:0.#},{rt.anchoredPosition.y:0.#})" : "?")
              .Append(" 锚=").Append(rt != null ? $"{rt.anchorMin.x:0.##},{rt.anchorMin.y:0.##}~{rt.anchorMax.x:0.##},{rt.anchorMax.y:0.##}" : "?")
              .Append(" piv=").Append(rt != null ? $"{rt.pivot.x:0.##},{rt.pivot.y:0.##}" : "?")
              .Append(" act=").Append(SafeAct(t));
            try { if (rt != null) sb.Append($" 局部缩放=({rt.localScale.x:0.####},{rt.localScale.y:0.####})"); } catch { }
            // ★ Selectable 的**过渡色**：原生按钮的 `Image.color` 常常是白的，真正的颜色由
            //   ColorTint 过渡**在 CanvasRenderer 上**设置（`normalColor`）—— 不看这一项就永远抄不对颜色。
            try
            {
                var sel = t.GetComponent<Selectable>();
                if (sel != null)
                {
                    var cb = sel.colors;
                    sb.Append($" ■Selectable 过渡={sel.transition} target={(sel.targetGraphic != null ? sel.targetGraphic.gameObject.name : "null")}")
                      .Append($" normal=({cb.normalColor.r:0.###},{cb.normalColor.g:0.###},{cb.normalColor.b:0.###},{cb.normalColor.a:0.###})")
                      .Append($" highlight=({cb.highlightedColor.r:0.###},{cb.highlightedColor.g:0.###},{cb.highlightedColor.b:0.###},{cb.highlightedColor.a:0.###})")
                      .Append($" pressed=({cb.pressedColor.r:0.###},{cb.pressedColor.g:0.###},{cb.pressedColor.b:0.###},{cb.pressedColor.a:0.###})")
                      .Append($" selected=({cb.selectedColor.r:0.###},{cb.selectedColor.g:0.###},{cb.selectedColor.b:0.###},{cb.selectedColor.a:0.###})")
                      .Append($" disabled=({cb.disabledColor.r:0.###},{cb.disabledColor.g:0.###},{cb.disabledColor.b:0.###},{cb.disabledColor.a:0.###})");
                }
            }
            catch { }
            try
            {
                var im = t.GetComponent<Image>();
                if (im != null)
                {
                    string sn = im.sprite != null ? im.sprite.name : "<无>";
                    sb.Append($" 底图={sn} 色=({im.color.r:0.###},{im.color.g:0.###},{im.color.b:0.###},{im.color.a:0.###})")
                      .Append($" type={im.type} ppuMul={im.pixelsPerUnitMultiplier:0.##} ray={im.raycastTarget}")
                      .Append($" 组件enabled={im.enabled}");
                    // ★ 真正决定屏上颜色的是 CanvasRenderer 的颜色（Selectable 的 ColorTint 是设在这里的，
                    //   所以 `image.color` 常常是白的而实画是黑的）—— 不复刻这一项就永远抄不对。
                    try
                    {
                        var cr = im.canvasRenderer;
                        if (cr != null)
                        {
                            var rc = cr.GetColor();
                            sb.Append($" 渲染色=({rc.r:0.###},{rc.g:0.###},{rc.b:0.###},{rc.a:0.###})");
                        }
                    }
                    catch { }
                }
            }
            catch { }
            try
            {
                var tx = t.GetComponent<TMP_Text>();
                if (tx != null)
                    sb.Append($" 文字='{Trim(tx.text ?? "", 22)}' 字体={(tx.font != null ? tx.font.name : "null")} 字号={tx.fontSize:0.##}")
                      .Append($" 色=({tx.color.r:0.###},{tx.color.g:0.###},{tx.color.b:0.###},{tx.color.a:0.###}) align={tx.alignment}")
                      .Append($" 自动={(tx.enableAutoSizing ? "Y" : "N")} rect={SafeRect(tx.rectTransform)}");
            }
            catch { }
            try
            {
                var rw = t.GetComponent<RawImage>();
                if (rw != null) sb.Append($" RawImage 色=({rw.color.r:0.###},{rw.color.g:0.###},{rw.color.b:0.###},{rw.color.a:0.###}) 贴图={(rw.texture != null ? rw.texture.name : "<无>")}");
            }
            catch { }
            sb.Append('\n');
        }
        catch { }

        try
        {
            for (int i = 0; i < t.childCount; i++) SpecNode(t.GetChild(i), depth + 1, maxDepth, sb);
        }
        catch { }
    }

    private static string SafeRect(RectTransform rt)
    {
        try { return rt != null ? $"{rt.rect.width:0.#}x{rt.rect.height:0.#}" : "?"; } catch { return "?"; }
    }

    /// <summary>TMP 文本的关键规格一行（字体/字号/颜色/对齐）。</summary>
    private static string TmpBrief(TMP_Text t)
    {
        if (t == null) return "null";
        try
        {
            return $"'{(t.text != null ? Trim(t.text, 14) : "")}' 字体={(t.font != null ? t.font.name : "null")} 字号={t.fontSize:0.##}"
                 + $" 色=({t.color.r:0.###},{t.color.g:0.###},{t.color.b:0.###},{t.color.a:0.###}) align={t.alignment} 自动={(t.enableAutoSizing ? "Y" : "N")}";
        }
        catch { return "?"; }
    }

    /// <summary>全场景按名字找第一个节点（优先已激活的）。</summary>
    private static Transform FindByName(string name)
    {
        try
        {
            Transform found = null;
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = ""; try { nm = t.name ?? ""; } catch { }
                if (!string.Equals(nm, name, StringComparison.Ordinal)) continue;
                bool act = false; try { act = t.gameObject != null && t.gameObject.activeInHierarchy; } catch { }
                if (act) return t;
                if (found == null) found = t;
            }
            return found;
        }
        catch { return null; }
    }

    private static float ScaleOf(Transform t)
    {
        try { return t != null ? t.lossyScale.x : 1f; } catch { return 1f; }
    }

    private static void WalkNode(Transform t, int depth, int maxDepth, int maxNodes, StringBuilder sb, int[] count)
    {
        if (t == null || depth > maxDepth || count[0] >= maxNodes) return;
        count[0]++;
        try
        {
            string pad = new string(' ', depth * 2);
            var rt = RectOf(t);
            bool act = false; try { act = t.gameObject != null && t.gameObject.activeSelf; } catch { }
            string nm = "-"; try { nm = t.name ?? "-"; } catch { }
            sb.Append(pad).Append('[').Append(depth).Append("] '").Append(nm).Append('\'')
              .Append(" act=").Append(act)
              .Append(" 尺寸=").Append(rt != null ? $"{rt.sizeDelta.x:0.#}x{rt.sizeDelta.y:0.#}" : "?")
              .Append(" rect=").Append(rt != null ? $"{rt.rect.width:0.#}x{rt.rect.height:0.#}" : "?")
              .Append(" pos=").Append(rt != null ? $"({rt.anchoredPosition.x:0.#},{rt.anchoredPosition.y:0.#})" : "?")
              .Append(" 锚=").Append(rt != null ? $"{rt.anchorMin.x:0.##},{rt.anchorMin.y:0.##}~{rt.anchorMax.x:0.##},{rt.anchorMax.y:0.##}" : "?")
              .Append(' ').Append(DescribeTypes(t));

            // TMP 文本
            try
            {
                var tx = t.GetComponent<TMP_Text>();
                if (tx != null)
                    sb.Append($" ⇒文字='{Trim(tx.text ?? "", 28)}' 字体={(tx.font != null ? tx.font.name : "null")} 字号={tx.fontSize:0.##}")
                      .Append($" 色=({tx.color.r:0.##},{tx.color.g:0.##},{tx.color.b:0.##},{tx.color.a:0.##})").Append(" align=").Append(tx.alignment);
            }
            catch { }
            // Image
            try
            {
                var im = t.GetComponent<Image>();
                if (im != null)
                {
                    string sn = im.sprite != null ? im.sprite.name : "<无>";
                    sb.Append($" ⇒底图={sn} 色={im.color} type={im.type} ppuMul={im.pixelsPerUnitMultiplier:0.##} ray={im.raycastTarget}");
                    // ★ 渲染色（`CanvasRenderer` 上的真色）—— 判“颜色和原生不一样”必须看这个：
                    //   `Image.color` 只是乘法底色，真正的 tint（Selectable 过渡）打在 CanvasRenderer 上。
                    try { var cr = im.canvasRenderer; if (cr != null) sb.Append($" 渲={cr.GetColor()}"); } catch { }
                }
            }
            catch { }
            // Slider
            try
            {
                var sl = t.GetComponent<Slider>();
                if (sl != null) sb.Append($" ⇒Slider min={sl.minValue:0.##} max={sl.maxValue:0.##} val={sl.value:0.##} 方向={sl.direction} 整值={sl.wholeNumbers}");
            }
            catch { }
            // Toggle
            try
            {
                var tg = t.GetComponent<Toggle>();
                if (tg != null) sb.Append($" ⇒Toggle isOn={tg.isOn} graphic={(tg.graphic != null ? tg.graphic.gameObject.name : "null")}");
            }
            catch { }
            // Dropdown（UGUI 与 TMP 两种）
            try
            {
                var dd = t.GetComponent<Dropdown>();
                if (dd != null)
                {
                    string cap = dd.captionText != null ? (dd.captionText.text ?? "") : "";
                    sb.Append($" ⇒Dropdown 选项={(dd.options != null ? dd.options.Count : 0)} 当前={dd.value} caption='{Trim(cap, 18)}'");
                }
            }
            catch { }
            try
            {
                var td = t.GetComponent<TMPro.TMP_Dropdown>();
                if (td != null)
                {
                    string cap2 = td.captionText != null ? (td.captionText.text ?? "") : "";
                    sb.Append($" ⇒TMP_Dropdown 选项={(td.options != null ? td.options.Count : 0)} 当前={td.value} caption='{Trim(cap2, 18)}'");
                }
            }
            catch { }
            // InputField
            try
            {
                var inp = t.GetComponent<TMPro.TMP_InputField>();
                if (inp != null)
                {
                    string ph = "";
                    try { if (inp.placeholder != null) ph = ((TMP_Text)inp.placeholder).text ?? ""; } catch { }
                    sb.Append($" ⇒InputField text='{Trim(inp.text ?? "", 18)}' 占位='{Trim(ph, 12)}'");
                }
            }
            catch { }
            // 自定义脚本（按钮点击等）
            try
            {
                var ms = t.GetComponents<MonoBehaviour>();
                for (int i = 0; ms != null && i < ms.Length && i < 3; i++)
                    if (ms[i] != null) { try { sb.Append(" ⇒脚本:").Append(ms[i].GetType().Name); } catch { } }
            }
            catch { }
            sb.Append('\n');
        }
        catch { }

        try
        {
            for (int i = 0; i < t.childCount; i++) WalkNode(t.GetChild(i), depth + 1, maxDepth, maxNodes, sb, count);
        }
        catch { }
    }

    private static string Trim(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length > max ? s.Substring(0, max) + "…" : s;
    }

    private static string DescribeTypes(Transform t)
    {        var sb = new StringBuilder();
        try
        {
            if (t.GetComponent<Button>() != null) sb.Append("Button ");
            if (t.GetComponent<Toggle>() != null) sb.Append("Toggle ");
            if (t.GetComponent<Slider>() != null) sb.Append("Slider ");
            if (t.GetComponent<ScrollRect>() != null) sb.Append("ScrollRect ");
            if (t.GetComponent<CanvasGroup>() != null) sb.Append("CanvasGroup ");
            if (t.GetComponent<TMP_Text>() != null) sb.Append("TMP ");
            if (t.GetComponent<Image>() != null) sb.Append("Image ");
            if (t.GetComponent<LayoutGroup>() != null) sb.Append("LayoutGroup ");
            var scripts = t.GetComponents<MonoBehaviour>();
            if (scripts != null)
                for (int i = 0; i < scripts.Length && i < 4; i++)
                    if (scripts[i] != null) { try { sb.Append(scripts[i].GetType().Name).Append(' '); } catch { } }
        }
        catch { }
        return sb.Length == 0 ? "-" : sb.ToString();
    }

    private static string Path(Transform t)
    {
        var sb = new StringBuilder(t != null ? t.name : "?");
        var p = t != null ? t.parent : null;
        int guard = 0;
        while (p != null && guard++ < 5) { sb.Insert(0, p.name + "/"); p = p.parent; }
        return sb.ToString();
    }

    /// <summary>
    /// 原生菜单**组件普查**：逐个原生 ScreenSpaceOverlay 画布列出组件数量（Image/Button/Toggle/Slider/ScrollRect/TMP）、
    /// 按钮尺寸与字号分布、素材清单（含尺寸与 `pixelsPerUnitMultiplier`），并给一张全局
    /// “ppuMul × 元素尺寸” 表 —— 用来回答“原生大面板/小按钮各自用多少倍率、字号多少”，
    /// 好让我们把主题跟原生对齐（或反过来确认哪张素材不该打 2.5）。
    /// </summary>
    public static string DumpMenu(int maxCanvases = 10, int maxRowsPerCanvas = 10)
    {
        var sb = new StringBuilder();
        var mulBuckets = new System.Collections.Generic.Dictionary<string, List<string>>();

        try
        {
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
            int shown = 0;
            for (int i = 0; canvases != null && i < canvases.Length && shown < maxCanvases; i++)
            {
                var c = canvases[i];
                if (c == null || c.gameObject == null) continue;
                if (c.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                string cname = "";
                try { cname = c.gameObject.name ?? ""; } catch { }
                if (cname.StartsWith("OpenNestUIKit_", StringComparison.Ordinal)) continue;   // 跳过我们自己
                if (IsOurs(c.transform)) continue;
                shown++;

                CanvasScaler sc = null;
                try { sc = c.GetComponent<CanvasScaler>(); } catch { }
                sb.Append($"\n■ 画布 '{cname}' order={c.sortingOrder} scaleFactor={c.scaleFactor:0.###} canvasRefPPU={c.referencePixelsPerUnit:0.#}");
                if (sc != null) sb.Append($" | {sc.uiScaleMode} ref={sc.referenceResolution.x:0}x{sc.referenceResolution.y:0} match={sc.screenMatchMode}/{sc.matchWidthOrHeight:0.##}");
                sb.Append('\n');

                // 组件数量
                int nBtn = Count<Button>(c), nTgl = Count<Toggle>(c), nSld = Count<Slider>(c),
                    nSrc = Count<ScrollRect>(c), nTxt = Count<TMP_Text>(c), nImg = Count<Image>(c);
                int nImgSprite = 0;
                var sprites = new System.Collections.Generic.Dictionary<string, string>();
                var fonts = new System.Collections.Generic.Dictionary<float, int>();
                var btnSizes = new System.Collections.Generic.Dictionary<string, int>();
                var btnFonts = new System.Collections.Generic.Dictionary<float, int>();

                var imgs = c.GetComponentsInChildren<Image>(true);
                for (int k = 0; imgs != null && k < imgs.Length; k++)
                {
                    var im = imgs[k];
                    if (im == null || im.sprite == null) continue;
                    nImgSprite++;
                    string sn = "";
                    try { sn = im.sprite.name ?? ""; } catch { }
                    if (sn.Length == 0) continue;
                    var rt = RectOf(im.transform);
                    string sizeStr = rt != null ? $"{rt.rect.width:0}x{rt.rect.height:0}" : "?";
                    float mul = SafeF(() => im.pixelsPerUnitMultiplier);
                    string key = $"ppuMul={mul:0.##}";
                    if (!mulBuckets.TryGetValue(key, out var list)) { list = new List<string>(); mulBuckets[key] = list; }
                    if (list.Count < 40) list.Add(sizeStr);
                    string line = $"{sn} ×{sizeStr}@{mul:0.##}";
                    if (sprites.TryGetValue(sn, out var agg)) sprites[sn] = agg + "、" + line;
                    else sprites[sn] = line;
                }

                var texts = c.GetComponentsInChildren<TMP_Text>(true);
                for (int k = 0; texts != null && k < texts.Length; k++)
                {
                    var t = texts[k];
                    if (t == null) continue;
                    float fs = SafeF(() => t.fontSize);
                    fonts[fs] = fonts.TryGetValue(fs, out var cc) ? cc + 1 : 1;
                }

                var btns = c.GetComponentsInChildren<Button>(true);
                for (int k = 0; btns != null && k < btns.Length; k++)
                {
                    var b = btns[k];
                    if (b == null) continue;
                    var rt = RectOf(b.transform);
                    string sz = rt != null ? $"{rt.rect.width:0}x{rt.rect.height:0}" : "?";
                    btnSizes[sz] = btnSizes.TryGetValue(sz, out var cc) ? cc + 1 : 1;
                    try
                    {
                        var t = b.GetComponentInChildren<TMP_Text>(true);
                        if (t != null)
                        {
                            float fs = SafeF(() => t.fontSize);
                            btnFonts[fs] = btnFonts.TryGetValue(fs, out var cf) ? cf + 1 : 1;
                        }
                    }
                    catch { }
                }

                sb.Append($"  组件：Image {nImg}（有 sprite {nImgSprite}）/ Button {nBtn} / Toggle {nTgl} / Slider {nSld} / ScrollRect {nSrc} / 文字 {nTxt}\n");
                sb.Append("  按钮尺寸分布：").Append(Top(btnSizes, maxRowsPerCanvas)).Append('\n');
                sb.Append("  按钮字号分布：").Append(TopF(btnFonts, maxRowsPerCanvas)).Append('\n');
                sb.Append("  文字字号分布：").Append(TopF(fonts, maxRowsPerCanvas)).Append('\n');
                sb.Append("  素材（含尺寸@ppuMul）：");
                int rows = 0;
                foreach (var kv in sprites)
                {
                    if (rows++ >= maxRowsPerCanvas) { sb.Append(" …"); break; }
                    sb.Append("\n    ").Append(kv.Key).Append(" → ").Append(kv.Value);
                }
                sb.Append('\n');
            }
        }
        catch (Exception ex) { sb.Append("(原生菜单普查失败: ").Append(ex.Message).Append(")\n"); }

        sb.Append("\n■ 全局：ppuMul 使用情况（key=pixelsPerUnitMultiplier，值是使用它的元素尺寸）\n");
        foreach (var kv in mulBuckets)
        {
            var l = kv.Value;
            float minW = float.MaxValue, maxW = 0f, minH = float.MaxValue, maxH = 0f;
            for (int i = 0; i < l.Count; i++)
            {
                var parts = l[i].Split('x');
                if (parts.Length != 2) continue;
                if (float.TryParse(parts[0], out float w) && float.TryParse(parts[1], out float h))
                {
                    if (w < minW) minW = w; if (w > maxW) maxW = w;
                    if (h < minH) minH = h; if (h > maxH) maxH = h;
                }
            }
            sb.Append($"  {kv.Key}：{l.Count} 个元素，尺寸范围 {(minW == float.MaxValue ? "?" : $"{minW:0}x{minH:0}")} ~ {maxW:0}x{maxH:0}\n");
        }

        sb.Append("\n■ 我方对照：uiTheme.SpritePpuMul=").Append(Theme.UiTheme.SpritePpuMul.ToString("0.##"))
          .Append("；窗口 ").Append(Menu.UiMenuWindow.WindowSize.x.ToString("0")).Append('x').Append(Menu.UiMenuWindow.WindowSize.y.ToString("0")).Append('\n');

        // 重点：9-slice 类素材“元素尺寸 @ ppuMul”对照 —— 判断倍率跟尺寸的关系
        sb.Append("\n■ 9-slice 类素材对照（原生，元素尺寸 @ ppuMul）：\n");
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<Image>(true);
            int rows = 0;
            for (int i = 0; all != null && i < all.Length && rows < 24; i++)
            {
                var im = all[i];
                if (im == null || im.sprite == null) continue;
                string sn = "";
                try { sn = im.sprite.name ?? ""; } catch { }
                if (sn.Length == 0) continue;
                if (sn.IndexOf("UI Box", StringComparison.OrdinalIgnoreCase) < 0 &&
                    sn.IndexOf("SGRounded", StringComparison.OrdinalIgnoreCase) < 0 &&
                    sn.IndexOf("TitleBorder", StringComparison.OrdinalIgnoreCase) < 0 &&
                    sn.IndexOf("UISprite", StringComparison.OrdinalIgnoreCase) < 0 &&
                    sn.IndexOf("Background", StringComparison.OrdinalIgnoreCase) < 0 &&
                    sn.IndexOf("InputField", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var rt = RectOf(im.transform);
                var b = RectOf(im.transform.parent);
                sb.Append($"  '{sn}' 尺寸={(rt != null ? $"{rt.rect.width:0}x{rt.rect.height:0}" : "?")}"
                          + $" @ppuMul={SafeF(() => im.pixelsPerUnitMultiplier):0.##} type={im.type}"
                          + $" 父='{(im.transform.parent != null ? im.transform.parent.name : "?")}'{(b != null ? $" {b.rect.width:0}x{b.rect.height:0}" : "")}"
                          + (IsOurs(im.transform) ? "  [我们]" : "") + '\n');
                rows++;
            }
            if (rows == 0) sb.Append("  (没找到)\n");
        }
        catch (Exception ex) { sb.Append("  (对照表失败: ").Append(ex.Message).Append(")\n"); }

        return sb.ToString();
    }

    private static int Count<T>(Canvas c) where T : UnityEngine.Component
    {
        try { var a = c.GetComponentsInChildren<T>(true); return a != null ? a.Length : 0; }
        catch { return 0; }
    }

    private static string Top(System.Collections.Generic.Dictionary<string, int> d, int max)
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var kv in d) { if (n++ >= max) { sb.Append(" …"); break; } sb.Append($" {kv.Key}×{kv.Value}"); }
        return sb.Length == 0 ? "-" : sb.ToString();
    }

    private static string TopF(System.Collections.Generic.Dictionary<float, int> d, int max)
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var kv in d) { if (n++ >= max) { sb.Append(" …"); break; } sb.Append($" {kv.Key:0.##}×{kv.Value}"); }
        return sb.Length == 0 ? "-" : sb.ToString();
    }

    /// <summary>取原生 UI 的画布 + 组件度量，并与我们的常量对比，返回可直接写日志的文本。</summary>
    public static string Dump()
    {
        var sb = new StringBuilder();

        sb.Append("== 画布 / 缩放（决定“逻辑尺寸 → 屏幕像素”的倍率）==\n");
        try
        {
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
            if (canvases != null)
            {
                for (int i = 0; i < canvases.Length; i++)
                {
                    var c = canvases[i];
                    if (c == null) continue;
                    CanvasScaler sc = null;
                    try { sc = c.GetComponent<CanvasScaler>(); } catch { }
                    sb.Append($"  '{(c.transform != null ? c.transform.name : "?")}' mode={c.renderMode} order={c.sortingOrder} scaleFactor={c.scaleFactor:0.###} canvasRefPPU={c.referencePixelsPerUnit:0.#}");
                    if (sc != null)
                        sb.Append($" | scaler uiScaleMode={sc.uiScaleMode} ref={sc.referenceResolution.x:0}x{sc.referenceResolution.y:0} screenMatch={sc.screenMatchMode}/{sc.matchWidthOrHeight:0.##} refPPU={sc.referencePixelsPerUnit:0.#}");
                    sb.Append('\n');
                }
            }
        }
        catch (Exception ex) { sb.Append("  (画布枚举失败: ").Append(ex.Message).Append(")\n"); }

        sb.Append("== 游戏原生组件（ESC 菜单按钮 / 其面板 / 其文字）==\n");
        try
        {
            // ⚠ GameObject.Find 只找**激活**对象，而原生 ESC 菜单容器常是未激活的 → 用全量扫（含未激活）
            Transform holder = null;
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            if (trs != null)
            {
                for (int i = 0; i < trs.Length; i++)
                {
                    var t = trs[i];
                    if (t == null) continue;
                    string n = "";
                    try { n = t.name ?? ""; } catch { }
                    if (n.IndexOf("ESC Menu Buttons", StringComparison.OrdinalIgnoreCase) >= 0) { holder = t; break; }
                }
            }
            if (holder == null)
                sb.Append("  没找到 'ESC Menu Buttons'容器（主菜单没加载？）\n");
            else
            {
                var buttons = holder.GetComponentsInChildren<Button>(true);
                var panel = holder.parent as RectTransform;
                if (panel != null)
                    sb.Append($"  面板 '{panel.name}' 尺寸={Size(panel)}\n");
                sb.Append($"  容器 '{holder.name}' 尺寸={(holder as RectTransform != null ? Size(holder as RectTransform) : "-")}；按钮数={buttons.Length}\n");
                for (int i = 0; i < buttons.Length && i < 3; i++)
                    sb.Append("  ").Append(Describe(buttons[i])).Append('\n');
            }

            // 原生典型部件采样：面板底图 / 图标 / 标题文字（看“原生一般多大”）
            sb.Append("  原生典型部件采样（前 8 个有名 sprite 的 Image）：\n");
            var imgs = UnityEngine.Object.FindObjectsOfType<Image>(true);
            int shown = 0;
            for (int i = 0; imgs != null && i < imgs.Length && shown < 8; i++)
            {
                var im = imgs[i];
                if (im == null || im.sprite == null) continue;
                if (IsOurs(im.transform)) continue;
                string sn = "";
                try { sn = im.sprite.name ?? ""; } catch { }
                if (sn.Length == 0) continue;
                var rt = RectOf(im.transform);
                float ppu = 0f;
                try { ppu = im.sprite.pixelsPerUnit; } catch { }
                sb.Append($"    '{sn}' type={im.type} 尺寸={(rt != null ? Size(rt) : "-")}");
                sb.Append($" ppu={ppu:0.#} ppuMul={SafeF(() => im.pixelsPerUnitMultiplier):0.##}\n");
                shown++;
            }
            var texts = UnityEngine.Object.FindObjectsOfType<TMP_Text>(true);
            int ts = 0;
            for (int i = 0; texts != null && i < texts.Length && ts < 5; i++)
            {
                var t = texts[i];
                if (t == null || IsOurs(t.transform)) continue;
                string txt = "";
                try { txt = t.text ?? ""; } catch { }
                if (txt.Length == 0) continue;
                if (txt.Length > 18) txt = txt.Substring(0, 18) + "…";
                sb.Append($"    原生文字 '{txt}' fontSize={t.fontSize:0.##} autoSize={Safe(() => t.enableAutoSizing)} 尺寸={(t.transform as RectTransform != null ? Size(t.transform as RectTransform) : "-")}\n");
                ts++;
            }
        }
        catch (Exception ex) { sb.Append("  (原生组件度量失败: ").Append(ex.Message).Append(")\n"); }

        sb.Append("== 我们（主题常量 / 窗口 / 我们的一个按钮）==\n");
        sb.Append($"  UiTheme: 参考分辨率 {Theme.UiTheme.ReferenceW:0}x{Theme.UiTheme.ReferenceH:0} 窗口 {Theme.UiTheme.WindowW:0}x{Theme.UiTheme.WindowH:0}"
                + $" 标题栏 {Theme.UiTheme.HeaderH:0} 底栏 {Theme.UiTheme.FooterH:0} 行高 {Theme.UiTheme.RowH:0} 控件高 {Theme.UiTheme.ControlH:0} 按钮高 {Theme.UiTheme.ButtonH:0}"
                + $" 字号 标题{Theme.UiTheme.FontTitle}/正文{Theme.UiTheme.FontBody}/小{Theme.UiTheme.FontSmall}\n");
        sb.Append($"  窗口实际尺寸={Menu.UiMenuWindow.WindowSize.x:0}x{Menu.UiMenuWindow.WindowSize.y:0} pos=({Menu.UiMenuWindow.WindowPos.x:0},{Menu.UiMenuWindow.WindowPos.y:0})\n");
        try
        {
            var wr = Menu.UiMenuWindow.WindowRect;
            if (wr != null)
            {
                var bs = wr.GetComponentsInChildren<Button>(true);
                int shown = 0;
                for (int i = 0; i < bs.Length && shown < 3; i++)
                {
                    if (bs[i] == null) continue;
                    sb.Append("  ").Append(Describe(bs[i])).Append('\n');
                    shown++;
                }
            }
        }
        catch (Exception ex) { sb.Append("  (我们的按钮度量失败: ").Append(ex.Message).Append(")\n"); }

        sb.Append("  我们自己的装饰层（素材名带 '@边框' = 正在用切片定义；type=Sliced/ppuMul=2.5 才对得上原生）：\n");
        try
        {
            var wr2 = Menu.UiMenuWindow.WindowRect;
            if (wr2 == null) sb.Append("    （窗口未创建）\n");
            else
            {
                var imgs2 = wr2.GetComponentsInChildren<Image>(true);
                int shown2 = 0;
                for (int i = 0; imgs2 != null && i < imgs2.Length && shown2 < 12; i++)
                {
                    var im = imgs2[i];
                    if (im == null || im.sprite == null) continue;
                    string sn = "";
                    try { sn = im.sprite.name ?? ""; } catch { }
                    var rt = im.transform as RectTransform;
                    sb.Append($"    '{sn}' type={im.type} fillCenter={Safe(() => im.fillCenter)} ppuMul={SafeF(() => im.pixelsPerUnitMultiplier):0.##} 尺寸={(rt != null ? Size(rt) : "-")}\n");
                    shown2++;
                }
                if (shown2 == 0) sb.Append("    （没有任何带 sprite 的 Image —— 全是纯色？）\n");
            }
        }
        catch (Exception ex) { sb.Append("    (我们的装饰层度量失败: ").Append(ex.Message).Append(")\n"); }

        return sb.ToString();
    }

    /// <summary>判断节点是否属于本模组（沿父链找 `OpenNestUIKit_` 前缀）。</summary>
    private static bool IsOurs(Transform t)
    {
        int guard = 0;
        while (t != null && guard++ < 32)
        {
            string n = "";
            try { n = t.name ?? ""; } catch { }
            if (n.StartsWith("OpenNestUIKit_", StringComparison.Ordinal)) return true;
            t = t.parent;
        }
        return false;
    }

    /// <summary>IL2CPP 下 `transform as RectTransform` 会失败（interop 包装类型不匹配）→ 用 GetComponent。</summary>
    private static RectTransform RectOf(Transform t)
    {
        try { return t != null ? t.GetComponent<RectTransform>() : null; }
        catch { return null; }
    }

    private static string Size(RectTransform rt)
    {
        try { return $"{rt.rect.width:0}x{rt.rect.height:0}（delta {rt.sizeDelta.x:0}x{rt.sizeDelta.y:0}）"; }
        catch { return "-"; }
    }

    /// <summary>一条组件摘要：rect 尺寸 + 文字字号/自动尺寸 + 背景素材的 ppu 与倍率。</summary>
    private static string Describe(Button b)
    {
        var sb = new StringBuilder();
        sb.Append("'").Append(b.name).Append("'");
        try { var rt = b.transform as RectTransform; if (rt != null) sb.Append(" 尺寸=").Append(Size(rt)); } catch { }

        try
        {
            var t = b.GetComponentInChildren<TMP_Text>(true);
            if (t != null)
            {
                string fontName = "?";
                try { if (t.font != null) fontName = t.font.name; } catch { }
                sb.Append($" 文字 fontSize={t.fontSize:0.##}");
                sb.Append($" 自动尺寸={Safe(() => t.enableAutoSizing)} 字号min/max={SafeF(() => t.fontSizeMin)}/{SafeF(() => t.fontSizeMax)}");
                sb.Append(" 字体=").Append(fontName);
            }
        }
        catch { }

        try
        {
            var imgs = b.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < imgs.Length; i++)
            {
                var im = imgs[i];
                if (im == null || im.sprite == null) continue;
                string sn = "";
                try { sn = im.sprite.name ?? ""; } catch { }
                float ppu = 0f;
                try { ppu = im.sprite.pixelsPerUnit; } catch { }
                float mul = SafeF(() => im.pixelsPerUnitMultiplier);
                var rt2 = im.transform as RectTransform;
                sb.Append($" 素材='{sn}' ppu={ppu:0.#} ppuMul={mul:0.##} type={im.type} 尺寸={(rt2 != null ? Size(rt2) : "-")}");
                break;
            }
        }
        catch { }

        return sb.ToString();
    }

    private static string Safe(Func<bool> f) { try { return f() ? "是" : "否"; } catch { return "?"; } }
    private static float SafeF(Func<float> f) { try { return f(); } catch { return 0f; } }
}
