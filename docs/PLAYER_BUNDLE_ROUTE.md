# player.bundle 技术路径打通实录（2026-08-15）

> 目标：让联机模组的玩家化身使用 **player.bundle（AssetBundle）+ Unity Animator 引擎**
> 渲染真正的 3D 模型 + 动画（Humanoid 重定向、动画混合全部交给引擎），
> 取代此前 glb + CSV 自采样动画路线（该路线因 Humanoid 重定向空间与 fbx 原始空间
> 结构性不匹配而反复失败：T-pose 悬浮、蒙皮横躺/压扁）。
>
> 本文档记录从"结论不可行"到"彻底打通"的完整调查与最终方案，供后续维护参考。

---

## 1. 背景与结论速览

**游戏**：Iron Nest: Heavy Turret Simulator（Steam 2950790，Unity 6000.3.21f1，**IL2CPP**，URP）。
游戏自身**不用** AssetBundle/Addressables/Resources 加载资源（Assembly-CSharp 无相关类），
只用场景内序列化资源 → IL2CPP 对 AssetBundle 的加载 API 采用**保守裁剪**。

**最终结论（一句话）**：
> `AssetBundle.LoadFromStream(Il2CppSystem.IO.FileStream)` 是该游戏 IL2CPP **唯一可用**的
> AssetBundle 加载入口。**关键两点**：参数必须是 `Il2CppSystem.IO.Stream` 对象指针
> （绕过 span/byte[] 裁剪），且 **FileStream 必须保持打开、绝不 Dispose**
> （bundle 延迟加载会回调 `ManagedStreamSeek` 重读 stream）。

**双端实证（BepInEx host + MelonLoader client）**：
- ✅ `player.bundle` 加载成功（`bundle='player.bundle'` → `prefab='Player'`）
- ✅ `ResolveProvider: using AnimatorAvatarVisualProvider`
- ✅ `CreateAvatar: provider=AnimatorAvatarVisualProvider pid=0/1`（两端互相看到对方化身）
- ✅ 进程稳定存活，无崩溃、无 `Mismatched serialization`

---

## 1.1 打通后的三个坑（都踩过，已解决）

### ① 动画 clip "看似空" 的误区（打包后编辑器曲线清空是正常的）
用 UnityPy 检查 bundle 里 AnimationClip 的 `m_FloatCurves`/`m_RotationCurves` **全空**，会误判"clip 空"。
实际上 **Unity 打包后动画曲线编译进 `m_MuscleClip`（肌肉曲线）**，编辑器曲线字段被清空是正常的。
判断依据：
- `m_MuscleClipSize`：空 clip=4096，有动画=5K~58K
- **运行时** `anim.GetCurrentAnimatorStateInfo(0).length`：空=∞，有动画=8.33s（250帧@30fps）
- 用户需在打包源确认动画 **Loop Time** 勾选（否则动画播一次停 = 看起来"没动画"）

### ② 材质不可见/无贴图（打包侧 + 运行时两层）
- **打包侧**：GermanWW2Soldier.fbx **无材质节点**（Material/Texture 计数=0），打包源虽有
  `PlayerMat.mat`+`German_Black_3_fix.png` 但模型没赋材质 → bundle 里是 `Default-Material`（无纹理）。
  修复：Unity 里给模型网格赋 `PlayerMat`（shader 用 **URP/Lit**，`_BaseMap` 引纹理）再打包。
- **运行时**（即使打包对）：Standard shader 在 URP 下不渲染 → 必须换成 `Shader.Find("Universal Render Pipeline/Lit")`，
  且 **Standard 的 `_MainTex` 要迁移到 URP/Lit 的 `_BaseMap`**，否则换 shader 后贴图不显示。
- 运行时诊断：`renderer.sharedMaterial.shader.name` + `GetTexture("_MainTex")` 确认。

### ③ 巨量掉帧（Update 每帧重调用）
IL2CPP 下 `Camera.main`（内部 FindObjectOfType）、`GetComponent`、`transform.Find`、
`Physics.Raycast`（脚贴地）每帧调用极慢 → 掉帧。修复：**全部缓存** +
**GroundModel 降频 0.15s** + **移除诊断刷屏**（见 §3.5）。

---

## 2. 四条路径的调查与失败原因

| # | 路径 | 结果 | 根因 |
|---|------|------|------|
| A | 托管 `AssetBundle.LoadFromFile(string)` | ❌ | C# 包装内部用 `ReadOnlySpan`，该游戏 Il2Cpp 缺 `GetPinnableReference()` |
| B | 托管 `AssetBundle.LoadFromMemory(byte[])` | ❌ | interop 签名要 `Il2CppStructArray<byte>`；托管 `byte[]` 封送 → `ObjectCollectedException` |
| C | 原生 icall 直调（`LoadFromMemory_Internal_Injected` → request → `get_assetBundle_Injected`） | ❌ 崩游戏 | 该游戏 icall 调用约定特殊；`il2cpp_runtime_invoke`/字段读取取 bundle 全部崩 |
| D | UnityWebRequest（`DownloadHandlerAssetBundle` + `SendWebRequest`） | ❌ | 发送链路 icall（`SendWebRequest`/`BeginWebRequest`）**未注册**，`get_result_Injected` 永远 0=InProgress，8 秒超时请求不推进 |
| **E** | **`AssetBundle.LoadFromStream(Il2CppSystem.IO.Stream)`** | **✅ 成功** | **参数是 Il2Cpp 对象指针，不经过被裁的 span/BindingsMarshaller 机制** |

### 2.1 关键诊断手段

不要轻信"API 被剥离"的猜测，全部用运行时/静态证据验证：

- **运行时枚举类方法**：`il2cpp_class_get_methods` → 确认 AssetBundle 类运行时仅剩
  `LoadAsset` 系列 4 个方法（`LoadFromFile/LoadFromMemory/Async` 全被裁）。
- **icall 注册检查**：`il2cpp_resolve_icall("...")` → `LoadFromStreamInternal_Injected` /
  `LoadFromStreamAsyncInternal_Injected` **已注册**（说明加载 icall 保留）。
- **静态反汇编**：`tools/disasm_icall.py`（capstone）反汇编 `UnityPlayer.dll`/`GameAssembly.dll`
  指定 RVA 确认 icall 签名。注意 icall 地址可能在任何模块，必须同运行打印模块基址算 RVA。
- **interop 类型清单**：`ilspycmd -l c` 列类型 → 发现 `Il2CppSystem.IO.FileStream` 在
  `Il2Cppmscorlib.dll` 中**完整存在**（这才是能构造 Stream 参数的关键）。

### 2.2 为什么前三路失败而 LoadFromStream 通

```
托管 API（A/B）：ReadOnlySpan / Il2CppStructArray 封送 → 被裁/封送失败
原生直调（C）：   icall 调用约定特殊，取 AssetBundle 对象崩游戏
UnityWebRequest（D）：发送 icall 被裁，请求无法启动
─────────────────────────────────────────────────────
LoadFromStream（E）：
  AssetBundle.LoadFromStream(Il2CppSystem.IO.Stream stream)
                                └─ 参数就是 Il2Cpp 托管对象引用
                                   （Il2CppInterop 直接传对象指针，无需 span/byte[] 封送）
```

---

## 3. 最终实现（代码要点）

文件：`src/OpenNestCoop/GameSync/AnimatorAvatarVisualProvider.cs`

### 3.1 加载（TryLoad）

```csharp
// 字段：进程内缓存，会话期间保持
private static GameObject _prefab;
private static bool _tried;
// 当前通过 AssetBundleIron 管理（受管句柄 + 全局引用计数 + 保持 stream 打开）：
private static OpenNestCore.Assets.AssetBundleIron _bundleHandle;

public bool TryLoad()
{
    if (!Enabled) return false;
    if (_tried) return _prefab != null;
    _tried = true;
    try
    {
        string path = FindBundlePath();   // env ONC_BUNDLE → 游戏根 Models/ → 游戏根 → 插件目录
        if (path == null) return false;

        string full = Path.GetFullPath(path);
        // ✅ 唯一可用入口封装在 AssetBundleIron：LoadFromStream(Il2CppSystem.IO.FileStream)
        //    （参数是 Il2Cpp 对象指针，绕过 span 裁剪；内部保持 FileStream 打开，会话期间不卸载）
        _bundleHandle = OpenNestCore.Assets.AssetBundleIron.Load(full);
        if (_bundleHandle != null && _bundleHandle.IsValid)
        {
            _prefab = _bundleHandle.LoadPrefab("Player", "PlayerPrefab", "Soldier", "Avatar", "player");
            if (_prefab != null) return true;
        }
        return false;
    }
    catch (Exception ex) { /* 日志后回退 false */ return false; }
}
```

### 3.2 必须注意的坑

> 现状（2026-08-15 后）：`TryLoad` 已改走 `OpenNestCore.Assets.AssetBundleIron`
> （受管句柄 + 全局引用计数 + 保持 FileStream 打开的封装），下述坑由 `AssetBundleIron`
> 统一处理，模组侧无需手写 FileStream（见 `OPEN_NEST_CORE.md` §五）。

1. **`Il2CppSystem.IO.FileStream` 构造器需 4 参**：`(string, FileMode, FileAccess, FileShare)`。
   3 参的构造器**不存在**（`FileStream(string, FileMode, FileAccess)` 编译不过）。
2. **FileStream 不实现 `System.IDisposable`**（Il2Cpp 系统类），不能 `using`，需手动 `Dispose`。
3. **绝不 Dispose stream**：bundle 延迟加载。若在 `LoadAsset`/`Instantiate` 前关闭 stream，
   Player.log 会出现：
   ```
   Mismatched serialization in the builtin class 'AnimationClip'. (Read 540 bytes but expected 25608 bytes)
   ArgumentException: ManagedStream object must be readable (stream.CanRead must return true)
   The file 'archive:/CAB-...' is corrupted! Remove it and launch unity again!
   [Position out of bounds!]        ← 最终崩溃
   ```
4. **引用**：需要 `Il2Cppmscorlib.dll`（`Il2CppSystem.IO.FileStream`）与
   `UnityEngine.AssetBundleModule.dll`，csproj 均已配置。

### 3.3 Prefab 提取与 Animator 驱动

```csharp
private static GameObject LoadPrefab(AssetBundle bundle)
{
    // 尝试常用名，兜底 LoadAllAssets<GameObject>
    foreach (var name in new[] { "Player", "PlayerPrefab", "Soldier", "Avatar", "player" })
    {
        var obj = bundle.LoadAsset<GameObject>(name);
        if (obj != null) return obj;
    }
    ...
}
```

- 创建：`UnityEngine.Object.Instantiate(_prefab, root, false)`，取 `Animator`，禁用根运动、
  `AlwaysAnimate` 离屏保持动画。
- 驱动：AnimatorController 参数约定（`Speed/Sprinting/Crouched/Airborne/Moving/Strafe/MoveFwd/HeadPitch`），
  `SetFloat`/`SetBool` 前先查 `anim.parameters` 收集到的参数集合，不存在的参数自动跳过。

### 3.4 PlayerSync / PlayerSyncV2 集成

V1 `PlayerSync.ResolveProvider` 与 V2 `PlayerSyncV2.ResolveProvider` 共用同一选择逻辑（`animatorBundleEnabled = true`）：
- 注册的 `IPlayerVisualProvider` 始终优先；`ONC_PROVIDER` 环境变量可选（soldier/cat/humanoid）。
- 正常联机模式：`AnimatorAvatarVisualProvider.TryLoad()` 成功 → 用 AssetBundle 化身（Unity Animator 真动画）；
- 失败 → 回退 `ExternalModelProvider`（glb 自采样）→ `CatCrewVisualProvider`（克隆猫船员）→ 兜底 `HumanoidVisualProvider`。

---

## 4. 调试定位技巧（本轮用到的）

| 问题 | 手段 |
|------|------|
| 进程启动即崩，无托管日志 | 看 `%LOCALAPPDATA%\LocalLow\Iron Nest\Iron Nest Heavy Turret Simulator\Player.log` |
| 崩在托管 try/catch 捕不到 | native 访问冲突是进程级崩溃，用 Player.log 的 `Mismatched serialization`/`corrupted` 定位 |
| 确认是哪个加载器重复注入 | G 端日志混入 `MelonLoader` Error（`CoopBehaviour already injected`）→ 检查 G 端 `MLLoader\Mods` 误部署的 MelonMod.dll |
| 确认各 API 是否真被裁 | `il2cpp_class_get_methods` / `il2cpp_resolve_icall` / `ilspycmd -l c` |
| 确认 icall 真实签名 | `tools/disasm_icall.py` capstone 反汇编 |

---

## 5. 双端部署注意

- **G 端（host，BepInEx 6）**：`dotnet build src\OpenNestCoop\OpenNestCoop.csproj -c Release -p:DeployToGame=true`
  → 部署到 `BepInEx\plugins`。
- **D 端（client，MelonLoader）**：构建 `src\OpenNestCoop.MelonMod\OpenNestCoop.MelonMod.csproj -c Release -p:DeployToMods=true`
  → ⚠️ 但 `MLBase=$(GameDir)\MLLoader` 会部署到 **G 端** `MLLoader\Mods`（不是 D 端！）。
  **D 端需手动复制** `bin\Release\net6.0\OpenNestCoop.MelonMod.dll` 到 `D:\...\Mods\`。
- G 端 MLLoader 目录有用户侧载模组（如 `IronNestFCS.CustomRecords.dll`），**不要动**。

---

## 6. 后续可优化方向

- [ ] 动画参数细分（跑步/走路分速、上下坡、受击反应）——扩展 AnimatorController + 参数映射
- [ ] 多皮肤/多 prefab 支持（`Models/*.bundle` 按玩家选择加载）
- [x] bundle 内存管理 —— 已由 `AssetBundleIron` 全局引用计数 + `CoopRuntime.Shutdown` → `UnloadAll()` 托管；
      会话结束（回大厅）不卸载、跨会话复用（见 `OPEN_NEST_CORE.md` §五）
- [ ] 远程玩家姿态平滑（当前由 PlayerSync 插值 + Animator 参数驱动）
