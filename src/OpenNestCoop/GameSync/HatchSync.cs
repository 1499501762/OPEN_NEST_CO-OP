using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 舱门/楼梯盖板同步（AnimatorBoolToggler，MsgType=117）。
/// 筛选 parameterName=="IsOpen" 且动画控制器名/路径含
/// Hatch/Stair/Ladder 的 toggler，同步开/关布尔；应用时 SetBool（fallback Animator.SetBool("IsOpen")）。
/// 双向：host 广播 / client 上行（谁操作谁发，对端应用）。
/// </summary>
public sealed class HatchSync : ISyncedModule
{
    public byte MsgType => 117;
    private const float Interval = 1.0f; // 1.0s（原 0.4s，降低 FindObjectsOfType<AnimatorBoolToggler> 高频扫描 CPU）
    private float _timer;
    private int _sendLog;
    private readonly Dictionary<AnimatorBoolToggler, bool> _known = new();

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        try
        {
            var tog = UnityEngine.Object.FindObjectsOfType<AnimatorBoolToggler>();
            if (tog == null || tog.Length == 0) return;
            int n = 0;
            foreach (var t in tog)
            {
                if (t == null || !IsHatch(t)) continue;
                n++;
                bool val = ReadOpen(t);
                if (_known.TryGetValue(t, out var last) && last == val) continue;
                _known[t] = val;
                SendState(t, val, net);
            }
            if ((++_sendLog % 25) == 1)
                CoopRuntime.LogSource?.LogInfo($"[HatchSync] scan togglers={tog.Length} hatches={n}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"HatchSync Tick: {ex.Message}"); }
    }

    private static bool IsHatch(AnimatorBoolToggler t)
    {
        try
        {
            if (!string.Equals(t.parameterName, "IsOpen", StringComparison.Ordinal)) return false;
            // 按钮驱动的舱门/楼梯盖板（如 Floor Hatch Barbet Stars：盖板根对象上有 IsOpen toggler，
            // 子对象 Universal Button 是按钮，含 LookAtTarget + 多个 AnimatorBoolToggler）：
            // 点击驱动**多个** toggler + 事件链，由 ButtonClickSync（118）复现完整点击（含手柄回弹动画）；
            // 此处跳过，避免 HatchSync 只 SetBool 单个 IsOpen 把 bool 钉在"打开" → 手柄/盖板卡在打开动作。
            // 关键：按钮可能是 hatch 的**子对象**（Universal Button 在盖板下方），故用
            // GetComponentInChildren（向下）**和** GetComponentInParent（向上）双重检查——
            // 只要 toggler 的 animator 所在对象（或其子级/父级）有任何按钮驱动，就交给 ButtonClickSync。
            if (t.transform != null)
            {
                try
                {
                    var latUp = t.transform.GetComponentInParent<LookAtTarget>();
                    if (latUp != null) return false;
                }
                catch { }
                try
                {
                    var latDown = t.transform.GetComponentInChildren<LookAtTarget>(true);
                    if (latDown != null) return false;
                }
                catch { }
            }
            var anim = t.animator;
            if (anim == null) return false;
            var ctrl = anim.runtimeAnimatorController;
            if (ctrl != null && ctrl.name.IndexOf("Hatch", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // 路径兜底：transform 名/父级含 Hatch/Stair/Ladder（且非按钮驱动——上面已排除）
            var tr = t.transform;
            while (tr != null)
            {
                string nm = tr.name ?? "";
                if (nm.IndexOf("Hatch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    nm.IndexOf("Stair", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    nm.IndexOf("Ladder", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
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

    private static void SendState(AnimatorBoolToggler t, bool val, NetManager net)
    {
        string id = PathOf(t.transform);
        var w = NetProtocol.Begin((MsgType)117);
        w.Put(id);
        w.Put(val ? (byte)1 : (byte)0);
        var data = NetProtocol.Snapshot(w);
        if (net.IsHost) net.EnqueueBatch(data, true);
        else net.EnqueueBatch(data, false);
        CoopRuntime.LogSource?.LogInfo($"[HatchSync] send '{id}' open={val} host={net.IsHost}");
    }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte();
            string id = r.GetString();
            bool val = r.GetByte() == 1;
            if (net.IsHost) net.EnqueueBatch(data, true);
            var tog = UnityEngine.Object.FindObjectsOfType<AnimatorBoolToggler>();
            if (tog != null)
                foreach (var t in tog)
                {
                    if (t == null || PathOf(t.transform) != id) continue;
                    try { t.SetBool(val); }
                    catch { try { t.animator.SetBool("IsOpen", val); } catch { } }
                    break;
                }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"HatchSync OnPacket: {ex.Message}"); }
    }

    private static string PathOf(Transform t)
    {
        if (t == null) return "";
        string path = t.name ?? "";
        var p = t.parent;
        while (p != null) { path = (p.name ?? "") + "/" + path; p = p.parent; }
        return path;
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }

    /// <summary>中途加入：主机构建当前所有舱门/盖板状态快照（供 StateSnapshotSync 打包）。</summary>
    public static byte[] BuildHatchSnapshot()
    {
        try
        {
            var tog = UnityEngine.Object.FindObjectsOfType<AnimatorBoolToggler>();
            if (tog == null || tog.Length == 0) return null;
            var w = NetProtocol.Begin((MsgType)117);
            int n = 0;
            // 先数（两遍：先统计再写入，简单可靠）
            foreach (var t in tog) if (t != null && IsHatch(t)) n++;
            w.Put((byte)Math.Min(n, 255));
            int written = 0;
            foreach (var t in tog)
            {
                if (t == null || !IsHatch(t) || written >= 255) continue;
                written++;
                bool val = ReadOpen(t);
                w.Put(PathOf(t.transform));
                w.Put(val ? (byte)1 : (byte)0);
            }
            return NetProtocol.Snapshot(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"HatchSync BuildHatchSnapshot: {ex.Message}"); }
        return null;
    }

    /// <summary>中途加入：新成员应用舱门/盖板状态快照（逐条 SetBool）。</summary>
    public static void ApplyHatchSnapshot(byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int n = r.GetByte();
            for (int i = 0; i < n; i++)
            {
                string id = r.GetString();
                bool val = r.GetByte() == 1;
                var tog = UnityEngine.Object.FindObjectsOfType<AnimatorBoolToggler>();
                if (tog != null)
                    foreach (var t in tog)
                    {
                        if (t == null || PathOf(t.transform) != id) continue;
                        try { t.SetBool(val); }
                        catch { try { t.animator.SetBool("IsOpen", val); } catch { } }
                        break;
                    }
            }
            CoopRuntime.LogSource?.LogInfo($"[HatchSync] apply snapshot n={n}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"HatchSync ApplyHatchSnapshot: {ex.Message}"); }
    }

    public void Reset() { _timer = 0f; _known.Clear(); }
}
