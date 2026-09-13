using System;
using System.Collections.Generic;
using OpenNestModMenu.API;
using OpenNestModMenu.Config;
using OpenNestModMenu.Core;
using UnityEngine;
using UnityEngine.UI;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestModMenu.UI;

/// <summary>
/// 模组菜单主界面（T4：覆盖 UI 骨架）。布局见 docs/MOD_MENU.md §5.1：
/// 标题栏 + 左栏列表（带来源标记）+ 右栏详情 + 底栏状态。
///
/// 设计要点：
/// - **普通静态类**，不做成 MonoBehaviour：IL2CPP 下每多注入一个类型就多一分风险，
///   而本模组已有 <see cref="Core.ModMenuBehaviour"/> 每帧驱动，直接调 <see cref="Tick"/> 即可。
/// - **懒创建**：首次打开才建 Canvas/控件；关闭只 SetActive(false)（不留每帧开销，也不反复重建）。
/// - **不按帧刷新**：只在打开时 + 清单变化后（节流 0.3s）重建列表（§6.3）。
/// - v1 用纯色（不接原生素材，§5.3）。
/// </summary>
public static class ModMenuUI
{
    // 视觉标准（调色板 / 尺寸 / 字号）集中在 ModMenuTheme；本文件只谈布局与状态
    private static Canvas _canvas;
    private static RectTransform _panel;
    private static ModMenuListView _list;
    private static TextMeshProUGUI _subtitle, _status, _detailName, _detailVersion, _detailHint;
    private static Button _toggleBtn;
    private static TextMeshProUGUI _toggleLabel;
    private static string _actionMsg = "";
    private static float _actionUntil;
    private static UiPill _pillSource, _pillFormat, _pillState;
    private static readonly List<Button> _chips = new();
    private static readonly List<TextMeshProUGUI> _chipTexts = new();
    private static readonly List<int> _chipHot = new();
    private static readonly List<DetailLine> _lines = new();

    /// <summary>
    /// 可点击热区（**自管指针命中**）。
    ///
    /// 为什么不依赖 uGUI 事件：实测（2026-09-12）任务场景里游戏自己的 `EventSystem` 是**未激活**的
    /// （`EventSystem.current == null`）→ 我们的按钮根本收不到点击，而游戏自己的“虚拟光标”照旧处理点击
    /// → 表现出来就是“点击穿透”。因此本模组自己用 `Mouse.current` + 矩形命中决定交互，
    /// 完全不依赖游戏 EventSystem（有 EventSystem 时也不重复触发，见 <see cref="ClickOnce"/>）。
    /// </summary>
    private sealed class Hot
    {
        public RectTransform Rt;
        public Image Bg;
        public Color Base, Hover;
        public Action Click;
    }

    private static readonly List<Hot> _hots = new();
    private static int _lastClickFrame = -1;
    private static int _lastHoverRow = -2;
    private static string _geomLoggedFor = "";
    // 自算边沿用的原始按下状态（菜单打开时我们在设备层吞掉了 wasPressedThisFrame，自己不能再读它）
    private static bool _f6Down, _lmbDown, _escDown;

    // 点击闸（T10 观察项修复）：按下连续帧数 / 按下时的目标 / 上一帧悬停目标 / 是否已观察到抬起 / 上次点击时间
    private static int _pressFrames, _pressRow, _pressHot, _lastHoverHot = -2;
    private static bool _released = true;
    private static float _lastClickAt = -10f;

    /// <summary>筛选 chip 顺序：null = 全部（其余为条目性质）。</summary>
    private static readonly EntryKind?[] _chipKinds = { null, EntryKind.Mod, EntryKind.Dependency, EntryKind.Disabled };

    private static ModEntryInfo[] _view = Array.Empty<ModEntryInfo>();
    private static string _selectedId = "";
    private static int _page, _filter;
    private static bool _built, _dirty = true;
    private static int _rightTab;                       // T6/T10：右栏页签 0=详情 1=设置 2=诊断
    private static RectTransform _settingsHost, _debugHost;
    private static Button _tabDetails, _tabSettings, _tabDebug;
    private static Button _orderUp, _orderDown;        // T8：顺序上移/下移
    private static float _clock, _nextRefresh, _nextGuard;

    /// <summary>右栏详情的一行（标签 + 值）：控件复用，纵向顺序排版。</summary>
    private sealed class DetailLine
    {
        public RectTransform LabelRt, ValueRt;
        public TextMeshProUGUI Label, Value;
    }

    /// <summary>菜单当前是否打开。</summary>
    public static bool IsOpen { get; private set; }

    // ---------------- 开关 ----------------

    /// <summary>切换显示（热键 / 入口按钮调用）。
    /// ⚠️ 环境里有 **OpenNestUIKit** 时，菜单由 UIKit 渲染（本模组只提供声明式页面）——
    /// 见 <see cref="UIKitIntegration"/>；没有 UIKit 才走本文件的自带覆盖 UI（回退）。</summary>
    public static void Toggle()
    {
        if (UIKitIntegration.TryToggleInUIKit())
        {
            CoopLog.Debug("modmenu.ui", () => "toggle → 交给 UIKit（本模组界面只作回退）");
            return;
        }
        CoopLog.Debug("modmenu.ui", () => $"toggle (from={(IsOpen ? "open" : "closed")})");
        if (IsOpen) Close(); else Open();
    }

    public static void Open()
    {
        try
        {
            EnsureBuilt();
            if (_canvas == null) return;
            IsOpen = true;
            _canvas.gameObject.SetActive(true);
            Refresh();
            UiInputGuard.Apply(_canvas);   // 压制其它画布的射线（防点击穿透，见 UiInputGuard）
            UiEscapeGuard.Block(true);      // T5：菜单打开时拦住游戏自己的 ESC 菜单
            UiInputGuard.LogProbe();       // 环境快照（EventSystem / 输入模块 / 光标画布）
            // 自绘指针只是**兜底**：正常情况下游戏的虚拟光标（32767）或系统硬件光标必定在我们（32766）之上，
            // 自绘会变成“双指针”（这就是之前日志里 sprite=<none: fallback> 的小白方块）。
            if (UiInputGuard.NeedsOwnPointer()) UiCursorOverlay.Show(_canvas);
            else UiCursorOverlay.Hide();
            CoopLog.Debug("modmenu.ui", () => $"opened (entries={ModInventory.Entries.Length})");
        }
        catch (Exception ex)
        {
            CoopLog.Error("modmenu.ui", () => $"open failed: {ex.Message}");
        }
    }

    public static void Close()
    {
        IsOpen = false;
        UiCursorOverlay.Hide();
        UiEscapeGuard.Block(false);   // T5：恢复游戏 ESC 菜单（关闭清理）
        UiInputGuard.Restore();   // 还原被压制的射线（先还原再隐藏，保证顺序无歧义）
        try { if (_canvas != null) _canvas.gameObject.SetActive(false); } catch { }
        CoopLog.Debug("modmenu.ui", () => "closed");
    }

    /// <summary>关停时销毁（<see cref="Core.ModMenuRuntime.Shutdown"/> 调用）。</summary>
    public static void Destroy()
    {
        UiCursorOverlay.Hide();
        UiEscapeGuard.Block(false);
        UiInputGuard.Restore();
        try { ModInventory.Changed -= OnInventoryChanged; } catch { }
        try { ModMenuLoc.Changed -= OnLocChanged; } catch { }
        try { ModRuntimeState.Changed -= OnInventoryChanged; } catch { }
        try { if (_canvas != null) UnityEngine.Object.Destroy(_canvas.gameObject); } catch { }
        _canvas = null; _panel = null; _list = null; _subtitle = null; _status = null;
        _detailName = null; _detailVersion = null; _detailHint = null;
        _toggleBtn = null; _toggleLabel = null;
        _chipTexts.Clear(); _chips.Clear(); _lines.Clear(); _view = Array.Empty<ModEntryInfo>();
        _hots.Clear(); _chipHot.Clear();
        _built = false; IsOpen = false; _selectedId = ""; _page = 0; _filter = 0;
    }

    // ---------------- 每帧 ----------------

    /// <summary>每帧驱动（<see cref="Core.ModMenuBehaviour.Update"/> 调用）：热键 + 节流刷新。</summary>
    public static void Tick(float dt)
    {
        _clock += dt;

        // 热键（新 Input System；旧 UnityEngine.Input 在该游戏被禁用）
        // ⚠️ 用 `isPressed` + 自算边沿：菜单打开时我们在设备层把 wasPressedThisFrame 吞掉了，自己也得走原始状态。
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            bool down = kb != null && kb.f6Key.isPressed;
            if (down && !_f6Down) Toggle();
            _f6Down = down;

            // T5：菜单打开时 ESC 关闭我们的菜单（游戏 ESC 菜单已被 UiEscapeGuard 拦住）
            if (IsOpen)
            {
                bool esc = kb != null && kb.escapeKey.isPressed;
                if (esc && !_escDown) Close();
                _escDown = esc;
            }
            else _escDown = false;
        }
        catch { }

        if (!IsOpen) return;

        UiInputGuard.TickLayerOrder();    // 每帧重申层级（我们在 32766、低于游戏光标层）
        ModMenuDebugView.Tick(dt);        // T10：诊断页可见时每秒重建
        HandlePointer();                  // 自管指针命中（不依赖游戏 EventSystem）

        // 菜单打开期间周期性重申输入压制（场景切换会新建画布/新 ESC 菜单实例）
        if (_clock >= _nextGuard)
        {
            _nextGuard = _clock + 1.5f;
            UiInputGuard.Apply(_canvas);
            UiEscapeGuard.Apply();
        }

        if (!_dirty || _clock < _nextRefresh) return;
        _dirty = false;
        _nextRefresh = _clock + 0.3f;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { Refresh(); }
        catch (Exception ex) { CoopLog.Warn("modmenu.ui", () => "refresh failed: " + ex.Message); }
        finally { try { FrameProfiler.Instance.AddMs("ui.refresh", sw.Elapsed.TotalMilliseconds); } catch { } }
    }

    private static void OnInventoryChanged() => _dirty = true;

    /// <summary>每帧后段驱动（<see cref="Core.ModMenuBehaviour.LateUpdate"/> 调用）：重申层级，压住游戏每帧写回的 sortingOrder。</summary>
    public static void TickLate()
    {
        if (!IsOpen) return;
        try { UiCursorOverlay.Tick(); } catch { }
        try { UiInputGuard.TickLayerOrder(); } catch { }
        try { UiEscapeGuard.TickFallback(); } catch { }
    }

    private static void OnLocChanged() => _dirty = true;
    private static void Noop() { }

    /// <summary>同一帧只允许触发一个点击（自管命中与 uGUI 点击可能同时到达时防重复）。</summary>
    private static void ClickOnce(Action a)
    {
        try
        {
            if (_lastClickFrame == Time.frameCount) return;
            _lastClickFrame = Time.frameCount;
            a?.Invoke();
        }
        catch { }
    }

    internal static void RegisterHot(Button btn, Color baseColor, Action onClick)
    {
        if (btn == null) return;
        try
        {
            _hots.Add(new Hot
            {
                Rt = btn.GetComponent<RectTransform>(),
                Bg = btn.GetComponent<Image>(),
                Base = baseColor,
                Hover = ModMenuTheme.Hover(baseColor),
                Click = () => ClickOnce(onClick),
            });
        }
        catch { }
    }

    /// <summary>每帧指针处理（菜单打开时）：悬停高亮 + 命中即点击。</summary>
    private static void HandlePointer()
    {
        try
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return;

            Vector2 pos;
            // 指针位置：优先游戏虚拟光标（玩家看到的就是它），退回原始鼠标
            if (!PointerPosition.TryGet(out pos)) pos = mouse.position.ReadValue();

            int hovered = -1;
            for (int i = 0; i < _hots.Count; i++)
            {
                var h = _hots[i];
                bool over = false;
                try
                {
                    over = h.Rt != null && h.Rt.gameObject.activeInHierarchy
                           && UnityEngine.RectTransformUtility.RectangleContainsScreenPoint(h.Rt, pos, null);
                }
                catch { }
                try { if (h.Bg != null) h.Bg.color = over ? h.Hover : h.Base; } catch { }
                if (over) hovered = i;
            }

            int row = -1;
            try { if (_list != null) row = _list.HitRowAt(pos); } catch { }
            try { _list?.SetHover(row); } catch { }

            if (row != _lastHoverRow)
            {
                float px = pos.x, py = pos.y;
                CoopLog.Debug("modmenu.ui", () => $"pointer: ({px:F0},{py:F0}) row={row} hot={hovered}");
            }

            bool clicked = false;
            try
            {
                // 主路径：原始按下状态 + 自算边沿（因为菜单打开时我们自己吞掉了 wasPressedThisFrame）
                bool down = mouse.leftButton.isPressed;

                // ⚠️ 实测观察项（T10 自测时发现）：菜单打开期间**每 ~1.5s 会凭空出现一次“点击”**（指针位置还在漂），
                //    实测会把列表选中项自己挑走（`click: hot=-1 row=6` → 0.3s 后 row=5 …）。
                //    来源判断：不是我们的吞输入（那层只让 wasPressedThisFrame 返回 false，不写状态），
                //    而更像游戏侧/其它模组往 Input System 写**合成**鼠标状态 → 裸边沿检测会把它当点击。
                //    对策 = 三道闸（真人点击天然满足，合成抖动几乎不可能同时满足）：
                //      ① 按下要**连续 2 帧**；② 上一帧就悬停在**同一个目标**上（指针不能刚跳过来）；
                //      ③ 距上次点击 ≥180ms，且必须观察到一次**抬起**才允许下一次点击。
                if (down && !_lmbDown) { _pressFrames = 1; _pressRow = row; _pressHot = hovered; }
                else if (down) _pressFrames++;
                else { _pressFrames = 0; _released = true; }

                clicked = _pressFrames == 2
                          && _pressRow == row && _lastHoverRow == row
                          && _pressHot == hovered && _lastHoverHot == hovered
                          && _released
                          && (Time.unscaledTime - _lastClickAt) > 0.18f;

                if (clicked) { _lastClickAt = Time.unscaledTime; _released = false; }
                _lmbDown = down;
            }
            catch { }
            finally
            {
                _lastHoverRow = row;
                _lastHoverHot = hovered;
            }
            if (!clicked) return;

            CoopLog.Info("modmenu.ui", () => $"click: hot={hovered} row={row} (press={_pressFrames}f, since_last={Time.unscaledTime - _lastClickAt:F2}s)");

            if (hovered >= 0)
            {
                var h = _hots[hovered];
                try { h.Click?.Invoke(); } catch { }
                return;
            }
            if (row >= 0) ClickOnce(() => OnRowClicked(row));
        }
        catch { }
    }

    /// <summary>当前筛选列表里选中的条目（无选中返回 null）。</summary>
    private static ModEntryInfo SelectedEntry()
    {
        int sel = IndexOfSelected();
        return sel >= 0 && sel < _view.Length ? _view[sel] : null;
    }

    /// <summary>启用 / 禁用当前选中的模组（改文件名；重启生效）。</summary>
    private static void ToggleSelected()
    {
        var e = SelectedEntry();
        if (e == null) return;

        string msg;
        bool ok = ModFileState.TryToggle(e, out msg);
        _actionMsg = msg;
        _actionUntil = _clock + 8f;
        if (ok)
        {
            ModInventory.RequestRefresh("toggle");
            ModMenuRegistry.RequestRescan("toggle");
        }
        _dirty = true;
    }

    // ---------------- 构建 ----------------

    private static void EnsureBuilt()
    {
        if (_built && _canvas != null) return;

        _canvas = UiKit.CreateCanvas("OpenNestModMenu", 32765, dontDestroy: true);
        var root = _canvas.transform;

        // ① 全屏压暗 + 射线拦截（只能拦住 sortingOrder 低于我们的画布）
        UiKit.MakeBlocker(root, ModMenuTheme.Backdrop);

        // ② 面板
        var panelImg = UiKit.MakeImage(root, ModMenuTheme.PanelX, ModMenuTheme.PanelY,
            ModMenuTheme.PanelW, ModMenuTheme.PanelH, ModMenuTheme.PanelBg, raycast: true);
        _panel = panelImg.rectTransform;

        // ③ 标题栏：左侧强调条 + 标题/副标题 + 关闭按钮
        UiKit.MakeImage(_panel, 0f, 0f, ModMenuTheme.PanelW, ModMenuTheme.HeaderH, ModMenuTheme.HeaderBg, raycast: true);
        UiKit.MakeImage(_panel, 0f, 0f, 4f, ModMenuTheme.HeaderH, ModMenuTheme.Accent);
        UiKit.MakeImage(_panel, 0f, ModMenuTheme.HeaderH - 1f, ModMenuTheme.PanelW, 1f, ModMenuTheme.Border);

        UiKit.MakeText(_panel, ModMenuLoc.L("Title"), 18f, 5f, 760f, 26f,
            ModMenuTheme.FontTitle, ModMenuTheme.TextPrimary, TextAlignmentOptions.Left);
        _subtitle = UiKit.MakeText(_panel, "", 18f, 30f, 940f, 16f,
            ModMenuTheme.FontSubtitle, ModMenuTheme.TextSecond, TextAlignmentOptions.Left);

        var closeBtn = UiKit.MakeButton(_panel, ModMenuLoc.L("Close"),
            ModMenuTheme.PanelW - 100f, 10f, 86f, 30f, Noop, ModMenuTheme.ButtonBg, ModMenuTheme.TextPrimary);
        ModMenuTheme.StyleButton(closeBtn, ModMenuTheme.ButtonBg);
        RegisterHot(closeBtn, ModMenuTheme.ButtonBg, Close);

        // 关闭按钮也是“自管命中”的一部分，避免依赖游戏 EventSystem（见 Hot 注释）

        // ④ 左栏：筛选 chip + 列表 + 翻页工具条
        var leftImg = UiKit.MakeImage(_panel, ModMenuTheme.Pad, ModMenuTheme.BodyY, ModMenuTheme.ColLeftW,
            ModMenuTheme.BodyH, ModMenuTheme.CardBg, raycast: true);
        var left = leftImg.rectTransform;

        const float chipW = 88f, chipGap = 6f, chipH = 26f;
        for (int i = 0; i < _chipKinds.Length; i++)
        {
            int captured = i;
            var btn = UiKit.MakeButton(left, "", 10f + i * (chipW + chipGap), 10f, chipW, chipH,
                Noop, ModMenuTheme.ChipBg, ModMenuTheme.TextSecond);
            ModMenuTheme.StyleButton(btn, ModMenuTheme.ChipBg);
            var txt = btn.transform.GetChild(0).GetComponent<TextMeshProUGUI>();
            try { txt.fontSize = ModMenuTheme.FontChip; } catch { }
            _chips.Add(btn);
            _chipTexts.Add(txt);
            _chipHot.Add(_hots.Count);
            RegisterHot(btn, ModMenuTheme.ChipBg, () => SelectFilter(captured));
        }

        // 列表区：chip 行之下、工具条之上
        _list = new ModMenuListView(left, 6f, 44f, ModMenuTheme.ColLeftW - 16f, ModMenuTheme.BodyH - 44f - 40f);
        _list.OnRowClicked += OnRowClicked;

        float barY = ModMenuTheme.BodyH - 32f;
        var prevBtn = UiKit.MakeButton(left, "<", 10f, barY, 46f, 26f, Noop, ModMenuTheme.ButtonBg, ModMenuTheme.TextPrimary);
        var nextBtn = UiKit.MakeButton(left, ">", 60f, barY, 46f, 26f, Noop, ModMenuTheme.ButtonBg, ModMenuTheme.TextPrimary);
        ModMenuTheme.StyleButton(prevBtn, ModMenuTheme.ButtonBg);
        ModMenuTheme.StyleButton(nextBtn, ModMenuTheme.ButtonBg);
        RegisterHot(prevBtn, ModMenuTheme.ButtonBg, () => ChangePage(-1));
        RegisterHot(nextBtn, ModMenuTheme.ButtonBg, () => ChangePage(+1));

        var rescan = UiKit.MakeButton(left, ModMenuLoc.L("Rescan"), 114f, barY, 150f, 26f,
            Noop, ModMenuTheme.ButtonBg, ModMenuTheme.TextPrimary);
        ModMenuTheme.StyleButton(rescan, ModMenuTheme.ButtonBg);
        RegisterHot(rescan, ModMenuTheme.ButtonBg, () =>
        {
            ModMenuRegistry.RequestRescan("ui");
            ModInventory.RequestRefresh("ui");
        });

        // ⑤ 右栏：详情卡片（标题 + 来源/格式/状态 pill + 标签值行）
        var rightImg = UiKit.MakeImage(_panel, ModMenuTheme.Pad + ModMenuTheme.ColLeftW + ModMenuTheme.Gap,
            ModMenuTheme.BodyY, ModMenuTheme.ColRightW, ModMenuTheme.BodyH, ModMenuTheme.CardBg, raycast: true);
        var right = rightImg.rectTransform;

        _detailName = UiKit.MakeText(right, "", ModMenuTheme.Pad, 12f, 480f, 26f,
            ModMenuTheme.FontDetailName, ModMenuTheme.TextPrimary, TextAlignmentOptions.Left);
        _detailVersion = UiKit.MakeText(right, "", 500f, 16f, ModMenuTheme.ColRightW - 520f, 20f,
            ModMenuTheme.FontSubtitle, ModMenuTheme.TextSecond, TextAlignmentOptions.Left);

        _pillSource = ModMenuTheme.MakePill(right, ModMenuTheme.Pad, 44f, 130f, ModMenuTheme.PillH);
        _pillFormat = ModMenuTheme.MakePill(right, ModMenuTheme.Pad + 136f, 44f, 150f, ModMenuTheme.PillH);
        _pillState = ModMenuTheme.MakePill(right, ModMenuTheme.Pad + 292f, 44f, 130f, ModMenuTheme.PillH);

        // 启用 / 禁用（T7）：与 pill 同行的右侧按钮（重启生效）
        _toggleBtn = UiKit.MakeButton(right, "", ModMenuTheme.ColRightW - ModMenuTheme.Pad - 176f, 44f, 176f, ModMenuTheme.PillH,
            Noop, ModMenuTheme.ButtonBg, ModMenuTheme.TextPrimary);
        ModMenuTheme.StyleButton(_toggleBtn, ModMenuTheme.ButtonBg);
        RegisterHot(_toggleBtn, ModMenuTheme.ButtonBg, ToggleSelected);
        _toggleLabel = _toggleBtn.transform.GetChild(0).GetComponent<TextMeshProUGUI>();
        try
        {
            _toggleLabel.fontSize = ModMenuTheme.FontPill;
            _toggleLabel.enableWordWrapping = false;
            _toggleLabel.overflowMode = TMPro.TextOverflowModes.Ellipsis;
        }
        catch { }
        _toggleBtn.gameObject.SetActive(false);

        UiKit.MakeImage(right, ModMenuTheme.Pad, 72f, ModMenuTheme.ColRightW - ModMenuTheme.Pad * 2f, 1f, ModMenuTheme.Border);

        // 行池：14 行（Id/作者/状态/运行中/宿主/格式/顺序/优先级/依赖/不兼容/配置/设置页/路径/备注）
        for (int i = 0; i < 14; i++)
        {
            var line = new DetailLine();
            // 两列都用 TopLeft：之前标签用 Left（垂直居中）、值用 TopLeft → 同一行看起来会错位
            line.Label = UiKit.MakeText(right, "", ModMenuTheme.Pad, 0f, 92f, ModMenuTheme.LineH,
                ModMenuTheme.FontLabel, ModMenuTheme.TextDim, TextAlignmentOptions.TopLeft);
            line.Value = UiKit.MakeText(right, "", ModMenuTheme.Pad + 96f, 0f,
                ModMenuTheme.ColRightW - ModMenuTheme.Pad * 2f - 96f, ModMenuTheme.LineH,
                ModMenuTheme.FontValue, ModMenuTheme.TextPrimary, TextAlignmentOptions.TopLeft);
            line.LabelRt = line.Label.rectTransform;
            line.ValueRt = line.Value.rectTransform;
            _lines.Add(line);
        }

        _detailHint = UiKit.MakeText(right, "", ModMenuTheme.Pad, 106f,
            ModMenuTheme.ColRightW - ModMenuTheme.Pad * 2f, 80f,
            ModMenuTheme.FontValue, ModMenuTheme.TextSecond, TextAlignmentOptions.TopLeft);

        // T6：右栏两个页签（详情 / 设置）+ 设置页宿主（内容从 y=106 开始）
        _tabDetails = UiKit.MakeButton(right, ModMenuLoc.L("TabDetails"), ModMenuTheme.Pad, 78f, 76f, 22f,
            Noop, ModMenuTheme.ChipOn, ModMenuTheme.TextPrimary);
        _tabSettings = UiKit.MakeButton(right, ModMenuLoc.L("TabSettings"), ModMenuTheme.Pad + 80f, 78f, 76f, 22f,
            Noop, ModMenuTheme.ChipBg, ModMenuTheme.TextSecond);
        // T10：第三个页签（诊断面板）
        _tabDebug = UiKit.MakeButton(right, ModMenuLoc.L("TabDebug"), ModMenuTheme.Pad + 160f, 78f, 76f, 22f,
            Noop, ModMenuTheme.ChipBg, ModMenuTheme.TextSecond);
        RegisterHot(_tabDetails, ModMenuTheme.ChipOn, () => SelectRightTab(0));
        RegisterHot(_tabSettings, ModMenuTheme.ChipBg, () => SelectRightTab(1));
        RegisterHot(_tabDebug, ModMenuTheme.ChipBg, () => SelectRightTab(2));
        try
        {
            _tabDetails.transform.GetChild(0).GetComponent<TextMeshProUGUI>().fontSize = ModMenuTheme.FontChip;
            _tabSettings.transform.GetChild(0).GetComponent<TextMeshProUGUI>().fontSize = ModMenuTheme.FontChip;
            _tabDebug.transform.GetChild(0).GetComponent<TextMeshProUGUI>().fontSize = ModMenuTheme.FontChip;
        }
        catch { }

        var hostGo = new GameObject("SettingsHost");
        hostGo.transform.SetParent(right, false);
        _settingsHost = hostGo.AddComponent<RectTransform>();
        _settingsHost.anchorMin = _settingsHost.anchorMax = new Vector2(0f, 1f);
        _settingsHost.pivot = new Vector2(0f, 1f);
        _settingsHost.anchoredPosition = new Vector2(0f, -106f);
        _settingsHost.sizeDelta = new Vector2(ModMenuTheme.ColRightW, ModMenuTheme.BodyH - 106f);
        ModMenuSettingsView.Build(_settingsHost);

        // T10：诊断页宿主（与设置页同位置；两边互斥显示）
        var dbgGo = new GameObject("DebugHost");
        dbgGo.transform.SetParent(right, false);
        _debugHost = dbgGo.AddComponent<RectTransform>();
        _debugHost.anchorMin = _debugHost.anchorMax = new Vector2(0f, 1f);
        _debugHost.pivot = new Vector2(0f, 1f);
        _debugHost.anchoredPosition = new Vector2(0f, -106f);
        _debugHost.sizeDelta = new Vector2(ModMenuTheme.ColRightW, ModMenuTheme.BodyH - 106f);
        ModMenuDebugView.Build(_debugHost);

        // T8：统一顺序调整（只在“详情”页、且条目在有效顺序里时出现）
        _orderUp = UiKit.MakeButton(right, ModMenuLoc.L("OrderUp"), ModMenuTheme.Pad, 516f, 96f, 22f,
            Noop, ModMenuTheme.ButtonBg, ModMenuTheme.TextPrimary);
        ModMenuTheme.StyleButton(_orderUp, ModMenuTheme.ButtonBg);
        _orderDown = UiKit.MakeButton(right, ModMenuLoc.L("OrderDown"), ModMenuTheme.Pad + 100f, 516f, 96f, 22f,
            Noop, ModMenuTheme.ButtonBg, ModMenuTheme.TextPrimary);
        ModMenuTheme.StyleButton(_orderDown, ModMenuTheme.ButtonBg);
        RegisterHot(_orderUp, ModMenuTheme.ButtonBg, () => MoveOrder(-1));
        RegisterHot(_orderDown, ModMenuTheme.ButtonBg, () => MoveOrder(+1));
        _orderUp.gameObject.SetActive(false);
        _orderDown.gameObject.SetActive(false);

        // ⑥ 底栏状态
        _status = UiKit.MakeText(_panel, "", ModMenuTheme.Pad, ModMenuTheme.PanelH - 24f,
            ModMenuTheme.PanelW - ModMenuTheme.Pad * 2f, 20f, ModMenuTheme.FontStatus,
            ModMenuTheme.TextSecond, TextAlignmentOptions.Left);

        ModInventory.Changed += OnInventoryChanged;
        ModMenuLoc.Changed += OnLocChanged;
        ModRuntimeState.Changed += OnInventoryChanged;
        _built = true;
    }

    // ---------------- 交互 ----------------

    private static void SelectFilter(int index)
    {
        if (index < 0 || index >= _chipKinds.Length || index == _filter) return;
        _filter = index;
        _page = 0;
        _dirty = true;
    }

    private static void OnRowClicked(int rowInPage)
    {
        int index = _page * _list.MaxRows + rowInPage;
        if (index < 0 || index >= _view.Length) return;
        _selectedId = _view[index].Id ?? "";
        _dirty = true;
    }

    private static void ChangePage(int delta)
    {
        int pages = PageCount(_view.Length);
        int np = _page + delta;
        if (np < 0) np = 0;
        if (np > pages - 1) np = pages - 1;
        if (np == _page) return;
        _page = np;
        _dirty = true;
    }

    private static int PageCount(int count) => Mathf.Max(1, Mathf.CeilToInt((float)count / Mathf.Max(1, _list.MaxRows)));

    // ---------------- 渲染 ----------------

    private static void Refresh()
    {
        var all = ModInventory.Entries;

        // ① 筛选：依赖库不再混进模组列表（实测反馈：一屏全是依赖）
        var kind = _chipKinds[_filter];
        var view = new List<ModEntryInfo>();
        int cMod = 0, cDep = 0, cOff = 0;
        for (int i = 0; i < all.Length; i++)
        {
            var e = all[i];
            var k = ModMenuDisplay.KindOf(e);
            if (k == EntryKind.Mod) cMod++; else if (k == EntryKind.Dependency) cDep++; else cOff++;
            if (kind == null || k == kind) view.Add(e);
        }
        _view = view.ToArray();

        // ② 选中项可能被筛掉 → 回落到第一条
        int sel = IndexOfSelected();
        if (sel < 0)
        {
            sel = _view.Length > 0 ? 0 : -1;
            _selectedId = sel >= 0 ? (_view[sel].Id ?? "") : "";
        }

        // ③ 分页
        int pages = PageCount(_view.Length);
        if (_page > pages - 1) _page = pages - 1;
        if (_page < 0) _page = 0;

        // ④ 当前页切片
        int start = _page * _list.MaxRows;
        var slice = new List<ModEntryInfo>();
        for (int i = start; i < _view.Length && slice.Count < _list.MaxRows; i++) slice.Add(_view[i]);
        int selRow = (sel >= start && sel < start + slice.Count) ? sel - start : -1;
        _list.SetRows(slice, selRow);
        _list.SetPageInfo(_page + 1, pages, _view.Length);

        // ⑤ chip 文案（带计数）+ 高亮
        string[] labels = { ModMenuLoc.L("ChipAll"), ModMenuLoc.L("ChipMods"),
                            ModMenuLoc.L("ChipDeps"), ModMenuLoc.L("ChipOff") };
        int[] counts = { all.Length, cMod, cDep, cOff };
        for (int i = 0; i < _chips.Count; i++)
        {
            try
            {
                _chipTexts[i].text = labels[i] + " " + counts[i];
                _chipTexts[i].color = i == _filter ? ModMenuTheme.TextPrimary : ModMenuTheme.TextSecond;
                var img = _chips[i].targetGraphic as Image;
                if (img != null) img.color = i == _filter ? ModMenuTheme.ChipOn : ModMenuTheme.ChipBg;

                // 热区底色跟着 chip 选中态走（自管指针命中会每帧回写颜色）
                if (i < _chipHot.Count)
                {
                    int hi = _chipHot[i];
                    if (hi >= 0 && hi < _hots.Count)
                    {
                        _hots[hi].Base = i == _filter ? ModMenuTheme.ChipOn : ModMenuTheme.ChipBg;
                        _hots[hi].Hover = ModMenuTheme.Hover(_hots[hi].Base);
                    }
                }
            }
            catch { }
        }

        // ⑥ 详情 + 底栏
        RenderDetail(sel >= 0 ? _view[sel] : null);
        _status.text = _clock < _actionUntil && !string.IsNullOrEmpty(_actionMsg) ? _actionMsg : StatusText(all, _view.Length);
        _subtitle.text = SubtitleText();
    }

    private static int IndexOfSelected()
    {
        if (string.IsNullOrEmpty(_selectedId)) return -1;
        for (int i = 0; i < _view.Length; i++)
            if (string.Equals(_view[i].Id, _selectedId, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>T6/T10：切右栏页签（0=详情 1=设置 2=诊断）；置脏让下一次刷新重渲右栏。</summary>
    private static void SelectRightTab(int tab)
    {
        if (_rightTab == tab) return;
        _rightTab = tab;
        _dirty = true;
        try
        {
            var btns = new Button[] { _tabDetails, _tabSettings, _tabDebug };
            for (int i = 0; i < btns.Length; i++)
            {
                if (btns[i] == null) continue;
                bool on = i == tab;
                var color = on ? ModMenuTheme.ChipOn : ModMenuTheme.ChipBg;
                var img = btns[i].targetGraphic as Image;
                if (img != null) img.color = color;
                try { btns[i].transform.GetChild(0).GetComponent<TextMeshProUGUI>().color = on ? ModMenuTheme.TextPrimary : ModMenuTheme.TextSecond; } catch { }

                // 页签热区底色跟着选中态走（自管命中会每帧回写底色）
                var rt = btns[i].GetComponent<RectTransform>();
                for (int h = 0; h < _hots.Count; h++)
                {
                    if (_hots[h].Rt != rt) continue;
                    _hots[h].Base = color;
                    _hots[h].Hover = ModMenuTheme.Hover(color);
                }
            }
        }
        catch { }
    }

    /// <summary>T8：上移/下移选中条目（写回统一顺序表 + 重算有效顺序 + 底栏提示）。</summary>
    private static void MoveOrder(int dir)
    {
        try
        {
            var e = SelectedEntry();
            if (e == null) { SetStatusText(ModMenuLoc.L("ToggleNoEntry")); return; }

            string msg;
            bool ok = ModInitScheduler.Move(e.Id, dir, out msg);
            SetStatusText(msg);
            if (ok) _dirty = true;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.order", () => "move from UI failed: " + ex.Message);
        }
    }

    /// <summary>底栏操作提示（设置页写回结果也走这里）。</summary>
    internal static void SetStatusText(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return;
        _actionMsg = msg;
        _actionUntil = _clock + 8f;
    }

    // ---------------- TEMP-VERIFY：自动化取证入口（-onnmm-selftest-t56） ----------------
    // 只给自测开关调用；正常路径不经过这里。

    /// <summary>选中 Id 含 <paramref name="match"/> 的第一条并立即重渲；返回实际选中的 Id（无匹配 = 空）。</summary>
    internal static string SelfTestSelect(string match)
    {
        try
        {
            if (string.IsNullOrEmpty(match)) return "";
            var all = ModInventory.Entries;
            string hit = "";
            for (int i = 0; i < all.Length; i++)
            {
                var id = all[i]?.Id;
                if (!string.IsNullOrEmpty(id) && id.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0) { hit = id; break; }
            }
            if (hit.Length == 0) return "";
            _selectedId = hit;
            _filter = 0;
            _page = 0;
            _dirty = true;
            _nextRefresh = 0f;      // 跳过节流，当场重渲
            Refresh();
            return hit;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.ui", () => "selftest select failed: " + ex.Message);
            return "";
        }
    }

    /// <summary>T6/T10：切换到右栏页签（0=详情 1=设置 2=诊断）并立即重渲。</summary>
    internal static void SelfTestTab(int tab)
    {
        try
        {
            SelectRightTab(tab);
            _dirty = true;
            _nextRefresh = 0f;
            Refresh();
        }
        catch { }
    }

    /// <summary>当前界面状态一行摘要（页签 / 选中项 / 底栏文案）。</summary>
    internal static string SelfTestState()
        => $"open={IsOpen} tab={_rightTab} selected='{_selectedId}' view={_view.Length} status='{_status?.text}'";

    /// <summary>
    /// TEMP-VERIFY：当前页第 <paramref name="rowIndex"/> 行（0 起）中心的**屏幕坐标**（Unity 坐标系，左下原点）。
    /// 自测钩子据此把真实 OS 点击注入到那一行，验证点击闸不会误杀真点击。
    /// </summary>
    internal static bool SelfTestRowScreenPoint(int rowIndex, out float x, out float y)
    {
        x = y = 0f;
        try
        {
            var rt = _list?.RowRect(rowIndex);
            if (rt == null) return false;
            var p = UnityEngine.RectTransformUtility.WorldToScreenPoint(null, rt.position);
            x = p.x; y = p.y;
            return true;
        }
        catch { return false; }
    }

    /// <summary>TEMP-VERIFY：当前页第 n 行对应的条目 Id（供自测核对点击后是否真的选中了它）。</summary>
    internal static string SelfTestRowId(int rowIndex)
    {
        try
        {
            int start = _page * (_list?.MaxRows ?? 0);
            int idx = start + rowIndex;
            return idx >= 0 && idx < _view.Length ? (_view[idx]?.Id ?? "") : "";
        }
        catch { return ""; }
    }

    /// <summary>右栏页眉（名称 / 版本 / 来源+格式+状态 pill / 启停按钮）——详情页与设置页共用。</summary>
    private static void RenderHeader(ModEntryInfo e)
    {
        try
        {
            _detailName.text = string.IsNullOrEmpty(e.DisplayName) ? (e.Id ?? "") : e.DisplayName;
            _detailVersion.text = string.IsNullOrEmpty(e.Version) ? "" : "v" + e.Version;
        }
        catch { }

        // 启用 / 禁用按钮：仅当该条目真的对应磁盘文件（且不是菜单自己）时出现
        try
        {
            string reason;
            bool can = ModFileState.CanToggle(e, out reason);
            _toggleBtn.gameObject.SetActive(can);
            if (can)
            {
                string label;
                if (e.Enabled)
                {
                    // 能当场停（ML 模组 / 受管模组）→ 说“立即”；BepInEx 插件只能“软停用”（实验性）；否则老实说“重启生效”
                    label = ModFileState.IsSoftStopOnly(e)
                        ? ModMenuLoc.L("BtnDisableExperimental")
                        : ModFileState.CanStopAtRuntime(e)
                            ? ModMenuLoc.L("BtnDisableNow")
                            : ModMenuLoc.L("BtnDisable") + " " + ModMenuLoc.L("BtnRestartHint");
                }
                else
                {
                    // 能立即启用（本会话停过的 / 文件刚启用可热加载）→ 说“立即”；否则老实说“重启生效”
                    label = ModFileState.CanStartAtRuntime(e)
                        ? ModMenuLoc.L("BtnEnableNow")
                        : ModMenuLoc.L("BtnEnable") + " " + ModMenuLoc.L("BtnRestartHint");
                }
                _toggleLabel.text = label;
            }
        }
        catch { }

        _pillSource.SetActive(true);
        _pillFormat.SetActive(true);
        _pillState.SetActive(true);
        _pillSource.Set(ModMenuDisplay.PillLabel(e), ModMenuDisplay.PillColor(e));
        _pillFormat.Set(ModMenuDisplay.FormatLabel(e), ModMenuTheme.PillUnknown);
        _pillState.Set(ModMenuDisplay.StateText(e), ModMenuTheme.PillUnknown);
        try { _pillState.Text.color = ModMenuDisplay.StateColor(e); } catch { }
    }

    /// <summary>右栏详情渲染（行池复用，纵向顺序排版）。</summary>
    private static void RenderDetail(ModEntryInfo e)
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            try { _lines[i].LabelRt.gameObject.SetActive(false); _lines[i].ValueRt.gameObject.SetActive(false); }
            catch { }
        }

        if (e == null)
        {
            _detailName.text = "";
            _detailVersion.text = "";
            _pillSource.SetActive(false);
            _pillFormat.SetActive(false);
            _pillState.SetActive(false);
            try { _toggleBtn.gameObject.SetActive(false); } catch { }
            ModMenuSettingsView.Clear();
            ModMenuDebugView.SetVisible(false);
            try { _orderUp.gameObject.SetActive(false); _orderDown.gameObject.SetActive(false); } catch { }
            _detailHint.gameObject.SetActive(true);
            _detailHint.text = ModMenuLoc.L("HintSelect");
            return;
        }

        // T6/T10：非“详情”页签时，右栏内容交给设置页 / 诊断页（标题/pill/启停按钮保留）
        if (_rightTab != 0)
        {
            _detailHint.gameObject.SetActive(false);
            try { _orderUp.gameObject.SetActive(false); _orderDown.gameObject.SetActive(false); } catch { }
            if (_rightTab == 1) ModMenuSettingsView.Bind(e);
            else ModMenuSettingsView.SetVisible(false);
            ModMenuDebugView.SetVisible(_rightTab == 2);
            RenderHeader(e);
            return;
        }

        ModMenuSettingsView.SetVisible(false);
        ModMenuDebugView.SetVisible(false);
        _detailHint.gameObject.SetActive(false);
        try
        {
            bool canOrder = ModInitScheduler.CanReorder(e.Id);
            _orderUp.gameObject.SetActive(canOrder);
            _orderDown.gameObject.SetActive(canOrder);
        }
        catch { }
        RenderHeader(e);

        int line = 0;
        float y = 106f;
        EmitLine(ref line, ref y, ModMenuLoc.L("DetailId"), e.Id, 1, ModMenuTheme.TextPrimary);
        if (!string.IsNullOrEmpty(e.Author))
            EmitLine(ref line, ref y, ModMenuLoc.L("DetailAuthor"), e.Author, 1, ModMenuTheme.TextPrimary);
        EmitLine(ref line, ref y, ModMenuLoc.L("DetailState"), ModMenuDisplay.StateText(e), 1, ModMenuDisplay.StateColor(e));
        ModRuntimeState.Info rtInfo = null;
        try { ModRuntimeState.TryGet(e.Id, out rtInfo); } catch { }
        EmitLine(ref line, ref y, ModMenuLoc.L("DetailRuntime"),
            rtInfo == null ? ModMenuLoc.L("RuntimeRunning") : ModMenuLoc.L(ModRuntimeState.LabelKey(rtInfo.Kind)),
            1, rtInfo == null ? ModMenuTheme.Ok : ModRuntimeState.ColorOf(rtInfo.Kind));
        EmitLine(ref line, ref y, ModMenuLoc.L("DetailHost"), ModMenuDisplay.HostLabel(e), 1, ModMenuTheme.TextPrimary);
        EmitLine(ref line, ref y, ModMenuLoc.L("DetailFormat"), ModMenuDisplay.FormatLabel(e), 1, ModMenuTheme.TextPrimary);
        if (e.Priority != 0)
            EmitLine(ref line, ref y, ModMenuLoc.L("DetailPriority"),
                e.Priority + ModMenuLoc.L("DetailPriorityNote"), 1, ModMenuTheme.TextPrimary);
        // T8：统一顺序（#n/总数 · 用户指定 / 加载器决定）
        EmitLine(ref line, ref y, ModMenuLoc.L("DetailOrder"), ModInitScheduler.Describe(e), 1,
            ModInitScheduler.IsUserOrdered(e.Id) ? ModMenuTheme.Ok : ModMenuTheme.TextSecond);
        if (e.DependsOn.Length > 0)
            EmitLine(ref line, ref y, ModMenuLoc.L("DetailDeps"), string.Join(", ", e.DependsOn), 2, ModMenuTheme.TextSecond);
        if (e.IncompatibleWith.Length > 0)
            EmitLine(ref line, ref y, ModMenuLoc.L("DetailIncompat"), string.Join(", ", e.IncompatibleWith), 2, ModMenuTheme.Warn);
        try
        {
            string cfg = e.ConfigFile;
            var found = ConfigLocator.Find(e);
            if (!string.IsNullOrEmpty(found)) cfg = found;
            if (!string.IsNullOrEmpty(cfg))
                EmitLine(ref line, ref y, ModMenuLoc.L("DetailConfig"), System.IO.Path.GetFileName(cfg), 1, ModMenuTheme.TextPrimary);
        }
        catch { }

        bool provider = e.Managed || ModMenuRegistry.TryGet(e.Id, out _);
        EmitLine(ref line, ref y, ModMenuLoc.L("DetailSettings"),
            provider ? ModMenuLoc.L("DetailSettingsOn") : ModMenuLoc.L("DetailSettingsOff"),
            1, provider ? ModMenuTheme.Ok : ModMenuTheme.TextDim);

        if (!string.IsNullOrEmpty(e.Path))
            EmitLine(ref line, ref y, ModMenuLoc.L("DetailPath"), e.Path, 2, ModMenuTheme.TextDim);
        if (!string.IsNullOrEmpty(e.Note))
            EmitLine(ref line, ref y, ModMenuLoc.L("DetailNote"), e.Note, 2, ModMenuTheme.TextDim);

        LogGeometryOnce(e);
    }

    /// <summary>每个选中项只记一次几何信息（用于实测取证：面板缩放/屏幕尺寸/启用禁用按钮位置）。</summary>
    private static void LogGeometryOnce(ModEntryInfo e)
    {
        string id = e?.Id ?? "";
        if (id == _geomLoggedFor) return;
        _geomLoggedFor = id;
        try
        {
            float sf = _canvas != null ? _canvas.scaleFactor : 1f;
            string btn = "hidden";
            if (_toggleBtn != null && _toggleBtn.gameObject.activeSelf)
            {
                var rt = _toggleBtn.GetComponent<RectTransform>();
                var p = rt.position;
                btn = $"pos=({p.x:F0},{p.y:F0}) size=({rt.rect.width * sf:F0}x{rt.rect.height * sf:F0}) text='{_toggleLabel?.text}' clickable={ModFileState.CanToggle(e, out _)}";
            }
            CoopLog.Info("modmenu.ui", () => $"geometry: screen={Screen.width}x{Screen.height} scale={sf:F3} toggle={btn}");
        }
        catch { }
    }

    /// <summary>写一行（标签 + 值）。<paramref name="lines"/> = 该值占几行（长文本 2 行）。</summary>
    private static void EmitLine(ref int line, ref float y, string label, string value, int lines, Color valueColor)
    {
        if (line >= _lines.Count) return;
        var l = _lines[line++];
        float h = ModMenuTheme.LineH * lines;
        float vy = y;
        float lh = h;
        try
        {
            l.LabelRt.gameObject.SetActive(true);
            l.ValueRt.gameObject.SetActive(true);
            ModMenuTheme.SetTopLeft(l.LabelRt, ModMenuTheme.Pad, vy);
            ModMenuTheme.SetTopLeft(l.ValueRt, ModMenuTheme.Pad + 96f, vy);
            l.LabelRt.sizeDelta = new Vector2(92f, lh);
            l.ValueRt.sizeDelta = new Vector2(ModMenuTheme.ColRightW - ModMenuTheme.Pad * 2f - 96f, lh);
            l.Label.text = label;
            l.Value.text = string.IsNullOrEmpty(value) ? ModMenuLoc.L("DetailNone") : value;
            l.Value.color = valueColor;
            l.Value.enableWordWrapping = lines > 1;
            l.Value.overflowMode = TMPro.TextOverflowModes.Ellipsis;
        }
        catch { }
        y += h + 2f;
    }

    private static string SubtitleText()
    {
        var l = LoaderDetector.Current;
        return l.HostLabel + "  ·  API " + ModMenuHost.ApiVersion
             + "  ·  " + ModMenuLoc.L("SubtitleHint");
    }

    private static string StatusText(ModEntryInfo[] all, int shown)
    {
        var l = LoaderDetector.Current;
        return ModMenuLoc.L("FooterLoaded") + " " + ModInventory.LoadedCount + "/" + all.Length
             + "   " + ModMenuLoc.L("FooterShown") + " " + shown
             + "   " + ModMenuLoc.L("FooterDisabled") + " " + ModInventory.DisabledCount
             + "   " + ModMenuLoc.L("FooterDup") + " " + ModInventory.DuplicateCount
             + "   " + ModMenuLoc.L("FooterDeps") + " " + ModInventory.DependencyCount
             + "   " + ModMenuLoc.L("FooterProviders") + " " + ModMenuRegistry.Count
             + "   interop=" + l.InteropSource
             + "   " + ModMenuLoc.L("FooterRefreshed") + " " + ModInventory.LastRefreshAt
             + "   " + ModMenuLoc.L("FooterToggle");
    }
}
