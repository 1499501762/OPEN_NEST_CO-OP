using System;
using System.Reflection;
using UnityEngine;
using OpenNestModMenu.Loaders;

namespace OpenNestModMenu.UI;

/// <summary>
/// 指针位置来源。**抄联机模组的取法**：官方联机 `MultiplayerMenu.PointerPosition()` 是
/// 「先读游戏虚拟光标 `VirtualCursor.ScreenPosition`，拿不到再退回原始鼠标 `Mouse.current.position`」，
/// `OpenNestCoop.UI.IronNestNativeUi` 也把 `VirtualCursor.ScreenPosition` 作为权威指针位置（见 docs/NATIVE_UI.md §四）。
///
/// 为什么优先用虚拟光标：玩家**看到的**是游戏画出来的那个指针，它与原始鼠标位置不一定相同
/// （游戏可能做灵敏度/平滑/手柄驱动）。命中判定用「看得见的那一个」才不会出现「看着在按钮上却点不到」。
///
/// 共享代码不能直接写 `VirtualCursor`（双端引用不同程序集）→ 全反射；反射失败时退回鼠标位置，绝不抛。
/// </summary>
internal static class PointerPosition
{
    private static Type _cursorType;
    private static object _cursor;
    private static float _nextProbe;
    private static bool _reflectionFailed;

    /// <summary>取当前指针屏幕坐标（虚拟光标优先，退回原始鼠标）。取不到返回 false。</summary>
    internal static bool TryGet(out Vector2 pos)
    {
        pos = default;

        if (!_reflectionFailed && TryVirtualCursor(out pos)) { SetSource("VirtualCursor.ScreenPosition"); return true; }

        try
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null)
            {
                pos = mouse.position.ReadValue();
                SetSource("Mouse.current");
                return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>当前实际使用的指针来源（T10 诊断面板显示；字符串只在变化时替换，无稳态分配）。</summary>
    internal static string LastSource { get; private set; } = "";

    private static string _sourceCurrent = "";

    private static void SetSource(string s)
    {
        if (string.Equals(_sourceCurrent, s, StringComparison.Ordinal)) return;
        _sourceCurrent = s;
        LastSource = s;
    }

    private static bool TryVirtualCursor(out Vector2 pos)
    {
        pos = default;
        try
        {
            // 场景切换会销毁光标实例 → 缓存对象失效时按 2s 节流重新找，避免每帧 FindObjectOfType
            if (_cursor == null)
            {
                float now = Time.unscaledTime;
                if (now < _nextProbe) return false;
                _nextProbe = now + 2f;

                if (_cursorType == null) _cursorType = LoaderReflect.FindType("VirtualCursor");
                if (_cursorType == null) { _reflectionFailed = true; LogOnce("Mouse.current（找不到 VirtualCursor 类型）"); return false; }
                _cursor = FindInstance(_cursorType);
                if (_cursor == null) return false;
            }

            var o = _cursor;
            if (o == null) return false;

            if (TryReadPosition(o, out pos)) { LogOnce("VirtualCursor.ScreenPosition"); return true; }

            _reflectionFailed = true;    // 属性读不到（裁剪/改名）→ 以后不再试，省开销
            LogOnce("Mouse.current（VirtualCursor 位置读不到）");
            return false;
        }
        catch
        {
            _cursor = null;
            return false;
        }
    }

    /// <summary>
    /// 读虚拟光标位置。⚠️ Il2CppInterop 对**方法**生成的成员是 <c>get_Xxx()</c>（不是 C# 属性），
    /// 对**字段**才生成同名属性 → 两种都要试（本机实测：只试属性会读不到，退回鼠标位置）。
    /// </summary>
    private static bool TryReadPosition(object o, out Vector2 pos)
    {
        pos = default;
        try
        {
            var t = o.GetType();
            string[] getters = { "get_ScreenPosition", "get__position" };
            for (int i = 0; i < getters.Length; i++)
            {
                var m = t.GetMethod(getters[i], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m == null) continue;
                var r = m.Invoke(o, null);
                if (r is Vector2 v) { pos = v; return true; }
            }
            string[] props = { "ScreenPosition", "_position" };
            for (int i = 0; i < props.Length; i++)
            {
                var p = t.GetProperty(props[i], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p == null) continue;
                var r = p.GetValue(o);
                if (r is Vector2 v) { pos = v; return true; }
            }
        }
        catch { }
        return false;
    }

    private static bool _sourceLogged;

    /// <summary>一次性记录指针来源（实测取证：到底用的是游戏虚拟光标还是原始鼠标）。</summary>
    private static void LogOnce(string source)
    {
        if (_sourceLogged) return;
        _sourceLogged = true;
        var s = source;
        CoopLog.Info("modmenu.ui", () => "pointer source: " + s);
    }

    /// <summary>
    /// 找场景里的实例。⚠️ IL2CPP 下 `Object.FindObjectOfType` 的重载参数是 <c>Il2CppSystem.Type</c>
    /// （不是 <c>System.Type</c>，实测编译不过）→ 这里按签名挑重载：参数是 <c>System.Type</c> 的直接传，
    /// 是 <c>Il2CppSystem.Type</c> 的先经 <c>Il2CppInterop.Runtime.Il2CppType.From</c> 转换（反射拿，双端都有）。
    /// </summary>
    private static object FindInstance(Type t)
    {
        try
        {
            var methods = typeof(UnityEngine.Object).GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (m.Name != "FindObjectOfType" || m.IsGenericMethod) continue;
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;

                object arg;
                var pt = ps[0].ParameterType;
                if (pt == typeof(Type)) arg = t;
                else if (pt.Name == "Type") { arg = Il2CppTypeFrom(t); if (arg == null) continue; }
                else continue;

                var r = m.Invoke(null, new object[] { arg });
                if (r != null) return r;
            }
        }
        catch { }
        return null;
    }

    private static object Il2CppTypeFrom(Type t)
    {
        try
        {
            var it = LoaderReflect.FindType("Il2CppInterop.Runtime.Il2CppType");
            if (it == null) return null;
            var ms = it.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < ms.Length; i++)
            {
                var m = ms[i];
                if (m.Name != "From") continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(Type)) return m.Invoke(null, new object[] { t });
            }
        }
        catch { }
        return null;
    }
}
