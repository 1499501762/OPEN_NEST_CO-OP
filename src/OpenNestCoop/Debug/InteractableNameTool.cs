using Il2CppInterop.Runtime;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.Debug;

/// <summary>
/// 调试工具：准星对准可交互物品时，屏幕顶部显示其名字 + 完整路径 + 交互组件。
/// 显示由 <see cref="DiagCycleController"/> 统一控制（按 F9 在 帧性能/网络/交互工具/不显示 间循环）。
/// 用于定位“发射拉索绑定拉杆”等游戏内无提示名的对象（可交互物品的 GameObject 名）。
/// F10 复制当前显示信息到系统剪贴板（保留）。
/// </summary>
public class InteractableNameTool : MonoBehaviour
{
    /// <summary>当前是否显示（由 DiagCycleController F9 循环控制，非按键自管理）。</summary>
    private bool _show;
    private string _text = "";
    private bool _logOnce;
    /// <summary>单例（供 DiagCycleController 切换）。</summary>
    public static InteractableNameTool Instance { get; private set; }

    public InteractableNameTool(System.IntPtr ptr) : base(ptr) { }

    public void Awake() { Instance = this; }

    /// <summary>由 DiagCycleController（F9 循环）控制显示。</summary>
    public void SetVisible(bool v)
    {
        _show = v;
        if (v) _logOnce = false; // 重新显示时重打 "running" 日志
    }

    public void Update()
    {
        try
        {
            // F10 复制剪贴板（保留；显示切换由 DiagCycleController 统一管理）
            // ⚠️ 2026-08-22：把当前显示的交互信息（名称/路径/组件）复制到系统剪贴板，
            // 方便把实体名贴到文档/聊天（此前只能截图或手抄）。
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && kb.f10Key.wasPressedThisFrame && !string.IsNullOrEmpty(_text))
                {
                    try { GUIUtility.systemCopyBuffer = _text; } catch { }
                    CoopRuntime.LogSource?.LogInfo($"[InteractableNameTool] F10 copied to clipboard:\n{_text}");
                }
            }
            catch { }

            if (!_show) { _text = ""; return; }

            if (!_logOnce)
            {
                _logOnce = true;
                CoopRuntime.LogSource?.LogInfo("[InteractableNameTool] running: crosshair shows interactable names");
            }
            var cam = Camera.main;
            if (cam == null) { _text = Loc("ToolNoCamera"); return; }
            var ray = cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
            if (!Physics.Raycast(ray, out var hit, 8f)) { _text = Loc("ToolNoHit"); return; }
            var go = hit.collider != null ? hit.collider.gameObject : null;
            if (go == null) { _text = Loc("ToolNoCollider"); return; }

            // 沿父链找交互组件：LookAtTarget（按钮/拉杆）/ Interactable（点击底层）/ Dial/Slider
            GameObject target = go;
            var lat = go.GetComponentInParent<LookAtTarget>();
            if (lat != null) target = lat.gameObject;
            else
            {
                var ia = go.GetComponentInParent<Interactable>();
                if (ia != null) target = ia.gameObject;
                else
                {
                    var da = go.GetComponentInParent<DialInteractable>();
                    if (da != null) target = da.gameObject;
                    else
                    {
                        var sa = go.GetComponentInParent<LinearSliderInteractable>();
                        if (sa != null) target = sa.gameObject;
                    }
                }
            }
            _text = Loc("ToolHeader") + $"\n{Loc("ToolName")}='{target.name}'\n{Loc("ToolPath")}: {PathOf(target.transform)}\n{Loc("ToolComponents")}: {ComponentsOf(target)}\n{Loc("ToolHit")}: {go.name}" + SyncStatus(lat);
        }
        catch (System.Exception ex)
        {
            _text = OpenNestCoop.Core.Loc.LocFile.Get("ToolError", ex.Message);
        }
    }

    /// <summary>语言键取文案（F10 工具文案已全部上语言文件）。</summary>
    private static string Loc(string key) => OpenNestCoop.Core.Loc.LocFile.Get(key);

    /// <summary>附一行“同步状态”（帮助判断这个交互实体到底有没有被同步：
    /// 点击复现（ButtonClickSync）是否跟踪——**已含配置开关判定**，如 Calculate Universal Button 默认关）。</summary>
    private static string SyncStatus(LookAtTarget lat)
    {
        try
        {
            if (lat == null) return "\n" + Loc("ToolSyncNA");
            bool tracked = OpenNestCoop.GameSync.ButtonClickSync.IsTracked(lat);
            return "\n" + Loc(tracked ? "ToolSyncOn" : "ToolSyncOff") + "  [CoopConfig " + OpenNestCoop.Core.CoopConfig.Summary() + "]";
        }
        catch (System.Exception ex) { return "\n" + OpenNestCoop.Core.Loc.LocFile.Get("ToolSyncUnknown") + " (" + ex.Message + ")"; }
    }

    private void OnGUI()
    {
        if (!_show || string.IsNullOrEmpty(_text)) return;
        // ⚠️ 2026-08-25：统一到右上角（与帧/网络诊断共用位置——F9 循环同时只显示一个）。
        // 加高（4 行文本 + 长路径 + 同步状态行）避免截断。
        GUI.Label(new Rect(Screen.width - 460, 12, 448, 200), _text);
    }

    private static string PathOf(Transform t)
    {
        if (t == null) return "";
        string path = t.name ?? "";
        var p = t.parent;
        int depth = 0;
        while (p != null && depth < 12) { path = (p.name ?? "") + "/" + path; p = p.parent; depth++; }
        return path;
    }

    private static string ComponentsOf(GameObject go)
    {
        if (go == null) return "";
        var cs = go.GetComponents<Component>();
        string s = "";
        for (int i = 0; i < cs.Length && i < 8; i++)
        {
            string tn = "?";
            try { tn = cs[i].GetIl2CppType().FullName; }
            catch { try { tn = cs[i].GetType().Name; } catch { } }
            s += (s.Length > 0 ? "," : "") + tn;
        }
        return s;
    }
}
