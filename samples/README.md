# Sample mod — adding a native-looking page from your own mod

A complete, buildable third-party mod: it declares a menu, the Open Nest UIKit host renders it
with the game's own widgets. Full contract reference: [`../docs/UI_KIT_API.md`](../docs/UI_KIT_API.md)
(in the public repository the same document is `docs/API.md`).

```
samples/
├── Shared/                  # loader-independent: this is the part you copy into your mod
│   ├── SampleMenu.cs         #   the provider (pages, rows, callbacks)
│   └── SampleConfig.cs       #   the settings it writes back into
├── BepInEx/                 # BepInEx 6 (IL2CPP) entry point + csproj
└── MelonLoader/             # MelonLoader 0.7.3 entry point + csproj
```

The provider file references **only** `OpenNestUIKit.API` — no Unity, no loader, no reference to
the mod itself. That is why both shells can compile the very same source: on the two loaders the
only difference is the entry point.

## What it shows

| Page | Demonstrates |
|---|---|
| root | labels, sub-page entries (`Nav`), a toggle whose change triggers `UiKitHost.Refresh()` |
| General | slider, dropdown, text field, action button — and a row that only exists while the feature is disabled |
| Keys | key binding plus a label that always shows the current binding |
| Live data | an embedded scrolling `List` rebuilt on demand, with the `IsTextInputFocused` guard |
| Two columns | a `Columns` layout: fixed-width list on the left, details on the right |
| About | host diagnostics (`IsHostAvailable`, `HostVersion`, `ApiVersion`, `ProviderCount`, `CurrentPageId`) |

## Build

Point the build at a game that already has the loaders installed (the same switch the mod uses):

```powershell
# BepInEx shell  (-> Binaries, and into <Game>/BepInEx/plugins with -p:DeployToGame=true)
dotnet build samples\BepInEx\OpenNestUIKit.Sample.BepInEx.csproj -c Release `
  -p:GameDir="C:\path\to\game" -p:DeployToGame=true

# MelonLoader shell  (-> into <Game>/Mods with -p:DeployToMods=true)
dotnet build samples\MelonLoader\OpenNestUIKit.Sample.MelonLoader.csproj -c Release `
  -p:GameDir="C:\path\to\game" -p:ClientGame="C:\path\to\game" -p:DeployToMods=true
```

## Run

1. Install **Open Nest UIKit** (which brings `OpenNestUIKit.API.dll` with it) —
   `BepInEx/plugins/` on BepInEx, `Mods/` + `UserLibs/` on MelonLoader.
2. Install the sample dll the same way.
3. Start the game and press **ESC** — the injected **Sample Mod** row opens the sample;
   it is also reachable inside the UIKit menu. Its pages then appear with native widgets,
   the same shapes the game's own settings screen uses.

## Don't ship the API dll

The sample references the contract with `Private="false"` on purpose: Open Nest UIKit already
ships `OpenNestUIKit.API.dll`, and a second copy in `plugins/` is a duplicate-assembly hazard.
Install the UI library (or don't — your mod must keep working without it) and reference the
contract only.

## Making it yours

1. Copy `Shared/SampleMenu.cs` and `Shared/SampleConfig.cs` into your mod.
2. Change `ModId`, `DisplayName` and the row keys/labels.
3. Replace `SampleConfig` with your own settings object — the host never stores anything for you.
4. Reference `OpenNestUIKit.API` (assembly or project) with `Private="false"`.
5. Register once at startup: `UiKitHost.Register(new YourMenu(yourConfig));`

That's the whole integration. If the UI library is missing, registration is a quiet no-op and
`UiKitHost.IsHostAvailable` tells you so.
