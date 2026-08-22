using System;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 唱片机/播放器同步（RecordPlayerSyncV2，MsgType=208）。M7：把 V1 <c>RecordPlayerSync</c> 迁入分层架构。
/// - 权威语义：谁操作谁变更，经 <see cref="IHostStore.Broadcast"/> 传播（主机→全员 / 客机→主机中继，
///   主机应用后广播）；_applying 防环（约束#3）。
/// - 状态：isPlaying + trackIndex + masterVolume + 槽内唱片名（视觉插入动作）。
/// - 中途加入：OnLateJoin 主机单播当前状态给新成员（替代 V1 StateSnapshotSync "recordplayer" 集成）。
/// </summary>
public sealed class RecordPlayerSyncV2 : ISyncedModule
{
    public static RecordPlayerSyncV2 Instance { get; } = new RecordPlayerSyncV2();

    private RecordPlayerSyncV2() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2Record;

    private const float Interval = 0.2f;
    private const float VolumeDeadzone = 0.01f;
    private float _timer;
    private RecordPlayerController _rp;
    private bool _applying;
    private int _sendLog, _recvLog;

    // 客户端本地已知状态（变化检测上行）
    private bool _localKnown, _localPlaying;
    private int _localTrack;
    private float _localVolume;
    private string _localRecName = "";

    // 主机已知状态（变化检测广播）
    private bool _hostKnown, _hostPlaying;
    private int _hostTrack;
    private float _hostVolume;
    private string _hostRecName = "";

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;

        var rp = GetRecordPlayer();
        if (rp == null) return;
        bool playing = rp._isPlaying;
        int track = rp._trackIndex;
        float vol = rp.MasterVolume;
        string recName = ReadSlotName(rp);

        if (Store.IsHost)
        {
            bool changed = !_hostKnown || playing != _hostPlaying || track != _hostTrack
                || Mathf.Abs(vol - _hostVolume) > VolumeDeadzone || recName != _hostRecName;
            _hostKnown = true;
            _hostPlaying = playing; _hostTrack = track; _hostVolume = vol; _hostRecName = recName;
            if (changed) SendState(playing, track, vol, recName);
        }
        else if (!_applying)
        {
            bool changed = !_localKnown || playing != _localPlaying || track != _localTrack
                || Mathf.Abs(vol - _localVolume) > VolumeDeadzone || recName != _localRecName;
            _localKnown = true;
            _localPlaying = playing; _localTrack = track; _localVolume = vol; _localRecName = recName;
            if (changed) SendState(playing, track, vol, recName);
        }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            bool playing = r.GetBool();
            int track = r.GetInt();
            float vol = r.GetFloat();
            string recName = r.GetString();
            var rp = GetRecordPlayer();
            if (rp == null) return;
            ApplyState(rp, playing, track, vol, recName);
            if (Store.IsHost)
            {
                // 主机收到客机上行 → 应用后更新已知状态，下一轮变化检测广播给全员
                _hostKnown = true;
                _hostPlaying = rp._isPlaying; _hostTrack = rp._trackIndex; _hostVolume = rp.MasterVolume;
                _hostRecName = ReadSlotName(rp);
            }
            else
            {
                _localKnown = true;
                _localPlaying = playing; _localTrack = track; _localVolume = vol; _localRecName = recName;
            }
            if ((++_recvLog % 20) == 1)
                CoopRuntime.LogSource?.LogInfo($"[RecordSyncV2] recv playing={playing} track={track} vol={vol:0.00} record='{recName}'");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[RecordPlayerSyncV2] OnPacket: {ex.Message}"); }
    }

    public void OnLateJoin(ulong steamId)
    {
        // 主机：把当前状态单播给新加入成员（替代 V1 StateSnapshotSync "recordplayer"）
        if (Store.IsHost && steamId != 0)
        {
            var rp = GetRecordPlayer();
            if (rp == null) return;
            SendStateTo(rp._isPlaying, rp._trackIndex, rp.MasterVolume, ReadSlotName(rp), steamId);
        }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset()
    {
        _localKnown = false; _hostKnown = false;
        _applying = false; _timer = 0f;
    }

    // ---------------- 内部 ----------------

    private void SendState(bool playing, int track, float volume, string recName)
    {
        Store.Broadcast((byte)OpenNestCoop.Net.MsgType.V2Record, w =>
        {
            w.Put(playing);
            w.Put(track);
            w.Put(volume);
            w.Put(recName ?? "");
        }, reliable: false);
        if ((++_sendLog % 20) == 1)
            CoopRuntime.LogSource?.LogInfo($"[RecordSyncV2] send playing={playing} track={track} vol={volume:0.00} record='{recName}'");
    }

    private void SendStateTo(bool playing, int track, float volume, string recName, ulong peer)
    {
        try
        {
            var net = _net;
            if (net == null) return;
            var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2Record);
            w.Put(playing);
            w.Put(track);
            w.Put(volume);
            w.Put(recName ?? "");
            net.Transport.Send(peer, NetProtocol.Snapshot(w), true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[RecordPlayerSyncV2] SendStateTo: {ex.Message}"); }
    }

    private void ApplyState(RecordPlayerController rp, bool playing, int track, float volume, string recordName)
    {
        _applying = true;
        try
        {
            if (rp._trackIndex != track) rp._trackIndex = track;
            rp.SetMasterVolume(volume);
            if (playing && !rp._isPlaying) rp.StartPlayback();
            else if (!playing && rp._isPlaying) rp.StopPlayback();
            if (string.IsNullOrEmpty(recordName))
            {
                if (rp.slot != null && rp.slot.HasItem) { try { rp.slot.ClearSlot(); } catch { } }
                try { rp._currentRecord = null; } catch { }
            }
            else
            {
                var items = UnityEngine.Object.FindObjectsOfType<RecordItem>();
                RecordItem record = null;
                if (items != null)
                    foreach (var it in items)
                        if (it != null && it.gameObject != null && it.gameObject.name == recordName)
                        { record = it; break; }
                if (record != null)
                {
                    try { rp._currentRecord = record; } catch { }
                    var d = record.GetComponent<DraggableItem>();
                    if (d != null && !d.IsBeingDragged && rp.slot != null)
                    {
                        try { rp.slot.PlaceItem(d); }
                        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[RecordPlayerSyncV2] PlaceItem: {ex.Message}"); }
                    }
                    if (rp.slot != null && rp.slot.itemAnchor != null)
                    {
                        try { record.gameObject.transform.position = rp.slot.itemAnchor.position; } catch { }
                    }
                }
                else
                {
                    // 单机解锁进度不同 → 主客机唱片 ID 可能不对应同一张：跳过插入视觉，播放仍同步
                    CoopRuntime.LogSource?.LogInfo($"[RecordPlayerSyncV2] record '{recordName}' not unlocked/missing locally, skip insert");
                }
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[RecordPlayerSyncV2] ApplyState: {ex.Message}"); }
        finally { _applying = false; }
    }

    private string ReadSlotName(RecordPlayerController rp)
    {
        try
        {
            var slot = rp.slot;
            if (slot == null || !slot.HasItem) return "";
            var it = slot.CurrentItem;
            return it != null && it.gameObject != null ? (it.gameObject.name ?? "") : "";
        }
        catch { return ""; }
    }

    private RecordPlayerController GetRecordPlayer()
    {
        try { if (_rp == null) _rp = UnityEngine.Object.FindFirstObjectByType<RecordPlayerController>(); }
        catch { _rp = null; }
        return _rp;
    }
}
