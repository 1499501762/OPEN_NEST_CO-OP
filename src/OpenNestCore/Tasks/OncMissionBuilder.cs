using System;
using System.Collections.Generic;

namespace OpenNestCore.Tasks;

/// <summary>
/// 自定义任务 Builder（C# 脚本定义方式）。用 <see cref="OncNode"/> 工厂构造节点，
/// <see cref="Add"/> 自动按顺序连线（前一节点 → 当前节点）；分支/汇合用节点上的
/// <see cref="OncNode.Or"/> / <see cref="OncNode.ToNode"/> / <see cref="OncNode.FromNode"/> 显式补充。
/// 等价 JSON 定义见 <see cref="OncMissionIO"/>（两种定义方式产出同一张任务图）。
///
/// 用法：
/// <code>
/// var m = OncMissionBuilder.Create("custom.defend")
///     .Named("坚守阵地").Described("保护阵地 5 分钟").InScene("Mission tutorial 2")
///     .Objective(new OncObjective { Id = "survive", Title = "坚守 5 分钟", Target = 300, Type = "survive" })
///     .Print("敌军接近！坚守阵地！")
///     .Objective("survive", OncObjectiveAction.Start)
///     .Wait(300f)
///     .ObjectiveComplete("survive")
///     .End()
///     .Build();
/// </code>
/// </summary>
public sealed class OncMissionBuilder
{
    private readonly OncMission _mission = new OncMission();
    private int _seq;
    private OncNode _last;

    private OncMissionBuilder() { }

    /// <summary>创建任务 builder（id 为任务唯一标识）。</summary>
    public static OncMissionBuilder Create(string id)
    {
        var b = new OncMissionBuilder();
        b._mission.Id = id;
        return b;
    }

    public OncMissionBuilder Named(string displayName) { _mission.DisplayName = displayName; return this; }
    public OncMissionBuilder Described(string description) { _mission.Description = description; return this; }
    public OncMissionBuilder InScene(string sceneName) { _mission.SceneName = sceneName; return this; }
    public OncMissionBuilder WithSeed(int seed) { _mission.Seed = seed; return this; }
    /// <summary>前置任务 id（全部完成后才解锁本任务）。</summary>
    public OncMissionBuilder Requires(params string[] missionIds)
    {
        if (missionIds != null)
            foreach (var id in missionIds)
                if (!string.IsNullOrEmpty(id) && !_mission.Requires.Contains(id))
                    _mission.Requires.Add(id);
        return this;
    }
    /// <summary>解锁条件（预留字符串）。</summary>
    public OncMissionBuilder UnlockWhen(string condition) { _mission.UnlockCondition = condition; return this; }

    /// <summary>注册目标（进度追踪；由 Objective 系节点驱动）。</summary>
    public OncMissionBuilder Objective(OncObjective objective)
    {
        if (objective != null && !string.IsNullOrEmpty(objective.Id))
        {
            for (int i = 0; i < _mission.Objectives.Count; i++)
                if (_mission.Objectives[i] != null && _mission.Objectives[i].Id == objective.Id)
                {
                    _mission.Objectives[i] = objective;
                    return this;
                }
            _mission.Objectives.Add(objective);
        }
        return this;
    }

    /// <summary>添加节点；未指定 Id 时自动生成 node-N；未指定连线时自动顺序连到前一节点。</summary>
    public OncMissionBuilder Add(OncNode node)
    {
        if (node == null) return this;
        if (string.IsNullOrEmpty(node.Id))
        {
            _seq++;
            node.Id = "node-" + _seq;
        }
        if (_last != null && node.From.Count == 0)
        {
            _last.To.Add(node.Id);
            node.From.Add(_last.Id);
        }
        if (!_mission.Nodes.Contains(node))
            _mission.Nodes.Add(node);
        _last = node;
        return this;
    }

    // ---- 语法糖（委托给 OncNode 工厂 + Add）----

    public OncMissionBuilder Wait(float seconds) => Add(OncNode.Wait(seconds));
    public OncMissionBuilder WaitFor(string eventId, float timeout = -1f)
        => Add(OncNode.WaitFor(eventId).WithTimeout(timeout));
    public OncMissionBuilder Branch(params OncEventRoute[] routes) => Add(OncNode.Branch(routes));
    public OncMissionBuilder Random() => Add(OncNode.Random());
    public OncMissionBuilder Split() => Add(OncNode.Split());
    public OncMissionBuilder Print(string text) => Add(OncNode.Print(text));
    public OncMissionBuilder Notify(string title, string description, float duration = 4f)
        => Add(OncNode.Notify(title, description, duration));
    public OncMissionBuilder Objective(string objectiveId, OncObjectiveAction action, int value = 0)
        => Add(OncNode.Objective(objectiveId, action, value));
    public OncMissionBuilder ObjectiveComplete(string objectiveId) => Add(OncNode.ObjectiveComplete(objectiveId));
    public OncMissionBuilder ObjectiveFail(string objectiveId) => Add(OncNode.ObjectiveFail(objectiveId));
    public OncMissionBuilder SpawnEntity(string entityId, string name, float x, float y, int health = 100, int armour = 0, string role = null, int stars = 0)
        => Add(OncNode.SpawnEntity(entityId, name, x, y, health, armour, role, stars));
    public OncMissionBuilder MoveEntity(string entityId, float x, float y, bool smooth = true, float seconds = 3f)
        => Add(OncNode.MoveEntity(entityId, x, y, smooth, seconds));
    public OncMissionBuilder DamageEntity(string entityId, int damage) => Add(OncNode.DamageEntity(entityId, damage));
    public OncMissionBuilder WaitEntityDestroyed(string entityId) => Add(OncNode.WaitEntityDestroyed(entityId));
    public OncMissionBuilder AddRequisitionPoints(int amount) => Add(OncNode.AddRequisitionPoints(amount));
    public OncMissionBuilder AddShell(string shellId, int amount, int slot = -1) => Add(OncNode.AddShell(shellId, amount, slot));
    public OncMissionBuilder AddPowderCharge(int amount) => Add(OncNode.AddPowderCharge(amount));
    public OncMissionBuilder StartTimer(string timerId, float seconds) => Add(OncNode.StartTimer(timerId, seconds));
    public OncMissionBuilder WaitTimerExpired(string timerId) => Add(OncNode.WaitTimerExpired(timerId));
    public OncMissionBuilder Unlock(string objectId) => Add(OncNode.Unlock(objectId));
    public OncMissionBuilder Impact(float x, float y) => Add(OncNode.Impact(x, y));
    public OncMissionBuilder End() => Add(OncNode.End());
    public OncMissionBuilder Fail() => Add(OncNode.Fail());
    public OncMissionBuilder Custom(Action<OncMissionContext> action) => Add(OncNode.Custom(action));

    /// <summary>构造任务图（自动确定入口节点）。</summary>
    public OncMission Build()
    {
        if (string.IsNullOrEmpty(_mission.EntryPointId))
        {
            // 入口：显式 EntryPointId > 第一个 Start 节点 > nodes[0]
            for (int i = 0; i < _mission.Nodes.Count; i++)
                if (_mission.Nodes[i] != null && _mission.Nodes[i].Kind == OncNodeKind.Start)
                {
                    _mission.EntryPointId = _mission.Nodes[i].Id;
                    break;
                }
            if (string.IsNullOrEmpty(_mission.EntryPointId) && _mission.Nodes.Count > 0)
                _mission.EntryPointId = _mission.Nodes[0].Id;
        }
        return _mission;
    }

    // ---- 战役快捷构造 ----

    /// <summary>把任务包成战役（Operation）——只有一个任务的战役。</summary>
    public OncOperation BuildOperation(string operationId = null)
    {
        var m = Build();
        var op = new OncOperation
        {
            Id = string.IsNullOrEmpty(operationId) ? (m.Id + ".op") : operationId,
            DisplayName = m.DisplayName,
            Description = m.Description,
        };
        var mr = new OncMissionRef(m.Id);
        for (int i = 0; i < m.Requires.Count; i++) mr.Requires.Add(m.Requires[i]);
        op.Missions.Add(mr);
        return op;
    }
}
