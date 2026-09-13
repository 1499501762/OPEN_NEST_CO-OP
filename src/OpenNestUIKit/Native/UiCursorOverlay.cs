using System;
using UnityEngine;
using UnityEngine.UI;

namespace OpenNestUIKit.Native;

/// <summary>
/// 自绘指针层（**兜底**，不是主方案）：菜单打开时在本库画布**最上层**画一个跟随指针的箭头。
///
/// 只在「游戏没有活跃的虚拟光标画布 **且** 硬件光标被隐藏」时启用（见 <see cref="UiInputGuard.NeedsOwnPointer"/>）——
/// 其余情况游戏自己的指针层级必定在我们之上（虚拟光标画布 32767 / 系统硬件光标），再自绘就成了"双指针"。
///
/// 与 <c>OpenNestModMenu.UI.UiCursorOverlay</c> 同法（那份已在双端实测）；差别：指针位置直接走桥接
/// （<see cref="UI.NativeUi.VirtualCursorPosition"/>），不需要反射。
/// </summary>
public static class UiCursorOverlay
{
    private static RectTransform _rt;
    private static Image _img;
    private static Canvas _canvas;
    private static bool _logged;

    /// <summary>显示并在每帧跟随指针。</summary>
    public static void Show(Canvas canvas)
    {
        try
        {
            _canvas = canvas;
            // 主菜单阶段可能还拿不到游戏指针精灵 → 每次打开都重试一次"抄真精灵"
            if (_rt != null && _img != null && _img.sprite == null)
            {
                try { UnityEngine.Object.Destroy(_rt.gameObject); } catch { }
                _rt = null; _img = null;
            }
            if (_rt == null) Build(canvas);
            if (_rt == null) return;
            _rt.gameObject.SetActive(true);
            Tick();
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.ui", () => "cursor overlay show failed: " + ex.Message);
        }
    }

    /// <summary>隐藏。</summary>
    public static void Hide()
    {
        try { if (_rt != null) _rt.gameObject.SetActive(false); } catch { }
    }

    /// <summary>每帧（LateUpdate）跟随指针。</summary>
    public static void Tick()
    {
        if (_rt == null || _canvas == null) return;
        try
        {
            Vector2 pos;
            if (!UiPointerRouter.TryGetPointer(out pos)) return;
            var canvasRt = _canvas.GetComponent<RectTransform>();
            if (canvasRt == null) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, pos, null, out var local)) return;
            _rt.anchoredPosition = local;
            _rt.SetAsLastSibling();   // 始终在本画布最上层
        }
        catch { }
    }

    private static void Build(Canvas canvas)
    {
        if (canvas == null) return;
        var sp = FindGameCursorSprite();

        _rt = UI.UiKit.MakeRect("cursor", canvas.transform,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, new Vector2(0f, 0f));
        _img = _rt.gameObject.AddComponent<Image>();
        _img.raycastTarget = false;

        if (sp != null)
        {
            _img.sprite = sp;
            _rt.sizeDelta = new Vector2(sp.rect.width, sp.rect.height);
        }
        else
        {
            _img.color = new Color(0.95f, 0.97f, 1f, 0.85f);   // 兜底：纯色小方块
            _rt.sizeDelta = new Vector2(14f, 20f);
        }
        _rt.SetAsLastSibling();

        if (!_logged)
        {
            _logged = true;
            string n = sp != null ? sp.name : "<none: fallback>";
            CoopLog.Info("uikit.ui", () => $"cursor overlay built (sprite={n})");
        }
    }

    /// <summary>从游戏虚拟光标画布上抄一个 sprite（找不到返回 null → 纯色兜底）。</summary>
    private static Sprite FindGameCursorSprite()
    {
        try
        {
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
            for (int i = 0; i < canvases.Length; i++)
            {
                var c = canvases[i];
                if (c == null) continue;
                if (string.IsNullOrEmpty(c.name) || c.name.IndexOf("cursor", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var imgs = c.GetComponentsInChildren<Image>(true);
                for (int k = 0; k < imgs.Length; k++)
                {
                    var im = imgs[k];
                    if (im != null && im.sprite != null) return im.sprite;
                }
            }
        }
        catch { }
        return null;
    }
}
