using System;
using UnityEngine;
using UnityEngine.UI;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestUIKit.Menu;

/// <summary>
/// 菜单窗口（本库的主界面）：画布 + 背板 + 窗口（标题栏/返回/关闭）+ 内容区（页面）+ 底栏（面包屑/状态）。
///
/// 职责：
/// - **页面栈**（<see cref="UiPageStack"/>）：主页 → 子页 → 返回（表现等同"跳到另一个菜单"）；
/// - **输入隔离编排**：打开时 <see cref="Native.UiInputGuard.Apply"/> + <see cref="Native.UiEscapeGuard"/> +
///   <see cref="Native.UiPointerRouter.Activate"/>，关闭时**按相反顺序**还原；
/// - **每帧驱动**：热键（F6 开关 / ESC 返回或关闭）+ 节流重申守卫 + 语言变化 + 输入框/快捷键轮询。
///
/// 布局：画布 1920×1080 参考分辨率；窗口 1180×720 居中；内容区右侧留出滚动条宽度。
/// </summary>
public static class UiMenuWindow
{
    private static Canvas _canvas;
    private static Widgets.UiWindow _window;
    private static RectTransform _content;
    private static RectTransform _footer;
    private static Widgets.UiText _crumb;
    private static Widgets.UiText _status;
    /// <summary>底栏右侧的**常驻**信息（第三方用 `UiKitHost.SetFooter` 设；与临时提示共用同一位置，提示优先）。</summary>
    private static string _footerInfo = "";
    private static Widgets.UiButton _backBtn;
    private static readonly UiPageStack _stack = new();
    private static readonly System.Collections.Generic.Dictionary<string, RectTransform> _hosts = new();
    private static readonly System.Collections.Generic.List<UiPage> _built = new();

    private static bool _builtOnce;
    private static float _clock, _nextGuard, _statusUntil;
    private static bool _f6Down, _escDown;
    private static string _lastLang = "";
    private static string _statusText = "";
    private static string _lastPage = "";                      // 上一次广播给契约的页面 id（PageChanged）
    private static Widgets.UiModal _modal;                      // 当前打开的确认框（同时只允许一个）
    private static Action _modalCancel;                          // ESC = 取消（弹框期间不执行返回）

    /// <summary>菜单是否打开。</summary>
    public static bool IsOpen { get; private set; }

    /// <summary>当前页 id。</summary>
    public static string CurrentPage => _stack.Current;

    /// <summary>画布层级（诊断）。</summary>
    public static int SortingOrder => _canvas != null ? _canvas.sortingOrder : -1;

    // ---------------- 开关 ----------------

    /// <summary>切换显示（热键 / 原生菜单项 / 第三方调用）。</summary>
    public static void Toggle()
    {
        if (IsOpen) Close(); else Open();
    }

    /// <summary>打开菜单（<paramref name="pageId"/> 为空 = 主页）。</summary>
    public static void Open(string pageId = null)
    {
        try
        {
            EnsureBuilt();
            if (_canvas == null) return;

            UiPageCatalog.EnsureProviders();
            string id = string.IsNullOrEmpty(pageId) ? UiPageCatalog.HomeId : pageId;
            if (UiPageCatalog.Get(id) == null)
            {
                CoopLog.Warn("uikit.ui", () => $"open '{id}' 找不到页面 → 退回主页");
                id = UiPageCatalog.HomeId;
            }

            IsOpen = true;
            _canvas.gameObject.SetActive(true);
            _stack.Reset(id);
            ShowCurrentPage();

            // 输入隔离（顺序：层级 → 射线/交互 → ESC → 指针路由）
            // ⚠️ 必须先压入 ESC 层级：`UiEscapeGuard.Apply()` 只在**有层级**时才碰游戏 ESC
            //    （无层级时它就是“我们不该占用 ESC”，见 UiEscapeLevels 的说明）。
            Native.UiEscapeLevels.Push(Native.UiEscapeLevels.Window);
            Native.UiInputGuard.Apply(_canvas);
            Native.UiEscapeGuard.Apply();
            Native.UiPointerRouter.Activate();
            if (Native.UiInputGuard.NeedsOwnPointer()) Native.UiCursorOverlay.Show(_canvas);
            else Native.UiCursorOverlay.Hide();

            if (_window != null) _window.PlayOpen();
            Native.UiInputGuard.LogProbe();
            SetStatus(UiKitLoc.T("已打开（F6 关闭 / ESC 返回）", "opened (F6 close / ESC back)"));
            CoopLog.Info("uikit.ui", () => $"菜单打开 page='{id}' stack={_stack.Depth} order={_canvas.sortingOrder}");
        }
        catch (Exception ex)
        {
            CoopLog.Error("uikit.ui", () => "open failed: " + ex.Message);
        }
    }

    /// <summary>关闭菜单（还原全部守卫）。</summary>
    public static void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;

        // 弹着确认框时关菜单：先当成“取消”（否则回调永不触发，第三方的流程会挂住）
        try { if (_modalCancel != null) { var c = _modalCancel; _modalCancel = null; c(); } } catch { }
        // 关闭顺序与打开相反：指针路由 → ESC → 射线/层级
        try { Native.UiPointerRouter.Deactivate(); } catch { }
        try { Widgets.UiTextInput.BlurAllInputs(); } catch { }
        try { Native.UiCursorOverlay.Hide(); } catch { }
        // ESC 关窗陷阱：如果这一次关窗是 ESC 按下的那一帧触发的，**不能立刻放行**游戏 ESC 菜单，
        // 否则同一次按键会被游戏收到 → 我们的窗口刚关、游戏暂停菜单马上弹出来。
        // （弹层级会触发守卫同步：层数归 0 → 放行；键还按着则推到抬起后。）
        try { Native.UiEscapeLevels.Pop(Native.UiEscapeLevels.Window); } catch { }
        try { Native.UiEscapeGuard.ReleaseWhenKeyUp(); } catch { }
        try { Native.UiInputGuard.Restore(); } catch { }
        try { Widgets.UiTooltip.Hide(); } catch { }

        for (int i = 0; i < _built.Count; i++)
        {
            try { _built[i]?.OnExit(); } catch { }
        }
        try { if (_window != null && _window.Go != null) _window.PlayClose(null); } catch { }
        try { if (_canvas != null) _canvas.gameObject.SetActive(false); } catch { }
        CoopLog.Info("uikit.ui", () => "菜单关闭");
    }

    /// <summary>导航到某页（Push 进页面栈；页面自身用 <see cref="Navigate"/> 做子菜单跳转）。</summary>
    public static void Navigate(string pageId)
    {
        if (string.IsNullOrEmpty(pageId)) return;
        if (!IsOpen) { Open(pageId); return; }
        var page = UiPageCatalog.Get(pageId);
        if (page == null)
        {
            SetStatus(UiKitLoc.T("页面不存在：", "no such page: ") + pageId);
            return;
        }
        ApplyStackChange(() => _stack.Push(pageId));
    }

    /// <summary>返回上一级（栈底时关闭菜单）。</summary>
    public static void Back()
    {
        if (!IsOpen) return;
        if (!_stack.CanGoBack) { Close(); return; }
        ApplyStackChange(() => _stack.Pop());
    }

    /// <summary>回到主页。</summary>
    public static void Home()
    {
        if (!IsOpen) return;
        ApplyStackChange(() => _stack.Reset(UiPageCatalog.HomeId));
    }

    /// <summary>窗口 RectTransform（测试/诊断用：算“图形有没有超出窗口”；未创建时 null）。</summary>
    public static RectTransform WindowRect
    {
        get
        {
            try { return _window != null ? _window.Rect : null; }
            catch { return null; }
        }
    }

    /// <summary>窗口位置（anchoredPosition；居中时为 0,0）。</summary>
    public static Vector2 WindowPos
    {
        get
        {
            try { return _window != null && _window.Rect != null ? _window.Rect.anchoredPosition : Vector2.zero; }
            catch { return Vector2.zero; }
        }
    }

    /// <summary>测试/诊断用：把窗口整体错开（验证与其它模组窗口重叠时的观感 / 不同位置下的渲染）。</summary>
    public static bool SetWindowOffset(float dx, float dy)
    {
        try
        {
            if (_window == null || _window.Rect == null) return false;
            _window.Rect.anchoredPosition = new Vector2(dx, dy);
            return true;
        }
        catch { return false; }
    }

    /// <summary>画布根（测试/诊断用：统计图形矩形；未创建时 null）。</summary>
    public static RectTransform CanvasRoot
    {
        get
        {
            try { return _canvas != null ? _canvas.GetComponent<RectTransform>() : null; }
            catch { return null; }
        }
    }

    /// <summary>窗口当前尺寸（未创建时为 zero）。</summary>
    public static Vector2 WindowSize
    {
        get
        {
            try { return _window != null && _window.Rect != null ? _window.Rect.sizeDelta : Vector2.zero; }
            catch { return Vector2.zero; }
        }
    }

    /// <summary>
    /// 测试/诊断用：改窗口尺寸并**重建页面**（验证不同尺寸/布局下的显示是否正常）。
    /// 尺寸是"以参考分辨率为基准"的逻辑像素（canvas 有 CanvasScaler，实际屏幕越小整体等比缩小）。
    /// </summary>
    public static bool SetWindowSize(float w, float h)
    {
        try
        {
            if (_window == null || _window.Rect == null) return false;
            if (w < 320f || h < 240f) return false;
            _window.Rect.sizeDelta = new Vector2(w, h);
            Invalidate();          // 销毁已建页面 → 下一帧按新尺寸重新测量/排列
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// **原地重建当前页**（第三方内容变了：大厅列表 / 成员 / 聊天 / 模组清单…）。
    ///
    /// 做法：销毁已建页面与它的宿主 → 让 provider 的 <c>BuildMenu</c> 重跑（收编新页面）→ 重新显示当前页。
    /// 页面栈/面包屑不变，用户停在原来那一页（输入框草稿会丢，所以第三方应在没在打字时刷）。
    /// </summary>
    public static void ReloadCurrentPage()
    {
        try
        {
            Invalidate();                                     // 销毁已建页面 + 宿主（下次显示时重建）
            UiPageCatalog.InvalidateProviders();              // 让 provider 重新 BuildMenu（拿到最新数据）
            UiPageCatalog.EnsureProviders();
            if (!IsOpen) return;
            // 当前页可能已被 provider 删掉（数据变了）→ 退回主页
            if (UiPageCatalog.Get(_stack.Current) == null) _stack.Reset(UiPageCatalog.HomeId);
            ShowCurrentPage();
        }
        catch (Exception ex) { CoopLog.Warn("uikit.ui", () => "reload current page failed: " + ex.Message); }
    }

    /// <summary>目录/注册表变化 → 下次打开重建入口（页面与宿主都销毁，避免半死状态）。</summary>
    public static void Invalidate()
    {
        try
        {
            for (int i = 0; i < _built.Count; i++)
            {
                try { _built[i]?.Destroy(); } catch { }
            }
            _built.Clear();
        }
        catch { }
        try
        {
            foreach (var kv in _hosts)
            {
                try { if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value.gameObject); } catch { }
            }
            _hosts.Clear();
        }
        catch { }
    }

    /// <summary>销毁（宿主关闭时调用）。</summary>
    public static void Destroy()
    {
        try { Native.UiEscapeLevels.Clear(); } catch { }      // 不留任何“占用 ESC”的残留
        try { Native.UiEscapeGuard.Block(false); } catch { }
        try { UiPointerRouterShutdown(); } catch { }
        try { UiPageCatalog.Clear(); } catch { }
        try { if (_canvas != null) UnityEngine.Object.Destroy(_canvas.gameObject); } catch { }
        _canvas = null; _window = null; _content = null; _footer = null;
        _crumb = null; _status = null; _backBtn = null;
        _hosts.Clear(); _built.Clear();
        _builtOnce = false; IsOpen = false;
    }

    private static void UiPointerRouterShutdown()
    {
        try { Native.UiPointerRouter.Clear(); } catch { }
    }

    /// <summary>底栏状态提示。</summary>
    public static void SetStatus(string text)
    {
        _statusText = text ?? "";
        _statusUntil = _clock + 6f;
        RefreshFooter();
    }

    /// <summary>
    /// 底栏右侧的**常驻**信息（第三方：<c>UiKitHost.SetFooter</c>）。与 <see cref="SetStatus"/> 的临时提示
    /// **共用同一位置**：有提示时显示提示，提示过期后回落到底栏信息。
    ///
    /// 用途（ModMenu 2026-09-13）：把“宿主 / UIKit 版本 / 提供者统计”这类摘要摆到底栏，
    /// 不再作为**页面行**占高度（那样会把页面撑出窗口）。
    /// </summary>
    public static void SetFooter(string text)
    {
        _footerInfo = text ?? "";
        RefreshFooter();
    }

    /// <summary>底栏右侧文本 = 临时提示优先，否则常驻底栏信息。</summary>
    private static void RefreshFooter()
    {
        try { if (_status != null) _status.Value = _statusText.Length > 0 ? _statusText : _footerInfo; } catch { }
    }

    // ---------------- 每帧 ----------------

    /// <summary>把当前页面的某一行（按 <c>UiRow.Key</c>）滚到可见位置（第三方契约 <c>UiKitHost.ScrollToKey</c> 走这里）。</summary>
    public static bool ScrollToKey(string key)
    {
        try
        {
            if (!IsOpen || string.IsNullOrEmpty(key)) return false;
            var page = UiPageCatalog.Get(CurrentPage);
            return page != null && page.ScrollToKey(key);
        }
        catch { return false; }
    }

    /// <summary>当前页面滚回顶部。</summary>
    public static void ScrollTop()
    {
        try
        {
            if (!IsOpen) return;
            UiPageCatalog.Get(CurrentPage)?.ScrollTop();
        }
        catch { }
    }

    /// <summary>
    /// 弹一个模态确认框（第三方契约 <c>UiKitHost.Confirm</c> 走这里）。
    ///
    /// 约定：**只在菜单打开时显示**（需要画布与输入隔离）；菜单关着 → 记一条 warn 并回调 <c>false</c>，
    /// 让调用方的流程能正常结束（不会“点了没反应”）。
    /// ESC = 取消（弹框期间不会顺手把菜单也关掉）。
    /// </summary>
    public static void ShowConfirm(string payload, Action<bool> onResult)
    {
        try
        {
            if (!IsOpen)
            {
                CoopLog.Warn("uikit.ui", () => "Confirm 忽略：菜单未打开（确认框需要画布与输入隔离）");
                try { onResult?.Invoke(false); } catch { }
                return;
            }
            EnsureBuilt();
            if (_modal != null) { try { _modalCancel?.Invoke(); } catch { } }

            string title = payload ?? "", body = "";
            int nl = title.IndexOf('\n');
            if (nl >= 0) { body = title.Substring(nl + 1); title = title.Substring(0, nl); }

            var parent = _window != null && _window.Content != null ? _window.Content : (Transform)CanvasRoot;
            const string Owner = "uikit.modal";
            bool done = false;

            Action<bool> deliver = ok =>
            {
                if (done) return;
                done = true;
                try { Native.UiEscapeLevels.Pop(Owner); } catch { }
                _modal = null;
                _modalCancel = null;
                try { onResult?.Invoke(ok); } catch (Exception ex) { CoopLog.Warn("uikit.ui", () => "confirm callback failed: " + ex.Message); }
            };

            Native.UiEscapeLevels.Push(Owner);
            _modal = Widgets.UiModal.Show(parent, title, body,
                UiKitLoc.T("确定", "OK"), UiKitLoc.T("取消", "Cancel"), deliver);
            _modalCancel = () => { var m = _modal; _modal = null; try { m?.Close(); } catch { } deliver(false); };
            CoopLog.Info("uikit.ui", () => $"确认框已打开 '{title}'");
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.ui", () => "confirm failed: " + ex.Message);
            try { onResult?.Invoke(false); } catch { }
        }
    }

    /// <summary>每帧驱动。</summary>
    public static void Tick(float dt)
    {
        _clock += dt;
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;

            // F6：开/关（输入框聚焦时不响应，避免打字触发）
            bool f6 = false;
            try { f6 = kb != null && kb.f6Key.isPressed; } catch { }
            if (f6 && !_f6Down && !Native.UiPointerRouter.TextFocus)
            {
                CoopLog.Debug("uikit.ui", () => $"热键 F6 → {(IsOpen ? "关闭" : "打开")}");
                Toggle();
            }
            _f6Down = f6;

            // ESC：返回上一级（栈底时关闭）；弹着确认框时先取消确认框
            if (IsOpen)
            {
                bool esc = false;
                try { esc = kb != null && kb.escapeKey.isPressed; } catch { }
                if (esc && !_escDown && !Native.UiPointerRouter.TextFocus && !Widgets.UiTextRouter.ConsumedEsc)
                {
                    if (_modalCancel != null)
                    {
                        CoopLog.Debug("uikit.ui", () => "ESC → 取消确认框");
                        try { _modalCancel(); } catch { }
                    }
                    else
                    {
                        CoopLog.Debug("uikit.ui", () => $"ESC → {(_stack.CanGoBack ? "返回上级" : "关闭菜单")}");
                        Back();
                    }
                }
                _escDown = esc;
            }
            else _escDown = false;

            if (!IsOpen) return;

            // 文本输入走独立管线（UiKitBehaviour.Update 已每帧驱动 UiTextRouter.Tick），这里只管快捷键绑定
            Widgets.UiKeyBind.TickAll(dt);

            // 周期重申守卫（场景切换会新建画布/新 ESC 菜单实例）
            if (_clock >= _nextGuard)
            {                _nextGuard = _clock + 1.5f;
                Native.UiInputGuard.Apply(_canvas);
                Native.UiEscapeGuard.Apply();
            }

            // 语言变化 → 刷新文案（含面包屑/状态）+ 推给第三方契约
            if (UiKitLoc.Current != _lastLang)
            {
                _lastLang = UiKitLoc.Current;
                try { API.UiKitLang.SetChinese(UiKitLoc.IsChinese); } catch { }
                try { UiPageCatalog.NotifyLanguageChanged(); } catch { }
                try { RefreshHeader(); } catch { }
            }

            // 当前页面变化 → 广播给第三方（打开/导航/返回/关闭都会走到）
            string nowPage = IsOpen ? (CurrentPage ?? "") : "";
            if (!string.Equals(nowPage, _lastPage, StringComparison.Ordinal))
            {
                string oldPage = _lastPage;
                _lastPage = nowPage;
                try { API.UiKitHost.RaisePageChanged(oldPage, nowPage); } catch { }
            }

            if (_statusText.Length > 0 && _clock >= _statusUntil)
            {
                _statusText = "";
                RefreshFooter();      // 回落到底栏常驻信息（没有就清空）
            }
        }
        catch { }
    }

    /// <summary>每帧后段（LateUpdate）：层级重申 + 自绘指针 + 提示跟随。</summary>
    public static void TickLate()
    {
        if (!IsOpen) return;
        try { Native.UiInputGuard.TickLayerOrder(); } catch { }
        try { Native.UiEscapeGuard.TickFallback(); } catch { }
        try { Native.UiCursorOverlay.Tick(); } catch { }
        try { Widgets.UiTooltip.Tick(Time.unscaledDeltaTime); } catch { }
    }

    // ---------------- 构建 ----------------

    private static void EnsureBuilt()
    {
        if (_builtOnce && _canvas != null) return;
        _builtOnce = true;

        try
        {
            _canvas = UI.UiKit.CreateCanvas("OpenNestUIKit_UI", 32766);
            if (_canvas == null) { CoopLog.Error("uikit.ui", () => "画布创建失败"); return; }

            // 背板（压暗 + 拦截射线）
            UI.UiKit.MakeBlocker(_canvas.transform, Theme.UiTheme.Backdrop);

            // ⚠ 2026-09-13（用户：“两个菜单右上角的 Close 和右上角的 X 只留一个就行了”）：
            //   关闭**只留一个** —— 交给下面的「返回/关闭」按钮（栈底时它就是关闭）。
            //   所以窗口**不再建自带的 X**（`onClose = null`；`UiWindow` 那边会相应把标题框拉宽）。
            _window = Widgets.UiWindow.Create(_canvas.transform, UiKitInfo.Name, null);
            if (_window == null) { CoopLog.Error("uikit.ui", () => "窗口创建失败"); return; }

            // 返回/关闭按钮（标题栏最右）：**栈底 = 关闭（图标 ×，紧凑方形）/ 有上级页 = 文字「< 返回」**
            // ⚠ 用户 2026-09-13：“右上角 Close 文本改成 ×，样式改好看点” —— 两态共用一个按钮（宽度随态变），
            //   文字与宽度统一由 <see cref="RefreshHeaderButton"/> 维护（含标题内缩避让）。
            _backBtn = Widgets.UiButton.Create(_window.Header, "×", Back,
                Widgets.UiButtonStyle.Secondary, 28f, 26f, color: Theme.UiTheme.HeaderBg, zoneName: "btn:headerclose");
            try
            {
                _backBtn.Rect.anchorMin = _backBtn.Rect.anchorMax = new Vector2(1f, 0.5f);
                _backBtn.Rect.pivot = new Vector2(1f, 0.5f);
                _backBtn.Rect.anchoredPosition = new Vector2(-12f, 0f);
                if (_backBtn.Text != null) { _backBtn.Text.FontSize = 16f; }   // 图标字形比正文大一号才不显得空
            }
            catch { }

            // 内容区（窗口内容 + 内边距）
            _content = _window.Content;
            // ⚠️ 这里必须**四边拉伸**（anchorMin=(0,0)）：早期版本用 (0,1)-(1,1)（顶对齐）却按
            // “左/下/右/上内缩”给 offset → 高度被算成 offsetMax.y-offsetMin.y = 负值（实测 -52），
            // 页面列表的视口因此为负高，`RectMask2D` 把整页裁掉 = “窗口开着但内容看不见”。
            var host = UI.UiKit.MakeRect("pageHost", _content, new Vector2(0f, 0f), new Vector2(1f, 1f),
                Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f));
            try
            {
                host.offsetMin = new Vector2(Theme.UiTheme.Pad, Theme.UiTheme.FooterH + Theme.UiTheme.GapSm);
                host.offsetMax = new Vector2(-Theme.UiTheme.Pad, -Theme.UiTheme.GapSm);
            }
            catch { }
            _content = host;

            // 底栏（面包屑 + 状态）——**锚点式**（不写死窗口高：页面可以有自己的首选尺寸）
            _footer = UI.UiKit.MakeRect("footer", _window.Rect, new Vector2(0f, 0f), new Vector2(1f, 0f),
                Vector2.zero, Vector2.zero, new Vector2(0.5f, 0f));
            try
            {
                _footer.offsetMin = new Vector2(Theme.UiTheme.Pad, 4f);
                _footer.offsetMax = new Vector2(-Theme.UiTheme.Pad, 4f + Theme.UiTheme.FooterH);
            }
            catch { }

            // 扁平工业风底栏：**实底 + 顶部发丝线**（状态栏那种“一块钢板”的观感，不靠渐变）。
            Theme.ModStyle.Fill(_footer, "bg", Theme.UiTheme.FooterBg);
            Theme.ModStyle.TopRule(_footer, Theme.UiTheme.Hairline);

            _crumb = Widgets.UiText.Create(_footer, "", Widgets.UiTextKind.Note, 0f, TextAlignmentOptions.MidlineLeft);
            try
            {
                _crumb.Rect.anchorMin = new Vector2(0f, 0f);
                _crumb.Rect.anchorMax = new Vector2(0.6f, 1f);
                _crumb.Rect.pivot = new Vector2(0f, 0.5f);
                _crumb.Rect.offsetMin = Vector2.zero;
                _crumb.Rect.offsetMax = Vector2.zero;
                _crumb.SetSingleLine(true);              // ⚠ 底栏是一行：多行会把文字溢出面板（实测三者宿主摘要）
            }
            catch { }

            _status = Widgets.UiText.Create(_footer, "", Widgets.UiTextKind.Note, 0f, TextAlignmentOptions.MidlineRight);
            try
            {
                _status.Rect.anchorMin = new Vector2(0.4f, 0f);
                _status.Rect.anchorMax = new Vector2(1f, 1f);
                _status.Rect.pivot = new Vector2(1f, 0.5f);
                _status.Rect.offsetMin = Vector2.zero;
                _status.Rect.offsetMax = Vector2.zero;
                _status.SetSingleLine(true);             // 同上：常驻摘要/状态提示都只能占一行（超长省略）
            }
            catch { }

            Widgets.UiTooltip.Init(_canvas);

            _stack.Changed += RefreshHeader;
            UiPageCatalog.Changed += OnCatalogChanged;
            _lastLang = UiKitLoc.Current;

            _canvas.gameObject.SetActive(false);
            CoopLog.Info("uikit.ui", () => $"界面骨架已建立（窗口 {Theme.UiTheme.WindowW:0}x{Theme.UiTheme.WindowH:0}，画布 order={_canvas.sortingOrder}）");
        }
        catch (Exception ex)
        {
            CoopLog.Error("uikit.ui", () => "EnsureBuilt failed: " + ex.Message);
        }
    }

    /// <summary>
    /// 按页面声明的**首选尺寸**调窗口（0 = 不动）。尺寸真变了才重建（已建页面/宿主全销毁）。
    /// 用途：ModMenu 要“左列表 + 右详情”的双栏（1180×700），Coop 要原模组那种紧凑窄面板（520 宽）。
    /// </summary>
    private static void ApplyPageWindowSize(UiPage page)
    {
        try
        {
            if (page == null || _window == null || _window.Rect == null) return;
            float w = page.PreferredWidth > 0f ? page.PreferredWidth : Theme.UiTheme.WindowW;
            float h = page.PreferredHeight > 0f ? page.PreferredHeight : Theme.UiTheme.WindowH;
            var cur = _window.Rect.sizeDelta;
            if (Mathf.Abs(cur.x - w) < 0.5f && Mathf.Abs(cur.y - h) < 0.5f) return;
            _window.Rect.sizeDelta = new Vector2(w, h);
            CoopLog.Debug("uikit.ui", () => $"窗口尺寸 → {w:0}x{h:0}（页面 '{page.Id}' 的首选尺寸）");
            Invalidate();
        }
        catch (Exception ex) { CoopLog.Warn("uikit.ui", () => "apply page window size failed: " + ex.Message); }
    }

    private static void OnCatalogChanged()
    {
        try { RefreshHeader(); } catch { }
    }

    /// <summary>切页：显示目标页（未构建则先建），隐藏其它页。</summary>
    private static void ShowCurrentPage()
    {
        string id = _stack.Current;
        var page = UiPageCatalog.Get(id);
        if (page == null)
        {
            CoopLog.Warn("uikit.ui", () => $"页面 '{id}' 不存在");
            return;
        }

        try
        {
            ApplyPageWindowSize(page);

            if (!page.IsBuilt)
            {
                RectTransform host;
                if (!_hosts.TryGetValue(page.Id, out host) || host == null)
                {
                    host = UI.UiKit.MakeRectFill("page:" + page.Id, _content);
                    _hosts[page.Id] = host;
                }
                // 页面可以声明自己的密度（紧凑 = 原模组窄面板观感）：构建期间生效，构建完全部还原
                Theme.UiTheme.PushDensity(page.Compact);
                try { page.Build(host); }
                finally { Theme.UiTheme.PopDensity(); }
                page.AttachRoot(host);          // 构建器没给 Root 时把宿主当页面根（否则旧页不隐藏 + 每次重显示重建一份）
                if (page.Root != null && !ReferenceEquals(page.Root, host))
                {
                    // 页面根铺满宿主（页面内部自己排布）
                    Layout.UiStretch.Fill(page.Root);
                }
                _built.Add(page);
                CoopLog.Debug("uikit.ui", () => $"页面已构建 '{page.Id}'（共 {_built.Count}）");
            }

            for (int i = 0; i < _built.Count; i++)
            {
                var p = _built[i];
                p.SetVisible(p != null && ReferenceEquals(p, page));
            }
            try { if (_hosts.TryGetValue(page.Id, out var h) && h != null) h.gameObject.SetActive(true); } catch { }
            page.OnEnter();
            RefreshHeader();
            try { Widgets.UiTextInput.BlurAllInputs(); } catch { }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.ui", () => $"ShowCurrentPage('{id}') failed: {ex.Message}");
        }
    }

    private static void ApplyStackChange(Action mutate)
    {
        try
        {
            var prev = UiPageCatalog.Get(_stack.Current);
            mutate?.Invoke();
            try { prev?.OnExit(); } catch { }
            ShowCurrentPage();
        }
        catch (Exception ex) { CoopLog.Warn("uikit.ui", () => "stack change failed: " + ex.Message); }
    }

    /// <summary>刷新标题 / 面包屑 / 返回按钮可见性。</summary>
    private static void RefreshHeader()
    {
        try
        {
            var page = UiPageCatalog.Get(_stack.Current);
            if (_window != null) _window.SetTitle(page != null ? page.Title : UiKitInfo.Name);
            if (_crumb != null)
            {
                var sb = new System.Text.StringBuilder();
                var path = _stack.Path;
                for (int i = 0; i < path.Length; i++)
                {
                    if (i > 0) sb.Append("  >  ");
                    var p = UiPageCatalog.Get(path[i]);
                    sb.Append(p != null ? p.Title : path[i]);
                }
                _crumb.Value = sb.ToString();
            }
            if (_backBtn != null)
            {
                RefreshHeaderButton();
            }
        }
        catch { }
    }

    /// <summary>
    /// 标题栏右侧那颗按钮的两态：栈底 = **关闭（图标 ×，紧凑方形）**；有上级页 = **文字「&lt; 返回」**。
    /// 宽度、文案、标题避让三件事都在这里统一改（否则会出现“标题压在按钮下面”）。
    /// </summary>
    private static void RefreshHeaderButton()
    {
        try
        {
            if (_backBtn == null) return;
            bool canBack = _stack.CanGoBack;
            float w = canBack ? 84f : 28f;
            _backBtn.Visible = true;                       // 始终显示（栈底点击 = 关闭）
            _backBtn.SetSize(w, 26f);
            _backBtn.SetText(canBack ? "< " + UiKitLoc.T("返回", "Back") : "×");
            if (_window != null && _window.Title != null)
                _window.Title.Rect.offsetMax = new Vector2(-(w + 20f), 0f);
        }
        catch { }
    }
}
