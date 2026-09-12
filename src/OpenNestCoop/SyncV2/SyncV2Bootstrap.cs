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

    /// <summary>
    /// ⚠️ 模块自注册（2026-08-30）：V2 模块已在各自文件里用 [ModuleInitializer] 通过
    /// <see cref="CoopSyncRegistry.PendingRegister(bool, Func{ISyncedModule}, string, NetModulePriority?, byte[])"/>
    /// 入队（newScheme=true），Startup 解析 --sync 方案后 FlushPending 统一注册——不再需要集中 RegisterAll。
    /// 本类仅保留 SyncV2 的 MsgType 常量（ValueLayer 等仍引用）。
    /// </summary>
}
