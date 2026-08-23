using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 舱门/楼梯盖板同步（HatchSyncV2，MsgType=221）。M7：把 V1 <c>HatchSync</c>（117）迁入分层架构。
/// 双向：谁变化谁广播（IsOpen bool，1s 变化检测），对端 SetBool 应用（fallback Animator.SetBool("IsOpen")）；
/// 主机中继。OnLateJoin 主机单播全量（替代 V1 StateSnapshotSync "hatch"）。
/// 按钮驱动的舱门/盖板（含 LookAtTarget）交给 ButtonLayer 复现完整点击，本层跳过（避免 SetBool 钉住打开）。
/// </summary>
public sealed class HatchSyncV2 : ISyncedModule
{
    public static HatchSyncV2 Instance { get; } = new HatchSyncV2();

    private HatchSyncV2() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2Hatch;

    private const float Interval = 1.0f;
    private float _timer;
    private readonly Dictionary<AnimatorBoolToggler, bool> _known = new();

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        try
        {
            var tog = UnityEngine.Object.FindObjectsOfType<AnimatorBoolToggler>();
            if (tog == null || tog.Length == 0) return;
            foreach (var t in tog)
            {
                if (t == null || !IsHatch(t)) continue;
                bool val = ReadOpen(t);
                if (_known.TryGetValue(t, out var last) && last == val) continue;
                _known[t] = val;
                SendState(t, val);
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[HatchSyncV2] Tick: {ex.Message}"); }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte();
            string id = r.GetString();
            bool val = r.GetByte() == 1;
            if (Store.IsHost) _net?.EnqueueBatch(data, true);
            var tog = UnityEngine.Object.FindObjectsOfType<AnimatorBoolToggler>();
            if (tog != null)
                foreach (var t in tog)
                {
                    if (t == null || PathOf(t.transform) != id) continue;
                    try { t.SetBool(val); }
                    catch { try { t.animator.SetBool("IsOpen", val); } catch { } }
                    // 防环：应用后更新本地已知，避免下轮 Tick 把刚应用的状态当“本地变化”回广播
                    _known[t] = val;
                    break;
                }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[HatchSyncV2] OnPacket: {ex.Message}"); }
    }

    public void OnLateJoin(ulong steamId)
    {
        if (Store.IsHost && steamId != 0)
        {
            var tog = UnityEngine.Object.FindObjectsOfType<AnimatorBoolToggler>();
            if (tog == null || tog.Length == 0) return;
            var net = _net;
            if (net == null) return;
            try
            {
                // 与 SendState 一致的【单条格式 [id][val]】逐条发送（避免打包格式与 OnPacket 解析不一致）。
                foreach (var t in tog)
                {
                    if (t == null || !IsHatch(t)) continue;
                    var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2Hatch);
                    w.Put(PathOf(t.transform));
                    w.Put(ReadOpen(t) ? (byte)1 : (byte)0);
                    net.Transport.Send(steamId, NetProtocol.Snapshot(w), true);
                }
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[HatchSyncV2] OnLateJoin: {ex.Message}"); }
        }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _timer = 0f; _known.Clear(); }

    private void SendState(AnimatorBoolToggler t, bool val)
    {
        string id = PathOf(t.transform);
        Store.Broadcast((byte)OpenNestCoop.Net.MsgType.V2Hatch, w =>
        {
            w.Put(id);
            w.Put(val ? (byte)1 : (byte)0);
        }, reliable: false);
        CoopRuntime.LogSource?.LogInfo($"[HatchSyncV2] send '{id}' open={val} host={Store.IsHost}");
    }

    private static bool IsHatch(AnimatorBoolToggler t)
    {
        try
        {
            if (!string.Equals(t.parameterName, "IsOpen", StringComparison.Ordinal)) return false;
            // 按钮驱动的舱门/楼梯盖板：交给 ButtonLayer 复现完整点击（含手柄回弹动画），本层跳过
            if (t.transform != null)
            {
                try { if (t.transform.GetComponentInParent<LookAtTarget>() != null) return false; } catch { }
                try { if (t.transform.GetComponentInChildren<LookAtTarget>(true) != null) return false; } catch { }
            }
            var anim = t.animator;
            if (anim == null) return false;
            var ctrl = anim.runtimeAnimatorController;
            if (ctrl != null && ctrl.name.IndexOf("Hatch", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var tr = t.transform;
            while (tr != null)
            {
                string nm = tr.name ?? "";
                if (nm.IndexOf("Hatch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    nm.IndexOf("Stair", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    nm.IndexOf("Ladder", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                tr = tr.parent;
            }
        }
        catch { }
        return false;
    }

    private static bool ReadOpen(AnimatorBoolToggler t)
    {
        try { return t.GetBool(); }
        catch { try { return t.animator.GetBool("IsOpen"); } catch { return false; } }
    }

    private static string PathOf(Transform t)
    {
        if (t == null) return "";
        string path = t.name ?? "";
        var p = t.parent;
        while (p != null) { path = (p.name ?? "") + "/" + path; p = p.parent; }
        return path;
    }
}
