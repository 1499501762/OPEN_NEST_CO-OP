using UnityEngine;
#if MELONLOADER
using Localisation = Il2CppLocalisation;
#endif

namespace OpenNestCoop.UI;

/// <summary>
/// 本地化：跟随游戏语言自动切换中文/英文。
/// 每个 UI 文本对应一个"语言键"（属性），zh/en 文案集中在此，便于后续扩展或接入完整本地化表。
/// 语言检测顺序：游戏 LocalisationManager.CurrentLanguage → Application.systemLanguage。
/// </summary>
public static class CoopLoc
{
    public enum Lang { Zh, En }

    public static Lang Current { get; private set; } = Lang.Zh;

    /// <summary>在每次界面刷新时调用，跟随游戏语言。</summary>
    public static void Refresh()
    {
        Current = Detect();
    }

    private static Lang Detect()
    {
        // 1) 游戏本地化管理器
        try
        {
            var mgr = Localisation.LocalisationManager.Instance;
            if (mgr != null && !string.IsNullOrEmpty(mgr.CurrentLanguage))
            {
                var lang = mgr.CurrentLanguage.ToLowerInvariant();
                if (lang.Contains("zh") || lang.Contains("chi") || lang.Contains("cn")) return Lang.Zh;
                if (lang.StartsWith("en")) return Lang.En;
            }
        }
        catch { }

        // 2) 回退：系统语言
        try
        {
            var sys = Application.systemLanguage;
            return (sys == SystemLanguage.Chinese
                    || sys == SystemLanguage.ChineseSimplified
                    || sys == SystemLanguage.ChineseTraditional)
                ? Lang.Zh : Lang.En;
        }
        catch { return Lang.Zh; }
    }

    /// <summary>按当前语言返回文案。</summary>
    public static string T(string zh, string en) => Current == Lang.Zh ? zh : en;

    // ---------------- 语言键 ----------------

    public static string MenuToggle => T("联机菜单", "Co-op Menu");
    public static string Title => T("Open Nest 联机", "Open Nest Co-op");
    public static string DefaultRoomName => T("Nest 联机房间", "Nest Co-op Room");
    public static string State => T("状态", "State");
    public static string SteamReady => T("已就绪", "Ready");
    public static string SteamInit => T("初始化中...", "Initializing...");

    public static string RoomNameLabel => T("房间名称", "Room Name");
    public static string RoomNamePlaceholder => T("(点击此处输入)", "(click to type)");
    public static string MaxPlayers => T("最大人数", "Max Players");
    public static string CreateLobby => T("创建房间(Steam 大厅)", "Create Lobby (Steam)");
    public static string RefreshLobbies => T("刷新大厅列表", "Refresh List");
    public static string Refreshing => T("刷新中...", "Refreshing...");
    public static string NoLobbies => T("(暂无其他联机房间，可邀请 Steam 好友)", "(No rooms yet - invite Steam friends)");
    public static string Join => T("加入", "Join");
    public static string Full => T("已满", "Full");
    public static string InviteHint => T("提示: 可在 Steam 好友列表右键邀请，接受后自动加入。", "Tip: right-click a Steam friend to invite; they join automatically.");

    public static string Room => T("房间", "Room");
    public static string Leave => T("离开", "Leave");
    public static string Members => T("成员", "Members");
    public static string MyRole => T("我的角色", "My Role");
    public static string HostTag => T("[主机]", "[Host]");
    public static string YouTag => T("(你)", "(you)");
    public static string Chat => T("聊天", "Chat");
    public static string ChatPlaceholder => T("(点击此处输入，回车发送)", "(click to type, Enter to send)");
    public static string Send => T("发送", "Send");
    public static string NoChat => T("(暂无消息，按回车开始聊天)", "(No messages - press Enter to chat)");
    public static string Kick => T("踢出", "Kick");
    public static string Invite => T("邀请", "Invite");
    public static string KickedHint => T("你已被主机移出房间", "You were removed by the host");
    public static string ChatHint => T("聊天已在左侧面板，按回车呼出", "Chat is on the left panel - press Enter");
    public static string ChatEnterHint => T("📢 按回车聊天", "📢 Enter to chat");

    public static string StatusIdle => T("大厅(未联机)", "Lobby (idle)");
    public static string StatusHosting => T("主机(等待成员)", "Host (waiting)");
    public static string StatusJoined => T("已加入(客户端)", "Joined (client)");

    public static string RoleNone => T("待分配", "Unassigned");
    public static string RoleCommander => T("指挥官(主机)", "Commander (host)");
    public static string RoleGunner => T("瞄准手", "Gunner");
    public static string RoleLoader => T("装填手", "Loader");
    public static string RoleFireControl => T("射击诸元", "Fire Control");

    // 本地模式大号角色徽章（HOST/CLIENT + 状态）
    public static string HostBadge => T("主机", "HOST");
    public static string ClientBadge => T("客户端", "CLIENT");
    public static string Standby => T("待机", "standby");
    public static string Online => T("联机中", "online");
}
