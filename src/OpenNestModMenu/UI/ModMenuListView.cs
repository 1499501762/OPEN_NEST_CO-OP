using System;
using System.Collections.Generic;
using OpenNestModMenu.API;
using OpenNestModMenu.Core;
using UnityEngine;
using UnityEngine.UI;
#if MELONLOADER
using TMPro = Il2CppTMPro;
#else
using TMPro;
#endif
namespace OpenNestModMenu.UI;

/// <summary>
/// 左栏列表控件（覆盖 UI 骨架 T4）：固定行数的行按钮 + 上下翻页。
///
/// 为什么用**翻页**而不是 ScrollRect：IL2CPP 下代码构建 ScrollRect（viewport/Mask/ContentSizeFitter/
/// VerticalLayoutGroup）容易踩坑，而模组数量在本项目里很小（十几~几十条）→ 翻页更稳、可预测。
/// 行控件**复用**（构造函数一次建好，之后只改文字/颜色），避免每次刷新都重建 UI 对象。
/// </summary>
public sealed class ModMenuListView
{
    /// <summary>一行（控件**复用**：只改文字/颜色，不重建 UI 对象）。</summary>
    public sealed class Row
    {
        public RectTransform Rt;
        public Button Button;
        public Image Bg;
        public Image Accent;   // 选中行左侧强调条
        public UiPill Pill;     // 来源标签
        public TextMeshProUGUI Name;
        public TextMeshProUGUI Meta;   // 右侧：版本号 / 状态词
    }

    private readonly RectTransform _root;
    private readonly List<Row> _rows = new();
    private readonly int _rowH;
    private readonly float _rowW;

    public TextMeshProUGUI PageLabel;

    public ModMenuListView(Transform parent, float x, float y, float w, float h, int rowH = (int)ModMenuTheme.RowH)
    {
        _root = UiKit.Place("list", parent, x, y, w, h);
        _rowH = rowH;
        _rowW = w - 16f;

        // 底部留出翻页信息条的高度
        int maxRows = Mathf.Max(1, (int)((h - 46f) / (rowH + ModMenuTheme.RowGap)));
        for (int i = 0; i < maxRows; i++)
        {
            int captured = i;   // ⚠️ 闭包捕获：必须用局部副本
            var row = new Row();
            row.Rt = UiKit.Place("row", _root, 8f, 6f + i * (rowH + ModMenuTheme.RowGap), _rowW, rowH);

            row.Bg = row.Rt.gameObject.AddComponent<Image>();
            row.Bg.color = ModMenuTheme.RowBg;
            row.Button = row.Rt.gameObject.AddComponent<Button>();
            ModMenuTheme.StyleButton(row.Button, ModMenuTheme.RowBg);
            // ⚠️ Il2Cpp 的 UnityAction 不接受裸 lambda（CS1660）→ 先落到 Action 再传（UiKit.MakeButton 同法）
            Action onClick = () => { try { OnRowClicked?.Invoke(captured); } catch { } };
            row.Button.onClick.AddListener(onClick);

            row.Accent = UiKit.MakeImage(row.Rt, 0f, 0f, 3f, rowH, ModMenuTheme.Accent);
            row.Accent.gameObject.SetActive(false);

            row.Pill = ModMenuTheme.MakePill(row.Rt, 11f, (rowH - ModMenuTheme.PillH) / 2f,
                ModMenuTheme.PillW, ModMenuTheme.PillH);

            float nameX = 11f + ModMenuTheme.PillW + 8f;
            const float metaW = 104f;
            row.Name = UiKit.MakeText(row.Rt, "", nameX, 0f, _rowW - nameX - metaW - 6f, rowH,
                ModMenuTheme.FontRow, ModMenuTheme.TextPrimary, TextAlignmentOptions.Left);
            row.Meta = UiKit.MakeText(row.Rt, "", _rowW - metaW - 4f, 0f, metaW, rowH,
                ModMenuTheme.FontRowMeta, ModMenuTheme.TextDim, TextAlignmentOptions.Right);
            SingleLine(row.Name);
            SingleLine(row.Meta);

            _rows.Add(row);
        }

        PageLabel = UiKit.MakeText(_root, "", 10f, h - 26f, w - 20f, 20f, ModMenuTheme.FontRowMeta,
            ModMenuTheme.TextSecond, TextAlignmentOptions.Left);
    }

    /// <summary>行内文字**不换行** + 省略号（长模组名换行会撑破行高、与下一行重叠）。</summary>
    private static void SingleLine(TextMeshProUGUI t)
    {
        try
        {
            t.enableWordWrapping = false;
            t.overflowMode = TMPro.TextOverflowModes.Ellipsis;
        }
        catch { }
    }

    /// <summary>一行被点击（参数 = 行号，**相对当前页**）。</summary>
    public event Action<int> OnRowClicked;

    public int MaxRows => _rows.Count;

    /// <summary>填充当前页（<paramref name="rows"/> = 当前页条目，选中行高亮）。</summary>
    public void SetRows(IList<ModEntryInfo> rows, int selectedRow)
    {
        _selectedRow = selectedRow;
        for (int i = 0; i < _rows.Count; i++)
        {
            bool used = rows != null && i < rows.Count;
            var row = _rows[i];
            try
            {
                row.Rt.gameObject.SetActive(used);
                if (!used) continue;

                var e = rows[i];
                bool sel = i == selectedRow;
                row.Bg.color = sel ? ModMenuTheme.RowSelected : ModMenuTheme.RowBg;
                row.Accent.gameObject.SetActive(sel);
                row.Pill.Set(ModMenuDisplay.PillLabel(e), ModMenuDisplay.PillColor(e));
                row.Name.text = string.IsNullOrEmpty(e.DisplayName) ? (e.Id ?? "") : e.DisplayName;
                row.Name.color = sel ? Color.white : ModMenuTheme.TextPrimary;
                row.Meta.text = ModMenuDisplay.MetaText(e);
                row.Meta.color = ModMenuDisplay.MetaColor(e);
            }
            catch { }
        }
    }

    public void SetPageInfo(int pageIndex1, int pageCount, int total)
        => PageLabel.text = ModMenuLoc.L("Page", pageIndex1, pageCount, total);

    /// <summary>按屏幕坐标命中的行（相对当前页；无则 -1）。自管指针命中用（不依赖游戏 EventSystem）。</summary>
    public int HitRowAt(Vector2 screenPos)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            try
            {
                if (row.Rt == null || !row.Rt.gameObject.activeInHierarchy) continue;
                if (UnityEngine.RectTransformUtility.RectangleContainsScreenPoint(row.Rt, screenPos, null)) return i;
            }
            catch { }
        }
        return -1;
    }

    /// <summary>鼠标悬停行（-1 = 无）；只改颜色，不重建。</summary>
    public void SetHover(int hoverRow)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            try
            {
                if (row.Rt == null || !row.Rt.gameObject.activeSelf) continue;
                if (i == _selectedRow) continue;   // 选中行颜色由 SetRows 负责
                row.Bg.color = i == hoverRow ? ModMenuTheme.Hover(ModMenuTheme.RowBg) : ModMenuTheme.RowBg;
            }
            catch { }
        }
    }

    private int _selectedRow = -1;

    /// <summary>TEMP-VERIFY：当前页第 <paramref name="index"/> 行（0 起）的 RectTransform（不在范围/已隐藏 = null）。
    /// 用途：自测钩子把**真实 OS 点击**注入到某一行中心，验证“点击闸”没把真点击也滤掉。</summary>
    public RectTransform RowRect(int index)
    {
        if (index < 0 || index >= _rows.Count) return null;
        try
        {
            var rt = _rows[index].Rt;
            if (rt == null || !rt.gameObject.activeInHierarchy) return null;
            return rt;
        }
        catch { return null; }
    }

    public void SetActive(bool active)
    {
        try { _root.gameObject.SetActive(active); } catch { }
    }
}
