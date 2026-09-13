using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OpenNestModMenu.UI;

/// <summary>
/// ESC 菜单处理（T5）：菜单打开期间**阻止游戏自己的 ESC 暂停菜单弹出**，并让 **ESC 关闭我们的菜单**；
/// 关闭时把动过的东西**按原状态恢复**（"关闭清理"）。
///
/// 依据（游戏类型 dump，`tools/dump_Assembly-CSharp.txt`）：
/// - `EscapeMenuToggleUnityEvent`：游戏自己的 ESC 菜单开关 —— `IsOpen`、`ForceClose(bool invokeEvent)`、
///   以及 `toggleAction`（`InputActionReference`）/`subscribedAction`（`InputAction`）——ESC 就是这两个 action 触发的；
/// - 它还有一个 blocker 集合（`RegisterBlocker/UnregisterBlocker` + `EscapeMenuOpenBlocker` 组件）。
///
/// ⚠️ 这里**不往场景里塞 `EscapeMenuOpenBlocker` 组件**（要依赖它的 `escapeMenuTag`/`GetEscapeMenu()` 能解析到菜单，
/// 失败就是静默无效）；改为**直接禁用触发 action**：ESC 根本不会打开游戏菜单，零闪烁。
/// 若某个实例的 action 拿不到（版本差异），退化为**每帧强制关闭**（`ForceClose(false)`）。
///
/// 两端都能直接写游戏类型：MelonLoader 侧由 `PlatformUsings.cs` 的 `global using Il2Cpp;` 适配（见该文件说明）。
/// </summary>
internal static class UiEscapeGuard
{
    private static readonly List<MenuRef> _menus = new();
    private static bool _on;

    private sealed class MenuRef
    {
        public EscapeMenuToggleUnityEvent Menu;
        public InputAction Toggle;
        public bool ToggleWasEnabled;
        public InputAction Subscribed;
        public bool SubscribedWasEnabled;
    }

    /// <summary>菜单打开/关闭时调用（可重复调用：新出现的 ESC 菜单会被增量接管）。</summary>
    public static void Block(bool on)
    {
        _on = on;
        if (on) Apply();
        else Restore();
    }

    /// <summary>菜单打开期间：把游戏 ESC 菜单的触发 action 关掉（并关掉已经打开的菜单）。</summary>
    public static void Apply()
    {
        if (!_on) return;
        try
        {
            EscapeMenuToggleUnityEvent[] all = null;
            try { all = UnityEngine.Object.FindObjectsOfType<EscapeMenuToggleUnityEvent>(true); } catch { }
            if (all == null) return;

            int newlyBlocked = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var menu = all[i];
                if (menu == null) continue;

                var r = Find(menu);
                if (r == null)
                {
                    r = new MenuRef { Menu = menu };
                    _menus.Add(r);
                }

                // ① 记录并禁用触发 action（ESC 不会打开游戏菜单）
                try
                {
                    var ref_ = menu.toggleAction;
                    var act = ref_ != null ? ref_.action : null;
                    if (act != null && r.Toggle == null)
                    {
                        r.Toggle = act;
                        r.ToggleWasEnabled = act.enabled;
                        if (act.enabled) { act.Disable(); newlyBlocked++; }
                    }
                }
                catch { }

                try
                {
                    var act = menu.subscribedAction;
                    if (act != null && r.Subscribed == null)
                    {
                        r.Subscribed = act;
                        r.SubscribedWasEnabled = act.enabled;
                        if (act.enabled) { act.Disable(); newlyBlocked++; }
                    }
                }
                catch { }

                // ② 已经打开的游戏菜单：关掉它（不要叠在我们的菜单后面）
                try { if (menu.IsOpen) menu.ForceClose(false); } catch { }
            }

            if (newlyBlocked > 0)
                CoopLog.Info("modmenu.ui", () => $"escape guard: disabled {newlyBlocked} game escape action(s) across {_menus.Count} menu instance(s)");
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.ui", () => "escape guard apply failed: " + ex.Message);
        }
    }

    /// <summary>
    /// TEMP-VERIFY：供自测开关（<c>-onnmm-selftest-t56</c>）读取状态的一行摘要 ——
    /// 意图是"游戏 ESC 菜单是否真的被拦住"能被日志证明（包含实例数 / 是否打开 / 我们接管了几条 action）。
    /// </summary>
    internal static string SelfTestState()
    {
        int inst = 0, open = 0, act = 0, enabledLeft = 0;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<EscapeMenuToggleUnityEvent>(true);
            if (all != null)
                for (int i = 0; i < all.Length; i++)
                {
                    inst++;
                    try { if (all[i].IsOpen) open++; } catch { }
                    try
                    {
                        var a = all[i].toggleAction != null ? all[i].toggleAction.action : null;
                        if (a != null) { act++; if (a.enabled) enabledLeft++; }
                    }
                    catch { }
                }
        }
        catch { }
        return $"blocked={_on} trackedMenus={_menus.Count} instances={inst} gameEscapeOpen={open} toggleActions={act} stillEnabled={enabledLeft}";
    }

    /// <summary>菜单关闭 / 关停时：恢复被我们禁用的 action（拿不到的就不动）。</summary>
    public static void Restore()
    {
        int n = 0;
        for (int i = 0; i < _menus.Count; i++)
        {
            var r = _menus[i];
            if (r == null) continue;
            try { if (r.Toggle != null && r.ToggleWasEnabled && !r.Toggle.enabled) { r.Toggle.Enable(); n++; } } catch { }
            try { if (r.Subscribed != null && r.SubscribedWasEnabled && !r.Subscribed.enabled) { r.Subscribed.Enable(); n++; } } catch { }
        }
        _menus.Clear();
        if (n > 0) CoopLog.Debug("modmenu.ui", () => $"escape guard: restored {n} game escape action(s)");
    }

    /// <summary>
    /// 兜底兜底（每帧，菜单打开时）：万一某个实例的 action 拿不到，就把已经打开的 ESC 菜单强制关掉。
    /// 只在真的开着时才调 `ForceClose`，平时零开销。
    /// </summary>
    public static void TickFallback()
    {
        if (!_on) return;
        try
        {
            for (int i = 0; i < _menus.Count; i++)
            {
                var r = _menus[i];
                var menu = r?.Menu;
                if (menu == null) continue;
                if (r.Toggle != null && r.Subscribed != null) continue;   // action 已接管 → 不需要兜底
                try { if (menu.IsOpen) menu.ForceClose(false); } catch { }
            }
        }
        catch { }
    }

    private static MenuRef Find(EscapeMenuToggleUnityEvent menu)
    {
        for (int i = 0; i < _menus.Count; i++)
            if (_menus[i]?.Menu == menu) return _menus[i];
        return null;
    }
}
