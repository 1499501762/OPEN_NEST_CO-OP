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
    public byte MsgType => 30;

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
            net.Transport.Send(steamId, NetProtocol.Snapshot(w), true);
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
