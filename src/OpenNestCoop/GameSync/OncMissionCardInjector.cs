using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using OpenNestCoop.Core;
using OpenNestCore.Tasks;
#if MELONLOADER
using SleepyNodes = Il2CppSleepyNodes;
using Localisation = Il2CppLocalisation;
#endif
#if !MELONLOADER
using TMPro;
#else
using TMPro = Il2CppTMPro;
#endif
namespace OpenNestCoop.GameSync;

/// <summary>
/// 自定义任务选任务面板——**创建与原生同构的 3D 任务卡片**（克隆模板卡片作 3D 骨架，不继承原生任务/状态）。
///
/// 反编译结论（Cpp2IL ISIL）：原生卡片**不是运行时脚本 new 出来的**，是场景预置的 3D 世界对象
/// （组件 = MapCard + Interactable，无 Canvas/UGUI）；`MapCardManager.UpdateMapCards` 只把战役任务
/// 绑定到场景卡片并刷新解锁/完成/奖章状态，全程序集无 AddComponent&lt;MapCard&gt; 调用点。
///
/// 因此自定义卡片用 **Instantiate 克隆一张原生模板卡片**（保留背景 3D 网格 + TMP 标题 + MapCard/Interactable
/// 组件与点击链），然后：清除原生 Mission/Campaign 绑定 → 改标题/描述 → 隐藏奖章/检查点状态 → 定位到模板下方
/// → 命名 `OncCustom_&lt;id&gt;` 标记。点击走原生 Interactable → ActivateMission，前缀 patch 拦截自定义卡片
/// 转 <see cref="OncMissionBridge.Start(id)"/>，其余放行原生。
/// 位置/尺寸/标题色从任务脚本 <see cref="OncMission.Card"/> 读（X/Y=相对模板世界偏移，Width/Height=缩放系数）。
/// </summary>
public static class OncMissionCardInjector
{
    private const string CardNamePrefix = "OncCustom_";
    private static HarmonyLib.Harmony _harmony;
    private static bool _applied;
    private static bool _built;
    /// <summary>自定义任务 id → 构造的原生 MissionGraph（点卡片走原生 LoadMission 用）。</summary>
    private static readonly Dictionary<string, SleepyNodes.MissionGraph> _graphs = new Dictionary<string, SleepyNodes.MissionGraph>(StringComparer.Ordinal);

    /// <summary>取自定义任务的原生 MissionGraph（用于原生 LoadMission 加载场景+初始化）。</summary>
    private static SleepyNodes.MissionGraph GetGraph(string id)
    {
        if (id == null || _graphs.Count == 0) return null;
        return _graphs.TryGetValue(id, out var g) ? g : null;
    }

    public static void Apply()
    {
        if (_applied) return;
        _applied = true;
        _harmony = new HarmonyLib.Harmony("dev.open-nest.coop.onccard");
        TryPatch(typeof(MapCardManager), "UpdateMapCards", postfix: nameof(PostUpdateMapCards));
        TryPatch(typeof(MapCard), "ActivateMission", prefix: nameof(PreActivateMission));
    }

    /// <summary>自定义卡片点击：原生格式任务 → ImportMission+LoadMission（原生完整运行）；\n    /// 自定义 OncMission 格式 → 转原生 ImportMission 验证。</summary>
    private static bool PreActivateMission(MapCard __instance)
    {
        try
        {
            if (__instance == null) return true;
            var n = __instance.name;
            if (n != null && n.StartsWith(CardNamePrefix, StringComparison.Ordinal))
            {
                string id = n.Substring(CardNamePrefix.Length);
                CoopRuntime.LogSource?.LogInfo($"[OncCard] activate custom '{id}'");
                try
                {
                    string nativeJson = OncMissionBridge.GetNativeJson(id);
                    if (nativeJson != null)
                    {
                        OncMissionBridge.StartNative(id); // 原生格式 → 原生导入运行
                        return false;
                    }
                    var m = OncMissionBridge.FindMission(id);
                    if (m != null)
                    {
                        // ⚠️ 2026-08-30 修复：Core 格式（Kind 字段）任务点击卡片必须**真正启动 Core 引擎**，
                        // 之前只 ImportAndLog（转原生验证+日志）就 return false，导致"进不去任务"
                        // （原生 ActivateMission 被拦 + Core 引擎没启动）。现在走 OncMissionBridge.Start：
                        // 检查前置后置 → LoadMissionScene（场景） → runtime.Start() → HUD。
                        if (!OncMissionBridge.Start(m))
                            CoopRuntime.LogSource?.LogWarning($"[OncCard] start '{id}' failed (locked or scene load error)");
                    }
                    else CoopRuntime.LogSource?.LogWarning($"[OncCard] no mission '{id}'");
                }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[OncCard] activate '{id}': {ex.Message}"); }
                return false;
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[OncCard] PreActivateMission: {ex.Message}"); }
        return true;
    }

    private static void TryPatch(Type target, string method, string prefix = null, string postfix = null)
    {
        try
        {
            var mi = AccessTools.Method(target, method);
            if (mi == null) { CoopRuntime.LogSource?.LogWarning($"OncCard: cannot find {target.Name}.{method}"); return; }
            _harmony.Patch(mi,
                prefix: prefix != null ? new HarmonyMethod(AccessTools.Method(typeof(OncMissionCardInjector), prefix)) : null,
                postfix: postfix != null ? new HarmonyMethod(AccessTools.Method(typeof(OncMissionCardInjector), postfix)) : null);
            CoopRuntime.LogSource?.LogInfo($"OncCard: patched {target.Name}.{method}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"OncCard: patch {target.Name}.{method} failed: {ex.Message}"); }
    }

    /// <summary>选任务面板卡片更新后：脚本创建自定义任务卡片，放入原生卡片同一父容器。</summary>
    private static void PostUpdateMapCards(MapCardManager __instance)
    {
        try
        {
            if (__instance == null || _built) return;
            var registered = OncMissionBridge.RegisteredMissions;
            if (registered == null || registered.Count == 0) return;
            var cards = __instance.MapCards;
            if (cards == null || cards.Count == 0)
            {
                CoopRuntime.LogSource?.LogWarning("[OncCard] no native cards, skip (no template for size)");
                return;
            }
            // 诊断：MapCards 列表内容 —— 验证是否运行时自动收集（GetComponentsInChildren<MapCard> 只在空时填充，
            // Init(card.Mission) 用卡片自带 MissionGraph 刷新；自定义卡片无 MissionGraph → 不能被列表"自动管理"）
            try
            {
                string cl = "";
                for (int ci = 0; ci < cards.Count && ci < 12; ci++)
                {
                    var c = cards[ci];
                    if (c == null) continue;
                    string mn = "null";
                    try { if (c.Mission != null) mn = c.Mission.name; } catch { mn = "?"; }
                    cl += (cl.Length > 0 ? "; " : "") + c.name + "(m=" + mn + ")";
                }
                CoopRuntime.LogSource?.LogInfo($"[OncCard] MapCards.Count={cards.Count} cards=[{cl}]");
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[OncCard] diag cards: {ex.Message}"); }

            // ⚠️ 场景列表：遍历所有原生 MapCard，dump 每个任务的 MissionID + SceneReference.sceneName
            // （选任务面板即有完整卡片；自定义任务 SceneName 填某个 scene 即进对应场景，用户要换 Chill 场景）
            try
            {
                for (int ci = 0; ci < cards.Count; ci++)
                {
                    var c = cards[ci];
                    if (c == null || c.Mission == null) continue;
                    string cmid = "?";
                    try { cmid = c.Mission.MissionID; } catch { cmid = "?"; }
                    string cssc = "null";
                    try { if (c.Mission.SceneReference != null) cssc = c.Mission.SceneReference.sceneName; } catch { cssc = "?"; }
                    CoopRuntime.LogSource?.LogInfo($"[OncCard] scene-list card='{c.name}' mission='{cmid}' scene='{cssc}'");
                }
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[OncCard] scene-list: {ex.Message}"); }

            // 模板卡片（尺寸/父容器参考）
            MapCard template = null;
            for (int i = 0; i < cards.Count; i++)
                if (cards[i] != null) { template = cards[i]; break; }
            if (template == null) return;

            Transform parent = template.transform.parent;
            if (parent == null) parent = __instance.transform;

            // ---- 诊断：面板容器结构（定位"卡片看不到"）----
            try
            {
                var prt = parent as RectTransform;
                string pInfo = "notRT";
                if (prt != null)
                    pInfo = $"rect={prt.rect.width:0.#}x{prt.rect.height:0.#} anchor=({prt.anchorMin.x:0.##},{prt.anchorMin.y:0.##})-({prt.anchorMax.x:0.##},{prt.anchorMax.y:0.##}) pos=({prt.anchoredPosition.x:0.#},{prt.anchoredPosition.y:0.#})";
                string kids = "";
                try
                {
                    int shown = 0;
                    for (int k = 0; k < parent.childCount && shown < 15; k++)
                    {
                        var c = parent.GetChild(k);
                        if (c == null) continue;
                        kids += (kids.Length > 0 ? "; " : "") + c.name + ":" + (c.gameObject.activeSelf ? "a" : "i");
                        shown++;
                    }
                }
                catch { }
                var tw = template.transform.position;
                var pw = parent.position;
                CoopRuntime.LogSource?.LogInfo($"[OncCard] parent '{parent.name}' {pInfo} hasRT={prt != null} world=({pw.x:0.#},{pw.y:0.#},{pw.z:0.#}) active={parent.gameObject.activeSelf}");
                CoopRuntime.LogSource?.LogInfo($"[OncCard] template '{template.name}' world=({tw.x:0.#},{tw.y:0.#},{tw.z:0.#}) lossy=({template.transform.lossyScale.x:0.###},{template.transform.lossyScale.y:0.###}) children=[{kids}]");
                // active 卡片世界布局（定位摆放间距/方向）
                string layout = "";
                int la = 0;
                try
                {
                    for (int k = 0; k < parent.childCount && la < 12; k++)
                    {
                        var c = parent.GetChild(k);
                        if (c == null || !c.gameObject.activeSelf) continue;
                        var p = c.position;
                        layout += (layout.Length > 0 ? "; " : "") + c.name + "@" + p.x.ToString("0.#") + "," + p.y.ToString("0.#") + "," + p.z.ToString("0.#");
                        la++;
                    }
                }
                catch { }
                CoopRuntime.LogSource?.LogInfo($"[OncCard] active layout: {layout}");
                // ---- 渲染方式诊断：模板组件 / Canvas 祖先 / 相机 ----
                string comps = "";
                try
                {
                    var all = template.GetComponents<UnityEngine.Component>();
                    if (all != null)
                        for (int ci = 0; ci < all.Length; ci++)
                            if (all[ci] != null) comps += (comps.Length > 0 ? "," : "") + all[ci].GetType().Name;
                }
                catch { }
                string canv = "none";
                try { var cv = template.GetComponentInParent<Canvas>(); if (cv != null) canv = cv.name + ":mode=" + cv.renderMode; } catch { }
                string cam = "?";
                try { var cm = UnityEngine.Camera.main; if (cm != null) { var cp = cm.transform.position; cam = cm.name + "@(" + cp.x.ToString("0.#") + "," + cp.y.ToString("0.#") + "," + cp.z.ToString("0.#") + ")"; } } catch { }
                CoopRuntime.LogSource?.LogInfo($"[OncCard] template comps=[{comps}] canvas={canv} camera={cam}");
                // ---- 诊断：模板任务真实场景名（自定义任务 SceneName 可填它进入任务场景）----
                try
                {
                    var tplM = template.Mission;
                    if (tplM != null)
                    {
                        string sid = "?";
                        try { sid = tplM.MissionID; } catch { }
                        string ssc = "null";
                        try { if (tplM.SceneReference != null) ssc = tplM.SceneReference.sceneName; } catch { ssc = "?"; }
                        CoopRuntime.LogSource?.LogInfo($"[OncCard] template mission id='{sid}' scene='{ssc}'");
                    }
                }
                catch { }
            }
            catch { }

            // ---- 创建与原生同构的 3D 卡片：克隆模板卡片作骨架 ----
            var basePos = template.transform.position;
            var baseRot = template.transform.rotation;

            // 参考原生卡片世界间距（取前两张 active 卡片的 y 差；失败回退 0.35）
            float spacing = 0.35f;
            try
            {
                float? prevY = null;
                for (int k = 0; k < parent.childCount; k++)
                {
                    var c = parent.GetChild(k);
                    if (c == null || !c.gameObject.activeSelf) continue;
                    var p = c.position;
                    if (prevY.HasValue) { spacing = Mathf.Abs(p.y - prevY.Value); if (spacing > 0.01f && spacing < 2f) break; }
                    prevY = p.y;
                }
            }
            catch { }

            int count = 0;
            for (int i = 0; i < registered.Count; i++)
            {
                var m = registered[i];
                if (m == null || string.IsNullOrEmpty(m.Id)) continue;
                string id = m.Id;
                string label = string.IsNullOrEmpty(m.DisplayName) ? m.Id : m.DisplayName;

                GameObject go;
                try { go = UnityEngine.Object.Instantiate(template.gameObject, parent, false); }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[OncCard] instantiate '{id}': {ex.Message}"); continue; }
                if (go == null) continue;

                go.name = CardNamePrefix + id;
                go.SetActive(true);

                // 数据绑定：构造自定义 MissionGraph 赋给卡片（标题/描述走原生 Init 自动刷新），
                // 并 Add 进 MapCards 列表 → 由 MapCardManager.UpdateMapCards 自动管理（Init 用 MissionName 显示）。
                // 数据绑定：Mission=构造图 + Campaign=模板战役；并把自定义任务注册进战役（MissionNode），
                // 让原生 ActivateMission/StartOperation 能找到并走完整启动链路（场景+相机初始化）。
                try
                {
                    var mc = go.GetComponent<MapCard>();
                    if (mc != null)
                    {
                        var mg = CreateMissionGraph(m, template.Mission);
                        try { _graphs[id] = mg; } catch { } // 缓存
                        try { mc.Mission = mg; } catch { }
                        // 复用模板战役（原生 ActivateMission/StartOperation 需 Campaign 非空才能走完整启动链路）
                        try { mc.Campaign = template.Campaign; } catch { }
                        // 把自定义任务注册进战役（MissionNode.Mission = 构造图）→ 原生 ActivateMission 状态检查能找到
                        try
                        {
                            var campaign = template.Campaign;
                            if (campaign == null) campaign = __instance.Campaign;
                            if (campaign != null)
                            {
                                try { if (mc.Campaign == null) mc.Campaign = campaign; } catch { }
                                if (campaign.nodes != null)
                                {
                                    var mn = new SleepyNodes.MissionNode();
                                    try { mn.Mission = mg; } catch { }
                                    campaign.nodes.Add(mn);
                                    CoopRuntime.LogSource?.LogInfo($"[OncCard] registered '{id}' into campaign");
                                }
                            }
                        }
                        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[OncCard] register '{id}': {ex.Message}"); }
                        try { if (mg != null) cards.Add(mc); } catch { } // 加入自动管理（Init 刷新显示）
                        // 手动标题/描述兜底（Init 若未刷新/失败仍显示）
                        if (mc.Text_Title != null) mc.Text_Title.text = label;
                        if (mc.Text_Description != null)
                            mc.Text_Description.text = string.IsNullOrEmpty(m.Description) ? "" : m.Description;
                        // 隐藏状态子物体（奖章/检查点图标）
                        try
                        {
                            var medals = mc.Medals;
                            if (medals != null)
                            {
                                int mn = medals.Count;
                                for (int mi = 0; mi < mn; mi++)
                                {
                                    var slot = medals[mi];
                                    if (slot == null) continue;
                                    var slotGo = slot.gameObject;
                                    if (slotGo != null) slotGo.SetActive(false);
                                }
                            }
                        }
                        catch { }
                        // ⚠️ 2026-08-25：MapCard 无 CheckpointIcon 属性（IL2CPP MissingMethodException）→ 移除
                        // （原生模板卡片没有该字段，访问触发 native->managed trampoline 异常，可能干扰后续初始化）
                    }
                }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[OncCard] rebind '{id}': {ex.Message}"); }

                // 标题颜色（可选，任务脚本 Card.TitleColor）
                try
                {
                    if (m.Card != null)
                    {
                        var tc = ParseColor(m.Card.TitleColor);
                        var mc = go.GetComponent<MapCard>();
                        if (tc.HasValue && mc != null && mc.Text_Title != null)
                            mc.Text_Title.color = tc.Value;
                    }
                }
                catch { }

                // 定位：相对模板的世界偏移；Card.X/Y=偏移（Y 正往下）。Card.Width/Height 作为**可选缩放系数**
                // （0.1~10，默认 1=模板原尺寸）；>10 视为旧像素值（220/140）忽略不缩放，避免卡片放大盖屏。
                float ox = 0f, oy = spacing * count, oz = 0f, scale = 1f;
                try
                {
                    if (m.Card != null)
                    {
                        if (m.Card.X != 0f) ox = m.Card.X;
                        if (m.Card.Y > 0f) oy = m.Card.Y;
                        float w = m.Card.Width, h = m.Card.Height;
                        if ((w > 0f && w <= 10f) || (h > 0f && h <= 10f))
                            scale = Mathf.Clamp(Mathf.Max(w, h), 0.1f, 10f);
                    }
                }
                catch { }
                try
                {
                    go.transform.rotation = baseRot;
                    go.transform.position = new Vector3(basePos.x + ox, basePos.y - oy, basePos.z + oz);
                    if (Mathf.Abs(scale - 1f) > 0.001f)
                        go.transform.localScale = template.transform.localScale * scale;
                }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[OncCard] place '{id}': {ex.Message}"); }

                CoopRuntime.LogSource?.LogInfo($"[OncCard] created custom card '{id}' @({(basePos.x + ox):0.##},{(basePos.y - oy):0.##},{(basePos.z + oz):0.##}) scale={scale:0.##}");
                count++;
            }

            _built = true;
            // 验证：克隆卡片是否被 MapCards 自动纳入（应不变 —— 若变了说明被 Init 覆盖/重复管理）
            try { CoopRuntime.LogSource?.LogInfo($"[OncCard] after create MapCards.Count={cards.Count} (unchanged => clones not auto-managed)"); }
            catch { }
            CoopRuntime.LogSource?.LogInfo($"[OncCard] built {count} custom 3D card(s) into panel (clone template '{template.name}', parent='{parent.name}', spacing={spacing:0.###})");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[OncCard] PostUpdateMapCards: {ex.Message}"); }
    }

    /// <summary>为自定义任务构造**字段完整**的 MissionGraph 实例（走原生 LoadMission 需要，否则 NRE）。
    /// 从模板卡片原生 MissionGraph 复制集合/配置字段保证合法；改自定义 MissionID/Name/Desc/Type/Scene；
    /// nodes 用新空 List（自定义任务不走原生图，也不污染模板）。失败返回 null。</summary>
    private static SleepyNodes.MissionGraph CreateMissionGraph(OncMission m, SleepyNodes.MissionGraph tpl)
    {
        if (m == null) return null;
        try
        {
            var mg = new SleepyNodes.MissionGraph();
            if (tpl != null)
            {
                // 复制模板字段（集合字段共享引用，不改模板）→ LoadMission/OnMissionLoaded/SetupMissionPunchcards 不 NRE
                try { mg.Medals = tpl.Medals; } catch { }
                try { mg.Zones = tpl.Zones; } catch { }
                try { mg.RequiredPunchcards = tpl.RequiredPunchcards; } catch { }
                try { mg.UnlockedPunchcards = tpl.UnlockedPunchcards; } catch { }
                try { mg.PassiveGraphs = tpl.PassiveGraphs; } catch { }
                try { mg.mutators = tpl.mutators; } catch { }
                try { mg.achievementForClearing = tpl.achievementForClearing; } catch { }
                try { mg.achievementForGolding = tpl.achievementForGolding; } catch { }
                try { mg.ShowCreditsAfterSummary = tpl.ShowCreditsAfterSummary; } catch { }
                try { mg.ResetTurretPlacement = tpl.ResetTurretPlacement; } catch { }
                try { mg.RequisitionPoints = tpl.RequisitionPoints; } catch { }
                try { mg.PowderCharges = tpl.PowderCharges; } catch { }
            }
            // nodes：新空 List（避免共享模板清空污染；自定义任务不走原生节点图）
            try { mg.nodes = new Il2CppSystem.Collections.Generic.List<SleepyNodes.Node>(); } catch { }
            // 自定义数据
            try { mg.MissionID = m.Id; } catch { }
            string label = string.IsNullOrEmpty(m.DisplayName) ? m.Id : m.DisplayName;
            try { mg.MissionName = new Localisation.TextIdentifier(label); } catch { }
            try { mg.MissionDescription = new Localisation.TextIdentifier(string.IsNullOrEmpty(m.Description) ? "" : m.Description); } catch { }
            try { mg.MissionType = ParseMissionType(m.MissionType); } catch { }
            if (!string.IsNullOrEmpty(m.SceneName))
                try { mg.SceneReference = new MissionSceneReference { sceneName = m.SceneName }; } catch { }
            return mg;
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"[OncCard] create MissionGraph '{m?.Id}': {ex.Message}");
            return null;
        }
    }

    /// <summary>任务脚本 MissionType（"Tutorial"/"Campaign"/"Challenge"/"Chill"）→ 原生 MissionTypes；空/未知 = Campaign。</summary>
    private static SleepyNodes.MissionGraph.MissionTypes ParseMissionType(string t)
    {
        if (string.IsNullOrEmpty(t)) return SleepyNodes.MissionGraph.MissionTypes.Campaign;
        string s = t.Trim();
        if (s.Equals("Tutorial", StringComparison.OrdinalIgnoreCase)) return SleepyNodes.MissionGraph.MissionTypes.Tutorial;
        if (s.Equals("Chill", StringComparison.OrdinalIgnoreCase)) return SleepyNodes.MissionGraph.MissionTypes.Chill;
        if (s.Equals("Challenge", StringComparison.OrdinalIgnoreCase) || s.Equals("Challange", StringComparison.OrdinalIgnoreCase))
            return SleepyNodes.MissionGraph.MissionTypes.Challange;
        return SleepyNodes.MissionGraph.MissionTypes.Campaign;
    }

    /// <summary>解析十六进制颜色（"RRGGBB" / "AARRGGBB" / 带 # 前缀）；空/非法返回 null。</summary>
    private static Color? ParseColor(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        hex = hex.Trim().TrimStart('#');
        if (hex.Length < 6) return null;
        try
        {
            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            float a = 1f;
            if (hex.Length >= 8) a = Convert.ToInt32(hex.Substring(6, 2), 16) / 255f;
            return new Color(r / 255f, g / 255f, b / 255f, a);
        }
        catch { return null; }
    }
}
