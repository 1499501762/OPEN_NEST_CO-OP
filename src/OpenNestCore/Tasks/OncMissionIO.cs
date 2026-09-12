using System;
using System.Collections.Generic;

namespace OpenNestCore.Tasks;

/// <summary>
/// 自定义任务 JSON 导入/导出（"任务文件"定义方式）。
/// 把 <see cref="OncMission"/>（节点图）/ <see cref="OncOperation"/>（战役，前置后置）与 JSON 字符串互转；
/// 也可导出/导入联机"同步序号"小负载（<see cref="OncMissionSyncState"/>）。
/// 显式字段映射（不用反射），IL2CPP 安全；JSON 解析用 <see cref="OncJson"/>。
///
/// 任务文件 JSON 结构（节选）：
/// <code>
/// {
///   "Id": "custom.defend", "DisplayName": "坚守阵地", "SceneName": "Mission tutorial 2",
///   "Requires": ["custom.prev"],
///   "Objectives": [ { "Id": "survive", "Title": "坚守", "Type": "survive", "Target": 300 } ],
///   "Nodes": [
///     { "Id": "n1", "Kind": "Start" },
///     { "Id": "n2", "Kind": "Print", "Message": "敌军接近！", "To": ["n3"] },
///     { "Id": "n3", "Kind": "Objective", "ObjectiveId": "survive", "ObjectiveAction": "Start" },
///     { "Id": "n4", "Kind": "WaitSeconds", "Seconds": 300, "From": ["n3"] },
///     { "Id": "n5", "Kind": "ObjectiveComplete", "ObjectiveId": "survive", "From": ["n4"] },
///     { "Id": "n6", "Kind": "End", "From": ["n5"] }
///   ]
/// }
/// </code>
/// </summary>
public static class OncMissionIO
{
    // ================= 导出 =================

    public static string Save(OncMission m)
    {
        if (m == null) return "null";
        var o = new Dictionary<string, object>();
        o["Id"] = m.Id;
        o["DisplayName"] = m.DisplayName;
        o["Description"] = m.Description;
        o["MissionType"] = m.MissionType;
        o["SceneName"] = m.SceneName;
        o["Seed"] = m.Seed;
        o["EntryPointId"] = m.EntryPointId;
        o["Requires"] = ToStrList(m.Requires);
        o["UnlockCondition"] = m.UnlockCondition;
        o["NativeComplete"] = m.NativeComplete;
        if (m.Card != null) o["Card"] = SaveCard(m.Card);
        o["Objectives"] = SaveObjectives(m.Objectives);
        o["Nodes"] = SaveNodes(m.Nodes);
        return OncJson.Serialize(o);
    }

    public static string SaveOperation(OncOperation op)
    {
        if (op == null) return "null";
        var o = new Dictionary<string, object>();
        o["Id"] = op.Id;
        o["DisplayName"] = op.DisplayName;
        o["Description"] = op.Description;
        var ms = new List<object>();
        if (op.Missions != null)
            for (int i = 0; i < op.Missions.Count; i++)
            {
                var mr = op.Missions[i];
                if (mr == null) continue;
                var mo = new Dictionary<string, object>();
                mo["MissionId"] = mr.MissionId;
                mo["Requires"] = ToStrList(mr.Requires);
                mo["Condition"] = mr.Condition;
                ms.Add(mo);
            }
        o["Missions"] = ms;
        return OncJson.Serialize(o);
    }

    /// <summary>导出联机"同步序号"负载（小 JSON）。</summary>
    public static string SaveSyncState(OncMissionSyncState s)
    {
        if (s == null) return "null";
        var o = new Dictionary<string, object>();
        o["MissionId"] = s.MissionId;
        o["IsFinished"] = s.IsFinished;
        o["IsSuccess"] = s.IsSuccess;
        o["DoneCount"] = s.DoneCount;
        o["DoneNodeIds"] = ToStrList(s.DoneNodeIds);
        o["ActiveNodeIds"] = ToStrList(s.ActiveNodeIds);
        var obs = new List<object>();
        if (s.Objectives != null)
            for (int i = 0; i < s.Objectives.Count; i++)
            {
                var os = s.Objectives[i];
                if (os == null) continue;
                var oo = new Dictionary<string, object>();
                oo["Id"] = os.Id;
                oo["Status"] = os.Status;
                oo["Progress"] = os.Progress;
                obs.Add(oo);
            }
        o["Objectives"] = obs;
        var vars = new List<object>();
        if (s.Variables != null)
            for (int i = 0; i < s.Variables.Count; i++)
            {
                var vs = s.Variables[i];
                if (vs == null) continue;
                var vo = new Dictionary<string, object>();
                vo["Name"] = vs.Name;
                vo["Value"] = vs.Value;
                vars.Add(vo);
            }
        o["Variables"] = vars;
        return OncJson.Serialize(o);
    }

    // ================= 导入 =================

    public static OncMission Load(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        var o = OncJson.ParseObject(json);
        if (o == null) return null;
        var syms = ReadSymbols(o); // 顶层 "#sym" 符号表（定义任务所在场景等）
        var m = new OncMission();
        m.Id = OncJson.GetString(o, "Id");
        m.DisplayName = OncJson.GetString(o, "DisplayName");
        m.Description = OncJson.GetString(o, "Description");
        m.MissionType = OncJson.GetString(o, "MissionType");
        m.SceneName = ResolveSym(OncJson.GetString(o, "SceneName"), syms);
        m.Seed = OncJson.GetInt(o, "Seed", -1);
        m.EntryPointId = OncJson.GetString(o, "EntryPointId");
        m.UnlockCondition = OncJson.GetString(o, "UnlockCondition");
        m.NativeComplete = OncJson.GetBool(o, "NativeComplete");
        m.Card = LoadCard(OncJson.GetObject(o, "Card"));
        m.Requires = ToList(OncJson.GetArray(o, "Requires"));
        m.Objectives = LoadObjectives(OncJson.GetArray(o, "Objectives"));
        m.Nodes = LoadNodes(OncJson.GetArray(o, "Nodes"));
        return m;
    }

    /// <summary>读取顶层 "#sym" 符号表（"#sym": { "名字": "值", ... }）——用于定义任务所在场景等可复用值。</summary>
    private static Dictionary<string, string> ReadSymbols(Dictionary<string, object> o)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var s = OncJson.GetObject(o, "#sym");
            if (s == null) return d;
            foreach (var kv in s)
                if (kv.Value != null) d[kv.Key] = kv.Value.ToString();
        }
        catch { }
        return d;
    }

    /// <summary>解析 "#sym:名字" 符号引用：优先查符号表；找不到剥掉 "#sym:" 前缀用字面
    /// （如 "#sym:Mission tutorial 1" → "Mission tutorial 1"）。</summary>
    private static string ResolveSym(string v, Dictionary<string, string> syms)
    {
        if (string.IsNullOrEmpty(v) || !v.StartsWith("#sym:", StringComparison.Ordinal)) return v;
        string key = v.Substring(5);
        if (syms != null && syms.TryGetValue(key, out var val) && !string.IsNullOrEmpty(val)) return val;
        return key;
    }

    public static OncOperation LoadOperation(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        var o = OncJson.ParseObject(json);
        if (o == null) return null;
        var op = new OncOperation();
        op.Id = OncJson.GetString(o, "Id");
        op.DisplayName = OncJson.GetString(o, "DisplayName");
        op.Description = OncJson.GetString(o, "Description");
        var arr = OncJson.GetArray(o, "Missions");
        if (arr != null)
            for (int i = 0; i < arr.Count; i++)
            {
                var mo = arr[i] as Dictionary<string, object>;
                if (mo == null) continue;
                var mr = new OncMissionRef();
                mr.MissionId = OncJson.GetString(mo, "MissionId");
                mr.Condition = OncJson.GetString(mo, "Condition");
                mr.Requires = ToList(OncJson.GetArray(mo, "Requires"));
                op.Missions.Add(mr);
            }
        return op;
    }

    public static OncMissionSyncState LoadSyncState(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        var o = OncJson.ParseObject(json);
        if (o == null) return null;
        var s = new OncMissionSyncState();
        s.MissionId = OncJson.GetString(o, "MissionId");
        s.IsFinished = OncJson.GetBool(o, "IsFinished");
        s.IsSuccess = OncJson.GetBool(o, "IsSuccess");
        s.DoneCount = OncJson.GetInt(o, "DoneCount");
        s.DoneNodeIds = ToList(OncJson.GetArray(o, "DoneNodeIds"));
        s.ActiveNodeIds = ToList(OncJson.GetArray(o, "ActiveNodeIds"));
        s.Objectives = new List<OncObjectiveState>();
        var arr = OncJson.GetArray(o, "Objectives");
        if (arr != null)
            for (int i = 0; i < arr.Count; i++)
            {
                var oo = arr[i] as Dictionary<string, object>;
                if (oo == null) continue;
                s.Objectives.Add(new OncObjectiveState
                {
                    Id = OncJson.GetString(oo, "Id"),
                    Status = OncJson.GetInt(oo, "Status"),
                    Progress = OncJson.GetInt(oo, "Progress"),
                });
            }
        s.Variables = new List<OncScriptVarState>();
        var varr = OncJson.GetArray(o, "Variables");
        if (varr != null)
            for (int i = 0; i < varr.Count; i++)
            {
                var vo = varr[i] as Dictionary<string, object>;
                if (vo == null) continue;
                s.Variables.Add(new OncScriptVarState
                {
                    Name = OncJson.GetString(vo, "Name"),
                    Value = OncJson.GetString(vo, "Value"),
                });
            }
        return s;
    }

    // ================= 内部转换 =================

    private static List<object> SaveNodes(List<OncNode> nodes)
    {
        var list = new List<object>();
        if (nodes == null) return list;
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (n == null) continue;
            var o = new Dictionary<string, object>();
            o["Id"] = n.Id;
            o["Kind"] = n.Kind.ToString();
            o["From"] = ToStrList(n.From);
            o["To"] = ToStrList(n.To);
            o["Seconds"] = n.Seconds;
            o["EventId"] = n.EventId;
            if (n.Routes != null && n.Routes.Count > 0)
            {
                var rs = new List<object>();
                for (int r = 0; r < n.Routes.Count; r++)
                    if (n.Routes[r] != null)
                    {
                        var ro = new Dictionary<string, object>();
                        ro["EventId"] = n.Routes[r].EventId;
                        ro["TargetNodeId"] = n.Routes[r].TargetNodeId;
                        rs.Add(ro);
                    }
                o["Routes"] = rs;
            }
            o["Timeout"] = n.Timeout;
            o["Title"] = n.Title;
            o["Message"] = n.Message;
            o["Duration"] = n.Duration;
            o["ObjectiveId"] = n.ObjectiveId;
            o["ObjectiveAction"] = n.ObjectiveAction.ToString();
            o["Value"] = n.Value;
            o["EntityId"] = n.EntityId;
            o["EntityName"] = n.EntityName;
            o["Health"] = n.Health;
            o["Armour"] = n.Armour;
            o["Stars"] = n.Stars;
            o["Role"] = n.Role;
            o["X"] = n.X;
            o["Y"] = n.Y;
            o["Smooth"] = n.Smooth;
            o["ShellId"] = n.ShellId;
            o["Slot"] = n.Slot;
            o["TimerId"] = n.TimerId;
            o["NotifId"] = n.NotifId;
            o["CustomData"] = n.CustomData;
            o["ModuleName"] = n.ModuleName;
            o["ModuleArgs"] = n.ModuleArgs;
            list.Add(o);
        }
        return list;
    }

    private static List<OncNode> LoadNodes(List<object> arr)
    {
        var nodes = new List<OncNode>();
        if (arr == null) return nodes;
        for (int i = 0; i < arr.Count; i++)
        {
            var o = arr[i] as Dictionary<string, object>;
            if (o == null) continue;
            var n = new OncNode();
            n.Id = OncJson.GetString(o, "Id");
            n.Kind = ParseKind(OncJson.GetString(o, "Kind"));
            n.From = ToList(OncJson.GetArray(o, "From"));
            n.To = ToList(OncJson.GetArray(o, "To"));
            n.Seconds = OncJson.GetFloat(o, "Seconds");
            n.EventId = OncJson.GetString(o, "EventId");
            n.Timeout = OncJson.GetFloat(o, "Timeout");
            n.Title = OncJson.GetString(o, "Title");
            n.Message = OncJson.GetString(o, "Message");
            n.Duration = OncJson.GetFloat(o, "Duration");
            n.ObjectiveId = OncJson.GetString(o, "ObjectiveId");
            n.ObjectiveAction = ParseObjectiveAction(OncJson.GetString(o, "ObjectiveAction"));
            n.Value = OncJson.GetInt(o, "Value");
            n.EntityId = OncJson.GetString(o, "EntityId");
            n.EntityName = OncJson.GetString(o, "EntityName");
            n.Health = OncJson.GetInt(o, "Health");
            n.Armour = OncJson.GetInt(o, "Armour");
            n.Stars = OncJson.GetInt(o, "Stars");
            n.Role = OncJson.GetString(o, "Role");
            n.X = OncJson.GetFloat(o, "X");
            n.Y = OncJson.GetFloat(o, "Y");
            n.Smooth = OncJson.GetBool(o, "Smooth");
            n.ShellId = OncJson.GetString(o, "ShellId");
            n.Slot = OncJson.GetInt(o, "Slot", -1);
            n.TimerId = OncJson.GetString(o, "TimerId");
            n.NotifId = OncJson.GetString(o, "NotifId");
            n.CustomData = OncJson.GetString(o, "CustomData");
            n.ModuleName = OncJson.GetString(o, "ModuleName");
            n.ModuleArgs = OncJson.GetString(o, "ModuleArgs");
            var routes = OncJson.GetArray(o, "Routes");
            if (routes != null)
                for (int r = 0; r < routes.Count; r++)
                {
                    var ro = routes[r] as Dictionary<string, object>;
                    if (ro == null) continue;
                    if (n.Routes == null) n.Routes = new List<OncEventRoute>();
                    n.Routes.Add(new OncEventRoute(
                        OncJson.GetString(ro, "EventId"),
                        OncJson.GetString(ro, "TargetNodeId")));
                }
            nodes.Add(n);
        }
        return nodes;
    }

    private static List<object> SaveObjectives(List<OncObjective> objectives)
    {
        var list = new List<object>();
        if (objectives == null) return list;
        for (int i = 0; i < objectives.Count; i++)
        {
            var obj = objectives[i];
            if (obj == null) continue;
            var o = new Dictionary<string, object>();
            o["Id"] = obj.Id;
            o["Title"] = obj.Title;
            o["Description"] = obj.Description;
            o["Type"] = obj.Type;
            o["Target"] = obj.Target;
            list.Add(o);
        }
        return list;
    }

    private static List<OncObjective> LoadObjectives(List<object> arr)
    {
        var list = new List<OncObjective>();
        if (arr == null) return list;
        for (int i = 0; i < arr.Count; i++)
        {
            var o = arr[i] as Dictionary<string, object>;
            if (o == null) continue;
            list.Add(new OncObjective
            {
                Id = OncJson.GetString(o, "Id"),
                Title = OncJson.GetString(o, "Title"),
                Description = OncJson.GetString(o, "Description"),
                Type = OncJson.GetString(o, "Type"),
                Target = OncJson.GetInt(o, "Target"),
            });
        }
        return list;
    }

    private static object SaveCard(OncMissionCard c)
    {
        var o = new Dictionary<string, object>();
        o["X"] = c.X;
        o["Y"] = c.Y;
        o["Width"] = c.Width;
        o["Height"] = c.Height;
        o["TitleColor"] = c.TitleColor;
        o["Background"] = c.Background;
        return o;
    }

    private static OncMissionCard LoadCard(Dictionary<string, object> o)
    {
        if (o == null) return null;
        return new OncMissionCard
        {
            X = OncJson.GetFloat(o, "X", -1f),
            Y = OncJson.GetFloat(o, "Y", -1f),
            Width = OncJson.GetFloat(o, "Width"),
            Height = OncJson.GetFloat(o, "Height"),
            TitleColor = OncJson.GetString(o, "TitleColor"),
            Background = OncJson.GetString(o, "Background"),
        };
    }

    private static List<object> ToStrList(List<string> list)
    {
        var r = new List<object>();
        if (list == null) return r;
        for (int i = 0; i < list.Count; i++) r.Add(list[i]);
        return r;
    }

    private static List<string> ToList(List<object> arr)
    {
        var r = new List<string>();
        if (arr == null) return r;
        for (int i = 0; i < arr.Count; i++)
            if (arr[i] != null) r.Add(arr[i].ToString());
        return r;
    }

    private static OncNodeKind ParseKind(string s)
    {
        if (string.IsNullOrEmpty(s)) return OncNodeKind.Custom;
        // 别名：JSON 里 "Print" → Teleprinter（打字机打印）
        if (s.Equals("Print", StringComparison.OrdinalIgnoreCase)) return OncNodeKind.Teleprinter;
        return Enum.TryParse(s, true, out OncNodeKind k) ? k : OncNodeKind.Custom;
    }

    private static OncObjectiveAction ParseObjectiveAction(string s)
    {
        if (string.IsNullOrEmpty(s)) return OncObjectiveAction.Start;
        return Enum.TryParse(s, true, out OncObjectiveAction a) ? a : OncObjectiveAction.Start;
    }
}
