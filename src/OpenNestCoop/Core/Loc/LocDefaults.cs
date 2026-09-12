namespace OpenNestCoop.Core.Loc;

/// <summary>
/// **内置语言键表**（键 → 中文 / English）。
///
/// ⚠️ 它不是“UI 文本的最终来源”——最终来源是外部语言文件
/// （`BepInEx/config/OpenNestCoop.lang.ini` 或 `<游戏目录>/UserData/OpenNestCoop.lang.ini`，见 `docs/LOCALIZATION.md`）：
///   - 首次运行/文件缺失/新增键 → 用本表**生成/补齐**语言文件；
///   - 运行时以**文件**为准（缺键才回退到本表 → 再回退到键名）。
/// 所以改文案请改语言文件（热重载，不用重启），改默认值才动本表。
/// </summary>
internal static class LocDefaults
{
    /// <summary>(键, 中文, English)。键 = `CoopLoc` 的属性名 / `LocFile.Get("…")` 的实参。</summary>
    public static readonly (string Key, string Zh, string En)[] Items =
    {
        // ---------------- 联机菜单（Steam / 通用）----------------
        ("MenuToggle", "联机菜单", "Coop Menu"),
        ("Title", "Open Nest 联机", "Open Nest Co-op"),
        ("DefaultRoomName", "Nest 联机房间", "Nest Co-op Room"),
        ("State", "状态", "State"),
        ("SteamReady", "已就绪", "Ready"),
        ("SteamInit", "初始化中...", "Initializing..."),
        ("RoomNameLabel", "房间名称", "Room Name"),
        ("RoomNamePlaceholder", "(点击此处输入)", "(click to type)"),
        ("RoomPassword", "房间密码(可选)", "Room Password (optional)"),
        ("RoomPasswordPlaceholder", "(留空=无密码)", "(empty = no password)"),
        ("PasswordRequired", "该房间需要密码", "This room is password protected"),
        ("PasswordError", "密码错误", "Wrong password"),
        ("Confirm", "确定", "OK"),
        ("Cancel", "取消", "Cancel"),
        ("VersionMismatch", "模组版本不一致", "Mod version mismatch"),
        ("RejoinLast", "重连上次房间", "Rejoin Last"),
        ("RoomPwd", "房间密码", "Room Password"),
        ("PwdHas", "有密码", "Password"),
        ("PwdNone", "无", "None"),
        ("MaxPlayers", "最大人数", "Max Players"),
        ("CreateLobby", "创建房间(Steam 大厅)", "Create Lobby (Steam)"),
        ("RefreshLobbies", "刷新大厅列表", "Refresh List"),
        ("Refreshing", "刷新中...", "Refreshing..."),
        ("NoLobbies", "(暂无其他联机房间，可邀请 Steam 好友)", "(No rooms yet - invite Steam friends)"),
        ("Join", "加入", "Join"),
        ("Full", "已满", "Full"),
        ("InviteHint", "提示: 可在 Steam 好友列表右键邀请，接受后自动加入。", "Tip: right-click a Steam friend to invite; they join automatically."),
        ("Locked", "[锁]", "[pw]"),
        ("OldVersion", "旧版?", "old?"),

        // ---------------- 房间内 / 成员 / 聊天 ----------------
        ("Room", "房间", "Room"),
        ("Leave", "离开", "Leave"),
        ("Members", "成员", "Members"),
        ("MyRole", "我的角色", "My Role"),
        ("HostTag", "[主机]", "[Host]"),
        ("YouTag", "(你)", "(you)"),
        ("Chat", "聊天", "Chat"),
        ("ChatPlaceholder", "(点击此处输入，回车发送)", "(click to type, Enter to send)"),
        ("Send", "发送", "Send"),
        ("NoChat", "(暂无消息，按回车开始聊天)", "(No messages - press Enter to chat)"),
        ("Kick", "踢出", "Kick"),
        ("Invite", "邀请", "Invite"),
        ("KickedHint", "你已被主机移出房间", "You were removed by the host"),
        ("ChatHint", "聊天已在左侧面板，按回车呼出", "Chat is on the left panel - press Enter"),
        ("ChatEnterHint", "[Enter] 按回车聊天", "[Enter] press Enter to chat"),
        ("Close", "关闭", "Close"),
        ("HostBadge", "主机", "HOST"),
        ("ClientBadge", "客户端", "CLIENT"),
        ("Standby", "待机", "standby"),
        ("Online", "联机中", "online"),
        ("LocalHostName", "主机(本地)", "Host(local)"),
        ("LocalClientName", "客机(本地)", "Client(local)"),

        // ---------------- 局域网（LAN）选项卡 ----------------
        ("TabSteam", "Steam 大厅", "Steam Lobby"),
        ("TabLan", "局域网联机", "LAN Co-op"),
        ("LanCreate", "创建局域网房间", "Host LAN Room"),
        ("LanScan", "扫描局域网", "Scan LAN"),
        ("LanScanning", "扫描中...", "Scanning..."),
        ("LanNoRooms", "(未发现房间 - 可手动填主机 IP 加入)", "(No rooms found - enter the host IP manually)"),
        ("LanIpLabel", "主机 IP / 机器名", "Host IP / machine name"),
        ("IpPlaceholder", "192.168.1.100", "192.168.1.100"),
        ("LanPortLabel", "端口", "Port"),
        ("LanMyIp", "本机 IP(告诉队友)", "My IP (tell your teammates)"),
        ("LanHint", "同一局域网/同一台机器；主机需放行防火墙(或首次弹窗时允许)。两端模组版本需一致。",
                   "Same LAN or same machine; allow the host through the firewall. Both ends need the same mod build."),
        ("LanIdentity", "我的身份", "My identity"),
        ("LanName", "我的名字", "My name"),
        ("LanNamePlaceholder", "(留空 = 自动)", "(empty = auto)"),
        ("Copy", "复制", "Copy"),
        ("Paste", "粘贴", "Paste"),
        ("Copied", "已复制", "Copied"),
        ("LanJoining", "连接中...", "Connecting..."),
        ("LanMismatch", "版本不一致", "version mismatch"),

        // ---------------- 状态 / 角色 ----------------
        ("StatusIdle", "大厅(未联机)", "Lobby (idle)"),
        ("StatusHosting", "主机(等待成员)", "Host (waiting)"),
        ("StatusJoined", "已加入(客户端)", "Joined (client)"),
        ("RoleNone", "待分配", "Unassigned"),
        ("RoleCommander", "指挥官(主机)", "Commander (host)"),
        ("RoleGunner", "瞄准手", "Gunner"),
        ("RoleLoader", "装填手", "Loader"),
        ("RoleFireControl", "射击诸元", "Fire Control"),

        // ---------------- F10 交互工具（诊断覆盖层）----------------
        ("ToolHeader", "[F9 循环] 交互工具 (4/4)  [F10复制]", "[F9 cycle] Interactable tool (4/4)  [F10 copy]"),
        ("ToolName", "交互名", "Name"),
        ("ToolPath", "路径", "Path"),
        ("ToolComponents", "组件", "Components"),
        ("ToolHit", "命中", "Hit"),
        ("ToolError", "工具异常: {0}", "Tool error: {0}"),
        ("ToolNoCamera", "(无相机)", "(no camera)"),
        ("ToolNoHit", "(未命中)", "(no hit)"),
        ("ToolNoCollider", "(无碰撞体)", "(no collider)"),
        ("ToolSyncNA", "同步(点击复现): 不适用(非 LookAtTarget 按钮)", "Sync (click replay): n/a (not a LookAtTarget)"),
        ("ToolSyncOn", "同步(点击复现): 开", "Sync (click replay): ON"),
        ("ToolSyncOff", "同步(点击复现): 关", "Sync (click replay): OFF"),
        ("ToolSyncUnknown", "同步(点击复现): ?", "Sync (click replay): ?"),

        // ---------------- 错误 / 拒绝原因（面板红字 + 被踢提示，可带 {0} 占位）----------------
        ("ErrSteamNotReady", "Steam 还没就绪，稍后再试", "Steam not ready yet, please try again later"),
        ("ErrPasswordProtected", "该房间有密码，请先输入密码", "This room is password protected"),
        ("ErrInviteUnsupportedLocal", "本地回环模式不支持 Steam 邀请", "Local loopback mode does not support Steam invites"),
        ("ErrInviteUnsupportedLan", "局域网模式：直接把 IP 告诉队友（或让他在局域网列表里加入）",
                                    "LAN mode: tell your teammates the IP (or let them join from the LAN list)"),
        ("ErrLanPortInUse", "端口 {0} 被占用（可在配置文件 [LAN] Port 改一个）",
                            "Port {0} in use - change [LAN] Port in OpenNestCoop.cfg"),
        ("ErrLanHostFailed", "局域网建房失败: {0}", "LAN host failed: {0}"),
        ("ErrLanNoIp", "请先填写主机 IP", "Enter the host IP first"),
        ("ErrLanUnreachable", "连不上 {0}:{1}（检查 IP/端口/防火墙；主机是否已建房）",
                              "Cannot reach {0}:{1} (check IP/port/firewall; is the host hosting?)"),
        ("ErrLanJoinFailed", "加入失败: {0}", "LAN join failed: {0}"),
        ("ErrHostLeft", "主机已离开房间", "Host has left the room"),
        ("ErrHostDisconnected", "主机已断开连接", "Host disconnected"),
        ("ErrHeaderWidthMismatch", "前导字节宽度不一致: 主机={0} 本端={1}",
                                   "Header width mismatch: host={0} you={1}"),
        ("ErrModVersionMismatch", "模组版本不一致: 主机={0} 本端={1}",
                                  "Mod version mismatch: host={0} you={1}"),
        ("RejectBanned", "你已被主机封禁", "Banned"),
        ("RejectSchemeMismatch", "同步方案不一致（两端要用同一个 --sync 参数）",
                                 "Sync scheme mismatch (both ends must use the same --sync)"),
        ("RejectOutdated", "模组版本过旧，请更新", "Outdated mod version - please update"),
        ("RejectWrongPassword", "房间密码错误", "Wrong room password"),
        ("RejectDuplicateIdentity", "身份重复（另一个玩家用了同一个 SteamID/FakeID）",
                                   "Duplicate identity - another player already uses this SteamID/FakeID"),
        ("RejectRegistrationMismatch", "注册通道不一致（两端构建不一致）",
                                       "Registration mismatch (different builds)"),
    };

    /// <summary>查内置默认值；找不到返回 null。</summary>
    public static (string Zh, string En)? Find(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var it in Items)
            if (it.Key == key) return (it.Zh, it.En);
        return null;
    }
}
