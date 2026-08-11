using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 发射台开关序列同步（LookAtTargetUnlockSequence5，MsgType=110）。
/// 同步每个序列的已解锁槽位计数 + 按下开关掩码（_toggledOn）。
/// 应用时模拟点击差异开关（OnClickDown/OnClickUp），刷新视觉与逻辑。
/// </summary>
public sealed class SequenceSync : ISyncedModule
{
    public byte MsgType => 110;
    private const float Interval = 0.5f;
    private float _timer;
    private readonly Dictionary<LookAtTargetUnlockSequence5, long> _known = new();

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
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
                var w = NetProtocol.Begin((MsgType)MsgType);
                w.Put((byte)i);
                w.Put((byte)Math.Max(0, count));
                w.Put((byte)(mask & 0xFF));
                var data = NetProtocol.Snapshot(w);
                if (net.IsHost) net.EnqueueBatch(data, true);
                else net.EnqueueBatch(data, false);
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"SequenceSync Tick: {ex.Message}"); }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            byte idx = r.GetByte();
            byte count = r.GetByte();
            byte mask = r.GetByte();
            if (net.IsHost) net.EnqueueBatch(data, true); // 转发
            Apply(idx, count, mask);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"SequenceSync OnPacket: {ex.Message}"); }
    }

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

    private static void Apply(byte idx, int count, int mask)
    {
        try
        {
            var seqs = UnityEngine.Object.FindObjectsOfType<LookAtTargetUnlockSequence5>();
            if (seqs == null || idx >= seqs.Length || seqs[idx] == null) return;
            var seq = seqs[idx];
            if (ReadMask(seq) == (mask & 0xFF)) return; // 无变化
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
            CoopRuntime.LogSource?.LogInfo($"[SequenceSync] applied idx={idx} count={count} mask={mask}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"SequenceSync Apply: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }

    /// <summary>中途加入：主机构建当前所有开关序列状态快照（供 StateSnapshotSync 打包）。</summary>
    public static byte[] BuildSequenceSnapshot()
    {
        try
        {
            var seqs = UnityEngine.Object.FindObjectsOfType<LookAtTargetUnlockSequence5>();
            if (seqs == null || seqs.Length == 0) return null;
            var w = NetProtocol.Begin((MsgType)110);
            w.Put((byte)Math.Min(seqs.Length, 255));
            int written = 0;
            for (int i = 0; i < seqs.Length && written < 255; i++)
            {
                var seq = seqs[i];
                if (seq == null) continue;
                written++;
                int count = 0; try { count = seq.GetUnlockedSlotCount(); } catch { }
                int mask = ReadMask(seq);
                w.Put((byte)i);
                w.Put((byte)Math.Max(0, count));
                w.Put((byte)(mask & 0xFF));
            }
            return NetProtocol.Snapshot(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"SequenceSync BuildSequenceSnapshot: {ex.Message}"); }
        return null;
    }

    /// <summary>中途加入：新成员应用开关序列状态快照。</summary>
    public static void ApplySequenceSnapshot(byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int n = r.GetByte();
            for (int i = 0; i < n; i++)
            {
                byte idx = r.GetByte();
                byte count = r.GetByte();
                byte mask = r.GetByte();
                Apply(idx, count, mask);
            }
            CoopRuntime.LogSource?.LogInfo($"[SequenceSync] apply snapshot n={n}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"SequenceSync ApplySequenceSnapshot: {ex.Message}"); }
    }

    public void Reset() { _known.Clear(); }
}
