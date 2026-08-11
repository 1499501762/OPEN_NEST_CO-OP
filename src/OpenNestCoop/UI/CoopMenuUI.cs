using OpenNestCoop.Net;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.UI;

/// <summary>
/// 联机主菜单（IMGUI）。
/// 注意：该游戏是 IL2CPP 裁剪构建，GUILayout.*（自动布局）已被 Unity 裁剪掉
/// （运行时报 “Method unstripping failed”）。因此本菜单全部使用 GUI.*（Rect 手动布局），
/// 只有这些方法在游戏里被保留可用。
/// </summary>
public static class CoopMenuUI
{
    // GUI.TextField 被 IL2CPP 裁剪（文本输入状态对象被移除），改用手动键盘捕获。
    private static string _roomName = "";
    private static string _chat = "";
    private static bool _typing;
    private static bool _richInit;
    private static int _browserOffset;
    private static int _chatOffset;

    private const float PanelW = 470f;
    private const float PanelH = 650f;

    public static void Draw()
    {
        var net = CoopRuntime.Net;
        if (net == null) return;

        if (!_richInit)
        {
            GUI.skin.label.richText = true;
            _richInit = true;
        }

        // 左上角开关
        var toggleRect = new Rect(8, 8, 130, 26);
        if (Button(toggleRect, net.MenuOpen ? "▣ 关闭联机菜单" : "☰ 联机菜单"))
            net.MenuOpen = !net.MenuOpen;

        if (!net.MenuOpen)
        {
            if (_typing) _typing = false;
            return;
        }

        // 主面板
        var px = (Screen.width - PanelW) / 2f;
        var py = 40f;
        GUI.Box(new Rect(px, py, PanelW, PanelH), "");

        float x = px + 10f;
        float w = PanelW - 20f;
        float cy = py + 10f;

        Label(x, cy, w, 22, "<b><size=16>Open Nest 联机</size></b>"); cy += 26;
        Label(x, cy, w, 20, $"Steam: {net.Local?.Name}"); cy += 24;
        Label(x, cy, w, 20, $"状态: <b>{StateText(net.State)}</b>"); cy += 22;
        Label(x, cy, w, 20, $"Steam: <b>{(net.SteamReady ? "<color=#8f8>已就绪</color>" : "<color=#fc0>初始化中…</color>")}</b>"); cy += 24;
        if (net.LastError.Length > 0)
        {
            Label(x, cy, w, 20, $"<color=#ff6b6b>{net.LastError}</color>"); cy += 24;
        }
        cy += 4;

        if (net.State == SessionState.Idle)
            cy = DrawIdle(net, x, cy, w, py);
        else
            cy = DrawLobby(net, x, cy, w, py);

        Label(x, py + PanelH - 22, w, 18, "<color=#666>M1 原型：大厅 / 传输 / 聊天</color>");
    }

    // ---- 大厅（未联机） ----

    private static float DrawIdle(NetManager net, float x, float cy, float w, float py)
    {
        Label(x, cy, 80, 20, "房间名称"); cy += 24;
        if (_roomName.Length == 0 && net.PendingLobbyName.Length > 0) _roomName = net.PendingLobbyName;
        DrawInputBox(x, cy, w - 56, 24, _roomName);
        if (Button(x + w - 52, cy, 46, 24, _typing ? "停" : "✎"))
            _typing = !_typing;
        cy += 32;

        Label(x, cy, 80, 22, "最大人数");
        if (Button(x + 100, cy, 26, 22, "-")) net.PendingMaxPlayers = Mathf.Max(2, net.PendingMaxPlayers - 1);
        Label(x + 132, cy, 40, 22, $" <b>{net.PendingMaxPlayers}</b>");
        if (Button(x + 178, cy, 26, 22, "+")) net.PendingMaxPlayers = Mathf.Min(8, net.PendingMaxPlayers + 1);
        cy += 30;

        if (Button(x, cy, w, 32, "创建房间（Steam 大厅）"))
        {
            net.PendingLobbyName = string.IsNullOrWhiteSpace(_roomName) ? "联机房间" : _roomName;
            net.CreateLobby();
        }
        cy += 40;

        if (Button(x, cy, 130, 26, "刷新大厅列表")) net.RefreshBrowser();
        if (net.Refreshing) Label(x + 140, cy, 90, 26, "刷新中…");
        cy += 34;

        if (net.Browser.Count == 0)
        {
            Label(x, cy, w, 20, "<color=#999>（暂无其他联机房间，可邀请 Steam 好友）</color>"); cy += 26;
        }
        else
        {
            cy += 4;
            // 简单分页：一页 6 行
            const int pageRows = 6;
            int pages = Mathf.Max(1, (net.Browser.Count + pageRows - 1) / pageRows);
            _browserOffset = Mathf.Clamp(_browserOffset, 0, pages - 1);
            int start = _browserOffset * pageRows;
            int end = Mathf.Min(start + pageRows, net.Browser.Count);
            for (int i = start; i < end; i++)
            {
                if (cy > py + PanelH - 60) break;
                var info = net.Browser[i];
                var title = info.IsFull
                    ? $"{info.Name}  <color=#888>{info.Players}/{info.MaxPlayers} 已满</color>"
                    : $"{info.Name}  <color=#8f8>{info.Players}/{info.MaxPlayers}</color>";
                Label(x, cy, w - 84, 22, title);
                if (!info.IsFull && Button(x + w - 78, cy, 72, 22, "加入"))
                    net.JoinLobby(info);
                cy += 26;
            }
            if (pages > 1)
            {
                if (Button(x, cy, 44, 20, "▲") && _browserOffset > 0) _browserOffset--;
                Label(x + 52, cy, 40, 20, $"{_browserOffset + 1}/{pages}");
                if (Button(x + 96, cy, 44, 20, "▼") && _browserOffset < pages - 1) _browserOffset++;
                cy += 26;
            }
        }
        cy += 4;
        Label(x, cy, w, 20, "<color=#999>提示：可在 Steam 好友列表右键邀请，接受后自动加入。</color>"); cy += 26;
        return cy;
    }

    // ---- 房间内 ----

    private static float DrawLobby(NetManager net, float x, float cy, float w, float py)
    {
        Label(x, cy, w - 70, 22, $"房间: <b>{net.PendingLobbyName}</b>");
        if (Button(x + w - 66, cy, 60, 22, "离开")) net.LeaveSession();
        cy += 28;

        Label(x, cy, w, 20, $"成员 ({net.Roster.Count}/{net.Lobby.MaxPlayers})  我的角色: {RoleText(net.Local?.Role)}"); cy += 24;
        foreach (var p in net.Roster)
        {
            var me = p.IsLocal ? "  <color=#7cf>(你)</color>" : "";
            var host = p.IsHost ? "  <color=#fc0>主机</color>" : "";
            var ping = p.IsLocal ? "" : $"  <color=#aaa>{p.PingMs:0}ms</color>";
            Label(x, cy, w, 20, $"#{p.PlayerId}  {p.Name}{host}{me}{ping}"); cy += 22;
        }

        cy += 6;

        // 聊天区（倒序显示最近 8 条 + 简单翻页）
        Label(x, cy, w, 20, "聊天"); cy += 24;
        const int chatRows = 8;
        int maxOffset = Mathf.Max(0, net.ChatLog.Count - chatRows);
        _chatOffset = Mathf.Clamp(_chatOffset, 0, maxOffset);
        int cstart = Mathf.Max(0, net.ChatLog.Count - chatRows - _chatOffset);
        for (int i = cstart; i < net.ChatLog.Count; i++)
        {
            if (cy > py + PanelH - 70) break;
            Label(x, cy, w, 18, net.ChatLog[i]); cy += 20;
        }
        if (net.ChatLog.Count > chatRows)
        {
            if (Button(x, cy, 40, 20, "▲") && _chatOffset < maxOffset) _chatOffset++;
            if (Button(x + 46, cy, 40, 20, "▼") && _chatOffset > 0) _chatOffset--;
            cy += 26;
        }
        else cy += 2;

        DrawInputBox(x, cy, w - 126, 24, _chat);
        if (Button(x + w - 122, cy, 60, 24, _typing ? "停" : "✎"))
            _typing = !_typing;
        if (Button(x + w - 58, cy, 52, 24, "发送"))
        {
            if (_chat.Trim().Length > 0) { net.SendChat(_chat); _chat = ""; }
        }
        cy += 32;

        Label(x, cy, w, 18, "<color=#999>M1：大厅与传输已就绪。炮塔/任务同步将在下一阶段接入。</color>"); cy += 22;
        return cy;
    }

    // ---- 键盘输入（替代被裁剪的 GUI.TextField） ----

    /// <summary>每帧从 CoopBehaviour.Update 调用。仅当 _typing（用户点了 ✎）时捕获按键。</summary>
    public static void PollInput()
    {
        if (!_typing) return;
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;

            bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

            if (kb.backspaceKey.wasPressedThisFrame) Backspace();
            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
            {
                Submit();
                return;
            }

            // 字母
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

            // 数字
            AppendKey(kb.digit0Key, '0', false); AppendKey(kb.digit1Key, '1', false);
            AppendKey(kb.digit2Key, '2', false); AppendKey(kb.digit3Key, '3', false);
            AppendKey(kb.digit4Key, '4', false); AppendKey(kb.digit5Key, '5', false);
            AppendKey(kb.digit6Key, '6', false); AppendKey(kb.digit7Key, '7', false);
            AppendKey(kb.digit8Key, '8', false); AppendKey(kb.digit9Key, '9', false);

            // 空格与常用标点
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

    private static void AppendKey(UnityEngine.InputSystem.Controls.KeyControl k, char ch, bool shift)
    {
        if (k != null && k.wasPressedThisFrame)
            Append(shift ? char.ToUpper(ch) : ch);
    }

    private static void Append(char c)
    {
        if (IsRoomMode())
        {
            if (_roomName.Length < 40) _roomName += c;
        }
        else
        {
            if (_chat.Length < 120) _chat += c;
        }
    }

    private static void Backspace()
    {
        if (IsRoomMode())
        {
            if (_roomName.Length > 0) _roomName = _roomName.Substring(0, _roomName.Length - 1);
        }
        else
        {
            if (_chat.Length > 0) _chat = _chat.Substring(0, _chat.Length - 1);
        }
    }

    private static bool IsRoomMode()
        => CoopRuntime.Net != null && CoopRuntime.Net.State == SessionState.Idle;

    private static void Submit()
    {
        var net = CoopRuntime.Net;
        if (net == null) return;
        if (IsRoomMode())
        {
            net.PendingLobbyName = string.IsNullOrWhiteSpace(_roomName) ? "联机房间" : _roomName;
            net.CreateLobby();
            _typing = false;
        }
        else
        {
            if (_chat.Trim().Length > 0) { net.SendChat(_chat); _chat = ""; }
        }
    }

    // ---- 手动按钮（GUI.Button 的点击被游戏 UGUI EventSystem 拦截，改用手动命中检测） ----

    private static bool _clickQueued;   // 本帧是否有点击（Update 里用 isPressed 边沿检测）
    private static bool _prevLeft;
    private static int _clickFrame = -1;
    private static bool _mouseDiagDone;

    /// <summary>每帧在 CoopBehaviour.Update 中调用：用 Mouse.current.leftButton.isPressed 做边沿检测。
    /// 读取原始设备状态，即使游戏 UGUI 拦截了 IMGUI 事件也能检测到点击。</summary>
    public static void TrackClick()
    {
        bool pressed = false;
        try
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            pressed = mouse != null && mouse.leftButton.isPressed;
            if (!_mouseDiagDone)
            {
                _mouseDiagDone = true;
                var kb = UnityEngine.InputSystem.Keyboard.current;
                CoopRuntime.LogSource?.LogInfo($"[输入] Mouse.current={(mouse != null ? "可用" : "NULL")} Keyboard.current={(kb != null ? "可用" : "NULL")}");
            }
        }
        catch { }
        _clickQueued = pressed && !_prevLeft;
        _prevLeft = pressed;
    }

    /// <summary>手动按钮：绘制 + 悬停高亮 + 点击命中检测。</summary>
    private static bool Button(float x, float y, float w, float h, string text)
    {
        bool queued = _clickQueued;
        // 兜底：若 Mouse.current 不可用，退化为 IMGUI 的 MouseDown 事件
        if (_clickFrame != Time.frameCount)
        {
            _clickFrame = Time.frameCount;
            if (!queued)
            {
                try
                {
                    var evt = Event.current;
                    if (evt != null && evt.type == EventType.MouseDown) queued = true;
                }
                catch { }
            }
        }
        var rect = new Rect(x, y, w, h);
        bool hover = false;
        try
        {
            if (Event.current != null)
                hover = rect.Contains(Event.current.mousePosition);
        }
        catch { }
        GUI.Box(rect, hover ? "▸ " + text : text);
        return queued && hover;
    }

    private static bool Button(Rect r, string text)
        => Button(r.x, r.y, r.width, r.height, text);

    private static void DrawInputBox(float x, float y, float w, float h, string text)
    {
        GUI.Box(new Rect(x, y, w, h), "");
        var display = text.Length > 0 ? text : "<color=#888>（点击 ✎ 开始输入）</color>";
        if (_typing) display += "▏";
        GUI.Label(new Rect(x + 4, y, w - 8, h), display);
    }

    // ---- 工具 ----

    private static void Label(float x, float y, float w, float h, string text)
        => GUI.Label(new Rect(x, y, w, h), text);

    private static string StateText(SessionState s) => s switch
    {
        SessionState.Idle => "大厅（未联机）",
        SessionState.Hosting => "主机（等待成员）",
        SessionState.Joined => "已加入（客户端）",
        _ => s.ToString()
    };

    private static string RoleText(CrewRole? r) => r switch
    {
        CrewRole.Commander => "指挥官（主机）",
        CrewRole.Gunner => "瞄准手",
        CrewRole.Loader => "装填手",
        CrewRole.FireControl => "射击诸元",
        _ => "待分配"
    };
}

