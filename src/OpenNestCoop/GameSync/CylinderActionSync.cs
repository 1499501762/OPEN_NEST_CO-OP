using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 弹舱动作同步（CylinderShellSelector，MsgType=141）——事件解耦（P1）。
///
/// 背景：`Universal Button Load shell Rammer`（推弹头）与 `Universal Button Move Cylinder`（切弹舱）
/// 原本走 ButtonClickSync 实体耦合（按路径复现 OnClickDown）——按钮 active 由装填状态机驱动，
/// 对端状态不同步时按钮 inactive → 点击排队丢弃。
///
/// 方案：Harmony patch `CylinderShellSelector.OnLoadButtonClicked()`（推弹）与 `OnMoveButtonClicked()`（切弹）
/// → 广播事件（携带实例 Transform 路径 + 事件类型）→ 对端按路径匹配实例直接调用同名方法
/// （IsApplyingCylinder 防环，不依赖按钮 active）。谁操作谁上报；主机中继给其他客机。
/// 实例定位：CylinderShellSelector 无单例，按 Transform 路径（含 Gun System Left/Right 区分左右炮）。
/// ⚠️ 这两个按钮从 ButtonClickSync.ShouldTrack 排除（见 ButtonClickSync），避免点击复现 + 方法调用双重触发。
/// </summary>
public sealed class CylinderActionSync : ISyncedModule
{
    public byte MsgType => 141;
    public const byte MsgTypeId = 141;

    private const byte EvLoadShell = 1;   // OnLoadButtonClicked：推弹头
    private const byte EvMoveCylinder = 2; // OnMoveButtonClicked：切弹舱

    /// <summary>公开事件常量（HarmonyPatches prefix 引用）。</summary>
    public const byte C_EvLoadShell = 1;
    public const byte C_EvMoveCylinder = 2;

    /// <summary>正在复现远端弹舱动作（防环：应用远端时本地方法不再转发）。</summary>
    public static bool IsApplyingCylinder;
    private static float _lastLocalAt = -1f;
    private static int _log;

    public void Tick(float dt) { } // 事件驱动，无轮询

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            byte ev = r.GetByte();
            string gunKey = r.GetString();
            if (net.IsHost)
            {
                // 主机中继给其他客机（不含发起者）
                foreach (var p in net.Roster)
                    if (!p.IsLocal && (ulong)p.SteamId != from)
                        net.Transport.Send(p.SteamId, data, true);
            }
            Apply(ev, gunKey);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CylinderActionSync OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { IsApplyingCylinder = false; _lastLocalAt = -1f; }

    /// <summary>本地 CylinderShellSelector 方法被调用（Harmony prefix）→ 广播事件。
    /// ⚠️ 用**关联 GunController 的 Transform 路径**（含 "Gun System Left/Right"，场景静态、两端一致）做标识——
    /// 与 ButtonClickSync 定位装药按钮同源（已验证两端稳定）。不用 turret.guns 索引（两端顺序可能不同：
    /// 客户端右炮=1、主机右炮=0 → 错位，"左右炮推弹头同步异常"根因）；也不用 CylinderShellSelector 自身路径
    /// （动态实例化两端可能不同）。</summary>
    public static void OnLocalAction(CylinderShellSelector cyl, int ev)
    {
        try
        {
            if (IsApplyingCylinder) return; // 防环：正在应用远端时不转发
            var net = CoopRuntime.Net;
            if (net == null || cyl == null || cyl.transform == null) return;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
            string gunKey = GunKeyOf(cyl);
            if (string.IsNullOrEmpty(gunKey))
            {
                CoopRuntime.LogSource?.LogWarning($"[CylinderActionSync] local gun key not found, skip (cyl path='{PathOf(cyl.transform)}')");
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now - _lastLocalAt < 0.15f) return; // 防环去重：同实例同事件 0.15s 内不重复上报
            _lastLocalAt = now;
            var w = NetProtocol.Begin((MsgType)MsgTypeId);
            w.Put((byte)ev); // ⚠️ 必须转 byte（OnPacket 用 GetByte 读）——原 w.Put(ev) 写 int 4 字节，读 1 字节错位 → gunKey 读空 → "apply cyl not found gun=''"
            w.Put(gunKey);
            var data = NetProtocol.Snapshot(w);
            if (net.IsHost)
            {
                foreach (var p in net.Roster)
                    if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
            else if (net.HostSteamId != 0)
                net.Transport.Send(net.HostSteamId, data, true);
            if ((++_log % 20) == 1)
                CoopRuntime.LogSource?.LogInfo($"[CylinderActionSync] local ev={ev} gun='{gunKey}'");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CylinderActionSync OnLocalAction: {ex.Message}"); }
    }

    /// <summary>对端应用：按 GunController 路径定位实例 → 调用同名方法（防环）。
    /// ⚠️ 不从 FindObjectsOfType 找 cyl（主机端 CylinderShellSelector 场景不可见/FindObjectsOfType 返回空 →
    /// 推弹头不同步根因）。改为从 turret.guns[i].artilleryReloadController.cylinderShellSelector 直接取
    /// （ReloadSync 已证明主机端能拿到 reload → 其 cylinderShellSelector 属性可用）。</summary>
    private static void Apply(byte ev, string gunKey)
    {
        var cyl = FindCylByGunKey(gunKey);
        if (cyl == null)
        {
            CoopRuntime.LogSource?.LogWarning($"[CylinderActionSync] apply cyl not found gun='{gunKey}'");
            return;
        }
        IsApplyingCylinder = true;
        try
        {
            // ⚠️ 2026-08-23 诊断：复现推弹头/切弹舱前状态机 st——定位"对端复现不推进 → Charge Rammer（state5）
            // 锁定不可交互"（延迟下对端 st 与主机不同步 → OnLoadButtonClicked 状态检查失败）。
            try
            {
                var rc = cyl.artilleryReloadController;
                if (rc != null)
                    CoopRuntime.LogSource?.LogInfo($"[CylinderActionSync] apply ev={ev} pre-st={rc.currentStateIndex} gun='{gunKey}'");
            }
            catch { }
            switch (ev)
            {
                case EvLoadShell:
                    // ⚠️ 2026-08-23 延迟容忍：复现推弹头前，若对端状态机落后（currentStateIndex < ShellRamming=4），
                    // 先 SetState 对齐到推弹阶段再 OnLoadButtonClicked——否则延迟下对端 st 还停在 BreechOpen(3)，
                    // OnLoadButtonClicked 状态检查失败 → 不推弹 → 对端 st 卡 → Charge Rammer（state5）锁定不可交互。
                    // SetState 只对齐到推弹前状态，推弹动画仍正常播放（不跳过）。
                    AlignReloadState(cyl, 4);
                    cyl.OnLoadButtonClicked();
                    break;
                case EvMoveCylinder: cyl.OnMoveButtonClicked(); break;
            }
            CoopRuntime.LogSource?.LogInfo($"[CylinderActionSync] applied ev={ev} gun='{gunKey}'");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CylinderActionSync apply: {ex.Message}"); }
        finally { IsApplyingCylinder = false; }
    }

    /// <summary>延迟容忍：对端状态机落后时 SetState 对齐到目标阶段（force=true 安全设置，不触发自动推进），
    /// 再刷新按钮 active（OnStateChanged + 弹舱按钮）——否则事件复现（推弹头/选药/下药）因对端 st 落后
    /// 状态检查失败 → 事件不执行 → 后续按钮锁定不可交互。</summary>
    private static void AlignReloadState(CylinderShellSelector cyl, int targetState)
    {
        try
        {
            var rc = cyl.artilleryReloadController;
            if (rc == null) return;
            if (rc.currentStateIndex >= targetState) return; // 不落后不设（不打断/不回退正常流程）
            rc.SetState(targetState, true);
            try { rc.OnStateChanged?.Invoke(rc.CurrentState); } catch { }
            // 兜底刷新弹舱按钮（推弹头 Load shell Rammer 等 active 随状态刷新）
            try { cyl.HandleReloadStateChanged(rc.CurrentState); } catch { }
            CoopRuntime.LogSource?.LogInfo($"[CylinderActionSync] align reload st -> {targetState} (was {rc.currentStateIndex})");
        }
        catch { }
    }

    /// <summary>按 GunController 路径找关联 CylinderShellSelector：
    /// 遍历 turret.guns，取 gun.artilleryReloadController.cylinderShellSelector（不依赖 FindObjectsOfType 场景可见性）。</summary>
    private static CylinderShellSelector FindCylByGunKey(string gunKey)
    {
        try
        {
            var turret = TurretController.Instance;
            if (turret == null || turret.guns == null) return null;
            for (int i = 0; i < turret.guns.Count; i++)
            {
                var g = turret.guns[i];
                if (g == null || g.transform == null) continue;
                string key = PathOf(g.transform);
                if (key != gunKey) continue;
                var reload = g.artilleryReloadController;
                if (reload == null) continue;
                var sel = reload.cylinderShellSelector;
                return sel; // 可能为 null（BepInEx interop 仅暴露 cylinderShellSelector）
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CylinderActionSync FindCylByGunKey: {ex.Message}"); }
        return null;
    }

    /// <summary>取 CylinderShellSelector 关联 GunController 的 Transform 路径（含 "Tactical Map/.../GunLeft"）。
    /// 通过 artilleryReloadController → TurretController.guns 匹配（不看索引顺序，按 Pointer 匹配）。</summary>
    private static string GunKeyOf(CylinderShellSelector cyl)
    {
        try
        {
            var turret = TurretController.Instance;
            if (turret == null || turret.guns == null || cyl == null) return "";
            var reload = cyl.artilleryReloadController;
            if (reload == null) return "";
            for (int i = 0; i < turret.guns.Count; i++)
            {
                var g = turret.guns[i];
                if (g == null || g.artilleryReloadController == null) continue;
                if (g.artilleryReloadController.Pointer == reload.Pointer)
                {
                    if (g.transform != null) return PathOf(g.transform);
                    return "gun" + i; // 兜底：拿不到路径用索引（通常不会发生）
                }
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CylinderActionSync GunKeyOf: {ex.Message}"); }
        return "";
    }

    private static string PathOf(Transform t)
    {
        try
        {
            string path = t.name ?? "";
            var p = t.parent;
            while (p != null)
            {
                path = (p.name ?? "") + "/" + path;
                p = p.parent;
            }
            return path;
        }
        catch { return ""; }
    }
}
