# OpenNestUIKit 第三方契约（`OpenNestUIKit.API`）

本文档面向**想给别的模组加菜单页**的作者（以及在本仓库里改契约的人）。
英文完整版（公开仓库用，内容与本文一致）在 `https://github.com/1499501762/Open-Nest-UIKit/blob/main/docs/API.md`。

> **软依赖**：第三方只引 `OpenNestUIKit.API.dll`，**不引**本体程序集。
> 没装 UIKit 时 `UiKitHost.Register` 只是往内存注册表里放一个对象（`IsHostAvailable == false`），
> 不影响对方模组自身加载；契约程序集**不依赖 Unity / BepInEx / MelonLoader**，同一份 provider 源码双端都能编译。

---

## 一、实现状态（2026-09-13，`ApiVersion = 1`）

| 契约部分 | 状态 |
|---|---|
| `UiKitHost` 注册表（`Register`/`Unregister`/`Unregister(id)`/`Clear`/`ProviderCount`/`Providers`/`Changed`/`IsHostAvailable`/`HostVersion`/`ApiVersion`） | ✅ |
| `IUiKitProvider`（身份 + `BuildMenu(IUiMenuTree)`） | ✅ 打开菜单与 `Refresh()` 时调用 |
| `IUiKitNativeEntry`（`ShowInNativeMenu`/`NativeOrder`/`NativeTitle`/`NativeTitleEn`） | ✅ 默认每个 provider 注入一行原生 ESC 入口；实现本接口可改标题/顺序/关掉 |
| 页面模型（`UiPageDef`/`UiRow`/`UiRowKind`/`UiMenuTree`） | ✅ |
| 行家族（18 个动词 + `Add` 逃生口） | ✅ 全部渲染成游戏原生控件（输入框走本库输入管线/输入法，快捷键行可按键捕获） |
| 行修饰（`Hint(text)` / `Hint(Func<string>)` / `MarkSelected`）与 ModMenu 同名别名（`Bool`/`Number`/`Action`） | ✅ 动态提示做成悬停 tooltip |
| 页面尺寸 `Size` / 密度 `SetCompact` | ✅ |
| 宿主控制（`OpenMenu`/`CloseMenu`/`ToggleMenu`/`Refresh`/`IsMenuOpen`/`CanControlMenu`/`CurrentPageId`/`IsTextInputFocused`） | ✅ |
| `UiKitHost.PageChanged`（页面展示变化） | ✅ 打开/导航/返回/关闭都广播 |
| 确认框（`Confirm`/`CanShowDialog`） | ✅ 原生模态；ESC = 取消 |
| 滚动定位（`ScrollToKey`/`ScrollToTop`） | ✅ 只对声明式页面生效；行已可见时不动 |
| 列表副文本（`SelectableList` 的 `hints`） | ✅ 每行可带**右对齐**副文本（版本/状态对齐成一列）；主/副文本都单行省略 |
| 底栏常驻信息（`SetFooter`） | ✅ 宿主底栏右侧；与宿主的临时提示共用位置（提示优先、过期后回落）。用途：把“环境/版本/统计”摘要摆到底栏，而不是让它当页面行占高度 |
| 语言键（`UiKitLang.T`/`IsChinese`/`Changed`） | ✅ 宿主推当前语言 |
| 可选 `IUiKitDefaults`（恢复默认按钮） | ✅ 宿主在根页追加带确认框的“恢复默认”行 |
| 悬浮聊天层（`SetChat`/`ClearChat`/`FocusChat`/`CloseChat`/`CanShowChat`/`IsChatTyping`） | ✅ |

宿主侧的完整实现说明见 `docs/UI_KIT.md`；可直接编译的示例见 `samples/`。

## 二、最小可用示例

```csharp
// 1) provider：只要 OpenNestUIKit.API，无 Unity 依赖
public sealed class MyModMenu : UiKitProviderBase
{
    public override string Id => "mymod";
    public override string DisplayName => "我的模组";
    public override string Version => "1.0.0";

    public override void BuildMenu(IUiMenuTree menu)
    {
        menu.Root.Nav("常规", "general");
        menu.Root.Toggle("mymod.on", "启用", MyCfg.On, v => MyCfg.On = v);

        var p = menu.Page("general", "常规");
        p.Slider("mymod.range", "范围(米)", MyCfg.Range, 10, 500, 5, v => MyCfg.Range = (float)v);
        p.Choice("mymod.mode", "模式", new[] { "快", "准" }, MyCfg.Mode, i => MyCfg.Mode = i);
        p.Text("mymod.name", "呼号", MyCfg.Name, s => MyCfg.Name = s);
        p.KeyBind("mymod.key", "打开菜单", MyCfg.Key, k => MyCfg.Key = k);
        p.Separator();
        p.Button("重置", "重置", () => MyCfg.Reset());
    }
}

// 2) 入口：随时注册（宿主没装/没启动都安全）
UiKitHost.Register(new MyModMenu());

// 3) 想自己打开这一页：宿主给 provider 页加命名空间 `provider:<Id>[:<页id>]`
UiKitHost.OpenMenu("provider:mymod");
```

## 三、页面与命名空间（容易踩）

| 第三方声明 | 宿主里的页面 id |
|---|---|
| `menu.Root` | `provider:<你的 Id>` |
| `menu.Page("general", …)` | `provider:<你的 Id>:general` |

- `menu.Page(id, title)` **同 id 重复调用返回同一个实例**（可多次补充行）。
- 宿主会**自动补 Nav**：你在 `Root` 里没用 `Nav` 指过的页，会被自动加一行入口（指过的不重复加，避免“同一个页出现两次”）。
- `Nav(label, pageId)` 是**页面栈**（有返回行），行为等同游戏原生多级菜单。
- `UiKitHost.CurrentPageId` 返回的就是带命名空间的 id；打开不存在的 id 会回主页并记一条 warn。

## 四、行动词（全部链式，返回同一个 `UiPageDef`）

```csharp
Header(string text)                     Label(string text)                       Separator()
Info(string label, string value)        // 键值行：标签列固定宽 + 值列左对齐（详情/诊断页属性表）
Button(string label, string buttonText, Action onClick)
Nav(string label, string pageId, string hint = null)
Tabs(string key, IReadOnlyList<string> tabs, int index, Action<int> onChanged)
Toggle(string key, string label, bool value, Action<bool> onChanged)
Slider(string key, string label, double value, double min, double max, double step, Action<double> onChanged)
Stepper(string key, string label, double value, double min, double max, double step, Action<double> onChanged)
Choice(string key, string label, IReadOnlyList<string> choices, int selected, Action<int> onChanged)
Text(string key, string label, string value, Action<string> onChanged)
Text(string key, string label, string value, Action<string> onChanged, string placeholder, int maxLength = 0)
KeyBind(string key, string label, string current, Action<string> onChanged)
Progress(string label, double value01)          // 只读，夹到 0..1
Foldout(string key, string label, bool expanded, Action<bool> onToggle, Action<UiPageDef> body)
SelectableList(string key, float height, IReadOnlyList<string> items, int selected, Action<int> onSelected)
SelectableList(string key, float height, IReadOnlyList<string> items, IReadOnlyList<string> hints, int selected, Action<int> onSelected)   // hints = 右侧右对齐副文本（版本/状态）
Columns(float leftWidth, Action<UiPageDef> left, Action<UiPageDef> right, float gap = 12f)
List(string key, float height, Action<UiPageDef> build)   // 内嵌滚动列表（自带滚动条/Bounce）
    // ⚠ height **< 0 = 吃剩余高度**（两栏页的桌面式布局）；`SelectableList` 同语义。
Hint(string text) / Hint(Func<string>) / MarkSelected(bool)   // 修饰“刚加的那一行”
Bool(...) / Number(...) / Action(...)                         // == Toggle/Slider/Button（与 ModMenu 同名）
Add(UiRow row)                                  // 逃生口：手搓行模型
```

要点：

- **`key` 只是你自己的把手**（也是 `UiKitHost.ScrollToKey` 的寻址键），宿主不用它存任何东西（宿主从不落盘）。
- **写回只到你自己的回调**：`Toggle` 回调 bool、`Choice` 回调索引（写回值是字符串）、`Text`/`KeyBind` 回调字符串；
  **没有任何自动配置持久化**，存哪由你决定。
- `Slider` 的 `step <= 0` = 宿主取默认（`(max-min)/100`）；`Stepper` 同理（默认 1）。数值以不变文化字符串过契约。
- `Text` 的带占位重载：空文本时画灰色占位；`maxLength > 0` 时**超出部分在输入时就截断**（含 IME 提交路径）。
- **`Foldout` 的 `body` 只在展开时被调用**（收起不花代价）；`onToggle` 里**只存状态**，宿主会自己重建页面。
- **`SelectableList`** 只把选中索引回调给你（宿主重画高亮）；需要自定义行时用 `List` + `MarkSelected()`。
- `samples/Shared/SampleMenu.cs` 是这些动词的完整用法（含 `Foldout`/`SelectableList`/动态 `Hint`/`Confirm`/`ScrollToKey`）。

## 五、宿主控制与刷新语义

```csharp
UiKitHost.ApiVersion / IsHostAvailable / HostVersion / ProviderCount / Providers / Changed
UiKitHost.PageChanged                       // (旧页 id, 新页 id)：打开/导航/返回/关闭都广播
UiKitHost.Register(provider) / Unregister(provider) / Unregister("id")
UiKitHost.IsMenuOpen / CanControlMenu / OpenMenu(pageId = null) / CloseMenu() / ToggleMenu(pageId = null)
UiKitHost.Refresh() / CurrentPageId / IsTextInputFocused
UiKitHost.ScrollToKey("key") / ScrollToTop()
UiKitHost.CanShowDialog / Confirm(title, body, ok => { })
```

- **宿主不在时全部静默无效**（不抛异常），需要提示玩家时用 `IsHostAvailable` / `CanControlMenu` 判断。
- `Refresh()` = **原地重建当前页**（页面栈与滚动位置保留，`BuildMenu` 会再跑一遍）。
- ⚠️ **`IsTextInputFocused == true` 时不要 `Refresh()`**，会把玩家正在输入的草稿冲掉：

  ```csharp
  void OnDataChanged() { if (!UiKitHost.IsTextInputFocused) UiKitHost.Refresh(); }
  ```

- `PageChanged` 是“懒加载”的正道：**自己这一页被看着时**再去拉数据（`newId == "provider:" + Id`），
  而不是在 `BuildMenu` 里做重活。
- `Confirm` **要求菜单已打开**（要画布与指针接管）；菜单关着时只记一条 warn 并回调 `false`，
  不会让你“点了没反应又不知道结束没”。ESC = 取消（不会顺手关菜单）。
- `ScrollToKey` 按行 `Key` 定位；行已经完整可见时**不动**（避免“一点就跳”）。

### 语言键与“恢复默认”（两个小接口）

```csharp
page.Label(UiKitLang.T("已连接", "Connected"));      // 跟着游戏语言
UiKitLang.Changed += () => UiKitHost.Refresh();       // 切语言后重画

public sealed class MyMenu : UiKitProviderBase, IUiKitDefaults
{
    public void ResetToDefaults() { MyConfig.Reset(); }  // 宿主在确认后才调你
}
```

- 类型名是 **`UiKitLang`**（不是 `UiKitLoc`）：宿主程序集里已经有一个内部 `UiKitLoc`，同名会造成歧义引用。
- 实现 `IUiKitDefaults` → 宿主在你的**根页**追加“恢复默认设置”行，点击 → 原生确认框 → 确认后才回调你，然后自动 `Refresh`。
  与 `OpenNestModMenu.API.IModMenuProvider.ResetToDefaults` **同名同义**，一份设置类可以同时服务两个模组。

## 六、原生 ESC 入口与聊天层

- 默认给每个 provider 在游戏 ESC 列表注一行（标题 `DisplayName`，点开 `provider:<Id>`）。
  实现 `IUiKitNativeEntry` 可改：`ShowInNativeMenu=false` 不占格位（原生面板本来就排满 8 行）、
  `NativeOrder`（默认 `100+注册序`）、`NativeTitle`（中文环境）/`NativeTitleEn`。
- 注入**全部由 UIKit 做**，第三方不碰游戏原生 UI（多模组同时注入不会抢格子）。
- 有会话/聊天的模组用 `UiKitHost.SetChat(lines, onSend, title, hint)` 交给宿主渲染悬浮层
  （**菜单关着也在**、收起时鼠标穿透、回车唤入/发送、ESC 收起）；离开会话 `ClearChat()`。

## 七、生命周期与错误处理

| 规则 | 原因 |
|---|---|
| 随时可 `Register`（宿主还没装载也行） | 注册只是进内存表，宿主启动时统一收编 |
| `Id`/`DisplayName`/`Version`/`Author` 保持"不抛异常、不做重活" | 收编时会读；抛了宿主会跳过这个 provider |
| `BuildMenu` 要轻、只碰自己的状态 | 每次开菜单/`Refresh` 都在主线程跑一遍 |
| 不要假设页面可见 | 收编时也会调用 `BuildMenu` |
| 第三方回调的异常被宿主兜住 | 一个坏 provider 不会拖垮菜单（但那行会静默失效） |
| 卸载时 `Unregister(provider)` | 否则页面会一直列着 |

## 八、打包与版本

- **只引不打包**：`OpenNestUIKit.API.dll` 由 UIKit 分发（BepInEx 在 `BepInEx/plugins/`，MLL 在 `UserLibs/`）。
  第三方工程要 `Private="false"`，否则 `plugins/` 里出现同名第二份 dll（重复程序集隐患）。
  `samples/BepInEx/*.csproj`、`samples/MelonLoader/*.csproj` 就是这么写的。
- `UiKitHost.ApiVersion` 只在**不兼容变更**时 +1；本体版本号独立走。
- 没装 UIKit：`Register` 成功、`IsHostAvailable=false`、所有控制调用无效 —— 这是软依赖的全部意义，别让模组因此起不来。

## 九、相关文件

- `src/OpenNestUIKit.API/`：契约源码（`UiKitHost.cs` / `IUiKitProvider.cs` / `IUiMenuTree.cs` / `UiPageDef.cs` / `UiRow.cs`）。
- 宿主侧消费逻辑：`src/OpenNestUIKit/Menu/UiPageCatalog.cs`（收编 provider + 同步原生入口）。
- 仓内示例 provider：`src/OpenNestUIKit/Pages/DemoProvider.cs`（内置演示页用）。
- 独立示例模组：`samples/`（双壳 + 共享 provider，可编译）。
