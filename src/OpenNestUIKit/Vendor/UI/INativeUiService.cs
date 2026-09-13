// ⚠️ Vendor 源码副本：拷自 src/OpenNestCore/UI/INativeUiService.cs，唯一改动 = namespace。见 Vendor/README.md
using System;
using UnityEngine;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#endif

namespace OpenNestUIKit.UI;

/// <summary>
/// 游戏原生 UI 能力桥接接口——把宿主游戏（Iron Nest: Heavy Turret Simulator）的原生 UI 子系统
/// 抽象成平台无关 API，供任何模组（即使不做联机）调用。
///
/// 为什么需要抽象：该游戏是 IL2CPP 裁剪构建，IMGUI(OnGUI) 会渲染在游戏 UGUI 主菜单**之下**
/// （鼠标箭头被盖住、点击被 UGUI 拦截），因此模组 UI 必须用 UGUI Canvas 实现；同时游戏原生 UI
/// 提供了现成能力（本地化 / 通知 toast / ESC 菜单 / 主菜单阶段 / 虚拟光标），直接在模组里引用
/// Assembly-CSharp 类型会失去 BepInEx / MelonLoader 双平台兼容。
///
/// 本接口在本库定义契约（仅依赖 UnityEngine 与 TMP，不依赖游戏 Assembly-CSharp）；
/// 游戏侧实现类（能引用 Assembly-CSharp 的代码，即 <c>Native/UiGameBridge.cs</c>）实现并注册到 <see cref="NativeUi"/>。
/// 未注册时所有门面方法安全返回默认值（null / false / original）。
/// </summary>
public interface INativeUiService
{
    /// <summary>桥接是否已就绪（游戏原生 UI 对象当前可访问）。</summary>
    bool IsAvailable { get; }

    // ---------------- 本地化（LocalisationManager） ----------------

    /// <summary>当前语言代码（如 "zh" / "en"）；未知返回 null。</summary>
    string CurrentLanguage { get; }

    /// <summary>语言切换事件（订阅后切换语言时刷新界面文案）。</summary>
    event Action LanguageChanged;

    /// <summary>按本地化 key 取文本；成功返回 true 并输出文本。</summary>
    bool TryLocalise(string key, out string text);

    /// <summary>把 TMP 字体替换为当前语言的本地化字体（中文需要 fallback 字体，否则显示方框）。
    /// 失败/不支持时返回 original 原样。</summary>
    TMPro.TMP_FontAsset GetLocalisedFont(TMPro.TMP_FontAsset original);

    // ---------------- 通知（UINotificationManager toast） ----------------

    /// <summary>显示一条原生 toast 通知。lifetime&lt;=0 用游戏默认；borderColor 为 null 时无边框色。</summary>
    void ShowToast(string title, string description, float lifetime, Color? borderColor);

    // ---------------- ESC 菜单（EscapeMenuToggleUnityEvent） ----------------

    /// <summary>ESC 菜单当前是否打开。</summary>
    bool IsEscapeMenuOpen { get; }

    /// <summary>ESC 菜单当前是否被某个 blocker 阻止打开。</summary>
    bool IsEscapeMenuBlocked { get; }

    /// <summary>阻止/放行 ESC 菜单打开（模组 UI 打开时通常需要阻止游戏暂停菜单弹出）。</summary>
    void SetEscapeMenuBlocked(bool blocked);

    /// <summary>强制关闭 ESC 菜单（不触发 onClosed 事件时传 false）。</summary>
    void ForceCloseEscapeMenu();

    // ---------------- 主菜单状态（MissionManager / MainMenuStateRelay） ----------------

    /// <summary>当前是否在主菜单（GamePhase.MainMenu）。</summary>
    bool IsMainMenu { get; }

    /// <summary>主菜单加载完成事件。</summary>
    event Action MainMenuLoaded;

    /// <summary>主菜单卸载事件（进入任务场景等）。</summary>
    event Action MainMenuUnloaded;

    // ---------------- 光标（VirtualCursor） ----------------

    /// <summary>游戏虚拟光标屏幕位置；不可用时返回 null。</summary>
    Vector2? VirtualCursorPosition { get; }

    /// <summary>虚拟光标当前是否悬停在可交互 UI 上（VirtualCursorInputModule）。</summary>
    bool IsCursorOverUi { get; }
}

/// <summary>
/// 游戏原生 UI 桥接注册表 + 静态门面。任何代码调用 <c>NativeUi.xxx</c> 即可使用游戏原生 UI 能力，
/// 无需引用游戏程序集。游戏侧桥接实现（INativeUiService）在加载时调用 <see cref="Register"/> 注入。
/// 未注册时门面方法安全返回默认值。
/// </summary>
public static class NativeUi
{
    private static INativeUiService _svc;
    private static readonly object _lock = new object();

    // 事件桥（把服务事件转发给门面订阅者；服务可能被替换/清除，这里统一管理）
    private static event Action _languageChanged;
    private static event Action _mainMenuLoaded;
    private static event Action _mainMenuUnloaded;

    /// <summary>当前已注册的桥接实现（未注册为 null）。</summary>
    public static INativeUiService Service => _svc;

    /// <summary>桥接是否可用（已注册且就绪）。</summary>
    public static bool Available => _svc != null && _svc.IsAvailable;

    /// <summary>注册游戏侧桥接实现（覆盖旧的）。传 null 清除。</summary>
    public static void Register(INativeUiService svc)
    {
        lock (_lock)
        {
            if (_svc != null) Unsubscribe(_svc);
            _svc = svc;
            if (_svc != null) Subscribe(_svc);
        }
    }

    /// <summary>清除桥接（恢复"无原生 UI 能力"）。</summary>
    public static void Clear() => Register(null);

    private static void Subscribe(INativeUiService s)
    {
        s.LanguageChanged += OnLanguageChanged;
        s.MainMenuLoaded += OnMainMenuLoaded;
        s.MainMenuUnloaded += OnMainMenuUnloaded;
    }

    private static void Unsubscribe(INativeUiService s)
    {
        s.LanguageChanged -= OnLanguageChanged;
        s.MainMenuLoaded -= OnMainMenuLoaded;
        s.MainMenuUnloaded -= OnMainMenuUnloaded;
    }

    private static void OnLanguageChanged() => _languageChanged?.Invoke();
    private static void OnMainMenuLoaded() => _mainMenuLoaded?.Invoke();
    private static void OnMainMenuUnloaded() => _mainMenuUnloaded?.Invoke();

    // ---------------- 门面转发 ----------------

    /// <summary>当前语言代码；未注册返回 null。</summary>
    public static string CurrentLanguage => _svc?.CurrentLanguage;

    /// <summary>按 key 取本地化文本；未注册或取不到时原样返回 key。</summary>
    public static string Localise(string key)
        => _svc != null && _svc.TryLocalise(key, out var t) && !string.IsNullOrEmpty(t) ? t : key;

    /// <summary>尝试按 key 取本地化文本；未注册返回 false。</summary>
    public static bool TryLocalise(string key, out string text)
    {
        if (_svc != null) return _svc.TryLocalise(key, out text);
        text = null;
        return false;
    }

    /// <summary>显示原生 toast 通知（默认生命周期，无边框色）。</summary>
    public static void Toast(string title, string description, float lifetime = 0f)
        => _svc?.ShowToast(title, description, lifetime, null);

    /// <summary>显示原生 toast 通知（指定生命周期 + 边框色）。</summary>
    public static void Toast(string title, string description, float lifetime, Color? borderColor)
        => _svc?.ShowToast(title, description, lifetime, borderColor);

    /// <summary>当前是否在主菜单。</summary>
    public static bool IsMainMenu => _svc?.IsMainMenu == true;

    /// <summary>ESC 菜单当前是否打开。</summary>
    public static bool IsEscapeMenuOpen => _svc?.IsEscapeMenuOpen == true;

    /// <summary>ESC 菜单当前是否被阻止。</summary>
    public static bool IsEscapeMenuBlocked => _svc?.IsEscapeMenuBlocked == true;

    /// <summary>阻止/放行 ESC 菜单（模组 UI 打开时阻止游戏暂停菜单弹出）。</summary>
    public static void BlockEscapeMenu(bool blocked) => _svc?.SetEscapeMenuBlocked(blocked);

    /// <summary>强制关闭 ESC 菜单。</summary>
    public static void CloseEscapeMenu() => _svc?.ForceCloseEscapeMenu();

    /// <summary>游戏虚拟光标屏幕位置；不可用返回 null。</summary>
    public static Vector2? VirtualCursorPosition => _svc?.VirtualCursorPosition;

    /// <summary>虚拟光标是否悬停在不透明 UI 上。</summary>
    public static bool IsCursorOverUi => _svc?.IsCursorOverUi == true;

    /// <summary>把 TMP 字体替换为当前语言的本地化字体；未注册时原样返回。</summary>
    public static TMPro.TMP_FontAsset GetLocalisedFont(TMPro.TMP_FontAsset original)
        => _svc != null ? _svc.GetLocalisedFont(original) : original;

    // ---------------- 事件 ----------------

    /// <summary>语言切换事件（随服务注册/替换自动接通）。</summary>
    public static event Action LanguageChanged
    {
        add { _languageChanged += value; }
        remove { _languageChanged -= value; }
    }

    /// <summary>主菜单加载完成事件。</summary>
    public static event Action MainMenuLoaded
    {
        add { _mainMenuLoaded += value; }
        remove { _mainMenuLoaded -= value; }
    }

    /// <summary>主菜单卸载事件。</summary>
    public static event Action MainMenuUnloaded
    {
        add { _mainMenuUnloaded -= value; }
        remove { _mainMenuUnloaded -= value; }
    }
}
