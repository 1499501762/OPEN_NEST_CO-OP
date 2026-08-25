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
    private const float Interval = 0.8f;        // 低频默认（静止/非拖拽控件值检测——GetValue IL2CPP 开销大，0.5s→0.8s：frame.log ControlSync 72-118ms/s 大头是 71 绑定 GetValue×2/s；拖拽/高频控件走 0.033s 实时）
    private const float HighFreqInterval = 0.033f; // 30Hz：仅炮塔 Lever/Gear（HighFreq 绑定）专用，其余不占高频带宽
    private const float InterpRate = 10f;
    private const float HeartbeatInterval = 4f; // 周期全量广播（初始对齐/状态自愈兜底）。⚠️ 2026-08-25 2s→4s：
    // 主通道是"变化才发包"（Bool/最终值变化走 reliable 立即 + 拖拽 settle 立即 reliable），心跳只是低频兜底，
    // 不需要 2s 全量重发所有值（省带宽/降级负载）——新成员/重连仍可靠对齐（4s 内）
    private static float _timer;
    private static float _hfTimer;
    private static float _logTimer;
    private static float _heartbeatTimer;
    private static float _diagTimer;
    private static float _dragTimer; // 拖拽检测计时（降频 0.25s——IsBusy IL2CPP 调用开销大；拖拽开始/释放 settle 延迟 0.25s 远小于网络延迟）
    /// <summary>本地释放保护截止时间（2026-08-23）：本地刚松开方向角/仰角等高频控件后，短暂忽略远端
    /// state——否则松开瞬间收到对端旧广播值（发送时刻早于本地最新 settle）会回跳（“松开动量回跳”根因）。
    /// 非操作方无释放 → 不保护，正常接收。窗口 0.35s（覆盖 170ms 延迟 ×2 往返）。</summary>
    private static float _releaseProtectUntil = -1f;
    private const float ReleaseProtectWindow = 0.35f;
    private static int _stateLog;
    private static int _cmdLog;
    private static int _stateSendLog;
    private static int _skipLog;
    private static int _cmdSendLog;
    private static int _fullRecvLog; // OnFullState（ControlFull 兼容路由）诊断计数——主机不再发全量包，保留字段防编译错
    // 方向角（__turret/rotation）收发诊断（2026-08-23）：每 ~30 条打一次 Info（拖拽 30Hz 时≈1s 一条），
    // 定位“方向角拉杆→Gear 动量不同步/争抢”是丢包还是双向覆盖。
    private static int _rotCmdLog;
    private static int _rotStateLog;

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
        /// <summary>⚠️ 2026-08-26：完全排除出 ControlFull 全量广播（含心跳）——主机权威"状态"绑定
        /// （引擎 running/压力/反炮兵等）**只走状态变化广播**（变化才发 SendState）。
        /// 否则 ControlFull 心跳会把主机开局 getter 误读/未启动值（如引擎 initialRunning=False）
        /// 强加给客机 → 客机被 STOP 关引擎 → 联机开局停电（"引擎停电"根因）。开局不广播、变化才广播。</summary>
        public bool SkipFull;
        public bool HighFreq; // 高频同步（30Hz）：仅炮塔 Lever/Gear（方向角/仰角）专用；其余保持低频 + 插值（省带宽）
        public bool SenderWhenBusy; // 2026-08-23：转速等"谁操作谁权威"值：**非操作方（busy=false）不上行**——
        // 防静止方广播 0 覆盖操作方动量（方向角来回转根因之一）；松开瞬间由 TickDraggingChange settle 放行发送最后值
        internal bool HSet; internal float HF; internal int HI; internal bool HB;
        internal bool LSet; internal float LF; internal int LI; internal bool LB;
        internal bool HasTarget; internal float TargetF;
        internal bool Applying;
        internal bool PrevDragging; // 上次拖拽状态（释放时触发 settle）
        /// <summary>强制下一轮广播（2026-08-23）：转盘停止后强制同步一次（忽略 deadzone 通量节流），
        /// 用于“转盘停止一段时间后同步角度”的精确对齐。</summary>
        internal bool ForceNext;
        /// <summary>值版本序号（发送端递增，包带 seq）。接收端按 seq 去旧保新——解决 unreliable 无时序/乱序/晚到覆盖 reliable 新值（2026-08-25）。</summary>
        internal ushort Seq;
        /// <summary>接收端已应用的最近 seq（拒绝旧/重复包，回绕安全）。</summary>
        internal ushort LastSeq;
        /// <summary>诊断回调（可选）：周期性打印绑定状态（定位同步异常，如方向角动量/丢包）。</summary>
        public Func<string> Diag;
        internal int DiagCounter;
    }

    private static readonly Dictionary<string, Binding> _bindings = new();
    /// <summary>有拖拽语义的绑定（IsBusy != null）——TickDraggingChange 只遍历此列表，避免每 0.15s 遍历全部绑定。
    /// ⚠️ 2026-08-25 帧性能：frame.log ControlSync≈90ms/s 大头是 TickDraggingChange 对 71 绑定全遍历 + IL2CPP IsBusy()，
    /// 大多数绑定无拖拽语义 → 只维护有 busy 的少数（Dial/Slider/Turret 等），遍历量 71 → <20。</summary>
    private static readonly List<Binding> _busyBindings = new();

    /// <summary>清空所有绑定（场景切换/重建时调用）。⚠️ V1 死代码：全仓无调用者（场景清理实际走 ControlSync.Remove 逐条移除），已注释。</summary>
    // public static void Clear() { _bindings.Clear(); }

    /// <summary>是否存在指定 id 的绑定。</summary>
    public static bool Has(string id) => _bindings.ContainsKey(id);

    /// <summary>强制指定绑定下一轮广播（2026-08-23）：忽略 deadzone 通量节流，用于转盘停止后角度对齐。</summary>
    public static void ForceSend(string id)
    {
        if (_bindings.TryGetValue(id, out var b)) b.ForceNext = true;
    }

    /// <summary>移除指定 id 的绑定（场景切换/控件销毁时清理）。</summary>
    public static void Remove(string id)
    {
        if (!_bindings.TryGetValue(id, out var b)) return;
        _bindings.Remove(id);
        if (b.IsBusy != null) _busyBindings.Remove(b); // 同步移除 busy 列表项
    }

    public static Binding AddFloat(string id, Func<float> get, Action<float> set,
        float deadzone = 0.001f, bool interp = false, Func<bool> busy = null)
    {
        var b = new Binding { Id = id, Kind = 0, Deadzone = deadzone, Interpolate = interp, GetF = get, SetF = set, IsBusy = busy };
        if (_bindings.ContainsKey(id)) Remove(id); // 重复注册：先移除旧绑定（含 busy 列表项）再覆盖
        _bindings[id] = b; // 索引器赋值：id 是唯一 key（注册处已去重），重复时覆盖旧绑定
        if (busy != null) _busyBindings.Add(b);
        return b;
    }

    public static Binding AddInt(string id, Func<int> get, Action<int> set,
        float deadzone = 1f, bool interp = false, Func<bool> busy = null)
    {
        var b = new Binding { Id = id, Kind = 1, Deadzone = deadzone, Interpolate = interp, GetI = get, SetI = set, IsBusy = busy };
        if (_bindings.ContainsKey(id)) Remove(id); // 重复注册：先移除旧绑定（含 busy 列表项）再覆盖
        _bindings[id] = b; // 索引器赋值：id 是唯一 key（注册处已去重），重复时覆盖旧绑定
        if (busy != null) _busyBindings.Add(b);
        return b;
    }

    public static Binding AddBool(string id, Func<bool> get, Action<bool> set, Func<bool> busy = null)
    {
        var b = new Binding { Id = id, Kind = 2, Deadzone = 0f, Interpolate = false, GetB = get, SetB = set, IsBusy = busy };
        if (_bindings.ContainsKey(id)) Remove(id); // 重复注册：先移除旧绑定（含 busy 列表项）再覆盖
        _bindings[id] = b; // 索引器赋值：id 是唯一 key（注册处已去重），重复时覆盖旧绑定
        if (busy != null) _busyBindings.Add(b);
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
            CoopLog.Info("ValueSync.bindings", () => $"[ValueSync] bindings={_bindings.Count} host={net.IsHost} state={net.State}", 5f);
        }

        // 每帧：客户端插值逼近远端目标
        ApplyInterpolated(dt);
        // 拖拽状态变化检测（释放瞬间 settle）——降频 0.15s（原 0.05s）：IL2CPP IsBusy 调用开销大（71 绑定/帧），
        // 拖拽开始/释放 settle 延迟 0.15s 远小于网络延迟，不影响同步精度
        // ⚠️ 2026-08-25：用真实时间（unscaledDeltaTime）——NetworkGovernor 分级降级会缩放传入 dt，
        // 若拖拽/心跳/低频定时器跟着变慢 → 最终数值可靠同步被延迟（看起来像"同步被丢"）
        float rdt = UnityEngine.Time.unscaledDeltaTime;
        _dragTimer += rdt;
        if (_dragTimer >= 0.25f)
        {
            _dragTimer = 0f;
            TickDraggingChange(net);
        }

        // 诊断：带 Diag 的绑定周期打印（每 ~1s 一次，节流）——定位方向角动量/丢包等同步异常
        _diagTimer += dt;
        // ⚠️ 2026-08-26 帧性能：诊断遍历（每 1s 遍历全部绑定查 Diag 回调）加 level guard——
        // Release（Debug 关）下跳过，零遍历开销。Diag 是诊断回调，Debug 构建/开 Debug 时才有用。
        if (_diagTimer >= 1f && OpenNestCore.Logging.CoopLog.Level <= OpenNestCore.Logging.LogLevel.Debug)
        {
            _diagTimer = 0f;
            foreach (var b in _bindings.Values)
            {
                if (b.Diag == null) continue;
                if ((b.DiagCounter++ % 60) == 0)
                {
                    try { CoopLog.Debug("ValueSync.diag", () => b.Diag()); } catch { }
                }
            }
        }

        bool online = net.State == SessionState.Hosting || net.State == SessionState.Joined;
        if (!online) return;

        // 高频 tick（30Hz）：只处理炮塔 Lever/Gear（HighFreq 绑定）——方向角/仰角专用，其余控件不占高频带宽
        // 用缩放 dt（受 NetworkGovernor 分级控制：降级时高频发包降频降负载）
        _hfTimer += dt;
        if (_hfTimer >= HighFreqInterval)
        {
            _hfTimer = 0f;
            if (net.IsHost) HostTickHighFreq(net);
            else ClientTick(net, highFreqOnly: true);
        }

        // 低频 tick（0.5s）：处理其余控件 + 心跳（心跳含高频，保证新加入/重连客户端初始对齐）。
        // ⚠️ 2026-08-25：低频/心跳用**真实时间**（不受分级降频缩放 dt 影响）——最终数值/状态同步必须可靠送达。
        // ⚠️ 2026-08-26 回退 ControlFull 全量机制：主机低频改回"逐绑定 Delta 变化才发单包"（Heartbeat 补发
        // 从未发送过的绑定做初始对齐）。不再发含所有绑定的全量覆盖包（原签名变化→整组全量包会覆盖开局状态）。
        _timer += rdt;
        if (_timer < Interval) return;
        _timer = 0f;

        _heartbeatTimer += rdt;
        bool heartbeat = _heartbeatTimer >= HeartbeatInterval;
        if (heartbeat) _heartbeatTimer = 0f;

        if (net.IsHost) HostSendVector(net, heartbeat);
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
            ushort seq = r.GetUShort();
            string id = r.GetString();
            float v = r.GetFloat();
            byte dragging = r.GetByte();
            var b = Find(id);
            if (b != null)
            {
                // ⚠️ 时序保护（2026-08-25）：拒绝旧/重复 cmd（unreliable 乱序/晚到）——不覆盖已应用的可靠新值
                if (!IsNewer(seq, b.LastSeq)) return;
                b.LastSeq = seq;
                bool localBusy = b.IsBusy != null && b.IsBusy();
                if (localBusy && (++_skipLog % 20) == 1)
                    CoopLog.Debug("ValueSync.skipLocalBusy", () => $"[ValueSync] cmd skip localBusy id='{id}' v={v:0.###} drag={dragging}");
                // 方向角诊断：主机收到客户端方向角上行（丢包/覆盖判定）
                if (id == "__turret/rotation" && (++_rotCmdLog % 30) == 1)
                    CoopRuntime.LogSource?.LogInfo($"[ValueSync] rot-cmd from={from} v={v:0.###} drag={dragging} localBusy={localBusy} b={b != null}");
                // 释放保护窗口（对称）：本端刚松开高频控件，短暂忽略远端 cmd——否则主机释放瞬间
                // 被客户端旧值覆盖 → 回跳（"松开动量回跳"根因，2026-08-23）。
                if (b.HighFreq && Time.time < _releaseProtectUntil)
                {
                    if ((++_skipLog % 20) == 1)
                        CoopLog.Debug("ValueSync.skipReleaseProtect", () => $"[ValueSync] cmd skip release-protect id='{id}' v={v:0.###}");
                    // 仍转发给其他客户端（若存在）：忽略应用但不吞消息（保持多客户端一致性）
                    if (net.IsHost)
                        net.EnqueueBatch(data, true, true);
                    return;
                }
                // 拖拽中（本端）不覆盖（谁操作谁权威，本地优先）。
                // ⚠️ 修复（2026-08-15）：原 `&& dragging == 0` 导致**发送端拖拽中**（dragging=1）主机
                // 不应用 → 仰角 Lever / Spur Gear 拖到目标值只在松手才同步到对端（"松手才同步过去"根因）。
                // 改为只跳过本端拖拽，应用远端实时拖拽值（30Hz HighFreq 实时跟随）；本端 busy 仍保护。
                if (!localBusy)
                {
                    b.Applying = true;
                    try { SetValue(b, v); }
                    finally { b.Applying = false; }
                    MarkHost(b);
                }
            }
            if (net.IsHost)
                // E 分级：高频 binding 的 cmd（30Hz Lever）转发走 unreliable（连续值，靠心跳 reliable 保底对齐）；低频/未知 → reliable
                net.EnqueueBatch(data, true, b != null && b.HighFreq);
            if ((++_cmdLog % 20) == 0)
                CoopLog.Debug("ValueSync.cmdRecv", () => $"[ValueSync] cmd recv kind={kind} id='{id}' v={v:0.###} drag={dragging}");
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
            ushort seq = r.GetUShort();
            string id = r.GetString();
            float v = r.GetFloat();
            byte dragging = r.GetByte();
            var b = Find(id);
            if (b == null) return;
            // ⚠️ 时序保护（2026-08-25）：拒绝旧/重复 state（unreliable 乱序/晚到）——不覆盖已应用的可靠新值
            if (!IsNewer(seq, b.LastSeq)) return;
            b.LastSeq = seq;
            if (b.IsBusy != null && b.IsBusy()) return; // 本端拖拽中，忽略远端
            // 释放保护窗口：本地刚松开高频控件（方向角/仰角 Lever），短暂忽略远端避免回跳
            if (b.HighFreq && Time.time < _releaseProtectUntil) return;
            // 方向角诊断：客户端收到主机方向角 state（是否被拖拽/覆盖判定）
            if (id == "__turret/rotation" && (++_rotStateLog % 30) == 1)
                CoopRuntime.LogSource?.LogInfo($"[ValueSync] rot-state v={v:0.###} drag={dragging}");
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
                CoopLog.Debug("ValueSync.stateRecv", () => $"[ValueSync] state recv kind={kind} id='{id}' v={v:0.###} drag={dragging}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ValueSync OnState: {ex.Message}"); }
    }

    // ---------------- 内部 ----------------

    /// <summary>每帧检测拖拽状态变化：释放瞬间立即 settle 发送当前值（不等 0.2s 周期）。
    /// ⚠️ 2026-08-25 帧性能：只遍历有拖拽语义的绑定（_busyBindings，Dial/Slider/Turret 等 <20 个），
    /// 不再遍历全部 _bindings.Values（71 个）——frame.log ControlSync 曾 90ms/s 大头。</summary>
    private static void TickDraggingChange(NetManager net)
    {
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        foreach (var b in _busyBindings)
        {
            if (b.IsBusy == null) continue; // 理论上不存在（只加入有 busy 的），防御
            bool dragging = b.IsBusy();
            if (dragging == b.PrevDragging) continue;
            b.PrevDragging = dragging;
            if (dragging) continue; // 刚进入拖拽：不立即发（等值变化/释放 settle）
            // 刚释放：settle 精确发送当前值（低频精确值 → 可靠通道，不可靠会丢 settle）
            _releaseProtectUntil = Time.time + ReleaseProtectWindow; // 释放保护窗口：本地刚释放，忽略远端防回跳
            float cur = GetValue(b);
            if (net.IsHost) SendState(net, b, cur, false);
            else SendCmd(net, b, cur, false);
        }
    }

    /// <summary>主机低频/心跳：逐绑定变化检测广播。
    /// ⚠️ 2026-08-26 回退 ControlFull 全量机制（01:32 引入）：原"签名变化→发含所有绑定的全量包
    /// ControlFull + 客机 OnFullState 覆盖式应用"——任何绑定变化就把所有绑定（含 Locking Lever、
    /// 引擎 Lever 等两端语义不同的值）全量推给客机覆盖 → 开局大量初始状态错乱（分片修复让大包到达后暴露）。
    /// 改回逐绑定 Delta：**只有变化的绑定**才 SendState 单包（心跳补发从未发送过的绑定做初始对齐）。
    /// 帧性能保留：一遍 GetValue（0.8s 低频遍历，非每帧）+ seq 时序保护 + SkipFull 主机权威变化广播。</summary>
    private static void HostSendVector(NetManager net, bool heartbeat)
    {
        foreach (var b in _bindings.Values)
        {
            // 非心跳：排除高频（方向角/仰角走 30Hz 逐绑定实时）；心跳：含高频（初始对齐）
            if (!heartbeat && b.HighFreq) continue;
            // 心跳跳过 NoHeartbeat（弹道计算机装药摇杆等：无操作时对端重复应用触发声音/动画）
            if (heartbeat && b.NoHeartbeat) continue;
            float cur = GetValue(b);
            // ⚠️ SkipFull（主机权威状态如引擎/压力/反炮兵）：只走变化广播（false→true / true→false
            // 变化才 SendState，开局首次只记录不上行，避免把开局 getter 误读/未启动值广播给客机覆盖）。
            if (b.SkipFull)
            {
                bool edgeSc = IsEdgeValue(cur);
                bool edgeSl = b.HSet && IsEdgeValue(HostLast(b));
                bool schanged = b.ForceNext || !b.HSet || Delta(cur, HostLastSafe(b)) >= b.Deadzone || edgeSc != edgeSl;
                bool first = !b.HSet;
                b.ForceNext = false;
                MarkHost(b);
                // 首次（!HSet）只记录不上行——开局不广播（避免误读值覆盖对端）；之后变化才广播
                if (!first && schanged)
                {
                    SendState(net, b, cur, false); // 状态变化：reliable（丢了永久不同步）
                }
                continue;
            }
            // 逐绑定 Delta 变化检测：只在 值变化 / edge 状态变化 / 强制广播 时发送（05cf321 语义）
            float last = b.HSet ? HostLast(b) : float.NaN;
            bool edgeCur = IsEdgeValue(cur);
            bool edgeLast = b.HSet && IsEdgeValue(last);
            bool forced = b.ForceNext;
            b.ForceNext = false;
            // 心跳补发从未发送过（!HSet）的绑定做初始对齐（新加入/重连）；非心跳变化才发
            bool changed = forced || !b.HSet || Delta(cur, last) >= b.Deadzone || edgeCur != edgeLast;
            if (!changed) continue;
            MarkHost(b);
            SendState(net, b, cur, false); // reliable（低频最终值/状态变化）
        }
    }

    /// <summary>客户端：收到向量全量包（ControlFull）→ 覆盖式应用所有值（天然最终一致）。
    /// 逐绑定带 seq 去旧 + busy/释放保护/ClientNoApply 同 OnState。</summary>
    public static void OnFullState(byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int n = 0;
            while (r.AvailableBytes > 0)
            {
                int kind = r.GetByte();
                ushort seq = r.GetUShort();
                string id = r.GetString();
                float v = r.GetFloat();
                byte dragging = r.GetByte();
                n++;
                var b = Find(id);
                if (b == null) continue;
                // 时序保护：拒绝旧/重复包
                if (!IsNewer(seq, b.LastSeq)) continue;
                b.LastSeq = seq;
                if (b.IsBusy != null && b.IsBusy()) continue; // 本端拖拽中，忽略远端
                if (b.HighFreq && Time.time < _releaseProtectUntil) continue; // 本地刚松开高频控件，短暂忽略远端防回跳
                if (b.ClientNoApply) { b.HasTarget = false; continue; }
                b.Applying = true;
                try { SetValue(b, v); }
                finally { b.Applying = false; }
                b.HasTarget = false;
                MarkLocal(b, v);
            }
            if ((++_fullRecvLog % 20) == 1)
                CoopLog.Debug("ValueSync.vecRecv", () => $"[ValueSync] vector recv n={n}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ValueSync OnFullState: {ex.Message}"); }
    }

    /// <summary>主机 30Hz 高频 tick：仅 HighFreq（方向角/仰角 Lever/Gear）逐绑定发包（unreliable 连续值实时）。
    /// 低频/心跳已改走 HostSendVector（向量全量包），不再走本方法。
    /// ⚠️ 2026-08-26 帧性能：遍历 _busyBindings（仅拖拽语义绑定 <20）而非 _bindings.Values（71 个）——
    /// 高频控件都有 IsBusy（拖拽/衰减期）→ 都在 _busyBindings；纯迭代 71→<20 减 ~70%。</summary>
    private static void HostTickHighFreq(NetManager net)
    {
        foreach (var b in _busyBindings)
        {
            // 高频 tick：HighFreq 绑定 或 正在拖拽的普通控件（PrevDragging 缓存——拖拽实时跟随，静止控件省 GetValue）
            if (!b.HighFreq && !b.PrevDragging) continue;
            // 转速等"谁操作谁权威"值：非操作方（busy=false）不上行（防静止方广播 0 干扰操作方动量）
            // ⚠️ 2026-08-25 性能：用缓存 PrevDragging 替代每帧 IsBusy()（IL2CPP 调用贵）——拖拽检测 0.15s 已更新缓存
            if (b.SenderWhenBusy && (b.IsBusy == null || !b.PrevDragging)) continue;
            float cur = GetValue(b);
            float last = b.HSet ? HostLast(b) : float.NaN;
            bool edgeCur = IsEdgeValue(cur);
            bool edgeLast = b.HSet && IsEdgeValue(last);
            bool forced = b.ForceNext;
            b.ForceNext = false;
            bool changed = (forced || !b.HSet || Delta(cur, last) >= b.Deadzone || edgeCur != edgeLast || forced);
            if (!changed) continue;
            MarkHost(b);
            SendState(net, b, cur, true); // 高频 → unreliable
        }
    }

    private static void ClientTick(NetManager net, bool highFreqOnly)
    {
        foreach (var b in _bindings.Values)
        {
            // 高频 tick：HighFreq 或 正在拖拽（实时跟随）；低频：非高频
            if (highFreqOnly) { if (!b.HighFreq && !b.PrevDragging) continue; }
            else if (b.HighFreq) continue;
            if (b.ClientNoSend) continue; // 主机权威状态：客户端只接收，不上行（防误改主机共享状态）
            if (b.SenderWhenBusy && (b.IsBusy == null || !b.PrevDragging)) continue; // 转速：非操作方不上行（缓存拖拽状态省 IL2CPP）
            if (b.Applying) continue; // 正在应用远端，不检测本地
            float cur = GetValue(b);
            float last = b.LSet ? LocalLast(b) : float.NaN;
            bool edgeCur = IsEdgeValue(cur);
            bool edgeLast = b.LSet && IsEdgeValue(last);
            bool forced = b.ForceNext;
            b.ForceNext = false;
            bool changed = forced || !b.LSet || Delta(cur, last) >= b.Deadzone || edgeCur != edgeLast;
            if (!changed) continue;
            MarkLocal(b, cur);
            // E 分级：高频 tick → unreliable；低频 → reliable
            SendCmd(net, b, cur, highFreqOnly);
        }
    }

    /// <summary>值接近 0 或 1（归一化刻度盘/滑块两端），边界处必须精确同步。</summary>
    private static bool IsEdgeValue(float v) => v <= 0.0001f || v >= 0.9999f;

    private static void ApplyInterpolated(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return;
        float t = 1f - Mathf.Exp(-InterpRate * dt);
        foreach (var b in _bindings.Values)
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

    private static void SendState(NetManager net, Binding b, float v, bool unreliable)
    {
        b.Seq = (ushort)(b.Seq + 1); // 值版本递增（心跳 reliable 与高频 unreliable 共用同一序号 → 接收端统一去旧）
        var w = NetProtocol.Begin(MsgType.ControlState);
        w.Put(b.Kind);
        w.Put(b.Seq);
        w.Put(b.Id ?? "");
        w.Put(v);
        w.Put(b.IsBusy != null && b.IsBusy() ? (byte)1 : (byte)0); // 拖拽状态
        var data = NetProtocol.Snapshot(w);
        net.EnqueueBatch(data, true, unreliable);
        if ((++_stateSendLog % 40) == 1)
            CoopLog.Debug("ValueSync.stateSend", () => $"[ValueSync] state send kind={b.Kind} id='{b.Id}' v={v:0.###} seq={b.Seq} drag={(b.IsBusy != null && b.IsBusy() ? 1 : 0)} unrel={unreliable}");
    }

    private static void SendCmd(NetManager net, Binding b, float v, bool unreliable)
    {
        b.Seq = (ushort)(b.Seq + 1); // 值版本递增
        var w = NetProtocol.Begin(MsgType.ControlCmd);
        w.Put(b.Kind);
        w.Put(b.Seq);
        w.Put(b.Id ?? "");
        w.Put(v);
        w.Put(b.IsBusy != null && b.IsBusy() ? (byte)1 : (byte)0); // 拖拽状态
        net.EnqueueBatch(NetProtocol.Snapshot(w), false, unreliable);
        // 低频诊断：确认客机上行哪些控件（尤其曲柄 Spur Gear / 仰角 Lever）
        if ((++_cmdSendLog % 40) == 1 && b.Id != null && b.Id.IndexOf("Spur Gear", System.StringComparison.OrdinalIgnoreCase) >= 0)
            CoopLog.Debug("ValueSync.cmdSend", () => $"[ValueSync] cmd send '{b.Id}' v={v:0.###} seq={b.Seq} unrel={unreliable}");
    }

    private static Binding Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return _bindings.TryGetValue(id, out var b) ? b : null;
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
    /// <summary>首次（HSet=false）返回 NaN（避免与未初始化的 0 比较误判变化）；已初始化返回 HostLast。</summary>
    private static float HostLastSafe(Binding b) => b.HSet ? HostLast(b) : float.NaN;
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

    /// <summary>seq 是否比 last 新（回绕安全：无符号差 >0 且 <32768；last==0 视为首包）。</summary>
    private static bool IsNewer(ushort seq, ushort last)
    {
        if (last == 0) return true;
        int d = (ushort)(seq - last);
        return d > 0 && d < 32768;
    }
}
