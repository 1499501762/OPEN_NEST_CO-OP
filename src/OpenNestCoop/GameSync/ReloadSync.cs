using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 装填 + 开火同步（主机权威 + 状态快照，复用 TurretSync 模式）。
/// - 装填：主机每 0.2s 轮询每门炮的 ArtilleryReloadController.currentStateIndex
///   + PowderChargeController.currentSelectedCharges，变化检测广播 ReloadState 快照。
///   客户端检测本地装填状态变化（用户推进/选药包）→ ReloadCmd 上行 → 主机应用 → 广播。
/// - 开火：客户端 RequestFire 被 Harmony 拦截 → FireRequest 上行主机 → 主机 RequestFire
///   （FireShell postfix 广播 GunFire）→ 客户端复现（IsApplyingFire 防环，避免复现时再上行）。
/// 用 gunIndex 定位每门炮，主机权威快照 + 事件双通道。
/// </summary>
public static class ReloadSync
{
    private const float Interval = 0.2f;
    private static float _timer;
    /// <summary>本地装填动画/推进进行中判定窗口：此期间不写远端 stateIndex（防打断本地动画）。
    /// ⚠️ V1 死代码：全仓零引用，已注释。</summary>
    // private const float StateActiveWindow = 1.0f;

    /// <summary>客户端复现网络开火时的标志（RequestFire prefix 据此放行）。</summary>
    public static bool IsApplyingFire;

    private sealed class GunReload
    {
        public int Index;
        public ArtilleryReloadController Reload;
        public PowderChargeController Powder;
        // 主机已知状态（变化检测广播）
        public int HostState = -1;
        public int HostCharges = -1;
        // 客户端本地已知状态（变化检测上行）
        public int KnownState = -1;
        public int KnownCharges = -1;
        // 本地 currentStateIndex 最近变化时间（装填动画/推进进行中检测；动画期间不写远端索引防打断）
        // ⚠️ V1 死代码：只写不读（全仓无读取点），已注释。
        // public float LastStateChange;
        public bool Applying;
    }

    private static List<GunReload> _guns = new List<GunReload>();
    private static IntPtr _cachedTurret;
    // 中途加入待补发的成员（场景未就绪时挂起，Tick 里每轮重试直到发出去）
    private static readonly System.Collections.Generic.List<ulong> _pendingLateJoin = new();
    // 中途加入：客机是否已请求过补发快照（进入炮台场景后一次性，拉取全量对齐）
    private static bool _snapshotRequested;
    private static int _stateLog;
    private static int _pDiag;
    private static bool _statesDumped;
    private static bool _powderDumped;

    private static string PowderButtonName(PowderChargeController powder)
    {
        try
        {
            if (powder.loadChargesButton != null && powder.loadChargesButton.gameObject != null)
                return powder.loadChargesButton.gameObject.name ?? "";
        }
        catch { }
        return "";
    }

    public static void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;

        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined)
        {
            _pendingLateJoin.Clear(); // 会话结束，清掉挂起的快照
            _snapshotRequested = false; // 重置中途加入请求标志
            return;
        }

        // 补发中途加入快照（场景就绪后 ResolveGuns 才有值）
        if (net.IsHost && _pendingLateJoin.Count > 0)
        {
            for (int i = _pendingLateJoin.Count - 1; i >= 0; i--)
            {
                if (SendFullStateToNow(_pendingLateJoin[i]))
                    _pendingLateJoin.RemoveAt(i);
            }
        }

        var guns = ResolveGuns();
        if (guns.Count == 0) return;

        // 中途加入：客机真正进入炮台场景（ResolveGuns 非空）后，一次性请求主机补发全量快照，
        // 把任务内静止状态（装填/舱门/序列/标记/令牌/唱片机）对齐。太早请求（还在主菜单/加载中）
        // 会让 ReloadState 应用端 ResolveGuns 空而丢弃，故在场景就绪后请求。
        if (!net.IsHost && !_snapshotRequested)
        {
            _snapshotRequested = true;
            StateSnapshotSync.RequestSnapshot();
            CoopRuntime.LogSource?.LogInfo("[ReloadSync] turret scene ready, requesting full snapshot resend");
        }

        if (net.IsHost)
        {
            HostTick(net, guns);
        }
        else
        {
            ClientTick(net, guns);
        }
    }

    /// <summary>客户端本地开火被拦截时调用：把开火请求上行给主机。</summary>
    public static void SendFireRequest(GunController gun)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost || net.State != SessionState.Joined) return;
        int idx = IndexOf(gun);
        if (idx < 0) return;
        try
        {
            var w = NetProtocol.Begin(MsgType.FireRequest);
            w.Put((byte)idx);
            net.Transport.Send(net.HostSteamId, NetProtocol.Snapshot(w), true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReloadSync SendFireRequest: {ex.Message}"); }
    }

    /// <summary>应用远端快照期间的 EchoGuard：防环（应用产生的状态变化不再上行/广播）。
    /// ⚠️ V1 死代码：只写不读（全仓无读取点，实际防环由 IsApplyingAdvance 承担），已注释。</summary>
    // public static bool IsApplyingReloadState;
    /// <summary>应用远端推进事件期间的 EchoGuard：防环（推进产生的状态变化不再上行/广播）。</summary>
    public static bool IsApplyingAdvance;
    /// <summary>host 收到客户端上行后强制下一轮广播（让其他 client 也对齐）。</summary>
    private static bool _forceBroadcast;

    /// <summary>本地装填推进/回退被游戏调用（patch OnUserInput_Advance/Regress）→ 广播事件，
    /// 对端 AdvanceState/RegressState 同步“动作”驱动（拉杆/动画过程同步）。
    /// 同时 HostTick/ClientTick 轮询 currentStateIndex 做快照兜底（绝对值对齐，防跳步/漂移）。
    /// 双轨：事件=操作过程同步，快照=最终状态对齐。
    /// ⚠️ V1 死代码：全仓无调用者（Harmony 已不再 patch OnUserInput_Advance/Regress），孤儿方法，已注释。</summary>
    // public static void OnLocalAdvance(ArtilleryReloadController reload, int dir)
    // {
    //     if (IsApplyingAdvance) return; // 应用中的推进不转发（防环）
    //     var net = CoopRuntime.Net;
    //     if (net == null || reload == null) return;
    //     if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
    //     // 若刚通过 LookAtTarget.OnClickDown 广播了点击（该拉杆是 LookAtTarget），推进已随点击转发到对端
    //     // （对端 OnClickDown→UnityEvent/内部逻辑触发推进），这里跳过避免对端“点击推进 + AdvanceState”双推进跳步。
    //     if (Time.realtimeSinceStartup - ButtonClickSync.LastClickAt < 0.1f) return;
    //     int idx = IndexOfReload(reload);
    //     if (idx < 0) return;
    //     try
    //     {
    //         string st = "";
    //         try { st = reload.CurrentState != null ? reload.CurrentState.stateKey : ""; } catch { }
    //         CoopRuntime.LogSource?.LogInfo($"[ReloadSync] local advance dir={dir} idx={idx} state='{st}'");
    //         var w = NetProtocol.Begin(MsgType.ReloadAdvance);
    //         w.Put((byte)idx);
    //         w.Put((byte)(dir > 0 ? (byte)1 : (byte)2));
    //         var data = NetProtocol.Snapshot(w);
    //         if (net.IsHost) net.EnqueueBatch(data, true);
    //         else net.EnqueueBatch(data, false);
    //     }
    //     catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReloadSync OnLocalAdvance: {ex.Message}"); }
    // }

    /// <summary>收到远端装填推进事件 → 对端 AdvanceState/RegressState（动作同步，防环）。</summary>
    public static void OnAdvanceEvent(byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int idx = r.GetByte();
            int dir = r.GetByte();
            if (net.IsHost)
                foreach (var p in net.Roster)
                    if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            var guns = ResolveGuns();
            if (idx < 0 || idx >= guns.Count) return;
            var g = guns[idx];
            if (g.Reload == null) return;
            IsApplyingAdvance = true;
            try
            {
                string before = "";
                try { before = g.Reload.CurrentState != null ? g.Reload.CurrentState.stateKey : ""; } catch { }
                if (dir == 1) g.Reload.AdvanceState();
                else if (dir == 2) g.Reload.RegressState();
                string after = "";
                try { after = g.Reload.CurrentState != null ? g.Reload.CurrentState.stateKey : ""; } catch { }
                CoopRuntime.LogSource?.LogInfo($"[ReloadSync] recv advance idx={idx} dir={dir} '{before}' -> '{after}'");
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReloadSync OnAdvanceEvent apply: {ex.Message}"); }
            finally { IsApplyingAdvance = false; }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReloadSync OnAdvanceEvent: {ex.Message}"); }
    }

    private static int IndexOfReload(ArtilleryReloadController reload)
    {
        var guns = ResolveGuns();
        for (int i = 0; i < guns.Count; i++)
            if (guns[i].Reload != null && guns[i].Reload.Pointer == reload.Pointer) return i;
        return -1;
    }

    // ---------------- 发射药事件（选药量 / 投放发射药） ----------------

    public static bool IsApplyingPowder;

    /// <summary>本地选发射药量（Button Dispencer 点击，patch OnChargeButtonPressed）→ 广播。</summary>
    public static void OnLocalPowderSelect(PowderChargeController powder, int chargeIndex)
    {
        if (IsApplyingPowder || ButtonClickSync.IsApplyingClick)
        {
            if ((++_pDiag % 20) == 1)
                CoopRuntime.LogSource?.LogInfo($"[ReloadSync] powder select blocked appPowder={IsApplyingPowder} appClick={ButtonClickSync.IsApplyingClick}");
            return;
        }
        var net = CoopRuntime.Net;
        if (net == null || powder == null) return;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        int idx = IndexOfPowder(powder);
        if (idx < 0)
        {
            if ((++_pDiag % 20) == 1) CoopRuntime.LogSource?.LogInfo("[ReloadSync] powder select idx NOT FOUND");
            return;
        }
        try
        {
            var w = NetProtocol.Begin(MsgType.PowderEvent);
            w.Put((byte)idx);
            w.Put((byte)1); // 选药量
            w.Put((byte)Math.Max(chargeIndex, 0));
            var data = NetProtocol.Snapshot(w);
            if (net.IsHost) net.EnqueueBatch(data, true);
            else net.EnqueueBatch(data, false);
            CoopRuntime.LogSource?.LogInfo($"[ReloadSync] powder select idx={idx} charge={chargeIndex}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReloadSync OnLocalPowderSelect: {ex.Message}"); }
    }

    /// <summary>本地投放发射药（Charge Rammer 点击，patch OnLoadChargesPressed）→ 广播。</summary>
    public static void OnLocalPowderLoad(PowderChargeController powder)
    {
        if (IsApplyingPowder || ButtonClickSync.IsApplyingClick) return; // 应用远端时不转发（防环）
        var net = CoopRuntime.Net;
        if (net == null || powder == null) return;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        int idx = IndexOfPowder(powder);
        if (idx < 0) return;
        try
        {
            var w = NetProtocol.Begin(MsgType.PowderEvent);
            w.Put((byte)idx);
            w.Put((byte)2); // 投放发射药
            w.Put((byte)0);
            var data = NetProtocol.Snapshot(w);
            if (net.IsHost) net.EnqueueBatch(data, true);
            else net.EnqueueBatch(data, false);
            CoopRuntime.LogSource?.LogInfo($"[ReloadSync] powder load idx={idx}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReloadSync OnLocalPowderLoad: {ex.Message}"); }
    }

    /// <summary>收到远端发射药事件 → 对端模拟点击对应按钮（LookAtTarget.OnClickDown → 动画+逻辑完整复现，防环）。
    /// 选药量=Button Dispencer(N)；投放=loadChargesButton（Charge Rammer）。</summary>
    public static void OnPowderEvent(byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int idx = r.GetByte();
            int ev = r.GetByte();
            int chargeIndex = r.GetByte();
            if (net.IsHost)
                foreach (var p in net.Roster)
                    if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            var guns = ResolveGuns();
            if (idx < 0 || idx >= guns.Count) return;
            var g = guns[idx];
            if (g.Powder == null) return;
            IsApplyingPowder = true;
            try
            {
                if (ev == 1)
                {
                    var btn = FindDispencerButton(g.Powder, chargeIndex);
                    if (btn != null && btn.isActive) { btn.OnClickDown(); btn.OnClickUp(); }
                    else g.Powder.OnChargeButtonPressed(chargeIndex); // 兜底：直接触发逻辑
                    CoopRuntime.LogSource?.LogInfo($"[ReloadSync] powder select apply idx={idx} charge={chargeIndex} btn={(btn != null)}");
                }
                else if (ev == 2)
                {
                    var btn = g.Powder.loadChargesButton;
                    if (btn != null && btn.isActive) { btn.OnClickDown(); btn.OnClickUp(); }
                    else g.Powder.OnLoadChargesPressed(); // 兜底：直接触发投放
                    CoopRuntime.LogSource?.LogInfo($"[ReloadSync] powder load apply idx={idx} btn={(btn != null)}");
                }
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReloadSync OnPowderEvent apply: {ex.Message}"); }
            finally { IsApplyingPowder = false; }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReloadSync OnPowderEvent: {ex.Message}"); }
    }

    /// <summary>按名字找 Button Dispencer (N) 的 LookAtTarget（powder 子对象），用于模拟点击播放按钮动画。</summary>
    private static LookAtTarget FindDispencerButton(PowderChargeController powder, int chargeIndex)
    {
        try
        {
            string want = $"Button Dispencer ({chargeIndex + 1})";
            var pc = powder.transform;
            for (int i = 0; i < pc.childCount; i++)
            {
                var ct = pc.GetChild(i);
                if (ct == null || ct.name == null) continue;
                if (ct.name == want)
                {
                    var lat = ct.GetComponent<LookAtTarget>();
                    if (lat != null) return lat;
                }
            }
        }
        catch { }
        return null;
    }

    private static int IndexOfPowder(PowderChargeController powder)
    {
        var guns = ResolveGuns();
        for (int i = 0; i < guns.Count; i++)
            if (guns[i].Powder != null && guns[i].Powder.Pointer == powder.Pointer) return i;
        return -1;
    }
    /// <summary>主机收到客户端开火请求 → 对主机权威炮执行 RequestFire（FireShell postfix 会广播 GunFire）。</summary>
    public static void OnFireRequest(byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null || !net.IsHost) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int idx = r.GetByte();
            var turret = TurretController.Instance;
            if (turret == null || turret.guns == null) return;
            if (idx < 0 || idx >= turret.guns.Count) return;
            var gun = turret.guns[idx];
            if (gun != null) gun.RequestFire();
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReloadSync OnFireRequest: {ex.Message}"); }
    }

    /// <summary>主机收到客户端装填状态上行 → 应用 → 强制下一轮广播（让其他端对齐）。</summary>
    public static void OnCmd(byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null || !net.IsHost) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int idx = r.GetByte();
            int st = r.GetByte();
            int ch = r.GetByte();
            CoopRuntime.LogSource?.LogInfo($"[ReloadSync] host recv cmd idx={idx} st={st} ch={ch}");
            ApplySnapshot(idx, st, ch, false);
            _forceBroadcast = true;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReloadSync OnCmd: {ex.Message}"); }
    }

    /// <summary>客户端收到主机状态快照 → 应用（直接写索引，绝对值对齐）。</summary>
    public static void OnState(byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int n = r.GetByte();
            int applyState = r.GetByte(); // 0=常规（不写 stateIndex）；1=中途加入初始（SetState 安全设置）
            var guns = ResolveGuns();
            for (int i = 0; i < n; i++)
            {
                int idx = r.GetByte();
                int st = r.GetByte();
                int ch = r.GetByte();
                if (idx < 0 || idx >= guns.Count) continue;
                var g = guns[idx];
                g.Applying = true;
                try
                {
                    string before = "";
                    try { before = g.Reload != null && g.Reload.CurrentState != null ? g.Reload.CurrentState.stateKey : ""; } catch { }
                    ApplySnapshot(idx, st, ch, applyState != 0);
                    string after = "";
                    try { after = g.Reload != null && g.Reload.CurrentState != null ? g.Reload.CurrentState.stateKey : ""; } catch { }
                    CoopRuntime.LogSource?.LogInfo($"[ReloadSync] recv state idx={idx} st={st} ch={ch} applyState={applyState} '{before}' -> '{after}'");
                }
                finally { g.Applying = false; }
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReloadSync OnState: {ex.Message}"); }
    }

    // ---------------- 内部 ----------------

    /// <summary>中途加入：主机把当前装填状态（每炮 stateIndex + 药量）单播给新成员。
    /// 场景未就绪（ResolveGuns 空）时入队，Tick 里重试直到发出。</summary>
    public static void SendFullStateTo(ulong steamId)
    {
        if (steamId == 0) return;
        if (!SendFullStateToNow(steamId))
        {
            if (!_pendingLateJoin.Contains(steamId))
            {
                _pendingLateJoin.Add(steamId);
                CoopRuntime.LogSource?.LogInfo($"[ReloadSync] scene not ready, mid-join snapshot pending -> {steamId}");
            }
        }
    }

    /// <summary>真正发送装填状态快照。返回是否成功（场景/炮就绪）。</summary>
    private static bool SendFullStateToNow(ulong steamId)
    {
        var net = CoopRuntime.Net;
        if (net == null || !net.IsHost || steamId == 0) return false;
        var guns = ResolveGuns();
        if (guns.Count == 0) return false;
        try
        {
            var w = NetProtocol.Begin(MsgType.ReloadState);
            w.Put((byte)guns.Count);
            w.Put((byte)1); // applyState 标志：1=中途加入需设置 stateIndex（SetState 安全设置）
            for (int i = 0; i < guns.Count; i++)
            {
                var g = guns[i];
                int st = g.Reload != null ? g.Reload.currentStateIndex : 0;
                int ch = g.Powder != null ? g.Powder.currentSelectedCharges : 0;
                w.Put((byte)g.Index);
                w.Put((byte)Math.Max(st, 0));
                w.Put((byte)Math.Max(ch, 0));
            }
            net.Transport.Send(steamId, NetProtocol.Snapshot(w), true);
            CoopRuntime.LogSource?.LogInfo($"[ReloadSync] full state → {steamId} guns={guns.Count}");
            return true;
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReloadSync SendFullStateToNow: {ex.Message}"); }
        return false;
    }

    private static void HostTick(NetManager net, List<GunReload> guns)
    {
        var w = NetProtocol.Begin(MsgType.ReloadState);
        w.Put((byte)guns.Count);
        w.Put((byte)0); // applyState 标志：0=常规广播（不写 stateIndex，防自动推进）
        bool anyChanged = _forceBroadcast;
        for (int i = 0; i < guns.Count; i++)
        {
            var g = guns[i];
            int st = g.Reload != null ? g.Reload.currentStateIndex : -1;
            int ch = g.Powder != null ? g.Powder.currentSelectedCharges : -1;
            if (st != g.HostState) { /* g.LastStateChange = Time.realtimeSinceStartup; V1 死代码（只写不读，已注释） */ } // 本地装填状态变化（动画推进中）
            w.Put((byte)g.Index);
            w.Put((byte)Math.Max(st, 0));
            w.Put((byte)Math.Max(ch, 0));
            if (st != g.HostState || ch != g.HostCharges) anyChanged = true;
            g.HostState = st;
            g.HostCharges = ch;
        }
        _forceBroadcast = false;
        if (!anyChanged) return;
        var data = NetProtocol.Snapshot(w);
        net.EnqueueBatch(data, true);
        if ((++_stateLog % 30) == 1)
            CoopRuntime.LogSource?.LogInfo($"[ReloadSync] host broadcast st/ch guns={guns.Count}");
    }

    private static void ClientTick(NetManager net, List<GunReload> guns)
    {
        for (int i = 0; i < guns.Count; i++)
        {
            var g = guns[i];
            if (g.Applying || IsApplyingAdvance) continue; // EchoGuard：应用远端快照/推进时不再上行
            int st = g.Reload != null ? g.Reload.currentStateIndex : -1;
            int ch = g.Powder != null ? g.Powder.currentSelectedCharges : -1;
            if (st != g.KnownState) { /* g.LastStateChange = Time.realtimeSinceStartup; V1 死代码（只写不读，已注释） */ } // 本地装填状态变化（动画推进中）
            if (st == g.KnownState && ch == g.KnownCharges) continue;
            g.KnownState = st;
            g.KnownCharges = ch;
            try
            {
                var w = NetProtocol.Begin(MsgType.ReloadCmd);
                w.Put((byte)g.Index);
                w.Put((byte)Math.Max(st, 0));
                w.Put((byte)Math.Max(ch, 0));
                net.EnqueueBatch(NetProtocol.Snapshot(w), false);
                CoopRuntime.LogSource?.LogInfo($"[ReloadSync] client up idx={g.Index} st={st} ch={ch}");
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReloadSync ClientTick: {ex.Message}"); }
        }
    }

    /// <summary>应用装填快照：同步药量（currentSelectedCharges）。
    /// ⚠️ 常规广播不写 currentStateIndex——游戏检测到 stateIndex 变化会触发状态机自动推进（自动上膛、锁膛后退膛/重置）。
    /// 装填状态完全靠事件驱动（OnClickDown 拉杆/按钮 + powder 选药量/投放 转发，对端执行操作自然推进），两端行为一致。
    /// 中途加入（applyState=true）例外：新成员需初始对齐，用游戏公开 SetState(index, force) 安全设置（不触发自动推进）+ 更新按钮 active。</summary>
    private static void ApplySnapshot(int idx, int stateIndex, int charges, bool applyState)
    {
        var guns = ResolveGuns();
        if (idx < 0 || idx >= guns.Count) return;
        var g = guns[idx];
        // IsApplyingReloadState = true; // V1 死代码（只写不读，已注释）
        try
        {
            if (g.Powder != null && g.Powder.currentSelectedCharges != charges)
            {
                try { g.Powder.currentSelectedCharges = charges; }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReloadSync ApplySnapshot charges: {ex.Message}"); }
            }
            if (applyState && g.Reload != null)
            {
                try
                {
                    if (g.Reload.CurrentStateIndex != stateIndex)
                    {
                        g.Reload.SetState(stateIndex, true); // force：跳过自动推进检查，安全对齐
                        try { g.Reload.UpdateAllAdvanceButtons(); } catch { } // 刷新按钮 active 表现
                        CoopRuntime.LogSource?.LogInfo($"[ReloadSync] mid-join SetState idx={idx} -> {stateIndex}");
                    }
                }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReloadSync ApplySnapshot SetState: {ex.Message}"); }
            }
        }
        finally { /* IsApplyingReloadState = false; */ }
    }

    private static int IndexOf(GunController gun)
    {
        var turret = TurretController.Instance;
        if (turret == null || turret.guns == null || gun == null) return -1;
        for (int i = 0; i < turret.guns.Count; i++)
        {
            var g = turret.guns[i];
            if (g != null && g.Pointer == gun.Pointer) return i;
        }
        return -1;
    }

    private static List<GunReload> ResolveGuns()
    {
        var turret = TurretController.Instance;
        if (turret == null)
        {
            _guns = new List<GunReload>();
            _cachedTurret = IntPtr.Zero;
            return _guns;
        }
        if (_guns.Count > 0 && _cachedTurret == turret.Pointer) return _guns;

        _cachedTurret = turret.Pointer;
        _guns = new List<GunReload>();
        try
        {
            var powders = UnityEngine.Resources.FindObjectsOfTypeAll<PowderChargeController>();
            for (int i = 0; i < turret.guns.Count; i++)
            {
                var gun = turret.guns[i];
                if (gun == null) continue;
                var reload = gun.artilleryReloadController;
                if (reload == null) continue;
                PowderChargeController powder = null;
                if (powders != null)
                    foreach (var pc in powders)
                    {
                        if (pc == null || pc.reloadController == null) continue;
                        if (pc.reloadController.Pointer == reload.Pointer) { powder = pc; break; }
                    }
                var gr = new GunReload { Index = i, Reload = reload, Powder = powder };
                _guns.Add(gr);
                // 一次性诊断：打印装填步骤（key + displayName + 推进按钮），理解装药/锁膛拉杆
                if (!_statesDumped && reload.reloadStates != null)
                {
                    for (int si = 0; si < reload.reloadStates.Count; si++)
                    {
                        var s = reload.reloadStates[si];
                        if (s == null) continue;
                        string btn = "";
                        try { if (s.advanceButton != null && s.advanceButton.gameObject != null) btn = s.advanceButton.gameObject.name; } catch { }
                        CoopRuntime.LogSource?.LogInfo($"[ReloadSync] gun{i} state[{si}] key='{s.stateKey}' display='{s.displayName}' btn='{btn}'");
                    }
                }
                // 一次性诊断：打印 PowderChargeController 子对象（定位装发射药拉杆/按钮组件）
                if (!_powderDumped && powder != null)
                {
                    var pc = powder.transform;
                    for (int ci = 0; ci < pc.childCount; ci++)
                    {
                        var ct = pc.GetChild(ci);
                        if (ct == null) continue;
                        string comps = "";
                        try
                        {
                            var cs = ct.GetComponents<Component>();
                            for (int cj = 0; cj < cs.Length && cj < 6; cj++)
                            {
                                string tn = "?";
                                try { tn = cs[cj].GetIl2CppType().FullName; } catch { }
                                comps += (comps.Length > 0 ? "," : "") + tn;
                            }
                        }
                        catch (Exception ex) { comps = "ERR:" + ex.Message; }
                        CoopRuntime.LogSource?.LogInfo($"[ReloadSync] powder child '{ct.name}' comps=[{comps}]");
                    }
                    CoopRuntime.LogSource?.LogInfo($"[ReloadSync] powder loadChargesButton='{PowderButtonName(powder)}'");
                }
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReloadSync ResolveGuns: {ex.Message}"); }
        _statesDumped = true;
        _powderDumped = true;
        return _guns;
    }
}
