using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 炮弹落点同步（MsgType=13，已有定义但此前无实现——两端各自 EvaluateImpact 算落点，位置不同步）。
/// 根因：铁巢（任务内）两端不同 → 落点本地坐标两端不同（ImpactDiag 曾测 2.25 vs 1.19）。
/// 方案：主机 EvaluateImpact 时广播落点 root 空间坐标（EvaluateImpact 参数），
/// 客户端收到后在 ImpactLocation 报告时把落点标记摆到主机坐标（PositionInRootSpace，绕开铁巢基准差异）。
/// </summary>
public sealed class ImpactSync : ISyncedModule
{
    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.Impact; // 13
    public static ImpactSync Instance;

    private Vector2 _pendingLoc;
    private bool _havePending;

    public ImpactSync() { Instance = this; }

    /// <summary>主机：PostEvalReport 后广播落点标记的本地位置（相对其父，两端父对象一致 → 本地坐标通用）。
    /// ⚠️ 2026-08-25：EvaluateImpact 的 loc 坐标系不确定（PositionInRootSpace 摆错）→ 改广播标记自身 localPosition。</summary>
    public void BroadcastMarkPos(Vector2 lp)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.State != SessionState.Hosting || !net.IsHost) return;
        try
        {
            CoopLog.Info("impact.sync0", () => $"[ImpactSync] host broadcast markPos=({lp.x:0.00},{lp.y:0.00})", 0.3f);
            var w = NetProtocol.Begin((OpenNestCoop.Net.MsgType)MsgType);
            w.Put(lp.x);
            w.Put(lp.y);
            var data = NetProtocol.Snapshot(w);
            net.EnqueueBatch(data, true, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ImpactSync BroadcastMarkPos: {ex.Message}"); }
    }

    /// <summary>客户端：把落点标记本地位置设为主机值（两端父对象一致 → 本地坐标通用）。</summary>
    public void ApplyTo(ImpactLocation il)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost || !_havePending || il == null || il.transform == null) return;
        try
        {
            var lp = il.transform.localPosition;
            lp.x = _pendingLoc.x; lp.y = _pendingLoc.y;
            il.transform.localPosition = lp;
            CoopLog.Info("impact.sync3", () => $"[ImpactSync] apply to '{il.gameObject?.name}' localPos=({lp.x:0.00},{lp.y:0.00})", 0.3f);
        }
        catch { }
    }

    public void Tick(float dt) { /* 事件驱动 */ }

    public void OnPacket(ulong from, byte[] data)
    {
        CoopLog.Info("impact.sync1", () => $"[ImpactSync] OnPacket called from={from} len={(data == null ? -1 : data.Length)}", 0.2f);
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return; // 仅客户端处理
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            _pendingLoc = new Vector2(r.GetFloat(), r.GetFloat());
            _havePending = true;
            CoopLog.Info("impact.sync2", () => $"[ImpactSync] client recv loc=({_pendingLoc.x:0.00},{_pendingLoc.y:0.00})", 0.3f);
            // ⚠️ 时序补偿：主机坐标可能晚到（客机已先自己算落点标记）→ 收到后重摆场景里所有落点标记校正
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<ImpactLocation>(true);
                if (all != null)
                    foreach (var il in all)
                        if (il != null && il.transform != null)
                            ApplyTo(il);
            }
            catch { }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ImpactSync OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }

    public void Reset()
    {
        _havePending = false;
        _pendingLoc = Vector2.zero;
    }
}
