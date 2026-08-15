# OpenNestCore — Shared Utility Library API

> Platform-agnostic utilities extracted from the OpenNest co-op mod, for developers of other **Iron Nest: Heavy Turret Simulator** mods.
> Project: `src/OpenNestCore/` (`OpenNestCore.csproj`, net6.0). Depends only on UnityEngine interop + Il2CppInterop + Il2CppSystem — **no dependency on the game's `Assembly-CSharp`**.

## 1. Why OpenNestCore

The OpenNestCoop co-op mod accumulated three reusable capabilities, now extracted into a standalone library:

| Capability | Namespace | Solves |
|---|---|---|
| Logging facade | `OpenNestCore.Logging` | FPS spam / log string-formatting overhead |
| Avatar extension API | `OpenNestCore.Avatar` | Custom online player models / skeletons / animations |
| AssetBundle tooling | `OpenNestCore.Assets` | Loading AssetBundles under IL2CPP |

**Positioning**: a pure utility library — it does **not** include the co-op networking core (network/sync/session live in the OpenNestCoop runtime). Any Unity IL2CPP mod (even non-co-op) can use it.

**Architecture pattern**: OpenNestCoop / OpenNestCoop.MelonMod consume it by **linking the source** (`<Compile Include="..\OpenNestCore\**\*.cs" />`, avoiding the BepInEx/MelonLoader interop-DLL compatibility issue); third-party mods can reference the built `OpenNestCore.dll` directly.

## 2. Referencing

```xml
<!-- Third-party mod: reference the DLL directly (build OpenNestCore first) -->
<Reference Include="OpenNestCore" HintPath="path\to\OpenNestCore.dll" />
```

OpenNestCore depends only on UnityEngine interop (CoreModule/AssetBundleModule) + Il2CppInterop.Runtime + Il2CppSystem. It contains **no game types**; cross-game usability depends on which interop assemblies you reference.
> ⚠️ **Per-platform DLL (since 0.1.0)**: BepInEx and MelonLoader ship different `Il2CppInterop.Runtime` versions (BepInEx 1.5.3 / MelonLoader 1.5.1), so when referencing the DLL you **must pick the build matching your loader platform**:
> - `release/OpenNestCore-0.1.0-BepInEx.zip` — compiled against BepInEx interop (1.5.3)
> - `release/OpenNestCore-0.1.0-MelonLoader.zip` — compiled against MelonLoader interop (1.5.1)
>
> If your mod targets both platforms, **linking the source is still recommended** (zero interop-compat risk).
## 3. `OpenNestCore.Logging` — Logging facade

### `CoopLog` (static class)

Level filtering (Debug is off by default; when off, messages are evaluated lazily via `Func<string>` — **zero string building**) + per-key throttling.

```csharp
using OpenNestCore.Logging;

// 1) Inject the platform log backend once at startup
CoopLog.SetLogSource(myLogger);   // myLogger implements OpenNestCore.Logging.ILogger

// 2) Log: `key` identifies throttling; intervalSec > 0 prints at most 1 per key per period
CoopLog.Info("myMod.init", () => $"initialized in {sw.ElapsedMilliseconds}ms");
CoopLog.Debug("myMod.detail", () => $"pos={pos}", 0.5f); // Debug off by default, zero cost
CoopLog.Warn("myMod.load", () => "bundle load failed");
CoopLog.Error("myMod.crash", () => ex.ToString());

// 3) Change level at runtime (default Info; Debug builds enable everything)
CoopLog.Level = LogLevel.Debug;
```

### `ILogger` / `LoggerExtensions`

```csharp
public interface ILogger {
    void Info(string message); void Warn(string message);
    void Error(string message); void Debug(string message);
}
// LoggerExtensions: logger.LogInfo("...") / LogWarning / LogError / LogDebug (BepInEx style)
```

**Platform adapters**:
- BepInEx: `CoopLog.SetLogSource(new BepInExManualLogSource(plugin.Log))` (wrap ManualLogSource in an `ILogger`)
- MelonLoader: `CoopLog.SetLogSource(new MelonLoggerAdapter(MelonLogger.Instance))`

## 4. `OpenNestCore.Avatar` — Avatar extension API

In the co-op runtime (OpenNestCoop), `PlayerSync` synchronizes remote player position/orientation + interpolation; **who renders the player** is decided by `IPlayerVisualProvider`. Other mods can register a custom model.

```csharp
using OpenNestCore.Avatar;

// 1) Implement the provider
public class MyAvatar : IPlayerVisualProvider
{
    public GameObject Create(Transform root, string playerName, Color tint) { /* instantiate your model under root, return visual root */ }
    public void Update(GameObject visual, float dt, ref AvatarPose pose)    { /* drive actions/animation (see AvatarPose) */ }
    public void Destroy(GameObject visual)                                  { /* free resources */ }
}

// 2) Register (once at mod load, overrides the default)
PlayerVisualRegistry.Register(new MyAvatar());

// 3) Restore default
PlayerVisualRegistry.Register(null);
```

### `AvatarPose` (intent state passed into `Update` every frame)

```csharp
struct AvatarPose {
    Vector3 Position; float Yaw; float Speed; bool Moving;
    CrewRole Role; PlayerAction Action; int DeviceId;   // device/mission actions
    float MoveFwd; float MoveStrafe;                     // local-space velocity components (strafe pose)
    bool Airborne; bool Crouched; bool Sprinting; float Pitch;  // pose / camera pitch (head turn)
}
```

### `PlayerAction` (drives the animation state machine)

`Idle / Moving / Reloading / LoadingShell / AdjustingElevation / OperatingDevice / Custom(255)`.

### `CrewRole`

`None / Commander / Gunner / Loader / FireControl` (crew assignment, drives role animations).

> The transport layer only sends "intent"; the provider maps it to local bones/animations locally (predictable playback when both ends share the same animation assets). To **precisely sync Animator parameters / bone transforms**, register them as value bindings through OpenNestCoop's sync registry (see the OpenNestCoop API doc).

## 5. `OpenNestCore.Assets` — AssetBundle IL2CPP tooling (`AssetBundleIron`, managed handle + lifecycle)

### `AssetBundleIron` (public class, mimics the native AssetBundle API)

IL2CPP pitfall encapsulation: this game's managed `LoadFromFile`/`LoadFromMemory` are stripped; only `LoadFromStream(Il2CppSystem.IO.Stream)` works (object pointer bypasses span stripping); packaged material shaders are empty and must be repaired. **Full lifecycle management** (global cached reference counting + managed handles) prevents the "FileStream native handle closed early → other mods' bundles break" crash. The raw native object is **NOT exposed by default** (safe); use `GetUnsafeRawBundle()` if you must (at your own risk).

```csharp
using OpenNestCore.Assets;

// 1) Static entry: load or refcount+1 (same path → shared handle + FileStream)
var h = AssetBundleIron.Load(@"G:\...\Models\player.bundle");
if (h == null) { /* load failed */ }

// 2) Instance method: load a prefab (tries candidate names, falls back to the first GameObject)
var prefab = h.LoadPrefab("Player", "Soldier", "Avatar");

// 3) After instancing, repair materials (empty/Standard shader → URP/Lit + _MainTex→_BaseMap migration)
var go = Object.Instantiate(prefab);
AssetBundleIron.RepairMaterials(go);

// 4) Release when done (destroy all GameObjects instanced from this bundle FIRST!)
Object.Destroy(go);
h.Dispose();
```

### API

**Static**:
- `AssetBundleIron Load(string fullPath)` — loads or refcount+1 (`null` = failure). **While held, never Dispose the internal bundle/stream**
- `void UnloadAll()` — unloads everything (**call before game exit / host-mod unload**; all consumers must already be released)
- `void RepairMaterials(GameObject go)` — URP shader replacement + texture migration (`_MainTex`→`_BaseMap`)
- `int LoadedCount` — number of loaded bundles (diagnostics)

**Instance (mimics the native AssetBundle API)**:
- `void Dispose()` — refcount-1; **only unloads when it reaches zero** (protects other holders while references remain)
- `GameObject LoadPrefab(params string[] names)` — falls back to the first GameObject
- Sync proxies: `T LoadAsset<T>(string)` / `T[] LoadAllAssets<T>()` / `T[] LoadAssetWithSubAssets<T>(string)`
- Async proxies: `AssetBundleRequest LoadAssetAsync<T>(string)` / `LoadAllAssetsAsync<T>()` / `LoadAssetWithSubAssetsAsync<T>(string)` ⚠️ may be stripped under this game's IL2CPP (same batch as LoadFromFile); try-catch or fall back to the sync APIs
- Queries: `bool Contains(string)` / `string[] GetAllAssetNames()` / `string[] GetAllScenePaths()`
- Property: `bool isStreamedSceneAssetBundle`
- Read-only: `bool IsValid` (not unloaded) / `int Ref` (reference count) / `string Name` (safe read-only name)
- ⚠️ **Escape**: `AssetBundle GetUnsafeRawBundle()` — returns the raw native bundle (bypasses managed lifecycle). **Any crash after calling this method is outside the project's support scope.**

### Lifecycle & usage patterns (recommended for mod authors)

`AssetBundleIron` auto-manages the lifecycle via the internal global cached refcount: same-path sharing (`Load` dedups), and `Dispose` unloads in safe order (`Unload(false)` → close FileStream) only when the refcount reaches zero; the host calls `UnloadAll()` on game exit / mod unload (OpenNestCoop already does in `CoopRuntime.Shutdown`).

**Pattern 1 — Short-lived bundle (most mod scenarios)**
Load the bundle → Instantiate → when done, **MUST Destroy all spawned objects first** → `Dispose`.
Good for one-off models / effects.

**Pattern 2 — Persistent bundle (no Dispose until game exit)**
Keep the bundle resources available for the whole session: after `Load`, **never call Dispose**; let the host call `AssetBundleIron.UnloadAll()` on game exit.

> Path lookup (e.g. where `Models/player.bundle` lives) is each mod's own configuration and is not part of this tool.

## 6. Version & compatibility

- `Version 0.1.0` (2026-08-15)
- Platform-agnostic with no game-type dependency (`OpenNestCore` does not reference `Assembly-CSharp`)
- Both platforms: BepInEx / MelonLoader consume it by linking source; third parties referencing the DLL should pick the per-platform package (`OpenNestCore-0.1.0-BepInEx.zip` / `OpenNestCore-0.1.0-MelonLoader.zip`)

## 7. Source layout

```
src/OpenNestCore/
├─ OpenNestCore.csproj
├─ Logging/ILogger.cs             (ILogger + LoggerExtensions)
├─ Logging/CoopLog.cs             (level filtering + throttling + SetLogSource)
├─ Avatar/CrewRole.cs
├─ Avatar/IPlayerVisualProvider.cs(IPlayerVisualProvider + PlayerVisualRegistry + AvatarPose + PlayerAction)
└─ Assets/AssetBundleIron.cs(AssetBundleIron: Load/Dispose/proxies/GetUnsafeRawBundle/RepairMaterials)
```
