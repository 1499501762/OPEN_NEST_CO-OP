using System;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 预备激发同步（ArmedFireRelayOneShot，MsgType=140）——事件解耦（P0）。
///
/// 背景：`Universal Button Arm Left/Right`（预备激发火炮拉杆）原本走 ButtonClickSync 实体耦合
/// （按路径找 LookAtTarget 复现 OnClickDown）——对端按钮 inactive 时点击排队 3s 丢弃 → 客机无法预备激发
/// （日志 177x `click queued (inactive)`）。而 `ArmedFireRelayOneShot.ArmLeft()/ArmRight()/DisarmLeft()/DisarmRight()`
/// 是独立业务方法（dump 确认存在、此前全仓零引用）。
///
/// 方案：Harmony patch 这 4 个方法 → 广播事件（携带实例 Transform 路径 + 事件类型）→ 对端按路径匹配
/// 实例直接调用同名方法（IsApplyingArm 防环，不依赖按钮 active）。谁操作谁上报；主机中继给其他客机。
/// 实例定位：ArmedFireRelayOneShot 无单例，用 Transform 路径（含 ArmingLeverParent Left/Right）区分左右炮。
/// </summary>
public sealed class ArmSync : ISyncedModule
{
    public byte MsgType => 140;
    public const byte MsgTypeId = 140;

    private const byte EvArmLeft = 1;
    private const byte EvArmRight = 2;
    private const byte EvDisarmLeft = 3;
    private const byte EvDisarmRight = 4;

    /// <summary>公开事件常量（HarmonyPatches prefix 引用，与 private 同值）。</summary>
    public const byte C_EvArmLeft = 1;
    public const byte C_EvArmRight = 2;
    public const byte C_EvDisarmLeft = 3;
    public const byte C_EvDisarmRight = 4;

    /// <summary>正在复现远端预备激发操作（防环：应用远端时本地方法不再转发）。</summary>
    public static bool IsApplyingArm;
    /// <summary>最近一次本地预备激发广播时间（防环去重：同实例同事件 0.15s 内不重复上报）。</summary>
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
            string path = r.GetString();
            if (net.IsHost)
            {
                // 主机中继给其他客机（不含发起者）
                foreach (var p in net.Roster)
                    if (!p.IsLocal && (ulong)p.SteamId != from)
                        net.Transport.Send(p.SteamId, data, true);
            }
            Apply(ev, path);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ArmSync OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { IsApplyingArm = false; _lastLocalAt = -1f; }

    /// <summary>本地 ArmedFireRelayOneShot 方法被调用（Harmony prefix/postfix）→ 广播事件。</summary>
    public static void OnLocalArm(Zagreekie.Tools.ArmedFireRelayOneShot relay, int ev)
    {
        try
        {
            if (IsApplyingArm) return; // 防环：正在应用远端时不转发
            var net = CoopRuntime.Net;
            if (net == null || relay == null || relay.transform == null) return;
            if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
            // 防环兜底：同实例同事件 0.15s 内去重（防 apply 触发二次上报 → 双端互相触发）
            float now = Time.realtimeSinceStartup;
            if (now - _lastLocalAt < 0.15f) return;
            _lastLocalAt = now;
            string path = PathOf(relay.transform);
            if (string.IsNullOrEmpty(path)) return;
            var w = NetProtocol.Begin((MsgType)MsgTypeId);
            w.Put((byte)ev); // ⚠️ 必须转 byte（OnPacket 用 GetByte 读）——原 w.Put(ev) 写 int 4 字节，读 1 字节错位
            w.Put(path);
            var data = NetProtocol.Snapshot(w);
            if (net.IsHost)
            {
                foreach (var p in net.Roster)
                    if (!p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
            else if (net.HostSteamId != 0)
                net.Transport.Send(net.HostSteamId, data, true);
            if ((++_log % 20) == 1)
                CoopRuntime.LogSource?.LogInfo($"[ArmSync] local ev={ev} path='{path}'");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ArmSync OnLocalArm: {ex.Message}"); }
    }

    /// <summary>对端应用：定位 ArmedFireRelayOneShot 实例 → 调用同名方法（防环）。
    /// ⚠️ 2026-08-25：客机左炮 Arm 未激活——若每个 ArmedFireRelayOneShot 实例只管一炮（左/右各一），
    /// 取 relays[0] 会调错炮。改为按方向匹配实例（path 含 Left/Right）；单实例管双炮时 fallback relays[0]。</summary>
    private static void Apply(byte ev, string path)
    {
        var relays = UnityEngine.Object.FindObjectsOfType<Zagreekie.Tools.ArmedFireRelayOneShot>(true);
        if (relays == null || relays.Length == 0)
        {
            CoopRuntime.LogSource?.LogWarning($"[ArmSync] apply ev={ev} NO RELAY FOUND (path='{path}')");
            return;
        }
        bool wantLeft = ev == EvArmLeft || ev == EvDisarmLeft;
        Zagreekie.Tools.ArmedFireRelayOneShot relay = null;
        string rp = "";
        foreach (var r in relays)
        {
            if (r == null || r.transform == null) continue;
            string rp2 = "";
            try { rp2 = PathOf(r.transform); } catch { }
            bool isLeft = rp2.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isLeft == wantLeft) { relay = r; rp = rp2; break; }
        }
        if (relay == null) { relay = relays[0]; try { if (relay.transform != null) rp = PathOf(relay.transform); } catch { } }
        CoopLog.Info("ArmSync.apply", () => $"[ArmSync] apply ev={ev} relays={relays.Length} relay='{rp}' path='{path}'", 0.5f);
        IsApplyingArm = true;
        try
        {
            switch (ev)
            {
                case EvArmLeft: relay.ArmLeft(); break;
                case EvArmRight: relay.ArmRight(); break;
                case EvDisarmLeft: relay.DisarmLeft(); break;
                case EvDisarmRight: relay.DisarmRight(); break;
            }
            CoopRuntime.LogSource?.LogInfo($"[ArmSync] applied ev={ev} n={relays.Length}");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ArmSync apply: {ex.Message}"); }
        finally { IsApplyingArm = false; }
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
