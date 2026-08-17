using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 唱片机/播放器同步（主机权威 + 状态同步，复用 TurretSync 模式）。
/// - 主机：每 0.2s 轮询本地 RecordPlayerController（_isPlaying/_trackIndex/音量），变化检测广播 RecordState。
/// - 客户端：每 0.2s 轮询本地唱片机，检测到本地变化（用户操作）上行 RecordCmd 给主机。
/// - 主机收到 RecordCmd 应用到本地 → 下一轮广播 RecordState 给全员；客户端收到后应用（_applying 防环）。
/// 状态：isPlaying + trackIndex + masterVolume（不追求精确播放位置）。
/// </summary>
public static class RecordPlayerSync
{
    private const float Interval = 0.2f;
    private const float VolumeDeadzone = 0.01f;

    private static float _timer;
    private static RecordPlayerController _rp;
    private static bool _applying;
    private static int _sendLog;
    private static int _recvLog;

    // 客户端：本地已知状态（变化检测上行）
    private static bool _localKnown;
    private static bool _localPlaying;
    private static int _localTrack;
    private static float _localVolume;
    private static string _localRecName = "";

    // 主机：已知状态（变化检测广播）
    private static bool _hostKnown;
    private static bool _hostPlaying;
    private static int _hostTrack;
    private static float _hostVolume;
    private static string _hostRecName = "";

    public static void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;

        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;

        var rp = GetRecordPlayer();
        if (rp == null) return;

        bool playing = rp._isPlaying;
        int track = rp._trackIndex;
        float vol = rp.MasterVolume;
        // 槽里放的是哪张唱片（视觉插入动作）：取槽内物品名，空=无
        string recName = ReadSlotName(rp);
        bool hasRecord = !string.IsNullOrEmpty(recName);

        if (net.IsHost)
        {
            // 主机：变化检测广播
            bool changed = !_hostKnown || playing != _hostPlaying || track != _hostTrack
                || Mathf.Abs(vol - _hostVolume) > VolumeDeadzone
                || recName != _hostRecName;
            _hostKnown = true;
            _hostPlaying = playing; _hostTrack = track; _hostVolume = vol; _hostRecName = recName;
            if (changed) SendState(net, playing, track, vol, recName);
        }
        else if (!_applying)
        {
            // 客户端：本地变化（用户操作/游戏逻辑）→ 上行
            bool changed = !_localKnown || playing != _localPlaying || track != _localTrack
                || Mathf.Abs(vol - _localVolume) > VolumeDeadzone
                || recName != _localRecName;
            _localKnown = true;
            _localPlaying = playing; _localTrack = track; _localVolume = vol; _localRecName = recName;
            if (changed) SendCmd(net, playing, track, vol, recName);
        }
    }

    /// <summary>客户端 -> 主机：本地唱片机状态变化上行（主机应用后广播）。</summary>
    public static void OnCmd(byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null || !net.IsHost) return;
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
            // 更新主机已知状态，确保下一轮广播（即使变化检测被跳过）
            _hostKnown = true;
            _hostPlaying = rp._isPlaying; _hostTrack = rp._trackIndex; _hostVolume = rp.MasterVolume;
            _hostRecName = ReadSlotName(rp);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"RecordPlayerSync OnCmd: {ex.Message}"); }
    }

    /// <summary>主机 -> 客户端：应用唱片机状态。</summary>
    public static void OnState(byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return;
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
            // 同步本地已知状态，避免把应用结果再当本地变化上行
            _localKnown = true;
            _localPlaying = playing; _localTrack = track; _localVolume = vol; _localRecName = recName;
            if ((++_recvLog % 20) == 1)
                CoopRuntime.LogSource?.LogInfo($"[RecordPlayerSync] guest recv playing={playing} track={track} vol={vol:0.00} record='{recName}'");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"RecordPlayerSync OnState: {ex.Message}"); }
    }

    // ---------------- 内部 ----------------

    private static void ApplyState(RecordPlayerController rp, bool playing, int track, float volume, string recordName)
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
                // 远端槽空 → 本地清空槽位 + 无当前唱片
                if (rp.slot != null && rp.slot.HasItem) { try { rp.slot.ClearSlot(); } catch { } }
                try { rp._currentRecord = null; } catch { }
            }
            else
            {
                // 远端槽里有唱片 → 找到本地唱片放进槽（视觉插入动作）
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
                        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"RecordPlayerSync PlaceItem: {ex.Message}"); }
                    }
                    // 插入后把唱片位置对齐到槽 anchor（修正偏移，视觉上"放进槽里"）
                    if (rp.slot != null && rp.slot.itemAnchor != null)
                    {
                        try { record.gameObject.transform.position = rp.slot.itemAnchor.position; } catch { }
                    }
                }
                else
                {
                    // 单机解锁进度不同 → 主客机唱片 ID（RecordDisk_N）可能不对应同一张：
                    // 找不到同名唱片时不阻塞播放（playing/track 已同步），仅跳过插入视觉
                    CoopRuntime.LogSource?.LogInfo($"[RecordPlayerSync] record '{recordName}' not unlocked/missing locally, skip insert (playback-only sync)");
                }
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"RecordPlayerSync ApplyState: {ex.Message}"); }
        finally { _applying = false; }
    }

    private static string ReadSlotName(RecordPlayerController rp)
    {
        try
        {
            var slot = rp.slot;
            if (slot == null) return "";
            if (!slot.HasItem) return "";
            var it = slot.CurrentItem;
            return it != null && it.gameObject != null ? (it.gameObject.name ?? "") : "";
        }
        catch { return ""; }
    }

    private static void SendState(NetManager net, bool playing, int track, float volume, string recordName)
    {
        try
        {
            var w = NetProtocol.Begin(MsgType.RecordState);
            w.Put(playing);
            w.Put(track);
            w.Put(volume);
            w.Put(recordName ?? "");
            var data = NetProtocol.Snapshot(w);
            net.EnqueueBatch(data, true);
            if ((++_sendLog % 20) == 1)
                CoopRuntime.LogSource?.LogInfo($"[RecordPlayerSync] host send playing={playing} track={track} vol={volume:0.00} record='{recordName}'");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"RecordPlayerSync SendState: {ex.Message}"); }
    }

    private static void SendCmd(NetManager net, bool playing, int track, float volume, string recordName)
    {
        try
        {
            var w = NetProtocol.Begin(MsgType.RecordCmd);
            w.Put(playing);
            w.Put(track);
            w.Put(volume);
            w.Put(recordName ?? "");
            net.EnqueueBatch(NetProtocol.Snapshot(w), false);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"RecordPlayerSync SendCmd: {ex.Message}"); }
    }

    private static RecordPlayerController GetRecordPlayer()
    {
        // 不用一次性 _haveRp：唱片机在炮台场景才出现，场景切换后对象销毁需能重新找到
        try { if (_rp == null) _rp = UnityEngine.Object.FindFirstObjectByType<RecordPlayerController>(); }
        catch { _rp = null; }
        return _rp;
    }

    // ---------------- 中途加入快照（方案 B） ----------------

    /// <summary>中途加入：主机构建当前唱片机状态快照（供 StateSnapshotSync 打包）。</summary>
    public static byte[] BuildRecordPlayerSnapshot()
    {
        try
        {
            var rp = GetRecordPlayer();
            if (rp == null) return null;
            var w = NetProtocol.Begin(MsgType.RecordState);
            w.Put(rp._isPlaying);
            w.Put(rp._trackIndex);
            w.Put(rp.MasterVolume);
            w.Put(ReadSlotName(rp) ?? "");
            return NetProtocol.Snapshot(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"RecordPlayerSync BuildRecordPlayerSnapshot: {ex.Message}"); }
        return null;
    }

    /// <summary>中途加入：新成员应用唱片机状态快照。</summary>
    public static void ApplyRecordPlayerSnapshot(byte[] data)
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
            _localKnown = true;
            _localPlaying = playing; _localTrack = track; _localVolume = vol; _localRecName = recName;
            CoopRuntime.LogSource?.LogInfo($"[RecordPlayerSync] apply snapshot playing={playing} track={track} vol={vol:0.00} record='{recName}'");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"RecordPlayerSync ApplyRecordPlayerSnapshot: {ex.Message}"); }
    }
}
