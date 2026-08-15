# OPEN-NEST-CO-OP-

**Multiplayer co-op mod for [Iron Nest: Heavy Turret Simulator](https://store.steampowered.com/app/2950790)** (Unity 6 / IL2CPP).

Play as a crew inside a giant fortress turret — calibrate machinery, load shells,
man the fire-control computer, brew coffee — together with friends over Steam.

- **Mode**: shared turret crew (host-authoritative), cooperative role division
- **Transport**: Steam P2P via the game's own Steamworks (relay punch-through, zero extra infra)
- **Loader**: BepInEx 6 IL2CPP (ships with the game)

---

## Features

| Milestone | Status | What's synced |
|---|---|---|
| M0 | ✅ | Tech recon: stack, loader, Steam APIs |
| M1 | ✅ | Steam lobby, P2P transport, roster, ping, chat, UGUI menu (localized) |
| M2 | ✅ | Turret rotation/elevation, gun fire, aimer input, **player avatars** (head+gas-mask), **record player**, load/fire workflow, map markers (incl. live dragging), cranks/dials/sliders, coffee machine |
| M3 | 🚧 | Mission/objectives/impacts, counter-battery, engine/pressure/lights |

Also includes:
- **Click-through blocking** while the coop menu is open (full-screen ray block + interaction lock)
- **Localization** (Chinese / English, auto-switches with the game)

## Open Extension APIs

Designed to be extended by other mods:

- **`CoopSyncRegistry`** — register device state values (`RegisterFloat/Int/Bool`) or custom sync modules (`RegisterModule(ISyncedModule)`)
- **`PlayerVisualRegistry`** — inject custom player models / skeletons / animations (`IPlayerVisualProvider`)

See [docs/API.md](docs/API.md) for the full API reference.

## Documentation

- [API Reference](docs/API.md) — sync/extension APIs, message protocol, skeleton/animation architecture
- [Development Guide](docs/DEVELOPMENT.md) — tech stack, build/deploy, env variables, test steps
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

Requirements: .NET SDK (net6.0 target), BepInEx 6 IL2CPP installed in the game, Steam.

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
tools/              AsmDump (assembly recon)
scripts/            deploy.ps1, env.ps1 / env.example.ps1
docs/               API.md, DEVELOPMENT.md
```

## Contributing

- Open an **Issue** for bugs, questions, or feature requests.
- Fork + **Pull Request** for code changes.
- Prebuilt binaries and local private files are **not** distributed with this repo.

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
