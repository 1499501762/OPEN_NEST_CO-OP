using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 发射台开关序列同步（LookAtTargetUnlockSequence5，MsgType=110）。
/// 主机权威 + 剥离交互事件与数值（2026-08-15 重构）：
/// - 交互事件（EvClick=2）：玩家点击 slot（Harmony patch HandleSlotClicked）→ 上报/广播 →
///   对端执行 HandleSlotClicked（解锁逻辑 + 动画完整复现）。谁操作谁上报。
/// - 数值对账（EvState=1）：仅主机权威广播最终 count + toggledOn mask；客机接收后**直接设置**
///   toggledOn/count（不模拟点击——模拟点击触发 HandleSlotClicked 副作用，两端互相触发 →
///   mask 7↔0 每 0.5s 来回回跳根因）。客机不上行数值（避免互相覆盖）。
/// 防环：IsApplying（应用远端时不广播）。
/// </summary>
public sealed class SequenceSync : ISyncedModule
{
    public byte MsgType => 110;
    private const float Interval = 0.5f;
    private float _timer;
    /// <summary>序列对象 → 最近一次已知状态签名（检测变化才广播）。static：Apply 复现后也更新（防环）。</summary>
    private static readonly Dictionary<LookAtTargetUnlockSequence5, long> _known = new();
    /// <summary>应用远端事件/状态期间的防环标志（不广播应用产生的状态变化）。</summary>
    private static bool IsApplying;

    private const byte EvState = 1; // 数值对账（主机权威广播，客机直接设置）
    private const byte EvClick = 2; // 交互事件（操作端上报，对端执行 HandleSlotClicked）

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        // 数值对账仅主机权威广播：客机不上行数值（避免两端互相覆盖来回切换，2026-08-15）
        if (!net.IsHost) return;
        if (IsApplying) return; // 应用远端状态期间不广播
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
                w.Put(EvState);
                w.Put((byte)i);
                w.Put((byte)Math.Max(0, count));
                w.Put((byte)(mask & 0xFF));
                var data = NetProtocol.Snapshot(w);
                foreach (var p in net.Roster)
                    if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"SequenceSync Tick: {ex.Message}"); }
    }

    /// <summary>本地 slot 点击（Harmony patch HandleSlotClicked）→ 上报/广播（对端执行同样解锁逻辑）。</summary>
    public static void OnLocalSlotClick(LookAtTargetUnlockSequence5 seq, int slotIndex)
    {
        try
        {
            if (IsApplying) return; // 应用远端时不转发（防环）
            var net = CoopRuntime.Net;
            if (net == null || seq == null) return;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
            var seqs = UnityEngine.Object.FindObjectsOfType<LookAtTargetUnlockSequence5>();
            if (seqs == null) return;
            int idx = -1;
            for (int i = 0; i < seqs.Length; i++)
                if (seqs[i] != null && seqs[i].Pointer == seq.Pointer) { idx = i; break; }
            if (idx < 0) return;
            var w = NetProtocol.Begin((MsgType)110);
            w.Put(EvClick);
            w.Put((byte)idx);
            w.Put((byte)Math.Max(0, slotIndex));
            var data = NetProtocol.Snapshot(w);
            if (net.IsHost)
            {
                foreach (var p in net.Roster)
                    if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
            else if (net.HostSteamId != 0)
                net.Transport.Send(net.HostSteamId, data, true);
            // 操作端本地 HandleSlotClicked 已由原方法执行 → 更新已知状态（防环）
            int count = 0; try { count = seq.GetUnlockedSlotCount(); } catch { }
            _known[seq] = ((long)count << 8) | (uint)(ReadMask(seq) & 0xFF);
            CoopRuntime.LogSource?.LogInfo($"[SequenceSync] local slot click idx={idx} slot={slotIndex}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"SequenceSync OnLocalSlotClick: {ex.Message}"); }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            byte ev = r.GetByte();
            byte idx = r.GetByte();
            if (net.IsHost)
            {
                // 转发给其他客户端（星型拓扑）
                foreach (var p in net.Roster)
                    if (!p.IsLocal && (ulong)p.SteamId != from)
                        net.Transport.Send(p.SteamId, data, true);
            }
            if (ev == EvClick)
            {
                byte slotIndex = r.GetByte();
                ApplyClick(idx, slotIndex);
            }
            else if (ev == EvState)
            {
                byte count = r.GetByte();
                byte mask = r.GetByte();
                ApplyState(idx, count, mask);
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"SequenceSync OnPacket: {ex.Message}"); }
    }

    /// <summary>对端执行点击：HandleSlotClicked（解锁逻辑 + 动画完整复现操作端）。</summary>
    private static void ApplyClick(byte idx, byte slotIndex)
    {
        try
        {
            var seqs = UnityEngine.Object.FindObjectsOfType<LookAtTargetUnlockSequence5>();
            if (seqs == null || idx >= seqs.Length || seqs[idx] == null) return;
            var seq = seqs[idx];
            IsApplying = true;
            try { seq.HandleSlotClicked(slotIndex); }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"SequenceSync ApplyClick: {ex.Message}"); }
            finally { IsApplying = false; }
            int count = 0; try { count = seq.GetUnlockedSlotCount(); } catch { }
            _known[seq] = ((long)count << 8) | (uint)(ReadMask(seq) & 0xFF);
            CoopRuntime.LogSource?.LogInfo($"[SequenceSync] applied click idx={idx} slot={slotIndex}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"SequenceSync ApplyClick outer: {ex.Message}"); }
    }

    /// <summary>数值对账：直接设置 toggledOn/count（不模拟点击——避免 HandleSlotClicked 副作用来回切换）。</summary>
    private static void ApplyState(byte idx, byte count, byte mask)
    {
        try
        {
            var seqs = UnityEngine.Object.FindObjectsOfType<LookAtTargetUnlockSequence5>();
            if (seqs == null || idx >= seqs.Length || seqs[idx] == null) return;
            var seq = seqs[idx];
            IsApplying = true;
            try
            {
                var toggledOn = seq._toggledOn;
                if (toggledOn != null)
                {
                    int n = Math.Min((int)toggledOn.Length, 5);
                    for (int i = 0; i < n; i++)
                        toggledOn[i] = (mask & (1 << i)) != 0;
                }
                try { seq.SetUnlockedSlotCount(count); } catch { }
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"SequenceSync ApplyState: {ex.Message}"); }
            finally { IsApplying = false; }
            _known[seq] = ((long)count << 8) | (uint)(mask & 0xFF);
            CoopRuntime.LogSource?.LogInfo($"[SequenceSync] applied state idx={idx} count={count} mask={mask}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"SequenceSync ApplyState outer: {ex.Message}"); }
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

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }

    /// <summary>中途加入：主机构建当前所有开关序列状态快照（供 StateSnapshotSync 打包，EvState 格式）。</summary>
    public static byte[] BuildSequenceSnapshot()
    {
        try
        {
            var net = CoopRuntime.Net;
            if (net == null || !net.IsHost) return null;
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
                w.Put(EvState);
                w.Put((byte)i);
                w.Put((byte)Math.Max(0, count));
                w.Put((byte)(mask & 0xFF));
            }
            return NetProtocol.Snapshot(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"SequenceSync BuildSequenceSnapshot: {ex.Message}"); }
        return null;
    }

    /// <summary>中途加入：新成员应用开关序列状态快照（EvState 格式，直接设置）。</summary>
    public static void ApplySequenceSnapshot(byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int n = r.GetByte();
            for (int i = 0; i < n; i++)
            {
                byte ev = r.GetByte();
                byte idx = r.GetByte();
                byte count = r.GetByte();
                byte mask = r.GetByte();
                if (ev == EvState) ApplyState(idx, count, mask);
                else if (ev == EvClick) ApplyClick(idx, count); // 兼容：快照里 slotIndex 落在 count 位置
            }
            CoopRuntime.LogSource?.LogInfo($"[SequenceSync] apply snapshot n={n}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"SequenceSync ApplySequenceSnapshot: {ex.Message}"); }
    }

    public void Reset() { _known.Clear(); }
}
