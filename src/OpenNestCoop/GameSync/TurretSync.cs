using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 炮塔开火事件同步（M2 精简版）。
/// 架构：仰角/方向角**不再做状态同步**（移除状态插值/状态广播/输入上行）——
/// 炮塔控制完全交给 ControlSync 的 Lever/Gear 位置值同步（谁操作谁权威，游戏本地逻辑驱动炮塔，
/// 动画+数值天然一致）。本类只保留**开火事件**（主机广播 GunFire，客户端复现）。
/// 依据：单机游戏 Lever/Gear 位置 → 游戏本地逻辑 → 炮塔状态是确定性的；
/// 两端 Lever/Gear 位置一致 → 炮塔自然一致，无需（也不能）跨端插值炮塔状态
/// （插值与 Lever 值同步打架是"曲柄无效/回退/不同步"的根因）。
/// </summary>
public static class TurretSync
{
    // ---------------- 开火事件 ----------------

    /// <summary>主机本地开火后调用（Harmony postfix 触发），广播给全员。</summary>
    public static void OnLocalGunFired(GunController gun)
    {
        var net = CoopRuntime.Net;
        if (net == null || !net.IsHost) return;
        var turret = TurretController.Instance;
        if (turret == null || turret.guns == null) return;
        try
        {
            int idx = -1;
            for (int i = 0; i < turret.guns.Count; i++)
            {
                var g = turret.guns[i];
                // ⚠️ IL2CPP interop：不能 `g == gun`（包装引用比较不可靠），必须按底层 Pointer 匹配
                // （与 ReloadSync.IndexOf 一致；此前用 == 导致 idx 永远找不到 → GunFire 从不广播 → 客机不开火）。
                if (g != null && gun != null && g.Pointer == gun.Pointer) { idx = i; break; }
            }
            if (idx < 0)
            {
                CoopRuntime.LogSource?.LogWarning($"[TurretSync] OnLocalGunFired: gun '{gun?.name}' not found in turret.guns ({turret.guns.Count})");
                return;
            }
            var w = NetProtocol.Begin(MsgType.GunFire);
            w.Put((byte)idx);
            var data = NetProtocol.Snapshot(w);
            foreach (var p in net.Roster)
                if (!p.IsLocal)
                    net.Transport.Send(p.SteamId, data, true);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"TurretSync OnLocalGunFired: {ex.Message}"); }
    }

    /// <summary>客户端复现开火。</summary>
    public static void OnGunFire(byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) { CoopRuntime.LogSource?.LogWarning("[TurretSync] OnGunFire net null"); return; }
        if (net.IsHost) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            int idx = r.GetByte();
            var turret = TurretController.Instance;
            if (turret == null || turret.guns == null)
            {
                CoopRuntime.LogSource?.LogWarning($"[TurretSync] OnGunFire idx={idx} turretNull={turret == null} gunsNull={turret?.guns == null}");
                return;
            }
            if (idx < 0 || idx >= turret.guns.Count)
            {
                CoopRuntime.LogSource?.LogWarning($"[TurretSync] OnGunFire idx={idx} out of range count={turret.guns.Count}");
                return;
            }
            var gun = turret.guns[idx];
            if (gun == null)
            {
                CoopRuntime.LogSource?.LogWarning($"[TurretSync] OnGunFire idx={idx} gun null");
                return;
            }
            // 客户端复现开火：放行 RequestFire（防被拦截上行造成循环）。
            // ⚠️ 不做手动 SetLeftArmed/恢复 armed——Arm 不是装弹，armed 由开火机制本身复位。
            CoopRuntime.LogSource?.LogInfo($"[TurretSync] OnGunFire RequestFire idx={idx} gun='{gun?.name}'");
            ReloadSync.IsApplyingFire = true;
            try { gun.RequestFire(); }
            finally { ReloadSync.IsApplyingFire = false; }
            CoopRuntime.LogSource?.LogInfo($"[TurretSync] OnGunFire RequestFire done idx={idx}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"TurretSync OnGunFire: {ex.Message}"); }
    }
}
