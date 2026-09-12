# OPEN-NEST-CO-OP-

**Multiplayer co-op mod for [Iron Nest: Heavy Turret Simulator](https://store.steampowered.com/app/2950790)** (Unity 6 / IL2CPP).

Play as a crew inside a giant fortress turret — calibrate machinery, load shells,
man the fire-control computer, brew coffee — together with friends over Steam.

- **Mode**: shared turret crew (host-authoritative), cooperative role division
- **Transport**: Steam P2P via the game's own Steamworks (relay punch-through, zero extra infra)
- **Loader**: BepInEx 6 IL2CPP **or** MelonLoader 0.7.3 (both shipped as prebuilt releases)

---

## Features

| Milestone | Status | What's synced |
|---|---|---|
| M0 | ✅ | Tech recon: stack (Unity 6 / IL2CPP), loaders (BepInEx 6 / MelonLoader), Steam APIs |
| M1 | ✅ | **Steam lobby**: create / join / browse, **room password** (host-authoritative handshake + kick), **mod version check & label** (same-version rooms listed first), P2P transport (reliable + unreliable, batched), roster, ping, chat (**native CJK IME input**), localized UGUI menu, **remembered lobby settings**, reusable **CoopInputBox** |
| M2 | ✅ | Turret rotation/elevation, gun fire, aimer input, **player avatars** (head + gas mask), **record player**, load/fire workflow (host-authoritative reload state, event-decoupled arm/cylinder/powder), map markers (add / live-drag / erase), cranks / dials / sliders, coffee machine, **cats** (host-AI soft sync + interaction events + hard sync), **typewriter** (event/state sync + per-char animation + content alignment + notification lights), **shell types**, **firing-sequence switch**, **punchcards** (sync by card ID), **requisition purchases**, **recon photo / counter-battery** (seed sync), **dynamic map entities** (alive/kill + missing-entity creation) |
| M3 | ✅ | Mission objectives / impacts / counter-battery, engine / pressure / lights — main paths done (v0.1.6+); remaining edge cases tracked in `docs/` |

Also includes:
- **Mid-join sync** — state-snapshot registry (`StateSnapshotSync`, 8+ modules) so late joiners converge
- **Dual sync engines** — V1 (default, `GameSync/`) and V2 layered (`SyncV2/`, `--sync new` beta)
- **Click-through blocking** while the coop menu is open (full-screen ray block + interaction lock)
- **Localization** (Chinese / English, auto-switches with the game)
- **Auto-join CLI + loopback mode** — unattended dual-instance testing (`--autohost/--autojoin/--local`)
- **External 3D avatar models** — SharpGLTF loader (`soldier.glb` etc.) + per-model `.cfg` fit config; ships `Models/player.bundle` as a sample
- **Custom mission framework** (`OpenNestCore.Tasks`) — missions defined via JSON or C# builders, bridged onto the game's node-graph engine, sequence-synced
- **Mod config file** (`OpenNestCoop.cfg`, standard INI, hot-reloaded in ~2s) — toggle cat sync, record-player sync, and other sync modules without rebuilding (`docs/CONFIG.md`)

## Version History

| Version | Highlights |
|---|---|
| **v0.2.1-Alpha-build** (2026-09-12) | **Shell launch-parameter sync** (`ShotSync`, dynamic channel `shotparams`): host broadcasts start/target/flight-time/path-length so both ends fly the identical trajectory; **shell origin root cause fixed** — the origin is the iron-nest map icon `Tactical Map/.../TurretLocation` (= `GunController.firePoint`), now host-authoritative synced (`NestSync` 146); **recon photo angle** derived deterministically from the synced impact point (no more per-end random roll); **typewriter** stall criterion changed to "local reveal actually advancing" (and the game's forced-complete API no longer used — it broke following prints); **client follows host back to main menu / mission select** (`LoadMainMenu` / `EnterBrowsingMap`); **critical-packet protection extended** to dynamic channels + public `RegisterCriticalType` API (fixes mod-side silent packet drops); **mod config file** (`CoopConfig`, standard INI, 2s hot-reload) with cat / record-player / calculate-button switches; new `Artillery Computer Console/Calculate Universal Button` click sync (off by default); `BlockerSync` heartbeat; F10 tool now shows per-interactable sync state |
| **v0.2.0** (2026-08-26) | **Nest (turret) position sync** (host-authoritative `MoveTurret`/`SetTurretLocation`, aligned with the game's nest coordinate) — fixes trajectory display / shell impact / `[GRID]` teleprinter output; **entity type icons**; host-authoritative entity positions; **ControlSync delta-only broadcast** (revert ControlFull override, fix start-state desync); **Locking Lever skip-full**; **MapToken nest-token start-state fix** (no broadcast of un-placed position); network governor (bandwidth/queue tiers); NetLagSim packet/bandwidth/loss sim; frame diagnostics (FrameDiagUI); OpenNestCore `RoutingLogger` / `ModLog` |
| **v0.1.9** (2026-08-23) | **Native CJK (real Chinese) input** via OS IME (`ImmGetCompositionStringW`), unified across lobby inputs + chat; reusable **CoopInputBox**; native UI kit (`OpenNestCore.UI` / `UiSpriteBank`); remembered lobby settings; **custom mission framework** (`OpenNestCore.Tasks`) |
| **v0.1.8** | Event-decoupled reload/powder/arm/cylinder sync (PowderEvent / ArmSync / CylinderActionSync), charge-count fix, Button Dispenser mis-activation fix, direction angular-momentum dual channel, .Charge Dial two-way regression, 13 docs refreshed |
| **v0.1.7** | FPS fix (EntitySync fallback throttle + ReloadSync 5s heartbeat + log throttle), missing-entity creation, reload-state desync fix (ApplySnapshot always SetState), Punchcard report dedup, Charge Dial early rescan |
| **v0.1.6** | Counter-battery stop sync, host-authoritative reload (left-gun / arm / shell-rammer lock fix), initial charge sync, dynamic entity + powder-charge mid-join, **pure-native MelonLoader 0.7.3**, F9 debug tool, missing-entity creation, EntitySync FPS fix |
| **v0.1.5** | Punchcard sync by card ID (host card-list + EnsureCards), fresh BepInEx 6.0.0-be.785 & MelonLoader 0.7.3 standalone packaging |
| **v0.1.4** | Packaging/version alignment (4 packages: BepInEx-Mod / MLL-Mod / BepInEx-Standalone / MLL-Standalone) |
| **v0.1.3** | Guest typewriter animation + type-name fix, entity-coordinate fix (use `fm.Entities`), teleprinter local-print suppression, host-authoritative firing-sequence switch, recon-photo/counter-battery seed sync, EntitySync IsAlive + kill events, purchase dedup, Rescan 30s, `Models/player.bundle` avatar sample (CC BY 4.0 credits), SyncV2 layer, OpenNestCore extraction |
| **v0.1.2** | External 3D soldier model via SharpGLTF then reverted to skeleton; `CoopLog` logging facade; control-sync performance |
| **v0.1.1** | Single-player fire fix (state guard); English-only console logs to prevent stutter on non-CJK systems |
| **v0.1.0** | Initial Steam lobby / P2P / roster / ping / chat / UGUI menu |

## Open Extension APIs

Designed to be extended by other mods:

- **`CoopSyncRegistry`** — register device state values (`RegisterFloat/Int/Bool`) or custom sync modules (`RegisterModule(ISyncedModule)`)
- **`PlayerVisualRegistry`** — inject custom player models / skeletons / animations (`IPlayerVisualProvider`)
- **`OpenNestCore.UI`** (`INativeUiService` / `UiKit` / `UiSpriteBank`) — platform-independent game-native UI capabilities (localization / notifications / ESC menu / virtual cursor / UGUI kit) usable by any mod, even non-co-op ones
- **`OpenNestCore.Tasks`** — platform-independent custom mission engine (map objectives, prerequisites/successors, fire-target sequences, events/branches/parallel/timers/rewards), defined via JSON or C# builders

See [docs/API.md](docs/API.md) for the full API reference.

## Documentation

- [API Reference](docs/API.md) — sync/extension APIs, message protocol, skeleton/animation architecture
- [Development Guide](docs/DEVELOPMENT.md) — tech stack, build/deploy, env variables, test steps
- [Lobby Guide](docs/LOBBY.md) — Steam lobby: room password, mod version check/label, handshake protocol
- [Native UI Study](docs/NATIVE_UI.md) — game UI internals + `OpenNestCore.UI` abstraction (UiKit/IronNestNativeUi)
- [Task System Study](docs/TASK_SYSTEM.md) — the game's SleepyNodes node-graph mission engine
- [Custom Mission Framework](docs/CUSTOM_MISSION.md) — `OpenNestCore.Tasks` JSON/script mission engine + bridge (含第 15 节脚本化模块、第 16 节如何测试)
- [Native Node Catalogue](docs/NODE_CATALOGUE.md) — 61 个原生任务节点（48 State_* + 13 Event_*）+ 字段速查
- [任务教程 examples/csm_tutorial](examples/csm_tutorial/README.md) — 7 课由浅入深：基础流程 → 目标/计时 → 事件分支 → 实体 → 脚本化模块 → 条件/挂起 → 原生格式
- [引擎自检集 examples/csm_selftest](examples/csm_selftest/README.md) — 8 个最小任务覆盖全部自定义任务引擎功能（含覆盖矩阵与验证要点）
- [双开自动联机测试](scripts/dualtest.ps1) — 带参数启动 host + client 自动联机（Steam 或本地回环）

## Auto-Join CLI (testing)

The mod reads game command-line args for unattended testing (no UI clicks):

| Arg | Meaning |
|---|---|
| `--autohost` | Auto-create a lobby on load (Steam host). Writes lobby id to a shared file. |
| `--autojoin` | Auto-join the host (Steam): read lobby id from the shared file and join. |
| `--autolobby <file>` | Shared lobby-id file path (default `%TEMP%\open_nest_lobby.txt`). |
| `--local host` | **Local loopback host** (no Steam): listen on `127.0.0.1:<port>`. |
| `--local join` | **Local loopback client** (no Steam): connect to `127.0.0.1:<port>`. |
| `--localport <n>` | Local loopback port (default `29507`). |

`scripts/dualtest.ps1`:

```powershell
# Same-machine, NO Steam (local TCP loopback — needs a second game install):
.\scripts\dualtest.ps1 -Local -HostGame G:\...\Iron Nest Heavy Turret Simulator -ClientGame D:\...\Iron Nest Heavy Turret Simulator

# Steam mode (two Steam sessions/accounts):
.\scripts\dualtest.ps1 -HostGame G:\... -ClientGame D:\...
```

> **Local mode**: uses a local TCP loopback transport instead of Steam P2P — the
> two game processes talk over `127.0.0.1` directly, so they **can share one
> Steam session** (no second account needed). Only use this for development
> testing (latency/relay behavior differs from real Steam P2P).
>
> **Steam note**: in Steam mode each game process needs its **own** Steam
> session (separate Steam client / account) — Steamworks rejects a second
> process with the same AppID on one Steam client.

## Getting Started

Requirements: .NET SDK (net6.0 target), BepInEx 6 IL2CPP **or** MelonLoader 0.7.3 installed in the game, Steam.

```powershell
# 1. Configure your local environment (copy template, fill paths)
Copy-Item scripts\env.example.ps1 scripts\env.ps1
#    edit scripts\env.ps1 → set $GameDir to your game install path

# 2. Build + deploy to game
.\scripts\deploy.ps1
```

> The game must be launched via Steam for Steamworks to be available.
> Note: `scripts/env.ps1` (local paths) is gitignored; only `env.example.ps1` is committed.

## Directory Layout

```
src/OpenNestCoop/   BepInEx plugin source (Net / GameSync / Patches / UI)
src/OpenNestCore/   Platform-independent library: UI kit (UiKit/UiSpriteBank), Tasks (custom mission framework), extensions
src/OpenNestCoop.MelonMod/   MelonLoader adapter project (#if MELONLOADER)
tools/              AsmDump (assembly recon)
scripts/            deploy.ps1, env.ps1 / env.example.ps1, dualtest.ps1, package.ps1
docs/               API.md, DEVELOPMENT.md, LOBBY.md, NATIVE_UI.md, TASK_SYSTEM.md, CUSTOM_MISSION.md, …
```

## Contributing

- Open an **Issue** for bugs, questions, or feature requests.
- **Fork + Pull Request** for code changes — branch from the **current** `main` of this
  repo (public `main` keeps a continuous, non-rewritten history, so PRs stay mergeable).
- This public repo is the collaboration point (issues / PRs welcome). Internal
  development and build env live in a separate private repo, so a PR here may be
  re-created or mirrored there — either way your contribution lands in the mod.
  Prebuilt binaries and local private files are **not** distributed with this repo.

## Third-Party Assets

The optional player avatar model shipped as a model example (`Models/player.bundle`)
is based on a third-party model, used with attribution under its CC BY 4.0 license:

> *German WW2 Soldier* by nisuaia.
> Source: [https://sketchfab.com/3d-models/german-ww2-soldier-c0302a245520419e8ea78ee30c54b4c8](https://sketchfab.com/3d-models/german-ww2-soldier-c0302a245520419e8ea78ee30c54b4c8)
> Licensed under CC BY 4.0 International: [https://creativecommons.org/licenses/by/4.0/](https://creativecommons.org/licenses/by/4.0/)
> Adapted / modified from the original model.

## License

[GNU Affero General Public License v3.0](LICENSE) (AGPLv3)

Copyright (c) 2026 Open Nest Co-op contributors.

This program is free software: you can redistribute it and/or modify it under
the terms of the GNU Affero General Public License as published by the Free
Software Foundation, either version 3 of the License, or (at your option) any
later version.

This program is distributed in the hope that it will be useful, but WITHOUT
ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more
details.

> **Network use**: AGPLv3 requires that if you run a modified version of this
> software on a network and users interact with it (e.g. a dedicated server /
> relay), you must offer the corresponding source code to those users. For a
> P2P co-op mod (no central server), this mainly matters if you redistribute
> modified binaries — share your source changes back.
