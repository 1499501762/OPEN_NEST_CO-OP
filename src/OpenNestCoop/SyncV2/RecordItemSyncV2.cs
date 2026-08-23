using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 唱片位置同步（RecordItemSyncV2，MsgType=218）。M7：把 V1 <c>RecordItemSync</c>（108）迁入分层架构。
/// <see cref="V2Authority.Host"/>：主机变化检测广播所有唱片位置（首次全量，之后只发变化的）；
/// 客机放下瞬间上行该唱片位置一次（主机权威应用后广播）。按名字匹配本地唱片（解决两端数量/顺序不一致）。
/// </summary>
public sealed class RecordItemSyncV2 : ISyncedModule
{
    public static RecordItemSyncV2 Instance { get; } = new RecordItemSyncV2();

    private RecordItemSyncV2() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2RecordItem;

    private const float Interval = 0.2f;
    private float _timer;
    private bool _applying;
    private int _sendLog, _recvLog;
    private readonly Dictionary<string, string> _hostSig = new();
    private readonly Dictionary<RecordItem, bool> _dragState = new();

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        try
        {
            var items = UnityEngine.Object.FindObjectsOfType<RecordItem>();
            if (items == null || items.Length == 0) return;
            if (Store.IsHost) HostSendChanges(items);
            else if (!_applying) ClientSendDropEvents(items);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[RecordItemSyncV2] Tick: {ex.Message}"); }
    }

    private void HostSendChanges(RecordItem[] items)
    {
        var net = _net;
        if (net == null) return;
        int n = Math.Min(items.Length, 16);
        var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2RecordItem);
        w.Put((byte)n);
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            var it = items[i];
            bool inSlot = it != null && IsInRecordSlot(it);
            if (it == null) { w.Put(""); w.Put((byte)2); w.Put(0f); w.Put(0f); w.Put(0f); continue; }
            string nm = it.name ?? "";
            var p = it.transform.position;
            string sig = $"{(inSlot ? 1 : 0)}|{p.x:0.###}|{p.y:0.###}|{p.z:0.###}";
            if (_hostSig.TryGetValue(nm, out var last) && last == sig) { w.Put(nm); w.Put((byte)2); w.Put(0f); w.Put(0f); w.Put(0f); continue; }
            _hostSig[nm] = sig;
            any = true;
            w.Put(nm);
            w.Put(inSlot ? (byte)1 : (byte)0);
            w.Put(p.x); w.Put(p.y); w.Put(p.z);
        }
        if (!any && _hostSig.Count > 0) return;
        net.EnqueueBatch(NetProtocol.Snapshot(w), true);
        if ((++_sendLog % 15) == 1) CoopRuntime.LogSource?.LogInfo($"[RecordItemSyncV2] host send n={n} sig={_hostSig.Count}");
    }

    private void ClientSendDropEvents(RecordItem[] items)
    {
        var net = _net;
        if (net == null) return;
        int n = Math.Min(items.Length, 16);
        var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2RecordItem);
        w.Put((byte)1);
        bool any = false;
        foreach (var it in items)
        {
            if (it == null) continue;
            bool dragging = IsBeingDragged(it);
            if (!_dragState.TryGetValue(it, out var prev)) { _dragState[it] = dragging; continue; }
            if (prev && !dragging)
            {
                _dragState[it] = false;
                var p = it.transform.position;
                w.Put(it.name ?? "");
                w.Put((byte)0);
                w.Put(p.x); w.Put(p.y); w.Put(p.z);
                any = true;
            }
            else _dragState[it] = dragging;
        }
        if (!any) return;
        net.EnqueueBatch(NetProtocol.Snapshot(w), false);
        CoopRuntime.LogSource?.LogInfo("[RecordItemSyncV2] client drop event up");
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int n = r.GetByte();
            var items = UnityEngine.Object.FindObjectsOfType<RecordItem>();
            _applying = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    string name = r.GetString();
                    byte inSlot = r.GetByte();
                    float x = r.GetFloat();
                    float y = r.GetFloat();
                    float z = r.GetFloat();
                    if (inSlot != 0 || items == null) continue;
                    RecordItem target = null;
                    foreach (var it in items)
                        if (it != null && it.name == name) { target = it; break; }
                    if (target != null) target.transform.position = new Vector3(x, y, z);
                }
            }
            finally { _applying = false; }
            if (Store.IsHost) _net?.EnqueueBatch(data, true);
            if ((++_recvLog % 15) == 1) CoopRuntime.LogSource?.LogInfo($"[RecordItemSyncV2] recv n={n} local={(items?.Length ?? 0)}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[RecordItemSyncV2] OnPacket: {ex.Message}"); }
    }

    private static bool IsBeingDragged(RecordItem it)
    {
        try
        {
            var d = it.GetComponent<DraggableItem>();
            return d != null && d.IsBeingDragged;
        }
        catch { return false; }
    }

    private static bool IsInRecordSlot(RecordItem it)
    {
        try
        {
            var players = UnityEngine.Object.FindObjectsOfType<RecordPlayerController>();
            if (players == null) return false;
            foreach (var p in players)
            {
                if (p == null) continue;
                if (p.slot != null && p.slot.CurrentItem != null
                    && p.slot.CurrentItem.gameObject != null
                    && p.slot.CurrentItem.gameObject == it.gameObject) return true;
                if (p._currentRecord != null && p._currentRecord.gameObject == it.gameObject) return true;
            }
        }
        catch { }
        return false;
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _timer = 0f; _applying = false; _hostSig.Clear(); _dragState.Clear(); }
}
