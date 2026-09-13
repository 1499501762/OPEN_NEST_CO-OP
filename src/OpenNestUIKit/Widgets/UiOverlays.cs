using System;
using UnityEngine;
using UnityEngine.UI;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif

namespace OpenNestUIKit.Widgets;

/// <summary>模态确认框（遮罩 + 面板 + 标题/正文/按钮组）。</summary>
public sealed class UiModal
{
    private RectTransform _root;
    private TextMeshProUGUI _body;
    private UiText _title;
    private bool _done;

    /// <summary>在 <paramref name="parent"/>（通常是菜单窗口内容区或画布）上弹一个模态框。</summary>
    public static UiModal Show(Transform parent, string title, string body,
        string confirmText, string cancelText, Action<bool> onResult)
    {
        if (parent == null) return null;
        var m = new UiModal();
        try
        {
            var root = UI.UiKit.MakeRectFill("modal", parent);
            m._root = root;

            var mask = root.gameObject.AddComponent<Image>();
            mask.color = new Color(0f, 0f, 0f, 0.55f);
            mask.raycastTarget = true;

            var panel = UI.UiKit.MakeRect("panel", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f));
            panel.sizeDelta = new Vector2(480f, 220f);
            panel.anchoredPosition = Vector2.zero;
            UiSurface.Build(panel, Theme.UiTheme.WindowBg, UiSurface.DialogRef());

            m._title = UiText.Create(panel, title, UiTextKind.Title, 0f, TextAlignmentOptions.Left);
            try
            {
                m._title.Rect.anchorMin = new Vector2(0f, 1f);
                m._title.Rect.anchorMax = new Vector2(1f, 1f);
                m._title.Rect.pivot = new Vector2(0f, 1f);
                m._title.Rect.offsetMin = new Vector2(18f, -46f);
                m._title.Rect.offsetMax = new Vector2(-18f, -14f);
                m._title.Rect.sizeDelta = new Vector2(-36f, 32f);
                m._title.Rect.anchoredPosition = Vector2.zero;
            }
            catch { }

            var bodyRt = UI.UiKit.MakeRect("body", panel, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(18f, -160f), new Vector2(-18f, -52f), new Vector2(0f, 1f));
            m._body = bodyRt.gameObject.AddComponent<TextMeshProUGUI>();
            m._body.text = body ?? "";
            m._body.fontSize = Theme.UiTheme.FontBody;
            m._body.color = Theme.UiTheme.TextSecondary;
            m._body.alignment = TextAlignmentOptions.TopLeft;
            m._body.raycastTarget = false;
            UI.UiKit.EnsureFont(m._body);

            var confirm = UiButton.Create(panel, string.IsNullOrEmpty(confirmText) ? UiKitLoc.T("确定", "OK") : confirmText,
                () => m.Finish(true, onResult), UiButtonStyle.Primary, 120f, Theme.UiTheme.ButtonH);
            try
            {
                confirm.Rect.anchorMin = confirm.Rect.anchorMax = new Vector2(1f, 0f);
                confirm.Rect.pivot = new Vector2(1f, 0f);
                confirm.Rect.anchoredPosition = new Vector2(-18f, 16f);
            }
            catch { }

            if (!string.IsNullOrEmpty(cancelText))
            {
                var cancel = UiButton.Create(panel, cancelText, () => m.Finish(false, onResult), UiButtonStyle.Secondary, 120f, Theme.UiTheme.ButtonH);
                try
                {
                    cancel.Rect.anchorMin = cancel.Rect.anchorMax = new Vector2(1f, 0f);
                    cancel.Rect.pivot = new Vector2(1f, 0f);
                    cancel.Rect.anchoredPosition = new Vector2(-148f, 16f);
                }
                catch { }
            }

            root.SetAsLastSibling();
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.widget", () => "modal show failed: " + ex.Message);
        }
        return m;
    }

    private void Finish(bool ok, Action<bool> onResult)
    {
        if (_done) return;
        _done = true;
        try { onResult?.Invoke(ok); } catch { }
        Close();
    }

    /// <summary>关闭（不触发回调）。</summary>
    public void Close()
    {
        try { if (_root != null) UnityEngine.Object.Destroy(_root.gameObject); } catch { }
        _root = null;
    }
}

/// <summary>
/// 悬浮提示（跟随指针的小标签）。
/// 用 <see cref="Bind"/> 挂到热区上（会自动**串联**该热区原有的进入/离开回调，不覆盖）。
/// </summary>
public static class UiTooltip
{
    private static RectTransform _root;
    private static TextMeshProUGUI _text;
    private static Canvas _canvas;
    private static Image _bg;

    /// <summary>初始化（由菜单窗口在建立画布后调用一次）。</summary>
    public static void Init(Canvas canvas)
    {
        if (canvas == null || _canvas == canvas) return;
        _canvas = canvas;
        try
        {
            _root = UI.UiKit.MakeRect("tooltip", canvas.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, Vector2.zero, new Vector2(0f, 1f));
            _bg = _root.gameObject.AddComponent<Image>();
            _bg.color = new Color(0.03f, 0.04f, 0.06f, 0.96f);
            _bg.raycastTarget = false;

            var txtRt = UI.UiKit.MakeRectFill("txt", _root);
            _text = txtRt.gameObject.AddComponent<TextMeshProUGUI>();
            _text.fontSize = Theme.UiTheme.FontSmall;
            _text.color = Theme.UiTheme.TextPrimary;
            _text.alignment = TextAlignmentOptions.TopLeft;
            _text.raycastTarget = false;
            try { _text.enableWordWrapping = false; } catch { }
            UI.UiKit.EnsureFont(_text);

            _root.sizeDelta = new Vector2(10f, 20f);
            _root.gameObject.SetActive(false);
        }
        catch (Exception ex) { CoopLog.Warn("uikit.widget", () => "tooltip init failed: " + ex.Message); }
    }

    /// <summary>把提示挂到热区（串联原有回调，不覆盖）。</summary>
    public static void Bind(Native.UiHotZone zone, Func<string> text)
    {
        if (zone == null || text == null) return;
        var prevEnter = zone.OnEnter;
        var prevExit = zone.OnExit;
        zone.OnEnter = () =>
        {
            try { prevEnter?.Invoke(); } catch { }
            try { Show(text()); } catch { }
        };
        zone.OnExit = () =>
        {
            try { prevExit?.Invoke(); } catch { }
            Hide();
        };
    }

    /// <summary>显示提示。</summary>
    public static void Show(string text)
    {
        if (_root == null || string.IsNullOrEmpty(text)) return;
        try
        {
            _text.text = text;
            float w = Mathf.Clamp(_text.preferredWidth + 16f, 60f, 420f);
            float h = Mathf.Max(22f, _text.preferredHeight + 8f);
            _root.sizeDelta = new Vector2(w, h);
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
            Tick(0f);
        }
        catch { }
    }

    /// <summary>隐藏。</summary>
    public static void Hide()
    {
        try { if (_root != null) _root.gameObject.SetActive(false); } catch { }
    }

    /// <summary>每帧跟随指针（由菜单窗口的 LateUpdate 调用）。</summary>
    public static void Tick(float dt)
    {
        if (_root == null || !_root.gameObject.activeSelf) return;
        try
        {
            Vector2 pos;
            if (!Native.UiPointerRouter.TryGetPointer(out pos)) return;
            var canvasRt = _canvas != null ? _canvas.GetComponent<RectTransform>() : null;
            if (canvasRt == null) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, pos, null, out var local)) return;
            // 指针右下 18/18（避免遮住指针）；越界时贴边
            float w = _root.sizeDelta.x, h = _root.sizeDelta.y;
            float x = Mathf.Min(local.x + 16f, canvasRt.rect.width * 0.5f - w);
            float y = Mathf.Max(local.y - 16f, -canvasRt.rect.height * 0.5f + h);
            _root.anchoredPosition = new Vector2(x, y);
        }
        catch { }
    }
}
