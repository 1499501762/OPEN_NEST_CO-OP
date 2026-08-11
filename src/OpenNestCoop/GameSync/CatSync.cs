using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 猫同步 v2（主机 AI 决策软同步 + 交互事件软同步 + 位置偏差硬同步，MsgType=106/133）。
///
/// 设计（用户方案：主机 AI 权威产生决策 → 软同步给客机 AI → 各自执行）：
/// - 主机权威 AI：主机的猫 AI 正常跑（唯一决策源，产生随机操作/运动指令/目标点），
///   每 1/3s 广播每只猫的 AI 决策（CatState + NavMesh 目标点 + 权威位置）。
/// - 客机软同步执行：收到主机 AI 决策 → SetDestination(目标点) 让本地 NavMesh 走同一目标，
///   两端路径一致、位置自然接近。客机 AI 决策不暂停（活动动画正常），目标点由主机权威覆盖。
/// - 交互事件软同步（MsgType=133）：玩家操作（拾起/放下/驱赶/抚摸/打断）→ 谁操作谁发 →
///   对端执行相同公开方法（StartCarrying/StopCarrying/ShooCat/PetTheCat/InterruptCat）。
/// - 位置硬同步：客机每 1/3s 检查本地猫位置 vs 主机权威位置，偏差 &gt; 阈值(1m) →
///   Teleport 对齐（兜底）；偏差小 → 靠软同步的目标点自然收敛，不抽搐。
/// </summary>
public sealed class CatSync : ISyncedModule
{
    public byte MsgType => 106;
    /// <summary>玩家-猫交互事件消息类型（同模块处理，见 CoopSyncRegistry.RegisterModule 附加类型）。</summary>
    public const byte CatEventMsgType = 133;

    private const float Interval = 1f / 3f;      // AI 状态心跳（用户指定 1/3 秒）
    private const float HardSyncDist = 1.0f;      // 位置偏差硬同步阈值（米）
    private const int MaxCats = 32;
    private float _timer;
    private int _sendLog;
    private int _recvLog;

    /// <summary>应用远端交互事件时的防环标志（Harmony patch 据此不重复上报）。</summary>
    public static bool IsApplyingCat;

    // 客机：本地猫 -> 最近主机 AI 状态（位置偏差检测）
    private sealed class HostState { public Vector3 Pos; public float Yaw; public int State; public Vector3 Dest; public bool HasDest; }
    private readonly Dictionary<CatController, HostState> _hostStates = new();

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;

        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;

        if (net.IsHost)
        {
            HostBroadcastState(net);
            // 主机也要处理客机上行的"被客机拾取的猫位置"（在 OnPacket 里应用）
        }
        else
        {
            ClientCheckHardSync(net);
            ClientSendHeldUp(net); // 客机拾起的猫位置上行（玩家持有的表现位置，非 AI 信息）
        }
    }

    // ---------------- 主机：AI 状态广播（每 1/3s） ----------------

    private void HostBroadcastState(NetManager net)
    {
        try
        {
            var cats = UnityEngine.Object.FindObjectsOfType<CatController>();
            if (cats == null || cats.Length == 0)
            {
                if ((++_sendLog % 30) == 1)
                    CoopRuntime.LogSource?.LogInfo("[CatSync] 未找到 CatController");
                return;
            }
            int n = Math.Min(cats.Length, MaxCats);
            var w = NetProtocol.Begin((MsgType)MsgType);
            w.Put((byte)n);
            for (int i = 0; i < n; i++)
            {
                var c = cats[i];
                if (c == null) { PutEmpty(w, i); continue; }
                var p = c.transform.position;
                Vector3 dest = c.transform.position;
                try { dest = c._movement._Agent.destination; } catch { }
                // 活动计时器：主机状态机进度（剩余时长）——客机同步它就能在"同一时刻"切换状态，
                // 移动/休息动作自然同步（软同步，不强制改 _currentState 避免闪烁）。
                float actTimer = 0f, actDur = 0f, afterLoop = 0f;
                bool isLoop = false;
                string loopEnd = "";
                try { actTimer = c._activityTimer; } catch { }
                try { actDur = c._currentActivityDuration; } catch { }
                // 活动类型标识：PerformingActivity 有多种随机活动（趴下/梳理/玩耍等），
                // 由 PickRandomActivity 用 Random 选——两端各自选会不同。同步 loopEndTrigger +
                // _isLoopingActivity + _afterLoopActivityDuration，客机状态机据此播放同一活动。
                try { isLoop = c._isLoopingActivity; } catch { }
                try { loopEnd = c.loopEndTrigger ?? ""; } catch { }
                try { afterLoop = c._afterLoopActivityDuration; } catch { }
                // 活动动画状态：主机 animator 当前播放的动画（shortNameHash + normalizedTime）。
                // 趴下/梳理等具体活动由 animator 状态决定——客机 Play 同一状态 → 动作一致。
                int animHash = 0;
                float animTime = 0f;
                try
                {
                    var aa = c._agentAnimation;
                    if (aa != null)
                    {
                        var anim = aa._animator;
                        if (anim != null)
                        {
                            var si = anim.GetCurrentAnimatorStateInfo(0);
                            animHash = (int)si.shortNameHash;
                            animTime = si.normalizedTime;
                        }
                    }
                }
                catch { }
                w.Put((byte)i);
                w.Put((byte)c.CurrentState);
                w.Put(dest.x); w.Put(dest.y); w.Put(dest.z);
                w.Put(p.x); w.Put(p.y); w.Put(p.z);
                w.Put(c.transform.eulerAngles.y);
                w.Put(actTimer);
                w.Put(actDur);
                w.Put(isLoop ? (byte)1 : (byte)0);
                w.Put(afterLoop);
                w.Put(loopEnd ?? "");
                w.Put(animHash);
                w.Put(animTime);
            }
            var data = NetProtocol.Snapshot(w);
            // 可靠直发兜底（猫数量少，确保不被 Batch 挤掉）
            try
            {
                foreach (var p in net.Roster)
                    if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
            catch { }
            if ((++_sendLog % 10) == 1)
                CoopRuntime.LogSource?.LogInfo($"[CatSync] host AI state n={n}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CatSync HostBroadcastState: {ex.Message}"); }
    }

    private static void PutEmpty(NetDataWriter w, int idx)
    {
        w.Put((byte)idx); w.Put((byte)0);
        w.Put(0f); w.Put(0f); w.Put(0f);
        w.Put(0f); w.Put(0f); w.Put(0f);
        w.Put(0f);
        w.Put(0f); w.Put(0f);
        w.Put((byte)0); w.Put(0f); w.Put("");
        w.Put(0); w.Put(0f);
    }

    // ---------------- 客机：本地 AI 跑 + 位置偏差硬同步 ----------------

    /// <summary>客机每 1/3s：检查本地猫位置 vs 主机权威位置，偏差大 → Teleport 硬同步。
    /// 本地 AI 不暂停（表现自然）；只对偏差大的猫硬对齐。</summary>
    private void ClientCheckHardSync(NetManager net)
    {
        if (_hostStates.Count == 0) return;
        List<CatController> gone = null;
        foreach (var kv in _hostStates)
        {
            var c = kv.Key;
            if (c == null) { (gone ??= new List<CatController>()).Add(kv.Key); continue; }
            var hs = kv.Value;
            // 本端正在拾取该猫：位置由本地控制，不硬同步
            if (IsCatHeld(c)) continue;
            // 猫被抱着（state=4 Carried，任意端持有）：位置由持有端控制（held 上行），
            // 硬同步追不上持有者移动 → 每 1/3s warp 抽搐。跳过——放下后（state 变 1/2）
            // 由软同步 SetDestination + 放下位置对齐恢复，不再反复拉回。
            if (hs.State == 4) continue;
            float dist = Vector3.Distance(c.transform.position, hs.Pos);
            if (dist > HardSyncDist)
            {
                try
                {
                    // 硬同步：用 NavMeshAgent.Warp 移动（正确更新 agent 路径，防止 agent 拉回），
                    // 并重设目标=当前位置（state<=2 时 agent 每帧向 dest 寻路，若只用 transform.position
                    // 会瞬间被 agent 拉回 → 每 1/3s 反复 teleport → 抽搐）。
                    bool warped = false;
                    try
                    {
                        var agent = c._movement._Agent;
                        if (agent != null)
                        {
                            agent.Warp(hs.Pos);
                            warped = true;
                        }
                    }
                    catch { }
                    if (!warped)
                    {
                        c.transform.position = hs.Pos;
                        c.transform.rotation = Quaternion.Euler(0f, hs.Yaw, 0f);
                    }
                    // 重置寻路目标为当前权威位置（防 agent 走回旧目标 → 反复拉回抽搐）
                    try
                    {
                        var mover = c._movement;
                        if (mover != null) mover.SetDestination(hs.Pos);
                    }
                    catch { }
                    CoopRuntime.LogSource?.LogInfo($"[CatSync] 硬同步 warp dist={dist:0.00} state={(int)c.CurrentState} cat='{c.gameObject?.name}'");
                }
                catch { }
            }
        }
        if (gone != null)
            foreach (var g in gone) _hostStates.Remove(g);
    }

    /// <summary>客机：本端玩家拾起/拖动的猫位置上行给主机（玩家持有的表现位置，非 AI 产生的信息）。
    /// 主机应用到本地并广播 → 其他端看到猫在客机玩家手里。</summary>
    private void ClientSendHeldUp(NetManager net)
    {
        try
        {
            var cats = UnityEngine.Object.FindObjectsOfType<CatController>();
            if (cats == null || cats.Length == 0) return;
            // 只收集被本端玩家拾取的猫（可变数量）
            var held = new List<(int idx, CatController c)>();
            for (int i = 0; i < cats.Length && i < MaxCats; i++)
            {
                var c = cats[i];
                if (c == null || !IsCatHeld(c)) continue;
                held.Add((i, c));
            }
            if (held.Count == 0) return;
            var w = NetProtocol.Begin((MsgType)MsgType);
            w.Put((byte)held.Count); // 注意：此处 count 是 held 数量，不是猫总数——用 0xFF 前缀区分？
            w.Put((byte)0xFF); // 标记：可变 held 列表（区别于主机 AI 广播的固定 n）
            for (int k = 0; k < held.Count; k++)
            {
                var (idx, c) = held[k];
                var p = c.transform.position;
                w.Put((byte)idx);
                w.Put((byte)c.CurrentState);
                w.Put(p.x); w.Put(p.y); w.Put(p.z);   // dest
                w.Put(p.x); w.Put(p.y); w.Put(p.z);   // pos
                w.Put(c.transform.eulerAngles.y);
            }
            net.Transport.Send(net.HostSteamId, NetProtocol.Snapshot(w), true);
            if ((++_sendLog % 10) == 1)
                CoopRuntime.LogSource?.LogInfo("[CatSync] client held cat up");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CatSync ClientSendHeldUp: {ex.Message}"); }
    }

    // ---------------- OnPacket：106 AI 状态 / 133 交互事件 ----------------

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            byte type = r.GetByte();
            if (type == CatEventMsgType)
            {
                OnCatEventPacket(from, r);
                return;
            }
            // 106：AI 状态（主机->客机，固定 n）或 held 位置（客机->主机/转发，count + 0xFF 标记）
            int n = r.GetByte();
            var cats = UnityEngine.Object.FindObjectsOfType<CatController>();
            // 检查是否 held 列表（第二字节是 0xFF 标记）
            bool isHeld = r.AvailableBytes > 0 && r.RawData[r.Position] == 0xFF;
            if (isHeld)
            {
                r.GetByte(); // 0xFF 标记
                HostApplyHeldFrom(from, n, r, cats, data);
                return;
            }
            for (int i = 0; i < n; i++)
                {
                    byte idx = r.GetByte();
                    byte state = r.GetByte();
                    float dx = r.GetFloat(); float dy = r.GetFloat(); float dz = r.GetFloat();
                    float px = r.GetFloat(); float py = r.GetFloat(); float pz = r.GetFloat();
                    float yaw = r.GetFloat();
                    // 活动计时器 + 活动类型 + 活动动画（主机 AI 广播附加字段，与 held 列表格式不同——held 无此字段）
                    float actTimer = 0f, actDur = 0f, afterLoop = 0f;
                    bool isLoop = false;
                    string loopEnd = "";
                    int animHash = 0;
                    float animTime = 0f;
                    if (r.AvailableBytes >= 8)
                    {
                        actTimer = r.GetFloat();
                        actDur = r.GetFloat();
                        if (r.AvailableBytes >= 1) isLoop = r.GetByte() != 0;
                        if (r.AvailableBytes >= 4) afterLoop = r.GetFloat();
                        if (r.AvailableBytes >= 1) loopEnd = r.GetString();
                        if (r.AvailableBytes >= 4) animHash = r.GetInt();
                        if (r.AvailableBytes >= 4) animTime = r.GetFloat();
                    }
                    if (cats == null || idx >= cats.Length || cats[idx] == null) continue;
                    var c = cats[idx];
                    // 本端正在拾取该猫：位置由本地控制，跳过
                    if (IsCatHeld(c)) continue;
                    // 软同步：主机 AI 权威决策（状态 + 目标点 + 活动计时器 + 活动类型 + 活动动画）→ 客机本地执行同一决策。
                    // 客机 SetDestination 走同一目标 + 同步活动计时器/类型/动画 → 两端路径/切换时机/活动一致。
                    ApplyHostAI(c, state, new Vector3(dx, dy, dz), actTimer, actDur, isLoop, afterLoop, loopEnd, animHash, animTime);
                    if (_hostStates.TryGetValue(c, out var hs))
                    {
                        hs.Pos = new Vector3(px, py, pz); hs.Yaw = yaw; hs.State = state;
                        hs.Dest = new Vector3(dx, dy, dz); hs.HasDest = true;
                    }
                    else
                        _hostStates[c] = new HostState { Pos = new Vector3(px, py, pz), Yaw = yaw, State = state, Dest = new Vector3(dx, dy, dz), HasDest = true };
                }
            if ((++_recvLog % 10) == 1)
                CoopRuntime.LogSource?.LogInfo($"[CatSync] guest recv AI state n={n} tracked={_hostStates.Count}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CatSync OnPacket: {ex.Message}"); }
    }

    /// <summary>软同步：主机 AI 权威决策 → 客机本地执行。
    /// 主机每 1/3s 广播每只猫的 AI 决策（CatState + NavMesh 目标点 + 活动计时器 + 活动动画）；客机收到后：
    /// 1) 同步主机的活动计时器（_activityTimer/_currentActivityDuration）——客机状态机
    ///    Update 自驱（HandleIdleState/HandleWalkingState/HandleActivityState），只需让它的
    ///    "剩余活动时间"与主机一致，切换时机就自然同步 → 移动/休息动作两端一致。
    /// 2) 状态变化 → 覆盖 _currentState（移动同步关键）；PerformingActivity 时同步活动动画
    ///    （animator shortNameHash + normalizedTime）——趴下/梳理等具体活动由 animator 状态决定，
    ///    客机 Play 主机同一状态 → 动作一致（不只位置一致）。
    /// 3) SetDestination(dest) 让本地 NavMesh 走向主机选定的同一目标 → 两端路径一致、位置自然接近。
    /// 位置硬同步（Teleport）只在意外偏差 &gt;1m 时兜底。</summary>
    private static void ApplyHostAI(CatController c, byte state, Vector3 dest, float actTimer, float actDur,
                                    bool isLoop, float afterLoop, string loopEnd, int animHash, float animTime)
    {
        if (c == null) return;
        try
        {
            if (state <= 2)
            {
                try
                {
                    var cur = (int)c.CurrentState;
                    // 1a) 状态变化 → 强制覆盖 _currentState：让客机状态机进入主机同一状态。
                    //     移动同步的关键——主机切到 WalkingToSpot 时，客机也必须切到 WalkingToSpot
                    //     并 SetDestination，否则客机仍处 Idle 不会启动移动。
                    //     只在状态真正不同时覆盖（避免每 1/3s 刷 setter 干扰 → 不闪烁）。
                    if (cur != state)
                    {
                        try { c._currentState = (CatState)state; } catch { }
                    }
                    // 1b) 同步活动计时器：让客机状态机在"同一剩余时间"推进（主机权威决策时间轴）。
                    //     状态一致时同步计时器 → 切换时刻一致（休息/移动时长两端一致）。
                    try { c._currentActivityDuration = actDur; } catch { }
                    try { c._activityTimer = actTimer; } catch { }
                    // 1c) 同步主机的动画状态（animator 当前状态）：走路（state=1）和活动（state=2）都同步。
                    //     趴下/梳理/玩耍等具体活动由 animator 状态决定（PickRandomActivity 用全局
                    //     Random 选活动 + AgentAnimation 播放），两端各自选会不同——直接 Play 主机
                    //     同一动画状态 → 动作一致。走路动画也同步（主机走→客机也切 Walk，
                    //     避免客机本地 AI 用不同走路动画 → 走路表现不一致）。
                    if (state >= 1 && animHash != 0)
                    {
                        try
                        {
                            var aa = c._agentAnimation;
                            if (aa != null)
                            {
                                var anim = aa._animator;
                                if (anim != null)
                                {
                                    var curAnim = anim.GetCurrentAnimatorStateInfo(0);
                                    if ((int)curAnim.shortNameHash != animHash)
                                    {
                                        anim.Play(animHash, 0, animTime);
                                        CoopRuntime.LogSource?.LogInfo($"[CatSync] apply activity anim hash={animHash} t={animTime:0.00}");
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            // 2) 运动指令：走向主机选定的目标点（本地 NavMesh 寻路执行，平滑不抽搐）。
            //    WalkingToSpot 时有效；活动/被持有等状态 dest=当前位置，SetDestination 无副作用。
            var mover = c._movement;
            if (mover != null)
            {
                try { mover.SetDestination(dest); } catch { }
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CatSync ApplyHostAI: {ex.Message}"); }
    }

    /// <summary>应用 held 猫位置列表（客机持有表现）。主机：应用到本地 + 转发其他客机；
    /// 其他客机：更新 _hostStates（位置由客机持有者控制）。格式：count + 0xFF + [idx][state][dest3][pos3][yaw]*。</summary>
    private void HostApplyHeldFrom(ulong from, int n, NetDataReader r, CatController[] cats, byte[] originalData)
    {
        try
        {
            var net = CoopRuntime.Net;
            bool isHost = net != null && net.IsHost;
            for (int i = 0; i < n; i++)
            {
                byte idx = r.GetByte();
                byte state = r.GetByte();
                float dx = r.GetFloat(); float dy = r.GetFloat(); float dz = r.GetFloat();
                float px = r.GetFloat(); float py = r.GetFloat(); float pz = r.GetFloat();
                float yaw = r.GetFloat();
                if (cats == null || idx >= cats.Length || cats[idx] == null) continue;
                var c = cats[idx];
                // 本端正在拾取该猫：位置由本地控制，跳过
                if (IsCatHeld(c)) continue;
                if (isHost)
                {
                    try
                    {
                        c.transform.position = new Vector3(px, py, pz);
                        c.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                        // 客机持有该猫 → 暂停主机本地 AI（位置由客机控制）
                        try { c.PauseBehavior(true); } catch { }
                    }
                    catch { }
                }
                else
                {
                    // 第三端客机：记录 held 猫的权威位置（硬同步对齐）
                    if (_hostStates.TryGetValue(c, out var hs))
                    {
                        hs.Pos = new Vector3(px, py, pz); hs.Yaw = yaw; hs.State = state;
                    }
                    else
                        _hostStates[c] = new HostState { Pos = new Vector3(px, py, pz), Yaw = yaw, State = state, Dest = new Vector3(px, py, pz), HasDest = true };
                }
            }
            // 主机转发给其他客机（让第三端也看到猫在客机手里）
            if (isHost && net != null)
                foreach (var p in net.Roster)
                    if (!p.IsLocal && (ulong)p.SteamId != from)
                        try { net.Transport.Send(p.SteamId, originalData, true); } catch { }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CatSync HostApplyHeldFrom: {ex.Message}"); }
    }

    // ---------------- 交互事件（MsgType=133）：谁操作谁发 ----------------

    /// <summary>本地玩家-猫交互（Harmony patch 调用）→ 广播。ev: 1=拾起 2=放下 3=驱赶 4=抚摸 5=打断。</summary>
    public static void OnLocalCatEvent(CatController cat, byte ev)
    {
        if (IsApplyingCat) return; // 应用远端事件时不重复上报（防环）
        var net = CoopRuntime.Net;
        if (net == null || cat == null) return;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        int idx = IndexOf(cat);
        if (idx < 0) return;
        try
        {
            var p = cat.transform.position;
            var w = NetProtocol.Begin((MsgType)CatEventMsgType);
            w.Put((byte)idx);
            w.Put(ev);
            w.Put(p.x); w.Put(p.y); w.Put(p.z);
            w.Put(cat.transform.eulerAngles.y);
            var data = NetProtocol.Snapshot(w);
            if (net.IsHost)
            {
                foreach (var p2 in net.Roster)
                    if (!p2.IsLocal) net.Transport.Send(p2.SteamId, data, true);
            }
            else if (net.HostSteamId != 0)
            {
                net.Transport.Send(net.HostSteamId, data, true);
            }
            CoopRuntime.LogSource?.LogInfo($"[CatSync] cat event ev={ev} idx={idx} host={net.IsHost}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CatSync OnLocalCatEvent: {ex.Message}"); }
    }

    private void OnCatEventPacket(ulong from, NetDataReader r)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            int idx = r.GetByte();
            byte ev = r.GetByte();
            float px = r.GetFloat(); float py = r.GetFloat(); float pz = r.GetFloat();
            float yaw = r.GetFloat();
            // 主机转发给其他客机（星型）
            if (net.IsHost)
            {
                var fwd = NetProtocol.Begin((MsgType)CatEventMsgType);
                fwd.Put((byte)idx); fwd.Put(ev);
                fwd.Put(px); fwd.Put(py); fwd.Put(pz); fwd.Put(yaw);
                var fd = NetProtocol.Snapshot(fwd);
                foreach (var p in net.Roster)
                    if (!p.IsLocal && (ulong)p.SteamId != from)
                        net.Transport.Send(p.SteamId, fd, true);
            }
            // 对端执行相同交互
            var cats = UnityEngine.Object.FindObjectsOfType<CatController>();
            if (cats == null || idx < 0 || idx >= cats.Length || cats[idx] == null) return;
            var c = cats[idx];
            ApplyCatEvent(c, ev, new Vector3(px, py, pz), yaw);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CatSync OnCatEventPacket: {ex.Message}"); }
    }

    /// <summary>对端执行交互事件（防环：执行期间 IsApplyingCat=true）。</summary>
    private static void ApplyCatEvent(CatController c, byte ev, Vector3 pos, float yaw)
    {
        IsApplyingCat = true;
        try
        {
            switch (ev)
            {
                case 1: // 拾起
                    try { c.StartCarrying(); } catch { }
                    try { c.transform.position = pos; c.transform.rotation = Quaternion.Euler(0f, yaw, 0f); } catch { }
                    CoopRuntime.LogSource?.LogInfo("[CatSync] apply 拾起");
                    break;
                case 2: // 放下
                    try { c.StopCarrying(); } catch { }
                    try { c.transform.position = pos; c.transform.rotation = Quaternion.Euler(0f, yaw, 0f); } catch { }
                    CoopRuntime.LogSource?.LogInfo("[CatSync] apply 放下");
                    break;
                case 3: // 驱赶
                    try { c.ShooCat(false); } catch { }
                    CoopRuntime.LogSource?.LogInfo("[CatSync] apply 驱赶");
                    break;
                case 4: // 抚摸
                    try { c.PetTheCat(); } catch { }
                    CoopRuntime.LogSource?.LogInfo("[CatSync] apply 抚摸");
                    break;
                case 5: // 打断
                    try { c.InterruptCat(); } catch { }
                    CoopRuntime.LogSource?.LogInfo("[CatSync] apply 打断");
                    break;
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CatSync ApplyCatEvent: {ex.Message}"); }
        finally { IsApplyingCat = false; }
    }

    // ---------------- 工具 ----------------

    private static int IndexOf(CatController target)
    {
        try
        {
            var cats = UnityEngine.Object.FindObjectsOfType<CatController>();
            if (cats == null) return -1;
            for (int i = 0; i < cats.Length; i++)
                if (cats[i] != null && cats[i] == target) return i;
        }
        catch { }
        return -1;
    }

    /// <summary>猫是否正被本端玩家拾取/拖动（交互时位置由本地控制）。</summary>
    private static bool IsCatHeld(CatController c)
    {
        try
        {
            var h = UnityEngine.Object.FindFirstObjectByType<CatPickUpHandler>();
            if (h != null && h.heldCat != null && h.heldCat.gameObject == c.gameObject) return true;
            var d = c.GetComponent<DraggableItem>();
            if (d != null && d.IsBeingDragged) return true;
        }
        catch { }
        return false;
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _timer = 0f; _hostStates.Clear(); }
}
