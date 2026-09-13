using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using OpenNestUIKit.Menu;
using OpenNestUIKit.Native;
using OpenNestUIKit.Theme;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestUIKit.Test;

/// <summary>
/// CLI 测试驱动（**模拟点击/拖拽/滚动** + 断言 + 报告）。
///
/// 命令行参数（正常启动无这些参数时本模组不打扰）：
/// <code>
/// -onuktest-autoopen              启动 6 秒后自动打开测试主页
/// -onuktest-page=&lt;pageId&gt;       启动 6 秒后自动打开指定页
/// -onuktest-zones                 打开菜单后打印一次热区清单
/// -onuktest-run=&lt;script&gt;        执行脚本（命令用 ';' 分隔，参数里的空格用 '_' 代替）
/// </code>
///
/// 脚本命令：
/// <code>
/// open[:pageId]      打开菜单（可直达某页）        close              关闭菜单
/// nav:&lt;pageId&gt;       进子页（页面栈 Push）         back               返回上一级
/// home               回主页                        wait:&lt;ms&gt;          等待
/// click:&lt;热区名片段&gt;  模拟真实点击（按下→抬起，走命中测试；用探针增量判定 PASS/FAIL）
/// tap:&lt;热区名片段&gt;    同上但不校验探针
/// move:&lt;热区名片段&gt;   只把指针移过去（悬停）        pointer:off         清除注入（回到真实鼠标）
/// drag:&lt;热区名片段&gt;:&lt;0..1&gt;  模拟拖拽到轨道比例（滑条）
/// scroll:&lt;片段&gt;:&lt;±N&gt;  模拟滚轮
/// zones              打印热区清单                  dump               打印状态摘要
/// winsize[:WxH]      改我们窗口逻辑尺寸（不带参数=报告）    winpos[:dx,dy]   移动窗口（避开别的模组窗口）
/// screen:WxH         改游戏分辨率（窗口化）          shot:&lt;名&gt;         截图到 OpenNestUIKitLogs\shots\
/// uiscale            打印“原生组件 vs 我们”的尺寸/字号/素材倍率（诊断观感差异）
/// nativemenu         原生菜单组件普查（各画布组件数/字号/素材/ppuMul）
/// menutree           原生菜单结构快照（ESC 容器逐个按钮：位置/间距/字号/底图/哪几个是我们注入的）
/// theme:on|off|recapture|clear  切换原生素材        inject             强制原生菜单注入
/// assertPage:&lt;pageId&gt;  断言当前页                 assert:&lt;探针键&gt;:&lt;次数&gt;  断言探针命中次数
/// report             写报告文件并汇总
/// </code>
/// </summary>
internal sealed class TestDriver
{
    private readonly Queue<string> _queue = new();
    private string _cur;
    private int _step;
    private float _timer;
    private float _keyReleaseAt;      // 注入按键的“抬起”计时（独立于脚本计时器）
    private int _probeBefore;
    private bool _finished;
    private float _autoOpenT;
    private bool _autoOpenPending;
    private string _autoOpenPage;
    private bool _zonesOnce;

    /// <summary>是否有活儿要干（无脚本 + 无自测参数时整个驱动都不跑）。</summary>
    public bool Active { get; private set; }

    public TestDriver(string[] args)
    {
        if (args == null) return;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i] ?? "";
            if (a.Equals("-onuktest-autoopen", StringComparison.OrdinalIgnoreCase))
            {
                _autoOpenPending = true;
                _autoOpenPage = _autoOpenPage ?? "test.home";
            }
            else if (a.StartsWith("-onuktest-page=", StringComparison.OrdinalIgnoreCase))
            {
                _autoOpenPending = true;
                _autoOpenPage = a.Substring("-onuktest-page=".Length).Trim();
            }
            else if (a.StartsWith("-onuktest-run=", StringComparison.OrdinalIgnoreCase))
            {
                LoadScript(a.Substring("-onuktest-run=".Length));
            }
            else if (a.Equals("-onuktest-zones", StringComparison.OrdinalIgnoreCase))
            {
                _zonesOnce = true;
            }
        }
        Active = _autoOpenPending || _queue.Count > 0 || _zonesOnce;
        if (Active)
            TestLog.Info($"测试驱动已装载：autoopen={_autoOpenPending} page='{_autoOpenPage}' 脚本命令={_queue.Count} zones={_zonesOnce}");
    }

    /// <summary>
    /// **自动取证看门狗**（不需要命令行）：原生 ESC 菜单**真的显示出来那一刻**（用户按 ESC）
    /// 自动存一张截图 + 把“注入行 vs 原生模板”的完整状态写进 test.log。
    ///
    /// 为什么要它：本机自动化到不了“游戏内按 ESC”那个状态（只能在题头页/主菜单，容器是关的），
    /// 而用户看到的问题只在那个状态下出现 —— 让用户按一下 ESC，事后再读日志/图即可。
    /// </summary>
    private void EscSnapWatch()
    {
        if (_escSnapDone) return;
        try
        {
            bool visible = false;
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = "";
                try { nm = t.name ?? ""; } catch { }
                if (!string.Equals(nm, "ESC Menu Buttons", StringComparison.Ordinal)) continue;
                bool act = false;
                try { act = t.gameObject != null && t.gameObject.activeInHierarchy; } catch { }
                if (act) { visible = true; break; }
            }
            if (!visible) { _escSnapFrames = 0; return; }
            if (++_escSnapFrames < 3) return;      // 等它稳定两三帧再抓
            _escSnapDone = true;

            var sb = new System.Text.StringBuilder();
            sb.Append("★ 原生 ESC 菜单已可见（自动取证）\n").Append(Native.UiNativeMetrics.DumpInjectedText());
            sb.Append("\n原生菜单结构：\n").Append(Native.UiNativeMetrics.DumpMenuTree());
            TestLog.Note("escsnap", sb.ToString());

#if !MELONLOADER
            try
            {
                string dir = System.IO.Path.Combine(OpenNestUIKit.Core.UiKitPaths.LogDir ?? ".", "shots");
                System.IO.Directory.CreateDirectory(dir);
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "escsnap.png"));
            }
            catch { }
#endif
        }
        catch { }
    }

    private bool _escSnapDone;
    private int _escSnapFrames;

    private void LoadScript(string script)
    {
        if (string.IsNullOrEmpty(script)) return;
        var parts = script.Split(';');
        for (int i = 0; i < parts.Length; i++)
        {
            string c = (parts[i] ?? "").Trim();
            if (c.Length == 0) continue;
            _queue.Enqueue(c);
        }
        TestLog.Note("script loaded", QueuedText());
    }

    private string QueuedText()
    {
        var arr = _queue.ToArray();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < arr.Length; i++) { if (i > 0) sb.Append("; "); sb.Append(arr[i]); }
        return sb.ToString();
    }

    /// <summary>每帧驱动。</summary>
    public void Tick(float dt)
    {
        if (!Active) return;
        EscSnapWatch();
        try
        {
            // 自动打开（等游戏起来 + 主菜单/场景就绪）
            if (_autoOpenPending)
            {
                _autoOpenT += dt;
                if (_autoOpenT > 6f)
                {
                    _autoOpenPending = false;
                    TestLog.Info($"autoopen page='{_autoOpenPage}'");
                    UiMenuWindow.Open(string.IsNullOrEmpty(_autoOpenPage) ? "test.home" : _autoOpenPage);
                    LogZonesOnce();
                }
                return;
            }

            if (_zonesOnce && UiMenuWindow.IsOpen)
            {
                _zonesOnce = false;
                LogZonesOnce();
            }

            if (_timer > 0f) { _timer -= dt; return; }

            // 注入按键的“抬起”：**必须用独立计时**，不能借 WaitThen（那是脚本单槽计时器，
            // 会被它的回调抢掉 → 脚本的 wait: 会提前结束、后续命令挤在一起。已踩过一次。）
            if (_keyReleaseAt > 0f)
            {
                _keyReleaseAt -= dt;
                if (_keyReleaseAt <= 0f)
                {
                    _keyReleaseAt = 0f;
                    try
                    {
                        var kb2 = UnityEngine.InputSystem.Keyboard.current;
                        if (kb2 != null)
                            UnityEngine.InputSystem.InputSystem.QueueStateEvent(kb2,
                                new UnityEngine.InputSystem.LowLevel.KeyboardState(new UnityEngine.InputSystem.Key[0]));
                    }
                    catch { }
                }
            }

            // 等待结束 → 执行挂起的校验回调（导航/悬停这类"稍后确认"的命令）
            if (_pending != null)
            {
                var p = _pending;
                _pending = null;
                try { p(); } catch (Exception ex) { TestLog.Warn("pending 回调异常：" + ex.Message); }
                Done();
                return;
            }

            if (_cur == null)
            {
                if (_queue.Count == 0)
                {
                    if (!_finished) Finish();
                    return;
                }
                _cur = _queue.Dequeue();
                _step = 0;
                TestLog.Note("cmd", _cur);
            }
            RunStep();
        }
        catch (Exception ex)
        {
            TestLog.Error($"driver tick 异常：{ex.Message}");
            _cur = null;
        }
    }

    private void LogZonesOnce()
    {
        var names = UiPointerRouter.ZoneNames;
        TestLog.Note("zones", names.Length + " 个 → " + string.Join(" | ", names));
    }

    private void Finish()
    {
        _finished = true;
        Active = false;
        TestLog.Flush();
        TestLog.Note("done", TestLog.Summary() + $"　探针总命中={TestProbe.Total}");
    }

    // ---------------- 命令执行 ----------------

    private void RunStep()
    {
        string cmd = _cur ?? "";
        string name, arg = null;
        int colon = cmd.IndexOf(':');
        if (colon > 0) { name = cmd.Substring(0, colon).ToLowerInvariant(); arg = cmd.Substring(colon + 1); }
        else name = cmd.ToLowerInvariant();
        arg = arg?.Replace('_', ' ');

        switch (name)
        {
            case "open":
                UiMenuWindow.Open(string.IsNullOrEmpty(arg) ? "test.home" : arg);
                TestLog.Pass("open " + (string.IsNullOrEmpty(arg) ? "test.home" : arg), "isOpen=" + UiMenuWindow.IsOpen + " page=" + UiMenuWindow.CurrentPage);
                Done();
                break;

            case "close":
                UiMenuWindow.Close();
                TestLog.Pass("close", "isOpen=" + UiMenuWindow.IsOpen);
                Done();
                break;

            case "nav":
                UiMenuWindow.Navigate(arg);
                WaitThen(0.35f, () =>
                {
                    if (string.Equals(UiMenuWindow.CurrentPage, arg, StringComparison.OrdinalIgnoreCase))
                        TestLog.Pass("nav " + arg, "page=" + UiMenuWindow.CurrentPage);
                    else
                        TestLog.Fail("nav " + arg, "当前页=" + UiMenuWindow.CurrentPage);
                });
                break;

            case "back":
                {
                    string before = UiMenuWindow.CurrentPage;
                    UiMenuWindow.Back();
                    WaitThen(0.3f, () => TestLog.Pass("back", $"'{before}' → '{UiMenuWindow.CurrentPage}'"));
                    break;
                }

            case "home":
                UiMenuWindow.Home();
                WaitThen(0.3f, () => TestLog.Pass("home", "page=" + UiMenuWindow.CurrentPage));
                break;

            case "wait":
                // ⚠️ 2026-09-13 修一个老 bug：这里以前写完 `_timer` 就调 `Done()`，而 `Done()` 会把 `_timer` 清零
                //    → **`wait:` 一直是个空操作**（“命令之间没真等”）。很多偶发问题（比如刚重建完页面就 tap → 找不到热区）
                //    都与此有关。现在自己收尾：清 `_cur/_step`，但**保留 `_timer`** 让 Update 真等完。
                _timer = ParseFloat(arg, 300f) / 1000f;
                _cur = null;
                _step = 0;
                break;

            case "winsize":
                WinSize(arg);
                Done();
                break;

            case "winpos":
                WinPos(arg);
                Done();
                break;

            case "screen":
                ScreenRes(arg);
                Done();
                break;

            case "shot":
                Shot(arg);
                break;          // Shot 内部等 2 帧（Unity 截图是异步写盘的）

            case "zones":
                LogZonesOnce();
                Done();
                break;

            case "pointer":
                if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase))
                {
                    UiPointerRouter.PointerOverride = null;
                    UiPointerRouter.ButtonOverride = false;
                }
                Done();
                break;

            case "move":
                StepMove(arg);
                break;

            case "click":
                StepClick(arg, verify: true);
                break;

            case "tap":
                StepClick(arg, verify: false);
                break;

            case "drag":
                StepDrag(arg);
                break;

            case "dragpick":
                StepDragPick(arg);
                break;

            case "vdrag":
                StepVDrag(arg);
                break;

            case "scroll":
                StepScroll(arg);
                break;

            case "theme":
                StepTheme(arg);
                break;

            case "inject":
                NativeMenuInjector.Inject(force: true);
                TestLog.Pass("inject", $"patched={NativeMenuInjector.IsPatched} injected={NativeMenuInjector.IsInjected} entries={NativeMenuBridge.Count}");
                Done();
                break;

            case "uninject":
                NativeMenuInjector.Detach();
                TestLog.Pass("uninject", "已移除我们注入的行（用来看原生行是否被动过）");
                Done();
                break;

            case "assertpage":
                {
                    bool ok = string.Equals(UiMenuWindow.CurrentPage, arg, StringComparison.OrdinalIgnoreCase);
                    if (ok) TestLog.Pass("assertPage " + arg);
                    else TestLog.Fail("assertPage " + arg, "当前页=" + UiMenuWindow.CurrentPage);
                    Done();
                    break;
                }

            case "assert":
                {
                    // assert:<探针键>:<次数>（键里不能带 ':'，用 '_' 代替空格）
                    string key = arg ?? "";
                    int want = 1;
                    int lastColon = key.LastIndexOf(':');
                    if (lastColon > 0)
                    {
                        string numPart = key.Substring(lastColon + 1);
                        if (int.TryParse(numPart, out int parsed)) { want = parsed; key = key.Substring(0, lastColon); }
                    }
                    int have = TestProbe.Count(key);
                    if (have >= want) TestLog.Pass($"assert {key} ≥ {want}", "实际=" + have);
                    else TestLog.Fail($"assert {key} ≥ {want}", "实际=" + have);
                    Done();
                    break;
                }

            case "dump":
                Dump();
                Done();
                break;

            case "visible":
                Visible();
                Done();
                break;

            case "rects":
                TestLog.Note("rects", Rects(arg));
                Done();
                break;

            case "mockcfg":
                // `mockcfg` = 缺失才写；`mockcfg!` = 强制重建（验收自动生成的配置控件用）
                MockConfig.Ensure(force: (arg ?? "").Trim() == "!");
                Done();
                break;

            case "imedecide":
                // `imedecide:<native>|<cached>|<cur>`（空格写 `_`）：**纯规则回归**，不靠真输入法。
                // 用来固定“原生串=拼音不追加 / 组合串=汉字才追加”这套判据（见 UiTextRouter.ImeDecision）。
                {
                    var parts = (arg ?? "").Split('|');
                    string nat = parts.Length > 0 ? parts[0].Replace('_', ' ') : "";
                    string cac = parts.Length > 1 ? parts[1].Replace('_', ' ') : "";
                    string cur = parts.Length > 2 ? parts[2].Replace('_', ' ') : "";
                    TestLog.Pass("imedecide", Widgets.UiTextRouter.ImeDecision(nat, cac, cur, ""));
                    Done();
                    break;
                }

            case "imefake":
                // `imefake:<文本>` 冒充 GCS_RESULTSTR（空格写 `_`；空 = 清除）。
                // 复现/回归“持值串回填”：同一条串会被反复返回 ⇒ 重新聚焦时**不得**再补进空框。
                {
                    string v = arg ?? "";
                    Widgets.UiTextRouter.FakeImeResult = v.Length == 0 ? null : v.Replace('_', ' ');
                    TestLog.Pass("imefake", Widgets.UiTextRouter.FakeImeResult == null ? "已清除（走系统 IME）" : $"已伪造='{Widgets.UiTextRouter.FakeImeResult}'");
                    Done();
                    break;
                }

            case "imecomp":
                // `imecomp:<文本>` 冒充**一帧**的 compositionString（下一帧消失 ⇒ 触发“组合结束”那条通道）。
                // 用来回归“同一次提交被两条通道各送一次”的重复问题（配 imefake 使用）。
                {
                    string v = arg ?? "";
                    Widgets.UiTextRouter.FakeComposition = v.Length == 0 ? null : v.Replace('_', ' ');
                    TestLog.Pass("imecomp", Widgets.UiTextRouter.FakeComposition == null ? "已清除" : $"已注入一帧组合串='{Widgets.UiTextRouter.FakeComposition}'");
                    Done();
                    break;
                }

            case "chain":
                TestLog.Note("chain", Chain());
                Done();
                break;

            case "escprobe":
                TestLog.Note("escprobe", EscProbe());
                Done();
                break;

            case "clipopen":
                TestLog.Pass("clipopen", ClipOpen());
                Done();
                break;

            case "pageprobe":
                TestLog.Note("pageprobe", NativeMenuPage.DumpRows());
                Done();
                break;

            case "perf":
                if (!string.IsNullOrEmpty(arg) && arg.Trim().Equals("reset", StringComparison.OrdinalIgnoreCase)) Core.Perf.Reset();
                TestLog.Note("perf", Core.Perf.Snapshot());
                Done();
                break;

            case "listprobe":
                TestLog.Note("listprobe", Widgets.UiList.ProbeAll()
                    + $"\n  滚轮：上次原始={UiPointerRouter.LastWheelRaw:0} → {UiPointerRouter.LastWheelNotches:0.##} 格，发给 '{UiPointerRouter.LastWheelZone}'"
                    + $"\n  拖拽：{UiPointerRouter.LastDragDiag}");
                Done();
                break;

            case "type":
                TestLog.Note("type", Widgets.UiTextInput.TryType(arg));
                Done();
                break;

            case "widgetprobe":
                TestLog.Note("widgetprobe",
                    Widgets.UiSlider.ProbeAll()
                    + "\n" + Widgets.UiTextInput.ProbeAll()
                    + "\n" + Widgets.UiTextRouter.Probe()
                    + "\n" + Widgets.UiChatOverlay.Probe()
                    + "\n" + OpenNestUIKit.Core.UiKitLoc.Probe()
                    + "\n" + Native.UiInputGuard.ProbeTextCapture()
                    + "\n" + Widgets.UiList.ProbeAll());
                Done();
                break;

            case "chatprobe":
                TestLog.Note("chatprobe", Widgets.UiChatOverlay.Probe());
                Done();
                break;

            case "lock":
                {
                    bool on = string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase);
                    Native.UiInputGuard.InteractionLockEnabled = on;
                    TestLog.Pass("lock", "交互锁=" + (on ? "开（旧行为：禁用场景交互组件）" : "关（默认；点击靠 LookAtTarget 前缀拦）"));
                    Done();
                }
                break;

            case "slices":
                TestLog.Note("slices", UiSliceStore.Dump());
                Done();
                break;

            case "slicereload":
                UiSliceStore.Load();
                UiSkin.InvalidateBaked();
                TestLog.Pass("slicereload", "count=" + UiSliceStore.Count);
                Done();
                break;

            case "sliceprobe":
                SliceProbe(arg);
                Done();
                break;

            case "tags":
                TestLog.Note("tags", TagsReport());
                Done();
                break;

            case "tagprobe":
                TagProbe(arg);
                Done();
                break;

            case "nativecheck":
                NativeCheck();
                Done();
                break;

            case "uiscale":
                TestLog.Note("uiscale", UiNativeMetrics.Dump());
                Done();
                break;

            case "nativemenu":
                TestLog.Note("nativemenu", UiNativeMetrics.DumpMenu());
                Done();
                break;

            case "menutree":
                TestLog.Note("menutree", UiNativeMetrics.DumpMenuTree());
                Done();
                break;

            case "esctext":
                TestLog.Note("esctext", UiNativeMetrics.DumpInjectedText());
                Done();
                break;

            case "escframe":
                EscFrame(string.IsNullOrEmpty(arg) ? "escframe" : arg);
                break;

            case "nativeopen":
                NativeOpen(arg);
                break;

            case "chatdemo":
                // 测试专用（2026-09-13 用户：“Chat 左侧框没有聊天记录 / 失去聚焦模式背景不够透明”）：
                // **不依赖联机会话**，直接给悬浮聊天层注册 N 条假消息（`arg` = 条数，默认 8）——
                // 让 `listprobe` / `chatprobe` / `shot:` 能确定性验证“框里到底有没有记录、收起态背景 alpha”。
                // 注：故意**不展开**（保持收起态）；要展开就接 `chatopen`。
                {
                    int n = 8;
                    if (!int.TryParse(arg, out n) || n <= 0) n = 8;
                    var demo = new List<string>();
                    for (int i = 1; i <= n; i++) demo.Add($"测试消息 {i} / sample line {i}");
                    Widgets.UiChatOverlay.SetFromHost("Chat", () => demo, _ => { }, () => "回车 打开聊天");
                    TestLog.Note("chatdemo", $"已注入 {n} 条假消息");
                    Done();
                }
                break;

            case "chatopen":
                // 测试专用：直接展开并聚焦悬浮聊天输入框（不靠回车）——用来把“回车没被看到”
                // 与“输入框/发送本身有问题”两件事分开验证。
                Widgets.UiChatOverlay.Focus();
                TestLog.Note("chatopen", Widgets.UiChatOverlay.Probe());
                Done();
                break;

            case "chatclose":
                // 测试专用（2026-09-13 用户：“Chat 失去焦点之后无法与菜单交互”）：
                // 直接收起悬浮聊天层（= ESC / 发送后的同一条路径），配合 `widgetprobe` 看
                // **输入管线聚焦**与**拦截层（游戏输入模块启用数）**是否回到常态。
                Widgets.UiChatOverlay.Close();
                TestLog.Note("chatclose", Widgets.UiChatOverlay.Probe());
                Done();
                break;

            case "key":
                SendKey(arg);
                break;

            case "clipraise":
                ClipRaise(arg);
                break;

            case "inactivetest":
                InactiveParentTest();
                break;

            case "clipcanvas":
                ClipCanvas(arg);
                break;

            case "nativebtn":
                NativeBtnProbe(arg);
                break;

            case "injectdiag":
                TestLog.Note("injectdiag", NativeMenuInjector.Diagnose());
                Done();
                break;

            case "escmode":
                {
                    bool want = !string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase);
                    TestLog.Pass("escmode", "面板" + (want ? "打开" : "关闭") + "=" + EscMode(want));
                    Done();
                }
                break;

            case "clicknative":
                TestLog.Pass("clicknative", ClickNative(arg));
                Done();
                break;

            // `clickgame:<对象名片段>`：点**游戏自己**的按钮（不是我们注入的）—— 例：`clickgame:OpenSettingsBtn`
            // 用来把剪贴板翻到原生 Settings 页，好在实机里对着原生控件比对我们复刻的控件（颜色/圆角/几何）。
            case "clickgame":
                TestLog.Pass("clickgame", ClickGameButton(arg));
                Done();
                break;

            case "nativeclick":                              // 点**游戏自己**的按钮（按 TMP 文案子串命中）
                TestLog.Note("nativeclick", NativeClick(arg));
                Done();
                break;

            case "clipboard":
                TestLog.Note("clipboard", UiNativeMetrics.DumpClipboard());
                Done();
                break;

            case "pagedump":
                TestLog.Note("pagedump", UiNativeMetrics.DumpPage(string.IsNullOrEmpty(arg) ? "Settings menu" : arg));
                Done();
                break;

            case "pagespec":                                 // 原生控件**样式规格**普查（尺寸/颜色/type/ppuMul 全量）
                TestLog.Note("pagespec", UiNativeMetrics.DumpSpecs());
                Done();
                break;

            case "pageopen":
                NativeMenuPage.DebugKeepAlive = true;       // 测试期别被 Tick 立刻复位（剪贴板没真打开）
                UiPointerRouter.IgnoreActiveForTest = true;  // 原生页在剪贴板里，测试环境画布未激活也允许命中
                TestLog.Pass("pageopen", "原生页=" + (NativeMenuPage.ShowFirst(arg) ? "已打开" : "打开失败"));
                Done();
                break;

            case "pageback":
                NativeMenuPage.Back();
                TestLog.Pass("pageback", "层级=" + NativeMenuPage.CurrentLevel + " shown=" + NativeMenuPage.IsShown);
                Done();
                break;

            case "nativew":                                  // 原生控件页演示（8 种控件一次全出现）
                NativeMenuPage.DebugKeepAlive = true;
                UiPointerRouter.IgnoreActiveForTest = true;
                NativeWidgets.Capture();
                TestLog.Pass("nativew", "控件页=" + (OpenNativeWidgetDemo() ? "已打开" : "打开失败")
                    + "；字体=" + NativeWidgets.FontName
                    + "；缺素材=" + NativeWidgets.MissingReport
                    + "；sprite=" + NativeWidgets.SpriteNames);
                Done();
                break;

            case "gallery":                                  // 原生 ESC 菜单里那个「控件 Gallery」条目（注册 + 直接打开）
                NativeMenuPage.DebugKeepAlive = true;
                UiPointerRouter.IgnoreActiveForTest = true;
                NativeGallery.Register();
                TestLog.Pass("gallery", "条目=" + (NativeGallery.Registered ? "已注册" : "未注册")
                    + "；页=" + (NativeGallery.Open() ? "已打开" : "打开失败")
                    + "；" + NativeGallery.State);
                Done();
                break;

            case "galleryoff":
                NativeGallery.Unregister();
                TestLog.Pass("galleryoff", "已撤下（registered=" + NativeGallery.Registered + "）");
                Done();
                break;

            case "pageclose":
                NativeMenuPage.Close();
                TestLog.Pass("pageclose", "shown=" + NativeMenuPage.IsShown);
                Done();
                break;

            case "hideothers":
                HideOthers(arg);
                Done();
                break;

            case "report":
                TestLog.Flush();
                TestLog.Note("report", TestLog.Summary());
                Done();
                break;

            default:
                TestLog.Warn("未知命令：" + cmd);
                Done();
                break;
        }
    }

    // ---------------- 模拟输入 ----------------

    /// <summary>
    /// 找热区：先按“必须激活”找；找不到再允许未激活的（测试环境里剪贴板画布不会被游戏真正打开，
    /// 但我们仍要能用坐标验证原生页那些行能不能点）。
    /// </summary>
    private static UiHotZone FindZoneForTest(string zoneName)
    {
        // 脚本会把参数里的 `_` 换成空格 → 比较时统一按 '_' 归一（与 ClickNative 一致）
        string want = zoneName != null ? zoneName.Replace(' ', '_') : zoneName;
        var z = UiPointerRouter.FindZone(want) ?? UiPointerRouter.FindZone(zoneName);
        if (z == null) z = UiPointerRouter.FindZone(want, requireActive: false) ?? UiPointerRouter.FindZone(zoneName, requireActive: false);
        return z;
    }

    private void StepMove(string zoneName)
    {
        var zone = FindZoneForTest(zoneName);
        if (zone == null)
        {
            TestLog.Fail("move " + zoneName, "找不到热区（用 zones 命令看清单）");
            Done();
            return;
        }
        if (!UiPointerRouter.TryGetScreenCenter(zone, out var pos))
        {
            TestLog.Fail("move " + zoneName, "取屏幕坐标失败");
            Done();
            return;
        }
        UiPointerRouter.PointerOverride = pos;
        WaitThen(0.1f, () =>
        {
            var hovered = UiPointerRouter.Hovered;
            string hname = hovered != null ? hovered.Name : "<none>";
            if (hovered != null && hovered.Name != null && hovered.Name.IndexOf(zoneName, StringComparison.OrdinalIgnoreCase) >= 0)
                TestLog.Pass("move " + zoneName, $"命中悬停 zone='{hname}' @{pos.x:0},{pos.y:0}");
            else
                TestLog.Fail("move " + zoneName, $"悬停实际为 '{hname}'（命中测试没落在这颗控件上）@ {pos.x:0},{pos.y:0}");
            UiPointerRouter.PointerOverride = null;
        });
    }

    private void StepClick(string zoneName, bool verify)
    {
        // step0：移到中心 + 按下；step1：抬起；step2：校验探针增量
        if (_step == 0)
        {
            var zone = FindZoneForTest(zoneName);
            if (zone == null)
            {
                TestLog.Fail("click " + zoneName, "找不到热区");
                Done();
                return;
            }
            if (!UiPointerRouter.TryGetScreenCenter(zone, out var pos))
            {
                TestLog.Fail("click " + zoneName, "取屏幕坐标失败");
                Done();
                return;
            }
            _probeBefore = TestProbe.Total;
            UiPointerRouter.PointerOverride = pos;
            UiPointerRouter.ButtonOverride = true;
            _step = 1;
            _timer = 0.05f;
            return;
        }
        if (_step == 1)
        {
            UiPointerRouter.ButtonOverride = false;
            _step = 2;
            _timer = 0.05f;
            return;
        }
        UiPointerRouter.PointerOverride = null;
        int delta = TestProbe.Total - _probeBefore;
        if (!verify) TestLog.Pass("tap " + zoneName, "probe+" + delta);
        else if (delta > 0) TestLog.Pass("click " + zoneName, "探针 +" + delta + "（点击真的走到了控件回调）");
        else TestLog.Fail("click " + zoneName, "探针 +0 —— 控件回调没被触发（若非探针控件请用 tap:）");
        Done();
    }

    private void StepDrag(string spec)
    {
        // spec = <热区片段>:<0..1>
        string zoneName = spec ?? "";
        float t = 0.8f;
        int idx = zoneName.LastIndexOf(':');
        if (idx > 0)
        {
            string tail = zoneName.Substring(idx + 1);
            if (float.TryParse(tail, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float parsed)) { t = parsed; zoneName = zoneName.Substring(0, idx); }
        }

        var zone = FindZoneForTest(zoneName);
        if (zone == null) { TestLog.Fail("drag " + zoneName, "找不到热区"); Done(); return; }
        var rect = zone.Rect;
        try
        {
            var cam = UiPointerRouter.CameraFor(rect);
            var world = rect.TransformPoint(new Vector2(rect.rect.xMin + 2f, rect.rect.center.y));
            var startPos = RectTransformUtility.WorldToScreenPoint(cam, world);
            var worldEnd = rect.TransformPoint(new Vector2(rect.rect.xMin + rect.rect.width * Mathf.Clamp01(t), rect.rect.center.y));
            var endPos = RectTransformUtility.WorldToScreenPoint(cam, worldEnd);

            if (_step == 0)
            {
                _probeBefore = TestProbe.Total;
                UiPointerRouter.PointerOverride = startPos;
                UiPointerRouter.ButtonOverride = true;
                _step = 1; _timer = 0.05f;
                return;
            }
            if (_step == 1)
            {
                UiPointerRouter.PointerOverride = endPos;   // 拖到目标比例
                _step = 2; _timer = 0.05f;
                return;
            }
            if (_step == 2)
            {
                UiPointerRouter.ButtonOverride = false;
                _step = 3; _timer = 0.05f;
                return;
            }
            UiPointerRouter.PointerOverride = null;
            int delta = TestProbe.Total - _probeBefore;
            if (delta > 0) TestLog.Pass("drag " + zoneName + " → " + t.ToString("0.##"), "探针 +" + delta);
            else TestLog.Fail("drag " + zoneName, "拖拽没有触发回调（探针 +0）");
            Done();
        }
        catch (Exception ex)
        {
            TestLog.Fail("drag " + zoneName, ex.Message);
            Done();
        }
    }

    /// <summary>
    /// `dragpick:&lt;热区片段&gt;:&lt;起点比例 0..1&gt;:&lt;终点比例 0..1&gt;`：**起点可指定**的水平拖拽。
    /// 用途（2026-09-13 用户：“只拖到 Handle 的时候似乎不更新值”）：把起点放在**手柄所在处**（= 当前值比例）
    /// 再拖到别处，验证“按住手柄”这条路径能不能改值；`drag:` 的起点固定在热区最左侧，复现不了。
    /// </summary>
    private void StepDragPick(string spec)
    {
        string s = spec ?? "";
        float x1 = 0.8f, x0 = 0.5f;
        // 从右往左剥两段数字（热区名里也可能有 ':'）
        int i2 = s.LastIndexOf(':');
        if (i2 > 0 && float.TryParse(s.Substring(i2 + 1), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float p2)) { x1 = p2; s = s.Substring(0, i2); }
        int i1 = s.LastIndexOf(':');
        if (i1 > 0 && float.TryParse(s.Substring(i1 + 1), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float p1)) { x0 = p1; s = s.Substring(0, i1); }

        var zone = FindZoneForTest(s);
        if (zone == null) { TestLog.Fail("dragpick " + s, "找不到热区"); Done(); return; }
        var rect = zone.Rect;
        try
        {
            var cam = UiPointerRouter.CameraFor(rect);
            var startW = rect.TransformPoint(new Vector2(rect.rect.xMin + rect.rect.width * Mathf.Clamp01(x0), rect.rect.center.y));
            var endW = rect.TransformPoint(new Vector2(rect.rect.xMin + rect.rect.width * Mathf.Clamp01(x1), rect.rect.center.y));
            var startPos = RectTransformUtility.WorldToScreenPoint(cam, startW);
            var endPos = RectTransformUtility.WorldToScreenPoint(cam, endW);

            if (_step == 0)
            {
                _probeBefore = TestProbe.Total;
                UiPointerRouter.PointerOverride = startPos;
                UiPointerRouter.ButtonOverride = true;
                _step = 1; _timer = 0.05f;
                return;
            }
            if (_step == 1)
            {
                UiPointerRouter.PointerOverride = endPos;
                _step = 2; _timer = 0.05f;
                return;
            }
            if (_step == 2)
            {
                UiPointerRouter.ButtonOverride = false;
                _step = 3; _timer = 0.05f;
                return;
            }
            UiPointerRouter.PointerOverride = null;
            int delta = TestProbe.Total - _probeBefore;
            string hit = UiPointerRouter.LastDragDiag;
            TestLog.Pass($"dragpick {s} {x0:0.##}→{x1:0.##}", $"探针 +{delta}；{hit}");
            Done();
        }
        catch (Exception ex) { TestLog.Fail("dragpick " + s, ex.Message); Done(); }
    }

    /// <summary>
    /// `vdrag:&lt;热区片段&gt;:&lt;像素&gt;`：在热区中心按下，把指针**向上**移 N 像素再松开
    /// （用于验证“按住拖动滚动”：手指上移 → 看后面的行）。
    /// </summary>
    private void StepVDrag(string spec)
    {
        string zoneName = spec ?? "";
        float px = 120f;
        int idx = zoneName.LastIndexOf(':');
        if (idx > 0)
        {
            string tail = zoneName.Substring(idx + 1);
            if (float.TryParse(tail, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float parsed)) { px = parsed; zoneName = zoneName.Substring(0, idx); }
        }
        var zone = FindZoneForTest(zoneName);
        if (zone == null) { TestLog.Fail("vdrag " + zoneName, "找不到热区"); Done(); return; }
        if (!UiPointerRouter.TryGetScreenCenter(zone, out var center)) { TestLog.Fail("vdrag " + zoneName, "取坐标失败"); Done(); return; }
        try
        {
            if (_step == 0)
            {
                UiPointerRouter.PointerOverride = center;
                UiPointerRouter.ButtonOverride = true;
                _step = 1; _timer = 0.06f;
                return;
            }
            if (_step == 1)
            {
                UiPointerRouter.PointerOverride = center + new Vector2(0f, px);   // 屏幕 y 向上
                _step = 2; _timer = 0.06f;
                return;
            }
            if (_step == 2)
            {
                UiPointerRouter.ButtonOverride = false;
                _step = 3; _timer = 0.06f;
                return;
            }
            UiPointerRouter.PointerOverride = null;
            TestLog.Pass("vdrag " + zoneName + " +" + px.ToString("0") + "px", Widgets.UiList.ProbeAll());
            Done();
        }
        catch (Exception ex) { TestLog.Fail("vdrag " + zoneName, ex.Message); }
    }

    private void StepScroll(string spec)
    {
        // spec = <热区片段>:<±N>（N = 滚动格数，原始值 = N*120）
        string zoneName = spec ?? "";
        float steps = 3f;
        int idx = zoneName.LastIndexOf(':');
        if (idx > 0)
        {
            string tail = zoneName.Substring(idx + 1);
            if (float.TryParse(tail, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float parsed)) { steps = parsed; zoneName = zoneName.Substring(0, idx); }
        }
        var zone = FindZoneForTest(zoneName);
        if (zone == null || zone.OnScroll == null) { TestLog.Fail("scroll " + zoneName, zone == null ? "找不到热区" : "该热区不支持滚轮"); Done(); return; }
        if (!UiPointerRouter.TryGetScreenCenter(zone, out var pos)) { TestLog.Fail("scroll " + zoneName, "取坐标失败"); Done(); return; }

        // 走**真实滚轮路径**：注入原始值（一格 = ±120），由路由器归一化后分发 → 能查出“方向/步长”对不对
        UiPointerRouter.PointerOverride = pos;
        UiPointerRouter.WheelOverride = steps * 120f;
        TestLog.Pass("scroll " + zoneName, $"注入原始滚轮 {steps * 120f:0}（1 格=120）");
        WaitThen(0.2f, () => UiPointerRouter.PointerOverride = null);
    }

    private void StepTheme(string arg)
    {
        switch ((arg ?? "").Trim().ToLowerInvariant())
        {
            case "on":
                UiSkin.UseNative = true;
                TestLog.Pass("theme on", "cached=" + UiSkin.Count);
                break;
            case "off":
                UiSkin.UseNative = false;
                TestLog.Pass("theme off");
                break;
            case "recapture":
                TestLog.Pass("theme recapture", "new=" + UiSkin.CaptureAll() + " cached=" + UiSkin.Count);
                break;
            case "clear":
                UiSkin.Clear();
                TestLog.Pass("theme clear", "cached=" + UiSkin.Count);
                break;
            default:
                TestLog.Warn("theme 参数应为 on|off|recapture|clear，收到：" + arg);
                break;
        }
        Done();
    }

    // ---------------- 工具 ----------------

    private void WaitThen(float seconds, Action then)
    {
        _step = 100;   // 用 _step 标记"等待回调"阶段
        _timer = seconds;
        _pending = then;
    }

    private Action _pending;

    private void Done()
    {
        _cur = null;
        _step = 0;
        _timer = 0f;
    }

    private void Dump()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("menu open=").Append(UiMenuWindow.IsOpen);
            sb.Append(" page=").Append(UiMenuWindow.CurrentPage);
            sb.Append(" order=").Append(UiMenuWindow.SortingOrder);
            sb.Append(" zones=").Append(UiPointerRouter.ZoneCount);
            sb.Append(" hovering=").Append(UiPointerRouter.Hovered != null ? UiPointerRouter.Hovered.Name : "-");
            sb.Append(" host=").Append(API.UiKitHost.IsHostAvailable ? API.UiKitHost.HostVersion : "offline");
            sb.Append(" pages=").Append(Menu.UiPageCatalog.Count);
            sb.Append(" skin=").Append(UiSkin.Count);
            sb.Append(" nativeInjected=").Append(NativeMenuInjector.IsInjected);
            sb.Append(" probeHits=").Append(TestProbe.Total);
            TestLog.Note("dump", sb.ToString());
        }
        catch (Exception ex) { TestLog.Warn("dump 失败：" + ex.Message); }
    }

    private static float ParseFloat(string s, float fallback)
    {
        if (float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v)) return v;
        return fallback;
    }

    /// <summary>取 "700x460" 的第 i 段（i=0 宽 / 1 高；也认 "," "×" "*" 分隔）。</summary>
    private static float ParsePart(string arg, int i, float fallback)
    {
        if (string.IsNullOrEmpty(arg)) return fallback;
        string a = arg.Replace("×", "x").Replace(",", "x").Replace("*", "x");
        string[] parts = a.Split('x');
        if (parts.Length <= i) return fallback;
        return ParseFloat(parts[i].Trim(), fallback);
    }

    /// <summary>
    /// 改我们菜单窗口的逻辑尺寸（`winsize:WxH`；不带参数=只报告当前尺寸）。
    /// 用于验证**不同尺寸下的布局/换行/滚动**是否正常（改完会重建页面 → 按新尺寸重新测量）。
    /// </summary>
    private void WinSize(string arg)
    {
        try
        {
            var before = UiMenuWindow.WindowSize;
            if (string.IsNullOrEmpty(arg))
            {
                TestLog.Note("winsize", $"当前窗口 {before.x:0}x{before.y:0}（屏幕 {Screen.width}x{Screen.height}）");
                ReportLayout("winsize-report");
                return;
            }
            float w = ParsePart(arg, 0, 0f), h = ParsePart(arg, 1, 0f);
            if (w <= 0f || h <= 0f)
            {
                TestLog.Fail("winsize", "格式应为 winsize:宽x高，例 winsize:700x460（收到 " + arg + "）");
                return;
            }
            bool ok = UiMenuWindow.SetWindowSize(w, h);
            var after = UiMenuWindow.WindowSize;
            if (ok)
                TestLog.Pass($"winsize {w:0}x{h:0}", $"{before.x:0}x{before.y:0} → {after.x:0}x{after.y:0}（屏幕 {Screen.width}x{Screen.height}）");
            else
                TestLog.Fail("winsize " + arg, "设置失败（菜单没开？尺寸过小？）");
        }
        catch (Exception ex) { TestLog.Fail("winsize " + arg, ex.Message); }
    }

    /// <summary>
    /// 测试用：临时藏掉**其它模组的 ScreenSpaceOverlay 画布**（例 Coop 大厅窗口盖住我们）→ 截图能看清我们的 UI。
    /// `hideothers:on` / `hideothers:off`；只动我们关掉的那些，off 时原样恢复。
    /// </summary>
    private static readonly List<GameObject> _hidden = new();
    private void HideOthers(string arg)
    {
        bool on = string.IsNullOrEmpty(arg) || arg.Equals("on", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (on)
            {
                var cs = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
                int n = 0;
                for (int i = 0; cs != null && i < cs.Length; i++)
                {
                    var c = cs[i];
                    if (c == null || c.gameObject == null) continue;
                    if (c.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                    string nm = "";
                    try { nm = c.gameObject.name ?? ""; } catch { }
                    if (nm.StartsWith("OpenNestUIKit_", StringComparison.Ordinal)) continue;   // 别藏自己
                    if (!c.gameObject.activeSelf) continue;
                    _hidden.Add(c.gameObject);
                    c.gameObject.SetActive(false);
                    n++;
                }
                TestLog.Pass("hideothers on", $"藏掉 {n} 个画布：{string.Join(", ", _hidden.ConvertAll(g => g.name))}");
            }
            else
            {
                for (int i = 0; i < _hidden.Count; i++)
                    try { if (_hidden[i] != null) _hidden[i].SetActive(true); } catch { }
                TestLog.Pass("hideothers off", $"恢复 {_hidden.Count} 个画布");
                _hidden.Clear();
            }
        }
        catch (Exception ex) { TestLog.Fail("hideothers " + arg, ex.Message); }
    }

    /// <summary>移动我们窗口（`winpos:dx,dy`，例 winpos:-460,0 避开别的模组窗口；`winpos:0,0` 回居中）。</summary>
    private void WinPos(string arg)
    {
        try
        {
            if (string.IsNullOrEmpty(arg))
            {
                var p0 = UiMenuWindow.WindowPos;
                TestLog.Note("winpos", $"当前窗口位置 ({p0.x:0},{p0.y:0})");
                return;
            }
            float dx = ParsePart(arg, 0, 0f), dy = ParsePart(arg, 1, 0f);
            bool ok = UiMenuWindow.SetWindowOffset(dx, dy);
            var p = UiMenuWindow.WindowPos;
            if (ok) TestLog.Pass($"winpos {dx:0},{dy:0}", $"→ ({p.x:0},{p.y:0})");
            else TestLog.Fail("winpos " + arg, "设置失败（菜单没开？）");
        }
        catch (Exception ex) { TestLog.Fail("winpos " + arg, ex.Message); }
    }

    /// <summary>改游戏分辨率（`screen:WxH`，窗口化）——验证真实小屏/大屏下的等比缩放。失败不致命。</summary>
    private void ScreenRes(string arg)
    {
        try
        {
            float w = ParsePart(arg, 0, 0f), h = ParsePart(arg, 1, 0f);
            if (w <= 0f || h <= 0f)
            {
                TestLog.Note("screen", $"当前分辨率 {Screen.width}x{Screen.height}（用法：screen:1600x900）");
                return;
            }
            Screen.SetResolution((int)w, (int)h, FullScreenMode.Windowed);
            WaitThen(0.5f, () => TestLog.Pass($"screen {w:0}x{h:0}", $"实际 {Screen.width}x{Screen.height}"));
        }
        catch (Exception ex) { TestLog.Fail("screen " + arg, ex.Message); }
    }

    /// <summary>
    /// **测试专用**：把原生 ESC 菜单的祖先链**临时激活**几帧 + 截一张图，然后**原样还原**。
    ///
    /// 为什么需要它（2026-09-13）：平时容器是关着的，`menutree` 里我们的行全是 `文本区=…/off`、
    /// “自然[字符数=0]”，光看属性没法判断用户**打开菜单那一刻看到什么**；
    /// 而游戏只在玩到一半按 ESC 时才打开它 —— 自动化里按不了 ESC，只能这样“举起来看一眼”。
    /// 只改 active、0.8 秒后还原（`EscMode` 早年的教训：改完不还原会让游戏的菜单状态错乱）。
    /// </summary>
    private void EscFrame(string name)
    {
        try
        {
            Transform esc = null;
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = "";
                try { nm = t.name ?? ""; } catch { }
                if (!string.Equals(nm, "ESC Menu Buttons", StringComparison.Ordinal)) continue;
                esc = t; break;
            }
            if (esc == null) { TestLog.Fail("escframe", "没找到 'ESC Menu Buttons'"); Done(); return; }

            var chain = new List<Transform>();
            for (var cur = esc; cur != null; cur = cur.parent) chain.Add(cur);
            var saved = new List<bool>();
            for (int k = 0; k < chain.Count; k++)
            {
                bool a = false;
                try { a = chain[k].gameObject.activeSelf; } catch { }
                saved.Add(a);
            }
            for (int k = 0; k < chain.Count; k++) { try { chain[k].gameObject.SetActive(true); } catch { } }

            string label = SafeName(name);
            string path = null;
            try
            {
                string dir = System.IO.Path.Combine(OpenNestUIKit.Core.UiKitPaths.LogDir ?? ".", "shots");
                System.IO.Directory.CreateDirectory(dir);
                path = System.IO.Path.Combine(dir, label + ".png");
                try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { } 
            }
            catch { }

            TestLog.Note("escframe", $"临时激活 '{Path(esc)}' 的 {chain.Count} 层祖先链 → 等几帧截图 → 还原");
            string p = path;
            WaitThen(0.35f, () =>
            {
#if MELONLOADER
                TestLog.Warn("escframe " + label + "：MLL 端无 ScreenCaptureModule，截图只在 BepInEx 端可用");
                Restore();
#else
                try { ScreenCapture.CaptureScreenshot(p); } catch (Exception ex) { TestLog.Warn("escframe 截图失败: " + ex.Message); }
                WaitThen(0.5f, () =>
                {
                    bool ok = p != null && System.IO.File.Exists(p);
                    long len = 0;
                    try { len = ok ? new System.IO.FileInfo(p).Length : 0; } catch { }
                    Restore();
                    if (ok && len > 0) TestLog.Pass("escframe " + label, $"{p} ({len / 1024}KB)（祖先链已还原）");
                    else TestLog.Fail("escframe " + label, "没写出文件：" + p);
                });
#endif
            });

            void Restore()
            {
                for (int k = 0; k < chain.Count; k++) { try { chain[k].gameObject.SetActive(saved[k]); } catch { } }
                Done();
            }
        }
        catch (Exception ex) { TestLog.Fail("escframe", ex.Message); Done(); }
    }

    /// <summary>
    /// **测试专用 · 根因验证**：在【inactive 父物体】下用 `CreateNativeButton` 建行（= 注入器建行的真实条件，
    /// 因为 ESC 容器平时是关着的），0.6s 后再把父物体激活（= 用户按 ESC 打开菜单），看文字到底出不出来。
    ///
    /// 依据：在游戏里真正打开 ESC 菜单时抓到的数据 —— 原生 `Text` 是 `字符数=8`，我们的 `字符数=0`
    /// （= TMP 从未生成字形）。怀疑是 TMP 的 `Awake` 没跑（在 inactive 层级下 `Awake` 会被推迟，
    /// 而 TMP 的 `OnPreRenderCanvas` 开头就有 `if (!m_isAwake) return;`）→ 之后激活也不重建。
    /// </summary>
    private void InactiveParentTest()
    {
        try
        {
            Transform esc = null;
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = "";
                try { nm = t.name ?? ""; } catch { }
                if (!string.Equals(nm, "ESC Menu Buttons", StringComparison.Ordinal)) continue;
                esc = t; break;
            }
            var template = esc != null ? Native.NativeMenuStyler.FindTemplate(esc, "OpenSettingsBtn", "Settings") : null;
            if (template == null) { TestLog.Fail("inactivetest", "拿不到原生模板按钮"); Done(); return; }

            var canvas = UI.UiKit.CreateCanvas("OpenNestUIKit_InactiveProbe", 32765);
            canvas.gameObject.SetActive(true);
            var root = canvas.GetComponent<RectTransform>();
            var panel = UI.UiKit.MakeRect("probe", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            try { panel.sizeDelta = new Vector2(420f, 260f); } catch { }
            var bg = panel.gameObject.AddComponent<UnityEngine.UI.Image>();
            bg.color = new Color(0.06f, 0.08f, 0.11f, 0.95f);

            // A：父物体 active 时创建（基线，应该正常）
            var a = Native.NativeMenuStyler.CreateNativeButton(panel, "ProbeA", "A 激活时建", template, null, 250f, 40f);
            try { a.GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, 60f); } catch { }
            var at = a.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);

            // B：父物体 **inactive** 时创建（= 注入器的真实条件）
            var holder = new GameObject("Holder");
            holder.transform.SetParent(panel, false);
            holder.SetActive(false);
            var b = Native.NativeMenuStyler.CreateNativeButton(holder.transform, "ProbeB", "B inactive 时建", template, null, 250f, 40f);
            try { b.GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, -20f); } catch { }
            var bt = b.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);

            TestLog.Note("inactivetest", $"建好（B 的父物体仍关着）：A字符数={Chars(at)} B字符数={Chars(bt)}（A 应 >0；若 B=0 → 命中根因）");

            WaitThen(0.6f, () =>
            {
                holder.SetActive(true);
                TestLog.Note("inactivetest", $"已把 B 的父物体激活：A字符数={Chars(at)} B字符数={Chars(bt)}"
                                             + (Chars(bt) == 0 ? "  ← B 永远不出字形（这就是“ESC 菜单注入项没字”）" : "  ← B 能出来"));
                string path = ShotPath("inactivetest");
#if MELONLOADER
                Done();
#else
                ScreenCapture.CaptureScreenshot(path);
                WaitThen(0.5f, () =>
                {
                    bool ok = path != null && System.IO.File.Exists(path);
                    if (ok) TestLog.Pass("inactivetest", path);
                    else TestLog.Warn("inactivetest：截图没写出（" + path + "）");
                    Done();
                });
#endif
            });
        }
        catch (Exception ex) { TestLog.Fail("inactivetest", ex.Message); Done(); }
    }

    private static int Chars(TMPro.TextMeshProUGUI t)
    {
        try { return t != null && t.textInfo != null ? t.textInfo.characterCount : -1; } catch { return -1; }
    }

    /// <summary>
    /// **测试专用 · 把游戏原生 ESC 画布“搬到屏幕上”看**：原生 ESC 菜单那套 UI 是
    /// **WorldSpace 画布**（`Canvas/scale=0.001`、挂在相机下，由 Animator 举到眼前），
    /// 平时“落着”的时候我们截不到它。这里把它临时改成 `ScreenSpaceOverlay`
    /// （并把祖先链激活），1 秒后截图，然后**原样还原**。
    ///
    /// 用途：终于能**亲眼看到**我们注入的行和原生按钮同框长什么样（字有没有、位置对不对）。
    /// </summary>
    private void ClipCanvas(string arg)
    {
        try
        {
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            int n = 0;
            string detail = "";
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = "";
                try { nm = t.name ?? ""; } catch { }
                if (!string.Equals(nm, "ESC Menu Buttons", StringComparison.Ordinal)) continue;
                var canvas = t.GetComponentInParent<Canvas>();
                if (canvas == null) { detail += " [无Canvas]"; continue; }
                n++;

                _clipSaved.Add(new ClipCanvasState
                {
                    Canvas = canvas,
                    Mode = canvas.renderMode,
                    Cam = canvas.worldCamera,
                    Enabled = canvas.enabled,
                    Plane = canvas.planeDistance,
                    Scale = canvas.transform.localScale,
                    SortingOrder = canvas.sortingOrder,
                });
                // 祖先链激活（记下来，最后还原）
                for (var cur = t; cur != null; cur = cur.parent)
                {
                    try
                    {
                        if (cur.gameObject != null && !cur.gameObject.activeSelf)
                        {
                            _clipActivated.Add(cur);
                            cur.gameObject.SetActive(true);
                        }
                    }
                    catch { }
                }
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.worldCamera = null;
                canvas.enabled = true;
                detail += $" '{t.name}'→Overlay";
            }
            TestLog.Note("clipcanvas", $"把 {n} 个 ESC 画布临时改成 ScreenSpaceOverlay{detail}；0.8s 后截图再还原");
            if (n == 0) { Done(); return; }

            string path = ShotPath("clipcanvas");
#if MELONLOADER
            WaitThen(0.8f, () => { RestoreClipCanvas(); Done(); });
#else
            WaitThen(0.8f, () =>
            {
                try { ScreenCapture.CaptureScreenshot(path); } catch (Exception ex) { TestLog.Warn("clipcanvas 截图失败: " + ex.Message); }
                WaitThen(0.6f, () =>
                {
                    bool ok = path != null && System.IO.File.Exists(path);
                    RestoreClipCanvas();
                    if (ok) TestLog.Pass("clipcanvas", path + "（已还原画布）");
                    else TestLog.Warn("clipcanvas：截图没写出（" + path + "），已还原画布");
                    Done();
                });
            });
#endif
        }
        catch (Exception ex) { RestoreClipCanvas(); TestLog.Fail("clipcanvas", ex.Message); Done(); }
    }

    private sealed class ClipCanvasState
    {
        public Canvas Canvas;
        public RenderMode Mode;
        public Camera Cam;
        public bool Enabled;
        public float Plane;
        public UnityEngine.Vector3 Scale;
        public int SortingOrder;
    }

    private readonly List<ClipCanvasState> _clipSaved = new List<ClipCanvasState>();
    private readonly List<Transform> _clipActivated = new List<Transform>();

    private void RestoreClipCanvas()
    {
        try
        {
            for (int i = 0; i < _clipSaved.Count; i++)
            {
                var s = _clipSaved[i];
                try
                {
                    if (s.Canvas == null) continue;
                    s.Canvas.renderMode = s.Mode;
                    s.Canvas.worldCamera = s.Cam;
                    s.Canvas.planeDistance = s.Plane;
                    s.Canvas.sortingOrder = s.SortingOrder;
                    s.Canvas.transform.localScale = s.Scale;
                    s.Canvas.enabled = s.Enabled;
                }
                catch { }
            }
            _clipSaved.Clear();
            for (int i = 0; i < _clipActivated.Count; i++) { try { if (_clipActivated[i] != null) _clipActivated[i].gameObject.SetActive(false); } catch { } }
            _clipActivated.Clear();
        }
        catch { }
    }

    /// <summary>
    /// **测试专用**：用游戏自己的 `ClipboardStateController.BeginSoftOverrideState(raised:true, focused:true, hidden:false, 时长)`
    /// 把剪贴板（也就是原生 ESC 菜单所在的那个世界空间面板）**举到相机前**。
    ///
    /// 为什么要它：注入行的问题**只在游戏内按 ESC 时**出现；自动化里按不了 ESC、也到不了游戏内，
    /// 之前只能“强开 Canvas”（菜单被 Animator 定位在屏幕外 → 截图空荡荡）。这个 API 是游戏自己升面板用的。
    /// `arg=down` 则降回去。
    /// </summary>
    private void ClipRaise(string arg)
    {
        try
        {
            bool down = string.Equals(arg, "down", StringComparison.OrdinalIgnoreCase);
            var type = Native.GameReflect.Find("ClipboardStateController");
            if (type == null) { TestLog.Fail("clipraise", "没找到游戏类型 ClipboardStateController"); Done(); return; }
            var miState = type.GetMethod("BeginSoftOverrideState", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var miRaised = type.GetMethod("BeginSoftOverrideRaised", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (miState == null && miRaised == null) { TestLog.Fail("clipraise", "没找到 BeginSoftOverride* 方法"); Done(); return; }

            var all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true);
            int n = 0;
            string detail = "";
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var c = all[i];
                if (c == null) continue;
                bool hit = false;
                try { hit = string.Equals(c.GetType().Name, "ClipboardStateController", StringComparison.Ordinal); } catch { }
                if (!hit) continue;
                n++;
                try
                {
                    if (miState != null) miState.Invoke(c, new object[] { !down, !down, down, 3600f });
                    else miRaised.Invoke(c, new object[] { !down, 3600f });
                    detail += " ok";
                }
                catch (Exception e) { detail += " [异常: " + e.Message + "]"; }
            }
            TestLog.Note("clipraise", $"命中 {n} 个剪贴板控制器 → {(down ? "降下" : "举起")}：{detail}");
            Done();
        }
        catch (Exception ex) { TestLog.Fail("clipraise", ex.Message); Done(); }
    }
    /// —— 等价于玩家按 ESC（游戏自己把剪贴板面板摆到相机前），于是能**真的截图看到**注入行长什么样。
    /// `arg=keep` 则只打开不关（后续命令继续用）；否则截图后自动 `ForceClose(false)` 还原。
    /// </summary>
    private void NativeOpen(string arg)
    {
        try
        {
            var type = Native.GameReflect.Find("EscapeMenuToggleUnityEvent");
            if (type == null) { TestLog.Fail("nativeopen", "没找到游戏类型 EscapeMenuToggleUnityEvent"); Done(); return; }
            var miOpen = type.GetMethod("ForceOpen", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var miClose = type.GetMethod("ForceClose", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var piOpen = type.GetProperty("IsOpen", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (miOpen == null) { TestLog.Fail("nativeopen", "EscapeMenuToggleUnityEvent.ForceOpen 不存在"); Done(); return; }

            var all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true);
            int n = 0;
            string detail = "";
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var c = all[i];
                if (c == null) continue;
                bool hit = false;
                try { hit = string.Equals(c.GetType().Name, "EscapeMenuToggleUnityEvent", StringComparison.Ordinal); } catch { }
                if (!hit) continue;
                n++;
                try { miOpen.Invoke(c, new object[] { true }); } catch (Exception e) { detail += " [open异常: " + e.Message + "]"; }
                try { detail += $" IsOpen={piOpen.GetValue(c)}"; } catch { }
                _escMenus.Add(c);
            }
            TestLog.Note("nativeopen", $"命中 {n} 个 ESC 菜单组件 → ForceOpen(true)：{detail}");

            bool keep = string.Equals(arg, "keep", StringComparison.OrdinalIgnoreCase);
            if (keep) { Done(); return; }

            string path = ShotPath("nativeopen");
#if MELONLOADER
            TestLog.Warn("nativeopen：MLL 端无 ScreenCaptureModule，只做开关验证");
            RestoreEscMenus();
#else
            WaitThen(0.4f, () =>
            {
                try { ScreenCapture.CaptureScreenshot(path); } catch (Exception ex) { TestLog.Warn("nativeopen 截图失败: " + ex.Message); }
                WaitThen(0.6f, () =>
                {
                    bool ok = path != null && System.IO.File.Exists(path);
                    RestoreEscMenus();
                    if (ok) TestLog.Pass("nativeopen", $"{path}（已 ForceClose 还原）");
                    else TestLog.Warn("nativeopen：截图没写出（" + path + "），仍已还原");
                });
            });
#endif
        }
        catch (Exception ex) { TestLog.Fail("nativeopen", ex.Message); Done(); }
    }

    private static readonly List<MonoBehaviour> _escMenus = new List<MonoBehaviour>();

    private void RestoreEscMenus()
    {
        try
        {
            for (int i = 0; i < _escMenus.Count; i++)
            {
                var c = _escMenus[i];
                if (c == null) continue;
                try
                {
                    var t = c.GetType();
                    var mi = t.GetMethod("ForceClose", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (mi != null) mi.Invoke(c, new object[] { false });
                }
                catch { }
            }
            _escMenus.Clear();
        }
        catch { }
        Done();
    }

    private static string ShotPath(string name)
    {
        try
        {
            string dir = System.IO.Path.Combine(OpenNestUIKit.Core.UiKitPaths.LogDir ?? ".", "shots");
            System.IO.Directory.CreateDirectory(dir);
            string p = System.IO.Path.Combine(dir, SafeName(name) + ".png");
            try { if (System.IO.File.Exists(p)) System.IO.File.Delete(p); } catch { }
            return p;
        }
        catch { return null; }
    }

    /// <summary>
    /// **测试专用 · 隔离复现**：在**我们自己的画布**里用**与注入器完全相同的调用**
    /// （`NativeMenuStyler.CreateNativeButton` + 同一颗原生模板按钮）造 3 行按钮，截图对照。
    ///
    /// 为什么：原生 ESC 菜单只有在游戏里按 ESC 才会打开（本机自动化到不了那个状态），
    /// 所以先在“看得见的地方”跑到同一段代码 —— 能看见 = 注入那段代码本身没问题，问题在游戏菜单的上下文
    /// （遮挡/裁剪/相机）；看不见 = 代码本身有问题，可以在这里快速迭代。
    /// </summary>
    private void NativeBtnProbe(string arg)
    {
        try
        {
            // 1) 找原生 ESC 容器 + 模板按钮（与注入器同款查找）
            Transform esc = null;
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = "";
                try { nm = t.name ?? ""; } catch { }
                if (!string.Equals(nm, "ESC Menu Buttons", StringComparison.Ordinal)) continue;
                esc = t; break;
            }
            if (esc == null) { TestLog.Fail("nativebtn", "没找到 ESC Menu Buttons（拿不到原生模板）"); Done(); return; }
            var template = Native.NativeMenuStyler.FindTemplate(esc, "OpenSettingsBtn", "Settings");
            if (template == null) { TestLog.Fail("nativebtn", "没找到模板按钮 OpenSettingsBtn"); Done(); return; }

            // 2) 我们的画布（独立、显眼，不受游戏菜单开关影响）
            var canvas = UI.UiKit.CreateCanvas("OpenNestUIKit_NativeProbe", 32765);
            canvas.gameObject.SetActive(true);
            var root = canvas.GetComponent<RectTransform>();
            var panel = UI.UiKit.MakeRect("probe", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            try { panel.sizeDelta = new Vector2(420f, 260f); panel.anchoredPosition = Vector2.zero; } catch { }
            var bg = panel.gameObject.AddComponent<UnityEngine.UI.Image>();
            bg.color = new Color(0.06f, 0.08f, 0.11f, 0.95f);

            string report = "";
            // 4 行对照：找出“我们的注入行到底哪一步变得不可见”
            //   R1: 手写构建（上一轮用户确认“没问题”的那条路）
            //   R2: NativeMenuStyler.CreateNativeButton（注入器走的路），250x40
            //   R3: 同上 + 注入器同款文字设置（字号 18 / 不换行 / Ellipsis）, 121x38
            //   R4: 同上但 Overflow（不裁剪）, 121x38
            for (int i = 0; i < 4; i++)
            {
                float w = i >= 2 ? 121f : 250f;
                float h = i >= 2 ? 38f : 40f;
                string label = "R" + (i + 1);
                RectTransform rt; UnityEngine.UI.Image img; UnityEngine.UI.Button btn; TMPro.TextMeshProUGUI tmp;
                if (i == 0)
                {
                    var go = new GameObject("Probe_" + i);
                    go.transform.SetParent(panel, false);
                    rt = go.AddComponent<RectTransform>();
                    rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.sizeDelta = new Vector2(w, h);
                    img = go.AddComponent<UnityEngine.UI.Image>();
                    img.color = new Color(0.16f, 0.20f, 0.26f, 0.96f);
                    btn = go.AddComponent<UnityEngine.UI.Button>();
                    Native.NativeMenuStyler.CopyButtonVisual(template, btn, img);
                    var txtGo = new GameObject("Text");
                    txtGo.transform.SetParent(go.transform, false);
                    var trt = txtGo.AddComponent<RectTransform>();
                    trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
                    trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
                    tmp = txtGo.AddComponent<TMPro.TextMeshProUGUI>();
                    tmp.text = label;
                    tmp.alignment = TMPro.TextAlignmentOptions.Center;
                    tmp.color = Color.white;
                    tmp.raycastTarget = false;
                    Native.NativeMenuStyler.CopyTextVisual(template, tmp);
                    Native.NativeMenuStyler.EnsureTextContrast(img, tmp);
                }
                else
                {
                    btn = Native.NativeMenuStyler.CreateNativeButton(panel, "Probe_" + i, label, template, null, w, h);
                    if (btn == null) { report += $"\n  [{i}] CreateNativeButton=null"; continue; }
                    rt = btn.GetComponent<RectTransform>();
                    img = btn.GetComponent<UnityEngine.UI.Image>();
                    tmp = btn.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
                    if (i >= 2 && tmp != null)
                    {
                        tmp.fontSize = 18f;
                        tmp.enableWordWrapping = false;
                        if (i == 2) tmp.overflowMode = TMPro.TextOverflowModes.Ellipsis;
                        tmp.text = label;
                    }
                }
                rt.anchoredPosition = new Vector2(0f, 140f - i * 56f);

                string info = "";
                try { info += $" img.enabled={img.enabled} img色={img.color}"; } catch { }
                try { info += $" imgAlpha={img.canvasRenderer.GetAlpha():0.##} cull={img.canvasRenderer.cull}"; } catch { }
                try { info += $" 过渡={btn.transition} normal={btn.colors.normalColor} target={(btn.targetGraphic != null ? btn.targetGraphic.gameObject.name : "null")}"; } catch { }
                try { info += $" 文本色={tmp.color} 文本Alpha={tmp.canvasRenderer.GetAlpha():0.##} 文本cull={tmp.canvasRenderer.cull} rect={tmp.rectTransform.rect.width:0}x{tmp.rectTransform.rect.height:0}"; } catch { }
                report += $"\n  [{i}] '{label}' {w:0}x{h:0}" + info;
            }
            TestLog.Note("nativebtn", "4 行对照（R1 手写 / R2 注入器同款 / R3 注入器文字设置 / R4 不裁剪）：" + report);

            // ★★ 根因对照（2026-09-13 定位）：注入行 = 121x38 格子 + 照抄模板内边距 → 文字框只有 81x18，
            //    而字号 18 的行高约 20 > 18 → `Ellipsis`（截断模式）会把整行裁掉 → 零字形（底图照画，所以“白框没字”）。
            //    这里并排放两行，只差“文字框高度”：
            //      X：旧行为（文字框 81x18 + Ellipsis）→ 应该没字
            //      Y：修法（文字框垂直铺满 121x38 + Ellipsis）→ 应该有字
            var xy = new (string Tag, bool FullHeight)[]
            {
                ("X 旧:文字框18高", false),
                ("Y 修:文字框铺满", true),
            };
            for (int i = 0; i < xy.Length; i++)
            {
                var v = xy[i];
                var btn = Native.NativeMenuStyler.CreateNativeButton(panel, "ProbeXY_" + i, v.Tag, template, null, 121f, 38f);
                if (btn == null) continue;
                try { btn.GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, -120f - i * 50f); } catch { }
                var t = btn.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
                if (t != null)
                {
                    t.fontSize = 18f;
                    t.enableWordWrapping = false;
                    t.overflowMode = TMPro.TextOverflowModes.Ellipsis;
                    if (v.FullHeight)
                    {
                        try
                        {
                            var tr = t.rectTransform;
                            var mn = tr.offsetMin; mn.y = 0f; tr.offsetMin = mn;
                            var mx = tr.offsetMax; mx.y = 0f; tr.offsetMax = mx;
                        }
                        catch { }
                    }
                    t.text = v.Tag;
                }
                try { TestLog.Note("nativebtn", $"{v.Tag}: 文字框={t.rectTransform.rect.width:0}x{t.rectTransform.rect.height:0} overflow={t.overflowMode} 字号={t.fontSize}"); } catch { }
                // ★ 客观判据（不信截图）：强制刷新网格后看 TMP 到底吐了多少行/多少字形/多少顶点。
                //    X（文字框 18 高 + Ellipsis）预期 lineCount=0 顶点=0 —— 整行被裁掉；Y（铺满）预期有行有顶点。
                try
                {
                    t.ForceMeshUpdate();
                    int chars = t.textInfo.characterCount;
                    int vis = 0;
                    for (int c = 0; c < chars; c++) if (t.textInfo.characterInfo[c].isVisible) vis++;
                    int verts = 0;
                    try { verts = t.textInfo.meshInfo[0].mesh.vertexCount; } catch { }
                    float lh = 0f;
                    try { if (t.textInfo.lineInfo != null && t.textInfo.lineInfo.Length > 0) lh = t.textInfo.lineInfo[0].lineHeight; } catch { }
                    TestLog.Note("nativebtn", $"{v.Tag}: 行数={t.textInfo.lineCount} 字符={chars} 可见字符={vis} 顶点={verts} 首选高={t.preferredHeight:0.#} 行高={lh:0.#}");
                }
                catch (Exception ex) { TestLog.Warn("nativebtn 字形统计失败: " + ex.Message); }
            }

            // ★ 关键对照实验：父物体 inactive 时创建 TMP → 之后再激活，文字还能出来吗？
            //    （注入器就是在 ESC 容器【关着】的时候建按钮的 —— 实测那时建的 TMP `字符数=0`，
            //      而原生模板 字符数=8；怀疑 TMP 的 Awake 没跑 → 永远不生成字形。）
            var holder = new GameObject("ProbeInactiveHolder");
            holder.transform.SetParent(panel, false);
            holder.SetActive(false);                                   // ← 先关掉（模拟 ESC 容器关着）
            var btnB = Native.NativeMenuStyler.CreateNativeButton(holder.transform, "Probe_B", "B 后激活", template, null, 250f, 40f);
            if (btnB != null)
            {
                var brt = btnB.GetComponent<RectTransform>();
                brt.anchoredPosition = new Vector2(0f, -110f);
                var bt = btnB.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
                int c0 = -1;
                try { c0 = bt != null && bt.textInfo != null ? bt.textInfo.characterCount : -1; } catch { }
                TestLog.Note("nativebtn", $"B 行已在【inactive 父物体】下创建：字符数={c0}（原生应为 8；0 = TMP 没生成）");
                // 等 0.5s 后激活 holder（模拟用户按 ESC 打开菜单），再截图看文字有没有
                WaitThen(0.5f, () =>
                {
                    holder.SetActive(true);
                    int c1 = -1;
                    try { c1 = bt != null && bt.textInfo != null ? bt.textInfo.characterCount : -1; } catch { }
                    TestLog.Note("nativebtn", $"B 行父物体已激活：字符数={c1}（若仍为 0 → 这就是“ESC 菜单注入项没字”的根因）");
                });
            }
            string path = ShotPath("nativebtn");
#if MELONLOADER
            TestLog.Warn("nativebtn：MLL 端无 ScreenCaptureModule，只做状态验证");
            Done();
#else
            WaitThen(0.35f, () =>
            {
                try { ScreenCapture.CaptureScreenshot(path); } catch (Exception ex) { TestLog.Warn("nativebtn 截图失败: " + ex.Message); }
                WaitThen(0.5f, () =>
                {
                    bool ok = path != null && System.IO.File.Exists(path);
                    if (ok) TestLog.Pass("nativebtn", path);
                    else TestLog.Warn("nativebtn：截图没写出（" + path + "）");
                    Done();
                });
            });
#endif
        }
        catch (Exception ex) { TestLog.Fail("nativebtn", ex.Message); Done(); }
    }

    /// <summary>
    /// **测试专用**：向游戏输入系统注入一次**真实按键**（`InputSystem.QueueStateEvent` + `KeyboardState`）。
    /// 用途：自动试验证“回车能不能发送/唤入聊天”这类**只靠改状态验证不了**的行为
    /// （`type:` 只是往输入框塞文本，走不到按键那条路）。arg 取 `enter` / `backspace` / `escape` / 单个字母。
    /// </summary>
    private void SendKey(string arg)
    {
        string step = "init";
        try
        {
            step = "取 Keyboard.current";
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) { TestLog.Fail("key " + arg, "Keyboard.current == null"); Done(); return; }

            step = "解析键名";
            UnityEngine.InputSystem.Key key;
            try { key = (UnityEngine.InputSystem.Key)Enum.Parse(typeof(UnityEngine.InputSystem.Key), KeyName(arg), true); }
            catch (Exception e) { TestLog.Fail("key " + arg, "Key 枚举里没有 " + KeyName(arg) + "：" + e.Message); Done(); return; }

            step = "构造 KeyboardState";
            var down = new UnityEngine.InputSystem.LowLevel.KeyboardState(new UnityEngine.InputSystem.Key[] { key });

            step = "QueueStateEvent(按下)";
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(kb, down);
            _keyReleaseAt = 0.25f;      // 按住 0.25s 再抬（跨多帧 → 上升沿检测才稳；由 Update 独立计时）
            TestLog.Pass("key " + arg, $"已注入真实按键 {KeyName(arg)}（InputSystem.QueueStateEvent · 按下）");
            Done();
        }
        catch (Exception ex) { TestLog.Fail("key " + arg, "[" + step + "] " + ex.Message); Done(); }
    }

    /// <summary>按“全名后缀”找类型（IL2CPP 端同一简单名可能有好几个，必须看命名空间）。
    /// ⚠ `Assembly.GetTypes()` 在 IL2CPP 程序集上常常抛 `ReflectionTypeLoadException`（部分类型加载不了）
    /// —— 必须用 `ex.Types`（能拿到的那部分），否则会把**整个程序集**跳过（已踩：InputSystem 就是这么丢的）。</summary>
    private static Type FindTypeBySuffix(string suffix)
    {
        try
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                Type[] ts = null;
                try { ts = asms[i].GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException rtle) { ts = rtle.Types; }
                catch { continue; }
                for (int k = 0; ts != null && k < ts.Length; k++)
                {
                    var t = ts[k];
                    if (t == null) continue;
                    string fn = t.FullName ?? "";
                    // IL2CPP 端的命名空间常常被改写成 `Il2CppInputSystem.*`（没有点分隔）→ 只按后缀比
                    if (fn == suffix || fn.EndsWith("." + suffix, StringComparison.Ordinal)
                        || fn.EndsWith(suffix, StringComparison.Ordinal)) return t;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>按“全名后缀 + 必须是枚举”找类型（`InputSystem.Key` 这类）。</summary>
    private static Type FindEnumType(string suffix)
    {
        try
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                Type[] ts = null;
                try { ts = asms[i].GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException rtle) { ts = rtle.Types; }
                catch { continue; }
                for (int k = 0; ts != null && k < ts.Length; k++)
                {
                    var t = ts[k];
                    if (t == null || !t.IsEnum) continue;
                    string fn = t.FullName ?? "";
                    if (fn == suffix || fn.EndsWith("." + suffix, StringComparison.Ordinal)
                        || fn.EndsWith(suffix, StringComparison.Ordinal)) return t;
                }
            }
        }
        catch { }
        return null;
    }

    private static string KeyName(string a)
    {        if (string.IsNullOrEmpty(a)) return "Enter";
        switch (a.Trim().ToLowerInvariant())
        {
            case "enter": case "return": return "Enter";
            case "numpadenter": return "NumpadEnter";
            case "escape": case "esc": return "Escape";
            case "backspace": return "Backspace";
            case "space": return "Space";
            default: return a.Trim().Length == 1 ? a.Trim().ToUpperInvariant() : a.Trim();
        }
    }

    /// <summary>截图到 &lt;Game&gt;\OpenNestUIKitLogs\shots\&lt;name&gt;.png（等几帧让 Unity 写真文件）。</summary>
    private void Shot(string name)    {
        if (string.IsNullOrEmpty(name)) name = "shot";
        string path = null;
        try
        {
            string dir = System.IO.Path.Combine(OpenNestUIKit.Core.UiKitPaths.LogDir ?? ".", "shots");
            System.IO.Directory.CreateDirectory(dir);
            path = System.IO.Path.Combine(dir, SafeName(name) + ".png");
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
#if MELONLOADER
            // MLL 端没有 UnityEngine.ScreenCaptureModule 程序集（Il2CppAssemblies 里缺这个模块）
            // → 截图命令只在 BepInEx 端可用；MLL 端用 winsize/layout 出数字结论。
            TestLog.Warn("shot " + SafeName(name) + "：MLL 端无 ScreenCaptureModule，截图只在 BepInEx 端可用");
            Done();
            return;
#else
            ScreenCapture.CaptureScreenshot(path);
            string p = path;
            string label = SafeName(name);
            WaitThen(0.4f, () =>
            {
                bool ok = System.IO.File.Exists(p);
                long len = 0;
                try { len = ok ? new System.IO.FileInfo(p).Length : 0; } catch { }
                if (ok && len > 0)
                    TestLog.Pass("shot " + label, $"{p} ({len / 1024}KB, 窗口 {UiMenuWindow.WindowSize.x:0}x{UiMenuWindow.WindowSize.y:0}, 屏幕 {Screen.width}x{Screen.height})");
                else
                    TestLog.Fail("shot " + label, "没写出文件：" + p);
            });
#endif
        }
        catch (Exception ex)
        {
            TestLog.Fail("shot " + name, ex.Message + " path=" + path);
            Done();
        }
    }

    private static string SafeName(string s)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            if (s.IndexOf(c) >= 0) s = s.Replace(c, '_');
        return s;
    }

    /// <summary>
    /// 把原生 ESC 面板（`ESC Menu Buttons` 的父层 `Canvas`）整体显隐 —— 测试用：
    /// 原生菜单平时由玩家按键打开，我们在无人值守跑测时手动点亮它，才能截到"注入后的原生菜单"。
    /// </summary>
    private static string EscMode(bool on)
    {
        try
        {
            // ⚠️ 只读！**绝不**改游戏节点的 active：早期版本在这里强开/强关面板 `Canvas`，
            // 会把游戏自己的“剪贴板开着/关着”状态搞乱（现象：之后按 ESC 菜单出不来了 = 像是崩了）。
            if (!on) Native.NativeMenuPage.Close();    // 关掉的只是**我们自己那一页**
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            int hit = 0;
            var sb = new System.Text.StringBuilder("（只读报告）");
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = "";
                try { nm = t.name ?? ""; } catch { }
                if (!string.Equals(nm, "ESC Menu Buttons", StringComparison.Ordinal)) continue;
                hit++;
                var panel = t.parent;
                bool pAct = false, cAct = false;
                try { pAct = panel != null && panel.gameObject != null && panel.gameObject.activeSelf; } catch { }
                try { cAct = t.gameObject != null && t.gameObject.activeSelf; } catch { }
                sb.Append(" [").Append(hit).Append("] 面板'").Append(panel != null ? panel.name : "-").Append("' active=").Append(pAct)
                  .Append(" 按钮列 active=").Append(cAct);
            }
            return sb.ToString();
        }
        catch (Exception ex) { return "EscMode: " + ex.Message; }
    }

    /// <summary>
    /// 点我们注入到原生菜单里的那一行（原生按钮不是本库的热区，`click:` 点不到）。
    /// <paramref name="arg"/> = 行名前缀/名字（空 = 第一行 `OpenNestUIKit_*`）；也支持 `#n`。
    /// </summary>
    private static string ClickNative(string arg)
    {
        try
        {
            // 脚本会把参数里的 `_` 换成空格（�script:`clicknative:row_x` → arg="row x"）→ 比较时统一按 '_' 归一下
            string want = string.IsNullOrEmpty(arg) ? "" : arg.Replace(' ', '_');
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = "";
                try { nm = t.name ?? ""; } catch { }
                if (!nm.StartsWith(Native.NativeMenuInjector.OurPrefix, StringComparison.Ordinal)) continue;
                if (want.Length > 0 && nm.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var b = t.GetComponent<Button>();
                if (b == null) continue;
                try { b.onClick.Invoke(); } catch (Exception e) { return "onClick 异常：" + e.Message; }
                return $"已点击 '{Path(t)}'（行内子文本='{TextOf(t)}'）";
            }
            return "没找到我们注入的原生行（前缀 " + Native.NativeMenuInjector.OurPrefix + "）";
        }
        catch (Exception ex) { return "ClickNative: " + ex.Message; }
    }

    /// <summary>点**游戏自己**的 UGUI 按钮（对象名包含片段；跳过我们注入的 `OpenNestUIKit_*`）。
    /// 用途：`clickgame:OpenSettingsBtn` → 剪贴板翻到原生 Settings 页，好在实机里和原生控件对照（颜色/圆角/几何）。</summary>
    private static string ClickGameButton(string arg)
    {
        try
        {
            string want = string.IsNullOrEmpty(arg) ? "" : arg.Replace(' ', '_');
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            string names = "";
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = "";
                try { nm = t.name ?? ""; } catch { }
                if (nm.StartsWith(Native.NativeMenuInjector.OurPrefix, StringComparison.Ordinal)) continue;   // 只看游戏的
                var b = t.GetComponent<Button>();
                if (b == null) continue;
                if (want.Length > 0 && nm.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                try { b.onClick.Invoke(); } catch (Exception e) { return "onClick 异常：" + e.Message + "（对象 " + nm + "）"; }
                return $"已点击游戏按钮 '{Path(t)}'（文本='{TextOf(t)}'）";
            }
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = ""; try { nm = t.name ?? ""; } catch { }
                if (nm.StartsWith(Native.NativeMenuInjector.OurPrefix, StringComparison.Ordinal)) continue;
                if (t.GetComponent<Button>() == null) continue;
                if (names.Length < 260) names += (names.Length > 0 ? ", " : "") + nm;
            }
            return $"没找到游戏按钮（片段='{want}'）；见过的按钮名：{names}";
        }
        catch (Exception ex) { return "ClickGameButton: " + ex.Message; }
    }

    private static string TextOf(Transform t)    {
        try
        {
            var txt = t.GetComponentInChildren<TMPro.TMP_Text>(true);
            return txt != null ? (txt.text ?? "") : "";
        }
        catch { return ""; }
    }

    /// <summary>点**游戏自己**的按钮（不是我们注入的）：按 TMP 文案子串命中第一个 Button 并 `onClick.Invoke()`。
    /// 用途：把原生 Settings 页打开（`nativeclick:Settings`），用于截图对照原生控件外观。</summary>
    private static string NativeClick(string arg)
    {
        try
        {
            string want = string.IsNullOrEmpty(arg) ? "" : arg.Replace('_', ' ');
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            // 两遍：先只认**激活**的（正常路径），没有再退到未激活的（ESC 菜单容器常整层未激活，
            // 但它的 `onClick` 照样能触发游戏的开关逻辑 —— 这是打开原生 Settings 页做外观对照的唯一办法）。
            for (int pass = 0; pass < 2; pass++)
            {
                bool requireActive = pass == 0;
                for (int i = 0; trs != null && i < trs.Length; i++)
                {
                    var t = trs[i];
                    if (t == null) continue;
                    string nm = "";
                    try { nm = t.name ?? ""; } catch { }
                    // 跳过我们自己注入的行（那些走 clicknative / 自管热区）
                    if (nm.StartsWith(Native.NativeMenuInjector.OurPrefix, StringComparison.Ordinal)) continue;
                    var b = t.GetComponent<Button>();
                    if (b == null) continue;
                    if (requireActive && !b.gameObject.activeInHierarchy) continue;
                    string txt = TextOf(t);
                    if (want.Length > 0 && txt.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try { b.onClick.Invoke(); } catch (Exception e) { return "onClick 异常：" + e.Message + " @" + Path(t); }
                    return $"已点 '{Path(t)}'（文='{txt}' active={b.gameObject.activeInHierarchy}）";
                }
            }
            return "没找到游戏按钮（子串='" + want + "'）";
        }
        catch (Exception ex) { return "NativeClick: " + ex.Message; }
    }

    /// <summary>演示：打开原生控件 Gallery 页（与 ESC 菜单里那个「控件 Gallery」条目同一份内容）。</summary>
    private static bool OpenNativeWidgetDemo()
    {
        try
        {
            Transform esc = null;
            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = ""; try { nm = t.name ?? ""; } catch { }
                if (string.Equals(nm, "ESC Menu Buttons", StringComparison.Ordinal)) { esc = t; break; }
            }
            if (esc == null) return false;
            var tpl = Native.NativeMenuStyler.FindTemplate(esc, "OpenSettingsBtn", "Settings");
            return NativeMenuPage.ShowRows(esc, tpl, "控件 Gallery", NativeGallery.BuildRows(), NativeGallery.BuildRows);
        }
        catch (Exception ex)
        {
            TestLog.Note("nativew", "OpenNativeWidgetDemo 失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 打印我们窗口下所有图形/文字的真实矩形与颜色（`rects[:数量]`）——
    /// 用来判断“内容看不见”到底是没建、摆错位、还是 alpha=0。
    /// </summary>
    private static string Rects(string arg)
    {
        int max = 40;
        if (!string.IsNullOrEmpty(arg) && int.TryParse(arg, out int n) && n > 0) max = n;
        try
        {
            var root = UiMenuWindow.CanvasRoot;
            if (root == null) return "窗口没开（CanvasRoot=null）";
            var sb = new System.Text.StringBuilder();
            int i = 0, tot = 0;
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int k = 0; all != null && k < all.Length; k++)
            {
                var t = all[k];
                if (t == null) continue;
                var rt = t.GetComponent<RectTransform>();
                var img = t.GetComponent<Image>();
                var txt = t.GetComponent<TMPro.TMP_Text>();
                if (rt == null) continue;
                bool graphic = img != null || txt != null;
                string nm = "";
                try { nm = t.name ?? ""; } catch { }
                bool chain = nm == "window" || nm == "content" || nm == "viewport" || nm == "header" || nm == "footer" || nm == "pages";
                if (!graphic && !chain) continue;
                tot++;
                if (i++ >= max) continue;
                string col = img != null
                    ? "img a=" + img.color.a.ToString("0.##") + " rgb=(" + img.color.r.ToString("0.##") + "," + img.color.g.ToString("0.##") + "," + img.color.b.ToString("0.##") + ") sprite=" + (img.sprite != null ? img.sprite.name : "无")
                    : (txt != null ? "txt a=" + txt.color.a.ToString("0.##") + " '" + Trim(txt.text) + "'" : "<矩形>");
                if (txt == null && img == null)
                    col = "anchors=(" + rt.anchorMin.x.ToString("0.##") + "," + rt.anchorMin.y.ToString("0.##") + ")-(" + rt.anchorMax.x.ToString("0.##") + "," + rt.anchorMax.y.ToString("0.##")
                        + ") off=" + rt.offsetMin.x.ToString("0") + "," + rt.offsetMin.y.ToString("0") + ".." + rt.offsetMax.x.ToString("0") + "," + rt.offsetMax.y.ToString("0")
                        + " sizeDelta=" + rt.sizeDelta.x.ToString("0") + "x" + rt.sizeDelta.y.ToString("0");
                sb.Append("\n  ").Append(Short(t)).Append(" rect=").Append($"{rt.rect.width:0}x{rt.rect.height:0}")
                  .Append($" pos=({rt.anchoredPosition.x:0},{rt.anchoredPosition.y:0})")
                  .Append(" active=").Append(t.gameObject.activeInHierarchy).Append(' ').Append(col);
            }
            sb.Insert(0, $"共 {tot} 个图形（列前 {Mathf.Min(max, tot)} 个）：");
            return sb.ToString();
        }
        catch (Exception ex) { return "Rects: " + ex.Message; }
    }

    private static string Trim(string s)
    {        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\n", " ");
        return s.Length > 18 ? s.Substring(0, 18) + "…" : s;
    }

    /// <summary>从第一块图形往上打印祖先链（名字/矩形/锚点/偏移）——定位“父链哪个节点尺寸不对”。</summary>
    private static string Chain()
    {
        try
        {
            var root = UiMenuWindow.CanvasRoot;
            if (root == null) return "窗口没开（CanvasRoot=null）";
            Transform deepest = null;
            var all = root.GetComponentsInChildren<Transform>(true);
            // 优先从列表视口往上（“内容看不见”多半是这条链上的尺寸不对）
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var t = all[i];
                if (t == null) continue;
                if (string.Equals(t.name, "viewport", StringComparison.Ordinal)) { deepest = t; break; }
            }
            if (deepest == null)
                for (int i = 0; all != null && i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null) continue;
                    if (t.GetComponent<TMPro.TMP_Text>() != null) { deepest = t; break; }
                }
            if (deepest == null) return "没找到文字节点";
            var sb = new System.Text.StringBuilder("从 '" + deepest.name + "' 往上：");
            var cur = deepest;
            for (int d = 0; cur != null && d < 8; d++)
            {
                var rt = cur.GetComponent<RectTransform>();
                sb.Append("\n  ").Append(new string(' ', d * 2)).Append(cur.name).Append(" rect=").Append(rt != null ? $"{rt.rect.width:0}x{rt.rect.height:0}" : "?")
                  .Append(" anchors=(").Append(rt != null ? $"{rt.anchorMin.x:0.##},{rt.anchorMin.y:0.##})-({rt.anchorMax.x:0.##},{rt.anchorMax.y:0.##}" : "?")
                  .Append(" off=").Append(rt != null ? $"{rt.offsetMin.x:0},{rt.offsetMin.y:0}..{rt.offsetMax.x:0},{rt.offsetMax.y:0}" : "?")
                  .Append(" sizeDelta=").Append(rt != null ? $"{rt.sizeDelta.x:0}x{rt.sizeDelta.y:0}" : "?")
                  .Append(" active=").Append(cur.gameObject.activeSelf);
                cur = cur.parent;
            }
            return sb.ToString();
        }
        catch (Exception ex) { return "Chain: " + ex.Message; }
    }

    /// <summary>
    /// ESC 全链路状态（只读）：我们窗口 / 我们的原生页 / ESC 守卫 / 游戏自己的 ESC 菜单 / 剪贴板面板与按钮列。
    /// 用来回答“ESC 到底是哪一环不对”：遮挡没放行、游戏 ESC 自己弹了、剪贴板没恢复……
    /// </summary>
    private static string EscProbe()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            sb.Append("窗口 open=").Append(UiMenuWindow.IsOpen).Append(" page='").Append(UiMenuWindow.CurrentPage).Append('\'');
            sb.Append(" | 原生页 shown=").Append(NativeMenuPage.IsShown).Append(" 层级='").Append(NativeMenuPage.CurrentLevel).Append('\'');
            sb.Append(" | 守卫 blocked=").Append(Native.UiEscapeGuard.IsBlocked).Append(" pendingRelease=").Append(Native.UiEscapeGuard.ReleasePending);
            // ★ ESC 层级（唯一真源）：无层级 = 0 时守卫必须已放行；有层级 = 我们占着 ESC（ESC 由我们逐层消费）
            sb.Append(" | ★层级=").Append(Native.UiEscapeLevels.Count).Append('[').Append(string.Join(",", Native.UiEscapeLevels.Owners)).Append(']');
            sb.Append(" | 游戏ESC open=").Append(UI.NativeUi.IsEscapeMenuOpen).Append(" blocked=").Append(UI.NativeUi.IsEscapeMenuBlocked);
            sb.Append(" | 指针注入=").Append(Native.UiPointerRouter.Injecting)
              .Append(" 自管指针=").Append(Native.UiPointerRouter.Active)
              .Append(" 残留热区=").Append(Native.UiPointerRouter.ZoneCount);
            // ★ “退到最外层后菜单不能交互”排查：拦截层的**残留**（关完页面后这三项必须回到 0/false）
            sb.Append(" | ★拦截层 禁用输入模块=").Append(Native.UiInputGuard.DisabledModuleCount)
              .Append(" 压制射线器=").Append(Native.UiInputGuard.SuppressedRaycasterCount)
              .Append(" 交互锁=").Append(UI.NativeUi.IsEscapeMenuBlocked || Native.UiInputGuard.InteractionLockEnabled)
              .Append(" 文本捕获=").Append(!string.IsNullOrEmpty(Native.UiInputGuard.ProbeTextCapture()) ? "on" : "off");

            var trs = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            int n = 0;
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i];
                if (t == null) continue;
                string nm = "";
                try { nm = t.name ?? ""; } catch { }
                if (!string.Equals(nm, "ESC Menu Buttons", StringComparison.Ordinal)) continue;
                n++;
                var panel = t.parent;
                bool p = false, c = false;
                try { p = panel != null && panel.gameObject.activeSelf; } catch { }
                try { c = t.gameObject.activeSelf; } catch { }
                string pages = "";
                try
                {
                    if (panel != null)
                        for (int k = 0; k < panel.childCount; k++)
                        {
                            var ch = panel.GetChild(k);
                            if (ch == null) continue;
                            pages += (k > 0 ? "," : "") + ch.name + "=" + ch.gameObject.activeSelf;
                        }
                }
                catch { }
                sb.Append("\n  [").Append(n).Append("] '").Append(Path(t)).Append("' 面板active=").Append(p)
                  .Append(" 按钮列active=").Append(c).Append(" 子=").Append(t.childCount).Append(" 页[").Append(pages).Append(']');

                // 剪贴板面板画布的渲染模式 / 射线器 —— 决定“我们那份原生页里的按钮靠什么收点击”
                try
                {
                    var cv = panel != null ? panel.GetComponent<Canvas>() : null;
                    if (cv != null)
                    {
                        string cam = "无";
                        try { cam = cv.worldCamera != null ? cv.worldCamera.name : "null"; } catch { }
                        sb.Append(" 画布[renderMode=").Append(cv.renderMode).Append(" worldCam=").Append(cam)
                          .Append(" scale=").Append(cv.scaleFactor.ToString("0.##")).Append(" order=").Append(cv.sortingOrder).Append(']');
                    }
                    var rcs = panel != null ? panel.GetComponentsInChildren<UnityEngine.UI.GraphicRaycaster>(true) : null;
                    if (rcs != null && rcs.Length > 0)
                    {
                        sb.Append(" 射线器[");
                        for (int k = 0; k < rcs.Length && k < 4; k++)
                            sb.Append(k > 0 ? "," : "").Append(rcs[k].gameObject.name).Append("=").Append(rcs[k].enabled);
                        sb.Append(']');
                    }
                }
                catch { }
            }
            if (n == 0) sb.Append("\n  没找到 ESC 容器");
        }
        catch (Exception ex) { sb.Append("\nEscProbe: ").Append(ex.Message); }
        return sb.ToString();
    }

    /// <summary>
    /// 尝试用**游戏自己的入口**打开剪贴板 ESC 菜单：找场景里路径含 `Clipboard` 的 `LookAtTarget`
    /// 并反射调 `OnClickDown()`（与玩家点书签同一入口）。成功后才能截到“真实原生菜单 + 我们注入的行”。
    /// </summary>
    private static string ClipOpen()
    {
        try
        {
            var type = GameReflect.Find("LookAtTarget");
            if (type == null) return "没找到 LookAtTarget（游戏类型未加载？）";
            var method = type.GetMethod("OnClickDown", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (method == null) return "LookAtTarget.OnClickDown 不存在";

            // 注意：`FindObjectsOfType(System.Type)` 不收 System.Type（要 Il2CppSystem.Type）→ 自己遍历 MonoBehaviour 按类型名筛
            var all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true);
            int n = 0;
            string first = null;
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var comp = all[i];
                if (comp == null) continue;
                bool isTarget = false;
                try { isTarget = string.Equals(comp.GetType().Name, "LookAtTarget", StringComparison.Ordinal); } catch { }
                if (!isTarget) continue;
                n++;
                var tr = comp.transform;
                string path = Path(tr);
                if (first == null) first = path;
                if (path.IndexOf("Clipboard", StringComparison.OrdinalIgnoreCase) < 0) continue;
                method.Invoke(comp, null);
                return $"已调 'Clipboard' LookAtTarget.OnClickDown（共 {n} 个 LookAtTarget，命中 '{path}'）";
            }
            return $"没找到名字/路径含 Clipboard 的目标（共 {n} 个，首个='{first}'）";
        }
        catch (Exception ex) { return "ClipOpen: " + ex.Message; }
    }

    private static string Short(Transform t)
    {
        try
        {
            var list = new List<string>();
            var cur = t;
            for (int i = 0; cur != null && i < 3; i++) { list.Insert(0, cur.name); cur = cur.parent; }
            return string.Join("/", list.ToArray());
        }
        catch { return t != null ? t.name : "?"; }
    }

    /// <summary>
    /// 布局体检：把当前屏幕上**我们所有图形的实际矩形**统计出来（越界/重叠/超宽都不靠肉眼猜）。
    /// 输出：图形数、超出窗口/屏幕的图形列表、按行统计的行高与横向溢出。
    /// </summary>
    private void ReportLayout(string tag)
    {
        try
        {
            var rect = UiMenuWindow.WindowRect != null ? UiMenuWindow.WindowRect : (UiMenuWindow.CanvasRoot);
            if (rect == null) { TestLog.Warn(tag + "：拿不到窗口矩形（菜单没开？）"); return; }
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                minX = Mathf.Min(minX, corners[i].x); maxX = Mathf.Max(maxX, corners[i].x);
                minY = Mathf.Min(minY, corners[i].y); maxY = Mathf.Max(maxY, corners[i].y);
            }
            var canvas = rect.GetComponentInParent<Canvas>();
            float sx = canvas != null && canvas.scaleFactor > 0f ? canvas.scaleFactor : 1f;
            var gs = rect.GetComponentsInChildren<UnityEngine.UI.Graphic>(false);
            int total = gs != null ? gs.Length : 0;
            int offscreen = 0, outsideWin = 0;
            var offenders = new System.Text.StringBuilder();
            for (int i = 0; i < total; i++)
            {
                var g = gs[i];
                if (g == null) continue;
                var gcorners = new Vector3[4];
                try { ((RectTransform)g.transform).GetWorldCorners(gcorners); } catch { continue; }
                float gx0 = gcorners[0].x, gy0 = gcorners[0].y, gx1 = gcorners[2].x, gy1 = gcorners[2].y;
                bool off = gx0 < 0f || gy0 < 0f || gx1 > Screen.width || gy1 > Screen.height;
                bool outWin = gx0 < minX - 1f || gx1 > maxX + 1f || gy0 < minY - 1f || gy1 > maxY + 1f;
                if (off) offscreen++;
                if (outWin) outsideWin++;
                if ((off || outWin) && offenders.Length < 700)
                    offenders.Append($"    '{Path(g.transform)}' screen=({gx0:0},{gy0:0})-({gx1:0},{gy1:0}) win=({minX:0},{minY:0})-({maxX:0},{maxY:0})")
                             .Append(off ? " [出屏!]" : "").Append(outWin ? " [出窗口]" : "").Append('\n');
            }
            TestLog.Note(tag ?? "layout", $"窗口 {UiMenuWindow.WindowSize.x:0}x{UiMenuWindow.WindowSize.y:0} pos=({UiMenuWindow.WindowPos.x:0},{UiMenuWindow.WindowPos.y:0}) scale={sx:0.##} 窗口屏幕区=({minX:0},{minY:0})-({maxX:0},{maxY:0}) 屏幕={Screen.width}x{Screen.height}\n"
                                         + $"  图形 {total} 个：出屏 {offscreen} / 超出窗口 {outsideWin}\n" + offenders);
        }
        catch (Exception ex) { TestLog.Warn(tag + " 失败：" + ex.Message); }
    }

    private static string Path(Transform t)
    {
        var sb = new System.Text.StringBuilder(t != null ? t.name : "?");
        var p = t != null ? t.parent : null;
        int guard = 0;
        while (p != null && guard++ < 6) { sb.Insert(0, p.name + "/"); p = p.parent; }
        return sb.ToString();
    }

    /// <summary>用途标记（tag）清单：每个 tag 有哪些素材，并标注该素材**是否已被捕获**（✗ = 定义现在不会生效）。</summary>
    private static string TagsReport()
    {
        var tags = UiSliceStore.Tags();
        var captured = new System.Collections.Generic.HashSet<string>(
            UiSkin.Names ?? new string[0], System.StringComparer.OrdinalIgnoreCase);
        var sb = new System.Text.StringBuilder();
        sb.Append($"用途标记 tag {tags.Length} 种（定义共 {UiSliceStore.Count} 条；已捕获素材 {captured.Count} 个）\n");
        for (int i = 0; i < tags.Length; i++)
        {
            var names = UiSliceStore.ByTag(tags[i]);
            sb.Append($"  {tags[i],-16} ");
            for (int k = 0; k < names.Length; k++)
                sb.Append(names[k]).Append(captured.Contains(names[k]) ? "✓ " : "✗未捕获 ");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>按 tag 取素材的验证：库里是否真的能按你标的用途挑到素材。</summary>
    private static void TagProbe(string tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            TestLog.Fail("tagprobe", "需要 tag，例：tagprobe:panel");
            return;
        }
        try
        {
            var names = UiSliceStore.ByTag(tag);
            var captured = new System.Collections.Generic.HashSet<string>(
                UiSkin.Names ?? new string[0], System.StringComparer.OrdinalIgnoreCase);
            var r = UiSkin.RefForTag(tag);
            TestLog.Note("tagprobe", $"tag='{tag}' 命中 {names.Length} 条：[{string.Join(", ", names)}] "
                                   + $"（✓捕获：{string.Join(", ", System.Array.FindAll(names, n => captured.Contains(n)))}）→ 采用 {r}");
            if (names.Length == 0)
                TestLog.Fail("tagprobe " + tag, "没有素材标这个 tag（先在切片工具里打标记）");
            else if (r.IsEmpty)
                TestLog.Fail("tagprobe " + tag, "有标记但取不到可用素材（该图未被捕获：主菜单场景里没用到它）");
            else
                TestLog.Pass("tagprobe " + tag, "按标记采用：" + r.Name);
        }
        catch (Exception ex) { TestLog.Fail("tagprobe " + tag, ex.Message); }
    }

    /// <summary>判断一个节点是否属于本模组（沿父链找 `OpenNestUIKit_` 前缀）。</summary>
    private static bool IsOurNode(UnityEngine.Transform t)
    {
        int guard = 0;
        while (t != null && guard++ < 32)
        {
            string n = t.name ?? "";
            if (n.StartsWith("OpenNestUIKit_", System.StringComparison.Ordinal))
                return true;
            t = t.parent;
        }
        return false;
    }

    /// <summary>
    /// 取证：本模组**没有改游戏原生 UI 的切片数据**。
    /// 分两类统计（我们的节点 vs 游戏自己的节点）：
    /// ① **游戏节点里有没有任何图用到我们 `Sprite.Create` 出的副本**（名字带 `@数字`）→ 必须 0；
    /// ② 我们的节点用了几张自己的副本（证明机制确实在跑）；
    /// ③ 游戏自己正在用、而我们 ini 里也定义过的图：它们的 `sprite.border` 仍是游戏原值。
    /// </summary>
    private static void NativeCheck()
    {
        try
        {
            var images = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Image>(true);
            int total = images != null ? images.Length : 0;
            int ourImgs = 0, gameImgs = 0, ourBakedOnGame = 0, ourBakedOnOurs = 0, gameSameName = 0;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < total; i++)
            {
                var img = images[i];
                var sp = img != null ? img.sprite : null;
                if (sp == null) continue;
                bool ours = IsOurNode(img.transform);
                if (ours) ourImgs++; else gameImgs++;
                string nm = "";
                try { nm = sp.name ?? ""; } catch { }
                bool isOurCopy = System.Text.RegularExpressions.Regex.IsMatch(nm, "@-?\\d");
                if (isOurCopy)
                {
                    if (ours) ourBakedOnOurs++;
                    else
                    {
                        ourBakedOnGame++;
                        if (ourBakedOnGame <= 5) sb.Append($"  ⚠ 游戏节点用了我们的副本：'{nm}'（节点 {img.name}）\n");
                    }
                    continue;
                }
                if (ours) continue;
                string key = nm;
                int at = key.IndexOf('@');
                if (at > 0) key = key.Substring(0, at);
                var slice = UiSliceStore.Get(key);
                if (!slice.HasValue) continue;
                gameSameName++;
                UnityEngine.Vector4 b = UnityEngine.Vector4.zero;
                try { b = sp.border; } catch { }
                if (gameSameName <= 6)
                    sb.Append($"  '{key}'（游戏节点 {img.name}）：游戏它自己用 border=({b.x:0},{b.y:0},{b.z:0},{b.w:0})"
                              + $"｜我们的定义 border={slice.BakedBorder}\n");
            }
            TestLog.Note("nativecheck", $"场景 Image={total}：我们的节点 {ourImgs} / 游戏节点 {gameImgs}\n"
                                        + $"  ① 游戏节点使用我们的切片副本 = {ourBakedOnGame}（必须 0）\n"
                                        + $"  ② 我们的节点使用自己的副本 = {ourBakedOnOurs}（>0 证明切片定义在生效）\n"
                                        + $"  ③ 与我们定义同名的游戏图 = {gameSameName}（它们的 border 仍是游戏原值）\n" + sb);
            if (ourBakedOnGame == 0)
                TestLog.Pass("nativecheck", $"游戏原生 UI 未被揻改；（我们的节点用自己的副本 {ourBakedOnOurs} 张）");
            else
                TestLog.Fail("nativecheck", $"有 {ourBakedOnGame} 张游戏节点用了我们的副本（异常）");
        }
        catch (Exception ex) { TestLog.Fail("nativecheck", ex.Message); }
    }

    /// <summary>
    /// 用来确认"切片工具导出的定义真的被库采用了"（日志里会出现 `（用户切片…）`）。
    /// `sliceprobe:名字` = 要求必须采用（没定义/没采用即 FAIL）；名字前加 `?` = 只观察不判定。
    /// </summary>
    private static void SliceProbe(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            TestLog.Fail("sliceprobe", "需要 sprite 名，例：sliceprobe:UI_Box_line");
            return;
        }
        bool expectAdopted = name[0] != '?';
        if (!expectAdopted) name = name.Substring(1).Trim();
        try
        {
            var slice = UiSliceStore.Get(name);
            var r = UiSkin.RefFor(name);
            var sb = new System.Text.StringBuilder();
            sb.Append($"sprite='{name}' author={UiSkin.AuthorBorder(name)} def={(slice.HasValue ? slice.ToString() : "<无>")}\n");
            sb.Append("  采用 → ").Append(r.IsEmpty ? "<纯色>" : r.ToString()).Append('\n');
            float[] hs = { 24f, 32f, 40f, 56f, 120f };
            for (int i = 0; i < hs.Length; i++)
            {
                var s = UiSkin.PickForSize(new[] { name }, 220f, hs[i]);
                sb.Append($"  h={hs[i]:0}: {(s != null ? s.name + " border=" + s.border : "纯色")}\n");
            }
            TestLog.Note("sliceprobe", sb.ToString());

            if (!expectAdopted)
            {
                TestLog.Pass("sliceprobe(观察) " + name, r.IsEmpty ? "纯色" : r.Name);
            }
            else if (slice.HasValue && r.FromUserSlice)
            {
                TestLog.Pass("sliceprobe " + name, "已采用切片定义：border=" + slice.BakedBorder);
            }
            else if (!slice.HasValue)
            {
                TestLog.Fail("sliceprobe " + name, "ini 里没有这条定义（检查 UiSliceStore.FilePath 指向的文件）");
            }
            else
            {
                TestLog.Fail("sliceprobe " + name, "有定义但未被采用（mode=none 或素材未捕获）");
            }
        }
        catch (Exception ex) { TestLog.Fail("sliceprobe " + name, ex.Message); }
    }

    /// <summary>
    /// 可见性探针：把"面板到底在不在屏幕上"变成日志事实（读图/截屏描述不可靠，用这个判定）。
    /// 输出：画布 active/enabled/sortingOrder/renderMode、子对象数、窗口与页面根的 active + 屏幕矩形。
    /// </summary>
    private static void Visible()
    {
        try
        {
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
            Canvas ours = null;
            for (int i = 0; i < canvases.Length; i++)
            {
                var c = canvases[i];
                if (c == null) continue;
                var root = c.transform.root;
                if (root != null && root.name.StartsWith("OpenNestUIKit", StringComparison.OrdinalIgnoreCase)) { ours = c; break; }
            }
            if (ours == null) { TestLog.Fail("visible", "找不到 OpenNestUIKit_UI 画布（菜单从未建过？）"); return; }

            var cgo = ours.gameObject;
            var crt = ours.GetComponent<RectTransform>();
            string rectInfo = "n/a";
            if (crt != null)
            {
                var c0 = UnityEngine.RectTransformUtility.WorldToScreenPoint(null, crt.TransformPoint(crt.rect.min));
                var c1 = UnityEngine.RectTransformUtility.WorldToScreenPoint(null, crt.TransformPoint(crt.rect.max));
                rectInfo = $"{c0.x:0},{c0.y:0} → {c1.x:0},{c1.y:0}";
            }
            int activeChildren = 0, graphics = 0;
            var gs = ours.GetComponentsInChildren<UnityEngine.UI.Graphic>(true);
            for (int i = 0; i < gs.Length; i++)
            {
                if (gs[i] == null) continue;
                if (gs[i].gameObject.activeInHierarchy) { graphics++; }
            }
            var kids = ours.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < kids.Length; i++)
                if (kids[i] != null && kids[i].gameObject.activeInHierarchy) activeChildren++;

            string screen = $"{UnityEngine.Screen.width}x{UnityEngine.Screen.height}";
            TestLog.Note("visible", $"canvas active={cgo.activeSelf}/{cgo.activeInHierarchy} enabled={ours.enabled} order={ours.sortingOrder} mode={ours.renderMode} screen={screen} canvasRect(screen)={rectInfo} activeChildren={activeChildren} activeGraphics={graphics} menuOpen={UiMenuWindow.IsOpen} page={UiMenuWindow.CurrentPage}");
            if (cgo.activeInHierarchy && ours.enabled) TestLog.Pass("visible", "画布在屏幕上（active+enabled）");
            else TestLog.Fail("visible", "画布未激活或未启用（面板不会显示）");
        }
        catch (Exception ex) { TestLog.Fail("visible", ex.Message); }
    }
}
