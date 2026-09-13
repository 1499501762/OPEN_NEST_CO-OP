using System;
using System.Runtime.InteropServices;

namespace OpenNestCoop.Core;

/// <summary>
/// 系统剪贴板读写（2026-09-12 新增，见 `docs/LOCALIZATION.md` 同级的 UI 说明 / `docs/LAN.md` 局域网面板“一键复制”）。
///
/// ⚠️ 为什么不用纯 `GUIUtility.systemCopyBuffer`：IL2CPP 裁剪构建下该属性的 setter/getter 可能被裁剪
/// 或静默无效（用户反馈“点击复制没生效”），故**主路径改用 Win32 原生剪贴板**
/// （与项目里 IME 用的 `ImmGetCompositionStringW` 同一路子，已验证可用），Unity 属性仅作兜底。
/// 两条路都失败才返回 false（调用方负责提示）。
/// </summary>
public static class Clipboard
{
    /// <summary>上次成功的通道（"win32" / "unity" / "" 失败）——诊断用（日志会打印）。</summary>
    public static string LastPath = "";

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>写入剪贴板（纯文本）。成功返回 true；LastPath 记录走的哪条通道。</summary>
    public static bool SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (SetWin32(text)) { LastPath = "win32"; return true; }
        if (SetUnity(text)) { LastPath = "unity"; return true; }
        LastPath = "";
        return false;
    }

    /// <summary>读剪贴板文本（无内容/失败返回空串）。</summary>
    public static string GetText()
    {
        var s = GetWin32();
        if (!string.IsNullOrEmpty(s)) { LastPath = "win32"; return s; }
        try
        {
            var u = UnityEngine.GUIUtility.systemCopyBuffer;
            if (!string.IsNullOrEmpty(u)) { LastPath = "unity"; return u; }
        }
        catch { }
        LastPath = "";
        return "";
    }

    // ---------------- Win32 实现 ----------------

    private static bool SetWin32(string text)
    {
        // 剪贴板可能被别的程序占用：重试几次（每次 ~10ms，最多 6 次）
        for (int attempt = 0; attempt < 6; attempt++)
        {
            IntPtr hMem = IntPtr.Zero;
            bool opened = false;
            try
            {
                if (!OpenClipboard(IntPtr.Zero)) continue;
                opened = true;
                var bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
                hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                if (hMem == IntPtr.Zero) return false;
                var ptr = GlobalLock(hMem);
                if (ptr == IntPtr.Zero) return false;
                try { Marshal.Copy(bytes, 0, ptr, bytes.Length); }
                finally { GlobalUnlock(hMem); }
                if (!EmptyClipboard()) return false;
                if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero) return false;
                hMem = IntPtr.Zero;   // 所有权已交给系统（不能自己释放）
                return true;
            }
            catch { return false; }
            finally
            {
                if (hMem != IntPtr.Zero) { try { GlobalFree(hMem); } catch { } }
                if (opened) { try { CloseClipboard(); } catch { } }
            }
        }
        return false;
    }

    private static string GetWin32()
    {
        try
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return "";
            if (!OpenClipboard(IntPtr.Zero)) return "";
            try
            {
                var handle = GetClipboardData(CF_UNICODETEXT);
                if (handle == IntPtr.Zero) return "";
                var ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero) return "";
                try { return Marshal.PtrToStringUni(ptr) ?? ""; }
                finally { GlobalUnlock(handle); }
            }
            finally { CloseClipboard(); }
        }
        catch { return ""; }
    }

    private static bool SetUnity(string text)
    {
        try
        {
            UnityEngine.GUIUtility.systemCopyBuffer = text;
            return true;
        }
        catch { return false; }
    }
}
