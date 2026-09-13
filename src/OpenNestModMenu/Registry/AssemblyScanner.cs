using System;
using System.Diagnostics;
using System.Reflection;
using OpenNestModMenu.API;

namespace OpenNestModMenu.Registry;

/// <summary>
/// 被动发现（双通道注册的第二条通道）：扫描**已加载的托管程序集**，
/// 找出实现了 <see cref="IModMenuProvider"/> 但**没有主动调用 <see cref="ModMenuHost.Register"/>** 的模组，
/// 构造其实例并注册 —— 因此本模组晚于第三方加载也能发现它们（见 docs/MOD_MENU.md §五）。
///
/// 设计约束：
/// - **逐项 try-catch**：程序集/类型反射在 IL2CPP 下随时可能抛（类型加载失败、interop 版本不匹配），
///   任何失败都只记录并继续，绝不影响其它模组或本模组启动；
/// - **按程序集名快筛**：框架/加载器/interop 程序集直接跳过，避免对上百个 interop 程序集做全类型枚举；
/// - **不重复注册**：已在契约注册表里的 Id 一律跳过（主动注册优先级更高）。
/// </summary>
public static class AssemblyScanner
{
    /// <summary>程序集名前缀黑名单（框架 / 加载器 / interop / 依赖库）——这些不可能含模组提供者。</summary>
    private static readonly string[] SkipPrefixes =
    {
        "System", "mscorlib", "netstandard", "Windows", "Microsoft.", "Internal.",
        "UnityEngine", "Unity.", "Il2Cpp",
        "BepInEx", "MelonLoader", "0Harmony", "HarmonyLib",
        "Mono.", "MonoMod", "dnlib", "AsmResolver", "Iced",
        "AssetsTools", "AssetRipper", "WebSocketDotNet", "bHapticsLib", "IndexRange",
        "Semver", "Tomlet", "Newtonsoft", "LiteNetLib", "SharpGLTF",
    };

    /// <summary>扫描一次；返回本次新注册的提供者数量。</summary>
    public static int ScanOnce(string why)
    {
        var sw = Stopwatch.StartNew();
        int assemblies = 0, candidates = 0, added = 0, loadFailed = 0, typeFailed = 0;
        var self = typeof(AssemblyScanner).Assembly;
        var contract = typeof(ModMenuHost).Assembly;

        Assembly[] all;
        try { all = AppDomain.CurrentDomain.GetAssemblies(); }
        catch (Exception ex)
        {
            CoopLog.Error("registry.scan", () => $"GetAssemblies failed: {ex.Message}");
            ModMenuRegistry.RecordScan(why, 0, 0, 0, 0, 1, sw.Elapsed.TotalMilliseconds);
            return 0;
        }

        for (int i = 0; i < all.Length; i++)
        {
            var asm = all[i];
            if (asm == null || asm == self || asm == contract) continue;

            string name;
            try { name = asm.GetName()?.Name ?? ""; }
            catch { continue; }
            if (name.Length == 0 || ShouldSkip(name)) continue;

            assemblies++;

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; loadFailed++; }
            catch { loadFailed++; continue; }
            if (types == null) continue;

            for (int t = 0; t < types.Length; t++)
            {
                var type = types[t];
                if (type == null) continue;
                try
                {
                    if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition) continue;
                    if (!typeof(IModMenuProvider).IsAssignableFrom(type)) continue;
                    candidates++;

                    var ctor = type.GetConstructor(Type.EmptyTypes);
                    if (ctor == null)
                    {
                        typeFailed++;
                        CoopLog.Warn("registry.scan", () => $"{name}:{SafeName(type)} implements IModMenuProvider but has no public parameterless constructor — skipped");
                        continue;
                    }

                    var provider = (IModMenuProvider)ctor.Invoke(null);
                    if (provider == null) { typeFailed++; continue; }

                    string id = null;
                    try { id = provider.Id; } catch (Exception ex) { typeFailed++; CoopLog.Warn("registry.scan", () => $"{name}:{SafeName(type)}.Id threw: {ex.Message}"); continue; }
                    if (string.IsNullOrEmpty(id))
                    {
                        typeFailed++;
                        CoopLog.Warn("registry.scan", () => $"{name}:{SafeName(type)} returned an empty Id — skipped");
                        continue;
                    }

                    // 已注册（多半是它自己主动注册过）→ 跳过，保持主动注册优先
                    if (ModMenuHost.TryGet(id, out var existing) && existing != null) continue;

                    // 预登记来源（Register 会同步触发 Sync，建记录时就能标对来源）
                    ModMenuRegistry.MarkScanPending(id);
                    bool isNew;
                    try { isNew = ModMenuHost.Register(provider); }
                    catch (Exception ex)
                    {
                        typeFailed++;
                        CoopLog.Warn("registry.scan", () => $"{name}:{SafeName(type)} register threw: {ex.Message}");
                        continue;
                    }
                    if (!isNew)
                    {
                        // 竞态：期间被主动注册了 → 撤销预登记，不重复标记
                        ModMenuRegistry.MarkFromScan(id);
                        continue;
                    }
                    ModMenuRegistry.MarkFromScan(id);
                    added++;
                    CoopLog.Info("registry.scan", () => $"discovered '{id}' from {name} (passive)");
                }
                catch (Exception ex)
                {
                    typeFailed++;
                    CoopLog.Debug("registry.scan.fail", () => $"{name}:{SafeName(type)} -> {ex.Message}");
                }
            }
        }

        sw.Stop();
        ModMenuRegistry.RecordScan(why, assemblies, candidates, added, loadFailed, typeFailed, sw.Elapsed.TotalMilliseconds);
        CoopLog.Info("registry.scan", () => $"'{why}': assemblies={assemblies} candidates={candidates} new={added} loadFail={loadFailed} typeFail={typeFailed} in {sw.ElapsedMilliseconds} ms (providers={ModMenuRegistry.Count})");
        return added;
    }

    private static bool ShouldSkip(string assemblyName)
    {
        for (int i = 0; i < SkipPrefixes.Length; i++)
            if (assemblyName.StartsWith(SkipPrefixes[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string SafeName(Type t)
    {
        try { return t.FullName ?? t.Name ?? "?"; } catch { return "?"; }
    }
}
