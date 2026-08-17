using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
using LiteNetLib.Utils;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 弹种同步（CylinderShellSelector，MsgType=109）。
/// 同步炮膛弹种选择器每个槽位的弹种（ShellId 逗号列表，主机权威 + 双向变化检测）。
/// 应用时用 cyl.shellPrefabs 匹配 ShellId 重建 bullets（不依赖不存在的 ShellRegistry）。
/// </summary>
public sealed class ShellSync : ISyncedModule
{
    public byte MsgType => 109;
    private const float Interval = 0.5f;
    private const float Heartbeat = 1.5f; // 周期全量广播弹舱（保证弹药消耗同步；主机权威）
    private float _timer;
    private float _heartbeat;
    private int _scanLog;
    private int _recvDiag;
    private readonly Dictionary<CylinderShellSelector, string> _known = new();

    public void Tick(float dt)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        if (net.State != SessionState.Hosting && net.State != SessionState.Joined) return;
        // 主机权威：只主机广播弹舱（含全 NULL——弹药消耗/清空也要同步）；
        // 客机切弹/上膛通过事件上行（ButtonClickSync 切舱按钮、ReloadAdvance 上膛），由主机应用后广播。
        if (!net.IsHost) return;
        _heartbeat += Interval;
        bool force = _heartbeat >= Heartbeat;
        if (force) _heartbeat = 0f;
        try
        {
            var cyls = UnityEngine.Object.FindObjectsOfType<CylinderShellSelector>();
            if (cyls == null || cyls.Length == 0)
            {
                if ((++_scanLog % 20) == 1)
                    CoopLog.Debug("ShellSync.noCyl", () => "[ShellSync] CylinderShellSelector not found (component missing or disabled)");
                return;
            }
            if ((++_scanLog % 10) == 1)
                CoopLog.Debug("ShellSync.scan", () => $"[ShellSync] scan cyls={cyls.Length} firstCsv='{SnapshotCsv(cyls[0])}'");
            for (int i = 0; i < cyls.Length; i++)
            {
                var cyl = cyls[i];
                if (cyl == null) continue;
                string csv = SnapshotCsv(cyl);
                // 主机权威：广播所有 csv（含全 NULL——弹药消耗同步）；变化或周期心跳才发
                if (!force && _known.TryGetValue(cyl, out var last) && last == csv) continue;
                _known[cyl] = csv;
                var w = NetProtocol.Begin((MsgType)MsgType);
                w.Put((byte)i);
                w.Put(csv ?? "");
                var data = NetProtocol.Snapshot(w);
                net.EnqueueBatch(data, true);
                CoopLog.Debug("ShellSync.send", () => $"[ShellSync] host send idx={i} csv='{csv}'", 1f);
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ShellSync Tick: {ex.Message}"); }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            byte idx = r.GetByte();
            string csv = r.GetString();
            if (net.IsHost)
                net.EnqueueBatch(data, true); // 转发
            // 诊断：打印收到内容（客机是否收到主机弹药 csv）
            if ((++_recvDiag % 3) == 1)
                CoopLog.Debug("ShellSync.recv", () => $"[ShellSync] recv idx={idx} csv='{csv}' isHost={net.IsHost}");
            ApplyCsv(idx, csv);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ShellSync OnPacket: {ex.Message}"); }
    }

    private static string SnapshotCsv(CylinderShellSelector cyl)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            if (cyl.bullets == null)
            {
                CoopLog.Debug("ShellSync.nullBullets", () => "[ShellSync] cyl.bullets is null (cannot detect shell type changes)");
                return sb.ToString();
            }
            int cnt = cyl.bullets.Count;
            int nullBp = 0, nullDef = 0;
            for (int i = 0; i < cnt; i++)
            {
                if (i > 0) sb.Append(',');
                var go = cyl.bullets[i];
                if (go == null) { sb.Append("NULL"); nullBp++; continue; }
                var bp = go.GetComponent<ShellBlueprint>();
                if (bp == null) bp = go.GetComponentInChildren<ShellBlueprint>(true);
                if (bp == null) { sb.Append("?"); nullBp++; continue; }
                if (bp.shellDefinition == null) { sb.Append("?"); nullDef++; continue; }
                sb.Append(bp.shellDefinition.ShellId ?? "");
            }
            if (cnt > 0 && (nullBp > 0 || nullDef > 0))
                CoopLog.Debug("ShellSync.snapshot", () => $"[ShellSync] SnapshotCsv cnt={cnt} nullBp={nullBp} nullDef={nullDef} csv='{sb}'", 1f);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ShellSync] SnapshotCsv exception: {ex.Message}"); }
        return sb.ToString();
    }

    // ⚠️ V1 死代码：BulletShellId 全仓零调用（逻辑已内联到 SnapshotCsv/ApplyCsv），已注释。
    // private static string BulletShellId(GameObject bullet)
    // {
    //     try
    //     {
    //         if (bullet == null) return "";
    //         var bp = bullet.GetComponent<ShellBlueprint>();
    //         if (bp == null) bp = bullet.GetComponentInChildren<ShellBlueprint>(true);
    //         if (bp == null || bp.shellDefinition == null) return "";
    //         return bp.shellDefinition.ShellId ?? "";
    //     }
    //     catch { return ""; }
    // }

    private static void ApplyCsv(byte idx, string csv)
    {
        try
        {
            var cyls = UnityEngine.Object.FindObjectsOfType<CylinderShellSelector>();
            if (cyls == null || idx >= cyls.Length || cyls[idx] == null) return;
            var cyl = cyls[idx];
            if (SnapshotCsv(cyl) == csv) return; // 无变化
            // 装填进行中不应用弹种（避免 ReplaceAllShells 打断/触发装填动画）
            try
            {
                if (cyl.artilleryReloadController != null && cyl.artilleryReloadController.working)
                {
                    CoopLog.Debug("ShellSync.skipReload", () => $"[ShellSync] reloading, skip shell apply idx={idx} csv='{csv}'");
                    return;
                }
            }
            catch { }
            var arr = string.IsNullOrEmpty(csv) ? Array.Empty<string>() : csv.Split(',');
            // 主机权威：全 NULL（空弹舱/弹药消耗空）也要应用——ReplaceAllShells 空数组清空对端弹舱跟随主机
            bool allNull = true;
            foreach (var id in arr)
                if (!string.IsNullOrEmpty(id) && id != "NULL" && id != "?") { allNull = false; break; }
            if (allNull)
            {
                try
                {
                    var empty = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GameObject>(0);
                    cyl.ReplaceAllShells(empty, false);
                    CoopRuntime.LogSource?.LogInfo($"[ShellSync] applied all-empty idx={idx} (host-authoritative clear follow)");
                }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ShellSync] all-empty apply failed: {ex.Message}"); }
                return;
            }
            // 诊断：打印本机 shellPrefabs 的 ShellId + prefab 名列表（排查 prefab 匹配失败）
            {
                var ids = new List<string>();
                var names = new List<string>();
                if (cyl.shellPrefabs != null)
                    foreach (var p in cyl.shellPrefabs)
                    {
                        string sid = "";
                        if (p != null)
                        {
                            names.Add(p.name ?? "");
                            var bp = p.GetComponent<ShellBlueprint>();
                            if (bp == null) bp = p.GetComponentInChildren<ShellBlueprint>(true);
                            if (bp != null && bp.shellDefinition != null) sid = bp.shellDefinition.ShellId ?? "";
                        }
                        ids.Add(sid);
                    }
                CoopRuntime.LogSource?.LogInfo($"[ShellSync] diag idx={idx} csv='{csv}' prefabCnt={(cyl.shellPrefabs == null ? -1 : cyl.shellPrefabs.Count)} prefabIds='{string.Join(",", ids)}' names='{string.Join("|", names)}'");
            }
            // ⚠️ 2026-08-15：shellPrefabs 偶发未初始化（客户端 gun1 元素全 null → 匹配失败 → 弹舱不同步
            // → 激发拉杆（Arm）/发射无法激活卡死）。先 EnsureInitialized() 确保 prefab 引用已填充。
            try
            {
                bool prefsEmpty = cyl.shellPrefabs == null || cyl.shellPrefabs.Length == 0;
                if (!prefsEmpty)
                {
                    bool prefsAllNull = true;
                    for (int pi = 0; pi < cyl.shellPrefabs.Length; pi++)
                        if (cyl.shellPrefabs[pi] != null) { prefsAllNull = false; break; }
                    prefsEmpty = prefsAllNull;
                }
                if (prefsEmpty) cyl.EnsureInitialized();
            }
            catch { }
            var list = new List<GameObject>();
            foreach (var id in arr)
            {
                GameObject go = null;
                if (!string.IsNullOrEmpty(id) && cyl.shellPrefabs != null)
                    foreach (var prefab in cyl.shellPrefabs)
                    {
                        if (prefab == null) continue;
                        var bp = prefab.GetComponent<ShellBlueprint>();
                        if (bp == null) bp = prefab.GetComponentInChildren<ShellBlueprint>(true);
                        if (bp != null && bp.shellDefinition != null && bp.shellDefinition.ShellId == id)
                        { go = prefab; break; }
                        // 兜底：ShellId 读不到（双设备下 shellDefinition 引用可能未加载）时按 prefab 名匹配
                        if (go == null && prefab.name != null
                            && prefab.name.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0)
                        { go = prefab; break; }
                    }
                // 兜底：shellPrefabs 仍未初始化/匹配不到时，从场景所有 ShellBlueprint 按 ShellId 匹配
                if (go == null && !string.IsNullOrEmpty(id))
                {
                    try
                    {
                        var bps = UnityEngine.Object.FindObjectsOfType<ShellBlueprint>(true);
                        if (bps != null)
                            foreach (var bp in bps)
                            {
                                if (bp == null || bp.shellDefinition == null) continue;
                                if (bp.shellDefinition.ShellId == id) { go = bp.gameObject; break; }
                            }
                    }
                    catch { }
                }
                list.Add(go);
            }
            // 全部匹配到真实 prefab 才应用（避免破坏弹药）；优先官方 ReplaceAllShells
            bool allMatched = list.Count > 0 && list.TrueForAll(g => g != null);
            if (!allMatched)
            {
                CoopRuntime.LogSource?.LogWarning($"[ShellSync] some prefabs unmatched, skip apply idx={idx} csv='{csv}'");
                return;
            }
            try
            {
                var arr2 = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GameObject>(list.Count);
                for (int i = 0; i < list.Count; i++) arr2[i] = list[i];
                cyl.ReplaceAllShells(arr2, false);
                CoopRuntime.LogSource?.LogInfo($"[ShellSync] applied idx={idx} csv='{csv}' (ReplaceAllShells)");
            }
            catch (Exception ex)
            {
                CoopRuntime.LogSource?.LogWarning($"[ShellSync] ReplaceAllShells failed, fallback rebuild: {ex.Message}");
                var rebuilt = new Il2CppSystem.Collections.Generic.List<GameObject>();
                foreach (var g in list) rebuilt.Add(g);
                cyl.bullets = rebuilt;
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"ShellSync ApplyCsv: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _known.Clear(); }
}
