using System;
using System.Collections.Generic;
namespace OpenNestUIKit.Native;

/// <summary>
/// ESC **层级**模型：我们自己打开的、需要消费 ESC 的 UI 层。
///
/// 为什么要有这个东西（用户反馈"ESC 层级还是不对"）：
/// 以前"是否阻止游戏 ESC"只有 <see cref="UiEscapeGuard"/> 一个裸 bool，
/// 打开/关闭由各处**各自**调用 <c>Block(true/false)</c>。只要有一条关闭路径漏调
/// （窗口被销毁、剪贴板被游戏关掉、场景切换……），这个 bool 就**永远停在 true** →
/// 表现就是"我们的界面早就关了，可游戏 ESC 菜单再也调不出来"。
///
/// 现在的规则（唯一真源）：
/// - 每开一个会消费 ESC 的界面 = <see cref="Push"/> 一层（窗口 / 原生次级菜单页）；
/// - 每关一个 = <see cref="Pop"/> 一层；
/// - **层数为 0 ⇒ 我们绝不占用 ESC**：<see cref="UiEscapeGuard"/> 每帧自愈（见 <c>UiEscapeGuard.Tick</c>），
///   即使某条路径漏了 Pop 或漏了放行，也会被纠正；
/// - 层数 > 0 ⇒ 才阻止游戏 ESC（ESC 由我们逐层消费）。
///
/// 另外，最后那一层的关闭动作如果是 ESC 自己触发的，<see cref="UiEscapeGuard.ReleaseWhenKeyUp"/>
/// 会把这一**次**按键吃掉（等抬起再放行），保证"关掉我们界面的那一下"不会顺势把游戏 ESC 菜单调出来。
/// </summary>
public static class UiEscapeLevels
{
    // 约定：owner 用稳定的字符串常量（日志/诊断里直接可读）
    /// <summary>我们的独立菜单窗口（<c>UiMenuWindow</c>）。</summary>
    public const string Window = "window";
    /// <summary>原生 ESC 面板里的次级菜单页（<c>NativeMenuPage</c>）。</summary>
    public const string NativePage = "native-page";

    private static readonly List<string> _owners = new();
    private static readonly object _sync = new();

    /// <summary>层数变化（Push/Pop/Clear）→ <see cref="UiEscapeGuard"/> 立即同步占用状态。</summary>
    public static event Action Changed;

    /// <summary>当前层数（0 = 无层级 = ESC 完全交还游戏）。</summary>
    public static int Count { get { lock (_sync) return _owners.Count; } }

    /// <summary>是否无层级（= 我们没有任何需要 ESC 的界面）。</summary>
    public static bool IsEmpty { get { lock (_sync) return _owners.Count == 0; } }

    /// <summary>是否已压入某层。</summary>
    public static bool Has(string owner)
    {
        if (string.IsNullOrEmpty(owner)) return false;
        lock (_sync) return _owners.Contains(owner);
    }

    /// <summary>压入一层（幂等：同 owner 重复 Push 只算一层）。</summary>
    public static void Push(string owner)
    {
        if (string.IsNullOrEmpty(owner)) return;
        bool changed = false;
        lock (_sync)
        {
            if (!_owners.Contains(owner)) { _owners.Add(owner); changed = true; }
        }
        if (!changed) return;
        CoopLog.Info("uikit.esc", () => $"ESC 层级 +1 → {Describe()}");
        Raise();
    }

    /// <summary>弹出该层（幂等：不在栈里就什么都不做）。</summary>
    public static void Pop(string owner)
    {
        if (string.IsNullOrEmpty(owner)) return;
        bool removed;
        lock (_sync) removed = _owners.Remove(owner);
        if (!removed) return;
        CoopLog.Info("uikit.esc", () => $"ESC 层级 -1 → {Describe()}");
        Raise();
    }

    /// <summary>清空全部层（宿主关闭时调用：不允许留下任何"占用 ESC"的残留）。</summary>
    public static void Clear()
    {
        bool had;
        lock (_sync) { had = _owners.Count > 0; _owners.Clear(); }
        if (!had) return;
        CoopLog.Info("uikit.esc", () => "ESC 层级清空 → 0");
        Raise();
    }

    /// <summary>层级快照（诊断/测试用）。</summary>
    public static string[] Owners
    {
        get { lock (_sync) return _owners.ToArray(); }
    }

    /// <summary>一行式描述（日志/`escprobe` 用）。</summary>
    public static string Describe()
    {
        var arr = Owners;
        return arr.Length == 0 ? "层级=0[]" : $"层级={arr.Length}[{string.Join(",", arr)}]";
    }

    private static void Raise()
    {
        try { Changed?.Invoke(); } catch { /* 订阅者异常不外溢 */ }
    }
}
