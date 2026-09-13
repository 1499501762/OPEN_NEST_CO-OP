using System;
using System.Collections.Generic;

namespace OpenNestUIKit.API;

/// <summary>
/// 静态注册表（第三方 ↔ 宿主之间的唯一汇合点）。
///
/// 设计同 <c>OpenNestModMenu.API.ModMenuHost</c>：
/// - 第三方**随时**可以 <see cref="Register"/>（哪怕 UIKit 还没加载/根本没装）——注册只是进内存表；
/// - 宿主启动后调用 <see cref="MarkHostAvailable"/>（internal，只有本库的壳能调）把"已就绪"写进来，
///   并 <see cref="Changed"/> 通知宿主来取；
/// - 第三方通过 <see cref="IsHostAvailable"/> 判断是否真的接上了（没装时它一直是 false，第三方据此降级）。
///
/// ⚠️ 一律不抛异常：任何 provider 的异常都不能影响第三方模组自身的加载。
/// </summary>
public static class UiKitHost
{
    /// <summary>契约版本（宿主与第三方对齐用；不兼容变更时 +1）。</summary>
    public const int ApiVersion = 1;

    private static readonly List<IUiKitProvider> _providers = new();
    private static readonly object _sync = new();

    /// <summary>注册表变化（注册/注销/宿主上线或下线）→ 宿主订阅后刷新界面。</summary>
    public static event Action Changed;

    /// <summary>宿主是否已就绪（未安装 OpenNestUIKit 时恒为 false）。</summary>
    public static bool IsHostAvailable { get; private set; }

    /// <summary>宿主版本（未就绪时为空串）。</summary>
    public static string HostVersion { get; private set; } = "";

    /// <summary>已注册 provider 数。</summary>
    public static int ProviderCount { get { lock (_sync) return _providers.Count; } }

    /// <summary>已注册 provider 快照（拷贝，调用方可安全遍历）。</summary>
    public static IUiKitProvider[] Providers
    {
        get { lock (_sync) return _providers.ToArray(); }
    }

    /// <summary>注册菜单提供者（重复注册同 Id 会替换旧的；null 忽略）。</summary>
    public static void Register(IUiKitProvider provider)
    {
        if (provider == null) return;
        string id = "";
        try { id = provider.Id ?? ""; } catch { }
        if (id.Length == 0) return;

        lock (_sync)
        {
            for (int i = 0; i < _providers.Count; i++)
            {
                string other = "";
                try { other = _providers[i]?.Id ?? ""; } catch { }
                if (string.Equals(other, id, StringComparison.OrdinalIgnoreCase))
                {
                    if (ReferenceEquals(_providers[i], provider)) return;   // 幂等
                    _providers[i] = provider;
                    RaiseChanged();
                    return;
                }
            }
            _providers.Add(provider);
        }
        RaiseChanged();
    }

    /// <summary>注销（第三方卸载时调用）。</summary>
    public static void Unregister(IUiKitProvider provider)
    {
        if (provider == null) return;
        bool removed;
        lock (_sync) removed = _providers.Remove(provider);
        if (removed) RaiseChanged();
    }

    /// <summary>按 Id 注销。</summary>
    public static void Unregister(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        bool removed = false;
        lock (_sync)
        {
            for (int i = _providers.Count - 1; i >= 0; i--)
            {
                string other = "";
                try { other = _providers[i]?.Id ?? ""; } catch { }
                if (string.Equals(other, id, StringComparison.OrdinalIgnoreCase))
                {
                    _providers.RemoveAt(i);
                    removed = true;
                }
            }
        }
        if (removed) RaiseChanged();
    }

    /// <summary>清空（宿主关闭时调用；第三方不必关心）。</summary>
    public static void Clear()
    {
        lock (_sync) _providers.Clear();
        RaiseChanged();
    }

    // ---------------- 宿主菜单控制（宿主启动时注入实现） ----------------
    //
    // 为什么要这层间接：第三方只引**契约 dll**（`OpenNestUIKit.API`），不引宿主程序集
    // （引了就等于硬依赖，没装 UIKit 时整个模组起不来）。而"打开/关闭本库菜单"这件事
    // 必须由宿主实现 —— 所以由宿主启动时把自己的方法挂进来，第三方通过本契约调用。

    private static Func<bool> _isMenuOpen;
    private static Action<string> _openMenu;
    private static Action _closeMenu;
    private static Action _refreshMenu;
    private static Func<string> _currentPage;
    private static Func<bool> _textFocused;
    private static Action<string, Func<IReadOnlyList<string>>, Action<string>, Func<string>> _setChat;
    private static Action _clearChat;
    private static Action<string> _focusChat;
    private static Action _closeChat;
    private static Action<string, Action<bool>> _confirm;
    private static Action<string> _scrollTo;
    private static Action _scrollTop;

    /// <summary>本库菜单当前是否打开（宿主未就绪时恒为 false）。</summary>
    public static bool IsMenuOpen
    {
        get { try { return _isMenuOpen != null && _isMenuOpen(); } catch { return false; } }
    }

    /// <summary>宿主是否提供了菜单控制（false = 宿主未就绪，调用会被忽略）。</summary>
    public static bool CanControlMenu => _openMenu != null;

    /// <summary>打开本库菜单（<paramref name="pageId"/> 为空 = 主页；找不到该页则回主页）。</summary>
    public static void OpenMenu(string pageId = null)
    {
        try { _openMenu?.Invoke(pageId ?? ""); } catch { /* 第三方调用不能把异常带回去 */ }
    }

    /// <summary>关闭本库菜单（未打开时无害）。</summary>
    public static void CloseMenu()
    {
        try { _closeMenu?.Invoke(); } catch { }
    }

    /// <summary>打开（未开则先开）/关闭（已开则关）。</summary>
    public static void ToggleMenu(string pageId = null)
    {
        if (IsMenuOpen) CloseMenu(); else OpenMenu(pageId);
    }

    /// <summary>
    /// **重建菜单内容**（当前页原地刷新，页面栈/位置不变）。
    ///
    /// 用途：第三方菜单里有“会变的东西”（大厅列表 / 成员 / 聊天记录 / 模组清单 / 开关状态）时，
    /// 在数据变化后调一次 —— 宿主会重新调用 <see cref="IUiKitProvider.BuildMenu"/> 把页面重建成最新状态。
    /// 用户输入框里的草稿会在重建时丢失 → 建议在 <see cref="IsTextInputFocused"/> = true 时跳过刷新。
    /// </summary>
    public static void Refresh()
    {
        try { _refreshMenu?.Invoke(); } catch { }
    }

    /// <summary>当前显示的页面 id（未打开时为空串）。第三方可用它判断“我这一页是不是正在被看”。</summary>
    public static string CurrentPageId
    {
        get { try { return _currentPage != null ? (_currentPage() ?? "") : ""; } catch { return ""; } }
    }

    /// <summary>本库菜单里是否有文本输入框正在聚焦（打字中）—— 自动刷新前先看这个，别把用户草稿冲掉。</summary>
    public static bool IsTextInputFocused
    {
        get { try { return _textFocused != null && _textFocused(); } catch { return false; } }
    }

    // ---------------- 页面事件 / 滚动定位 / 确认框（宿主提供实现） ----------------

    /// <summary>
    /// 当前显示页面发生变化（<c>(旧页 id, 新页 id)</c>；关闭菜单 = 新 id 为空串）。
    ///
    /// 用途：页面被打开时才去拉数据 / 只在"我这一页被看着"时启动轮询，而不是在 <c>BuildMenu</c> 里做重活。
    /// </summary>
    public static event Action<string, string> PageChanged;

    /// <summary>宿主专用：页面切换时通知契约（第三方不要调用）。</summary>
    internal static void RaisePageChanged(string oldId, string newId)
    {
        try { PageChanged?.Invoke(oldId ?? "", newId ?? ""); } catch { /* 订阅者异常不外溢 */ }
    }

    /// <summary>
    /// 弹一个**原生确认框**（遮罩 + 标题/正文 + 确定/取消），结果回调 <paramref name="onResult"/>。
    ///
    /// 用途："恢复默认""退出房间"这种要确认的动作；没装宿主时静默无效（不抛异常）。
    /// ESC = 取消（宿主会压一层 ESC 等级，不会顺手把菜单也关掉）。
    /// </summary>
    public static void Confirm(string title, string body, Action<bool> onResult)
    {
        try { _confirm?.Invoke(MakeConfirmPayload(title, body), onResult); } catch { }
    }

    /// <summary>把标题/正文打包（宿主侧按 '\\n' 拆；保持契约只有一个 string 参数，方便以后加字段）。</summary>
    private static string MakeConfirmPayload(string title, string body)
        => (title ?? "") + "\n" + (body ?? "");

    /// <summary>宿主是否提供了确认框（未就绪 = false）。</summary>
    public static bool CanShowDialog => _confirm != null;

    /// <summary>把某一行（按 <see cref="UiRow.Key"/>）滚动到可见位置 —— 列表/详情联动用。</summary>
    public static void ScrollToKey(string key)
    {
        try { _scrollTo?.Invoke(key ?? ""); } catch { }
    }

    /// <summary>页面滚回顶部（<see cref="Refresh"/> 之后复位视线用）。</summary>
    public static void ScrollToTop()
    {
        try { _scrollTop?.Invoke(); } catch { }
    }

    // ---------------- 悬浮聊天层（第三方“会话中的聊天”交给宿主渲染） ----------------

    /// <summary>宿主有没有挂上悬浮层实现（没挂 = 调用无害）。</summary>
    public static bool CanShowChat
    {
        get { try { return _setChat != null; } catch { return false; } }
    }

    /// <summary>悬浮聊天层当前是否展开（正在打字）。</summary>
    public static bool IsChatTyping
    {
        get { try { return IsTextInputFocused; } catch { return false; } }
    }

    /// <summary>
    /// 注册/更新**悬浮聊天层**（默认是左侧中部的独立小面板，与菜单窗口无关）：
    /// · 收起时只显示最近几条聊天（半透明、鼠标穿透）；
    /// · **回车唤入** → 展开输入行（自动唤起系统输入法，走本库输入管线）；
    /// · **回车发送 / ESC 收起**；发送后自动收起。
    ///
    /// <paramref name="lines"/> 返回“最近的消息”（新的在后面；宿主每 0.4s 拉一次，内容变了才重建界面）；
    /// <paramref name="onSend"/> 收到用户提交的文本；<paramref name="hint"/> 可选，返回收起时显示的那行提示。
    /// 离开会话时调 <see cref="ClearChat"/>。
    /// </summary>
    public static void SetChat(Func<IReadOnlyList<string>> lines, Action<string> onSend,
                               string title = null, Func<string> hint = null)
    {
        try { _setChat?.Invoke(title, lines, onSend, hint); } catch { }
    }

    /// <summary>注销悬浮聊天层（离开会话）。</summary>
    public static void ClearChat()
    {
        try { _clearChat?.Invoke(); } catch { }
    }

    /// <summary>展开并聚焦聊天输入（第三方想让玩家直接开口聊天时用；<paramref name="draft"/> 可预填草稿）。</summary>
    public static void FocusChat(string draft = null)
    {
        try { _focusChat?.Invoke(draft); } catch { }
    }

    /// <summary>收起聊天输入（保留历史）。</summary>
    public static void CloseChat()
    {
        try { _closeChat?.Invoke(); } catch { }
    }

    /// <summary>宿主专用：挂上悬浮层实现（重入安全；宿主关闭时传 null 即解钩）。</summary>
    private static Action<string> _setFooter;

    /// <summary>
    /// 底栏右侧的**常驻**信息（宿主保持到下次设置；与宿主自己的状态提示共用同一位置，提示优先）。
    ///
    /// 典型用途：把“运行环境摘要 / 版本 / 统计”摆到底栏，而不是作为页面行占高度——
    /// 后者会把页面（乃至窗口）高度顶出去。传空串 = 清除；宿主未实现时静默忽略。
    /// </summary>
    public static void SetFooter(string text)
    {
        try { _setFooter?.Invoke(text ?? ""); } catch { }
    }

    internal static void SetFooterControl(Action<string> set) { _setFooter = set; }

    internal static void SetChatControls(Action<string, Func<IReadOnlyList<string>>, Action<string>, Func<string>> set,
                                         Action clear, Action<string> focus, Action close)
    {
        _setChat = set;
        _clearChat = clear;
        _focusChat = focus;
        _closeChat = close;
    }

    /// <summary>宿主专用：挂上菜单控制实现（重入安全；宿主关闭时传 null 即解钩）。</summary>
    internal static void SetMenuControls(Func<bool> isOpen, Action<string> open, Action close,
                                         Action refresh = null, Func<string> currentPage = null, Func<bool> textFocused = null)
    {
        _isMenuOpen = isOpen;
        _openMenu = open;
        _closeMenu = close;
        _refreshMenu = refresh;
        _currentPage = currentPage;
        _textFocused = textFocused;
        RaiseChanged();
    }

    /// <summary>宿主专用：挂上确认框 / 滚动定位实现（宿主关闭时传 null 即解钩）。</summary>
    internal static void SetViewControls(Action<string, Action<bool>> confirm, Action<string> scrollToKey, Action scrollTop)
    {
        _confirm = confirm;
        _scrollTo = scrollToKey;
        _scrollTop = scrollTop;
    }

    // ---------------- 宿主专用（internal：靠 InternalsVisibleTo 只对本库壳开放） ----------------

    /// <summary>宿主启动：标记"宿主已就绪"。第三方注册表在此之前的内容会被保留。</summary>
    internal static void MarkHostAvailable(string version)
    {
        HostVersion = version ?? "";
        IsHostAvailable = true;
        RaiseChanged();
    }

    /// <summary>宿主关闭：标记"宿主已下线"（第三方据 <see cref="IsHostAvailable"/> 降级）。</summary>
    internal static void MarkHostUnavailable()
    {
        IsHostAvailable = false;
        HostVersion = "";
        RaiseChanged();
    }

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { /* 任何订阅者异常都不能外溢 */ }
    }
}
