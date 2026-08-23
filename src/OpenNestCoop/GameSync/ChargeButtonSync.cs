using System;
using System.Collections.Generic;
using System.Linq;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using OpenNestCoop.Core;

namespace OpenNestCoop.GameSync;

/// <summary>
/// Button Dispencer active 掩码同步（MsgType=143）。
///
/// 背景（2026-08-23）：Button Dispencer (N) 是"逐档激活"（选 N 档激活到 N，ActivateNextChargeButtonIfValid），
/// 客机激活链断（拉 2 个拉杆后 Button 3 不激活，allActive 停 110000）→ 客机锁定 3-6；
/// 且"选弹种状态可拉装药拉杆"（客机按钮 active 与状态机不同步）。
///
/// 方案：主机权威读取每炮 chargeButtons 的 isActive 掩码（6 位），变化广播 → 客机按掩码
/// LookAtTarget.SetActive 对齐（直接同步视觉 active，不依赖游戏逐档激活链在客机的执行）。
/// </summary>
public sealed class ChargeButtonSync : ISyncedModule
{
    public byte MsgType => 143;
    private float _timer;
    private string _lastSig = "";
    private float _lastSendTime = -10f; // 上次广播时间（用于周期补发）

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined)
        {
            _lastSig = "";
            return;
        }
        _timer += dt;
        if (_timer < 0.3f) return; // 0.3s 轮询（掩码变化不频繁）
        _timer = 0f;
        if (!net.IsHost) return; // 仅主机权威广播掩码
        try
        {
            var guns = ReloadSync.ResolveGuns();
            if (guns == null || guns.Count == 0) return;
            string sig = "";
            var masks = new List<(int idx, int mask)>();
            foreach (var g in guns)
            {
                int mask = 0;
                var btns = g.Powder != null ? g.Powder.chargeButtons : null;
                if (btns != null)
                    for (int j = 0; j < btns.Count && j < 6; j++)
                    {
                        bool a = false;
                        try { a = btns[j] != null && btns[j].isActive; } catch { }
                        if (a) mask |= (1 << j);
                    }
                masks.Add((g.Index, mask));
                sig += g.Index + ":" + mask + ";";
            }
            // ⚠️ 2026-08-23：掩码变化立即广播；**每 3s 补发一次**（即使 sig 没变）——
            // 客机首次收到广播时场景可能未就绪（ResolveGuns 空被丢弃），之后 sig 不变就不重发 → 掩码同步失效。
            // 周期补发确保客机场景就绪后能对齐主机掩码。
            if (sig == _lastSig && (UnityEngine.Time.realtimeSinceStartup - _lastSendTime) < 3f) return;
            _lastSig = sig;
            _lastSendTime = UnityEngine.Time.realtimeSinceStartup;
            var w = NetProtocol.Begin((MsgType)MsgType);
            w.Put((byte)masks.Count);
            foreach (var (idx, mask) in masks)
            {
                w.Put((byte)idx);
                w.Put((byte)mask);
            }
            var data = NetProtocol.Snapshot(w);
            foreach (var p in net.Roster)
                if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            CoopRuntime.LogSource?.LogInfo($"[ChargeButtonSync] host broadcast masks=[{sig}]");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ChargeButtonSync Tick: {ex.Message}"); }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null || net.IsHost) return; // 仅客机应用（主机权威掩码）
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int n = r.GetByte();
            var guns = ReloadSync.ResolveGuns();
            for (int i = 0; i < n; i++)
            {
                int idx = r.GetByte();
                int mask = r.GetByte();
                var g = guns.FirstOrDefault(x => x.Index == idx);
                if (g == null || g.Powder == null) continue;
                var btns = g.Powder.chargeButtons;
                if (btns == null) continue;
                for (int j = 0; j < btns.Count && j < 6; j++)
                {
                    var btn = btns[j];
                    if (btn == null) continue;
                    bool want = (mask & (1 << j)) != 0;
                    bool have = false;
                    try { have = btn.isActive; } catch { }
                    if (have != want)
                    {
                        try { btn.SetActive(want); } catch { }
                    }
                }
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ChargeButtonSync OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { _lastSig = ""; }
    public void Reset() { _lastSig = ""; }
}
