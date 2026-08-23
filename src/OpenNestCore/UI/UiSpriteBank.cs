using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using OpenNestCore.Assets;
using OpenNestCore.Logging;

namespace OpenNestCore.UI;

/// <summary>
/// 原生 UI 图集库（中英双语注释）。两条路线：
/// 1. **原生路线（推荐，开发者同法）**：<see cref="FromResources"/> / <see cref="PrefabFromResources"/> 直接用
///    `UnityEngine.Resources.Load`（Unity 6000 把 Resources 类并入 CoreModule，双平台 interop 都可用）从
///    `resources.assets` 加载原生 Sprite / UI prefab（UIFoldout*/UICheckMark/UIElement8px/IronRoadMap/Achievement* 等）。
///    主菜单框架（SGRounded/key art/UI Box*）在 sharedassets0、Resources 按名够不着 → 用 <see cref="PrefabFromResources"/>
///    加载原生 prefab 后 <see cref="CollectSprites"/> 收走其引用的 sharedassets0 原生图（受控、无场景卸载风险）。
/// 2. **AssetBundle 路线（备用）**：<see cref="Load"/> 用 <see cref="AssetBundleIron"/> 从自打包 ui.bundle 加载。
///
/// 生命周期：Resources 路线无句柄管理（Resources 自行持有，UnloadUnusedAssets 会释放）；
/// AssetBundle 路线用 AssetBundleIron 引用计数，常驻模式游戏退出由 <see cref="AssetBundleIron.UnloadAll"/> 统一清理。
/// </summary>
public static class UiSpriteBank
{
    private static AssetBundleIron _bundle;
    private static string _path;
    private static readonly Dictionary<string, Sprite> _cache = new();

    /// <summary>当前是否已加载有效 UI bundle。</summary>
    public static bool IsLoaded => _bundle != null && _bundle.IsValid;

    /// <summary>bundle 名字（诊断，安全只读）。</summary>
    public static string BundleName => _bundle?.Name;

    /// <summary>已缓存 sprite 数（诊断）。</summary>
    public static int CachedCount => _cache.Count;

    /// <summary>
    /// 加载 UI 图集 bundle（同路径已加载则复用）。成功返回 true；
    /// bundle 缺失/加载失败返回 false（不抛异常，记 Warn）。
    /// </summary>
    public static bool Load(string bundlePath)
    {
        try
        {
            if (string.IsNullOrEmpty(bundlePath)) return false;
            string full = System.IO.Path.GetFullPath(bundlePath);
            if (_bundle != null && _path == full && _bundle.IsValid) return true;

            var h = AssetBundleIron.Load(full);
            if (h == null)
            {
                CoopLog.Warn("UiSpriteBank.load", () => $"Load failed: '{bundlePath}' (no such bundle? place Models/ui.bundle like player.bundle)");
                return false;
            }

            // 替换旧的（引用-1；归零才卸载）
            _bundle?.Dispose();
            _bundle = h;
            _path = full;
            _cache.Clear();
            CoopLog.Info("UiSpriteBank.load", () => $"loaded UI sprite bundle '{h.Name}'");
            return true;
        }
        catch (System.Exception ex)
        {
            CoopLog.Warn("UiSpriteBank.load", () => $"Load exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>按名字取 sprite（LoadAsset&lt;Sprite&gt; + 缓存）。未加载/找不到返回 null。</summary>
    public static Sprite Get(string name)
    {
        if (_bundle == null || !_bundle.IsValid || string.IsNullOrEmpty(name)) return null;
        if (_cache.TryGetValue(name, out var cached) && cached != null) return cached;
        try
        {
            var sp = _bundle.LoadAsset<Sprite>(name);
            if (sp != null) _cache[name] = sp;
            return sp;
        }
        catch (System.Exception ex)
        {
            CoopLog.Warn("UiSpriteBank.get", () => $"Get('{name}'): {ex.Message}");
            return null;
        }
    }

    /// <summary>取出 bundle 内全部 Sprite（供发现/预览/挨个尝试）。</summary>
    public static Sprite[] LoadAllSprites()
    {
        if (_bundle == null || !_bundle.IsValid) return null;
        try { return _bundle.LoadAllAssets<Sprite>(); }
        catch (System.Exception ex)
        {
            CoopLog.Warn("UiSpriteBank.all", () => $"LoadAllSprites: {ex.Message}");
            return null;
        }
    }

    /// <summary>释放句柄（引用-1；归零才真正卸载并关闭 FileStream）。
    /// ⚠️ 调用前必须先销毁所有用这些 sprite 构建的 UI 实例。常驻模式可不调用（游戏退出统一 UnloadAll）。</summary>
    public static void Unload()
    {
        _bundle?.Dispose();
        _bundle = null;
        _path = null;
        _cache.Clear();
    }

    // ==================== 原生路线：Resources.Load（与游戏开发者同法） ====================

    private static readonly Dictionary<string, Sprite> _resCache = new();

    /// <summary>已缓存的原生 Resources sprite 数（诊断）。</summary>
    public static int ResourcesCachedCount => _resCache.Count;

    /// <summary>原生路线：从 resources.assets 按名加载原生 Sprite（`Resources.Load&lt;Sprite&gt;`，开发者同法）。
    /// 可加载：UIFoldoutOpened/Closed、UICheckMark、UIElement8px、IronRoadMap、AchievementBackground 等。
    /// ⚠️ 主菜单框架（SGRounded/key art/UI Box*）在 sharedassets0、按名够不着 → 用 <see cref="PrefabFromResources"/> + <see cref="CollectSprites"/>。</summary>
    public static Sprite FromResources(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_resCache.TryGetValue(name, out var cached) && cached != null) return cached;
        try
        {
            var sp = Resources.Load<Sprite>(name);
            if (sp != null) _resCache[name] = sp;
            else CoopLog.Debug("UiSpriteBank.res", () => $"FromResources('{name}') null (path 不存在或名字不同)");
            return sp;
        }
        catch (System.Exception ex)
        {
            CoopLog.Warn("UiSpriteBank.res", () => $"FromResources('{name}'): {ex.Message}");
            return null;
        }
    }

    /// <summary>原生路线：从 resources.assets 按名加载原生 UI prefab（`Resources.Load&lt;GameObject&gt;`，如 AchievementDisplay）。
    /// Instantiate 后其引用的 sharedassets0 sprite 进入内存 → <see cref="CollectSprites"/> 收走。</summary>
    public static GameObject PrefabFromResources(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        try
        {
            var go = Resources.Load<GameObject>(name);
            if (go == null) CoopLog.Debug("UiSpriteBank.res", () => $"PrefabFromResources('{name}') null");
            return go;
        }
        catch (System.Exception ex)
        {
            CoopLog.Warn("UiSpriteBank.res", () => $"PrefabFromResources('{name}'): {ex.Message}");
            return null;
        }
    }

    /// <summary>收集 GameObject（含子物体）上所有 Image 引用的 Sprite（去重）。
    /// 用途：Resources.Load 原生 prefab → Instantiate → 收走其引用的 sharedassets0 原生图，喂给 <see cref="UiKit.MakePanel"/>（9-slice）。</summary>
    public static List<Sprite> CollectSprites(GameObject root)
    {
        var result = new List<Sprite>();
        if (root == null) return result;
        try
        {
            var seen = new HashSet<Sprite>();
            foreach (var img in root.GetComponentsInChildren<Image>(true))
            {
                if (img == null || img.sprite == null) continue;
                if (seen.Add(img.sprite)) result.Add(img.sprite);
            }
        }
        catch (System.Exception ex)
        {
            CoopLog.Warn("UiSpriteBank.collect", () => $"CollectSprites: {ex.Message}");
        }
        return result;
    }

    // ==================== 现场捕获（防卸载：Texture2D 复制自持有） ====================

    /// <summary>从现场原生 UI 捕获名字匹配的 sprite 并**复制**为自持有副本（防场景卸载失效）。
    /// 用法：主菜单/提示框显示时调用（可多次，幂等去重）；匹配 pattern（如 "UI Box"）的 Image.sprite
    /// 会被复制（new Texture2D + SetPixels + 重建 Sprite 带 border/rect/pivot）缓存，之后 <see cref="NativeGet"/> 取用。
    /// 返回本次新捕获数。</summary>
    public static int CaptureFromScene(string namePattern)
    {
        if (string.IsNullOrEmpty(namePattern)) return 0;
        int captured = 0;
        try
        {
            var seen = new HashSet<string>();
            var images = UnityEngine.Object.FindObjectsOfType<Image>(true);
            if (images != null)
            {
                foreach (var img in images)
                {
                    if (img == null || img.sprite == null) continue;
                    var sp = img.sprite;
                    string nm;
                    try { nm = sp.name ?? ""; } catch { continue; }
                    if (nm.IndexOf(namePattern, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (_resCache.ContainsKey(nm) || !seen.Add(nm)) continue;
                    var copy = CopySprite(sp);
                    if (copy != null)
                    {
                        _resCache[nm] = copy;
                        captured++;
                        CoopLog.Info("UiSpriteBank.capture", () => $"captured '{nm}' ({copy.rect.width}x{copy.rect.height} border={copy.border})");
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            CoopLog.Warn("UiSpriteBank.capture", () => $"CaptureFromScene('{namePattern}'): {ex.Message}");
        }
        return captured;
    }

    /// <summary>把原生 Sprite 复制为独立副本（新 Texture2D + 新 Sprite，保留 rect/pivot/ppu/border），
    /// 不依赖原场景/资源生命周期（场景卸载后仍有效）。源纹理不可读（未开 Read/Write）时自动走
    /// Graphics.Blit → RenderTexture → ReadPixels（GPU 读回）。
    /// **border 统一保留作者值**：作者在 Sprite Editor 里精确设定了各 UI Box 的 9-slice 切片点
    /// （Castile 18,34,18,16 / star 18,11,7,18 / line 14,14,14,13…），这些值覆盖实际装饰/边框线位置，
    /// 是唯一正确的切片点。不能用 alpha bbox 覆盖（star 装饰集中在左上，bbox 会误判切片）。
    /// 边框"粗细"由渲染方 pixelsPerUnitMultiplier 控制，与切片点无关。</summary>
    public static Sprite CopySprite(Sprite src)
    {
        if (src == null) return null;
        try
        {
            var tex = src.texture;
            if (tex == null) return null;
            var rect = src.rect;
            int rw = (int)rect.width, rh = (int)rect.height;
            var copy = CopyTextureReadable(tex, rect);
            if (copy == null)
            {
                CoopLog.Warn("UiSpriteBank.copy", () => $"CopySprite('{src.name}'): 纹理复制失败（不可读且 GPU 读回也失败）");
                return null;
            }
            return Sprite.Create(copy, new Rect(0, 0, rw, rh), src.pivot, src.pixelsPerUnit, 0, SpriteMeshType.FullRect, src.border);
        }
        catch (System.Exception ex)
        {
            CoopLog.Warn("UiSpriteBank.copy", () => $"CopySprite('{src.name}'): {ex.Message}");
            return null;
        }
    }

    /// <summary>用 alpha 层解析 sprite 实际可见边框厚度（四边不透明像素包围盒到边缘的距离）。
    /// 阈值 alpha&gt;0.5（≈128）：避开抗锯齿半透明边缘。仅用于**尺寸计算**（按钮/输入框高度），
    /// 不作为 9-slice 切割值（切割仍用作者 border 防拉伸）。内容四角贴边（实心角框，如 Boxed Corners）
    /// 时无法用 bbox 推断，回退传入 fallback。</summary>
    public static Vector4 MeasureActualBorder(Sprite src)
    {
        if (src == null) return Vector4.zero;
        try
        {
            var tex = src.texture;
            if (tex == null) return Vector4.zero;
            var rect = src.rect;
            int w = (int)rect.width, h = (int)rect.height;
            var copy = CopyTextureReadable(tex, rect);
            if (copy == null) return Vector4.zero;
            var px = copy.GetPixels();
            const float THR = 0.5f; // 128/255
            int minX = w, minY = h, maxX = -1, maxY = -1;
            bool any = false;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (px[y * w + x].a > THR)
                    {
                        any = true;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (!any) return Vector4.zero;
            if (minX == 0 && minY == 0 && maxX == w - 1 && maxY == h - 1) return Vector4.zero;
            return new Vector4(minX, h - 1 - maxY, w - 1 - maxX, minY);
        }
        catch { return Vector4.zero; }
    }

    /// <summary>复制纹理的 (x,y,w,h) 子矩形为独立可读 Texture2D。优先 GetPixels（可读纹理）；
    /// 不可读时 Graphics.Blit → RenderTexture → ReadPixels（GPU 读回，不要求源 Read/Write）。</summary>
    private static Texture2D CopyTextureReadable(Texture2D src, Rect rect)
    {
        int rw = (int)rect.width, rh = (int)rect.height;
        // 1) 可读纹理：直接像素复制
        try
        {
            var t = new Texture2D(rw, rh, TextureFormat.RGBA32, false);
            t.SetPixels(src.GetPixels((int)rect.x, (int)rect.y, rw, rh));
            t.Apply();
            return t;
        }
        catch { }
        // 2) 不可读：GPU 读回（Blit 全图到临时 RT，再 ReadPixels 子矩形）
        try
        {
            int tw = src.width, th = src.height;
            if (tw <= 0 || th <= 0) return null;
            var rt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var t = new Texture2D(rw, rh, TextureFormat.RGBA32, false);
                t.ReadPixels(new Rect(rect.x, rect.y, rw, rh), 0, 0);
                t.Apply();
                return t;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
        catch { }
        return null;
    }

    /// <summary>按名取已捕获/Resources 的原生 sprite；缓存未命中则尝试 <see cref="FromResources"/> 兜底。
    /// "UI Box" 名首次取用时自动尝试 <see cref="CaptureFromScene"/>（幂等）——解决模组 UI 构建早于主菜单加载、
    /// 捕获还没发生的时序问题（取用即触发一次捕获，主菜单已加载时能拿到）。</summary>
    public static Sprite NativeGet(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_resCache.TryGetValue(name, out var s) && s != null) return s;
        if (!_uiBoxCaptureTried && name.IndexOf("UI Box", System.StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _uiBoxCaptureTried = true;
            CaptureFromScene("UI Box");
            if (_resCache.TryGetValue(name, out s) && s != null) return s;
        }
        return FromResources(name);
    }

    private static bool _uiBoxCaptureTried;
}
