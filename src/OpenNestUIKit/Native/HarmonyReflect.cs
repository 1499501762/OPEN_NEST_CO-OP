using System;
using System.Reflection;

namespace OpenNestUIKit.Native;

/// <summary>
/// Harmony 反射封装（**不引用 HarmonyLib**：BepInEx 的 `0Harmony` 与 MelonLoader 内嵌的
/// `HarmonyLib` 是两个程序集，双端共享代码不能直接引用，见 `docs/MOD_MENU.md` §八）。
///
/// ⚠️ 不能按精确参数类型找 `Patch` 重载：Harmony 2.x 的 `Patch` 重载数目随版本变
/// （新版多一个 ilmanipulator 参数）。这里按"名字 = Patch + 第 1 参是 MethodBase + 其余都是 HarmonyMethod"
/// 筛选，再把多余参数补 null。
/// </summary>
public static class HarmonyReflect
{
    /// <summary>给 <paramref name="target"/> 挂 prefix / postfix（都可为 null）。返回是否成功。</summary>
    public static bool Patch(MethodBase target, MethodInfo prefix, MethodInfo postfix, string label)
    {
        if (target == null) return false;
        try
        {
            var harmonyType = GameReflect.Find("HarmonyLib.Harmony");
            var harmonyMethodType = GameReflect.Find("HarmonyLib.HarmonyMethod");
            if (harmonyType == null || harmonyMethodType == null)
            {
                CoopLog.Warn("uikit.harmony", () => $"patch '{label}' skipped: HarmonyLib not found");
                return false;
            }

            object prefixHm = prefix != null ? Activator.CreateInstance(harmonyMethodType, new object[] { prefix }) : null;
            object postfixHm = postfix != null ? Activator.CreateInstance(harmonyMethodType, new object[] { postfix }) : null;
            var harmony = Activator.CreateInstance(harmonyType, new object[] { UiKitInfo.Guid });

            MethodInfo patch = null;
            var methods = harmonyType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (m.Name != "Patch") continue;
                var ps = m.GetParameters();
                if (ps.Length < 2) continue;
                if (!typeof(MethodBase).IsAssignableFrom(ps[0].ParameterType)) continue;
                bool ok = true;
                for (int j = 1; j < ps.Length; j++)
                    if (ps[j].ParameterType != harmonyMethodType) { ok = false; break; }
                if (!ok) continue;
                patch = m;
                break;
            }
            if (patch == null)
            {
                CoopLog.Warn("uikit.harmony", () => $"patch '{label}' failed: no matching Patch overload");
                return false;
            }

            var args = new object[patch.GetParameters().Length];
            args[0] = target;
            if (args.Length > 1) args[1] = prefixHm;
            if (args.Length > 2) args[2] = postfixHm;
            patch.Invoke(harmony, args);
            CoopLog.Debug("uikit.harmony", () => $"harmony patched: {label}");
            return true;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.harmony", () => $"harmony patch '{label}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>按类型名 + 方法名挂 patch（类型/方法找不到返回 false）。
    /// <paramref name="callbacksOn"/> = prefix/postfix 回调所在类型（默认找本类）。</summary>
    public static bool PatchByTypeName(string typeName, string methodName, string prefixName, string postfixName, string label,
        Type callbacksOn = null)
    {
        try
        {
            var t = GameReflect.Find(typeName);
            if (t == null) return false;
            var m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (m == null)
            {
                CoopLog.Warn("uikit.harmony", () => $"patch '{label}' skipped: {typeName}.{methodName} not found");
                return false;
            }
            var self = callbacksOn ?? typeof(HarmonyReflect);
            MethodInfo prefix = prefixName != null ? self.GetMethod(prefixName, BindingFlags.Public | BindingFlags.Static) : null;
            MethodInfo postfix = postfixName != null ? self.GetMethod(postfixName, BindingFlags.Public | BindingFlags.Static) : null;
            if (prefix == null && postfix == null)
            {
                CoopLog.Warn("uikit.harmony", () => $"patch '{label}' skipped: callback not found on {self.Name}");
                return false;
            }
            return Patch(m, prefix, postfix, label);
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.harmony", () => $"patch '{label}' failed: {ex.Message}");
            return false;
        }
    }
}
