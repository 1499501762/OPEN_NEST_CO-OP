using System;

namespace OpenNestCore.Tasks;

/// <summary>
/// 任务宿主桥接接口（游戏侧实现）：把 Core 自定义任务引擎的输出接到游戏真实任务系统
/// （场景加载 / 打字机 / UI 通知 / 地图实体 / 奖励 / 结算 / 随机种子 / 前置后置解锁）。
/// Core 侧只依赖此接口，不依赖游戏类型，保证 OpenNestCore 平台无关。
///
/// 实现建议：继承 <see cref="OncMissionHostAdapter"/>（所有方法空实现），只覆写需要的。
/// 游戏侧参考实现：OpenNestCoop.GameSync.OncMissionBridge 的默认 host。
/// </summary>
public interface IOncMissionHost
{
    // ---- 任务生命周期 ----
    void OnMissionStarted(OncMissionRuntime runtime);
    void OnNodeEntered(OncMissionRuntime runtime, OncNode node);
    void OnMissionCompleted(OncMissionRuntime runtime);
    void OnMissionFailed(OncMissionRuntime runtime);
    void OnMissionCanceled(OncMissionRuntime runtime);

    // ---- 场景 / 前置后置 / 随机 ----
    /// <summary>加载任务场景（mission.SceneName）。返回是否已加载。</summary>
    bool LoadMissionScene(OncMission mission);

    /// <summary>前置任务解锁判定（战役链：mission.Requires 是否都完成）。返回是否可启动。</summary>
    bool IsMissionUnlocked(OncMission mission);

    /// <summary>应用任务随机种子（对应 FireMission.useFixedSeed/fixedSeed）。返回实际使用的 seed。</summary>
    int ApplySeed(OncMission mission);

    // ---- 输出 ----
    /// <summary>弹 UI 通知（对应 UINotificationManager.ShowNotification）。</summary>
    void ShowNotification(string title, string description, float duration);

    /// <summary>打印打字机文本（对应 Teleprinter.SubmitLines）。</summary>
    void PrintTeleprinter(string text);

    /// <summary>场景通知（对应 State_SendSceneNotification）。</summary>
    void SendSceneNotification(string notifId);

    // ---- 地图实体（Fire 目标）----
    /// <summary>在地图生成实体（对应 FireMission.CreateMapEntity）。</summary>
    void SpawnEntity(OncNode node);

    /// <summary>移动实体到地图坐标（对应 FireMission.MoveMapEntity）。</summary>
    void MoveEntity(OncNode node);

    /// <summary>对实体造成伤害（对应 State_DamageEntity）。</summary>
    void DamageEntity(string entityId, int damage);

    /// <summary>设置实体状态（对应 State_SetEntityState）。</summary>
    void SetEntityState(string entityId, string state, int value);

    /// <summary>触发着弹（对应 State_TriggerImpact / ImpactGraph）。</summary>
    void TriggerImpact(float x, float y);

    /// <summary>实体是否已被摧毁（驱动 WaitEntityDestroyed 节点）。</summary>
    bool IsEntityDestroyed(string entityId);

    // ---- 奖励 / 资源 ----
    void AddRequisitionPoints(int amount);
    void AddShell(string shellId, int amount, int slot);
    void AddPowderCharge(int amount);

    // ---- 解锁 ----
    void UnlockSceneObject(string objectId);

    // ---- 地图图（MissionMapLoader）----
    /// <summary>异步加载任务地图图（IronRoadMap 地图/地形图，走游戏 MissionMapLoader.Acquire）。
    /// <paramref name="onLoaded"/> 参数为 UnityEngine.Sprite（加载失败为 null）。</summary>
    void LoadMapSprite(OncMission mission, bool topography, Action<object> onLoaded);

    // ---- 脚本化模块 ----
    /// <summary>执行脚本化模块（<see cref="OncNodeKind.Scripted"/> 节点）。
    /// 宿主按 <paramref name="ctx"/>.ModuleName 分派到注册的脚本模块（C# 回调）或解释 Args（JSON）。</summary>
    void RunScriptedModule(OncScriptContext ctx);

    /// <summary>脚本化条件求值（<see cref="OncNodeKind.ScriptedCondition"/>）：分派模块，读 <paramref name="ctx"/>.BoolResult。</summary>
    bool RunScriptedCondition(OncScriptContext ctx);

    /// <summary>脚本化挂起轮询（<see cref="OncNodeKind.ScriptedWait"/>）：每帧问模块"好了没"，读 BoolResult。</summary>
    bool RunScriptedWait(OncScriptContext ctx);

    /// <summary>脚本事件广播（A4 联机）：主机权威把脚本事件广播给全员，客机收到后本地触发。</summary>
    void BroadcastScriptEvent(string eventId, object payload);

    /// <summary>计时器到期回调（A2：Core runtime 计时器归零 → 宿主转发 "timer.expired.&lt;id&gt;" 事件）。</summary>
    void OnTimerExpired(string timerId);
}

/// <summary>
/// 任务宿主空实现基类：桥接实现继承它只覆写需要的回调，避免实现全部接口方法。
/// </summary>
public abstract class OncMissionHostAdapter : IOncMissionHost
{
    public virtual void OnMissionStarted(OncMissionRuntime runtime) { }
    public virtual void OnNodeEntered(OncMissionRuntime runtime, OncNode node) { }
    public virtual void OnMissionCompleted(OncMissionRuntime runtime) { }
    public virtual void OnMissionFailed(OncMissionRuntime runtime) { }
    public virtual void OnMissionCanceled(OncMissionRuntime runtime) { }

    public virtual bool LoadMissionScene(OncMission mission) => false;
    public virtual bool IsMissionUnlocked(OncMission mission) => true;
    public virtual int ApplySeed(OncMission mission) => mission.Seed;

    public virtual void ShowNotification(string title, string description, float duration) { }
    public virtual void PrintTeleprinter(string text) { }
    public virtual void SendSceneNotification(string notifId) { }

    public virtual void SpawnEntity(OncNode node) { }
    public virtual void MoveEntity(OncNode node) { }
    public virtual void DamageEntity(string entityId, int damage) { }
    public virtual void SetEntityState(string entityId, string state, int value) { }
    public virtual void TriggerImpact(float x, float y) { }
    public virtual bool IsEntityDestroyed(string entityId) => false;

    public virtual void AddRequisitionPoints(int amount) { }
    public virtual void AddShell(string shellId, int amount, int slot) { }
    public virtual void AddPowderCharge(int amount) { }

    public virtual void UnlockSceneObject(string objectId) { }

    public virtual void LoadMapSprite(OncMission mission, bool topography, Action<object> onLoaded) { }

    public virtual void RunScriptedModule(OncScriptContext ctx) { }
    public virtual bool RunScriptedCondition(OncScriptContext ctx) => true;
    public virtual bool RunScriptedWait(OncScriptContext ctx) => true;
    public virtual void BroadcastScriptEvent(string eventId, object payload) { }
    public virtual void OnTimerExpired(string timerId) { }
}
