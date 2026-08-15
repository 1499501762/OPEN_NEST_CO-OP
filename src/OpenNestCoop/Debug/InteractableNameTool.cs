using Il2CppInterop.Runtime;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.Debug;

/// <summary>
/// 调试工具：准星对准可交互物品时，屏幕顶部显示其名字 + 完整路径 + 交互组件。
/// 按 F9 开关。用于定位"发射拉索绑定拉杆"等游戏内无提示名的对象（可交互物品的 GameObject 名）。
/// 默认关闭；仅在本地回环测试模式（LocalMode，--local host/join）自动开启，正常联机不显示。
/// </summary>
public class InteractableNameTool : MonoBehaviour
{
    private bool _show = false; // 默认关闭（仅本地调试 LocalMode 自动开启）
    private bool _userToggled;  // 用户 F9 手动切换后不再被本地模式自动覆盖
    private string _text = "";
    private bool _logOnce;

    public InteractableNameTool(System.IntPtr ptr) : base(ptr) { }

    public void Update()
    {
        try
        {
            // 仅本地调试（LocalMode）自动开启；正常联机（Steam 等）默认关闭
            bool local = false;
            try { local = CoopRuntime.Net != null && CoopRuntime.Net.LocalMode; } catch { }
            if (!local) _show = false;
            else if (!_userToggled) _show = true;

            // F9 手动切换（本地模式可关掉，正常联机可临时打开）
            try
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.F9)) { _userToggled = true; _show = !_show; }
            }
            catch { }

            if (!_show) { _text = ""; return; }

            if (!_logOnce)
            {
                _logOnce = true;
                CoopRuntime.LogSource?.LogInfo("[InteractableNameTool] running: crosshair shows interactable names");
            }
            var cam = Camera.main;
            if (cam == null) { _text = "(无相机)"; return; }
            var ray = cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
            if (!Physics.Raycast(ray, out var hit, 8f)) { _text = "(未命中)"; return; }
            var go = hit.collider != null ? hit.collider.gameObject : null;
            if (go == null) { _text = "(无碰撞体)"; return; }

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
            _text = $"[F9关闭] 交互名='{target.name}'\n路径: {PathOf(target.transform)}\n组件: {ComponentsOf(target)}\n命中: {go.name}";
        }
        catch (System.Exception ex)
        {
            _text = "工具异常: " + ex.Message;
        }
    }

    private void OnGUI()
    {
        if (!_show || string.IsNullOrEmpty(_text)) return;
        GUI.Label(new Rect(12, 12, Screen.width - 24, 90), _text);
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
