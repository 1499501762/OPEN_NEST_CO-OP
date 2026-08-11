using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 通用"值状态"同步框架（统一曲柄/旋钮/滑块、唱片机、装填、咖啡机、引擎/压力/灯光等设备状态）。
/// - 每个可同步"值"注册一个 Binding（float/int/bool），提供：本地读 + 远端写 + 死区 + 插值 + 忙态。
/// - 统一 Tick：主机周期变化检测广播 ControlState；客户端本地变化上行 ControlCmd；应用端防环 + 插值。
/// - 消息复用 ControlState/ControlCmd（kind: 0=float, 1=int, 2=bool；值统一用 float 承载）。
/// 本项目的通用轮子，替代为每个设备逐个手写同步逻辑。
/// 事件/对象类同步（玩家位置、地图标记）不走此框架。
/// </summary>
public static class ValueSync
{
    private const float Interval = 0.2f;        // 低频默认：大多数控件（变化检测 + 心跳）——该省的地方省
    private const float HighFreqInterval = 0.033f; // 30Hz：仅炮塔 Lever/Gear（HighFreq 绑定）专用，其余不占高频带宽
    private const float InterpRate = 10f;
    private const float HeartbeatInterval = 2f; // 周期全量广播（初始对齐/状态自愈，解决“开局不同步”）
    private static float _timer;
    private static float _hfTimer;
    private static float _logTimer;
    private static float _heartbeatTimer;
    private static int _stateLog;
    private static int _cmdLog;
    private static int _stateSendLog;
    private static int _skipLog;
    private static int _cmdSendLog;

    public sealed class Binding
    {
        public string Id;      // 跨端唯一标识（控件用场景路径）
        public byte Kind;      // 0=float 1=int 2=bool
        public float Deadzone; // 变化检测死区
        public bool Interpolate;
        public Func<float> GetF; public Action<float> SetF;
        public Func<int> GetI; public Action<int> SetI;
        public Func<bool> GetB; public Action<bool> SetB;
        public Func<bool> IsBusy; // 本地操作中（跳过远端覆盖）
        public bool ClientNoApply; // 客户端收到 state 时不应用（用于被其他系统接管的控件，如炮塔旋转曲柄→TurretSync）
        public bool ClientNoSend; // 客户端不上行（主机权威全局状态，如引擎/压力/反炮兵——避免客机开局读到 false 上行误关主机）
        public bool NoHeartbeat; // 心跳（周期全量广播）时跳过——避免无操作时对端反复应用触发声音/动画（如弹道计算机装药摇杆）
        public bool HighFreq; // 高频同步（30Hz）：仅炮塔 Lever/Gear（方向角/仰角）专用；其余保持低频 + 插值（省带宽）
        internal bool HSet; internal float HF; internal int HI; internal bool HB;
        internal bool LSet; internal float LF; internal int LI; internal bool LB;
        internal bool HasTarget; internal float TargetF;
        internal bool Applying;
        internal bool PrevDragging; // 上次拖拽状态（释放时触发 settle）
    }

    private static readonly List<Binding> _bindings = new();

    /// <summary>清空所有绑定（场景切换/重建时调用）。</summary>
    public static void Clear() { _bindings.Clear(); }

    /// <summary>是否存在指定 id 的绑定。</summary>
    public static bool Has(string id)
    {
        foreach (var b in _bindings) if (b.Id == id) return true;
        return false;
    }

    /// <summary>移除指定 id 的绑定（场景切换/控件销毁时清理）。</summary>
    public static void Remove(string id)
    {
        for (int i = _bindings.Count - 1; i >= 0; i--)
            if (_bindings[i].Id == id) { _bindings.RemoveAt(i); return; }
    }

    public static Binding AddFloat(string id, Func<float> get, Action<float> set,
        float deadzone = 0.001f, bool interp = false, Func<bool> busy = null)
    {
        var b = new Binding { Id = id, Kind = 0, Deadzone = deadzone, Interpolate = interp, GetF = get, SetF = set, IsBusy = busy };
        _bindings.Add(b);
        return b;
    }

    public static Binding AddInt(string id, Func<int> get, Action<int> set,
        float deadzone = 1f, bool interp = false, Func<bool> busy = null)
    {
        var b = new Binding { Id = id, Kind = 1, Deadzone = deadzone, Interpolate = interp, GetI = get, SetI = set, IsBusy = busy };
        _bindings.Add(b);
        return b;
    }

    public static Binding AddBool(string id, Func<bool> get, Action<bool> set, Func<bool> busy = null)
    {
        var b = new Binding { Id = id, Kind = 2, Deadzone = 0f, Interpolate = false, GetB = get, SetB = set, IsBusy = busy };
        _bindings.Add(b);
        return b;
    }

    public static void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;

        _logTimer += dt;
        if (_logTimer >= 5f)
        {
            _logTimer = 0f;
            CoopRuntime.LogSource?.LogInfo($"[ValueSync] bindings={_bindings.Count} host={net.IsHost} state={net.State}");
        }

        // 每帧：客户端插值逼近远端目标
        ApplyInterpolated(dt);
        // 每帧：拖拽状态变化检测（释放瞬间 settle 精确发送，解决曲柄/拉杆“结束值不同步”）
        TickDraggingChange(net);

        bool online = net.State == SessionState.Hosting || net.State == SessionState.Joined;
        if (!online) return;

        // 高频 tick（30Hz）：只处理炮塔 Lever/Gear（HighFreq 绑定）——方向角/仰角专用，其余控件不占高频带宽
        _hfTimer += dt;
        if (_hfTimer >= HighFreqInterval)
        {
            _hfTimer = 0f;
            if (net.IsHost) HostTick(net, false, highFreqOnly: true);
            else ClientTick(net, highFreqOnly: true);
        }

        // 低频 tick（0.2s）：处理其余控件 + 心跳（心跳含高频，保证新加入/重连客户端初始对齐）
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;

        _heartbeatTimer += Interval;
        bool heartbeat = _heartbeatTimer >= HeartbeatInterval;
        if (heartbeat) _heartbeatTimer = 0f;

        if (net.IsHost) HostTick(net, heartbeat, highFreqOnly: false);
        else ClientTick(net, highFreqOnly: false);
    }

    /// <summary>客户端 -> 主机：本地值变化上行（主机应用后广播）。</summary>
    public static void OnCmd(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null || !net.IsHost) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int kind = r.GetByte();
            string id = r.GetString();
            float v = r.GetFloat();
            byte dragging = r.GetByte();
            var b = Find(id);
            if (b != null)
            {
                bool localBusy = b.IsBusy != null && b.IsBusy();
                if (localBusy && (++_skipLog % 20) == 1)
                    CoopRuntime.LogSource?.LogInfo($"[ValueSync] cmd skip localBusy id='{id}' v={v:0.###} drag={dragging}");
                // 拖拽中（远端或本端）不覆盖；释放/静态时精确应用
                if (!localBusy && dragging == 0)
                {
                    b.Applying = true;
                    try { SetValue(b, v); }
                    finally { b.Applying = false; }
                    MarkHost(b);
                }
            }
            if (net.IsHost)
                net.EnqueueBatch(data, true);
            if ((++_cmdLog % 20) == 0)
                CoopRuntime.LogSource?.LogInfo($"[ValueSync] cmd recv kind={kind} id='{id}' v={v:0.###} drag={dragging}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ValueSync OnCmd: {ex.Message}"); }
    }

    /// <summary>主机 -> 客户端：应用值状态（float+插值走缓冲，其余直接应用）。</summary>
    public static void OnState(byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int kind = r.GetByte();
            string id = r.GetString();
            float v = r.GetFloat();
            byte dragging = r.GetByte();
            var b = Find(id);
            if (b == null) return;
            if (b.IsBusy != null && b.IsBusy()) return; // 本端拖拽中，忽略远端
            if (b.ClientNoApply)
            {
                // 该控件由另一系统接管（如炮塔旋转曲柄→TurretSync）：客户端不设曲柄值，
                // 避免客户端游戏用曲柄值驱动炮塔覆盖 TurretSync 的主机炮塔快照。
                b.HasTarget = false;
                return;
            }
            if (dragging == 1)
            {
                // 远端拖拽中：插值平滑追（不精确覆盖，避免跳变）
                if (b.Kind == 0 && b.Interpolate)
                {
                    b.TargetF = v;
                    b.HasTarget = true;
                }
                else
                {
                    b.Applying = true;
                    try { SetValue(b, v); }
                    finally { b.Applying = false; }
                }
            }
            else
            {
                // 远端释放/静态：精确 settle
                b.Applying = true;
                try { SetValue(b, v); }
                finally { b.Applying = false; }
                b.HasTarget = false;
            }
            MarkLocal(b, v);
            if ((++_stateLog % 20) == 0)
                CoopRuntime.LogSource?.LogInfo($"[ValueSync] state recv kind={kind} id='{id}' v={v:0.###} drag={dragging}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ValueSync OnState: {ex.Message}"); }
    }

    // ---------------- 内部 ----------------

    /// <summary>每帧检测拖拽状态变化：释放瞬间立即 settle 发送当前值（不等 0.2s 周期）。</summary>
    private static void TickDraggingChange(NetManager net)
    {
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        foreach (var b in _bindings)
        {
            bool dragging = b.IsBusy != null && b.IsBusy();
            if (dragging == b.PrevDragging) continue;
            b.PrevDragging = dragging;
            if (dragging) continue; // 刚进入拖拽：不立即发（等值变化/释放 settle）
            // 刚释放：settle 精确发送当前值
            float cur = GetValue(b);
            if (net.IsHost) SendState(net, b, cur);
            else SendCmd(net, b, cur);
        }
    }

    private static void HostTick(NetManager net, bool heartbeat, bool highFreqOnly)
    {
        foreach (var b in _bindings)
        {
            // 高频 tick 只处理高频绑定；低频 tick 只处理非高频；心跳时全量（含高频，初始对齐）
            if (!heartbeat && b.HighFreq != highFreqOnly) continue;
            float cur = GetValue(b);
            float last = b.HSet ? HostLast(b) : float.NaN;
            bool edgeCur = IsEdgeValue(cur);
            bool edgeLast = b.HSet && IsEdgeValue(last);
            // 只在 值变化 / edge 状态变化 / 周期心跳 时发送（NoHeartbeat 绑定心跳时跳过）。
            // 心跳每 2s 一次（不刷屏），保证新加入/重连客户端拿到完整状态（初始对齐）。
            bool changed = (!heartbeat || !b.NoHeartbeat) && (!b.HSet || Delta(cur, last) >= b.Deadzone || edgeCur != edgeLast);
            if (!changed) continue;
            MarkHost(b);
            SendState(net, b, cur);
        }
    }

    private static void ClientTick(NetManager net, bool highFreqOnly)
    {
        foreach (var b in _bindings)
        {
            if (b.HighFreq != highFreqOnly) continue; // 高频/低频分开处理
            if (b.ClientNoSend) continue; // 主机权威状态：客户端只接收，不上行（防误改主机共享状态）
            if (b.Applying) continue; // 正在应用远端，不检测本地
            float cur = GetValue(b);
            float last = b.LSet ? LocalLast(b) : float.NaN;
            bool edgeCur = IsEdgeValue(cur);
            bool edgeLast = b.LSet && IsEdgeValue(last);
            bool changed = !b.LSet || Delta(cur, last) >= b.Deadzone || edgeCur != edgeLast;
            if (!changed) continue;
            MarkLocal(b, cur);
            SendCmd(net, b, cur);        }
    }

    /// <summary>值接近 0 或 1（归一化刻度盘/滑块两端），边界处必须精确同步。</summary>
    private static bool IsEdgeValue(float v) => v <= 0.0001f || v >= 0.9999f;

    private static void ApplyInterpolated(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return;
        float t = 1f - Mathf.Exp(-InterpRate * dt);
        foreach (var b in _bindings)
        {
            if (!b.HasTarget) continue;
            if (b.IsBusy != null && b.IsBusy()) { b.HasTarget = false; continue; } // 本地操作中，放弃远端目标
            float cur = GetValue(b);
            float next = Mathf.Lerp(cur, b.TargetF, t);
            if (Mathf.Abs(next - cur) < 0.0005f) { next = b.TargetF; b.HasTarget = false; }
            b.Applying = true;
            try { SetValue(b, next); }
            finally { b.Applying = false; }
            MarkLocal(b, next);
        }
    }

    private static void SendState(NetManager net, Binding b, float v)
    {
        var w = NetProtocol.Begin(MsgType.ControlState);
        w.Put(b.Kind);
        w.Put(b.Id ?? "");
        w.Put(v);
        w.Put(b.IsBusy != null && b.IsBusy() ? (byte)1 : (byte)0); // 拖拽状态
        var data = NetProtocol.Snapshot(w);
        net.EnqueueBatch(data, true);
        if ((++_stateSendLog % 40) == 1)
            CoopRuntime.LogSource?.LogInfo($"[ValueSync] state send kind={b.Kind} id='{b.Id}' v={v:0.###} drag={(b.IsBusy != null && b.IsBusy() ? 1 : 0)}");
    }

    private static void SendCmd(NetManager net, Binding b, float v)
    {
        var w = NetProtocol.Begin(MsgType.ControlCmd);
        w.Put(b.Kind);
        w.Put(b.Id ?? "");
        w.Put(v);
        w.Put(b.IsBusy != null && b.IsBusy() ? (byte)1 : (byte)0); // 拖拽状态
        net.EnqueueBatch(NetProtocol.Snapshot(w), false);
        // 低频诊断：确认客机上行哪些控件（尤其曲柄 Spur Gear / 仰角 Lever）
        if ((++_cmdSendLog % 40) == 1 && b.Id != null && b.Id.IndexOf("Spur Gear", System.StringComparison.OrdinalIgnoreCase) >= 0)
            CoopRuntime.LogSource?.LogInfo($"[ValueSync] cmd send '{b.Id}' v={v:0.###}");
    }

    private static Binding Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var b in _bindings)
            if (b.Id == id) return b;
        return null;
    }

    private static float GetValue(Binding b)
    {
        try
        {
            return b.Kind switch
            {
                0 => b.GetF(),
                1 => b.GetI(),
                _ => b.GetB() ? 1f : 0f
            };
        }
        catch { return 0f; }
    }

    private static void SetValue(Binding b, float v)
    {
        try
        {
            if (b.Kind == 0) b.SetF(v);
            else if (b.Kind == 1) b.SetI((int)Mathf.Round(v));
            else b.SetB(v >= 0.5f);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ValueSync SetValue: {ex.Message}"); }
    }

    private static float HostLast(Binding b) => b.Kind switch { 0 => b.HF, 1 => b.HI, _ => b.HB ? 1f : 0f };
    private static float LocalLast(Binding b) => b.Kind switch { 0 => b.LF, 1 => b.LI, _ => b.LB ? 1f : 0f };

    private static void MarkHost(Binding b)
    {
        b.HSet = true;
        float v = GetValue(b);
        if (b.Kind == 0) b.HF = v;
        else if (b.Kind == 1) b.HI = (int)Mathf.Round(v);
        else b.HB = v >= 0.5f;
    }

    private static void MarkLocal(Binding b, float v)
    {
        b.LSet = true;
        if (b.Kind == 0) b.LF = v;
        else if (b.Kind == 1) b.LI = (int)Mathf.Round(v);
        else b.LB = v >= 0.5f;
    }

    private static float Delta(float a, float b) => Mathf.Abs(a - b);
}
