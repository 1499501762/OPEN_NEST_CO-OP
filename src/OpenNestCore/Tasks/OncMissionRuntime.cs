using System;
using System.Collections.Generic;

namespace OpenNestCore.Tasks;

/// <summary>
/// 自定义任务运行时（节点图状态机执行器，平台无关）。
/// 驱动 <see cref="OncMission"/> 的节点图：从入口激活节点，瞬时节点执行动作后沿出边继续，
/// 挂起节点（等待秒/事件/实体/计时器）留在激活表，由 <see cref="Update"/> 推进 / <see cref="Raise"/> 喂事件。
///
/// 图语义（对应游戏 SleepyNodes 状态图，见 docs/TASK_SYSTEM.md）：
/// - 节点出边（To）多个 = 分支/并行（Split）；入边（From）多个 = 汇合（全部完成才激活）；
/// - <see cref="OncNodeKind.WaitForEvent"/>/<see cref="OncNodeKind.Branch"/> 用事件路由表分流；
/// - <see cref="OncNodeKind.End"/> 成功 / <see cref="OncNodeKind.Fail"/> 失败结束；
/// - 无 End 且所有路径走完 = 自动成功。
///
/// 输出经 <see cref="IOncMissionHost"/>（可为 null）交给游戏侧；也可订阅本类事件自己做 UI。
/// 联机同步：<see cref="BuildSyncState"/> 导出"序号 + 目标状态"小负载（"同步序号"方案，见 docs/CUSTOM_MISSION.md）。
/// </summary>
public sealed class OncMissionRuntime
{
    public OncMission Mission { get; }
    public IOncMissionHost Host { get; }

    public bool IsRunning { get; private set; }
    public bool IsFinished { get; private set; }
    public bool IsSuccess { get; private set; }
    public bool IsFailed { get; private set; }
    public bool IsCanceled { get; private set; }

    /// <summary>任务开始后经过的秒数。</summary>
    public float Elapsed { get; private set; }

    /// <summary>任务变量（≈ StateGraph.Variables）。</summary>
    public Dictionary<string, object> Variables { get; } = new Dictionary<string, object>();

    /// <summary>任务目标（Id → 目标；构造时从 Mission.Objectives 复制）。</summary>
    public Dictionary<string, OncObjective> Objectives { get; } = new Dictionary<string, OncObjective>();

    // ---- 生命周期事件 ----
    public event Action<OncMissionRuntime> OnStarted;
    public event Action<OncMissionRuntime, OncNode> OnNodeEntered;
    public event Action<OncMissionRuntime, OncNode> OnNodeCompleted;
    public event Action<OncMissionRuntime> OnCompleted;
    public event Action<OncMissionRuntime> OnFailed;
    public event Action<OncMissionRuntime> OnCanceled;
    public event Action<OncMissionRuntime, OncObjective> OnObjectiveChanged;

    // ---- 内部状态 ----
    private sealed class ActiveNode
    {
        public OncNode Node;
        public float Timer;      // WaitSeconds 累计 / 等待超时累计
    }

    private readonly HashSet<string> _done = new HashSet<string>();
    private readonly List<ActiveNode> _active = new List<ActiveNode>();
    private readonly Dictionary<string, float> _timers = new Dictionary<string, float>();  // 计时器剩余秒
    private readonly HashSet<string> _timersPaused = new HashSet<string>();
    private readonly Random _rng = new Random();
    private float _lastDt;
    private int _activationDepth;

    public OncMissionRuntime(OncMission mission, IOncMissionHost host = null)
    {
        Mission = mission;
        Host = host;
        if (mission != null && mission.Objectives != null)
            for (int i = 0; i < mission.Objectives.Count; i++)
                if (mission.Objectives[i] != null)
                    Objectives[mission.Objectives[i].Id] = mission.Objectives[i];
    }

    // ---------- 生命周期 ----------

    /// <summary>启动任务：复位并从入口激活。</summary>
    public void Start()
    {
        if (Mission == null || Mission.Nodes == null || Mission.Nodes.Count == 0)
        {
            IsFinished = true;
            IsFailed = true;
            return;
        }
        IsRunning = true;
        IsFinished = IsSuccess = IsFailed = IsCanceled = false;
        Elapsed = 0f;
        _lastDt = 0f;
        _done.Clear();
        _active.Clear();
        _timers.Clear();
        _timersPaused.Clear();
        foreach (var kv in Objectives)
            if (kv.Value != null) kv.Value.Status = OncObjectiveStatus.Inactive;

        string entry = !string.IsNullOrEmpty(Mission.EntryPointId) ? Mission.EntryPointId : FindStartNodeId();
        if (string.IsNullOrEmpty(entry)) entry = Mission.Nodes[0].Id;

        try { Host?.OnMissionStarted(this); } catch { }
        try { OnStarted?.Invoke(this); } catch { }

        ActivateNode(entry);
        CheckAutoComplete();
    }

    /// <summary>每帧推进（等待节点计时/超时、计时器递减、实体摧毁轮询）。由外部驱动。</summary>
    public void Update(float dt)
    {
        if (!IsRunning) return;
        Elapsed += dt;
        _lastDt = dt;

        TickTimers(dt);

        // 遍历激活表（倒序遍历，允许完成时移除）
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var a = _active[i];
            if (a == null || a.Node == null) continue;
            switch (a.Node.Kind)
            {
                case OncNodeKind.WaitSeconds:
                    a.Timer += dt;
                    if (a.Timer >= a.Node.Seconds) CompleteNode(a.Node.Id);
                    break;

                case OncNodeKind.WaitForEvent:
                case OncNodeKind.Branch:
                    if (a.Node.Timeout > 0f && a.Timer >= a.Node.Timeout)
                        CompleteNode(a.Node.Id);
                    else
                        a.Timer += dt;
                    break;

                case OncNodeKind.WaitEntityDestroyed:
                    if (Host == null || SafeIsDestroyed(a.Node.EntityId))
                        CompleteNode(a.Node.Id);
                    break;

                case OncNodeKind.WaitTimerExpired:
                    if (TimerExpired(a.Node.TimerId))
                        CompleteNode(a.Node.Id);
                    break;
            }
        }
    }

    /// <summary>触发任务事件（驱动 WaitFor/Branch 节点；也可由宿主上报实体摧毁/计时器到期等）。</summary>
    public void Raise(string eventId, object payload = null) => Raise(new OncEvent(eventId, payload));

    public void Raise(OncEvent evt)
    {
        if (!IsRunning || evt == null || string.IsNullOrEmpty(evt.Id)) return;
        for (int i = 0; i < _active.Count; i++)
        {
            var a = _active[i];
            if (a == null || a.Node == null) continue;
            var n = a.Node;
            if (n.Kind != OncNodeKind.WaitForEvent && n.Kind != OncNodeKind.Branch) continue;

            string target = null;
            bool hit = false;
            if (n.Routes != null && n.Routes.Count > 0)
            {
                for (int r = 0; r < n.Routes.Count; r++)
                    if (n.Routes[r] != null && n.Routes[r].EventId == evt.Id)
                    {
                        hit = true;
                        target = n.Routes[r].TargetNodeId;
                        break;
                    }
            }
            else if (n.EventId == evt.Id)
            {
                hit = true;
            }

            if (hit)
            {
                if (string.IsNullOrEmpty(target))
                    CompleteNode(n.Id);
                else
                    CompleteNodeThenActivate(n.Id, target);
                return;
            }
        }
    }

    /// <summary>手动标记任务成功。</summary>
    public void Complete()
    {
        if (!IsRunning) return;
        IsRunning = false;
        IsFinished = true;
        IsSuccess = true;
        foreach (var kv in Objectives)
            if (kv.Value != null && kv.Value.Status == OncObjectiveStatus.Active)
                kv.Value.Status = OncObjectiveStatus.Completed;
        try { Host?.OnMissionCompleted(this); } catch { }
        try { OnCompleted?.Invoke(this); } catch { }
    }

    /// <summary>手动标记任务失败。</summary>
    public void Fail(string reason = null)
    {
        if (!IsRunning) return;
        IsRunning = false;
        IsFinished = true;
        IsFailed = true;
        foreach (var kv in Objectives)
            if (kv.Value != null && kv.Value.Status == OncObjectiveStatus.Active)
                kv.Value.Status = OncObjectiveStatus.Failed;
        try { Host?.OnMissionFailed(this); } catch { }
        try { OnFailed?.Invoke(this); } catch { }
    }

    /// <summary>取消任务（不成功也不失败）。</summary>
    public void Cancel()
    {
        if (!IsRunning) return;
        IsRunning = false;
        IsFinished = true;
        IsCanceled = true;
        try { Host?.OnMissionCanceled(this); } catch { }
        try { OnCanceled?.Invoke(this); } catch { }
    }

    // ---------- 目标 API ----------

    public OncObjective Objective(string id)
    {
        if (id == null) return null;
        return Objectives.TryGetValue(id, out var o) ? o : null;
    }

    public void SetObjectiveProgress(string id, int value) => MutateObjective(id, OncObjectiveAction.SetProgress, value);
    public void AddObjectiveProgress(string id, int delta) => MutateObjective(id, OncObjectiveAction.AddProgress, delta);
    public void CompleteObjective(string id) => SetObjectiveStatus(id, OncObjectiveStatus.Completed);
    public void FailObjective(string id) => SetObjectiveStatus(id, OncObjectiveStatus.Failed);

    // ---------- 计时器 API（宿主/外部可查）----------

    public float TimerRemaining(string timerId)
        => timerId != null && _timers.TryGetValue(timerId, out float rem) ? rem : -1f;
    public bool TimerExpired(string timerId)
        => timerId != null && _timers.TryGetValue(timerId, out float rem) && rem <= 0f;

    // ---------- 联机同步（"同步序号"方案）----------

    /// <summary>导出任务当前同步状态（序号 + 目标状态）——供联机"同步序号"小负载。</summary>
    public OncMissionSyncState BuildSyncState()
    {
        var s = new OncMissionSyncState();
        s.MissionId = Mission?.Id;
        s.DoneCount = _done.Count;
        s.DoneNodeIds = new List<string>(_done); // 已完成节点 ID 集合（精确恢复用）
        s.ActiveNodeIds = new List<string>();
        for (int i = 0; i < _active.Count; i++)
            if (_active[i] != null && _active[i].Node != null)
                s.ActiveNodeIds.Add(_active[i].Node.Id);
        s.Objectives = new List<OncObjectiveState>();
        foreach (var kv in Objectives)
            if (kv.Value != null)
                s.Objectives.Add(new OncObjectiveState
                {
                    Id = kv.Value.Id,
                    Status = (int)kv.Value.Status,
                    Progress = kv.Value.Progress,
                });
        s.IsFinished = IsFinished;
        s.IsSuccess = IsSuccess;
        return s;
    }

    /// <summary>应用"同步序号"负载（联机主机权威：客机按主机快照对齐运行时状态）。
    /// 对齐：已完成节点集 + 激活集 + 目标状态 + 结束标记。不重放节点动作（动作由各自图执行）。
    /// 供 Core 引擎任务（<see cref="OncMissionRuntime"/> 驱动）联机使用；原生格式 CSM 走原生图
    /// （联机由 MissionSync/MissionSyncV2 同步 scene/seed + 原生图节点）。</summary>
    public void ApplySyncState(OncMissionSyncState s)
    {
        if (s == null || Mission == null) return;
        if (s.MissionId != null && s.MissionId != Mission.Id) return; // 任务不匹配 → 忽略

        // 已完成节点集：主机权威（客机本地多做的动作以下次主机快照为准回退）
        _done.Clear();
        if (s.DoneNodeIds != null)
            for (int i = 0; i < s.DoneNodeIds.Count; i++)
            {
                var id = s.DoneNodeIds[i];
                if (id != null && Mission.Node(id) != null) _done.Add(id);
            }

        // 激活集：清空并重建为主机激活节点（客机本地挂起的等待节点被主机权威覆盖）
        _active.Clear();
        if (s.ActiveNodeIds != null)
            for (int i = 0; i < s.ActiveNodeIds.Count; i++)
            {
                var id = s.ActiveNodeIds[i];
                if (id == null) continue;
                var node = Mission.Node(id);
                if (node == null) continue;
                _active.Add(new ActiveNode { Node = node });
                try { OnNodeEntered?.Invoke(this, node); } catch { }
                try { Host?.OnNodeEntered(this, node); } catch { }
            }

        // 目标状态
        if (s.Objectives != null)
            for (int i = 0; i < s.Objectives.Count; i++)
            {
                var os = s.Objectives[i];
                if (os == null) continue;
                var o = Objective(os.Id);
                if (o == null) continue;
                o.Status = (OncObjectiveStatus)os.Status;
                o.Progress = os.Progress;
                try { OnObjectiveChanged?.Invoke(this, o); } catch { }
            }

        // 结束标记
        if (s.IsFinished)
        {
            if (s.IsSuccess) { if (IsRunning) Complete(); else { IsRunning = false; IsFinished = true; IsSuccess = true; } }
            else { if (IsRunning) Fail(); else { IsRunning = false; IsFinished = true; IsFailed = true; } }
        }
    }

    // ---------- 内部：图执行 ----------

    private string FindStartNodeId()
    {
        for (int i = 0; i < Mission.Nodes.Count; i++)
            if (Mission.Nodes[i] != null && Mission.Nodes[i].Kind == OncNodeKind.Start)
                return Mission.Nodes[i].Id;
        return null;
    }

    private bool IsActive(string id)
    {
        for (int i = 0; i < _active.Count; i++)
            if (_active[i] != null && _active[i].Node != null && _active[i].Node.Id == id)
                return true;
        return false;
    }

    private void ActivateNode(string id)
    {
        var node = Mission.Node(id);
        if (node == null) return;
        if (_done.Contains(id) || IsActive(id)) return;

        // 汇合：所有入边（From）节点必须已完成
        for (int i = 0; i < node.From.Count; i++)
            if (!_done.Contains(node.From[i]))
                return;

        if (++_activationDepth > 1000)
        {
            _activationDepth = 0;
            Fail("mission graph loop guard");
            return;
        }

        _active.Add(new ActiveNode { Node = node });
        try { OnNodeEntered?.Invoke(this, node); } catch { }
        try { Host?.OnNodeEntered(this, node); } catch { }
        ExecuteNode(node);
        _activationDepth = 0;
    }

    /// <summary>执行节点：挂起类留下；瞬时类执行动作并完成。</summary>
    private void ExecuteNode(OncNode node)
    {
        switch (node.Kind)
        {
            case OncNodeKind.WaitSeconds:
            case OncNodeKind.WaitForEvent:
            case OncNodeKind.Branch:
            case OncNodeKind.WaitEntityDestroyed:
            case OncNodeKind.WaitTimerExpired:
                return; // 挂起，等 Update / Raise

            case OncNodeKind.End:
                Complete();
                return;
            case OncNodeKind.Fail:
                Fail();
                return;

            default:
                RunAction(node);
                if (IsRunning) CompleteNode(node.Id);
                return;
        }
    }

    /// <summary>瞬时节点动作（游戏无关：经 Host 交给游戏执行；日志/目标/计时/自定义在本地执行）。</summary>
    private void RunAction(OncNode n)
    {
        try
        {
            switch (n.Kind)
            {
                case OncNodeKind.Teleprinter:
                    Host?.PrintTeleprinter(n.Message);
                    break;
                case OncNodeKind.Notify:
                    Host?.ShowNotification(n.Title, n.Message, n.Duration);
                    break;
                case OncNodeKind.SceneNotification:
                    Host?.SendSceneNotification(n.NotifId);
                    break;

                case OncNodeKind.Objective:
                    MutateObjective(n.ObjectiveId, n.ObjectiveAction, n.Value);
                    break;
                case OncNodeKind.ObjectiveComplete:
                    SetObjectiveStatus(n.ObjectiveId, OncObjectiveStatus.Completed);
                    break;
                case OncNodeKind.ObjectiveFail:
                    SetObjectiveStatus(n.ObjectiveId, OncObjectiveStatus.Failed);
                    break;

                case OncNodeKind.SpawnEntity:
                    Host?.SpawnEntity(n);
                    break;
                case OncNodeKind.MoveEntity:
                    Host?.MoveEntity(n);
                    break;
                case OncNodeKind.DamageEntity:
                    Host?.DamageEntity(n.EntityId, n.Value);
                    break;
                case OncNodeKind.SetEntityState:
                    Host?.SetEntityState(n.EntityId, n.Role, n.Value);
                    break;
                case OncNodeKind.Impact:
                    Host?.TriggerImpact(n.X, n.Y);
                    break;

                case OncNodeKind.AddRequisitionPoints:
                    Host?.AddRequisitionPoints(n.Value);
                    break;
                case OncNodeKind.AddShell:
                    Host?.AddShell(n.ShellId, n.Value, n.Slot);
                    break;
                case OncNodeKind.AddPowderCharge:
                    Host?.AddPowderCharge(n.Value);
                    break;

                case OncNodeKind.StartTimer:
                    _timers[n.TimerId] = Math.Max(0f, n.Seconds);
                    _timersPaused.Remove(n.TimerId);
                    break;
                case OncNodeKind.StopTimer:
                    _timers.Remove(n.TimerId);
                    _timersPaused.Remove(n.TimerId);
                    break;
                case OncNodeKind.PauseTimer:
                    _timersPaused.Add(n.TimerId);
                    break;
                case OncNodeKind.ResumeTimer:
                    _timersPaused.Remove(n.TimerId);
                    break;
                case OncNodeKind.AddTimerTime:
                    if (_timers.TryGetValue(n.TimerId, out float rem)) _timers[n.TimerId] = rem + n.Seconds;
                    else _timers[n.TimerId] = n.Seconds;
                    break;

                case OncNodeKind.UnlockSceneObject:
                    Host?.UnlockSceneObject(n.NotifId);
                    break;

                case OncNodeKind.Custom:
                    if (n.Action != null)
                    {
                        var ctx = new OncMissionContext
                        {
                            Mission = Mission,
                            Runtime = this,
                            Host = Host,
                            DeltaTime = _lastDt,
                            Time = Elapsed,
                            Variables = Variables,
                        };
                        n.Action(ctx);
                    }
                    break;
            }
        }
        catch (Exception)
        {
            // 节点动作异常不应中断任务流程（宿主实现各自 try-catch，这里兜底）
        }
    }

    private void CompleteNode(string id)
    {
        var node = Mission.Node(id);
        RemoveActive(id);
        _done.Add(id);
        if (node != null) { try { OnNodeCompleted?.Invoke(this, node); } catch { } }

        if (node != null && node.Kind == OncNodeKind.RandomBranch)
        {
            if (node.To.Count > 0)
                ActivateNode(node.To[_rng.Next(node.To.Count)]);
        }
        else if (node != null)
        {
            for (int i = 0; i < node.To.Count; i++)
                ActivateNode(node.To[i]);
        }
        CheckAutoComplete();
    }

    /// <summary>完成节点但只激活指定目标（事件路由精确分流）。</summary>
    private void CompleteNodeThenActivate(string id, string targetId)
    {
        var node = Mission.Node(id);
        RemoveActive(id);
        _done.Add(id);
        if (node != null) { try { OnNodeCompleted?.Invoke(this, node); } catch { } }
        if (!string.IsNullOrEmpty(targetId)) ActivateNode(targetId);
        CheckAutoComplete();
    }

    private void RemoveActive(string id)
    {
        for (int i = _active.Count - 1; i >= 0; i--)
            if (_active[i] != null && _active[i].Node != null && _active[i].Node.Id == id)
            {
                _active.RemoveAt(i);
                return;
            }
    }

    private void CheckAutoComplete()
    {
        if (IsRunning && _active.Count == 0 && _done.Count > 0)
            Complete(); // 所有路径走完且无 End → 自动成功
    }

    // ---------- 内部：计时器 / 目标 ----------

    private void TickTimers(float dt)
    {
        if (_timers.Count == 0) return;
        var keys = new List<string>(_timers.Count);
        foreach (var kv in _timers) keys.Add(kv.Key);
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            if (_timersPaused.Contains(k)) continue;
            float rem = _timers[k] - dt;
            _timers[k] = rem < 0f ? 0f : rem;
        }
    }

    private bool SafeIsDestroyed(string entityId)
    {
        try { return Host.IsEntityDestroyed(entityId); }
        catch { return false; }
    }

    private void MutateObjective(string id, OncObjectiveAction action, int value)
    {
        var o = Objective(id);
        if (o == null) return;
        switch (action)
        {
            case OncObjectiveAction.Start:
                o.Status = OncObjectiveStatus.Active;
                o.Progress = 0;
                break;
            case OncObjectiveAction.SetProgress:
                o.Progress = value < 0 ? 0 : value;
                break;
            case OncObjectiveAction.AddProgress:
                o.Progress += value;
                if (o.Progress < 0) o.Progress = 0;
                break;
        }
        if (o.Status == OncObjectiveStatus.Active && o.Target > 0 && o.Progress >= o.Target)
            o.Status = OncObjectiveStatus.Completed;
        try { OnObjectiveChanged?.Invoke(this, o); } catch { }
    }

    private void SetObjectiveStatus(string id, OncObjectiveStatus status)
    {
        var o = Objective(id);
        if (o == null) return;
        o.Status = status;
        try { OnObjectiveChanged?.Invoke(this, o); } catch { }
    }
}

/// <summary>任务同步状态（"同步序号"小负载）：已完成节点数 + 当前激活节点序号 + 目标状态。</summary>
public sealed class OncMissionSyncState
{
    public string MissionId;
    public bool IsFinished;
    public bool IsSuccess;
    public int DoneCount;
    public List<string> DoneNodeIds;      // 已完成节点 ID 集合（精确恢复用）
    public List<string> ActiveNodeIds;    // 当前激活（挂起）节点
    public List<OncObjectiveState> Objectives;
}

/// <summary>目标同步状态（供"同步序号"负载）。</summary>
public sealed class OncObjectiveState
{
    public string Id;
    public int Status;
    public int Progress;
}
