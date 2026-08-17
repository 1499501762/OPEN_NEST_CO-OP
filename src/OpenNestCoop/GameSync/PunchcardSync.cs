using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 征信点卡牌位置同步（PunchcardRuntime，MsgType=136）。
/// 卡牌（PunchcardRuntime）是可拖拽物品（DraggableItem），玩家从牌堆拖到
/// RequisitionSlot（卡槽）→ 插入（槽位 CurrentCard=卡牌）→ 拉征用杆购买。
/// 两端物理/交互独立会分叉（卡牌位置/插入槽位不同 → 购买找不到卡）。
/// 主机权威：主机广播所有卡牌位置 + active + 是否在槽；客机本地拖放（放下瞬间）上行。
/// 按 FindObjectsOfType 索引定位（两端同场景实例顺序一致）。
/// </summary>
public sealed class PunchcardSync : ISyncedModule
{
    public byte MsgType => 136;
    /// <summary>卡牌入槽/出槽事件（MsgType=137）：PlaceCard/RemoveCard → 广播 → 对端执行同一操作，
    /// 槽位 CurrentCard 两端一致（购买依赖卡牌在槽）。</summary>
    public const byte CardSlotEventMsgType = 137;
    /// <summary>卡牌集合同步（MsgType=138，方案 1）：主机广播当前卡牌定义 ID 列表，客机
    /// 用 RequisitionConsoleManager.EnsureCards 补齐缺失卡牌 → 两端卡牌集合一致（索引/数量对齐）。</summary>
    public const byte CardListMsgType = 138;
    private const float Interval = 0.2f;
    private float _timer;
    private bool _applying;
    private int _sendLog;
    private int _recvLog;
    // 主机：每张卡牌上次状态签名（按卡牌定义 ID 做 key——两端索引顺序可能不同，2026-08-15）
    private readonly System.Collections.Generic.Dictionary<string, string> _hostSig = new();
    // 客机：拖拽状态跟踪（放下瞬间上行一次位置）
    private readonly System.Collections.Generic.Dictionary<PunchcardRuntime, bool> _dragState = new();
    // 主机：最近一次广播的卡牌集合签名（变化才广播）
    private string _hostCardSig;

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
            var cards = UnityEngine.Object.FindObjectsOfType<PunchcardRuntime>(true); // includeInactive：卡牌可能 active=false（隐藏）
            if (net.IsHost)
            {
                if (cards == null || cards.Length == 0) return;
                HostSendChanges(net, cards);
                HostBroadcastCardList(net); // 方案 1：卡牌集合变化广播
            }
            else if (!_applying)
            {
                if (cards == null || cards.Length == 0) return;
                ClientSendDropEvents(net, cards);
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"PunchcardSync Tick: {ex.Message}"); }
    }

    /// <summary>主机：变化检测广播所有卡牌位置/active/是否在槽（首次全量，之后只发变化的）。
    /// ⚠️ 2026-08-15：按卡牌定义 ID（CardIdOf）广播——两端 FindObjectsOfType 索引顺序可能不同
    /// （“索引对应的卡牌不同步”根因），改用 ID 后客机按内容匹配。</summary>
    private void HostSendChanges(NetManager net, PunchcardRuntime[] cards)
    {
        if (cards == null || cards.Length == 0) return;
        var w = NetProtocol.Begin((MsgType)MsgType);
        // 第一遍统计有效卡牌数（非 null 且有定义 ID）
        int n = 0;
        for (int i = 0; i < cards.Length; i++)
            if (cards[i] != null && !string.IsNullOrEmpty(CardIdOf(cards[i]))) n++;
        if (n == 0) return;
        w.Put((byte)Math.Min(n, 32));
        bool any = false;
        int sent = 0;
        for (int i = 0; i < cards.Length && sent < 32; i++)
        {
            var c = cards[i];
            if (c == null) continue;
            string id = CardIdOf(c);
            if (string.IsNullOrEmpty(id)) continue;
            sent++;
            bool inSlot = IsInRequisitionSlot(c);
            bool act = false;
            try { act = c.gameObject.activeSelf; } catch { }
            var p = c.transform.position;
            var r = c.transform.localEulerAngles;
            string sig = $"{(inSlot ? 1 : 0)}|{(act ? 1 : 0)}|{p.x:0.###}|{p.y:0.###}|{p.z:0.###}|{r.x:0.#}|{r.y:0.#}|{r.z:0.#}";
            if (_hostSig.TryGetValue(id, out var last) && last == sig)
            {
                // 无变化：inSlot=2 标记跳过（客机不应用）——⚠️ 不能广播 inSlot=1+act=0，
                // 那会让客机把这些卡牌 SetActive(false)（客机剩余卡全消失的根因）。
                w.Put(id); w.Put((byte)2); w.Put(0f); w.Put(0f); w.Put(0f); w.Put((byte)0);
                w.Put(0f); w.Put(0f); w.Put(0f);
                continue;
            }
            _hostSig[id] = sig;
            any = true;
            w.Put(id);
            w.Put(inSlot ? (byte)1 : (byte)0);
            w.Put(p.x); w.Put(p.y); w.Put(p.z);
            w.Put(act ? (byte)1 : (byte)0);
            w.Put(r.x); w.Put(r.y); w.Put(r.z);
        }
        if (!any && _hostSig.Count > 0) return; // 无变化不发
        var data = NetProtocol.Snapshot(w);
        net.EnqueueBatch(data, true);
        if ((++_sendLog % 15) == 1)
        {
            // 诊断：打印每张卡牌 active/位置（确认客机卡牌为何被隐藏）
            string cdiag = "";
            for (int i = 0; i < cards.Length && i < 8; i++)
            {
                var c = cards[i];
                if (c == null) { cdiag += $" #{i}:null"; continue; }
                bool act2 = false; try { act2 = c.gameObject.activeSelf; } catch { }
                var p2 = c.transform.position;
                cdiag += $" #{i}:{(act2 ? "A" : "H")}@({p2.x:0.0},{p2.y:0.0},{p2.z:0.0})";
            }
            CoopRuntime.LogSource?.LogInfo($"[PunchcardSync] host send n={n} sig={_hostSig.Count} cards=[{cdiag}]");
        }
    }

    /// <summary>客机：检测拖放事件（IsBeingDragged true→false）→ 放下瞬间上行该卡牌位置（主机权威）。</summary>
    private void ClientSendDropEvents(NetManager net, PunchcardRuntime[] cards)
    {
        int n = Math.Min(cards.Length, 16);
        var w = NetProtocol.Begin((MsgType)MsgType);
        w.Put((byte)1);
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            var c = cards[i];
            if (c == null) continue;
            bool dragging = IsBeingDragged(c);
            if (!_dragState.TryGetValue(c, out var prev)) { _dragState[c] = dragging; continue; }
            if (prev && !dragging) // 刚放下
            {
                _dragState[c] = false;
                string id = CardIdOf(c);
                if (string.IsNullOrEmpty(id)) continue;
                var p = c.transform.position;
                var r = c.transform.localEulerAngles;
                w.Put(id); // 按卡牌定义 ID（两端索引顺序可能不同）
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
        CoopRuntime.LogSource?.LogInfo("[PunchcardSync] client drop event up");
    }

    // ---------------- 方案 1：卡牌集合同步（MsgType=138）----------------
    // 两端 FindObjectsOfType 顺序/数量可能不同 → 让客机按主机卡牌定义 ID 列表补齐缺失卡牌，
    // 保证两端卡牌集合一致（索引/数量对齐）。客机本地卡牌缺失时（缺卡）也能正确匹配。

    /// <summary>主机：检测卡牌定义 ID 集合变化并广播（连接后/卡牌生成/入槽出槽时两端对齐）。</summary>
    private void HostBroadcastCardList(NetManager net)
    {
        var mgr = RequisitionConsoleManager.Instance;
        if (mgr == null) return;
        try
        {
            var all = mgr.GetAllCards();
            var ids = new System.Collections.Generic.List<string>();
            if (all != null)
                for (int i = 0; i < all.Length; i++)
                {
                    var c = all[i];
                    if (c == null) continue;
                    string id = CardIdOf(c);
                    if (!string.IsNullOrEmpty(id) && !ids.Contains(id)) ids.Add(id);
                }
            ids.Sort();
            string sig = string.Join("|", ids);
            if (_hostCardSig == sig) return;
            _hostCardSig = sig;
            var w = NetProtocol.Begin((MsgType)CardListMsgType);
            w.Put((byte)Math.Min(ids.Count, 64));
            for (int i = 0; i < ids.Count && i < 64; i++) w.Put(ids[i]);
            var data = NetProtocol.Snapshot(w);
            foreach (var p in net.Roster)
                if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            CoopRuntime.LogSource?.LogInfo($"[PunchcardSync] host card list broadcast n={ids.Count}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"PunchcardSync HostBroadcastCardList: {ex.Message}"); }
    }

    /// <summary>收到主机卡牌 ID 列表 → 客机补齐缺失卡牌（方案 1：开局按主机卡牌实体生成）。</summary>
    private void ApplyCardList(NetDataReader r, NetManager net)
    {
        int n = r.GetByte();
        var ids = new System.Collections.Generic.List<string>(n);
        for (int i = 0; i < n; i++) ids.Add(r.GetString());
        CoopRuntime.LogSource?.LogInfo($"[PunchcardSync] recv card list n={ids.Count}");
        if (net.IsHost) return; // 主机无需补齐
        EnsureHostCards(ids);
    }

    /// <summary>客机：用 RequisitionConsoleManager 生成主机有而本地缺的卡牌（EnsureCards）。</summary>
    private void EnsureHostCards(System.Collections.Generic.List<string> hostIds)
    {
        try
        {
            var mgr = RequisitionConsoleManager.Instance;
            if (mgr == null) return;
            // 补给界面未初始化时跳过——界面打开后下次广播再补齐
            bool init = false;
            try { init = mgr.initialized; } catch { }
            if (!init) return;
            var allDefs = mgr.AllDefinitions;
            if (allDefs == null) return;
            var existing = mgr.GetAllCards();
            var existingIds = new System.Collections.Generic.HashSet<string>();
            if (existing != null)
                for (int i = 0; i < existing.Length; i++)
                {
                    var c = existing[i];
                    if (c == null) continue;
                    try { string id = c.CurrentDefinition?.ID; if (!string.IsNullOrEmpty(id)) existingIds.Add(id); } catch { }
                }
            var missing = new Il2CppSystem.Collections.Generic.List<PunchcardDefinitionV2>();
            foreach (var id in hostIds)
            {
                if (existingIds.Contains(id)) continue;
                try
                {
                    PunchcardDefinitionV2 def = null;
                    if (allDefs.ContainsKey(id)) def = allDefs[id];
                    if (def != null) missing.Add(def);
                }
                catch { }
            }
            if (missing.Count == 0) return;
            mgr.EnsureCards(missing);
            CoopRuntime.LogSource?.LogInfo($"[PunchcardSync] ensure host cards missing={missing.Count} totalHost={hostIds.Count} existing={existingIds.Count}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"PunchcardSync EnsureHostCards: {ex.Message}"); }
    }

    // ---------------- 卡牌入槽/出槽事件（MsgType=137）----------------

    /// <summary>应用远端卡槽事件时的防环标志（Harmony patch 据此不重复上报）。</summary>
    public static bool IsApplyingCard;

    /// <summary>本地放卡（ItemSlot.PlaceItem 被调用，Harmony patch）→ 广播事件。
    /// ⚠️ 卡牌插入卡槽走 ItemSlot.PlaceItem（不是 RequisitionSlot.PlaceCard——那个不触发）。</summary>
    public static void OnLocalPlaceCard(ItemSlot slot, DraggableItem item)
    {
        try
        {
            if (IsApplyingCard) return;
            var net = CoopRuntime.Net;
            if (net == null || slot == null || item == null) return;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
            var card = item.GetComponent<PunchcardRuntime>();
            if (card == null) return; // 非卡牌物品（其他 DraggableItem）不处理
            int slotIdx = IndexOfSlot(slot);
            string cardId = CardIdOf(card);
            CoopRuntime.LogSource?.LogInfo($"[PunchcardSync] OnLocalPlaceCard slot={slotIdx} card='{cardId}' host={net.IsHost}");
            if (slotIdx < 0 || string.IsNullOrEmpty(cardId)) return;
            var w = NetProtocol.Begin((MsgType)CardSlotEventMsgType);
            w.Put((byte)1); w.Put((byte)slotIdx); w.Put(cardId);
            BroadcastCardEvent(w, net);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"PunchcardSync OnLocalPlaceCard: {ex.Message}"); }
    }

    /// <summary>本地取卡（ItemSlot.RemoveItem 被调用，Harmony patch）→ 广播事件。</summary>
    public static void OnLocalRemoveCard(ItemSlot slot, DraggableItem item)
    {
        try
        {
            if (IsApplyingCard) return;
            var net = CoopRuntime.Net;
            if (net == null || slot == null || item == null) return;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
            var card = item.GetComponent<PunchcardRuntime>();
            if (card == null) return;
            int slotIdx = IndexOfSlot(slot);
            string cardId = CardIdOf(card);
            CoopRuntime.LogSource?.LogInfo($"[PunchcardSync] OnLocalRemoveCard slot={slotIdx} card='{cardId}' host={net.IsHost}");
            if (slotIdx < 0 || string.IsNullOrEmpty(cardId)) return;
            var w = NetProtocol.Begin((MsgType)CardSlotEventMsgType);
            w.Put((byte)2); w.Put((byte)slotIdx); w.Put(cardId);
            BroadcastCardEvent(w, net);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"PunchcardSync OnLocalRemoveCard: {ex.Message}"); }
    }

    private static void BroadcastCardEvent(NetDataWriter w, NetManager net)
    {
        var data = NetProtocol.Snapshot(w);
        if (net.IsHost)
        {
            foreach (var p in net.Roster)
                if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
        }
        else if (net.HostSteamId != 0)
            net.Transport.Send(net.HostSteamId, data, true);
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

    /// <summary>取卡牌定义 ID（PunchcardDefinitionV2.ID，AllDefinitions 字典 key）——按内容匹配卡牌，
    /// 替代 FindObjectsOfType 索引（两端卡牌集合/顺序可能不同 → 索引不同步根因，2026-08-15）。</summary>
    private static string CardIdOf(PunchcardRuntime card)
    {
        try { return card?.CurrentDefinition?.ID ?? ""; }
        catch { return ""; }
    }

    /// <summary>按卡牌定义 ID 找卡牌（不依赖两端索引一致）。</summary>
    private static PunchcardRuntime FindCardById(string id, PunchcardRuntime[] cards)
    {
        if (string.IsNullOrEmpty(id) || cards == null) return null;
        for (int i = 0; i < cards.Length; i++)
        {
            var c = cards[i];
            if (c == null) continue;
            try { if (c.CurrentDefinition != null && c.CurrentDefinition.ID == id) return c; }
            catch { }
        }
        return null;
    }

    /// <summary>处理卡槽事件（137）：对端执行 PlaceCard/RemoveCard → 槽位 CurrentCard 两端一致。</summary>
    /// <summary>卡牌事件去重：同 (slot, cardId) 短时间（0.6s）只处理一次（防双端放/取互相触发循环回跳
    /// → 卡牌在槽位反复 Place/Remove → 客机补给台卡牌无法稳定交互）。</summary>
    private string _lastEvKey = "";
    private float _lastEvTime = -1f;
    private const float EvDedupWindow = 0.6f;

    private void ApplyCardSlotEvent(NetDataReader r, NetManager net, ulong from)
    {
        byte ev = r.GetByte();
        byte slotIdx = r.GetByte();
        string cardId = r.GetString();
        // 同 (slot, card) 事件去重（放卡/取卡交替也算同 key）——防 apply 后触发二次事件循环
        try
        {
            string key = $"{slotIdx}|{cardId}";
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (key == _lastEvKey && now - _lastEvTime < EvDedupWindow) return;
            _lastEvKey = key;
            _lastEvTime = now;
        }
        catch { }
        if (net.IsHost)
        {
            // 主机转发给其他客机（不含发起者）
            var fwd = NetProtocol.Begin((MsgType)CardSlotEventMsgType);
            fwd.Put(ev); fwd.Put(slotIdx); fwd.Put(cardId);
            var fd = NetProtocol.Snapshot(fwd);
            foreach (var p in net.Roster)
                if (!p.IsLocal && (ulong)p.SteamId != from)
                    net.Transport.Send(p.SteamId, fd, true);
        }
        var slots = UnityEngine.Object.FindObjectsOfType<ItemSlot>();
        var cards = UnityEngine.Object.FindObjectsOfType<PunchcardRuntime>(true); // includeInactive
        CoopRuntime.LogSource?.LogInfo($"[PunchcardSync] recv CardSlotEvent ev={ev} slot={slotIdx} card='{cardId}' nSlots={(slots?.Length ?? 0)} nCards={(cards?.Length ?? 0)}");
        if (slots == null || slots.Length == 0) return;
        if (slotIdx >= slots.Length || slots[slotIdx] == null) return;
        var card = FindCardById(cardId, cards);
        if (card == null)
        {
            // 客机端缺该卡牌（生成不一致）：诊断（无法购买）
            CoopRuntime.LogSource?.LogWarning($"[PunchcardSync] apply CardSlotEvent card NOT FOUND id='{cardId}' nCards={(cards?.Length ?? 0)}");
            return;
        }
        IsApplyingCard = true;
        try
        {
            if (ev == 1)
            {
                slots[slotIdx].PlaceItem(card.DraggableItem);
                CoopRuntime.LogSource?.LogInfo($"[PunchcardSync] apply PlaceItem slot={slotIdx} card='{cardId}'");
            }
            else if (ev == 2)
            {
                slots[slotIdx].RemoveItem(card.DraggableItem);
                CoopRuntime.LogSource?.LogInfo($"[PunchcardSync] apply RemoveItem slot={slotIdx} card='{cardId}'");
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"PunchcardSync ApplyCardSlotEvent: {ex.Message}"); }
        finally { IsApplyingCard = false; }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            byte msgType = r.GetByte();
            if (msgType == CardSlotEventMsgType)
            {
                ApplyCardSlotEvent(r, net, from);
                return;
            }
            if (msgType == CardListMsgType)
            {
                ApplyCardList(r, net);
                return;
            }
            int n = r.GetByte();
            var cards = UnityEngine.Object.FindObjectsOfType<PunchcardRuntime>(true); // includeInactive
            _applying = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    string cardId = r.GetString(); // 按卡牌定义 ID 匹配（不依赖两端索引顺序）
                    byte inSlot = r.GetByte();
                    float x = r.GetFloat();
                    float y = r.GetFloat();
                    float z = r.GetFloat();
                    bool act = true;
                    if (r.AvailableBytes >= 1) act = r.GetByte() != 0;
                    float rx = 0f, ry = 0f, rz = 0f;
                    if (r.AvailableBytes >= 12) { rx = r.GetFloat(); ry = r.GetFloat(); rz = r.GetFloat(); }
                    if (inSlot == 2) continue; // 无变化跳过（不要应用占位值）
                    var c = FindCardById(cardId, cards);
                    if (c == null) continue;
                    // 本端正在拖动该卡牌：位置由本地 DraggableItem 控制，不覆盖
                    if (IsBeingDragged(c)) continue;
                    // ⚠️ 客机端：非槽位（牌堆）卡牌不应用主机位置——卡牌拖拽由客机本地控制
                    // （谁操作谁权威），防止每 0.2s 位置同步覆盖拖拽 → 客机无法拖动卡牌。
                    // 只应用槽位（inSlot=1）卡牌位置/旋转（吸附到槽位锚点）。
                    if (!net.IsHost && inSlot != 1) continue;
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
            if (net.IsHost) net.EnqueueBatch(data, true); // 转发给其他客户端
            if ((++_recvLog % 15) == 1)
                CoopRuntime.LogSource?.LogInfo($"[PunchcardSync] recv n={n} local={(cards?.Length ?? 0)}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"PunchcardSync OnPacket: {ex.Message}"); }
    }

    /// <summary>卡牌是否正在被本端玩家拖动（拖动中位置由本地 DraggableItem 控制，快照不覆盖）。</summary>
    private static bool IsBeingDragged(PunchcardRuntime c)
    {
        try
        {
            var d = c.GetComponent<DraggableItem>();
            return d != null && d.IsBeingDragged;
        }
        catch { return false; }
    }

    /// <summary>卡牌是否已在某 RequisitionSlot 卡槽里（在槽里时位置由槽控制，PunchcardSync 不覆盖）。</summary>
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
                if (cc != null && cc.gameObject != null && cc.gameObject == c.gameObject)
                    return true;
            }
        }
        catch { }
        return false;
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _timer = 0f; _applying = false; _hostSig.Clear(); _dragState.Clear(); }
}
