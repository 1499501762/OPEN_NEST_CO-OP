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
/// 原生菜单注入器：把 <see cref="NativeMenuBridge"/> 里注册的条目注入**游戏原生 ESC 菜单**
/// （主菜单与游戏内暂停菜单是**两个** `ESC Menu Buttons` 容器 → 全场景遍历，都要注入）。
///
/// 权威时机：Harmony postfix 挂 `MainMenuStateRelay.HandleMainMenuLoaded`
/// （`docs/NATIVE_UI.md` §7 的实证时机）；Harmony 不可用时退化为
/// "桥接的主菜单加载事件 + 每 2 秒轮询补注入"（行为一致，只是时机稍晚）。
///
/// 注入策略（2026-09-13 v3）：
/// 1. 只注入 `ShowInNative=true` 的**顶层**条目；它们**排成一行多列**（默认 3 列、字号 16），
///    插在**设置按钮之后**的同一行里 —— 原生列表只让出 1 行，节距几乎不变；
///    条目超出容量（列数 × 行数）时最后一格变「更多...」，点它进原生页逐级展开；
/// 2. 行外观用**原生样式**（抄设置按钮的底图/字体，混在原生列表里不突兀）；
/// 3. 摆位以**首次见到的原生几何模板**为基准重排（块内节距 ≈36、块间 45~52、行高 40），
///    只压间隔、**不改任何行的尺寸/字号**（旧版“自动缩放高度”会让字穿出格子）；
/// 4. 不克隆原生按钮（避免带原生链接脚本）；
/// 5. 已注入则不重复（条目签名变化时重建）。
/// </summary>
public static class NativeMenuInjector
{
    /// <summary>我们注入的按钮名前缀（重排时用它排除自己）。</summary>
    public const string OurPrefix = "OpenNestUIKit_";

    private const float EscGap = 38f;
    private const float EscBtnH = 38f;
    private const float EscBtnW = 250f;

    private static bool _patched;
    private static bool _injected;
    private static string _signature = "";
    private static float _retryT;
    private static int _blockedLog;

    /// <summary>原生时机（Harmony postfix）是否已挂上。</summary>
    public static bool IsPatched => _patched;

    /// <summary>是否已把条目注入到至少一个原生菜单容器。</summary>
    public static bool IsInjected => _injected;

    /// <summary>附加：patch 原生主菜单加载完成回调（失败不致命，有轮询兜底）。</summary>
    public static void Attach()
    {
        if (_patched)
        {
            CoopLog.Debug("uikit.native", () => "Attach 重复调用（已忽略）");
            return;
        }
        _patched = true;
        try
        {
            bool ok = HarmonyReflect.PatchByTypeName(
                "MainMenuStateRelay", "HandleMainMenuLoaded",
                prefixName: null, postfixName: nameof(PostMainMenuLoaded),
                label: "MainMenuStateRelay.HandleMainMenuLoaded",
                callbacksOn: typeof(NativeMenuInjector));

            CoopLog.Info("uikit.native", () => ok
                ? "原生菜单注入时机已挂上（MainMenuStateRelay.HandleMainMenuLoaded postfix）"
                : "原生菜单 patch 未挂上（退化为事件/轮询注入）");
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.native", () => "Attach failed (degraded): " + ex.Message);
        }
    }

    /// <summary>解挂（宿主关闭时调用）。</summary>
    public static void Detach()
    {
        try { RemoveInjected(); } catch { }
        _injected = false;
        _signature = "";
    }

    /// <summary>Harmony postfix 回调：原生主菜单加载完成（签名与原生方法一致）。</summary>
    public static void PostMainMenuLoaded(string sceneName)
    {
        try
        {
            CoopLog.Info("uikit.native", () => $"原生主菜单加载完成 scene='{sceneName}' → 注入原生菜单项");
            Theme.UiSkin.CaptureAll();
            Inject(force: true);
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.native", () => "PostMainMenuLoaded: " + ex.Message);
        }
    }

    /// <summary>桥接的主菜单事件回调（与 patch 互为兜底）。</summary>
    public static void OnMainMenuLoaded()
    {
        try { Inject(force: false); }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "OnMainMenuLoaded: " + ex.Message); }
    }

    /// <summary>每帧驱动：节流补注入（容器/条目可能后出现）。</summary>
    public static void Tick(float dt)
    {
        _retryT += dt;
        if (_retryT < 2f) return;
        _retryT = 0f;
        try
        {
            if (string.IsNullOrEmpty(Signature()) && NativeMenuBridge.Count == 0) return;   // 没有条目就不用忙
            Inject(force: false);
        }
        catch { }
    }

    /// <summary>条目签名（条目集合变化 → 需要重建）。</summary>
    private static string Signature()
    {
        var tops = NativeMenuBridge.TopLevel();
        if (tops.Length == 0) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < tops.Length; i++) sb.Append(tops[i].Id).Append('|');
        return sb.ToString();
    }

    /// <summary>诊断快照（测试/排查用）：把注入决策链上的每个量都列出来。</summary>
    public static string Diagnose()
    {
        try
        {
            var tops = NativeMenuBridge.TopLevel();
            var containers = FindEscContainers();
            var sb = new System.Text.StringBuilder();
            sb.Append("patch=").Append(_patched).Append(" injected=").Append(_injected)
              .Append(" 全部条目=").Append(NativeMenuBridge.Count)
              .Append(" 顶层=").Append(tops.Length)
              .Append(" 签名='").Append(Signature()).Append('\'')
              .Append(" 容器=").Append(containers.Count);
            for (int i = 0; i < containers.Count; i++)
            {
                var esc = containers[i];
                var er = esc.GetComponent<RectTransform>();
                var tpl = NativeMenuStyler.FindTemplate(esc, "OpenSettingsBtn", "Settings");
                var natives = CollectNativeButtons(esc, out float topY, out int si);
                float gap = EstimateGap(natives, topY);
                float minY = float.MaxValue;
                for (int k = 0; k < natives.Count; k++) minY = Mathf.Min(minY, Y(natives[k]));
                float span = natives.Count == 0 ? 0f : (topY - minY) + EscBtnH;
                float avail = EstimatePanelHeight(esc);
                int free = 0;
                for (int k = 0; k < natives.Count; k++)
                    if (natives[k].gameObject != null && !natives[k].gameObject.activeSelf) free++;
                sb.Append("\n  [").Append(i).Append("] '").Append(esc.name).Append("' 容器active=").Append(esc.gameObject.activeSelf)
                  .Append(" 矩形=").Append(er != null ? $"{er.rect.width:0}x{er.rect.height:0}" : "-")
                  .Append(" 模板=").Append(tpl != null ? tpl.name : "无")
                  .Append(" 原生按钮=").Append(natives.Count)
                  .Append(" 设置索引=").Append(si)
                  .Append(" 节距=").Append($"{gap:0.#}")
                  .Append(" 顶部y=").Append($"{topY:0.#}")
                  .Append(" 原生跨高=").Append($"{span:0}")
                  .Append(" 面板可用高=").Append($"{avail:0}")
                  .Append(" 非激活槽位=").Append(free)
                  .Append(" 放得下=").Append(span + tops.Length * gap <= avail + 0.5f);
            }
            return sb.ToString();
        }
        catch (Exception ex) { return "Diagnose failed: " + ex.Message; }
    }

    /// <summary>执行注入（幂等）。<paramref name="force"/> = 无视签名强制重建。</summary>
    public static void Inject(bool force)
    {
        string sig = Signature();
        if (sig.Length == 0)
        {
            if (_injected) { RemoveInjected(); _injected = false; _signature = ""; }
            return;
        }
        if (!force && _injected && sig == _signature) return;

        try
        {
            var allTops = NativeMenuBridge.TopLevel();
            var topsList = new List<NativeMenuEntry>();
            for (int i = 0; i < allTops.Length; i++)
                if (allTops[i] != null && allTops[i].ShowInNative) topsList.Add(allTops[i]);   // 只注入“要在原生列表里占一行”的
            if (topsList.Count == 0)
            {
                CoopLog.Debug("uikit.native", () => $"没有标记 ShowInNative 的顶层条目（共 {allTops.Length} 个顶层）→ 不注入");
                return;
            }
            var tops = topsList.ToArray();
            var containers = FindEscContainers();
            if (containers.Count == 0)
            {
                CoopLog.Debug("uikit.native", () => "未找到 'ESC Menu Buttons' 容器（可能不在主菜单/菜单未创建）");
                return;
            }

            int done = 0;
            for (int i = 0; i < containers.Count; i++)
            {
                if (InjectInto(containers[i], tops)) done++;
            }
            if (done > 0)
            {
                _injected = true;
                _signature = sig;
                CoopLog.Info("uikit.native", () => $"注入完成：{tops.Length} 个原生菜单项 → {done} 个 ESC 容器");
            }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.native", () => "inject failed: " + ex.Message);
        }
    }

    /// <summary>全场景找 `ESC Menu Buttons` 容器（主菜单 + 游戏内暂停菜单可能各有一个）。</summary>
    private static List<Transform> FindEscContainers()
    {
        var list = new List<Transform>();
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null) continue;
                if (!string.Equals(t.name, "ESC Menu Buttons", StringComparison.Ordinal)) continue;
                list.Add(t);
            }
        }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "FindEscContainers: " + ex.Message); }
        return list;
    }

    /// <summary>向一个 ESC 容器注入全部顶层条目（并对原生按钮做 slot 让位重排）。</summary>
    private static bool InjectInto(Transform esc, NativeMenuEntry[] tops)
    {
        try
        {
            var escRt = esc.GetComponent<RectTransform>();
            Button template = NativeMenuStyler.FindTemplate(esc, "OpenSettingsBtn", "Settings");
            if (template == null)
            {
                if (_blockedLog++ < 3) CoopLog.Warn("uikit.native", () => $"'{esc.name}' 无按钮模板（跳过）");
                return false;
            }

            // 1) 清掉旧注入（条目变化时重建）
            RemoveInjected(esc);

            // 2) 收集原生按钮（直接子物体，按 y 降序 = 从上到下）
            var natives = CollectNativeButtons(esc, out float topY, out int settingsIdx);
            if (natives.Count == 0) return false;

            // 3) 建我们的按钮（**原生样式**：抄设置按钮的底图/字体/内边距，混在原生列表里不突兀）
            //
            // ⚠ 2026-09-13 v4（用户：“原生菜单多列宽度压缩又导致显示不全了，这里的压缩应该也包括上面的
            //    Settings，所以最多应该同时显示 5 个注入按钮 + 1 个原生 Settings，三行，最后一个如果
            //    超过 5 个变为 More...”）：
            //    · 格子从“一行 3 列（78px 宽，中文被省略号截断）”改成 **2 列 × 最多 3 行**
            //      → 格子宽 = (250 - 8) / 2 = 121px，字号 18，中文标签放得下；
            //    · **最多 5 个我们的格子**（含「更多...」）→ 5 格正好占 2+2+1 = **三行**；
            //    · 原生 **Settings 那一行不动尺寸**（还是整行 250 宽），它作为“+1”计入同屏容量；
            //    · 因此整个块最多 3 行高，**压缩必须把 Settings 及其上方原生行一起算进去**（见下方第 4 步）。
            var actives = new List<Button>();
            for (int i = 0; i < natives.Count; i++)
                if (natives[i].gameObject != null && natives[i].gameObject.activeSelf) actives.Add(natives[i]);
            if (actives.Count == 0) actives = natives;               // 退化：全都非激活（异常情况）

            int cells = GridCells;
            bool overflow = tops.Length > cells;
            int shown = overflow ? cells - 1 : Mathf.Min(tops.Length, cells);   // 溢出时最后一格留给「更多...」
            if (shown < 0) shown = 0;

            var mine = new List<Button>();
            var mineEntries = new List<NativeMenuEntry>();
            float cellW = (EscBtnW - CellGap * (GridCols - 1)) / GridCols;
            for (int i = 0; i < shown; i++)
            {
                var e = tops[i];
                string label = UiKitLoc.T(e.Title, string.IsNullOrEmpty(e.TitleEn) ? e.Title : e.TitleEn);
                var entry = e;
                var escRef = esc;
                var tplRef = template;
                var btn = NativeMenuStyler.CreateNativeButton(esc, OurPrefix + Sanitize(e.Id), label, template,
                    () => OnEntryClicked(entry, escRef, tplRef, label), cellW, GridCellH);
                if (btn == null) continue;
                StyleGridCell(btn, cellW, label);
                mine.Add(btn);
                mineEntries.Add(e);
            }
            if (overflow || mine.Count == 0)
            {
                string more = UiKitLoc.T("更多...", "More...");
                var escRef = esc;
                var tplRef = template;
                var btn = NativeMenuStyler.CreateNativeButton(esc, OurPrefix + "more", more, template,
                    () => ShowMorePage(escRef, tplRef), cellW, GridCellH);
                if (btn != null)
                {
                    StyleGridCell(btn, cellW, more);
                    mine.Add(btn);
                    mineEntries.Add(null);                            // 占位（「更多...」不对应具体条目）
                }
            }
            if (mine.Count == 0) return false;

            // 我们的块占几行（每行 GridCols 个格子）
            int gridRows = Mathf.Max(1, Mathf.CeilToInt(mine.Count / (float)GridCols));
            if (gridRows > MaxGridRows) gridRows = MaxGridRows;
            float ourRowPitch = GridRowPitch;                              // 块内行距（格子 30 + 3 缝 = 33）
            float extraRows = (gridRows - 1) * ourRowPitch;                // 块内部额外占的高度（要计入压缩）

            // 4) 摆位（2026-09-13 v3：**一行多列插到锚点后面** + 按原生节距结构重排）
            //
            // 用户反馈三条：① 我们的行应该出现在**设置按钮（Settings）后面**，不是列表末尾；
            //              ② 旧算法“自动缩放高度”把行压得比字还矮 → 字穿出格子；
            //              ③ 只压间隔显示效果也差 → 改成**一行多列**（见上面第 3 步），只占一行。
            //
            // 做法：
            //   · **位置**：默认插在 `OpenSettingsBtn` 之后（`NativeMenuEntry.InsertAfter` 可换锚点；空串 = 追加到最后）；
            //     同一锚点的多条按注册顺序依次排；我们的格子**共用一行**（列内水平并排）。
            //   · **基准**：以**第一次见到的原生几何**为模板（`GetTemplate`：块内节距 ≈36 / 块间 45~52 / 行高 40）。
            //     不拿“当前 y”当基准 —— 重排会改原生 y，反复以当前值当基准会把节距越压越小。
            //   · **节距表**：两端都是原生 → 照抄模板里这两行的节距；我们的行 → 用块内节距（≈36）。
            //   · **压缩**（只压间隔，**绝不改任何行的尺寸/字号**）：① 块间 > 40 先压到 40；
            //     ② 再整体等比压，节距**下限 30**；③ 连下限都保不住才继续压并告警。
            var tpl = GetTemplate(esc, actives);
            var slots = new List<Slot>();
            for (int i = 0; i < actives.Count; i++) slots.Add(new Slot { Name = actives[i].name, Native = actives[i] });

            var anchorUsed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // ⚠ 我们的**所有格子共用一行**（水平并排）：只占一个槽位 → 原生列表只让出 1 行。
            {
                string anchor = mineEntries.Count > 0 && mineEntries[0] != null ? mineEntries[0].InsertAfter : null;
                if (string.IsNullOrEmpty(anchor)) anchor = DefaultAnchor;
                int used = anchorUsed.TryGetValue(anchor, out int u) ? u : 0;
                int at = IndexAfterAnchor(slots, anchor) + used;
                slots.Insert(Mathf.Clamp(at, 0, slots.Count), new Slot { Name = "OpenNestUIKit_grid", Grid = mine });
                anchorUsed[anchor] = used + 1;
            }

            float avail = EstimatePanelHeight(esc);
            const float Edge = 8f;
            float panelBottom = -(avail * 0.5f) + Edge;
            float top = tpl.Ys.Count > 0 ? tpl.Ys[0] : topY;                          // 顶 = 原生列表首行
            float intra = IntraPitch(tpl);

            var pitch = new List<float>();
            for (int i = 0; i < slots.Count - 1; i++)
            {
                bool bothNative = slots[i].Native != null && slots[i + 1].Native != null;
                pitch.Add(bothNative ? TplPitch(tpl, slots[i].Name, slots[i + 1].Name, intra) : intra);
            }

            float span = Sum(pitch);
            float rowH = Mathf.Max(tpl.RowH, GridCellH);                              // 最后一行“框底 → 面板底”的余量按较高的框算
            float maxSpan = top - (panelBottom + rowH * 0.5f);                        // 首行中心 → 末行中心
            // ⚠ v4：我们的块是**多行**（最多 3 行）→ 块内部额外占的高度也要从可用高度里扣掉，
            //       否则算下来“放得下”，实际末行会掉出面板底（用户报的“显示不全”）。
            float totalWithOurs = Sum(pitch) + extraRows;
            if (totalWithOurs > maxSpan && span > 0f)
            {
                for (int i = 0; i < pitch.Count; i++) if (pitch[i] > BlockCap) pitch[i] = BlockCap;   // ① 块间空档先让位
                totalWithOurs = Sum(pitch) + extraRows;
            }

            // ② **整体等比缩小**（v5 关键改动）：以前只压间隔 → 原生 40 高的按钮被挤到 30 的节距上、互相压 10px，
            //    用户：“各个按钮没有对应的缩小一些，高度不够挤在一起了”。
            //    现在把原生行的**框和字一起**按 k 缩（见 ScaleNativeRow）→ 节距小了、框也小了，不再互相压。
            float nativeSpan = Sum(pitch);
            float scale = 1f;
            if (totalWithOurs > maxSpan && nativeSpan > 1f)
                scale = Mathf.Clamp((maxSpan - extraRows) / nativeSpan, MinNativeScale, 1f);
            if (scale < 0.999f)
                for (int i = 0; i < pitch.Count; i++) pitch[i] *= scale;
            span = Sum(pitch);
            bool tight = Sum(pitch) + extraRows > maxSpan + 0.5f;                     // 真的还是放不下（k 已到下限）→ 告警

            // ②' 我们的格子**也跟着原生一起缩**（用户：“一行两列按钮之后整个行的宽度超过了原生宽度，不一致…需要统一缩小一些”）：
            //    原生行缩到 250*k 宽 → 我们那一行（2 列）也必须正好 250*k，否则会比原生行突出一截。
            //    格子宽/高/间距/字号全部乘 k，并把块内行距也跟着缩。
            float cellWs = cellW * scale;
            float cellHs = GridCellH * scale;
            float gapS = CellGap * scale;
            float fontS = Mathf.Max(6f, GridFont * scale);
            float ourRowPitchS = cellHs + 3f * scale;
            for (int i = 0; i < mine.Count; i++) ApplyGridMetrics(mine[i], cellWs, cellHs, fontS);

            // 5) 摆位：原生行改 y + 按 k 缩尺寸/字号；我们的块按 2 列 × gridRows 行铺开
            for (int i = 0; i < slots.Count; i++)
                if (slots[i].Native != null) ScaleNativeRow(tpl, slots[i].Native, scale);
            float y = top;
            float firstOurY = 0f, lastOurY = 0f;
            bool gotOurs = false;
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                if (s.Native != null) Place(s.Native, y);
                else if (s.Grid != null)
                {
                    for (int k = 0; k < s.Grid.Count; k++)
                    {
                        int row = k / GridCols, col = k % GridCols;
                        int inRow = Mathf.Min(GridCols, s.Grid.Count - row * GridCols);      // 末行可能只有 1 格
                        float rowW = inRow * cellWs + gapS * Mathf.Max(0, inRow - 1);
                        float x0 = -rowW * 0.5f + cellWs * 0.5f;                              // 每行各自居中
                        float yy = y - row * ourRowPitchS;
                        Place(s.Grid[k], yy, cellHs, x0 + col * (cellWs + gapS));
                        if (!gotOurs) { firstOurY = yy; gotOurs = true; }
                        lastOurY = yy;
                    }
                    y -= (Mathf.Max(1, Mathf.CeilToInt(s.Grid.Count / (float)GridCols)) - 1) * ourRowPitchS;
                }
                if (i < pitch.Count) y -= pitch[i];
            }
            float bottomEdge = lastOurY - cellHs * 0.5f;
            if (!gotOurs) { firstOurY = lastOurY = bottomEdge = 0f; }

            CoopLog.Info("uikit.native", () => $"'{esc.name}' 注入 {mine.Count} 项（原生样式；插在 '{mineEntries[0].InsertAfter ?? DefaultAnchor}' 之后，按原生节距结构重排）："
                + $"原生 {actives.Count} 行 模板节距={intra:0.#} 顶={top:0} 我们首行={firstOurY:0} 我们末行={lastOurY:0} 底={bottomEdge:0} 面板底={panelBottom:0} 面板={avail:0}"
                + $" 总缩={span:0} 可用={maxSpan:0} 块内额外={extraRows:0} 压缩={scale:0.###} 行高(原生/我们)={tpl.RowH:0}/{GridCellH:0} 格子={cellW:0.#}x{GridCellH:0}×{gridRows}行");
            if (tight)
                CoopLog.Warn("uikit.native", () => $"'{esc.name}' 行太多：整体已缩到下限 {MinNativeScale:0.##} 倍（均节距 {(pitch.Count > 0 ? span / pitch.Count : 0):0.#}）→ 行会有些重叠，建议减少注入项或改用二级菜单");
            if (bottomEdge < panelBottom + 2f)
                CoopLog.Warn("uikit.native", () => $"'{esc.name}' 我们的块底边 {bottomEdge:0} 已到/越过面板底 {panelBottom:0}（面板高 {avail:0}）");

            LogLayout(esc, mine);
            return true;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.native", () => $"InjectInto('{esc.name}'): {ex.Message}");
            return false;
        }
    }

    // ==================================================================
    //  原生布局模板 + 重排辅助（“正确重排”的基准）
    // ==================================================================

    /// <summary>默认插入锚点（原生按钮名片段）：**设置按钮之后**（用户要求：“应该在 Settings 后面”）。</summary>
    private const string DefaultAnchor = "OpenSettingsBtn";

    /// <summary>我们那一行的列数（v4：2 列 → 格子 121px，中文标签放得下）。</summary>
    private const int GridCols = 2;

    /// <summary>我们最多占几行（v4：3 行 → 2+2+1 = 同屏 5 个我们的格子 + 1 个原生 Settings）。</summary>
    private const int MaxGridRows = 3;

    /// <summary>我们最多同时显示几个格子（含「更多...」；用户：“最多应该同时显示 5 个注入按钮”）。</summary>
    private const int GridCells = 5;

    /// <summary>格子之间的水平间隙。</summary>
    private const float CellGap = 8f;

    /// <summary>
    /// 格子高度（v5：30，原来是 38）。
    /// ⚠ 2026-09-13 用户：“各个按钮没有对应的缩小一些，高度不够挤在一起了”：
    ///   原生行被压缩后节距只剩 ~30，而原生按钮是 40 高、我们原来是 38 高 → 相邻行的框互相压 10px，看着挤成一团。
    ///   现在我们的格子缩到 **30 高**（≈压缩后的节距）→ 正好一间一间排开，不再跟上下行叠。
    /// </summary>
    private const float GridCellH = 30f;

    /// <summary>块内行距（格子高 + 3 的缝）。</summary>
    private const float GridRowPitch = GridCellH + 3f;

    /// <summary>
    /// 格子里的字号（v5：16，原来 18）；高度缩小后字号同步缩小，保证“框小、字也小”。
    /// ⚠ 字号与行高强相关（CourierPrime 行高 ≈ 1.467×字号）：16 → ~23.5px；格子内文字框垂直铺满（见 `StyleGridCell`），
    ///   30px 高足够放下，不会被 TMP 的省略号截断把整行丢掉。
    /// </summary>
    private const float GridFont = 16f;

    /// <summary>块间间隔上限：超过它的间隔在收缩时先让位（原生块间 45~52）。</summary>
    private const float BlockCap = 40f;

    /// <summary>
    /// 原生行（尺寸+字号）整体缩小的下限倍数。
    /// 用户：“各个按钮没有对应的缩小一些，高度不够挤在一起了” → 节距缩小后框也要缩；但缩太多会看不清，
    /// 到下限就接受轻微重叠并告警（<see cref="MinPitch"/> 时代的下限是节距 30）。
    /// </summary>
    private const float MinNativeScale = 0.72f;

    /// <summary>节距下限（仅用于诊断文案/旧口径参考）。</summary>
    private const float MinPitch = 30f;

    /// <summary>一个槽位：原生行，或我们那一行（多个格子）。</summary>
    private sealed class Slot
    {
        public string Name;
        public Button Native;
        public List<Button> Grid;
    }

    /// <summary>
    /// 原生布局模板：**每个容器只在第一次见到时记一次**（那一刻就是游戏自己排的位置），
    /// 之后重排一律以它为基准 —— 否则“拿重排后的 y 当基准”会把节距一次次压小（用户实测到过 0.66 倍那种）。
    /// 行集合变化（原生激活的行数变了，例如进任务后多出几行）时才重新记录。
    /// </summary>
    private sealed class NativeTemplate
    {
        public readonly List<string> Names = new();
        public readonly List<float> Ys = new();
        public float RowH = 40f;

        /// <summary>每行的**原始尺寸**（重排把整体缩小 <see cref="MinNativeScale"/>～1 倍时按它算，避免反复重算越缩越小）。</summary>
        public readonly Dictionary<string, Vector2> Size = new(StringComparer.Ordinal);

        /// <summary>每行下所有文字的**原始字号**（与尺寸同比例缩 —— 只缩框不缩字就会“字穿出格子”，用户报过）。</summary>
        public readonly Dictionary<string, List<float>> Fonts = new(StringComparer.Ordinal);
    }

    private static readonly Dictionary<Transform, NativeTemplate> _templates = new();

    private static NativeTemplate GetTemplate(Transform esc, List<Button> actives)
    {
        NativeTemplate t;
        if (_templates.TryGetValue(esc, out t) && t != null && t.Names.Count == actives.Count)
        {
            bool same = true;
            for (int i = 0; i < t.Names.Count; i++)
            {
                if (string.Equals(t.Names[i], actives[i].name, StringComparison.Ordinal)) continue;
                same = false;
                break;
            }
            if (same) return t;
        }

        t = new NativeTemplate();
        for (int i = 0; i < actives.Count; i++)
        {
            string nm = actives[i].name;
            t.Names.Add(nm);
            t.Ys.Add(Y(actives[i]));
            try
            {
                var r = actives[i].GetComponent<RectTransform>();
                if (r != null)
                {
                    if (!t.Size.ContainsKey(nm)) t.Size[nm] = r.sizeDelta;
                    if (r.sizeDelta.y > 1f && r.sizeDelta.y < 120f) t.RowH = Mathf.Max(t.RowH, r.sizeDelta.y);
                }
            }
            catch { }
            try
            {
                var fs = new List<float>();
                var ts = actives[i].GetComponentsInChildren<TMPro.TMP_Text>(true);
                for (int k = 0; ts != null && k < ts.Length; k++) if (ts[k] != null) fs.Add(ts[k].fontSize);
                if (!t.Fonts.ContainsKey(nm)) t.Fonts[nm] = fs;
            }
            catch { }
        }
        _templates[esc] = t;
        CoopLog.Debug("uikit.native", () => $"'{esc.name}' 记录原生布局模板：{actives.Count} 行 行高={t.RowH:0} 顶={t.Ys[0]:0} 末行={t.Ys[t.Ys.Count - 1]:0}");
        return t;
    }

    /// <summary>
    /// 把一行**原生**按钮整体按 <paramref name="k"/> 缩小（尺寸 + 字号同比例）。
    ///
    /// ⚠ 2026-09-13 用户：“各个按钮没有对应的缩小一些，高度不够挤在一起了”：
    ///   我们的注入项要占地方 → 原生列表的**节距**被压小（40 的框挤到 30 的节距上）→ 原生按钮之间互相压 10px、
    ///   看着挤成一团。光压间隔是不够的，**框和字也要跟着缩**（只缩框不缩字 → 字穿出格子，之前就报过）。
    ///   原值一律从模板里取（不是取“当前值”），所以反复注入不会越缩越小。
    /// </summary>
    private static void ScaleNativeRow(NativeTemplate t, Button b, float k)
    {
        if (b == null || t == null) return;
        try
        {
            Vector2 orig;
            if (!t.Size.TryGetValue(b.name, out orig) || orig.x < 1f || orig.y < 1f) return;
            var r = b.GetComponent<RectTransform>();
            if (r != null) r.sizeDelta = new Vector2(orig.x * k, orig.y * k);      // 等比缩（居中 → 位置不变）

            if (t.Fonts.TryGetValue(b.name, out var fs) && fs != null && fs.Count > 0)
            {
                var ts = b.GetComponentsInChildren<TMPro.TMP_Text>(true);
                int n = Mathf.Min(ts != null ? ts.Length : 0, fs.Count);
                for (int i = 0; i < n; i++)
                {
                    var tx = ts[i];
                    if (tx == null) continue;
                    try { tx.fontSize = Mathf.Max(6f, fs[i] * k); } catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>模板里的“块内节距”（相邻行差值的中位数，去掉块间的大间隔），夹到 32~40。</summary>
    private static float IntraPitch(NativeTemplate t)
    {
        try
        {
            var diffs = new List<float>();
            for (int i = 1; i < t.Ys.Count; i++)
            {
                float d = t.Ys[i - 1] - t.Ys[i];
                if (d > 20f && d <= 40f) diffs.Add(d);            // 只看块内（块间 45~52 排除）
            }
            if (diffs.Count == 0) return EscGap;
            diffs.Sort();
            return Mathf.Clamp(diffs[diffs.Count / 2], 32f, 40f);
        }
        catch { return EscGap; }
    }

    /// <summary>模板里两行之间的原始节距（找不到或不相邻 → 返回 <paramref name="fallback"/>）。</summary>
    private static float TplPitch(NativeTemplate t, string aName, string bName, float fallback)
    {
        try
        {
            int ia = t.Names.IndexOf(aName), ib = t.Names.IndexOf(bName);
            if (ia >= 0 && ib == ia + 1)
            {
                float d = t.Ys[ia] - t.Ys[ib];
                if (d > 4f && d < 200f) return d;
            }
        }
        catch { }
        return fallback;
    }

    /// <summary>锚点行之后的插入下标（找不到锚点 → 追加到最后，不乱插）。</summary>
    private static int IndexAfterAnchor(List<Slot> slots, string anchor)
    {
        if (string.IsNullOrEmpty(anchor)) return slots.Count;
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Native == null) continue;
            if (slots[i].Name.IndexOf(anchor, StringComparison.OrdinalIgnoreCase) >= 0) return i + 1;
        }
        return slots.Count;
    }

    private static float Sum(List<float> v)
    {
        float s = 0f;
        for (int i = 0; i < v.Count; i++) s += v[i];
        return s;
    }

    /// <summary>面板可用高度：从 ESC 容器往上找第一个“像面板”的祖先（宽≥200 且高 200~1200，例 300x400）；找不到返回 400。</summary>
    private static float EstimatePanelHeight(Transform esc)
    {
        try
        {
            var t = esc != null ? esc.parent : null;
            for (int d = 0; t != null && d < 6; d++)
            {
                var rt = t.GetComponent<RectTransform>();
                if (rt != null)
                {
                    float w = rt.rect.width, h = rt.rect.height;
                    if (w >= 200f && h >= 200f && h <= 1200f) return h;
                }
                t = t.parent;
            }
        }
        catch { }
        return 400f;
    }

    private static void OnEntryClicked(NativeMenuEntry e, Transform esc, Button template, string label)
    {
        try
        {
            if (e.OnClick != null) { e.OnClick(); return; }

            // 叶子条目（没有子级）→ **直开目标页**，不要再先进原生页 —— 否则
            // “ESC 列表 → 原生页 → 窗口 → 子页” 层层嵌套（用户反馈：“层层嵌套的显示效果太差了”）。
            // 只有带子级的条目（例如本库自己的「模组 UI 库」）才进原生页逐级展开。
            bool hasKids = false;
            try { var kids = NativeMenuBridge.ChildrenOf(e.Id); hasKids = kids != null && kids.Length > 0; } catch { }
            if (!hasKids && !string.IsNullOrEmpty(e.PageId))
            {
                Menu.UiMenuWindow.Open(e.PageId);
                return;
            }

            // 带子级：在原生面板里打开**原生样式的次级菜单页**（照抄原生 Settings 页的切页方式）
            if (NativeMenuPage.Show(esc, template, label)) return;
            Menu.UiMenuWindow.Open(e.PageId);   // 退路：自建画布窗口
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.native", () => $"entry '{e.Id}' click failed: {ex.Message}");
        }
    }

    /// <summary>收集原生按钮（排除我们自己注入的；按 y 降序）。同时给出顶部 y 与设置按钮索引。</summary>
    private static List<Button> CollectNativeButtons(Transform esc, out float topY, out int settingsIdx)
    {
        var list = new List<Button>();
        topY = 0f;
        settingsIdx = -1;
        try
        {
            var all = esc.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var b = all[i];
                if (b == null) continue;
                if (b.name.StartsWith(OurPrefix, StringComparison.Ordinal)) continue;
                if (!NativeMenuStyler.IsDirectChildOf(b.transform, esc)) continue;
                var r = b.GetComponent<RectTransform>();
                if (r == null) continue;
                list.Add(b);
                if (r.anchoredPosition.y > topY) topY = r.anchoredPosition.y;
            }
            list.Sort((a, b) => Y(b).CompareTo(Y(a)));   // y 降序 = 从上到下（原生菜单顺序）
            for (int i = 0; i < list.Count; i++)
            {
                string n = list[i].name ?? "";
                if (n.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0) { settingsIdx = i; break; }
            }
        }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "CollectNativeButtons: " + ex.Message); }
        return list;
    }

    private static float Y(Button b)
    {
        try
        {
            var r = b.GetComponent<RectTransform>();
            return r != null ? r.anchoredPosition.y : 0f;
        }
        catch { return 0f; }
    }

    private static float EstimateGap(List<Button> natives, float topY)
    {
        try
        {
            if (natives.Count >= 2)
            {
                // 取相邻按钮 y 差的中位数（原生菜单间距），夹到合理范围
                var ys = new List<float>();
                for (int i = 0; i < natives.Count; i++) ys.Add(Y(natives[i]));
                ys.Sort((a, b) => b.CompareTo(a));
                var diffs = new List<float>();
                for (int i = 1; i < ys.Count; i++)
                {
                    float d = ys[i - 1] - ys[i];
                    if (d > 4f && d < 200f) diffs.Add(d);
                }
                if (diffs.Count > 0)
                {
                    diffs.Sort();
                    float median = diffs[diffs.Count / 2];
                    return Mathf.Clamp(median, 30f, 60f);
                }
            }
        }
        catch { }
        return EscGap;
    }

    /// <summary>只改 y（尺寸/锚点/宽度全部保留）——给**原生行**用。</summary>
    private static void Place(Button b, float y)
    {
        try
        {
            var r = b.GetComponent<RectTransform>();
            if (r == null) return;
            r.anchoredPosition = new Vector2(r.anchoredPosition.x, y);
        }
        catch { }
    }

    /// <summary>改 y + 行高（宽度只在我们自己的按钮上补齐）——给**我们自己的行**用。</summary>
    private static void Place(Button b, float y, float h)
        => Place(b, y, h, float.NaN);

    /// <summary>改 y + 行高 + x（格子并排用）。<paramref name="x"/> 为 NaN = 只居中不动 x。</summary>
    private static void Place(Button b, float y, float h, float x)
    {
        try
        {
            var r = b.GetComponent<RectTransform>();
            if (r == null) return;
            var sd = r.sizeDelta;
            r.sizeDelta = new Vector2(sd.x > 1f ? sd.x : EscBtnW, h);
            float px = float.IsNaN(x) ? r.anchoredPosition.x : x;
            r.anchoredPosition = new Vector2(px, y);
        }
        catch { }
    }

    /// <summary>格子样式（创建时按**未缩放**的尺寸先套一遍；<see cref="InjectInto"/> 算出整体倍率 k 后会再调
    /// <see cref="ApplyGridMetrics"/> 用缩放后的尺寸/字号覆盖）。</summary>
    private static void StyleGridCell(Button b, float cellW, string text)
    {
        ApplyGridMetrics(b, cellW, GridCellH, GridFont);
        try
        {
            if (b == null) return;
            var tmp = b.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
            if (tmp != null) tmp.text = text;                            // 重设一次让省略号生效
        }
        catch { }
    }

    /// <summary>
    /// 格子度量：**尺寸 + 字号 + 文字框**一次定好。这是三个坑的交汇点，改动前先读完：
    ///
    /// ⚠⚠⚠ ① “ESC 菜单里注入项没有字”的真因（2026-09-13 定位）：
    ///   文字框原来照抄模板内边距 → 格子 121x38 只剩 **81x18**；而 CourierPrime **行高 ≈ 1.467×字号**
    ///   （18 号 ≈ 26.4px）→ 18 < 26.4，TMP 在 `Ellipsis`（截断类）模式下**整行不画**
    ///   （`行数=0 字符=0`，底图照画 → “白框没字”）。**必须让文字框垂直铺满格子**。
    ///
    /// ⚠⚠ ② 文字被省略号截掉（用户：“里面的字显示不全”）：模板是 250 宽的行（左右各 20px 内边距），
    ///   我们格子只有 ~110 宽，再照抄 20px 就只剩 ~68px → 中文标签必然截断。
    ///   → 左右内边距按格子宽度的 6% 给（下限 3px）。
    ///
    /// ⚠ ③ 整行宽度要和原生行一致（用户：“一行两列按钮之后整个行的宽度超过了原生宽度”）：由调用方
    ///   把 `w/h/font` 都乘上整体倍率 k（原生行缩多少，我们这行也缩多少）。
    /// </summary>
    private static void ApplyGridMetrics(Button b, float w, float h, float font)
    {
        try
        {
            if (b == null) return;
            var rt = b.GetComponent<RectTransform>();
            if (rt != null) rt.sizeDelta = new Vector2(w, h);

            var tmp = b.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
            if (tmp == null) return;
            tmp.fontSize = font;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TMPro.TextOverflowModes.Ellipsis;

            var tr = tmp.rectTransform;
            if (tr != null)
            {
                float inset = Mathf.Clamp(w * 0.06f, 3f, 20f);
                tr.anchorMin = Vector2.zero;
                tr.anchorMax = Vector2.one;
                tr.offsetMin = new Vector2(inset, 0f);      // 垂直 0 = 铺满（见 ①）
                tr.offsetMax = new Vector2(-inset, 0f);
            }
        }
        catch { }
    }

    /// <summary>「更多...」：进我们的原生页（列出全部条目，可逐级进入）——只占一行时靠它装下多出来的入口。</summary>
    private static void ShowMorePage(Transform esc, Button template)
    {
        try
        {
            if (NativeMenuPage.Show(esc, template, UiKitLoc.T("更多", "More"))) return;
            Menu.UiMenuWindow.Open("home");
        }
        catch (Exception ex) { CoopLog.Warn("uikit.native", () => "ShowMorePage: " + ex.Message); }
    }

    private static void RemoveInjected()
    {
        try
        {
            var containers = FindEscContainers();
            for (int i = 0; i < containers.Count; i++) RemoveInjected(containers[i]);
        }
        catch { }
    }

    private static void RemoveInjected(Transform esc)
    {
        try
        {
            var all = esc.GetComponentsInChildren<Transform>(true);
            for (int i = all.Length - 1; i >= 0; i--)
            {
                var t = all[i];
                if (t == null) continue;
                if (!t.name.StartsWith(OurPrefix, StringComparison.Ordinal)) continue;
                if (!NativeMenuStyler.IsDirectChildOf(t, esc)) continue;
                UnityEngine.Object.Destroy(t.gameObject);
            }
        }
        catch { }
    }

    /// <summary>几何日志（注入后核对：所有按钮均匀、无重叠）。</summary>
    private static void LogLayout(Transform esc, List<Button> mine)
    {
        try
        {
            if (!CoopLog.Level.Equals(LogLevel.Debug) && CoopLog.Level > LogLevel.Debug) return;
            var sb = new System.Text.StringBuilder("ESC 布局：");
            var all = esc.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var b = all[i];
                if (b == null || !NativeMenuStyler.IsDirectChildOf(b.transform, esc)) continue;
                var r = b.GetComponent<RectTransform>();
                sb.Append("\n  ").Append(b.name).Append(" y=").Append(r != null ? r.anchoredPosition.y.ToString("0.#") : "?");
            }
            string s = sb.ToString();
            CoopLog.Debug("uikit.native", () => s);
        }
        catch { }
    }

    /// <summary>按钮名安全化（原生对象名不能含某些字符；只保留字母数字与下划线）。</summary>
    private static string Sanitize(string id)
    {
        if (string.IsNullOrEmpty(id)) return "entry";
        var sb = new System.Text.StringBuilder(id.Length);
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }
        return sb.ToString();
    }
}
