using UnityEngine;
using OpenNestCoop.Core.Loc;
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

    /// <summary>在每次界面刷新时调用，跟随游戏语言（同时把语言代码告诉 <see cref="LocFile"/> → 决定读哪一段键）。</summary>
    public static void Refresh()
    {
        Current = Detect();
        LocFile.SetLanguage(Code(Current));
    }

    /// <summary>语言代码（"zh"/"en"）—— 语言键文件里的段名。</summary>
    public static string Code(Lang l) => l == Lang.En ? "en" : "zh";

    private static Lang Detect()
    {
        // 1) 原生 UI 桥接（OpenNestCore.UI.NativeUi）——经 IronNestNativeUi 读游戏 LocalisationManager
        try
        {
            var lang = NativeUi.CurrentLanguage;
            if (!string.IsNullOrEmpty(lang))
            {
                lang = lang.ToLowerInvariant();
                if (lang.Contains("zh") || lang.Contains("chi") || lang.Contains("cn")) return Lang.Zh;
                if (lang.StartsWith("en")) return Lang.En;
            }
        }
        catch { }

        // 2) 兜底：游戏本地化管理器（桥接未注册时直读）
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

    /// <summary>按当前语言返回文案（未上语言键的临时/日志文案用；UI 文案请走 <see cref="LocFile"/> 键）。</summary>
    public static string T(string zh, string en) => Current == Lang.Zh ? zh : en;

    // ---------------- 语言键（文本来自外部语言文件 OpenNestCoop.lang.ini，见 docs/LOCALIZATION.md）----------------

    public static string MenuToggle => LocFile.Get("MenuToggle"); // 左上角开关 + 主菜单/ESC 入口共用
    public static string Title => LocFile.Get("Title");
    public static string DefaultRoomName => LocFile.Get("DefaultRoomName");
    public static string State => LocFile.Get("State");
    public static string SteamReady => LocFile.Get("SteamReady");
    public static string SteamInit => LocFile.Get("SteamInit");

    public static string RoomNameLabel => LocFile.Get("RoomNameLabel");
    public static string RoomNamePlaceholder => LocFile.Get("RoomNamePlaceholder");
    public static string RoomPassword => LocFile.Get("RoomPassword");
    public static string RoomPasswordPlaceholder => LocFile.Get("RoomPasswordPlaceholder");
    public static string PasswordRequired => LocFile.Get("PasswordRequired");
    public static string PasswordError => LocFile.Get("PasswordError");
    public static string Confirm => LocFile.Get("Confirm");
    public static string Cancel => LocFile.Get("Cancel");
    public static string VersionMismatch => LocFile.Get("VersionMismatch");
    public static string RejoinLast => LocFile.Get("RejoinLast");
    public static string RoomPwd => LocFile.Get("RoomPwd");
    public static string PwdHas => LocFile.Get("PwdHas");
    public static string PwdNone => LocFile.Get("PwdNone");
    public static string MaxPlayers => LocFile.Get("MaxPlayers");
    public static string CreateLobby => LocFile.Get("CreateLobby");
    public static string RefreshLobbies => LocFile.Get("RefreshLobbies");
    public static string Refreshing => LocFile.Get("Refreshing");
    public static string NoLobbies => LocFile.Get("NoLobbies");
    public static string Join => LocFile.Get("Join");
    public static string Full => LocFile.Get("Full");
    public static string InviteHint => LocFile.Get("InviteHint");
    /// <summary>“🔒 有密码”标记（房间列表前缀）。</summary>
    public static string Locked => LocFile.Get("Locked");
    /// <summary>“旧版？”——房间版本与本体不一致时的标记。</summary>
    public static string OldVersion => LocFile.Get("OldVersion");

    public static string Room => LocFile.Get("Room");
    public static string Leave => LocFile.Get("Leave");
    public static string Members => LocFile.Get("Members");
    public static string MyRole => LocFile.Get("MyRole");
    public static string HostTag => LocFile.Get("HostTag");
    public static string YouTag => LocFile.Get("YouTag");
    public static string Chat => LocFile.Get("Chat");
    public static string ChatPlaceholder => LocFile.Get("ChatPlaceholder");
    public static string Send => LocFile.Get("Send");
    public static string NoChat => LocFile.Get("NoChat");
    public static string Kick => LocFile.Get("Kick");
    public static string Invite => LocFile.Get("Invite");
    public static string KickedHint => LocFile.Get("KickedHint");
    public static string ChatHint => LocFile.Get("ChatHint");
    public static string ChatEnterHint => LocFile.Get("ChatEnterHint");

    // ---------------- 局域网（LAN）选项卡 ----------------
    public static string TabSteam => LocFile.Get("TabSteam");
    public static string TabLan => LocFile.Get("TabLan");
    public static string LanCreate => LocFile.Get("LanCreate");
    public static string LanScan => LocFile.Get("LanScan");
    public static string LanScanning => LocFile.Get("LanScanning");
    public static string LanNoRooms => LocFile.Get("LanNoRooms");
    public static string LanIpLabel => LocFile.Get("LanIpLabel");
    /// <summary>主机 IP 输入框的占位示例。</summary>
    public static string IpPlaceholder => LocFile.Get("IpPlaceholder");
    public static string LanPortLabel => LocFile.Get("LanPortLabel");
    public static string LanMyIp => LocFile.Get("LanMyIp");
    public static string LanHint => LocFile.Get("LanHint");
    public static string LanIdentity => LocFile.Get("LanIdentity");
    public static string LanName => LocFile.Get("LanName");
    public static string LanNamePlaceholder => LocFile.Get("LanNamePlaceholder");
    public static string Copy => LocFile.Get("Copy");
    public static string Paste => LocFile.Get("Paste");
    public static string Copied => LocFile.Get("Copied");
    public static string LanJoining => LocFile.Get("LanJoining");
    public static string LanMismatch => LocFile.Get("LanMismatch");
    /// <summary>局域网房间列表超出面板高度时的提示（{0} = 未显示数量）。</summary>
    public static string LanMoreRooms => LocFile.Get("LanMoreRooms");
    /// <summary>复制失败提示（{0} = 要复制的文本）。</summary>
    public static string ErrCopyFailed => LocFile.Get("ErrCopyFailed");

    public static string StatusIdle => LocFile.Get("StatusIdle");
    public static string StatusHosting => LocFile.Get("StatusHosting");
    public static string StatusJoined => LocFile.Get("StatusJoined");

    public static string RoleNone => LocFile.Get("RoleNone");
    public static string RoleCommander => LocFile.Get("RoleCommander");
    public static string RoleGunner => LocFile.Get("RoleGunner");
    public static string RoleLoader => LocFile.Get("RoleLoader");
    public static string RoleFireControl => LocFile.Get("RoleFireControl");

    // 本地模式大号角色徽章（HOST/CLIENT + 状态）
    public static string HostBadge => LocFile.Get("HostBadge");
    public static string ClientBadge => LocFile.Get("ClientBadge");
    public static string Standby => LocFile.Get("Standby");
    public static string Online => LocFile.Get("Online");
    // 联机大厅面板右上角关闭按钮（CoopUIManager 用）
    public static string Close => LocFile.Get("Close");
}
