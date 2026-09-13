# 原生 UI 库（OpenNestUIKit）设计

> **目的**：新建**独立模组** `OpenNestUIKit` —— 一个"原生 UI 库"类模组，把游戏原生 UI 的能力与观感做成**可复用的库**：
> 1. **动态布局**：内容驱动测量 → 自动排列（文字换行自适应高度、语言切换/内容变化后重排）；
> 2. **完整基础组件**：窗口/面板/标签/按钮/开关/滑条/步进/选择/输入框/快捷键/进度/分隔线/页签/列表/滚动/提示/模态框；
> 3. **可修改游戏原生 UI 菜单**：注入项、重排、抄样式；
> 4. **子菜单**：原生菜单里点击进入我们的子菜单并可返回原生；菜单内**多级页面栈**（主页 → 子页 → 返回）；
> 5. **自带几个菜单**：组件总览 / 列表页 / 原生观感 / 原生注入 / 关于（既是演示也是自检）。
>
> **与既有模组的关系**：`OpenNestUIKit` 与 `OpenNestCoop` / `OpenNestCore` / `OpenNestModMenu` **程序集级独立**
> （不引用它们的程序集），只做"源码 Vendor 拷贝 + 改独立命名空间"复用（与 `OpenNestModMenu` 同法，见 `docs/MOD_MENU.md` §1.2）。
> 第三方模组通过 `OpenNestUIKit.API`（纯 .NET 契约程序集，**软依赖**）把自己的菜单页注册进来。
>
> **已定决策（2026-09-12，用户确认）**：
> 1. **形态** = 新建独立模组 `OpenNestUIKit`：`OpenNestUIKit.API`（契约，无 Unity 依赖）+ `OpenNestUIKit`（BepInEx 壳 + 主体）+ `OpenNestUIKit.MelonMod`（ML 壳，共用主体源码）。
> 2. **目标菜单** = ① 自带多级菜单窗口 ② 原生主菜单/ESC 菜单注入入口 ③ 列表/设置页（行渲染 + 控件复用）④ 原生观感（UI Box 边框 + 原生字体 + 过渡）。
> 3. **原生菜单改造程度** = **可插入多级页面项**（原生菜单里点击进入我们的子菜单，关闭后回到原生菜单）。
> 4. **双端** = BepInEx + MelonLoader。
>
> **复用来源（读代码核实）**：
> - `src/OpenNestCore/UI/{UiKit.cs,INativeUiService.cs}`（Vendor 拷贝改名 `OpenNestUIKit.UI`）、`src/OpenNestCore/Logging/{ILogger,CoopLog,ModLog}.cs`（Vendor 拷贝改名 `OpenNestUIKit.Logging`）；
> - `src/OpenNestModMenu/`：主题分层（`UI/ModMenuTheme.cs`）、行模型 + 行池列表（`UI/ModMenuListView.cs`、`UI/ModMenuSettingsView.cs`）、
>   输入守卫与自管指针（`UI/UiInputGuard.cs`）、自绘光标（`UI/UiCursorOverlay.cs`）、ESC 守卫（`UI/UiEscapeGuard.cs`）、
>   声明式第三方契约（`src/OpenNestModMenu.API/IModMenu{Provider,Page}.cs`）、工程/双壳/Vendor 手法；
> - `src/OpenNestCoop/UI/MainMenuEntry.cs`：**原生菜单注入的实证做法**（patch `MainMenuStateRelay.HandleMainMenuLoaded` postfix、
>   全场景遍历 `ESC Menu Buttons`、slot 让位、抄模板样式、字号必须固定 + 关 autoSizing）；
> - `docs/NATIVE_UI.md`：游戏原生 UI 全景（本地化/通知/ESC/主菜单阶段/虚拟光标）+ 素材（`UI Box *`）研究结论。
>
> - 2026-09-13（五）**注入与皮肤策略定稿（用户确认）+ 修掉“窗口内容看不见”的两个真因**：
>   ① 皮肤三分：**注入到原生 ESC 列表的入口 = 原生样式**（抄设置按钮：`UI Box Castile@2.5`/字号 20/250x38）、
>      **原生面板内那页（`NativeMenuPage`）= 原生样式**、**我们自己的独立窗口（`UiMenuWindow` 各页）= 第三方自有样式**
>      （`Theme/ModStyle.cs` + `Theme/UiSkin.UseNative=false` 默认）；
>   ② 摆位改成“**参照原生块布局、只追加不动别人的尺寸**”：见 §六；
>   ③ 修 `UiMenuWindow` 内容区 `pageHost` 锚点错（`(0,1)-(1,1)` 却按四边内缩给 offset → 高度 **-52**）
>      与 `UiList.Create(host, 0)` 没铺满宿主（视口 300x0）——两者叠加导致 `RectMask2D` 把整页裁光。见 §5.2；
>   ④ 新增测试命令 `escmode / clicknative / injectdiag / uninject / rects / chain / pageopen / pageback / pageclose`（见 `docs/UI_KIT_TEST.md`）。

> **更新记录**：
> - 2026-09-13（四十六）**中文输入：拼音尾巴 + 一次“切不了了”的回归（结论修正）**（用户：“中文输入会跟随拼音变成‘中文zhong'wen’这样” → 修完又报“中文输入切不了了”）：
>   ① **通道模型（之前判反了，这里是实测修正）**：`GCS_RESULTSTR`（原生串）在某些输入法/Unity 组合下给的是
>      **未转换的拼音**（`zhong'wen`），而 **转换后的汉字在“组合结束那一刻”的 `compositionString`** 里。
>      ⇒ 单纯按“已收到原生提交就不再追加组合串”去挡，就会**把唯一能送汉字的通道关死**（中文完全进不来）。
>   ② **最终判据（两条路统一，纯看内容不看来源）**：
>      · `ShouldAppendNative(native)` —— 只收**含 CJK** 的（拼音串丢弃）；
>      · `ShouldAppendComposition(cached, cur, lastNative)` —— 含 CJK + 与现有文本不重复（**不再有“是否已收到原生提交”这种前置条件**）。
>   ③ 同帧双通道去重：`_nativeCjkFrame`（只有**真的追加了含 CJK 的原生串**才标记），同帧内 `OnTextInput` 不再追加。
>   ④ **可离线回归**：新增 `UiTextRouter.ImeDecision(native, cached, cur, lastNative)`（纯函数）
>      + 测试模组命令 `imedecide:<native>|<cached>|<cur>`（空格写 `_`）——不需真输入法就能锁住这套判据。
>      实测 6 组：`native=zhong'wen` → skip（拼音不进框）；`native=中文` → append；
>      `native=中文,cached=中文,cur=中文` → native append / cached skip（不重复）；两者皆拼音 → 都不追加。
>   ⑤ IME 诊断（原生提交/拼音跳过/组合结束/OnTextInput CJK）由 `CoopLog.Debug` 升为 **Info**：
>      以后再出这类问题，`uikit.log` 直接能看到是哪个通道在送什么串。
>   ⑥ **第二轮（用户：“系统的输入法在打开输入框的时候切换不了中文输入模式，只能在英文输入模式”）**：
>      这是**系统层**问题，不是提交逻辑 —— Unity 只在 `TMP_InputField` **真正拿到焦点**时才把
>      `Input.imeCompositionMode` 置为 `On`；我们的窗口在自己的画布上、游戏 EventSystem 常常禁用 ⇒
>      焦点不生效 ⇒ 组合模式停在 `Auto/Off` ⇒ OS 没有把 IME 上下文关联到窗口 ⇒ **输入法锁在英文/直接模式**。
>      实测探针：`IME 上下文=未关联（系统输入法会被锁在英文模式）`。
>      修法：聚焦时**显式**把组合模式置 `On`（`Input.imeCompositionMode` 在 IL2CPP interop 里没暴露 ⇒ 用**反射**，
>      游戏里确实有该属性，见 `tools/dump_inputlegacy.txt`；失焦恢复原值）；仍在 `EventSystem.SetSelectedGameObject +
>      ActivateInputField + Keyboard.SetIMEEnabled(true) + SetIMECursorPosition` 全套；
>      再加**兜底**：聚焦后 0.4s 复查 `ImmGetContext(hwnd)`，仍为 0（=没关联）就 `ImmCreateContext + ImmAssociateContext`。
>      实测日志：`IME 激活：组合模式=On（进入前=Auto）` → `IME 上下文已由引擎关联（正常）`
>      → **`IME 原生提交 added='中文'`**（真汉字入库，用户实机同时间输入）。
>      `widgetprobe` 已带上 `组合模式 / 进入前 / 锚点焦点 / EventSystem / hwnd / IME 上下文` 六项，看一眼就知道卡在哪层。
>   ⑦ **第三轮（用户：“中文输入之后回车发送之后又唤起输入了、上一条输入的信息还在 / 清空再回车还是返回上条结果”）**：
>      两个独立原因：
>      · **旧文本回填**：失焦/提交时没清 `_compositionLast`（组合缓存）→ 下次聚焦时“组合结束”那条兜底
>        把**上一次的文本**补进空框 → 框里又有字 → 回车就把同一条消息再发一次。
>        修：`SetFocused(null)` 与 `Submit()` 都清掉 `_compositionLast / _lastNative / _pendingChar / _composing`。
>      · **发送后又被弹开**：中文输入法里“回车确认候选词”会从**文本通道**发一个 `\r`，那一刻还在组合中（不走提交分支）
>        ⇒ `ConsumedEnter=false` ⇒ 聊天层“回车唤入”把它当新回车又把输入框弹开。
>        修：`UiChatOverlay.Tick` 的唤入条件加**提交冷却**（`Time - UiTextRouter.LastSubmitAt > 0.35s`，新增该时间戳）。
>      另外：`UiChatOverlay.Focus()` 在**全新展开**（之前是收起态）时把输入框清空（`draft` 显式给了才保留）。
>      实测（真 Enter 注入）：发送后 `上次发送='你好'｜输入框文本=''｜展开=False｜输入框聚焦=否`，900ms 后仍保持；
>      `聊天关→聊天开` 后 `输入框文本=''`（无残留）。
>   ⑧ **（用户：“聚焦修好了，但旧文本回填还是在”）真因已定位（关键：`GCS_RESULTSTR` 是“持值”而不是“事件”）**：
>      `ImmGetCompositionStringW(GCS_RESULTSTR)` 会把**上一次提交的串一直返回**；而旧代码在**聚焦时**把去重键 `_lastNative` 清空
>      ⇒ 重新聚焦第一帧就把它当“新提交”又追加进空框。
>      修法：① **`_lastNative` 跨聚焦持久，永不清空**（同一条只追加一次）；
>      ② 同一条串何时算真提交？—— 只有**在这之后又观察到过一次“组合结束”**（`_wasComposing` 下降沿 + `_imeCommitFresh`）
>      才算（既能防回填，也不会把“同一个词连打两次”误判成重复）；
>      ③ 原生结果串仍**无条件读**：实测日志里汉字全是 `IME 原生提交 added='…'` 进来的（连“组合结束”那条路都没走过）
>      ⇒ 加“组合窗口/打字活动”之类的门会直接把他们的输入法堵死（不能加）。
>      ④ 新增测试钩子 `UiTextRouter.FakeImeResult` + 命令 `imefake:<文本>`（冒充持值串），**把“不可自动化的输入法”变成可回滚的回归**：
>      实测 `imefake:旧文本` → 首次聚焦追加一次 → 发送清空 → 重新聚焦后**仍为空**（旧版会回填）；`chatprobe` 连续两次采样都为空。
>   ⑨ **（用户：“第一次出现的候选词会重复一遍，比如 '中文' 变 '中文中文'，第二次再打就不会”）**：
>      一次提交可能被**两条通道**各送一次（原生结果串 / 组合结束 / 逐字 `OnTextInput`），两者可能隔几十帧；
>      “第二次不会”正是因为那时 `_lastNative` 已等于该串。
>      修法：**跨通道去重 = 框尾就是这段文本 + 距上次追加不过久 + 期间没有新的打字活动**（`RecentlyAppended`）：
>      “期间有无新活动”是区分「同一次提交的第二个通道」与「用户新打了一个词」的**唯一可靠判据**。
>      活动信号包含：物理键、逐字通道、锚点组合串 —— 以及 **原生 `GCS_COMPSTR`（`_composing`）**：
>      本机输入法恰好只能从原生侧看到组合（§6 的教训），**不把 `_composing` 计入活动就会把新词误拦**
>      （实测日志：`'中午呢'/'中文'/'英文'` 被当成重复跳过 → 已修）。
>      回归：`imecomp:<文本>`（冒充一帧组合串）+ `imefake:<文本>`（冒充持值原生串）可以离线造出“两通道各送一次”：
>      实测 `组合结束提交 '中文'` → 原生 `'中文'` 被跳（去重）→ `输入框文本='中文'`；再打一个词 → `'中文英文'`（**新词未被误拦**）。
> - 2026-09-13（四十五）**观感第二轮（用户：“好看多了，还能更好看吗”+“右上角 Close 文本改成 ×”）**：
>   ① **标题栏按钮两态**：栈底 = **图标「×」紧凑方形（28×0）**、有上级页 = 文字「&lt; 返回（84 宽）」；
>      新增 `UiButton.SetSize(w,h)`（运行期改尺寸 + 重挑素材），三件事（宽度/文案/标题避让）统一在 `UiMenuWindow.RefreshHeaderButton()`。
>      实测：`crop_close.png` —— 深底 + 1px 描边 + 白「×」（字体有该字形，不是 tofu）。
>   ② **页签扁平化**：不再用暗金填充底（与下划线双重强调显得脏）→ 底色统一内容底，**选中 = 琥珀文字 + 2px 下划线**，
>      并给整条页签栏加 1px 发丝线。
>   ③ **分组标题**：左侧加 **2px 琥珀竖条** + 字距 1.5（工业风“机柜标签”感）；普通分隔线改用 `Hairline`。
>   ④ **按钮统一 1px 描边**（主/危险用琥珀/红半透明，次按钮用冷灰）—— 否则纯色块看着没“壳”。
>   ⑤ **标题栏左缘 3px 琥珀竖条** + 标题单行省略 + 字距 1.5。
>   ⑥ **底栏文字单行省略**（`_crumb`/`_status`）：宿主给的摘要一长就折成三行、溢出底栏面板（用户截图实证）。
>   ⑦ **页签热区加前缀**：`UiTabs.Create(..., zonePrefix)` → `tab:&lt;键&gt;:&lt;i&gt;`（一页里两条页签栏不再共用 `tab0..`，自动化/排查才不会命中错的那条）。
> - 2026-09-13（四十四）**扁平化工业风落地 + 修“外面那个块被撑大/外层滚动条”**（用户：“扁平化工业风 / 左右两块外面那个块还是被撑大了，而且有滚动条”）：
>   ① **修外层被撑大（真因 = 高度自引用）**：`UiList.ApplyLayout` 里 `SetRect(Content, …, ContentHeight)` 而
>      `ContentHeight = Σ子项高`；两栏页里子项（`Grow(1)` 的列表）又依赖 `flow.known`（= 内容层高）
>      ⇒ **自引用收敛到 0**：实测 `colL/mm.list/viewport rect=440x0`、`colR/mm.right/viewport=685x0`
>      （内嵌列表视口 0 高、内容被 `RectMask2D` 整片裁光）。
>      现约定：**宿主 flow 声明 `AutoHeight=false` ⇒ 内容层铺满视口**（`fillHost`），`ContentHeight = vh`
>      ⇒ 外层滚动量恒为 0（外层滚动条消失），滚动交给内嵌列表。复测：`mm.list/viewport=440x461`、
>      `mm.right/viewport=685x479`、`list/viewport/content=1137x594`。
>   ② **调色板改扁平工业**：冷灰钢底（`WindowBg 0.078/0.086/0.098`）+ `Border 0.216/0.235/0.263`（1px 冷描边）
>      + 新增 `Hairline`（1px 发丝线，alpha 0.5）；**行底与内容底同色**（不再靠明度阶梯分层）。
>   ③ **程序化描边/发丝线**（`ModStyle.{Outline,TopRule,BottomRule,LeftRule}`，全是 1px 纯色块，不依赖任何游戏素材）：
>      整窗一圈描边、标题栏下沿琥珀细规、底栏实底 + 顶部发丝线、列表行底发丝线 → **层级全靠线条，不靠阴影/渐变**。
>   ④ **新增 `UiRowKind.Info = 17` + `UiPageDef.Info(label, value)`**（详情/诊断页属性表）：
>      标签列固定宽 `UiTheme.KvLabelW = 76`、值列**左对齐**求两列对齐（不再是“状态：xxx”一行到底）。
>      实测：`content/kv/text rect=76x36 pos=(0,0)`（标签）+ `rect=594x36 pos=(84,0)`（值）。
>      ModMenu 详情/诊断页的 状态/文件/配置/版本/作者/依赖/不兼容/备注 已全部改用它。
>   - 实测截图 `shots/flat2.png`（两栏视口修复后）、`shots/flat3.png`（扁平风 + 键值两列）、`shots/flat4.png`（Coop 回归，无异常）。
>   - 上一轮列的“未做”里，**详情页键值两列已落地**；图标隐喻 / 圆角质感 / 全局密度重排仍待定（先看这版基调）。
> - 2026-09-13（四十三）**观感精修（第一轮，工业拟真方向）**（用户：“整个 UI 风格重新设计一下，不好看”；当时不在线 ⇒ 按“与游戏一致”的方向自主推进）：
>   ① **修 `UiSeparator` 高度归零**（实测 `sep/line rect=440x0`）：建线时把 `offsetMin/offsetMax` 都置 0（而垂直方向是点锚）
>      ⇒ `sizeDelta.y` 被归零 ⇒ **分隔线根本看不见**（分组没有视觉分隔）。现把高度写回（贴图线 6px / 纯色线 1px）。
>      ⚠ 这是“看着平”的一大来源。
>   ② **配色层次拉开**：`ContentBg 0.071→0.055`、`RowBg 0.086→0.098`（行与底终于有明度差）、`Border 0.165→0.145`（分隔线不抢戏）。
>   ③ **选中态改暗金**：`RowSelected = (0.196,0.161,0.082)`（与 `Accent` 金呼应）+ 新增 `RowSelectedText` 米金色；
>      并在 `UiNavRow` 加 **3px 左侧强调条**（只选中时显示）—— 比“只换底色”更能一眼看出选中在哪行。
>   ④ ModMenu 左栏副文本按状态**着色**（`ModMenuDisplay.MetaColor` 富文本：已加载绿 / 未加载灰 / 禁用橙 / 重复红）。
>   - 实测截图 `shots/sty1.png`（ModMenu）与 `shots/sty2.png`（Coop，选中页签已呈金色）。
>   - **未做（待定方向再动）**：详情页键值两列对齐、图标隐喻、圆角/描边质感、标题栏底栏重做、全局密度与字号重排
>     —— 这些会同时影响所有页面（ModMenu/Coop/自带页），建议先定基调再动。
> - 2026-09-13（四十二）**列表行：右对齐副文本 + 主文本截断（`SelectableList` 带 hints）**（用户：“左侧列表没有最大字符数量限制，版本号没有强制在列表右侧右对齐显示”）：
>   ① 契约：`UiRow.ListHints` + 新的 `UiPageDef.SelectableList(key, height, items, hints, selected, onSelected)` 重载——
>      信息型列表的标准排版（左主文本 + 右版本/状态**对齐成一列**）。
>   ② `UiNavRow`：原来 `hint` 右对齐区写死 196 宽且**主文本不让位**（长名称会盖住它）⇒ 现按 `HintW` 让位 + 主/副文本都**单行省略**
>      （`SetSingleLine`）；`HintW` 收窄到 **110**（模组列表左栏只 440，过宽会把名称都省略掉）。
>   ③ 踩到并修正：`ModMenuDisplay.MetaText` 本身就已是“行右侧短状态（已加载给版本号，否则给状态词）” ⇒
>      调用方**不要再拼 `e.Version`**（否则出现 `2.3.2 2.3.2`）。
>   ④ ModMenu 侧：来源 pill 用 `PadRight` 补到固定宽 + **每一行都预留** `> ` 的两字符前缀（游戏字体是等宽 CourierPrime，
>      空格能对齐）⇒ 所有行的名称起点对齐；名称硬截断 22 字符（ASCII 三点）与渲染层单行省略双保险。
>   - 实测截图 `shots/mm7.png`：`[BepInEx] BepInEx.MelonLoad...  2.3.2` / `[dep] LiteNetLib  not loaded` 式排版，名称对齐、版本右对齐。
> - 2026-09-13（四十一）**修 `Columns` 右栏宽度不撑满 + 新增底栏常驻信息 `UiKitHost.SetFooter`**（用户：“ModMenu 右侧块宽度没有撑满宽度，底下那个块又把块高度撑出去了，直接放底栏那个 Open Nest Mod Menu 右侧吧”）：
>   ① **右栏宽度真因（三层）**：(a) 页面构建时**页面流宿主还是兜底宽 300**（`UiList.Create` 写的内容区初值），
>      此时 `columns` 容器被排成 300 ⇒ `colR` 算出来只有 340（窗口 1180）；(b) 更致命的是 `lf/rf.Apply()` 会把
>      `colL/colR` 的锚点**改成点锚**（`UiFlow.SetRect` 的语义）⇒ 宽度从此**钉死在构建时的值**，容器变宽也不再跟随；
>      (c) 原实现里右栏宽度是 `anchor + sizeDelta` 手算的（`sizeDelta.x` 还写成 `+40`）。
>      **修**：左右栏宽度全交给 `columns` 容器的**横向 flow**（`UiSize.Fixed(leftW)` + `UiSize.Grow(1)`），
>      并把这个 flow **本身**挂进页面流（`UiFlow.IsFlow` ⇒ 上层流 `Apply()` 时会**递归 Apply** 它）
>      ⇒ 容器尺寸后到 / 窗口变化时右栏自动跟上。实测：`colR` 宽度 **340 → 685**（= 1137−440−12，精确撑满）。
>   ② **底栏常驻信息**：新增契约 `UiKitHost.SetFooter(text)`（宿主实现 `UiMenuWindow.SetFooter`）——
>      与宿主自己的临时提示**共用底栏右侧位置**（提示优先，过期后回落）。ModMenu 把原来占页面高度的
>      “宿主 / UIKit / 提供者统计”三行移到这里 ⇒ 页面高度不再被顶出窗口。
>   ③ 实测：`rects` → `colR/mm.right/viewport rect=685x470`、`colL/mm.list/viewport rect=440x470`；
>      底栏右侧显示 `宿主：… · UIKit：…｜提供者 …`；截图 `shots/mm5.png`。`PASS=5 FAIL=0`。
>  ④ 排查手法（值得复用）：给 `UiFlow.Apply` / `UiList.ApplyLayout` 加一行 `[diag]` 日志打印**宿主名+实测宽**，
>      一眼看出“页面流宿主宽 300（构建时兜底）→ 就绪后 1137”，而容器宽没跟上 —— 比单纯看 `rects` 快得多。
> - 2026-09-13（四十）**菜单标题栏只留一个关闭按钮**（用户：“两个菜单右上角的 Close 和右上角的 X 只留一个就行了”）：
>   以前标题栏右侧有**两个**：`UiWindow` 自带的 `X`（热区 `btn:close`，贴右 12px）+ `UiMenuWindow` 自己加的「返回/关闭」按钮
>   （栈底时文案=关闭；原本左移到 -54 就是给那个 X 让位）。
>   现在**只留后者**（它有返回语义，子页面必需）：`UiMenuWindow` 建窗口时传 `onClose = null` —— `UiWindow` 相应把标题框右边界
>   从 `-56` 放宽到 `-Pad`（只有真建了关闭按钮才给按钮留位），并把「返回/关闭」按钮贴回右边距 12、
>   标题右边界按按钮宽重新内缩（-106）避免压字。
>   - 实测：`zones` 从 `btn:close | btn:< Back` 变为只剩 `btn:< Back`；截图 `shots/hdrcoop.png`（Coop 菜单）与 `shots/hdrmm.png`（ModMenu）
>     标题栏均只剩一个按钮。`PASS=7 FAIL=0`。
>   - `UiWindow.CloseButton` 不再被赋值（无消费者）⇒ 传 `onClose = null` 时它就是“无自带关闭按钮”的骨架（第三方仍可传回调保留 X）。
> - 2026-09-13（三十九）**修“聚焦输入框再失焦 → 游戏 UI 全部点不动”**（用户：“Chat 聚焦在失去聚焦之后就会导致菜单组件都没法接受点击”）：
>   真因是 `UiInputGuard` 的 **`_textRc`（聚焦期额外压住的射线器）只增不减、从未还原** ——
>   `SetTextCapture(false)` 的还原分支只做了“模块重新停用 + EventSystem 还原”**漏了射线器**（§8.2 当时的描述是错的），
>   `Restore()` 与 `EnsureReleasedWhenIdle()` 也都没管它 ⇒ 只要聚焦过一次输入框，游戏自己的射线器就**永久 `enabled=false`**。
>   实测：聚焦瞬间压住 **60 个** `GraphicRaycaster`；失焦后修复前仍为 60（游戏 UI 彻底没点击），修复后回 **0**。
>   修：新增 `RestoreTextRaycasters()`，在 `SetTextCapture(false)` 与 `Restore()` 里都调用，并把 `_textRc.Count` 纳入
>   `EnsureReleasedWhenIdle()` 的“残留”判定（自愈）；探针（`widgetprobe` 的拦截层行）新增 `聚焦临时压住射线器=N 打字中=…`。
>   ⚠ **复盘教训**：上一轮用“拦截层：游戏输入模块 启用/停用”就下了“无残留”的结论是**不够的**——
>   那两项本来就正常，漏的恰恰是当时探针没覆盖的 `_textRc`。**探针没覆盖到的东西，等于没验证。**
> - 2026-09-13（三十八）**修收起态提示行压住最后一条消息**（用户：“回车 打开聊天和最后一条消息重叠了”）：
>   列表视口高以前写死 `PanelH - 30`（只扣了标题 24 + 间隙），**漏算了底部那行提示（24）** →
>   列表底边 -184 落在提示行（顶边 -166）上，**重叠 18px**。现改为按分区显式计算并用常量统一三处：
>   `ListH_Collapsed = PanelH - 标题24 - 提示行24 - 6×2 = 130`、`ListH_Expanded = PanelH - 74 = 116`（保持原值），
>   三处 = `UiList.Create` 的初值 / `EnsureBuilt` 的 offsetMin·offsetMax·sizeDelta / `SetExpanded`。
>   实测：收起态 `视口=324x130 可滚=579.4 偏移=579.4 可见行=6#25~#30`，截图 `shots\chat hint.png` 提示行与消息已分开。
> - 2026-09-13（三十七）**悬浮聊天层交互修复：菜单关着时点不动/滚不动、不在最底、抢菜单点击**（用户：“左侧 Chat 失去焦点之后无法与菜单交互，层级问题或者穿透拦截没有正常取消 / Chat 滚动应该默认在最底，滚轮不生效，无法点击”）：
>   ① **真因（滚轮不生效 + 无法点击）= 指针路由根本没开**：`UiPointerRouter.Tick` 第一行是 `if (!Active) return;`，
>      而 `Activate()` **只在菜单窗口/原生页打开时**被调 —— 菜单关着时浮窗的热区全是死的。
>      修：`UiChatOverlay.Tick` 每帧兵底 `if (!Active) Activate();`（浮窗在 → 路由就得活），`Clear()` 里若菜单没开则 `Deactivate()`。
>      ⚠ 附带发现：`Clear()` 原先只 Destroy 画布、**从不清热区** → 反复进出会话就不断累积死热区（Rect 已销毁，靠 try/catch 跳过）。已改为先 `Remove` 再 Destroy。
>   ② **抢菜单点击**：“点浮窗展开”热区覆盖整个浮窗且**后登记=优先** —— 若浮窗在菜单**之后**注册（先开菜单、再进会话），
>      与菜单重叠的那块就点不动了。修：**菜单打开时直接禁用它**（`Tick` 每帧同步 `Enabled = !展开 && !菜单开`；
>      展开时也禁用 —— 那正是“点列表没反应”的原因：收起态它在列表之后注册、展开态又把列表的点击全吃掉）。
>   ③ **不在最底**：展开把视口从 160 压到 116，可滚量 549.4→593.4，而 `_scroll` 停在 549.4。修：`SetExpanded` 改完高度后 `ScrollBy(float.MaxValue)`。
>   ④ **实机取证（`chatdemo` + G 端）**：收起态滚轮 `发给 'chat-list-viewport'` 偏移 549.4→423.4；
>      `tap:chat-open` → `展开=True`；展开态 `可滚=593.4 偏移=593.4`（在底）→ 滚轮后 467.4；菜单打开时 `展开热区=禁用`。`PASS=5 FAIL=0`。
>   ⑤ **“穿透拦截没正常取消”排除**：`chatopen`（展开/打字）与 `chatclose`（收起）两态的 `widgetprobe` 都是
>      `输入管线：聚焦=-/''` + `拦截层：模块 启用=0 停用=0 世界点击前缀=未装 交互锁=False` → 拦截层无残留，不复现该猜测。
> - 2026-09-13（三十六）**悬浮聊天层两处渲染修复**（用户：“Chat 左侧框还是没有聊天记录，失去聚焦模式背景没有足够透明”）：
>   ① **`UiList` 重排不收敛 → 内容被整片裁掉（真因）**：`ApplyLayout` 里 `SetRect(Content, …, ContentHeight)` 用的是**上一轮**的
>      `ContentHeight`，而 `ClearRows()` 会把它归零 ⇒ 重建后写下去的是 **0 高内容区**，`RectMask2D` 把行全部裁掉
>      （`Count/rows` 都 >0，但一条也看不见）。常规页面靠“父尺寸后到”会再排一次盖过去；**固定尺寸宿主**
>      （悬浮聊天层宽度从头到尾不变）下 `MaybeRelayout` 因宽度相等直接 return ⇒ 永不收敛。修：`ContentHeight` 算出后**再落定一次**。
>   ② **面板宽度写成了绝对宽**：`UiChatOverlay.SetExpanded` 里 `_list.Rect.sizeDelta = (PanelW - 16, …)`，
>      但列表水平 anchor 是 (0,1)-(1,1) **拉伸态** ⇒ 实际宽 = 父宽 + 324，配合居中 pivot 把整块内容推到面板**左边框之外**
>      （短消息一条都看不见）。修：写相对增减 `-16`；并在改高度后立刻 `ApplyLayout()`（否则滚动上限按旧视口高算）。
>   ③ **输入框背景与面板背景是同一个 Image**：`_inputBg = _panelBg` 使 `SetExpanded` 末尾那次“输入框色”赋值
>      把面板 alpha 覆盖成**不透明**（收起态本该 0.34）⇒ 即用户看到的“失去聚焦模式背景没有足够透明”。修：删掉共享。
>   ④ **实机验证（注入式，不依赖联机会话）**：新增测试命令 `chatdemo:<条数>` ⇒ `listprobe` 输出
>      `chat-list rows=6 内容高=143.5 视口=324x160 可见行=6#1~#6`；`chatprobe` 输出收起态 `alpha=0.34 列表高=160`、
>      展开态 `alpha=0.94 列表高=116`；截图 `shots\chat collapsed.png` / `chat expanded.png` 都是“框里有 6 条 + 半透明”。`PASS=4 FAIL=0`。
>   ⑤ 诊断增强：`UiChatOverlay.Probe()`（即 `chatprobe`）增打 **面板背景 alpha** 与 **列表高** —— 透明度/几何一眼可验。
> - 2026-09-13（三十五）**契约扩容（第一/二档 + 命名统一）+ 发 `0.0.1-Alpha-3`**（用户：“第一档先做掉，第二档你提的几个都做掉，第三档改名统一先做”）：
>   ① **新行动词**：`Stepper`（原生 `− 值 +`，宿主已有 `UiStepper` 终于暴露）、`Foldout`（折叠分组，
>      **只在展开时调用 body**，收/展后宿主自己重建页面，第三方只需存状态）、`SelectableList`（单行选中高亮，
>      回调索引）、`Text(..., placeholder, maxLength)`（占位 + **在最窄咽喉 `SetValueInternal` 截断**，覆盖物理键/IME/退格/外部赋值）。
>   ② **行修饰**：`Hint(string)` / `Hint(Func<string>)`（动态悬停提示）/ `MarkSelected()`；
>      **与 ModMenu 同名别名** `Bool`/`Number`/`Action`（= `Toggle`/`Slider`/`Button`）—— 一份设置页两个模组都能编译。
>   ③ **宿主控制**：`Confirm`（原生模态，ESC = 取消；菜单未开时 warn + 回调 false，不会“点了没反应”）、
>      `CanShowDialog`、`ScrollToKey`/`ScrollToTop`、`PageChanged(old,new)`（懒加载正道）、
>      `UiKitLang.T/IsChinese/Changed`（宿主推语言；**故意不叫 `UiKitLoc`** —— 宿主里有同名内部类，会歧义引用）、
>      可选 `IUiKitDefaults.ResetToDefaults()`（宿主在根页追加带确认框的“恢复默认”，与 ModMenu 同名同义）。
>   ④ **两个设计教训（都已写进代码注释）**：
>      (a) **折叠箭头不能用字型**：`▾`(U+25BE)/`▸`(U+25B8) 在游戏字体里**无字形**，屏上是一个金色 tofu 方框（截图取证）；
>          改为 `NativeWidgets.TriangleSprite()` **程序化三角贴图** + 旋转（展开 0°/收起 90°）——与“滑条圆块自己画”同一手法。
>      (b) **热区注册顺序 = 命中优先级（后注册优先）**：给声明式行“补一个整行热区”本想只为悬停提示，
>          却会盖住控件内部的 `<`/`>`、`+`/`-`、页签 ⇒ **只给只读行补**；可交互控件的整行热区由控件工厂**先**登记
>          （`UiStepper`/`UiChoice` 已补），`UiActionRow` 的整行热区补上 `Owner`（以前漏了，会被当成“没热区”）。
>   ⑤ **实机验证（G 端 BepInEx，脚本驱动）**：基础烟测 `PASS=11 FAIL=0`；交互跑逐项 PASS，截图取证：
>      折叠展开后子行出现（含 `− 3 +` 步进器）、选中列表第三项高亮且标签变 `当前选中：2`、点“重置”弹出原生确认框
>      （`Reset to defaults / … / Cancel / OK`）。
>   ⑥ **发布 `0.0.1-Alpha-3`**：tag `v0.0.1-Alpha-3`（commit `8d7bf67`，31 文件 / 其中 src 23），Release id `387845341`（Pre-release），
>      两个 zip（175.5/175.3 KB）；公开仓四工程（mod 双端 + 样例双壳）0 错，`release` 包内 README 编码复检 0 乱码。
>      （gh 创建发布时报了一次 HTTP 502，API 回读证明发布已成功 —— 遇到这种瞬时错误用 `gh release view` 回读，别急着重试。）
>
> - 2026-09-13（三十四）**U9 完成 + 发 `0.0.1-Alpha-2`**（用户：“把 U9 做了，更下一个版本”）：
>   ① **契约文档**：新增 `docs/UI_KIT_API.md`（本仓中文版）/ 公开仓 `docs/API.md`（英文版，同内容），把 API 的 26 个公开成员、
>      14 个行动词、**页面命名空间 `provider:<Id>[:<页id>]`**、`Refresh()` 与 `IsTextInputFocused` 的冲突规则、
>      原生入口 `IUiKitNativeEntry`、聊天层、生命周期、**“只引不打包 Api dll”（`Private="false"`）** 全部写成对照实际的说明。
>   ② **示例模组**：新增 `samples/`（本仓 + 公开仓同路径）= `Shared/{SampleMenu,SampleConfig}.cs`（纯 .NET provider）
>      + `BepInEx/`、`MelonLoader/` 两个壳各带 csproj（含 `DeployToGame`/`DeployToMods`）；
>      四个工程（mod 双端 + 样例双壳）实测 **0 错**，样例产物只有 1 个 dll（证明 `Private="false"` 生效）。
>   ③ **发布 `0.0.1-Alpha-2`**：tag `v0.0.1-Alpha-2`（commit `322842d`，注释标签；先前 `a4158ca` 是文档/示例提交），
>      Release id `387840362`（**Pre-release**），资产两个 zip（168.6 / 168.4 KB），公开仓 102 文件、样例 7 文件。
>   ④ **两个发布期缺陷（已修）**：
>      (a) **`.gitignore` 未锚定的 `BepInEx/` 会连 `samples/BepInEx/` 一起忽略** → 样例的 BepInEx 壳没进仓库/tag；
>          修法：把“装进游戏的目录”全部锚定到仓库根（`/BepInEx/` 等），再用 `git tag -f` + `git push --force origin <tag>` 把标签重指到修复提交。
>      (b) **PS 5.1 按 ANSI 读无 BOM 的 UTF-8** → Release 正文的 `—` 变成 `鈥?`、包内 `README.txt` 的中文变 `鍘熺敓…`；
>          修法：数据文件一律 `[IO.File]::ReadAllText(path, UTF8)`（或 `gh --notes-file`），含非 ASCII 的 `.ps1` 存 UTF-8 **with BOM**；
>          已用 `gh release edit --notes-file` + `gh release upload --clobber` 重刷两版正文与资产。
>      （本机现已安装 `gh` 2.100.0 并登录 `1499501762`，发布流程以 `gh` 为首选；完整坑表见公开仓 `docs/PUBLISH.md` §七。）
> - 2026-09-13（三十三）**拆分成独立公开仓库 + 发首个版本 `0.0.1-Alpha-1`**（用户：“创建一个新公开仓库，推代码，release 第一个版本
>   0.0.1-Alpha-1(Pre-release)”“协议和贡献模仿 OpenNestModMenu”）：
>   ① **新仓库** = `d:\Dev\Open-Nest-UIKit`（对应 GitHub `1499501762/Open-Nest-UIKit`，Public），按家族“一模组一仓库”惯例与主仓库分离；
>      只含 `src\OpenNestUIKit{,.API,.MelonMod,.Test,.Test.MelonMod}`、`docs\UI_KIT{,_SLICE,_TEST}.md`、
>      `tools\slice_tool.py`、`scripts\slice-tool.ps1`、`LICENSE`（AGPL-3.0，与主仓库同一份）+ 新增
>      `README.md` / `CONTRIBUTING.md` / `docs\PUBLISH.md` / `docs\RELEASE_NOTES.md` / `scripts\package.ps1` / `.gitignore`；
>      94 个文件、27 874 行，首个提交 `feat: initial public release 0.0.1-Alpha-1`，注释 tag `v0.0.1-Alpha-1`。
>      自足性已核实：5 个工程只引用彼此 + 加载器/interop dll，`GameDir`/`BepInExDir`/`ClientGame`→`MLBase`/`MLCore` 默认值都写在工程内，
>      无根 `Directory.Build.props`；4 个工程在独立目录里双端编译 **0 错 0 警**。
>   ② **版本号两处**：`src/OpenNestUIKit/UiKitInfo.cs` 的 `public const string Version` 与 5 个 `.csproj` 的 `<Version>`（含测试模组
>      `TestInfo.Version`），从 `0.1.0` 统一为 `0.0.1-Alpha-1`（`ProductVersion` 实测 `0.0.1-Alpha-1`）。
>      ⚠️ `docs\UI_KIT*.md` 里旧日志摘录中的 `v0.1.0` 是**当时实测取证**，不随版本号改写。
>   ③ **打包**（命名对齐家族 `OpenNestUIKit-<ver>-<Loader>.zip`，脚本 `scripts\package.ps1`）：
>      `OpenNestUIKit-0.0.1-Alpha-1-BepInEx.zip`（169 KB，`BepInEx\plugins\{OpenNestUIKit,OpenNestUIKit.API}.dll`）、
>      `OpenNestUIKit-0.0.1-Alpha-1-MelonLoader.zip`（168 KB，`Mods\OpenNestUIKit.MelonMod.dll` + `UserLibs\OpenNestUIKit.API.dll`），
>      各带双语 `README.txt`；`release/` 不入库（`.gitignore`）。
>   ④ **已发布**（2026-09-13）：公开仓库 <https://github.com/1499501762/Open-Nest-UIKit>（commit `1abcd5a`，tag `v0.0.1-Alpha-1`），
>      Release id `387838334`，**已勾 Pre-release**，正文 = `docs/RELEASE_NOTES.md`，资产 = 上述两个 zip（168.8 / 168.6 KB）；
>      topics = `bepinex, bepinex-plugin, il2cpp, iron-nest, melonloader, ui, ui-library, unity-mod`。
>      发布前脱敏：本机路径/用户名 = 0、闭源模组名 = 0（`UiInputGuard.cs` 里引用了某个闭源模组的类型名，
>      已改成“官方联机 UI 用的也是这一档”—— **公开树里连名字都不要出现**）。
>      **克隆校验**：`git clone --branch v0.0.1-Alpha-1` → 94 文件 → 双端 `dotnet build` 0 错（BepInEx 0 警 / ML 端仅 0Harmony 引用告警，属本机环境）。
>      发布手法与三条坑（PS 脚本必须纯 ASCII、`$ErrorActionPreference='Stop'` 会被 git stderr 误伤、
>      `ConvertTo-Json` 把长正文膨胀成 454 KB 被 GitHub 拒）见新仓库 `docs\PUBLISH.md` §六。
>   ⑤ 主仓库同步关系：UIKit 源码双仓维护，改动后按 `docs\PUBLISH.md` §五 覆盖拷贝（`robocopy /XD bin obj`）。

> - 2026-09-13（三十二）**填充起点缺口 / 手柄与滚动条手柄颜色对齐原生**（用户：“拖拽条起始位置空一块”“Handle 应该是和原生一样颜色的，
>   滚动条的也一样”“圆现在是正常的”）：
>   ① **拖拽条起始（也是两端）为何空一块**：原生 `Fill Area` 是 `尺寸=-20x0 pos=(-5,0)`（左右各内缩 10 再整体左移 5）
>      ⇒ 填充在**最左端**留下 5 单位、最右端留下 15 单位不涂（用户红圈就是左边那块）。
>      现在把 `Fill Area` 直接设成**整条轨道**（`Slider` 只重写 `Fill` 自己的锚点，不动这个容器）⇒
>      填充从轨道最左端开始、满就是整条轨道。取证（`pageprobe` 的页面本地单位）：
>      `轨道[Background x[43..335.8]]` 与 `填充[Fill x[43..335.8]]` **完全一致**（改前填充是 x[48..320.7]，两端各差 5/15）。
>   ② **滚动条手柄颜色**：原生 `Scrollbar Vertical` 的 `Selectable` 过渡是 `ColorTint target=Handle`，
>      实机 `渲=RGBA(0.388,0.282,0.176,1)`（**棕色**）⇒ 原生手柄是**深棕**，不是我们之前的浅灰。
>      现在照抄那层 tint。取证：`滚动条手柄 色=(0.55,0.57,0.6) 渲=(0.39,0.28,0.18)`。
>   ③ **拖拽条圆块颜色**：原生 `Handle/Bg` 写的是 `色=(0.545,0.565,0.604)` + `渲=白`，但**实机渲出来是深蓝灰**
>      （同一张剪贴板上量到原生圆块 ≈ **(31,35,48)**，我们同色值渲出 ≈ **(110,122,146)** —— 原生那个 `Bg` 上
>      还挂了个 MonoBehaviour，运行时另行压暗）。⇒ 按**实机渲染色**反推，给我们的圆块再乘 tint `(0.28,0.29,0.33)`
>      （取证：探针 `渲=(0.28,0.29,0.33)`）。
>      量法：`clickgame:OpenSettingsBtn` 把剪贴板翻到**原生 Settings 页** → `shot:` 截图 → 同一坐标系下取像素对比
>      （这也是本轮新加的测试能力；“原生到底长什么样”以后都这么量，不要靠猜）。
>   ④ 探针：`pageprobe` 的拖拽条一行改为**直接用 `Slider.fillRect/handleRect` 引用**（名字查找在重建帧会拿到已销毁对象 → 打出 `?`），
>      并补印圆块/滚动条手柄的 `色` 与 `渲`。
> - 2026-09-13（三十一）**手柄圆角 / 抓不住手柄**（用户：“拖动条的 Handle 是方的，圆角不够”“只拖到 Handle 的时候似乎不更新值”）：
>   ① **圆块改成程序化圆片**。原因链：原生手柄用 `SGRounded` + `Image.Type.Simple`，而这张贴图是 **256×256、圆角只有 48px**（~15%）
>      ⇒ 缩到 30×30 本地时圆角只剩 ~5.6（屏上 ×0.3591 ≈ **2px**）——放大 12 倍截图看就是**方块**。
>      先试过“`Sliced` + 按 `border/ppu` 反算 `pixelsPerUnitMultiplier`（算出 31.25）把圆角撑到半径 15”，**实测仍是方块**
>      ⇒ 改为自绘：`NativeWidgets.CircleSprite()`（64×64、1px 抗锯齿、缓存）→ 手柄 `Bg` 用 `Simple` 贴它。
>      取证：同一处 10 倍放大截图，之前是“light blue **square**”，现在是“**circle**”。
>   ② **抓不住手柄的真因**：滑条热区原来挂在**轨道**（高 15）上，而手柄盒高 **30**（上下各露出 7.5 本地 ≈ 2.7px）、
>      圆块两端还会越出 5 —— 指针落在手柄露出部分时就命中不到热区（屏上手柄 ≈11px、轨道命中区 ≈5px，**一半以上的手柄面积抓不动**）。
>      现在热区改成 **`Slider` 本体**（高 30 = 手柄盒高、水平铺满 = 与轨道同心），`local` 基准与归一化方式不变。
>      新增测试命令 `dragpick:<热区>:<起点比例>:<终点比例>`（起点可指定）——把起点放在手柄上复现“抓手柄拖动”：
>      实测 `0.67→0.2` 值 67→18、`0.2→0.6` 值 19→61 ✓
>   ③ 探针：`pageprobe` 的拖拽条一行现在还会打**圆块素材**（`素材/type/ppuMul/border/图尺寸`）。
> - 2026-09-13（三十）**手感与外观到端 / 鼠标穿透真因 / Bounce 弹性**（用户：“滚动条和拖拽条还是有外观到不了极致的问题”
>   “鼠标还是有穿透”“原生有的页有 Bounce 效果，可以拖拽/滚动 超出页面的位置然后弹性弹回来”）：
>   ① **鼠标穿透的真因（实测定位）**：`UiInputGuard.InstallGameClickBlock()`（Harmony 前缀挂 `LookAtTarget.OnClickDown`，
>      世界交互的唯一点击入口）**只在 `Apply()` 里调用**，而 `Apply()` 只在**我们自己的窗口**打开时跑；
>      原生页是**从游戏 ESC 菜单直接进**的，那条路上窗口可能从未开过 ⇒ 前缀根本没装 ⇒ 点我们那页的同一下也会点到
>      世界交互（炮塔/拉杆/控制台）。证据：日志里只有 Coop 模组的 `patched LookAtTarget.OnClickDown`，没有我们那条。
>      现在 `SuppressForNativePage(true)` 里就装（并顺带 `LockInteractions(true)` 冻结玩家/组件级兜底）。
>      修后实机：`game click block installed: LookAtTarget.OnClickDown prefix` + **`已拦=7`** + `拦截层：… 世界点击前缀=已装`。
>   ② **输入压制加固**：`Restore()` 现在在原生页期间**直接拒绝还原**（任何关闭路径都不该把游戏输入放回去），
>      另加每帧幂等的 `UiInputGuard.EnsureNativePageSuppress()`（把游戏写回的模块/射线器重新停用；实测模块始终
>      `启用=0 停用=2`）。
>   ③ **Bounce（弹性越界）**：`_scrollOffset` 改为允许越界 —— 滚轮/按住空白拖动都可以冲过边界（越界部分按 0.4 衰减、
>      上限 0.22×视口高），每帧由 `NativeMenuPage.TickScroll(dt)` 用 `Mathf.SmoothDamp`（0.12s）**弹性拉回**边界；
>      拖动中不回弹（松手才弹），语义与原生 `ScrollRect.movementType = Elastic` 一致。实测：滚轮 -99 格 →
>      `滚动偏移=198.2/195.8`（越界）→ 1.5s 后 `195.8/195.8`（弹回）。
>   ④ **滚动条手柄行程改成「整根槽」**（`NativeScrollbar.Pad = 0`）：用户三次报“外观到不了极值”，
>      而原生是上下各内缩 10（9-slice 留白）。实测改为 `Pad=0` 后：比例=0 时 `手柄 y[233.9..425.7]` 上端=槽顶，
>      比例=1 时 `手柄 y[-401.5..-209.6]` 下端=槽底（完全对齐）。要还原原生内缩只需把 `Pad` 改回 `10f`。
>   ⑤ **我们自带窗口的滑条也修了**：`UiSlider.Refresh()` 旧版按“把手半宽内缩”（`(0.5-p)*knobW`）⇒ 把手**中心**
>      永远到不了轨道两端（填充到了、把手差半格）；现在中心 = p·trackW。`widgetprobe` 会直接打
>      `实测屏幕比例` 和 `✓ 到端 / ❌ 外观未到端`（比看截图靠谱）。
>   ⑥ 探针增强：`pageprobe` 新增 `GeometryText()` —— 把拖拽条（轨道/填充/手柄盒/圆块）与滚动条（槽/滑动区/手柄）
>      的**边沿**换算到**页面块本地单位**打印，并附游戏输入拦截状态，直接回答“到没到端 / 拦没拦住”。
> - 2026-09-13（二十九）**手柄是圆的 / 顶部那块是固定标题块 / 滚动方向反了**（用户：“原生的有可见手柄，是个圆的”
>   “顶部还是空出来一块，这一块应该是实际原生的标题块，因为不参与滚动”“滚动条滚动方向和实际页面滚动方向反了”）：
>   ① **我上一条（二十八）的结论是错的**：我说“原生 `Handle` 无底图无子节点 = 没有可见手柄”，那是因为
>      `pagespec` 的按名字摘要**只到固定深度、没下钻 Handle 的子树**。用 `pagedump:SliderConsoleUGUI_(clipboardOffsetPercent)`
>      （`DumpPage` 默认 14 层）才看到真结构 —— **`Handle` 本体无图（20×30 定位盒），可见手柄是它的子物体**：
>      `Handle/Bg` = **30×30 `SGRounded` 灰(0.545,0.565,0.604) `Simple` ppuMul=1**（看着就是个圆）
>      ＋ `Handle/Shadow` = `SUGShadowLite`（rect 80×90、pos (0,5.5)）＋ `Handle/HitArea` 60×70。
>      已恢复（`Bg` 必须是**点锚子节点**，否则被 `Slider` 的锚点改写拉成竖条）。取证 `pageprobe`：
>      `Background=292.8x15 … Fill=182.8x15 Shadow=80x90 Bg=30x30`。
>      ⇒ 教训：**判断“有没有子物体”不能用只到 3~5 层的摘要**。
>   ② **顶部那块空区 = 原生 `TabsCtn`**（871.7×72、Image `enabled=false`），而大标题 `Title Settings` 是 `Settings` 的
>      **直接子物体**（不在 `ContentCtn/Scroll View` 里）⇒ **它不参与滚动**。现在页面多一层**固定标题条**
>      `nw_header`（锚 0,1~1,1、`sizeDelta(0,72)`、y=0），第一条 `Title` 行建在这里（条内居中，行高 87 与原生一样稍微溢出），
>      **不再计入内容高、也不在滚动层**。取证：`└'nw_header' 尺寸=0x72 y=0 子=1`，内容高 1131 → 1040。
>   ③ **滚动方向反了的真因**：内容层 pivot (0.5,1) 顶对齐视口 ⇒ 看后面的内容 = 内容**整体上移** = `anchoredPosition.y` **变正**；
>      旧代码写成 `_scrollBaseY - _scrollOffset`（越大越**下**移）⇒ 往下滚内容反而往下跑，且顶部露出一块空白
>      （用户看到的“顶部空出来一块”正是它）。已改为 `+`。取证：滚轮 `scroll:nw:scrollarea:-99` →
>      `滚动偏移=195.8/195.8 内容y=+195.8`（末尾行贴到视口底，无空白）；`:99` → `0/195.8`；
>      `vdrag:nw:scroll:120`（手柄上移 120px）→ `0/195.8` ⇒ 手柄与滚轮方向一致。
>      ⚠ 竖直滚动条**不能用 `drag:` 验方向**（那是水平拖，只会在中点停下）；要用 `vdrag:`。
>   ④ 探针增强：`pageprobe` 逐行打 `字符/顶点 cull= 强制=字符/顶点`（区分“被 `RectMask2D` 裁掉”“TMP 没建网格”“正常”），
>      并逐行列出该行各图的尺寸 —— 手柄在不在、多大，一眼可见。
> - 2026-09-13（二十八）**页面块几何 / 大标题字号 / 拖拽条手柄 / 拖拽与滚动到端 / 点击穿透 / 语言键**（用户：
>   “拖动条的手柄样式还是和游戏原生不一致”“点击现在是穿透了的，应该阻挡”“小标题正确但是大标题不够大，应该是不同块上的，
>   没拷贝过来”“块尺寸也不对，上下都缺了一块，右侧也缺了一块或者没有居中（可能是 Canvas 的尺寸问题）”
>   “拖动条和滚动条最大和最小的位置都滚不到（值能达到，应该是渲染/样式问题）”“没加语言键”）：
>   ① **手柄：原生根本没有可见手柄**。`pagespec` 实测 `SliderConsoleUGUI/.../Handle` = `尺寸=20x0 rect=20x30`
>      **无底图、无子节点**（20×30 只是个定位/命中盒）；我上一轮照早前笔记补的“30×30 灰 `SGRounded` 方块”是多余的
>      —— 看起来像手柄的其实是 `Fill` 的右端 + 轨道 `SUGShadowLite` 底影。现在照抄原生：`Handle` 不挂任何 Graphic。
>   ② **页面块几何照抄原生 `Settings` 节点**（这是“块尺寸/上下缺一块/右侧没居中”的正解）。以前我按“面板 300 宽”自己
>      估了个 743×288 的视口，而原生整块是 **871.7×1012.5 本地**（`localScale 0.3591` ⇒ 屏上 313×363.6）、
>      位置 `(-0.9,-13.1)`，内容区 `ContentCtn` = **871.7×844.2、顶边在块顶下方 72**。现在：
>      `nw_page`(871.7×1012.5, `localScale=S`) → `nw_view`(=`ContentCtn`，锚 0,1~1,1，`pivot(0.5,1)`，
>      `sizeDelta(0, +844.2)`，`anchoredPosition(0,-72)`，带 `RectMask2D`) → `nw_content`(宽 `RowW` 742.6 居中、
>      内容高 1131)。**坑**：`nw_view` 的 y 轴不是 stretch（上下锚点都在 1）⇒ `sizeDelta.y` 就是实际高度，必须给
>      **正数**；照 native `sizeDelta` 抄成 `-844.2` 会让 rect 高度变负 ⇒ `RectMask2D` 把整页剪成空气。
>      取证 `pageprobe`：`视口=871.7x844.2 内容宽=742.6 内容高=1131`、`y=-13.1 尺寸=872x1013`。
>   ③ **大标题字号**：原生 `Title Settings` 是 **17 本地 × `localScale 5.1476` = 86.7 本地**（屏上 31，
>      比行标签的 32 本地字号大一半多，颜色黑 α0.902、居中），不是“行标签放大一点点”。`FTitle = 86.7`、
>      `HeightOf(Title) = 87`。取证：`字号=85.55 字符=14 顶点=56`（顶点 >0 ⇒ 真的画出来了）。
>   ④ **拖到两端到底**：值映射原来按**轨道**宽（292.8）归一化，而 `Slider` 是按**手柄滑动区**（左右各内缩 10，271）
>      摆手柄 ⇒ 指针到端点时手柄中心还差 10 单位（用户：“最大和最小的位置都滚不到，值能达到”）。现按滑动区宽度映射。
>      取证：`drag:nw:slider:1` → `滑条=100`；`drag:nw:slider:0` → `滑条=0`。滚动条同理：
>      `_scrollUsable = 内容高 - PageContentH`，`scroll:nw:scrollarea:-99` → `滚动偏移=286.8/286.8 内容y=-286.8`（到底），
>      `scroll:nw:scrollarea:99` → `0/286.8`（到顶）。
>   ⑤ **点击不再穿透**（用户：“点击现在是穿透了的，应该阻挡”）：以前开原生页只开了自管指针，游戏自己的
>      `BaseInputModule` 仍在跑 ⇒ 同一击既点我们那页、又点游戏（世界/按钮）。新增
>      `UiInputGuard.SuppressForNativePage(bool)`：开页禁用游戏输入模块（日志 `game input module(s) disabled: 1`），
>      关页和“剪贴板被游戏关掉”两条路径都还原；`EnsureReleasedWhenIdle` 在压制期间**不再抢着还原**（否则下一帧就被放回去）；
>      世界点击前缀 `PrefixLookAtTargetClick` 也把原生页算进拦截条件。自管指针不受影响（压制期间 `drag` 仍改值）。
>      取证 `uikit.log`：`原生页：已压制游戏输入（点击不再穿透）` → `pageclose` → `原生页：已还原游戏输入`。
>   ⑥ **补语言键**：控件 Gallery 里硬编码的中文（“滚动验证区（超出一屏 → 右侧滚动条）”“填充行 N”“最近操作：”以及各控件标签/
>      回调文案）全部改走 `UiKitLoc.T(中, 英)`。取证：英文环境下 `pageprobe` 文案是 `'Widget Gallery'`、`最近='slider → 100'`
>      （改前是中文）。`最近操作` 这类带状态的句子没法逐字译，英文下用固定串占位。
>   ⑦ 顺手对齐两处原生事实：`TabsCtn` 的 Image 是 `enabled=false` ⇒ 不再画页签底条；页签字号用原生
>      `TabButtonUGUI/Normal/Text (TMP)` 的 **30**。
>   ⑧ 探针增强：`pageprobe` 头部新增 `滚动偏移=<当前>/<可用> 内容y 内容高 内容宽 视口`，并下钻 `nw_view`/`nw_content`
>      （以前只到 `nw_view` 就断了，永远看不到行内几何）。
> - 2026-09-13（二十七）**拖拽条方块被拉长 / 小标题下划线抖动 / 输入框失焦不保存 / Gallery 看不到滚动条**（用户：“拖拽条样式还是不对，
>   滚动条没有在 Gallery 里”+“标题下那个线在闪烁”+“输入框应该失焦就自动保存”）：
>   ① **拖拽条方块被拉成竖条（真因）**：`Slider` 会把 `handleRect` 的锚点改成
>      `anchorMin=(value,0) anchorMax=(value,1)` ⇒ 手柄高 = 容器高(30) + `sizeDelta.y`。
>      我原来把 30×30 的方块**直接当手柄**（sizeDelta 30,30）→ 实际 **30×60 竖条**。
>      现在照原生结构：`Handle` 本体只负责定位（20 宽、高交给 Slider），方块是**点锚**子节点（`0.5,0.5` + 30×30）→
>      永远正方形。取证：`pageprobe` 现在会列出行内各图的尺寸 —— `Background=292.8x15 Fill=272.8x15 Bg=30x30 Shadow=60x60`✓
>   ② **小标题下划线“闪烁”**：原来用 `SGRounded` + Sliced，而线只有 4 本地单位高（屏上≈1.4）→ 九宫格边框被强制压缩，
>      加上亚像素定位就抖。原生用的是 `RawImage`（**无 sprite/无贴图**的纯色块），已改成同样做法（无 sprite 的 Image）。
>   ③ **输入框失焦即保存**：以前只有回车才回调，现在**失焦 / 页面重建 / 关页**都会提交（`CommitInput` / `CommitAllInputs`，
>      `ClearInputs` 里先提交再清）；比较值不同才回调，不会循环。第三方页面同理（`UiTextInput.OnRouterBlur` 比对聚焦时的值）。
>   ④ **Gallery 看不到滚动条**：内容原来只有 12 行（一屏放得下 ⇒ 不挂滚动条）。现在尾部加了“滚动验证区”
>      （小标题 + 10 行文本），共 **23 行 / 内容高 1088 本地（≈391 单位）> 视口 288** ⇒ 右侧原生滚动条出现，
>      顺便验证“超出屏幕的行会渲染”“点组件不会跳回顶部”。`MaxWidgetRows` 18 → 32。
>     实测：`滚动=True`；`scroll:nw:scrollarea:-4` → `nw_content.y` 由 -57.4 → +45.4（clamp 到可滚上限）✓
>   ⑤ 探针增强：`pageprobe` 每行现在还会打印行内各图的**尺寸**与文字**字符/顶点数**（顶点 0 只在“被裁剪到视口外”时正常，
>      在视口内仍是 0 就是“字没了”）。
>   ⑥ **顺带修掉一个真 bug：滚轮热区的矩形是空的**。`nw:scrollarea` 原来用 `_rows.Find("nw_view")` 找视口，
>      实测 `Find` 返回 null（`zones` 里显示 `nw:scrollarea [hidden]`）→ 热区矩形为 null → 路由器命中时跳过它
>      ⇒ **滚轮滚不动**（只有拖滚动条能滚）。现在建视口时就把引用存进 `_widgetView`，热区直接用这个引用；
>      实测 `scroll:nw:scrollarea:-3` → PASS，内容 y 由 -57.4 → +32.6；再点一下复选框（会重建整页）→ y=+45.4
>      ⇒ **点击组件不会跳回顶部**（旧 bug 用 23 行的 Gallery 复现并已消除）。
> - 2026-09-13（二十六）**“字没了 / Checkbox 被挤扁 / 输入框没 Caret / 第三方 Caret 不闪 / TextScroller 箭头反了 / 聊天记录不显示 / Send 占一整行 / 退到最外层后菜单不能交互”**：
>   ① **“字没了”的真因（重要）**：原生字号是 **32（本地）**，而行内文字矩形只有 20~30 高（行高 40），
>      一行字高 ≈ 字号×1.2 ≈ 38 > 30 —— 而我们在 `AddTxt` 里设了 `enableWordWrapping=false` + 
>      `overflowMode=Ellipsis`，TMP 在这种情形下**会把整行直接丢掉（无字形）**（与早期 ESC 注入格“18 高→行数=0”同一个坑）。
>      已改成原生默认的 `Overflow` + 允许换行。取证手段：`pageprobe` 现在每行都打 **字符/顶点数**（顶点 0 = 屏幕什么都不会有），
>      实测修复后每行 `字符>0 顶点>0`。
>   ② **Checkbox 被挤扁**：方框尺寸写成了 `sizeDelta=(20,+20)`，而原生是 `(20,-20)`（锚 1,0~1,1 ⇒ 高度要减行高）
>      → 之前实际是 20×60 的竖条。已改回原生值。
>   ③ **输入框加 Caret（会闪）**：原生控件页的输入框以前**根本没画光标**；现在文本末尾拼 `|`，
>      每 0.5s 一亮一灭（打字时复位成常亮；灭时换成空格，宽度不跳）。实测 `pageprobe` 三次：
>      `文='输入框 / InputField|' → '…Field ' → '…Field|'`✓
>   ④ **第三方页面（Coop/ModMenu）的输入框 Caret 不闪**：`UiTextInput.Refresh` 之前拼的是一个**固定** `|`。
>      现在由 `UiTextInput.TickCaret(dt)`（`UiKitBehaviour.Update` 每帧）翻转 `_caretOn` 再 Refresh；
>      聚焦/打字会 `ResetCaret()` 复位成常亮。实测 `widgetprobe`：`光标=|（亮） → 空格（灭） → |（亮）`✓
>   ⑤ **TextScroller 左右箭头反了**：`SGDownArrow` 是“向下”箭头，旋转 +90° 后指向**右**（(x,y)→(-y,x)），
>      所以右侧/下一项应该 +90°、左侧/上一项 -90°（之前写反）。已交换。
>   ⑥ **Coop 聊天页**（`src/OpenNestCoop/UI/UIKitIntegration.cs`）：
>      · 聊天记录原来是**一整段**带 `\n` 的 Label → 宿主那行只给一行高，多行看不到。改成 `page.List()`
>        （内嵌可滚列表，每行一条，保留最近 80 条）。
>      · Send 按钮改成与输入框**两栅一行**（`page.Columns`）：左输入框占剩余宽度，右发送按钮。
>   ⑦ **“退到最外层再打开 ESC 菜单无法交互”的兵底**：新增 `UiInputGuard.EnsureReleasedWhenIdle()`（每帧、极便宜）：
>      只要我们**没有任何界面在占用输入**，而拦截层还有残留（游戏输入模块还停着/射线器还压着/画布层级被动过/交互锁还开着），
>      就立即全部还原并打一条 Warn（日志会写明到底漏了什么）。另外 `NativeMenuPage.Close(fromEsc)`：
>      **点击**关闭（X 关闭/返回钮）时立即放行游戏 ESC，不再等键盘抬起（只有 ESC 自己触发的那种才等）。
>      注：用户另报“切出再切回只在 BepInEx 端卡顿”，怀疑是别的模组，暂挂起（本库的全场景扫描早已限流 10s 一次）。
> - 2026-09-13（二十五）**滚动位置跳回顶部 + 组件内文字错用白字**（用户：“滚动过的页面在点击组件的时候点完滚动条会跳回到
>   初始位置 / 组件内应该是黑色的文本也变白色了”）：
>   ① **滚动位置跳回**：点击控件 → `ReloadRows()` 重建整页 → `BuildWidgetLevel` 里把 `_scrollOffset` 归零了。
>      现在重建时**不归零**（只 `Clamp` 到新的可滚范围），只在“新开一页 / 进子页”时归零（`ShowRows` / `OnRowClicked`）。
>   ② **文字颜色的铁律（这个根因很坑）**：实测 `ref/ui_sprites_all` 里这些贴图**是空心的** ——
>      `UI Box Castile` 中线 **alpha 全 0**（只有描边/装饰）、`UI box partial star` 中心 A=0、
>      `SUGGradientRounded` / `_Top` 中心 **A=41（16% 极淡填充）**；只有 `SGRounded` 是实心（A=255）。
>      ⇒ 原生的“深色框”实际是**深色描边 + 薄底**（纸面透出来），所以框里的字/箭头原生就是**黑**：
>      下拉 `ButtonLabel`/`Arrow` = (0,0,0,1)、输入框文字 = (0.255,0.255,0.255,1)、选项卡未选中 = 黑。
>      上一版我按“深色底 ⇒ 白字”把它们改白了，错了，已全部改回原生值（选中态仍用原生强调青 0.55,0.65,0.83）。
>      ⚠ 因为框是空心，**聚焦态不能再改 `Image.color`**（会直接把框染成一个黑框）——改成压渲染色（`Rc` 到 0.55 灰）。
>      勾选框里的 `SGCheckMark` 因为无原生值可抄，取黑（浅底上白勾看不见）。
> - 2026-09-13（二十四）**控件尺寸的真正对法：整页按原生显示缩放；主/次按钮黑底、输入框深底**（用户：“竖向超出第一页的内容
>   没有渲染，各个控件尺寸和游戏原生尺寸完全不一样，滚动条和拖拽条和原生外观完全不一样”+“主按钮和次按钮还是白色背景的，
>   应该是和原生一样的颜色的 / 输入框是白色背景，没有套用原生样式”）：
>   ① **尺寸真因**（`pagespec` 实测）：原生 `Settings` 页本地是 **871.7×1012.5**，而它所在剪贴板画布只有 **300×400** ——
>      游戏是**把整页缩小塞进去**的（**实测显示缩放 0.3591**：1012.5 × 0.3591 ≈ 364 ≈ 面板 400 − 标题那一行）。
>      所以原生的 “40 高行 / 32 号字 / ppuMul 2.5” 都是**本地值**，屏上只有 ~0.36 倍（行 ≈267×14.4、字 ≈11.5、
>      九宫格圆角也是 ~1/2.8）。
>      ⇒ 改法（`NativeWidgets` + `NativeMenuPage`）：控件**全按原生本地数值建**，整层挂在一个
>      `localScale = NativeWidgets.DisplayScale` 的 `nw_content` 上 —— 宽度/字号/行距/ppuMul 全自动一致。
>      实测校验：`nw_content` 743×602 本地、局部缩放 0.359、12 行；渲染后 ≈**267×216** ⇒ 一屏放下、**不再需要滚动**
>      （也就顺带解掉了“超出第一页的内容不渲染”——以前 40 高的行把 288 高的视口塞爆）。
>      `DisplayScale` 初值取 0.3591（实测值）；另外 `PollNativeScale()`（本库页未显示时每 10 秒最多一次）
>      会在玩家的原生 Settings 页真的显示过（`localScale>0.05`）时**再实测并纠正**（实测生效过：0.3442 → 0.3591）并重排。
>   ② **裁剪窗口不能跟着内容走**（用户报“越界内容不渲染”）：`RectMask2D` 原来加在被平移的那一层，
>      裁剪框跟着内容跑 ⇒ 屏外的行永远在裁剪区外。现在：`nw_view`（带 mask、**固定**）+ `nw_content`（只平移它），
>      且**不滚动时也走这一套**（否则行会以原生尺寸直接铺进 300 宽的面板）。
>   ③ **主/次按钮白底的真因＝颜色抄错了层**：原生 `Bg` 是 `UI Box Castile`/`UI box partial star`（**白图**、`Image.color` 也白），
>      真颜色由 **Selectable 的 ColorTint 设在 `CanvasRenderer` 上**（实测 `渲染色=(0,0,0,1)` → 屏上是黑底）。
>      我们建的是裸 `Image`，没有这层 → 一直是白底。现在：`Rc(img,color)` 写渲染色（normal 黑 / hover 0.453 / press 黑），
>      并且**不再画那层 `BGShadow`**（`SUGShadowLite`）：原生那个 Image 组件是 `enabled=false`，
>      我们照抄的那一版给漂了一层白光晕 = 用户看到的“白色背景”。
>   ④ **输入框/键位/下拉/选择器的深色底**（实测）：输入框 `InputField (TMP)` = `SUGGradientRounded` `(0,0,0,0.902)`、Sliced、**ppuMul 7**
>      （旧版用了 `InputFieldBackground`+白底，根本不属于这页）；下拉 `Bg` 同色 ppuMul 7（锚 0,0~1,0.57，上半 40 是标签）；
>      选择器 `ValueBg` `(0,0,0,1)` ppuMul 5；选项卡整条 `SUGGradientRounded_Top` `(0.014,0.039,0.066,0.961)` ppuMul 4、
>      格子 `Active` 无 sprite 纯色 `(0,0,0,0.8)` ppuMul 5。深底上的字/箭头取白（原生 prefab 里是黑，但那是**未激活页**的原始值，黑压黑看不见）。
>   ⑤ 新增 `pagespec` 探针（见 `docs/UI_KIT_TEST.md`）：逐控件打印**尺寸/锚点/底图/色/type/ppuMul/组件enabled/渲染色/
>      Selectable 过渡色**，并附“宿主链”（`Settings menu` 往上到 Canvas 每层的 rect/缩放/active）——本次三个 bug 全靠它定位。
>   ⑥ 旧版把行高提到 40（当成“原生尺寸”）是**错的**，已改正为原生本地 40 × 0.3442 ≈ 13.8 面板单位。
> - 2026-09-13（二十三）**原生控件页补齐：TextScroller / TabBar / Keybind + 尺寸对齐原生 + 两个交互 bug**（用户：“除了拖拽条和
>   滚动条其他没有交互了 / Gallery 不全，Checkbox 没有文本 / 滚动条的滚动页面的方向反了 / 拖拽条/滚动条/输入框样式不是原生的，
>   还有个 Keybind 漏了 / Text Scroller 和 TabBar 也没有，控件尺寸和游戏原生的也不一样大”）：
>   ① **修“其他控件点不动”**：滚轮热区（铺满整个视口）原来**建在行之后** → 本库命中是“后登记在上层”，它把整页的点击都吃了。
>      现在它**先登记**（在最底层；滚轮走 `HitTopScrollable`，照样能找到它）。
>   ② **修滚动方向**：偏移语义改成“**正 = 内容上移 = 看下面的内容**”（滚轮向下 → 内容上移；拖手柄向下 → 同理），
>      之前符号写反了（内容往下走 = 越滚越回到顶部）。
>   ③ **Checkbox 布局**：照原生 `ToggleConsoleUGUI` —— **标签在左、20×20 方框贴最右**（之前方框在最左、标签右移，
>      看起来像“只有一个空框、没有文本”）。
>   ④ **新增 `TextScroller`**（原生 `OptionsButtonConsoleUGUI`，就是 Graphics 页那个 `◄ UltraQuality ►`）：
>      左标签 + 右侧深色值框（`SUGGradientRounded` 黑）+ 居中值 + 两个 `SGDownArrow` 箭头（**转 ±90°**，原生就这么干的）
>      + 左右各 1/4 宽的透明可点区（原生 `PreviousOnPointerClick`/`NextOnPointerClick`）。
>   ⑤ **TabBar 改成原生观感**（用户附了截图）：未选中=纸面上黑字无底；**选中=深色圆角底(黑 0.8) + 青字(0.55,0.65,0.83)
>      + 下方一个小菱形**（原生 `Active/Image` 15×15 纯色块转 45°）。之前那版“深色整条 + 底部蓝条”不对。
>   ⑥ **新增 `Keybind`**（原生 `InputBindingConsoleUGUI`）：左标签 + 右侧框显示键名；点一下进入监听态（原生那态是**蓝底**
>      0.259,0.443,0.753），下一个按下的键就绑上（用 `Keyboard.current.allKeys` 取 `wasPressedThisFrame` + `displayName`）。
>      ⚠ 原生这个控件**不是输入框**（不能自由打字）。
>   ⑦ **输入框改成原生样式**（原生 `TextfieldConsoleUGUI`：左标签 + 右侧框；文字用 `JMH Typewriter`、**右对齐**、深灰 0.25
>      ⇒ 底必须是浅色，用游戏自己的 `InputFieldBackground`），**驱动仍是我们自己的**（`TickInputs`：ASCII/退格/回车）。
>   ⑧ **尺寸对齐原生量级**：行高统一提到 **40**（原生行高就是 40；之前 30~38），字号同步放大（标签 20~22、值 20），
>      滑条 44 高（轨道 15 ✓、手柄 30 ✓）、复选框 40（方框 26）、选项卡 46、下拉 56（标签在上 + 框在下，同原生）、
>      输入/键位/选择器 40（值框 32~40 高）。横向仍受面板宽度限制（250 vs 原生 742），所以只对齐**竖向尺寸**、字号取“放得下的最大”。
>   实测（Gallery 自动化）：`tap:nw:scnext → 选择器 → 第 4 项`、`tap:nw:tab → 页签 → 诊断`、`tap:nw:check → 勾选框 → 开/关`、
>   `tap:nw:主按钮 → 点了主按钮`、`scroll:nw:scrollarea:-3 → nw_view.y 由 -6 变 +84`（向下滚 = 内容上移 ✓）。
> - 2026-09-13（二十二）**拖拽条改成原生样式 + 滚动条 + “设置页没加载就空样式”的兜底**（用户：“还有个滚动条没做”、
>   “拖拽条的样式不像游戏原生的”、“应该是设置菜单没加载，超过来的直接空样式了”）：
>   ① **拖拽条照抄原生**（实测原生 `SliderConsoleUGUI`）：轨道 `SGRounded` 黑 0.471 **高 15**（原来是 10）、
>      填充 `SGRounded` 黑 0.863、左右各内缩 10；手柄 = **30×30 `SGRounded` 灰(0.545,0.565,0.604) 用 `Simple` 画**
>      （原来是 14×20 白细条 Sliced → 形状完全不对），手柄背后还有一层 `SUGShadowLite`；右侧数值右对齐。
>   ② **原生滚动条**（`NativeWidgets.NativeScrollbar` + `BuildScrollbar`）：宽 20 的 `SGRounded` 黑 0.471 槽
>      + `Sliding Area`（上下各内缩 10）+ `SGRounded` 灰手柄（高度按“可视/内容”比例、可拖）——
>      控件页内容超过一屏时自动挂右侧，**滚轮**（路由器本来就发给“指针下最上层可滚动热区”）与**拖手柄**都能滚，
>      内容装进带 `RectMask2D` 的 `nw_view` 里整体平移；返回/关闭钮**固定**在底部（不跟着滚）。行数上限 9 → 14（超了靠滚动）。
>      实测：`nativew` + `scroll:nw:scrollarea:-3` → `nw_view` 的 y 从 -6 → -40（内容真的动了）。
>   ③ **“设置菜单没加载 → 空样式”的真因**：`NativeWidgets.Capture()` 只在开页那一刻扫**原生 Settings 页**，
>      而那页是玩家打开设置时才建的 → 一个素材都抄不到：**字体 null（字全不显示）** + 底图全无
>      （历史实现在缺素材时给阴影层画了**纯白** → 页面上出现一大块白）。
>      现在：字体与素材都做**懒加载 + 三级兜底**（重试 Settings 页 → 全场景扫同名 sprite → 我们的 `UiSkin.NativeGet`），
>      字体兜底顺序 = Settings 页 → **ESC 模板按钮上的字体** → `UiKit.EnsureFont`；
>      装饰层（`SUGShadowLite`）**缺素材就不画**，按钮/滑块底色缺失时退化成“纸色/灰”而不是白。
>      诊断：`nativew` 现在会打印 `字体=… 缺素材=… sprite=…`（实测 `字体=CourierPrime-Regular SDF 缺素材=（无）`）。
> - 2026-09-13（二十一）**Coop 入口白底修复 + 原生控件 Gallery（测试模组提供）**：
>   ① 用户：“Settings 右下角 Coop Menu 变成白色背景了” —— 真因是我上一版为了去白叠层，在“我们自己没有根底图”时把
>      `targetGraphic` 设成了 **null** → 抄来的 `colors`（normalColor 纯黑）**不生效** → 镜像出来的 `Bg` 保持白色；
>      而原生那一层是被 ColorTint 染黑的 ⇒ 我们比原生白一大截。修法：**与 UIKit 侧同口径** ——
>      “模板的 tint 目标是谁，就把我们的 `targetGraphic` 指到镜像出来的那一层”（`MainMenuEntry.CopyTintLikeNative`）。
>   ② 新增 **原生控件 Gallery**（`src/OpenNestUIKit.Test/NativeGallery.cs`）：测试模组启动时注册一个
>      `NativeMenuEntry`（id `test.uitest.gallery`，`RowPage` = 全部控件），在原生 ESC 菜单里单独占一格
>      「Widget Gallery」——就是第三方模组的正规用法。实测 `menutree`：4 个格子（uikit_home / coop / mod_menu / gallery）
>      同尺寸 109×27。
>      控件页内容：大标题 / 小标题 / `最近操作` 文本 / 检查框 / 拖拽条 / 下拉框 / 选项卡 / 主按钮 / 次按钮 / 输入框 + 返回；
>      **任何离散操作都会刷新页首那行“最近操作”**（能一眼看出回调真的跑了）。命令：`gallery`（注册+开页）/ `galleryoff`。
>   ③ 控件页重建改成**下一帧**（`NativeMenuPage.ReloadRows()` → `Tick` 里执行）：在点击回调里当场 `Destroy` 热区自身
>      会让路由器引用到已销毁对象（后续点击可能错乱）。
> - 2026-09-13（二十）**原生控件页：把游戏 Settings 页那套控件搬到我们注入的原生子页**（用户：“用在注入的原生菜单点击
>   分出的子页的 UI 上…都做…可以直接抄，都在原生的 Settings 子页下面”）：
>   新增 `Native/NativeWidgets.cs` + `NativeRow` 声明式行模型，支持 **9 种行**：大标题 / 小标题（带下划线）/ 纯文本 /
>   普通行按钮 / 主按钮 / 次按钮 / 检查框 / 拖拽条 / 下拉框 / 选项卡 / 输入框。
>   ① **素材照抄**：`NativeWidgets.Capture()` 扫一遍原生 `Settings menu` 页，按名字收 sprite（实测收到 16 个：
>      `SUGGradientRounded(_Top/_Bottom)`、`SGRounded`、`SGCheckMark`、`SGDownArrow`、`SUGShadowLite`、`UI Box Castile`、
>      `UI box partial star` …）+ CourierPrime 字体；之后即使那页被关也能建控件（没收到则退回纯色块）。
>   ② **控件实现**：检查框 = 20×20 黑圆角框 + `SGCheckMark`；拖拽条 = `SGRounded` 轨道（黑 0.47）+ 黑填充(0.86)
>      + 手柄 + 右侧数值，用**真的 `Slider`**（`fillRect/handleRect`）且用热区拖拽驱动；下拉框 = 深色圆角框 + `SGDownArrow`，
>      点开展**选项列表**（选项热区后登记 = 在上层，实测展开后 zones 里多出 `nw:ddopt:*`）；选项卡 = `SUGGradientRounded_Top`
>      深色条 + 选中项变色（强调色 0.55/0.65/0.83）+ 底部亮块；输入框 = 深色框 + 文本（v1 只收 ASCII/退格/回车，
>      中文请走我们自己的窗口页——那边有 IME 管线）。
>   ③ **入口**：`NativeMenuEntry.RowPage = () => List<NativeRow>`（优先级 `子条目 > RowPage > OnClick > PageId`），
>      点击后在本页用 `NativeMenuPage.ShowRows` 渲染；演示命令 `nativew`（一页铺开全部控件，实测 8 行 y=122…-128 刚好放下）。
>   ④ **两个已经报过的 bug 一起修了**：
>      · “注入的三个按钮点不了”：去掉根底图后**按钮没了射线目标** → 镜像出来的那层底图改为 `raycastTarget=true`
>        （与原生 `Bg` 一致：`pagedump` 里原生的按钮 `Bg` 就是 `ray=True`）。
>      · “Settings 里右下角 CoopMenu 注入也有白色叠层”：`MainMenuEntry` 两处注入同样改成“模板根有 Graphic 才自建根底图”，
>        否则只镜像原生那一层（实测场景里 `OpenNest_CoopEntry` 现在只剩 `Bg`(ray) + `Text`）。
> - 2026-09-13（十九）**注入行“贴齐原生”：单层底图 + 去掉兜底换色 + 格子跟原生等比缩**（用户：“颜色正常了，不需要兜底
>   换色，原生只有一种背景…一行两列按钮之后整个行的宽度超过了原生宽度，不一致，然后里面的字显示不全，需要统一缩小一些”）：
>   ① **只镜像一层底图**：实测原生 `OpenSettingsBtn` 根节点**没有底图**，可画的子图只有 `Bg`（`UI Box Castile` ppuMul 2.5，
>      tint 目标）与 `Selected`（ppuMul 2.79，在 `SelectionUGUI` 下，**未激活**）→ 玩家看到的只有 `Bg` 一层。
>      我们以前“根上再画一张自己的白底图” = 原生没有的那层（“白色的叠层”）。现在 `NativeMenuStyler.CopyTint` 只镜像
>      **最上面那层启用中的、非阴影的子底图**，我们自己不再加任何底图（日志：“底图照抄原生**单层**”）。
>   ② **删掉兜底换色**：`EnsureTextContrast` 停用（保留体、不调用）—— 文字与底图颜色一律照抄原生，不自作聪明换色。
>   ③ **格子跟原生等比缩**：以前原生行缩了 k、我们的两列格子还是 250 宽 → 整行突出（实测原生 225 vs 我们 250）。
>      现在格子宽/高/间距/字号全部乘 k：`109x27`、字号 `14.37`、两列 x=±58 → 整行 **225.2 ≈ 原生 225**；
>      块内行距也跟着缩（29.6）；另：文字框左右内边距不再照抄原生 250 宽行的 20px，改成格子宽度的 6%
>      → 文字区 `96x27`（“Mod UI Kit” 等标签不再被省略号截断）。
>   取证：`esctext`（底图只列一层 `[Bg]`）+ `menutree`（尺寸/字号/文本区）+ `native.log`（“格子=121x30×2行 压缩=0.898”）。
> - 2026-09-13（十八）**修正十七的错误归因（“注入项没有字”的真因）+ 格子缩小 + 颜色照抄**（用户：“文字显示正常了，
>   但是各个按钮没有对应的缩小一些，高度不够挤在一起了，颜色改为抄颜色而不是瞎改成白的”）：
>   ① **真因（已实验证实）**：不是颜色、也不是 tint —— 是**文字框比行高还矮 + TMP 截断模式**。照抄模板内边距后
>      格子 121×38 的文字框只剩 **81×18**，而 CourierPrime **行高 ≈ 1.467×字号**（18 号 ≈ 26.4px）→ 18 < 26.4，
>      TMP 在 `Ellipsis`（截断类）模式下**整行不画**（`行数=0 字符=0 顶点=0`），底图 Image 照画 ⇒ “白框里没有字”。
>      原生模板文字框也只有 20 高（字号 25！）却正常，因为原生用 `Overflow`。
>      **修**：`NativeMenuInjector.StyleGridCell` 把文字框**垂直铺满**格子（`offsetMin.y = offsetMax.y = 0`，保留左右内边距）。
>      **铁证**：`nativebtn` 的 X/Y 对照（同画布同代码，只差文字框高）`X 旧:18高 → 行数=0 字符=0` /
>      `Y 修:铺满 → 行数=1 字符=6`。
>      （十七说的黑 tint 是**另一个真问题**：它让底图变黑；把它当“没字”的原因是误判，见下条。）
>   ② **颜色：改为照抄，不再改白**：`NativeMenuStyler.NormalizeTint`（“太暗就换白底”）删除，改为 `CopyTint` ——
>      `colors`/`transition` **原样照抄** + 照原生把 tint 打在**镜像出来的同名子节点**（原生是子物体 `Bg`）上，
>      根底图不吃 tint ⇒ 静止时与原生纸面一致，悬停仍有反馈。Coop 自带回退注入 `MainMenuEntry.CopyTintLikeNative` 同口径。
>   ③ **尺寸：整体等比缩小**（“各个按钮没有对应的缩小一些”）：我们的注入块要占地方，只压间隔会让原生 40 高的框挤在
>      30 的节距上互相压 10px。现在 `ScaleNativeRow` 把**原生行的框与字号按同一 k 一起缩**（k=(可用高−我们块)/原生跨度，
>      下限 `MinNativeScale=0.72`，原值取模板 ⇒ 不会越缩越小）；我们的格子缩到 **121×30 / 字号 16**（`GridCellH`/`GridFont`），
>      块内行距 33。实测 k=0.898：原生 `250×40 → 225×36`、字号 `25 → 22.46`、节距 `35.6 → 32`
>      （节距/框高仍是游戏自己的 0.89 → 叠加量与原生自身同级）。
> - 2026-09-13（十七）**“ESC 菜单注入项没有字”的归因（后被十八修正）+ 聊天回车 + 输入框左对齐**：
>   ① ~~真因不是文字颜色，而是**按钮的 ColorTint**~~ —— **归因错误，见（十八）**：ColorTint 确实会生效（`白底 × 黑 tint = 黑块`），
>      造成的是“**框黑**”而不是“没有字”；“没有字”的真因是文字框 81×18 < 行高 26.4 且 `Ellipsis` 把整行丢掉。
>      原生按钮 `colors.normalColor` 是**纯黑**，原生那边 tint 落在子物体
>      `Bg` 上（上面还有一层白的 `Selected` 盖着，所以无害）；**我们把底图 Image 放在按钮自己身上**，
>      `AddComponent<Button>()` 让它自动成为 `targetGraphic` → `白底 × 黑 tint = 黑块`。
>      所有“看着正常”的属性（`img.color` 仍是白、字体/材质/尺寸/cull 全对）都查不出来，得看
>      `Selectable.colors` 与 `CanvasRenderer.GetColor()`。当时的修法 `NormalizeTint`（太暗的 tint 换白底）
>      **已被（十八）按用户要求撤掉**（改成 `CopyTint`：照抄颜色 + 镜像 tint 子节点），因为用户要的是“抄颜色而不是瞎改成白的”。
>      验证：`nativebtn` 在**我们自己的画布**里跑同一段 `CreateNativeButton` —— 修前 4 行全黑什么都看不到，
>      修后 2 行带字可见（截图 `nativebtn.png`）；`esctext` 显示我们的行 `normal=(1,1,1,1)`、原生仍是黑（对照组）。
>   ② **输入框“居中了”**：`UiTextInput.Create` 不管有没有标签都固定留 110px 标签宽 → 悬浮聊天那种**无标签**输入框
>      被挤到中间（看起来像居中）。现在没标签就铺满整行（左对齐就是真左边）。
>   ③ **回车**（用户：“回车聊天框还是没生效 / 输入回车后没有效果”）：以前只看 `kb.enterKey.wasPressedThisFrame`
>      （单帧脉冲，那帧没读到就整次丢掉），而且**文本通道的 `\r` 是被我们故意丢掉的**。
>      现在三通道取并（物理键**上升沿** + wasPressedThisFrame + 文本通道 `\r`，组合中不当作回车 —— 那是输入法确认），
>      悬浮聊天层的“回车唤入”与输入框的“回车提交”共用同一份判定 `UiTextRouter.EnterThisFrame`；
>      并用 `ConsumedEnter` 隔开“同一次回车既提交又立刻重新展开”（现象就是“回车没反应”）。
>   ④ 诊断补齐：`UiTextRouter.Diag`（上一帧停在哪一步）/`LastEnter`（最近一次回车的决策）/`UiChatOverlay.Probe`
>      增打 `上次发送 / 开·关次数 / 最近收起原因`。
> - 2026-09-13（十六）**注入到原生 ESC 菜单的文字终于真看得见了**（用户：“ESC菜单里的注入项还是没有字”）：
>   上一版把模板里“暗色”的正文**提亮成白色**（当时以为黑色=看不见）——**恰恰相反**：本游戏原生 ESC 按钮是
>   **浅色纸面（`UI Box Castile`，`底色=(1,1,1,1)`）+ 黑字**（实测 `颜色=RGBA(0,0,0,1)`）→ 白字白底 = 全看不见。
>   现在 `NativeMenuStyler.CopyTextVisual` **原样照抄模板正文颜色**（只跳过 shadow/outline/glow 与 alpha≈0 的副本），
>   并加了对比度兜底 `EnsureTextContrast`（底图不透明且亮度差 < 0.35 才按底翻成黑/白）。
>   取证（不再靠猜，也不靠“看起来对”）：新增 `esctext`（逐行对比 **我们 vs 原生模板** 的底图/底色、字体、材质、
>   着色器、字号、颜色、文本矩形、字符数/顶点数——两边一致 = 可见性一致）与 `escframe:<名>`（把 ESC 容器的
>   **祖先链临时激活**几帧截图，截完立即还原）。实测两端：我们与原生 **底图/底色/字体/材质/颜色逐项相同**。
>   顺带查到并记下的两件事：① 同一帧内 `RemoveInjected`→重建 会在 dump 里看到 6 行（`Destroy` 帧末生效，
>   下一帧就正常）；② `menutree` 里的 `/off` 只是容器没开时的 `activeInHierarchy`，不代表字本体有问题。
> - 2026-09-13（十五）**联机菜单恢复原面板配色 + 配置空值项给空文本框**（用户：“联机菜单的各种颜色也没了”）：
>   ① UIKit 版的联机页第一版全用默认体色（`page.Label`），把原面板 `CoopUIManager` 里那套 TMP 富文本配色丢了。
>      现在**原样复用原面板色值**（不另造一套）：版本 `#8f8` + 加载器 `#aaa`、身份 tag `#8cf`、
>      Steam 就绪 `#7f7` / 初始化中 `#fa0`、版本不一致·满员·错误 `#ff6b6b`、带锁 `#fa0`、提示行 `#9aa3b3`；
>      另补齐：选中页签 `> ` + 绿色（原 `BuildIdleTabs` 写法）、带密码 `#8f8` / 无密码 `#aaa`、
>      成员行（房主标 `#fa0` / 自己标 `#8cf` / 延迟分级 / 角色按色）、聊天行（人名 `#8cf` + 正文 `#d9e6ff`）。
>      ⚠️ 用的是**原面板就在用的 3 位十六进制**（`<color=#8f8>`），实测游戏字体支持，不必写成 6 位。
>      实测取证：G 端 `c-main.png`（绿标题 + 绿选中页签 + 青色身份）、`c-lan.png`（绿页签 + 绿 IP + 绿标题）。
>   ② **空值项要空文本框而非“暗掉的一行”**（用户原话）：`page.Text` → `UiTextInput` 在空值时就是
>      **一个空框**（只在聚焦时画光标），不必额外做占位符；配合 `docs/MOD_MENU.md` 更新记录二十（文本/快捷键项不再标只读）。
>      实测：G 端 `cfg2.png` 设置页=3 个开关 + `FakeId/Name/Port/LastHost` 四个输入框（前三个空值显示为空框）。
> - 2026-09-13（十四）**ModMenu 界面细节对齐原生（用户 5 条反馈）**：
>   ① **选中标记变成方框**：`▸`(U+25B8) 在游戏 TMP 字库里缺字，和 `✕`/`‹`/`›` 同一类问题 → 全改 ASCII `> `。
>      （原生 `ModMenuDebugView` 里就有同样注释：“`▸` 之类会渲染成方框，已踩”。）
>   ② **标题宽度没铺满、错误换行**：`UiSeparator` 的标题写死 `200` 宽 → 长标题在 200px 里折行，
>      而行高固定 22 → 第二行溢出/被裁。改为**铺满整块 + 单行省略**（`UiText.SetSingleLine`）；
>      动作行标签也改成单行省略（长模组名不再折行撞下一行）。
>   ③ **来源标记没了**：列表行恢复原来的 `[BepInEx]` / `[MLL]` / `[dep]` pill（**纯文本前缀**，颜色走 TMP 富文本），
>      后面跟 `MetaText`（版本号 / 已加载 / 未加载 / 磁盘禁用）——**状态颜色也回来了**（绿/灰/琥珀，同原生 `MetaColor`）。
>      热区名去掉富文本标签（`row:Open Nest Co-op  …`），自动化寻址干净。
>   ④ **配置项里的输入框**：文本 / 无范围数值 / 快捷键类设置以前和自带界面 v1 一样**只做只读展示**
>      （用户看到“有值但没有输入框”）；现改为**可编辑输入框**（`page.Text` → `UiTextInput`，走原模组输入管线，
>      中文可输），写失败弹 toast。
>   ⑤ **诊断页补齐**：把原生 `ModMenuDebugView.Collect()` 的**全部分组**搬过来——该模组（格式/宿主/状态/路径/
>      注册表/重复风险）、**帧性能（FPS·均值·最差 + top 热点）**、加载器与桥（双 Harmony 警告）、全部路径、
>      资产扫描统计、**统一顺序（含完整顺序表）**、重复加载与 **Harmony 程序集**、能力探测（输入/指针/API/语言）、
>      日志与配置层；警告项用红字，读不到就写“（无）”。
> - 2026-09-13（十三）**交互补齐（用户：“布局好多了，但是交互还是差点，中文输入支持了，但是会串拼音进去”）**：
>   ① **串拼音**：打拼音时**首字母那一帧系统输入法还没开始组合**（`GCS_COMPSTR` 还空）→ 物理键直接把它写进输入框了。
>      照搬原模组的**首字母缓冲 + 快模式**（`UiTextRouter.ProcessKeyChar/ProcessPendingChar`）：
>      首字符先缓 2 帧 —— 组合起来了就丢弃（是拼音首键），没起来就补回去（是英文）并进快模式（后续英文不再延迟）；
>      组合判定同时看原生 `GCS_COMPSTR`、`compositionString`、`compositionLength`。
>   ② **ModMenu 左栏模组列表改成内嵌 List**（契约新增 `UiRowKind.List` + `UiPageDef.List(key, height, build)`）：
>      列表固定高、**自带滚动条与滚轮/拖拽**，不再把整页撑长（以前 13 行 Button 直接摊在左栏）。
>   ③ **Coop 悬浮聊天层**（契约新增 `UiKitHost.SetChat/ClearChat/FocusChat/CloseChat/IsChatTyping`，
>      宿主渲染 `Widgets/UiChatOverlay.cs`）：原模组那套“左中常驻半透明浮窗 + **回车唤入** + 回车发送 + ESC 收起”搬回来了。
>      它用**自己的画布**（`OpenNestUIKit_HUD`，层级 32760 —— 菜单画布关菜单时会整体隐藏，而浮窗要在菜单关着时也在）；
>      聊天输入直接用 `UiTextInput` → 与其它输入框同一套输入管线（1. 物理键 + IME）。
>   ④ **注入到原生菜单的文字消失了**（用户报）：实机 `menutree` 实测抄到的文字颜色是 **`(0,0,0,1)`（纯黑）** ——
>      模板按钮的子节点里**还有阴影/描边副本**，`GetComponentsInChildren(...)[0]` 拿到了阴影。
>      现改为 `NativeMenuStyler.PickBodyText`：跳过名字含 shadow/outline/glow 的，再按亮度挑“正文”那一颗，
>      亮度太低就退回白色。实测修后为 `颜色=(1,1,1,1)`。
>   ⑤ `menutree` 诊断命令增强：注入行额外打印 **文字/颜色/文本区尺寸**（以后这类“字没了”一眼就能定位）。
> - 2026-09-13（十二）**输入框照搬原模组的实现 + 布局向原模组看齐 + 注入改“2 列 × 3 行”**
>   （用户：“输入框你直接学一下原来模组的怎么实现的”｜“模组菜单的布局逻辑还是不如原来的”｜
>   “联机菜单的布局逻辑好多了，但还是怪怪的……仔细研究一下布局逻辑，改还是学原来的看你的”｜
>   “原生菜单多列宽度压缩又导致显示不全了，这里的压缩应该也包括上面的 Settings……”）：
>   ① **输入管线重写为原模组方案**（`Widgets/UiTextRouter.cs`，见 §8.2）：不再用 `TMP_InputField` 收键，
>      而是 **物理键轮询 + 隐藏 IME 锚点 + 原生 Win32 IME 读取 + `Keyboard.OnTextInput` patch 兜底**
>      （照搬 `CoopUIManager.PollInput/ActivateImeAnchor/ReadImeCommitted` 与 `CoopInputBox` 的自绘文本 + 光标 + 密码掩码）。
>      聚焦时只做一件事：**临时放行拦截层**（激活游戏 `EventSystem` + 启用那 1~2 个输入模块 + 压住外部射线器），
>      让隐形锚点真的“聚焦”——输入法组合才会送到它；失焦全部还原。
>      实机：`IME 锚点=已聚焦`、`聚焦='Room Name'（文本='…'）`，`type:` 写入后文本同步给 provider。
>   ② **契约新增“两栏容器”**：`UiRowKind.Columns` + `UiPageDef.Columns(leftWidth, left, right, gap)`
>      —— 把一个面板拆成「左栏固定宽 + 右栏占剩余」，两栏各自纵向流，高度取较大者。
>      同时新增**页面首选窗口尺寸** `UiPageDef.Size(w,h)` 与**紧凑密度** `UiPageDef.SetCompact()`
>      （`UiTheme.PushDensity/PopDensity`：行高 36→26、内边距 18→12、字号降 1~2 档，**构建期**生效）。
>   ③ **ModMenu 页面改成原模组的双栏**（`ModMenuTheme` 的 1180×700 / 左 440 / 右 700）：
>      左栏 = 筛选 chip + 模组列表（选中行前缀 `▸`）；右栏 = 名称/版本 → [详情][设置][诊断] 页签 → 内容；
>      底部保留运行环境与扫描统计。截图 `r12mm.png`。
>   ④ **Coop 页面改成原模组的紧凑窄面板**（`CoopUIManager` 原来是 500 宽单栏、行高 20~26、间距 6、动作按钮右对齐）：
>      `Size(520, 660) + SetCompact()` → 观感与原面板一致（截图 `r10coop/r12coop`）。
>   ⑤ **注入摆位 v4**（见 §六第 4 条）：格子从“一行 3 列（78px，中文被省略号截断）”改成 **2 列 × 最多 3 行**
>      （格子 121×38、字号 18，中文放得下），**最多 5 个我们的格子（含「更多...」）** + 1 个原生 Settings；
>      块内额外行高**计入压缩**（以前只算节距 → 末行掉出面板底 = “显示不全”）。
> - 2026-09-13（十一）**三处控件级 bug 修复**（用户：“输入框没有实现 / 拖动条上面那个拖动点位置初始化有问题 /
>   块内容超出没有自动加滚动条和拖动效果”）：
>   ① **输入框能打字了**：根因是 `UiTextInput.TickAll` 从未被调用 + 原生路径被自己（停用游戏输入模块）挡住 +
>      TMP 文本从不回写 `OnChanged`（详见 §8.2 的历史说明；十二版已整体换成原模组管线）；
>   ② **滑条把手**改成**锚点比例定位**（旧版按“构建时的像素宽 × 比例”算，构建时布局未跑 → 把手跑到轨道外），
>      并按半宽内缩；实机实测屏幕比例 0.335（值 4 / 范围 2..8 = 0.333）；
>   ③ **滚动条 + 拖动**：真因是 `UiList.ContentHeight` 只统计 `UiList.Add` 的行，而声明式页面的行直接挂在 `UiFlow` 上
>      → 内容高恒 0 → 滚动上限 0、滚动条判定“不超出”而隐藏。现 `UiFlow` 暴露 `ContentHeight` 并被列表采用，
>      列表右侧新增**可见滚动条**（轨道 + 滑块，内容不超出时自动隐藏；内容宽度预留 7px），轨道**可点可拖**（见 §8.3）。
> - 2026-09-12 建档：需求与决策整理 + 复用来源 + 技术基座核实（interop 类型实地检查）+ 工程/模块拆解 + 动态布局与页面栈设计。
> - 2026-09-12（二）**首版实现完成并双端编译 0 错误 0 警告**：三个工程 + 布局引擎 + 组件集 + 页面栈菜单 + 原生菜单注入 + 原生观感（`Theme/UiSkin`）+ 自带 7 个演示页 + 第三方声明式契约示例；§二 文件清单按**实际文件**重写；§十 改为实测结论；§十二 新增“已实现 / 待完善”清单。
> - 2026-09-12（三）**G 端游戏内冒烟实测**（`-onuk-autoopen[=pageId]` 自动开菜单 + 截屏脚本）：加载/建画布/页面渲染/12 个原生素材捕获/两个 `ESC Menu Buttons` 容器注入/输入隔离全部生效；实测中修了 4 处（不透明原生 sprite 当装饰层导致面板发白、Harmony 回调查找类型错、素材捕获日志误报“退化为纯色”、目录变化时页面半死状态）。详见 §十二。
> - 2026-09-12（四）**按钮 9-slice 选材修正 + 新增独立测试模组**：
>   ① 用户反馈“按钮背景像平铺、多颗按钮只有一种样式”→ 根因与修正见 §七.2：**九宫格要求 `border 合计 + 余量 ≤ 控件尺寸`**，
>   否则 Unity 的 `Image.GetAdjustedBorders` 会把整张图整体压进控件；且旧版四种子风格共用同一张图。现改为
>   `UiSkin.ButtonSpriteFor(primary, danger, w, h)`：候选按风格分开 + 尺寸不达标时把 `border ÷ scale` 烤入副本 + 仍不达标退纯色，
>   并逐颗打印实际使用的 sprite/border（实测日志 24 条）。
>   ② 新增**独立测试模组** `src/OpenNestUIKit.Test(+.MelonMod)`（§十三）：契约路径 + 直接调组件路径 + **CLI 脚本化模拟点击/拖拽/滚动 + 断言 + 报告**；
>   实测 `PASS=16 FAIL=0`（含模拟点击 4 次、拖拽 13 次回调、断言 5 条）。
>   ③ UIKit 侧为此新增测试注入点（`UiPointerRouter.PointerOverride/ButtonOverride`、`FindZone/TryGetScreenCenter/ZoneNames`）
>   与可选**热区名**参数（`UiButton/UiNavRow/UiSlider`），让自动化脚本能语言无关地精确寻址。
> - 2026-09-12（五）**切片定义链路 + 切片工具**：新增 `Theme/UiSlice.cs`（`UiSlice` + `UiSliceStore`：
>   读 `<configDir>\OpenNestUIKit.slices.ini`、2 秒热重载、`Changed` → `UiSkin.InvalidateBaked()`；
>   `UiSkin.RefFor/ButtonRefFor/PickForSize` **第 0 步先查定义**（有定义就无条件采用，不再自动缩放/退纯色）；
>   `UiButton/UiPanel/分隔线/模态框`改走 `UiSpriteRef`（携带 `Type`/`FillCenter`）。
>   工具为**独立离线 Python**：`tools/slice_tool.py`（tkinter GUI 拖切线 + 多尺寸实时预览 + CLI 批量/预览/自检/部署），
>   详见 `docs/UI_KIT_SLICE.md`；测试模组新增验证命令 `slices`/`slicereload`/`sliceprobe`（G 端实测 `PASS=5 FAIL=0`，
>   日志出现 `按钮素材 = '…@6,6,6,6'（用户切片；…）`）。
> - 2026-09-12（六）**用途标记 tag + 不侵入取证 + 修栈溢出 bug**：定义新增 `tag`（哪种组件的背景），
>   `UiSkin.PanelRef/LineRef/DialogRef/ButtonRefFor` **优先采用打了对应 tag 的素材**（`uiSkin.RefForTag`/`RefForTagOr`；
>   `button` 前缀含 `button.primary`/`button.danger`）；新增 `CaptureDefinedSlices()` 按 ini 名字补捕获。
>   修 **`Bake ↔ Resolve` 无限递归栈溢出**（ini 里定义了但未捕获的图会崩游戏）；
>   测试模组新增 `tags`/`tagprobe`/`nativecheck`，实测证明：本模组**只改自己画的界面**，
>   游戏原有 Sprite 的 `border` 从未被改写（`nativecheck`：游戏节点用我们副本 = 0；同名游戏图仍为作者原值）。
> - 2026-09-13（七）**原生菜单组件普查 + 倍率对齐（半成）**：新增 `Native/UiNativeMetrics.cs`（只读诊断，
>   `uiscale` / `nativemenu` 命令：把原生画布/组件/字号/素材/`pixelsPerUnitMultiplier` 量出来）；
>   实测**原生用 `UI Box Castile` 的按钮/面板底都是 `ppuMul=2.5`**（我方之前硬写 1 → 装饰比原生粗 2.5 倍），
>   已新增 `UiTheme.SpritePpuMul = 2.5f` 并用于 `UiButton/UiPanel` 的装饰层。
>   ⚠ **⾸时搞置（待重新定方案）**：改成全局 2.5 后尺寸对了，但大面板上的边框观感"过细/不像原作者的装饰框"
>   （原生对不同尺寸元素可能用不同 ppuMul；`UI Box Castile` 18/34/18/16 在 1180x720 窗口上按 2.5 渲染只剩 7/13。
>   待办：按元素尺寸选 ppuMul，或把 ppuMul 做成切片定义里的字段（`ppumul=`）。见 `docs/UI_KIT_SLICE.md` 七节。
>   该次新增 `nativemenu` 普查命令，**已量清原生规律**：按用途让渲染后的边框落在 ≈7~14px（非全局固定值），
>   完整画布/组件/字号/ppuMul 表见 §7.4；下一步按“目标边框像素”反推 ppuMul。
> - 2026-09-13（十）**注入改成“一行多列 + More...” + 两个模组改成“一页 + 页签”（对齐原界面）**：
>   ① 用户反馈“只压间隔也不行，显示效果太差了，应该改为一行多列，超过多少之后最后一个按钮变为 More...” →
>      我们注入的条目**排成一行多列**（`NativeMenuInjector.GridCols=3 / MaxGridRows=1 / GridFont=16`），
>      整块**只占 1 行**（原来是 3 行竖排）→ 原生列表只让出 1 行，**节距 1:1 不再压缩**
>      （实测日志：`总缩=298 可用=301 压缩=1`；三个格子 78x38 在同一 y、x=-86/0/86）；
>      条目数超过容量（列×行=3）时**最后一格变「更多...」**，点它进原生页逐级展开；
>   ② 用户反馈“层层嵌套的显示效果太差，不如原来两个模组的布局方式” → 两个模组的 UIKit 菜单改成**只有一页**：
>      - Coop：页签 [Steam 大厅][局域网][设置]（未联机）/ [房间·成员][聊天]（已联机），内容渲染在同一页；
>      - ModMenu：筛选页签（全部/模组/依赖/已禁用）+ 模组列表（点行选中）+ 选中项的 [详情][设置][诊断] 页签；
>      - 契约新增 **`UiRowKind.Tabs`**（`UiPageDef.Tabs(key, tabs, index, onChanged)`，渲染成真页签栏）；
>      - 叶子入口（没有子级）点一下**直开目标页**，不再先过一层原生页。
> - 2026-09-13（九）**注入摆位 v2（“应该在 Settings 后面”+ 不再自动缩放高度）+ 两个模组的整套菜单迁到 UIKit**：
>   ① **摆位改成“插到锚点之后 + 按原生节距结构重排”**：默认插在 **`OpenSettingsBtn`（设置）之后**（可用 `NativeMenuEntry.InsertAfter` 换锚点，空串 = 追加到最后）；
>      基准是**首次见到的原生几何模板**（块内节距 ≈35.6、块间 45~52、行高 40），重排时**任何行的尺寸/字号都不动**，
>      只压**间隔**：① 块间 > 40 先压到 40 → ② 再等比压，**节距下限 30** → ③ 还不够才继续压并告警。
>      为什么这么做：旧算法用“估计节距”把原生行拉开再整体缩，注入 3 行时压到 0.66 倍 ——
>      用户反馈“**自动缩放高度导致高度不够内部的字穿出去了**”；现在行高不再被改，只是节距变紧。见 §六第 4 条；
>   ② **provider 根页不再重复列子页**：根页自己用 `Nav` 指过的页不再自动再加一行（否则同一页在根页出现两次）；
>      provider 也改成“只声明当前状态下有意义的页”（在大厅里就不声明大厅/局域网页）。
>   ③ **契约新增三个能力**（动态内容必需）：`UiKitHost.Refresh()`（原地重建当前页）、
>      `CurrentPageId`（我这一页是不是正在被看）、`IsTextInputFocused`（**打字中不刷**，别把草稿冲掉）。
>   ④ **Coop 整套联机菜单改由 UIKit 渲染**（用户：“联机菜单的页面没有好好设计过，刚好你可以好好设计一下；不反对重新布局，但要实现原有功能”）：
>      根页 = 状态 + 去哪；子页 = Steam 大厅（房间名/密码/人数/创建/刷新/重连/列表逐条加入）、
>      局域网（身份/名字/本机 IP 一键复制/创建/手动 IP:端口+粘贴/加入/扫描/房间逐条加入）、
>      房间与成员（房间信息/密码状态/角色/成员列表+角色/踢出/邀请/离开）、聊天（历史 + 输入 + 发送）、
>      同步设置（三个开关 + 身份/端口/配置路径）、关于。原 UGUI 面板（`CoopUIManager`）**降为回退实现**，
>      检测到 UIKit 时自动收起（不再两个面板同时出现），F8/入口都走 UIKit。
>   ⑤ **ModMenu 补齐原有功能**：每个模组一页 = 详情（版本/宿主/格式/路径/配置/依赖/不兼容/备注）+
>      操作（磁盘启停、顺序上/下移、运行时启停、恢复默认、重跑受管初始化）+ 设置行（与自带设置页同源）+ 筛选页 + 逐条状态诊断页。
> - 2026-09-13（八）**ESC 层级模型 + 菜单注入归 UIKit 一家 + Coop/ModMenu 接入（用户要求）**：
>   ① **ESC 层级模型**（`Native/UiEscapeLevels.cs`）：以前“要不要阻止游戏 ESC”只有一个裸 bool，哪条关闭路径漏放行就**永久阻止**
>      （用户反馈：“**ESC 层级还是不对，应该无层级的地方调不出 ESC 菜单**”）。现改为**显式层栈**：每开一个消费 ESC 的界面（窗口 / 原生页）
>      `Push` 一层，关掉 `Pop` 一层；**层数 0 ⇒ 我们一步不留地交还游戏**（层数变化立即同步 + 守卫每帧自愈），
>      “关掉最后一层的那一次按键”仍由 `ReleaseWhenKeyUp()` 吃掉（不会顺势把 ESC 菜单调出来）。详见 §8.1；
>   ② **运行标记**：新增 `OpenNestUIKit.UiKitMarker`（宿主程序集里的“环境里有 UIKit”标记，第三方**反射**探测、不必引任何 dll）；
>      契约新增 `UiKitHost.IsMenuOpen / OpenMenu / CloseMenu / ToggleMenu / CanControlMenu`（宿主启动时挂实现，
>      第三方无需引宿主程序集就能开/关本库菜单）；
>   ③ **注入归 UIKit 一家**：第三方/provider 只要实现 `IUiKitProvider`，宿主**自动替它往原生 ESC 列表注入一行**
>      （标题/排序/是否注入用可选接口 `IUiKitNativeEntry` 定制；provider 注销时行自动移除）。
>      于是 Coop / ModMenu 不再各自往原生容器里塞按钮、各自重排（旧的 `OpenNest_CoopEsc` 已消失）。详见 §5.3；
>   ④ **注入摆位改了**：默认**原生行一行不动**，我们的块接在原生末行下方（旧“重排 + 等比压缩”在注入 3 行时会压到 0.66 倍：
>      38 高按钮只剩 29 节距 = 看着叠在一起）；只有下方真的放不下才退回压缩。见 §六第 4 条；
>   ⑤ **Coop / ModMenu 接入 UIKit（软依赖，缺则回退自带界面）**：新增 `OpenNestCoop.UI.UIKitIntegration` +
>      `OpenNestModMenu.UI.UIKitIntegration`（反射探测 + `[MethodImpl(NoInlining)]` 隔离，没装 UIKit 时纹丝不动）。
>      ModMenu 的模组列表/设置行交给 UIKit 渲染（行内容与自带设置页共用 `ModMenuSettingsSource`，避免两处漂移）；
>      Coop 先迁“菜单入口 + 常用同步开关”，**大厅面板（服务器列表/聊天/IME）仍是自带实现**（下一阶段）。

---

## 一、技术基座核实（2026-09-12 实地检查）

「动态布局 + 完整组件」依赖 UGUI 的布局组/滚动/控件类型在 IL2CPP 裁剪构建里**没被裁掉**。
本次用二进制字符串堆检查两端 interop 程序集（`GetString(ReadAllBytes(dll)).Contains(typeName)`），结果：

| 类型 | G 端 `BepInEx\interop\UnityEngine.UI.dll` | D 端 `MelonLoader\Il2CppAssemblies\UnityEngine.UI.dll` |
|---|---|---|
| `VerticalLayoutGroup` / `HorizontalLayoutGroup` / `GridLayoutGroup` / `LayoutGroup` | ✅ | ✅ |
| `ContentSizeFitter` / `LayoutElement` / `LayoutRebuilder` / `AspectRatioFitter` | ✅ | ✅ |
| `ScrollRect` / `RectMask2D` / `Mask` / `Scrollbar` | ✅ | ✅ |
| `Toggle` / `ToggleGroup` / `Slider` / `Dropdown` / `InputField` | ✅ | ✅ |
| `CanvasGroup` / `Selectable` / `Shadow` / `Outline` / `RawImage` | ✅ | ✅ |
| `TMP_Dropdown` / `TMP_InputField`（`Unity.TextMeshPro.dll`） | ✅ | ✅ |

**结论**：类型在两端都存在（**未被裁剪**）。⚠️ 但"类型存在"只证明可 `AddComponent`，**不证明运行时布局/滚动行为正确** ——
`docs/MOD_MENU.md` 记过"IL2CPP 下代码构建 ScrollRect 容易踩坑"，且 `pixelsPerUnitMultiplier` 在本游戏 IL2CPP 下**改值不生效**
（`docs/NATIVE_UI.md` §6）。因此本库的布局引擎**不把 UGUI 布局组当唯一路径**（见 §三）。

---

## 二、工程结构（文件级拆解）

```
src/OpenNestUIKit.API/                契约程序集（纯 .NET：无 Unity / Il2CppInterop / 游戏程序集依赖）
  OpenNestUIKit.API.csproj
  UiKitHost.cs                        静态注册表（Register/Unregister/Changed/IsHostAvailable/ApiVersion）
  IUiKitProvider.cs                   第三方模组契约（Id/DisplayName/Version/Author/BuildMenu）+ 默认实现
  IUiMenuTree.cs                      声明式菜单树（UiMenuTree：Root/Page(id,title)/Pages/Find）
  UiPageDef.cs                        页面构建器 + IUiPageDef（Header/Label/Separator/Button/Nav/Toggle/Slider/Choice/Text/KeyBind/Progress/Tabs/Columns + Size/SetCompact）
  UiRow.cs                            行模型（UiRowKind 枚举 + 值/选项/范围/回调 + 两栏容器的左右子行）

src/OpenNestUIKit.Test/               独立**测试模组**（BepInEx 壳；开发/验收工具，生产可不部署）
  OpenNestUIKit.Test.csproj
  TestInfo.cs / TestPlugin.cs / TestMelonEntry.cs    身份 + 双端入口壳
  TestRuntime.cs                     运行时骨架 + 每帧驱动（含 TestBehaviour）
  TestLog.cs                         自带日志/报告器（写 OpenNestUIKitLogs\test.log，PASS/FAIL 计数）
  TestProbe.cs                       点击探针（CLI 自动判定"点击是否真的生效"）
  TestProvider.cs                    契约路径测试（只引 .API）
  TestPages.cs                       直接调 UIKit 组件的测试页（按钮素材实验室/组件/热区/契约状态）
  TestDriver.cs                      **CLI 驱动程序**（模拟点击/拖拽/滚动 + 断言 + 报告）
src/OpenNestUIKit.Test.MelonMod/     测试模组的 MelonLoader 壳（共用同一份源码）

src/OpenNestUIKit/                    主体（BepInEx 壳 + 全部 Unity 代码；ML 壳工程 Compile Include 同一份源码）
  OpenNestUIKit.csproj                部署：BepInEx\plugins\
  UiKitInfo.cs                        身份常量（Guid/Name/Version，双端共用）
  Plugin.cs                           BepInEx 入口壳（BasePlugin）
  MelonModEntry.cs                    MelonLoader 入口壳（BepInEx 工程排除、ML 工程编译）
  GlobalUsings.cs / PlatformUsings.cs 全局 using + 双端 interop 命名空间适配（Il2Cpp 前缀）

  Vendor/                             【拷贝副本，只改 namespace】Logging/{ILogger,CoopLog,ModLog}.cs、UI/{UiKit,INativeUiService}.cs
  Core/UiKitRuntime.cs                平台无关运行时（Initialize/Startup/Shutdown + 幂等守卫 + 文件日志路由）
  Core/UiKitBehaviour.cs              每帧驱动（MonoBehaviour；IL2CPP 需 ClassInjector；含 -onuk-autoopen 自测钩子）
  Core/UiKitPaths.cs                  双加载器目录解析 + 部署形态探测
  Core/UiKitLoc.cs                    文案门面 T(zh,en)（按当前语言选）+ 语言变化轮询事件

  Theme/UiTheme.cs                    调色板 / 尺寸 / 字号 / 过渡时长 / 小工具（Hex/Hover/Pressed/StyleButton）
  Theme/UiSkin.cs                     原生观感：UI Box 系列现场捕获 + 副本自持有 + border 烤入缩放
  Theme/UiKitTween.cs                 过渡动画（淡入/缩放，由 UiKitBehaviour 驱动，无协程）

  Layout/UiSize.cs                    尺寸约束（Auto/Fixed/Grow/Percent）+ 方向 + 内边距
  Layout/UiMeasure.cs                 测量中枢（登记函数 → LayoutElement → 子 Flow → TMP preferredHeight → 兜底）
  Layout/UiFlow.cs                    动态布局容器（垂直/水平 + Padding/Gap + Grow/Percent + 递归 Apply）
  Layout/UiStretch.cs                 摆位小工具（Fill/StretchTop/Size/HostSize）

  Widgets/UiWidget.cs                 组件基类（Rect/显隐/销毁/热区注销）
  Widgets/UiText.cs                   文本（Title/Header/Body/Note/Value 语义样式 + 自动换行 + 内容高度）
  Widgets/UiButton.cs                 按钮（Primary/Secondary/Danger/Ghost）+ UiSurface（填充+装饰双层）
  Widgets/UiPanel.cs                  面板/卡片 + 分隔线 + 进度条
  Widgets/UiControls.cs               开关 / 滑条 / 步进器 / 循环选择
  Widgets/UiInputs.cs                 文本输入（自绘文本/光标/密码掩码；输入交给 UiTextRouter）/ 快捷键绑定
  Widgets/UiTextRouter.cs             文本输入总管线（物理键 + 隐藏 IME 锚点 + 原生 Win32 IME + OnTextInput patch）
  Widgets/UiTabs.cs                   页签栏
  Widgets/UiRows.cs                   子菜单入口行 / 只读信息行 / 动作行
  Widgets/UiList.cs                   滚动列表（行池 + 视口裁剪 + 自管滚轮/拖拽）
  Widgets/UiWindow.cs                 窗口（标题栏 + 关闭 + 内容区 + 淡入/缩放过渡）
  Widgets/UiOverlays.cs               模态确认框 + 悬浮提示

  Menu/UiPage.cs                      页面基类 + UiSimplePage（委托构建）
  Menu/UiPageStack.cs                 页面栈（Push/Pop/Replace/Reset + 栈路径）
  Menu/UiPageCatalog.cs               页面目录（内置页 + 第三方 provider 收编）
  Menu/UiDeclarativePages.cs          声明式页面（UiRow → 真实控件）
  Menu/UiMenuWindow.cs                菜单窗口（画布 + 窗口 + 面包屑/返回/关闭 + 守卫编排 + 热键）

  Native/UiGameBridge.cs              游戏侧桥接（INativeUiService 实现 + 主菜单事件里触发素材捕获/原生注入）
  Native/GameReflect.cs               游戏类型反射查找（全名 → 简单名扫描 → 缓存 + 未命中诊断）
  Native/HarmonyReflect.cs            Harmony 反射封装（prefix/postfix，不引用 HarmonyLib）
  Native/NativeMenuBridge.cs          原生菜单项注册（多级：Parent/Order/InsertAfter）
  Native/NativeMenuStyler.cs          抄模板样式（不克隆按钮 + 跳过阴影层 + 字号固定 + 关 autoSizing）
  Native/NativeMenuInjector.cs        注入编排（Harmony postfix 时机 + slot 让位重排 + 幂等重建）
  Native/UiPointerRouter.cs           自管指针命中（点击/悬停/拖拽/滚轮 + 热区表）
  Native/UiInputGuard.cs              输入守卫（层级停靠 + 射线压制 + 世界点击拦截 + 组件级交互锁）
  Native/UiEscapeGuard.cs             ESC 守卫（阻止游戏暂停菜单）
  Native/UiCursorOverlay.cs           自绘指针（兜底；正常情况用游戏虚拟光标）

  Pages/DemoStarter.cs                内置页面与原生菜单入口注册（含主页）
  Pages/DemoPages.cs                  组件总览 / 动态布局 / 列表 / 原生观感 / 原生注入 / 关于
  Pages/DemoProvider.cs               第三方声明式契约示例（子页导航/控件/回写）
```

---

## 三、动态布局引擎（`Layout/`）

**为什么自研而不直接堆 UGUI 布局组**：`LayoutGroup` + `ContentSizeFitter` 的死循环/重排时序在 IL2CPP 下不可控，
且我们最常见的场景是"**文本换行后的高度未知**"（TMP 的 `preferredHeight` 只有在宽度定下来后才有意义）。
所以本库采用**显式测量 → 排列**（measure → arrange），完全可预测、可日志取证：

- `IUiElement`：`Rect` + `MeasureH(width)` + `MeasureW(height)`；叶子组件（文本/按钮/输入框）自己实现，
  容器（`UiFlow`）递归汇总；
- `UiSize`：`Fixed(v)` / `Auto`（内容决定）/ `Grow(weight)`（分享剩余）/ `Percent(p)`；
- `UiFlow.Apply()`：
  1. **测量**：主轴逐子项取 `MeasureH/MeasureW`，交叉轴按容器内宽（含 Padding）测量；`Auto` 用测量值，`Fixed` 用给定值；
  2. **分配**：`Grow` 均分（或按权重分配）主轴剩余；无剩余时按最小高度压缩；
  3. **排列**：写 `anchoredPosition` / `sizeDelta`（左上锚点，`y` 向下为正），并让容器自身高度 = 内容总高（`AutoHeight`）；
- **重排触发**：`Apply()` 显式调用（构建后 / 语言切换 / 内容变化 / 窗口尺寸变化），不做每帧布局（避免帧开销）。
- **与 UGUI 布局组的关系**：组件不依赖布局组；`UiList` 的滚动用 `ScrollRect` + `RectMask2D`（只有滚动需要它），
  滚动条的拖拽/滚轮走**自管指针路由**（游戏 `EventSystem` 在任务场景常为未激活，见 `docs/MOD_MENU.md` §八）。

**降级策略**：`UiFlow` 的测量/排列全包在 `try/catch` 里，测量失败退化为 `Rect.sizeDelta`（绝不抛出）；
验证动态布局用自带的「动态布局」页（增删行/改文字长度后观察重排），比另做一个探针类更直接。

---

## 四、基础组件集（`Widgets/`）

统一的组件契约：**构造在 `UiFlow` 里声明尺寸（`Fixed/Auto/Grow`），组件自己负责视觉与交互，交互走 `UiPointerRouter` 热区**。

| 组件 | 说明 | 关键点 |
|---|---|---|
| `UiWindow` | 窗口：标题栏 + 关闭 + 内容区 + 淡入/缩放过渡 | 内容区是 `UiFlow`，高度自适应 |
| `UiPanel` | 面板/卡片底板 | 有原生 sprite 时 9-slice，否则纯色 + 边框 |
| `UiLabel` | 文本 | 语义字号（Title/Subtitle/Body/Note）+ 自动换行 + `Auto` 高度 |
| `UiButton` | 按钮 | 4 种风格 + 悬停/按压/禁用；文字自动本地化字体 |
| `UiToggle` | 开关 | 行样式（标签 + 开关），值变化回调 |
| `UiSlider` | 滑条 | 拖动由自管指针驱动；显示数值；**把手用锚点比例定位**（不依赖布局时序） |
| `UiStepper` | 步进器 | `-` / 值 / `+`，支持 step/范围/格式化 |
| `UiChoice` | 循环选择 | `‹ 值 ›`，`readonly` 可当只读展示 |
| `UiTextInput` | 输入框 | **自绘文本 + 光标 + 密码掩码**；键盘/输入法走 `UiTextRouter`（物理键 + 隐藏 IME 锚点 + 原生 Win32 IME + `OnTextInput` patch，见 §8.2） |
| `UiKeyBind` | 快捷键 | 点击进入"等待按键"态，`Keyboard.current` 轮询捕获 |
| `UiSeparator` | 分隔线 | 优先原生 `UI Box` 细线（Tiled），否则 1px 纯色 |
| `UiProgress` | 进度条 | 背景 + 前景 + 百分比文本 |
| `UiTabs` | 页签栏 | 当前页签高亮 + 切换回调 |
| `UiRow` | 行 | `标签 + 控件 + 说明`（设置页/列表页统一模型） |
| `UiList` | 列表 | **行对象池**（不重建）+ 视口裁剪 + 自管滚轮/拖拽 + **可见滚动条**（内容不超出时自动隐藏；轨道可点可拖） |
| `UiModal` | 模态框 | 遮罩 + 标题/正文/按钮组，返回值回调 |
| `UiTooltip` | 悬浮提示 | 跟随指针，靠指针路由的 hover 事件 |

> 组件视觉全部走 `Theme/UiTheme`（调色板/尺寸/字号集中一处），换原生观感只动 `UiSkin` + `UiTheme`。

---

## 五、菜单系统与子菜单（`Menu/`）

### 5.1 页面栈（"表现一致即可"的实现）

用户要求"在原生主菜单中点击跳转到另一个子菜单，**不一定非要这么实现，表现一致即可**"。本库采用**同一窗口内的页面栈**：

- `UiPageStack`：`Push(page)` / `Pop()` / `Replace(page)` / `Home()`；栈深 > 1 时显示**面包屑**
  （`主页 › 子页 › 孙页`，每段可点）+ **返回**按钮；`ESC` = 返回上一级（栈底时 = 关闭菜单）。
- **视觉一致**：切换页面时窗口**不重建**，只换内容区（旧页面 `SetActive(false)` 保留在池里），配 `UiTween` 的快速淡入，
  观感等同"跳到另一个菜单"。
- **原生菜单入口**：原生菜单按钮点击 → 打开本菜单并直达指定页（`NativeMenuBridge`），关闭即回到原生菜单（覆盖式，不破坏原生菜单树）。

### 5.2 菜单窗口

`UiMenuWindow` 负责：画布（`sortingOrder = 32766`，抄 `CoopUIManager`/官方联机的权威做法）、窗口底板、
标题栏（标题 + 面包屑 + 返回 + 关闭）、内容区、底栏（状态/操作反馈）、开关编排：

`Open()` → `UiInputGuard.Apply` + `UiEscapeGuard.Block(true)` + 光标覆盖判定；
`Close()` → 全部还原（先还原守卫再隐藏画布，顺序与 `ModMenuUI` 一致）。
**内容区两个真因（2026-09-13 修，很重要——它们叠加会让“窗口开着但内容看不见”）**：

| 位置 | 错法 | 现象 | 正确做法 |
|---|---|---|---|
| `UiMenuWindow` 建 `pageHost` | 锚点用 `(0,1)-(1,1)`（顶对齐），却把 `offsetMin/offsetMax` 当“左/下/右/上内缩”写 | 高度 = `offsetMax.y-offsetMin.y` = **-52**（负高） | 锚点必须**四边拉伸** `(0,0)-(1,1)`，再给内缩 offset |
| `UiList.Create(parent, 0f)` | 只把尺寸写成 `300×h`，**没铺满宿主** | 视口 `300×0`（宽度回退到内部常量 300），`RectMask2D` 把内容整片裁掉 | `height <= 0` 时 `UiStretch.Fill(rt)` 铺满宿主；`height > 0` 用“横向拉伸 + 固定高” |

两者都存在时，`rects` 里能看到 `pageHost rect=1144x-52`（`chain` 命令从上往下打印祖先链的锚点/偏移，一步定位）。
另外 `UiList` 新增 `TickAll()`：页面常在窗口尺寸就绪**之前**构建，首轮量不到宽度时不落定错误布局，下一帧补算；
窗口尺寸变化（`winsize`）也靠它自动重排。

**页面互斥显隐（2026-09-13 修，很重要）**：`UiSimplePage.Build` 只把 `parent` 转给构建器，自己**不设 `Root`** →
`IsBuilt` 永远 false、`SetVisible` 无效。后果：① 每次显示都**再建一份**（同一个宿主里越堆越多）；
② 旧页全部留在屏幕上（现象=`垂直方向挤在一起`/看到别的页的控件）。修法是 `UiPage.AttachRoot(host)`：
`Build` 后若页面没设 `Root`，就把 `pageHost` 当作页面根 → 不重建 + 切页只做 `SetActive` 显隐。

**字体符号（2026-09-13）**：游戏 TMP 字库**没有 `✕`(U+2715)**——实测在关闭按钮上渲染成“方块”。
界面文案一律用字库一定有的 ASCII：`✕→X`、`‹→<`、`›→>`、`→→->`；分隔点 `·`(U+00B7) 实测可渲染，保留。

### 5.3 第三方声明式页面

第三方（无 Unity 依赖）实现 `IUiKitProvider` → `BuildMenu(IUiMenuTree)` 描述页面与行；
宿主用 `Menu/UiDeclarativePages.cs` 把它渲染成真实页面（`UiRow` 列表），导航项（`Nav`）在页面栈里 `Push`。
**软依赖**：未安装 `OpenNestUIKit` 时 `UiKitHost.Register` 只进内存注册表（`IsHostAvailable == false`），不影响第三方模组加载。

**宿主替第三方做的事情**（2026-09-13 补齐，第三方只声明菜单即可）：

| 能力 | 契约 | 说明 |
|---|---|---|
| 原生 ESC 入口 | 自动（可选 `IUiKitNativeEntry` 定制） | 每个 provider 自动在原生 ESC 列表里占一行：标题= `DisplayName`、页面= 它的根页；不要就去实现 `IUiKitNativeEntry.ShowInNativeMenu => false`（示例/测试 provider 就这么做 —— 原生列表已排满）。provider 注销时对应行会被移除 |
| 开/关本库菜单 | `UiKitHost.OpenMenu(pageId)` / `CloseMenu()` / `ToggleMenu(pageId)` | 宿主启动时挂实现；第三方**不必引用宿主程序集**（避免硬依赖） |
| **原地刷新当前页** | `UiKitHost.Refresh()` | 大厅列表 / 成员 / 聊天 / 模组清单这类“会变的内容”在变化后调一次；宿主重新跑 `BuildMenu` 并重建当前页（页面栈/位置不变） |
| 当前页 / 是否在打字 | `UiKitHost.CurrentPageId` / `IsTextInputFocused` | 自动刷新前先看这两个：只在“我这一页正被看、且用户没在打字”时才刷（否则会冲掉输入草稿） |
| 页签栏（一行多列） | `UiPageDef.Tabs(key, tabs, index, onChanged)` | 把“原来一个面板里切页签”的布局搬过来（Coop 的 Steam/局域网、ModMenu 的详情/设置/诊断）；渲染成真页签（选中高亮 + 底部指示条） |
| **两栏容器** | `UiPageDef.Columns(leftWidth, left, right, gap)` | 左栏固定宽 + 右栏占剩余（两栏各自纵向流，高度取较大者）——把原模组“左列表 + 右详情”的布局搬回来（ModMenu 页） |
| **页面首选窗口尺寸** | `UiPageDef.Size(w, h)` | 不同页可以有不同窗口大小（ModMenu 1180×700 双栏；Coop 520 宽紧凑单栏）；0 = 用默认 |
| **紧凑密度** | `UiPageDef.SetCompact()` | 行高 36→26、内边距 18→12、字号降 1~2 档（`UiTheme.PushDensity/PopDensity`，**构建期**生效）——原模组窄面板的观感 |
| **内嵌可滚动列表** | `UiPageDef.List(key, height, build)` | 行数不确定的区域（模组列表 / 房间列表）用它：列表自己有滚动条与滚轮/拖拽，不会把整页撑长 |
| **行控件（开关/滑条/下拉/输入框）** | `UiPageDef.Toggle/Slider/Choice/Text/KeyBind(key, label, …, onChanged)` | `Text` / `KeyBind` 是**可编辑输入框**（§8.2 输入管线，支持中文）；**值为空时就显示一个空文本框**（不是灰字只读行、也不画占位符）；热区名 `toggle:/slider:/choice:/input:<key>` |
| **悬浮聊天层** | `UiKitHost.SetChat(lines, onSend, title, hint)` / `ClearChat()` / `FocusChat()` / `CloseChat()` / `IsChatTyping` | 宿主渲染一个**独立画布**上的浮窗（左中）：收起时只看最近几条（半透明穿透）、**回车唤入**输入、回车发送、ESC 收起（见 §十三）|
| 菜单是否打开 | `UiKitHost.IsMenuOpen` / `CanControlMenu` | 同上 |
| “环境里有没有 UIKit” | `OpenNestUIKit.UiKitMarker`（宿主程序集） | 第三方可**纯反射**探测（`Type.GetType("OpenNestUIKit.UiKitMarker, OpenNestUIKit")`）；真正能否接上以 `UiKitHost.IsHostAvailable` 为准 |

参考实现：`OpenNestCoop.UI.UIKitIntegration` / `OpenNestModMenu.UI.UIKitIntegration`（反射探测 + `[MethodImpl(NoInlining)]` 隔离，
缺 UIKit 时回退各自自带界面；另见 `docs/MOD_MENU.md` 更新记录、`docs/NATIVE_UI.md` 第七节）。

---

## 六、原生菜单注入（`Native/`，多级页面项）

**权威时机**（`docs/NATIVE_UI.md` §7 + `MainMenuEntry.cs` 实测）：Harmony postfix 挂
`MainMenuStateRelay.HandleMainMenuLoaded`；也等价于订阅 `MissionManager.MainMenuLoaded`。

`NativeMenuBridge` 提供注册模型（多级）：

```csharp
NativeMenuBridge.Add(new NativeMenuEntry {
    Title    = "模组 UI 库",          // 显示文案（走 UiKitLoc）
    PageId   = "home",                // 点击后在本菜单里打开哪个页面
    Parent   = null,                  // null = 顶层；否则挂到某个已注册项下（多级）
    InsertAfter = "OpenSettingsBtn",  // 插入位置：某个原生按钮名之后（默认：设置按钮之后）
    Order    = 0,                     // 同位置的排序
});
```

注入实现要点（**2026-09-13 定稿策略**，与早期“slot 让位”不同）：
1. **ESC 菜单多实例**：主菜单与游戏内暂停菜单是**两个** `ESC Menu Buttons` → 全场景 `FindObjectsOfType` 遍历所有实例；
2. **只注入“要在原生列表里占一行”的顶层条目**（`NativeMenuEntry.ShowInNative == true`）；
   其余条目（含子页）只在**我们的原生页**里展开 —— 面板只有 400 高，多插一行就把原生列表挤爆；
3. **样式 = 原生**（用户要求：“注入的用原生的”）：自建按钮 + `NativeMenuStyler` 抄设置按钮（底图/字体/内边距/字号固定 20 + 关 autoSizing），
   **不克隆**原生按钮（克隆会带原生链接脚本/`onClick` 持久绑定）；
4. **摆位：2 列 × 最多 3 行插到锚点之后 + 按原生节距结构重排**（2026-09-13 **v4**，`NativeMenuInjector`）：
   - **格子规模 = 2 列 × 最多 3 行**（用户：“多列宽度压缩又导致显示不全了……最多应该同时显示 5 个注入按钮 + 1 个原生 Settings，三行，
     最后一个如果超过 5 个变为 More...”）：格子宽 `(250-8)/2 = 121px`、字号 18、单行省略 —— 中文标签（如「联机菜单」）放得下；
     **最多 5 个我们的格子（含「更多...」）** = 2+2+1 三行；原生 **Settings 那一行不动尺寸**，作为“+1”计入同屏容量。
   - **压缩把上面的 Settings 一起算**：我们的块现在是多行，块内额外行高（`(行数-1)×40`）**必须计入可用高度**，
     否则“算出放得下、实际末行掉出面板底”（用户报的“又导致显示不全了”就是它）。
     压缩顺序不变：① 块间 > 40 先压到 40；② 等比压，**节距下限 30**；③ 保底继续压并告警。
   - 实测（原生 8 行 + 我们 3 项）：格子 121×38、字号 18，pos y=42/2（行 0 两格 x=±65，行 1 居中 x=0）；
     `menutree` 可见 `★我们 … 121x38 字号=18 底图=UI Box Castile@2.5`。
   ⚠ 旧做法：v3 是“一行 3 列”（格子 78px，中文被省略号截断）；v0 是“按估计节距把原生行拉开 + 整体等比压缩”（注入 3 行时压到 0.66 倍，字会穿出格子）；
   v1 是“原生行一行不动、我们的块接在列表末尾”（位置不对）；v2 是“3 行竖排插在设置后面”（只压间隔，观感差）。
   v3 靠“一行多列”把纵向成本从 3 行降到 1 行，**从根上不用压缩**。
6. 容器可能 `inactive`（主菜单加载时未打开）→ **不要**替游戏 `SetActive`（会搞乱它的“剪贴板开着/关着”状态），只读；
7. 注入幂等（`_injected` + 条目签名），条目变化时重建自己的行。

### 6.1 实机现状（2026-09-13 快照，`menutree` / `injectdiag` 命令）

游戏里有**两个** `ESC Menu Buttons` 容器（内容相同、各自独立）；容器子节点含原生行 + **本库代注入的行**
（Coop / ModMenu 自 2026-09-13 起不再自己注入，见 §5.3 与更新记录八）：

| 序 | 名字 | 尺寸 | y | 字号 | 底图@ppuMul | 备注 |
|---|---|---|---|---|---|---|
| 00-01 | `iron nest jumping` / `(1)` | 55x55 | 171 | – | `iron nest jumping icon@2.5` | 装饰图标 |
| 02 | `Text (TMP)` | 81x21 | 174 | 20 | – | 愿望单小字 |
| 03 | `Wishlist Game` | 250x38 | 176 | 20 | `UI Box Double line@3` | **inactive 但仍占 slot** |
| 04 | `Return to Game Btn` | 250x38 | 138 | 25 | `UI Box Castile@2.5` | |
| 05 | `OpenSettingsBtn` | 250x38 | 106 | 25 | `UI Box Castile@2.5` | |
| 06 | `Feedback` | 250x38 | 41 | 20 | `UI Box line@4` | |
| 07 | `Report BUG` | 250x38 | 8 | 20 | `UI Box line@4` | |
| 08 | `Report Localization Error` | 250x38 | -24 | 14.85 | `UI Box line@4` | |
| 09 | `Save Mission` | 250x38 | -57 | 20 | `UI Box line@4` | |
| 10 | `Return to Main Menu` | 250x38 | -89 | 21.55 | `UI Box Castile@2.5` | |
| 11 | `Quit Game Btn` | 250x38 | -122 | 25 | `UI Box Castile@2.5` | |
| 12 | `BuildVersion` | 327x17 | -155 | 16.5 | – | 左下角版本号 |
| 13 | **`OpenNestUIKit_uikit_home`（我们·格子 1/3）** | 78x38 | 53（x=-86） | 16 | `UI Box Castile@2.5` | 插在设置按钮**之后**、**同一行** |
| 14 | **`..._provider_open_nest_coop`（格子 2/3）** | 78x38 | 53（x=0） | 16 | `UI Box Castile@2.5` | Coop 入口（由本库注入） |
| 15 | **`..._provider_open_nest_mod_menu`（格子 3/3）** | 78x38 | 53（x=86） | 16 | `UI Box Castile@2.5` | ModMenu 入口（由本库注入） |

**验证方法**：`menutree` 前后跑一次 `uninject`（移除我们的行）对比 —— 原生行的 y **完全一致**（本页数据即如此），
说明我们的注入**没有挪动任何原生行**；我们的三行落在面板内（末行中心 -173、底边 -192 = 面板底，刚好放下）。
注：`-93/-133/-173` 是“接在原生末行下方 + 行距 40”算出来的（`native.log` 里那行
`注入 3 项（原生样式，接在原生末行下方；**原生行一行未动**）` 就是这条路径的取证）。

⚠ 注意点：**同仓库的 Coop 模组原来也往同一容器注入**（`OpenNest_CoopEsc`，独立实现 + 自己重排列表）。
2026-09-13 起 Coop / ModMenu 都改为**由本库代注入**（它们实现 `IUiKitProvider`，宿主自动给它们各加一行）——
`menutree` 里现在能看到三行我们托管的行：`OpenNestUIKit_uikit_home` / `..._provider_open_nest_coop` /
`..._provider_open_nest_mod_menu`（都 250x38 / 字号 20 / `UI Box Castile@2.5`，原生行一行未动），
而 Coop 自己注入的 `OpenNest_CoopEsc` 已消失（它检测到宿主就绪后主动跳过）。
（示例 provider / 测试 provider 用 `IUiKitNativeEntry.ShowInNativeMenu=false` 主动不占格位 —— 原生列表本来就挤。）

---

## 七、原生观感（`Theme/`）

### 7.1 字体与过渡

- **字体**：`UiKit.EnsureFont` → `NativeUi.GetLocalisedFont`（游戏本地化字体）+ 运行时扫描兜底（默认字体无中文字形）；
  ⚠️ MLL 下 `LocalisationManager.GetFont` 缺失（`MissingMethodException` 在 trampoline 抛出、managed catch 捕不到）→ 桥接里 `#if !MELONLOADER` 排除。
- **过渡**：`UiKitTween`（打开淡入 0.12s + 缩放 0.96→1；关闭淡出），由 `UiKitBehaviour.Update` 推进，不用 Unity 协程。

### 7.2 按钮/面板素材（九宫格）——**必须按尺寸选**（2026-09-12 实测修正）

✅ **是的，用的是九宫格（`Image.Type.Sliced`）**，不是平铺（`Tiled`）。但旧版有两个真实问题，已修：

1. **九宫格"放不下"会被整体压缩 → 看起来像图案被压实/平铺**：
   九宫格生效前提是 `sprite.border` 合计 + 留白 ≤ 控件尺寸。`UI Box Castile` 纵向边框 `top+bottom = 34+16 = 50px`，
   而按钮只有 24~32px 高 → Unity 的 `Image.GetAdjustedBorders` 按最小比例把**整张 128px 图案**压进按钮
   （城堡/基座装饰被压成一团）。
2. **四种子风格共用同一张图**（只差底色）→ 看起来"多颗按钮只有一种样式"。

**修法**（`UiSkin.ButtonSpriteFor(primary, danger, w, h)` + `UiSkin.PickForSize`）：

| 步骤 | 规则 |
|---|---|
| ① 候选按风格分开 | 次按钮：`line → Double line → Castile`；主按钮：`Castile → Double line → line`；危险：`partial star → Boxed Corners → line`；幽灵：无素材 |
| ② 按尺寸筛选 | 取第一个满足 `borderX + 8 ≤ w && borderY + 8 ≤ h` 的（原样使用） |
| ③ 都放不下 | 把第一候选的 `border ÷ scale` **烤入副本**（`NativeGetBorderScaled`；⚠️ 不用 `pixelsPerUnitMultiplier`，本游戏 IL2CPP 下改值无效） |
| ④ 仍不达标 | 返回 null（纯色）——绝不硬塞图案 |
| ⑤ 取证 | 每颗按钮均打印 `按钮素材 = '<名>'（原样/烤入缩放；border=…；目标 WxH）` |

实测（G 端，目标尺寸 → 选中素材，取自 `OpenNestUIKitLogs/uikit.log`）：

| 目标 | 选中 | border |
|---|---|---|
| Secondary 24/28/32、窄按钮 64/96/140/220 × 32 | `UI Box line#2`（÷2） | 7,7,7,6 |
| Secondary 40/56 | `UI Box line`（原样） | 14,14,14,13 |
| Primary 24 / 28 / 32 | `UI Box Castile#4` / `#3` | 4,8,4,4 / 6,11,6,5 |
| Primary 40/56 | `UI Box Double line`（原样） | 13,14,13,13 |
| Danger 小 / 大 | `UI box partial star#2` / 原样 | 9,5,3,9 / 18,11,7,18 |

面板/窗口（1180×720 等大尺寸）直接用 `UI Box Castile` 原样（放得下）。
素材取用走"**填充层（纯色）+ 装饰层（sprite）**"双层：`UI Box Castile` 中心透明靠底层色兜住；
`SGRounded` 这类**不透明底板**靠装饰层按面板色着色（不洗白）。

⚠️ 已知小瑕：`Ghost` 风格无素材（有意为之，纯文字按钮），因此**不产生** `按钮素材 =` 日志行。

### 7.3 人工切片优先于自动规则（2026-09-12 追加）
上表 ①~④ 是**没有人工切片定义时**的兜底。一旦某张图在切片定义文件里有条目，
`PickForSize` 的**第 0 步**就直接采用它（`mode=none` 则显式退回纯色），不再走到 ②③④；
`border` 放不下也**不阻止**采用，只在日志里附 `⚠ border 放不下会被压缩`。
切片定义 = 人的审美判断，优先于工具的启发式；定义文件位置/格式、热重载、离线 GUI/CLI 工具
（`tools/slice_tool.py`）与实机取证见 **`docs/UI_KIT_SLICE.md`**。

**用途标记（tag）优先于风格候选**：定义里可给每张图标 `panel` / `button.primary` / `dialog` 等，
`PanelRef/LineRef/DialogRef/ButtonRefFor` 会先查 tag（`UiSkin.RefForTagOr`），再回原有候选名。
标记是**首选不是霸占**：只在该尺寸放得下时采用（border 原值），放不下则退回后续候选；
全都不放得下才仍用第一个有定义的（日志标会被压缩）。

**本模组不侵入游戏原生 UI**：切片只作用于我们自建的 `Image`；改切片走 `Sprite.Create` 造新副本，
游戏原 `Sprite` 的 `border` 不会被改，也不会被换成我们的副本（测试模组 `nativecheck` 可实机取证，见 `docs/UI_KIT_TEST.md` §4.2）。

### 7.4 原生菜单组件普查（2026-09-13 实测，`nativemenu` 命令）

用于“把我们的主题对齐到原生”的基准数据（G 端主菜单场景，屏幕 1920x1080，**所有画布 scaleFactor=1**）：

| 画布 | 顺序 | Scaler | 组件（Image/Button/文字） | 备注 |
|---|---|---|---|---|
| `Cursor Canvas` | 32767 | ConstantPixelSize | 0/0/0 | 游戏自绘光标 |
| `Canvas`（主菜单） | 20 | ScaleWithScreenSize 1920x1080 **Expand/1** | 10（sprite 3）/0/5 | **主菜单一个 `Button` 组件都没有**（自定义点击处理） |
| `Canvas` | 1 | 1920x1080 **Match/1** | 12（8）/0/13 | 主菜单装饰层（`SGRounded @4.74`） |
| `feedback form Canvas`（×2） | 1 | 1920x1080 Match/1 | 15（12）/5/9 | `UI Box Castile @2.79`、`Double line @3`、`line @3` |
| `Screenspace Popup Confirmation` | 9999 | 1920x1080 Match/1 | 9（8）/2/5 | `Boxed Corners @1 / @2`、`Double line @2 / @4`、`UI_Line Star Center @0.53` |
| `Credits` | 9999 | 1920x1080 Match/1 | 9（5）/0/6 | 有 `ScrollRect` |
| `ESC Menu Buttons` 容器 | — | — | 11 个 `Button` | 按钮 **250x38**、字号 **20/25**、背景 `UI Box Castile @2.5` |
| `OpenNestCoop_UI`（同仓库另一个模组） | 32765 | 1920x1080 Match/0.5 | 18（**sprite 0**）/13/28 | 全是纯色，无 9-slice；字号 14/15 |
| `OpenNestUIKit_UI`（我们） | 32766 | 1920x1080 Match/**0.5** | — | 我们不使用 Expand/Match=1 |

**字号分布**（原生）：主菜单/标题 39~50/50.85/48、弹框 45/60、反馈表单 72/50/20、菜单按钮 20/25、
其他小字 28/30/32/34/35.8/36/18。→ 我们的 `FontTitle 22 / FontBody 15 / FontSmall 12` **偏小**（原生按钮 20）。

**`pixelsPerUnitMultiplier` 是“每张素材/每个用途手调”的（不是固定值）**，实测分布：

| 素材 | 元素尺寸 | ppuMul | 素材原 border | 渲染后边框 ≈ |
|---|---|---|---|---|
| `UI Box Castile` | 250x38 / 178x40 / 186x64 | 2.5 | 18/34/18/16 | 7 / 13.6 / 7 / 6.4 |
| `UI Box Castile` | 反馈表弹框 | 2.79 | 同上 | 6.5 / 12 |
| `UI Box Double line` | 弹框 | 2 / 3 / 4 | 13/14/13/13 | 6.5 / 4.7 / 3.3 |
| `UI Box Boxed Corners` | 弹框 | 1 / 2 | 26/26/26/25 | 26 / 13 |
| `SGRounded` | tooltip 380x50 | 4.74 | 48 | ~10 |
| `SGRounded` | 753x50/753x70 | 5 | 48 | ~9.6 |
| `UI_Line Star Center` | 分隔线 | 0.53（<1 = 变粗） | — | — |

**结论（用于搁置项的下一步）**：原生是**按用途把“渲染后的边框”控制在 ≈7~14px**（小件偏小、大件偏大），
而不是全局一个倍率；其余无关素材用 1。→ 我们应当**按元素尺寸/用途选 ppuMul**，或把它做成切片定义字段。

### 7.5 宿主几何与颜色真值（2026-09-13 实测，`pagespec` 命令）

#### 宿主链（把 `Settings` 页塞进剪贴板的方式）

| 层 | rect / sizeDelta | 缩放 | 说明 |
|---|---|---|---|
| `Canvas` | **300×400** | `lossyScale=0.001` | WorldSpace 画布（物理剪贴板面板）；同层还有 `ESC Menu Buttons`、`Main Menu Cover` |
| `Settings menu` | 100×100 | 0.001 | 页容器（三个同级“页”都挂在这里） |
| `Settings` | **871.7×1012.5** | 未显示时 `localScale=0`；**显示时 ≈0.3591** | 整页；1012.5×0.3591 ≈ 364 ≈ 400 − 标题位（**按高度适配**）|

⇒ **原生控件尺寸都是“本地值”，屏上要乘 ~0.3591**（见更新记录二十四）。控件按本地值建 + 容器 `localScale` 是唯一能全等的方式。

#### 颜色：一定看**渲染色**，不要只看 `Image.color`

`Selectable` 的 `ColorTint` 是把颜色设在 **`CanvasRenderer`** 上的，`Image.color` 常是白的：

| 控件 | 底图 | `Image.color` | **渲染色** | 过渡色（normal/highlight） |
|---|---|---|---|---|
| ESC 行 / 主·次按钮 | `UI Box Castile` / `UI box partial star` | (1,1,1,1) | **(0,0,0,1) 黑** | (0,0,0,1) / (0.453,0.453,0.453,1) |
| 输入框 `InputField (TMP)` | `SUGGradientRounded` ppuMul 7 | (0,0,0,0.902) | (1,1,1,1) | (1,1,1,1) / (0.961…) |
| 下拉 `Bg` | `SUGGradientRounded` ppuMul 7 | (0,0,0,0.902) | (1,1,1,1) | 同上 |
| 选择器 `ValueBg` | `SUGGradientRounded` ppuMul 5 | (0,0,0,1) | (1,1,1,1) | 同上 |
| 检查框 `Toggle` | `SUGGradientRounded` ppuMul 7 | (0,0,0,1) | (1,1,1,1) | 同上 |
| 拖拽条轨道 `Background` | `SGRounded` ppuMul 7 | (0,0,0,0.471) | (1,1,1,1) | (1,1,1,1) |
| 拖拽条手柄 `Bg` | `SGRounded` **Simple** | (0.545,0.565,0.604,1) | (1,1,1,1) | — |
| 滚动条 `Handle` | `SGRounded` ppuMul 7 | (0.545,0.565,0.604,1) | (1,1,1,1) | (0.388,0.282,0.176,1) 褐 / (0.698,0.604,0.51,1) |
| 选项卡 `TabsCtn` | `SUGGradientRounded_Top` ppuMul 4 | (0.014,0.039,0.066,0.961) | (1,1,1,1) | — |
| 选项卡选中 `Active` | **无 sprite** | (0,0,0,0.8) | (1,1,1,1) | — |
| 阴影层 `SUGShadowLite` | 白软光晕图（中心 A=0、最大值 A≈23，其实很淡） | (1,1,1,1) | — | 按钮上的那层原生 **`enabled=false`**（别画） |

#### ⚠ 素材是**空心**的 —— 不要“深底配白字”

实测（`ref/ui_sprites_all` 的 alpha）：

| 素材 | 中心 alpha | 含义 |
|---|---|---|
| `UI Box Castile` | **0**（整条中线均 0） | 只有描边/装饰 → 兑黑渲染色后是**黑边框 + 纸面**，字要**黑** |
| `UI box partial star` | **0** | 同上 |
| `SUGGradientRounded` / `_Top` | **41**（16%） | 深色描边 + 极淡填充，字仍然要**黑** |
| `SGRounded` | **255** | 真·实心（拖拽条轨道/滚动条槽） |
| `SUGShadowLite` | 0（最大 23） | 很淡的白光晕 |

⇒ 原生就是“**浅底 + 黑字**”（实测：下拉 `ButtonLabel`/`Arrow` = (0,0,0,1)、输入框文字 (0.255,0.255,0.255,1)）。
组件的“深色”感来自**描边**，不是底。此外：因为框是空心的，**聚焦高亮不能改 `Image.color`**（会把框染成实心黑框），
要改就改渲染色（乘在描边上）。

行内文字锚点（本地，随页一起缩）：行标签锚 `0,0~0.95,1`、`sizeDelta(-10,-10)`、字号 **32**；
按钮文字 `sizeDelta(-40,-20)`、字号 **30**、`Midline`；下拉标签 28、输入框文字 `JMH Typewriter` 26 右对齐；
右半栏（输入/键位/选择器）锚 `0.51,0~1,1`；拖拽条左标签锚 `0,0~0.51,1`、轨道锚 `0.51,0~1,1` 再内缩 71.1 留数值位。

---

## 八、输入与层级（复用 `OpenNestModMenu` 已验证的五层方案）

> **性能（2026-09-13 实测，`perf` 命令）**：菜单打开空闲时本模组每帧 `Update 平均 0.09ms / 最大 0.19ms`、`LateUpdate 平均 0.01ms`；
> 开菜单那一帧 `Apply` 仅 **0.08~0.14ms**（重活只在**场景指纹变化**时做）。

**每层在干什么（2026-09-13 重构后的定稿）**：

| 层 | 做什么 | 为什么这么做 |
|---|---|---|
| 自管指针 `UiPointerRouter` | 自己的控件命中/点击/拖拽/滚轮 | 游戏 `EventSystem` 常为未激活；**菜单关闭时 `Tick` 立即返回**（正常玩法零开销） |
| **停用游戏输入模块** | `EventSystem` 上 1~2 个 `BaseInputModule.enabled=false` | “菜单打开期间游戏不该收输入”的**语义正确**做法；比逐个压制 36~59 个射线器短得多（旧做法还要周期重做，帧尖峰+GC） |
| **世界点击拦截** | Harmony prefix 挂 `LookAtTarget.OnClickDown` 返回 false | 游戏所有交互点击的**唯一入口**，成本≈一个 bool |
| 交互锁（**默认关**） | 禁用场景交互组件 | 会改 100+ 个游戏组件状态，而后点击已被上一层拦住；需要“连悬停高亮一起锁”时才开（`lock:on`） |
| 玩家冻结 | `FirstPersonController.SetFrozen(true)` | 1 次调用、可逆 |
| 层级停靠 | 本模组画布 `32766`，其它非光标画布降到之下 | 保证我们的 `Blocker` 终是命中最近的一层（**从不改光标画布**） |

**我们注入到原生面板里的行也必须走自管指针**：剪贴板画布是 `WorldSpace`（实测 `renderMode=WorldSpace`、`worldCamera=null`），
靠游戏 EventSystem 收点击在实测里**根本不响应**（用户反馈“点 Close/X 没减少层级”）。所以 `NativeMenuPage` 把每行注册为热区，
并在显示/关闭时 `UiPointerRouter.Activate/Deactivate`；路由器的命中测试也修正为**按热区所在画布的相机**（Overlay→null，WorldSpace→`worldCamera ?? Camera.main`）。

### 8.1 ESC 层级模型（`UiEscapeLevels`，2026-09-13 定稿）

**为什么改**：旧实现只有一个裸 bool（`UiEscapeGuard._blocked`），打开/关闭由各处**各自**调用 `Block(true/false)`。
只要有一条关闭路径漏调（窗口被销毁 / 剪贴板被游戏关掉 / 场景切换），这个 bool 就**永远停在 true** ——
表现就是用户说的“**我们的界面早就关了，可游戏 ESC 菜单再也调不出来**”。

**现在的规则（唯一真源 = `Native/UiEscapeLevels.cs`）**：

| 事件 | 层数 | ESC 归谁 |
|---|---|---|
| 什么都没开（**无层级**） | 0 | **游戏**：ESC 菜单该弹就弹（守卫不得占用，每帧自愈） |
| 我们的窗口开着（`Push("window")`） | 1 | 我们：ESC = 退页/关窗 |
| 原生次级菜单页开着（`Push("native-page")`） | 1 | 我们：ESC = 退一级（顶层→回原生列表） |
| 两者同时开 | 2 | 我们：逐层消费 |

- `Push`/`Pop` **幂等**（同一 owner 只算一层）；层数变化通过 `Changed` **立即**同步守卫（不用等下一帧）；
- **层数从 >0 → 0**：立即放行；若这一下正是 ESC 触发的 → `ReleaseWhenKeyUp()` 等按键抬起再放行
  （否则同一次按键会被游戏收到，窗口刚关、暂停菜单马上弹）；
- **自愈**：`UiEscapeGuard.Tick()` 每帧检查“层数 = 0 但仍在阻止” → 直接放行并告警；
- **绝不主动调出**游戏 ESC 菜单：`UiEscapeGuard.Apply()` 只在**有层级**时干活（无层级时调用会被忽略并告警）。

**实机取证**（G 端主菜单，测试模组 `escprobe`，日志 `OpenNestUIKitLogs\test.log`）：

| 操作 | `escprobe` |
|---|---|
| `open:list` | `★层级=1[window]` blocked=True |
| `close` | `★层级=0[]` blocked=False |
| `pageopen` | `★层级=1[native-page]` blocked=True |
| `pageclose` | `★层级=0[]` blocked=False |

1. **自管指针命中**（`UiPointerRouter`）：`Mouse.current.position` + `RectTransformUtility.RectangleContainsScreenPoint`
   + 热区表 + 按帧去重（`ClickOnce`）——因为任务场景里游戏自己的 `EventSystem` 常为**未激活**，UGUI 事件收不到点击；
   **菜单关闭时 `Tick` 立即返回**（正常玩法下零开销）；
2. **压制外部 raycaster**：菜单打开时把所有非本模组画布的 `GraphicRaycaster.enabled = false`，关闭时原样恢复；
   只在**开菜单 / 换场景**时做（不周期重做）；
3. **Harmony 拦世界点击**：prefix 挂 `LookAtTarget.OnClickDown` 返回 false（反射拿 Harmony，双端通用；MLL 端类型名带 `Il2Cpp` 前缀）——**这是防穿透的主力**（游戏所有交互点击的唯入口）；
4. **组件级交互锁**（辅助层，路径无关）：按类型名 `LookAtTarget` 做**原生级搜索（只查激活）**并**分帧**禁用；
   收集放在开菜单的下一帧；全场景遍历仅作为“类型名解析不到”时的兜底；
5. **层级**：本模组画布固定 `32766`（严格低于游戏光标层 `32767`），**从不修改光标画布**；把其它 ≥ 我们的非光标画布降到我们之下。

### 8.2 输入框（文本输入）生效链路 —— **照搬原模组那套**（2026-09-13 十二）

**最终方案**（`Widgets/UiTextRouter.cs` + 自绘文本的 `Widgets/UiInputs.cs`）：
不再用 `TMP_InputField` **收键**，改成原模组（`CoopInputBox` + `CoopUIManager`）已经实机验证过的四通道管线：

| 通道 | 作用 | 备注 |
|---|---|---|
| ① **物理键轮询** | 英文/数字/常用符号 + Shift + 退格（0.45s 后每 0.06s 连发）+ 回车提交 + ESC 结束打字 | `Keyboard.current`；无 native 风险 |
| ② **隐藏 IME 锚点** | 屏幕内、全透明、不可点、不可见光标的 `TMP_InputField`（**与真实输入框同构**：Image + Text + Placeholder + textViewport） | 只为**唤起系统输入法**；配 `Keyboard.SetIMEEnabled(true)` + `SetIMECursorPosition`（定位到当前输入框） |
| ③ **原生 Win32 IME 读取** | `ImmGetCompositionStringW`：`GCS_RESULTSTR` = 刚提交的中文（主通道）、`GCS_COMPSTR` = 正在组合的拼音（**组合中屏蔽物理键**，否则拼音字母/空格/数字选字会漏进输入框） | IL2CPP 下 `TMP_InputField.text` 恒空、`OnTextInput` 把 CJK 破坏成 U+FFFD（原模组实测） |
| ④ **`Keyboard.OnTextInput` patch** | postfix 转发 CJK（`> 0x2E7F`）作为兜底；`\b`/`\r` 不处理（避免双删/IME 空格确认误提交） | `HarmonyReflect` 反射挂，失败只告警 |

**聚焦时唯一要做的“环境准备”**：临时放行拦截层（`UiInputGuard.SetTextCapture(true)`）——
激活游戏 `EventSystem`（主菜单实测 `EventSystem.current == null`）+ 启用那 1~2 个原本 enabled 的输入模块 +
压住外部 `GraphicRaycaster`。原因：**输入法组合只送到已聚焦的输入框**，而 `isFocused` 需要 `EventSystem` 与输入模块。
失焦立即原样还原（模块重新停用、EventSystem 还原、**聚焦期临时压住的射线器 `_textRc` 也要放回去**）。

> ⚠️ **2026-09-13 真坑（用户：“Chat 聚焦在失去聚焦之后就会导致菜单组件都没法接受点击”）**：
>   以前 `SetTextCapture(false)` 的还原分支**只做了前两项，漏了 `_textRc`**，而 `_textRc` 只增不减，
>   `Restore()` 与 `EnsureReleasedWhenIdle()` 也都没管它 ⇒ **只要聚焦过一次输入框，游戏自己的射线器就永久 `enabled=false`**。
>   实测：聚焦瞬间压住 **60 个** `GraphicRaycaster`；失焦后修复前仍是 60、修复后回 **0**——
>   游戏 UI（含 ESC 菜单里的按钮 / 原生菜单）因此完全接不到点击。
>   现修：新增 `RestoreTextRaycasters()`，在 `SetTextCapture(false)` 与 `Restore()` 里都调用，
>   并把 `_textRc.Count` 纳入 `EnsureReleasedWhenIdle()` 的残留判定（自愈）。
>   ⚠️ 诊断教训：只看“游戏输入模块 启用/停用”就判定“无残留”是**不够的**（那两项本来就正常）——
>   探针新增 `聚焦临时压住射线器=N 打字中=…`，以后这条链路一目了然。

> 历史（十一）走过的弯路：用真实 `TMP_InputField` 收键 → ① `UiTextInput.TickAll` 从未被调用（托管轮询是死的）；
> ② 我们为防穿透停用了游戏输入模块 → TMP 收不到键盘；③ TMP 文本从不回写 `OnChanged`。
> 这三条正是“输入框没有实现”的真因；十二版直接换成原模组管线，三条一起消失。

**诊断**：`widgetprobe` → `输入框 N 个（聚焦 M）` + `输入管线：聚焦='…'（文本='…'）｜组合中=…｜IME 锚点=已聚焦/无｜键盘=…｜最近原生提交='…'`
+ `拦截层：游戏输入模块 启用/停用`。测试命令 `type:<文本>` 向聚焦输入框写文本（与真实输入走同一入口）。

**实机取证**（G/D 两端，`open:provider:open-nest-coop` → `tap:input:coop.roomName` → `type:ZZ`）：

```
输入框 2 个（聚焦 1）：
输入管线：聚焦='Room Name'（文本='Open Nest DEV'）｜组合中=False｜IME 锚点=已聚焦｜键盘=True｜patch=已尝试
拦截层：游戏输入模块 启用=1 停用=1｜新输入系统 键盘=True 鼠标=True
type — 向内 'Room Name' 输入 'ZZ'（现文本='Open Nest DEVZZ'）
```

### 8.3 列表滚动与滚动条（2026-09-13 修：“块内容超出没有自动加滚动条和拖动效果”）

- **真因**：`UiList.ContentHeight` 只统计 `UiList.Add` 加进来的行；而**声明式页面（provider）的行是直接挂到
  `UiFlow` 上的**（不走 `Add`）→ 内容高恒为 **0** → `max = 内容高 - 视口高 = 0`：
  滚轮/拖拽的偏移被 `Clamp(0)` 吃掉（“拖不动”），滚动条判定“内容不超出”而隐藏。
  现 `UiFlow` 暴露 **`ContentHeight`**（最近一次 `Apply()` 的排列结果）并被 `UiList` 采用。
- **滚动条**：列表右边缘内侧**预留 7px**（内容宽度相应缩窄，不会盖住行右侧的按钮），
  轨道 + 滑块（滑块高 `视口/内容 × 轨道高`，最小 24px；位置 1:1 跟随偏移）；
  **内容不超出视口时整条隐藏且不参与命中**（`UiHotZone.Enabled=false`）。
- **轨道可点可拖**：局部 y → 滚动比例（含 pivot 换算）；命中优先级靠**注册顺序**（滚动条热区必须
  **晚于**视口热区注册，否则那几像素会被视口热区吃掉）。
- **实机取证**（ModMenu 页，内容 897 / 视口 614）：`滚动条 显示`；`vdrag:scrollbar:80` → 偏移 `0 → 139.1`；
  `scroll:list-viewport:-3`（滚轮 3 格）→ `139.1 → 265.1`（+126 = 3×42px）；Coop 页（内容 473 < 视口 614）→ `滚动条 隐藏`。
- **诊断**：`listprobe` 现在会打印每个存活列表的 `内容高/视口/可滚/偏移` 与 **滚动条显示状态**，外加
  `滚轮：原始值 → 格数，发给哪个热区` 和 `拖拽：当前拖拽热区`。

---

## 九、自带菜单（"几个菜单"）与自测钩子
| 页面 | 内容 |
|---|---|
| 主页 | 入口列表（导航到各子页，演示页面栈 + 面包屑 + 返回） |
| 组件总览 | 全部基础组件各来一个（含交互与实时值显示） |
| 动态布局 | 增删行 / 改文字长度 / 改语言 → 观察自动重排（验证"动态布局"） |
| 列表页 | 行对象池 + 虚拟化滚动（对齐 `ModMenuListView` 的行模型） |
| 原生观感 | UI Box 素材捕获状态 + 切换 9-slice/纯色 + 过渡演示 |
| 原生注入 | 原生菜单项注册（多级页面项）+ 重新注入 + 结构探测日志 |
| 关于 | 版本 / 宿主 / API 版本 / 第三方注册页列表 |
| **画廊 · 按钮 / 行** | 四种风格 × 三种宽度 + 禁用态 + 行组件（导航/动作/信息）+ 文本层级；每颗按钮标出实际用的底图 |
| **画廊 · 输入控件** | 开关 / 滑条 / 步进 / 选择 / 文本输入 / 快捷键 / 进度（操作后顶部显示最新值） |
| **画廊 · 容器与浮层** | 面板 / 卡片 / 分隔线 / 页签 / 模态框 / 提示 / 滚动列表 |

**原生菜单里的树（多级）**（`DemoStarter` 实注，`NativeMenuPage` 逐级展开）：

```
模组 UI 库（唯一注入原生 ESC 列表的一行，ShowInNative=true）
├─ 全部页面 ›            组件总览 / 动态布局 / 列表·滚动 / 原生观感 / 原生菜单注入 / 关于
├─ 原生组件画廊 ›        按钮·行 / 输入控件 / 容器与浮层（上面三页）
└─ 关于
```

面板只有 400 高（一页最多 8 行），所以**只把顶层那一行**放进原生列表，其余走原生页展开。

**自测钩子（命令行参数，正常启动行为不变）**：`-onuk-autoopen[=<pageId>]`（N 秒后自动打开菜单/直达某页）、
`-onuk-selftest=<name>`（跑组件/布局自检并打日志）。用于自动化截图与回归。

---

## 十、构建 / 部署 / 验收

```powershell
# BepInEx 端（部署到 G 端 BepInEx\plugins\）
dotnet build src\OpenNestUIKit\OpenNestUIKit.csproj -c Release -p:DeployToGame=true
# MelonLoader 端（部署到 D 端 Mods\ + UserLibs\）
dotnet build src\OpenNestUIKit.MelonMod\OpenNestUIKit.MelonMod.csproj -c Release -p:DeployToMods=true
```

### 验收清单（2026-09-12 实测结果）

| 项 | 状态 | 证据 |
|---|---|---|
| 双端构建 0 错误 0 警告 | ✅ | `dotnet build` 两工程 `errors=0 warnings=0`（`-t:Rebuild`） |
| 加载与启动 | ✅ | `=== Open Nest UIKit v0.1.0 (BepInEx 构建) ===` / `behaviour mounted` / `started in 17 ms` |
| 画布层级与光标 | ✅ | `layer dock: ours 32766->32766（低于游戏光标层 32767）... active-cursor-canvases=1（未动）` |
| 外部射线压制 | ✅ | `suppressed 36 external GraphicRaycaster(s) (total saved=59)` |
| ESC 阻止 + 世界点击拦截 | ✅ | `game click block installed: LookAtTarget.OnClickDown prefix` + `player frozen (SetFrozen(true))` |
| 组件级交互锁 | ✅ | `interaction lock: disabled 27 component(s) [LookAtTarget=27]`（任务场景）；关菜单 `restored 27` |
| 菜单打开与页面渲染 | ✅ | `菜单打开 page='widgets' stack=1 order=32766` + 截屏（标题栏 “Native UI Kit” + 行列表） |
| 热区/悬停命中 | ✅ | `pointer: zones=21 hover=btn:‹ Back drag=-` |
| 原生素材捕获 | ✅ | `captured 'UI Box Castile' (128x128 border=(18,34,18,16))` … 共 **12 个**（含 SGRounded/BaseFrame/TitleBorder） |
| 原生菜单注入（多级页面项） | ✅ | 两个容器各注入成功：`'ESC Menu Buttons'(root=Barbet / Main Camera) 注入 1 项：模板='OpenSettingsBtn' … 共11按钮` |
| 原生时机 patch | ✅ | 修复后：`原生菜单注入时机已挂上（MainMenuStateRelay.HandleMainMenuLoaded postfix）` |
| 动态布局 / 滚动 / 输入框 IME / 真实鼠标点击手感 | 🔄 | 需人工在游戏里实际点一遍（自动截图读图不可靠） |

---

## 十一、风险与待实测

| # | 风险 | 现状 / 对策 |
|---|---|---|
| 1 | `ScrollRect`/布局组在 IL2CPP 下异常 | ✅ **绕开**：布局全部自研（测量 → 分配 → 写 rect），滚动只用 `RectMask2D` 做裁剪，不依赖 `ScrollRect`/`LayoutGroup`/`ContentSizeFitter` |
| 2 | 自绘测量与 UGUI 布局组混用互相打架 | ✅ 组件**不使用**布局组（已验证） |
| 3 | 原生 `UI Box` sprite 捕获时机 | ✅ 已解决：主菜单加载完成（postfix/事件）自动捕获；MLL 端需后续实测 |
| 4 | 原生菜单 slot 让位/样式拄错 | ✅ 照抄 11 条坑；实测两个容器各 11 按钮均匀（间距 38） |
| 5 | MLL 端 `LocalisationManager.GetFont` 缺失 | ✅ 编译期 `#if !MELONLOADER` 排除 + 运行时扫描兜底 |
| 6 | 与其它模组画布/输入压制互相干扰 | ✅ 只压制**其它**画布，关闭立即还原；画布固定 32766 不抢 32767 |
| 7 | 不透明原生 sprite 当装饰层会把面板洗白 | ✅ 已修（2026-09-12 实测发现）：装饰层按面板色着色（`UiSurface.Build`）；待用户确认最终观感 |
| 8 | 托管输入路径无系统 IME | ⚠️ 已知限制：`EventSystem` 不可用时只能英文/数字；需中文则必须在任务场景里保持 EventSystem 可用（或后续接 CoopInputBox 式 Win32 IME） |

---

## 十二、实现状态（2026-09-12）

### 已实现（双端 0 错误 0 警告）

| 能力 | 实现位置 | 说明 |
|---|---|---|
| 动态布局 | `Layout/UiFlow.cs` + `UiMeasure.cs` + `UiSize.cs` | 测量→分配→排列，Grow/Percent/Fixed/Auto，递归容器；内容/语言变化后 `Apply()` 重排 |
| 基础组件集 | `Widgets/*.cs` | 窗口/面板/文本/按钮/开关/滑条/步进/选择/输入框/快捷键/分隔线/进度/页签/列表/入口行/信息行/动作行/模态框/提示（共 19 个） |
| 多级菜单（子菜单） | `Menu/UiPageStack.cs` + `UiMenuWindow.cs` | 同一窗口内页面栈 + 面包屑 + 返回/关闭；页面实例缓存（切页只显隐，状态不丢） |
| 原生菜单注入（多级页面项） | `Native/NativeMenu*` | `MainMenuStateRelay.HandleMainMenuLoaded` postfix（失败退化为事件/轮询）+ ESC 容器全场景遍历 + slot 让位 + 抄模板样式 |
| 原生观感 | `Theme/UiSkin.cs` + `UiTheme.cs` + `UiKitTween.cs` | UI Box/SGRounded 系列捕获（实测 12 个）+ 副本自持有 + border 烤入缩放 + 淡入/缩放过渡 |
| 第三方契约 | `src/OpenNestUIKit.API/` | 纯 .NET 软依赖；声明式菜单树 + 行模型；示例见 `Pages/DemoProvider.cs` |
| 输入隔离（五层） | `Native/UiPointerRouter.cs` + `UiInputGuard.cs` + `UiEscapeGuard.cs` + `UiCursorOverlay.cs` | 与 `OpenNestModMenu` 同源的已验证方案 |
| 自带菜单 | `Pages/*.cs` | 主页 / 组件总览 / 动态布局 / 列表 / 原生观感 / 原生注入 / 关于（7 页） |

### 待完善（下一步）

1. **人工实机验收**：真实鼠标点击手感、输入框中文 IME、原生入口按钮观感（自动化已覆盖：模拟点击/拖拽/断言/页面栈/注入，见 §十三）；
2. **D 端（原生 MelonLoader）实测**：目前只在 G 端（BepInEx + 桥）跑过；测试模组已有 MLL 壳，跑 `-onuktest-run` 即可回归；
3. **行池真正复用**：`UiList.Obtain<T>` 已备好，但测试页/演示页仍是重建；大列表（上百行）时改成取用/回收；
4. **网格/自适应列**：目前只有纵/横流；需要表格或卡片墙时补 `UiGrid`；
5. **外部语言文件**：现在只有内联双语 `UiKitLoc.T(zh,en)`；多语言模组需要时可参考 `OpenNestModMenu/Loc/*` 接语言键文件；
6. **原生菜单项重复注入去重**：目前“桥接事件 + postfix”可能各注入一次（最终状态一致，日志多一条）；
7. **输入框中文输入**：接 CoopInputBox 式 Win32 IME（已有实现可参考）以在无 EventSystem 时支持中文；
8. **Ghost 风格不产生素材日志**（有意为之）——若需诊断可补一行。

---

## 十三、测试模组与 CLI 自动化

独立的**测试模组** `src/OpenNestUIKit.Test`（+ `.MelonMod` 壳）从“外部调用方”角度验收本库：
契约路径、直接调组件路径、原生注入、素材选择、输入守卫，全部可用**命令行脚本**驱动：
模拟点击 / 拖拽 / 滚轮 + 断言 + 报告（`-onuktest-run=<脚本>`），报告写 `OpenNestUIKitLogs\test.log`。

实测（2026-09-12 G 端）：`PASS=16 FAIL=0`（模拟点击 4 次、拖拽回调 13 次、断言 5 条全过）。

完整命令表、 “为什么模拟点击可信”（注入指针/按键后仍走**与真实鼠标同一条命中路径**）、
热区命名约定与回归步骤 → 见 [`docs/UI_KIT_TEST.md`](UI_KIT_TEST.md)。

---

## 十四、悬浮聊天层（`Widgets/UiChatOverlay.cs`）

第三方（目前是 Coop）只要调 `UiKitHost.SetChat(lines, onSend, title, hint)`，宿主就渲染一个**独立浮窗**：

| 状态 | 表现 | 进入方式 |
|---|---|---|
| **收起** | 左中半透明小面板：只显示最近几条聊天 + 一行提示（默认“回车 打开聊天”）；鼠标穿透（不挡游戏） | 默认；回车唤入失败时保持 |
| **展开** | 面板变深：历史区变矮 + 底部出现输入行（`UiTextInput`）并**聚焦**（唤起系统输入法，走 §8.2 输入管线） | **回车**（菜单没开且没人正在打字时）/ 鼠标点浮窗 / `UiKitHost.FocusChat()` |
| 发送 | 回车 → `onSend(文本)` → 清空 → 自动收起（历史保留，滚到最新） | — |
| 收起 | ESC（输入框失焦 → 自动收起）/ `UiKitHost.CloseChat()` | — |

要点：
- **自己的画布**（`OpenNestUIKit_HUD`，`sortingOrder = 32760`）：菜单画布在关菜单时会整体隐藏，而浮窗必须**在菜单关着的时候也在**；
  层级比菜单低一档（菜单 32766），所以菜单打开时不会被浮窗挡住。
- 历史每 0.4s 拉一次（`lines()` 返回最近 N 条），**内容 signature 变了才重建**（不闪、不丢滚动位置）；
- 行数不确定 → 历史区就是一个 `UiList`（自带滚动条，见 §8.3）；
- 诊断：测试模组 `chatprobe` / `widgetprobe` → `悬浮聊天层：注册=… 展开=… 行数=… 面板背景alpha=… 列表高=…｜输入框聚焦=…`。

### 已知坑（2026-09-13，三个都已修，前两个在 `UiList` 通用层）

1. **`UiList` 一次重排不收敛**：`ApplyLayout` 里 `SetRect(Content, …, ContentHeight)` 用的是上一轮的 `ContentHeight`，
   `ClearRows()` 归零后写下去的是 **0 高** ⇒ `RectMask2D` 把行全裁掉（`Count>0` 却一条不可见）。
   **固定尺寸宿主不会再有第二次重排** ⇒ 必须在 `ContentHeight` 算完之后再落定一次。
2. **水平拉伸 anchor 下 `sizeDelta.x` 必须是“相对父宽的增减”**（`-16`）：写绝对宽会把内容推出宿主左/右边界。
   与之配套：改了视口高度要**立刻 `ApplyLayout()`**，否则滚动上限按旧视口高算。
3. **不要拿 `_panelBg` 兼作输入框背景**：`SetExpanded` 末尾会把它覆盖成不透明，收起态就不透了（输入框背景由 `UiTextInput` 自管）。
4. **浮窗类常驻 UI 必须自己保证 `UiPointerRouter.Active`**：它默认只在菜单/原生页打开时激活，菜单关着时浮窗热区全死（“滚轮/点击没反应”）。
5. **新建的常驻热区要“看场合让位”**：覆盖整块的通用热区（如 `chat-open`）后登记=优先，会吃掉内部控件的点击；
   菜单/展开态都应禁用，且注销时必须 `UiPointerRouter.Remove(zone)`（否则死热区累积）。
6. **分区高度要一起扣**：收起态的列表高必须把**底部那行提示**也扣掉（`PanelH - 标题24 - 提示行24 - 2×6 = 130`），
   以前写死 `PanelH - 30` → 提示行压在最后一条消息上（用户 2026-09-13）。面板内分区现在都是常量（`HeaderH`/`HintH`/`InputH`/`Gap`）。

验证方式：`chatdemo:<条数>`（灌假消息，不需要联机会话）+ `chatopen`/`chatclose` + `listprobe`（行数/内容高/视口/**可滚量/当前偏移**/可见行）
+ `chatprobe`（展开/面板 alpha/列表高/**展开热区启用状态**）+ `scroll:chat-list-viewport:±N`（⇐ 必须用**内部**热区名；
  `scroll:chat-list` 会先匹配到后登记的 `chat-list-scrollbar` 而报“该热区不支持滚轮”），见 `docs/UI_KIT_TEST.md`。
