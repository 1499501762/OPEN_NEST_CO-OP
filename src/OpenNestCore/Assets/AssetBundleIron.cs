using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using OpenNestCore.Logging;

namespace OpenNestCore.Assets;

/// <summary>
/// 受管 AssetBundle 句柄（对外主类），模仿原生 AssetBundle API 语感。
/// Managed AssetBundle handle (public-facing class), mimicking the native AssetBundle API.
///
/// 生命周期由内部全局缓存引用计数**自动管理**：
/// Lifecycle is auto-managed by the internal global cached refcount:
/// - <see cref="Load(string)"/>：同路径已加载则引用+1（多模组共享同一句柄 + FileStream）。
///   Load: refcount+1 if already loaded (mods share one handle + FileStream).
/// - <see cref="Dispose"/>：引用归零才按安全顺序自动卸载（Unload(false) → 关闭 FileStream）。
///   Dispose: unloads in safe order (Unload(false) → close FileStream) only when refcount hits zero.
/// - <see cref="UnloadAll"/>：游戏退出/模组卸载前由宿主调用。Called by host on game exit / mod unload.
///
/// 默认不暴露原生 bundle（安全）；确有需要时用 <see cref="GetUnsafeRawBundle"/>（风险自负）。
/// The raw bundle is NOT exposed by default (safe); use GetUnsafeRawBundle() if you must (at your own risk).
/// </summary>
public sealed class AssetBundleIron : IDisposable
{
    internal AssetBundle Bundle;                      // 原生 bundle（内部，默认不公开）
    internal Il2CppSystem.IO.FileStream Stream;       // 保持打开的 native 句柄（内部）
    internal string BundlePath;                       // 注册表 key（完整路径）
    internal int RefCount;

    // 全局缓存注册表：path → 句柄（引用计数 + 去重共享，多模组不重复开 native 句柄）。
    // Global registry: path → handle (refcount + dedup sharing; no duplicate native handles across mods).
    private static readonly Dictionary<string, AssetBundleIron> _registry = new();
    private static readonly object _sync = new();

    /// <summary>是否仍有效（未被 Unload）。Whether still valid (not unloaded).</summary>
    public bool IsValid => Bundle != null;

    /// <summary>安全只读：bundle 名字（容错，不逃逸原生对象）。Safe read-only bundle name (fault-tolerant).</summary>
    public string Name
    {
        get
        {
            try { return Bundle != null ? Bundle.name : null; } catch { return null; }
        }
    }

    /// <summary>当前引用计数。Current reference count.</summary>
    public int Ref => RefCount;

    /// <summary>已加载 bundle 数（诊断）。Loaded bundle count (diagnostics).</summary>
    public static int LoadedCount { get { lock (_sync) return _registry.Count; } }

    /// <summary>
    /// 静态入口：加载或引用+1。同路径已加载则复用（多模组共享，不重复开 native 句柄）；
    /// 未加载则 LoadFromStream（保持 stream 打开）。返回 null 表示失败。
    /// Static entry: load or refcount+1. Reuses if already loaded; LoadFromStream otherwise. null = failure.
    /// </summary>
    public static AssetBundleIron Load(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
        {
            CoopLog.Warn("AssetBundleIron.load", () => $"Load: path invalid or missing '{fullPath}'");
            return null;
        }
        string full = Path.GetFullPath(fullPath);
        lock (_sync)
        {
            if (_registry.TryGetValue(full, out var existing))
            {
                existing.RefCount++;
                return existing;
            }
            var h = LoadBundleInternal(full);
            if (h == null) return null;
            h.RefCount = 1;
            _registry[full] = h;
            return h;
        }
    }

    /// <summary>
    /// 释放一次引用。归零时执行卸载契约（调用方应已销毁所有实例 → Unload(false) → 关闭 stream → 移出注册表）。
    /// 还有引用时不动作（保护其他持有方）。
    /// Release one reference. Unloads when reaching zero. No-op while references remain.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            RefCount--;
            if (RefCount > 0) return;
            if (BundlePath != null) _registry.Remove(BundlePath);
            UnloadBundle(this);
        }
    }

    /// <summary>
    /// 卸载全部已加载 bundle（游戏退出 / 宿主模组卸载前调用；此时所有消费方必须已释放）。
    /// ⚠️ 只应在本进程确实不再需要任何 bundle 时调用——若还有其他模组持有句柄会使其后续调用失效（Native 崩溃风险由调用方负责）。
    /// Unload all loaded bundles (call before game exit / host-mod unload; all consumers must be released first).
    /// ⚠️ Only call when this process truly needs no more bundles — other mods still holding handles will break (caller's responsibility).
    /// </summary>
    public static void UnloadAll()
    {
        lock (_sync)
        {
            foreach (var h in _registry.Values) UnloadBundle(h);
            _registry.Clear();
        }
    }

    // ==================== 代理：同步加载 / Sync load proxies ====================

    /// <summary>同步加载指定资源。Synchronously load the asset with the given name.</summary>
    public T LoadAsset<T>(string name) where T : UnityEngine.Object => Bundle?.LoadAsset<T>(name);

    /// <summary>同步加载该 bundle 中类型 T 的全部资源。Load all assets of type T in the bundle.</summary>
    public T[] LoadAllAssets<T>() where T : UnityEngine.Object => Bundle?.LoadAllAssets<T>();

    /// <summary>同步加载资源及其所有子资源。Load the asset and all its sub-assets.</summary>
    public T[] LoadAssetWithSubAssets<T>(string name) where T : UnityEngine.Object => Bundle?.LoadAssetWithSubAssets<T>(name);

    // ==================== 代理：异步加载 / Async load proxies ====================
    // ⚠️ 该游戏 IL2CPP 裁剪极深：异步 API（LoadAssetAsync 等）可能与 LoadFromFile 同批被裁，
    //    调用时若抛 MethodNotFoundException 需 try-catch 或改用同步 API。请自行验证。
    // ⚠️ This game's IL2CPP strips deeply: async APIs may be stripped like LoadFromFile;
    //    if a MethodNotFoundException is thrown, try-catch it or fall back to the sync APIs. Verify yourself.

    /// <summary>异步加载指定资源。Asynchronously load the asset with the given name.</summary>
    public AssetBundleRequest LoadAssetAsync<T>(string name) where T : UnityEngine.Object => Bundle?.LoadAssetAsync<T>(name);

    /// <summary>异步加载该 bundle 中类型 T 的全部资源。Asynchronously load all assets of type T.</summary>
    public AssetBundleRequest LoadAllAssetsAsync<T>() where T : UnityEngine.Object => Bundle?.LoadAllAssetsAsync<T>();

    /// <summary>异步加载资源及其所有子资源。Asynchronously load the asset and its sub-assets.</summary>
    public AssetBundleRequest LoadAssetWithSubAssetsAsync<T>(string name) where T : UnityEngine.Object => Bundle?.LoadAssetWithSubAssetsAsync<T>(name);

    // ==================== 查询 / Queries ====================

    /// <summary>判断 bundle 是否包含指定资源。Whether the bundle contains the named asset.</summary>
    public bool Contains(string name) => Bundle != null && Bundle.Contains(name);

    /// <summary>获取 bundle 内全部资源名。All asset names in the bundle.</summary>
    public string[] GetAllAssetNames() => Bundle?.GetAllAssetNames();

    /// <summary>获取 bundle 内全部场景路径。All scene paths in the bundle.</summary>
    public string[] GetAllScenePaths() => Bundle?.GetAllScenePaths();

    // ==================== 属性 / Properties ====================

    /// <summary>是否为流式场景 bundle。Whether this bundle is a streamed-scene AssetBundle.</summary>
    public bool isStreamedSceneAssetBundle => Bundle != null && Bundle.isStreamedSceneAssetBundle;

    // ==================== 便捷 / Convenience ====================

    /// <summary>
    /// 便捷：按候选名逐一加载 GameObject 预置（兜底取第一个 GameObject 资源）。
    /// Convenience: load a GameObject prefab by candidate names (falls back to the first GameObject).
    /// </summary>
    public GameObject LoadPrefab(params string[] candidateNames)
    {
        var bundle = Bundle;
        if (bundle == null) return null;
        if (candidateNames != null)
            foreach (var name in candidateNames)
            {
                try
                {
                    var obj = bundle.LoadAsset<GameObject>(name);
                    if (obj != null) return obj;
                }
                catch (Exception ex) { CoopLog.Warn("AssetBundleIron.prefab", () => $"LoadAsset({name}) → {ex.GetType().Name}: {ex.Message}"); }
            }
        try
        {
            var all = bundle.LoadAllAssets<GameObject>();
            if (all != null)
                foreach (var o in all)
                    if (o != null) return o;
        }
        catch (Exception ex) { CoopLog.Warn("AssetBundleIron.prefab", () => $"LoadAllAssets → {ex.GetType().Name}: {ex.Message}"); }
        return null;
    }

    /// <summary>
    /// 修复材质：打包 shader 引用为空/内置（Standard/Legacy）→ 用游戏内 URP shader 替换，
    /// 并把 _MainTex 纹理迁移到 _BaseMap（URP/Lit 用 _BaseMap，否则贴图不显示）。
    /// Repair materials: empty/Standard/Legacy shaders → game's URP shader + _MainTex→_BaseMap texture migration.
    /// </summary>
    public static void RepairMaterials(GameObject go)
    {
        try
        {
            // 先探测游戏内可用的 shader（URP 游戏构建里应有）Probe usable in-game shaders.
            string[] candidates = {
                "Universal Render Pipeline/Lit",
                "Universal Render Pipeline/Simple Lit",
                "Standard",
                "Legacy Shaders/Diffuse",
                "Sprites/Default",
            };
            Shader found = null;
            foreach (var cn in candidates)
            {
                try
                {
                    var s = Shader.Find(cn);
                    if (s != null) { found = s; CoopLog.Debug("AssetBundleIron.shader", () => $"Shader.Find('{cn}') → ok"); break; }
                    CoopLog.Debug("AssetBundleIron.shader", () => $"Shader.Find('{cn}') → null");
                }
                catch (Exception ex) { CoopLog.Debug("AssetBundleIron.shader", () => $"Shader.Find err: {ex.Message}"); }
            }
            if (found == null) { CoopLog.Warn("AssetBundleIron.shader", () => "无可用 shader，材质无法修复 / no usable shader, materials not repaired"); return; }

            int fixedMats = 0;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                try
                {
                    var mats = r.sharedMaterials;
                    if (mats == null) continue;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null) continue;
                        var s = m.shader;
                        // URP 下 Standard/Legacy 不渲染 → 替换。Replace non-URP shaders under URP.
                        bool bad = s == null || string.IsNullOrEmpty(s.name)
                                   || s.name.StartsWith("Standard", StringComparison.Ordinal)
                                   || s.name.StartsWith("Legacy", StringComparison.Ordinal);
                        if (bad)
                        {
                            m.shader = found;
                            // 纹理迁移：_MainTex → _BaseMap。Texture migration.
                            try
                            {
                                if (m.HasProperty("_MainTex") && m.HasProperty("_BaseMap"))
                                {
                                    var tex = m.GetTexture("_MainTex");
                                    if (tex != null)
                                    {
                                        m.SetTexture("_BaseMap", tex);
                                        CoopLog.Debug("AssetBundleIron.mat", () => $"迁移纹理 _MainTex → _BaseMap ({tex.name})");
                                    }
                                }
                            }
                            catch (Exception ex) { CoopLog.Debug("AssetBundleIron.mat", () => $"纹理迁移 err: {ex.Message}"); }
                            fixedMats++;
                            CoopLog.Debug("AssetBundleIron.mat", () => $"修复材质 {r.name}[{i}] {((s != null) ? s.name : "null")} → {found.name}");
                        }
                    }
                }
                catch (Exception ex) { CoopLog.Warn("AssetBundleIron.mat", () => $"材质修复 {r.name} err: {ex.Message}"); }
            }
            CoopLog.Debug("AssetBundleIron.mat", () => $"材质修复完成 fixed={fixedMats} / materials repaired");
        }
        catch (Exception ex) { CoopLog.Warn("AssetBundleIron.mat", () => $"RepairMaterials err: {ex.GetType().Name}: {ex.Message}"); }
    }

    // ==================== 安全逃逸 / Unsafe escape ====================

    /// <summary>
    /// ⚠️ 显式逃逸接口：返回底层原生 AssetBundle（绕过托管生命周期 / 引用计数保护）。
    /// ⚠️ Unsafe escape: returns the raw native AssetBundle, bypassing managed lifecycle / refcount protection.
    /// 方法名已明确警告风险——**调用此方法之后产生的崩溃不在项目支持范围**。
    /// The method name warns of the risk — **any crash after calling this method is outside the project's support scope**.
    /// </summary>
    public AssetBundle GetUnsafeRawBundle() => Bundle;

    // ==================== 内部 / Internal ====================

    /// <summary>加载 bundle（内部）：LoadFromStream 保持 stream 打开；失败时关闭已开的 stream。</summary>
    private static AssetBundleIron LoadBundleInternal(string full)
    {
        try
        {
            var stream = new Il2CppSystem.IO.FileStream(full, Il2CppSystem.IO.FileMode.Open, Il2CppSystem.IO.FileAccess.Read, Il2CppSystem.IO.FileShare.Read);
            var bundle = AssetBundle.LoadFromStream(stream);
            if (bundle == null)
            {
                try { stream.Close(); } catch { }
                CoopLog.Warn("AssetBundleIron.load", () => "AssetBundle.LoadFromStream 返回 null / returned null");
                return null;
            }
            return new AssetBundleIron { Bundle = bundle, Stream = stream, BundlePath = full };
        }
        catch (Exception ex)
        {
            CoopLog.Warn("AssetBundleIron.load", () => $"LoadFromStream → {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>按契约卸载单个：Unload(false) → 关闭 native 句柄 → 置空。</summary>
    private static void UnloadBundle(AssetBundleIron h)
    {
        if (h == null) return;
        try { if (h.Bundle != null) h.Bundle.Unload(false); } catch (Exception ex) { CoopLog.Warn("AssetBundleIron.unload", () => $"Unload(false) err: {ex.Message}"); }
        try { if (h.Stream != null) h.Stream.Close(); } catch (Exception ex) { CoopLog.Warn("AssetBundleIron.unload", () => $"stream close err: {ex.Message}"); }
        h.Bundle = null; h.Stream = null;
    }
}
