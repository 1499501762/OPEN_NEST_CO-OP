using System;
using System.Collections.Generic;
using OpenNestCoop.Core;

namespace OpenNestCoop.Net;

/// <summary>网络负载分级（Quality Tier）：Critical 最低负载 → High 全速。</summary>
public enum NetQualityTier
{
    /// <summary>最低保底：模块频率 ×0.25、合包上限 64、拆包 400B、关闭 unreliable 通道（只 reliable 保底对齐）。</summary>
    Critical = 0,
    /// <summary>低负载：模块频率 ×0.5、合包上限 128、拆包 600B。</summary>
    Low = 1,
    /// <summary>标准：模块频率 ×0.75、合包上限 192、拆包 800B。</summary>
    Normal = 2,
    /// <summary>全速（默认）：模块频率 ×1.0、合包上限 256、拆包 1000B。</summary>
    High = 3,
}

/// <summary>模块网络优先级（per-module 分级）：同一全局档位下各模块的频率缩放不同。
/// 声明在 <see cref="ISyncedModule.NetPriority"/>；高频/容忍丢失的模块设 Low/Bulk（全局降频时优先降），
/// 关键交互（控件/装填/玩家位置）设 Critical/High（几乎不降）。</summary>
public enum NetModulePriority
{
    /// <summary>关键交互（控件/装填/玩家位置）：全局降频时几乎不降。</summary>
    Critical = 0,
    /// <summary>重要状态：轻微降频。</summary>
    High = 1,
    /// <summary>常规（默认）。</summary>
    Normal = 2,
    /// <summary>容忍丢失的高频状态（实体/猫/位置）：优先降频。</summary>
    Low = 3,
    /// <summary>批量/低频/非关键：降频最多。</summary>
    Bulk = 4,
}

/// <summary>
/// 网络负载调控器（NetworkGovernor，单例）：**分级 + 动态**控制联机网络负载（Steam P2P 硬限制）。
///
/// 背景：Steam P2P unreliable 单包上限约 1200B（超限整包被拒收），高频下丢包率高；reliable 大包
/// 显著增加延迟/拥塞。任务场景内大量周期状态（EntitySync/ControlSync/PlayerSync 等）可能让单帧
/// 合包逼近 Steam 硬限制 → 需要按网络状况分级降频/降流量保护（否则拥塞 → 丢包 → 重传 → 更拥塞）。
///
/// 机制（分三层控制面）：
/// - **频率缩放（模块侧）**：<see cref="FreqMultiplier"/> —— NetManager.UpdateCommon 把模块 Tick 的
///   dt 统一乘此系数（dtScaled）。模块 `_timer += dtScaled` 走得更慢 → 周期状态自然降频，
///   **无需改动任何模块**（对 V1 硬编码模块 + CoopSyncRegistry 注册模块统一生效）。
/// - **流量上限（NetManager 侧）**：<see cref="MaxBatchItems"/>（合包缓冲上限，超限丢弃 + 记负载信号）、
///   <see cref="MaxPacketBytes"/>（reliable 拆包阈值，低档更小包 → 单包延迟低）、
///   <see cref="UnreliableMaxBytes"/>（unreliable 单条安全上限）。
/// - **通道策略**：<see cref="AllowUnreliable"/> —— Critical 档关闭 unreliable 通道（高频连续状态直接
///   丢弃，发送端 reliable 保底/心跳负责最终对齐），最大化省带宽。
///
/// 动态评估：每 1s 采样（发送字节/合包丢弃/队列峰值/RTT）→ 自动升降级；带**滞回 + 冷却**防抖动。
/// 手动：<see cref="SetTier"/> 锁定等级（如玩家手动选"低负载"），<see cref="SetAuto"/> 恢复自动。
/// 联机命令行参数：`--nettier &lt;critical|low|normal|high&gt;`（AutoJoin 解析，先于 SetAuto 生效则锁手动）。
/// </summary>
public sealed class NetworkGovernor
{
    public static NetworkGovernor Instance { get; } = new();
    private NetworkGovernor() { }

    private sealed class TierCfg
    {
        public float Freq;            // 模块 Tick 频率缩放系数（1 = 全速）
        public int MaxBatchItems;     // 合包缓冲上限（超限丢弃）
        public int MaxPacketBytes;    // reliable 拆包阈值（B）
        public int UnreliableMaxBytes;// unreliable 单条安全上限（B）
        public bool AllowUnreliable;  // 是否启用 unreliable 通道

        public TierCfg(float freq, int items, int pkt, int urMax, bool allowUr)
        {
            Freq = freq; MaxBatchItems = items; MaxPacketBytes = pkt;
            UnreliableMaxBytes = urMax; AllowUnreliable = allowUr;
        }
    }

    private static readonly TierCfg[] _tiers =
    {
        // ⚠️ 2026-08-25：Critical 档 AllowUnreliable 改为 true——**不关闭 unreliable 通道**。
        // 理由：unreliable 承载高频连续状态（炮塔值/玩家位置），彻底关闭 = 两端明显不同步
        // （炮弹落点错/化身卡顿）。负载控制靠 Freq 降频（高频状态自动变慢），不靠吞包。
        new(0.25f,   64, 400,  600, true),  // Critical（仅降频至 ×0.25 + 小包，不吞 unreliable）
        new(0.50f,  128, 600,  800, true),  // Low
        new(0.75f,  192, 800, 1000, true),  // Normal
        new(1.00f,  256, 1000, 1100, true), // High（默认，与原 BatchMaxItems/MaxPacketBytes/UnreliableMaxBytes 一致）
    };

    // ⚠️ per-module 分级系数表：行=全局档位（Critical/Low/Normal/High），列=模块优先级（Critical/High/Normal/Low/Bulk）。
    // 含义：同一全局档位下，关键模块（Critical/High）几乎不降频，高频容忍丢失模块（Low/Bulk）优先降频——
    // 比统一 dt 缩放更精细（交互保流畅、高频状态让路）。
    private static readonly float[,] _priorityTable =
    {
        //                 Critical  High   Normal  Low    Bulk
        /* Critical */  { 1.00f,    0.80f,  0.55f,  0.40f,  0.25f },
        /* Low */       { 1.00f,    0.90f,  0.70f,  0.55f,  0.40f },
        /* Normal */    { 1.00f,    0.95f,  0.85f,  0.75f,  0.60f },
        /* High */      { 1.00f,    1.00f,  1.00f,  1.00f,  1.00f },
    };

    private NetQualityTier _tier = NetQualityTier.High;
    private bool _manual;

    // ---- 采样（1s 窗口）----
    private float _window;
    private long _sentBytes;     // 窗口内实际发送字节（含 peers 倍数）
    private int _dropCount;      // 窗口内合包超限丢弃数
    private int _queuePeak;      // 窗口内合包队列峰值
    private float _rttMs;        // 窗口内最大 RTT（Ping/Pong 每 3s 一次）
    private float _rttBaseline;  // RTT 平滑基线（EMA：上升慢跟 0.1 / 下降快跟 0.5）——拥塞/健康判定参照
    // 丢包率（Ping/Pong unreliable 探测，滚动窗口 LossWindowSec）——多维评估的丢包率维度
    private int _pingSent;       // 当前滚动窗口累计发出 Ping
    private int _pongRecv;       // 当前滚动窗口累计收到 Pong
    private float _lossWindow;   // 滚动窗口计时（s）
    private float _lossRate = -1f; // 最近结算丢包率（0-1；-1 = 样本不足，不参与评估）
    private float _cooldown;     // 升降级冷却（滞回，防抖动）
    /// <summary>窗口内各 MsgType 入队字节（per-module 带宽占用，EnqueueBatch 归因；1s 重置）。</summary>
    private readonly long[] _typeBytes = new long[256];

    // ---- 自动评估阈值 ----
    // ---- 自动评估阈值（多维：流量 / 延迟 / 丢包率 / 队列，组合判定防单维度误报）----
    private const float DownBytesPerSec = 40_000f;  // 流量维度：发送 > 40KB/s → 拥塞维度命中
    private const float UpBytesPerSec = 10_000f;    // 发送 < 10KB/s → 空闲
    private const float HardBytesPerSec = 80_000f;  // 流量爆表：> 80KB/s → 单维度即降（硬信号）
    // ⚠️ 2026-08-25：RTT 判拥塞用【相对基线】（延迟维度），不用绝对阈值。
    // 绝对 180ms 在模拟延迟（--lag 170 → RTT ~450ms）或高延迟网络下必然判拥塞 → 持续降到 Critical →
    // 所有模块降频（×0.25）→ 同步明显变慢（用户反馈“直接降级”）。**延迟高 ≠ 拥塞**（固有延迟无法靠降频缓解），
    // 只有 RTT 相对基线明显上升（真拥塞的队列积压表现）才算延迟维度拥塞。
    private const float RttRiseThresholdMs = 150f;  // 延迟维度：RTT 比基线上升 > 150ms → 拥塞
    private const float RttHealthyDeltaMs = 60f;    // RTT 相对基线差 < 60ms → 延迟健康
    // 丢包率维度（Ping/Pong unreliable 探测；滚动窗口 LossWindowSec）
    private const float LossWindowSec = 24f;        // 滚动窗口（24s ≈ 8 个 Ping，粒度 ~12.5%）
    private const float DownLossRate = 0.10f;       // 丢包率 > 10% → 拥塞维度命中
    private const float UpLossRate = 0.03f;         // 丢包率 < 3% → 健康
    private const float HardLossRate = 0.25f;       // 丢包率 > 25% → 单维度即降（硬信号）
    private const int MinLossSamples = 4;           // 至少 4 个 Ping 样本才启用丢包率维度
    private const float CooldownSec = 3f;           // 升降级后冷却（秒）——滞回防抖动

    // ---------------- 查询（供 NetManager / 模块 / UI）----------------

    public NetQualityTier Tier => _tier;
    public bool IsManual => _manual;
    /// <summary>全局档位的频率基准（= Normal 优先级模块的系数，UI/文档展示档位强度用）。
    /// 实际驱动用 <see cref="ModuleFreq"/>（按模块优先级精细化）。</summary>
    public float FreqMultiplier => _priorityTable[(int)_tier, (int)NetModulePriority.Normal];
    public int MaxBatchItems => _tiers[(int)_tier].MaxBatchItems;
    public int MaxPacketBytes => _tiers[(int)_tier].MaxPacketBytes;
    public int UnreliableMaxBytes => _tiers[(int)_tier].UnreliableMaxBytes;
    public bool AllowUnreliable => _tiers[(int)_tier].AllowUnreliable;

    /// <summary>某模块优先级在当前全局档位下的频率缩放系数（per-module 分级）。
    /// 驱动：CoopSyncRegistry.TickAll 每模块 dt×本系数；V1 硬编码模块由 NetManager 逐个指定。</summary>
    public float ModuleFreq(NetModulePriority priority)
        => _priorityTable[(int)_tier, (int)priority];

    // ---- 当前采样窗口（只读，供 F9 网络诊断 UI 显示）----
    public long CurrentSentBytes => _sentBytes;
    public int CurrentDropCount => _dropCount;
    public int CurrentQueuePeak => _queuePeak;
    public float CurrentRttMs => _rttMs;
    /// <summary>最近结算丢包率（0-1；-1 = 样本不足，不参与评估）。</summary>
    public float CurrentLossRate => _lossRate;

    // ---------------- 驱动（NetManager.UpdateCommon 每帧调用）----------------

    public void Tick(float dt)
    {
        if (_manual) return; // 手动锁定：不自动调
        _cooldown = Math.Max(0f, _cooldown - dt);
        _window += dt;
        if (_window < 1f) return;
        _window = 0f;

        var cfg = _tiers[(int)_tier];
        // RTT 基线（EMA，仅窗口内测得 RTT 时更新）：上升慢跟（0.1）、下降快跟（0.5）——稳定高 RTT 基线也高，不误判
        if (_rttMs > 0f)
        {
            if (_rttBaseline <= 0f) _rttBaseline = _rttMs;
            else _rttBaseline += (_rttMs - _rttBaseline) * (_rttMs > _rttBaseline ? 0.1f : 0.5f);
        }
        // 丢包率滚动窗口结算（每 LossWindowSec 结算一次，用上一窗口丢包率评估；样本不足记 -1 忽略维度）
        _lossWindow += 1f;
        if (_lossWindow >= LossWindowSec)
        {
            _lossWindow = 0f;
            _lossRate = _pingSent >= MinLossSamples ? (float)(_pingSent - _pongRecv) / _pingSent : -1f;
            _pingSent = 0; _pongRecv = 0;
        }

        // ---- 多维评估：流量 / 延迟 / 丢包率 / 队列 ----
        bool flowHigh = _sentBytes > DownBytesPerSec;                              // 流量维度
        bool flowBurst = _sentBytes > HardBytesPerSec;                             // 硬：流量爆表
        bool rttCongested = _rttMs > 0f && _rttBaseline > 0f && _rttMs > _rttBaseline + RttRiseThresholdMs; // 延迟维度（相对基线上升）
        bool lossHigh = _lossRate >= DownLossRate;                                 // 丢包率维度
        bool lossBad = _lossRate >= HardLossRate;                                  // 硬：高丢包
        bool queueHigh = _queuePeak >= cfg.MaxBatchItems;                          // 队列维度
        // 降级：≥2 个维度同时拥塞（组合判定，防单维度误报）或任一硬信号（流量爆表/高丢包/合包主动丢弃）
        int dims = (flowHigh ? 1 : 0) + (rttCongested ? 1 : 0) + (lossHigh ? 1 : 0) + (queueHigh ? 1 : 0);
        bool congested = dims >= 2 || flowBurst || lossBad || _dropCount > 0;
        // 升档：全维度健康（无丢弃、流量低、延迟相对健康、丢包低、队列不紧张）
        bool rttHealthy = _rttMs <= 0f || _rttBaseline <= 0f || _rttMs < _rttBaseline + RttHealthyDeltaMs;
        bool lossHealthy = _lossRate < 0f || _lossRate < UpLossRate;
        bool idle = _dropCount == 0 && !flowHigh && rttHealthy && lossHealthy
            && _sentBytes < UpBytesPerSec && _queuePeak < cfg.MaxBatchItems * 0.6f;

        int old = (int)_tier;
        if (_cooldown <= 0f)
        {
            if (congested && _tier > NetQualityTier.Critical)
            {
                _tier = (NetQualityTier)((int)_tier - 1);
                _cooldown = CooldownSec;
                CoopLog.Info("net.governor", () => $"[NetGovernor] DOWN {old}→{_tier} sent={_sentBytes}B drop={_dropCount} rtt={_rttMs:0}ms base={_rttBaseline:0}ms loss={_lossRate * 100f:0}% peak={_queuePeak} dims={dims}");
            }
            else if (!congested && idle && _tier < NetQualityTier.High)
            {
                _tier = (NetQualityTier)((int)_tier + 1);
                _cooldown = CooldownSec * 0.6f;
                CoopLog.Info("net.governor", () => $"[NetGovernor] UP {old}→{_tier} sent={_sentBytes}B rtt={_rttMs:0}ms base={_rttBaseline:0}ms loss={_lossRate * 100f:0}%");
            }
        }

        // 复位采样
        _sentBytes = 0; _dropCount = 0; _queuePeak = 0; _rttMs = 0f;
        Array.Clear(_typeBytes, 0, 256);
    }

    // ---------------- 采样喂入（NetManager 调用）----------------

    /// <summary>记录实际发送字节（含 peers 倍数）。</summary>
    public void RecordSent(int bytes) { if (bytes > 0) _sentBytes += bytes; }

    /// <summary>记录一次合包超限丢弃（负载过高信号）。</summary>
    public void RecordDrop() { _dropCount++; }

    /// <summary>记录合包队列长度（峰值采样）。</summary>
    public void RecordQueue(int count) { if (count > _queuePeak) _queuePeak = count; }

    /// <summary>记录一次 RTT 测量（取窗口内最大）。</summary>
    public void RecordRtt(float ms)
    {
        if (ms > _rttMs) _rttMs = ms;
        else if (_rttMs <= 0f) _rttMs = ms;
    }

    /// <summary>记录发出一个 Ping（unreliable 探测，丢包率统计）。NetManager 发 Ping 后调用。</summary>
    public void RecordPingSent() { _pingSent++; }

    /// <summary>记录收到一个 Pong（丢包率统计）。NetManager OnPong 后调用。</summary>
    public void RecordPongRecv() { _pongRecv++; }

    /// <summary>记录某 MsgType 本帧入队字节（per-module 带宽占用，按类型归因；窗口 1s 重置）。
    /// NetManager.EnqueueBatch 调用——反映"每模块产生多少带宽"（含会被丢弃/限流的量）。</summary>
    public void RecordTypeBytes(byte msgType, int bytes)
    {
        if (bytes > 0) _typeBytes[msgType] += bytes; // msgType 是 byte，天然 <256
    }

    /// <summary>当前窗口各 MsgType 字节占用（降序，取前 n；供 F8 诊断 UI/日志）。</summary>
    public List<(byte Type, long Bytes)> GetTypeBytesTop(int n)
    {
        var list = new List<(byte Type, long Bytes)>();
        for (int i = 0; i < 256; i++)
            if (_typeBytes[i] > 0) list.Add(((byte)i, _typeBytes[i]));
        list.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        if (list.Count > n) list.RemoveRange(n, list.Count - n);
        return list;
    }

    // ---------------- 手动 / 自动 ----------------

    /// <summary>手动锁定等级（null = 恢复自动）。用于 UI 或命令行 --nettier。</summary>
    public void SetTier(NetQualityTier? tier)
    {
        if (tier == null)
        {
            if (_manual)
            {
                _manual = false;
                CoopLog.Info("net.governor", () => $"[NetGovernor] auto mode (current {_tier})");
            }
            return;
        }
        _manual = true;
        _tier = tier.Value;
        CoopLog.Info("net.governor", () => $"[NetGovernor] manual tier={_tier} (freq={FreqMultiplier:0.00} batch={MaxBatchItems} pkt={MaxPacketBytes}B unreliable={(AllowUnreliable ? "on" : "off")})");
    }
}
