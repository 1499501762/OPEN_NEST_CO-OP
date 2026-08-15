using OpenNestCoop.Core;
using OpenNestCoop.GameSync;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 新同步方案（分层架构）注册入口。`--sync new` 时由 <see cref="CoopRuntime.Startup"/> 调用，
/// 替代 V1 的整组模块注册（不注册旧模块，避免 MsgType 冲突与双重同步）。
///
/// 分层目标（开发文档 docs/SYNC_V2_DEV.md）：
///   HostDataLayer（主机权威数据层）/ EventLayer（事件层）/ ValueLayer（数值层）/ ButtonLayer（按钮层）。
/// 硬性约束：MsgType ≥ 200（现有 1-32 / 100+ / 120 / 131 / 133 / 134 不能重叠）；
///           主机权威不变；事件复现保留防环；双端同方案（Hello/Welcome 握手校验）。
///
/// 当前进度：M1 骨架完成——`--sync new` 对接（AutoJoin 解析 + CoopRuntime 分支 + Hello/Welcome 握手校验
/// + dualtest -Sync 传参）。V2 走 CoopSyncRegistry 统一路由/Tick，MsgType 独立段 200+。
/// 里程碑 M2-M8（docs/SYNC_V2_TASKS.md 轨道 B）：
///   M2 HostDataLayer（IHostStore/IRoleAuthority + 授权 + 广播）
///   M3 ValueLayer（MsgType=201，迁移一个 ValueSync Binding 验证值一致）
///   M4 EventLayer（MsgType=200，泛型事件广播 + 对端复现 + 防环）
///   M5 ButtonLayer（MsgType=202，按钮 toggle 状态跨端一致）
///   M6 迁移 ControlSync 发现逻辑到 HostDataLayer（吸收场景加载事件驱动，去 3s 全场景扫描）
///   M7 按 ARCHITECTURE.md 顺序迁移其余模块；M8 收尾（V1 保留，V2 全绿）。
/// </summary>
public static class SyncV2Bootstrap
{
    /// <summary>同步方案标识：Hello/Welcome 握手校验用（0=old V1，1=new SyncV2）。</summary>
    public static byte SchemeId => 1;

    // ---- SyncV2 MsgType 分配（≥200，独立段，见 NetProtocol.MsgType）----
    public const byte EventMsgType = 200;   // EventLayer：泛型事件广播 + 对端复现
    public const byte ValueMsgType = 201;   // ValueLayer：值同步（deadzone/心跳/插值/settle）
    public const byte ButtonMsgType = 202;  // ButtonLayer：按钮/交互控件状态（toggle/click）

    /// <summary>注册所有 V2 分层模块（MsgType ≥ 200）。CoopRuntime.Startup 在 `--sync new` 时调用。</summary>
    public static void RegisterAll()
    {
        // M2：HostDataLayer（主机权威数据层，MsgType=203）——其他层经它读写/广播，不直接碰控件/网络。
        CoopSyncRegistry.RegisterModule(HostDataLayer.Instance);
        // M3：ValueLayer（数值层，MsgType=201）——值同步（deadzone/edge/心跳/插值/settle），
        // 权威模型：Operator（谁操作谁权威，默认，客机可本地执行+广播+心跳对齐）/ Host（主机权威，仅主机广播）。
        CoopSyncRegistry.RegisterModule(ValueLayer.Instance);
        // M4：EventLayer（事件层，MsgType=200）——纯事件广播 + 对端复现 + 防环（_reproducing，约束#3）。
        // 权威模型同 ValueLayer；M4 迁移开火事件（host 权威：客机请求→主机执行→广播复现）。
        CoopSyncRegistry.RegisterModule(EventLayer.Instance);
        // M5：ButtonLayer（按钮层，MsgType=202）——按钮/交互控件 toggle 状态跨端一致；
        // 点击事件交给 EventLayer（Operator 权威：谁点谁发+对端复现），状态轮询走本层（202，实例缓存去扫描）。
        CoopSyncRegistry.RegisterModule(ButtonLayer.Instance);
        // M6：ControlSyncV2（控件发现层，MsgType=204 占位，无自身消息）——把场景控件注册进 ValueLayer：
        // 场景加载事件驱动 + 12s 低频兜底（去 V1 3s 全场景扫描，吸收 ARCHITECTURE.md B1 优化）。
        CoopSyncRegistry.RegisterModule(ControlSyncV2.Instance);
        // M7：PlayerSyncV2（玩家位置/朝向，MsgType=205）——Operator 权威（谁移动谁上报+星型中继），
        // 复用 V1 视觉 Provider 基建（渲染与同步解耦）。
        CoopSyncRegistry.RegisterModule(PlayerSyncV2.Instance);
        // M7：EntitySyncV2（任务实体状态，MsgType=206）——Host 权威（客机聚合上行→主机应用→广播），
        // 主数据源 FireMission.Entities 字典 + 显式枚举器（IL2CPP）。
        CoopSyncRegistry.RegisterModule(EntitySyncV2.Instance);
        // M7：CatSyncV2（猫同步，MsgType=207）——主机 AI 决策软同步 + held 上行 + 偏差硬同步；
        // 交互事件（拾/放/驱赶/抚摸/打断）走 EventLayer（Operator 权威，IsApplyingCat 防环）。
        CoopSyncRegistry.RegisterModule(CatSyncV2.Instance);
        // M7：RecordPlayerSyncV2（唱片机，MsgType=208）——谁操作谁变更，经主机传播；OnLateJoin 快照。
        CoopSyncRegistry.RegisterModule(RecordPlayerSyncV2.Instance);
        // M7：ReloadSyncV2（装填/弹种，MsgType=209/210/214）——Host 权威状态快照 + 粉末事件→EventLayer(Operator)。
        CoopSyncRegistry.RegisterModule(ReloadSyncV2.Instance,
            (byte)OpenNestCoop.Net.MsgType.V2ReloadCmd,
            (byte)OpenNestCoop.Net.MsgType.V2ReloadSnapshotReq);
        // M7：纯事件层（无自身 MsgType，走 EventLayer 200）——实例化即注册复现回调。
        _ = MissionEventSyncV2.Instance;   // 任务过渡事件（完成/失败/重载/回菜单）
        _ = NotificationSyncV2.Instance;   // 打字机通知
        // M7：ReconPhotoSyncV2（侦察照片 seed，MsgType=215，Host 权威）+ CoffeeSyncV2（咖啡机，216，Host 权威）。
        CoopSyncRegistry.RegisterModule(ReconPhotoSyncV2.Instance);
        CoopSyncRegistry.RegisterModule(CoffeeSyncV2.Instance);
        // M7：CounterBatterySyncV2（反炮兵 seed，217，Host 权威）+ RecordItemSyncV2（唱片位置，218，Host 权威）。
        CoopSyncRegistry.RegisterModule(CounterBatterySyncV2.Instance);
        CoopSyncRegistry.RegisterModule(RecordItemSyncV2.Instance);
        // M7：ShellSyncV2（弹舱，219，Host 权威）+ SequenceSyncV2（发射台开关，220，谁变化谁广播）。
        CoopSyncRegistry.RegisterModule(ShellSyncV2.Instance);
        CoopSyncRegistry.RegisterModule(SequenceSyncV2.Instance);
        // M7：HatchSyncV2（舱门，221）+ MapTokenSyncV2（战术标记，222）——谁变化谁广播，OnLateJoin 快照。
        CoopSyncRegistry.RegisterModule(HatchSyncV2.Instance);
        CoopSyncRegistry.RegisterModule(MapTokenSyncV2.Instance);
        // M7：PurchaseSyncV2（购买，223，Host 权威事件）。
        CoopSyncRegistry.RegisterModule(PurchaseSyncV2.Instance);
        // M7：PunchcardSyncV2（征信点卡牌，224 状态 + 225 卡槽事件）。
        CoopSyncRegistry.RegisterModule(PunchcardSyncV2.Instance,
            (byte)OpenNestCoop.Net.MsgType.V2PunchcardSlot);
        // M7：MapMarkerSyncV2（地图标记/画线，226，事件驱动 + OnLateJoin 快照）。
        CoopSyncRegistry.RegisterModule(MapMarkerSyncV2.Instance);
        // M7：MissionSyncV2（任务 scene/phase/seed，227，Host 权威 + OnLateJoin 快照）。
        CoopSyncRegistry.RegisterModule(MissionSyncV2.Instance);
        // M7：TeleprinterSyncV2（打字机打印/状态/清除，228，Host 权威）。
        CoopSyncRegistry.RegisterModule(TeleprinterSyncV2.Instance);
        // M7：GunLinkSyncV2（仰角联动/锁定插销 isLinked，229，谁变化谁广播+主机中继）。
        CoopSyncRegistry.RegisterModule(GunLinkSyncV2.Instance);
        // M7：环境/补给值绑定 → ValueLayer（Host 权威，客机只接收）。
        M3EnvSyncV2.Register();      // 引擎/压力/反炮兵倒计时
        RequisitionSyncV2.Register(); // 征用点/发射药库存
        // M3+ 按里程碑逐个注册（每个都实现 ISyncedModule，走 CoopSyncRegistry 统一路由/Tick）：
        //   RegisterModule(new EventLayer());     // M4  (MsgType=200)
        //   RegisterModule(new ValueLayer());     // M3  (MsgType=201)
        //   RegisterModule(new ButtonLayer());    // M5  (MsgType=202)
        CoopRuntime.LogSource?.LogInfo("[SyncV2] HostDataLayer registered (分层新方案开发中, docs/SYNC_V2_DEV.md)");
    }
}
