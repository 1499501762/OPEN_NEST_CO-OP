using System;
using UnityEngine;
using UnityEngine.UI;

namespace OpenNestModMenu.UI;

/// <summary>
/// 自绘指针层（**兜底**，不是主方案）：菜单打开时在**我们画布的最上层**画一个跟随指针的箭头。
///
/// 只在「游戏没有活跃的虚拟光标画布 **且** 硬件光标被隐藏」时才会启用（见
/// <see cref="UiInputGuard.NeedsOwnPointer"/>）—— 其余情况游戏自己的指针层级必定在我们之上
/// （虚拟光标画布 32767 / 系统硬件光标），再自绘就成了“双指针”。
///
/// ⚠️ 历史上本文件曾是主方案（因为旧实现把游戏光标画布 +1，导致指针掉到菜单下面）。
/// 现在层级规则已改成联机模组同款（本模组固定 32766、不碰光标画布），指针不会再被菜单盖住。
/// 精灵直接从游戏虚拟光标上抄，拿不到时用纯色小方块兜底。
/// </summary>
public static class UiCursorOverlay
{
    private static RectTransform _rt;
    private static Image _img;
    private static Canvas _canvas;
    private static bool _logged;
    private static float _diag;

    public static void Show(Canvas canvas)
    {
        try
        {
            _canvas = canvas;

            // 主菜单时游戏可能还没有虚拟光标画布（拿不到 sprite）→ 每次打开都再试一次拿真精灵
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
            CoopLog.Warn("modmenu.ui", () => "cursor overlay show failed: " + ex.Message);
        }
    }

    public static void Hide()
    {
        try { if (_rt != null) _rt.gameObject.SetActive(false); } catch { }
    }

    /// <summary>每帧（LateUpdate）跟随鼠标。</summary>
    public static void Tick()
    {
        if (_rt == null) return;
        try
        {
            _diag += Time.unscaledDeltaTime;
            bool log = _diag > 2f;
            if (log) _diag = 0f;

            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null)
            {
                if (log) CoopLog.Debug("modmenu.ui", () => "cursor overlay: no mouse device");
                return;
            }
            Vector2 pos;
            // 与命中判定同一来源（虚拟光标优先），否则“看到的指针”与“实际命中点”会差开
            if (!PointerPosition.TryGet(out pos)) pos = mouse.position.ReadValue();

            var canvasRt = _canvas != null ? _canvas.GetComponent<RectTransform>() : null;
            if (canvasRt == null)
            {
                if (log) CoopLog.Debug("modmenu.ui", () => "cursor overlay: canvas rect missing");
                return;
            }

            Vector2 local;
            bool ok = UnityEngine.RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, pos, null, out local);

            int sib = _rt.GetSiblingIndex();
            int total = _rt.parent != null ? _rt.parent.childCount : 1;
            float w = _rt.sizeDelta.x, h = _rt.sizeDelta.y;
            string sp = _img != null && _img.sprite != null ? _img.sprite.name : "<fallback>";
            if (log)
            {
                Vector2 lp = local;
                CoopLog.Debug("modmenu.ui", () => $"cursor overlay: mouse={pos.x:F0},{pos.y:F0} local={lp.x:F0},{lp.y:F0} ok={ok} size={w:F0}x{h:F0} sibling={sib}/{total - 1} sprite={sp}");
            }

            if (!ok) return;
            _rt.anchoredPosition = local;
            _rt.SetAsLastSibling();   // 始终在本画布最上层
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.ui", () => "cursor overlay tick failed: " + ex.Message);
        }
    }

    private static void Build(Canvas canvas)
    {
        var sp = FindGameCursorSprite();

        _rt = UiKit.MakeRect("cursor", canvas.transform,
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
            CoopLog.Info("modmenu.ui", () => $"cursor overlay built (sprite={n})");
        }
    }

    /// <summary>从游戏的虚拟光标画布上抄一个 sprite（找不到返回 null → 用纯色兜底）。</summary>
    private static Sprite FindGameCursorSprite()
    {
        try
        {
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
            for (int i = 0; i < canvases.Length; i++)
            {
                var c = canvases[i];
                if (c == null || string.IsNullOrEmpty(c.name)) continue;
                if (c.name.IndexOf("cursor", StringComparison.OrdinalIgnoreCase) < 0) continue;

                var imgs = c.GetComponentsInChildren<Image>(true);
                for (int j = 0; j < imgs.Length; j++)
                {
                    if (imgs[j] != null && imgs[j].sprite != null) return imgs[j].sprite;
                }
            }
        }
        catch { }
        return null;
    }
}
