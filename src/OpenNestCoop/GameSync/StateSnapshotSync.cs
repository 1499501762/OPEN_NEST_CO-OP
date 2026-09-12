using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 中途加入统一快照容器（MsgType=30，方案 B：状态注册表）。
///
/// 各模块通过 <see cref="Register"/> 注册一对"构建快照"与"应用快照"回调。
/// 主机收到新成员（NetManager.OnHello → OnLateJoin）时，遍历所有注册模块，
/// 把每个模块的当前状态打包进一个 StateSnapshot 容器单播给新成员；
/// 新成员收到后按模块名分发回各模块应用 → 初始对齐当前游戏状态。
///
/// 相比逐模块 OnLateJoin（方案 A），本方案新增模块只需 Register 一对回调，
/// 无需改 NetManager 或接口。
/// </summary>
public sealed class StateSnapshotSync : ISyncedModule
{
    public int MsgType => 30;

    // ⚠️ 模块自注册：程序集加载时入队（V1 方案），Startup FlushPending 统一注册
    // ⚠️ 2026-09-12 回退 extraTypes={31}：该改动（此前为修"打字机开局未同步"）让客机的快照重发请求
    // MsgType=31 真正路由生效 → 客机进炮台场景时主动 RequestSnapshot → 主机重发**所有模块**全量快照
    // （含 ReloadSync applyState=1）→ 客机 ReloadSync 被 SetState 强拉到主机当前 st（主机装填状态机进任务
    // 后自动演进，如已在 st=2）→ **客机开局"直接跳第二个灯"**（本地 0→1→2 的正常演进被跳过）。
    // 且打字机已改为 EvPrint/EvState 驱动（不再依赖快照重发），extraTypes={31} 已无必要。
    // 中途加入的装填对齐仍由主机 OnLateJoin → ReloadSync.SendFullStateTo（pending 重试）负责。
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SelfRegister() => CoopSyncRegistry.PendingRegister(false, () => new StateSnapshotSync());

    private sealed class Provider
    {
        public string Name;
        public Func<byte[]> Build;
        public Action<byte[]> Apply;
    }

    private static readonly List<Provider> _providers = new();

    /// <summary>注册快照模块（构建 + 应用）。重复注册同名忽略。</summary>
    public static void Register(string name, Func<byte[]> build, Action<byte[]> apply)
    {
        if (string.IsNullOrEmpty(name) || build == null || apply == null) return;
        foreach (var p in _providers)
            if (p.Name == name) return;
        _providers.Add(new Provider { Name = name, Build = build, Apply = apply });
    }

    /// <summary>客机：任务场景加载完成后，请求主机补发一次全量快照（把任务内静止状态对齐）。</summary>
    public static void RequestSnapshot()
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return;
        try
        {
            var w = NetProtocol.Begin((MsgType)31);
            net.Transport.Send(net.HostSteamId, NetProtocol.Snapshot(w), true);
            CoopRuntime.LogSource?.LogInfo("[StateSnapshot] requested snapshot resend -> host");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"StateSnapshotSync RequestSnapshot: {ex.Message}"); }
    }

    /// <summary>主机：新成员加入，收集所有模块当前状态 → 打包成 StateSnapshot 单播。</summary>
    public void OnLateJoin(ulong steamId)
    {
        var net = CoopRuntime.Net;
        if (net == null || !net.IsHost || steamId == 0) return;
        try
        {
            var w = NetProtocol.Begin((MsgType)MsgType);
            // 只打包有内容的模块（build 返回非空）
            var payloads = new List<(string name, byte[] data)>();
            foreach (var p in _providers)
            {
                try
                {
                    var d = p.Build();
                    if (d != null && d.Length > 0) payloads.Add((p.Name, d));
                }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"StateSnapshot {p.Name} build: {ex.Message}"); }
            }
            w.Put((byte)payloads.Count);
            foreach (var (name, data) in payloads)
            {
                w.Put(name ?? "");
                w.PutBytesWithLength(data); // 自带 ushort 长度前缀
            }
            // ⚠️ 2026-08-26：直发大包自动分片（Steam P2P 单包硬性限制）——中途加入快照（entity 35 实体等）
            // 超过 900B 会整包被拒收/丢 → 客机缺实体/没类型。分片后接收端重组。
            net.SendDirectFragmented(steamId, NetProtocol.Snapshot(w), true);
            CoopRuntime.LogSource?.LogInfo($"[StateSnapshot] → {steamId} modules={payloads.Count}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"StateSnapshotSync OnLateJoin: {ex.Message}"); }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            byte msgType = r.GetByte();
            if (msgType == 31)
            {
                // 客机请求补发快照（任务场景加载完成后）→ 主机重发容器快照 + 装填状态
                if (net.IsHost)
                {
                    CoopRuntime.LogSource?.LogInfo($"[StateSnapshot] received resend request -> {from}");
                    OnLateJoin(from);
                    // 装填快照单独发（SetState 安全对齐按钮）：此时新成员已进场景，能正确应用。
                    // 若场景仍未就绪，SendFullStateTo 内部入 pending，Tick 里重试直到成功。
                    try { ReloadSync.SendFullStateTo(from); }
                    catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[StateSnapshot] ReloadSync resend: {ex.Message}"); }
                }
                return;
            }
            int n = r.GetByte();
            for (int i = 0; i < n; i++)
            {
                string name = r.GetString();
                var sub = r.GetBytesWithLength();
                bool applied = false;
                foreach (var p in _providers)
                {
                    if (p.Name != name) continue;
                    try { p.Apply(sub); applied = true; }
                    catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"StateSnapshot {name} apply: {ex.Message}"); }
                    break;
                }
                if (!applied)
                    CoopRuntime.LogSource?.LogWarning($"[StateSnapshot] unknown module '{name}' ignored");
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"StateSnapshotSync OnPacket: {ex.Message}"); }
    }

    public void Tick(float dt) { }
    public void OnSessionStarted() { }
    public void OnSessionEnded() { }
    public void Reset() { }
}
