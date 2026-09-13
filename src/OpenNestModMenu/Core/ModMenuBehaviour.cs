using System;
using OpenNestModMenu.API;
using UnityEngine;

namespace OpenNestModMenu.Core;

/// <summary>
/// 每帧驱动（由 <see cref="ModMenuRuntime"/> 注入并挂到常驻 GameObject）：
/// 现在只做 <see cref="ModLog"/> 批量落盘 + <see cref="FrameProfiler"/> 窗口结算；
/// 后续任务在此加热键轮询（T5）、语言/场景变化检测（T4/T9）。
///
/// ⚠️ IL2CPP：注入的 MonoBehaviour 方法必须是 public。
/// </summary>
public sealed class ModMenuBehaviour : MonoBehaviour
{
    // TEMP-VERIFY：命令行带 `-onnmm-autoopen` 时，启动 6 秒后自动打开一次菜单
    //（仅供自动化截屏验证 UI；**正常启动无此参数时行为完全不变**）。
    // `-onnmm-selftest-runtime=<匹配串>`：列出 ML 模组实例，并对匹配项做“运行中停用 → 2 秒后恢复”（**不改文件**），
    // 用来验证 MLL Unregister / LoadMelons 真的可用。
    private bool _autoOpen;
    private string _selfTestRuntime = null;
    private int _selfTestStage;
    private float _t;

    public void Awake()
    {
        try { CoopLog.Debug("modmenu.behaviour", () => "Awake"); } catch { }
        try
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (string.Equals(a, "-onnmm-autoopen", StringComparison.OrdinalIgnoreCase)) _autoOpen = true;
                else if (a.StartsWith("-onnmm-selftest-runtime=", StringComparison.OrdinalIgnoreCase))
                    _selfTestRuntime = a.Substring("-onnmm-selftest-runtime=".Length).Trim();
                else if (a.StartsWith("-onnmm-selftest-toggle=", StringComparison.OrdinalIgnoreCase))
                    _selfTestToggle = a.Substring("-onnmm-selftest-toggle=".Length).Trim();
                else if (a.StartsWith("-onnmm-selftest-t56-ui=", StringComparison.OrdinalIgnoreCase))
                {
                    _selfTestT56 = a.Substring("-onnmm-selftest-t56-ui=".Length).Trim();
                    _t56UiOnly = true;   // 只走到“切设置页”为止，留着菜单开着供截图
                }
                else if (a.StartsWith("-onnmm-selftest-tab=", StringComparison.OrdinalIgnoreCase))
                {
                    // TEMP-VERIFY：只做“开菜单 + 选条目 + 切页签”（供诊断/设置页截图），不写任何东西
                    _selfTestTab = a.Substring("-onnmm-selftest-tab=".Length).Trim();
                }
                else if (a.StartsWith("-onnmm-selftest-t56=", StringComparison.OrdinalIgnoreCase))
                    _selfTestT56 = a.Substring("-onnmm-selftest-t56=".Length).Trim();
            }
        }
        catch { }
    }

    public void Update()
    {
        try
        {
            float dt = Time.unscaledDeltaTime;
            ModLog.Flush(dt);
            FrameProfiler.Instance.RecordFrameMs(dt * 1000.0);   // T10：喂帧耗时（否则调试面板的 FPS 永远是 0）
            FrameProfiler.Instance.Tick(dt);
            ModMenuRegistry.Tick(dt);   // T2：注册表扫描调度
            ModInventory.Tick(dt);      // T3：模组清单刷新调度
            ModInitScheduler.Tick(dt);  // T8：统一顺序 + 受管初始化调度
            ModMenuLoc.Tick(dt);        // 语言键文件热重载 + 语言变化轮询
            ModMenuUI.Tick(dt);         // T4：热键（F6）+ 界面节流刷新
            UIKitIntegration.Tick();    // 环境里有 OpenNestUIKit → 菜单交给它渲染（纯探测，平时零开销）

            // TEMP-VERIFY
            if (_autoOpen)
            {
                _t += dt;
                if (_t > 6f) { _autoOpen = false; ModMenuUI.Open(); }
            }

            // TEMP-VERIFY：运行中启停自测
            if (_selfTestRuntime != null) TickSelfTest(dt);

            // TEMP-VERIFY：完整启停自测（走真实 `ModFileState.TryToggle`：改名 + 热加载，8s 时启用、20s 时禁用）
            if (_selfTestToggle != null) TickSelfTestToggle(dt);

            // TEMP-VERIFY：T5（ESC）/ T6（设置页）/ T8（统一顺序）分阶段自测
            if (_selfTestT56 != null) TickSelfTestT56(dt);

            // TEMP-VERIFY：只开菜单+切页签（T10 诊断页截图用）
            if (_selfTestTab != null) TickSelfTestTab(dt);
        }
        catch { /* 每帧驱动绝不抛 */ }
    }

    public void OnDestroy()
    {
        try { CoopLog.Debug("modmenu.behaviour", () => "OnDestroy"); } catch { }
    }

    /// <summary>
    /// 每帧后段（晚于所有 Update，早于渲染）：重申“光标/上层画布在菜单之上”。
    /// 必须在 LateUpdate 做——游戏会在自己的 Update 里把光标画布的 sortingOrder 写回去。
    /// </summary>
    public void LateUpdate()
    {
        try
        {
            ModMenuUI.TickLate();
            _lateDiag += Time.unscaledDeltaTime;
            if (_lateDiag > 5f)
            {
                _lateDiag = 0f;
                CoopLog.Debug("modmenu.behaviour", () => $"late tick (menuOpen={ModMenuUI.IsOpen})");
            }
        }
        catch { }
    }

    private float _lateDiag;

    // TEMP-VERIFY：运行中启停自测（-onnmm-selftest-runtime=<匹配串>；匹配串 = `list` 时只列目标）
    private float _selfT;

    private void TickSelfTest(float dt)
    {
        _selfT += dt;
        try
        {
            if (_selfTestStage == 0 && _selfT > 8f)
            {
                _selfTestStage = 1;
                var insts = LoaderReflect.ReadMelonInstances();
                var names = new System.Text.StringBuilder();
                for (int i = 0; i < insts.Count; i++)
                    names.Append("\n  [").Append(i).Append("] ").Append(insts[i].Name)
                         .Append(" asm=").Append(insts[i].AssemblyName).Append(" loc=").Append(insts[i].Location);
                var n = names.ToString();
                CoopLog.Info("modmenu.runtime", () => $"selftest: melons={insts.Count}{n}");
            }
            else if (_selfTestStage == 1 && _selfT > 10f)
            {
                _selfTestStage = 2;
                var e = FindSelfTestTarget();
                if (e == null) { CoopLog.Warn("modmenu.runtime", () => $"selftest: no target matched '{_selfTestRuntime}'"); return; }
                CoopLog.Info("modmenu.runtime", () => $"selftest: target '{e.Id}' loaded={e.Loaded} fmt={e.Format}");
                if (!string.Equals(_selfTestRuntime, "list", StringComparison.OrdinalIgnoreCase))
                {
                    var note = ModFileState.TrySetRuntime(e, true);
                    CoopLog.Info("modmenu.runtime", () => $"selftest(STOP): {note}");
                }
            }
            else if (_selfTestStage == 2 && _selfT > 14f)
            {
                _selfTestStage = 3;
                if (string.Equals(_selfTestRuntime, "list", StringComparison.OrdinalIgnoreCase)) { _selfTestRuntime = null; return; }
                var e = FindSelfTestTarget();
                if (e == null) return;
                var note = ModFileState.TrySetRuntime(e, false);
                CoopLog.Info("modmenu.runtime", () => $"selftest(RESUME): {note}");
                _selfTestRuntime = null;
            }
        }
        catch (Exception ex) { CoopLog.Warn("modmenu.runtime", () => "selftest failed: " + ex.Message); }
    }

    private ModEntryInfo FindSelfTestTarget()
    {
        if (string.IsNullOrEmpty(_selfTestRuntime)) return null;
        var all = ModInventory.Entries;
        for (int i = 0; i < all.Length; i++)
        {
            var e = all[i];
            string id = e.Id ?? "";
            if (id.IndexOf(_selfTestRuntime, StringComparison.OrdinalIgnoreCase) >= 0) return e;
            if (!string.IsNullOrEmpty(e.DisplayName) && e.DisplayName.IndexOf(_selfTestRuntime, StringComparison.OrdinalIgnoreCase) >= 0) return e;
        }
        return null;
    }

    // TEMP-VERIFY：完整启停自测 —— `-onnmm-selftest-toggle=<id 匹配串>`
    //   8s：对匹配条目做一次真实启用（`ModFileState.TryToggle`：先改名再热加载）
    //   20s：再做一次真实停用（改名；能停运行中的会一并停）
    // 用来在不点 UI 的情况下验证"启用不需要重启"是否真的成立。
    private string _selfTestToggle;
    private float _toggleT;
    private int _toggleStage;

    private void TickSelfTestToggle(float dt)
    {
        _toggleT += dt;
        try
        {
            var e = FindToggleTarget();
            if (e == null && _toggleT > 8f) { CoopLog.Warn("modmenu.toggle", () => $"selftest-toggle: no target matched '{_selfTestToggle}'"); _selfTestToggle = null; return; }

            if (_toggleStage == 0 && _toggleT > 8f)
            {
                _toggleStage = 1;
                CoopLog.Info("modmenu.toggle", () => $"selftest-toggle: target '{e.Id}' enabled={e.Enabled} loaded={e.Loaded} fmt={e.Format} path={e.Path}");
                if (!e.Enabled)
                {
                    bool ok = ModFileState.TryToggle(e, out string msg);
                    CoopLog.Info("modmenu.toggle", () => $"selftest-toggle(ENABLE): ok={ok} msg='{msg}'");
                }
                else CoopLog.Info("modmenu.toggle", () => "selftest-toggle(ENABLE): already enabled, skipped");
            }
            else if (_toggleStage == 1 && _toggleT > 20f)
            {
                _toggleStage = 2;
                var e2 = FindToggleTarget();
                if (e2 == null) { _selfTestToggle = null; return; }
                CoopLog.Info("modmenu.toggle", () => $"selftest-toggle: target2 '{e2.Id}' enabled={e2.Enabled} loaded={e2.Loaded} fmt={e2.Format} path={e2.Path}");
                if (e2.Enabled)
                {
                    bool ok = ModFileState.TryToggle(e2, out string msg);
                    CoopLog.Info("modmenu.toggle", () => $"selftest-toggle(DISABLE): ok={ok} msg='{msg}'");
                }
            }
        }
        catch (Exception ex) { CoopLog.Warn("modmenu.toggle", () => "selftest-toggle failed: " + ex.Message); }
    }

    private ModEntryInfo FindToggleTarget()
    {
        if (string.IsNullOrEmpty(_selfTestToggle)) return null;
        var all = ModInventory.Entries;
        for (int i = 0; i < all.Length; i++)
        {
            var e = all[i];
            string id = e.Id ?? "";
            if (id.IndexOf(_selfTestToggle, StringComparison.OrdinalIgnoreCase) >= 0) return e;
            if (!string.IsNullOrEmpty(e.DisplayName) && e.DisplayName.IndexOf(_selfTestToggle, StringComparison.OrdinalIgnoreCase) >= 0) return e;
        }
        return null;
    }

    // TEMP-VERIFY：T5/T6/T8 自动化取证 —— `-onnmm-selftest-t56=<Id 匹配串>`
    //   7s  打开菜单 + 记录 T5 擦除守卫状态
    //   8.5s 选中匹配条目
    //   10s  切到“设置”页签 + 列出条目（证明配置读取成功）
    //   11.5s 点第一个可编辑控件（写回）
    //   13s  绕过缓存从磁盘重读该键（证明“真的落盘了”）
    //   14.5s T8：把该条目上移一位 + 打印有效顺序
    //   16s  注入一个真实 ESC 按键（keybd_event）
    //   18s  读状态：我们的菜单应已关闭、游戏 ESC 菜单应为未打开
    private string _selfTestT56;
    private bool _t56UiOnly;
    private float _t56;
    private int _t56Stage;

    private void TickSelfTestT56(float dt)
    {
        _t56 += dt;
        try
        {
            if (_t56Stage == 0 && _t56 > 7f)
            {
                _t56Stage = 1;
                if (!ModMenuUI.IsOpen) ModMenuUI.Open();
                CoopLog.Info("modmenu.selftest", () => $"t56#1 menu={ModMenuUI.SelfTestState()} | escape: {UiEscapeGuard.SelfTestState()}");
            }
            else if (_t56Stage == 1 && _t56 > 8.5f)
            {
                _t56Stage = 2;
                string hit = ModMenuUI.SelfTestSelect(_selfTestT56);
                CoopLog.Info("modmenu.selftest", () => $"t56#2 select '{_selfTestT56}' -> '{hit}' | {ModMenuUI.SelfTestState()}");
            }
            else if (_t56Stage == 2 && _t56 > 10f)
            {
                _t56Stage = 3;
                ModMenuUI.SelfTestTab(1);
                CoopLog.Info("modmenu.selftest", () => $"t56#3 settings tab | {ModMenuUI.SelfTestState()}\n{ModMenuSettingsView.SelfTestSummary()}");
            }
            else if (_t56Stage == 3 && _t56 > 11.5f)
            {
                _t56Stage = 4;
                CoopLog.Info("modmenu.selftest", () => $"t56#4 write: {ModMenuSettingsView.SelfTestStepFirstEditable()}");
                if (_t56UiOnly)
                {
                    CoopLog.Info("modmenu.selftest", () => "t56: UI-ONLY DONE（菜单保持打开、停在设置页，供截图）");
                    _selfTestT56 = null;
                }
            }
            else if (_t56Stage == 4 && _t56 > 13f)
            {
                _t56Stage = 5;
                CoopLog.Info("modmenu.selftest", () => $"t56#5 verify: {ModMenuSettingsView.SelfTestVerifyFromDisk()}");
            }
            else if (_t56Stage == 5 && _t56 > 14.5f)
            {
                _t56Stage = 6;
                string msg;
                bool ok = ModInitScheduler.Move(_selfTestTargetId(), -1, out msg);
                CoopLog.Info("modmenu.selftest", () => $"t56#6 order move-up ok={ok} msg='{msg}' order=[{string.Join(" < ", ModInitScheduler.EffectiveOrder)}] table={ModOrderTable.Describe()}");
            }
            else if (_t56Stage == 6 && _t56 > 16f)
            {
                // 注入的按键会走**系统输入队列** → 只有游戏窗口在前台时才会送到游戏。
                // 先等前台（外面用 PowerShell 抢前台配合），拿不到就老实记“跳过”，不假装验证过。
                if (_t56WaitStart <= 0f) _t56WaitStart = _t56;
                if (EnsureForeground())
                {
                    _t56Stage = 7;
                    PressEscape(true);
                    CoopLog.Info("modmenu.selftest", () => $"t56#7 ESC down | keySeen={EscapeKeyDown()} | {ModMenuUI.SelfTestState()}");
                }
                else if (_t56 - _t56WaitStart > 20f)
                {
                    _t56Stage = 9;
                    CoopLog.Warn("modmenu.selftest", () => "t56#7 ESC test SKIPPED: 拿不到窗口前台 → 注入按键不会到达游戏，无法自动验证 ESC 关菜单");
                    _selfTestT56 = null;
                }
                else if (_t56 - _t56LastRetry > 1f)
                {
                    _t56LastRetry = _t56;
                    CoopLog.Info("modmenu.selftest", () => "t56#7 waiting for game window to become foreground (for ESC injection)…");
                }
            }
            else if (_t56Stage == 7 && _t56 > 16.4f)
            {
                _t56Stage = 8;
                CoopLog.Info("modmenu.selftest", () => $"t56#7b ESC held | keySeen={EscapeKeyDown()} | {ModMenuUI.SelfTestState()}");
                PressEscape(false);
            }
            else if (_t56Stage == 8 && _t56 > 18.5f)
            {
                _t56Stage = 9;
                bool closed = !ModMenuUI.IsOpen;
                CoopLog.Info("modmenu.selftest", () => $"t56#8 AFTER-ESC closed={closed} | {ModMenuUI.SelfTestState()} | escape: {UiEscapeGuard.SelfTestState()}");
                CoopLog.Info("modmenu.selftest", () => "t56: DONE (expect closed=True, gameEscapeOpen=0)");
                _selfTestT56 = null;
            }
        }
        catch (Exception ex) { CoopLog.Warn("modmenu.selftest", () => "t56 failed: " + ex.Message); }
    }

    // TEMP-VERIFY：`-onnmm-selftest-tab=<details|settings|debug>[:<Id 匹配串>]`
    //   6s：开菜单 + 选中匹配条目 + 切到指定页签，之后不再做任何事（供截图 + 诊断快照日志）。
    private string _selfTestTab;
    private float _tabT;
    private bool _tabDone;

    private void TickSelfTestTab(float dt)
    {
        _tabT += dt;
        if (_tabDone || _tabT < 6f) return;
        _tabDone = true;
        try
        {
            string spec = _selfTestTab;
            _selfTestTab = null;
            ModMenuDebugView.SelfTestMode = true;

            string tab = spec, match = "";
            int colon = spec.IndexOf(':');
            if (colon >= 0) { tab = spec.Substring(0, colon).Trim(); match = spec.Substring(colon + 1).Trim(); }

            if (!ModMenuUI.IsOpen) ModMenuUI.Open();
            string hit = match.Length > 0 ? ModMenuUI.SelfTestSelect(match) : "";
            int idx = tab.Equals("debug", StringComparison.OrdinalIgnoreCase) || tab == "2" ? 2
                    : tab.Equals("settings", StringComparison.OrdinalIgnoreCase) || tab == "1" ? 1 : 0;
            ModMenuUI.SelfTestTab(idx);
            // UIKit 接管时上面两句只动了自带界面的状态 ⇒ 同步把 UIKit 那页定位到同一处
            // （否则 `-onnmm-selftest-tab=settings:…` 在装了 UIKit 的机器上等于什么都没发生）
            try { UIKitIntegration.SelfTestSelect(match, idx); } catch { }
            CoopLog.Info("modmenu.selftest", () => $"selftest-tab: '{tab}' idx={idx} match='{match}' hit='{hit}' | {ModMenuUI.SelfTestState()}");
        }
        catch (Exception ex) { CoopLog.Warn("modmenu.selftest", () => "selftest-tab failed: " + ex.Message); }
    }

    private string _t56TargetId = "";
    private string _selfTestTargetId()
    {
        if (_t56TargetId.Length > 0) return _t56TargetId;
        try
        {
            var all = ModInventory.Entries;
            for (int i = 0; i < all.Length; i++)
            {
                var id = all[i]?.Id ?? "";
                if (id.IndexOf(_selfTestT56 ?? "", StringComparison.OrdinalIgnoreCase) >= 0) { _t56TargetId = id; break; }
            }
        }
        catch { }
        return _t56TargetId;
    }

    // TEMP-VERIFY：`-onnmm-selftest-click=<行号>` —— 把**真实 OS 鼠标点击**注入到列表某一行中心，
    //   8s 移动到该行中心并按下 → 8.2s 抬起 → 10s 读回选中项（期望变成该行条目）。
    //   用途：验证点击闸（按住 2 帧 + 悬停稳定 + 冷却）不会把**真人点击**也滤掉。
    private string _selfTestClick;
    private float _clickT;
    private int _clickStage;
    private string _clickExpectId = "";

    private void TickSelfTestClick(float dt)
    {
        _clickT += dt;
        try
        {
            if (_clickStage == 0 && _clickT > 8f)
            {
                _clickStage = 1;
                int row = 1;
                int.TryParse(_selfTestClick, out row);
                if (!ModMenuUI.IsOpen) ModMenuUI.Open();
                if (!ModMenuUI.SelfTestRowScreenPoint(row, out float sx, out float sy))
                {
                    CoopLog.Warn("modmenu.selftest", () => $"selftest-click: row {row} has no rect (skip)");
                    _selfTestClick = null;
                    return;
                }
                _clickExpectId = ModMenuUI.SelfTestRowId(row);
                InjectMouse(sx, sy, true);
                CoopLog.Info("modmenu.selftest", () => $"selftest-click: row={row} expect='{_clickExpectId}' screen=({sx:F0},{sy:F0}) os=({_clickOsX},{_clickOsY}) DOWN | {ModMenuUI.SelfTestState()}");
            }
            else if (_clickStage == 1 && _clickT > 8.35f)
            {
                _clickStage = 2;
                InjectMouse(0f, 0f, false);
                CoopLog.Info("modmenu.selftest", () => "selftest-click: UP");
            }
            else if (_clickStage == 2 && _clickT > 10f)
            {
                _clickStage = 3;
                CoopLog.Info("modmenu.selftest", () => $"selftest-click: RESULT expect='{_clickExpectId}' | {ModMenuUI.SelfTestState()}");
                _selfTestClick = null;
            }
        }
        catch (Exception ex) { CoopLog.Warn("modmenu.selftest", () => "selftest-click failed: " + ex.Message); }
    }

    private struct RECT { public int Left, Top, Right, Bottom; }
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    private static int _clickOsX, _clickOsY;

    /// <summary>把 OS 光标移到（Unity 屏幕坐标）并按下/抬起——与真人鼠标点走同一条输入路径。</summary>
    private static void InjectMouse(float screenX, float screenY, bool down)
    {
        try
        {
            if (down)
            {
                var h = OurHwnd();
                var r = new RECT();
                GetClientRect(h, out r);
                int clientH = r.Bottom - r.Top;
                // Unity 屏幕坐标是左下原点且基于渲染分辨率；客户端像素是左上原点 → 需要按比例换算（通常 1:1）
                float k = clientH > 0 && UnityEngine.Screen.height > 0 ? (float)clientH / UnityEngine.Screen.height : 1f;
                var p = new POINT { X = (int)(screenX * k), Y = (int)((UnityEngine.Screen.height - screenY) * k) };
                ClientToScreen(h, ref p);
                _clickOsX = p.X; _clickOsY = p.Y;
                SetCursorPos(p.X, p.Y);
                System.Threading.Thread.Sleep(60);          // 让 Input System 先看到位置
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);  // LEFTDOWN
            }
            else
            {
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);  // LEFTUP
            }
        }
        catch (Exception ex) { CoopLog.Warn("modmenu.selftest", () => "inject mouse failed: " + ex.Message); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    /// <summary>注入/释放 ESC（0x1B）——用来验证“游戏 ESC 菜单真的被拦住 + ESC 关掉我们的菜单”。
    /// ⚠️ 必须按住几帧再松：菜单读的是 `isPressed` 电平（自算边沿），瞬时按下+松开会在同一帧内看不见（已踩）。</summary>
    private static void PressEscape(bool down)
    {
        try { keybd_event(0x1B, 0, down ? 0u : 0x0002u, UIntPtr.Zero); }
        catch (Exception ex) { CoopLog.Warn("modmenu.selftest", () => "ESC inject failed: " + ex.Message); }
    }

    /// <summary>输入系统此刻是否看到 ESC 按住（用于区分“注入没到达”与“菜单没处理”）。</summary>
    private static string EscapeKeyDown()
    {
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return "no-keyboard";
            return kb.escapeKey.isPressed ? "yes" : "no";
        }
        catch (Exception ex) { return "err:" + ex.Message; }
    }

    private float _t56WaitStart, _t56LastRetry;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static IntPtr OurHwnd()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; } catch { return IntPtr.Zero; }
    }

    private static bool ForegroundIsOurs()
    {
        try
        {
            var h = OurHwnd();
            return h != IntPtr.Zero && GetForegroundWindow() == h;
        }
        catch { return false; }
    }

    /// <summary>尽力把游戏窗口抢到前台（拿不到就返回 false —— 调用方据此跳过注入式验证）。</summary>
    private static bool EnsureForeground()
    {
        try
        {
            if (ForegroundIsOurs()) return true;
            var h = OurHwnd();
            if (h == IntPtr.Zero) return false;
            SetForegroundWindow(h);
            return ForegroundIsOurs();
        }
        catch { return false; }
    }
}
