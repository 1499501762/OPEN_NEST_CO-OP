using System;
using System.Reflection;
using UnityEngine;
using OpenNestCore.UI;
#if MELONLOADER
using Localisation = Il2CppLocalisation;
using TMPro = Il2CppTMPro;
#endif

namespace OpenNestCoop.UI;

/// <summary>
/// Iron Nest 原生 UI 桥接实现（游戏侧，能引用 Assembly-CSharp）。
/// 把游戏原生 UI 子系统（LocalisationManager / UINotificationManager / EscapeMenuToggleUnityEvent /
/// MissionManager / VirtualCursor）实现为 <see cref="INativeUiService"/>，注册到 <see cref="NativeUi"/>，
/// 供 OpenNestCoop 及第三方模组以平台无关方式调用。
///
/// 生命周期：CoopRuntime.Startup 调 <see cref="Hook"/> 创建实例 + 注册 + 挂载轮询 Behaviour
/// （语言/主菜单阶段变化检测，避免依赖原生事件订阅的稳定性）。Shutdown 调 <see cref="Unhook"/>。
/// </summary>
public sealed class IronNestNativeUi : INativeUiService
{
    public static IronNestNativeUi Instance;

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

    /// <summary>创建实例 + 注册到 NativeUi + 挂载轮询 Behaviour。重复调用安全。</summary>
    public static void Hook()
    {
        if (Instance != null) return;
        try
        {
            Instance = new IronNestNativeUi();
            NativeUi.Register(Instance);
            // 轮询 Behaviour（语言/主菜单阶段变化检测）
            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<NativeUiPoll>();
            var go = new GameObject("OpenNest_NativeUiPoll");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<NativeUiPoll>();
            OpenNestCore.Logging.CoopLog.Info("NativeUi.hook", () => "IronNestNativeUi hooked (registered to NativeUi)");

            // B2 原生路线运行时验证：尝试 Resources.Load 原生 sprite（开发者同法），确认双端无 MissingMethodException。
            // 日志可见 `Resources.Load OK: <name>`（成功）或 `not found / failed`（需排查路径或剥离）。
            TryVerifyNativeResources();
        }
        catch (Exception ex)
        {
            OpenNestCore.Logging.CoopLog.Error("NativeUi.hook", () => "IronNestNativeUi Hook failed: " + ex);
            Instance = null;
        }
    }

    /// <summary>探测 resources.assets 里可加载的原生资源（B2 运行时验证）：prefab 按名 / sprite 同步 / sprite 异步。
    /// 日志 `NativeUi.verify`。实测（2026-08-23 G 端）：Resources.Load<Sprite> 无异常但候选名全 null →
    /// 名字/路径与 m_Name 不同或同步 Load 被裁；用本探测区分 prefab/同步/异步三条路径。</summary>
    private static void TryVerifyNativeResources()
    {
        try
        {
            // 1) 原生 prefab 按名加载（Resources.Load<GameObject>）
            string[] prefabs = { "AchievementDisplay", "Panel", "Title", "DebugUICanvas" };
            foreach (var n in prefabs)
            {
                var go = UiSpriteBank.PrefabFromResources(n);
                if (go != null)
                {
                    int spriteCount = UiSpriteBank.CollectSprites(go).Count;
                    OpenNestCore.Logging.CoopLog.Info("NativeUi.verify", () => $"Resources.Load<GameObject> OK: '{n}' (images={spriteCount})");
                }
            }

            // 2) 原生 sprite 按名同步加载（Resources.Load<Sprite>）
            string[] sprites = { "UIElement8px", "UIFoldoutOpened", "UIFoldoutClosed", "UICheckMark", "IronRoadMap" };
            int spriteOk = 0;
            foreach (var n in sprites)
            {
                var s = UiSpriteBank.FromResources(n);
                if (s != null) { spriteOk++; OpenNestCore.Logging.CoopLog.Info("NativeUi.verify", () => $"Resources.Load<Sprite> OK: '{n}' ({s.rect.width}x{s.rect.height})"); }
            }

            // 3) 异步变体（游戏自己用的 Resources.LoadAsync）——若同步被裁而异步在，此路可用
            try
            {
                var req = Resources.LoadAsync<Sprite>("UIElement8px");
                if (req != null)
                    OpenNestCore.Logging.CoopLog.Info("NativeUi.verify", () => $"Resources.LoadAsync OK: request!=null isDone={req.isDone}");
                else
                    OpenNestCore.Logging.CoopLog.Warn("NativeUi.verify", () => "Resources.LoadAsync: request null");
            }
            catch (System.Exception ex)
            {
                OpenNestCore.Logging.CoopLog.Warn("NativeUi.verify", () => "Resources.LoadAsync failed: " + ex.Message);
            }

            if (spriteOk == 0)
                OpenNestCore.Logging.CoopLog.Warn("NativeUi.verify", () => "候选原生 sprite 全 null（路径/名字与 m_Name 不同，或同步 Load 被裁）");

            // ---- 细查（2026-08-23）：区分"子目录路径≠m_Name"还是"同步 sprite Load 被剥离" ----
            // A. LoadAll<Sprite>("") 枚举 Resources 根目录运行时真实可加载的精灵（决定性）
            try
            {
                var all = Resources.LoadAll<Sprite>("");
                OpenNestCore.Logging.CoopLog.Info("NativeUi.verify", () => $"probe LoadAll<Sprite>('') count={(all != null ? all.Length : -1)}");
                if (all != null && all.Length > 0)
                {
                    var names = new System.Collections.Generic.List<string>();
                    for (int i = 0; i < all.Length; i++) names.Add(all[i].name);
                    OpenNestCore.Logging.CoopLog.Info("NativeUi.verify", () => $"probe LoadAll root names: {string.Join("|", names)}");
                }
            }
            catch (System.Exception ex) { OpenNestCore.Logging.CoopLog.Warn("NativeUi.verify", () => $"probe LoadAll('') EX: {ex.Message}"); }

            // D. 非泛型/类型重载对照（裁决：是否仅"泛型 Load<T> 实例化"失效）
            try
            {
                var d1 = Resources.Load("IronRoadMap");
                OpenNestCore.Logging.CoopLog.Info("NativeUi.verify", () => $"probe Load('IronRoadMap') [non-generic] -> {(d1 != null ? "OK " + d1.name : "null")}");
            }
            catch (System.Exception ex) { OpenNestCore.Logging.CoopLog.Warn("NativeUi.verify", () => $"probe Load('IronRoadMap') EX: {ex.Message}"); }
            try
            {
                var d3 = Resources.Load("AchievementDisplay");
                OpenNestCore.Logging.CoopLog.Info("NativeUi.verify", () => $"probe Load('AchievementDisplay') [non-generic] -> {(d3 != null ? "OK " + d3.name : "null")}");
            }
            catch (System.Exception ex) { OpenNestCore.Logging.CoopLog.Warn("NativeUi.verify", () => $"probe Load('AchievementDisplay') EX: {ex.Message}"); }

            // B. 常见子目录 LoadAll 盲试（若有命中 → 路径≠m_Name 成立）
            string[] dirs = { "UI", "Sprites", "Images", "Icons", "UI/Sprites", "Sprites/UI" };
            foreach (var d0 in dirs)
            {
                try
                {
                    var arr = Resources.LoadAll<Sprite>(d0);
                    OpenNestCore.Logging.CoopLog.Info("NativeUi.verify", () => $"probe LoadAll<Sprite>('{d0}') count={(arr != null ? arr.Length : -1)}");
                    if (arr != null && arr.Length > 0)
                        for (int i = 0; i < arr.Length && i < 10; i++)
                            OpenNestCore.Logging.CoopLog.Info("NativeUi.verify", () => $"    [{d0}] '{arr[i].name}'");
                }
                catch (System.Exception ex) { OpenNestCore.Logging.CoopLog.Warn("NativeUi.verify", () => $"probe LoadAll('{d0}') EX: {ex.Message}"); }
            }

            // C. UIElement8px 路径变体 Load（若某变体 OK → 路径≠m_Name；若全 null → 同步 sprite Load 被剥离）
            string[] variants = { "UIElement8px", "UI/UIElement8px", "Sprites/UIElement8px", "Images/UIElement8px", "UIElement8px.png" };
            foreach (var v in variants)
            {
                try
                {
                    var s = Resources.Load<Sprite>(v);
                    OpenNestCore.Logging.CoopLog.Info("NativeUi.verify", () => $"probe Load<Sprite>('{v}') -> {(s != null ? "OK " + s.name : "null")}");
                }
                catch (System.Exception ex) { OpenNestCore.Logging.CoopLog.Warn("NativeUi.verify", () => $"probe Load<Sprite>('{v}') EX: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            OpenNestCore.Logging.CoopLog.Warn("NativeUi.verify", () => "Resources.Load failed: " + ex);
        }
    }

    /// <summary>卸载：清除注册 + 销毁轮询 Behaviour。重复调用安全。</summary>
    public static void Unhook()
    {
        try { NativeUi.Clear(); } catch { }
        Instance = null;
    }

    // ---------------- 本地化 ----------------

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
                // 优先原生 TryGet（无异常）；Get 兜底（部分 key 缺失时 TryGet 可能返回 false）
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
            //    IL2CPP trampoline 抛出，managed catch 捕获不到）→ 编译期排除，MLL 直接返回原字体。
            var lm = Localisation.LocalisationManager.Instance;
            if (lm != null)
            {
                var f = lm.GetFont(original);
                if (f != null && f.name != (original != null ? original.name : "")) return f;
            }
#endif
        }
        catch (Exception ex) { OpenNestCore.Logging.CoopLog.Warn("NativeUi.font", () => "GetLocalisedFont: " + ex.Message); }
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
        catch (Exception ex) { OpenNestCore.Logging.CoopLog.Warn("NativeUi.toast", () => "ShowToast: " + ex.Message); }
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
                _blockerGo = new GameObject("OpenNest_EscapeBlocker");
                UnityEngine.Object.DontDestroyOnLoad(_blockerGo);
                _blocker = _blockerGo.AddComponent<EscapeMenuOpenBlocker>();
                _blocker.blockerLabel = "Open Nest Co-op UI";
                try { _blocker.GetEscapeMenu(); } catch { }
                try { _blocker.Register(); } catch { } // OnEnable 已注册，显式再调确保 cachedEscapeMenu 生效
            }
            else if (!blocked && _blocker != null)
            {
                try { _blocker.Unregister(); } catch { }
                UnityEngine.Object.Destroy(_blockerGo);
                _blockerGo = null;
                _blocker = null;
            }
        }
        catch (Exception ex) { OpenNestCore.Logging.CoopLog.Warn("NativeUi.esc", () => "SetEscapeMenuBlocked: " + ex.Message); }
    }

    public void ForceCloseEscapeMenu()
    {
        try { var e = FindEscapeMenu(); if (e != null) e.ForceClose(false); }
        catch (Exception ex) { OpenNestCore.Logging.CoopLog.Warn("NativeUi.esc", () => "ForceCloseEscapeMenu: " + ex.Message); }
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
                // GamePhase: MainMenu=0 / BrowsingMap=1 / MissionActive=2（可安全 cast）
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

    // ---------------- 轮询（语言/主菜单阶段变化 → 事件） ----------------

    public void Poll()
    {
        try
        {
            // 语言变化
            var lang = CurrentLanguage;
            if (lang != _lastLang)
            {
                var changed = _lastLang != null && lang != null;
                _lastLang = lang;
                if (changed) { try { LanguageChanged?.Invoke(); } catch { } }
            }
            // 主菜单阶段变化（false→true = 主菜单加载完成；true→false = 卸载）
            var main = IsMainMenu;
            if (main != _lastMainMenu)
            {
                var was = _lastMainMenu;
                _lastMainMenu = main;
                if (main && !was)
                {
                    try { MainMenuLoaded?.Invoke(); } catch { }
                }
                else if (!main && was) { try { MainMenuUnloaded?.Invoke(); } catch { } }
            }
            // ESC 菜单联机入口诊断（MainMenuEntry 内部节流 2s）
            try { MainMenuEntry.Poll(); } catch { }
        }
        catch { }
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

/// <summary>轮询 Behaviour：每帧检测语言/主菜单阶段变化，转发给 IronNestNativeUi 的事件。</summary>
public sealed class NativeUiPoll : MonoBehaviour
{
    public NativeUiPoll(System.IntPtr ptr) : base(ptr) { }

    public void Update()
    {
        var s = IronNestNativeUi.Instance;
        if (s == null) return;
        try { s.Poll(); } catch { }
    }
}
