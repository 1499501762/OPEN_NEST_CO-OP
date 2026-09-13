using System;
using UnityEngine;

namespace OpenNestUIKit.Core;

/// <summary>
/// 文案门面（本库自用；不含外部语言文件系统，v1 用「双语内联 + 按当前语言选」）：
/// - <see cref="T"/>：`T("中文", "English")` 按当前语言二选一；
/// - <see cref="Current"/>：当前语言代码（优先游戏本地化桥接，桥接不可用时退化为
///   <c>Application.systemLanguage</c> 判断）；
/// - <see cref="Changed"/>：语言变化事件（由 <see cref="Tick(float)"/> 轮询驱动，
///   不订阅原生事件——IL2CPP 下原生事件订阅有稳定性风险，见 docs/NATIVE_UI.md §5）。
/// </summary>
public static class UiKitLoc
{
    private static string _lang = "";
    private static float _pollT;
    private static int _lastSysLang = -1;

    /// <summary>语言变化事件（宿主/界面订阅后刷新文案）。</summary>
    public static event Action Changed;

    /// <summary>当前语言代码："zh" / "en" / 其它（游戏返回什么就是什么）。</summary>
    public static string Current => _lang.Length > 0 ? _lang : (_lang = Detect());

    /// <summary>是否中文（文案选择用）。</summary>
    public static bool IsChinese
    {
        get
        {
            string l = Current ?? "";
            return l.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                || l.StartsWith("cn", StringComparison.OrdinalIgnoreCase)
                || l.StartsWith("chinese", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>按当前语言二选一（中文 → <paramref name="zh"/>，否则 → <paramref name="en"/>）。</summary>
    public static string T(string zh, string en) => IsChinese ? (zh ?? en ?? "") : (en ?? zh ?? "");

    /// <summary>按当前语言三选一（中文 / 英文 / 其它语言回退英文）。</summary>
    public static string T(string zh, string en, string other)
        => IsChinese ? (zh ?? en ?? "") : (en ?? other ?? "");

    /// <summary>强制指定语言（诊断/自测用）。</summary>
    public static void ForceLanguage(string lang)
    {
        string prev = _lang;
        _lang = lang ?? "";
        if (!string.Equals(prev, _lang, StringComparison.OrdinalIgnoreCase)) Raise();
    }

    /// <summary>每帧驱动（<see cref="UiKitBehaviour.Update"/> 调用）：节流轮询语言变化。</summary>
    public static void Tick(float dt)
    {
        _pollT += dt;
        if (_pollT < 1f) return;
        _pollT = 0f;
        string prev = _lang;
        _lang = Detect();
        int sys = (int)Application.systemLanguage;
        if (!string.Equals(prev, _lang, StringComparison.OrdinalIgnoreCase) || sys != _lastSysLang) Raise();
    }

    private static void Raise()
    {
        _lastSysLang = (int)Application.systemLanguage;
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>探测当前语言：优先游戏本地化桥接，否则看系统语言。
    /// ⚠ 游戏给回来的代码可能是 `Chinese` / `zh-Hans` / `CHS` 等，**归一化**成 `zh`/`en`
    /// （否则 `IsChinese` 判定不成 → 注入到原生菜单的标题会变成英文，而游戏本身是中文）。</summary>
    private static string Detect()
    {
        try
        {
            string l = NativeUi.CurrentLanguage;
            if (!string.IsNullOrEmpty(l))
            {
                string c = l.Trim().ToLowerInvariant();
                if (c.StartsWith("zh") || c.StartsWith("cn") || c.StartsWith("chinese")
                    || c.StartsWith("chs") || c.StartsWith("cht") || c.Contains("中文") || c.Contains("简体") || c.Contains("繁體") || c.Contains("繁体"))
                    return "zh";
                if (c.StartsWith("en")) return "en";
                return c;
            }
        }
        catch { }
        try
        {
            var sl = Application.systemLanguage;
            if (sl == SystemLanguage.Chinese || sl == SystemLanguage.ChineseSimplified
                || sl == SystemLanguage.ChineseTraditional) return "zh";
            return "en";
        }
        catch { return "en"; }
    }

    /// <summary>诊断：语言探测链（实机取证用）。</summary>
    public static string Probe()
    {
        string raw = null, sys = "?", avail = "?";
        try { avail = NativeUi.Available ? "是" : "否"; } catch { }
        try { raw = NativeUi.CurrentLanguage; } catch { }
        try { sys = Application.systemLanguage.ToString(); } catch { }
        return $"语言：Current='{Current}'（IsChinese={IsChinese}）｜游戏桥接={avail} CurrentLanguage='{raw ?? "-"}'｜系统={sys}";
    }
}
