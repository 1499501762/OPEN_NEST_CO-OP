using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using OpenNestCoop.Core;

namespace OpenNestCoop.GameSync;

/// <summary>
/// 装药库存同步（PowderChargeInventory.CurrentCharges，MsgType=142）。
///
/// 背景（2026-08-23）：Button Dispencer (N) 的 active 由**可用药包库存**决定——PowderChargeController
/// 订阅 PowderChargeInventory.OnChargesChanged，库存变化时刷新各 Button Dispencer 的 active
/// （库存 ≥ N 的按钮可交互，> 库存的按钮 inactive 锁定）。
/// 主机下药/补给消耗库存 → 库存变化 → 客机若不刷新 → 客机 Button Dispencer 高编号按钮 inactive 锁定
/// （"主机正常、客机错误锁定"）。
///
/// 方案：主机权威轮询 CurrentCharges，变化广播 → 客机 set CurrentCharges（触发 OnChargesChanged
/// → PowderChargeController 自动刷新 Button Dispencer active）。客机不上行（库存只由主机权威管理，
/// 补给/消耗最终都发生在主机侧任务逻辑）。
/// </summary>
public sealed class ChargeInventorySync : ISyncedModule
{
    public byte MsgType => 142;
    private float _timer;
    private int _lastHostCharges = -1;
    private int _sendLog;

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined)
        {
            _lastHostCharges = -1;
            return;
        }
        _timer += dt;
        if (_timer < 0.5f) return; // 0.5s 轮询（库存变化不频繁）
        _timer = 0f;
        try
        {
            var inv = PowderChargeInventory.Instance;
            if (inv == null) return;
            int cur = inv.CurrentCharges;
            if (!net.IsHost)
            {
                _lastHostCharges = cur; // 客机记录主机已知值（应用在 OnPacket）
                return;
            }
            if (cur == _lastHostCharges) return;
            _lastHostCharges = cur;
            var w = NetProtocol.Begin((MsgType)MsgType);
            w.Put((byte)Math.Max(cur, 0));
            var data = NetProtocol.Snapshot(w);
            foreach (var p in net.Roster)
                if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            if ((++_sendLog % 20) == 1)
                CoopRuntime.LogSource?.LogInfo($"[ChargeInventorySync] host broadcast charges={cur}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ChargeInventorySync Tick: {ex.Message}"); }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return; // 仅客机应用（主机权威）
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int cur = r.GetByte();
            var inv = PowderChargeInventory.Instance;
            if (inv == null) return;
            if (inv.CurrentCharges != cur)
            {
                // ⚠️ 2026-08-23：set CurrentCharges 触发 OnChargesChanged → PowderChargeController 刷新
                // Button Dispencer active（库存≥N 的按钮可交互，>库存 inactive）——解决客机按钮锁定。
                inv.CurrentCharges = cur;
                CoopRuntime.LogSource?.LogInfo($"[ChargeInventorySync] client apply charges={cur}");
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ChargeInventorySync OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { _lastHostCharges = -1; }
    public void Reset() { _lastHostCharges = -1; }
}
