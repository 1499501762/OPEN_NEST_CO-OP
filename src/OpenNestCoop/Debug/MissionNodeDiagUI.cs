using System;
using System.Collections.Generic;
using UnityEngine;

using OpenNestCoop.Core;
using OpenNestCore.Tasks;
namespace OpenNestCoop.Debug;

/// <summary>
/// ⚠️ **已由 <see cref="MissionGraphViewUI"/> 替代**（F9 Mission 页改用完整节点图可视化）。
/// 本文件保留为平面摘要实现（不再注册/显示）。节点逻辑关系 + 内容 + 交互见 MissionGraphViewUI。
///
/// 旧实现（保留参考）：自定义任务引擎节点运行状态可视化诊断（F9 循环页之一，模式 Mission）。
/// 显示 <see cref="OpenNestCoop.GameSync.OncMissionBridge.Current"/>（Core 引擎 runtime）：
/// - 任务总览：模式（Core 引擎 / 原生图）、id/显示名、running/finished/success/failed/canceled、耗时；
/// - 目标列表：状态 + 进度（progress/target）；
/// - 激活（挂起）节点：id + kind + 计时器剩余；
/// - 计时器表：id → 剩余秒；
/// - 节点图状态可视化：任务图每个节点按 DONE(绿)/ACTIVE(蓝)/WAIT(黄)/pending(灰) 着色；
/// - 原生图（如当前是原生格式任务）：CurrentState 节点 + 图节点数。
///
/// 渲染：IMGUI(OnGUI) + GUI.contentColor 逐行着色（不用 GUIStyle——IL2CPP 运行时 GUIStyle 构造
/// 可能抛异常导致 OnGUI 渲染失效，见 FrameDiagUI 同款说明）。
/// 独立日志：显示时 dump + 每 5s 周期 dump（key=mission.diag → OpenNestLogs/mission.log，
/// 由 CoopRuntime.InitFileLogs 路由；onc.mission.* 全部路由到 mission.log）。
/// </summary>
public class MissionNodeDiagUI : MonoBehaviour
{
    private bool _show;
    private float _refresh;
    private float _dumpTimer;

    /// <summary>行样式枚举（颜色）。</summary>
    private enum Sk { Title, Normal, Good, Warn, Bad, Info, Dim }
    private readonly List<(string Text, Sk Style)> _lines = new();

    /// <summary>样式→颜色（与 FrameDiagUI 同款，不用 GUIStyle）。</summary>
    private static Color ColorOf(Sk s) => s switch
    {
        Sk.Title => new Color(1f, 0.92f, 0.42f),
        Sk.Good => new Color(0.45f, 0.92f, 0.45f),
        Sk.Warn => new Color(1f, 0.78f, 0.28f),
        Sk.Bad => new Color(1f, 0.42f, 0.38f),
        Sk.Info => new Color(0.45f, 0.72f, 1f),
        Sk.Dim => new Color(0.62f, 0.62f, 0.62f),
        _ => new Color(0.88f, 0.88f, 0.88f),
    };

    /// <summary>单例（供 DiagCycleController 切换）。</summary>
    public static MissionNodeDiagUI Instance { get; private set; }

    public MissionNodeDiagUI(System.IntPtr ptr) : base(ptr) { }

    public void Awake() { Instance = this; }

    /// <summary>由 DiagCycleController（F9 循环）控制显示；显示时 dump 独立日志（key=mission.diag）。</summary>
    public void SetVisible(bool v)
    {
        _show = v;
        if (v) { _refresh = 0f; LogDump(); }
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
                BuildLines();
            }
            // 独立日志：显示时每 5s dump 一次（抓运行中状态变化）
            _dumpTimer += dt;
            if (_dumpTimer >= 5f)
            {
                _dumpTimer = 0f;
                LogDump();
            }
        }
        catch { }
    }

    // ---------------- 行构建 ----------------

    private void BuildLines()
    {
        _lines.Clear();
        try
        {
            var rt = OpenNestCoop.GameSync.OncMissionBridge.Current;
            if (rt == null || rt.Mission == null)
            {
                _lines.Add(("[F9 循环] 任务节点诊断 (3/4)", Sk.Title));
                _lines.Add(("任务引擎：无运行中的自定义任务", Sk.Dim));
                AppendNativeSummary();
                return;
            }

            var m = rt.Mission;
            _lines.Add(("[F9 循环] 任务节点诊断 (3/4)  " + (m.DisplayName ?? m.Id ?? ""), Sk.Title));
            string state =
                rt.IsRunning ? "运行中" :
                rt.IsSuccess ? "成功" :
                rt.IsFailed ? "失败" :
                rt.IsCanceled ? "已取消" : rt.IsFinished ? "已结束" : "未启动";
            _lines.Add(($"ID: {m.Id}  状态: {state}", Sk.Info));
            _lines.Add(($"场景: {m.SceneName}  耗时: {rt.Elapsed:0.0}s", Sk.Normal));

            // 目标
            if (rt.Objectives != null && rt.Objectives.Count > 0)
            {
                _lines.Add(("—— 目标 ——", Sk.Normal));
                foreach (var kv in rt.Objectives)
                {
                    var o = kv.Value;
                    if (o == null) continue;
                    string icon = o.IsCompleted ? "✔" : o.IsActive ? "▶" : o.Status == OncObjectiveStatus.Failed ? "✘" : "○";
                    _lines.Add(($"{icon} {o.Id}: {o.Progress}/{o.Target} [{o.Status}]", StyleOf(o)));
                }
            }

            // 激活（挂起）节点
            _lines.Add(("—— 激活节点（挂起等待）——", Sk.Normal));
            var sync = rt.BuildSyncState();
            if (sync != null && sync.ActiveNodeIds != null && sync.ActiveNodeIds.Count > 0)
            {
                foreach (var id in sync.ActiveNodeIds)
                {
                    var n = m.Node(id);
                    if (n == null) continue;
                    string detail = NodeDetail(n, rt);
                    _lines.Add(($"▶ {id} ({n.Kind}){detail}", Sk.Warn));
                }
            }
            else _lines.Add(("（无激活节点——瞬时节点瞬时完成）", Sk.Dim));

            // 计时器
            _lines.Add(("—— 计时器 ——", Sk.Normal));
            bool anyTimer = false;

            if (m.Nodes != null)
                foreach (var n in m.Nodes)
                {
                    if (n == null || string.IsNullOrEmpty(n.TimerId)) continue;
                    if (n.Kind != OncNodeKind.StartTimer && n.Kind != OncNodeKind.AddTimerTime) continue;
                    float rem = rt.TimerRemaining(n.TimerId);
                    if (rem < 0f) continue;
                    anyTimer = true;
                    _lines.Add(($"⏱ {n.TimerId}: {rem:0.0}s", Sk.Info));
                }
            if (!anyTimer) _lines.Add(("（无运行中计时器）", Sk.Dim));

            // 节点图状态可视化（全部节点按 DONE/ACTIVE/pending 着色）
            _lines.Add(($"—— 节点图状态（共 {m.Nodes?.Count ?? 0}）——", Sk.Normal));
            var done = sync != null ? sync.DoneNodeIds : null;
            var active = sync != null ? sync.ActiveNodeIds : null;
            if (m.Nodes != null)
                for (int i = 0; i < m.Nodes.Count; i++)
                {
                    var n = m.Nodes[i];
                    if (n == null) continue;
                    bool isDone = done != null && done.Contains(n.Id);
                    bool isActive = active != null && active.Contains(n.Id);
                    string icon = isDone ? "✔" : isActive ? "▶" : "·";
                    Sk style = isDone ? Sk.Good : isActive ? Sk.Warn : Sk.Dim;
                    _lines.Add(($"{icon} {n.Id} ({n.Kind})", style));
                }

            AppendNativeSummary();
        }
        catch (Exception ex)
        {
            _lines.Add(($"任务诊断错误: {ex.Message}", Sk.Bad));
        }
    }

    /// <summary>原生图摘要（当前是原生格式任务时显示 CurrentState）。</summary>
    private void AppendNativeSummary()
    {
        try
        {
            var mm = MissionManager.Instance;
            if (mm == null || mm.CurrentMission == null) return;
            var g = mm.CurrentMission;
            string id = "?"; try { id = g.MissionID; } catch { }
            string cur = "?"; try { cur = g.CurrentState == null || g.CurrentState.Node == null ? "(no state)" : g.CurrentState.Node.NodeID; } catch { }
            int n = -1; try { n = g.nodes != null ? g.nodes.Count : -1; } catch { }
            _lines.Add(("—— 原生图 ——", Sk.Normal));
            _lines.Add(($"原生 {id}  currentState={cur}  nodes={n}", Sk.Info));
        }
        catch { }
    }

    /// <summary>挂起节点细节（计时器剩余 / 等待秒数）。</summary>
    private static string NodeDetail(OncNode n, OncMissionRuntime rt)
    {
        try
        {
            if (n.Kind == OncNodeKind.WaitTimerExpired && !string.IsNullOrEmpty(n.TimerId))
            {
                float rem = rt.TimerRemaining(n.TimerId);
                return rem >= 0f ? $" (计时器剩 {rem:0.0}s)" : "";
            }
            if (n.Kind == OncNodeKind.WaitSeconds) return $" (剩 {Math.Max(0f, n.Seconds - rt.Elapsed):0.0}s)";
            if (n.Kind == OncNodeKind.WaitForEvent || n.Kind == OncNodeKind.Branch)
            {
                if (n.Timeout > 0f) return $" (等事件 '{n.EventId}' 超时 {n.Timeout:0.0}s)";
                return $" (等事件 '{n.EventId}')";
            }
            return "";
        }
        catch { return ""; }
    }

    private static Sk StyleOf(OncObjective o)
    {
        if (o.IsCompleted) return Sk.Good;
        if (o.Status == OncObjectiveStatus.Failed) return Sk.Bad;
        if (o.IsActive) return Sk.Warn;
        return Sk.Dim;
    }

    // ---------------- 渲染 ----------------

    private void OnGUI()
    {
        if (!_show || _lines.Count == 0) return;
        try
        {
            int oldDepth = GUI.depth;
            GUI.depth = -100;
            // 右上角面板（与其他 F9 诊断统一位置）
            float x = Screen.width - 640f;
            float y = 12f;
            for (int i = 0; i < _lines.Count; i++)
            {
                var ln = _lines[i];
                var oldC = GUI.contentColor;
                GUI.contentColor = ColorOf(ln.Style);
                GUI.Label(new Rect(x, y, 620, 20), ln.Text);
                GUI.contentColor = oldC;
                y += 20f;
            }
            GUI.depth = oldDepth;
        }
        catch { }
    }

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
            CoopLog.Info("mission.diag", () => sb.ToString());
        }
        catch (Exception ex) { CoopLog.Warn("mission.diag", () => $"MissionNodeDiagUI dump error: {ex.Message}"); }
    }
}
