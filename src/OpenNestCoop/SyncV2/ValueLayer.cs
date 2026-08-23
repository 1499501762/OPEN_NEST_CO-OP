using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 值/事件的权威模型（核心设计问题：哪些事客机可执行、哪些必须走主机）。
/// <para><see cref="Operator"/>（默认）：谁操作谁权威——操作者本地立即执行 + 广播给全端（主机中继），
/// 心跳周期全量广播兜底对齐。适用于绝大多数交互控件（dial/slider/lever/曲柄/按钮）与交互事件：
/// 操作者本地读取/点击就已生效，无需主机裁决内容，主机只做中继。</para>
/// <para><see cref="Host"/>：主机权威——全局共享状态（任务状态、引擎 running、征信点库存/购买、弹种、
/// 猫 AI 决策等）。只有主机广播，客机只接收应用（不上行；防客机开局读 0 上行覆盖主机，如引擎 running）。</para>
/// </summary>
public enum V2Authority
{
    /// <summary>谁操作谁权威（默认）。</summary>
    Operator,
    /// <summary>主机权威（客机只接收应用，不上行）。</summary>
    Host
}

/// <summary>数值层绑定：一个可同步的"值"（float/int/bool）。get/set 对接控件；网络经 HostDataLayer 收发。</summary>
public sealed class ValueBinding
{
    public string Id;             // 跨端唯一标识（控件用场景路径，如 __turret/rotation）
    public byte Kind;             // 0=float 1=int 2=bool
    public V2Authority Authority; // Operator（默认） / Host
    public float Deadzone;        // 变化检测死区
    public bool Interpolate;      // 远端拖拽中插值平滑追（否=直接设值）
    public Func<float> GetF; public Action<float> SetF;
    public Func<int> GetI; public Action<int> SetI;
    public Func<bool> GetB; public Action<bool> SetB;
    public Func<bool> IsBusy;     // 本地操作中（跳过远端覆盖 / 不主动发）
    public bool NoHeartbeat;      // 心跳时跳过（避免无操作反复应用触发声音/动画）
    public bool HighFreq;         // 30Hz 高频（方向角 Lever/Gear 专用）

    internal bool Applying;       // 防环（约束#3）：应用远端期间不检测本地变化
    internal bool HasTarget; internal float TargetF; // 插值目标
    internal bool LSet; internal float LF; internal int LI; internal bool LB; // 本地 last 值
    /// <summary>客机应用远端值后的回驱抑制截止（秒，Time.realtimeSinceStartup）。
    /// 防：游戏把本地控件回驱成旧值→被误判“变化”回灌主机→主机回弹（发射/重置争抢根因）。</summary>
    internal float SuppressUpUntil;
    /// <summary>上次拖拽状态（grab 沿检测：真操作开始→清除抑制窗口）。</summary>
    internal bool PrevBusy;
}

/// <summary>
/// 数值层（ValueLayer，MsgType=201）。M3：把 V1 <c>ValueSync</c> 的值同步引擎迁到分层架构：
/// - 通过 <see cref="IHostStore"/> 读写/广播，不直接碰控件/网络。
/// - <see cref="V2Authority.Operator"/>（默认）：谁操作谁执行 + 广播（主机中继）+ 2s 心跳兜底对齐。
/// - <see cref="V2Authority.Host"/>：仅主机广播，客机只接收应用（防客机读 0 覆盖主机）。
/// - 防环：应用远端置 <c>Applying</c>（约束#3）；拖拽中本地优先（谁操作谁权威）。
/// - IL2CPP：绑定字典用显式枚举器；消息经批量合包（无每帧堆分配，除心跳/事件）。
/// </summary>
public sealed class ValueLayer : ISyncedModule
{
    public static ValueLayer Instance { get; } = new ValueLayer();

    private ValueLayer() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2Value;

    private const float Interval = 0.2f;            // 低频默认
    private const float HighFreqInterval = 0.033f;  // 30Hz：仅 HighFreq 绑定
    private const float HeartbeatInterval = 2f;     // 心跳全量广播（对齐/自愈/新加入）
    private const float InterpRate = 10f;
    private float _timer, _hfTimer, _heartbeatTimer, _diagTimer;

    private readonly Dictionary<string, ValueBinding> _bindings = new();

    // ---------------- 注册 API（M6 控件发现层调用；其余模块也可直接注册） ----------------

    public ValueBinding RegisterFloat(string id, Func<float> get, Action<float> set,
        float deadzone = 0.001f, bool interp = false, Func<bool> busy = null,
        V2Authority authority = V2Authority.Operator)
    {
        var b = new ValueBinding
        {
            Id = id, Kind = 0, Deadzone = deadzone, Interpolate = interp,
            GetF = get, SetF = set, IsBusy = busy, Authority = authority
        };
        _bindings[id] = b;
        return b;
    }

    public ValueBinding RegisterInt(string id, Func<int> get, Action<int> set,
        float deadzone = 1f, bool interp = false, Func<bool> busy = null,
        V2Authority authority = V2Authority.Operator)
    {
        var b = new ValueBinding
        {
            Id = id, Kind = 1, Deadzone = deadzone, Interpolate = interp,
            GetI = get, SetI = set, IsBusy = busy, Authority = authority
        };
        _bindings[id] = b;
        return b;
    }

    public ValueBinding RegisterBool(string id, Func<bool> get, Action<bool> set,
        Func<bool> busy = null, V2Authority authority = V2Authority.Operator)
    {
        var b = new ValueBinding
        {
            Id = id, Kind = 2, Deadzone = 0f, Interpolate = false,
            GetB = get, SetB = set, IsBusy = busy, Authority = authority
        };
        _bindings[id] = b;
        return b;
    }

    public bool Has(string id) => _bindings.ContainsKey(id ?? "");
    public void Remove(string id) { if (id != null) _bindings.Remove(id); }
    public void Clear() { _bindings.Clear(); }

    // ---------------- ISyncedModule ----------------

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;

        // 每帧：客机插值逼近远端目标（Interpolate 绑定）
        ApplyInterpolated(dt);

        _heartbeatTimer += dt;
        bool heartbeat = _heartbeatTimer >= HeartbeatInterval;
        if (heartbeat) _heartbeatTimer = 0f;

        // 高频 tick（30Hz）：只处理 HighFreq 绑定
        _hfTimer += dt;
        if (_hfTimer >= HighFreqInterval)
        {
            _hfTimer = 0f;
            TickBindings(highFreq: true, heartbeat);
        }

        // 低频 tick（0.2s）：处理其余 + 心跳全量
        _timer += dt;
        if (_timer >= Interval)
        {
            _timer = 0f;
            TickBindings(highFreq: false, heartbeat);
        }

        _diagTimer += dt;
        if (_diagTimer >= 5f)
        {
            _diagTimer = 0f;
            CoopLog.Info("SyncV2.value", () => $"[SyncV2] ValueLayer bindings={_bindings.Count} host={Store.IsHost}", 5f);
        }
    }

    /// <summary>按频段/心跳检测本地变化并广播（<see cref="V2Authority.Operator"/> 谁操作谁发；
    /// <see cref="V2Authority.Host"/> 仅主机发）。</summary>
    private void TickBindings(bool highFreq, bool heartbeat)
    {
        using var e = _bindings.GetEnumerator();
        while (e.MoveNext())
        {
            var b = e.Current.Value;
            if (!heartbeat && b.HighFreq != highFreq) continue; // 心跳全量；否则只处理对应频段
            // 主机权威绑定：客机不检测/不上行（只接收应用，防读 0 覆盖主机）
            if (b.Authority == V2Authority.Host && !Store.IsHost) continue;
            // 本地拖拽：跟踪 grab 沿（真操作开始 → 清除“应用后抑制”窗口）。
            // ⚠️ 修复（2026-08-15）：拖拽中**不再 continue 跳过发送**——否则仰角 Lever / Spur Gear
            // 拖到目标值只在松手才同步到对端（“松手才同步过去”）。改为拖拽中也走变化检测广播
            // （HighFreq 30Hz 实时跟随）；接收端本端 busy 时跳过应用（OnPacket IsBusy 保护）。
            bool busy = b.IsBusy != null && b.IsBusy();
            if (busy)
            {
                if (!b.PrevBusy) b.SuppressUpUntil = 0f; // 用户真正抓住 → 拖拽中/释放后照常上报
                b.PrevBusy = true;
            }
            else b.PrevBusy = false;
            if (b.Applying) continue;                    // 应用远端中，不检测本地（防环）
            // 客机刚应用远端值后的回驱抑制：游戏把本地控件回驱成旧值会误报“变化”回灌主机
            // （发射/重置后回弹争抢根因）。抑制窗口内不上报；真操作 grab（busy）已清除。
            if (!Store.IsHost && Time.realtimeSinceStartup < b.SuppressUpUntil) continue;
            float cur = GetValue(b);
            float last = Last(b);
            bool changed = !b.LSet || Delta(cur, last) >= b.Deadzone || IsEdge(cur) != IsEdge(b.LSet ? last : cur);
            // 心跳：仅主机全量发送对齐（客机不心跳上行——客机把应用后回驱旧值回灌主机正是争抢根因）；
            // NoHeartbeat 且无变化时跳过（避免无操作反复应用触发动画/声音）。
            bool sendOnHeartbeat = heartbeat && !b.NoHeartbeat && Store.IsHost;
            if (!changed && !sendOnHeartbeat) continue;
            SendValue(b, cur);
            MarkLocal(b, cur);
        }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int kind = r.GetByte();
            string id = r.GetString();
            float v = r.GetFloat();
            byte busy = r.GetByte();
            var b = Find(id);
            if (b == null) return;
            // 本端拖拽中：忽略远端（谁操作谁权威，本地优先，防覆盖）
            if (b.IsBusy != null && b.IsBusy()) return;
            // 远端拖拽中（busy=1）且需插值：平滑追，不直接精确覆盖
            if (busy == 1 && b.Kind == 0 && b.Interpolate)
            {
                b.TargetF = v;
                b.HasTarget = true;
            }
            else
            {
                b.Applying = true;
                try { SetValue(b, v); }
                finally { b.Applying = false; }
                b.HasTarget = false;
                // 镜像到主机权威 store（两端一致；主机侧为后续 late-join 基线）
                if (b.Kind == 0) Store.ApplyFloat(id, v);
                else if (b.Kind == 1) Store.ApplyInt(id, (int)Math.Round(v));
                else Store.ApplyBool(id, v >= 0.5f);
                // 客机：应用远端值后开回驱抑制窗口（防游戏回驱旧值被误上报回灌主机）
                if (!Store.IsHost) b.SuppressUpUntil = Time.realtimeSinceStartup + 1.0f;
            }
            MarkLocal(b, v);
            // 主机中继：收到客机操作者上行 → 转发给其他客机（星型拓扑，客机间无直连）
            if (Store.IsHost)
                _net?.EnqueueBatch(data, true);
            CoopLog.Debug("SyncV2.valueRecv", () => $"[SyncV2] ValueLayer recv kind={kind} id='{id}' v={v:0.###} busy={busy}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ValueLayer] OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { /* 绑定由发现层按场景注册；会话开始无需清空（场景加载会 Clear+重扫） */ }
    public void OnSessionEnded() { }
    public void Reset() { }

    // ---------------- 内部 ----------------

    private void SendValue(ValueBinding b, float v)
    {
        var store = Store;
        // 主机侧镜像到 store（操作者权威：主机本地也记录；客机由接收路径 Apply）
        if (store.IsHost) store.SetFloat(b.Id, v);
        // 会话广播（操作者权威/主机权威通用）：主机→全员，客机→主机中继
        store.Broadcast(SyncV2Bootstrap.ValueMsgType, w =>
        {
            w.Put(b.Kind);
            w.Put(b.Id ?? "");
            w.Put(v);
            w.Put(b.IsBusy != null && b.IsBusy() ? (byte)1 : (byte)0);
        }, reliable: false);
    }

    /// <summary>客户端插值逼近远端目标（Interpolate 绑定）。</summary>
    private void ApplyInterpolated(float dt)
    {
        if (Store.IsHost) return;
        float t = 1f - Mathf.Exp(-InterpRate * dt);
        using var e = _bindings.GetEnumerator();
        while (e.MoveNext())
        {
            var b = e.Current.Value;
            if (!b.Interpolate || !b.HasTarget) continue;
            if (b.IsBusy != null && b.IsBusy()) { b.HasTarget = false; continue; } // 本地操作中，放弃远端目标
            float cur = GetValue(b);
            float next = Mathf.Lerp(cur, b.TargetF, t);
            if (Mathf.Abs(next - cur) < 0.0005f) { next = b.TargetF; b.HasTarget = false; }
            b.Applying = true;
            try { SetValue(b, next); }
            finally { b.Applying = false; }
            MarkLocal(b, next);
            // 插值驱动中同样抑制回灌（值来自远端，不误上报）
            if (!Store.IsHost) b.SuppressUpUntil = Time.realtimeSinceStartup + 1.0f;
        }
    }

    private ValueBinding Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return _bindings.TryGetValue(id, out var b) ? b : null;
    }

    private static float GetValue(ValueBinding b)
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

    private static void SetValue(ValueBinding b, float v)
    {
        try
        {
            if (b.Kind == 0) b.SetF(v);
            else if (b.Kind == 1) b.SetI((int)Math.Round(v));
            else b.SetB(v >= 0.5f);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ValueLayer] SetValue: {ex.Message}"); }
    }

    private static bool IsEdge(float v) => v <= 0.0001f || v >= 0.9999f;

    private static void MarkLocal(ValueBinding b, float v)
    {
        b.LSet = true;
        if (b.Kind == 0) b.LF = v;
        else if (b.Kind == 1) b.LI = (int)Math.Round(v);
        else b.LB = v >= 0.5f;
    }

    private static float Last(ValueBinding b) => b.Kind switch { 0 => b.LF, 1 => b.LI, _ => b.LB ? 1f : 0f };

    private static float Delta(float a, float b) => Math.Abs(a - b);
}
