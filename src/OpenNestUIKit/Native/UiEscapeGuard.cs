using UnityEngine;

namespace OpenNestUIKit.Native;

/// <summary>
/// ESC 守卫：**有层级**时阻止游戏自己的 ESC 暂停菜单，**无层级**时一步不留地交还游戏。
///
/// 实现直接走游戏侧桥接（<see cref="UI.NativeUi.BlockEscapeMenu"/> → `EscapeMenuOpenBlocker`），
/// 不重复造轮子；桥接不可用（未挂上/游戏对象未就绪）时退化为"只记录状态"。
///
/// ❗ 本类**不再**自己决定“该不该阻止” —— 唯一真源是 <see cref="UiEscapeLevels"/>（我们开了几层 UI）：
/// - 层数从 0 → &gt;0：立刻阻止（<see cref="UiEscapeLevels.Changed"/> 订阅里做）；
/// - 层数从 &gt;0 → 0：立刻放行；若这一下正是 ESC 触发的则等按键抬起（<see cref="ReleaseWhenKeyUp"/>）；
/// - 每帧 <see cref="Tick"/> 还会**自愈**：层数为 0 却还处于阻止状态 → 直接放行。
///   这就是用户反馈的“无层级的地方调不出 ESC 菜单”（旧实现里 bool 一旦漏放行就永久阻止，界面关了 ESC 菜单也出不来）的根治。
///
/// 关闭顺序约定（与 <c>ModMenuUI.Close</c> 一致）：先 <see cref="Block"/>(false)，再还原其它守卫，
/// 最后隐藏画布 —— 顺序无歧义。
/// </summary>
public static class UiEscapeGuard
{
    private static bool _blocked;
    private static bool _releaseWhenUp;

    static UiEscapeGuard()
    {
        // 层级变化 → 立即同步占用状态（不等下一帧）
        try { UiEscapeLevels.Changed += SyncWithLevels; } catch { }
    }

    /// <summary>当前是否处于"已阻止"状态。</summary>
    public static bool IsBlocked => _blocked;

    /// <summary>是否还在等 ESC 抬起后放行（诊断用）。</summary>
    public static bool ReleasePending => _releaseWhenUp;

    /// <summary>阻止/放行（幂等；重复调用只做状态确认）。</summary>
    public static void Block(bool blocked)
    {
        _blocked = blocked;
        if (!blocked) _releaseWhenUp = false;
        try { UI.NativeUi.BlockEscapeMenu(blocked); } catch { }
    }

    /// <summary>
    /// 层数变化时同步（<see cref="UiEscapeLevels.Changed"/>）：
    /// 有层级 = 我们占着 ESC（阻止游戏）；无层级 = 必须放行。
    /// </summary>
    private static void SyncWithLevels()
    {
        if (UiEscapeLevels.IsEmpty)
        {
            // 无层级：不允许再占着 ESC（若已经没占着就是幂等的）
            if (_blocked) ReleaseWhenKeyUp();
            return;
        }
        // 有层级：确保阻止
        if (!_blocked)
        {
            _blocked = true;
            try { UI.NativeUi.BlockEscapeMenu(true); } catch { }
            CoopLog.Debug("uikit.esc", () => $"有层级 → 阻止游戏 ESC（{UiEscapeLevels.Describe()}）");
        }
    }

    /// <summary>
    /// “这次关闲是 ESC 按下的那一帧触发的” —— **不能立刻放行**，否则同一次按键会被游戏收到
    /// （实测现象：我们的窗口刚关 / 原生页刚退，游戏暂停菜单马上弹出来）。
    /// 这里记下待放行，由 <see cref="Tick"/> 在按键抬起后真的放行。
    /// </summary>
    public static void ReleaseWhenKeyUp()
    {
        if (!_blocked) return;
        bool held = false;
        try { var kb = UnityEngine.InputSystem.Keyboard.current; held = kb != null && kb.escapeKey.isPressed; } catch { }
        if (held) { _releaseWhenUp = true; CoopLog.Debug("uikit.esc", () => "ESC 仍按着 → 抬起后再放行游戏 ESC 菜单"); }
        else Block(false);
    }

    /// <summary>每帧（<see cref="Core.UiKitBehaviour"/> 调用）：自愈 + 待放行的在按键抬起后生效。</summary>
    public static void Tick()
    {
        // 自愈：层数为 0 却还阻止着 —— 必定是某条关闭路径漏了（或场景切换换掉了子系统的状态）→ 纠正
        if (!_releaseWhenUp && _blocked && UiEscapeLevels.IsEmpty)
        {
            CoopLog.Warn("uikit.esc", () => $"无层级但仍在阻止游戏 ESC → 自动放行（{UiEscapeLevels.Describe()}）");
            Block(false);
            return;
        }

        if (!_releaseWhenUp) return;
        // 待放行期间层级又起来了（用户在这一按里又开了界面）→ 取消放行
        if (!UiEscapeLevels.IsEmpty)
        {
            _releaseWhenUp = false;
            CoopLog.Debug("uikit.esc", () => "待放行期间又有了层级 → 取消放行");
            return;
        }
        bool held = false;
        try { var kb = UnityEngine.InputSystem.Keyboard.current; held = kb != null && kb.escapeKey.isPressed; } catch { }
        if (held) return;
        Block(false);
        CoopLog.Debug("uikit.esc", () => "ESC 已抬起 → 放行游戏 ESC 菜单");
    }

    /// <summary>菜单打开时调用：先关掉**已经打开**的游戏 ESC 菜单，再阻止它再次打开。
    /// ⚠️ 只允许在**有层级**时调用（层级由 <see cref="UiEscapeLevels.Push"/> 先压入）；
    /// 无层级时不碰游戏 ESC —— “无层级的地方”不该被我们动。</summary>
    public static void Apply()
    {
        if (UiEscapeLevels.IsEmpty)
        {
            CoopLog.Warn("uikit.esc", () => "Apply() 在无层级时被调用 → 忽略（不碰游戏 ESC）");
            return;
        }
        try
        {
            if (UI.NativeUi.IsEscapeMenuOpen) UI.NativeUi.CloseEscapeMenu();
        }
        catch { }
        Block(true);
    }

    /// <summary>每帧后段重申（场景切换可能换掉 ESC 菜单实例）。**无层级时什么都不做**。</summary>
    public static void TickFallback()
    {
        if (UiEscapeLevels.IsEmpty) return;
        if (!_blocked) { Block(true); return; }
        try
        {
            if (!UI.NativeUi.IsEscapeMenuBlocked) UI.NativeUi.BlockEscapeMenu(true);
        }
        catch { }
    }

    /// <summary>诊断：ESC 菜单当前状态 + 层级。</summary>
    public static string Probe()
    {
        try
        {
            return $"esc open={UI.NativeUi.IsEscapeMenuOpen} blocked={UI.NativeUi.IsEscapeMenuBlocked} "
                 + $"(ours={_blocked} 待放行={_releaseWhenUp}) {UiEscapeLevels.Describe()}";
        }
        catch { return "esc probe failed"; }
    }
}
