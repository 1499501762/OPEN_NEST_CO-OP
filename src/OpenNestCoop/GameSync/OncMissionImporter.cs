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
                // 字段语义见 13.2/13.4：OnlyQueue=false 出字；WaitUntilComplete=false 不卡任务；
                // AlarmState 简报告警灯；EntityIDToReplace 缺 → OnEnter 遍历 NRE（补空列表）。
                d["Text"] = TextIdentifierJson(n.Message);
                d["Printer"] = 0;               // Primary
                d["OnlyQueue"] = false;
                d["WaitUntilComplete"] = false;
                d["AlarmState"] = "Low";
                d["EntityIDToReplace"] = new List<object>();
                break;

            case OncNodeKind.SpawnEntity:
                // 参考 `ref/mission editor/Docs/Observador.definition.json` + 13.5 取证。
                // Role 复合枚举（默认 33=Enemy Field Artillery；要可击杀目标用 131617=Target|EnemyGroup3|Infantry）。
                d["ID"] = n.EntityId;
                d["DisplayName"] = TextIdentifierJson(n.EntityName ?? n.EntityId);
                d["Role"] = ParseRoleInt(n.Role, 33);
                d["Icon"] = n.Role ?? "Enemy Field Artillery";
                d["Health"] = n.Health > 0 ? n.Health : 1;
                d["Armour"] = n.Armour;
                d["Stars"] = n.Stars;
                d["NumberToSpawn"] = 1;
                d["Scale"] = 1;
                d["StartingState"] = "None";
                d["PresetIcon"] = true;
                // ⚠️ 13.5：SetContextVariable=true + LastSpawnedEntity=EntityTarget → 生成后把实体存上下文键，
                // 配合 WaitEntityDestroyed 的 Entites(FromContext+EntityTarget) 才能正确等待摧毁。
                d["SetContextVariable"] = true;
                d["LastSpawnedEntity"] = "EntityTarget";
                d["LocationToSpawn"] = new Dictionary<string, object> {
                    ["LocationType"] = 1,       // Zone
                    ["ZoneID"] = n.Role != null && n.Role.IndexOf("Ally", StringComparison.OrdinalIgnoreCase) >= 0 ? "Allied" : "Enemy",
                    ["FuzzyLocation"] = true,
                    ["RandomiseSubgrid"] = true,
                };
                d["ImmuneShells"] = new List<object>();
                break;

            case OncNodeKind.WaitEntityDestroyed:
                // ⚠️ 13.5：Entites（TargetSelection）为 null → OnExecute 立即通过（不等待）。
                // 配 FromContext + EntityTarget + All 才真正等待对应实体被摧毁。
                d["Entites"] = new Dictionary<string, object> {
                    ["SourceType"] = 0,   // FromContext
                    ["ContextKey"] = 0,   // EntityContextKeys.EntityTarget
                    ["CountType"] = 0,    // All
                    ["Count"] = 0,
                };
                break;

            case OncNodeKind.DamageEntity:
                d["EntityFilter"] = n.EntityId;
                d["Damage"] = n.Value;
                break;

            case OncNodeKind.MoveEntity:
                d["LocationToMoveTo"] = GridLocationJson(n.X, n.Y);
                if (n.Smooth) d["Duration"] = n.Seconds > 0f ? n.Seconds : 3f;
                break;

            case OncNodeKind.SetEntityState:
                d["StateToAdd"] = n.Role ?? "Destroyed";
                d["StateValue"] = n.Value;
                break;

            case OncNodeKind.Impact:
                d["LocationHit"] = GridLocationJson(n.X, n.Y);
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
            case OncNodeKind.WaitTimerExpired: // State_GenericTimer 兜底：跑一个命名计时器
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

            case OncNodeKind.WaitForEvent:
                d["NotificationID"] = n.EventId;
                break;

            case OncNodeKind.SceneNotification:
                d["NotifID"] = n.NotifId;
                break;

            case OncNodeKind.UnlockSceneObject:
                d["UnlockedSceneObjects"] = new List<object> { n.NotifId };
                break;

            case OncNodeKind.Scripted:
                // 脚本化模块 → 原生 State_CustomTrackingVariable 载体：
                // variableName="onc.script.<name>"，桥接层 Update 轮询 CurrentState 进入该节点时分派脚本模块。
                d["variableName"] = "onc.script." + n.ModuleName;
                d["CustomVariableKey"] = n.ModuleName;
                break;

            case OncNodeKind.End:
                d["ForceSetState"] = false;
                break;
        }
        return d;
    }

    /// <summary>文本标识（TextIdentifier：Raw + 空 Key；ImportMission 需 RepairImportedTexts 回填）。</summary>
    private static object TextIdentifierJson(string raw)
    {
        var d = new Dictionary<string, object>();
        d["Raw"] = raw ?? "";
        d["Key"] = "";
        return d;
    }

    /// <summary>网格坐标（GridLocation：Location 网格名 + X/Y）。</summary>
    private static object GridLocationJson(float x, float y)
    {
        var d = new Dictionary<string, object>();
        d["Location"] = "";
        d["X"] = x;
        d["Y"] = y;
        return d;
    }

    /// <summary>Role 字符串 → 复合枚举 int（数字原样；别名查表；未知默认 33=Enemy Field Artillery）。</summary>
    private static int ParseRoleInt(string role, int fallback)
    {
        if (string.IsNullOrEmpty(role)) return fallback;
        if (int.TryParse(role, out int v)) return v;
        // 13.5：Target|EnemyGroup3|Infantry = 131617（可被 ImpactTracker 识别为炮击目标）
        if (role.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0)
            return 131617;
        if (role.IndexOf("Ally", StringComparison.OrdinalIgnoreCase) >= 0)
            return 6; // Ally, Infantry（参考样例 Role=6）
        if (role.IndexOf("Enemy", StringComparison.OrdinalIgnoreCase) >= 0)
            return 33;
        return fallback;
    }

    /// <summary>OncNodeKind → 原生 State_* 节点类型名（权威目录见 docs/NODE_CATALOGUE.md / memory）。
    /// ⚠️ 事件类等待（WaitForEvent/WaitTimerExpired）原生语义是 Event_* 节点接线，这里用最接近的 State 节点
    /// 兜底（可在原生 JSON 里手写精确 Event 接线）；Objective 系需完整 ObjectiveGraph 引用，见 13.5 限制。</summary>
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
            case OncNodeKind.WaitTimerExpired: return "State_GenericTimer";
            case OncNodeKind.Branch: return "State_ConditionBranch";
            case OncNodeKind.RandomBranch: return "State_RandomBranch";
            case OncNodeKind.Split: return "State_SplitBranch";
            case OncNodeKind.Objective: return "State_Objective";
            case OncNodeKind.ObjectiveComplete: return "State_Objective";
            case OncNodeKind.ObjectiveFail: return "State_Objective";
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
            case OncNodeKind.Scripted: return "State_CustomTrackingVariable";
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
