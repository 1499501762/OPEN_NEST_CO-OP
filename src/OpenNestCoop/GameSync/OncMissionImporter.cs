using System;
using System.Collections.Generic;
using OpenNestCoop.Core;
using OpenNestCore.Tasks;
#if MELONLOADER
using SleepyNodes = Il2CppSleepyNodes;
using Localisation = Il2CppLocalisation;
#endif
namespace OpenNestCoop.GameSync;

/// <summary>
/// 自定义任务 → **原生 MissionImporter** 转换器（"原生效果"方案）。
///
/// 原生从 JSON 构造完整合法任务图的机制是 <c>MissionImporter.ImportMission(json) → MissionGraph</c>
/// （原生 F3 调试链路：读 Exported.dat → ImportMission → StartOperation，全原生：场景/相机/HUD/节点执行）。
/// 我们把自定义任务 <see cref="OncMission"/> 序列化成 MissionImporter 的 JSON 格式
/// （MissionDefinition + NodeReference：NodeType=原生 State_* 名、NodeData=节点字段、Connections=连线），
/// 用 ImportMission 让原生构造完整图并 StartOperation —— 完全原生效果，无黑屏/NRE。
///
/// ⚠️ 节点映射：OncNodeKind → 原生 State_*（见 SleepyNodes diffable-cs）。先支持常用节点，逐步扩展。
/// </summary>
public static class OncMissionImporter
{
    /// <summary>把自定义任务导出成 MissionImporter JSON 格式（节点映射 + 连线）。</summary>
    public static string ExportMission(OncMission m)
    {
        if (m == null) return null;
        var root = new Dictionary<string, object>();
        root["MissionID"] = m.Id;
        root["MissionName"] = string.IsNullOrEmpty(m.DisplayName) ? m.Id : m.DisplayName;
        root["MissionType"] = MapType(m.MissionType);
        root["MapImage"] = "";
        root["MapTopoImage"] = "";
        root["RequisitionPoints"] = 0;
        root["PowderCharges"] = 0;
        // Zones（样例格式：Allied=炮台生成区 Role2 / Enemy=目标生成区 Role1）
        root["Zones"] = new List<object>
        {
            new Dictionary<string, object> {
                ["ID"] = "Allied", ["Name"] = "Turret Spawn", ["Role"] = 2, ["ZoneShape"] = 0,
                ["BottomLeft"] = new Dictionary<string, object> { ["Location"] = 44, ["X"] = 0, ["Y"] = 0 },
                ["Width"] = 10, ["Height"] = 10, ["Regions"] = new List<object>()
            },
            new Dictionary<string, object> {
                ["ID"] = "Enemy", ["Name"] = "Target Spawn", ["Role"] = 1, ["ZoneShape"] = 0,
                ["BottomLeft"] = new Dictionary<string, object> { ["Location"] = 84, ["X"] = 0, ["Y"] = 0 },
                ["Width"] = 10, ["Height"] = 10, ["Regions"] = new List<object>()
            }
        };

        var nodes = new Dictionary<string, object>();
        if (m.Nodes != null)
            for (int i = 0; i < m.Nodes.Count; i++)
            {
                var n = m.Nodes[i];
                if (n == null) continue;
                nodes[n.Id] = ExportNode(n);
            }
        root["Nodes"] = nodes;
        return OncJson.Serialize(root);
    }

    private static object ExportNode(OncNode n)
    {
        var nd = new Dictionary<string, object>();
        nd["ID"] = n.Id;
        nd["NodeType"] = MapNodeType(n.Kind);
        nd["PosX"] = 0.0;
        nd["PosY"] = 0.0;
        var data = ExportNodeData(n);
        data["NodeID"] = n.Id; // 样例格式：NodeData 含 NodeID
        nd["NodeData"] = data;

        var conns = new List<object>();
        if (n.To != null)
            for (int i = 0; i < n.To.Count; i++)
            {
                if (string.IsNullOrEmpty(n.To[i])) continue;
                var c = new Dictionary<string, object>();
                c["SourceNodeID"] = n.Id;
                c["SourceFieldName"] = "To";
                c["DestinationNodeID"] = n.To[i];
                c["DestinationFieldName"] = "From"; // 样例格式
                conns.Add(c);
            }
        nd["Connections"] = conns;
        return nd;
    }

    private static Dictionary<string, object> ExportNodeData(OncNode n)
    {
        var d = new Dictionary<string, object>();
        switch (n.Kind)
        {
            case OncNodeKind.WaitSeconds:
                d["Seconds"] = n.Seconds;
                break;
            case OncNodeKind.Notify:
                d["Text_Title"] = TextIdentifierJson(n.Title);
                d["Text_Description"] = TextIdentifierJson(n.Message);
                d["Duration"] = n.Duration > 0f ? n.Duration : 4f;
                break;
            case OncNodeKind.Teleprinter:
                d["Text"] = TextIdentifierJson(n.Message);
                break;
            case OncNodeKind.SpawnEntity:
                d["ID"] = n.EntityId;
                d["DisplayName"] = n.EntityName ?? n.EntityId;
                d["Role"] = 33; // Enemy Field Artillery（样例）
                d["Health"] = n.Health > 0 ? n.Health : 1;
                d["Armour"] = n.Armour;
                d["Stars"] = n.Stars;
                d["NumberToSpawn"] = 1;
                d["LocationToSpawn"] = new Dictionary<string, object> {
                    ["LocationType"] = 1, ["ZoneID"] = "Enemy", ["FuzzyLocation"] = false, ["RandomiseSubgrid"] = false
                };
                break;
            case OncNodeKind.DamageEntity:
                d["EntityFilter"] = n.EntityId;
                d["Damage"] = n.Value;
                break;
            case OncNodeKind.AddRequisitionPoints:
                d["Amount"] = n.Value;
                break;
            case OncNodeKind.AddPowderCharge:
                d["Amount"] = n.Value;
                break;
            case OncNodeKind.AddShell:
                d["ShellType"] = n.ShellId;
                d["Amount"] = n.Value;
                break;
            case OncNodeKind.StartTimer:
                d["TimerName"] = n.TimerId;
                d["Duration"] = n.Seconds;
                break;
            case OncNodeKind.StopTimer:
            case OncNodeKind.PauseTimer:
            case OncNodeKind.ResumeTimer:
                d["TimerName"] = n.TimerId;
                break;
            case OncNodeKind.AddTimerTime:
                d["TimerName"] = n.TimerId;
                d["Time"] = n.Seconds;
                break;
            case OncNodeKind.End:
                d["ForceSetState"] = false;
                break;
        }
        return d;
    }

    private static object TextIdentifierJson(string raw)
    {
        var d = new Dictionary<string, object>();
        d["Raw"] = raw ?? "";
        d["Key"] = "";
        return d;
    }

    /// <summary>OncNodeKind → 原生 State_* 节点类型名。</summary>
    private static string MapNodeType(OncNodeKind k)
    {
        switch (k)
        {
            case OncNodeKind.Start: return "State_Start";
            case OncNodeKind.End: return "State_End";
            case OncNodeKind.Fail: return "State_MissionFailed";
            case OncNodeKind.WaitSeconds: return "State_WaitSeconds";
            case OncNodeKind.WaitForEvent: return "State_WaitForNotification";
            case OncNodeKind.WaitEntityDestroyed: return "State_WaitEntityDestroyed";
            case OncNodeKind.Branch: return "State_ConditionBranch";
            case OncNodeKind.RandomBranch: return "State_RandomBranch";
            case OncNodeKind.Split: return "State_SplitBranch";
            case OncNodeKind.Objective: return "State_Objective";
            case OncNodeKind.Teleprinter: return "State_TeleprinterText";
            case OncNodeKind.Notify: return "State_SendUINotification";
            case OncNodeKind.SceneNotification: return "State_SendSceneNotification";
            case OncNodeKind.SpawnEntity: return "State_SpawnMapEntity";
            case OncNodeKind.MoveEntity: return "State_MoveMapEntity";
            case OncNodeKind.DamageEntity: return "State_DamageEntity";
            case OncNodeKind.SetEntityState: return "State_SetEntityState";
            case OncNodeKind.Impact: return "State_TriggerImpact";
            case OncNodeKind.AddRequisitionPoints: return "State_AddRequisitionPoints";
            case OncNodeKind.AddShell: return "State_AddShell";
            case OncNodeKind.AddPowderCharge: return "State_AddPowderCharge";
            case OncNodeKind.StartTimer: return "State_StartTimer";
            case OncNodeKind.StopTimer: return "State_StopTimer";
            case OncNodeKind.PauseTimer: return "State_PauseTimer";
            case OncNodeKind.ResumeTimer: return "State_UnpauseTimer";
            case OncNodeKind.AddTimerTime: return "State_TimerAddTime";
            case OncNodeKind.UnlockSceneObject: return "State_UnlockSceneObject";
            default: return "State_TestNode";
        }
    }

    private static string MapType(string t)
    {
        if (string.IsNullOrEmpty(t)) return "Campaign";
        string s = t.Trim();
        if (s.Equals("Chill", StringComparison.OrdinalIgnoreCase)) return "Chill";
        if (s.Equals("Challenge", StringComparison.OrdinalIgnoreCase) || s.Equals("Challange", StringComparison.OrdinalIgnoreCase)) return "Challange";
        if (s.Equals("Tutorial", StringComparison.OrdinalIgnoreCase)) return "Tutorial";
        return "Campaign";
    }

    /// <summary>
    /// 调原生 MissionImporter.ImportMission（JSON → 完整原生 MissionGraph）。失败返回 null。
    /// ⚠️ 原生标准格式是 **ExportPackage**（{MissionName, MissionJson: 字符串, Files: [base64图片]}）——
    /// ImportMission 内部遍历 Files、ConvertValue(JToken, Type, ExportPackage) 需要 package 上下文按目标类型
    /// 转换（TextIdentifier 等）。裸 MissionDefinition JSON（无 MissionJson/Files）走退化路径 →
    /// TextIdentifier 被还原成 Object → 打字机简报不显示。故这里自动把裸 JSON 包装成 ExportPackage。
    /// </summary>
    public static SleepyNodes.MissionGraph Import(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return MissionImporter.ImportMission(WrapExportPackage(json)); }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[OncImporter] ImportMission: {ex.Message}"); return null; }
    }

    /// <summary>裸 MissionDefinition JSON → ExportPackage 包装（MissionJson 字符串 + 空 Files）。
    /// 已含 "MissionJson" 键则视为已是 ExportPackage 原样返回。</summary>
    public static string WrapExportPackage(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;
        try
        {
            if (json.IndexOf("\"MissionJson\"", StringComparison.Ordinal) >= 0) return json; // 已是包装格式
            var root = new Dictionary<string, object>();
            string name = "Custom Mission";
            var o = OncJson.ParseObject(json);
            if (o != null && o.TryGetValue("MissionName", out var nm) && nm is string ns && !string.IsNullOrEmpty(ns)) name = ns;
            root["MissionName"] = name;
            root["MissionJson"] = json; // 字符串（ImportMission 内部再解析）
            root["Files"] = new List<object>(); // 无图片（MapImage/MapTopoImage 留空）
            return OncJson.Serialize(root);
        }
        catch { return json; }
    }

    /// <summary>导出 + 导入 + 日志（验证 MissionImporter 格式 / 生成的原生图）。</summary>
    public static SleepyNodes.MissionGraph ImportAndLog(OncMission m)
    {
        if (m == null) return null;
        var json = ExportMission(m);
        if (json == null) return null;
        CoopRuntime.LogSource?.LogInfo($"[OncImporter] json: {json}");
        var g = Import(json);
        if (g != null)
        {
            int nodeCount = 0;
            try { nodeCount = g.nodes != null ? g.nodes.Count : 0; } catch { }
            string gid = "?";
            try { gid = g.MissionID; } catch { }
            CoopRuntime.LogSource?.LogInfo($"[OncImporter] ImportMission OK: '{m.Id}' nodes={nodeCount} graph='{gid}'");
        }
        return g;
    }
}
