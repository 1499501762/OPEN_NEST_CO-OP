using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 征信点卡牌同步（PunchcardSyncV2，MsgType=224/225）。M7：把 V1 <c>PunchcardSync</c>（136/137）迁入分层架构。
/// - 状态（224，Host 权威）：主机变化检测广播所有卡牌位置/active/是否在槽；客机放下瞬间上行；
///   客机非槽位卡牌不应用主机位置（谁操作谁拖拽，防覆盖）。
/// - 卡槽事件（225，Operator）：PlaceCard/RemoveCard → 广播 → 对端 PlaceItem/RemoveItem（槽位 CurrentCard 一致，
///   购买依赖卡在槽）。IsApplyingCard 防环。
/// </summary>
public sealed class PunchcardSyncV2 : ISyncedModule
{
    public static PunchcardSyncV2 Instance { get; } = new PunchcardSyncV2();

    private PunchcardSyncV2() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2Punchcard;

    /// <summary>应用远端卡槽事件时的防环（Harmony patch 据此不重复上报）。</summary>
    public static bool IsApplyingCard;

    private const float Interval = 0.2f;
    private float _timer;
    private bool _applying;
    private int _sendLog, _recvLog;
    private readonly Dictionary<int, string> _hostSig = new();
    private readonly Dictionary<PunchcardRuntime, bool> _dragState = new();

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        try
        {
            var cards = UnityEngine.Object.FindObjectsOfType<PunchcardRuntime>(true);
            if (cards == null || cards.Length == 0) return;
            if (Store.IsHost) HostSendChanges(cards);
            else if (!_applying) ClientSendDropEvents(cards);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[PunchcardSyncV2] Tick: {ex.Message}"); }
    }

    private void HostSendChanges(PunchcardRuntime[] cards)
    {
        var net = _net;
        if (net == null) return;
        int n = Math.Min(cards.Length, 16);
        var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2Punchcard);
        w.Put((byte)n);
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            var c = cards[i];
            if (c == null)
            {
                w.Put((byte)i); w.Put((byte)2); w.Put(0f); w.Put(0f); w.Put(0f); w.Put((byte)0);
                w.Put(0f); w.Put(0f); w.Put(0f);
                continue;
            }
            bool inSlot = IsInRequisitionSlot(c);
            bool act = false;
            try { act = c.gameObject.activeSelf; } catch { }
            var p = c.transform.position;
            var r = c.transform.localEulerAngles;
            string sig = $"{(inSlot ? 1 : 0)}|{(act ? 1 : 0)}|{p.x:0.###}|{p.y:0.###}|{p.z:0.###}|{r.x:0.#}|{r.y:0.#}|{r.z:0.#}";
            if (_hostSig.TryGetValue(i, out var last) && last == sig)
            {
                // 无变化：inSlot=2 跳过（不能广播 inSlot=1+act=0 让客机卡牌消失）
                w.Put((byte)i); w.Put((byte)2); w.Put(0f); w.Put(0f); w.Put(0f); w.Put((byte)0);
                w.Put(0f); w.Put(0f); w.Put(0f);
                continue;
            }
            _hostSig[i] = sig;
            any = true;
            w.Put((byte)i);
            w.Put(inSlot ? (byte)1 : (byte)0);
            w.Put(p.x); w.Put(p.y); w.Put(p.z);
            w.Put(act ? (byte)1 : (byte)0);
            w.Put(r.x); w.Put(r.y); w.Put(r.z);
        }
        if (!any && _hostSig.Count > 0) return;
        net.EnqueueBatch(NetProtocol.Snapshot(w), true);
        if ((++_sendLog % 15) == 1) CoopRuntime.LogSource?.LogInfo($"[PunchcardSyncV2] host send n={n}");
    }

    private void ClientSendDropEvents(PunchcardRuntime[] cards)
    {
        var net = _net;
        if (net == null) return;
        var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2Punchcard);
        w.Put((byte)1);
        bool any = false;
        for (int i = 0; i < cards.Length && i < 16; i++)
        {
            var c = cards[i];
            if (c == null) continue;
            bool dragging = IsBeingDragged(c);
            if (!_dragState.TryGetValue(c, out var prev)) { _dragState[c] = dragging; continue; }
            if (prev && !dragging)
            {
                _dragState[c] = false;
                var p = c.transform.position;
                var r = c.transform.localEulerAngles;
                w.Put((byte)i);
                w.Put((byte)0);
                w.Put(p.x); w.Put(p.y); w.Put(p.z);
                w.Put(c.gameObject.activeSelf ? (byte)1 : (byte)0);
                w.Put(r.x); w.Put(r.y); w.Put(r.z);
                any = true;
            }
            else _dragState[c] = dragging;
        }
        if (!any) return;
        net.EnqueueBatch(NetProtocol.Snapshot(w), false);
        CoopRuntime.LogSource?.LogInfo("[PunchcardSyncV2] client drop event up");
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            byte msgType = r.GetByte();
            if (msgType == (byte)OpenNestCoop.Net.MsgType.V2PunchcardSlot) { ApplyCardSlotEvent(r, from); return; }
            int n = r.GetByte();
            var cards = UnityEngine.Object.FindObjectsOfType<PunchcardRuntime>(true);
            _applying = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    int idx = r.GetByte();
                    byte inSlot = r.GetByte();
                    float x = r.GetFloat(), y = r.GetFloat(), z = r.GetFloat();
                    bool act = true;
                    if (r.AvailableBytes >= 1) act = r.GetByte() != 0;
                    float rx = 0f, ry = 0f, rz = 0f;
                    if (r.AvailableBytes >= 12) { rx = r.GetFloat(); ry = r.GetFloat(); rz = r.GetFloat(); }
                    if (inSlot == 2 || cards == null || idx < 0 || idx >= cards.Length) continue;
                    var c = cards[idx];
                    if (c == null || IsBeingDragged(c)) continue;
                    // 客机：只应用槽位卡牌（非槽位由客机本地拖拽控制，防覆盖）
                    if (!Store.IsHost && inSlot != 1) continue;
                    try
                    {
                        if (c.gameObject.activeSelf != act) c.gameObject.SetActive(act);
                        c.transform.position = new Vector3(x, y, z);
                        c.transform.localEulerAngles = new Vector3(rx, ry, rz);
                    }
                    catch { }
                }
            }
            finally { _applying = false; }
            if (Store.IsHost) _net?.EnqueueBatch(data, true);
            if ((++_recvLog % 15) == 1) CoopRuntime.LogSource?.LogInfo($"[PunchcardSyncV2] recv n={n}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[PunchcardSyncV2] OnPacket: {ex.Message}"); }
    }

    // ---------------- 卡槽事件（225） ----------------

    /// <summary>本地放卡（ItemSlot.PlaceItem，Harmony postfix，V2 分支）→ 广播。</summary>
    public void OnLocalPlaceCard(ItemSlot slot, DraggableItem item)
    {
        BroadcastCardEvent(slot, item, 1);
    }

    /// <summary>本地取卡（ItemSlot.RemoveItem，Harmony postfix，V2 分支）→ 广播。</summary>
    public void OnLocalRemoveCard(ItemSlot slot, DraggableItem item)
    {
        BroadcastCardEvent(slot, item, 2);
    }

    private void BroadcastCardEvent(ItemSlot slot, DraggableItem item, byte ev)
    {
        if (IsApplyingCard || slot == null || item == null || !Store.IsOnline) return;
        var card = item.GetComponent<PunchcardRuntime>();
        if (card == null) return;
        int slotIdx = IndexOfSlot(slot);
        int cardIdx = IndexOfCard(card);
        if (slotIdx < 0 || cardIdx < 0) return;
        var net = _net;
        if (net == null) return;
        try
        {
            var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2PunchcardSlot);
            w.Put(ev); w.Put((byte)slotIdx); w.Put((byte)cardIdx);
            var data = NetProtocol.Snapshot(w);
            if (Store.IsHost)
            {
                for (int i = 0; i < net.Roster.Count; i++)
                {
                    var p = net.Roster[i];
                    if (p != null && !p.IsLocal) net.Transport.Send(p.SteamId, data, true);
                }
            }
            else if (net.HostSteamId != 0)
                net.Transport.Send(net.HostSteamId, data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[PunchcardSyncV2] BroadcastCardEvent: {ex.Message}"); }
    }

    private void ApplyCardSlotEvent(NetDataReader r, ulong from)
    {
        byte ev = r.GetByte();
        byte slotIdx = r.GetByte();
        byte cardIdx = r.GetByte();
        var net = _net;
        if (Store.IsHost && net != null)
        {
            for (int i = 0; i < net.Roster.Count; i++)
            {
                var p = net.Roster[i];
                if (p != null && !p.IsLocal && (ulong)p.SteamId != from)
                    net.Transport.Send(p.SteamId, dataFwd(ev, slotIdx, cardIdx), true);
            }
        }
        var slots = UnityEngine.Object.FindObjectsOfType<ItemSlot>();
        var cards = UnityEngine.Object.FindObjectsOfType<PunchcardRuntime>(true);
        if (slots == null || slots.Length == 0 || slotIdx >= slots.Length || slots[slotIdx] == null) return;
        var card = FindCard(cardIdx, cards);
        if (card == null) return;
        IsApplyingCard = true;
        try
        {
            if (ev == 1) slots[slotIdx].PlaceItem(card.DraggableItem);
            else if (ev == 2) slots[slotIdx].RemoveItem(card.DraggableItem);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[PunchcardSyncV2] ApplyCardSlotEvent: {ex.Message}"); }
        finally { IsApplyingCard = false; }
    }

    private static byte[] dataFwd(byte ev, byte slotIdx, byte cardIdx)
    {
        var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2PunchcardSlot);
        w.Put(ev); w.Put(slotIdx); w.Put(cardIdx);
        return NetProtocol.Snapshot(w);
    }

    private static int IndexOfSlot(ItemSlot target)
    {
        try
        {
            var slots = UnityEngine.Object.FindObjectsOfType<ItemSlot>();
            if (slots == null) return -1;
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] != null && slots[i] == target) return i;
        }
        catch { }
        return -1;
    }

    private static int IndexOfCard(PunchcardRuntime target)
    {
        try
        {
            var cards = UnityEngine.Object.FindObjectsOfType<PunchcardRuntime>(true);
            if (cards == null) return -1;
            for (int i = 0; i < cards.Length; i++)
                if (cards[i] != null && cards[i] == target) return i;
        }
        catch { }
        return -1;
    }

    private static PunchcardRuntime FindCard(int idx, PunchcardRuntime[] cards)
    {
        if (cards == null || cards.Length == 0) return null;
        if (idx >= 0 && idx < cards.Length && cards[idx] != null) return cards[idx];
        return null;
    }

    private static bool IsBeingDragged(PunchcardRuntime c)
    {
        try
        {
            var d = c.GetComponent<DraggableItem>();
            return d != null && d.IsBeingDragged;
        }
        catch { return false; }
    }

    private static bool IsInRequisitionSlot(PunchcardRuntime c)
    {
        try
        {
            var slots = UnityEngine.Object.FindObjectsOfType<RequisitionSlot>();
            if (slots == null) return false;
            foreach (var s in slots)
            {
                if (s == null) continue;
                var cc = s.CurrentCard;
                if (cc != null && cc.gameObject != null && cc.gameObject == c.gameObject) return true;
            }
        }
        catch { }
        return false;
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _timer = 0f; _applying = false; _hostSig.Clear(); _dragState.Clear(); }
}
