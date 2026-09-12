using System;

namespace OpenNestCoop.Core;

/// <summary>
/// **本端联机身份**（2026-09-12 新增，见 `docs/LAN.md`）。
///
/// 规则（用户要求）：
///   1. **认证/识别优先用 SteamID** —— Steam 就绪且拿到有效 SteamID64 时，身份/名单/踢人封禁全用它；
///   2. **SteamID 拿不到（Steam 未运行/未登录、纯离线）→ 用配置里的 FakeID**
///      （`[Identity] FakeId`，首次自动生成并写回配置文件，跨重启稳定 —— 见 <see cref="CoopConfig.FakeId"/>）。
///
/// FakeID 标记位：高 16 位固定 `0xFACE`（真实 SteamID64 约 7.65e16 &lt; 0x0110_0000_0000_0000，
/// 两者不可能撞号）→ 日志/名单里一眼认出是不是 Fake。
/// </summary>
public static class Identity
{
    private const ulong FakeMask = 0xFFFF000000000000UL;
    private const ulong FakeTag = 0xFACE000000000000UL;

    /// <summary>本端身份（ulong）。SteamID 优先；否则 FakeID（读配置，首次会生成并落盘）。</summary>
    public static ulong Local
    {
        get
        {
            ulong steam = RealSteamId();
            if (steam != 0) return steam;
            try { return CoopConfig.FakeId; } catch { return 0xFACE000000000001UL; }
        }
    }

    /// <summary>真实 SteamID（不可用返回 0）。用 try/catch 包住：MelonLoader / 纯离线环境都能安全调用。</summary>
    public static ulong RealSteamId()
    {
        try
        {
#if MELONLOADER
            if (!Il2CppSteamworks.SteamAPI.IsSteamRunning()) return 0;
            var id = (ulong)Il2CppSteamworks.SteamUser.GetSteamID();
            return id == 0 ? 0 : id;
#else
            if (!Steamworks.SteamAPI.IsSteamRunning()) return 0;
            var id = (ulong)Steamworks.SteamUser.GetSteamID();
            return id == 0 ? 0 : id;
#endif
        }
        catch { return 0; }
    }

    /// <summary>Steam 昵称（不可用返回空串）。</summary>
    public static string SteamName()
    {
        try
        {
#if MELONLOADER
            var n = Il2CppSteamworks.SteamFriends.GetPersonaName();
#else
            var n = Steamworks.SteamFriends.GetPersonaName();
#endif
            return n ?? "";
        }
        catch { return ""; }
    }

    /// <summary>给某个身份判断是不是 FakeID（诊断/UI 标记用）。</summary>
    public static bool IsFake(ulong id) => (id & FakeMask) == FakeTag;

    /// <summary>身份的可读短标签（日志/名单）：`Steam#1234` 或 `Fake#ABCD`。</summary>
    public static string Tag(ulong id)
    {
        if (id == 0) return "?";
        return IsFake(id) ? $"Fake#{id & 0xFFFF:X4}" : $"Steam#{id % 10000:0000}";
    }

    /// <summary>本端显示名（名单/聊天用）：配置 `[Identity] Name` 优先 → Steam 昵称 → `PlayerABCD`（FakeID 后 4 位）。</summary>
    public static string LocalName()
    {
        try
        {
            var cfg = CoopConfig.LocalName;
            if (!string.IsNullOrWhiteSpace(cfg)) return cfg.Trim();
        }
        catch { }
        var steam = SteamName();
        if (!string.IsNullOrWhiteSpace(steam)) return steam;
        ulong id = Local;
        return "Player" + (id & 0xFFFF).ToString("X4");
    }
}
