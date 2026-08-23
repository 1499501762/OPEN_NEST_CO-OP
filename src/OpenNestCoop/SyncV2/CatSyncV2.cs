using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 猫同步 v2（CatSyncV2，MsgType=207）。M7：把 V1 <c>CatSync</c> 迁入分层架构。
/// - 状态（207，<see cref="V2Authority.Host"/>）：主机 AI 权威决策（CatState + NavMesh 目标点 + 权威位置
///   + 活动计时器/类型/动画），每 1/3s 广播；客机软同步执行（SetDestination 走同一目标 + 同步计时器/动画），
///   位置偏差 &gt;1m 硬同步 Warp 兜底；客机拾起的猫位置 held 上行（0xFF 标记）。
/// - 交互事件（经 EventLayer，<see cref="V2Authority.Operator"/>）：谁操作谁发（拾起/放下/驱赶/抚摸/打断），
///   对端执行 StartCarrying/StopCarrying/ShooCat/PetTheCat/InterruptCat，<see cref="IsApplyingCat"/> 防环。
/// - 吸收 A2：猫实例缓存 + 场景切换即时刷新 + 3s 兜底（去高频 FindObjectsOfType）。
/// </summary>
public sealed class CatSyncV2 : ISyncedModule
{
    public static CatSyncV2 Instance { get; } = new CatSyncV2();

    private CatSyncV2()
    {
        // 交互事件 → EventLayer（Operator 权威：谁操作谁发，对端复现）
        EventLayer.Instance.Register(CatEventId, V2Authority.Operator, ReproduceCatEvent);
    }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2Cat;

    /// <summary>交互事件 id（EventLayer 通道）。</summary>
    public const string CatEventId = "v2/cat/event";

    /// <summary>应用远端交互事件时的防环标志（Harmony patch 据此不重复上报）。</summary>
    public static bool IsApplyingCat;

    private const float Interval = 1f / 3f;
    private const float HardSyncDist = 1.0f;
    private const int MaxCats = 32;
    private float _timer;
    private int _sendLog, _recvLog;

    // 猫实例缓存（A2：场景切换即时刷新 + 3s 兜底；顺序 = FindObjectsOfType，跨端索引一致依赖）
    private static CatController[] _catCache;
    private static float _catCacheTimer;
    private static int _lastSceneIdx = int.MinValue;

    private sealed class HostState { public Vector3 Pos; public float Yaw; public int State; public Vector3 Dest; public bool HasDest; }
    private readonly Dictionary<CatController, HostState> _hostStates = new();

    private static CatController[] GetCats()
    {
        int sceneIdx = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
        if (sceneIdx != _lastSceneIdx) { _lastSceneIdx = sceneIdx; return RefreshCats(); }
        _catCacheTimer += Time.deltaTime;
        if (_catCacheTimer >= 3f || _catCache == null) { _catCacheTimer = 0f; return RefreshCats(); }
        return _catCache;
    }

    private static CatController[] RefreshCats() => _catCache = UnityEngine.Object.FindObjectsOfType<CatController>();

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (Store.IsHost) HostBroadcastState();
        else
        {
            ClientCheckHardSync();
            ClientSendHeldUp();
        }
    }

    // ---------------- 主机：AI 状态广播（Host 权威） ----------------

    private void HostBroadcastState()
    {
        try
        {
            var cats = GetCats();
            if (cats == null || cats.Length == 0)
            {
                if ((++_sendLog % 30) == 1) CoopRuntime.LogSource?.LogInfo("[CatSyncV2] CatController not found");
                return;
            }
            int n = Math.Min(cats.Length, MaxCats);
            Store.Broadcast((byte)OpenNestCoop.Net.MsgType.V2Cat, w =>
            {
                w.Put((byte)n);
                for (int i = 0; i < n; i++)
                {
                    var c = cats[i];
                    if (c == null) { PutEmpty(w, i); continue; }
                    var p = c.transform.position;
                    Vector3 dest = p;
                    try { dest = c._movement._Agent.destination; } catch { }
                    float actTimer = 0f, actDur = 0f, afterLoop = 0f;
                    bool isLoop = false;
                    string loopEnd = "";
                    try { actTimer = c._activityTimer; } catch { }
                    try { actDur = c._currentActivityDuration; } catch { }
                    try { isLoop = c._isLoopingActivity; } catch { }
                    try { loopEnd = c.loopEndTrigger ?? ""; } catch { }
                    try { afterLoop = c._afterLoopActivityDuration; } catch { }
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
                    w.Put(actTimer); w.Put(actDur);
                    w.Put(isLoop ? (byte)1 : (byte)0);
                    w.Put(afterLoop);
                    w.Put(loopEnd ?? "");
                    w.Put(animHash);
                    w.Put(animTime);
                }
            }, reliable: true);
            if ((++_sendLog % 10) == 1) CoopRuntime.LogSource?.LogInfo($"[CatSyncV2] host AI state n={n}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CatSyncV2] HostBroadcastState: {ex.Message}"); }
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

    // ---------------- 客机：硬同步 + held 上行 ----------------

    private void ClientCheckHardSync()
    {
        if (_hostStates.Count == 0) return;
        List<CatController> gone = null;
        using var e = _hostStates.GetEnumerator();
        while (e.MoveNext())
        {
            var c = e.Current.Key;
            if (c == null) { (gone ??= new List<CatController>()).Add(c); continue; }
            var hs = e.Current.Value;
            if (IsCatHeld(c)) continue;      // 本端拾取中，位置本地控制
            if (hs.State == 4) continue;     // 被持有（任意端）：位置由持有端控制，跳过防 warp 抽搐
            float dist = Vector3.Distance(c.transform.position, hs.Pos);
            if (dist <= HardSyncDist) continue;
            try
            {
                bool warped = false;
                try { var agent = c._movement._Agent; if (agent != null) { agent.Warp(hs.Pos); warped = true; } } catch { }
                if (!warped) { c.transform.position = hs.Pos; c.transform.rotation = Quaternion.Euler(0f, hs.Yaw, 0f); }
                try { var mover = c._movement; if (mover != null) mover.SetDestination(hs.Pos); } catch { }
                CoopRuntime.LogSource?.LogInfo($"[CatSyncV2] hard-sync warp dist={dist:0.00} state={(int)c.CurrentState}");
            }
            catch { }
        }
        if (gone != null) foreach (var g in gone) _hostStates.Remove(g);
    }

    private void ClientSendHeldUp()
    {
        try
        {
            var cats = GetCats();
            if (cats == null || cats.Length == 0) return;
            var held = new List<(int idx, CatController c)>();
            for (int i = 0; i < cats.Length && i < MaxCats; i++)
            {
                var c = cats[i];
                if (c == null || !IsCatHeld(c)) continue;
                held.Add((i, c));
            }
            if (held.Count == 0) return;
            Store.Broadcast((byte)OpenNestCoop.Net.MsgType.V2Cat, w =>
            {
                w.Put((byte)held.Count);
                w.Put((byte)0xFF); // 标记：可变 held 列表（区别于主机 AI 广播固定 n）
                for (int k = 0; k < held.Count; k++)
                {
                    var (idx, c) = held[k];
                    var p = c.transform.position;
                    w.Put((byte)idx);
                    w.Put((byte)c.CurrentState);
                    w.Put(p.x); w.Put(p.y); w.Put(p.z);
                    w.Put(p.x); w.Put(p.y); w.Put(p.z);
                    w.Put(c.transform.eulerAngles.y);
                }
            }, reliable: true);
            if ((++_sendLog % 10) == 1) CoopRuntime.LogSource?.LogInfo("[CatSyncV2] client held cat up");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CatSyncV2] ClientSendHeldUp: {ex.Message}"); }
    }

    // ---------------- OnPacket：207 状态 / held ----------------

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int n = r.GetByte();
            var cats = GetCats();
            bool isHeld = r.AvailableBytes > 0 && r.RawData[r.Position] == 0xFF;
            if (isHeld)
            {
                r.GetByte(); // 0xFF
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
                float actTimer = 0f, actDur = 0f, afterLoop = 0f;
                bool isLoop = false;
                string loopEnd = "";
                int animHash = 0;
                float animTime = 0f;
                if (r.AvailableBytes >= 8)
                {
                    actTimer = r.GetFloat(); actDur = r.GetFloat();
                    if (r.AvailableBytes >= 1) isLoop = r.GetByte() != 0;
                    if (r.AvailableBytes >= 4) afterLoop = r.GetFloat();
                    if (r.AvailableBytes >= 1) loopEnd = r.GetString();
                    if (r.AvailableBytes >= 4) animHash = r.GetInt();
                    if (r.AvailableBytes >= 4) animTime = r.GetFloat();
                }
                if (cats == null || idx >= cats.Length || cats[idx] == null) continue;
                var c = cats[idx];
                if (IsCatHeld(c)) continue;
                ApplyHostAI(c, state, new Vector3(dx, dy, dz), actTimer, actDur, isLoop, afterLoop, loopEnd, animHash, animTime);
                if (_hostStates.TryGetValue(c, out var hs))
                {
                    hs.Pos = new Vector3(px, py, pz); hs.Yaw = yaw; hs.State = state;
                    hs.Dest = new Vector3(dx, dy, dz); hs.HasDest = true;
                }
                else
                    _hostStates[c] = new HostState { Pos = new Vector3(px, py, pz), Yaw = yaw, State = state, Dest = new Vector3(dx, dy, dz), HasDest = true };
            }
            if ((++_recvLog % 10) == 1) CoopRuntime.LogSource?.LogInfo($"[CatSyncV2] guest recv AI state n={n} tracked={_hostStates.Count}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CatSyncV2] OnPacket: {ex.Message}"); }
    }

    /// <summary>软同步：主机 AI 权威决策 → 客机本地执行（状态/计时器/动画 + SetDestination）。</summary>
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
                    int cur = (int)c.CurrentState;
                    if (cur != state) { try { c._currentState = (CatState)state; } catch { } }
                    try { c._currentActivityDuration = actDur; } catch { }
                    try { c._activityTimer = actTimer; } catch { }
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
                                    if ((int)curAnim.shortNameHash != animHash) anim.Play(animHash, 0, animTime);
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            var mover = c._movement;
            if (mover != null) { try { mover.SetDestination(dest); } catch { } }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CatSyncV2] ApplyHostAI: {ex.Message}"); }
    }

    private void HostApplyHeldFrom(ulong from, int n, NetDataReader r, CatController[] cats, byte[] originalData)
    {
        try
        {
            bool isHost = Store.IsHost;
            for (int i = 0; i < n; i++)
            {
                byte idx = r.GetByte();
                byte state = r.GetByte();
                float dx = r.GetFloat(); float dy = r.GetFloat(); float dz = r.GetFloat();
                float px = r.GetFloat(); float py = r.GetFloat(); float pz = r.GetFloat();
                float yaw = r.GetFloat();
                if (cats == null || idx >= cats.Length || cats[idx] == null) continue;
                var c = cats[idx];
                if (IsCatHeld(c)) continue;
                if (isHost)
                {
                    try
                    {
                        c.transform.position = new Vector3(px, py, pz);
                        c.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                        try { c.PauseBehavior(true); } catch { }
                    }
                    catch { }
                }
                else
                {
                    if (_hostStates.TryGetValue(c, out var hs))
                    { hs.Pos = new Vector3(px, py, pz); hs.Yaw = yaw; hs.State = state; }
                    else
                        _hostStates[c] = new HostState { Pos = new Vector3(px, py, pz), Yaw = yaw, State = state, Dest = new Vector3(px, py, pz), HasDest = true };
                }
            }
            // 主机转发给其他客机（星型，让第三端看到猫在客机手里）
            if (isHost && from != 0)
            {
                var net = _net;
                if (net != null)
                    for (int k = 0; k < net.Roster.Count; k++)
                    {
                        var p = net.Roster[k];
                        if (p != null && !p.IsLocal && (ulong)p.SteamId != from)
                            try { net.Transport.Send(p.SteamId, originalData, true); } catch { }
                    }
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CatSyncV2] HostApplyHeldFrom: {ex.Message}"); }
    }

    // ---------------- 交互事件（→ EventLayer，Operator） ----------------

    /// <summary>本地玩家-猫交互（Harmony patch 调用）→ 经 EventLayer 广播。ev: 1=拾起 2=放下 3=驱赶 4=抚摸 5=打断。</summary>
    public void OnLocalCatEvent(CatController cat, byte ev)
    {
        if (IsApplyingCat || cat == null || !Store.IsOnline) return; // 应用远端事件时不重复上报（防环）
        int idx = IndexOf(cat);
        if (idx < 0) return;
        try
        {
            var p = cat.transform.position;
            EventLayer.Instance.Raise(CatEventId, w =>
            {
                w.Put((byte)idx);
                w.Put(ev);
                w.Put(p.x); w.Put(p.y); w.Put(p.z);
                w.Put(cat.transform.eulerAngles.y);
            });
            CoopRuntime.LogSource?.LogInfo($"[CatSyncV2] cat event ev={ev} idx={idx} host={Store.IsHost}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CatSyncV2] OnLocalCatEvent: {ex.Message}"); }
    }

    /// <summary>EventLayer 复现：对端执行相同交互（IsApplyingCat 防环）。</summary>
    private static void ReproduceCatEvent(NetDataReader r)
    {
        try
        {
            int idx = r.GetByte();
            byte ev = r.GetByte();
            float px = r.GetFloat(); float py = r.GetFloat(); float pz = r.GetFloat();
            float yaw = r.GetFloat();
            var cats = GetCats();
            if (cats == null || idx < 0 || idx >= cats.Length || cats[idx] == null) return;
            var c = cats[idx];
            IsApplyingCat = true;
            try
            {
                switch (ev)
                {
                    case 1: try { c.StartCarrying(); } catch { }
                        try { c.transform.position = new Vector3(px, py, pz); c.transform.rotation = Quaternion.Euler(0f, yaw, 0f); } catch { }
                        break;
                    case 2: try { c.StopCarrying(); } catch { }
                        try { c.transform.position = new Vector3(px, py, pz); c.transform.rotation = Quaternion.Euler(0f, yaw, 0f); } catch { }
                        break;
                    case 3: try { c.ShooCat(false); } catch { } break;
                    case 4: try { c.PetTheCat(); } catch { } break;
                    case 5: try { c.InterruptCat(); } catch { } break;
                }
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CatSyncV2] ApplyCatEvent: {ex.Message}"); }
            finally { IsApplyingCat = false; }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CatSyncV2] ReproduceCatEvent: {ex.Message}"); }
    }

    // ---------------- 工具 ----------------

    private static int IndexOf(CatController target)
    {
        try
        {
            var cats = GetCats();
            if (cats == null) return -1;
            for (int i = 0; i < cats.Length; i++)
                if (cats[i] != null && cats[i] == target) return i;
        }
        catch { }
        return -1;
    }

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
