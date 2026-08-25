using System;
using UnityEngine;
using OpenNestCoop.Core;
using OpenNestCore.UI;
using OpenNestCore.Tasks;
#if !MELONLOADER
using TMPro;
#else
using TMPro = Il2CppTMPro;
#endif
namespace OpenNestCoop.GameSync;

/// <summary>
/// 自定义任务 HUD（任务场景内显示任务名/目标/简报，让自定义任务"可见"——原生效果的一环）。
/// 用 <see cref="UiKit"/> 创建 ScreenSpaceOverlay UI，跨场景存活（DontDestroyOnLoad），
/// 由 <see cref="OncMissionBridge"/> 驱动：Start → Show，Update → Refresh，Stop/完成/失败 → Hide。
/// </summary>
public static class OncMissionHud
{
    private static Canvas _canvas;
    private static GameObject _root;
    private static TextMeshProUGUI _title;
    private static TextMeshProUGUI _desc;
    private static TextMeshProUGUI[] _objRows = new TextMeshProUGUI[0];

    public static void Show(OncMission mission)
    {
        try
        {
            EnsureCanvas();
            _root.SetActive(true);
            string name = (mission != null && !string.IsNullOrEmpty(mission.DisplayName)) ? mission.DisplayName
                : (mission != null ? mission.Id : "");
            _title.text = "任务：" + name;
            _desc.text = mission != null ? mission.Description : "";
            int n = mission != null && mission.Objectives != null ? mission.Objectives.Count : 0;
            EnsureRows(n);
            for (int i = 0; i < n; i++)
            {
                var o = mission.Objectives[i];
                _objRows[i].text = (o != null && o.Title != null ? o.Title : "") + "  [未开始]";
            }
            CoopRuntime.LogSource?.LogInfo("[OncMissionHud] show");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[OncMissionHud] Show: {ex.Message}"); }
    }

    /// <summary>每帧刷新目标状态（Active/Completed + 进度）。</summary>
    public static void Refresh(OncMissionRuntime runtime)
    {
        try
        {
            if (_root == null || !_root.activeSelf || runtime == null) return;
            if (runtime.Objectives == null || _objRows.Length == 0) return;
            int i = 0;
            foreach (var kv in runtime.Objectives)
            {
                var o = kv.Value;
                if (o == null || i >= _objRows.Length) { i++; continue; }
                string st = o.Status.ToString();
                string prog = o.Target > 0 ? $" ({o.Progress}/{o.Target})" : "";
                _objRows[i].text = (o.Title ?? "") + "  [" + st + "]" + prog;
                i++;
            }
        }
        catch { }
    }

    public static void Hide()
    {
        try { if (_root != null) _root.SetActive(false); } catch { }
    }

    private static void EnsureCanvas()
    {
        if (_canvas != null) return;
        _canvas = UiKit.CreateCanvas("OncMissionHud", 32000, true);
        _root = new GameObject("Root");
        _root.transform.SetParent(_canvas.transform, false);
        var rt = _root.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        // 面板（左上角）：任务名 + 简报 + 目标列表
        UiKit.MakePanel(_root.transform, 24f, 24f, 500f, 200f, null, new Color(0f, 0f, 0f, 0.55f));
        _title = UiKit.MakeText(_root.transform, "", 40f, 32f, 460f, 34f, 22, Color.white, TextAlignmentOptions.Left);
        _desc = UiKit.MakeText(_root.transform, "", 40f, 70f, 460f, 34f, 14, new Color(0.85f, 0.85f, 0.85f, 1f), TextAlignmentOptions.Left);
        _root.SetActive(false);
    }

    private static void EnsureRows(int n)
    {
        if (n <= _objRows.Length) return;
        var old = _objRows;
        _objRows = new TextMeshProUGUI[n];
        Array.Copy(old, _objRows, old.Length);
        for (int i = old.Length; i < n; i++)
            _objRows[i] = UiKit.MakeText(_root.transform, "", 40f, 108f + i * 26f, 460f, 24f, 16, Color.white, TextAlignmentOptions.Left);
    }
}
