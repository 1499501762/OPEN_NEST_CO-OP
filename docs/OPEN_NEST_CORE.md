# OpenNestCore — 通用工具库 API

> OpenNest 联机模组抽出的**平台无关通用能力**，供其他 Iron Nest: Heavy Turret Simulator 模组开发者引用。
> 项目：`src/OpenNestCore/`（`OpenNestCore.csproj`，net6.0，仅依赖 UnityEngine interop + Il2CppInterop，**不依赖游戏 Assembly-CSharp**）。

## 一、为什么用 OpenNestCore

OpenNestCoop 联机模组在开发中沉淀了三类可复用能力，已抽象为独立库：

| 能力 | 命名空间 | 解决的问题 |
|---|---|---|
| 日志门面 | `OpenNestCore.Logging` | FPS 刷屏 / 日志字符串拼接开销 |
| 化身扩展 API | `OpenNestCore.Avatar` | 自定义联机玩家模型 / 骨架 / 动画 |
| AssetBundle 工具 | `OpenNestCore.Assets` | IL2CPP 下加载 AssetBundle 的封装 |

**定位**：纯通用工具库，**不包含联机核心**（网络/同步/会话在 OpenNestCoop 运行时里）。任何 Unity IL2CPP 模组（即使不做联机）都能用。

**架构模式**：OpenNestCoop / OpenNestCoop.MelonMod 通过**链接源码**使用（`<Compile Include="..\OpenNestCore\**\*.cs" />`，避免 BepInEx/MelonLoader 两套 interop DLL 兼容问题）；第三方模组可直接引用构建出的 `OpenNestCore.dll`。

## 二、引用方式

```xml
<!-- 第三方模组：直接引用 dll（先构建 OpenNestCore） -->
<Reference Include="OpenNestCore" HintPath="path\to\OpenNestCore.dll" />
```

OpenNestCore 只依赖 UnityEngine interop（CoreModule/AssetBundleModule）+ Il2CppInterop.Runtime + Il2CppSystem，**不含游戏类型**，跨游戏场景可用性取决于你引用的 interop。

> ⚠️ **双平台 dll（0.1.0 起）**：BepInEx 与 MelonLoader 的 `Il2CppInterop.Runtime` 版本不同（BepInEx 1.5.3 / MelonLoader 1.5.1），直接引用 dll 时**必须按加载器平台选择对应构建**：
> - `release/OpenNestCore-0.1.0-BepInEx.zip` — 按 BepInEx interop（1.5.3）编译
> - `release/OpenNestCore-0.1.0-MelonLoader.zip` — 按 MelonLoader interop（1.5.1）编译
>
> 若你的模组同时面向两平台，**仍推荐链接源码**（零 interop 兼容风险，最稳妥）。

## 三、`OpenNestCore.Logging` — 日志门面

### `CoopLog`（静态类）

等级过滤（Debug 默认关闭，关闭时消息 **Func 惰性求值零拼接**）+ 按 key 节流。

```csharp
using OpenNestCore.Logging;

// 1) 启动时注入平台日志后端（一次）
CoopLog.SetLogSource(myLogger);   // myLogger 实现 OpenNestCore.Logging.ILogger

// 2) 打日志：key 用于节流标识；intervalSec > 0 时同 key 该秒数内只打 1 条
CoopLog.Info("myMod.init", () => $"initialized in {sw.ElapsedMilliseconds}ms");
CoopLog.Debug("myMod.detail", () => $"pos={pos}", 0.5f); // Debug 默认关闭，零开销
CoopLog.Warn("myMod.load", () => "bundle load failed");
CoopLog.Error("myMod.crash", () => ex.ToString());

// 3) 运行时调等级（默认 Info，Debug 构建全开）
CoopLog.Level = LogLevel.Debug;
```

### `ILogger` / `LoggerExtensions`

```csharp
public interface ILogger {
    void Info(string message); void Warn(string message);
    void Error(string message); void Debug(string message);
}
// LoggerExtensions：logger.LogInfo("...") / LogWarning / LogError / LogDebug（BepInEx 风格）
```

**接入平台**：
- BepInEx：`CoopLog.SetLogSource(new BepInExManualLogSource(plugin.Log))`（实现 ILogger 包装 ManualLogSource）
- MelonLoader：`CoopLog.SetLogSource(new MelonLoggerAdapter(MelonLogger.Instance))`

## 四、`OpenNestCore.Avatar` — 化身扩展 API

联机运行时（OpenNestCoop）的 `PlayerSync` 负责远端玩家位置/朝向同步 + 插值；**谁渲染这个玩家**由 `IPlayerVisualProvider` 决定。别的模组可注册自定义模型。

```csharp
using OpenNestCore.Avatar;

// 1) 实现提供者
public class MyAvatar : IPlayerVisualProvider
{
    public GameObject Create(Transform root, string playerName, Color tint) { /* 实例化你的模型到 root 下，返回视觉根 */ }
    public void Update(GameObject visual, float dt, ref AvatarPose pose)    { /* 驱动动作/动画（见 AvatarPose） */ }
    public void Destroy(GameObject visual)                                  { /* 清理资源 */ }
}

// 2) 注册（模组加载时调用一次，覆盖默认）
PlayerVisualRegistry.Register(new MyAvatar());

// 3) 恢复默认
PlayerVisualRegistry.Register(null);
```

### `AvatarPose`（每帧传入 Update 的意图状态）

```csharp
struct AvatarPose {
    Vector3 Position; float Yaw; float Speed; bool Moving;
    CrewRole Role; PlayerAction Action; int DeviceId;   // 设备/任务动作
    float MoveFwd; float MoveStrafe;                     // 本地空间速度分量（横移姿态）
    bool Airborne; bool Crouched; bool Sprinting; float Pitch;  // 姿态/俯仰（头转向）
}
```

### `PlayerAction`（驱动动画状态机）

`Idle / Moving / Reloading / LoadingShell / AdjustingElevation / OperatingDevice / Custom(255)`。

### `CrewRole`

`None / Commander / Gunner / Loader / FireControl`（炮组分工，驱动角色动画）。

> 传输层只传"意图"，具体骨骼/动画由提供者本地映射（两端同动画资源时可预测播放）。如需**精确同步 Animator 参数/骨骼变换**，请用 OpenNestCoop 的同步注册表（见 OpenNestCoop API.md）注册为值绑定。

## 五、`OpenNestCore.Assets` — AssetBundle IL2CPP 工具（`AssetBundleIron`，受管句柄 + 生命周期）

### `AssetBundleIron`（对外主类，模仿原生 AssetBundle API 语感）

IL2CPP 踩坑经验封装：该游戏托管 `LoadFromFile`/`LoadFromMemory` 被裁剪，仅 `LoadFromStream(Il2CppSystem.IO.Stream)` 可用（对象指针绕过 span 裁剪）；打包材质 shader 为空需修复。**带完整生命周期管理**（全局缓存引用计数 + 受管句柄），解决 FileStream native 句柄被提前关闭导致的其他模组崩溃问题。默认**不公开原生对象**（安全）；确有需要时用 `GetUnsafeRawBundle()`（风险自负）。

```csharp
using OpenNestCore.Assets;

// 1) 静态入口：加载或引用+1（同路径已加载则共享同一句柄 + FileStream）
var h = AssetBundleIron.Load(@"G:\...\Models\player.bundle");
if (h == null) { /* 加载失败 */ }

// 2) 实例方法：加载预置（按候选名逐一尝试，兜底取第一个 GameObject）
var prefab = h.LoadPrefab("Player", "Soldier", "Avatar");

// 3) 实例化后修复材质（打包 shader 为空/Standard → URP/Lit + _MainTex→_BaseMap 迁移）
var go = Object.Instantiate(prefab);
AssetBundleIron.RepairMaterials(go);

// 4) 用完释放（先销毁所有由此 bundle 实例化的 GameObject！）
Object.Destroy(go);
h.Dispose();
```

### API

**静态**：
- `AssetBundleIron Load(string fullPath)` — 加载或引用+1（`null`=失败）。**持有期间禁止 Dispose 内部 bundle/stream**
- `void UnloadAll()` — 卸载全部（**游戏退出 / 宿主模组卸载前**调用；此时所有消费方必须已释放）
- `void RepairMaterials(GameObject go)` — URP shader 替换 + 纹理迁移（`_MainTex`→`_BaseMap`）
- `int LoadedCount` — 已加载 bundle 数（诊断）

**实例（模仿原生 AssetBundle API 语感）**：
- `void Dispose()` — 引用-1；**归零才真正卸载**（还有引用保护其他持有方）
- `GameObject LoadPrefab(params string[] names)` — 兜底取第一个 GameObject
- 同步代理：`T LoadAsset<T>(string)` / `T[] LoadAllAssets<T>()` / `T[] LoadAssetWithSubAssets<T>(string)`
- 异步代理：`AssetBundleRequest LoadAssetAsync<T>(string)` / `LoadAllAssetsAsync<T>()` / `LoadAssetWithSubAssetsAsync<T>(string)` ⚠️ 该游戏 IL2CPP 下可能被裁剪（同 LoadFromFile 批次），需 try-catch 或改用同步 API
- 查询：`bool Contains(string)` / `string[] GetAllAssetNames()` / `string[] GetAllScenePaths()`
- 属性：`bool isStreamedSceneAssetBundle`
- 只读：`bool IsValid`（未 Unload）/ `int Ref`（引用计数）/ `string Name`（安全只读名字）
- ⚠️ **逃逸**：`AssetBundle GetUnsafeRawBundle()` — 返回底层原生 bundle（绕过托管生命周期）。**调用此方法之后产生的崩溃不在项目支持范围**

### 生命周期与使用模式（推荐给模组作者）

`AssetBundleIron` 用全局缓存引用计数**内部自动管理**生命周期：同路径共享（`Load` 去重）、`Dispose` 引用归零时才按安全顺序自动卸载（`Unload(false)` → 关闭 FileStream）；游戏退出/模组卸载由宿主调用 `UnloadAll()`（OpenNestCoop 已在 `CoopRuntime.Shutdown` 中调用）。

**模式一：短生命周期 bundle（绝大多数模组场景）**
加载 bundle → Instantiate → 使用完毕 **必须先 Destroy 所有生成物体** → `Dispose`。
适合一次性加载模型、特效。

**模式二：常驻 bundle（不 Dispose，直到游戏退出）**
模组需要这个 bundle 资源全程可用：**调用 `Load` 之后永不调用 Dispose**，等到游戏退出统一调用 `AssetBundleIron.UnloadAll()`。

> 路径查找（如 `Models/player.bundle` 在哪）属各模组自己的配置，不在此工具内。

## 六、版本与兼容

- `Version 0.1.0`（2026-08-15）
- 平台无关 + 无游戏类型依赖（`OpenNestCore` 不引用 Assembly-CSharp）
- 双平台：BepInEx / MelonLoader 均通过源码链接使用；第三方引用 dll 时按平台选用对应包（`OpenNestCore-0.1.0-BepInEx.zip` / `OpenNestCore-0.1.0-MelonLoader.zip`）

## 七、源码结构

```
src/OpenNestCore/
├─ OpenNestCore.csproj
├─ Logging/ILogger.cs            （ILogger + LoggerExtensions）
├─ Logging/CoopLog.cs            （等级过滤 + 节流 + SetLogSource）
├─ Avatar/CrewRole.cs
├─ Avatar/IPlayerVisualProvider.cs（IPlayerVisualProvider + PlayerVisualRegistry + AvatarPose + PlayerAction）
└─ Assets/AssetBundleIron.cs（AssetBundleIron：Load/Dispose/代理 API/GetUnsafeRawBundle/RepairMaterials）
```
