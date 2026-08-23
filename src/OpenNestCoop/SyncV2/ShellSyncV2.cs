using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 弹种同步（ShellSyncV2，MsgType=219）。M7：把 V1 <c>ShellSync</c>（109）迁入分层架构。
/// <see cref="V2Authority.Host"/>：只主机广播弹舱 ShellId 逗号列表（含全 NULL——弹药消耗/清空也要同步，
/// 0.5s 变化检测 + 1.5s 心跳）；客机切弹/上膛经事件上行（ButtonLayer 点击、ReloadSyncV2），主机应用后广播。
/// 应用用 cyl.shellPrefabs 匹配 ShellId 重建 bullets；装填中跳过；全 NULL 清空跟随主机。
/// </summary>
public sealed class ShellSyncV2 : ISyncedModule
{
    public static ShellSyncV2 Instance { get; } = new ShellSyncV2();

    private ShellSyncV2() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2Shell;

    private const float Interval = 0.5f;
    private const float Heartbeat = 1.5f;
    private float _timer, _heartbeat;
    private readonly Dictionary<CylinderShellSelector, string> _known = new();

    public void Tick(float dt)
    {
        if (!Store.IsOnline || !Store.IsHost) return; // 主机权威：只主机广播
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        _heartbeat += Interval;
        bool force = _heartbeat >= Heartbeat;
        if (force) _heartbeat = 0f;
        try
        {
            var cyls = UnityEngine.Object.FindObjectsOfType<CylinderShellSelector>();
            if (cyls == null || cyls.Length == 0) return;
            for (int i = 0; i < cyls.Length; i++)
            {
                var cyl = cyls[i];
                if (cyl == null) continue;
                string csv = SnapshotCsv(cyl);
                if (!force && _known.TryGetValue(cyl, out var last) && last == csv) continue;
                _known[cyl] = csv;
                Store.Broadcast((byte)OpenNestCoop.Net.MsgType.V2Shell, w =>
                {
                    w.Put((byte)i);
                    w.Put(csv ?? "");
                }, reliable: false);
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ShellSyncV2] Tick: {ex.Message}"); }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            byte idx = r.GetByte();
            string csv = r.GetString();
            if (Store.IsHost) _net?.EnqueueBatch(data, true); // 转发
            ApplyCsv(idx, csv);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ShellSyncV2] OnPacket: {ex.Message}"); }
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _known.Clear(); }

    private static string SnapshotCsv(CylinderShellSelector cyl)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            if (cyl.bullets == null) return sb.ToString();
            int cnt = cyl.bullets.Count;
            for (int i = 0; i < cnt; i++)
            {
                if (i > 0) sb.Append(',');
                var go = cyl.bullets[i];
                if (go == null) { sb.Append("NULL"); continue; }
                var bp = go.GetComponent<ShellBlueprint>();
                if (bp == null) bp = go.GetComponentInChildren<ShellBlueprint>(true);
                if (bp == null || bp.shellDefinition == null) { sb.Append("?"); continue; }
                sb.Append(bp.shellDefinition.ShellId ?? "");
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ShellSyncV2] SnapshotCsv: {ex.Message}"); }
        return sb.ToString();
    }

    private static void ApplyCsv(byte idx, string csv)
    {
        try
        {
            var cyls = UnityEngine.Object.FindObjectsOfType<CylinderShellSelector>();
            if (cyls == null || idx >= cyls.Length || cyls[idx] == null) return;
            var cyl = cyls[idx];
            if (SnapshotCsv(cyl) == csv) return;
            try { if (cyl.artilleryReloadController != null && cyl.artilleryReloadController.working) return; } catch { }
            var arr = string.IsNullOrEmpty(csv) ? Array.Empty<string>() : csv.Split(',');
            bool allNull = true;
            foreach (var id in arr)
                if (!string.IsNullOrEmpty(id) && id != "NULL" && id != "?") { allNull = false; break; }
            if (allNull)
            {
                try
                {
                    var empty = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GameObject>(0);
                    cyl.ReplaceAllShells(empty, false);
                }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ShellSyncV2] all-empty apply: {ex.Message}"); }
                return;
            }
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
                        if (bp != null && bp.shellDefinition != null && bp.shellDefinition.ShellId == id) { go = prefab; break; }
                        if (go == null && prefab.name != null
                            && prefab.name.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0) { go = prefab; break; }
                    }
                list.Add(go);
            }
            bool allMatched = list.Count > 0 && list.TrueForAll(g => g != null);
            if (!allMatched)
            {
                CoopRuntime.LogSource?.LogWarning($"[ShellSyncV2] some prefabs unmatched, skip apply idx={idx} csv='{csv}'");
                return;
            }
            try
            {
                var arr2 = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GameObject>(list.Count);
                for (int i = 0; i < list.Count; i++) arr2[i] = list[i];
                cyl.ReplaceAllShells(arr2, false);
            }
            catch (Exception ex)
            {
                CoopRuntime.LogSource?.LogWarning($"[ShellSyncV2] ReplaceAllShells fallback: {ex.Message}");
                var rebuilt = new Il2CppSystem.Collections.Generic.List<GameObject>();
                foreach (var g in list) rebuilt.Add(g);
                cyl.bullets = rebuilt;
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[ShellSyncV2] ApplyCsv: {ex.Message}"); }
    }
}
