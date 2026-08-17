# Contributing to Open Nest Co-op

Thanks for helping out! This project is an open-source Steam co-op mod.
Contributions of all kinds are welcome: bug reports, feature requests,
code, docs, and translations.

## Code of Conduct

Be respectful and constructive. Harassment, hate speech, or spam will not be
tolerated. Keep discussions focused on the project.

## How to Report an Issue

Open an [Issue](https://github.com/1499501762/OPEN-NEST-CO-OP-/issues) and
include:

- **Game / mod version** (see the BepInEx console line on load) and OS
- **Steps to reproduce**
- **Expected vs. actual behavior**
- The relevant part of the log (`BepInEx/LogOutput.log` or console output)
- Screenshots/videos if visual

Use the provided issue templates if available.

## Development Setup

See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) for the full guide.

Quick start:

```powershell
Copy-Item scripts\env.example.ps1 scripts\env.ps1   # then fill in your game path
.\scripts\deploy.ps1                                 # build + deploy to the game
```

- The game must be launched via **Steam** for Steamworks to be available.
- Close the game before deploying (the plugin DLL is locked while running).

## Commit Message Convention

Use **Conventional Commits**:

```
<type>(<scope>): <subject>
```

| Type | Meaning |
|------|---------|
| `feat` | new feature / sync support |
| `fix` | bug fix |
| `docs` | documentation only |
| `style` | formatting, no behavior change |
| `refactor` | code change without behavior change |
| `perf` | performance improvement |
| `test` | adding/fixing tests |
| `build` / `ci` | build system / CI |
| `chore` | maintenance |

Examples:

```
feat(sync): add coffee machine sync
fix(ui): block click-through when menu is open
docs: update API reference for ISyncedModule
```

- Use imperative, lowercase subject (e.g. `add`, not `added`).
- Add a body explaining the **why** when non-obvious.

## Branch & Pull Request Workflow

1. Fork the repo and create a branch from `main`:
   `git checkout -b feat/your-feature`
2. Make focused changes; keep commits small and conventional.
3. Build locally with `.\scripts\deploy.ps1` (must compile, 0 errors).
4. Push and open a **Pull Request** back to `main`.
5. Reference any related Issue (e.g. `Closes #12`).
6. A maintainer will review; address feedback with follow-up commits.

Notes:

- Keep PRs **small and scoped**; large rewrites are hard to review.
- Do **not** commit build outputs, local private files, or `scripts/env.ps1`
  (see `.gitignore`).

## Code Style

- C# with `LangVersion=latest`, `net6.0` target.
- Follow the existing structure: `Net/` (transport), `GameSync/` (sync),
  `Patches/` (Harmony), `UI/` (menu).
- Wrap risky interop calls in try/catch and log via `Plugin.LogSource`.
- New sync features: prefer the framework (`CoopSyncRegistry` / `ISyncedModule`)
  instead of writing a standalone sync class.

## Extension APIs

The mod exposes open extension points for other mods:

- **`CoopSyncRegistry`** — register device values (`RegisterFloat/Int/Bool`)
  or custom sync modules (`RegisterModule(ISyncedModule)`).
- **`PlayerVisualRegistry`** — inject custom player models / skeletons /
  animations (`IPlayerVisualProvider`).

Full reference: [docs/API.md](docs/API.md).

## License

By contributing, you agree that your contributions are licensed under the
[GNU Affero General Public License v3.0](LICENSE) (AGPLv3).

- Contributions (code, docs, examples) become part of the AGPLv3-licensed project.
- Any modification/redistribution must keep AGPLv3 and publish the corresponding
  source (see AGPLv3 §13 for network use).
- If you only **call** the extension APIs (`CoopSyncRegistry` / `IPlayerVisualProvider`)
  without copying this code, your own mod may use any license.
