# Vendor —— 拷入的源码副本（独立命名空间）

本目录的代码**不是**本模组原创，而是从仓库内其它模组**拷贝**过来、并做了一处唯一改动：**改命名空间**。

## 为什么用拷贝而不是引用

`OpenNestModMenu` 与 `OpenNestCoop` / `OpenNestCore` 是**相互独立的模组**（互不引用程序集）。
但 Core 的 UI / 日志基建是公开可复用的（见 `docs/NATIVE_UI.md`），所以按"拷贝 + 独立命名空间"复用：

- 好处：本模组可单独分发、版本互不牵连；也与 OpenNestCoop **不共享静态状态**
  （`NativeUi` 注册表 / `CoopLog` 门面各是各的 CLR 类型，见 `docs/MOD_MENU.md` §1.2）。
- 代价：上游修 bug 必须手动同步（见下表 + 同步流程）。

## 清单（上游 → 本目录）

| 本目录文件 | 上游源码 | 命名空间改动 |
|---|---|---|
| `Logging/ILogger.cs` | `src/OpenNestCore/Logging/ILogger.cs` | `OpenNestCore.Logging` → `OpenNestModMenu.Logging` |
| `Logging/CoopLog.cs` | `src/OpenNestCore/Logging/CoopLog.cs` | 同上 |
| `Logging/ModLog.cs` | `src/OpenNestCore/Logging/ModLog.cs` | 同上 |
| `UI/INativeUiService.cs` | `src/OpenNestCore/UI/INativeUiService.cs` | `OpenNestCore.UI` → `OpenNestModMenu.UI` |
| `UI/UiKit.cs` | `src/OpenNestCore/UI/UiKit.cs` | 同上 |
| `Diagnostics/FrameProfiler.cs` | `src/OpenNestCore/FrameProfiler.cs` | `OpenNestCoop.Core` → `OpenNestModMenu.Diagnostics` |

**未拷贝**（有上游依赖，等用到再说）：

- `UiSpriteBank.cs`：依赖 `OpenNestCore.Assets.AssetBundleIron`。v1 不激活原生素材（`UseNativeSkin=false`），
  要启用时需一并拷 `Assets/` 或裁掉 bundle 路线（详见 `docs/MOD_MENU.md` §5.3）。

## 同步流程（上游改了怎么办）

1. 逐个对照上表，把上游文件内容覆盖到本目录对应文件；
2. **只改 `namespace`（以及文件头注释）**，其余保持逐字一致；
3. `dotnet build` 双端验证 0 error；
4. 在本文件下方记录同步日期与上游提交（便于下次增量比对）。

## 同步记录

- 2026-09-12 首次拷入（上游 = 本仓库 main，同期 `CoopLog`/`ModLog`/`FrameProfiler`/`UiKit`/`NativeUi` /`INativeUiService`）。
