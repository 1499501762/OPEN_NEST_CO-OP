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
/// - 远端玩家视觉通过 IPlayerVisualProvider 提供（默认：程序化人形 + 防毒面罩 + 3D名字），
///   其他模组可 PlayerVisualRegistry.Register 注入自定义模型/骨架/动画。位置指数插值平滑。
/// </summary>
public static class PlayerSync
{
    private const float Interval = 0.1f;   // 位置/朝向上报周期（10Hz，走路更平滑）
    private const float PosDeadzone = 0.03f;   // 米：位置变化死区
    private const float YawDeadzone = 1.5f;    // 度：朝向变化死区
    private const float PitchDeadzone = 1.5f;  // 度：俯仰变化死区（抬头低头也触发发包）
    private const float InterpRate = 12f;      // 位置插值系数
    private const float SpeedSmooth = 0.25f;   // 远端速度低通滤波（抑制瞬时微分抖动）
    // E 分级保底：普通位置帧走 unreliable（连续值容忍丢，减拥塞）；每 2s 无条件 reliable 心跳帧保底对齐（防长期丢失漂移）
    private const float PlayerPosHbInterval = 2f;

    private static float _timer;
    private static float _rosterTimer;
    private static FirstPersonController _fpc;
    // D2: _fpc 查找失败退避（每 1s 重查一次，避免查找失败时每帧 FindFirstObjectByType 全场景扫描卡顿）
    private static float _fpcRetryTimer;

    private static bool _hasSent;
    private static Vector3 _lastPos;
    private static float _lastYaw;
    private static float _lastPitch;      // v0.2.4：上次发送俯仰（抬头低头也触发发包）
    private static float _lastSendTime;   // v0.2.3：上次发送时刻（算真实速度）
    private static float _lastPosHbTime;  // E: 位置心跳帧计时（2s reliable 保底）
    private static byte _lastFlags;       // E: 上次发送的状态标记（变化才发 reliable）
    private static bool _hasSentState;    // E: 状态标记是否发送过（首次必发保证初始对齐）
    private static Vector3 _lastLocalPos; // v0.2：上一帧位置（算本地横移方向）
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
        public float MoveFwd;                  // 平滑后的本地前进速度分量
        public float MoveStrafe;               // 平滑后的本地横向速度分量
        public float TargetMoveFwd;
        public float TargetMoveStrafe;
        public bool Airborne;                  // 空中（跳跃/下落）
        public bool Crouched;                  // 蹲下
        public bool Sprinting;                 // 奔跑
        public bool TargetAirborne;
        public bool TargetCrouched;
        public bool TargetSprinting;
        public float TargetPitch;              // 摄像机俯仰（驱动头部转向）
        public float Pitch;
        public float TargetSpeed;              // v0.2.3：发送端真实速度（替代插值估算）
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
                var oldPos = a.Root.transform.position;
                var newPos = Vector3.Lerp(oldPos, a.TargetPos, t);
                // v0.2.3：速度用发送端上报的真实速度（平滑）；若插值已收敛到目标位置
                //（发送端可能已停止发包），则视为停止 → 速度归零，避免一直保持旧速度走动
                float targetSpd = a.TargetSpeed;
                if (Vector3.Distance(newPos, a.TargetPos) < 0.02f)
                    targetSpd = 0f;
                a.Speed = Mathf.Lerp(a.Speed, targetSpd, SpeedSmooth);
                a.Root.transform.position = newPos;
                // 朝向：走最短角度路径（避免 350°→10° 绕大圈）
                float dy = Mathf.DeltaAngle(a.Root.transform.eulerAngles.y, a.TargetYaw);
                a.Root.transform.rotation = Quaternion.Euler(0f, a.Root.transform.eulerAngles.y + dy * t, 0f);
                // 本地移动方向分量平滑（横移姿态）
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
            bool isHb = r.GetByte() != 0;   // 心跳帧标记（对端据此 reliable 转发保底）
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            float yaw = r.GetFloat();
            float moveFwd = r.GetFloat();   // v0.2：本地前进速度分量
            float moveStrafe = r.GetFloat();// v0.2：本地横向速度分量
            float speed = r.GetFloat();     // v0.2.3：发送端真实水平速度

            if (net.Local != null && pid == net.Local.PlayerId) return; // 忽略自己

            // 主机：转发给其他客户端（保持星型一致；合包）——用 State 判断不依赖 Lobby.IsHost。
            // E 分级：心跳帧 reliable 转发保底，普通帧 unreliable（连续位置容忍丢）
            if (net.State == SessionState.Hosting)
                net.EnqueueBatch(data, true, !isHb);

            if (_avatars.TryGetValue(pid, out var a))
            {
                a.TargetPos = pos;
                a.TargetYaw = yaw;
                a.HasTarget = true;
                a.TargetMoveFwd = moveFwd;
                a.TargetMoveStrafe = moveStrafe;
                a.TargetSpeed = speed;   // v0.2.3：真实速度
            }

            // 诊断日志：约每 30 包一次，确认位置在接收
            if ((++_recvLogCount % 30) == 0)
                CoopLog.Debug("PlayerSync.recv", () => $"[PlayerSync] recv pid={pid} pos=({pos.x:0.0},{pos.y:0.0},{pos.z:0.0}) avatars={_avatars.Count}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"PlayerSync OnPacket: {ex.Message}"); }
    }

    /// <summary>玩家状态标记（空中/蹲下/冲刺 + 俯仰）——必须 reliable 送达（丢失会卡在错误状态）。
    /// 变化才发（频率低，reliable 开销小）；主机转发保持 reliable。</summary>
    public static void OnStatePacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            byte pid = r.GetByte();
            byte flags = r.GetByte();
            float pitch = r.GetFloat();
            if (net.Local != null && pid == net.Local.PlayerId) return; // 忽略自己
            // 主机转发（状态必须可靠，转发保持 reliable）
            if (net.State == SessionState.Hosting)
                net.EnqueueBatch(data, true);
            if (_avatars.TryGetValue(pid, out var a))
            {
                a.TargetAirborne = (flags & 1) != 0;
                a.TargetCrouched = (flags & 2) != 0;
                a.TargetSprinting = (flags & 4) != 0;
                a.TargetPitch = pitch;
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"PlayerSync OnStatePacket: {ex.Message}"); }
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
                CoopRuntime.LogSource?.LogWarning("PlayerSync: local player body position not found, position won't sync");
            }
            return;
        }
        float yaw = GetYaw();
        float pitch = GetPitch();

        bool changed = !_hasSent
            || Vector3.SqrMagnitude(tr.position - _lastPos) > PosDeadzone * PosDeadzone
            || Mathf.Abs(Mathf.DeltaAngle(yaw, _lastYaw)) > YawDeadzone;
        // pitch 变化走状态包（reliable），不触发位置包
        // E 分级保底：普通帧 unreliable；每 2s 无条件心跳帧 reliable（防 unreliable 长期丢包后位置漂移）
        bool hb = Time.time - _lastPosHbTime >= PlayerPosHbInterval;
        if (hb) _lastPosHbTime = Time.time;
        if (!changed && !hb) return;
        _hasSent = true;

        // v0.2.3：真实水平速度 = 距上次发送位移 / 距上次发送时间（在覆盖 _lastPos 之前计算）
        Vector3 prevSentPos = _lastPos;
        float now = Time.time;
        float dtSend = Mathf.Max(now - _lastSendTime, 0.001f);
        _lastSendTime = now;
        _lastPos = tr.position;
        _lastYaw = yaw;
        float realSpeed = 0f;
        {
            Vector3 d = tr.position - prevSentPos;
            d.y = 0f;
            realSpeed = d.magnitude / dtSend;
        }

        // ===== 位置包（unreliable）：连续位置/移动值，容忍丢帧（下帧纠正）；心跳帧 reliable 保底防漂移 =====
        var w = NetProtocol.Begin(MsgType.PlayerPos);
        w.Put(net.Local.PlayerId);
        w.Put(hb ? (byte)1 : (byte)0); // 心跳帧标记（对端据此 reliable 转发保底）
        w.Put(tr.position.x); w.Put(tr.position.y); w.Put(tr.position.z);
        w.Put(yaw);
        // 本地空间速度分量（供横移姿态）与真实速度
        ComputeLocalMove(tr.position, yaw, out float moveFwd, out float moveStrafe);
        w.Put(moveFwd);
        w.Put(moveStrafe);
        w.Put(realSpeed);
        var data = NetProtocol.Snapshot(w);
        // E 分级：心跳帧 reliable 保底，普通帧 unreliable（连续位置容忍丢，减拥塞）
        if (net.State == SessionState.Hosting)
            net.EnqueueBatch(data, true, !hb);
        else
            net.EnqueueBatch(data, false, !hb);

        // ===== 状态包（reliable）：离散状态标记（空中/蹲下/冲刺）+ 俯仰——变化才发，必须可靠送达 =====
        // （状态标记丢了会卡在错误状态，不能靠下帧纠正；变化才发频率低，reliable 开销小）
        byte flags = 0;
        if (IsAirborne()) flags |= 1;
        if (IsCrouched()) flags |= 2;
        if (IsSprinting()) flags |= 4;
        bool stateChanged = flags != _lastFlags
            || Mathf.Abs(Mathf.DeltaAngle(pitch, _lastPitch)) > PitchDeadzone;
        if (stateChanged || !_hasSentState || hb)
        {
            _lastFlags = flags;
            _lastPitch = pitch;
            _hasSentState = true;
            var sw = NetProtocol.Begin(MsgType.PlayerState);
            sw.Put(net.Local.PlayerId);
            sw.Put(flags);
            sw.Put(pitch);
            var sd = NetProtocol.Snapshot(sw);
            if (net.State == SessionState.Hosting)
                net.EnqueueBatch(sd, true);   // reliable（默认）
            else
                net.EnqueueBatch(sd, false);  // reliable（默认）
        }

        // 诊断日志：约每 5s 一次，确认位置在发送
        if ((++_sendLogCount % 25) == 0)
            CoopLog.Debug("PlayerSync.send", () => $"[PlayerSync] send pid={net.Local.PlayerId} state={net.State} pos=({tr.position.x:0.0},{tr.position.y:0.0},{tr.position.z:0.0}) yaw={yaw:0}");
    }

    /// <summary>本地玩家身体位置（地面高度）。优先 FirstPersonController，回退主相机。</summary>
    private static Transform GetBodyTransform()
    {
        if (_fpc == null)
        {
            // D2: 查找失败退避（1s 重查一次），避免每帧 FindFirstObjectByType 全场景扫描
            _fpcRetryTimer -= Time.deltaTime;
            if (_fpcRetryTimer <= 0f)
            {
                _fpcRetryTimer = 1f;
                try { _fpc = UnityEngine.Object.FindFirstObjectByType<FirstPersonController>(); }
                catch { _fpc = null; }
            }
        }
        else _fpcRetryTimer = 0f; // 已找到：保持就绪
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

    // ---------------- 移动方向 / 姿态（v0.2） ----------------

    /// <summary>把世界位移投影到本地空间，得前/横速度分量（用于横移姿态）。</summary>
    private static void ComputeLocalMove(Vector3 pos, float yaw, out float moveFwd, out float moveStrafe)
    {
        moveFwd = 0f;
        moveStrafe = 0f;
        // 用上一帧记录的本地位置差估算水平位移
        Vector3 delta = pos - _lastLocalPos;
        _lastLocalPos = pos;
        if (Mathf.Abs(delta.y) > 2f) delta.y = 0f; // 防止场景切换/传送跳变
        delta.y = 0f;
        float dist = delta.magnitude;
        if (dist < 0.001f) return;
        // 速度方向（每 0.1s 一个 Interval，简单近似为方向向量；速度大小交给 Speed）
        var dir = delta / dist;
        float yawRad = yaw * Mathf.Deg2Rad;
        // 本地坐标系：+Z 朝前（面朝 yaw），+X 朝右
        var fwd = new Vector3(Mathf.Sin(yawRad), 0f, Mathf.Cos(yawRad));
        var right = new Vector3(Mathf.Cos(yawRad), 0f, -Mathf.Sin(yawRad));
        moveFwd = Vector3.Dot(dir, fwd);
        moveStrafe = Vector3.Dot(dir, right);
    }

    /// <summary>玩家是否空中（跳跃/下落）。优先 FPC.isGrounded。</summary>
    private static bool IsAirborne()
    {
        if (_fpc == null) return false;
        try { return !_fpc.isGrounded; }
        catch { return false; }
    }

    /// <summary>玩家是否蹲下。优先 FPC.isCrouched。</summary>
    private static bool IsCrouched()
    {
        if (_fpc == null) return false;
        try { return _fpc.isCrouched; }
        catch { return false; }
    }

    /// <summary>玩家是否奔跑。优先 FPC.isSprinting。</summary>
    private static bool IsSprinting()
    {
        if (_fpc == null) return false;
        try { return _fpc.isSprinting; }
        catch { return false; }
    }

    /// <summary>摄像机俯仰角（抬头为正，范围 -90~90）。驱动远端头部转向。</summary>
    private static float GetPitch()
    {
        if (_fpc != null)
        {
            try { return Mathf.Clamp(_fpc.pitch, -90f, 90f); }
            catch { }
        }
        return 0f;
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
        var a = new Avatar { PlayerId = p.PlayerId, Name = p.Name, Role = p.Role };        try
        {
            var tint = ColorFor(p.SteamId);
            var root = new GameObject($"CoopAvatar_{p.PlayerId}");
            // 造型选择：本地测试模式（LocalMode）显示骨架便于调动画；正常模式显示外部 3D 士兵模型
            var provider = ResolveProvider(CoopRuntime.Net);
            CoopLog.Info("PlayerSync.avatar", () => $"CreateAvatar: provider={provider.GetType().Name} pid={p.PlayerId}");
            GameObject visual = null;
            try { visual = provider.Create(root.transform, p.Name, tint); }
            catch (Exception ex) { CoopLog.Warn("PlayerSync.avatar", () => $"provider.Create: {ex.Message}"); }
            if (visual == null)
            {
                // 提供者失败 → 回退默认
                provider = HumanoidVisualProvider.Instance;
                visual = provider.Create(root.transform, p.Name, tint);
                CoopLog.Warn("PlayerSync.avatar", () => "CreateAvatar: fell back to HumanoidVisualProvider");
            }
            a.Root = root;
            a.Provider = provider;
            a.Visual = visual;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("PlayerSync.avatar", () => $"CreateAvatar: {ex.Message}");
        }
        return a;
    }

    /// <summary>
    /// 选择视觉提供者：
    /// - 注册的自定义 provider 始终优先。
    /// - 默认：本地测试模式（LocalMode=true）显示骨架（便于调动画）；正常模式显示外部 3D 士兵模型。
    /// - 额外开关（环境变量 ONC_MODEL 或配置文件 Models/oncmodel.txt，优先级：配置 > 环境变量）：
    ///     1 → 强制显示外部模型（本地测试想查看模型效果时用）
    ///     0 → 强制显示骨架（正常模式调试骨架用）
    ///     不设置 → 按模式默认（本地=骨架，正常=外部模型）
    /// </summary>
    private static IPlayerVisualProvider ResolveProvider(NetManager net)
    {
        if (PlayerVisualRegistry.Provider != null)
            return PlayerVisualRegistry.Provider;
        var localMode = net != null && net.LocalMode;

        int overrideMode = ReadModelOverride();
        CoopLog.Info("PlayerSync.provider", () => $"ResolveProvider: localMode={localMode} override={overrideMode}");

        // 方案 A：Animator AssetBundle 化身（Unity 引擎成熟链路：模型/骨骼/动画/Humanoid 重定向全交给引擎）。
        // ✅ 2026-08-15 打通：AssetBundle.LoadFromStream(Il2CppSystem.IO.FileStream) 是唯一可用入口
        // （参数是 Il2CppSystem.IO.Stream 对象指针，绕过该游戏 IL2CPP 对 span/byte[] 的裁剪）。
        // 托管 LoadFromFile/LoadFromMemory 被裁、原生 icall 直调崩游戏、UnityWebRequest 发送 icall 被裁。
        const bool animatorBundleEnabled = true;
        bool wantAnimator = animatorBundleEnabled && !localMode;   // 默认：正常模式优先 bundle，本地测试不用
        if (animatorBundleEnabled && overrideMode == 1) wantAnimator = true;      // 强制模型（含 bundle）
        else if (animatorBundleEnabled && overrideMode == -1) wantAnimator = false; // 强制骨架

        if (wantAnimator)
        {
            var anim = AnimatorAvatarVisualProvider.Instance;
            if (anim.TryLoad())
            {
                CoopLog.Info("PlayerSync.provider", () => "using AnimatorAvatarVisualProvider (AssetBundle, Unity Animator 真动画)");
                return anim;
            }
            CoopLog.Warn("PlayerSync.provider", () => "AssetBundle 未就绪，回退");
        }

        // 外部模型开关（2026-08-13）：绑定已改为 glTF IBM（修复扭曲），恢复启用验证方向B T-pose。
        // 若仍有问题可改回 false 禁用（代码完整保留）。双端 oncmodel.txt=1 强制模型便于本地测试。
        const bool externalModelEnabled = true;

        // 决定是否使用外部模型（方向B：动画已改为 T-pose 基准，外部模型零改动即可用）
        bool wantModel = externalModelEnabled && !localMode;   // 默认：正常模式用模型，本地测试不用
        if (externalModelEnabled && overrideMode == 1) wantModel = true;     // 强制模型
        else if (externalModelEnabled && overrideMode == -1) wantModel = false; // 强制骨架

        if (wantModel)
        {
            // 形象选择（环境变量 ONC_PROVIDER，避免每次改代码）：
            //   cat      → 克隆游戏猫船员（Unity 真 Animator 动画——唯一跑通真动画的方案）
            //   soldier  → 外部 3D 士兵模型（Soldier.glb = player.bundle 的模型源，SharpGLTF 自采样动画）
            //   humanoid → 程序化人形（骨架）
            //   默认     → 猫船员优先（真动画），失败回退士兵
            string providerChoice = "";
            try { providerChoice = (Environment.GetEnvironmentVariable("ONC_PROVIDER") ?? "").Trim().ToLowerInvariant(); }
            catch { }

            if (providerChoice == "soldier")
            {
                var ext = ExternalModelProvider.Instance;
                if (ext.TryLoad()) return ext;
            }
            else if (providerChoice == "humanoid")
            {
                return HumanoidVisualProvider.Instance;
            }
            else // 默认：人类士兵优先（ExternalModel = player.bundle 模型源 Soldier.glb，自采样动画）
            {
                var ext = ExternalModelProvider.Instance;
                if (ext.TryLoad())
                {
                    CoopLog.Info("PlayerSync.provider", () => "using ExternalModelProvider (人类士兵, 自采样动画)");
                    return ext;
                }
                var cat = CatCrewVisualProvider.Instance;
                if (cat.TryLoad())
                {
                    CoopLog.Info("PlayerSync.provider", () => "using CatCrewVisualProvider (克隆游戏猫船员, 真动画)");
                    return cat;
                }
            }
        }
        return HumanoidVisualProvider.Instance;
    }

    /// <summary>读取模型强制开关：返回 1=强制模型，-1=强制骨架，0=未设置。
    /// 优先级：配置文件 Models/oncmodel.txt > 环境变量 ONC_MODEL（兼容旧 ONC_MODEL_FORCE）。</summary>
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
            if (string.IsNullOrEmpty(v))
                v = Environment.GetEnvironmentVariable("ONC_MODEL_FORCE");
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
