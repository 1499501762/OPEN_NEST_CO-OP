using System;
using System.Collections.Generic;
using System.Reflection;

namespace OpenNestUIKit.Native;

/// <summary>
/// 游戏类型反射查找（不引用游戏程序集也能找到类型）：
/// - 优先按**全名**找（BepInEx 端游戏类型在全局命名空间，如 `LookAtTarget`）；
/// - 命中不了再按**简单名**扫描全部程序集（MLL 端类型带 `Il2Cpp` 前缀，如 `Il2Cpp.LookAtTarget`，
///   见 `docs/MOD_MENU.md` §八 的实测根因）；
/// - 结果缓存 + 未命中打诊断日志（同一类型只报一次）。
///
/// ⚠️ 只做"找类型"，不做 patching（Harmony 相关留给需要的模组自己处理）。
/// </summary>
public static class GameReflect
{
    private static readonly Dictionary<string, Type> _cache = new();
    private static readonly HashSet<string> _missLogged = new();

    public static Type Find(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (_cache)
        {
            if (_cache.TryGetValue(name, out var t)) return t;
        }
        Type found = null;
        try
        {
            // 1) 全名（含命名空间）
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(name, false);
                    if (t != null) { found = t; break; }
                }
                catch { }
            }
            // 2) 简单名扫描（MLL 端带 Il2Cpp 前缀 / 类型在别的命名空间）
            if (found == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types = null;
                    try { types = asm.GetTypes(); } catch { continue; }
                    if (types == null) continue;
                    for (int i = 0; i < types.Length; i++)
                    {
                        var t = types[i];
                        if (t == null) continue;
                        if (t.Name == name || t.Name == "Il2Cpp" + name) { found = t; break; }
                    }
                    if (found != null) break;
                }
            }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.reflect", () => $"Find('{name}') failed: {ex.Message}");
        }
        if (found != null)
        {
            lock (_cache) _cache[name] = found;
            CoopLog.Debug("uikit.reflect", () => $"type '{name}' -> {found.FullName}");
        }
        else
        {
            lock (_missLogged)
            {
                if (_missLogged.Add(name))
                    CoopLog.Warn("uikit.reflect", () => $"type NOT found: '{name}'（该功能会静默降级）");
            }
        }
        return found;
    }

    /// <summary>取类型上的实例方法（含非 public；找不到返回 null）。</summary>
    public static MethodInfo FindMethod(Type type, string name)
    {
        if (type == null || string.IsNullOrEmpty(name)) return null;
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var m = type.GetMethod(name, flags);
            return m;
        }
        catch { return null; }
    }
}
