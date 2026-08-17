using System;
using System.Collections.Generic;
using OpenNestCoop.Net;
#if !MELONLOADER
using TMPro;
using Localisation;
#else
using TMPro = Il2CppTMPro;
using Localisation = Il2CppLocalisation;
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
    private Image _chatBg;              // 面板背景（未聚焦时隐藏）
    private GameObject _chatHintBar;    // 未聚焦时的小提示条（"回车聊天"）
    private TextMeshProUGUI _chatTitle;
    private TextMeshProUGUI _chatBody;  // 聊天记录（未聚焦也常驻显示）
    private TMP_InputField _chatInput;  // 真实 TMP 输入框（聚焦激活→唤起中文 IME）
    private TextMeshProUGUI _chatInputShow; // 输入框上方叠加显示层（TMP_InputField 文本渲染不可靠时兜底显示 _chat）
    private const float ChatW = 380f;
    private const float ChatH = 340f;

    // 菜单打开时的交互锁
    private static FirstPersonController _fpc;
    private static List<Interactable> _disabledInteractables;

    // 文本输入状态
    private string _roomName = "";
    private string _chat = "";
    private bool _typing;
    private float _focusAt = -1f; // 最近一次聚焦时间（回车提交防抖用）
    private static int _pollDiag; // TMP_InputField 轮询诊断日志节流
    private static int _keyDiag;  // 英文物理键诊断日志节流

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

        // 输入框文本轮询（中文 IME 方案核心）+ 诊断：
        // TMP_InputField 聚焦唤起系统 IME 后，候选字确认若写入 TMP_InputField.text 则增量同步 _chat。
        // ⚠️ 只同步 text **非空**且包含 _chat 前缀时（TMP_InputField 收不到字符时 text 为空，
        //    空串会覆盖 PollInput 输入的英文——"英文进不去"根因）。text 为空 = TMP_InputField 没收到，
        //    不覆盖 _chat。
        try
        {
            if (_typing && _chatInput != null && _chatInput.gameObject.activeSelf)
            {
                string t = _chatInput.text;
                if (t != null && t.Length > 0 && t != _chat)
                {
                    if ((++_pollDiag % 60) == 1)
                        CoopRuntime.LogSource?.LogInfo($"[UI] poll text='{t}' chat='{_chat}' (text 变化)");
                    // 增量同步：只处理 text 以 _chat 为前缀的追加部分（IME 提交的中文字符）
                    if (t.StartsWith(_chat))
                    {
                        string added = t.Substring(_chat.Length);
                        _chat += added;
                        if (_chat.Length > 120) _chat = _chat.Substring(0, 120);
                    }
                    // else：text 不以 _chat 开头（例如 TMP_InputField 收到过但被清）→ 不覆盖（保留 PollInput 输入）
                }
                else if (t != null && t.Length == 0 && _chat.Length > 0 && ((++_pollDiag % 120) == 1))
                {
                    CoopRuntime.LogSource?.LogInfo($"[UI] poll text 空 chat='{_chat}' (TMP_InputField 未收到字符，保留 PollInput 输入)");
                }
            }
        }
        catch { }
        // 输入框叠加显示层实时刷新（聚焦时显示用户输入的内容）
        try
        {
            if (_typing && _chatInputShow != null)
                UpdateChatInputShow(true);
        }
        catch { }

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
            catch (System.Exception ex) { CoopRuntime.LogSource?.LogError($"[UI] Rebuild exception: {ex}"); }
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
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] RoleBadge create: {ex.Message}"); }

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
    /// 结构（常驻，不销毁重建）：
    ///   _chatRoot   — 容器（背景 _chatBg + 提示条 _chatHintBar + 内容 _chatContent）
    ///   _chatTitle  — 标题（"聊天"，聚焦时显示）
    ///   _chatBody   — 聊天记录（**未聚焦也常驻显示**，历史消息始终可见）
    ///   _chatInput  — 真实 TMP_InputField（聚焦时激活 → 唤起中文 IME）
    /// 聚焦态切换只改显隐/激活，不销毁重建（避免输入框失焦）。
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

            // 标题（聚焦时显示）
            _chatTitle = MakeText(_chatContent, CoopLoc.Chat, 10, 4f, ChatW - 20, 20, 15, Color.white, TextAlignmentOptions.Left);

            // 聊天记录（常驻：未聚焦也显示历史消息）——先占位，RebuildChat 填文本
            _chatBody = MakeText(_chatContent, "", 10, 24f, ChatW - 20, ChatH - 64f, 13, new Color(0.85f, 0.9f, 1f), TextAlignmentOptions.TopLeft);
            _chatBody.richText = true;
            _chatBody.enableWordWrapping = true;
            _chatBody.alignment = TextAlignmentOptions.TopLeft;

            // 真实 TMP_InputField（聚焦激活 → 唤起系统中文 IME）。
            // ⚠️ 自制 Button+Text 输入框永远无法唤起输入法——Unity 只有在聚焦 TMP_InputField
            //    时才会弹出 IME（游戏自带 CatNameSetting/InputFieldHelper 等 TMP_InputField + InputSystemUIInputModule）。
            _chatInput = MakeChatInput(_chatContent);

            _chatRoot.SetActive(false); // 默认隐藏，联机时 RebuildChat 显示
            CoopRuntime.LogSource?.LogInfo("[UI] BuildChatPanel done (TMP_InputField IME 输入框)");
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] BuildChatPanel: {ex.Message}"); }
    }

    /// <summary>创建真实 TMP_InputField（聊天输入框）。聚焦 ActivateInputField 唤起中文 IME；
    /// 文本经 onValueChanged 同步到 _chat；回车提交由 PollInput 处理。</summary>
    private TMP_InputField MakeChatInput(Transform parent)
    {
        try
        {
            // ⚠️ 标准 TMP_InputField 结构：主对象=Image背景+TMP_InputField；**子对象**=TextMeshProUGUI
            //    （textComponent）。不能把 TextMeshProUGUI 放 Image 同一对象（此前 NRE）。
            //    子对象是干净 GameObject，AddComponent<TextMeshProUGUI> 自动补 CanvasRenderer（同 MakeText 成功路径）。
            var go = new GameObject("ChatInputField");
            go.transform.SetParent(parent, false);
            CoopRuntime.LogSource?.LogInfo("[UI] MakeChatInput s1: GameObject 创建");
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(0f, 0f);
            rt.sizeDelta = new Vector2(0f, 0f);
            CoopRuntime.LogSource?.LogInfo("[UI] MakeChatInput s2: RectTransform ok");
            var img = go.AddComponent<Image>();
            // ⚠️ 背景透明：TMP_InputField 仅用于唤起 IME（ActivateInputField），**不显示**。
            // 可见输入框由叠加层 _chatInputShow（可靠渲染）负责，避免"两个输入框"。
            img.color = new Color(0f, 0f, 0f, 0f);
            img.raycastTarget = false; // 不拦截（输入框自身不接收点击/键盘，IME 唤起靠 ActivateInputField）
            CoopRuntime.LogSource?.LogInfo("[UI] MakeChatInput s3: Image ok(透明)");

            // 子对象 Text（干净对象，AddComponent<TextMeshProUGUI> 自动补 CanvasRenderer，同 MakeText）
            var txtGo = new GameObject("Text");
            txtGo.transform.SetParent(go.transform, false);
            var txtRt = txtGo.AddComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = new Vector2(8f, 0f);
            txtRt.offsetMax = new Vector2(-8f, 0f);
            var txt = txtGo.AddComponent<TextMeshProUGUI>();
            if (txt == null) { CoopRuntime.LogSource?.LogWarning("[UI] MakeChatInput AddComponent<TextMeshProUGUI> 返回 null"); return null; }
            CoopRuntime.LogSource?.LogInfo("[UI] MakeChatInput s4a: AddComponent<TextMeshProUGUI> ok");
            try
            {
                EnsureFont(txt);   // 先设字体（fontSize setter 内部访问 font，顺序敏感）
                txt.fontSize = 15;
                txt.color = new Color(0f, 0f, 0f, 0f); // 透明（TMP_InputField 自身文本不显示，叠加层负责）
                txt.alignment = TextAlignmentOptions.Left;
                txt.raycastTarget = false; // 不拦截
                txt.text = "";     // 初始空文本（占位由叠加层显示）
                CoopRuntime.LogSource?.LogInfo("[UI] MakeChatInput s4b: 文本属性设置 ok");
            }
            catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] MakeChatInput txt 属性: {ex.Message}"); }

            // 子对象 Placeholder（空文本时的占位提示）——TMP_InputField 标准配置，否则空输入框不可见
            var phGo = new GameObject("Placeholder");
            phGo.transform.SetParent(go.transform, false);
            var phRt = phGo.AddComponent<RectTransform>();
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(8f, 0f);
            phRt.offsetMax = new Vector2(-8f, 0f);
            var ph = phGo.AddComponent<TextMeshProUGUI>();
            try
            {
                EnsureFont(ph);
                ph.fontSize = 15;
                ph.color = new Color(0f, 0f, 0f, 0f); // 透明（占位由叠加层显示）
                ph.alignment = TextAlignmentOptions.Left;
                ph.text = "";
                ph.raycastTarget = false;
            }
            catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] MakeChatInput placeholder: {ex.Message}"); }

            var field = go.AddComponent<TMP_InputField>();
            if (field == null) { CoopRuntime.LogSource?.LogWarning("[UI] MakeChatInput AddComponent<TMP_InputField> 返回 null"); return null; }
            CoopRuntime.LogSource?.LogInfo("[UI] MakeChatInput s5: AddComponent<TMP_InputField> ok");
            try { field.textComponent = txt; CoopRuntime.LogSource?.LogInfo("[UI] MakeChatInput s6: textComponent ok"); }
            catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] MakeChatInput textComponent: {ex.Message}"); }
            try { field.placeholder = ph; } catch { }
            try { field.textViewport = txtRt; } catch { }
            try { field.characterLimit = 120; } catch { }
            // lineType 用默认（SingleLine）
            CoopRuntime.LogSource?.LogInfo("[UI] MakeChatInput s7: 属性设置完成");
            // ⚠️ 不用 onValueChanged.AddListener（IL2CPP interop 的 UnityEvent<string> 降级为
            //    AddListener(IntPtr)，方法组桥接报 CS1503）。改由 Update 每帧轮询 _chatInput.text
            //    同步到 _chat（TMP_InputField 自己维护 text，英文/中文 IME 提交都进 text）。

            // 叠加显示层 = **可见输入框**（可靠渲染路径，同 MakeText）：
            // 主对象=Image 背景 + Button（点击聚焦）；**子对象**=TextMeshProUGUI（显示 _chat 内容 + 光标）。
            // ⚠️ TextMeshProUGUI 不能与 Image 同一 GameObject（IL2CPP interop 下 NRE，同 MakeChatInput
            //    教训）——文本必须放子对象（干净 GameObject，自动补 CanvasRenderer）。
            var showGo = new GameObject("InputShow");
            showGo.transform.SetParent(parent, false);
            var showRt = showGo.AddComponent<RectTransform>();
            showRt.anchorMin = showRt.anchorMax = new Vector2(0f, 1f);
            showRt.pivot = new Vector2(0f, 1f);
            // 与输入框同位置（输入框在 RebuildChat 每次定位；这里同步定位）
            showRt.anchoredPosition = new Vector2(10f, -(ChatH - 28f - 8f));
            showRt.sizeDelta = new Vector2(ChatW - 20f, 28f);
            var showImg = showGo.AddComponent<Image>();
            showImg.color = new Color(0.10f, 0.12f, 0.16f, 1f); // 输入框背景（可见）
            var showBtn = showGo.AddComponent<Button>();
            showBtn.onClick.AddListener(new Action(() => FocusChat())); // 点击聚焦
            // 子对象文本（干净对象，TextMeshProUGUI 自动补 CanvasRenderer，同 MakeText）
            var showTxtGo = new GameObject("ShowText");
            showTxtGo.transform.SetParent(showGo.transform, false);
            var showTxtRt = showTxtGo.AddComponent<RectTransform>();
            showTxtRt.anchorMin = Vector2.zero;
            showTxtRt.anchorMax = Vector2.one;
            showTxtRt.offsetMin = new Vector2(8f, 0f);
            showTxtRt.offsetMax = new Vector2(-8f, 0f);
            _chatInputShow = showTxtGo.AddComponent<TextMeshProUGUI>();
            _chatInputShow.fontSize = 15;
            _chatInputShow.color = new Color(0.92f, 0.95f, 1f);
            _chatInputShow.alignment = TextAlignmentOptions.Left;
            _chatInputShow.raycastTarget = false; // 文本不拦截（Button 处理点击）
            _chatInputShow.text = "";
            EnsureFont(_chatInputShow);
            showGo.SetActive(false); // 默认隐藏，聚焦时显示

            return field;
        }
        catch (System.Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"[UI] MakeChatInput NRE@{(new System.Diagnostics.StackTrace()).GetFrame(0)?.GetMethod()?.Name ?? "?"}: {ex.Message}");
            return null;
        }
    }

    /// <summary>重建聊天面板内容，按聚焦态切换显隐（常驻结构，不销毁重建——避免输入框失焦）。
    /// 未聚焦 → 背景/标题/输入框隐藏，**聊天记录常驻显示**（鼠标穿透：记录文本 raycastTarget=false）；
    /// 聚焦 → 显示背景 + 标题 + 记录 + 输入框，并激活 TMP_InputField 唤起中文 IME。
    /// 无参（从 CoopRuntime.Net 取）——避免 IL2CPP 自定义类型参数问题。</summary>
    private void RebuildChat()
    {
        if (_chatRoot == null || _chatContent == null) return;
        var net = CoopRuntime.Net;
        if (net == null) return;
        try
        {
            bool inSession = net.State == SessionState.Hosting || net.State == SessionState.Joined;
            bool show = inSession; // 聊天面板独立于主菜单：联机会话时始终显示
            _chatRoot.SetActive(show);
            CoopRuntime.LogSource?.LogInfo($"[UI] RebuildChat show={show} typing={_typing}");
            if (!show) return;

            // 聚焦态决定显隐
            bool focused = _typing;
            if (_chatBg != null) _chatBg.enabled = focused;              // 未聚焦隐藏背景（鼠标穿透）
            if (_chatHintBar != null) _chatHintBar.SetActive(!focused);  // 未聚焦显示提示条
            if (_chatTitle != null) _chatTitle.gameObject.SetActive(focused); // 标题聚焦时显示

            // 聊天记录：**始终常驻显示**（未聚焦也显示历史消息）——只在有内容时重建文本
            UpdateChatBody(net);

            // 输入框：聚焦时显示 + 定位底部 + 激活（唤起 IME）；未聚焦隐藏
            if (_chatInput != null)
            {
                bool inputActive = _chatInput.gameObject.activeSelf;
                if (inputActive != focused)
                {
                    _chatInput.gameObject.SetActive(focused);
                    inputActive = focused;
                }
                if (focused)
                {
                    // 每次聚焦定位输入框到底部
                    var irt = _chatInput.transform as RectTransform;
                    if (irt != null)
                    {
                        irt.anchorMin = irt.anchorMax = new Vector2(0f, 1f);
                        irt.pivot = new Vector2(0f, 1f);
                        irt.anchoredPosition = new Vector2(10f, -(ChatH - 28f - 8f));
                        irt.sizeDelta = new Vector2(ChatW - 20f, 28f);
                    }
                    // 同步当前 _chat 到输入框（保留未聚焦期间的输入）——TMP_InputField 文本
                    // 渲染可能不可靠，但同步它无害（叠加显示层负责可见）。
                    if (_chatInput.text != _chat)
                    {
                        try { _chatInput.text = _chat; }
                        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] set input text: {ex.Message}"); }
                    }
                    // ⚠️ 让 EventSystem 选中输入框（键盘事件才能到达 TMP_InputField）：
                    // 我们用的是独立 Canvas，但游戏有自己的 EventSystem.current。
                    // 未聚焦时选中游戏原本对象，聚焦时选中我们的输入框。
                    try
                    {
                        var es = UnityEngine.EventSystems.EventSystem.current;
                        if (es != null)
                        {
                            var cur = es.currentSelectedGameObject;
                            if (cur != _chatInput.gameObject)
                                es.SetSelectedGameObject(_chatInput.gameObject);
                        }
                    }
                    catch { }
                    // 激活输入框 → 唤起中文 IME（首次聚焦时调用，避免每次重建重置 IME 组合）
                    if (!_chatInput.isFocused)
                    {
                        try
                        {
                            _chatInput.ActivateInputField();
                            CoopRuntime.LogSource?.LogInfo("[UI] TMP_InputField activated (IME 唤起)");
                        }
                        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] ActivateInputField: {ex.Message}"); }
                    }
                }
            }
            // 叠加显示层：聚焦时显示输入内容（可靠渲染），未聚焦隐藏
            UpdateChatInputShow(focused);
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] RebuildChat: {ex.Message}"); }
    }

    /// <summary>更新输入框上方叠加显示层：显示 _chat 内容 + 光标（TMP_InputField 渲染不可靠时兜底可见）。</summary>
    private void UpdateChatInputShow(bool focused)
    {
        try
        {
            if (_chatInputShow == null) return;
            // 父对象（showGo：Image 背景 + Button）承载定位/显隐；
            // 子对象文本锚定父对象全屏（offsetMin/Max 已在创建时设置，不改）。
            Transform parentTf = _chatInputShow.transform.parent;
            if (parentTf == null) return;
            bool show = focused;
            if (parentTf.gameObject.activeSelf != show)
                parentTf.gameObject.SetActive(show);
            if (!show) return;
            // 与输入框同步定位（父对象定位；子对象锚定全屏自动跟随）
            var srt = parentTf as RectTransform;
            if (srt != null)
            {
                srt.anchorMin = srt.anchorMax = new Vector2(0f, 1f);
                srt.pivot = new Vector2(0f, 1f);
                srt.anchoredPosition = new Vector2(10f, -(ChatH - 28f - 8f));
                srt.sizeDelta = new Vector2(ChatW - 20f, 28f);
            }
            string disp = _chat;
            if (disp.Length == 0) disp = CoopLoc.ChatPlaceholder; // 空输入显示占位
            else disp += "|";                                     // 有内容显示光标
            if (_chatInputShow.text != disp) _chatInputShow.text = disp;
        }
        catch { }
    }

    /// <summary>更新聊天记录文本（常驻 _chatBody）：最新若干行，无内容显示占位。</summary>
    private void UpdateChatBody(NetManager net)
    {
        if (_chatBody == null) return;
        try
        {
            int maxRows = (int)((ChatH - 64f) / 18f);
            int start = Mathf.Max(0, net.ChatLog.Count - maxRows);
            string body = "";
            for (int i = start; i < net.ChatLog.Count; i++)
            {
                string line = net.ChatLog[i];
                if (line.Length > 52) line = line.Substring(0, 52) + "…";
                body += line + "\n";
            }
            if (body.Length == 0) body = CoopLoc.NoChat;
            if (_chatBody.text != body) _chatBody.text = body;
        }
        catch { }
    }

    /// <summary>聚焦聊天（未聚焦状态点提示条 / 回车呼出）。
    /// 统一聚焦流程：启用 IME → 显示输入框 → EventSystem 选中 → ActivateInputField（唤起输入法）。
    /// 顺序关键：必须先 SetActive(true) 再 ActivateInputField（inactive 时激活无效）。</summary>
    private void FocusChat()
    {
        _typing = true;
        _focusAt = UnityEngine.Time.realtimeSinceStartup; // 记录聚焦时刻（回车提交防抖）
        // ⚠️ 聊天聚焦时关闭主菜单（避免 _blocker 全屏拦截 / LockPlayer 干扰聊天输入）
        if (_menuOpen)
        {
            _menuOpen = false;
            try { ApplyMenuState(); } catch { }
        }
        SetImeActive(true); // Keyboard.SetIMEEnabled(true)（输入法允许）
        try
        {
            if (_chatInput != null)
            {
                if (!_chatInput.gameObject.activeSelf) _chatInput.gameObject.SetActive(true);
                // 定位输入框到底部（与 RebuildChat 一致）
                var irt = _chatInput.transform as RectTransform;
                if (irt != null)
                {
                    irt.anchorMin = irt.anchorMax = new Vector2(0f, 1f);
                    irt.pivot = new Vector2(0f, 1f);
                    irt.anchoredPosition = new Vector2(10f, -(ChatH - 28f - 8f));
                    irt.sizeDelta = new Vector2(ChatW - 20f, 28f);
                }
                // 同步当前文本
                if (_chatInput.text != _chat)
                {
                    try { _chatInput.text = _chat; } catch { }
                }
                // EventSystem 选中
                try
                {
                    var es = UnityEngine.EventSystems.EventSystem.current;
                    if (es != null) es.SetSelectedGameObject(_chatInput.gameObject);
                }
                catch { }
                // 激活输入框 → 唤起 IME
                if (!_chatInput.isFocused)
                {
                    try
                    {
                        _chatInput.ActivateInputField();
                        CoopRuntime.LogSource?.LogInfo("[UI] FocusChat: ActivateInputField (IME 唤起)");
                    }
                    catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] FocusChat Activate: {ex.Message}"); }
                }
            }
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] FocusChat: {ex.Message}"); }
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
    /// 文本输入入口（Harmony patch Keyboard.OnTextInput 转发）：CJK 中文 + 控制键（退格/回车）。
    /// 英文/数字/符号走 PollInput 物理键（PostKeyTextInput 只转发 CJK，避免双通道）。
    /// </summary>
    public static void OnImeText(char c)
    {
        try
        {
            var inst = Instance;
            if (inst == null || !inst._typing) return;
            if (c == '\b') { inst.Backspace(); return; }      // 退格
            if (c == '\r' || c == '\n')
            {
                // 回车提交（防抖：呼出那下 0.3s 内忽略，避免刚聚焦就发送失焦）
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now - inst._focusAt > 0.3f) inst.Submit();
                return;
            }
            inst.Append(c); // 中文 CJK 字符
        }
        catch { }
    }

    /// <summary>切换输入态（点输入框）——同步启用/禁用 IME + 激活/停用 TMP_InputField。</summary>
    private void ToggleTyping()
    {
        _typing = !_typing;
        SetImeActive(_typing);
    }

    /// <summary>退出输入态（点按钮/提交/菜单关）——停用 TMP_InputField（IME 收起）+ 禁用 IME。
    /// 诊断：打印调用来源（定位"聚焦后立即失焦"根因）。</summary>
    private void StopTyping()
    {
        CoopRuntime.LogSource?.LogInfo($"[UI] StopTyping called from: {new System.Diagnostics.StackTrace(1, false)?.GetFrame(0)?.GetMethod()?.Name}");
        if (_typing)
        {
            SetImeActive(false);
            try { if (_chatInput != null && _chatInput.isFocused) _chatInput.DeactivateInputField(); } catch { }
        }
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
                    CoopRuntime.LogSource?.LogInfo($"[UI] Enter opens chat (state={net.State})");
                    FocusChat();
                    return; // 回车已消费为呼出，不再提交
                }
            }

            if (!_typing) return;
            bool idle = net != null && net.State == SessionState.Idle;
            if (idle)
            {
                // 房间名输入（Idle 状态，主菜单自制输入框）：物理字符处理（英文/数字/符号）。
                // 房间名非聊天，无需中文 IME；保留 PollInput 物理键。
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
            else
            {
                // 聊天输入（inSession）方案：
                // - 中文：TMP_InputField 唤起 IME → 候选字确认写 text → Update 轮询增量同步 _chat。
                // - 英文/数字/符号：PollInput 物理键直接处理。
                // ⚠️ IME 组合中（compositionLength>0，拼音候选窗口打开）：**跳过所有物理字符**
                //   （字母/数字/空格/符号）——它们都是 IME 的拼音输入/候选字选择（空格确认、1-9 选字），
                //   不能 Append 到 _chat，否则与 IME 冲突（"空格/数字键选候选字不行"根因）。
                //   组合完成（compositionLength==0）后才处理物理字符（英文输入）。
                // - 控制键：退格（PollInput + 清 TMP_InputField.text 尾部）、回车提交（防抖）。
                bool imeComposing = false;
                try { if (_chatInput != null) imeComposing = _chatInput.compositionLength > 0; } catch { }

                if (kb.backspaceKey.wasPressedThisFrame)
                {
                    Backspace();
                    // 同步清 TMP_InputField.text（否则轮询会把退格前的字符再补回来）
                    try { if (_chatInput != null && _chatInput.text.Length > 0) _chatInput.text = _chatInput.text.Substring(0, _chatInput.text.Length - 1); } catch { }
                }
                // ⚠️ 回车提交防抖：回车呼出聚焦后（同帧/下一帧 enterKey.wasPressedThisFrame 仍为 true），
                // 会立即触发 Submit → StopTyping → 输入框刚聚焦就失焦。聚焦后 0.3s 内忽略回车提交。
                float now = UnityEngine.Time.realtimeSinceStartup;
                bool enterDown = kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
                if (enterDown && now - _focusAt > 0.3f) { Submit(); return; }

                // IME 组合中：跳过所有物理字符（交给 IME 处理拼音/选字/空格确认）
                if (imeComposing) { return; }

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
        }
        catch { }
    }

    private void AppendKey(UnityEngine.InputSystem.Controls.KeyControl k, char ch, bool shift)
    {
        // 诊断：英文物理键是否触发（确认 PollInput 聊天分支英文输入路径）
        if (k != null && k.wasPressedThisFrame && ((++_keyDiag % 40) == 1))
            CoopRuntime.LogSource?.LogInfo($"[UI] AppendKey '{ch}' typing={_typing}");
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
            // 清空输入框 text（否则下次聚焦轮询会把旧文本读回 _chat）
            try { if (_chatInput != null) _chatInput.text = ""; } catch { }
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
            // ⚠️ 中文字体：默认字体（CourierPrime-Regular SDF）不含中文字形 → 中文显示方框 □。
            // 优先尝试本地化字体，失败则运行时找非默认字体（含中文字形）。
            if (_font == null)
            {
                string defName = "";
                try { if (TMP_Settings.defaultFontAsset != null) defName = TMP_Settings.defaultFontAsset.name ?? ""; } catch { }
                // 1) 本地化字体（仅 BepInEx：MLL 的 Il2CppLocalisation.LocalisationManager.GetFont
                //    方法缺失，MissingMethodException 在 IL2CPP trampoline 抛出且 managed catch 捕不到，
                //    会中断本方法 → 用独立 helper 隔离，MLL 编译期排除，避免中断 FindObjectsOfType 兜底）
                TryGetLocalisationFont(ref _font, defName);
                // 2) 兜底：运行时找所有 TMP_FontAsset，挑非默认的（中文等语言字体，含中文字形）。
                if (_font == null)
                {
                    try
                    {
                        var all = UnityEngine.Object.FindObjectsOfType<TMP_FontAsset>(true);
                        if (all != null)
                        {
                            foreach (var f in all)
                            {
                                if (f == null) continue;
                                string fn = "";
                                try { fn = f.name ?? ""; } catch { }
                                if (fn.Length > 0 && fn != defName && fn.IndexOf("Default", StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    _font = f;
                                    CoopRuntime.LogSource?.LogInfo($"[UI] EnsureFont: 运行时找到字体 '{fn}' (default='{defName}')");
                                    break;
                                }
                            }
                        }
                    }
                    catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] EnsureFont 扫描: {ex.Message}"); }
                }
                if (_font == null) _font = TMP_Settings.defaultFontAsset;
            }
            if (_font != null) t.font = _font;
        }
        catch { }
    }

#if !MELONLOADER
    /// <summary>本地化字体获取（BepInEx 专用）：LocalisationManager.GetFont 按当前语言返回字体。
    /// ⚠️ MLL 下该 interop 方法缺失（MissingMethodException 在 IL2CPP trampoline 抛出，managed
    /// catch 捕获不到会中断调用链）——因此 MLL 编译期排除此方法，MLL 只用 FindObjectsOfType 兜底。</summary>
    private static void TryGetLocalisationFont(ref TMP_FontAsset target, string defName)
    {
        try
        {
            var lm = Localisation.LocalisationManager.Instance;
            if (lm != null)
            {
                var f = lm.GetFont(TMP_Settings.defaultFontAsset);
                if (f != null && f.name != defName)
                {
                    target = f;
                    CoopRuntime.LogSource?.LogInfo($"[UI] EnsureFont: GetFont='{f.name}' lang={lm.CurrentLanguage}");
                }
            }
        }
        catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[UI] EnsureFont GetFont: {ex.Message}"); }
    }
#else
    private static void TryGetLocalisationFont(ref TMP_FontAsset target, string defName) { }
#endif

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
