using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace OpenNestUIKit.Theme;

/// <summary>
/// 原生观感素材（"UI Box *" 系列装饰框）：**现场捕获 + 副本自持有**。
///
/// 为什么用"现场捕获"而不是 AssetBundle/Resources 按名加载（依据 `docs/NATIVE_UI.md` §6 的多轮结论）：
/// - 游戏 UI 是**静态场景/prefab UGUI**，主菜单框架素材（`UI Box Castile/line/Boxed Corners/Double line/partial star`、
///   `SGRounded` 等）在 `sharedassets0.assets`，`Resources.Load&lt;Sprite&gt;` 按名**恒为 null**（嵌套路径）；
/// - 但它们在主菜单加载后**已经在内存里**（场景 `Image.sprite` 引着）→ 扫现场 Image 拿引用最可靠；
/// - 场景卸载会让原生 Sprite 失效 → 因此**必须复制**（`new Texture2D + SetPixels + Sprite.Create` 带 border/pivot）
///   成自持有副本（`CopySprite`，纹理不可读时走 `Graphics.Blit` GPU 读回）。
///
/// ⚠️ 已知坑（`docs/NATIVE_UI.md` §6 + `native-ui-sprites.md`）：
/// - **边框粗细走 `pixelsPerUnitMultiplier`**：游戏原生用 `UI Box Castile` 的按钮/面板底
///   **都设 `pixelsPerUnitMultiplier = 2.5`**（实测，见 `UiTheme.SpritePpuMul`）→ 边框按 1/2.5 渲染。
///   我们照拄此值；需要更细时用 <see cref="NativeGetBorderScaled"/> 把 `border ÷ scale` **烤进副本**
///   （两者可叠加：ppuMul 管“边框渲染多大”，烤入管“切割线在哪”）；
/// - **`Image.pixelsPerUnit` 不是 sprite 原始 ppu**（= sprite.ppu / canvas.referencePixelsPerUnit，默认 1）；
/// - `UI Box Castile` 是 128px 装饰框（中心透明，作者用法 = Sliced 拉伸背景 + 少量 Tiled 细分隔线），
///   用它做按钮时要**保留实心填充底色**，否则中心透明看不见。
/// </summary>
public static class UiSkin
{
    private static readonly Dictionary<string, Sprite> _cache = new();
    private static readonly Dictionary<string, Sprite> _scaled = new();
    private static bool _captureTried;
    private static bool _logged;

    /// <summary>是否启用原生观感（关闭则全部退回纯色；演示页可切换对比）。</summary>
    public static bool UseNative { get; set; } = false;

    /// <summary>已捕获的原生素材数。</summary>
    public static int Count { get { lock (_cache) return _cache.Count; } }

    /// <summary>素材发生变化（捕获成功/清空）→ 界面可重建。</summary>
    public static event Action Changed;

    /// <summary>是否已拿到任何原生观感素材（决定面板/按钮走 9-slice 还是纯色）。</summary>
    public static bool Available
    {
        get
        {
            if (!UseNative) return false;
            EnsureCapture();
            lock (_cache) return _cache.Count > 0;
        }
    }

    /// <summary>面板底图（窗口/卡片/行）：Castile 优先，退 SGRounded，再退任意 "UI Box"。</summary>
    public static Sprite PanelSprite => Resolve("UI Box Castile", "SGRounded", "SUGGradientRounded", "UI Box");

    /// <summary>按钮底图（默认 = 次按钮）：按**尺寸自适应**挑素材，见 <see cref="ButtonSpriteFor"/>。</summary>
    public static Sprite ButtonSprite => ButtonSpriteFor(false, false, 120f, 32f);

    /// <summary>分隔线（细装饰）：line / Double line。</summary>
    public static Sprite LineSprite => Resolve("UI Box line", "UI Box Double line", "UI box partial star");

    /// <summary>提示框/弹框底图：Boxed Corners / Double line / Castile。</summary>
    public static Sprite DialogSprite => Resolve("UI Box Boxed Corners", "UI Box Double line", "UI Box Castile", "UI Box");

    // ---------------- 切片定义（用户/工具给出的"正确切片"，优先级最高） ----------------

    /// <summary>面板引用（含切片模式与是否填中心）：优先采用工具里标记 `panel` 的素材。</summary>
    public static UiSpriteRef PanelRef() => RefForTagOr("panel", "UI Box Castile", "SGRounded", "SUGGradientRounded", "UI Box");

    /// <summary>分隔线引用：优先标记 `separator`。</summary>
    public static UiSpriteRef LineRef() => RefForTagOr("separator", "UI Box line", "UI Box Double line", "UI box partial star");

    /// <summary>弹框/提示框引用：优先标记 `dialog`。</summary>
    public static UiSpriteRef DialogRef() => RefForTagOr("dialog", "UI Box Boxed Corners", "UI Box Double line", "UI Box Castile", "UI Box");

    /// <summary>
    /// 按 tag 取素材引用（**工具里的人工标记**：这张图是哪种组件的背景）。
    /// 精确 tag 优先，其次前缀（`button` → `button.primary`）；找不到返回空引用。
    /// </summary>
    public static UiSpriteRef RefForTag(string tag)
    {
        if (!UseNative || string.IsNullOrEmpty(tag)) return default;
        var names = UiSliceStore.ByTag(tag);
        for (int i = 0; i < names.Length; i++)
        {
            var r = RefFor(names[i]);
            if (!r.IsEmpty) return r;
        }
        return default;
    }

    /// <summary>先按 tag，找不到再按候选名。</summary>
    public static UiSpriteRef RefForTagOr(string tag, params string[] candidates)
    {
        var r = RefForTag(tag);
        return !r.IsEmpty ? r : RefFor(candidates);
    }

    /// <summary>按钮候选：**先放工具里打了 tag 的素材**（danger→`button.danger`/`button`；primary→`button.primary`/`button`；否则 `button.secondary`/`button`），再接风格候选名。</summary>
    private static string[] ButtonCandidates(bool primary, bool danger)
    {
        string[] baseNames = danger
            ? new[] { "UI box partial star", "UI Box Boxed Corners", "UI Box line" }
            : primary
                ? new[] { "UI Box Castile", "UI Box Double line", "UI Box line" }
                : new[] { "UI Box line", "UI Box Double line", "UI Box Castile" };

        string[] tags = danger ? new[] { "button.danger", "button" }
            : primary ? new[] { "button.primary", "button" }
                      : new[] { "button.secondary", "button" };

        var tagged = new List<string>();
        for (int t = 0; t < tags.Length; t++)
        {
            var names = UiSliceStore.ByTag(tags[t]);
            for (int i = 0; i < names.Length; i++)
                if (!tagged.Contains(names[i])) tagged.Add(names[i]);
        }
        if (tagged.Count == 0) return baseNames;

        var all = new string[tagged.Count + baseNames.Length];
        tagged.CopyTo(all, 0);
        Array.Copy(baseNames, 0, all, tagged.Count, baseNames.Length);
        return all;
    }

    /// <summary>
    /// 按钮引用：**tag 标记/已定义的素材优先（前提是“放得下”）**，放不下则该尺寸退回后续候选；
    /// 若全部都不放得下，仍用第一个有定义的（你的 border 原样用，日志会标 ⚠）。
    /// </summary>
    public static UiSpriteRef ButtonRefFor(bool primary, bool danger, float w, float h)
    {
        if (!UseNative) return default;
        string[] candidates = ButtonCandidates(primary, danger);

        // 0a) 用户切片（首选）：有定义 **且放得下** 就直接用（数值原样，不再自动缩放）
        //     放不下的**先记下作兜底**，继续看后面的候选（可能有一张恰好放得下）
        string fallbackName = null;
        UiSlice fallbackSlice = default;
        for (int i = 0; i < candidates.Length; i++)
        {
            var slice = UiSliceStore.Get(candidates[i]);
            if (!slice.HasValue) continue;
            if (slice.Mode == UiSliceMode.None) return default;
            var baked = Bake(candidates[i], slice.BakedBorder);
            if (baked == null) continue;
            if (slice.Fits(w, h))
            {
                CoopLog.Info("uikit.skin", () => $"按钮素材 = '{candidates[i]}'（用户切片；{slice}；目标 {w:0}x{h:0}）");
                return new UiSpriteRef { Sprite = baked, Type = SliceType(slice), FillCenter = slice.FillCenter, Name = candidates[i], FromUserSlice = true };
            }
            if (fallbackName == null) { fallbackName = candidates[i]; fallbackSlice = slice; }
        }

        // 0b) 都不放得下 → 仍用第一个有定义的（保持“定义优先”，但你的 border 会被 Unity 压缩）
        if (fallbackName != null)
        {
            var baked = Bake(fallbackName, fallbackSlice.BakedBorder);
            if (baked != null)
            {
                NoteSkip(fallbackName, w, h, fallbackSlice);
                return new UiSpriteRef { Sprite = baked, Type = SliceType(fallbackSlice), FillCenter = fallbackSlice.FillCenter, Name = fallbackName, FromUserSlice = true };
            }
        }

        var s = PickForSize(candidates, w, h);
        return s == null ? default : new UiSpriteRef { Sprite = s, Type = Image.Type.Sliced, FillCenter = true, Name = s.name };
    }

    /// <summary>切片定义 → 绘制模式。</summary>
    private static Image.Type SliceType(UiSlice slice)
        => slice.Mode == UiSliceMode.Tiled ? Image.Type.Tiled
         : slice.Mode == UiSliceMode.Simple ? Image.Type.Simple : Image.Type.Sliced;

    /// <summary>记一笔“因为放不下而跳过了你的切片”（按 素材+尺寸 去重，便于查“为何小按钮没用我标的那张”）。</summary>
    private static void NoteSkip(string name, float w, float h, UiSlice slice)
    {
        try
        {
            string key = "skip:" + name + "@" + Mathf.RoundToInt(w) + "x" + Mathf.RoundToInt(h);
            lock (_chosen)
            {
                if (!_chosen.Add(key)) return;
            }
            CoopLog.Info("uikit.skin", () => $"按钮素材 = '{name}'（用户切片 border={slice.BakedBorder} 放不下 {w:0}x{h:0}，仍按定义使用 → 会被 Unity 压缩）");
        }
        catch { }
    }

    /// <summary>
    /// 按名取素材引用：**先查切片定义**（有就直接烘焙采用，不再自动缩放/退纯色），
    /// 否则用作者原值（Sliced + 填中心）。
    /// </summary>
    public static UiSpriteRef RefFor(string name)
    {
        if (!UseNative || string.IsNullOrEmpty(name)) return default;
        var slice = UiSliceStore.Get(name);
        if (slice.HasValue)
        {
            if (slice.Mode == UiSliceMode.None) return default;
            var baked = Bake(name, slice.BakedBorder);
            if (baked == null) return default;
            return new UiSpriteRef
            {
                Sprite = baked,
                Type = slice.Mode == UiSliceMode.Tiled ? Image.Type.Tiled
                     : slice.Mode == UiSliceMode.Simple ? Image.Type.Simple : Image.Type.Sliced,
                FillCenter = slice.FillCenter,
                Name = name,
                FromUserSlice = true,
            };
        }
        var s = NativeGet(name);
        return s == null ? default : new UiSpriteRef { Sprite = s, Type = Image.Type.Sliced, FillCenter = true, Name = s.name };
    }

    /// <summary>按名取素材引用（候选名依次尝试，命中第一个）。</summary>
    public static UiSpriteRef RefFor(params string[] candidates)
    {
        if (candidates == null) return default;
        for (int i = 0; i < candidates.Length; i++)
        {
            var r = RefFor(candidates[i]);
            if (!r.IsEmpty) return r;
        }
        return default;
    }

    /// <summary>已捕获素材名（切片探针/外部工具遍历用）。</summary>
    public static string[] Names
    {
        get
        {
            EnsureCapture();
            lock (_cache)
            {
                var list = new List<string>(_cache.Count);
                foreach (var kv in _cache) list.Add(kv.Key);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                return list.ToArray();
            }
        }
    }

    /// <summary>作者原 border（工具 `--reset-author` 对照用；未捕获返回 zero）。</summary>
    public static Vector4 AuthorBorder(string name)
    {
        var s = NativeGet(name);
        if (s == null) return Vector4.zero;
        try { return s.border; } catch { return Vector4.zero; }
    }

    // ---------------- 按尺寸挑选（九宫格"放不下"= 看起来像平铺/被压实的根因） ----------------

    /// <summary>sprite 九宫格切片边框合计（x = 横向 left+right，y = 纵向 top+bottom）。border 全 0 = 非九宫格素材。</summary>
    public static Vector2 BorderSum(Sprite s)
    {
        if (s == null) return Vector2.zero;
        try { var b = s.border; return new Vector2(b.x + b.z, b.y + b.w); } catch { return Vector2.zero; }
    }

    /// <summary>
    /// 按**尺寸**挑按钮素材（修"按钮背景看着像平铺"）：
    /// 九宫格要求 `border 合计 + 留白 ≤ 控件尺寸`，否则 Unity 的 `Image.GetAdjustedBorders` 会把整张图
    /// 按最小比例**整体压缩**进控件（128px 的 `UI Box Castile` 纵向边框合计 50px 塞进 32px 高按钮 →
    /// 城堡/基座装饰被压成一团，看起来就像图案被重复压实）。规则：
    /// 1) 候选里挑第一个"横/纵向边框都放得下"的（留 8px 余量）；
    /// 2) 都放不下 → 把第一候选的 `border ÷ scale` **烤进副本**（`docs/NATIVE_UI.md` §6：不用
    ///    `pixelsPerUnitMultiplier` 照拄原生（2.5，见 `UiTheme.SpritePpuMul`）；
    /// 3) 仍不达标 → 返回 null（纯色，观感干净，绝不硬塞图案）。
    /// 每种按钮风格给**不同候选**（主按钮偏重框、次按钮偏细线、危险按钮偏角标），
    /// 解决"多个按钮背景只有一个样式"。
    /// 候选顺序里会**先放你打了 tag 的素材**（例 `button.secondary`），但它只在**放得下**时优先；
    /// 放不下的小尺寸会退回后续候选（或把 border ÷ scale 烤进副本），避免把大图案压进小控件。
    /// </summary>
    public static Sprite ButtonSpriteFor(bool primary, bool danger, float w, float h)
    {
        if (!UseNative) return null;
        return PickForSize(ButtonCandidates(primary, danger), w, h);
    }

    /// <summary>
    /// 按尺寸挑素材（规则见 <see cref="ButtonSpriteFor"/>）：
    /// 0a) **你的切片定义（含 tag 选出的）只要放得下就直接用**（border 原值，不缩放不替换）；
    /// 0b) 都不放得下 → 仍用第一个有定义的（定义优先，但会被 Unity 压缩，日志标 ⚠）；
    /// 1) 挑第一个“原样放得下”的（留 8px 余量）；2) 烤入 `border ÷ scale`；3) 纯色。
    /// </summary>
    public static Sprite PickForSize(string[] candidates, float w, float h)
    {
        if (candidates == null || candidates.Length == 0) return null;

        // 0a) 用户切片（首选）：有定义 **且放得下** 就直接用（数值原样）
        string fallbackName = null;
        UiSlice fallbackSlice = default;
        for (int i = 0; i < candidates.Length; i++)
        {
            var slice = UiSliceStore.Get(candidates[i]);
            if (!slice.HasValue) continue;
            if (slice.Mode == UiSliceMode.None) { Note(null, candidates[i], w, h, "用户切片指定 mode=none"); return null; }
            var bakedUser = Bake(candidates[i], slice.BakedBorder);
            if (bakedUser == null) continue;
            if (slice.Fits(w, h)) return Note(bakedUser, candidates[i], w, h, "用户切片");
            if (fallbackName == null) { fallbackName = candidates[i]; fallbackSlice = slice; }
        }

        // 0b) 都不放得下 → 仍用第一个有定义的（定义优先；日志标明会被压缩）
        if (fallbackName != null)
        {
            var bakedFallback = Bake(fallbackName, fallbackSlice.BakedBorder);
            if (bakedFallback != null)
                return Note(bakedFallback, fallbackName, w, h, "用户切片（border 放不下，会被压缩）");
        }

        // 1) 原样可用
        for (int i = 0; i < candidates.Length; i++)
        {
            var s = Resolve(candidates[i]);
            if (s == null) continue;
            var bs = BorderSum(s);
            if (bs.x + 8f <= w && bs.y + 8f <= h) return Note(s, candidates[i], w, h, "原样");
        }

        // 2) 第一候选烤入缩放（border ÷ scale）
        var first = Resolve(candidates[0]);
        if (first != null)
        {
            var bs = BorderSum(first);
            float need = Mathf.Max(bs.x / Mathf.Max(1f, w - 8f), bs.y / Mathf.Max(1f, h - 8f));
            float scale = Mathf.Max(1f, Mathf.Ceil(need));
            var scaled = NativeGetBorderScaled(candidates[0], scale);
            if (scaled != null)
            {
                var bs2 = BorderSum(scaled);
                if (bs2.x + 8f <= w && bs2.y + 8f <= h)
                    return Note(scaled, candidates[0] + "÷" + scale.ToString("0"), w, h, "border 烤入缩放");
            }
        }

        // 3) 纯色兜底
        Note(null, candidates[0], w, h, $"尺寸放不下（{candidates[0]} 边框合计 {BorderSum(first).x:0}/{BorderSum(first).y:0}）");
        return null;
    }

    private static readonly HashSet<string> _chosen = new();
    private static readonly Dictionary<string, Sprite> _baked = new();
    private static bool _baking;

    /// <summary>
    /// 按指定 border 烘焙一张副本（保留原图/ pivot / ppu，只换九宫格切割线）。
    /// 用途：采用切片定义（`border ÷ scale`）或外部切片工具预览。带缓存（同名同 border 只做一次）。
    /// </summary>
    public static Sprite Bake(string name, Vector4 border)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_baking) return null;                 // 重入保护（防任何形式的相互递归）
        var src = Cached(name);                   // ⚠ 只查缓存，不要调 NativeGet（会与 Resolve 相互递归）
        if (src == null) return null;

        string key = name + "@" + border.x.ToString("0.#") + "," + border.y.ToString("0.#") + "," + border.z.ToString("0.#") + "," + border.w.ToString("0.#");
        lock (_baked)
            if (_baked.TryGetValue(key, out var cached) && cached != null) return cached;

        _baking = true;
        try
        {
            var rect = src.rect;
            int rw = (int)rect.width, rh = (int)rect.height;
            if (rw <= 0 || rh <= 0) return src;
            var tex = CopyTextureReadable(src.texture, rect);
            if (tex == null) return src;
            var sp = Sprite.Create(tex, new Rect(0, 0, rw, rh), src.pivot, src.pixelsPerUnit, 0, SpriteMeshType.FullRect, border);
            if (sp == null) return src;
            try { sp.name = key; } catch { }
            lock (_baked) _baked[key] = sp;
            CoopLog.Debug("uikit.skin", () => $"烘焙切片副本 '{name}' border={border}");
            return sp;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.skin", () => $"Bake('{name}'): {ex.Message}");
            return src;
        }
        finally
        {
            _baking = false;
        }
    }

    /// <summary>选中记录（按 素材+目标尺寸 去重打印一次，便于取证"到底用了哪张图"）。</summary>
    private static Sprite Note(Sprite s, string name, float w, float h, string how)
    {
        try
        {
            string key = name + "@" + Mathf.RoundToInt(w) + "x" + Mathf.RoundToInt(h);
            lock (_chosen)
            {
                if (!_chosen.Add(key)) return s;
            }
            var b = s != null ? s.border : Vector4.zero;
            CoopLog.Info("uikit.skin", () => s != null
                ? $"按钮素材 = '{s.name}'（{how}；border={b}；目标 {w:0}x{h:0}）"
                : $"按钮素材 = 纯色（{how}；目标 {w:0}x{h:0}）");
        }
        catch { }
        return s;
    }

    /// <summary>
    /// 现场捕获名字含 <paramref name="namePattern"/>（忽略大小写）的 sprite 并复制为自持有副本（幂等）。
    /// 返回本次新捕获数。**建议在"主菜单已加载"之后调用**（那时原生 UI 才在场景里）。
    /// </summary>
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
                for (int i = 0; i < images.Length; i++)
                {
                    var img = images[i];
                    if (img == null || img.sprite == null) continue;
                    var sp = img.sprite;
                    string nm;
                    try { nm = sp.name ?? ""; } catch { continue; }
                    if (nm.Length == 0) continue;
                    if (nm.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (Has(nm) || !seen.Add(nm)) continue;
                    var copy = CopySprite(sp);
                    if (copy != null)
                    {
                        lock (_cache) _cache[nm] = copy;
                        captured++;
                        CoopLog.Info("uikit.skin", () => $"captured '{nm}' ({copy.rect.width}x{copy.rect.height} border={copy.border})");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.skin", () => $"CaptureFromScene('{namePattern}'): {ex.Message}");
        }
        if (captured > 0) { try { Changed?.Invoke(); } catch { } }
        return captured;
    }

    /// <summary>
    /// 按**切片定义里出现过的名字**补捕获（工具/手写 ini 里定义过的图必须能取到，否则定义等于没用）。
    /// 与 <see cref="CaptureFromScene"/> 同法，但是**精确名字集合**匹配（不限 “UI Box” 这些系列）。
    /// </summary>
    public static int CaptureDefinedSlices()
    {
        var names = UiSliceStore.Names;
        if (names == null || names.Length == 0) return 0;
        var want = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        int captured = 0;
        try
        {
            var seen = new HashSet<string>();
            var images = UnityEngine.Object.FindObjectsOfType<Image>(true);
            if (images != null)
            {
                for (int i = 0; i < images.Length; i++)
                {
                    var img = images[i];
                    if (img == null || img.sprite == null) continue;
                    var sp = img.sprite;
                    string nm;
                    try { nm = sp.name ?? ""; } catch { continue; }
                    if (nm.Length == 0 || !want.Contains(nm)) continue;
                    if (Has(nm) || !seen.Add(nm)) continue;
                    var copy = CopySprite(sp);
                    if (copy != null)
                    {
                        lock (_cache) _cache[nm] = copy;
                        captured++;
                        CoopLog.Info("uikit.skin", () => $"captured(by slice def) '{nm}' ({copy.rect.width}x{copy.rect.height} border={copy.border})");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.skin", () => $"CaptureDefinedSlices: {ex.Message}");
        }

        // ② 场景里没有 Image 用到它，但资源**已经在内存里**（图集/prefab/Resources，如城堡框只在别的界面用）
        //    → 用 Resources.FindObjectsOfTypeAll<Sprite>() 按名直接找（比扫 Image 广）
        try
        {
            var all = Resources.FindObjectsOfTypeAll<Sprite>();
            if (all != null)
            {
                var seen2 = new HashSet<string>();
                for (int i = 0; i < all.Length; i++)
                {
                    var sp = all[i];
                    if (sp == null) continue;
                    string nm;
                    try { nm = sp.name ?? ""; } catch { continue; }
                    if (nm.Length == 0 || !want.Contains(nm)) continue;
                    if (Has(nm) || !seen2.Add(nm)) continue;
                    var copy = CopySprite(sp);
                    if (copy == null) continue;
                    lock (_cache) _cache[nm] = copy;
                    captured++;
                    CoopLog.Info("uikit.skin", () => $"captured(by slice def/内存) '{nm}' ({copy.rect.width}x{copy.rect.height} border={copy.border})");
                }
            }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.skin", () => $"CaptureDefinedSlices(内存): {ex.Message}");
        }

        if (captured > 0) { try { Changed?.Invoke(); } catch { } }
        return captured;
    }

    /// <summary>一次性捕获全部已知原生 UI 系列（幂等；主菜单加载后调用一次即可）。</summary>
    public static int CaptureAll()
    {
        int n = 0;
        n += CaptureFromScene("UI Box");
        n += CaptureFromScene("UI box");
        n += CaptureFromScene("SGRounded");
        n += CaptureFromScene("SUGGradientRounded");
        n += CaptureFromScene("BaseFrame");
        n += CaptureFromScene("TitleBorder");
        n += CaptureDefinedSlices();   // 工具里切过/标记过的图（名字可能不在上面这些系列里）
        return n;
    }

    /// <summary>按名取已捕获素材（找不到返回 null）。</summary>
    public static Sprite NativeGet(string name)
    {
        var s = Cached(name);
        if (s != null) return s;
        return Resolve(name);
    }

    /// <summary>
    /// **只查已捕获缓存**（精确 → 宽松包含），不做“用户切片优先”也不烘焙。
    /// ⚠ 为什么要单独一个：`Bake` 需要拿原图，而“用户切片优先”的 <see cref="Resolve"/> 又会调 `Bake`
    /// —— 若 `Bake` 直接调 `NativeGet`，遇到“ini 里定义了、但场景里没捕获到”的图就会
    /// `Bake → NativeGet → Resolve → Bake → …` **无限递归栈溢出**（实测崩游戏）。
    /// </summary>
    private static Sprite Cached(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        EnsureCapture();
        lock (_cache)
        {
            if (_cache.TryGetValue(name, out var s) && s != null) return s;
            // 只做“正向”宽松匹配（缓存名包含请求名），例如请求 "UI Box" → "UI Box line"。
            // ⚠ 不做反向（请求名包含缓存名）：否则 "InputFieldBackground" 会被 "Background" 误匹配，
            //    拿到完全不相干的素材（实测踩过）。
            foreach (var kv in _cache)
            {
                if (kv.Value == null) continue;
                if (kv.Key.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return kv.Value;
            }
        }
        return null;
    }

    /// <summary>
    /// 按名取素材，并把 `border ÷ scale` **烤进新副本**（用于把 9-slice 边框做细：
    /// Castile 原 border(18,34,18,16) ÷ 2 = (9,17,9,8)）。
    /// ⚠️ `pixelsPerUnitMultiplier` 与“烤入缩放”作用不同：前者管**边框渲染多大**（照拄原生 2.5），
    /// 本方法管**切割线在哪**；两者可叠加。
    /// </summary>
    public static Sprite NativeGetBorderScaled(string name, float scale)
    {
        if (scale <= 0f) scale = 1f;
        if (Mathf.Approximately(scale, 1f)) return NativeGet(name);
        string key = (name ?? "") + "#" + scale.ToString("0.##");
        lock (_scaled)
        {
            if (_scaled.TryGetValue(key, out var cached) && cached != null) return cached;
        }
        var src = NativeGet(name);
        if (src == null) return null;
        try
        {
            var b = src.border;
            var nb = new Vector4(Mathf.Floor(b.x / scale), Mathf.Floor(b.y / scale), Mathf.Floor(b.z / scale), Mathf.Floor(b.w / scale));
            var tex = CopyTextureReadable(src.texture, src.rect);
            if (tex == null) return src;
            var copy = Sprite.Create(tex, new Rect(0, 0, src.rect.width, src.rect.height), src.pivot, src.pixelsPerUnit, 0, SpriteMeshType.FullRect, nb);
            if (copy != null)
            {
                try { copy.name = key; } catch { }
                lock (_scaled) _scaled[key] = copy;
                CoopLog.Info("uikit.skin", () => $"border-scaled '{name}' ×1/{scale:0.##} border={b} → {nb}");
                return copy;
            }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.skin", () => $"NativeGetBorderScaled('{name}', {scale}): {ex.Message}");
        }
        return src;
    }

    /// <summary>把一个原生 Sprite 复制为独立副本（保留 rect/pivot/ppu/border）。源纹理不可读时走 GPU 读回。</summary>
    public static Sprite CopySprite(Sprite src)
    {
        if (src == null) return null;
        try
        {
            var tex = src.texture;
            if (tex == null) return null;
            var rect = src.rect;
            int rw = (int)rect.width, rh = (int)rect.height;
            if (rw <= 0 || rh <= 0) return null;
            var copy = CopyTextureReadable(tex, rect);
            if (copy == null)
            {
                CoopLog.Warn("uikit.skin", () => $"CopySprite('{src.name}'): 纹理复制失败（不可读且 GPU 读回也失败）");
                return null;
            }
            var sp = Sprite.Create(copy, new Rect(0, 0, rw, rh), src.pivot, src.pixelsPerUnit, 0, SpriteMeshType.FullRect, src.border);
            // ⚠️ Sprite.Create 出来的副本 name 是空的 → 必须继承原名，
            //    否则后续 NativeGetBorderScaled(first.name, …) 按名找不到（会静默退化为纯色）。
            if (sp != null) { try { sp.name = src.name; } catch { } }
            return sp;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.skin", () => $"CopySprite('{src.name}'): {ex.Message}");
            return null;
        }
    }

    /// <summary>切片定义变化时调用：只让"烘焙副本/选材记录"失效（捕获的原图保留）。</summary>
    public static void InvalidateBaked()
    {
        lock (_baked) _baked.Clear();
        lock (_scaled) _scaled.Clear();
        lock (_chosen) _chosen.Clear();
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>清空缓存（诊断/重捕获用；切片定义不变）。</summary>
    public static void Clear()
    {
        lock (_cache) _cache.Clear();
        lock (_scaled) _scaled.Clear();
        lock (_baked) _baked.Clear();
        lock (_chosen) _chosen.Clear();
        _captureTried = false;
        _logged = false;
        try { Changed?.Invoke(); } catch { }
    }

    // ---------------- 内部 ----------------

    /// <summary>首次取用时自动尝试捕获一次（解决"UI 构建早于主菜单加载"的时序问题；幂等）。</summary>
    private static void EnsureCapture()
    {
        if (_captureTried) return;
        _captureTried = true;
        if (NativeUi.Available && !NativeUi.IsMainMenu)
        {
            // 不在主菜单：原生主菜单素材不在场景里，等主菜单加载后再捕获（由 NativeMenuInjector 触发）
            CoopLog.Debug("uikit.skin", () => "capture 延后（当前不在主菜单）");
            _captureTried = false;
            return;
        }
        int n = CaptureAll();
        if (!_logged)
        {
            _logged = true;
            int cached = Count;
            CoopLog.Info("uikit.skin", () => $"capture 完成：本次新捕获 {n} 个，缓存 {cached} 个；{(cached > 0 ? "面板/按钮走原生 9-slice" : "退化为纯色")}");
        }
    }

    private static bool Has(string name)
    {
        lock (_cache) return _cache.ContainsKey(name);
    }

    /// <summary>按候选名依次精确/宽松查找（返回第一个命中的缓存副本）。**用户切片优先**。</summary>
    private static Sprite Resolve(params string[] candidates)
    {
        if (!UseNative) return null;

        // 0) 用户切片（权威）：候选里第一个“**已捕获**且有定义”的直接用
        //    ⚠ 未捕获就直接跳过（否则 Bake 拓不到原图，还会递归）
        for (int i = 0; i < candidates.Length; i++)
        {
            var slice = UiSliceStore.Get(candidates[i]);
            if (!slice.HasValue) continue;
            if (slice.Mode == UiSliceMode.None) return null;
            if (Cached(candidates[i]) == null) continue;      // 定义在、但没捕到这张图 → 当没用
            var baked = Bake(candidates[i], slice.BakedBorder);
            if (baked != null) return baked;
        }

        EnsureCapture();
        lock (_cache)
        {
            if (_cache.Count == 0) return null;
            // 1) 精确名
            for (int i = 0; i < candidates.Length; i++)
                if (_cache.TryGetValue(candidates[i], out var s) && s != null) return s;
            // 2) 包含匹配（按候选顺序优先）
            for (int i = 0; i < candidates.Length; i++)
            {
                string want = candidates[i] ?? "";
                if (want.Length == 0) continue;
                foreach (var kv in _cache)
                {
                    if (kv.Value == null) continue;
                    if (kv.Key.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) return kv.Value;
                }
            }
        }
        return null;
    }

    /// <summary>复制纹理的 (x,y,w,h) 子矩形为独立可读 Texture2D（GetPixels → 失败则 GPU Blit 读回）。</summary>
    private static Texture2D CopyTextureReadable(Texture2D src, Rect rect)
    {
        if (src == null) return null;
        int rw = (int)rect.width, rh = (int)rect.height;
        try
        {
            var t = new Texture2D(rw, rh, TextureFormat.RGBA32, false);
            t.SetPixels(src.GetPixels((int)rect.x, (int)rect.y, rw, rh));
            t.Apply();
            return t;
        }
        catch { }
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
}

/// <summary>
/// 素材引用：**一张图 + 怎么画**（sprite + <c>Image.Type</c> + 是否填中心 + 是否来自用户切片）。
///
/// 为什么要带模式：九宫格不是唯一画法 —— `Image.Type.Tiled`（平铺）与 `Simple`（整张拉伸）
/// 对不同素材才正确，而 `fillCenter=false`（只画边框、中心留空）是 UI 框类素材的常见需求。
/// 这些都由**切片定义**（`OpenNestUIKit.slices.ini`）决定，组件只负责照做。
/// </summary>
public struct UiSpriteRef
{
    /// <summary>图（null = 用纯色）。</summary>
    public Sprite Sprite;

    /// <summary>绘制模式。</summary>
    public Image.Type Type;

    /// <summary>九宫格/Tiled 时是否绘制中心。</summary>
    public bool FillCenter;

    /// <summary>素材名（诊断）。</summary>
    public string Name;

    /// <summary>是否来自用户切片定义。</summary>
    public bool FromUserSlice;

    /// <summary>是否为空（无图）。</summary>
    public bool IsEmpty => Sprite == null;

    /// <summary>从普通 sprite 构造（默认 Sliced + 填中心）。</summary>
    public static UiSpriteRef From(Sprite s)
        => s == null ? default : new UiSpriteRef { Sprite = s, Type = Image.Type.Sliced, FillCenter = true, Name = s.name };

    public override string ToString()
        => IsEmpty ? "<纯色>" : $"{Name} type={Type} center={(FillCenter ? 1 : 0)}{(FromUserSlice ? " (用户切片)" : "")}";
}
