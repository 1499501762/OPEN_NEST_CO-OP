# UI 库切片工具与切片定义（九宫格切割线）

> **目的**：把"九宫格切割线"这件事从**自动猜测**升级为**人工定准 + 工具产出 + 库无条件采用**。
> 工具：`tools/slice_tool.py`（**独立离线**，不开游戏）；产物：`OpenNestUIKit.slices.ini`（库读它，2 秒热重载）。
> 相关：`docs/UI_KIT.md` §7.2（九宫格必须按尺寸选）、`docs/NATIVE_UI.md` §六（原生图集与 `pixelsPerUnitMultiplier` 无效的实测）。
>
> **更新记录**
> - 2026-09-12 新建：工具 `tools/slice_tool.py`（GUI + CLI + 自检 + 一键部署）、定义文件格式、库侧消费链路与实测取证。
> - 2026-09-12（二）**剔除不需要的素材 + 防误切**：
>   ① GUI 新增「删除此条定义（Del）」、列表 `● 已定义 / ○ 未定义` 标记与只看已定义/未定义的过滤器；
>   ② **光浏览不再自动写入定义**（只有真的改了字段才把那张图加进定义）——旧版浏览即写入，会把图标/单位等非组件素材顺手存进 ini；
>   ③ 拖线与字段输入**按图尺寸钳制**（单边 ≤ 图宽/高，且 左+右 ≤ 宽、上+下 ≤ 高），预览对越界值给警告；
>   ④ CLI 新增 `--remove` / `--remove-match`；⑤ 修 GUI 两个致命问题：控件比 border 小时中间段被压成 0 像素导致渲染崩溃（现新增钳制与退化跳过）、从任意 cwd 运行相对路径失效（现以仓库根为基准）。
> - 2026-09-12（三）**修主 bug：画布切线位置错 + 预览看不懂**（用户反馈“线和实际预览图没对上、没法正常用”）：>   ① **左/上两条切线漏加了图片居中偏移**（`redraw` 里写成了 `b*z` 而应为 `off+b*z`）→ 图居中时左/上线被画到图外，而右/下线是对的；现已修正，并把该坐标一致性纳入 `--selftest`（会把这条 bug 拦住）。
>   ② 预览改成**竖排**（旧版横向拼图宽度写死 440，第三档经常根本没画）；每档预览图上**直接画出切线**：
>   实线 = Unity 实际生效位置（可能被压缩），虚线 = 你设的切线 —— 两者不重合就是被压缩（这正是“线与预览对不上”的真正原因：
>   `SGRounded`(48) 塞进 120×32 会被压到 ×0.33，`TitleBorder`(82) 到 ×0.20），下方说明给出压缩倍率与逐边变化。
>   ③ 新增「按当前切线给尺寸」按钮（自动给 3 档放得下的尺寸）；窗口尺寸按屏幕自适应；画布随窗口缩放重新居中。
> - 2026-09-12（四）**用途标记 tag + “不侵入游戏”取证**:
>   ① 定义新增 `tag` 字段（说明“这张图是哪种组件的背景”）：GUI 下拉/自定义输入 + 列表 `[tag]` 标记 + 按 tag 过滤；
>   CLI `--set` 第 8 字段 / `--tag` / `--tags`；库侧 `UiSliceStore.ByTag/Tags` + `UiSkin.RefForTag/RefForTagOr`，
>   且 `PanelRef/LineRef/DialogRef/ButtonRefFor` **优先采用打了对应 tag 的素材**（`button` → `button.primary`/`button.danger`）。
>   ② **修一个真 bug（会崩游戏）**：`Bake → NativeGet → Resolve（用户切片优先）→ Bake` 死循环——
>   触发条件是“ini 里定义了、但这张图没被捕获”；现新增只查缓存的 `Cached()` + `Bake` 重入保护 + `Resolve` 先要求已捕获。
>   ③ 新增 `CaptureDefinedSlices()`：按 ini 里的名字补捕获，定义过且场景里存在的图一定能取到。
>   ④ 测试模组新增 `tags` / `tagprobe:<tag>` / `nativecheck` 三条取证命令（后者区分“我们的节点/游戏节点”）。
> - 2026-09-13（五）**用上去更方便**：新增 `scripts\slice-tool.ps1` 包装脚本（从任意 cwd 都能开，不再因
>   `python slice_tool.py` 路径不对而失败）；新增 `--check`：部署前体检（素材是否存在、border 是否越界、
>   哪些尺寸会被压缩），可 `--check --deploy` 连做；输出标记改 ASCII（控制台 GBK 打不出 ✓/⚠）并为 stdout 加 error 容忍。
> - 2026-09-13（六）**tag 语义改为“首选+自适应” + 城堡框类图的捕获**（用户：“大部分的切片其实是对的”）：
>   ① tag/定义现在只在**放得下**时优先，放不下则该尺寸退回后续候选（避免把 512×102 的宽框压进 24px 按钮）；
>   全部放不下时才退到“仍用第一个有定义的”，并在日志标 `用户切片（border 放不下，会被压缩）`。
>   ② 新增 `CaptureDefinedSlices()` 的**内存按名捕获**（`Resources.FindObjectsOfTypeAll<Sprite>()`）：
>   实测 `Castles wide Frame`/`Castles Title Frame`/`InputFieldBackground` 在主菜单场景里没有 Image 引用，
>   之前压根取不到（定义等于没用），现在能拿到（已捕获素材 16 → 23）；实测 `sliceprobe` 两者均 PASS 采用定义。

---

## 一、为什么需要人工切片

九宫格（`Image.Type.Sliced`）生效的前提是：

```
border 横向合计 + 余量 ≤ 控件宽   且   border 纵向合计 + 余量 ≤ 控件高
```

一旦放不下，Unity 的 `Image.GetAdjustedBorders` 会把**整张图连同四条边按最小比例整体压缩**进控件 ——
观感就是"图案被压实 / 像平铺 / 装饰糊成一团"。实测案例：`UI Box Castile` 作者 border `(18,34,18,16)`，
纵向合计 `34+16 = 50px`，塞进 24~32px 高的按钮必然被压缩。

库侧原来的兜底是"自动换素材 / 自动把 `border ÷ scale` 烤进副本 / 退纯色"（见 `docs/UI_KIT.md` §7.2）。
自动规则能救急，但**正确的切线是审美判断**：哪几像素是"边框/角"，哪几像素才是"可拉伸的中段"，
人看图 3 秒能定，代码猜不准（尤其城堡、星形、双线这类装饰框）。
另一个硬约束：`Image.pixelsPerUnitMultiplier` 在本游戏 IL2CPP 下**改值无效**（`docs/NATIVE_UI.md` §六末），
所以"更细的边框"只能靠**烤入新 border 的 sprite 副本**实现 —— 这正是切片工具输出的 `scale` 字段。

**分工**：工具产出定义 → 库读到定义就**无条件采用**（不再自动缩放、不再退纯色）→ 没定义的图才走自动规则。

---

## 二、切片定义文件（库读的那份）

### 2.1 路径

由 `Core/UiKitPaths.cs` 按**本模组实际被谁加载**决定（`SlicesFile` = `<configDir>\OpenNestUIKit.slices.ini`）：

| 部署形态 | 文件位置 |
| --- | --- |
| BepInEx 端 | `<Game>\BepInEx\config\OpenNestUIKit.slices.ini` |
| MelonLoader 原生端 | `<Game>\UserData\OpenNestUIKit.slices.ini` |
| 桥环境（BepInEx + MelonLoader.Loader） | `<Game>\MLLoader\UserData\OpenNestUIKit.slices.ini` |

文件不存在时 `UiSliceStore.Init` 会自动建一份**带注释的空模板**（`Core/UiKitRuntime.cs` 启动链路里调用），
所以你也可以直接手写编辑器改它。

### 2.2 格式

INI，UTF-8（无 BOM），**图片名大小写不敏感**；`#`/`;` 开头为注释：

```ini
[UI Box Castile]
border=18,34,18,16      ; 左,上,右,下 —— 基于该图【原图尺寸】的切割线（像素）
scale=1                 ; 把 border 再除以它后烤进副本（1=原样；调大=更细的边框）
mode=sliced             ; sliced | tiled | simple | none
center=1                ; sliced/tiled 时是否绘制中心（1=画，0=只画边框，中心由底层纯色兜住）
```

| 字段 | 缺省 | 说明 |
| --- | --- | --- |
| `border` | `0,0,0,0` | 四段顺序固定 `左,上,右,下`；必须 4 个值，否则该项被忽略（保留既有值） |
| `scale` | `1` | 下限被夹到 `0.01`；实际烤入的 border = `floor(border ÷ scale)`（`UiSlice.BakedBorder`） |
| `mode` | `sliced` | `tiled`=边缘/中心平铺；`simple`=整图拉伸；`none`=**不使用这张图**（该处退回纯色） |
| `center` | `1` | 只对 `sliced`/`tiled` 有意义；中心透明的装饰框给 `1`（底层纯色会填住），想彻底空心中间给 `0` |
| `tag` | 空 | **用途标记**（这张图是哪种组件的背景，见 §2.3）；空 = 未标记，也可写 `tag=none` 显式取消 |

未标记时写盘**不会**输出 `tag=` 行（与库侧 `Snapshot()` 一致）。

与 C# 端一一对应：`Theme/UiSlice.cs` 的 `UiSliceStore.Load()` / `Snapshot()`（导出格式与上表完全一致）。

### 2.3 用途标记 `tag`（这张图是哪种组件的背景）

预置词表（可自由填写/自定义）：`panel`、`button`、`button.primary`、`button.danger`、`button.secondary`、
`dialog`、`separator`、`input`、`tab`、`row`、`frame`、`other`。

库侧消费规则（`UiSkin.RefForTag`）：

| 控件 | 查找顺序 |
| --- | --- |
| 面板 `PanelRef` | `panel`（**精确优先**）→ `panel.small` 这类 **前缀**（`panel.` 开头的都算）→ 原有候选名 |
| 分隔线 `LineRef` | `separator` → 原候选 |
| 弹框 `DialogRef` | `dialog` → 原候选 |
| 按钮 `ButtonRefFor` | danger：`button.danger` → `button`；primary：`button.primary` → `button`；其它：`button.secondary` → `button` → **再接**原风格候选名；然后在候选里挑**第一个“放得下”**的 |
| 按名 `RefFor(name)` | 直接用该名的定义（不管尺寸，已给定尺寸的场景走上面那行） |

两条硬规则，标记前请知悉：

1. **标记是“首选”而不是“霸占”**：打得 tag 的素材**只要这个尺寸放得下就直接用**（border 原值，不缩放不替换）；
   放不下时该尺寸**退回后续候选**（按尺寸自动挑一张放得下的，或把 border ÷ scale 烤进副本）；
   只有当**全都不放得下**时才退到“仍用第一个有定义的”（此时日志标 `用户切片（border 放不下，会被压缩）`）。
   例：把 `Castles wide Frame`(512×128, border 44/34/43/17) 标成 `button.secondary` 后，
   56px 高以上的次按钮用它，32px 高的小按钮自动改用放得下的 `UI Box line`。
2. **定义/标记只有在该图被捕获时才有用**：库在主菜单加载后①扫现场 `Image` 捕获素材；
   ②额外把 ini 里出现过的名字从**内存资源**里按名抓一遍（`Resources.FindObjectsOfTypeAll<Sprite>()`，
   像城堡框这种只在别的界面用、主菜单场景里没有 Image 引用的图靠这一步拿）；
   两者都没拿到才会无效——`tags` 命令会标 `✗未捕获`。

### 2.4 本文件只给 OpenNestUIKit 用（不侵入游戏原生 UI）

- 本模组**只写自己的 `Image`**（自建画布/注入项）。改切片时走 `Sprite.Create` 造 **新副本**，
  游戏原有 `Sprite` 的 `border` **从未被改写**，也不会被换成我们的副本。
- 我们在原生菜单里注入的按钮是**自建 + 抄样式**（只**读**模板按钮的 `Image.sprite`），不克隆原生按钮。
- 实测取证（`nativecheck` 命令，G 端）：把 `UI Box Castile` 改成非作者值 `8,8,8,6` 后：
  场景 `Image=1942`（我们的节点 29 / 游戏节点 1609）；
  **① 游戏节点使用我们的切片副本 = 0**；② 我们的节点用自己的副本 = 27（机制生效）；
  ③ 与定义同名的游戏图 681 张，它们的 `border` 仍为游戏原值 `(18,34,18,16)`。

---

### 2.5 热重载

`UiSliceStore.Tick(dt)` 每 **2 秒**比对文件 mtime，变了就重载并触发 `Changed` 事件；
`Core/UiKitRuntime.cs` 订阅该事件 → `UiSkin.InvalidateBaked()` 清掉烘焙缓存 → 界面重建即见新观感。
**游戏不用重启**：工具里点「保存并部署到游戏」，2 秒内生效。

---

## 三、工具 `tools/slice_tool.py`

依赖：Python 3 + Pillow（预览渲染必需）；tkinter 仅在 `--gui` 时需要（Python 自带）。
**不需要开游戏、不需要 Unity**：素材读 `ref/ui_sprites_all/`（285 张）或 `ref/ui_sprites_menu/`（39 张），
作者原值读同目录的 `ui_sprites_manifest.txt`（由 `tools/extract_ui_sprites.py` 生成，
`border=(x,y,z,w)` 即 `左,上,右,下`）。

### 3.1 图形界面（推荐）

```powershell
python tools/slice_tool.py --gui                      # 默认 ref/ui_sprites_all + ref/ui_slices.ini
python tools/slice_tool.py --gui --dir ref/ui_sprites_menu --ini ref/ui_slices_menu.ini

# 更方便（从任意目录，不用担心 cwd/路径）：
scripts\slice-tool.ps1                                 # = --gui
scripts\slice-tool.ps1 -Check                          # = --check（部署前体检）
scripts\slice-tool.ps1 -Deploy                         # = --deploy（把 ref\ui_slices.ini 交到游戏）
```

界面构成与操作：

| 区域 | 内容 |
| --- | --- |
| 左：素材列表 | 顶部输入框正则过滤（例 `UI Box`）；下面单选 **全部 / ● 已定义 / ○ 未定义**；再下面是 **tag 过滤器（全部 tag / (未标记) / 各 tag）**；条目前缀 `●`=已定义、`○`=未定义，已标记的显示 `[tag]`；双击/方向键 `← →` 换图 |
| 左：按钮 | 「保存 ini」「保存并部署到游戏」「**删除此条定义（Del）**」「导出预览 PNG」「重置为作者值」；下方显示`已定义 N 条 / 素材 M 张`；`Ctrl+S` 保存 |
| 中：原图画布 | 深色底 + 原图（自动缩放，最大 ×4）；4 条切线用四色画（红=左、黄=上、绿=右、蓝=下）；**鼠标拖线**直接改，`↑ ↓` 切换选中线，`Shift+←/→` 微调 1px；底部标出`已定义 / 未定义（显示作者原值）` |
| 右：字段 | `左/上/右/下` 数值框、`scale`、`mode` 下拉、`center` 勾选、**`用途标记 tag` 下拉（可手输自定义）**（改完即时重画）；**所有值自动钳制在图尺寸内** |
| 右：预览 | 竖排显示 3 档尺寸（默认 `120x32,240x56,420x160`，可改）的实时渲染，**每张图上直接画切线**；下方逐档标注。超面板时整体等比缩小显示（不会像旧版那样默默不画） |

关于“预览里的线”：

- **实线 = Unity 实际生效的位置**（`Image.GetAdjustedBorders` 之后的 border）；**虚线 = 你设的原始切线**（只在两者不同时才出现）。
- 两者不重合 = 这一档尺寸下 `border` 放不下，Unity 会整体压缩（“图案被压实”）。说明行会给出 `⚠ 压缩×0.33（上 48→16…）`。
- 这是**游戏里的真实行为**，不是工具算错：例 `SGRounded`(border 48，图 256×256) 塞进 120×32 压缩到 ×0.33；
  `TitleBorder`(82) 到 ×0.20。小按钮要干净就得用 border 小的素材（`UI Box line` 14、`UISprite`/`Background` 10）。
- 想看“不被压缩”的样子：点**「按当前切线给尺寸」**（自动给 3 档放得下的尺寸），或自己把尺寸改大。
- 导出到 PNG（`导出预览 PNG` / CLI `--preview`）同样是竖排且带切线标注（实线/虚线含义相同）。

**删除与"不写入"语义**（避免把非组件素材存进 ini）：

- **浏览等于只读**：翻到一张没有定义的图，字段里显示的是**作者原值**，不会白占一条定义；只有你真的改了字段（拖线/改数值/换 mode/勾 center）才会把那张图**加进定义**。
- **删除此条定义（或列表/画布上按 `Del` / `BackSpace`）**：把这张图从定义里拿掉 → 库回到"自动选材"（不是禁用）。模型要彻底禁用某张图用 `mode=none`。
- 批量删：CLI `--remove "<名>"`（可多次）或 `--remove-match "<正则>"`（例：`"Icon|Logo|Arrow|Dot|key art"` 一次剔掉非组件素材）。
- 边界钳制：单边 ≤ 图宽/高，且 `左+右 ≤ 宽`、`上+下 ≤ 高` —— 拖到图外或手敲 `999` 都会被收进合法范围（作者 border 本身超出图尺寸的极端素材除外，预览会给 ⚠）。

预览渲染**刻意复刻 Unity 规则**（角固定 1:1、边单向拉伸、中心双向拉伸、`tiled` 平铺、放不下时按
`GetAdjustedBorders` 等比压缩），所以"预览里难看"≈"游戏里也难看"，可离线判断。

`保存并部署到游戏` = 写 `ref/ui_slices.ini` + 拷到 `scripts/env.ps1` 里 `$GameDir`/`$ClientGame`
三个可能位置中**已存在**的那些（见 §2.1 表）。

### 3.2 命令行（批量 / 脚本 / 由助手直接调用）

| 命令 | 作用 |
| --- | --- |
| `--list` | 列出素材与作者 border、当前定义（配 `--match <正则>` 过滤） |
| `--template --out <ini>` | 把匹配到的图按**作者原值**写成模板 ini（起点文件） |
| `--set "<名>=L,T,R,B[,scale[,mode[,center[,tag]]]]"` | 改一条（可多次，第 8 字段 = tag）；随后写出到 `--out`（缺省= `--ini`） |
| `--tag "<名>=panel"` | 只打/改用途标记（`=` 后给空串 = 取消标记） |
| `--tags` | 列出已用的 tag 与对应素材 |
| `--remove "<名>"` / `--remove-match "<正则>"` | 删一条 / 按正则批量删（素材文件不动；删 = 回自动选材） |
| `--preview "<名>" --sizes 120x32,240x56,420x160 --out x.png` | 导出九宫格预览 PNG（多档尺寸拼一张，带标题与尺寸标注） |
| `--selftest` | 无界面自检：ini 解析/往返、`border ÷ scale`、渲染尺寸、放不下时的压缩、`mode=none` 全透明、**GUI 切线坐标与预览标注** |
| `--check` | **部署前体检**：素材是否存在（名字拼错）、`border` 是否超出图尺寸（不可能切出来）、哪些尺寸会被压缩；可 `--check --deploy` 连做 |
| `--deploy [<GAMEDIR>]` | 把 ini 拷到游戏配置目录（不给参数则从 `scripts/env.ps1` 读 `$GameDir`/`$ClientGame`） |

示例（本仓库已跑过）：

```powershell
# 面板/框类候选的作者原值模板 → ref/ui_slices.ini（18 条，用户编辑起点）
python tools/slice_tool.py --dir ref/ui_sprites_all `
  --match "UI Box|SGRounded|BaseFrame|TitleBorder|Castile|Header|Background|InputField|UISprite|UIMask|Castles" `
  --template --out ref/ui_slices.ini

# 改两条并部署到游戏（游戏内 2 秒热重载）
python tools/slice_tool.py --ini ref/ui_slices.ini --out ref/ui_slices_verify.ini `
  --set "UI Box line=6,6,6,6,1,sliced,0" --set "UI Box Castile=10,10,10,10,1,sliced,1" --deploy
```

---

## 四、库侧怎么消费（写接口的人需要知道）

| 环节 | 行为 |
| --- | --- |
| 解析 | `UiSliceStore` 单例；`Init(路径)` → `Load()`；`Tick(dt)` 2 秒热重载；`Changed` 事件 |
| 取素材 | `UiSkin.RefFor(name)` / `RefFor(candidates)`：**先查定义**，有定义就 `Bake(name, slice.BakedBorder)` 出副本并带上 `mode`/`center`；没定义才用作者原值（Sliced + 填中心） |
| 按尺寸选材 | `UiSkin.ButtonRefFor(primary, danger, w, h)` / `PickForSize(candidates, w, h)`：**第 0 步就是用户切片**（权威），后面才是"原样可用 → 烤入缩放 → 纯色" |
| 组件采用 | `UiButton`（`UiSurface` 填充层 + 装饰层）、`UiPanel`/分隔线、模态框（`UiOverlays`）都改走 `UiSpriteRef`，因此 `Type`（Sliced/Tiled/Simple）与 `FillCenter` 都跟着定义走 |
| 缓存 | 副本按 `名字@border` 缓存；定义变化 → `InvalidateBaked()` 清空重烘 |
| 日志 | 加载/热重载写 key `uikit.slice`；每次选材写 key `uikit.skin`，采用定义时明确标注 **`（用户切片；…）`** |

`mode=none` 的语义要点：**该图被显式禁用**，对应位置退回纯色（不是"回退到自动选材"）。
`border` 放不下时**不阻止**采用，只在日志里附 `⚠ border 放不下会被压缩` —— 切片是人的决定，库不擅自改。

---

## 五、验收与取证（已实测）

1. **工具自检**：`python tools/slice_tool.py --selftest` → `PASS=28 FAIL=0`
   （ini 解析/缺省补全/往返稳定、`border÷scale=(4,4,5,5)`、渲染尺寸、小尺寸压缩、大尺寸不压缩、
   放不下判定、`mode=none` 全透明，以及 6 条退化断言：border>控件、`12x8` 极小控件、`border=0`、
   `1x1`、`height=1`、`tiled` 小控件 —— 这些曾经直接抛 `ValueError`；
   **另含 4 条 GUI 数学断言**：四条切线屏幕坐标与 border 一致、拖左线到 x=14 处数值回到 14、
   压缩时能画出实/虚线、无压缩时线在 48 处 ——「左/上切线漏加居中偏移」就是被这组断言拦住的）。
2. **GUI 无窗口自检**（脚本化构造界面并驱动）：初始/浏览 10 张后定义数仍为 0（浏览不写入）、
   拖线后新增 1 条、`●/○` 标记正确、只看已定义=1 行 / 只看未定义=素材数-1、删除后回 0、空删不崩、
   重置为作者值后条目仍在、全量 285 张 ×2 档渲染无异常。
3. **边界钳制**：向图外拖线 → `border` 被收进图尺寸；手敲 `999` → 同样被钳制；
   旧的无效应定义（例 `Background` 写成 `82,65,3,3` 而图只有 32×32）在预览里会报 ⚠。
4. **预览正确性**：`--preview "UI Box Castile"` 导出作者原值 `(18,34,18,16)` 的 `120x32 / 240x56 / 420x160`
   三联图 → 120×32 一档明显被整体压缩（纵向 50 > 32），另两档正常 —— 与游戏表现一致，可用于离线判断。
5. **库采用（实机 G 端，BepInEx）**：把**非作者值**部署进去后跑测试模组 CLI
   （`docs/UI_KIT_TEST.md`）：

   ```
   -onuktest-run=open:test.buttons;wait:60;slices;sliceprobe:UI_Box_line;sliceprobe:UI_Box_Castile;report
   ```

   结果：`[NOTE] slices — 切片定义 18 条 ← …\BepInEx\config\OpenNestUIKit.slices.ini`
   → `[PASS] sliceprobe UI Box line — 已采用切片定义：border=(6.00, 6.00, 6.00, 6.00)`
   → `[PASS] sliceprobe UI Box Castile — 已采用切片定义：border=(10.00, 10.00, 10.00, 10.00)`
   → `[NOTE] report — PASS=5 FAIL=0`。
   日志同屏可见作者值与定义值的差异（`author=(14,14,14,13)` vs `def=(6,6,6,6)`），
   `uikit.log` 里 5 档尺寸全部记成 `按钮素材 = 'UI Box line@6,6,6,6'（用户切片；…）`
   —— 证明**定义优先级高于自动选材，且各尺寸都按定义走**。
6. 验收后已**清掉游戏内的验证用 ini**（恢复原自动选材观感），仓库里保留 `ref/ui_slices.ini` 模板与
   `ref/ui_slices_verify.ini` 验证样例，供随时复现。
7. **tag 与“不侵入游戏”取证**（G 端实测，`PASS=6 FAIL=0`）：

   ```
   -onuktest-run=wait:120;slices;tags;tagprobe:panel;tagprobe:separator;tagprobe:input;nativecheck;report

   [NOTE] tags — 用途标记 tag 6 种（定义共 18 条；已捕获素材 17 个）
     panel            UI Box Castile✓ UISprite✓
     input            InputFieldBackground✗未捕获
   [PASS] tagprobe panel — 按标记采用：UI Box Castile
   [PASS] nativecheck — 游戏原生 UI 未被揻改；（我们的节点用自己的副本 N 张）
   ```

   另一次专门验证（把 `UI Box Castile` 改成非作者值 `8,8,8,6`，开菜单后）：
   `sliceprobe` → `已采用切片定义：border=(8.00, 8.00, 8.00, 6.00)`，5 档尺寸全部 `UI Box Castile@8,8,8,6`；
   同一次 `nativecheck` → **游戏节点使用我们的切片副本 = 0**、我们的节点用自己的副本 27 张、
   与定义同名的 681 张游戏图 `border` 仍是 `(18,34,18,16)` —— 即**只影响我们的界面，不动游戏原生**。

---

## 六、常见问题

| 现象 | 原因与处理 |
| --- | --- |
| 改了 ini 游戏里没反应 | ① 路径不对：确认是**本模组实际部署形态**对应的那一份（§2.1 表；桥环境注意 `MLLoader\UserData`）；② 名字没对上（大小写不敏感，但必须与**原生 sprite 名**一致，可用 `--list` 或 `slices` 命令核对）；③ 该图未被捕获（`--deploy` 里 `slices` 显示条数为 0 说明文件没被读到） |
| 写了 `border` 但观感没变 | `border` 必须 4 个值（`左,上,右,下`）；只有 3 个会被整条忽略 |
| 素材看着还是被压扁 | 看日志 `⚠ border 放不下会被压缩`：把 border 调小（或 `scale` 调大），或预览里换一档尺寸验证 |
| 想把某张图彻底去掉 | 该图写 `mode=none`（退回纯色）；**删掉整个条目**则是"回到自动选材"，语义不同 |
| 想恢复默认 | 删掉文件 / 删掉对应条目；或工具里「重置为作者值」 |
| 定义/标记了但没效果 | 该图**未被捕获**（主菜单场景里没用到它）：`tags` 命令会标 `✗未捕获`，`sliceprobe` 会报 FAIL。只能选场景里实际用到的图，或让它在主菜单出现一次 |
| 标了 `button` 后小按钮变得很难看 | tag 是“钦定”：打了标记就会在所有尺寸采用它（不再按尺寸换图）。换 border 小的素材，或改用 `button.primary`/`button.danger`/`button.secondary` 分风格 |
| 中心是空的/被填住 | `center` 字段：`1` 画中心（底层纯色填住透明处）、`0` 只画四边与中心 | 
| 中文路径/编码 | ini 必须 UTF-8（无 BOM）；工具写出的即为 UTF-8，游戏端用 `UTF8Encoding(false)` 读 |
| 列表里一堆没切过的图 | 旧版**浏览即写入**的遗留（已修）；用「只看已定义」+ `Del` 一键剔，或 CLI `--remove-match` |
| `border` 比图还大 | 拖线到图外/手改 ini 造成；现工具已钳制，预览对越界值报 ⚠，重拖一次即可 |
| 预览里线不在图上/位置怪 | 已修（左/上切线漏加居中偏移）；若仍见异常，`--selftest` 的 GUI 断言会直接 FAIL，把这行贴给我 |

---

## 七、已知边界与待办

- 工具只做**切割线**（border/scale/mode/center），不改图、不重打包；素材来源仍是 `extract_ui_sprites.py` 的导出结果。
- 名字对齐靠"PNG 文件名 → manifest 原名"反查（文件名做过非法字符替换）；极端重名/被替换字符重叠时需人工确认。
- `tiled` 模式的平铺相位与 Unity 的 `Image` 是否逐像素一致未逐像素比对（仅做定性预览）。
- 未做：批量"一键把所有框类图按某档尺寸自动反推 border"（自动反推容易切坏装饰，仍建议人工定）。

### 7.1 搞置项：9-slice 的 `pixelsPerUnitMultiplier` 该用多少（2026-09-13）

**现象**：把 `UiTheme.SpritePpuMul` 全局设为 `2.5`（照拄原生）后，**尺寸正确了**，但**切片观感不对**：
大面板（1180x720）上 `UI Box Castile` 的边框只剩 `18/34/18/16 ÷ 2.5 = 7/13/7/6` px，装饰框显得过细、看不出原作者的角饰。

**已知事实**（`uiscale` 实测）：
- 游戏原生用 `UI Box Castile` 做底的 Image （尺寸 250x38 / 178x40 / 186x64）**全是 `ppuMul = 2.5`**；
- 我方装饰层现在是 `type=Sliced ppuMul=2.5`，素材名带 `@边框`（证明确实用了切片定义）。

**待定方案**（下次接着做）：
1. 把 `ppuMul` 做成**切片定义字段**（`ppumul=`，库/工具同步），逐图逐用途调（大面板=1，小按钮=2.5）；
2. 或**按元素尺寸选** `ppuMul`（如 `mul = max(1, borderSum / (尺寸 - 留白))`）；
3. 或保持 `ppuMul=1` + 用 `scale` 烤入更细边框（现有手段，但会改变切割线位置）。
先用 `nativemenu` 把“原生大面板到底用多少 ppuMul”量清楚再定。**已量完（2026-09-13）**：
原生是**按用途让“渲染后的边框”落在 ≈7~14px**（`UI Box Castile`@2.5 → 边框 7/13.6；`SGRounded`@4.74 → ~10；
`Boxed Corners`@1 → 26），**不是全局一个值**；完整表见 `docs/UI_KIT.md` §7.4。
→ 下次接着做时直接按“目标边框像素”反推：`ppuMul = max(1, 素材最大边框 / 目标像素)`（目标像素随元素尺寸 8~14）。
- 待办：D 端（MelonLoader 原生）跑一遍同样的 `sliceprobe` 回归；把 GUI 的"导出对比图（原图 vs 应用定义）"做成默认动作。
