using System;
using System.Collections.Generic;
using UnityEngine;

using OpenNestCoop.Core;
using OpenNestCore.Tasks;
namespace OpenNestCoop.Debug;

/// <summary>
/// 自定义任务引擎 **节点图完整可视化**（F9 循环页之一，模式 Mission，右上角页序号 3/4）。
/// 把任务图（<see cref="OncMission.Nodes"/> + To/From 连线）渲染成可交互的节点图：
/// - **节点**：方框显示 id + kind + 关键内容（等待秒/事件/计时器/实体/脚本模块/目标等），
///   按运行状态着色（✔完成绿 / ▶激活黄 / ·待执行灰 / ✘失败红）；
/// - **连线**：按 To 出边画线（方向箭头），展示节点逻辑关系；
/// - **交互**：滚轮缩放、右键/中键拖拽平移、左键点选节点、悬停高亮；
/// - **检查器**：点选节点后，左上角显示该节点全部字段（From/To 连线、各参数）+ 目标/计时器摘要；
/// - **顶部**：任务总览（id/状态/耗时/完成/激活节点数）。
///
/// 渲染：IMGUI(OnGUI) + GUI.contentColor/GUI.DrawTexture（不用 GUIStyle——IL2CPP 运行时
/// GUIStyle 构造可能抛异常导致 OnGUI 渲染失效，见 FrameDiagUI 同款说明）。
/// 独立日志：显示时 dump + 每 5s 周期 dump（key=mission.diag → OpenNestLogs/mission.log）。
/// </summary>
public class MissionGraphViewUI : MonoBehaviour
{
    private bool _show;
    private float _refresh;
    private float _dumpTimer;

    // ---- 相机（平移 + 缩放；节点框固定像素大小，缩放只改变间距）----
    private float _panX;
    private float _panY;
    private float _zoom = 1f;
    private bool _panInit;      // 首次显示已居中（布局中心→屏幕中心）
    private bool _dragging;
    private Vector2 _dragStart;
    private Vector2 _camStart;

    // ---- 布局缓存（任务不变则复用）----
    private string _layoutKey = "";
    private readonly Dictionary<string, Vector2> _pos = new(StringComparer.Ordinal); // 节点 id → 布局坐标（缩放单位）
    private readonly Dictionary<string, float> _nodeH = new(StringComparer.Ordinal); // 节点 id → 框高（px）

    // ---- 运行状态 ----
    private readonly HashSet<string> _done = new(StringComparer.Ordinal);
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private string _selected;
    private string _hover;

    // ---- 常量 ----
    private const float NodeW = 200f;     // 节点框宽（px，固定）
    private const float LineH = 22f;      // 每行文本高（⚠️ 2026-08-30：默认 GUI.Label 行高 ~20px，之前 15 会纵向裁剪文字一半）
    private const float HSpace = 280f;    // 层间距（缩放单位）
    private const float VSpace = 26f;     // 层内行间距（缩放单位）
    private static Texture2D _whiteTex;

    /// <summary>单例（供 DiagCycleController 切换）。</summary>
    public static MissionGraphViewUI Instance { get; private set; }

    public MissionGraphViewUI(System.IntPtr ptr) : base(ptr) { }

    public void Awake() { Instance = this; }

    /// <summary>由 DiagCycleController（F9 循环）控制显示；显示时 dump 独立日志（key=mission.diag）。</summary>
    public void SetVisible(bool v)
    {
        _show = v;
        if (v)
        {
            _refresh = 0f;
            RefreshData();
            LogDump();
        }
    }

    /// <summary>相机居中 + 自动适配缩放：计算布局边界（含节点框半宽/半高），缩放让全图适配屏幕并居中。
    /// 玩家手动滚轮/拖拽后保持用户视角（_panInit 已置 true 不重复）。</summary>
    private void CenterCamera()
    {
        try
        {
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            bool any = false;
            foreach (var kv in _pos)
            {
                float h = _nodeH.TryGetValue(kv.Key, out var hh) ? hh : 60f;
                float x0 = kv.Value.x - NodeW / 2f, x1 = kv.Value.x + NodeW / 2f;
                float y0 = kv.Value.y - h / 2f, y1 = kv.Value.y + h / 2f;
                if (x0 < minX) minX = x0; if (x1 > maxX) maxX = x1;
                if (y0 < minY) minY = y0; if (y1 > maxY) maxY = y1;
                any = true;
            }
            if (!any) { _panX = Screen.width * 0.5f; _panY = Screen.height * 0.5f; return; }
            float contentW = maxX - minX, contentH = maxY - minY;
            float availW = Screen.width * 0.9f, availH = Screen.height * 0.85f;
            float zx = contentW > 0f ? availW / contentW : 1f;
            float zy = contentH > 0f ? availH / contentH : 1f;
            // ⚠️ 2026-08-30：下限 0.5（配合蛇形换行布局，图不会过宽；太低 → 字太小不可读）
            _zoom = Mathf.Clamp(Mathf.Min(zx, zy), 0.5f, 1.5f);
            // 居中：世界中心 → 屏幕中心
            float cx = (minX + maxX) * 0.5f, cy = (minY + maxY) * 0.5f;
            _panX = Screen.width * 0.5f - cx * _zoom;
            _panY = Screen.height * 0.5f - cy * _zoom;
        }
        catch { }
    }

    public void Update()
    {
        try
        {
            if (!_show) return;
            float dt = Time.unscaledDeltaTime;
            _refresh -= dt;
            if (_refresh <= 0f)
            {
                _refresh = 0.25f;
                RefreshData();
            }
            _dumpTimer += dt;
            if (_dumpTimer >= 5f) { _dumpTimer = 0f; LogDump(); }
        }
        catch { }
    }

    // ---------------- 数据刷新 ----------------

    private void RefreshData()
    {
        _done.Clear();
        _active.Clear();
        var rt = OpenNestCoop.GameSync.OncMissionBridge.Current;
        if (rt == null || rt.Mission == null) { _layoutKey = ""; return; }
        var s = rt.BuildSyncState();
        if (s != null)
        {
            if (s.DoneNodeIds != null) foreach (var id in s.DoneNodeIds) if (id != null) _done.Add(id);
            if (s.ActiveNodeIds != null) foreach (var id in s.ActiveNodeIds) if (id != null) _active.Add(id);
        }
        // 布局缓存：任务/节点数变化才重排
        string key = rt.Mission.Id + "#" + rt.Mission.Nodes?.Count;
        if (key != _layoutKey)
        {
            _layoutKey = key;
            BuildLayout(rt.Mission);
            // ⚠️ 2026-08-30：布局重建后自动适配缩放+居中（首次显示/换任务时全图可见，不超出屏幕）
            if (!_panInit) { _panInit = true; CenterCamera(); }
            else CenterCameraIfLayoutBig();
        }
    }

    /// <summary>布局变化后：若当前缩放放不下全图则重新适配（玩家手动缩放过的场景尽量保留，
    /// 但任务/布局明显变化时保证全图可见）。</summary>
    private void CenterCameraIfLayoutBig()
    {
        try
        {
            if (_pos.Count == 0) return;
            float maxX = float.MinValue, minX = float.MaxValue, maxY = float.MinValue, minY = float.MaxValue;
            foreach (var kv in _pos)
            {
                float h = _nodeH.TryGetValue(kv.Key, out var hh) ? hh : 60f;
                if (kv.Value.x + NodeW / 2f > maxX) maxX = kv.Value.x + NodeW / 2f;
                if (kv.Value.x - NodeW / 2f < minX) minX = kv.Value.x - NodeW / 2f;
                if (kv.Value.y + h / 2f > maxY) maxY = kv.Value.y + h / 2f;
                if (kv.Value.y - h / 2f < minY) minY = kv.Value.y - h / 2f;
            }
            float cw = maxX - minX, ch = maxY - minY;
            float needZ = Mathf.Min(Screen.width * 0.9f / Mathf.Max(cw, 1f), Screen.height * 0.85f / Mathf.Max(ch, 1f));
            if (needZ < _zoom) CenterCamera(); // 全图放不下 → 自动缩小适配
        }
        catch { }
    }

    /// <summary>分层布局：layer(n) = 1 + max(layer(from))；同层上下排，层间左右分。</summary>
    private void BuildLayout(OncMission m)
    {
        _pos.Clear();
        _nodeH.Clear();
        if (m == null || m.Nodes == null || m.Nodes.Count == 0) return;

        var layer = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in m.Nodes) if (n != null) layer[n.Id] = 0;
        // 迭代松弛直到稳定
        for (int it = 0; it < m.Nodes.Count + 2; it++)
        {
            bool changed = false;
            foreach (var n in m.Nodes)
            {
                if (n == null) continue;
                for (int i = 0; i < n.To.Count; i++)
                {
                    string t = n.To[i];
                    if (t == null) continue;
                    int v = layer.TryGetValue(n.Id, out int nl) ? nl + 1 : 1;
                    int cur = layer.TryGetValue(t, out int tc) ? tc : 0;
                    if (v > cur) { layer[t] = v; changed = true; }
                }
            }
            if (!changed) break;
        }

        // 节点框高（按内容行数）
        foreach (var n in m.Nodes)
        {
            if (n == null) continue;
            int rows = NodeContentRows(n);
            _nodeH[n.Id] = LineH * (rows + 2) + 6f;
        }

        // 同层排布（层内居中）
        var byLayer = new Dictionary<int, List<string>>();
        foreach (var kv in layer)
        {
            int L = kv.Value;
            if (!byLayer.TryGetValue(L, out var list)) { list = new List<string>(); byLayer[L] = list; }
            list.Add(kv.Key);
        }
        // ⚠️ 2026-08-30：蛇形换行——每"带"最多 BandLayers 层，超限折返到下一带（右→左交替）。
        // 之前纯横向分层：深层图（如自检3 14 节点多层）横向无限延伸 → 自动缩放压到 0.25 → 字太小。
        // 换行后图宽度受限，zoom 可保持较大 → 文字清晰可读。
        const int BandLayers = 5; // 每带最多层数（横向）
        int band = 0, colInBand = 0;
        float bandOffsetX = 0f;
        // 层号排序（字典序；byLayer 的 key 是层号 int，按数值排序）
        var layerKeys = new List<int>();
        foreach (var kv in byLayer) layerKeys.Add(kv.Key);
        layerKeys.Sort();
        foreach (var L in layerKeys)
        {
            if (colInBand >= BandLayers)
            {
                band++;
                colInBand = 0;
                bandOffsetX += (BandLayers * HSpace);
            }
            var list = byLayer[L];
            for (int i = 0; i < list.Count; i++)
            {
                string id = list[i];
                float rowH = _nodeH.TryGetValue(id, out float h) ? h : 60f;
                float y = (i - (list.Count - 1) / 2f) * (rowH + VSpace);
                _pos[id] = new Vector2(bandOffsetX + (colInBand * HSpace), y);
            }
            colInBand++;
        }
    }

    /// <summary>节点内容行数（标题 + 关键参数行）。</summary>
    private static int NodeContentRows(OncNode n)
    {
        return NodeContentLines(n, null).Count;
    }

    /// <summary>节点内容行（首行 = 标题 id+kind；其余 = 关键参数）。</summary>
    private static List<string> NodeContentLines(OncNode n, OncMissionRuntime rt)
    {
        var list = new List<string>();
        if (n == null) { list.Add("(null)"); return list; }
        list.Add($"{n.Id} [{n.Kind}]");
        switch (n.Kind)
        {
            case OncNodeKind.WaitSeconds:
                list.Add($"秒: {n.Seconds:0.#}");
                break;
            case OncNodeKind.WaitForEvent:
            case OncNodeKind.Branch:
                list.Add($"事件: {n.EventId}");
                if (n.Timeout > 0f) list.Add($"超时: {n.Timeout:0.#}s");
                if (n.Routes != null && n.Routes.Count > 0) list.Add($"路由: {n.Routes.Count} 条");
                break;
            case OncNodeKind.Teleprinter:
            case OncNodeKind.Notify:
                list.Add($"文本: {Trunc(n.Message ?? n.Title, 20)}");
                break;
            case OncNodeKind.Objective:
                list.Add($"目标: {n.ObjectiveId} ({n.ObjectiveAction})");
                break;
            case OncNodeKind.ObjectiveComplete:
            case OncNodeKind.ObjectiveFail:
                list.Add($"目标: {n.ObjectiveId}");
                break;
            case OncNodeKind.SpawnEntity:
                list.Add($"实体: {n.EntityId}");
                list.Add($"位置: ({n.X:0.#},{n.Y:0.#}) hp={n.Health} role={n.Role}");
                break;
            case OncNodeKind.MoveEntity:
                list.Add($"实体: {n.EntityId} → ({n.X:0.#},{n.Y:0.#})");
                break;
            case OncNodeKind.DamageEntity:
                list.Add($"伤害: {n.EntityId} -{n.Value}");
                break;
            case OncNodeKind.WaitEntityDestroyed:
                list.Add($"等摧毁: {n.EntityId}");
                break;
            case OncNodeKind.StartTimer:
                list.Add($"计时器: {n.TimerId} ({n.Seconds:0.#}s)");
                break;
            case OncNodeKind.WaitTimerExpired:
                list.Add($"等计时器: {n.TimerId}");
                if (rt != null && !string.IsNullOrEmpty(n.TimerId))
                {
                    float rem = rt.TimerRemaining(n.TimerId);
                    if (rem >= 0f) list.Add($"剩余: {rem:0.0}s");
                }
                break;
            case OncNodeKind.StopTimer:
            case OncNodeKind.PauseTimer:
            case OncNodeKind.ResumeTimer:
                list.Add($"计时器: {n.TimerId}");
                break;
            case OncNodeKind.AddRequisitionPoints:
                list.Add($"补给: +{n.Value}");
                break;
            case OncNodeKind.AddShell:
                list.Add($"炮弹: {n.ShellId} ×{n.Value}");
                break;
            case OncNodeKind.AddPowderCharge:
                list.Add($"发射药: +{n.Value}");
                break;
            case OncNodeKind.Scripted:
            case OncNodeKind.ScriptedCondition:
            case OncNodeKind.ScriptedWait:
                list.Add($"模块: {n.ModuleName}");
                if (!string.IsNullOrEmpty(n.ModuleArgs)) list.Add($"参数: {Trunc(n.ModuleArgs, 18)}");
                break;
            case OncNodeKind.Impact:
                list.Add($"着弹: ({n.X:0.#},{n.Y:0.#})");
                break;
            case OncNodeKind.UnlockSceneObject:
            case OncNodeKind.SceneNotification:
                list.Add($"通知: {n.NotifId}");
                break;
            case OncNodeKind.End:
            case OncNodeKind.Fail:
                list.Add("结束");
                break;
        }
        return list;
    }

    private static string Trunc(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    // ---------------- 渲染 ----------------

    private void OnGUI()
    {
        if (!_show) return;
        try
        {
            HandleInput();
            int oldDepth = GUI.depth;
            GUI.depth = -100;

            var rt = OpenNestCoop.GameSync.OncMissionBridge.Current;
            if (rt == null || rt.Mission == null)
            {
                DrawHeader(rt);
                DrawTextTopRight("[F9 循环] 任务节点诊断 (3/4)", SkDim, 0);
                DrawTextTopRight("无运行中的自定义任务（原生任务见右下 CurrentState）", SkDim, 1);
                DrawNativeSummary();
                GUI.depth = oldDepth;
                return;
            }

            DrawHeader(rt);
            DrawEdges(rt.Mission);
            DrawNodes(rt);
            DrawInspector(rt);

            GUI.depth = oldDepth;
        }
        catch { }
    }

    // ---------------- 交互（平移/缩放/选择）----------------

    private void HandleInput()
    {
        try
        {
            var e = Event.current;
            if (e == null) return;
            // 滚轮缩放（以光标为中心）
            if (e.type == EventType.ScrollWheel && Mathf.Abs(e.delta.y) > 0.001f)
            {
                float factor = e.delta.y > 0f ? 1.12f : 0.89f;
                float newZoom = Mathf.Clamp(_zoom * factor, 0.3f, 2.5f);
                // 保持光标下的点不动
                Vector2 mp = e.mousePosition;
                _panX = mp.x - (mp.x - _panX) * (newZoom / _zoom);
                _panY = mp.y - (mp.y - _panY) * (newZoom / _zoom);
                _zoom = newZoom;
                e.Use();
            }
            // 右键/中键拖拽平移
            if (e.type == EventType.MouseDown && (e.button == 1 || e.button == 2))
            {
                _dragging = true;
                _dragStart = e.mousePosition;
                _camStart = new Vector2(_panX, _panY);
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _dragging)
            {
                _panX = _camStart.x + (e.mousePosition.x - _dragStart.x);
                _panY = _camStart.y + (e.mousePosition.y - _dragStart.y);
                e.Use();
            }
            else if (e.type == EventType.MouseUp)
            {
                _dragging = false;
            }
            // 左键点选
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                _selected = HitTest(e.mousePosition);
                e.Use();
            }
            // 悬停
            if (e.type == EventType.Repaint)
                _hover = HitTest(e.mousePosition);
        }
        catch { }
    }

    /// <summary>鼠标命中节点（逆序取最上层）。</summary>
    private string HitTest(Vector2 mp)
    {
        var rt = OpenNestCoop.GameSync.OncMissionBridge.Current;
        if (rt == null || rt.Mission == null) return null;
        var nodes = rt.Mission.Nodes;
        if (nodes == null) return null;
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            var n = nodes[i];
            if (n == null) continue;
            var r = NodeScreenRect(n.Id);
            if (r.Contains(mp)) return n.Id;
        }
        return null;
    }

    private Vector2 WorldToScreen(Vector2 w)
    {
        return new Vector2(w.x * _zoom + _panX, w.y * _zoom + _panY);
    }

    private Rect NodeScreenRect(string id)
    {
        if (!_pos.TryGetValue(id, out var w)) return new Rect(0, 0, 0, 0);
        float h = _nodeH.TryGetValue(id, out var hh) ? hh : 60f;
        var s = WorldToScreen(w);
        return new Rect(s.x - NodeW / 2f, s.y - h / 2f, NodeW, h);
    }

    // ---------------- 绘制：头部 / 边 / 节点 / 检查器 ----------------

    private void DrawHeader(OncMissionRuntime rt)
    {
        float x = Screen.width - 640f;
        float y = 12f;
        DrawText(new Rect(x, y, 620, 20), "[F9 循环] 任务节点诊断 (3/4)  " + (rt.Mission?.DisplayName ?? ""), SkTitle); y += 20f;
        string state = rt == null ? "-" :
            rt.IsRunning ? "运行中" : rt.IsSuccess ? "成功" : rt.IsFailed ? "失败" : rt.IsCanceled ? "已取消" : rt.IsFinished ? "已结束" : "未启动";
        DrawText(new Rect(x, y, 620, 20), $"ID: {rt?.Mission?.Id}  状态: {state}  耗时: {rt?.Elapsed:0.0}s", SkInfo); y += 20f;
        DrawText(new Rect(x, y, 620, 20), $"完成 {_done.Count}  激活 {_active.Count}  节点 {rt?.Mission?.Nodes?.Count ?? 0}   [滚轮缩放/右键平移/左键查看]", SkNormal); y += 20f;
    }

    private void DrawEdges(OncMission m)
    {
        if (m.Nodes == null) return;
        var lineCol = new Color(0.55f, 0.55f, 0.55f, 0.8f);
        var hotCol = new Color(1f, 0.85f, 0.3f, 1f);
        foreach (var n in m.Nodes)
        {
            if (n == null || n.To == null) continue;
            var sa = NodeScreenRect(n.Id);
            Vector2 a = new Vector2(sa.x + sa.width, sa.y + sa.height / 2f);
            foreach (var t in n.To)
            {
                if (t == null) continue;
                var sb = NodeScreenRect(t);
                Vector2 b = new Vector2(sb.x, sb.y + sb.height / 2f);
                bool hot = _selected == n.Id || _selected == t || _hover == n.Id || _hover == t;
                DrawLine(a, b, hot ? hotCol : lineCol, 3f);
                DrawArrowHead(b, hot ? hotCol : lineCol);
            }
        }
    }

    private void DrawNodes(OncMissionRuntime rt)
    {
        var m = rt.Mission;
        if (m.Nodes == null) return;
        foreach (var n in m.Nodes)
        {
            if (n == null) continue;
            var r = NodeScreenRect(n.Id);
            bool isDone = _done.Contains(n.Id);
            bool isActive = _active.Contains(n.Id);
            Color bg = isDone ? new Color(0.12f, 0.35f, 0.16f, 0.92f)
                : isActive ? new Color(0.42f, 0.33f, 0.08f, 0.94f)
                : new Color(0.16f, 0.16f, 0.18f, 0.9f);
            if (_hover == n.Id) bg = new Color(bg.r + 0.1f, bg.g + 0.1f, bg.b + 0.1f, 0.96f);
            if (_selected == n.Id) bg = new Color(0.2f, 0.3f, 0.5f, 0.96f);
            GUI.DrawTexture(r, WhiteTex(), ScaleMode.StretchToFill, false, 0f, bg, 0f, 0f);
            // 标题（状态图标 + id + kind）
            string icon = isDone ? "✔" : isActive ? "▶" : "·";
            Color titleCol = isDone ? SkGood : isActive ? SkWarn : SkDim;
            DrawText(new Rect(r.x + 4, r.y + 3, r.width - 8, LineH), icon + " " + n.Id, titleCol);
            // 内容行
            var lines = NodeContentLines(n, rt);
            float ty = r.y + 3 + LineH;
            for (int i = 1; i < lines.Count; i++)
            {
                DrawText(new Rect(r.x + 4, ty, r.width - 8, LineH), lines[i], SkNormal);
                ty += LineH;
            }
        }
    }

    /// <summary>检查器：选中节点全部字段 + 连线 + 目标/计时器摘要（左上角固定面板）。</summary>
    private void DrawInspector(OncMissionRuntime rt)
    {
        if (string.IsNullOrEmpty(_selected) || rt == null) return;
        var m = rt.Mission;
        var n = m != null ? m.Node(_selected) : null;
        if (n == null) { _selected = null; return; }
        var lines = new List<string>();
        lines.Add("── 节点检查器 ──");
        lines.Add($"Id: {n.Id}");
        lines.Add($"Kind: {n.Kind}");
        lines.Add($"From: {JoinList(n.From)}");
        lines.Add($"To: {JoinList(n.To)}");
        if (n.Seconds != 0f) lines.Add($"Seconds: {n.Seconds}");
        if (!string.IsNullOrEmpty(n.EventId)) lines.Add($"EventId: {n.EventId}");
        if (n.Routes != null && n.Routes.Count > 0)
            for (int i = 0; i < n.Routes.Count; i++)
                if (n.Routes[i] != null) lines.Add($"  路由[{i}]: {n.Routes[i].EventId} → {n.Routes[i].TargetNodeId}");
        if (n.Timeout > 0f) lines.Add($"Timeout: {n.Timeout}");
        if (!string.IsNullOrEmpty(n.Title)) lines.Add($"Title: {n.Title}");
        if (!string.IsNullOrEmpty(n.Message)) lines.Add($"Message: {Trunc(n.Message, 40)}");
        if (n.Duration != 0f) lines.Add($"Duration: {n.Duration}");
        if (!string.IsNullOrEmpty(n.ObjectiveId)) lines.Add($"ObjectiveId: {n.ObjectiveId} ({n.ObjectiveAction})");
        if (n.Value != 0) lines.Add($"Value: {n.Value}");
        if (!string.IsNullOrEmpty(n.EntityId)) lines.Add($"EntityId: {n.EntityId}");
        if (!string.IsNullOrEmpty(n.EntityName)) lines.Add($"EntityName: {n.EntityName}");
        if (n.Health != 0) lines.Add($"Health: {n.Health}");
        if (n.Armour != 0) lines.Add($"Armour: {n.Armour}");
        if (n.Stars != 0) lines.Add($"Stars: {n.Stars}");
        if (!string.IsNullOrEmpty(n.Role)) lines.Add($"Role: {n.Role}");
        if (n.X != 0f || n.Y != 0f) lines.Add($"X/Y: {n.X:0.#}/{n.Y:0.#}");
        if (!string.IsNullOrEmpty(n.ShellId)) lines.Add($"ShellId: {n.ShellId}");
        if (n.Slot != 0) lines.Add($"Slot: {n.Slot}");
        if (!string.IsNullOrEmpty(n.TimerId)) lines.Add($"TimerId: {n.TimerId}");
        if (!string.IsNullOrEmpty(n.NotifId)) lines.Add($"NotifId: {n.NotifId}");
        if (!string.IsNullOrEmpty(n.CustomData)) lines.Add($"CustomData: {Trunc(n.CustomData, 30)}");
        if (!string.IsNullOrEmpty(n.ModuleName)) lines.Add($"ModuleName: {n.ModuleName}");
        if (!string.IsNullOrEmpty(n.ModuleArgs)) lines.Add($"ModuleArgs: {Trunc(n.ModuleArgs, 30)}");
        // 目标/计时器摘要
        if (rt.Objectives != null && rt.Objectives.Count > 0)
        {
            lines.Add("── 目标 ──");
            foreach (var kv in rt.Objectives)
            {
                var o = kv.Value;
                if (o == null) continue;
                lines.Add($"  {o.Id}: {o.Progress}/{o.Target} [{o.Status}]");
            }
        }
        lines.Add("── 计时器 ──");
        bool anyT = false;
        if (m.Nodes != null)
            foreach (var nn in m.Nodes)
            {
                if (nn == null || string.IsNullOrEmpty(nn.TimerId)) continue;
                if (nn.Kind != OncNodeKind.StartTimer && nn.Kind != OncNodeKind.AddTimerTime) continue;
                float rem = rt.TimerRemaining(nn.TimerId);
                if (rem < 0f) continue;
                anyT = true;
                lines.Add($"  {nn.TimerId}: {rem:0.0}s");
            }
        if (!anyT) lines.Add("  （无运行中计时器）");
        // 渲染（左上角）
        float y = 12f;
        foreach (var ln in lines)
        {
            Color st = ln.StartsWith("──") ? SkTitle : (ln.StartsWith("   ") ? SkInfo : SkNormal);
            DrawText(new Rect(12, y, 560, 18), ln, st);
            y += 18f;
        }
    }

    private static string JoinList(List<string> l)
    {
        if (l == null || l.Count == 0) return "(无)";
        return string.Join(",", l);
    }

    // ---------------- 绘制辅助 ----------------

    private void DrawTextTopRight(string text, Color c, int row)
    {
        float x = Screen.width - 640f;
        DrawText(new Rect(x, 12 + row * 20f, 620, 20), text, c);
    }

    private void DrawNativeSummary()
    {
        try
        {
            var mm = MissionManager.Instance;
            if (mm == null || mm.CurrentMission == null) return;
            string id = "?"; try { id = mm.CurrentMission.MissionID; } catch { }
            string cur = "?"; try { cur = mm.CurrentMission.CurrentState == null || mm.CurrentMission.CurrentState.Node == null ? "(no state)" : mm.CurrentMission.CurrentState.Node.NodeID; } catch { }
            int cnt = -1; try { cnt = mm.CurrentMission.nodes != null ? mm.CurrentMission.nodes.Count : -1; } catch { }
            DrawTextTopRight($"原生图 {id}  current={cur}  nodes={cnt}", SkInfo, 2);
        }
        catch { }
    }

    private static void DrawText(Rect r, string text, Color c)
    {
        var old = GUI.contentColor;
        GUI.contentColor = c;
        GUI.Label(r, text);
        GUI.contentColor = old;
    }

    private void DrawLine(Vector2 a, Vector2 b, Color c, float thick)
    {
        float dist = Vector2.Distance(a, b);
        int steps = Mathf.Max(1, Mathf.CeilToInt(dist / 3f));
        float half = thick / 2f;
        for (int i = 0; i < steps; i++)
        {
            float t = (i + 0.5f) / steps;
            var p = Vector2.Lerp(a, b, t);
            GUI.DrawTexture(new Rect(p.x - half, p.y - half, thick, thick), WhiteTex(), ScaleMode.StretchToFill, false, 0f, c, 0f, 0f);
        }
    }

    /// <summary>连线目标端小箭头（"&gt;" 形，指向 b）。</summary>
    private void DrawArrowHead(Vector2 b, Color c)
    {
        Vector2 dirA = b + new Vector2(-7f, -5f);
        Vector2 dirB = b + new Vector2(-7f, 5f);
        DrawLine(dirA, b, c, 2f);
        DrawLine(dirB, b, c, 2f);
    }

    private static Texture2D WhiteTex()
    {
        if (_whiteTex == null)
        {
            _whiteTex = new Texture2D(1, 1);
            _whiteTex.SetPixel(0, 0, Color.white);
            _whiteTex.Apply();
        }
        return _whiteTex;
    }

    // ---- 颜色 ----
    private static readonly Color SkTitle = new Color(1f, 0.92f, 0.42f);
    private static readonly Color SkGood = new Color(0.45f, 0.92f, 0.45f);
    private static readonly Color SkWarn = new Color(1f, 0.78f, 0.28f);
    private static readonly Color SkBad = new Color(1f, 0.42f, 0.38f);
    private static readonly Color SkInfo = new Color(0.45f, 0.72f, 1f);
    private static readonly Color SkNormal = new Color(0.88f, 0.88f, 0.88f);
    private static readonly Color SkDim = new Color(0.62f, 0.62f, 0.62f);

    // ---------------- 独立日志 ----------------

    private void LogDump()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[MissionDiag] show={_show}");
            var rt = OpenNestCoop.GameSync.OncMissionBridge.Current;
            if (rt == null || rt.Mission == null)
            {
                sb.AppendLine("  no custom runtime");
                var mm = MissionManager.Instance;
                if (mm != null && mm.CurrentMission != null)
                {
                    string id = "?"; try { id = mm.CurrentMission.MissionID; } catch { }
                    string cur = "?"; try { cur = mm.CurrentMission.CurrentState == null ? "(no state)" : (mm.CurrentMission.CurrentState.Node?.NodeID ?? "?"); } catch { }
                    sb.AppendLine($"  native graph '{id}' current={cur}");
                }
                CoopLog.Info("mission.diag", () => sb.ToString());
                return;
            }
            var s = rt.BuildSyncState();
            sb.AppendLine($"  id={rt.Mission.Id} running={rt.IsRunning} done={s?.DoneNodeIds?.Count ?? 0} active={s?.ActiveNodeIds?.Count ?? 0}");
            sb.AppendLine($"  elapsed={rt.Elapsed:0.0}s success={rt.IsSuccess} failed={rt.IsFailed}");
            if (s != null && s.ActiveNodeIds != null)
                foreach (var id in s.ActiveNodeIds) sb.AppendLine($"    ACTIVE {id}");
            // 连线 dump（看逻辑关系）
            if (rt.Mission.Nodes != null)
                foreach (var n in rt.Mission.Nodes)
                {
                    if (n == null) continue;
                    string stt = _done.Contains(n.Id) ? "DONE" : _active.Contains(n.Id) ? "ACT" : "wait";
                    sb.AppendLine($"    [{stt}] {n.Id} ({n.Kind}) → {JoinList(n.To)}");
                }
            CoopLog.Info("mission.diag", () => sb.ToString());
        }
        catch (Exception ex) { CoopLog.Warn("mission.diag", () => $"MissionGraphViewUI dump error: {ex.Message}"); }
    }
}
