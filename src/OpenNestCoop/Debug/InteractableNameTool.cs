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
            // 仅本地调试（LocalMode）自动开启；正常联机（Steam 等）默认关闭。
            // ⚠️ 2026-08-16 修复：原 `if (!local) _show = false` 每帧强制关闭，Steam 联机时
            // 用户按 F9 切到 true 后下一帧又被覆盖 → F9 永远没反应。改为只在用户未手动切换前生效，
            // 用户 F9 切换（_userToggled）后保持手动状态，不再被每帧覆盖。
            if (!_userToggled)
            {
                bool local = false;
                try { local = CoopRuntime.Net != null && CoopRuntime.Net.LocalMode; } catch { }
                _show = local;
            }

            // F9 手动切换（本地模式可关掉，正常联机可临时打开）
            // ⚠️ 2026-08-15：游戏用新版 Input System，旧 UnityEngine.Input.GetKeyDown 被禁用 → F9 无响应。
            // 改用 Input System Keyboard.current.f9Key.wasPressedThisFrame。
            // ⚠️ 2026-08-22：新增 F10 —— 把当前 F9 显示的交互信息（名称/路径/组件）复制到系统剪贴板，
            // 方便把实体名贴到文档/聊天（此前只能截图或手抄）。
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null)
                {
                    if (kb.f9Key.wasPressedThisFrame) { _userToggled = true; _show = !_show; }
                    if (kb.f10Key.wasPressedThisFrame && !string.IsNullOrEmpty(_text))
                    {
                        try { GUIUtility.systemCopyBuffer = _text; } catch { }
                        CoopRuntime.LogSource?.LogInfo($"[InteractableNameTool] F10 copied to clipboard:\n{_text}");
                    }
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
            _text = $"[F9关闭|F10复制] 交互名='{target.name}'\n路径: {PathOf(target.transform)}\n组件: {ComponentsOf(target)}\n命中: {go.name}";
        }
        catch (System.Exception ex)
        {
            _text = "工具异常: " + ex.Message;
        }
    }

    private void OnGUI()
    {
        if (!_show || string.IsNullOrEmpty(_text)) return;
        // ⚠️ 2026-08-22：原 (12,12) 顶部被联机大厅"显示/隐藏"按钮挡住一部分 → 下移到 y=130 避开。
        // 加高（4 行文本 + 长路径）避免截断。
        GUI.Label(new Rect(12, 130, Screen.width - 24, 150), _text);
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
