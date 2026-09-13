using System;
using OpenNestUIKit.API;

namespace OpenNestUIKit.Test;

/// <summary>
/// **契约路径测试**：只用 `OpenNestUIKit.API`（纯 .NET，无 Unity）把一整棵测试菜单树注册进库，
/// 覆盖全部行类型 + 子页导航 + 回写回调。这条路径验证"第三方模组不需要引用 Unity 也能挂菜单"。
///
/// 注册时机在**测试模组 Startup**（早于/独立于 UIKit 是否加载）—— 若 UIKit 没装，
/// 注册只进内存注册表（<see cref="UiKitHost.IsHostAvailable"/> == false），不影响本模组加载。
/// </summary>
internal sealed class TestProvider : UiKitProviderBase, IUiKitNativeEntry
{
    public const string ProviderId = "open.nest.uikit.test";

    public override string Id => ProviderId;
    public override string DisplayName => "UIKit 测试模组";
    public override string Version => TestInfo.Version;
    public override string Author => TestInfo.Author;

    // 测试模组不占原生 ESC 列表格位（那里很挤：原生 8 行已占满面板；要测注入用测试页上的
    // "加一个测试入口 / 重新注入" 那两行，它们本来就往桥里塞临时条目）。
    public bool ShowInNativeMenu => false;
    public int NativeOrder => 0;
    public string NativeTitle => DisplayName;
    public string NativeTitleEn => DisplayName;

    // 契约页的示例状态（真实模组应写自己的配置）
    private bool _flag = true;
    private double _num = 0.5;
    private int _mode;
    private string _text = "";
    private string _key = "f8";

    public override void BuildMenu(IUiMenuTree menu)
    {
        // ---- 根页 ----
        menu.Root.Header("契约注册（第三方路径）");
        menu.Root.Label("本页由测试模组通过 OpenNestUIKit.API 注册，完全没引用 Unity。");
        menu.Root.Nav("全部控件行", "rows", "Toggle/Slider/Choice/Text/KeyBind/Progress");
        menu.Root.Nav("子页导航", "nav", "再进一层，验证页面栈");
        menu.Root.Toggle("test.flag", "布尔开关", _flag, v => { _flag = v; Menu.UiMenuWindow.SetStatus("test.flag=" + v); });

        // ---- 控件行页 ----
        var rows = menu.Page("rows", "全部控件行");
        rows.Header("输入类");
        rows.Toggle("test.a", "开关 A", _flag, v => { _flag = v; TestProbe.Hit("api:toggle"); });
        rows.Slider("test.b", "滑条 B", _num, 0, 1, 0.05, v => { _num = v; TestProbe.Hit("api:slider"); });
        rows.Choice("test.c", "选择 C", new[] { "一", "二", "三" }, _mode, i => { _mode = i; TestProbe.Hit("api:choice"); });
        rows.Text("test.d", "文本 D", _text, v => { _text = v; TestProbe.Hit("api:text"); });
        rows.KeyBind("test.e", "快捷键 E", _key, v => { _key = v; TestProbe.Hit("api:key"); });
        rows.Header("展示类");
        rows.Progress("进度", 0.35);
        rows.Label("这是一段说明文字。");
        rows.Separator();
        rows.Button("动作按钮", "执行", () => TestProbe.Hit("api:action"));
        rows.Nav("返回根页", "root");

        // ---- 子页（多级）----
        var nav = menu.Page("nav", "子页导航");
        nav.Label("从导航页再进一层，返回时面包屑应显示 root › nav（或 rows › nav）。");
        nav.Nav("控件行页", "rows");
        nav.Button("回上一层（库内 Back）", "返回", () => Menu.UiMenuWindow.Back());
    }
}
