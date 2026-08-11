using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 玩家化身同步（巨型炮台内跑动的队友可视化）。
/// - 每个客户端（含主机）周期性把自己的世界位置/朝向上报（变化检测）。
/// - 星型拓扑：客户端上行给主机 → 主机转发给其他客户端；主机直接广播自己的。
/// - 远端玩家视觉通过 IPlayerVisualProvider 提供（默认：头球+防毒面罩+3D名字），
///   其他模组可 PlayerVisualRegistry.Register 注入自定义模型/骨架/动画。位置指数插值平滑。
/// </summary>
public static class PlayerSync
{
    private const float Interval = 0.2f;
    private const float PosDeadzone = 0.05f;   // 米：位置变化死区
    private const float YawDeadzone = 2f;      // 度：朝向变化死区
    private const float InterpRate = 12f;      // 位置插值系数

    private static float _timer;
    private static float _rosterTimer;
    private static FirstPersonController _fpc;

    private static bool _hasSent;
    private static Vector3 _lastPos;
    private static float _lastYaw;
    private static bool _warnedNoBody;
    private static int _sendLogCount;
    private static int _recvLogCount;

    private static readonly Dictionary<byte, Avatar> _avatars = new Dictionary<byte, Avatar>();

    private static readonly Color[] Palette =
    {
        new Color(0.30f, 0.75f, 1.00f), // 蓝
        new Color(1.00f, 0.55f, 0.30f), // 橙
        new Color(0.45f, 1.00f, 0.45f), // 绿
        new Color(1.00f, 0.90f, 0.30f), // 黄
        new Color(0.80f, 0.50f, 1.00f), // 紫
        new Color(1.00f, 0.40f, 0.40f), // 红
    };

    private sealed class Avatar
    {
        public byte PlayerId;
        public string Name;
        public GameObject Root;
        public IPlayerVisualProvider Provider; // 视觉提供者（可能为默认）
        public GameObject Visual;              // 提供者返回的视觉根
        public CrewRole Role;
        public Vector3 TargetPos;
        public float TargetYaw;
        public bool HasTarget;
        public bool Dead;
        public float Speed;                    // 估算移动速度（供动画）
    }

    public static void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;

        // 每帧：远端化身插值跟随 + 驱动角色视觉（动作/动画/billboard）
        foreach (var kv in _avatars)
        {
            var a = kv.Value;
            if (a.Root == null) continue;
            if (a.HasTarget)
            {
                float t = 1f - Mathf.Exp(-InterpRate * dt);
                var newPos = Vector3.Lerp(a.Root.transform.position, a.TargetPos, t);
                a.Speed = Vector3.Distance(newPos, a.Root.transform.position) / Mathf.Max(dt, 0.0001f);
                a.Root.transform.position = newPos;
                a.Root.transform.rotation = Quaternion.Slerp(
                    a.Root.transform.rotation, Quaternion.Euler(0f, a.TargetYaw, 0f), t);
            }
            else
            {
                a.Speed = Mathf.Lerp(a.Speed, 0f, 0.1f);
            }

            if (a.Provider != null && a.Visual != null)
            {
                var pose = new AvatarPose
                {
                    Position = a.Root.transform.position,
                    Yaw = a.Root.transform.eulerAngles.y,
                    Speed = a.Speed,
                    Moving = a.Speed > 0.05f,
                    Role = a.Role,
                    Action = a.Speed > 0.05f ? PlayerAction.Moving : PlayerAction.Idle,
                    DeviceId = 0,
                };
                try { a.Provider.Update(a.Visual, dt, ref pose); }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"PlayerSync provider.Update: {ex.Message}"); }
            }
        }

        // 名单对齐：新成员建化身，离开者清理（0.25s 一次）
        _rosterTimer += dt;
        if (_rosterTimer >= 0.25f)
        {
            _rosterTimer = 0f;
            SyncRoster(net);
        }

        // 周期性上报自己的位置
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        SendLocal(net);
    }

    /// <summary>收到 PlayerPos 包：主机转发，客户端更新化身。</summary>
    public static void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            byte pid = r.GetByte();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            float yaw = r.GetFloat();

            if (net.Local != null && pid == net.Local.PlayerId) return; // 忽略自己

            // 主机：转发给其他客户端（保持星型一致；合包）——用 State 判断不依赖 Lobby.IsHost
            if (net.State == SessionState.Hosting)
                net.EnqueueBatch(data, true);

            if (_avatars.TryGetValue(pid, out var a))
            {
                a.TargetPos = pos;
                a.TargetYaw = yaw;
                a.HasTarget = true;
            }

            // 诊断日志：约每 30 包一次，确认位置在接收
            if ((++_recvLogCount % 30) == 0)
                CoopRuntime.LogSource?.LogInfo($"[PlayerSync] recv pid={pid} pos=({pos.x:0.0},{pos.y:0.0},{pos.z:0.0}) avatars={_avatars.Count}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"PlayerSync OnPacket: {ex.Message}"); }
    }

    // ---------------- 本地上报 ----------------

    private static void SendLocal(NetManager net)
    {
        if (net.Local == null || net.Local.PlayerId == 255) return; // 尚未由主机分配 ID
        var tr = GetBodyTransform();
        if (tr == null)
        {
            if (!_warnedNoBody)
            {
                _warnedNoBody = true;
                CoopRuntime.LogSource?.LogWarning("PlayerSync: 找不到本地玩家身体位置，位置不会同步");
            }
            return;
        }
        float yaw = GetYaw();

        bool changed = !_hasSent
            || Vector3.SqrMagnitude(tr.position - _lastPos) > PosDeadzone * PosDeadzone
            || Mathf.Abs(Mathf.DeltaAngle(yaw, _lastYaw)) > YawDeadzone;
        if (!changed) return;
        _hasSent = true;
        _lastPos = tr.position;
        _lastYaw = yaw;

        var w = NetProtocol.Begin(MsgType.PlayerPos);
        w.Put(net.Local.PlayerId);
        w.Put(tr.position.x); w.Put(tr.position.y); w.Put(tr.position.z);
        w.Put(yaw);
        var data = NetProtocol.Snapshot(w);

        // 用 State 判断广播/上行（不依赖易变的 Lobby.IsHost）：
        // 主机(State=Hosting) 广播给全员，客机(Joined) 上行给主机。
        if (net.State == SessionState.Hosting)
            net.EnqueueBatch(data, true);
        else
            net.EnqueueBatch(data, false);

        // 诊断日志：约每 5s 一次，确认位置在发送
        if ((++_sendLogCount % 25) == 0)
            CoopRuntime.LogSource?.LogInfo($"[PlayerSync] send pid={net.Local.PlayerId} state={net.State} pos=({tr.position.x:0.0},{tr.position.y:0.0},{tr.position.z:0.0}) yaw={yaw:0}");
    }

    /// <summary>本地玩家身体位置（地面高度）。优先 FirstPersonController，回退主相机。</summary>
    private static Transform GetBodyTransform()
    {
        if (_fpc == null)
        {
            try { _fpc = UnityEngine.Object.FindFirstObjectByType<FirstPersonController>(); }
            catch { _fpc = null; }
        }
        if (_fpc != null)
        {
            try
            {
                var t = _fpc.mainGameObjectTransform;
                if (t != null) return t;
                var cr = _fpc.cameraRoot;
                if (cr != null) return cr;
            }
            catch { }
            return _fpc.transform; // 兜底：FPC 自身 transform
        }
        var cam = Camera.main;
        return cam != null ? cam.transform : null;
    }

    private static float GetYaw()
    {
        // 归一化到 0~360（_fpc.yaw 是累加值，可能超过 360）
        if (_fpc != null) return Mathf.Repeat(_fpc.yaw, 360f);
        var tr = GetBodyTransform();
        return tr != null ? Mathf.Repeat(tr.eulerAngles.y, 360f) : 0f;
    }

    // ---------------- 化身管理 ----------------

    private static void SyncRoster(NetManager net)
    {
        foreach (var kv in _avatars) kv.Value.Dead = true;
        if (net.Roster != null)
        {
            foreach (var p in net.Roster)
            {
                if (p.IsLocal) continue;
                if (_avatars.TryGetValue(p.PlayerId, out var a))
                {
                    a.Dead = false;
                    a.Name = p.Name;
                    a.Role = p.Role;
                }
                else
                {
                    _avatars[p.PlayerId] = CreateAvatar(p);
                }
            }
        }
        List<byte> toRemove = null;
        foreach (var kv in _avatars)
            if (kv.Value.Dead) (toRemove ??= new List<byte>()).Add(kv.Key);
        if (toRemove != null)
            foreach (var id in toRemove)
            {
                DestroyAvatar(_avatars[id]);
                _avatars.Remove(id);
            }
    }

    private static Avatar CreateAvatar(PlayerSession p)
    {
        var a = new Avatar { PlayerId = p.PlayerId, Name = p.Name, Role = p.Role };
        try
        {
            var tint = ColorFor(p.SteamId);
            var root = new GameObject($"CoopAvatar_{p.PlayerId}");
            var provider = PlayerVisualRegistry.Provider ?? (IPlayerVisualProvider)DefaultPlayerVisualProvider.Instance;
            GameObject visual = null;
            try { visual = provider.Create(root.transform, p.Name, tint); }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"PlayerSync provider.Create: {ex.Message}"); }
            if (visual == null)
            {
                // 提供者失败 → 回退默认
                provider = DefaultPlayerVisualProvider.Instance;
                visual = provider.Create(root.transform, p.Name, tint);
            }
            a.Root = root;
            a.Provider = provider;
            a.Visual = visual;
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"PlayerSync CreateAvatar: {ex.Message}");
        }
        return a;
    }

    private static void DestroyAvatar(Avatar a)
    {
        if (a.Provider != null && a.Visual != null)
        {
            try { a.Provider.Destroy(a.Visual); }
            catch { }
        }
        if (a.Root != null)
        {
            try { UnityEngine.Object.Destroy(a.Root); }
            catch { }
            a.Root = null;
        }
    }

    private static Color ColorFor(ulong steamId) =>
        Palette[(int)(steamId % (ulong)Palette.Length)];
}
