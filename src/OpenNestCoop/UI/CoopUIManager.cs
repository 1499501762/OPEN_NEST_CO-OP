using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
#if !MELONLOADER
using TMPro;
#else
using TMPro = Il2CppTMPro;
#endif
using UnityEngine;
using UnityEngine.UI;

using OpenNestCoop.Core;
namespace OpenNestCoop.UI;

/// <summary>
/// 联机菜单（UGUI 实现）。
///
/// 该游戏是 IL2CPP 裁剪构建，且 IMGUI(OnGUI) 会渲染在游戏 UGUI 主菜单**之下**
/// （鼠标箭头都被盖住、点击被 UGUI 拦截），因此菜单必须用 UGUI Canvas 实现：
/// ScreenSpaceOverlay + 高 sortingOrder，点击走游戏自己的 EventSystem，天然可用。
/// 文本输入不用 GUI.TextField（被裁剪），改用 InputSystem 键盘捕获 + TMP 显示。
/// </summary>
public class CoopUIManager : MonoBehaviour
{
    public static CoopUIManager Instance;

    private GameObject _root;
    private Canvas _canvas;
    private GameObject _panel;
    private RectTransform _panelRt;
    private RectTransform _content;   // 动态内容容器（每次重建整体销毁重建）
    private TMP_FontAsset _font;
    private GameObject _blocker;      // 全屏射线拦截层（屏蔽下方 UI / 物品点击穿透）

    // 独立聊天浮动面板（屏幕左中，主菜单之外常驻；联机会话时显示）
    private GameObject _chatRoot;
    private RectTransform _chatContent;
    private Image _chatBg;              // 面板背景（未聚焦时隐藏，只留提示条）
    private GameObject _chatHintBar;    // 未聚焦时的小提示条（"回车聊天"）
    private TextMeshProUGUI _chatTitle;
    private TextMeshProUGUI _chatBody;
    private Button _chatInputBtn;
    private const float ChatW = 380f;
    private const float ChatH = 340f;

    // 菜单打开时的交互锁
    private static FirstPersonController _fpc;
    private static List<Interactable> _disabledInteractables;

    // 文本输入状态
    private string _roomName = "";
    private string _chat = "";
    private bool _typing;

    private bool _menuOpen = true;
    private string _lastUiKey = "";

    // 本地模式大号 Client/Host 标识
    private TextMeshProUGUI _roleBadge;
    private string _lastRoleBadge = "";

    private const float PW = 500f;
    private const float PH = 700f;

    public CoopUIManager(System.IntPtr ptr) : base(ptr) { }

    public void Start()
    {
        Instance = this;
        try
        {
            _roomName = CoopRuntime.Net?.PendingLobbyName ?? CoopLoc.DefaultRoomName;
        }
        catch { }
        BuildCanvas();
        ApplyMenuState();
    }

    // ---------------- IME / 文本输入（PollInput 物理键 + IME composition patch） ----------------

    /// <summary>
    /// 文本输入支持：
    /// - 英文/数字/符号：PollInput 物理按键（可靠，无 native 调用风险）
    /// - 中文：Harmony patch Keyboard.OnIMECompositionChanged（见 HarmonyPatches.PostImeComposition）
    ///   ——IME 组合提交时含 CJK 字符，转发到 OnImeText。
    /// ⚠️ 不用 onTextInput 监听器（add_onTextInput + Il2Cpp 委托）：native 调用会乱码/崩溃
    /// （字母前方框、闪退），且与 PollInput 双通道重复（跳字）。
    /// </summary>
    private UnityEngine.InputSystem.Keyboard _imeKeyboard;

    public void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void Update()
    {
        try { PollInput(); }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] PollInput: {ex.Message}"); }

        // 本地模式大号角色标识：实时刷新（HOST/CLIENT + 状态，本地联机测试一眼看清）
        try
        {
            var netBadge = CoopRuntime.Net;
            if (_roleBadge != null)
            {
                bool local = netBadge != null && netBadge.LocalMode;
                if (local && netBadge != null)
                {
                    // LocalMode 可能在 Start 之后才由 AutoJoin 置位（当时被隐藏），这里重新显示
                    if (!_roleBadge.gameObject.activeSelf) _roleBadge.gameObject.SetActive(true);
                    string txt;
                    bool isHost = netBadge.IsHost;
                    bool inSession = netBadge.State == SessionState.Hosting || netBadge.State == SessionState.Joined;
                    var hostBadge = CoopLoc.HostBadge;
                    var clientBadge = CoopLoc.ClientBadge;
                    var standby = CoopLoc.Standby;
                    var online = CoopLoc.Online;
                    if (!inSession)
                        txt = isHost ? $"<color=#ffaa55>{hostBadge}</color>\u00A0\u00A0{standby}" : $"<color=#55ff55>{clientBadge}</color>\u00A0\u00A0{standby}";
                    else
                        txt = isHost ? $"<color=#ff5555>{hostBadge}</color>\u00A0\u00A0{online}" : $"<color=#55ff55>{clientBadge}</color>\u00A0\u00A0{online}";
                    if (txt != _lastRoleBadge)
                    {
                        _lastRoleBadge = txt;
                        _roleBadge.text = txt;
                    }
                }
                else if (_roleBadge.gameObject.activeSelf)
                {
                    _roleBadge.gameObject.SetActive(false);
                }
            }
        }
        catch { }

        // 只在内容变化时才整体重建（省 GC、避免重建在按下→抬起之间销毁按钮导致点击丢失）；
        // 静止时完全不重建。
        var net = CoopRuntime.Net;
        if (net == null) return;
        var key = UiKey(net);
        if (key != _lastUiKey)
        {
            _lastUiKey = key;
            try { Rebuild(); }
            catch (System.Exception ex) { CoopRuntime.LogSource?.LogError($"[UI] Rebuild 异常: {ex}"); }
        }

        // 聊天面板独立驱动（不依赖主菜单 Rebuild 门控）：联机会话 + 聚焦态/聊天数变化时重建
        string chatKey = ChatKey();
        if (chatKey != _lastChatUiKey)
        {
            _lastChatUiKey = chatKey;
            try { RebuildChat(); }
            catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] RebuildChat: {ex.Message}"); }
        }
    }

    /// <summary>聊天面板内容指纹：会话状态 + 聚焦态 + 聊天条数（变化即重建，独立于主菜单）。</summary>
    private string ChatKey()
    {
        try
        {
            var net = CoopRuntime.Net;
            if (net == null) return "?";
            var sb = new System.Text.StringBuilder(32);
            sb.Append((int)net.State).Append('|');
            sb.Append(_typing ? 1 : 0).Append('|');
            sb.Append(net.ChatLog.Count).Append('|');
            sb.Append(_chat).Append('|');
            return sb.ToString();
        }
        catch { return "?"; }
    }
    private string _lastChatUiKey = "";

    /// <summary>UI 内容指纹：任意字段变化即重建（状态/名单/聊天/大厅列表/输入/错误等）。</summary>
    private string UiKey(NetManager net)
    {
        try
        {
            var sb = new System.Text.StringBuilder(96);
            sb.Append((int)net.State).Append('|');
            sb.Append(net.Roster.Count).Append('|');
            sb.Append(net.ChatLog.Count).Append('|');
            sb.Append(net.Browser.Count).Append('|');
            if (net.Browser.Count > 0) sb.Append(net.Browser[0].Id);
            sb.Append('|');
            sb.Append(net.LastError).Append('|');
            sb.Append(_typing ? 1 : 0).Append('|');
            sb.Append(_menuOpen ? 1 : 0).Append('|');
            sb.Append(net.PendingLobbyName).Append('|');
            sb.Append(net.PendingMaxPlayers).Append('|'); // 大厅人数增减后立即刷新界面
            sb.Append(_roomName).Append('|');
            sb.Append(_chat).Append('|');
            sb.Append(net.Local?.PlayerId).Append('|');
            sb.Append(net.Local?.Role);
            // ping 实时显示：成员 PingMs 变化即重建（否则一直显示初始 0ms）
            if (net.Roster != null)
                foreach (var p in net.Roster)
                    sb.Append('|').Append((int)p.PingMs);
            return sb.ToString();
        }
        catch { return "?"; }
    }

    // ---------------- 交互锁（屏蔽点击穿透） ----------------

    private void ApplyMenuState()
    {
        try
        {
            if (_blocker != null) _blocker.SetActive(_menuOpen);
            if (_menuOpen) LockPlayer(true); else LockPlayer(false);
            if (_menuOpen) DisableInteractables(true); else DisableInteractables(false);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] ApplyMenuState: {ex.Message}"); }
    }

    private static void LockPlayer(bool locked)
    {
        try
        {
            if (_fpc == null) _fpc = UnityEngine.Object.FindFirstObjectByType<FirstPersonController>();
            if (_fpc != null) _fpc.SetFrozen(locked);
        }
        catch { }
    }

    /// <summary>菜单打开时禁用场景 3D 交互组件（物品点击），关闭时恢复。</summary>
    private static void DisableInteractables(bool disable)
    {
        try
        {
            if (disable)
            {
                if (_disabledInteractables != null) return; // 已在禁用态
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<Interactable>();
                _disabledInteractables = new List<Interactable>();
                if (all != null)
                    foreach (var i in all)
                        if (i != null && i.enabled)
                        {
                            _disabledInteractables.Add(i);
                            i.enabled = false;
                        }
            }
            else if (_disabledInteractables != null)
            {
                foreach (var i in _disabledInteractables)
                    if (i != null) i.enabled = true;
                _disabledInteractables = null;
            }
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] DisableInteractables: {ex.Message}"); }
    }

    // ---------------- 构建 ----------------

    private void BuildCanvas()
    {
        _root = new GameObject("OpenNestCoop_UI");
        DontDestroyOnLoad(_root);

        _canvas = _root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 32766;

        var scaler = _root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        _root.AddComponent<GraphicRaycaster>();

        // 全屏射线拦截层（菜单打开时屏蔽下方游戏 UI / 3D 物品的点击穿透）
        _blocker = new GameObject("Blocker");
        _blocker.transform.SetParent(_root.transform, false);
        var blkRt = _blocker.AddComponent<RectTransform>();
        blkRt.anchorMin = Vector2.zero;
        blkRt.anchorMax = Vector2.one;
        blkRt.offsetMin = Vector2.zero;
        blkRt.offsetMax = Vector2.zero;
        var blkImg = _blocker.AddComponent<Image>();
        blkImg.color = new Color(0f, 0f, 0f, 0f); // 全透明，但 raycastTarget=true 拦截射线
        _blocker.transform.SetAsFirstSibling();  // 置于最底层，按钮/面板在上层仍可点击
        _blocker.SetActive(false);

        // 左上角开关（常驻，可重开菜单）
        MakeButton(_root.transform, CoopLoc.MenuToggle, 8, 8, 130, 30, () =>
        {
            _menuOpen = !_menuOpen;
            ApplyMenuState();
        });

        // 本地模式大号角色标识（左下角，常驻）：只在 LocalMode 下显示，HOST 红 / CLIENT 绿
        try
        {
            var badgeGo = new GameObject("RoleBadge");
            badgeGo.transform.SetParent(_root.transform, false);
            var badgeRt = badgeGo.AddComponent<RectTransform>();
            badgeRt.anchorMin = new Vector2(0f, 0f);
            badgeRt.anchorMax = new Vector2(0f, 0f);
            badgeRt.pivot = new Vector2(0f, 0f);
            badgeRt.anchoredPosition = new Vector2(16f, 16f);
            badgeRt.sizeDelta = new Vector2(560f, 80f);
            _roleBadge = badgeGo.AddComponent<TextMeshProUGUI>();
            _roleBadge.fontSize = 48;
            _roleBadge.fontStyle = TMPro.FontStyles.Bold;
            _roleBadge.alignment = TextAlignmentOptions.Left;
            _roleBadge.raycastTarget = false;
            _roleBadge.enableWordWrapping = false;
            _roleBadge.overflowMode = TMPro.TextOverflowModes.Overflow;
            EnsureFont(_roleBadge);
            var net0 = CoopRuntime.Net;
            if (net0 == null || !net0.LocalMode) _roleBadge.gameObject.SetActive(false);
            else _roleBadge.text = net0.IsHost ? "<color=#ff5555>HOST</color>" : "<color=#55ff55>CLIENT</color>";
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] RoleBadge 创建: {ex.Message}"); }

        // 主面板
        _panel = new GameObject("Panel");
        _panel.transform.SetParent(_root.transform, false);
        _panelRt = _panel.AddComponent<RectTransform>();
        _panelRt.anchorMin = _panelRt.anchorMax = new Vector2(0.5f, 0.5f);
        _panelRt.sizeDelta = new Vector2(PW, PH);
        var img = _panel.AddComponent<Image>();
        img.color = new Color(0.05f, 0.06f, 0.09f, 0.94f);

        BuildChatPanel();
    }

    // ---------------- 独立聊天浮动面板（屏幕左中） ----------------

    /// <summary>
    /// 聊天面板：屏幕左侧中间，主菜单之外常驻。联机会话（Hosting/Joined）时显示。
    /// 内容（标题/记录/输入框）由 RebuildChat 每次重建刷新；面板本身不销毁。
    /// </summary>
    private void BuildChatPanel()
    {
        try
        {
            _chatRoot = new GameObject("ChatPanel");
            _chatRoot.transform.SetParent(_root.transform, false);
            var rt = _chatRoot.AddComponent<RectTransform>();
            // 锚点：屏幕左中（anchorMin=anchorMax=(0,0.5)），偏移到左侧
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(16f, 0f);
            rt.sizeDelta = new Vector2(ChatW, ChatH);
            _chatBg = _chatRoot.AddComponent<Image>();
            _chatBg.color = new Color(0.05f, 0.06f, 0.09f, 0.88f);

            // 未聚焦小提示条（独立于 _chatContent，聚焦/非聚焦都常驻）：点击也可聚焦
            _chatHintBar = new GameObject("ChatHint");
            _chatHintBar.transform.SetParent(_chatRoot.transform, false);
            var hintRt = _chatHintBar.AddComponent<RectTransform>();
            hintRt.anchorMin = hintRt.anchorMax = new Vector2(0f, 0f);
            hintRt.pivot = new Vector2(0f, 0.5f);
            hintRt.anchoredPosition = new Vector2(0f, 0f);
            hintRt.sizeDelta = new Vector2(210f, 30f);
            var hintImg = _chatHintBar.AddComponent<Image>();
            hintImg.color = new Color(0.05f, 0.06f, 0.09f, 0.7f);
            var hintBtn = _chatHintBar.AddComponent<Button>();
            hintBtn.onClick.AddListener(new Action(() => FocusChat()));
            var hintTxt = MakeTextFill(_chatHintBar.transform, CoopLoc.ChatEnterHint, 14, TextAlignmentOptions.Left);
            hintTxt.color = new Color(0.7f, 0.75f, 0.85f);
            hintTxt.rectTransform.offsetMin = new Vector2(10f, 0f);
            hintTxt.rectTransform.offsetMax = Vector2.zero;

            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(_chatRoot.transform, false);
            _chatContent = contentGo.AddComponent<RectTransform>();
            _chatContent.anchorMin = _chatContent.anchorMax = new Vector2(0f, 1f);
            _chatContent.pivot = new Vector2(0f, 1f);
            _chatContent.anchoredPosition = Vector2.zero;
            _chatContent.sizeDelta = new Vector2(ChatW, ChatH);

            _chatRoot.SetActive(false); // 默认隐藏，联机时 RebuildChat 显示
            CoopRuntime.LogSource?.LogInfo("[UI] BuildChatPanel 完成");
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] BuildChatPanel: {ex.Message}"); }
    }

    /// <summary>重建聊天面板内容，按聚焦态切换：
    /// 未聚焦 → 隐藏背景/输入框，只留小提示条；聚焦 → 显示完整面板（记录+输入框+发送）。
    /// 无参（从 CoopRuntime.Net 取）——避免 IL2CPP 自定义类型参数问题。</summary>
    private void RebuildChat()
    {
        if (_chatRoot == null || _chatContent == null) return;
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            bool inSession = net.State == SessionState.Hosting || net.State == SessionState.Joined;
            bool show = inSession; // 聊天面板独立于主菜单：联机会话时始终显示（聚焦态切换背景）
            _chatRoot.SetActive(show);
            CoopRuntime.LogSource?.LogInfo($"[UI] RebuildChat show={show} typing={_typing}");
            if (!show) return;

            // 聚焦态决定背景/输入框显隐
            bool focused = _typing;
            if (_chatBg != null) _chatBg.enabled = focused;   // 未聚焦隐藏背景
            if (_chatHintBar != null) _chatHintBar.SetActive(!focused); // 未聚焦显示提示条
            if (_chatContent != null) _chatContent.gameObject.SetActive(focused); // 未聚焦隐藏内容(含输入框)

            if (!focused) return; // 未聚焦：只留提示条，不重建内容

            // 清空旧内容
            for (int i = _chatContent.childCount - 1; i >= 0; i--)
            {
                var c = _chatContent.GetChild(i);
                if (c != null) UnityEngine.Object.Destroy(c.gameObject);
            }

            float y = 4f;
            _chatTitle = MakeText(_chatContent, CoopLoc.Chat, 10, y, ChatW - 20, 20, 15, Color.white, TextAlignmentOptions.Left); y += 24;

            // 输入框固定贴背景底部（ChatH - 输入框高 - 边距）
            const float inputH = 28f;
            float inputY = ChatH - inputH - 8f;

            // 聊天记录区：标题下方 → 输入框上方，自适应高度
            float bodyTop = y;                       // 记录区顶部（标题下方）
            float bodyBottom = inputY - 6f;          // 记录区底部（输入框上方）
            float bodyH = Mathf.Max(40f, bodyBottom - bodyTop);
            int maxRows = (int)(bodyH / 18f);
            int start = Mathf.Max(0, net.ChatLog.Count - maxRows);
            string body = "";
            for (int i = start; i < net.ChatLog.Count; i++)
            {
                string line = net.ChatLog[i];
                if (line.Length > 52) line = line.Substring(0, 52) + "…";
                body += line + "\n";
            }
            if (body.Length == 0) body = CoopLoc.NoChat;
            _chatBody = MakeText(_chatContent, body, 10, bodyTop, ChatW - 20, bodyH, 13, new Color(0.85f, 0.9f, 1f), TextAlignmentOptions.TopLeft);
            _chatBody.richText = true;
            _chatBody.enableWordWrapping = true;
            _chatBody.alignment = TextAlignmentOptions.TopLeft;

            // 输入框（回车发送，无需发送按钮——按 Enter 提交并取消聚焦）
            _chatInputBtn = MakeInputBox(_chatContent, _chat, CoopLoc.ChatPlaceholder, 10, inputY, ChatW - 20, inputH, () => ToggleTyping());
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] RebuildChat: {ex.Message}"); }
    }

    /// <summary>聚焦聊天（未聚焦状态点提示条 / 回车呼出）。</summary>
    private void FocusChat()
    {
        _typing = true;
        SetImeActive(true);
        CoopRuntime.LogSource?.LogInfo($"[UI] FocusChat typing=true menuOpen={_menuOpen}");
    }

    // ---------------- 动态重建（状态变化后刷新） ----------------

    private void Rebuild()
    {
        CoopLoc.Refresh(); // 跟随游戏语言
        if (_panel != null) _panel.SetActive(_menuOpen);

        // 整体销毁旧内容（先隐藏再销毁，避免闪一帧旧内容）
        if (_content != null)
        {
            _content.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_content.gameObject);
            _content = null;
        }
        if (!_menuOpen) return;
        var net = CoopRuntime.Net;
        if (net == null || _panelRt == null) return;

        var contentGo = new GameObject("Content");
        contentGo.transform.SetParent(_panelRt, false);
        _content = contentGo.AddComponent<RectTransform>();
        _content.anchorMin = _content.anchorMax = new Vector2(0f, 1f);
        _content.pivot = new Vector2(0f, 1f);
        _content.anchoredPosition = Vector2.zero;
        _content.sizeDelta = new Vector2(PW, PH);

        var title = MakeText(_content, $"<b>{CoopLoc.Title}</b>", 12, 6, PW - 24, 30, 18, Color.white, TextAlignmentOptions.Left);
        title.richText = true;

        float y = 42f;
        StatusLine(ref y, $"Steam: {net.Local?.Name}");
        StatusLine(ref y, $"{CoopLoc.State}: {StateText(net.State)}    Steam: {(net.SteamReady ? $"<color=#7f7>{CoopLoc.SteamReady}</color>" : $"<color=#fa0>{CoopLoc.SteamInit}</color>")}");
        string errText = net.LastError;
        if (net.WasKicked) errText = CoopLoc.KickedHint;
        if (errText.Length > 0)
        {
            MakeText(_content, errText, 12, y, PW - 24, 20, 14, new Color(1f, 0.4f, 0.4f), TextAlignmentOptions.Left);
            y += 24;
        }
        y += 6;

        if (net.State == SessionState.Idle)
            BuildIdle(net, ref y);
        else
            BuildLobby(net, ref y);
        // 聊天面板由 Update 独立驱动（ChatKey 变化时重建），不在此处重建
    }

    private void StatusLine(ref float y, string text)
    {
        var t = MakeText(_content, text, 12, y, PW - 24, 20, 14, new Color(0.85f, 0.9f, 1f), TextAlignmentOptions.Left);
        t.richText = true;
        y += 22;
    }

    private void BuildIdle(NetManager net, ref float y)
    {
        MakeText(_content, CoopLoc.RoomNameLabel, 12, y, 120, 20, 14, Color.white, TextAlignmentOptions.Left); y += 24;

        // 点击输入框即进入输入态
        MakeInputBox(_roomName, CoopLoc.RoomNamePlaceholder, 12, y, PW - 24, 26, () => ToggleTyping()); y += 32;

        MakeButton(_content, "-", 12, y, 26, 24, () => net.PendingMaxPlayers = Mathf.Max(2, net.PendingMaxPlayers - 1));
        MakeText(_content, $"{CoopLoc.MaxPlayers}: {net.PendingMaxPlayers}", 46, y, 260, 24, 14, Color.white, TextAlignmentOptions.Left);
        MakeButton(_content, "+", PW - 40, y, 26, 24, () => net.PendingMaxPlayers = Mathf.Min(8, net.PendingMaxPlayers + 1));
        y += 30;

        MakeButton(_content, CoopLoc.CreateLobby, 12, y, PW - 24, 34, () =>
        {
            net.PendingLobbyName = string.IsNullOrWhiteSpace(_roomName) ? CoopLoc.DefaultRoomName : _roomName;
            net.CreateLobby();
        });
        y += 42;

        MakeButton(_content, CoopLoc.RefreshLobbies, 12, y, 170, 26, () => net.RefreshBrowser());
        y += 34;

        if (net.Browser.Count == 0)
        {
            MakeText(_content, CoopLoc.NoLobbies, 12, y, PW - 24, 20, 13, new Color(0.6f, 0.65f, 0.7f), TextAlignmentOptions.Left); y += 26;
        }
        else
        {
            y += 2;
            foreach (var info in net.Browser)
            {
                if (y > PH - 24) break;
                var title = info.IsFull
                    ? $"{info.Name}  ({info.Players}/{info.MaxPlayers} {CoopLoc.Full})"
                    : $"{info.Name}  ({info.Players}/{info.MaxPlayers})";
                MakeText(_content, title, 12, y, PW - 100, 22, 14, Color.white, TextAlignmentOptions.Left);
                if (!info.IsFull)
                {
                    MakeButton(_content, CoopLoc.Join, PW - 88, y, 72, 22, () => net.JoinLobby(info));
                }
                y += 26;
            }
        }

        MakeText(_content, CoopLoc.InviteHint, 12, y + 4, PW - 24, 18, 12, new Color(0.6f, 0.65f, 0.7f), TextAlignmentOptions.Left);
    }

    private void BuildLobby(NetManager net, ref float y)
    {
        MakeText(_content, $"{CoopLoc.Room}: {net.PendingLobbyName}", 12, y, PW - 90, 22, 15, Color.white, TextAlignmentOptions.Left);
        MakeButton(_content, CoopLoc.Leave, PW - 90, y, 76, 22, () => net.LeaveSession());
        if (net.IsHost)
        {
            // 主机：邀请好友（Steam overlay 对话框）
            MakeButton(_content, CoopLoc.Invite, PW - 170, y, 74, 22, () => net.InviteFriends());
        }
        y += 28;

        MakeText(_content, $"{CoopLoc.Members} ({net.Roster.Count}/{net.Lobby.MaxPlayers})   {CoopLoc.MyRole}: {RoleText(net.Local?.Role)}", 12, y, PW - 24, 20, 14, Color.white, TextAlignmentOptions.Left); y += 24;

        foreach (var p in net.Roster)
        {
            var me = p.IsLocal ? "  " + CoopLoc.YouTag : "";
            var host = p.IsHost ? "  " + CoopLoc.HostTag : "";
            var ping = p.IsLocal ? "" : $"  {p.PingMs:0}ms";
            MakeText(_content, $"#{p.PlayerId}  {p.Name}{host}{me}{ping}", 12, y, PW - 120, 20, 14, Color.white, TextAlignmentOptions.Left);
            float bx = PW - 212; // 按钮区起始（角色 + 踢出两列）
            if (net.IsHost && !p.IsLocal)
            {
                // 主机：角色循环按钮
                var role = p.Role;
                MakeButton(_content, RoleText(role), bx, y, 104, 20, () =>
                    net.SetRole(p.SteamId, NextRole(role)));
                // 主机：踢出（本次会话封禁）
                MakeButton(_content, CoopLoc.Kick, bx + 108, y, 44, 20, () =>
                {
                    net.KickPlayer(p.SteamId, true);
                    _chat = "";
                });
            }
            y += 22;
        }

        y += 6;
        // 聊天已拆到独立左中浮动面板（RebuildChat）——这里只留一行提示
        MakeText(_content, CoopLoc.ChatHint, 12, y, PW - 24, 18, 12, new Color(0.6f, 0.65f, 0.7f), TextAlignmentOptions.Left);
        y += 22;
    }

    // ---------------- 输入（InputSystem 键盘捕获，替代被裁剪的 GUI.TextField） ----------------

    /// <summary>输入态切换时同步启用/禁用 IME（中文输入法弹出/收起）。</summary>
    private void SetImeActive(bool active)
    {
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null) _imeKeyboard = kb;
            if (kb == null)
            {
                CoopRuntime.LogSource?.LogWarning($"[UI] SetImeActive({active}) Keyboard.current=null");
                return;
            }
            kb.SetIMEEnabled(active);
            CoopRuntime.LogSource?.LogInfo($"[UI] SetIMEEnabled({active}) ok");
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] SetImeActive: {ex.Message}"); }
    }

    /// <summary>
    /// 中文 IME 字符入口（Harmony patch Keyboard.OnIMECompositionChanged 转发 CJK）。
    /// 英文/数字/符号走 PollInput 物理按键；这里只收 IME 提交的中文字符，无重复通道。
    /// </summary>
    public static void OnImeText(char c)
    {
        try
        {
            var inst = Instance;
            if (inst == null || !inst._typing) return;
            if ((++_imeLog % 40) == 1)
                CoopRuntime.LogSource?.LogInfo($"[UI] IME '{c}' (0x{(int)c:x})");
            inst.Append(c);
        }
        catch { }
    }
    private static int _imeLog;

    /// <summary>切换输入态（点输入框）——同步启用/禁用 IME。</summary>
    private void ToggleTyping()
    {
        _typing = !_typing;
        SetImeActive(_typing);
    }

    /// <summary>退出输入态（点按钮/提交/菜单关）——同步禁用 IME。</summary>
    private void StopTyping()
    {
        if (_typing) SetImeActive(false);
        _typing = false;
    }

    private void PollInput()
    {
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;
            var net = CoopRuntime.Net;
            bool inSession = net != null && (net.State == SessionState.Hosting || net.State == SessionState.Joined);

            // 回车呼出聊天：未聚焦 + 联机会话时，按回车 → 聚焦聊天输入框（独立于主菜单开关）
            if (!_typing && inSession)
            {
                if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
                {
                    CoopRuntime.LogSource?.LogInfo($"[UI] Enter 呼出聊天 (state={net.State})");
                    FocusChat();
                    return; // 回车已消费为呼出，不再提交
                }
            }

            if (!_typing) return;
            // 物理字符：英文/数字/符号/空格（可靠，无 native 调用）——中文走 OnIMECompositionChanged。
            if (kb.backspaceKey.wasPressedThisFrame) { Backspace(); }
            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) { Submit(); return; }
            bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            AppendKey(kb.aKey, 'a', shift); AppendKey(kb.bKey, 'b', shift);
            AppendKey(kb.cKey, 'c', shift); AppendKey(kb.dKey, 'd', shift);
            AppendKey(kb.eKey, 'e', shift); AppendKey(kb.fKey, 'f', shift);
            AppendKey(kb.gKey, 'g', shift); AppendKey(kb.hKey, 'h', shift);
            AppendKey(kb.iKey, 'i', shift); AppendKey(kb.jKey, 'j', shift);
            AppendKey(kb.kKey, 'k', shift); AppendKey(kb.lKey, 'l', shift);
            AppendKey(kb.mKey, 'm', shift); AppendKey(kb.nKey, 'n', shift);
            AppendKey(kb.oKey, 'o', shift); AppendKey(kb.pKey, 'p', shift);
            AppendKey(kb.qKey, 'q', shift); AppendKey(kb.rKey, 'r', shift);
            AppendKey(kb.sKey, 's', shift); AppendKey(kb.tKey, 't', shift);
            AppendKey(kb.uKey, 'u', shift); AppendKey(kb.vKey, 'v', shift);
            AppendKey(kb.wKey, 'w', shift); AppendKey(kb.xKey, 'x', shift);
            AppendKey(kb.yKey, 'y', shift); AppendKey(kb.zKey, 'z', shift);
            AppendKey(kb.digit0Key, '0', false); AppendKey(kb.digit1Key, '1', false);
            AppendKey(kb.digit2Key, '2', false); AppendKey(kb.digit3Key, '3', false);
            AppendKey(kb.digit4Key, '4', false); AppendKey(kb.digit5Key, '5', false);
            AppendKey(kb.digit6Key, '6', false); AppendKey(kb.digit7Key, '7', false);
            AppendKey(kb.digit8Key, '8', false); AppendKey(kb.digit9Key, '9', false);
            if (kb.spaceKey.wasPressedThisFrame) Append(' ');
            if (kb.periodKey.wasPressedThisFrame) Append('.');
            if (kb.commaKey.wasPressedThisFrame) Append(',');
            if (kb.minusKey.wasPressedThisFrame) Append('-');
            if (kb.slashKey.wasPressedThisFrame) Append('/');
            if (kb.semicolonKey.wasPressedThisFrame) Append(';');
            if (kb.quoteKey.wasPressedThisFrame) Append('\'');
        }
        catch { }
    }

    private void AppendKey(UnityEngine.InputSystem.Controls.KeyControl k, char ch, bool shift)
    {
        if (k != null && k.wasPressedThisFrame)
            Append(shift ? char.ToUpper(ch) : ch);
    }

    private void Append(char c)
    {
        var net = CoopRuntime.Net;
        if (net != null && net.State == SessionState.Idle)
        {
            if (_roomName.Length < 40) _roomName += c;
        }
        else
        {
            if (_chat.Length < 120) _chat += c;
        }
    }

    private void Backspace()
    {
        var net = CoopRuntime.Net;
        if (net != null && net.State == SessionState.Idle)
        {
            if (_roomName.Length > 0) _roomName = _roomName.Substring(0, _roomName.Length - 1);
        }
        else
        {
            if (_chat.Length > 0) _chat = _chat.Substring(0, _chat.Length - 1);
        }
    }

    private void Submit()
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        if (net.State == SessionState.Idle)
        {
            net.PendingLobbyName = string.IsNullOrWhiteSpace(_roomName) ? CoopLoc.DefaultRoomName : _roomName;
            net.CreateLobby();
            StopTyping();
        }
        else
        {
            if (_chat.Trim().Length > 0) { net.SendChat(_chat); _chat = ""; }
            StopTyping(); // 发送后取消聚焦（回车发送 → 输入法收起）
        }
    }

    // ---------------- 控件工具 ----------------

    private RectTransform Place(float x, float y, float w, float h, Transform parent)
    {
        var go = new GameObject("ui");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    private TextMeshProUGUI MakeText(Transform parent, string text, float x, float y, float w, float h, int size, Color color, TextAlignmentOptions align)
    {
        var rt = Place(x, y, w, h, parent);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.raycastTarget = false;
        EnsureFont(t);
        return t;
    }

    private TextMeshProUGUI MakeTextFill(Transform parent, string text, int size, TextAlignmentOptions align)
    {
        var go = new GameObject("txt");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        var t = go.AddComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = size;
        t.color = Color.white;
        t.alignment = align;
        t.raycastTarget = false;
        EnsureFont(t);
        return t;
    }

    private RectTransform MakeBox(Transform parent, float x, float y, float w, float h, Color color)
    {
        var rt = Place(x, y, w, h, parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return rt;
    }

    private Button MakeButton(Transform parent, string text, float x, float y, float w, float h, Action onClick)
    {
        var go = new GameObject("btn");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x + w / 2f, -(y + h / 2f));
        rt.sizeDelta = new Vector2(w, h);

        var img = go.AddComponent<Image>();
        img.color = new Color(0.20f, 0.24f, 0.30f, 1f);

        var btn = go.AddComponent<Button>();
        var txt = MakeTextFill(go.transform, text, 14, TextAlignmentOptions.Center);
        txt.color = Color.white;

        var act = new Action(() => { StopTyping(); onClick(); });
        btn.onClick.AddListener(act);
        return btn;
    }

    /// <summary>可点击的输入框：点击切换输入态，显示当前文本 + 光标。</summary>
    private Button MakeInputBox(string text, string placeholder, float x, float y, float w, float h, Action onTap)
        => MakeInputBox(_content, text, placeholder, x, y, w, h, onTap);

    private Button MakeInputBox(Transform parent, string text, string placeholder, float x, float y, float w, float h, Action onTap)
    {
        var go = new GameObject("input");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x + w / 2f, -(y + h / 2f));
        rt.sizeDelta = new Vector2(w, h);

        var img = go.AddComponent<Image>();
        img.color = new Color(0.10f, 0.12f, 0.16f, 1f);
        var btn = go.AddComponent<Button>();

        var shown = text.Length > 0 ? text : placeholder;
        if (_typing) shown += "|";
        var txt = MakeTextFill(go.transform, shown, 15, TextAlignmentOptions.Left);
        txt.richText = true;
        txt.color = new Color(0.92f, 0.95f, 1f);
        txt.rectTransform.offsetMin = new Vector2(8f, 0f);
        txt.rectTransform.offsetMax = Vector2.zero;

        var act = new Action(onTap);
        btn.onClick.AddListener(act);
        return btn;
    }

    private void EnsureFont(TextMeshProUGUI t)
    {
        try
        {
            if (_font == null) _font = TMP_Settings.defaultFontAsset;
            if (_font != null) t.font = _font;
        }
        catch { }
    }

    private static string StateText(SessionState s) => s switch
    {
        SessionState.Idle => CoopLoc.StatusIdle,
        SessionState.Hosting => CoopLoc.StatusHosting,
        SessionState.Joined => CoopLoc.StatusJoined,
        _ => s.ToString()
    };

    private static string RoleText(CrewRole? r) => r switch
    {
        CrewRole.Commander => CoopLoc.RoleCommander,
        CrewRole.Gunner => CoopLoc.RoleGunner,
        CrewRole.Loader => CoopLoc.RoleLoader,
        CrewRole.FireControl => CoopLoc.RoleFireControl,
        _ => CoopLoc.RoleNone
    };

    /// <summary>主机点角色按钮时循环下一个角色。</summary>
    private static CrewRole NextRole(CrewRole r) => r switch
    {
        CrewRole.None => CrewRole.Gunner,
        CrewRole.Gunner => CrewRole.Loader,
        CrewRole.Loader => CrewRole.FireControl,
        _ => CrewRole.None
    };
}
