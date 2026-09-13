# 测试模组与 CLI 自动化（`OpenNestUIKit.Test`）

> **更新记录**：
> - 2026-09-13（二十二）**新增 `chatclose`；摸清 `scroll:` 的寻址陷阱**：
>   ① `chatclose` = 直接收起悬浮聊天层（= ESC/发送后的同一条路径），用于验证“收起后输入管线聚焦与拦截层是否回到常态”。
>   ② ⚠ `scroll:<热区>:±N` 是**子串匹配 + 后登记优先**：写 `scroll:chat-list` 会先命中 `chat-list-scrollbar`（无 `OnScroll`）
>      → 报 `该热区不支持滚轮`；验证聊天浮窗滚动要写 **`scroll:chat-list-viewport:±N`**。
>      成功时 `listprobe` 会打 `滚轮：上次原始=360 → 3 格，发给 'chat-list-viewport'`（方方 3×42=126px）。
>   ③ 本轮用它捶定：菜单关着时 `UiPointerRouter` 不 Active（浮窗热区全死）、`chat-open` 抢点击。
> - 2026-09-13（二十一）**新增 `chatdemo:<条数>`**：不依赖联机会话，直接给悬浮聊天层**灌 N 条假消息**（默认 8）并注册
>   （**故意保持收起态**，要展开就接 `chatopen`）。用途：确定性验证“左侧 Chat 框到底有没有记录 / 收起态背景多透”
>   —— 配合 `listprobe`（行数/内容高/视口/**可见行号区间**）、`chatprobe`（**面板背景 alpha / 列表高**）与 `shot:`。
>   同时 `chatprobe` 的探针串新增 **面板背景 alpha** 与 **列表高** 两项（透明度和几何一眼可验）。
>   本次用它捶定两个 bug：`UiList` 一次重排不收敛（内容高写 0 → `RectMask2D` 裁光）、`UiChatOverlay` 把面板宽写成绝对宽。
> - 2026-09-13（二十）**新增 `clickgame:<对象名片段>`**：点**游戏自己**的 UGUI 按钮（跳过我们注入的 `OpenNestUIKit_*`），
>   例 `clickgame:OpenSettingsBtn` → 剪贴板翻到**原生 Settings 页**，于是能对着**原生控件**截图量颜色/圆角/几何
>   （这是“原生到底长什么样”的唯一可靠办法，比 `pagespec` 的文字 dump 更直观）。
>   配合 `nativeopen:keep`（强制打开 ESC 菜单）使用；没找到时会把见过的按钮名列出来。
> - 2026-09-13（十九）**新增 `dragpick`**：`dragpick:<热区片段>:<起点比例 0..1>:<终点比例 0..1>` ——
>   **起点可指定**的水平拖拽（`drag:` 起点固定在最左侧，复现不了“按住手柄拖动”）。
>   例：`dragpick:nw:slider:0.67:0.2`（把起点放在手柄所在处再拖）、`dragpick:nw:slider:0.2:0.6`。
>   输出带 `UiPointerRouter.LastDragDiag`（看拖拽落到哪个热区）。⚠ 容器内滑条/滚动条不走探针计数，`探针 +0` 是正常的。
> - 2026-09-13（十八）**`pageprobe` 增几何边沿与拦截状态；`shot:` 可用**：
>   ① `pageprobe` 现在打 `GeometryText()`：拖拽条 `轨道/填充/手柄盒/圆块` 与滚动条 `槽/滑动区/手柄` 的
>      **x/y 边沿**（统一换算到页面块本地单位）+ 游戏输入拦截状态（模块启用/停用、世界点击前缀是否已装、已拦次数、
>      原生页压制、交互锁）——“外观到没到端”“鼠标拦没拦住”都能一眼看出（见 `docs/UI_KIT.md` 更新记录三十）。
>   ② `UiSlider.ProbeAll()` 增 `✓ 到端 / ❌ 外观未到端`（比 `实测屏幕比例` 与值比例，光看锚点会漏掉“半宽内缩”那类 bug）。
>   ③ `UiList.ProbeAll()` 增 `滚动条=滑块高/顶距/行程/实测比例`。
>   ④ `shot:<名>`（Unity `ScreenCapture`）本轮**实测可用**（1920×1080 PNG 落到 `OpenNestUIKitLogs\shots\`）——
>      之前“常写不出”不是必然；桌面截取（`runlog\shot_window.ps1`）反而常抓到别的窗口。
> - 2026-09-13（十七）**`pageprobe` 增 `cull`/`强制` 与逐行图尺寸；竖滚动条验方向要用 `vdrag`**：
>   ① 每行文字现在打 `字符/顶点 cull=<真假> 强制=<字符/顶点>`（`ForceMeshUpdate` 后的值）。
>      `cull=True` = 被 `RectMask2D` 裁掉；`cull=False` 但自然值为 0、强制值 >0 = 只是那颗 TMP 还没更新过（帧刚建）；
>      两者都 0 = 真的没字（排查套路与注 [`docs/UI_KIT.md`] 更新记录二十六 一致）。
>   ② 验“拖手柄 → 内容往哪走”**不能用 `drag:`**（它只沿**水平**移动，竖滚动条只会停在中点，看上去像“没反应”）；
>      用 `vdrag:<热区>:<px>`（在热区中心按下、指针**上移** N 像素再松）。
> - 2026-09-13（十六）**`pageprobe` 增滚动状态与下钻；`drag`/`scroll` 立为“到端”验收手段**：
>   ① `pageprobe` 头部新增 `滚动偏移=<当前>/<可用> 内容y 内容高 内容宽 视口`，并不再在 `nw_view` 断掉
>      （以前只列到 `nw_view` 的 sizeDelta，看不到行内几何）。验收例（见 `docs/UI_KIT.md` 更新记录二十八）：
>      `scroll:nw:scrollarea:-99` → `滚动偏移=286.8/286.8 内容y=-286.8`（到底）；`:99` → `0/286.8`（到顶）。
>   ② `drag:<热区片段>:<0..1>` 拖到轨道两端验“最大/最小能真到”：`drag:nw:slider:1` → 页首状态 `滑条=100`，
>      `:0` → `滑条=0`。⚠ 这类容器内滑条**不探针计数**，`drag` 会报 `探针 +0`（不是失败）——
>      看 `gallery` 回写的状态值才准。
> - 2026-09-13（十五）**新增 `pagespec`**（原生控件**样式规格**普查）：逐目标控件打印
>   尺寸/锚点/子节点底图(sprite·色·type·ppuMul·`组件enabled`·**渲染色**)/TMP(字体·字号·色·对齐)/Selectable 过渡色，
>   并附**宿主链**（`Settings menu` 往上到 Canvas 每层的 rect/缩放/active）与该页直接子节点；
>   还按**组件类型**扫 `Settings menu` 子树（`TMP_InputField`/`TMP_Dropdown`/`Slider`/`Toggle`/`Scrollbar`/`Button`）
>   —— 控件是按需实例化的（只有当前选项卡的行存在），按名字找会「没找到」，按类型才稳。
>   例证：本次“主/次按钮白底”“输入框白底”“控件尺寸全不对”三个 bug 都靠它定位（见 `docs/UI_KIT.md` 更新记录二十四 / §7.5）。
> - 2026-09-13（十四）**新增 `gallery` / `galleryoff`**：`gallery` 把测试模组的「控件 Gallery」注册进原生 ESC 菜单
>   （启动时已自动注册）并直接开页；`galleryoff` 撤下（验“provider 注销 → 格子自动消失”）。
>   页内操作会在页首 `最近操作` 行写出刚发生的事（验回调）。⚠ 用 `tap:` 时建议用**子串**寻址（`tap:nw:check`），
>   写全名会因为 `_`→空格 的替换而找不到热区。
> - 2026-09-13（十三）**新增 `pagedump[:页名]` 与 `nativew`**（配合“原生控件页”）：
>   `pagedump` 逐节点打印指定原生页（默认 `Settings menu`）的名字/激活/尺寸/锚点/组件类型 + 关键值
>   （TMP 文案·字体·字号·颜色、Image 的 sprite·type·ppuMul·raycast、Slider min/max/value、
>   Toggle isOn、Dropdown 选项、InputField 文本）—— 默认深度 11（设置页里的滑条/勾选框/下拉框在 `Layout` 组的孙子层，
>   深度小于 9 看不到）。
>   `nativew` 打开**原生控件页演示**（大标题/小标题/检查框/拖拽条/下拉框/选项卡/输入框 + 返回行各一个），
>   并打印是否抄到原生素材与素材名清单；配合 `tap:` 可以验收交互：
>   ⚠ 测试脚本会把参数里的 `_` 换成空格，而热区名里用 `_` 代替了空格 → **用子串寻址**（`tap:nw:check`、`tap:nw:dd`），
>   写全名会 “找不到热区”。实测 `tap:nw:dd` 展开后 `zones` 里会多出 `nw:ddopt:*` 三个选项热区。
> - 2026-09-13（十二）**定位“注入行没字”的最后一招：把数值探针当判据（不靠截图）**：
>   新增 **`clipcanvas`**（把原生 ESC 画布**临时**改成 `ScreenSpaceOverlay` 再截图后还原 ——
>   原生 ESC 画布是 `WorldSpace`，普通截图里看不到）+ **`inactivetest`**（父物体 inactive 时建 TMP，之后激活，看能否出字形）。
>   并给 `nativebtn` 加 **X/Y 对照行**：同画布、同代码，**只差文字框高度**，各自打印
>   `行数 / 字符数 / 可见字符 / 顶点数 / 首选高 / 行高`（`ForceMeshUpdate` 之后）——
>   实测 `X 文字框18高 + Ellipsis → 行数=0 字符=0`、`Y 文字框铺满 → 行数=1 字符=6`，
>   这条数字对照直接坐实了真因（见 `docs/UI_KIT.md` 更新记录十八）。
>   ⚠ `ScreenCapture.CaptureScreenshot` 在本机**经常写不出文件**（只警告不报错，`shots\` 里没新图）→
>   验收优先用数值探针，截图只当辅助（同一次运行里 `escsnap` 能写出、`nativebtn` 写不出就是这种情况）。
> - 2026-09-13（十一）**修掉一个一直存在的老 bug：`wait:` 从来没真等过** —— 它写完 `_timer` 就调 `Done()`，
>   而 `Done()` 会把 `_timer` 清零 → 命令之间“零等待”。很多偶发问题（刚重建完页面就 tap → 找不到热区、
>   命令挤在一帧里）都是它。现在 `wait:` 自己收尾（清 `_cur/_step`、保留 `_timer`）。
>   新增命令：`esctext`（�²入行 vs 原生模板逐项对比：底图/底色/字体/材质/着色器/字号/颜色/文本矩形/字符数顶点数，
>   以及 **Selectable 的过渡/normalColor/tint 目标**）、`escframe:<名>`（临时激活原生 ESC 容器祖先链几帧后截图再还原）、
>   `nativebtn`（在我们自己的画布上用同一段 `CreateNativeButton` 复现注入行，做 A/B 对照）、
>   `nativeopen`（调游戏自己的 `EscapeMenuToggleUnityEvent.ForceOpen`）、`chatopen`、`key:<enter|escape|字母>`。
>   ⚠ 实测 `key:`（`InputSystem.QueueStateEvent` + `KeyboardState`）在本环境下**没能真的让 `Keyboard.current`
>   看到该键**（`isPressed` / `wasPressedThisFrame` 都读不到）→ 涉及“真按键”的验收不要只靠它，
>   优先 `type:`（往输入框塞文本）+ 产品侧探针，并让人实机确认。
> - 2026-09-13（十）新增 **`esctext`** / **`escframe:<名>`**（排查“注入到原生 ESC 菜单里的字看不见”）：
>   `esctext` 把每个 `ESC Menu Buttons` 容器的祖先链**临时激活**，逐行打印 **原生模板按钮 vs 我们注入行** 的
>   底图/底色、字体、材质、着色器、字号、颜色、文本矩形、**字符数/顶点数**（顶点 0 = 画面上什么都不会有），
>   跑完原样还原；`escframe:<名>` 则临时激活几帧 + 截一张图 —— 自动化里按不了 ESC，这是唯一能“看到”
>   原生菜单里注入行长什么样的办法。
>   ⚠ 两个读数陷阱：① 容器没开时 `menutree` 的 `/off` 只是 `activeInHierarchy`（容器关了），**不代表字有问题**；
>   ② `ForceMeshUpdate` 对**从未真正显示过**的文字不重建 → `字符数` 会读到 0，看**顶点数**与“与原生是否一致”更可靠。
>   ⚠ 同一帧内重建会在 dump 里看到重复行（`Destroy` 帧末生效），下一帧就正常，不是重复注入。
> - 2026-09-13（九）**两条寻址经验（都是实机踩出来的）**：
>   ① `FindZone` 是**连续子串**匹配（且从后往前取最先命中 = 最后登记的那个），所以名字里夹了前缀的控件
>      不能带前缀去找：模组列表行热区叫 `row:[BepInEx] Open Nest Co-op  0.2.1-Alpha-2`，
>      写 `tap:row:Co-op` 会失败（`row:` 后面紧跟 `[`，不连续）→ 用**唯一子串** `tap:Co-op`。
>      （同理：同一页面里两个页签栏的热区名都是 `tab0/tab1/…`，`tap:tab1` 命中的是**后登记**那一个 ——
>      ModMenu 页左侧筛选页签先建、右侧详情页签后建，所以 `tap:tab1` = 右侧的「设置」。）
>   ② `click:` 会校验**探针增量**，而输入框/开关这类行控件不涨探针 → 会报 `FAIL … 探针 +0`（假阴性）；
>   对它们用 `tap:`（或 `click:` 后无视那一条），真正的证据看 `widgetprobe` 里的 `聚焦='…'（文本='…'）`
>   与落盘后的配置文件。
> - 2026-09-13（八）新增 `chatprobe`；`widgetprobe` 增打**输入管线 / 语言探测 / 悬浮聊天层**；
>   `menutree` 对注入行额外打印 **文字/颜色/文本区尺寸**（一眼定位“注入按钮的字没了”这类问题）。
>   热区命名：内嵌列表 = `<key>-viewport` / `<key>-scrollbar`（如 `mm.list-scrollbar`），聊天层 = `chat-*`。
> - 2026-09-13（七）新增 `widgetprobe` / `type:<文本>`：
>   `widgetprobe` 一次打印**滑块/输入框/打字模式/列表滚动条**四组状态（含滑块的“锚点比例 vs 实测屏幕比例”、
>   输入框当前路径（原生/托管）、游戏输入模块启用数、EventSystem 记录与激活状态、每个列表的滚动条是否显示），
>   是验证“输入框能打字 / 拖动点位置对不对 / 内容超出有没有滚动条”的直接证据链；
>   `type:<文本>` 向当前聚焦的输入框“打进”一段文本（原生路径下**只改 TMP 框自己的 text**，
>   用来验证“每帧回写 → 触发 OnChanged”这条链路，而不是伪造结果）。
>   声明式行的热区名统一成 **ASCII 行 key**：`input:<key>` / `slider:<key>` / `toggle:<key>`，列表另有 `scrollbar`。
> - 2026-09-13（四）新增 `perf[:reset]`：本模组帧耗时（Update/LateUpdate 平均与最大，滚动窗口）+ 拦截层耗时拆分（Apply / 交互锁收集 / 原生搜索 / 入队）；用它对拦“防止指针穿透“优化前后做数字对比。
> - 2026-09-13（五）新增 `listprobe` / `vdrag:` / `lock:`，`scroll:` 改为发**真实滚轮原始值**；
>   `listprobe` 同时输出“上次滚轮原始值+分发对象”与“拖拽仲裁诊断”，是定位“滚动方向反了 / 行数变少”的关键证据链。
> - 2026-09-13（三）新增 `escprobe / pageprobe / clipopen`；`pageopen` 在测试期置 `NativeMenuPage.DebugKeepAlive`
>   （剪贴板没真打开时不被 Tick 立刻复位，便于 `pageprobe` 读行）；`clicknative` 的参数容忍脚本把 `_` 换成空格。
> - 2026-09-13（六）`escprobe` 增打 **`★层级=N[owner,...]`**（ESC 层级唯一真源），用来验证“无层级时守卫必须已放行”：
>   实测 `open:list` → `层级=1[window] blocked=True`；`close` → `层级=0[] blocked=False`；
>   `pageopen` → `层级=1[native-page] blocked=True`；`pageclose` → `层级=0[] blocked=False`（G 端主菜单，`PASS=6 FAIL=0`）。
> - 2026-09-13（二）新增原生菜单与布局诊断命令：`menutree / injectdiag / uninject / escmode / clicknative / pageopen / pageback / pageclose / rects / chain`；
>   并用它们定位并修掉“窗口开着但内容看不见”的两个真因（`pageHost` 负高 + `UiList` 没铺满宿主，见 `docs/UI_KIT.md` §5.2）。
>
> 目的：用**另一个模组**从"外部调用方"的角度验收 `OpenNestUIKit` 的全部能力，
> 并且**不靠人手点** —— 用命令行脚本驱动"模拟点击/拖拽/滚动 + 断言 + 报告"。
> 这样每次改动都能一条命令回归，而不是让人反复开游戏试。

> **更新记录**
> - 2026-09-12 建档：工程与部署、覆盖面、CLI 命令表、模拟点击为何可信、实测 `PASS=16 FAIL=0`。
> - 2026-09-12（二）新增切片验证命令 `slices` / `slicereload` / `sliceprobe`（§3 命令表）+ §4.1 切片定义实机验收。
> - 2026-09-12（三）新增 `tags` / `tagprobe:<tag>` / `nativecheck`（§3 命令表）+ §4.2 用途标记与“不侵入游戏”取证。

## 一、工程与部署

| 工程 | 说明 |
|---|---|
| `src/OpenNestUIKit.Test/` | BepInEx 壳 + 主体（引用 `OpenNestUIKit.API` 与 `OpenNestUIKit`） |
| `src/OpenNestUIKit.Test.MelonMod/` | MelonLoader 壳（共用同一份源码；引用 UIKit 的 ML 版主体） |

```powershell
dotnet build src\OpenNestUIKit.Test\OpenNestUIKit.Test.csproj -c Release -p:DeployToGame=true   # G 端
dotnet build src\OpenNestUIKit.Test.MelonMod\OpenNestUIKit.Test.MelonMod.csproj -c Release -p:DeployToMods=true   # D 端
```

报告文件：`<Game>\OpenNestUIKitLogs\test.log`（与 UIKit 的诊断日志同目录，便于对照）。

## 二、测试模组覆盖了什么

| 覆盖面 | 位置 | 说明 |
|---|---|---|
| **契约路径**（第三方，纯 .NET） | `TestProvider.cs` | 只引 `OpenNestUIKit.API`，注册整棵菜单树（全部行类型 + 子页导航 + 回写回调）；启动时记录 `hostAvailable` |
| **直接调组件路径** | `TestPages.cs` | 页面/列表/流式布局/全部组件由**外部模组**亲手构建（验证公开 API 够用） |
| **按钮素材（九宫格）** | `test.buttons` | 4 风格 × 5 高度 + 4 种窄宽度，每颗都如实报告"用了哪张图 / border 多少" |
| **原生菜单注入** | `test.api` | 加/删测试入口 + 强制重注入 + 状态（`patched` / `injected` / `entries`） |
| **输入守卫 / 层级** | `test.api` | 一键打印 `UiInputGuard.LogProbe()` 环境快照 |
| **原生素材** | `test.buttons` | 开关 `UiSkin.UseNative` / 重新捕获 / 清空，对照纯色与原生的差别 |
| **热区与点击** | `test.zones` | 热区清单（CLI 靶子）+ 探针命中计数 |

## 三、CLI

```powershell
# 自动开菜单（6 秒后）
-onuktest-autoopen             # → 打开 test.home
-onuktest-page=test.buttons    # 直达某页
-onuktest-zones                # 打开菜单后打印一次热区清单

# 脚本化（命令用 ';' 分隔；参数里的空格写 '_'）
-onuktest-run=wait:9000;open:test.home;wait:900;zones;click:nav:buttons;wait:900;assertPage:test.buttons;click:btn:P24;wait:500;assert:btn:P24:1;drag:slider:main:0.8;inject;report
```

| 命令 | 作用 |
|---|---|
| `open[:pageId]` / `close` | 开/关菜单（可直达页面） |
| `nav:<pageId>` / `back` / `home` | 页面栈导航（Push / Pop / 回主页） |
| `click:<热区名片段>` | **模拟真实点击**：移到控件中心 → 按下 → 抬起 → 用**探针增量**判定 PASS/FAIL |
| `tap:<热区名片段>` | 同 `click` 但不校验探针（用于非探针控件，如库自带的返回按钮） |
| `move:<热区名片段>` | 只悬停，并校验**命中测试**真的落在该控件上 |
| `drag:<热区名片段>:<0..1>` | 模拟拖拽（滑条按轨道比例） |
| `scroll:<热区名片段>:<±N>` | 注入**真实滚轮原始值**（N×120，一格=120）走完整路径（能查出方向/步长对不对；早期只发 ±1 → 一格跳 4 屏） |
| `wait:<ms>` / `zones` / `dump` / `report` | 等待 / 打印热区 / 打印状态摘要 / 写报告 |
| `theme:on|off|recapture|clear` | 切换/重捕/清空原生素材 |
| `inject` | 强制原生菜单注入 |
| `assertPage:<pageId>` / `assert:<探针键>:<次数>` | 断言（当前页 / 探针命中次数） |
| `slices` | 打印当前切片定义表（条数 + 来源文件路径 + 逐条 `border/scale/mode/center`） |
| `slicereload` | 手动重载切片定义文件（并清烘焙缓存；平常靠 2 秒热重载） |
| `sliceprobe:<名>` / `sliceprobe:?<名>` | 打印某张图的作者值/定义值/各尺寸实际采用的 sprite 与 border；**不带 `?` 则要求"定义必须被采用"**（否则 FAIL），带 `?` 只观察不判定 |
| `tags` | 打印用途标记清单（每个 tag 下的素材，并标 `✓已捕获` / `✗未捕获` —— 后者意味着该定义/标记不会生效） |
| `tagprobe:<tag>` | 按 tag 取素材的验证（库里能不能按你标的用途挑到）；同时打印命中的素材与哪些已捕获 |
| `nativecheck` | **取证“不侵入游戏”**：分“我们的节点/游戏节点”统计，断言游戏节点没用我们的切片副本，并列出与定义同名的游戏图其 border 仍为原值 |
| `menutree` | 原生 ESC 容器快照（逐个子节点的名字/尺寸/y/字号/底图@ppuMul，并标出哪些是我们注入的） |
| `injectdiag` | 注入决策链诊断（顶层条目数/签名/容器数/每个容器：模板、原生按钮数、设置索引、节距、顶部 y、原生跨高、面板可用高、非激活槽位、放不放得下） |
| `uninject` | 移除我们注入的行（配合 `menutree` 对比，验证**原生行的 y 没被我们动过**） |
| `escmode[:off]` | **只读报告**原生 ESC 面板与按钮列的 active 状态（❗早期版本会强开/强关面板 Canvas，会把游戏的剪贴板状态搞乱 —— 已改成只读） |
| `clicknative[:名字片段]` | 点我们注入到**原生菜单**里的那一行（原生按钮不是本库热区，`click:` 点不到） |
| `pageopen[:标题]` / `pageback` / `pageclose` | 直接驱动原生面板内那页（打开 / 返回上一级 / 关闭并恢复原生列） |
| `rects[:数量]` | 打印我们窗口下图形/文字的**真实矩形 + 颜色 + 无图形节点（带锚点/偏移）**，用于判断“内容看不见”是没建、摆错位、还是 alpha=0 |
| `chain` | 从列表视口往上打印**祖先链**的名字/矩形/锚点/偏移/sizeDelta（一步定位“哪层尺寸不对”） |
| `escprobe` | ESC 全链路状态（只读）：窗口/原生页/ESC 守卫/游戏 ESC/剪贴板面板与三个同级页的 active |
| `pageprobe` | 我们的原生页行探针：层级/标题/**滚动偏移·内容 y·内容高宽·视口**/行数/每行名字·文案·y·尺寸·字号·底图@ppuMul；新增：钻到 `nw_view/nw_content` 里逐行列出（名字@y/高 + 第一张非阴影底图的 sprite·色·**渲染色** + 第一颗 TMP 的字号·色·字体） |
| `pagespec` | 原生控件**样式规格**普查：尺寸/锚点/底图/色/type/ppuMul/组件enabled/**渲染色**/Selectable 过渡色 + 宿主链（`Settings menu` 往上到 Canvas）+ 按组件类型扫 `Settings menu` 子树 |
| `pagespec` | 原生控件**样式规格**普查（尺寸/锚点/底图/色/type/ppuMul/组件enabled/**渲染色**/Selectable 过渡色）+ 宿主链 + 按组件类型扫 `Settings menu` 子树 |
| `clipopen` | 尝试用**游戏自己的入口**（路径含 `Clipboard` 的 `LookAtTarget.OnClickDown`）打开剪贴板，便于截“真实原生菜单” |
| `perf[:reset]` | 打印本模组的帧耗时（Update/LateUpdate 平均与最大，滚动窗口）+ 拦截层耗时拆分（Apply / 交互锁收集 / 原生搜索 / 入队）；`perf:reset` 清零后测更准 |
| `listprobe` | 所有存活列表的状态：行数/内容高/视口/可滚量/当前偏移/**可见行号区间**/**滚动条是否显示** + 上次滚轮原始值与分发对象 + 拖拽仲裁诊断 |
| `widgetprobe` | 五组状态一次打完：滑块（值/范围/比例/**锚点比例 vs 实测屏幕比例**）、输入框（文本/聚焦）、**输入管线**（IME 锚点/组合中/最近原生提交）、**悬浮聊天层**、**语言探测**、拦截层（模块启用数）、列表（内容高/可滚/滚动条） |
| `chatprobe` | 悬浮聊天层状态：注册 / id / 展开 / 行数 / **面板背景 alpha / 列表高** / 输入框聚焦 / 菜单是否打开 |
| `chatdemo:<条数>` | **注入假聊天**（默认 8 条；不依赖联机会话）并注册悬浮聊天层（保持收起态）—— 验证“框里有没有记录 / 收起态透明度”；接 `chatopen` 看展开态，配 `listprobe` 看几何 |
| `chatclose` | 收起悬浮聊天层（= ESC/发送后的同一条路径）；配 `widgetprobe` 看输入管线聚焦与拦截层是否回到常态 |
| `type:<文本>` | 向当前聚焦的输入框斟入文本（原生路径只改 TMP 框 text，验证“回写 → OnChanged”链路） |
| `vdrag:<热区>:<像素>` | 垂直拖动（屏幕坐标向上移 N 像素再松手）——验证“按住拖动滚动”（行盖在视口上时要靠点击/滚动仲裁） |
| `lock:on|off` | 交互锁开关（默认 **off**；on = 旧行为：禁用场景里的交互组件） |
| `mockcfg[!]` | **写测试模组自己的模拟配置文件**（`BepInEx\config\open.nest.uikit.test.cfg`）：`mockcfg` = 缺失才写、`mockcfg!` = 强制重建。启动时也会自动补一次（缺才写）。 |
| `imefake:<文本>` | **冒充 `GCS_RESULTSTR` 的返回值**（空格写 `_`；空 = 清除）——复现/回归“持值串回填”（同一条串会被反复返回 ⇒ 重新聚焦时**不得**再补进空框）。真实输入法没法自动化，这是它的可回归替身。 |
| `imecomp:<文本>` | **冒充一帧的 `compositionString`**（空 = 清除）——下一帧它就“结束”从而触发“组合结束”那条通道；与 `imefake` 搭配可离线造出“同一次提交被两条通道各送一次”的重复场景。 |
| `imedecide:<native>\|<cached>\|<cur>` | **IME 提交判据回归**（纯函数，不需要真输入法）：打印两条通道的决策（`native=append/skip(无CJK=拼音)`、`cached=append/skip`）—— 锁住“拼音不进框、汉字才追加、不重复追加”这套规则（见 `docs/UI_KIT.md` 更新记录四十六）。 |

切片相关命令细节与定义文件格式见 `docs/UI_KIT_SLICE.md`。

### `mockcfg`：验收“自动生成的配置控件”（2026-09-13 六）

用户：“给测试模组做个模拟配置文件，看看自动生成的配置控件够不够好用”。

```powershell
steam.exe -applaunch 2950790 -onuktest-run="wait:12000;mockcfg;open:provider:open-nest-mod-menu;wait:2500;tap:pick:mm.list:<行号>;wait:1600;tap:tab:mm.tabs:1;wait:2000;shot:p5;report"
```

- 文件落到 `BepEx\config\open.nest.uikit.test.cfg`（BepInEx 的 `<GUID>.cfg` 惯例；MLL 原生端退 `UserData\`）
  ⇒ 模组菜单靠加载器元数据**直接命中**，不需要任何映射。
- 覆盖项：bool / int + 范围 / float + 步进 / 枚举（含中文选项）/ 快捷键 / 文本 / **空文本**（验“空值要画成可输入的空框”）/
  只读（声明 `ReadOnly` 才真只读）/ 无类型声明（按值推断）/ 多 `[Section]` / 项目风格“键后注释”元数据（`## 类型: 数值 | 默认: 3 | 范围: 0..10`）。
- 控件由 `ModMenuSettingsSource.Rows()` → `UiSetting` → UIKit `AddRow`（或自带 `ModMenuSettingsView`）生成：
  **两套界面同一数据源**，所以两边应该看到同样的行；实测该文件被解析成 **13 项**并全部绑上控件。
- 配套钩子：`-onnmm-selftest-tab=settings:<Id 匹配串>`（ModMenu 侧）可自动选中该模组 + 切到设置页；
  行号可以用 `listprobe:mm.list`（行数/可见行区间）+ 左栏截图确认（行热区名 `pick:mm.list:<i>`）。

### 为什么"模拟点击"可信

模拟不走"直接调回调"，而是给 `UiPointerRouter` 注入**指针位置**与**左键状态**：

```csharp
UiPointerRouter.PointerOverride = 控件中心屏幕坐标;   // 覆盖真实设备读数
UiPointerRouter.ButtonOverride  = true;             // 按下
// 下一帧 → false（抬起）→ 路由器按常规路径派发 悬停/按下/点击/拖拽
```

也就是说，被测的是**和真实鼠标完全相同的那条路径**：命中测试（`RectTransformUtility`）、
层级/最上层优先、按下→抬起同区才算点击、拖拽独占指针。控件回调里调 `TestProbe.Hit(key)`，
驱动程序对比点击前后的命中计数 → 给出 PASS/FAIL。

热区名可寻址性：`UiButton`/`UiNavRow`/`UiSlider` 都有可选 `zoneName` 参数，
测试页用**语言无关的 ASCII token**（`btn:P24`、`btn:N64`、`nav:buttons`、`slider:main`、
`btn:probeA`），因此中英文环境下脚本都一样跑。

## 四、实测结果（2026-09-12，G 端 BepInEx + 桥）

```
[PASS] 契约注册 — 宿主在线（UIKit 已加载）        providers=2
[PASS] 测试页注册 — pages=12
[NOTE] zones — 9 个 → btn:✕ | btn:‹ Back | list-viewport | nav:buttons | nav:widgets | nav:zones | nav:api | nav:test | btn:Refresh
[PASS] open test.home — isOpen=True page=test.home
[PASS] click nav:buttons — 探针 +1（点击真的走到了控件回调）
[PASS] assertPage test.buttons
[PASS] assert nav:buttons ≥ 1 — 实际=1
[NOTE] dump — menu open=True page=test.buttons order=32766 zones=38 host=0.1.0 pages=19 skin=13 nativeInjected=True
[PASS] click btn:P24 — 探针 +1
[PASS] click btn:N64 — 探针 +1
[PASS] nav test.zones — page=test.zones
[PASS] click btn:probeA — 探针 +1
[PASS] drag slider:main → 0.8 — 探针 +13
[PASS] theme recapture — new=3 cached=16
[PASS] inject — patched=True injected=True entries=2
[NOTE] report — PASS=16 FAIL=0
```

### 4.1 切片定义采用验收（2026-09-12）

先把**非作者值**写进切片定义文件并部署（`tools/slice_tool.py --set … --deploy`，见 `docs/UI_KIT_SLICE.md`）：

```
UI Box line    → border=6,6,6,6   center=0        （作者原值 14,14,14,13）
UI Box Castile → border=10,10,10,10                （作者原值 18,34,18,16）
```

```
-onuktest-autoopen -onuktest-run=open:test.buttons;wait:60;slices;sliceprobe:UI_Box_line;sliceprobe:UI_Box_Castile;report

[NOTE] slices — 切片定义 18 条 ← …\BepInEx\config\OpenNestUIKit.slices.ini
[NOTE] sliceprobe — sprite='UI Box line' author=(14.00, 14.00, 14.00, 13.00) def=border=(6.00, 6.00, 6.00, 6.00) … mode=sliced center=0
[PASS] sliceprobe UI Box line — 已采用切片定义：border=(6.00, 6.00, 6.00, 6.00)
[PASS] sliceprobe UI Box Castile — 已采用切片定义：border=(10.00, 10.00, 10.00, 10.00)
[NOTE] report — PASS=5 FAIL=0
```

`uikit.log` 同屏 5 档尺寸（24/32/40/56/120）全部记为
`按钮素材 = 'UI Box line@6,6,6,6'（用户切片；…）` —— 定义优先于自动选材，且尺寸变化不会把它改成别的图。

### 4.2 用途标记 tag 与“不侵入游戏”取证（2026-09-12）

```
-onuktest-run=wait:120;slices;tags;tagprobe:panel;tagprobe:separator;tagprobe:input;nativecheck;report

[NOTE] tags — 用途标记 tag 6 种（定义共 18 条；已捕获素材 17 个）
  dialog           UI Box Boxed Corners✓
  frame            SGRounded✓
  input            InputFieldBackground✗未捕获
  panel            UI Box Castile✓ UISprite✓
  panel.small      UISprite✓
  separator        UI Box line✓
[PASS] tagprobe panel — 按标记采用：UI Box Castile
[PASS] tagprobe separator — 按标记采用：UI Box line
[PASS] nativecheck — 游戏原生 UI 未被揻改（我们的节点用自己的副本 27 张）
[NOTE] report — PASS=6 FAIL=0
```

另一次专门验证：把 `UI Box Castile` 改成**非作者值** `8,8,8,6` 并开我们的菜单后：

```
[PASS] sliceprobe UI Box Castile — 已采用切片定义：border=(8.00, 8.00, 8.00, 6.00)   （作者原值 18,34,18,16）
[NOTE] nativecheck — 场景 Image=1942：我们的节点 29 / 游戏节点 1609
  ① 游戏节点使用我们的切片副本 = 0（必须 0）
  ② 我们的节点使用自己的副本 = 27（>0 证明切片定义在生效）
  ③ 与我们定义同名的游戏图 = 681（它们的 border 仍是游戏原值）
  'UI Box Castile'（游戏节点 OpenNest_CoopEsc）：游戏它自己用 border=(18,34,18,16)｜我们的定义 border=(8.00, 8.00, 8.00, 6.00)
[PASS] nativecheck — 游戏原生 UI 未被揻改
```

## 五、注意事项

1. 测试模组**引用 UIKit 主体程序集**（不是软依赖）——它要访问 `UiPointerRouter` 等内部能力，
   所以是"开发/验收工具"，生产环境可以不部署；契约路径的验证仍只走 `OpenNestUIKit.API`。
2. 脚本里的热区名片段是**包含匹配**、且**后登记（上层）优先**：`click:btn:P` 会点到第一颗匹配的按钮；
   要精确就用带尺寸 token 的名字（`btn:P24`）。
3. `wait:` 是必要的：菜单打开/页面切换/素材捕获都要等一帧以上；实测 400~900ms 足够稳。
4. 跑脚本前别手动抢鼠标（注入期间会覆盖指针位置）。
5. 切片相关：`sliceprobe` 靠**名字**寻图，名字里空格写 `_`（与其它参数同规则）；`slices` 的条数
   与来源路径是“定义文件到底被没被读到”的第一取证点。6. **`click:` / `drag:` 的 FAIL 不等于功能坏了**：它们的 PASS/FAIL 来自**探针计数**，
   对画廊/演示页这类“只是改状态文字”的控件会报 `探针 +0`（属于假阴性）；用 `tap:` / `drag:` 后截图看状态文字为准。
7. **截图里有别的模组窗口**：先 `hideothers`（临时藏掉其它模组的 ScreenSpaceOverlay 画布，`hideothers:off` 恢复）。
8. **原生 ESC 面板没法脚本打开**：它是游戏交互打开的（还要播展开动画），
   `escmode` 只能报告状态，**不能**强开（强开会把游戏自己的剪贴板状态搞乱）。
   因此“注入行在原生菜单里的最终观感”请以游戏内实际按键打开为准；脚本侧用 `menutree` / `injectdiag` 的数字验证。
9. **部署前必须关游戏**：dll 被占用会报 `MSB3021/MSB3027`（不是代码错）——先 `Stop-Process -Name 'Iron Nest Heavy Turret Simulator'`。