# 游戏原生 UI 研究与抽象（Native UI Bridge）

> **目的**：研究 Iron Nest: Heavy Turret Simulator 的原生 UI 是怎么实现的，能否把"调用/使用原生 UI 能力"
> 抽象成平台无关 API，放进 **OpenNestCore**（`OpenNestCore.UI`），让任何模组（即使不做联机）都能复用
> 游戏自带 UI 子系统（本地化 / 通知 toast / ESC 菜单 / 主菜单阶段 / 虚拟光标）+ UGUI 构建工具。
>
> **信息来源**：`tools/dump_Assembly-CSharp.txt`（IL2CPP 反编译）、`src/OpenNestCoop/UI/CoopUIManager.cs`
> （模组 UGUI 菜单，2026-08 起用）、`src/OpenNestCoop/GameSync/NotificationSync.cs`、`src/OpenNestCore/UI/*`。
>
> **更新记录**：
> - 2026-08-23 建档：原生 UI 研究 + `OpenNestCore.UI` 抽象（INativeUiService + NativeUi + UiKit）+ 游戏侧桥接 IronNestNativeUi。
> - 2026-08-23 原生 UI 图集（sprite）复现：全盘确认游戏**无 UI AssetBundle**（仅 player.bundle）→ 走"自打包 ui.bundle + UiSpriteBank"路线；Core 新增 `UiSpriteBank`（经 AssetBundleIron 加载 Sprite）+ `UiKit.MakePanel/MakeButton(bgSprite:)`。
> - 关联文档：`docs/OPEN_NEST_CORE.md`（Core API）、`docs/INTERACTABLES.md`（世界内可交互实体——与"屏幕 UI"不同范畴）。

---

## 一、游戏原生 UI 全景（研究结论）

### 1.1 UI 框架

| 层 | 技术 | 说明 |
|---|---|---|
| 渲染 | **UGUI Canvas**（`UnityEngine.UI`）| 主菜单/设置/结算全部 UGUI，ScreenSpaceOverlay |
| 文本 | **TextMeshPro**（`Unity.TextMeshPro`）| TMP_Text / TMP_InputField / TMP_Dropdown / TMP_FontAsset |
| 输入 | **新 Input System**（`Unity.InputSystem`）| `Keyboard.OnTextInput(char)`、`VirtualCursor` + `VirtualCursorInputModule`（BaseInputModule）驱动 UI 交互与 IME |
| 本地化 | **`Localisation.LocalisationManager`** | `CurrentLanguage` / `Get(key)` / `TryGet(key,out)` / `GetFont(TMP_FontAsset)` / `OnLanguageChanged`；`StaticLocalisedText` 组件按 `TextIdentifier` 自动翻译 |
| 通知 | **`UINotificationManager`** | 静态单例，`ShowNotification(title, desc, lifetime, Nullable<Color>)` —— 游戏原生 toast |
| 菜单 | **`EscapeMenuToggleUnityEvent`** + `EscapeMenuOpenBlocker` | ESC 暂停菜单；blocker 注册进 `activeBlockers` 阻止弹出 |
| 场景 | **`MissionManager`** + `MainMenuStateRelay` | `GamePhase`（MainMenu=0/BrowsingMap=1/MissionActive=2）、`MainMenuLoaded/Loading/Unloaded` 事件 |

### 1.2 关键限制（为什么模组 UI 必须用 UGUI）

- 该游戏是 **IL2CPP 裁剪构建**：`GUILayout.*`（自动布局）已被裁剪（运行时报 "Method unstripping failed"），只剩 `GUI.*` 手动 Rect。
- IMGUI(OnGUI) 渲染在游戏 **UGUI 主菜单之下**：鼠标箭头被盖住、点击被 UGUI 拦截 → 模组菜单必须用 **UGUI Canvas**（`CoopUIManager` 已如此：ScreenSpaceOverlay + sortingOrder 32766 + CanvasScaler + GraphicRaycaster）。
- 文本输入：`GUI.TextField` 被裁剪；**中文 IME 只有聚焦 `TMP_InputField` 才会弹出**（自制 Button+Text 输入框无法唤起输入法）→ 用隐藏 `TMP_InputField` 锚点 + `Keyboard.OnTextInput` patch。
- **中文显示方框**：默认字体不含中文字形 → 需用 `LocalisationManager.GetFont`（当前语言字体）或运行时扫描 TMP_FontAsset 兜底。

### 1.3 可复用原生 UI 能力清单

| 能力 | 游戏类/方法 | 抽象到 Core 后 |
|---|---|---|
| 本地化 | `LocalisationManager.CurrentLanguage/Get/TryGet/GetFont/OnLanguageChanged` | `NativeUi.CurrentLanguage / Localise / GetLocalisedFont / LanguageChanged` |
| 通知 toast | `UINotificationManager.ShowNotification(...)` | `NativeUi.Toast(title, desc, lifetime, color?)` |
| ESC 菜单 | `EscapeMenuToggleUnityEvent.IsOpen/ForceClose` + `EscapeMenuOpenBlocker.Register/Unregister` | `NativeUi.IsEscapeMenuOpen / CloseEscapeMenu / BlockEscapeMenu` |
| 主菜单状态 | `MissionManager.CurrentPhase`（GamePhase） | `NativeUi.IsMainMenu` + `MainMenuLoaded/Unloaded` 事件 |
| 虚拟光标 | `VirtualCursor.ScreenPosition` / `VirtualCursorInputModule._isOverInteractableUI` | `NativeUi.VirtualCursorPosition / IsCursorOverUi` |
| UGUI 构建 | CoopUIManager 手写全套（Canvas/Image/Text/Button/Input） | `UiKit.*`（自动接本地化字体 + 文案） |

> **无法抽象**（研究确认）：游戏原生 UI **不是插件框架**——主菜单场景是 UGUI 对象树，没有"往原生主菜单加一个菜单项"的公开 API。可复用的是**子系统能力**（上表）与**同款 UGUI 构建方式**；加自己的菜单/面板仍需自建 Canvas（用 `UiKit` 即可）。

---

## 二、抽象设计：`OpenNestCore.UI`

定位与既有 `Logging`/`Avatar`/`Assets` 一致：**Core 定义契约（接口 + 注册表 + 工具），游戏侧实现并注册**，
Core 不依赖游戏 `Assembly-CSharp`。

### 2.1 `INativeUiService`（接口契约）

`src/OpenNestCore/UI/INativeUiService.cs`。只依赖 UnityEngine + TMP，无游戏类型。方法：
`IsAvailable`、`CurrentLanguage`、`LanguageChanged`、`TryLocalise`、`GetLocalisedFont`、
`ShowToast(title,desc,lifetime,Color?)`、`IsEscapeMenuOpen/IsEscapeMenuBlocked`、`SetEscapeMenuBlocked`、
`ForceCloseEscapeMenu`、`IsMainMenu`、`MainMenuLoaded/Unloaded`、`VirtualCursorPosition`、`IsCursorOverUi`。

### 2.2 `NativeUi`（静态门面 + 注册表）

同文件。`NativeUi.Register(INativeUiService)` 注入游戏侧实现；之后模组以 `NativeUi.xxx` 调用。
**未注册时安全返回默认值**（null / false / 原 key / 原字体）。事件（LanguageChanged/MainMenuLoaded/Unloaded）
在注册/替换/清除时自动接通转发。

### 2.3 `UiKit`（UGUI 构建工具集）

`src/OpenNestCore/UI/UiKit.cs`。封装 `CoopUIManager` 已验证的 UGUI 构建方式，自动接入 `NativeUi` 本地化字体/文案：
`CreateCanvas(name, sortingOrder, dontDestroy)`、`MakeBlocker`、`MakeRect/MakeRectFill/Place`、
`MakeImage`、`MakeText/MakeTextFill`、`MakeButton`、`MakeInputBox`（点击式）、`MakeInputField`（TMP_InputField，中文 IME 必需）、
`L(key)`（本地化文案）、`EnsureFont(TMP_Text)`（本地化字体 → 运行时扫描兜底）。

### 2.4 游戏侧桥接 `IronNestNativeUi`

`src/OpenNestCoop/UI/IronNestNativeUi.cs`（实现 `INativeUiService`，能引用 Assembly-CSharp）：
- `ShowToast` → `UINotificationManager.ShowNotification`（与 NotificationSync 同款调用，含空 `Nullable<Color>`）
- `TryLocalise/CurrentLanguage` → `LocalisationManager.Get/CurrentLanguage`（桥接就绪要求 `IsReady`）
- `GetLocalisedFont` → `LocalisationManager.GetFont`；⚠️ **MLL 下该 interop 方法缺失**（MissingMethodException 在 IL2CPP trampoline 抛出、managed catch 捕不到）→ `#if !MELONLOADER` 编译期排除，MLL 直接返回原字体
- ESC → `EscapeMenuToggleUnityEvent`（`FindObjectsOfType(includeInactive:true)` 查找 + 1s 冷却缓存）；`SetEscapeMenuBlocked` 用 `EscapeMenuOpenBlocker` 占位组件 Register/Unregister
- 主菜单 → `MissionManager.Instance.CurrentPhase == (GamePhase)0`
- 光标 → `VirtualCursor.ScreenPosition`；`IsCursorOverUi` 用反射读 `VirtualCursorInputModule._isOverInteractableUI`（与 TeleprinterSync 读私有字段同法）
- **事件用轮询而非原生事件订阅**：`NativeUiPoll`（MonoBehaviour，CoopRuntime.Startup 挂载，DontDestroyOnLoad）每帧比对语言/主菜单阶段变化 → 触发 `LanguageChanged/MainMenuLoaded/Unloaded`（规避 IL2CPP 原生事件订阅的稳定性风险）

### 2.5 挂载点

- `CoopRuntime.Startup` → `IronNestNativeUi.Hook()`（注册 + 挂轮询）；`Shutdown` → `IronNestNativeUi.Unhook()`。
- `GlobalUsings.cs` 加 `global using OpenNestCore.UI;`。
- 已接入：`CoopLoc.Detect()` 语言检测**桥接优先 + 原直读兜底**（桥接未注册时行为不变）。

---

## 三、用法示例（第三方模组）

```csharp
using OpenNestCore.UI; // global using 或显式

// 1) 游戏侧桥接由 OpenNestCoop 注册（CoopRuntime.Startup）；模组只消费：
if (NativeUi.Available)
{
    NativeUi.Toast("Open Nest", "联机房间已创建", 3f);
    NativeUi.BlockEscapeMenu(true);          // 打开模组 UI 时阻止游戏暂停菜单
    var lang = NativeUi.CurrentLanguage;     // "zh" / "en"
    var tip  = NativeUi.Localise("some.key");// 走游戏本地化表
}

// 2) 建自己的 UGUI 菜单（IMGUI 不可用，必须 UGUI）：
var canvas = UiKit.CreateCanvas("MyModUI", sortingOrder: 32766);
UiKit.MakeButton(canvas.transform, "确定", 10, 10, 160, 40, () => { /* ... */ });
var input = UiKit.MakeInputField(canvas.transform, "", 10, 60, 300, 40, 15); // 中文 IME 可用
```

---

## 四、与游戏类型映射表（桥接实现）

| Core API | 游戏类型/方法 | 文件 |
|---|---|---|
| `NativeUi.CurrentLanguage/Localise/GetLocalisedFont/LanguageChanged` | `Localisation.LocalisationManager` | `IronNestNativeUi.cs` |
| `NativeUi.Toast` | `UINotificationManager.ShowNotification` | 同上（`NotificationSync` MsgType=131 另管联机同步） |
| `NativeUi.IsEscapeMenuOpen/BlockEscapeMenu/CloseEscapeMenu` | `EscapeMenuToggleUnityEvent` + `EscapeMenuOpenBlocker` | 同上 |
| `NativeUi.IsMainMenu/MainMenuLoaded/Unloaded` | `MissionManager.CurrentPhase` | 同上 |
| `NativeUi.VirtualCursorPosition/IsCursorOverUi` | `VirtualCursor` + `VirtualCursorInputModule` | 同上 |

> 注意：`NotificationSync`（打字机通知联机同步）与 `NativeUi.Toast`（本地原生通知）是**两回事**——
> 前者广播 title/desc 给远端复现，后者只是本地弹原生 toast。桥接的 Toast 不参与联机。

---

## 五、注意事项 / 坑

1. **MLL 的 `GetFont` 缺失**：MelonLoader 下 `LocalisationManager.GetFont` 缺失且 managed catch 捕不到
   （IL2CPP trampoline 抛出中断调用链）→ 桥接用 `#if !MELONLOADER` 排除，MLL 的 `EnsureFont` 靠运行时扫描兜底。
2. **原生事件订阅慎用**：桥接的 `LanguageChanged/MainMenuLoaded/Unloaded` 用 `NativeUiPoll` 轮询实现
   （每帧比对），不订阅原生事件（CoopUIManager 记录过 `add_onTextInput + Il2Cpp 委托` 会乱码/崩溃的教训）。
3. **`IsCursorOverUi` 读私有字段**：`VirtualCursorInputModule._isOverInteractableUI` 无公开 getter，
   用反射读取（与 TeleprinterSync 读 `_revealMask` 同法）；失败返回 false（best-effort）。
4. **ESC blocker 生命周期**：`SetEscapeMenuBlocked(true)` 创建 `EscapeMenuOpenBlocker` 占位组件并 Register；
   记得成对调用 `SetEscapeMenuBlocked(false)` 释放（或 `Unhook` 时由 GameObject 销毁触发 Unregister）。
5. **桥接未注册 = 安全默认**：任何模组可在 OpenNestCoop 未加载时使用 `NativeUi`/`UiKit`——能力返回默认值，
   `UiKit.EnsureFont` 自行扫描字体。

---

## 六、原生 UI 图集（sprite）复现——背景/按钮观感

**目标**：让模组 UI 的背景/按钮**观感与游戏原生一致**（圆角/边框/贴图），而不只是纯色块。

### 6.1 事实约束（2026-08-23 全盘扫描 + UnityPy + Cpp2IL 反编译 + 运行时实测，结论多轮修正后定论）

- **最终定论**：**UI 是静态场景/prefab UGUI**（Image/Button/TMP_Text 组件真实存在于场景与 resources.assets prefab 里）。早期"场景 0 Image、UI 运行时构建"结论**错误**——UnityPy 读 IL2CPP 序列化文件时 UnityEngine.UI 组件（Image/Text/Button/TMP）显示为 MonoBehaviour 且因无 typetree 读不了，导致误判。
- **证据**：①Cpp2IL `dll_default` 反编译 `MissionSaveMenuController` 有 `Button saveButtonComponent` / `TMP_Text saveButtonText`（场景 UI 引用）；②运行时 `Resources.Load<GameObject>("AchievementDisplay")` 实测 OK，`CollectSprites` 数到 3 个 Image（prefab 确有 Image）；③callanalyzer 无 `AddComponent<Image>` 调用者 → UI 非 AddComponent 构建。
- **反编译工具结论**：Cpp2IL（MLLoader 捆绑 2022.1.0-pre）`diffable-cs`=只有签名、`il_recovery`/`dll_default`=空方法体 → **拿不到真实方法体**；但 **`callanalyzer` 可用**（`--use-processor callanalyzer --output-as diffable-cs` → `[Calls]` 注解，可查 Resources.Load/Instantiate/AddComponent 调用者）。
- **UI 素材分布**：`resources.assets`(287 Sprite + 588 GameObject prefab：Iron Nest 自己的 UIFoldout*/UICheckMark/UIElement8px/IronRoadMap/Achievement* + 混入的外部历史策略素材包 cta_*/wu_zhu 等)；`sharedassets0.assets`（主菜单框架 SGRounded/UI Box*/BaseFrame/TitleBorder/key art 等 40 个 + 任务/结算全套，被场景 Image 引用）。
- **✅ `Resources.Load` 对模组可用**（Unity 6000 并入 CoreModule，双平台 interop 暴露；实测 G 端同步 `Load<GameObject>` + 异步 `LoadAsync` 均 OK，无 MissingMethodException；sprite 按 m_Name 加载为 null=嵌套路径）。
- 这些序列化文件都不是 AssetBundle，`AssetBundleIron`/`AssetBundle.LoadFromStream` 读不了。

### 6.2 可行路线（原生路线已实现进 Core）

**路线 ①（推荐，已实现）：原生 Resources 路线 —— 与游戏开发者同法**
- `UiSpriteBank.FromResources(name)`：`Resources.Load<Sprite>` 从 `resources.assets` 按名加载原生 sprite（UIFoldout*/UICheckMark/UIElement8px/IronRoadMap/AchievementBackground 等），缓存。
- `UiSpriteBank.PrefabFromResources(name)`：`Resources.Load<GameObject>` 加载原生 UI prefab（如 `AchievementDisplay`）。
- `UiSpriteBank.CollectSprites(GameObject)`：收集 prefab 实例上所有 Image 引用的 Sprite（去重）——**主菜单框架（SGRounded 等在 sharedassets0）用此法从原生 prefab 带出**（受控，无场景卸载风险）。
- **`UiSpriteBank.CaptureFromScene(pattern)`（现场捕获 + Texture2D 复制防卸载）**：主菜单/提示框显示时扫现场 `Image`，匹配名字（如 `"UI Box"`）的 sprite **复制为自持有副本**（`new Texture2D + SetPixels + Sprite.Create` 带 border/rect/pivot，压缩自动解码 RGBA32）→ 场景卸载后仍有效（无卸载风险）。`NativeGet(name)` 取用。**目标确认（用户 2026-08-23）：`UI Box *` 系列（line/Castile/Boxed Corners/Double line/partial star）就是主菜单提示框/弹框背景**。已接入 `IronNestNativeUi.Poll()`：主菜单加载完成自动 `CaptureFromScene("UI Box")`。
- **B2 运行时验证钩子已加**：`IronNestNativeUi.Hook()` 里 `TryVerifyNativeResources()`；实测 G 端 `Resources.Load<GameObject>("AchievementDisplay")` OK（3 Image）、`LoadAsync` OK（同步/异步均无 MissingMethodException）。
- 位置：`src/OpenNestCore/UI/UiSpriteBank.cs`（Core，双平台 0 错误）。

**路线 ②（备用）：自打包 UI bundle（沿用 player.bundle 流程）**
- **提取**：`tools/extract_ui_sprites.py`（UnityPy）从 `sharedassets0.assets` 导出 sprite → PNG（含 border 清单）。`--match 正则` 按名筛：主菜单框架 40 个已导出到 `ref/ui_sprites_menu/`（`--match "^(key art|SGRounded|SUGGradientRounded|...)"`），任务/结算全套到 `ref/ui_sprites/`。
- **打包**：用 Unity 把这些 PNG 做成 atlas（保持 border 九宫格）→ 打包成 `Models\ui.bundle`（与 `player.bundle` 同流程）。
- Core 侧：`OpenNestCore.UI.UiSpriteBank`（`src/OpenNestCore/UI/UiSpriteBank.cs`）用 **`AssetBundleIron`（Core 已打通的 LoadFromStream 加载器）**加载该 bundle + 缓存 Sprite：
  - `UiSpriteBank.Load("Models/ui.bundle")` / `Get("PanelBackground")` / `LoadAllSprites()` / `Unload()`
  - 生命周期 = AssetBundleIron 引用计数自动管理；常驻模式游戏退出由 `AssetBundleIron.UnloadAll()`（CoopRuntime.Shutdown 已调）统一清理。
- 构建：`UiKit.MakePanel(...)`（9-slice Sliced，四角不拉伸）+ `UiKit.MakeButton(..., bgSprite:)`。
- ⚠️ 需要你提供 `ui.bundle`（含原生 sprite 的名字/九宫格 border），Core 只做"加载 + 缓存 + 9-slice 构建"，与内容无关。

**路线 ③（备选，未实现）：运行时从现场原生 UI 捕获**
- 原生主菜单加载后，`Image.sprite` 引用的 Sprite 已在内存 → `FindObjectsOfType<Sprite>()` / 读原生 `Image.sprite` 捕获原生图集，赋给 `UiKit` 面板。
- 坑：原生 Sprite 属主菜单场景，进任务场景被卸载 → 需复制 `Texture2D` 副本（`new Texture2D + SetPixels` + 重建 Sprite 带 border）自持有，或每场景重捕获。

### 6.3 当前状态

- ✅ 结论定论（用户确认）：UI = **"作者资产（prefab/场景）+ 程序控制（布局/文本/事件）"混合体**（`UINotification` 用 `LayoutRebuilder.ForceRebuildLayoutImmediate`+`ContentSizeFitter` 重排=语言尺寸自适应证据）；目标 = **`UI Box *` 提示框/弹框背景**。
- ✅ 原生路线 Core 侧已实现：`UiSpriteBank.FromResources/PrefabFromResources/CollectSprites/CaptureFromScene/CopySprite/NativeGet` + `IronNestNativeUi` 运行时验证钩子 + 主菜单加载自动捕获（B2），双平台 0 错误。
- ⏳ 待办：①双端实测主菜单自动捕获日志（`UiSpriteBank.capture captured 'UI Box ...'`）；②把模组菜单背景接线到捕获的 sprite（`UiKit.MakePanel(NativeGet("UI Box line"))` 等，CoopUIManager 共享文件需谨慎增量）。
- `UiKit.MakeImage/MakeButton/MakeInputBox` 默认仍是**纯色**（无 sprite 时退回）。
