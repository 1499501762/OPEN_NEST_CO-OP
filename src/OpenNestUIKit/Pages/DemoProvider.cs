using System;
using OpenNestUIKit.API;

namespace OpenNestUIKit.Pages;

/// <summary>
/// **第三方模组示例**：演示"别的模组怎么把自己的菜单页挂进原生 UI 库"。
///
/// 关键点（也是给第三方看的模板）：
/// - 只引用 `OpenNestUIKit.API`（**纯 .NET，无 Unity 依赖**）→ 同一份代码双端都能编译；
/// - 未安装本库时 <see cref="UiKitHost.Register"/> 只是进内存注册表（`IsHostAvailable == false`），
///   不影响模组自身加载（**软依赖**）；
/// - 界面用"动词方法"声明（Header/Label/Nav/Toggle/Slider/Choice/Text/KeyBind/Button/Progress），
///   渲染由本库负责；写回通过回调交回给第三方（第三方自己决定往哪写）。
/// </summary>
internal sealed class DemoProvider : UiKitProviderBase, IUiKitNativeEntry, IUiKitDefaults
{
    public override string Id => "demo.provider";
    public override string DisplayName => "示例模组";
    public override string Version => "1.0.0";
    public override string Author => "OpenNestUIKit";

    /// <summary>可选接口：宿主会在根页底部放一个“恢复默认”（带确认框）。</summary>
    public void ResetToDefaults()
    {
        _enabled = true;
        _scale = 1.0;
        _mode = 1;
        _name = "";
        _key = "f8";
        _gear = 3;
        _expanded = true;
        _pick = 0;
        Menu.UiMenuWindow.SetStatus(UiKitLoc.T("已恢复默认", "defaults restored"));
    }

    // 原生 ESC 列表本来就已经排满（实测原生 8 行占满 400 高面板），每多一行都要等比压缩节距。
    // 示例页只演示"怎么声明菜单"，没必要占原生格位 → 不注入原生入口（只在本库菜单里出现）。
    public bool ShowInNativeMenu => false;
    public int NativeOrder => 0;
    public string NativeTitle => DisplayName;
    public string NativeTitleEn => DisplayName;

    // 示例设置（真实模组里应读写自己的配置文件）
    private bool _enabled = true;
    private double _scale = 1.0;
    private int _mode = 1;
    private string _name = "";
    private string _key = "f8";
    private int _gear = 3;
    private bool _expanded = true;
    private bool _advancedOpen = false;
    private int _pick = 0;

    public override void BuildMenu(IUiMenuTree menu)
    {
        // ---- 根页：子菜单入口 + 少量常用项 ----
        menu.Root.Header("示例模组");
        menu.Root.Label("这是一个用声明式契约注册的页面（不依赖 Unity）。");
        menu.Root.Nav("常规设置", "general", "开关 / 数值 / 枚举");
        menu.Root.Nav("快捷键", "keys", "按键绑定");
        menu.Root.Nav("布局", "layout", "折叠分组 / 可选中列表");
        menu.Root.Nav("只读信息", "info", "进度条与信息展示");
        menu.Root.Toggle("demo.enabled", "启用示例模组", _enabled, v =>
        {
            _enabled = v;
            Menu.UiMenuWindow.SetStatus("demo.enabled = " + v);
        });

        // ---- 子页 1：常规 ----
        var general = menu.Page("general", "常规设置");
        general.Header("数值");
        general.Slider("demo.scale", "缩放", _scale, 0.5, 2.0, 0.05, v =>
        {
            _scale = v;
            Menu.UiMenuWindow.SetStatus("demo.scale = " + v.ToString("0.00"));
        });
        general.Choice("demo.mode", "模式", new[] { "简单", "标准", "专家" }, _mode, i =>
        {
            _mode = i;
            Menu.UiMenuWindow.SetStatus("demo.mode = " + i);
        });
        general.Stepper("demo.gear", "档位", _gear, 1, 8, 1, v =>
        {
            _gear = (int)v;
            Menu.UiMenuWindow.SetStatus("demo.gear = " + _gear);
        }).Hint(() => UiKitLoc.T("当前档位 = ", "current gear = ") + _gear);   // 动态悬停提示
        general.Text("demo.name", "名称", _name, v =>
        {
            _name = v;
            Menu.UiMenuWindow.SetStatus("demo.name = '" + v + "'");
        }, UiKitLoc.T("输入呼号…", "type a callsign…"), 16);                    // 占位 + 最长 16 字符
        general.Separator();
        general.Header("动作");
        general.Button("立即执行一次", "执行", () => Menu.UiMenuWindow.SetStatus("demo: action executed"));
        general.Nav("查看只读信息", "info");

        // ---- 子页 2：快捷键 ----
        var keys = menu.Page("keys", "快捷键");
        keys.Label("点击右侧的键位框，然后按任意键完成绑定（ESC 取消）。");
        keys.KeyBind("demo.key", "打开面板", _key, v =>
        {
            _key = v;
            Menu.UiMenuWindow.SetStatus("demo.key = " + v);
        });
        keys.Separator();
        keys.Label("绑定结果由模组自己保存（本示例只存在内存里）。");

        // ---- 子页 3：布局（折叠分组 + 可选中列表）----
        var layout = menu.Page("layout", "布局");
        layout.Label("折叠分组展开的子行直接进入页面流；列表选中会把索引写回模组。");
        layout.Foldout("demo.fold.basic", "基础选项", _expanded, v =>
        {
            _expanded = v;
            Menu.UiMenuWindow.SetStatus("fold.basic = " + v);
        }, g =>
        {
            g.Label("这些行只在展开时存在（声明式）。");
            g.Toggle("demo.fold.flag", "分组内的开关", _enabled, v => _enabled = v);
            g.Slider("demo.fold.slider", "分组内滑块", _scale, 0.5, 2.0, 0.05, v => _scale = v);
        });
        layout.Foldout("demo.fold.adv", "高级选项", _advancedOpen, v =>
        {
            // 状态必须由第三方保存（宿主不替你记）；宿主会在回调后自动重建页面。
            _advancedOpen = v;
            Menu.UiMenuWindow.SetStatus("fold.adv = " + v);
        }, g =>
        {
            g.Label("（收起时不会调用 build，因此这两行根本不存在）");
            g.Stepper("demo.fold.gear", "分组内档位", _gear, 1, 8, 1, v => _gear = (int)v);
        });
        layout.Separator();
        layout.SelectableList("demo.pick", 220f,
            new[] { "第一个条目", "第二个条目", "第三个条目", "第四个条目" }, _pick, i =>
        {
            _pick = i;
            Menu.UiMenuWindow.SetStatus("pick = " + i);
        });
        layout.Label("当前选中：" + _pick);

        // ---- 子页 4：只读信息 ----
        var info = menu.Page("info", "只读信息");
        info.Header("状态");
        info.Label("宿主：" + (UiKitHost.IsHostAvailable ? UiKitHost.HostVersion : "（未安装 OpenNestUIKit）"));
        info.Label("契约 API 版本：" + UiKitHost.ApiVersion);
        info.Progress("示例进度", 0.42);
        info.Separator();
        info.Nav("返回常规设置", "general");
        info.Button("恢复默认设置（带确认框）", "重置", () =>
            UiKitHost.Confirm(UiKitLoc.T("恢复默认设置", "Reset to defaults"),
                              UiKitLoc.T("确定把示例设置恢复成默认值吗？", "Reset the sample settings to their defaults?"),
                              ok =>
                              {
                                  Menu.UiMenuWindow.SetStatus(ok ? "confirm = ok" : "confirm = cancel");
                                  if (ok) { ResetToDefaults(); UiKitHost.Refresh(); }
                              }));
        info.Button("滚到“常规设置”那一行", "滚动", () => UiKitHost.ScrollToKey("demo.scale"));
        info.Button("回到页面顶部", "置顶", () => UiKitHost.ScrollToTop());
    }
}
