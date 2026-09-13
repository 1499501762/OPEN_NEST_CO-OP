using System;
using System.Reflection;
using UnityEngine;
using OpenNestUIKit.UI;
#if MELONLOADER
using Localisation = Il2CppLocalisation;
using TMPro = Il2CppTMPro;
#endif

namespace OpenNestUIKit.Native;

/// <summary>
/// Iron Nest 原生 UI 桥接实现（游戏侧，能引用 Assembly-CSharp）。
/// 把游戏原生 UI 子系统（LocalisationManager / UINotificationManager / EscapeMenuToggleUnityEvent /
/// MissionManager / VirtualCursor）实现为 <see cref="INativeUiService"/>，注册到 <see cref="NativeUi"/>。
///
/// 与 <c>OpenNestCoop.UI.IronNestNativeUi</c> 同法（那份已在双端实测），差别：
/// - **不另挂 MonoBehaviour**：轮询合并进 <see cref="Tick(float)"/>，由本模组已有的
///   <see cref="Core.UiKitBehaviour"/> 每帧驱动（少注入一个 IL2CPP 类型 = 少一分风险）；
/// - 主菜单加载完成时顺带做两件事：**捕获原生 UI 素材**（<see cref="Theme.UiSkin.CaptureAll"/>）与
///   **原生菜单注入**（<see cref="NativeMenuInjector.OnMainMenuLoaded"/>）。
///
/// ⚠️ MLL 端游戏类型带 `Il2Cpp` 前缀（`Il2CppLocalisation`），故用 `using` 别名适配（PlatformUsings 已全局处理多数类型）。
/// </summary>
public sealed class UiGameBridge : INativeUiService
{
    public static UiGameBridge Instance;

    // ---- 事件 ----
    public event Action LanguageChanged;
    public event Action MainMenuLoaded;
    public event Action MainMenuUnloaded;

    // ---- 缓存（原生对象查找，带冷却刷新） ----
    private EscapeMenuToggleUnityEvent _esc;
    private VirtualCursor _cursor;
    private VirtualCursorInputModule _cursorModule;
    private float _lastFind;
    private const float FindCooldown = 1f;

    // ---- ESC blocker（SetEscapeMenuBlocked 用的占位组件） ----
    private GameObject _blockerGo;
    private EscapeMenuOpenBlocker _blocker;

    // ---- 轮询状态 ----
    private string _lastLang;
    private bool _lastMainMenu;
    private float _pollT;

    /// <summary>创建实例 + 注册到 NativeUi（重复调用安全）。</summary>
    public static void Hook()
    {
        if (Instance != null) return;
        try
        {
            Instance = new UiGameBridge();
            NativeUi.Register(Instance);
            CoopLog.Info("uikit.bridge", () => "UiGameBridge hooked (registered to NativeUi)");
        }
        catch (Exception ex)
        {
            CoopLog.Error("uikit.bridge", () => "UiGameBridge.Hook failed: " + ex);
            Instance = null;
        }
    }

    /// <summary>卸载（重复调用安全）。</summary>
    public static void Unhook()
    {
        try { NativeUi.Clear(); } catch { }
        try { Instance?.SetEscapeMenuBlocked(false); } catch { }
        Instance = null;
    }

    /// <summary>每帧驱动（由 <see cref="Core.UiKitBehaviour.Update"/> 调用）：节流轮询语言/主菜单阶段。</summary>
    public static void Tick(float dt)
    {
        var inst = Instance;
        if (inst == null) return;
        inst._pollT += dt;
        if (inst._pollT < 0.25f) return;
        inst._pollT = 0f;
        inst.Poll();
    }

    /// <summary>轮询：语言变化 / 主菜单加载卸载 → 事件（不订阅原生事件，规避 IL2CPP 委托风险）。</summary>
    public void Poll()
    {
        try
        {
            var lang = CurrentLanguage;
            if (lang != _lastLang)
            {
                bool changed = _lastLang != null && lang != null;
                _lastLang = lang;
                if (changed) { try { LanguageChanged?.Invoke(); } catch { } }
            }

            var main = IsMainMenu;
            if (main != _lastMainMenu)
            {
                bool was = _lastMainMenu;
                _lastMainMenu = main;
                if (main && !was)
                {
                    CoopLog.Info("uikit.bridge", () => "主菜单加载完成");
                    try { MainMenuLoaded?.Invoke(); } catch { }
                    try { Theme.UiSkin.CaptureAll(); } catch { }
                    try { NativeMenuInjector.OnMainMenuLoaded(); } catch { }
                }
                else if (!main && was)
                {
                    CoopLog.Info("uikit.bridge", () => "主菜单卸载");
                    try { MainMenuUnloaded?.Invoke(); } catch { }
                }
            }
        }
        catch { }
    }

    // ---------------- 本地化 ----------------

    public bool IsAvailable
    {
        get
        {
            try
            {
                var lm = Localisation.LocalisationManager.Instance;
                return lm != null;
            }
            catch { return false; }
        }
    }

    public string CurrentLanguage
    {
        get
        {
            try
            {
                var lm = Localisation.LocalisationManager.Instance;
                return lm != null ? lm.CurrentLanguage : null;
            }
            catch { return null; }
        }
    }

    public bool TryLocalise(string key, out string text)
    {
        try
        {
            var lm = Localisation.LocalisationManager.Instance;
            if (lm != null && lm.IsReady)
            {
                var s = lm.Get(key);
                if (!string.IsNullOrEmpty(s)) { text = s; return true; }
            }
        }
        catch { }
        text = null;
        return false;
    }

    public TMPro.TMP_FontAsset GetLocalisedFont(TMPro.TMP_FontAsset original)
    {
        try
        {
#if !MELONLOADER
            // ⚠️ MLL 下 LocalisationManager.GetFont interop 方法缺失（MissingMethodException 在
            //    IL2CPP trampoline 抛出，managed catch 捕获不到）→ 编译期排除，MLL 直接返回原字体
            //    （UiKit.EnsureFont 会走"运行时扫描 TMP_FontAsset"兜底，中文仍可显示）。
            var lm = Localisation.LocalisationManager.Instance;
            if (lm != null)
            {
                var f = lm.GetFont(original);
                if (f != null && f.name != (original != null ? original.name : "")) return f;
            }
#endif
        }
        catch (Exception ex) { CoopLog.Warn("uikit.font", () => "GetLocalisedFont: " + ex.Message); }
        return original;
    }

    // ---------------- 通知 toast ----------------

    public void ShowToast(string title, string description, float lifetime, Color? borderColor)
    {
        try
        {
            var mgr = UINotificationManager.Instance;
            if (mgr == null) return;
            Il2CppSystem.Nullable<UnityEngine.Color> border;
            if (borderColor.HasValue) border = new Il2CppSystem.Nullable<UnityEngine.Color>(borderColor.Value);
            else border = new Il2CppSystem.Nullable<UnityEngine.Color>();
            UINotificationManager.ShowNotification(title ?? "", description ?? "", lifetime, border);
        }
        catch (Exception ex) { CoopLog.Warn("uikit.toast", () => "ShowToast: " + ex.Message); }
    }

    // ---------------- ESC 菜单 ----------------

    public bool IsEscapeMenuOpen
    {
        get { try { var e = FindEscapeMenu(); return e != null && e.IsOpen; } catch { return false; } }
    }

    public bool IsEscapeMenuBlocked
    {
        get { try { var e = FindEscapeMenu(); return e != null && e.IsBlocked; } catch { return false; } }
    }

    public void SetEscapeMenuBlocked(bool blocked)
    {
        try
        {
            var e = FindEscapeMenu();
            if (e == null) return;
            if (blocked && _blocker == null)
            {
                _blockerGo = new GameObject("OpenNestUIKit_EscapeBlocker");
                UnityEngine.Object.DontDestroyOnLoad(_blockerGo);
                _blocker = _blockerGo.AddComponent<EscapeMenuOpenBlocker>();
                _blocker.blockerLabel = "Open Nest UIKit";
                try { _blocker.GetEscapeMenu(); } catch { }
                try { _blocker.Register(); } catch { }   // OnEnable 已注册，显式再调确保 cachedEscapeMenu 生效
                CoopLog.Debug("uikit.esc", () => "ESC 菜单已阻止");
            }
            else if (!blocked && _blocker != null)
            {
                try { _blocker.Unregister(); } catch { }
                UnityEngine.Object.Destroy(_blockerGo);
                _blockerGo = null;
                _blocker = null;
                CoopLog.Debug("uikit.esc", () => "ESC 菜单已放行");
            }
        }
        catch (Exception ex) { CoopLog.Warn("uikit.esc", () => "SetEscapeMenuBlocked: " + ex.Message); }
    }

    public void ForceCloseEscapeMenu()
    {
        try { var e = FindEscapeMenu(); if (e != null) e.ForceClose(false); }
        catch (Exception ex) { CoopLog.Warn("uikit.esc", () => "ForceCloseEscapeMenu: " + ex.Message); }
    }

    // ---------------- 主菜单状态 ----------------

    public bool IsMainMenu
    {
        get
        {
            try
            {
                var mm = MissionManager.Instance;
                if (mm == null) return false;
                // GamePhase: MainMenu=0 / BrowsingMap=1 / MissionActive=2
                return (int)mm.CurrentPhase == 0;
            }
            catch { return false; }
        }
    }

    // ---------------- 光标 ----------------

    public Vector2? VirtualCursorPosition
    {
        get
        {
            try
            {
                var c = FindCursor();
                return c != null ? (Vector2?)c.ScreenPosition : null;
            }
            catch { return null; }
        }
    }

    public bool IsCursorOverUi
    {
        get
        {
            try
            {
                var m = FindCursorModule();
                if (m == null) return false;
                // 私有 backing 字段（IL2CPP interop 下用反射读，与 TeleprinterSync 读 _revealMask 同法）
                var fi = m.GetType().GetField("_isOverInteractableUI",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                return fi != null && fi.GetValue(m) is bool b && b;
            }
            catch { return false; }
        }
    }

    // ---------------- 查找（带冷却缓存） ----------------

    private EscapeMenuToggleUnityEvent FindEscapeMenu()
    {
        if (_esc != null) return _esc;
        if (Time.unscaledTime - _lastFind < FindCooldown) return null;
        _lastFind = Time.unscaledTime;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<EscapeMenuToggleUnityEvent>(true);
            _esc = (all != null && all.Length > 0) ? all[0] : null;
        }
        catch { _esc = null; }
        return _esc;
    }

    private VirtualCursor FindCursor()
    {
        if (_cursor != null) return _cursor;
        if (Time.unscaledTime - _lastFind < FindCooldown) return null;
        _lastFind = Time.unscaledTime;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<VirtualCursor>(true);
            _cursor = (all != null && all.Length > 0) ? all[0] : null;
        }
        catch { _cursor = null; }
        return _cursor;
    }

    private VirtualCursorInputModule FindCursorModule()
    {
        if (_cursorModule != null) return _cursorModule;
        if (Time.unscaledTime - _lastFind < FindCooldown) return null;
        _lastFind = Time.unscaledTime;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<VirtualCursorInputModule>(true);
            _cursorModule = (all != null && all.Length > 0) ? all[0] : null;
        }
        catch { _cursorModule = null; }
        return _cursorModule;
    }
}
