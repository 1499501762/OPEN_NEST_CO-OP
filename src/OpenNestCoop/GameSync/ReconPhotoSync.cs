using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 侦察照片同步（seed 版，与反炮兵落点同构）。
/// 侦察照片是程序化生成的：拍照时各端用相同随机种子（UnityEngine.Random.InitState）
/// 本地生成一致的照片内容，无需传输纹理。
/// - 主机：拍照（MapReconClearHandle.RegisterChild）前生成新 seed + InitState + 广播（reliable）。
/// - 客户端：拍照前用最近收到的 seed InitState。
/// Harmony patch：RegisterChild prefix（生成前统一 seed，返回 true 继续原方法）。
/// </summary>
public sealed class ReconPhotoSync : ISyncedModule
{
    public int MsgType => 105;

    // ⚠️ 模块自注册：程序集加载时入队（V1 方案），Startup FlushPending 统一注册
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SelfRegister() => CoopSyncRegistry.PendingRegister(false, () => new ReconPhotoSync());
    public static ReconPhotoSync Instance;

    private int _seed;        // 主机：递增种子
    private int _pendingSeed; // 客户端：最近收到的种子
    private bool _havePending;
    private int _localSeq;    // 客户端：本地拍照序号（收到新主机 seed 时重置，对齐主机递增）
    // ⚠️ 2026-08-26：照片位置/方向由 MapTokenSync 统一同步（照片 = MapToken_Recon），ReconPhotoSync 只同步 seed。

    public ReconPhotoSync() { Instance = this; }

    /// <summary>拍照生成照片对象前调用（Harmony prefix）：统一随机种子 + 主机权威同步照片位置。
    /// ⚠️ 2026-08-15：客户端拍照 seed 可靠性修复——原实现用 seed 后 `_havePending=false` 消耗掉，
    /// 客户端连续拍照或 seed 到达延迟时用本地 Random → 照片拍摄方向不同（“着弹点照片方向不同步”根因）。
    /// 改为：客户端保留最新 seed，拍照用 `_pendingSeed + _localSeq` 本地递增近似主机递增（两端拍照计数一致时方向一致）。
    /// ⚠️ 2026-08-25：照片**位置**同步——seed 只同步内容，位置由各端本地相机算 → 战术地图上照片位置两端不同。
    /// 主机拍照广播照片对象本地坐标（相对战术地图），客机应用到自己的照片对象（两端 Tactical Map 位置一致）。
    /// ⚠️ 2026-08-26 照片位置/方向由 MapTokenSync 统一同步（照片 = MapToken_Recon，非拖拽也广播位置+方向）——
    /// ReconPhotoSync **只同步 seed（照片内容）**，不设照片位置（避免与 MapTokenSync 双重同步争抢 →
    /// "照片方向不同步"根因）。</summary>
    public void OnLocalPhoto(GameObject child)
    {
        var net = CoopRuntime.Net;
        if (net == null || (net.State != SessionState.Hosting && net.State != SessionState.Joined)) return;
        try
        {
            if (net.IsHost)
            {
                _seed++;
                UnityEngine.Random.InitState(_seed);
                CoopRuntime.LogSource?.LogInfo($"[ReconPhoto] host photo seed={_seed} broadcast");
                BroadcastSeed(net, _seed);
            }
            else
            {
                if (_havePending)
                {
                    UnityEngine.Random.InitState(_pendingSeed + _localSeq);
                    CoopRuntime.LogSource?.LogInfo($"[ReconPhoto] client photo seed={_pendingSeed}+{_localSeq}={_pendingSeed + _localSeq}");
                    _localSeq++; // 保留 _havePending：客户端可能连续拍照，本地序号递增近似主机递增
                    // ⚠️ 2026-08-26：照片位置/方向由 MapTokenSync 同步（照片 = MapToken_Recon），
                    // 此处不设 child 位置（避免双重同步争抢）。
                }
                else
                {
                    CoopRuntime.LogSource?.LogInfo("[ReconPhoto] client photo NO PENDING SEED (local random)");
                }
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReconPhotoSync OnLocalPhoto: {ex.Message}"); }
    }

    public void Tick(float dt) { /* 事件驱动 */ }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return; // 仅客户端处理
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            _pendingSeed = r.GetInt();
            _havePending = true;
            _localSeq = 0; // 收到新主机 seed：本地序号重置（对齐主机当前拍照序号）
            // ⚠️ 2026-08-26：照片位置/方向由 MapTokenSync 同步，此处只读 seed（跳过位置字段，兼容旧格式）
            // 不再读位置（避免双重同步争抢）。
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ReconPhotoSync OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }

    public void Reset()
    {
        _seed = 0; _pendingSeed = 0; _havePending = false; _localSeq = 0;
    }

    private void BroadcastSeed(NetManager net, int seed)
    {
        var w = NetProtocol.Begin((MsgType)MsgType);
        w.Put(seed);
        var data = NetProtocol.Snapshot(w);
        // ⚠️ 直发 Transport.Send 未达客机 → 改用 EnqueueBatch 合包（已知工作，照片 seed 也走合包）
        net.EnqueueBatch(data, true, true);
    }
}
