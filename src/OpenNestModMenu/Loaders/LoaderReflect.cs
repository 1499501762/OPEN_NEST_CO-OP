using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace OpenNestModMenu.Loaders;

/// <summary>
/// 反射读取两个加载器状态的公共工具。
///
/// ⚠️ **全部走反射**：本模组的 BepInEx 版与 MelonLoader 版共用同一份源码，
/// 而两者引用的加载器程序集不同（BepInEx 版没有 MelonLoader.dll，MelonLoader 版没有 BepInEx.Core.dll），
/// 因此**共享代码里绝不能直接写 `BepInEx.X` / `MelonLoader.X` 类型** —— 那会导致另一端编译失败。
/// 反射还顺带满足了"对桥零编译期依赖"这条硬要求（§3.2）。
/// </summary>
internal static class LoaderReflect
{
    /// <summary>
    /// 在已加载程序集里按全名找类型（找不到返回 null；绝不抛）。
    ///
    /// ⚠️ 全名找不到时会**按简单名再扫一遍**：实测踩过 —— MLL 端 `FindType("LookAtTarget")` 直接返回 null，
    /// 导致"世界点击拦截"整层静默失效（G 端却能找到）。游戏类型可能被放在命名空间里、或两端 interop
    /// 生成的类型名不完全一致，因此按简单名兜底匹配（只匹配类型名，不做模糊/子串匹配）。
    /// 结果缓存；未命中缓存到"程序集数量变化"为止（避免 PointerPosition 这类周期探测反复全量扫描）。
    /// </summary>
    internal static Type FindType(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return null;

        Assembly[] all;
        try { all = AppDomain.CurrentDomain.GetAssemblies(); }
        catch { return null; }

        if (_typeCache.TryGetValue(fullName, out var cached)) return cached;
        if (_typeMisses.Contains(fullName) && _missAssemblyCount == all.Length) return null;

        Type found = null;
        for (int i = 0; i < all.Length && found == null; i++)
        {
            try { found = all[i]?.GetType(fullName, false); }
            catch { }
        }

        if (found == null)
        {
            string simple = fullName;
            int dot = simple.LastIndexOf('.');
            if (dot >= 0) simple = simple.Substring(dot + 1);

            for (int i = 0; i < all.Length && found == null; i++)
            {
                Type[] types = null;
                try { types = all[i]?.GetTypes(); } catch { }   // 裁剪/依赖缺失的程序集会抛 → 跳过它
                if (types == null) continue;
                for (int j = 0; j < types.Length; j++)
                {
                    var t = types[j];
                    try
                    {
                        if (t != null && string.Equals(t.Name, simple, StringComparison.Ordinal)) { found = t; break; }
                    }
                    catch { }
                }
            }

            if (found != null)
            {
                var f = found;
                CoopLog.Info("modmenu.loader", () => $"type by simple name: '{fullName}' -> {f.FullName} ({LoaderReflect.AssemblyLocation(f.Assembly)})");
            }
        }

        if (found != null) { _typeCache[fullName] = found; return found; }

        _typeMisses.Add(fullName);
        _missAssemblyCount = all.Length;
        LogMissOnce(fullName, all);
        return null;
    }

    private static readonly Dictionary<string, Type> _typeCache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _typeMisses = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _missLogged = new(StringComparer.Ordinal);
    private static int _missAssemblyCount = -1;

    /// <summary>未命中诊断（每个名字一次）：列出已加载程序集，便于定位"游戏类型到底在不在 AppDomain 里"。</summary>
    private static void LogMissOnce(string fullName, Assembly[] all)
    {
        if (!_missLogged.Add(fullName)) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("type NOT found: '").Append(fullName).Append("' | assemblies=").Append(all?.Length ?? 0);
            int shown = 0;
            for (int i = 0; i < (all?.Length ?? 0) && shown < 40; i++)
            {
                string n = "";
                try { n = all[i]?.GetName()?.Name ?? ""; } catch { }
                if (n.Length == 0) continue;
                sb.Append(shown == 0 ? " [" : ", ").Append(n);
                shown++;
            }
            if (shown > 0) sb.Append(']');
            CoopLog.Warn("modmenu.loader", () => sb.ToString());
        }
        catch { }
    }

    /// <summary>读静态属性（失败返回 null）。</summary>
    internal static object StaticGet(Type type, string property)
    {
        if (type == null) return null;
        try
        {
            var p = type.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            return p?.GetValue(null);
        }
        catch { return null; }
    }

    /// <summary>读实例属性 / 字段（失败返回 null）。</summary>
    internal static object GetMember(object obj, string name)
    {
        if (obj == null || string.IsNullOrEmpty(name)) return null;
        try
        {
            var t = obj.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null) return p.GetValue(obj);
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f?.GetValue(obj);
        }
        catch { return null; }
    }

    internal static string GetString(object obj, string name)
    {
        try { return GetMember(obj, name)?.ToString() ?? ""; } catch { return ""; }
    }

    internal static int GetInt(object obj, string name, int fallback = 0)
    {
        try
        {
            var v = GetMember(obj, name);
            return v == null ? fallback : Convert.ToInt32(v);
        }
        catch { return fallback; }
    }

    /// <summary>程序集版本（读不到返回空串）。</summary>
    internal static string AssemblyVersion(Assembly asm)
    {
        try { var v = asm?.GetName()?.Version; return v == null ? "" : v.ToString(); }
        catch { return ""; }
    }

    /// <summary>位置（Assembly.Location 在 IL2CPP 下可能为空）。</summary>
    internal static string AssemblyLocation(Assembly asm)
    {
        try { return asm?.Location ?? ""; } catch { return ""; }
    }

    /// <summary>程序集名前缀黑名单（框架 / 加载器 / interop / 依赖库）——这些不可能含模组。</summary>
    internal static bool IsLoaderOrFrameworkAssembly(string name)
    {
        // ⚠️ 例外：桥 `BepInEx.MelonLoader.Loader*` **本身就是一个插件**（它把 MelonLoader 寄宿进 BepInEx，
        //    见 docs/MOD_MENU.md §3.2）——不能因为前缀是 BepInEx 就被跳过，否则既看不到它、也读不到桥版本。
        if (name.StartsWith("BepInEx.MelonLoader.Loader", StringComparison.OrdinalIgnoreCase)) return false;

        for (int i = 0; i < SkipPrefixes.Length; i++)
            if (name.StartsWith(SkipPrefixes[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly string[] SkipPrefixes =
    {
        "System", "mscorlib", "netstandard", "Windows", "Microsoft.", "Internal.",
        "UnityEngine", "Unity.", "Il2Cpp",
        "BepInEx", "MelonLoader", "0Harmony", "HarmonyLib",
        "Mono.", "MonoMod", "dnlib", "AsmResolver", "Iced",
        "AssetsTools", "AssetRipper", "WebSocketDotNet", "bHapticsLib", "IndexRange",
        "Semver", "Tomlet", "Newtonsoft", "LiteNetLib", "SharpGLTF",
    };

    // ---------------- BepInEx ----------------

    internal sealed class PluginLite
    {
        public string Guid = "";
        public string Name = "";
        public string Version = "";
        public string Location = "";
        public string TypeName = "";
        /// <summary>声明的依赖（BepInEx `BepInDependency` 的 GUID；MLL 侧见 <see cref="MelonLite.DependsOn"/>）。</summary>
        public string[] DependsOn = Array.Empty<string>();
    }

    /// <summary>
    /// 读取 BepInEx 已加载插件。两条通道：
    ///   1. **首选** `Chainloader.PluginInfos`（权威；BepInEx 6 IL2CPP 里 Chainloader 类型名不唯一，逐个候选试）；
    ///   2. **兜底** 扫描已加载程序集里继承 `BasePlugin`/`BaseUnityPlugin` 的类型，再读其 `[BepInPlugin]` 特性。
    ///      （不依赖 Chainloader 的具体类型名，宿主是桥时同样可用。）
    /// 宿主不是 BepInEx 时返回空列表；全程逐项 try-catch。
    /// </summary>
    internal static List<PluginLite> ReadBepInExPlugins()
    {
        var list = ReadBepInExPluginsFromChainloader();
        _chainDiagLogged = true;   // 诊断只打一次（无论后面是否走兜底）
        if (list.Count > 0) return list;

        var fallback = ReadBepInExPluginsByTypeScan();
        if (fallback.Count > 0)
        {
            int n = fallback.Count;
            CoopLog.Debug("loader.diag", () => $"PluginInfos unavailable → type-scan fallback found {n} plugin(s)");
            return fallback;
        }
        return list;
    }

    private static readonly string[] ChainloaderTypeNames =
    {
        "BepInEx.Unity.IL2CPP.IL2CPPChainloader",
        "BepInEx.Bootstrap.Chainloader",
    };

    private static List<PluginLite> ReadBepInExPluginsFromChainloader()
    {
        var list = new List<PluginLite>();
        for (int c = 0; c < ChainloaderTypeNames.Length; c++)
        {
            var chain = FindType(ChainloaderTypeNames[c]);
            if (_chainDiagLogged)
                CoopLog.Debug("loader.diag", () => $"chainloader candidate '{ChainloaderTypeNames[c]}' -> {(chain == null ? "not found" : chain.FullName)}");
            if (chain == null) continue;

            object infos = StaticGet(chain, "PluginInfos");
            if (_chainDiagLogged)
                CoopLog.Debug("loader.diag", () => $"  PluginInfos value={(infos == null ? "null" : infos.GetType().FullName)}");
            if (infos is not IDictionary dict) continue;

            try
            {
                foreach (DictionaryEntry e in dict)
                {
                    try
                    {
                        var lite = FromPluginInfo(e.Value);
                        if (lite != null) list.Add(lite);
                    }
                    catch { }
                }
            }
            catch { }

            if (list.Count > 0) break;
        }
        return list;
    }

    /// <summary>链式反射诊断每进程只打一次（避免每次清单刷新重复刷日志）。</summary>
    private static bool _chainDiagLogged;

    private static PluginLite FromPluginInfo(object info)
    {
        if (info == null) return null;
        var meta = GetMember(info, "Metadata");
        var lite = new PluginLite
        {
            Guid = GetString(meta, "GUID"),
            Name = GetString(meta, "Name"),
            Version = GetString(meta, "Version"),
            Location = GetString(info, "Location"),
        };
        var inst = GetMember(info, "Instance");
        if (inst != null)
        {
            try { lite.TypeName = inst.GetType().Assembly.GetName().Name ?? ""; } catch { }
            if (lite.Location.Length == 0) lite.Location = AssemblyLocation(inst.GetType().Assembly);
        }
        if (lite.Name.Length == 0) lite.Name = lite.Guid.Length > 0 ? lite.Guid : lite.TypeName;

        // ⚠ 2026-09-13（用户：“依赖需要加载顺序吗？有跟随加载顺序吗？”）：
        //   MLL 那边一直读 `MelonAdditionalDependencies`，而 **BepInEx 这边的依赖从来没读** ⇒
        //   所有 BepInEx 插件的 `DependsOn` 都是空 ⇒ 调度器里的“依赖前移”形同虚设（实机 fixups=0）。
        //   BepInEx 的依赖就在 `PluginInfo.Dependencies`（BepInDependency[]，每项有 `DependencyGUID`）。
        lite.DependsOn = ReadBepInExDependencies(info);
        return lite.Name.Length > 0 ? lite : null;
    }

    /// <summary>读 `PluginInfo.Dependencies`（BepInDependency[]）里的 `DependencyGUID`。</summary>
    private static string[] ReadBepInExDependencies(object pluginInfo)
    {
        try
        {
            var deps = GetMember(pluginInfo, "Dependencies");
            if (deps == null) return Array.Empty<string>();
            var list = new List<string>();
            if (deps is System.Collections.IEnumerable en)
            {
                foreach (var d in en)
                {
                    string g = GetString(d, "DependencyGUID");
                    if (g.Length == 0) g = GetString(d, "GUID");
                    if (g.Length > 0 && !list.Contains(g)) list.Add(g);
                }
            }
            return list.Count > 0 ? list.ToArray() : Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>兜底：扫已加载程序集里的 BasePlugin 子类（不依赖 Chainloader 类型名）。</summary>
    private static List<PluginLite> ReadBepInExPluginsByTypeScan()
    {
        var list = new List<PluginLite>();
        Assembly[] all;
        try { all = AppDomain.CurrentDomain.GetAssemblies(); }
        catch { return list; }

        for (int i = 0; i < all.Length; i++)
        {
            var asm = all[i];
            string name;
            try { name = asm?.GetName()?.Name ?? ""; }
            catch { continue; }
            if (name.Length == 0 || IsLoaderOrFrameworkAssembly(name)) continue;

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch { continue; }
            if (types == null) continue;

            for (int k = 0; k < types.Length; k++)
            {
                var t = types[k];
                if (t == null || t.IsAbstract) continue;
                if (!IsBepInExPluginType(t)) continue;

                var lite = new PluginLite { TypeName = name, Name = name, Location = AssemblyLocation(asm) };
                ReadBepInPluginAttribute(t, lite);
                if (lite.Name.Length > 0 && !list.Exists(x => string.Equals(x.Name, lite.Name, StringComparison.Ordinal)))
                    list.Add(lite);
            }
        }
        return list;
    }

    /// <summary>基类链里是否出现 BasePlugin / BaseUnityPlugin（按类型**名**匹配，不引用 BepInEx 程序集）。</summary>
    private static bool IsBepInExPluginType(Type t)
    {
        try
        {
            for (var b = t.BaseType; b != null; b = b.BaseType)
            {
                string n = b.Name;
                if (n == "BasePlugin" || n == "BaseUnityPlugin") return true;
            }
        }
        catch { }
        return false;
    }

    private static void ReadBepInPluginAttribute(Type t, PluginLite lite)
    {
        try
        {
            var deps = new List<string>();
            foreach (var cad in t.GetCustomAttributesData())
            {
                string an = cad.Constructor?.DeclaringType?.Name ?? "";
                if (string.Equals(an, "BepInPlugin", StringComparison.Ordinal))
                {
                    var a = cad.ConstructorArguments;
                    if (a.Count >= 1 && a[0].Value is string g) lite.Guid = g;
                    if (a.Count >= 2 && a[1].Value is string n) lite.Name = n;
                    if (a.Count >= 3 && a[2].Value is string v) lite.Version = v;
                }
                else if (string.Equals(an, "BepInDependency", StringComparison.Ordinal))
                {
                    // 兜底通道（拿不到 Chainloader.PluginInfos 时）：依赖也写在类型上
                    var a = cad.ConstructorArguments;
                    if (a.Count >= 1 && a[0].Value is string g2 && g2.Length > 0 && !deps.Contains(g2)) deps.Add(g2);
                }
            }
            if (deps.Count > 0) lite.DependsOn = deps.ToArray();
        }
        catch { }
    }

    // ---------------- MelonLoader ----------------

    internal sealed class MelonLite
    {
        public string Name = "";
        public string Version = "";
        public string Author = "";
        public string AssemblyName = "";
        public string Location = "";
        public int Priority;
        public string[] DependsOn = Array.Empty<string>();
        public string[] IncompatibleWith = Array.Empty<string>();
        public bool IsPlugin;   // MelonPlugin（不是 MelonMod）
    }

    /// <summary>
    /// 读取 MelonLoader 已注册模组（`MelonBase.RegisteredMelons`，含 Mod 与 Plugin）。
    /// 宿主没有 MelonLoader 运行时（或尚未 Start）时返回空列表。
    /// </summary>
    internal static List<MelonLite> ReadMelons()
    {
        var list = new List<MelonLite>();
        Type baseType = FindType("MelonLoader.MelonBase");
        if (baseType == null) return list;

        object registered = StaticGet(baseType, "RegisteredMelons");
        if (registered is not IEnumerable seq) return list;

        try
        {
            foreach (var m in seq)
            {
                try
                {
                    if (m == null) continue;
                    var info = GetMember(m, "Info");
                    var asmObj = GetMember(m, "MelonAssembly");
                    var asm = GetMember(asmObj, "Assembly") as Assembly;

                    var lite = new MelonLite
                    {
                        Name = GetString(info, "Name"),
                        Version = GetString(info, "Version"),
                        Author = GetString(info, "Author"),
                        AssemblyName = asm?.GetName()?.Name ?? "",
                        Location = AssemblyLocation(asm),
                        Priority = GetInt(m, "Priority"),
                    };
                    if (lite.Name.Length == 0) lite.Name = lite.AssemblyName;

                    string tn = m.GetType().BaseType?.FullName ?? "";
                    lite.IsPlugin = tn.Contains("MelonPlugin");

                    if (asm != null)
                    {
                        lite.DependsOn = ReadAssemblyNames(asm, "MelonAdditionalDependenciesAttribute");
                        lite.IncompatibleWith = ReadAssemblyNames(asm, "MelonIncompatibleAssembliesAttribute");
                    }
                    if (lite.Name.Length > 0) list.Add(lite);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    // ---------------- ML 模组运行时切换（热停用 / 热启用） ----------------

    /// <summary>ML 模组**实例**（`MelonBase`，托管对象）：保住引用才能调 `Unregister`。</summary>
    internal sealed class MelonInstance
    {
        public object Instance;
        public object Assembly;        // MelonAssembly 对象
        public string Name = "";
        public string AssemblyName = "";
        public string Location = "";
    }

    /// <summary>读取 ML 模组**实例**（与 <see cref="ReadMelons"/> 同源，但保留对象引用）。</summary>
    internal static List<MelonInstance> ReadMelonInstances()
    {
        var list = new List<MelonInstance>();
        Type baseType = FindType("MelonLoader.MelonBase");
        if (baseType == null) return list;

        object registered = StaticGet(baseType, "RegisteredMelons");
        if (registered is not IEnumerable seq) return list;

        try
        {
            foreach (var m in seq)
            {
                try
                {
                    if (m == null) continue;
                    var info = GetMember(m, "Info");
                    var asmObj = GetMember(m, "MelonAssembly");
                    var asm = GetMember(asmObj, "Assembly") as Assembly;
                    list.Add(new MelonInstance
                    {
                        Instance = m,
                        Assembly = asmObj,
                        Name = GetString(info, "Name"),
                        AssemblyName = asm?.GetName()?.Name ?? "",
                        Location = AssemblyLocation(asm),
                    });
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 运行中停用一个 ML 模组。
    ///
    /// 依据 MLL 0.7 源码（`MelonLoader/Melons/MelonBase.cs`）：
    /// <c>Unregister()</c> 的官方说明是 “unsubscribes the Melons from all Callbacks/MelonEvents and unpatches all Methods
    /// that were patched by Harmony, but doesn't actually unload the whole Assembly”，
    /// 实现里依次做 `OnDeinitializeMelon()` → `UnregisterInternal()` → 移出 `_registeredMelons` → `HarmonyInstance.UnpatchSelf()`。
    /// → 模组代码还在内存，但**不再跑**（patch 已撤、回调已退订）。
    /// </summary>
    internal static bool TryMelonUnregister(object melonInstance, string reason)
    {
        if (melonInstance == null) return false;
        try
        {
            var mi = melonInstance.GetType().GetMethod("Unregister",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(bool) }, null);
            if (mi == null) return false;
            mi.Invoke(melonInstance, new object[] { reason ?? "disabled via OpenNestModMenu", false });
            return true;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.runtime", () => "melon unregister failed: " + ex.Message);
            return false;
        }
    }

    // ---------------- BepInEx 插件：热加载（运行中启用，不需要重启） ----------------

    /// <summary>
    /// 链加载器的候选类型名（用于找**实例**）。放 IL2CPP 运行时 + Mono 运行时 + BepInEx 5 风格三个候选。
    /// </summary>
    private static readonly string[] HotLoadChainloaderTypes =
    {
        "BepInEx.Unity.IL2CPP.IL2CPPChainloader",
        "BepInEx.Unity.Mono.Bootstrap.UnityChainloader",
        "BepInEx.Bootstrap.Chainloader",
    };

    /// <summary>当前进程里的 BepInEx 链加载器实例（热加载的前提）；取不到返回 null。</summary>
    internal static object FindChainloaderInstance()
    {
        for (int i = 0; i < HotLoadChainloaderTypes.Length; i++)
        {
            try
            {
                var t = FindType(HotLoadChainloaderTypes[i]);
                if (t == null) continue;
                var inst = StaticGet(t, "Instance");
                if (inst != null) return inst;
            }
            catch { }
        }
        return null;
    }

    /// <summary>宿主能不能热加载 BepInEx 插件（UI 用它决定按钮文案）。</summary>
    internal static bool CanHotLoadBepInEx()
    {
        try { return FindChainloaderInstance() != null; }
        catch { return false; }
    }

    /// <summary>宿主能不能对 BepInEx 插件做"软停用"（需要链加载器 + Harmony 都在）。</summary>
    internal static bool CanSoftStopBepInEx()
    {
        try { return FindChainloaderInstance() != null && FindType("HarmonyLib.Harmony") != null; }
        catch { return false; }
    }

    /// <summary>
    /// **软停用**一个已加载的 BepInEx 插件（实验性，最佳努力；**不是真卸载**）。
    ///
    /// 做三件事（每步都是"尽力 + 计数 + 记日志"，绝不因某步失败就整体失败）：
    /// 1. 若插件重写了 `BasePlugin.Unload()`（`DeclaringType != BasePlugin`）→ 调它，让插件自己先收拾
    ///    （BepInEx 默认实现 `=> false`，意味着"我没法自己卸"）；
    /// 2. **撤掉它打的所有 Harmony patch**：`Harmony.GetAllPatchedMethods()` → `GetPatchInfo(method)` →
    ///    对 `prefixes/postfixes/transpilers/finalizers/ilmanipulators` 里 `Patch.PatchMethod.DeclaringType.Assembly`
    ///    == 该插件程序集 的补丁，逐个 `Harmony.Unpatch(method, patchMethod)`（按补丁方法精确撤，不用 `UnpatchAll(owner)`
    ///    以免误伤同名 owner）。HarmonyX 2.10.2 已实测这些 API 都在（`GetAllPatchedMethods/GetPatchInfo/Unpatch`）。
    /// 3. **销毁它加进场景的组件**：遍历全部已加载场景的根对象 → 组件类型程序集 == 该插件程序集 的 `MonoBehaviour`
    ///    → `Destroy`（连带停掉它的 `Update`/协程）。
    ///
    /// ⚠️ **做不到的事（必须诚实告知）**：程序集不能卸载、静态字段/单例/静态构造留下的状态还在、
    /// `System.Threading.Timer`/后台线程还在跑、插件自己往游戏侧注册的回调/订阅还在（只是它的 patch 没了）。
    /// 所以 UI 把它标为"实验性"，并且**仍然改文件名**——重启后彻底干净。
    /// </summary>
    internal static bool TryBepInExSoftStop(string pluginPath, out string message)
    {
        message = "";
        try
        {
            var chain = FindChainloaderInstance();
            if (chain == null) { message = "chainloader not found"; return false; }

            var dict = GetMember(chain, "Plugins") as IDictionary;
            if (dict == null) { message = "Plugins unreadable"; return false; }

            string want;
            try { want = Path.GetFullPath(pluginPath); } catch { want = pluginPath; }

            object info = null;
            foreach (DictionaryEntry e in dict)
            {
                string loc = GetString(e.Value, "Location");
                bool same;
                try { same = string.Equals(Path.GetFullPath(loc), want, StringComparison.OrdinalIgnoreCase); }
                catch { same = string.Equals(loc, pluginPath, StringComparison.OrdinalIgnoreCase); }
                if (same) { info = e.Value; break; }
            }
            if (info == null) { message = "plugin not loaded (nothing to stop)"; return false; }

            var instance = GetMember(info, "Instance");
            if (instance == null) { message = "plugin instance missing"; return false; }

            Assembly asm = null;
            try { asm = instance.GetType().Assembly; } catch { }
            if (asm == null) { message = "assembly unknown"; return false; }

            int unloaded = 0, unpatched = 0, destroyed = 0;
            string unloadReturned = "";
            string err = "";

            // 1) 让插件自己先收拾（只有它重写了 Unload 才有意义）
            try
            {
                var mi = instance.GetType().GetMethod("Unload", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (mi != null && mi.DeclaringType != null && mi.DeclaringType.Name != "BasePlugin")
                {
                    var r = mi.Invoke(instance, null);
                    unloaded = 1;
                    unloadReturned = r?.ToString() ?? "null";
                }
            }
            catch (Exception ex) { err += "unload:" + (ex.InnerException?.Message ?? ex.Message) + ";"; }

            // 2) 撤 patch
            try { unpatched = UnpatchAssembly(asm, out string perr); if (perr.Length > 0) err += perr + ";"; }
            catch (Exception ex) { err += "unpatch:" + ex.Message + ";"; }

            // 3) 销毁它加的组件
            try { destroyed = DestroyAssemblyComponents(asm); }
            catch (Exception ex) { err += "components:" + ex.Message + ";"; }

            string an = "";
            try { an = asm.GetName().Name ?? ""; } catch { }
            message = $"unloaded={unloaded}" + (unloadReturned.Length > 0 ? $"({unloadReturned})" : "")
                    + $" unpatched={unpatched} components={destroyed} left={_lastLeft} asm='{an}'";
            if (err.Length > 0) message += " errors=" + err;

            var msg = message;
            CoopLog.Info("modmenu.toggle", () => "bep soft-stop: " + msg);
            return unpatched > 0 || destroyed > 0 || unloaded > 0;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    /// <summary>撤掉"补丁方法属于指定程序集"的所有 Harmony 补丁，返回撤掉的条数。</summary>
    private static int UnpatchAssembly(Assembly asm, out string error)
    {
        error = "";
        int n = 0;
        try
        {
            var harmonyType = FindType("HarmonyLib.Harmony");
            if (harmonyType == null) { error = "harmony missing"; return 0; }

            var harmony = Activator.CreateInstance(harmonyType, new object[] { "open.nest.modmenu" });
            var miAll = harmonyType.GetMethod("GetAllPatchedMethods", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
            var miInfo = harmonyType.GetMethod("GetPatchInfo", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance,
                null, new[] { typeof(MethodBase) }, null);
            var miUnpatch = harmonyType.GetMethod("Unpatch", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance,
                null, new[] { typeof(MethodBase), typeof(MethodInfo) }, null);
            if (miAll == null || miInfo == null || miUnpatch == null) { error = "harmony api missing"; return 0; }

            // ⚠️ GetAllPatchedMethods/GetPatchInfo 在 Harmony 2.x 里是 static（用实例当 target 调 static 也合法）
            var all = miAll.Invoke(miAll.IsStatic ? null : harmony, null) as IEnumerable;
            if (all == null) { error = "no patch list"; return 0; }

            var patches = new List<MethodBase>();
            foreach (var m in all) if (m is MethodBase mb) patches.Add(mb);

            int totalPatches = 0;
            var ownerSet = new HashSet<string>(StringComparer.Ordinal);
            // ⚠️ 去重必须按 **(目标方法, 补丁方法)** 成对做 —— 同一插件常用**同一个补丁方法**挂多个目标
            //   （实测踩过：只按补丁方法去重 → 第二个目标的补丁被跳过、残留没撤掉）。大小写两套字段会读到同一批，成对 key 天然去重。
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < patches.Count; i++)
            {
                var target = patches[i];
                object pinfo;
                try { pinfo = miInfo.Invoke(miInfo.IsStatic ? null : harmony, new object[] { target }); }
                catch (Exception ex) { if (error.Length == 0) error = "getpatchinfo: " + (ex.InnerException?.Message ?? ex.Message); continue; }
                if (pinfo == null) continue;

                string targetKey = TokenOf(target);
                foreach (var kind in PatchArrayFields)
                {
                    // ⚠️ HarmonyX 2.10 的 `Patches` 用**只读集合**字段（Prefixes/Postfixes/…），
                    //    不是数组（实测 `is not Array` → 一条都读不到 → unpatched=0）；小写名字兼容早期 Harmony 的 `PatchInfo`。
                    object raw;
                    try { raw = GetMember(pinfo, kind); } catch { continue; }
                    if (raw is not IEnumerable seq) continue;

                    foreach (var patch in seq)
                    {
                        if (patch == null) continue;
                        var pm = GetMember(patch, "PatchMethod") as MethodInfo;
                        if (pm == null) continue;
                        if (!seen.Add(targetKey + "|" + TokenOf(pm))) continue;
                        totalPatches++;

                        var owner = GetMember(patch, "owner") as string ?? "";
                        if (owner.Length > 0) ownerSet.Add(owner);

                        bool mine;
                        try { mine = pm.DeclaringType != null && pm.DeclaringType.Assembly == asm; }
                        catch { mine = false; }
                        if (!mine) continue;

                        try { miUnpatch.Invoke(miUnpatch.IsStatic ? null : harmony, new object[] { target, pm }); n++; }
                        catch (Exception ex) { error = "unpatch fail: " + (ex.InnerException?.Message ?? ex.Message); }
                    }
                }
            }

            // 复查：该程序集还剩几条补丁（撤完应当是 0；不是 0 就在 UI 备注里如实说）
            int left = CountAssemblyPatches(asm, harmony, miAll, miInfo);
            try
            {
                int methodCount = patches.Count, patchCount = totalPatches, hit = n;
                string owners = string.Join(",", ownerSet);
                CoopLog.Info("modmenu.toggle", () => $"patch census: methods={methodCount} patches={patchCount} owners=[{owners}] matched={hit} left={left}");
            }
            catch { }
            _lastLeft = left;
        }
        catch (Exception ex) { error = ex.Message; }
        return n;
    }

    /// <summary>复查用：数一下某程序集还剩多少条补丁。</summary>
    private static int CountAssemblyPatches(Assembly asm, object harmony, MethodInfo miAll, MethodInfo miInfo)
    {
        int left = 0;
        try
        {
            var all = miAll.Invoke(miAll.IsStatic ? null : harmony, null) as IEnumerable;
            if (all == null) return 0;
            foreach (var m in all)
            {
                if (m is not MethodBase target) continue;
                object pinfo;
                try { pinfo = miInfo.Invoke(miInfo.IsStatic ? null : harmony, new object[] { target }); }
                catch { continue; }
                if (pinfo == null) continue;
                foreach (var kind in PatchArrayFields)
                {
                    if (GetMember(pinfo, kind) is not IEnumerable seq) continue;
                    foreach (var patch in seq)
                    {
                        var pm = patch == null ? null : GetMember(patch, "PatchMethod") as MethodInfo;
                        if (pm == null) continue;
                        try { if (pm.DeclaringType != null && pm.DeclaringType.Assembly == asm) left++; } catch { }
                    }
                }
            }
        }
        catch { }
        return left;
    }

    private static int _lastLeft;

    /// <summary>方法的稳定标识（MetadataToken 可能不可用 → 退回 名称+签名）。</summary>
    private static string TokenOf(MethodBase m)
    {
        try { return m.MetadataToken.ToString(); }
        catch { }
        try { return (m.DeclaringType?.FullName ?? "") + "::" + m.Name; }
        catch { return m?.ToString() ?? ""; }
    }

    private static readonly string[] PatchArrayFields =
    {
        // HarmonyX（BepInEx 6 自带的 0Harmony 2.10.x）：`Harmony.GetPatchInfo` 返回 `Patches`，字段为只读集合
        "Prefixes", "Postfixes", "Transpilers", "Finalizers", "ILManipulators",
        // 早期 Harmony 2 的 `PatchInfo`（小写字段，Patch[]）——两套都试，成本可忽略
        "prefixes", "postfixes", "transpilers", "finalizers", "ilmanipulators",
    };

    /// <summary>销毁"组件类型属于指定程序集"的 MonoBehaviour（连带停掉它的 Update/协程），返回条数。</summary>
    private static int DestroyAssemblyComponents(Assembly asm)
    {
        int n = 0;
        try
        {
            // ⚠️ 必须两个来源都扫（实测踩过）：
            //   - 插件加在 **BepInEx_Manager** 上的组件属于 `HideFlags.HideAndDontSave` 对象，
            //     `Object.FindObjectsOfType` **看不见它**（首次实现 components=0、组件心跳还在跑）；
            //   - `Resources.FindObjectsOfTypeAll<T>()` 能拿到隐藏/非活跃对象，但代价略高。
            var list = new List<UnityEngine.MonoBehaviour>();
            try
            {
                var a = UnityEngine.Object.FindObjectsOfType<UnityEngine.MonoBehaviour>(true);
                if (a != null) list.AddRange(a);
            }
            catch { }
            try
            {
                var b = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.MonoBehaviour>();
                if (b != null) list.AddRange(b);
            }
            catch { }

            var done = new HashSet<int>();
            for (int j = 0; j < list.Count; j++)
            {
                var mb = list[j];
                if (mb == null) continue;
                int iid;
                try { iid = mb.GetInstanceID(); } catch { continue; }
                if (!done.Add(iid)) continue;          // 两个来源会重叠
                bool mine;
                try { mine = mb.GetType().Assembly == asm; }
                catch { mine = false; }
                if (!mine) continue;
                try { UnityEngine.Object.Destroy(mb); n++; } catch { }
            }
        }
        catch { }
        return n;
    }

    /// <summary>
    /// **运行中加载一个 BepInEx 插件**（不需要重启）。
    ///
    /// 原理 —— 走 BepInEx 自己的官方路径（源码已核实，`BepInEx.Core/Bootstrap/BaseChainloader.cs` +
    /// `BepInEx.Unity.IL2CPP/IL2CPPChainloader.cs`）：
    /// <c>Instance.LoadPlugins(string[] dirs)</c> → `DiscoverPluginsFrom(dir)`（Cecil 只读元数据发现插件）
    /// → `ModifyLoadOrder`（**GUID 已在 `Plugins` 里的会被跳过**："Skipping [x] because a plugin with a similar
    /// GUID has been already loaded" → 天然幂等，不会把已加载的插件二次实例化）
    /// → `LoadPlugin` = `Activator.CreateInstance(type)` + `pluginInstance.Load()`（即插件的 `Awake`）。
    ///
    /// ⚠️ **必须传目录，不能传文件**：`TypeLoader.FindPluginTypes` 内部是
    /// `Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories)`，传文件路径会直接抛 `DirectoryNotFoundException`。
    /// ⚠️ 依赖不满足（硬依赖缺失/版本不符）/ 进程过滤不符 / 与已加载插件不兼容 → BepInEx 自己跳过并写
    /// `DependencyErrors` + 日志，**不抛异常** → 我们靠读回 `Plugins` 字典判断真实结果。
    /// ⚠️ **patcher 不参与**（preloader 阶段早过了）；插件若强依赖"启动最早时机"（例如必须在首个场景前打 patch），
    /// 行为可能与重启加载不同 —— 这是这类热加载的固有代价，UI 文案已说明"卸载仍需重启"。
    /// ⚠️ **卸载依然不可能**：`BasePlugin.Unload()` 在 BepInEx 里默认 `=> false`，且链加载器没有任何调用点；
    /// Harmony patch 也不会被撤销（除非插件自己 `UnpatchSelf`）。
    /// </summary>
    internal static bool TryBepInExHotLoad(string pluginPath, out string message)
    {
        message = "";
        long t0 = 0;
        try { t0 = Environment.TickCount64; } catch { }
        try
        {
            if (string.IsNullOrEmpty(pluginPath) || !File.Exists(pluginPath)) { message = "file missing"; return false; }

            string dir = Path.GetDirectoryName(pluginPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { message = "dir missing"; return false; }

            var chain = FindChainloaderInstance();
            if (chain == null) { message = "chainloader instance not found"; return false; }

            var mi = chain.GetType().GetMethod("LoadPlugins",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string[]) }, null);
            if (mi == null) { message = "LoadPlugins(string[]) not found"; return false; }

            var args = new object[] { new[] { dir } };
            try { mi.Invoke(chain, args); }
            catch (Exception ex) { message = "invoke failed: " + (ex.InnerException?.Message ?? ex.Message); return false; }

            bool ok = VerifyPluginLoaded(chain, pluginPath, out message);
            // 耗时也记一条：LoadPlugins 会 Cecil 扫整个目录（含已加载插件的“已加载跳过”），偶发卡顿要能看出来。
            // ⚠️ 日志单独 try：绝不能因为"打日志失败"把已成功的热加载报成失败。
            try
            {
                long ms = Environment.TickCount64 - t0;
                var m = message; string d = dir;
                CoopLog.Info("modmenu.toggle", () => $"bep hot-load: ok={ok} in={ms}ms dir='{d}' result='{m}'");
            }
            catch { }
            return ok;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    /// <summary>热加载结果校验：链加载器 `Plugins`（GUID → PluginInfo）里有没有"本文件 + 有实例"的条目。</summary>
    private static bool VerifyPluginLoaded(object chain, string pluginPath, out string message)
    {
        message = "";
        try
        {
            var dict = GetMember(chain, "Plugins") as IDictionary;
            if (dict == null) { message = "Plugins unreadable"; return false; }

            string want;
            try { want = Path.GetFullPath(pluginPath); } catch { want = pluginPath; }

            foreach (DictionaryEntry e in dict)
            {
                var info = e.Value;
                if (info == null) continue;

                string loc = GetString(info, "Location");
                bool same;
                try { same = string.Equals(Path.GetFullPath(loc), want, StringComparison.OrdinalIgnoreCase); }
                catch { same = string.Equals(loc, pluginPath, StringComparison.OrdinalIgnoreCase); }
                if (!same) continue;

                var inst = GetMember(info, "Instance");
                if (inst == null) { message = "found but no instance"; return false; }
                message = GetString(GetMember(info, "Metadata"), "GUID");
                return true;
            }

            message = "not registered by chainloader";
            return false;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    /// <summary>按**程序集名或路径**找回 `MelonAssembly` 对象（`LoadedAssemblies` 在 Unregister 后仍保留它）。</summary>
    internal static object FindMelonAssembly(string assemblyNameOrPath)
    {
        if (string.IsNullOrEmpty(assemblyNameOrPath)) return null;
        try
        {
            Type t = FindType("MelonLoader.MelonAssembly");
            if (t == null) return null;
            object all = StaticGet(t, "LoadedAssemblies");
            if (all is not IEnumerable seq) return null;

            foreach (var ma in seq)
            {
                try
                {
                    var asm = GetMember(ma, "Assembly") as Assembly;
                    string loc = AssemblyLocation(asm);
                    string an = asm?.GetName()?.Name ?? "";
                    if (string.Equals(loc, assemblyNameOrPath, StringComparison.OrdinalIgnoreCase)) return ma;
                    if (string.Equals(an, assemblyNameOrPath, StringComparison.OrdinalIgnoreCase)) return ma;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 运行中**重新注册**一个之前被 `Unregister` 的 ML 模组。
    ///
    /// ⚠️ 两个坑（源码依据）：`MelonAssembly.LoadMelons()` 开头有 `if (melonsLoaded) return;` 守卫，
    /// 而 `UnregisterMelons` **不复位** 该字段 → 必须反射把 `melonsLoaded` 置回 false；
    /// 同时清空 `loadedMelons`（否则旧实例会累积）。之后 `LoadMelons()` 会**重新实例化**并跑 `OnInitializeMelon`。
    /// </summary>
    internal static bool TryMelonReload(string pathOrName)
    {
        if (string.IsNullOrEmpty(pathOrName)) return false;
        try
        {
            // 路径 ①：按加载器自己的入口重新装载（`MelonAssembly.LoadMelonAssembly(path, loadMelons: true)`）
            //   —— 与 MLL 扫描目录时同一条路，不依赖任何私有字段。
            if (File.Exists(pathOrName))
            {
                if (TryReloadViaLoadAssembly(pathOrName)) return true;
            }

            // 路径 ②：复用内存里已有的 MelonAssembly，复位 melonsLoaded 后重新创建实例。
            //   ⚠️ 两个必知事实（源码依据）：
            //   - `MelonAssembly.LoadMelons()` 开头有 `if (melonsLoaded) return;` 守卫，而 `UnregisterMelons` **不复位**它；
            //   - `LoadMelons()` 只**创建实例 + 填元数据**，真正的注册在 `MelonBase.RegisterSorted(...)`
            //     （MelonFolderHandler 就是这么调的）——只调 LoadMelons 会"看着成功但模组不跑"（实测踩过）。
            var ma = FindMelonAssembly(pathOrName);
            if (ma == null) return false;

            var t = ma.GetType();
            var fLoaded = t.GetField("melonsLoaded", BindingFlags.NonPublic | BindingFlags.Instance);
            var fList = t.GetField("loadedMelons", BindingFlags.NonPublic | BindingFlags.Instance);
            if (fLoaded == null || fList == null) return false;

            fLoaded.SetValue(ma, false);
            (fList.GetValue(ma) as System.Collections.IList)?.Clear();

            var loadMi = t.GetMethod("LoadMelons", BindingFlags.Public | BindingFlags.Instance);
            if (loadMi == null) return false;
            loadMi.Invoke(ma, null);

            var listObj = fList.GetValue(ma);
            if (listObj is not System.Collections.IEnumerable seq) return false;
            if (!RegisterMelons(seq)) return false;

            return IsRegistered(ma, pathOrName);
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.runtime", () => "melon reload failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>路径 ①：`MelonAssembly.LoadMelonAssembly(path, true)` + `RegisterSorted`（与加载器同路）。</summary>
    private static bool TryReloadViaLoadAssembly(string path)
    {
        try
        {
            Type t = FindType("MelonLoader.MelonAssembly");
            if (t == null) return false;

            var loadAsm = t.GetMethod("LoadMelonAssembly",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(bool) }, null);
            if (loadAsm == null) return false;

            var ma = loadAsm.Invoke(null, new object[] { path, true });
            if (ma == null) return false;

            var melonsProp = t.GetProperty("LoadedMelons", BindingFlags.Public | BindingFlags.Instance);
            var melons = melonsProp?.GetValue(ma);
            if (melons is not System.Collections.IEnumerable seq) return false;

            if (!RegisterMelons(seq)) return false;
            return IsRegistered(ma, path);
        }
        catch (Exception ex)
        {
            CoopLog.Debug("modmenu.runtime", () => "reload via LoadMelonAssembly failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>把（尚未注册的）实例交给 `MelonBase.RegisterSorted&lt;T&gt;` —— 注册含依赖/优先级排序、patch、事件订阅。</summary>
    private static bool RegisterMelons(System.Collections.IEnumerable melons)
    {
        Type baseType = FindType("MelonLoader.MelonBase");
        if (baseType == null) return false;

        // 待注册 = 列表里 Registered == false 的实例
        var pending = new List<object>();
        foreach (var m in melons)
        {
            if (m == null) continue;
            bool registered = false;
            try
            {
                var regProp = m.GetType().GetProperty("Registered", BindingFlags.Public | BindingFlags.Instance);
                if (regProp != null) registered = (bool)regProp.GetValue(m);
            }
            catch { }
            if (!registered) pending.Add(m);
        }
        if (pending.Count == 0) return false;

        MethodInfo registerSorted = null;
        var statics = baseType.GetMethods(BindingFlags.Public | BindingFlags.Static);
        for (int i = 0; i < statics.Length; i++)
        {
            var mi = statics[i];
            if (mi.Name != "RegisterSorted" || !mi.IsGenericMethodDefinition) continue;
            try { registerSorted = mi.MakeGenericMethod(baseType); } catch { registerSorted = null; }
            if (registerSorted != null) break;
        }
        if (registerSorted == null) return false;

        // 参数是 IEnumerable<T>：用 List<MelonBase> 承载（全部是托管类型，可安全构造泛型）
        var typed = Activator.CreateInstance(typeof(List<>).MakeGenericType(baseType));
        var addMi = typed.GetType().GetMethod("Add");
        for (int i = 0; i < pending.Count; i++) addMi.Invoke(typed, new[] { pending[i] });

        registerSorted.Invoke(null, new object[] { typed });
        return true;
    }

    /// <summary>校验：该程序集是否真的有实例回到了 `MelonBase.RegisteredMelons`（**失败就老实报错**，不假称成功）。</summary>
    private static bool IsRegistered(object melonAssembly, string pathOrName)
    {
        try
        {
            Type baseType = FindType("MelonLoader.MelonBase");
            if (baseType == null) return false;
            object all = StaticGet(baseType, "RegisteredMelons");
            if (all is not System.Collections.IEnumerable seq) return false;
            if (melonAssembly == null) return false;

            string wantAsm = "";
            try { wantAsm = GetMember(GetMember(melonAssembly, "Assembly"), "FullName") as string ?? ""; } catch { }

            foreach (var m in seq)
            {
                if (m == null) continue;
                string loc = "";
                try { loc = GetString(GetMember(m, "MelonAssembly"), "Location"); } catch { }
                if (!string.IsNullOrEmpty(loc) &&
                    (string.Equals(loc, pathOrName, StringComparison.OrdinalIgnoreCase) ||
                     AssemblyNameMatches(wantAsm, loc))) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool AssemblyNameMatches(string assemblyFullName, string location)
    {
        if (string.IsNullOrEmpty(assemblyFullName) || string.IsNullOrEmpty(location)) return false;
        try
        {
            string file = System.IO.Path.GetFileNameWithoutExtension(location) ?? "";
            int comma = assemblyFullName.IndexOf(',');
            string name = comma > 0 ? assemblyFullName.Substring(0, comma) : assemblyFullName;
            return string.Equals(name, file, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// 读程序集级特性的字符串数组（按**特性类型名**匹配，不引用类型）；
    /// 用 CustomAttributeData —— 只读元数据、不实例化特性对象（更安全）。
    /// </summary>
    private static string[] ReadAssemblyNames(Assembly asm, string attributeTypeName)
    {
        try
        {
            foreach (var cad in asm.GetCustomAttributesData())
            {
                var tn = cad.Constructor?.DeclaringType?.Name;
                if (!string.Equals(tn, attributeTypeName, StringComparison.Ordinal)) continue;

                var names = new List<string>();
                foreach (var arg in cad.ConstructorArguments)
                    CollectStrings(names, arg.Value);
                foreach (var na in cad.NamedArguments)
                    CollectStrings(names, na.TypedValue.Value);
                if (names.Count > 0) return names.ToArray();
            }
        }
        catch { }
        return Array.Empty<string>();
    }

    /// <summary>把特性参数里的字符串（单个或数组）收集进 names。</summary>
    private static void CollectStrings(List<string> names, object value)
    {
        if (value is string s) { names.Add(s); return; }
        if (value is IEnumerable en)
            foreach (var v in en) if (v is string vs) names.Add(vs);
    }

    /// <summary>读程序集级 int 特性（如 MelonPriorityAttribute）。</summary>
    internal static int? ReadAssemblyInt(Assembly asm, string attributeTypeName)
    {
        try
        {
            foreach (var cad in asm.GetCustomAttributesData())
            {
                if (!string.Equals(cad.Constructor?.DeclaringType?.Name, attributeTypeName, StringComparison.Ordinal)) continue;
                if (cad.ConstructorArguments.Count == 1 && cad.ConstructorArguments[0].Value is int v) return v;
            }
        }
        catch { }
        return null;
    }
}
