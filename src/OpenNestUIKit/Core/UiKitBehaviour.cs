using System;
using UnityEngine;

namespace OpenNestUIKit.Core;

/// <summary>
/// 每帧驱动（由 <see cref="UiKitRuntime"/> 注入并挂到常驻 GameObject）：
/// 批量落盘 + 语言轮询 + 指针路由 + 菜单热键/守卫重申 + 过渡动画 + 原生菜单注入重试 + 自测钩子。
///
/// ⚠️ IL2CPP：注入的 MonoBehaviour 方法必须是 public；每帧路径**绝不抛异常**。
/// </summary>
public sealed class UiKitBehaviour : MonoBehaviour
{
    // 自测钩子（命令行参数；正常启动行为完全不变，见 docs/UI_KIT.md §九）
    //   -onuk-autoopen            启动 6 秒后自动打开菜单
    //   -onuk-autoopen=&lt;pageId&gt;  直达某页
    private string _autoOpenPage;
    private bool _autoOpenPending;
    private float _autoOpenT;

    public void Awake()
    {
        try { CoopLog.Debug("uikit.behaviour", () => "Awake"); } catch { }
        try
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("-onuk-autoopen", StringComparison.OrdinalIgnoreCase))
                {
                    _autoOpenPending = true;
                    int eq = a.IndexOf('=');
                    if (eq > 0) _autoOpenPage = a.Substring(eq + 1).Trim();
                }
            }
        }
        catch { }
    }

    public void Update()
    {
        float t0 = 0f;
        bool timing = false;
        try { t0 = Time.realtimeSinceStartup; timing = true; } catch { }
        try
        {
            float dt = Time.unscaledDeltaTime;
            ModLog.Flush(dt);
            UiKitLoc.Tick(dt);
            UiSliceStore.Tick(dt);
            UiKitTween.Tick(dt);
            Native.UiEscapeGuard.Tick();     // ESC 关窗/退页后，等按键抬起再放行游戏 ESC（防同一次按键穿到游戏）
            UiPointerRouter.Tick(dt);
            Widgets.UiTextRouter.Tick(dt);   // ★ 文本输入管线（物理键 + 隐藏 IME 锚点 + 原生 Win32 IME）——
                                             //   原模组 CoopUIManager.PollInput 同款；早期用 TMP_InputField 收键，
                                             //   但游戏输入模块被我们停用 → 点了打不了字
            Widgets.UiChatOverlay.Tick(dt);  // 悬浮聊天层（回车唤入 / 回车发送 / ESC 收起）
            Widgets.UiTextInput.TickCaret(dt);  // ★ 输入框光标闪烁（0.5s；第三方页面的输入框用）
            Widgets.UiList.TickAll();       // 尺寸后到 / 窗口尺寸变化 → 补一次列表重排（否则内容按 300 宽落定）
            Menu.UiMenuWindow.Tick(dt);
            NativeMenuInjector.Tick(dt);
            NativeMenuPage.Tick();          // 剪贴板关闭时把我们的原生次级菜单页复位（避免下次打开停在那一页）
            NativeMenuPage.TickScroll(dt);  // ★ Bounce：越界偏移每帧弹性拉回边界（原生 ScrollRect Elastic 同款手感）
            Native.UiInputGuard.TickLock();  // 交互锁分帧执行（避免开菜单时 6.5ms 单帧尖峰）
            Native.UiInputGuard.EnsureReleasedWhenIdle();  // ★ 兵底不变量：我们没开界面时，拦截层必须干净
            Native.UiInputGuard.EnsureNativePageSuppress(); // ★ 原生页占用期间每帧确保游戏输入被压住（防“写回”造成穿透）
            UiGameBridge.Tick(dt);

            if (_autoOpenPending)
            {
                _autoOpenT += dt;
                if (_autoOpenT > 6f)
                {
                    _autoOpenPending = false;
                    CoopLog.Info("uikit.selftest", () => $"autoopen page='{_autoOpenPage}'");
                    Menu.UiMenuWindow.Open(_autoOpenPage);
                }
            }
        }
        catch { /* 每帧驱动绝不抛 */ }
        if (timing) Perf.Sample(t0);
    }

    public void LateUpdate()
    {
        float t0 = 0f;
        bool timing = false;
        try { t0 = Time.realtimeSinceStartup; timing = true; } catch { }
        try { Menu.UiMenuWindow.TickLate(); } catch { }
        if (timing) Perf.SampleLate(t0);
    }

    public void OnDestroy()
    {
        try { CoopLog.Debug("uikit.behaviour", () => "OnDestroy"); } catch { }
    }
}
