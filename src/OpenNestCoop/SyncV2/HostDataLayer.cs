using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 主机能力抽象：谁是主机 / 谁可写 / 主机 id。
/// 分层核心：所有层（Event/Value/Button）都通过 <see cref="IHostStore"/> 与主机权威数据层交互，
/// 不直接绑定到控件或网络细节。
/// </summary>
public interface IRoleAuthority
{
    /// <summary>当前是否为主机（SessionState.Hosting）。</summary>
    bool IsHost { get; }

    /// <summary>是否处于联机会话（主机或已加入）。</summary>
    bool IsOnline { get; }

    /// <summary>主机 SteamId（主机本地为 0）。</summary>
    ulong HostId { get; }
}

/// <summary>
/// 主机权威数据层接口（其他层统一经此读写共享状态 + 广播）。
/// - 读：主/客通用（<see cref="GetFloat"/> 等）。
/// - 写：<see cref="SetFloat"/> 等为本地权威写，仅主机有效（客机调用被忽略——不能覆盖主机共享状态，约束#2）。
/// - 远端应用：<see cref="ApplyFloat"/> 等由网络接收路径使用（自动防环，约束#3）。
/// - 发送：<see cref="Broadcast"/> 会话广播（操作者权威/主机权威通用：主机→全员，客机→主机中继）与
///   <see cref="SendToHost"/>（客机→主机定向），reliable=true 直发（事件/命令），false 走批量合包（周期状态）。
/// </summary>
public interface IHostStore : IRoleAuthority
{
    float GetFloat(string id);
    bool GetBool(string id);
    int GetInt(string id);

    /// <summary>本地权威写：仅主机有效（客机调用被忽略，防覆盖主机共享状态）。</summary>
    void SetFloat(string id, float v);
    void SetBool(string id, bool v);
    void SetInt(string id, int v);

    /// <summary>网络远端值应用（主机收到客机上行 / 客机收到主机广播都用它；自动防环）。</summary>
    void ApplyFloat(string id, float v);
    void ApplyBool(string id, bool v);
    void ApplyInt(string id, int v);

    /// <summary>会话广播（msgType ≥200）：主机→全员，客机→主机中继（星型拓扑，客机间无直连）。
    /// reliable=true 直发（事件/命令），false 走批量合包（周期状态）。操作者权威/主机权威通用。</summary>
    void Broadcast(byte msgType, Action<NetDataWriter> write, bool reliable);

    /// <summary>客机上行到主机（reliable 语义同 <see cref="Broadcast"/>）。</summary>
    void SendToHost(byte msgType, Action<NetDataWriter> write, bool reliable);
}

/// <summary>
/// HostDataLayer 实现（里程碑 M2）。游戏无关的"主机权威数据层"：
/// - 共享状态存储（id → float/bool/int）：主机写 + 客机读；客机本地写被守卫（约束#2），
///   远端应用走 <see cref="ApplyFloat"/> 等（_applying 防环，约束#3）。
/// - 发送封装：Broadcast / SendToHost（reliable=直发，unreliable=批量合包）。
/// - 中途加入：主机把全量 store 快照单播给新成员（MsgType=V2HostData=203），客机应用对齐基线。
/// 实时值变化同步由 ValueLayer（MsgType=201，M3）负责；本层不做周期值广播，避免与 ValueLayer 重叠。
/// </summary>
public sealed class HostDataLayer : ISyncedModule, IHostStore
{
    /// <summary>单例：分层各层都经 <see cref="Instance"/> 访问主机权威数据层。</summary>
    public static HostDataLayer Instance { get; } = new HostDataLayer();

    private HostDataLayer() { }

    private NetManager _net => CoopRuntime.Net;

    // ---- IRoleAuthority ----
    public bool IsHost => _net != null && _net.State == SessionState.Hosting;
    public bool IsOnline => _net != null && (_net.State == SessionState.Hosting || _net.State == SessionState.Joined);
    public ulong HostId => _net != null ? _net.HostSteamId : 0UL;

    // ---- 共享状态存储（主机权威）----
    private readonly Dictionary<string, float> _floats = new();
    private readonly Dictionary<string, bool> _bools = new();
    private readonly Dictionary<string, int> _ints = new();

    /// <summary>防环：应用远端快照/值期间置位（约束#3），避免 Apply 触发本地写→再广播→再 Apply 死循环。</summary>
    private bool _applying;

    // ---- 读（主/客通用）----
    public float GetFloat(string id) => _floats.TryGetValue(id ?? "", out var v) ? v : 0f;
    public bool GetBool(string id) => _bools.TryGetValue(id ?? "", out var v) ? v : false;
    public int GetInt(string id) => _ints.TryGetValue(id ?? "", out var v) ? v : 0;

    // ---- 本地权威写（仅主机；客机调用被忽略，防覆盖主机共享状态）----
    public void SetFloat(string id, float v)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!IsHost || _applying) return;
        _floats[id] = v;
    }

    public void SetBool(string id, bool v)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!IsHost || _applying) return;
        _bools[id] = v;
    }

    public void SetInt(string id, int v)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!IsHost || _applying) return;
        _ints[id] = v;
    }

    // ---- 网络远端值应用（接收路径专用，自动防环）----
    public void ApplyFloat(string id, float v)
    {
        if (string.IsNullOrEmpty(id)) return;
        _applying = true;
        try { _floats[id] = v; }
        finally { _applying = false; }
    }

    public void ApplyBool(string id, bool v)
    {
        if (string.IsNullOrEmpty(id)) return;
        _applying = true;
        try { _bools[id] = v; }
        finally { _applying = false; }
    }

    public void ApplyInt(string id, int v)
    {
        if (string.IsNullOrEmpty(id)) return;
        _applying = true;
        try { _ints[id] = v; }
        finally { _applying = false; }
    }

    // ---- 发送封装 ----
    public void Broadcast(byte msgType, Action<NetDataWriter> write, bool reliable)
    {
        var net = _net;
        if (net == null || !IsOnline) return;
        var data = Build(msgType, write);
        if (data == null) return;
        if (IsHost)
        {
            // 主机：发给所有非本地成员（含中继各客机；reliable=true 直发事件/命令，false 批量合包周期状态）
            for (int i = 0; i < net.Roster.Count; i++)
            {
                var p = net.Roster[i];
                if (p != null && !p.IsLocal) net.Transport.Send(p.SteamId, data, reliable);
            }
        }
        else
        {
            // 客机：上行到主机（星型拓扑，客机间无直连；主机接收后由对应层中继给其他客机）
            ulong hostId = net.HostSteamId;
            if (hostId == 0) return;
            if (reliable) net.Transport.Send(hostId, data, true);
            else net.EnqueueBatch(data, false);
        }
    }

    public void SendToHost(byte msgType, Action<NetDataWriter> write, bool reliable)
    {
        if (!IsOnline || IsHost) return;
        var net = _net;
        ulong hostId = net.HostSteamId;
        if (hostId == 0) return;
        var data = Build(msgType, write);
        if (data == null) return;
        if (reliable) net.Transport.Send(hostId, data, true);
        else net.EnqueueBatch(data, false);
    }

    private static byte[] Build(byte msgType, Action<NetDataWriter> write)
    {
        try
        {
            var w = NetProtocol.Begin((MsgType)msgType);
            write?.Invoke(w);
            return NetProtocol.Snapshot(w);
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"[HostDataLayer] build msgType={msgType}: {ex.Message}");
            return null;
        }
    }

    // ---- ISyncedModule ----
    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2HostData;

    public void Tick(float dt)
    {
        // M2：不在此做周期值广播（实时值同步归 ValueLayer，M3）。
        // 仅低频诊断：确认主机/客机 store 规模与角色，便于双端验证本层工作。
        _diagTimer += dt;
        if (_diagTimer >= 5f)
        {
            _diagTimer = 0f;
            CoopLog.Info("SyncV2.hostData", () =>
                $"[SyncV2] HostDataLayer host={IsHost} online={IsOnline} floats={_floats.Count} bools={_bools.Count} ints={_ints.Count}", 5f);
        }
    }

    private float _diagTimer;

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            _applying = true;
            try { ApplyFullState(r); }
            finally { _applying = false; }
            CoopLog.Debug("SyncV2.hostDataRecv", () => $"[SyncV2] HostDataLayer recv full-state from={from}");
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"[HostDataLayer] OnPacket: {ex.Message}");
        }
    }

    public void OnSessionStarted()
    {
        // 新会话：清空 store（上一会话的共享状态作废）
        _floats.Clear();
        _bools.Clear();
        _ints.Clear();
    }

    public void OnSessionEnded()
    {
        _floats.Clear();
        _bools.Clear();
        _ints.Clear();
    }

    public void Reset()
    {
        _floats.Clear();
        _bools.Clear();
        _ints.Clear();
    }

    public void OnLateJoin(ulong steamId)
    {
        // 主机：把全量 store 快照单播给新加入/重连的成员（基线对齐）
        if (IsHost && steamId != 0)
        {
            var data = BuildFullState();
            if (data != null) _net?.Transport.Send(steamId, data, true);
        }
    }

    // ---- 全量快照（MsgType=V2HostData）----
    /// <summary>
    /// 格式：`[fCount:ushort][(id:string)(v:float)]*  [bCount:ushort][(id)(v:bool)]*  [iCount:ushort][(id)(v:int)]*`
    /// 用显式索引遍历（IL2CPP foreach 不可靠，保持统一风格）。
    /// </summary>
    private static byte[] BuildFullState()
    {
        var h = Instance;
        try
        {
            var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2HostData);
            var floats = new KeyValuePair<string, float>[h._floats.Count];
            int fi = 0;
            using (var e = h._floats.GetEnumerator())
                while (e.MoveNext()) floats[fi++] = e.Current;
            w.Put((ushort)floats.Length);
            for (int i = 0; i < floats.Length; i++) { w.Put(floats[i].Key); w.Put(floats[i].Value); }

            var bools = new KeyValuePair<string, bool>[h._bools.Count];
            int bi = 0;
            using (var e = h._bools.GetEnumerator())
                while (e.MoveNext()) bools[bi++] = e.Current;
            w.Put((ushort)bools.Length);
            for (int i = 0; i < bools.Length; i++) { w.Put(bools[i].Key); w.Put(bools[i].Value); }

            var ints = new KeyValuePair<string, int>[h._ints.Count];
            int ii = 0;
            using (var e = h._ints.GetEnumerator())
                while (e.MoveNext()) ints[ii++] = e.Current;
            w.Put((ushort)ints.Length);
            for (int i = 0; i < ints.Length; i++) { w.Put(ints[i].Key); w.Put(ints[i].Value); }

            return NetProtocol.Snapshot(w);
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"[HostDataLayer] BuildFullState: {ex.Message}");
            return null;
        }
    }

    private void ApplyFullState(NetDataReader r)
    {
        int fCount = r.GetUShort();
        for (int i = 0; i < fCount && r.AvailableBytes > 0; i++)
            _floats[r.GetString()] = r.GetFloat();

        int bCount = r.GetUShort();
        for (int i = 0; i < bCount && r.AvailableBytes > 0; i++)
            _bools[r.GetString()] = r.GetBool();

        int iCount = r.GetUShort();
        for (int i = 0; i < iCount && r.AvailableBytes > 0; i++)
            _ints[r.GetString()] = r.GetInt();
    }
}
