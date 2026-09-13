using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenNestCoop.Core;
using OpenNestCoop.Net;

namespace OpenNestCoop.UI;

/// <summary>
/// 与 <b>OpenNestUIKit</b> 的集成（**软依赖**，2026-09-13）。
///
/// 环境里有 UIKit → **整套联机菜单由 UIKit 渲染**；原生 ESC 里的入口也由 UIKit 注入；
/// 没有 UIKit → 回退本模组自带的 UGUI 面板（<see cref="CoopUIManager"/>）。
///
/// 布局对齐**原面板**（用户：“这两个模组接入改来的菜单层层嵌套的显示效果太差了，不如原来两个模组的布局方式”）：
/// 原面板就是**一个窗口 + 一个页签栏 + 一列内容**，所以 UIKit 版同样是**一页**
/// —— 不再“根页 → 子页 → 子页”逐层点进去：
/// - 页签：未联机 = [Steam 大厅][局域网][设置]；已联机 = [房间 / 成员][聊天]；
/// - 页签下面的内容全部渲染在**同一页**里（与原生面板的“切页签换内容”一致）；
/// - 原生 ESC 里的入口点一下**直开这一页**（叶子条目不再先经过原生页，少一层）。
///
/// 为什么用「反射探测 + NoInlining 隔离」：UIKit 是可选依赖（契约 dll 只随它一起装），
/// 在启动路径上直接触碰契约类型会在 JIT 阶段抛 <c>TypeLoadException</c> → 联机模组起不来。
/// </summary>
internal static class UIKitIntegration
{
    /// <summary>本模组在 UIKit 里注册的 provider Id（页面 id = <c>provider:&lt;Id&gt;</c>）。</summary>
    public const string ProviderId = "open-nest-coop";

    /// <summary>环境里有 UIKit（宿主程序集已加载）。</summary>
    public static bool Present { get; private set; }

    /// <summary>已把菜单注册进 UIKit。</summary>
    public static bool Connected { get; private set; }

    /// <summary>UIKit 宿主已就绪（此时“ESC 入口由 UIKit 注入”才可信）。</summary>
    public static bool HostReady { get; private set; }

    private static bool _absent;
    private static int _attempts;

    // 动态内容自动刷新：状态指纹 + 节流
    private static float _nextAuto;
    private static string _lastSig = "";

    /// <summary>诊断串。</summary>
    public static string Describe() => $"present={Present} connected={Connected} hostReady={HostReady}";

    /// <summary>宿主程序集名（**环境里有 UIKit 的标记**）。
    /// ⚠️ 双端名字不同：BepInEx 端 = `OpenNestUIKit`；MelonLoader 端 = `OpenNestUIKit.MelonMod`。</summary>
    private static bool IsHostAssemblyName(string n)
        => string.Equals(n, "OpenNestUIKit", StringComparison.OrdinalIgnoreCase)
        || string.Equals(n, "OpenNestUIKit.MelonMod", StringComparison.OrdinalIgnoreCase);

    /// <summary>每帧驱动（<c>CoopBehaviour.Update</c> 调用）：探测 + 接入 + 动态内容自动刷新。</summary>
    public static void Tick()
    {
        try
        {
            if (!Connected && !_absent)
            {
                if (_attempts >= 4) _absent = true;
                else
                {
                    _attempts++;
                    Present = HostAssemblyLoaded();
                    if (Present) TryConnect();
                    else if (_attempts >= 4)
                    {
                        _absent = true;
                        CoopLog.Info("coop.uikit", () => "环境里没有 OpenNestUIKit → 沿用本模组自带 UGUI 面板");
                    }
                }
            }
            if (Connected) AutoRefresh();
        }
        catch (Exception ex) { CoopLog.Warn("coop.uikit", () => "集成驱动失败: " + ex.Message); }
    }

    /// <summary>动态内容变了就原地刷新当前页（节流 0.6s；**打字中不刷**）。</summary>
    private static void AutoRefresh()
    {
        float t = 0f;
        try { t = UnityEngine.Time.unscaledTime; } catch { }
        if (t < _nextAuto) return;
        _nextAuto = t + 0.6f;

        string sig = Signature();
        if (sig == _lastSig) return;
        _lastSig = sig;
        try
        {
            if (!OpenNestUIKit.API.UiKitHost.IsMenuOpen) return;
            if (OpenNestUIKit.API.UiKitHost.IsTextInputFocused) return;      // 打字中 → 别冲掉草稿
            string page = OpenNestUIKit.API.UiKitHost.CurrentPageId;
            if (string.IsNullOrEmpty(page) || page.IndexOf(ProviderId, StringComparison.OrdinalIgnoreCase) < 0) return;
            // 帧剖析：整页重建很贵，单独归因（排查“进会话后掉帧”时先看这一项占了多少 ms/s）。
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { OpenNestUIKit.API.UiKitHost.Refresh(); }
            finally
            {
                OpenNestCoop.Core.FrameProfiler.Instance.AddMs("UiKitRefresh",
                    (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
        }
        catch { }
    }

    /// <summary>状态指纹：只看“会显示在界面上、且会自己变”的量。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string Signature()
    {
        try
        {
            var net = CoopRuntime.Net;
            if (net == null) return "null";
            int members = net.Roster != null ? net.Roster.Count : 0;
            int chat = net.ChatLog != null ? net.ChatLog.Count : 0;
            int browser = net.Browser != null ? net.Browser.Count : 0;
            int rooms = net.LanRooms != null ? net.LanRooms.Count : 0;
            return string.Concat(
                ((int)net.State).ToString(), "|", members, "|", chat, "|", browser, "|", rooms, "|",
                net.Refreshing ? "R" : "-", net.LanScanning ? "S" : "-", "|", net.LastError ?? "", "|",
                net.PendingLobbyName ?? "", "|", net.LanJoinHost ?? "", "|", net.LanJoinPort);
        }
        catch { return "err"; }
    }

    private static bool HostAssemblyLoaded()
    {
        try
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                string n = "";
                try { n = asms[i].GetName().Name; } catch { continue; }
                if (IsHostAssemblyName(n)) return true;
            }
        }
        catch { }
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TryConnect()
    {
        try
        {
            OpenNestUIKit.API.UiKitHost.Register(new CoopUiKitProvider());
            OpenNestUIKit.API.UiKitHost.Changed += OnHostChanged;
            Connected = true;
            HostReady = OpenNestUIKit.API.UiKitHost.IsHostAvailable;
            CoopLog.Info("coop.uikit", () => $"已接入 UIKit：provider='{ProviderId}'，整套联机菜单改由 UIKit 渲染（一页 + 页签）、原生 ESC 入口由 UIKit 注入（{Describe()}）");
        }
        catch (Exception ex)
        {
            Present = false;
            _absent = true;
            CoopLog.Warn("coop.uikit", () => "接入 UIKit 失败 → 回退自带面板: " + ex.Message);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OnHostChanged()
    {
        try { HostReady = OpenNestUIKit.API.UiKitHost.IsHostAvailable; } catch { }
    }

    /// <summary>ESC 入口是否已由 UIKit 接管（→ <c>MainMenuEntry</c> 就不该再自己注入一行）。</summary>
    public static bool EscEntryOwnedByUIKit
    {
        get { if (!Connected) return false; try { return HostReady; } catch { return false; } }
    }

    /// <summary>让 UIKit 开/关本模组菜单页；UIKit 不可用 → false（调用方走自带面板）。</summary>
    public static bool TryToggleInUIKit()
    {
        if (!Connected) return false;
        try { return ToggleInUIKit(); } catch { return false; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ToggleInUIKit()
    {
        if (!OpenNestUIKit.API.UiKitHost.CanControlMenu) return false;
        OpenNestUIKit.API.UiKitHost.ToggleMenu("provider:" + ProviderId);
        return true;
    }

    /// <summary>动作执行后叫 UIKit 重刷当前页（把新状态画出来）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void RequestRefresh()
    {
        try { OpenNestUIKit.API.UiKitHost.Refresh(); } catch { }
    }

    // ==================================================================================
    //  provider：**一页 + 页签**（对齐原面板布局；UIKit 负责渲染 + 原生 ESC 入口）
    // ==================================================================================

    private sealed class CoopUiKitProvider : OpenNestUIKit.API.UiKitProviderBase, OpenNestUIKit.API.IUiKitNativeEntry
    {
        public override string Id => ProviderId;
        public override string DisplayName => CoopLoc.MenuToggle;
        public override string Version => NetConfig.Version;
        public override string Author => "OpenNestCoop";

        // ---- 原生 ESC 菜单里的那一格（由 UIKit 注入；一行多列的其中一格） ----
        public bool ShowInNativeMenu => true;
        public int NativeOrder => 40;
        public string NativeTitle => CoopLoc.MenuToggle;
        public string NativeTitleEn => "Coop";

        // ---- 界面状态（provider 是单例：页签选择 / 聊天草稿都放这里） ----
        private static int _tabIdle;         // 0=Steam 大厅 1=局域网 2=设置
        private static int _tabSession;      // 0=房间/成员 1=聊天
        private static string _chatDraft = "";

        public override void BuildMenu(OpenNestUIKit.API.IUiMenuTree menu)
        {
            var net = SafeNet();
            var page = menu.Root;                 // ★ 整个菜单就这一页（不再有子页 → 不会层层嵌套）

            // ★ 布局照搬原模组自带面板（`CoopUIManager.Rebuild`）：
            //   原来是 **500 宽的窄面板 + 紧凑单栏**（行高 20~26、间隙 6、动作按钮右对齐、面板高度随内容）。
            //   原来 UIKit 版铺满 1180 → 行被拉得很宽、按钮飞到最右边（用户：“好多了，但还是怪怪的”）。
            page.Size(520f, 660f).SetCompact();

            page.Header(CoopLoc.Title);
            // 配色照搬原面板（用户：“联机菜单的各种颜色也没了”）：版本绿 + 加载器灰
            page.Label(Col(COk, "v" + NetConfig.Version) + "  " + Col(CMute, NetConfig.LoaderName));

            if (net == null)
            {
                page.Label(Col(CWarn, T("联机模块还没起来（等游戏加载完再打开）", "Coop module is not up yet (open after the game loads)")));
                return;
            }

            page.Label(T("玩家", "Player") + "：" + Col(CMute, Safe(() => net.LocalDisplayName))
                       + "    " + T("身份", "ID") + "：" + Col(CInfo, Safe(() => Identity.Tag(net.LocalIdentity))));
            page.Label(T("状态", "State") + "：" + Col(StateColor(net.State), StateText(net.State))
                       + "    Steam：" + (SafeBool(() => net.SteamReady)
                           ? Col(CSteamOk, T("已就绪", "ready"))
                           : Col(CWarn, T("初始化中…", "init…"))));
            string err = Safe(() => net.LastError);
            if (SafeBool(() => net.WasKicked)) err = CoopLoc.KickedHint;
            if (!string.IsNullOrEmpty(err)) page.Label(T("提示", "Notice") + "：" + Col(CErr, err));

            bool idle = net.State == SessionState.Idle;

            // ★ 悬浮聊天层（原模组是“左中常驻浮窗 + 回车唤入/发送/ESC 收起”，这里交给宿主渲染）：
            //   联机中注册，回到空闲就注销（空闲时没有聊天）。
            try
            {
                if (idle) OpenNestUIKit.API.UiKitHost.ClearChat();
                else
                {
                    var n = net;
                    OpenNestUIKit.API.UiKitHost.SetChat(
                        () => SafeList(() => n.ChatLog),
                        msg => { try { n.SendChat(msg); } catch { } },
                        CoopLoc.Chat,
                        () => CoopLoc.ChatEnterHint);
                }
            }
            catch { }

            if (idle)
            {
                // 页签栏（与原面板一致：Steam 大厅 / 局域网；设置放这里，避免“再点一层”）
                int tab = Math.Max(0, Math.Min(2, _tabIdle));
                page.Tabs("coop.tab", new[]
                {
                    TabLabel(0, tab, "Steam " + T("大厅", "Lobby")),
                    TabLabel(1, tab, CoopLoc.TabLan),
                    TabLabel(2, tab, T("设置", "Settings")),
                }, tab, i => { _tabIdle = i; RequestRefresh(); });

                if (tab == 0) BuildSteamTab(page, net);
                else if (tab == 1) BuildLanTab(page, net);
                else BuildSettingsTab(page, net);
            }
            else
            {
                int tab = Math.Max(0, Math.Min(1, _tabSession));
                page.Tabs("coop.tab", new[]
                {
                    TabLabel(0, tab, CoopLoc.Room + " / " + CoopLoc.Members),
                    TabLabel(1, tab, CoopLoc.Chat),
                }, tab, i => { _tabSession = i; RequestRefresh(); });

                if (tab == 0) BuildRoomTab(page, net);
                else BuildChatTab(page, net);
            }
        }

        // ---------------- 页签 1：Steam 大厅（对应原面板 Steam 页签） ----------------

        private static void BuildSteamTab(OpenNestUIKit.API.UiPageDef page, NetManager net)
        {
            page.Header(T("创建房间", "Create room"));
            page.Text("coop.roomName", CoopLoc.RoomNameLabel, Safe(() => net.PendingLobbyName),
                v => { try { net.PendingLobbyName = v ?? ""; } catch { } });
            page.Text("coop.roomPwd", CoopLoc.RoomPassword, Safe(() => net.PendingPassword),
                v => { try { net.PendingPassword = v ?? ""; } catch { } });
            page.Slider("coop.max", CoopLoc.MaxPlayers, SafeInt(() => net.PendingMaxPlayers), 2, 8, 1,
                v => { try { net.PendingMaxPlayers = (int)Math.Round(v); } catch { } });
            page.Button(CoopLoc.CreateLobby, T("创建", "create"), () =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(net.PendingLobbyName)) net.PendingLobbyName = CoopLoc.DefaultRoomName;
                    net.CreateLobby();
                }
                catch (Exception ex) { Log("create lobby", ex); }
                RequestRefresh();
            });

            page.Header(T("大厅列表", "Lobbies"));
            page.Button(CoopLoc.RefreshLobbies, T("刷新", "refresh"), () =>
            {
                try { net.RefreshBrowser(); } catch { }
                RequestRefresh();
            });
            if (SafeULong(() => net.LastLobbyId) != 0)
            {
                page.Button(CoopLoc.RejoinLast, T("重连", "rejoin"), () =>
                {
                    try { net.JoinRecentLobby(); } catch { }
                    RequestRefresh();
                });
            }
            if (SafeBool(() => net.Refreshing)) page.Label(Col(CSteamOk, CoopLoc.Refreshing));

            var browser = SafeList(() => net.Browser);
            if (browser.Count == 0)
            {
                page.Label(Col(CDim, CoopLoc.NoLobbies));
                return;
            }
            for (int i = 0; i < browser.Count; i++)
            {
                var info = browser[i];
                if (info == null) continue;
                string label = LobbyLabel(info);
                if (info.IsFull)
                {
                    page.Label(label + "  " + Col(CErr, CoopLoc.Full));   // 满员 → 红（原面板同色）
                    continue;
                }
                var target = info;
                page.Button(label, CoopLoc.Join, () =>
                {
                    try
                    {
                        if (target.HasPassword && !net.VerifyRoomPassword(target, net.PendingPassword))
                        {
                            net.LastError = CoopLoc.PasswordRequired + ": " + target.Name;
                            return;
                        }
                        net.JoinLobby(target);
                    }
                    catch (Exception ex) { Log("join lobby", ex); }
                    RequestRefresh();
                });
            }
        }

        // ---------------- 页签 2：局域网（对应原面板 LAN 页签） ----------------

        private static void BuildLanTab(OpenNestUIKit.API.UiPageDef page, NetManager net)
        {
            page.Header(T("本机", "This machine"));
            page.Label(CoopLoc.LanIdentity + "：" + Col(CInfo, Safe(() => Identity.Tag(net.LocalIdentity))));
            page.Text("coop.lanName", CoopLoc.LanName, Safe(() => net.LanLocalName),
                v => { try { net.LanLocalName = v ?? ""; } catch { } });

            try
            {
                var ips = LanTransport.LocalIPv4();
                if (ips != null && ips.Count > 0)
                {
                    page.Header(CoopLoc.LanMyIp);
                    for (int i = 0; i < ips.Count; i++)
                    {
                        string ipPort = ips[i] + ":" + SafeInt(() => net.LanJoinPort);
                        string copy = ipPort;
                        page.Button(Col(COk, ipPort), CoopLoc.Copy, () => { try { CoopUIManager.CopyToClipboard(copy); } catch { } });
                    }
                }
            }
            catch { }

            page.Header(T("创建局域网房间", "Create LAN room"));
            page.Text("coop.lanRoom", CoopLoc.RoomNameLabel, Safe(() => net.PendingLobbyName),
                v => { try { net.PendingLobbyName = v ?? ""; } catch { } });
            page.Text("coop.lanPwd", CoopLoc.RoomPassword, Safe(() => net.PendingPassword),
                v => { try { net.PendingPassword = v ?? ""; } catch { } });
            page.Slider("coop.lanMax", CoopLoc.MaxPlayers, SafeInt(() => net.PendingMaxPlayers), 2, 8, 1,
                v => { try { net.PendingMaxPlayers = (int)Math.Round(v); } catch { } });
            page.Button(CoopLoc.LanCreate, T("创建", "create"), () =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(net.PendingLobbyName)) net.PendingLobbyName = CoopLoc.DefaultRoomName;
                    net.CreateLanRoom();
                }
                catch (Exception ex) { Log("create lan room", ex); }
                RequestRefresh();
            });

            page.Header(T("加入局域网房间", "Join LAN room"));
            page.Text("coop.lanHost", CoopLoc.LanIpLabel, Safe(() => net.LanJoinHost),
                v => { try { net.LanJoinHost = v ?? ""; } catch { } });
            page.Text("coop.lanPort", CoopLoc.LanPortLabel, SafeInt(() => net.LanJoinPort).ToString(),
                v => { try { if (int.TryParse(v, out int p) && p > 0 && p < 65536) net.LanJoinPort = p; } catch { } });
            page.Button(CoopLoc.Paste, T("从剪贴板", "from clipboard"), () =>
            {
                try
                {
                    var clip = (CoopUIManager.ReadClipboard() ?? "").Trim();
                    if (clip.Length == 0) return;
                    int ci = clip.IndexOf(':');
                    if (ci > 0 && int.TryParse(clip.Substring(ci + 1), out int p2) && p2 > 0 && p2 < 65536)
                    {
                        net.LanJoinHost = clip.Substring(0, ci);
                        net.LanJoinPort = p2;
                    }
                    else net.LanJoinHost = clip;
                }
                catch { }
                RequestRefresh();
            });
            page.Button(CoopLoc.Join, T("加入", "join"), () =>
            {
                try
                {
                    net.LanJoinHost = (net.LanJoinHost ?? "").Trim();
                    net.JoinLanRoom();
                }
                catch (Exception ex) { Log("join lan", ex); }
                RequestRefresh();
            });

            page.Button(CoopLoc.LanScan, T("扫描", "scan"), () =>
            {
                try { net.ScanLan(); } catch { }
                RequestRefresh();
            });
            if (SafeBool(() => net.LanScanning)) page.Label(Col(CSecond, CoopLoc.LanScanning));

            var rooms = SafeList(() => net.LanRooms);
            if (rooms.Count == 0)
            {
                page.Label(Col(CDim, CoopLoc.LanNoRooms));
            }
            else
            {
                for (int i = 0; i < rooms.Count; i++)
                {
                    var room = rooms[i];
                    if (room == null) continue;
                    string label = LobbyLabel(room);
                    var target = room;
                    page.Button(label, CoopLoc.Join, () =>
                    {
                        try
                        {
                            net.LanJoinHost = target.Host;
                            net.LanJoinPort = target.Port;
                            if (target.HasPassword && !net.VerifyLanPassword(target, net.PendingPassword))
                            {
                                net.LastError = CoopLoc.PasswordRequired + ": " + target.Name;
                                return;
                            }
                            net.JoinLanRoom();
                        }
                        catch (Exception ex) { Log("join lan room", ex); }
                        RequestRefresh();
                    });
                }
            }
            page.Label(Col(CDim, CoopLoc.LanHint));
        }

        // ---------------- 页签 3：设置（同步开关 + 身份/局域网 + 关于） ----------------

        private static void BuildSettingsTab(OpenNestUIKit.API.UiPageDef page, NetManager net)
        {
            page.Header(T("同步开关（本端行为；双端一致才有效）", "Sync switches (per-end; both ends must match)"));
            page.Toggle("CoopConfig.Sync.CatSync", T("猫同步", "Cat sync"), CoopConfig.CatSync, v => Write("Sync", "CatSync", v));
            page.Toggle("CoopConfig.Sync.RecordPlayerSync", T("唱片机同步", "Record player sync"), CoopConfig.RecordPlayerSync, v => Write("Sync", "RecordPlayerSync", v));
            page.Toggle("CoopConfig.Interactables.CalculateButtonSync", T("计算万能按钮同步", "Calculate button sync"), CoopConfig.CalculateButtonSync, v => Write("Interactables", "CalculateButtonSync", v));

            page.Header(T("身份与局域网", "Identity & LAN"));
            page.Label(T("本端名称", "Local name") + "：" + Col(CInfo, Show(CoopConfig.LocalName)));
            page.Label(CoopLoc.LanPortLabel + "：" + Col(COk, CoopConfig.LanPort.ToString()));
            page.Label(T("上次加入", "Last joined") + "：" + Col(CMute, Show(CoopConfig.LanLastHost)));
            page.Label(T("配置文件", "Config file") + "：" + Col(CDim, Show(CoopConfig.FilePath)));

            page.Header(T("关于", "About"));
            page.Label(Col(COk, $"OpenNestCoop  v{NetConfig.Version}") + " · " + Col(CMute, NetConfig.LoaderName));
            page.Label(Col(CDim, T("菜单由 OpenNestUIKit 渲染（一页 + 页签，对齐原面板布局）",
                          "Menu rendered by OpenNestUIKit (single page + tabs, mirroring the original panel)")));
        }

        // ---------------- 已联机页签 1：房间 / 成员（对应原面板联机后布局） ----------------

        private static void BuildRoomTab(OpenNestUIKit.API.UiPageDef page, NetManager net)
        {
            page.Header(CoopLoc.Room);
            page.Label(CoopLoc.Room + "：" + Col(COk, Safe(() => net.PendingLobbyName))
                       + "    " + CoopLoc.MaxPlayers + "：" + Col(CMute, SafeInt(() => net.Lobby.MaxPlayers).ToString()));
            bool hasPwd = SafeBool(() => net.IsHost)
                ? Safe(() => net.PendingPassword).Length > 0
                : Safe(() => net.Lobby.PasswordHash).Length > 0;
            page.Label(CoopLoc.RoomPwd + "：" + (hasPwd ? Col(COk, CoopLoc.PwdHas) : Col(CMute, CoopLoc.PwdNone)));
            page.Label(CoopLoc.Members + " (" + Col(COk, SafeInt(() => net.Roster.Count) + "/" + SafeInt(() => net.Lobby.MaxPlayers)) + ")"
                       + "    " + CoopLoc.MyRole + "：" + Col(RoleColor(SafeRole(() => net.Local)), RoleText(SafeRole(() => net.Local))));

            var roster = SafeList(() => net.Roster);
            for (int i = 0; i < roster.Count; i++)
            {
                var p = roster[i];
                if (p == null) continue;
                // 成员行：编号/标签/延迟/角色上色（原面板是白字单行，这里补色但保持同一行布局）
                string tag = Col(CDim, "#" + p.PlayerId) + "  " + p.Name
                             + (p.IsHost ? Col(CWarn, "  " + CoopLoc.HostTag) : "")
                             + (p.IsLocal ? Col(CInfo, "  " + CoopLoc.YouTag)
                                          : (p.PingMs > 0 ? Col(PingColor(p.PingMs), "  " + p.PingMs.ToString("0") + "ms") : ""))
                             + "    " + Col(RoleColor(p.Role), RoleName(p.Role));
                if (SafeBool(() => net.IsHost) && !p.IsLocal)
                {
                    var peer = p;
                    page.Button(tag, CoopLoc.Kick, () =>
                    {
                        try { net.KickPlayer(peer.SteamId, true); } catch (Exception ex) { Log("kick", ex); }
                        RequestRefresh();
                    });
                }
                else
                {
                    page.Label(tag);
                }
            }

            page.Header(T("操作", "Actions"));
            if (SafeBool(() => net.IsHost))
            {
                page.Button(CoopLoc.Invite, "Steam", () => { try { net.InviteFriends(); } catch { } });
            }
            page.Button(CoopLoc.Leave, T("离开", "leave"), () =>
            {
                try { net.LeaveSession(); } catch (Exception ex) { Log("leave", ex); }
                RequestRefresh();
            });
            page.Label(Col(CDim, CoopLoc.ChatHint));
        }

        // ---------------- 已联机页签 2：聊天（原面板是独立浮窗，这里放进页签） ----------------

        private static void BuildChatTab(OpenNestUIKit.API.UiPageDef page, NetManager net)
        {
            page.Header(CoopLoc.Chat);
            var log = SafeList(() => net.ChatLog);
            if (log.Count == 0)
            {
                page.Label(Col(CDim, CoopLoc.NoChat));
            }
            else
            {
                // ⚠ 2026-09-13 用户：“左侧Chat框不显示聊天信息记录” ——
                //   之前把整段记录拼成**一个** Label（带 `\n`）。宿主那行只给一行高，多行会被裁掉/看不到；
                //   现在改用**内嵌可滚列表**（每行一条），列表自己滚、多少条都看得到。
                int from = Math.Max(0, log.Count - 80);
                page.List("coop.chatlog", 220f, l =>
                {
                    for (int i = from; i < log.Count; i++) l.Label(ChatLine(log[i]));
                });
            }

            // ⚠ 2026-09-13 用户：“菜单侧的 Chat 框的 Send 按钮布局错误，占了一整行” ——
            //   改成**两栏**：左边输入框占剩余宽度、右边发送按钮（同一行）。
            page.Columns(520f - 96f, l => l.Text("coop.chat", CoopLoc.ChatPlaceholder, _chatDraft, v => { _chatDraft = v ?? ""; }),
                r => r.Button(CoopLoc.Send, T("发送", "send"), () =>
                {
                    try
                    {
                        string msg = (_chatDraft ?? "").Trim();
                        if (msg.Length > 0) net.SendChat(msg);
                        _chatDraft = "";
                    }
                    catch (Exception ex) { Log("send chat", ex); }
                    RequestRefresh();
                }), gap: 8f);
            page.Label(Col(CDim, CoopLoc.ChatEnterHint));
        }

        // ==================================================================================
        //  配色（**照搬原面板 `CoopUIManager` 的富文本色**；用户：“联机菜单的各种颜色也没了”）
        //
        //  原面板用的就是 TMP 富文本 3 位十六进制（`<color=#8f8>`，实测游戏字体支持）——
        //  这里**原样复用那几个色值**，保证“迁移到 UIKit 之后颜色和原来一致”，而不是另造一套。
        // ==================================================================================

        private const string COk = "#8f8";          // 绿：就绪 / 版本一致 / IP / 房间名 / 有密码
        private const string CSteamOk = "#7f7";     // 绿（Steam 就绪；原面板就用的这一档）
        private const string CWarn = "#fa0";        // 橙：初始化中 / 房主标 / 带锁
        private const string CErr = "#ff6b6b";      // 红：错误 / 版本不一致 / 满员
        private const string CInfo = "#8cf";        // 青：身份 tag / 自己标 / 聊天人名
        private const string CMute = "#aaa";        // 灰：加载器 / 房主名 / 玩家名
        private const string CDim = "#9aa3b3";      // 暗：提示行（原 0.6,0.65,0.7）
        private const string CSecond = "#c8d3e6";   // 次级（原 0.8,0.85,0.9）
        private const string CBody = "#d9e6ff";     // 正文（原 0.85,0.9,1）

        /// <summary>上色（空串不上色，免得出现空标签）。</summary>
        private static string Col(string hex, string s) => string.IsNullOrEmpty(s) ? "" : $"<color={hex}>{s}</color>";

        /// <summary>页签文案：选中的那个加 `&gt; ` 前缀 + 绿色（**照搬原面板 `BuildIdleTabs`**）。</summary>
        private static string TabLabel(int index, int selected, string text)
            => index == selected ? $"<color={COk}>" + "> " + text + "</color>" : text;

        /// <summary>会话状态色（原面板未上色；这里按语义分级，仍是同一行文案）。</summary>
        private static string StateColor(SessionState s) => s switch
        {
            SessionState.Hosting => COk,
            SessionState.Joined => CInfo,
            _ => CDim,
        };

        /// <summary>角色色（成员行 / 我的角色）。</summary>
        private static string RoleColor(PlayerSession p) => RoleColor(p != null ? p.Role : OpenNestCore.Avatar.CrewRole.None);

        private static string RoleColor(OpenNestCore.Avatar.CrewRole r) => r switch
        {
            OpenNestCore.Avatar.CrewRole.Commander => "#ffd166",     // 金色：指挥
            OpenNestCore.Avatar.CrewRole.Gunner => "#ff9a76",        // 橙红：炮手
            OpenNestCore.Avatar.CrewRole.Loader => COk,              // 绿：装填
            OpenNestCore.Avatar.CrewRole.FireControl => CInfo,       // 青：火控
            _ => CMute,
        };

        /// <summary>延迟色：绿 ≤80ms / 橙 ≤160ms / 红 更高。</summary>
        private static string PingColor(float ms) => ms <= 80f ? COk : (ms <= 160f ? CWarn : CErr);

        /// <summary>`[名字] 文本` → 人名上色（正文统一体色；原面板聊天正文就是 0.85,0.9,1）。</summary>
        private static string ChatLine(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (raw[0] == '[')
            {
                int close = raw.IndexOf(']');
                if (close > 0)
                    return Col(CInfo, raw.Substring(0, close + 1)) + Col(CBody, raw.Substring(close + 1));
            }
            return Col(CBody, raw);
        }

        // ---------------- 文案与安全读取 ----------------

        private static string T(string zh, string en) => CoopLoc.T(zh, en);

        private static NetManager SafeNet()
        {
            try { return CoopRuntime.Net; } catch { return null; }
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? ""; } catch { return ""; }
        }

        private static int SafeInt(Func<int> f)
        {
            try { return f(); } catch { return 0; }
        }

        private static ulong SafeULong(Func<ulong> f)
        {
            try { return f(); } catch { return 0; }
        }

        private static bool SafeBool(Func<bool> f)
        {
            try { return f(); } catch { return false; }
        }

        private static List<T> SafeList<T>(Func<List<T>> f)
        {
            try { return f() ?? new List<T>(); } catch { return new List<T>(); }
        }

        private static PlayerSession SafeRole(Func<PlayerSession> f)
        {
            try { return f(); } catch { return null; }
        }

        private static string RoleText(PlayerSession p) => RoleName(p != null ? p.Role : OpenNestCore.Avatar.CrewRole.None);

        private static string RoleName(OpenNestCore.Avatar.CrewRole r) => r switch
        {
            OpenNestCore.Avatar.CrewRole.Commander => CoopLoc.RoleCommander,
            OpenNestCore.Avatar.CrewRole.Gunner => CoopLoc.RoleGunner,
            OpenNestCore.Avatar.CrewRole.Loader => CoopLoc.RoleLoader,
            OpenNestCore.Avatar.CrewRole.FireControl => CoopLoc.RoleFireControl,
            _ => CoopLoc.RoleNone,
        };

        private static string StateText(SessionState s) => s switch
        {
            SessionState.Hosting => CoopLoc.StatusHosting,
            SessionState.Joined => CoopLoc.StatusJoined,
            _ => CoopLoc.StatusIdle,
        };

        private static string Show(string s) => string.IsNullOrEmpty(s) ? T("（未设置）", "(not set)") : s;

        /// <summary>Steam 大厅条目一行文案（房名 / 锁 / 加载器 / 人数 / 版本 / 房主）—— 配色同原面板。</summary>
        private static string LobbyLabel(LobbyInfo info)
        {
            try
            {
                string lockTag = info.HasPassword ? Col(CWarn, CoopLoc.Locked) + " " : "";
                string loaderTag = info.Loader == "MelonLoader" ? "[ML] " : info.Loader == "BepInEx" ? "[BE] " : "";
                loaderTag = loaderTag.Length > 0 ? Col(CMute, loaderTag) : "";
                string ver = string.IsNullOrEmpty(info.Version)
                    ? Col(CErr, CoopLoc.OldVersion)
                    : (info.Version == NetConfig.Version
                        ? Col(COk, "v" + info.Version)
                        : Col(CErr, "v" + info.Version + " !"));      // 版本不一致 → 红
                string owner = string.IsNullOrEmpty(info.OwnerName) ? "" : "  " + Col(CMute, "by " + info.OwnerName);
                string full = info.IsFull ? "  " + Col(CErr, CoopLoc.Full) : "";
                return $"{info.Name}  {lockTag}{loaderTag}({info.Players}/{info.MaxPlayers}){full}  {ver}{owner}";
            }
            catch { return info != null ? info.Name : "?"; }
        }

        /// <summary>局域网房间条目一行文案—— 配色同原面板。</summary>
        private static string LobbyLabel(LanDiscovery.LanRoom room)
        {
            try
            {
                string lockTag = room.HasPassword ? Col(CWarn, CoopLoc.Locked) + " " : "";
                string ver = string.IsNullOrEmpty(room.Version)
                    ? Col(CErr, "?")
                    : (room.Version == NetConfig.Version
                        ? Col(COk, "v" + room.Version)
                        : Col(CErr, "v" + room.Version + " " + CoopLoc.LanMismatch));
                return $"{lockTag}{room.Name} ({room.Players}/{room.MaxPlayers})  {ver}  " + Col(CMute, room.Host + ":" + room.Port);
            }
            catch { return room != null ? room.Name : "?"; }
        }

        private static bool Write(string section, string key, bool value)
        {
            try
            {
                CoopConfig.Set(section, key, value ? "true" : "false");
                CoopLog.Info("coop.uikit", () => $"设置 [{section}] {key} = {value}");
                return true;
            }
            catch (Exception ex)
            {
                CoopLog.Warn("coop.uikit", () => $"写设置失败 [{section}] {key}: {ex.Message}");
                return false;
            }
        }

        private static void Log(string what, Exception ex)
            => CoopLog.Warn("coop.uikit", () => $"{what} 失败: {ex.Message}");
    }
}
