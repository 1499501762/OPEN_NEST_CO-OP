using System;

namespace OpenNestUIKit.API;

/// <summary>
/// 语言键（与宿主同一个语言）：第三方页面的文案用 <see cref="T"/> 写双语，
/// 宿主在语言变化时推给契约并触发 <see cref="Changed"/>。
///
/// ⚠️ 名字是 <c>UiKitLang</c>（不是 <c>UiKitLoc</c>）：宿主程序集里也有一个内部用的
/// <c>OpenNestUIKit.Core.UiKitLoc</c>，两个同名类型会让"同时 using 两边"的宿主文件产生歧义引用。
/// 第三方只用这一个，不必关心宿主那个。
///
/// **软依赖语义**：宿主不在（或还没启动）时 <see cref="IsChinese"/> 按系统语言探测，
/// <see cref="T"/> 仍会返回一个可用文本 —— 第三方不需要写"没装 UIKit 时的降级分支"。
///
/// 用法：
/// <code>
/// page.Toggle("mymod.on", UiKitLang.T("启用", "Enable"), cfg.On, v => cfg.On = v);
/// // 语言切换后想让页面跟着变：订阅一次，然后 Refresh 一下
/// UiKitLang.Changed += () => UiKitHost.Refresh();
/// </code>
/// </summary>
public static class UiKitLang
{
    /// <summary>宿主语言变化（中/英切换）→ 刷新界面。</summary>
    public static event Action Changed;

    private static bool _chinese = DetectChinese();

    /// <summary>当前是否为中文（宿主未就绪时 = 按系统语言探测）。</summary>
    public static bool IsChinese => _chinese;

    /// <summary>按当前语言二选一（<paramref name="zh"/> 中文文案，<paramref name="en"/> 英文文案）。</summary>
    public static string T(string zh, string en)
        => _chinese ? (zh ?? en ?? "") : (en ?? zh ?? "");

    /// <summary>两种语言之外的第三语言兜底（宿主暂时只支持中/英）。</summary>
    public static string T(string zh, string en, string other)
        => _chinese ? (zh ?? en ?? other ?? "") : (en ?? zh ?? other ?? "");

    /// <summary>宿主专用：语言变化时推给契约（第三方不要调用）。</summary>
    internal static void SetChinese(bool chinese)
    {
        if (_chinese == chinese) return;
        _chinese = chinese;
        try { Changed?.Invoke(); } catch { /* 订阅者异常不能影响宿主 */ }
    }

    private static bool DetectChinese()
    {
        try
        {
            string lang = System.Globalization.CultureInfo.CurrentCulture?.TwoLetterISOLanguageName ?? "";
            return lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

/// <summary>
/// 可选接口：让宿主在页面底部替你放一个"恢复默认"按钮（并走原生确认框）。
///
/// 设置页的通用约定（与 <c>OpenNestModMenu.API.IModMenuProvider.ResetToDefaults</c> 同名同义）。
/// 实现它之后：宿主在**你的根页**末尾追加一行 <c>Reset to defaults</c> →
/// 点击 → 确认框 → 确认后回调你 → 宿主自动 <see cref="UiKitHost.Refresh"/>。
/// 不实现 = 页面上不会出现这个按钮（你可以用 <c>page.Button</c> 自己做一个）。
/// </summary>
public interface IUiKitDefaults
{
    /// <summary>把设置恢复成默认值（写入你自己的配置；不要在这里调 Refresh，宿主会调）。</summary>
    void ResetToDefaults();
}
