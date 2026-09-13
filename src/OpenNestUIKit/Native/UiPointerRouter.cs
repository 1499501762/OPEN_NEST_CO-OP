using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestUIKit.Native;

/// <summary>
/// 可点击热区（自管指针命中）。**不使用 UGUI 事件系统** ——
/// 实测（`docs/MOD_MENU.md` §八）：任务场景里游戏自己的 `EventSystem` 常为**未激活**，
/// 我们的 uGUI 按钮根本收不到点击，而游戏自己的虚拟光标照旧处理输入 → 表现为"点击穿透"。
/// 因此本库所有交互（按钮/开关/滑条/列表滚动/文本输入聚焦）都走这里的命中判定。
/// </summary>
public sealed class UiHotZone
{
    /// <summary>诊断用名字（如 "btn:确定"）。</summary>
    public string Name = "";

    /// <summary>命中矩形。</summary>
    public RectTransform Rect;

    /// <summary>是否参与命中（禁用态置 false）。</summary>
    public bool Enabled = true;

    /// <summary>点击（按下+抬起都在同一热区内）。</summary>
    public Action OnClick;

    /// <summary>悬停进入/离开。</summary>
    public Action OnEnter, OnExit;

    /// <summary>按下 / 抬起。</summary>
    public Action OnPress, OnRelease;

    /// <summary>按下（带局部坐标）；返回 true = 开始拖拽（滑条用）。</summary>
    public Func<Vector2, bool> OnPressLocal;

    /// <summary>拖拽中 / 拖拽结束（参数为局部坐标）。</summary>
    public Action<Vector2> OnDrag, OnDragEnd;

    /// <summary>滚轮（参数为滚轮增量，正 = 向上）。</summary>
    public Action<float> OnScroll;

    /// <summary>背景图（用于自动套用悬停/按下色）。</summary>
    public Image Bg;

    /// <summary>底色 / 悬停色 / 按下色（Tint=true 时自动套用）。</summary>
    public Color BaseColor = Color.white, HoverColor = Color.white, PressColor = Color.white;

    /// <summary>是否自动套用颜色。</summary>
    public bool Tint;

    /// <summary>裁剪矩形（列表行用：超出视口的热区不响应，避免滚出视口的行还能点到）。</summary>
    public Func<Rect> Clip;

    /// <summary>归属者（便于批量注销；可为 null）。</summary>
    public object Owner;

    /// <summary>当前是否被悬停（只读，诊断/自绘用）。</summary>
    public bool Hovered { get; internal set; }

    /// <summary>当前是否被按住。</summary>
    public bool Pressed { get; internal set; }

    /// <summary>设底色并刷新（保留当前悬停/按下态）。</summary>
    public void SetBaseColor(Color c)
    {
        BaseColor = c;
        HoverColor = Theme.UiTheme.Hover(c);
        PressColor = Theme.UiTheme.Pressed(c);
        ApplyColor();
    }

    internal void ApplyColor()
    {
        if (!Tint || Bg == null) return;
        try { Bg.color = Pressed ? PressColor : (Hovered ? HoverColor : BaseColor); } catch { }
    }
}

/// <summary>
/// 自管指针路由：每帧做一次"命中测试 → 悬停/按下/点击/拖拽/滚轮"分发。
/// 只在菜单打开（<see cref="Activate"/>）时工作；关闭后立即停止并清空状态。
///
/// 命中优先级：**后登记的热区在上层**（与 UGUI 的 sibling 顺序一致：控件先建父再建子，
/// 子控件后登记 → 优先命中）。
/// </summary>
public static class UiPointerRouter
{
    private static readonly List<UiHotZone> _zones = new();
    private static UiHotZone _hover, _press, _drag;
    private static UiHotZone _dragCandidate;      // 按下时记下的“可滚动上级”（行盖在视口上时，移动后转为滚动）
    private static Vector2 _pressPos;
    private static bool _wasDown;
    private static string _lastDiag = "";
    private static float _diagT;

    /// <summary>菜单打开时置 true（关闭后停止一切分发）。</summary>
    public static bool Active { get; private set; }

    /// <summary>是否正在拖拽（滑条/滚动条用）。</summary>
    public static bool Dragging => _drag != null;

    /// <summary>当前悬停热区（可空）。</summary>
    public static UiHotZone Hovered => _hover;

    /// <summary>有输入框聚焦时置 true（此时吞掉热键，避免打字触发菜单开关）。</summary>
    public static bool TextFocus { get; set; }

    /// <summary>热区数量（诊断）。</summary>
    public static int ZoneCount => _zones.Count;

    // ---------------- 测试注入点（CLI 模拟点击用；正常游戏里为 null/false） ----------------

    /// <summary>
    /// 指针位置覆盖（屏幕坐标）。非空时 <see cref="Tick"/> 不读真实设备，改用这个位置——
    /// 这样模拟点击走的是**和真实鼠标完全相同的命中测试/悬停/按下/拖拽路径**（而不是直接调回调），
    /// 能真正验证命中数学、层级与热区表。测试模组用完清回 null。
    /// </summary>
    public static Vector2? PointerOverride { get; set; }

    /// <summary>左键按下状态覆盖（配合 <see cref="PointerOverride"/> 模拟点击/拖拽）。</summary>
    public static bool ButtonOverride { get; set; }

    /// <summary>滚轮注入（测试用）：设置后**本帧**用它代替真实滚轮值（原始值，一格=±120），用完自动清。</summary>
    public static float? WheelOverride { get; set; }

    /// <summary>测试用：命中测试时忽略“节点是否激活”（剪贴板画布在测试环境不会真被游戏打开）。</summary>
    public static bool IgnoreActiveForTest { get; set; }

    /// <summary>诊断：最近一次按下/仲裁/拖拽的关键信息。</summary>
    public static string LastDragDiag { get; private set; } = "-";

    /// <summary>诊断：最近一次收到的滚轮原始值 / 归一化后的格数 / 分发到的热区名。</summary>
    public static float LastWheelRaw { get; private set; }
    public static float LastWheelNotches { get; private set; }
    public static string LastWheelZone { get; private set; } = "-";

    /// <summary>是否使用注入的指针/按键（等价于 <c>PointerOverride.HasValue || ButtonOverride</c>）。</summary>
    public static bool Injecting => PointerOverride.HasValue || ButtonOverride;

    /// <summary>当前热区名快照（诊断/CLI 定位用）。</summary>
    public static string[] ZoneNames
    {
        get
        {
            var list = new List<string>(_zones.Count);
            for (int i = 0; i < _zones.Count; i++)
            {
                var z = _zones[i];
                if (z == null) continue;
                bool active = false;
                try { active = z.Rect != null && z.Rect.gameObject.activeInHierarchy; } catch { }
                list.Add((z.Name ?? "") + (z.Enabled ? "" : " [disabled]") + (active ? "" : " [hidden]"));
            }
            return list.ToArray();
        }
    }

    /// <summary>按名字片段找热区（**后登记优先** = 上层优先；找不到返回 null）。</summary>
    public static UiHotZone FindZone(string nameSubstring, bool requireActive = true)
    {
        if (string.IsNullOrEmpty(nameSubstring)) return null;
        for (int i = _zones.Count - 1; i >= 0; i--)
        {
            var z = _zones[i];
            if (z == null || z.Rect == null) continue;
            if (requireActive)
            {
                try { if (!z.Rect.gameObject.activeInHierarchy && !IgnoreActiveForTest) continue; } catch { continue; }
            }            if (z.Name != null && z.Name.IndexOf(nameSubstring, StringComparison.OrdinalIgnoreCase) >= 0) return z;
        }
        return null;
    }
    /// <summary>
    /// 按归属对象找热区（后登记优先）。声明式行渲染后用它把**动态悬停提示**（<c>UiTooltip</c>）
    /// 挂到那一行的热区上 —— 否则控件工厂不返回热区，外部拿不到"这行是哪个热区"。
    /// </summary>
    public static UiHotZone FindZoneOf(object owner)
    {
        if (owner == null) return null;
        for (int i = _zones.Count - 1; i >= 0; i--)
        {
            var z = _zones[i];
            if (z == null || z.Rect == null) continue;
            try { if (ReferenceEquals(z.Owner, owner)) return z; } catch { }
        }
        return null;
    }

    /// <summary>
    /// 取某个归属对象的**全部**热区（一个控件可能有多个：页签的每列、列表的每行…），
    /// 后登记优先的顺序不变。绑定悬停提示时要全部绑上，否则只生效在最后一列上。
    /// </summary>
    public static UiHotZone[] ZonesOf(object owner)
    {
        if (owner == null) return System.Array.Empty<UiHotZone>();
        var list = new System.Collections.Generic.List<UiHotZone>(4);
        for (int i = _zones.Count - 1; i >= 0; i--)
        {
            var z = _zones[i];
            if (z == null || z.Rect == null) continue;
            try { if (ReferenceEquals(z.Owner, owner)) list.Add(z); } catch { }
        }
        return list.ToArray();
    }
    /// <summary>取热区中心的屏幕坐标（供模拟点击对准）。</summary>
    public static bool TryGetScreenCenter(UiHotZone zone, out Vector2 screenPos)
    {
        screenPos = default;
        try
        {
            if (zone?.Rect == null) return false;
            var world = zone.Rect.TransformPoint(zone.Rect.rect.center);
            screenPos = RectTransformUtility.WorldToScreenPoint(CameraFor(zone.Rect), world);
            return true;
        }
        catch { return false; }
    }

    public static void Activate()
    {
        Active = true;
        _wasDown = false;
    }

    public static void Deactivate()
    {
        Active = false;
        try { if (_hover != null) { _hover.Hovered = false; _hover.ApplyColor(); } } catch { }
        _hover = null; _press = null; _drag = null;
        _wasDown = false;
        TextFocus = false;
        PointerOverride = null;
        ButtonOverride = false;
    }

    /// <summary>登记热区。</summary>
    public static UiHotZone Add(UiHotZone zone)
    {
        if (zone == null) return null;
        _zones.Add(zone);
        return zone;
    }

    /// <summary>便捷重载（建一个只做点击/悬停的热区）。</summary>
    public static UiHotZone Add(string name, RectTransform rect, Action onClick, Image bg = null, Color? baseColor = null, object owner = null)
    {
        var z = new UiHotZone
        {
            Name = name,
            Rect = rect,
            OnClick = onClick,
            Bg = bg,
            Owner = owner,
        };
        if (baseColor.HasValue && bg != null)
        {
            z.Tint = true;
            z.SetBaseColor(baseColor.Value);
        }
        return Add(z);
    }

    /// <summary>注销热区。</summary>
    public static void Remove(UiHotZone zone)
    {
        if (zone == null) return;
        _zones.Remove(zone);
        if (ReferenceEquals(_hover, zone)) _hover = null;
        if (ReferenceEquals(_press, zone)) _press = null;
        if (ReferenceEquals(_drag, zone)) _drag = null;
    }

    /// <summary>注销某归属者的全部热区（页面销毁时调用）。</summary>
    public static void RemoveOwner(object owner)
    {
        if (owner == null) return;
        for (int i = _zones.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_zones[i].Owner, owner)) Remove(_zones[i]);
    }

    /// <summary>清空全部热区（菜单销毁时调用）。</summary>
    public static void Clear()
    {
        _zones.Clear();
        _hover = _press = _drag = null;
        _wasDown = false;
        Active = false;
        TextFocus = false;
        PointerOverride = null;
        ButtonOverride = false;
    }

    /// <summary>取当前指针屏幕坐标（虚拟光标优先 → 硬件鼠标兜底）。</summary>
    public static bool TryGetPointer(out Vector2 pos)
    {
        pos = default;
        try
        {
            // 玩家"看到的"是游戏画出来的虚拟光标 → 用它做命中判定才不会"看着在按钮上却点不到"
            var v = UI.NativeUi.VirtualCursorPosition;
            if (v.HasValue) { pos = v.Value; return true; }
        }
        catch { }
        try
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null) { pos = mouse.position.ReadValue(); return true; }
        }
        catch { }
        return false;
    }

    /// <summary>每帧分发（由 <see cref="Core.UiKitBehaviour.Update"/> 调用）。</summary>
    public static void Tick(float dt)
    {
        if (!Active) return;
        try
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            Vector2 pos;
            if (PointerOverride.HasValue) pos = PointerOverride.Value;              // 测试注入优先
            else if (!TryGetPointer(out pos)) return;

            bool down = mouse != null && mouse.leftButton.isPressed;
            float scroll = 0f;
            if (WheelOverride.HasValue) { scroll = WheelOverride.Value; WheelOverride = null; }   // 测试注入：原始值（一格=±120）
            else try { if (mouse != null) scroll = mouse.scroll.ReadValue().y; } catch { }

            // 测试注入：覆盖指针位置/左键状态（仍走同一条命中路径）
            if (PointerOverride.HasValue) pos = PointerOverride.Value;
            if (ButtonOverride) down = true;

            bool downEdge = down && !_wasDown;
            bool upEdge = !down && _wasDown;
            if (Mathf.Abs(scroll) < 0.01f) scroll = 0f;

            // ---- 0) 点击/滚动仲裁 ----
            // 问题：列表的**行**是后登记的（比列表视口更“上层”），按在行上时按下目标会是行，
            // 于是“按住拖动没反应”（用户反馈）。处理：按下时记下压在下面的**可滚动**热区，
            // 一旦移动超过阈值就转成拖拽滚动，并取消这次点击。
            if (_drag == null && _dragCandidate != null && _press != null)
            {
                try
                {
                    if (Vector2.Distance(pos, _pressPos) > 6f)
                    {
                        if (_press != null) { _press.Pressed = false; _press.ApplyColor(); _press = null; }
                        _drag = _dragCandidate;
                        LastDragDiag = $"仲裁→滚动 '{_drag.Name}' moved={Vector2.Distance(pos, _pressPos):0.#}";
                        // 让热区初始化拖拽起点（列表用它记 lastLocalY）
                        if (_drag.OnPressLocal != null && TryLocal(_drag, pos, out var lp0)) _drag.OnPressLocal(lp0);
                    }
                }
                catch { }
            }

            // ---- 1) 拖拽中：独占指针 ----
            if (_drag != null)
            {
                if (TryLocal(_drag, pos, out var localDrag))
                {
                    LastDragDiag = $"拖拽 '{_drag.Name}' localY={localDrag.y:0.#}";
                    try { _drag.OnDrag?.Invoke(localDrag); } catch { }
                }
                if (!down)
                {
                    try { _drag.OnDragEnd?.Invoke(localDrag); } catch { }
                    _drag.Pressed = false; _drag.ApplyColor();
                    _drag = null;
                }
                _wasDown = down;
                return;
            }

            // ---- 2) 按下 ----
            if (downEdge)
            {
                var z = HitTop(pos);
                _press = z;
                _pressPos = pos;
                _dragCandidate = (z != null && z.OnDrag != null) ? null : HitTopScrollable(pos);
                LastDragDiag = $"按下 '{z?.Name ?? "-"}' 滚动候选='{_dragCandidate?.Name ?? "-"}'（行 OnDrag=null → 候选=压在下面的滚动区）";
                if (z != null)
                {
                    z.Pressed = true; z.ApplyColor();
                    try { z.OnPress?.Invoke(); } catch { }
                    if (z.OnPressLocal != null)
                    {
                        if (TryLocal(z, pos, out var lp) && z.OnPressLocal(lp)) _drag = z;
                    }
                }
            }

            // ---- 3) 抬起（按下与抬起在同一热区 = 点击）----
            if (upEdge)
            {
                var z = HitTop(pos);
                if (_press != null && ReferenceEquals(z, _press))
                {
                    try { _press.OnClick?.Invoke(); } catch (Exception ex) { CoopLog.Warn("uikit.pointer", () => $"click handler failed: {ex.Message}"); }
                }
                if (_press != null)
                {
                    try { _press.OnRelease?.Invoke(); } catch { }
                    _press.Pressed = false;
                    _press.ApplyColor();
                }
                _press = null;
                _dragCandidate = null;
            }

            // ---- 4) 悬停 ----
            var hover = HitTop(pos);
            if (!ReferenceEquals(hover, _hover))
            {
                if (_hover != null) { _hover.Hovered = false; _hover.ApplyColor(); try { _hover.OnExit?.Invoke(); } catch { } }
                _hover = hover;
                if (_hover != null) { _hover.Hovered = true; _hover.ApplyColor(); try { _hover.OnEnter?.Invoke(); } catch { } }
            }

            // ---- 5) 滚轮 ----
            // ⚠ 归一化：新输入系统的 `Mouse.scroll` 是**原始值（一格 = ±120）**，而旧 `mouseScrollDelta` 是 ±1。
            // 早期直接把原始值传给滚动热区（`ScrollBy(-delta*36)`）→ 一格就跳 4320px（瞬间到底/到顶，看着就像“方向反了”）。
            // ⚠ 另一个坑（用户报“没法向下滚”）：列表**行**是后登记的，比列表视口更“上层”，
            // 早期把滚轮发给“最上层热区”（= 行）→ 行没有 OnScroll → 什么也不发生。
            // 正确做法：发给**指针下最上层“可滚动”的热区**。
            if (scroll != 0f)
            {
                var target = HitTopScrollable(pos) ?? hover;
                if (target != null && target.OnScroll != null)
                {
                    float notches = Mathf.Abs(scroll) > 20f ? scroll / 120f : scroll;
                    LastWheelRaw = scroll;
                    LastWheelNotches = notches;
                    LastWheelZone = target.Name ?? "?";
                    LogWheel(scroll, notches, target.Name);
                    try { target.OnScroll(notches); } catch { }
                }
            }

            _wasDown = down;
            DiagTick(dt);
        }
        catch { /* 每帧驱动绝不抛 */ }
    }

    /// <summary>热区所在画布的相机（Overlay 为 null；WorldSpace/相机模式必须传它的 worldCamera，否则命中数学全错）。</summary>
    public static Camera CameraFor(RectTransform rt)
    {
        try
        {
            var cv = rt != null ? rt.GetComponentInParent<Canvas>() : null;
            if (cv != null)
            {
                if (cv.renderMode == RenderMode.ScreenSpaceOverlay) return null;
                if (cv.worldCamera != null) return cv.worldCamera;
                return Camera.main;      // WorldSpace 画布 worldCamera 为空时，Unity 自己也回退到主相机
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 滚轮方向系数（乘在“格数”上；<c>+1</c> = 保持 Unity 设备符号 "上滚为正" 的常规映射）。
    /// 为什么做成可配：不同环境（Steam Input 重映射 / 触控板 / 系统级反转）会给出相反的 raw 符号；
    /// 环境变量 <c>OPENNESTUIKIT_WHEEL</c> 可以设 <c>1</c> 或 <c>-1</c> 现场纠正，不用重编译。
    /// 日志（<c>uikit.pointer</c>）会把真实 raw 值与发送对象一起记下来，便于一次对清。
    /// </summary>
    public static float WheelSign { get; set; } = ReadWheelSignFromEnv();

    private static float ReadWheelSignFromEnv()
    {
        try
        {
            var s = Environment.GetEnvironmentVariable("OPENNESTUIKIT_WHEEL");
            if (!string.IsNullOrEmpty(s) && float.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float v) && Mathf.Abs(v) > 0.5f)
                return v > 0f ? 1f : -1f;
        }
        catch { }
        return 1f;
    }

    private static float _wheelLogT;
    private static void LogWheel(float raw, float notches, string zone)
    {
        try
        {
            float now = Time.unscaledTime;
            if (now - _wheelLogT < 0.7f) return;
            _wheelLogT = now;
            CoopLog.Info("uikit.pointer", () => $"wheel raw={raw:0} → {notches:0.##} 格（符号={WheelSign:0}）→ '{zone}'");
        }
        catch { }
    }

    /// <summary>命中测试：从后往前（后登记 = 上层），返回第一个命中的热区。</summary>
    private static UiHotZone HitTop(Vector2 screenPos)
    {
        for (int i = _zones.Count - 1; i >= 0; i--)
        {
            var z = _zones[i];
            if (z == null || !z.Enabled || z.Rect == null) continue;
            try
            {
                if (!z.Rect.gameObject.activeInHierarchy && !IgnoreActiveForTest) continue;
                if (z.Clip != null)
                {
                    var clip = z.Clip();
                    if (clip.width > 0f && clip.height > 0f && !clip.Contains(screenPos)) continue;
                }
                if (RectTransformUtility.RectangleContainsScreenPoint(z.Rect, screenPos, CameraFor(z.Rect))) return z;
            }
            catch { }
        }
        return null;
    }

    /// <summary>命中测试（只要**可滚动**的热区）：滚轮专用 —— 列表行叠在视口上时，滚轮必须落到视口。</summary>
    private static UiHotZone HitTopScrollable(Vector2 screenPos)
    {
        for (int i = _zones.Count - 1; i >= 0; i--)
        {
            var z = _zones[i];
            if (z == null || !z.Enabled || z.Rect == null || z.OnScroll == null) continue;
            try
            {
                if (!z.Rect.gameObject.activeInHierarchy && !IgnoreActiveForTest) continue;
                if (z.Clip != null)
                {
                    var clip = z.Clip();
                    if (clip.width > 0f && clip.height > 0f && !clip.Contains(screenPos)) continue;
                }
                if (RectTransformUtility.RectangleContainsScreenPoint(z.Rect, screenPos, CameraFor(z.Rect))) return z;
            }
            catch { }
        }
        return null;
    }

    private static bool TryLocal(UiHotZone z, Vector2 screenPos, out Vector2 local)
    {
        local = default;
        try
        {
            if (z?.Rect == null) return false;
            return RectTransformUtility.ScreenPointToLocalPointInRectangle(z.Rect, screenPos, CameraFor(z.Rect), out local);
        }
        catch { return false; }
    }

    /// <summary>诊断：悬停/拖拽变化与热区数（默认 Debug 级，排障时开 <c>CoopLog.Level = Debug</c>）。</summary>
    private static void DiagTick(float dt)
    {
        _diagT += dt;
        if (_diagT < 1f) return;
        _diagT = 0f;
        string cur = $"zones={_zones.Count} hover={(_hover != null ? _hover.Name : "-")} drag={(_drag != null ? _drag.Name : "-")}";
        if (cur == _lastDiag) return;
        _lastDiag = cur;
        CoopLog.Debug("uikit.pointer", () => "pointer: " + cur);
    }
}
