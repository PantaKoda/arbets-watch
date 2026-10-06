# ArbetsWatch

A Windows-first desktop app that keeps a local, filterable view of the job ads currently published on Platsbanken, using Arbetsförmedlingen's public JobTech APIs. Built with C#, .NET 10 and Avalonia; the shared core stays portable for later macOS/Linux builds.

> **Status: in development (v0.1).** See [docs/progress.md](docs/progress.md) for what works and what has been verified.

![ArbetsWatch filtered to Göteborg, Mölndal and Hallands län, part-time](docs/images/filtered-goteborg-molndal-halland-deltid.png)

## How it works

- On first start, ArbetsWatch downloads the complete JobStream snapshot (≈ 450 MB, about a minute on a fast connection) and keeps a compact summary of every current ad in a local SQLite database.
- It then polls the JobStream change feed (default every 5 minutes) and applies new, changed and removed ads.
- Filtering by län, kommun and worktime (Heltid, Deltid, not specified) happens locally, so changing filters is instant and works offline.
- "New" means newly detected by this monitor while it was running, not necessarily newly published.

No account or API key is needed. Data stays on your PC in `%LOCALAPPDATA%\ArbetsWatch`.

## Install and run (Windows)

1. Build the portable ZIP with `pwsh scripts/publish-windows.ps1` (see below), or take `ArbetsWatch-<version>-win-x64.zip` from a release.
2. Check the download: `Get-FileHash ArbetsWatch-<version>-win-x64.zip -Algorithm SHA256` must match the `.sha256` file.
3. Extract it to a folder you can write to, for example `%LOCALAPPDATA%\Programs\ArbetsWatch`, and start `ArbetsWatch\ArbetsWatch.exe`.

The ZIP is self-contained (the .NET runtime is included) and needs no administrator rights. The executable is not code-signed, so Windows SmartScreen may warn; continue only when the hash matches.

- **First start** downloads all current ads; the window shows progress. Later starts open instantly with the saved list.
- **Close** (or the – button) hides the window to the notification area; monitoring continues. **Quit** is in the tray menu and in Settings.
- **Keys:** F5 refresh, Ctrl+L places, Ctrl+, settings, Enter opens the selected ad, Esc closes a panel.
- **Remove:** quit, delete the app folder and `%LOCALAPPDATA%\ArbetsWatch` (database, preferences, logs).

## Build

Requires the .NET 10 SDK.

```powershell
dotnet restore ArbetsWatch.slnx --locked-mode
dotnet build ArbetsWatch.slnx --configuration Release --no-restore
dotnet test ArbetsWatch.slnx --configuration Release --no-build --no-restore
```

## Repository layout

| Path | Contents |
|---|---|
| `src/ArbetsWatch.Core` | Models, matching rules, synchronization, HTTP adapters, SQLite persistence, bundled geography. No UI dependency. |
| `src/ArbetsWatch.Desktop` | Avalonia views, view models, tray and window integration. |
| `tests/ArbetsWatch.Core.Tests` | Deterministic unit and SQLite tests with sanitized fixtures. |
| `tools/TaxonomyExport` | Regenerates `src/ArbetsWatch.Core/Places/places.json` from the Taxonomy API. |
| `docs/api-contracts.md` | Verified API behavior and open questions. |
| `docs/progress.md` | Milestone status and validation evidence. |

## Release build

```powershell
pwsh scripts/publish-windows.ps1
```

Writes `artifacts/release/ArbetsWatch-<version>-win-x64.zip` and its `.sha256`. `-SkipTests` skips the build/test step.

## Regenerating the geography

```powershell
dotnet run --project tools/TaxonomyExport
```

The tool pins the latest taxonomy version and stops if the region or municipality count differs from the expected 21/290.
