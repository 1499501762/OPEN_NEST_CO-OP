using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 玩家位置/朝向同步（PlayerSyncV2，MsgType=205）。M7：把 V1 <c>PlayerSync</c> 迁入分层架构。
/// - <see cref="V2Authority.Operator"/> 语义：位置/朝向由本人移动决定，谁移动谁上报（10Hz + 死区），
///   经 <see cref="IHostStore.Broadcast"/> 会话广播（主机→全员 / 客机→主机中继），远端化身指数插值。
/// - 化身渲染（IPlayerVisualProvider 系统）复用 V1 公共基建（PlayerVisualRegistry / Humanoid /
///   AnimatorAvatar / ExternalModel / CatCrew），只做同步层，不做渲染重写。
/// - 星型：主机收到客机上报 → EnqueueBatch 转发其他客机；忽略自己（pid == 本地）。
/// - IL2CPP：化身字典显式遍历；_fpc 查找失败 1s 退避（吸收 D2 优化）。
/// </summary>
public sealed class PlayerSyncV2 : ISyncedModule
{
    public static PlayerSyncV2 Instance { get; } = new PlayerSyncV2();

    private PlayerSyncV2() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2Player;

    private const float Interval = 0.1f;      // 10Hz
    private const float PosDeadzone = 0.03f;
    private const float YawDeadzone = 1.5f;
    private const float PitchDeadzone = 1.5f;
    private const float InterpRate = 12f;
    private const float SpeedSmooth = 0.25f;

    private float _timer, _rosterTimer;
    private FirstPersonController _fpc;
    private float _fpcRetryTimer;   // D2：查找失败 1s 退避
    private bool _hasSent;
    private Vector3 _lastPos;
    private float _lastYaw, _lastPitch, _lastSendTime;
    private Vector3 _lastLocalPos;
    private bool _warnedNoBody;
    private int _sendLog, _recvLog;

    private readonly Dictionary<byte, Avatar> _avatars = new();

    private static readonly Color[] Palette =
    {
        new Color(0.30f, 0.75f, 1.00f), new Color(1.00f, 0.55f, 0.30f),
        new Color(0.45f, 1.00f, 0.45f), new Color(1.00f, 0.90f, 0.30f),
        new Color(0.80f, 0.50f, 1.00f), new Color(1.00f, 0.40f, 0.40f),
    };

    private sealed class Avatar
    {
        public byte PlayerId;
        public string Name;
        public GameObject Root;
        public IPlayerVisualProvider Provider;
        public GameObject Visual;
        public CrewRole Role;
        public Vector3 TargetPos;
        public float TargetYaw;
        public bool HasTarget;
        public bool Dead;
        public float Speed, MoveFwd, MoveStrafe;
        public float TargetMoveFwd, TargetMoveStrafe;
        public bool Airborne, Crouched, Sprinting;
        public bool TargetAirborne, TargetCrouched, TargetSprinting;
        public float TargetPitch, Pitch, TargetSpeed;
    }

    // ---------------- ISyncedModule ----------------

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;

        // 每帧：远端化身插值跟随 + 驱动角色视觉
        using var e = _avatars.GetEnumerator();
        while (e.MoveNext())
        {
            var a = e.Current.Value;
            if (a.Root == null) continue;
            if (a.HasTarget)
            {
                float t = 1f - Mathf.Exp(-InterpRate * dt);
                var oldPos = a.Root.transform.position;
                var newPos = Vector3.Lerp(oldPos, a.TargetPos, t);
                float targetSpd = a.TargetSpeed;
                if (Vector3.Distance(newPos, a.TargetPos) < 0.02f) targetSpd = 0f; // 已收敛→停止
                a.Speed = Mathf.Lerp(a.Speed, targetSpd, SpeedSmooth);
                a.Root.transform.position = newPos;
                float dy = Mathf.DeltaAngle(a.Root.transform.eulerAngles.y, a.TargetYaw);
                a.Root.transform.rotation = Quaternion.Euler(0f, a.Root.transform.eulerAngles.y + dy * t, 0f);
                a.MoveFwd = Mathf.Lerp(a.MoveFwd, a.TargetMoveFwd, 0.2f);
                a.MoveStrafe = Mathf.Lerp(a.MoveStrafe, a.TargetMoveStrafe, 0.2f);
                a.Airborne = a.TargetAirborne;
                a.Crouched = a.TargetCrouched;
                a.Sprinting = a.TargetSprinting;
                a.Pitch = Mathf.Lerp(a.Pitch, a.TargetPitch, 0.2f);
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
                    MoveFwd = a.MoveFwd,
                    MoveStrafe = a.MoveStrafe,
                    Airborne = a.Airborne,
                    Crouched = a.Crouched,
                    Sprinting = a.Sprinting,
                    Pitch = a.Pitch,
                };
                try { a.Provider.Update(a.Visual, dt, ref pose); }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[PlayerSyncV2] provider.Update: {ex.Message}"); }
            }
        }

        // 名单对齐：新成员建化身，离开者清理（0.25s 一次）
        _rosterTimer += dt;
        if (_rosterTimer >= 0.25f) { _rosterTimer = 0f; SyncRoster(); }

        // 周期性上报自己的位置
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        SendLocal();
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            byte pid = r.GetByte();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            float yaw = r.GetFloat();
            float moveFwd = r.GetFloat();
            float moveStrafe = r.GetFloat();
            byte flags = r.GetByte();
            float pitch = r.GetFloat();
            float speed = r.GetFloat();

            var net = _net;
            if (net?.Local != null && pid == net.Local.PlayerId) return; // 忽略自己
            // 主机：转发给其他客户端（星型一致；合包）
            if (Store.IsHost) net?.EnqueueBatch(data, true);
            if (_avatars.TryGetValue(pid, out var a))
            {
                a.TargetPos = pos;
                a.TargetYaw = yaw;
                a.HasTarget = true;
                a.TargetMoveFwd = moveFwd;
                a.TargetMoveStrafe = moveStrafe;
                a.TargetAirborne = (flags & 1) != 0;
                a.TargetCrouched = (flags & 2) != 0;
                a.TargetSprinting = (flags & 4) != 0;
                a.TargetPitch = pitch;
                a.TargetSpeed = speed;
            }
            if ((++_recvLog % 30) == 0)
                CoopLog.Debug("SyncV2.playerRecv", () => $"[SyncV2] PlayerSyncV2 recv pid={pid} pos=({pos.x:0.0},{pos.y:0.0},{pos.z:0.0}) avatars={_avatars.Count}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[PlayerSyncV2] OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset()
    {
        using var e = _avatars.GetEnumerator();
        var gone = new List<byte>();
        while (e.MoveNext()) gone.Add(e.Current.Key);
        for (int i = 0; i < gone.Count; i++)
        {
            if (_avatars.TryGetValue(gone[i], out var a)) DestroyAvatar(a);
            _avatars.Remove(gone[i]);
        }
        _fpc = null;
        _hasSent = false;
    }

    // ---------------- 本地上报（Operator 权威） ----------------

    private void SendLocal()
    {
        var net = _net;
        if (net == null || net.Local == null || net.Local.PlayerId == 255) return;
        var tr = GetBodyTransform();
        if (tr == null)
        {
            if (!_warnedNoBody)
            {
                _warnedNoBody = true;
                CoopRuntime.LogSource?.LogWarning("[PlayerSyncV2] local player body position not found, position won't sync");
            }
            return;
        }
        float yaw = GetYaw();
        float pitch = GetPitch();
        bool changed = !_hasSent
            || Vector3.SqrMagnitude(tr.position - _lastPos) > PosDeadzone * PosDeadzone
            || Mathf.Abs(Mathf.DeltaAngle(yaw, _lastYaw)) > YawDeadzone
            || Mathf.Abs(Mathf.DeltaAngle(pitch, _lastPitch)) > PitchDeadzone;
        if (!changed) return;
        _hasSent = true;

        // 真实水平速度 = 距上次发送位移 / 时间
        Vector3 prevSentPos = _lastPos;
        float now = Time.time;
        float dtSend = Mathf.Max(now - _lastSendTime, 0.001f);
        _lastSendTime = now;
        _lastPos = tr.position;
        _lastYaw = yaw;
        _lastPitch = pitch;
        float realSpeed = 0f;
        { Vector3 d = tr.position - prevSentPos; d.y = 0f; realSpeed = d.magnitude / dtSend; }

        byte pid = net.Local.PlayerId;
        Vector3 pos = tr.position;
        ComputeLocalMove(pos, yaw, out float mf, out float ms);
        byte flags = 0;
        if (IsAirborne()) flags |= 1;
        if (IsCrouched()) flags |= 2;
        if (IsSprinting()) flags |= 4;

        // 会话广播（Operator 权威）：主机→全员 / 客机→主机中继
        Store.Broadcast((byte)OpenNestCoop.Net.MsgType.V2Player, w =>
        {
            w.Put(pid);
            w.Put(pos.x); w.Put(pos.y); w.Put(pos.z);
            w.Put(yaw);
            w.Put(mf); w.Put(ms);
            w.Put(flags);
            w.Put(pitch);
            w.Put(realSpeed);
        }, reliable: false);

        if ((++_sendLog % 25) == 0)
            CoopLog.Debug("SyncV2.playerSend", () => $"[SyncV2] PlayerSyncV2 send pid={pid} pos=({pos.x:0.0},{pos.y:0.0},{pos.z:0.0}) yaw={yaw:0}");
    }

    private Transform GetBodyTransform()
    {
        if (_fpc == null)
        {
            _fpcRetryTimer -= Time.deltaTime;
            if (_fpcRetryTimer <= 0f)
            {
                _fpcRetryTimer = 1f;
                try { _fpc = UnityEngine.Object.FindFirstObjectByType<FirstPersonController>(); }
                catch { _fpc = null; }
            }
        }
        else _fpcRetryTimer = 0f;
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
            return _fpc.transform;
        }
        var cam = Camera.main;
        return cam != null ? cam.transform : null;
    }

    private float GetYaw()
    {
        if (_fpc != null) return Mathf.Repeat(_fpc.yaw, 360f);
        var tr = GetBodyTransform();
        return tr != null ? Mathf.Repeat(tr.eulerAngles.y, 360f) : 0f;
    }

    private float GetPitch()
    {
        if (_fpc != null)
        {
            try { return Mathf.Clamp(_fpc.pitch, -90f, 90f); }
            catch { }
        }
        return 0f;
    }

    private bool IsAirborne() { try { return _fpc != null && !_fpc.isGrounded; } catch { return false; } }
    private bool IsCrouched() { try { return _fpc != null && _fpc.isCrouched; } catch { return false; } }
    private bool IsSprinting() { try { return _fpc != null && _fpc.isSprinting; } catch { return false; } }

    /// <summary>把世界位移投影到本地空间，得前/横速度分量（用于横移姿态）。</summary>
    private void ComputeLocalMove(Vector3 pos, float yaw, out float moveFwd, out float moveStrafe)
    {
        moveFwd = 0f; moveStrafe = 0f;
        Vector3 delta = pos - _lastLocalPos;
        _lastLocalPos = pos;
        if (Mathf.Abs(delta.y) > 2f) delta.y = 0f;
        delta.y = 0f;
        float dist = delta.magnitude;
        if (dist < 0.001f) return;
        var dir = delta / dist;
        float yawRad = yaw * Mathf.Deg2Rad;
        var fwd = new Vector3(Mathf.Sin(yawRad), 0f, Mathf.Cos(yawRad));
        var right = new Vector3(Mathf.Cos(yawRad), 0f, -Mathf.Sin(yawRad));
        moveFwd = Vector3.Dot(dir, fwd);
        moveStrafe = Vector3.Dot(dir, right);
    }

    // ---------------- 化身管理（复用 V1 渲染基建） ----------------

    private void SyncRoster()
    {
        var net = _net;
        if (net == null) return;
        using var e = _avatars.GetEnumerator();
        while (e.MoveNext()) e.Current.Value.Dead = true;
        if (net.Roster != null)
            for (int i = 0; i < net.Roster.Count; i++)
            {
                var p = net.Roster[i];
                if (p.IsLocal) continue;
                if (_avatars.TryGetValue(p.PlayerId, out var a))
                {
                    a.Dead = false; a.Name = p.Name; a.Role = p.Role;
                }
                else
                {
                    _avatars[p.PlayerId] = CreateAvatar(p);
                }
            }
        var gone = new List<byte>();
        using (var e2 = _avatars.GetEnumerator())
            while (e2.MoveNext()) if (e2.Current.Value.Dead) gone.Add(e2.Current.Key);
        for (int i = 0; i < gone.Count; i++)
        {
            if (_avatars.TryGetValue(gone[i], out var a)) DestroyAvatar(a);
            _avatars.Remove(gone[i]);
        }
    }

    private Avatar CreateAvatar(PlayerSession p)
    {
        var a = new Avatar { PlayerId = p.PlayerId, Name = p.Name, Role = p.Role };
        try
        {
            var tint = ColorFor(p.SteamId);
            var root = new GameObject($"CoopAvatar_{p.PlayerId}");
            var provider = ResolveProvider(_net);
            CoopLog.Info("SyncV2.playerAvatar", () => $"[SyncV2] CreateAvatar: provider={provider.GetType().Name} pid={p.PlayerId}");
            GameObject visual = null;
            try { visual = provider.Create(root.transform, p.Name, tint); }
            catch (Exception ex) { CoopLog.Warn("SyncV2.playerAvatar", () => $"[SyncV2] provider.Create: {ex.Message}"); }
            if (visual == null)
            {
                provider = HumanoidVisualProvider.Instance;
                visual = provider.Create(root.transform, p.Name, tint);
            }
            a.Root = root; a.Provider = provider; a.Visual = visual;
        }
        catch (Exception ex) { CoopLog.Warn("SyncV2.playerAvatar", () => $"[SyncV2] CreateAvatar: {ex.Message}"); }
        return a;
    }

    /// <summary>视觉提供者选择（复用 V1 公共基建）：注册 provider 优先 → 按模式/开关选 bundle/外部模型/猫/人形。</summary>
    private static IPlayerVisualProvider ResolveProvider(NetManager net)
    {
        if (PlayerVisualRegistry.Provider != null) return PlayerVisualRegistry.Provider;
        var localMode = net != null && net.LocalMode;
        int overrideMode = ReadModelOverride();

        const bool animatorBundleEnabled = true;
        bool wantAnimator = animatorBundleEnabled && !localMode;
        if (animatorBundleEnabled && overrideMode == 1) wantAnimator = true;
        else if (animatorBundleEnabled && overrideMode == -1) wantAnimator = false;
        if (wantAnimator)
        {
            var anim = AnimatorAvatarVisualProvider.Instance;
            if (anim.TryLoad()) return anim;
        }

        const bool externalModelEnabled = true;
        bool wantModel = externalModelEnabled && !localMode;
        if (externalModelEnabled && overrideMode == 1) wantModel = true;
        else if (externalModelEnabled && overrideMode == -1) wantModel = false;
        if (wantModel)
        {
            string choice = "";
            try { choice = (Environment.GetEnvironmentVariable("ONC_PROVIDER") ?? "").Trim().ToLowerInvariant(); } catch { }
            if (choice == "soldier")
            {
                var ext = ExternalModelProvider.Instance;
                if (ext.TryLoad()) return ext;
            }
            else if (choice == "humanoid") return HumanoidVisualProvider.Instance;
            else
            {
                var ext = ExternalModelProvider.Instance;
                if (ext.TryLoad()) return ext;
                var cat = CatCrewVisualProvider.Instance;
                if (cat.TryLoad()) return cat;
            }
        }
        return HumanoidVisualProvider.Instance;
    }

    private static int ReadModelOverride()
    {
        try
        {
            var cfg = ExternalModelProvider.Instance.ConfigValue;
            if (!string.IsNullOrEmpty(cfg))
            {
                if (string.Equals(cfg.Trim(), "1", StringComparison.OrdinalIgnoreCase)) return 1;
                if (string.Equals(cfg.Trim(), "0", StringComparison.OrdinalIgnoreCase)) return -1;
            }
        }
        catch { }
        try
        {
            var v = Environment.GetEnvironmentVariable("ONC_MODEL");
            if (string.IsNullOrEmpty(v)) v = Environment.GetEnvironmentVariable("ONC_MODEL_FORCE");
            if (!string.IsNullOrEmpty(v))
            {
                if (string.Equals(v, "1", StringComparison.OrdinalIgnoreCase)) return 1;
                else if (string.Equals(v, "0", StringComparison.OrdinalIgnoreCase)) return -1;
            }
        }
        catch { }
        return 0;
    }

    private static void DestroyAvatar(Avatar a)
    {
        if (a.Provider != null && a.Visual != null)
        {
            try { a.Provider.Destroy(a.Visual); } catch { }
        }
        if (a.Root != null)
        {
            try { UnityEngine.Object.Destroy(a.Root); } catch { }
            a.Root = null;
        }
    }

    private static Color ColorFor(ulong steamId) => Palette[(int)(steamId % (ulong)Palette.Length)];
}
