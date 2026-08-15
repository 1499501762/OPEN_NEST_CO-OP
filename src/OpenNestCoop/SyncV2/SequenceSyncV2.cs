using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 发射台开关序列同步（SequenceSyncV2，MsgType=220）。M7：把 V1 <c>SequenceSync</c>（110）迁入分层架构。
/// 谁变化谁广播（Operators 语义：count + 按下开关掩码），对端模拟点击差异开关（OnClickDown/Up）刷新视觉逻辑；
/// 主机中继。OnLateJoin 主机单播全量（替代 V1 StateSnapshotSync "sequence"）。
/// </summary>
public sealed class SequenceSyncV2 : ISyncedModule
{
    public static SequenceSyncV2 Instance { get; } = new SequenceSyncV2();

    private SequenceSyncV2() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2Sequence;

    private const float Interval = 0.5f;
    private float _timer;
    private readonly Dictionary<LookAtTargetUnlockSequence5, long> _known = new();

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        try
        {
            var seqs = UnityEngine.Object.FindObjectsOfType<LookAtTargetUnlockSequence5>();
            if (seqs == null || seqs.Length == 0) return;
            for (int i = 0; i < seqs.Length; i++)
            {
                var seq = seqs[i];
                if (seq == null) continue;
                int count = 0; try { count = seq.GetUnlockedSlotCount(); } catch { }
                int mask = ReadMask(seq);
                long state = ((long)count << 8) | (uint)(mask & 0xFF);
                if (_known.TryGetValue(seq, out var last) && last == state) continue;
                _known[seq] = state;
                Store.Broadcast((byte)OpenNestCoop.Net.MsgType.V2Sequence, w =>
                {
                    w.Put((byte)i);
                    w.Put((byte)Math.Max(0, count));
                    w.Put((byte)(mask & 0xFF));
                }, reliable: false);
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[SequenceSyncV2] Tick: {ex.Message}"); }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            byte idx = r.GetByte();
            byte count = r.GetByte();
            byte mask = r.GetByte();
            if (Store.IsHost) _net?.EnqueueBatch(data, true); // 转发
            Apply(idx, count, mask);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[SequenceSyncV2] OnPacket: {ex.Message}"); }
    }

    public void OnLateJoin(ulong steamId)
    {
        if (Store.IsHost && steamId != 0)
        {
            var seqs = UnityEngine.Object.FindObjectsOfType<LookAtTargetUnlockSequence5>();
            if (seqs == null || seqs.Length == 0) return;
            var net = _net;
            if (net == null) return;
            try
            {
                // 与 Tick 广播一致的【单条格式 [idx][count][mask]】逐条发送（避免打包格式与 OnPacket 解析不一致）。
                for (int i = 0; i < seqs.Length; i++)
                {
                    var seq = seqs[i];
                    if (seq == null) continue;
                    int count = 0; try { count = seq.GetUnlockedSlotCount(); } catch { }
                    int mask = ReadMask(seq);
                    var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2Sequence);
                    w.Put((byte)i);
                    w.Put((byte)Math.Max(0, count));
                    w.Put((byte)(mask & 0xFF));
                    net.Transport.Send(steamId, NetProtocol.Snapshot(w), true);
                }
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[SequenceSyncV2] OnLateJoin: {ex.Message}"); }
        }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _known.Clear(); }

    private static int ReadMask(LookAtTargetUnlockSequence5 seq)
    {
        int mask = 0;
        try
        {
            if (seq._toggledOn != null)
                for (int i = 0; i < seq._toggledOn.Length && i < 8; i++)
                    if (seq._toggledOn[i]) mask |= (1 << i);
        }
        catch { }
        return mask;
    }

    private void Apply(byte idx, int count, int mask)
    {
        try
        {
            var seqs = UnityEngine.Object.FindObjectsOfType<LookAtTargetUnlockSequence5>();
            if (seqs == null || idx >= seqs.Length || seqs[idx] == null) return;
            var seq = seqs[idx];
            if (ReadMask(seq) == (mask & 0xFF)) return;
            var toggledOn = seq._toggledOn;
            var slots = new[] { seq.slot1, seq.slot2, seq.slot3, seq.slot4, seq.slot5 };
            int n = toggledOn != null ? (int)toggledOn.Length : 0;
            for (int i = 0; i < n && i < 5; i++)
            {
                bool want = (mask & (1 << i)) != 0;
                bool cur = toggledOn[i];
                if (cur == want) continue;
                var slot = slots[i];
                if (slot != null)
                {
                    try
                    {
                        slot.isClicked = false;
                        slot.isActive = true;
                        slot.OnClickDown();
                        slot.OnClickUp();
                    }
                    catch { }
                }
                toggledOn[i] = want;
            }
            try { seq.SetUnlockedSlotCount(count); } catch { }
            // 防环：应用后同步本地已知状态，避免下轮 Tick 把刚应用的状态当“本地变化”回广播
            _known[seq] = ((long)count << 8) | (uint)(mask & 0xFF);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[SequenceSyncV2] Apply: {ex.Message}"); }
    }
}
