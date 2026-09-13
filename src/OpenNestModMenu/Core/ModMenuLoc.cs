using System;
using OpenNestModMenu.Loc;
using UnityEngine;

namespace OpenNestModMenu.Core;

/// <summary>
/// 界面文案门面（**语言键**）：代码里只出现键，文本来自外部语言键文件
/// `OpenNestModMenu.lang.ini`（读写见 <see cref="LocFile"/>，默认表见 <see cref="LocDefaults"/>）。
/// 见 `docs/MOD_MENU.md` §5.4。
///
/// 语言判定顺序：
///   1. 原生 UI 桥接已注册（<see cref="NativeUi"/>）→ 跟随游戏当前语言（游戏内切语言即时生效）；
///   2. 否则 Unity <see cref="Application.systemLanguage"/>；
///   3. 都拿不到 → `en`。
///
/// 语言每秒轮询一次；变化时抛 <see cref="Changed"/> 让 UI 重刷。
/// </summary>
public static class ModMenuLoc
{
    /// <summary>语言变化（UI 订阅 → 置脏重刷）。</summary>
    public static event Action Changed;

    /// <summary>
    /// 主动通知一次“文案/开关变了，界面该重刷”（设置页切了开关但不想等下一帧语言轮询时用）。
    /// 两套界面都订阅了它（自带 UGUI 界面 → 置脏重画；UIKit 界面 → 另行 Refresh）。
    /// </summary>
    public static void NotifyChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }

    private static bool _init;
    private static string _code = "";
    private static float _pollTimer;

    /// <summary>取文案（键）。</summary>
    public static string L(string key) => LocFile.Get(key);

    /// <summary>取文案（键，带 `{0}`/`{1}` 占位符）。</summary>
    public static string L(string key, params object[] args) => LocFile.Get(key, args);

    /// <summary>初始化（Startup 调用一次）：定位语言文件 + 首次语言检测。</summary>
    public static void Init(string langFilePath)
    {
        if (_init) return;
        _init = true;
        try { LocFile.Init(langFilePath); } catch { }
        _code = DetectLanguage();
        try { LocFile.SetLanguage(_code); } catch { }
    }

    /// <summary>每帧驱动：语言文件热重载 + 每秒语言变化轮询。</summary>
    public static void Tick(float dt)
    {
        if (!_init) return;
        try { LocFile.Tick(dt); } catch { }

        _pollTimer += dt;
        if (_pollTimer < 1f) return;
        _pollTimer = 0f;
        try
        {
            string code = DetectLanguage();
            if (!string.Equals(code, _code, StringComparison.OrdinalIgnoreCase))
            {
                _code = code;
                LocFile.SetLanguage(code);
                try { Changed?.Invoke(); } catch { }
            }
        }
        catch { }
    }

    /// <summary>当前语言代码（`zh` / `en` / 用户自加的语言段）。</summary>
    public static string Current => string.IsNullOrEmpty(_code) ? "en" : _code;

    /// <summary>当前是否中文。</summary>
    public static bool IsChinese => Current.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>语言文件路径（诊断用）。</summary>
    public static string FilePath => LocFile.FilePath;

    private static string DetectLanguage()
    {
        // 1) 原生 UI 桥接（游戏内切语言能实时跟随）
        try
        {
            if (NativeUi.Available)
            {
                var lang = NativeUi.CurrentLanguage;
                if (!string.IsNullOrEmpty(lang)) return Normalize(lang);
            }
        }
        catch { }

        // 2) 系统语言
        try
        {
            var l = Application.systemLanguage;
            if (l == SystemLanguage.Chinese || l == SystemLanguage.ChineseSimplified || l == SystemLanguage.ChineseTraditional)
                return "zh";
        }
        catch { }

        // 3) 兜底
        return "en";
    }

    private static string Normalize(string code)
    {
        var c = code.Trim().ToLowerInvariant();
        if (c.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh";
        if (c.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
        return c;   // 用户自加的语言段（如 ja）也直接生效
    }
}
