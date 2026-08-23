using System;
using UnityEngine;
using UnityEngine.UI;
#if !MELONLOADER
using TMPro;
#else
using TMPro = Il2CppTMPro;
#endif
using OpenNestCoop.Core;

namespace OpenNestCoop.UI;

/// <summary>
/// 通用可复用输入框组件（UGUI）：自制 Button+Text 视觉 + 中文 IME 支持。
///
/// ⚠️ 普通类（非 MonoBehaviour）：IL2CPP 环境下给 mod 自定义类型用泛型 AddComponent&lt;T&gt;() 会崩
///    （MethodInfoStoreGeneric_AddComponent 初始化 NRE）——本类只向 GameObject 添加 Unity 类型组件，
///    UI 逻辑由按钮点击回调驱动（OnTap/OnSubmit/OnChanged），无需 Update/协程。
///
/// 封装内容：
/// - 视觉：背景 Image + Button + 文本子对象（掩码 / 占位 / 光标渲染）
/// - 状态：<see cref="Value"/>、<see cref="IsPassword"/>、<see cref="Placeholder"/>、<see cref="Focused"/>
/// - 交互：点击聚焦（登记为 <see cref="Active"/>）、回车提交（<see cref="OnSubmit"/>）
/// - 增删：<see cref="AppendText"/> / <see cref="BackspaceChar"/>（统一入口，同步 OnChanged + 渲染）
///
/// 输入机制（PollInput 物理键 + 原生 Win32 IME 真汉字 + compositionString 拼音兜底 + 首字符缓冲防闪烁）
/// 统一由 <c>CoopUIManager</c> 路由到当前 <see cref="Active"/> 输入框，本组件只负责状态与渲染。
/// 复用处：房间名、房间密码、加入密码弹窗。（聊天用真实 TMP_InputField，机制不同暂不迁移）
/// </summary>
public class CoopInputBox
{
    /// <summary>当前聚焦（活跃）输入框。CoopUIManager 的输入/IME 逻辑只路由到它。</summary>
    public static CoopInputBox Active;

    /// <summary>输入框种类标识（CoopUIManager 用它区分重建后恢复哪个框聚焦：1=房间名 2=密码 3=弹窗密码）。</summary>
    public int Kind;

    private string _value = "";
    /// <summary>文本值（密码框存明文，渲染时掩码 *）。</summary>
    public string Value { get => _value; set { _value = value ?? ""; Render(); } }

    /// <summary>是否密码框（渲染掩码）。</summary>
    public bool IsPassword;
    /// <summary>占位提示（空且未聚焦时显示）。</summary>
    public string Placeholder = "";
    /// <summary>当前是否聚焦（输入态）。</summary>
    public bool Focused;

    /// <summary>文本变化回调（外部同步数据 / 触发 UI 重建）。参数为最新 Value。</summary>
    public Action<string> OnChanged;
    /// <summary>回车提交回调（建房 / 弹窗确认）。</summary>
    public Action OnSubmit;

    private TextMeshProUGUI _txt;
    private RectTransform _rt;

    /// <summary>输入框 RectTransform（CoopUIManager 定位 IME 候选窗用）。</summary>
    public RectTransform Rect => _rt;

    /// <summary>在 parent 下创建输入框（布局同原 MakeInputBox：锚点左上、pivot 居中）。</summary>
    public static CoopInputBox Create(Transform parent, float x, float y, float w, float h,
        int kind, bool isPassword, string placeholder, Action<string> onChanged, Action onSubmit)
    {
        var box = new CoopInputBox();
        box.Kind = kind;
        box.IsPassword = isPassword;
        box.Placeholder = placeholder;
        box.OnChanged = onChanged;
        box.OnSubmit = onSubmit;

        var go = new GameObject("InputBox");
        go.transform.SetParent(parent, false);

        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x + w / 2f, -(y + h / 2f));
        rt.sizeDelta = new Vector2(w, h);
        box._rt = rt;

        var img = go.AddComponent<Image>();
        CoopUIManager.ApplySharedBoxBg(img);
        var btn = go.AddComponent<Button>();
        btn.onClick.AddListener(new Action(() => box.OnTap()));

        // 文本子对象（干净对象，TextMeshProUGUI 自动补 CanvasRenderer，同 MakeInputBox）
        var txtGo = new GameObject("Text");
        txtGo.transform.SetParent(go.transform, false);
        var txtRt = txtGo.AddComponent<RectTransform>();
        txtRt.anchorMin = Vector2.zero;
        txtRt.anchorMax = Vector2.one;
        txtRt.offsetMin = new Vector2(8f, 0f);
        txtRt.offsetMax = Vector2.zero;
        var txt = txtGo.AddComponent<TextMeshProUGUI>();
        txt.fontSize = 15;
        txt.color = new Color(0.92f, 0.95f, 1f);
        txt.alignment = TextAlignmentOptions.Left;
        txt.raycastTarget = false;
        txt.richText = true;
        CoopUIManager.ApplySharedFont(txt);
        box._txt = txt;
        box.Render();
        CoopRuntime.LogSource?.LogInfo($"[UI] InputBox kind={kind} created pos=({x:F0},{y:F0}) w={w:F0} h={h:F0} active={go.activeInHierarchy} parent={parent.name}");
        return box;
    }

    private void OnTap()
    {
        if (Focused) Unfocus(); else Focus();
    }

    /// <summary>聚焦：先失焦旧的，登记为 Active，通知 CoopUIManager 唤起 IME 锚点。</summary>
    public void Focus()
    {
        if (Active != null && Active != this) Active.Unfocus();
        Focused = true;
        Active = this;
        CoopUIManager.OnInputFocused(Kind);
        Render();
    }

    /// <summary>失焦：解除 Active，通知 CoopUIManager 收起 IME。</summary>
    public void Unfocus()
    {
        Focused = false;
        if (Active == this) Active = null;
        CoopUIManager.OnInputUnfocused();
        Render();
    }

    /// <summary>追加文本（英文 / 中文提交 / 组合捕获统一入口）→ 同步 OnChanged + 渲染。</summary>
    public void AppendText(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        if (Value.Length + s.Length > 40) s = s.Substring(0, Math.Max(0, 40 - Value.Length));
        if (s.Length == 0) return;
        Value += s;
        OnChanged?.Invoke(Value);
        Render();
    }

    /// <summary>删除末尾一个字符。</summary>
    public void BackspaceChar()
    {
        if (Value.Length == 0) return;
        Value = Value.Substring(0, Value.Length - 1);
        OnChanged?.Invoke(Value);
        Render();
    }

    /// <summary>提交（回车）→ 外部回调。</summary>
    public void Submit() => OnSubmit?.Invoke();

    /// <summary>渲染：掩码 / 占位 / 光标。聚焦空内容不显示占位（点击即清空）。</summary>
    public void Render()
    {
        if (_txt == null) return;
        string shown = Value;
        if (IsPassword && shown.Length > 0) shown = new string('*', shown.Length);
        if (shown.Length == 0) shown = Focused ? "" : Placeholder;
        if (Focused) shown += "|";
        _txt.text = shown;
    }
}
