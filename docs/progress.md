# Progress

Status of the milestones in `AGENTS.md`, with the validation that was actually run.

| Milestone | Status |
|---|---|
| M0 — Contracts | Done |
| M1 — Foundation | Done |
| M2 — Persistence | Done |
| M3 — Bootstrap | Done |
| M4 — Monitoring | Done |
| M5 — Usable UI | Done |
| M6 — Desktop release | Done (see limitations) |

## M0 — Contracts

`docs/api-contracts.md` records the live probes and source reading from 2026-10-06: snapshot size (449 MB, 40,844 ads, 68 s), Stockholm query semantics with explicit offsets, whole-second inclusive bounds, removal shape and nightly removals, tombstones visible after 11 months, no rate-limit headers. Sanitized fixtures are in `tests/ArbetsWatch.Core.Tests/Fixtures`. Open questions are listed at the end of that document.

## M1 — Foundation

- Solution `ArbetsWatch.slnx`: Core, Desktop (empty window), Core tests, `tools/TaxonomyExport`.
- SDK pinned in `global.json`; central package versions; NuGet lock files committed; nullable and warnings-as-errors on.
- `places.json` generated from taxonomy version 31: 21 regions, 290 municipalities, codes as strings.
- Core: `PlaceCatalog`, `SwedishTime`, `AdRecordParser`, shared `AdFilter`/`AdMatcher`/`AdFilterSql`.
- CI: `build-test-windows`, `core-tests-linux`, `format`, actions pinned to release commit SHAs.

Validation run locally on Windows 11:

```text
dotnet run --project tools/TaxonomyExport          → 21 regions, 290 municipalities, version 31
dotnet restore ArbetsWatch.slnx --locked-mode      → ok
dotnet build ArbetsWatch.slnx                      → 0 warnings, 0 errors
dotnet test ArbetsWatch.slnx                       → 41 passed
dotnet format ArbetsWatch.slnx --verify-no-changes → ok
```

The CI workflow has not run yet; there is no GitHub remote.

## M2 — Persistence

- Schema v1 (`PRAGMA user_version`): `ad_summary`, `ad_state`, `sync_state`, `preferences`, `snapshot_staging`; instants as Unix ms, source text kept.
- `AdStore`: single writer, WAL, every operation off the caller's thread. `CommitBatchAsync` applies a batch, expiry and pruning, and the checkpoint in one transaction. Snapshot staging and activation (used in M3) keep read state and preserve newer live states.
- `SourceOrder`: incoming states strictly older than the stored one are rejected; ad vs ad in milliseconds, anything involving a removal in whole seconds; ties apply.
- Unread is decided once, when an ID is first seen after the baseline, from the filter in effect then. Edits, re-publication, filter expansion and re-matching never create unread markers.
- `AppPreferences` (filter, interval 1–60 min, window, theme, transparency, always-on-top, pause) stored as one JSON value; unreadable values fall back to defaults. `AppPaths` resolves the per-platform data directory.

Validation (local, temporary SQLite files): restart persistence, rollback on failure and cancellation, identical replay, older-state rejection, removal tie, unknown-ID removal, re-publication, unread rules, filter expansion, location change, mark-matching-read scope, expiry without removal, empty interval, 90-day pruning, newer-schema refusal, preferences round trip.

```text
dotnet build ArbetsWatch.slnx  → 0 warnings, 0 errors
dotnet test ArbetsWatch.slnx   → 66 passed
dotnet format --verify-no-changes → ok
```

## M3 — Bootstrap

- `JobStreamClient`: `application/jsonl`, line-by-line parsing, explicit-offset query bounds, a 2-minute read-stall watchdog, and failures classified as transient, rate-limited (`Retry-After`) or permanent (4xx).
- `RequestGate`: one shared minimum spacing (60 s) and server back-off for every request.
- `SyncEngine.LoadSnapshotAsync`: captures the start before requesting, stages in 2,000-row transactions, replays `[start − 5 min, now − 2 min]` into staging (older states never overwrite newer ones; removals become tombstones), then activates and checkpoints atomically. `PollIntervalAsync` requests one bounded interval and commits it with its checkpoint.
- Snapshot policy: first start, time-adapter change, gap over 7 days, or weekly reconciliation.

Validation:

- Unit tests with a fake HTTP handler (request shape and offset encoding, status mapping, truncated record, dropped connection, stall, caller cancellation) and a fake JobStream (first baseline, interrupted snapshot after 4,000 staged rows leaves the old cache and checkpoint, edits/removals/older states during the download, reconciliation keeps read state and marks new IDs).
- Live measurement through Core (Release, Windows 11): 40,849 ads in 73.4 s, peak working set 79 MB, database 31 MB, all-Sweden query 318 ms. Details in `docs/api-contracts.md`.

```text
dotnet test ArbetsWatch.slnx → 83 passed
```

## M4 — Monitoring

- `RefreshCoordinator`: one loop owns timer, manual refresh, resume, retry and startup; at most one mutation at a time. Requests arriving while a refresh runs are absorbed by it. Filter changes and "mark matching as read" wait for the running batch, so a batch is judged against one filter.
- Restart resumes the schedule from the last success instead of requesting immediately.
- Bounded catch-up: up to 16 gated requests per cycle, each interval at most 12 h; gaps over 7 days use a snapshot.
- Failures keep the cached list visible. Transient: 30 s doubling, capped at the poll interval, ±25 % jitter, never shorter than `Retry-After`. Rejected (4xx): automatic retries stop until a manual refresh. Status phases: Idle, LoadingSnapshot, Updating, Paused, Offline, Failed.
- Expiry is applied at every commit and at query time, so expired ads disappear without network access.

Validation: coordinator tests with a fake clock (startup snapshot, timer, coalesced manual refreshes, transient backoff window, permanent stop and manual recovery, pause/unpause, filter change during a running batch, 30-hour catch-up in three contiguous ≤ 12 h intervals, restart scheduling) and engine tests (autumn DST intervals with exact query strings, nothing requested before due, `Retry-After` deferring the shared gate without moving the checkpoint, gate spacing, sparse removal plus expiry in one poll).

```text
dotnet test ArbetsWatch.slnx → 97 passed
```

## M5 — Usable UI

- Borderless resizable window (620 × 760) in Repo Watch's HUD style: drag header with status pill, scan line only while you wait, filter bar, virtualized list, footer with counts, resize edges and grip. Light and dark themes.
- Places panel with search: tick a län for the whole län, or single kommuner; All of Sweden; summary such as "Göteborg, Mölndal, Hallands län". Ticking a kommun while All of Sweden is on switches to that choice.
- Worktime chips Heltid / Deltid / Ej angiven; at least one stays selected.
- Rows: two-line title, employer, place (or neutral label), worktime chip, Swedish publication time, unread dot, one-time glow for newly detected ads. Double-click, Enter or the row button opens the validated Platsbanken page and marks the ad read. "Mark these as read" clears only the current results.
- Updates arriving while you are scrolled down are held behind a "N new ads — show" pill; rows that left the results are dimmed until then.
- States: first download (with count), no data (retry), choose places, no matches, offline/failed notice with the last data kept.
- Settings panel: interval 1–60 min, pause, theme, transparency, always on top, data folder, quit, version and taxonomy version.
- Presentation rules live in Core (`AdLinkPolicy`, `PlaceSelection`, `DisplayText`) and are unit tested.

Validation on Windows 11 (Debug build, scratch data folder via `ARBETSWATCH_DATA_DIR`), driven through Windows UI Automation and captured with `PrintWindow` (window only):

- First start downloaded 40,859 ads in 78 s while showing the download state; the list then showed 40,633 Swedish ads newest first.
- Places search "teborg"/"ndal"/"Halland", ticking Göteborg, Mölndal and the whole Hallands län, then Deltid only: 621 ads from Göteborg, Mölndal and Kungsbacka (`docs/images/filtered-goteborg-molndal-halland-deltid.png`).
- Hide to tray, then a second launch exited with code 0 and showed the running window (one process).
- Quit from Settings logged "Stopped"; relaunch restored places, worktime, window bounds and the list without a new snapshot.
- Startup poll after more than 5 minutes; a manual refresh waited for the 60 s request spacing, then applied 17 records.
- Dark theme (`docs/images/list-dark.png`).

Not verified: opening an ad in the browser (not clicked, to avoid launching your browser), held updates while scrolled (no matching new ads arrived during the session), transparency on/off, high contrast and reduced motion.

```text
dotnet build ArbetsWatch.slnx → 0 warnings, 0 errors
dotnet test ArbetsWatch.slnx  → 118 passed
```

## M6 — Desktop release

- `scripts/publish-windows.ps1`: locked restore, Release build and tests, self-contained `win-x64` publish, ZIP and SHA-256. Core declares the `win-x64` RID so locked restore matches the publish (SQLite native assets).
- Tray: Show, Refresh now, Pause monitoring (checked state follows Settings), Settings, Quit; tooltip shows the status. Closing hides to the tray; without a tray, closing quits.
- Resume detection: a 30 s tick that finds a gap over 2 minutes requests a catch-up refresh.
- Window bounds restored per saved position and moved back onto a working area when unreachable.
- Transparency: acrylic/Mica/blur when available, surface only (text opaque); solid in high contrast, remote sessions, or when the platform grants none.
- Unreadable database files are moved aside and the app starts with an empty cache; a database from a newer version is never opened.

Validation on Windows 11:

```text
pwsh scripts/publish-windows.ps1 → 118 tests passed; ArbetsWatch-0.1.0-win-x64.zip, 73.4 MB (208 MB unpacked, 236 files)
```

- Published exe started with `PATH=C:\Windows\System32;C:\Windows` and no `DOTNET_ROOT`: runs (self-contained).
- Offline restart (only the app process pointed at a refused proxy): saved list shown immediately; the scheduled poll failed as "No connection to Arbetsförmedlingen. Showing saved ads."; retry scheduled with backoff; Quit logged "Stopped".
- Saved bounds at (−6000, −6000): window opened on screen at (0, 0).
- Transparent + dark theme: frosted surface with opaque text (captured).

Limitations and checks not performed:

- Real sleep/resume was not exercised (the machine was not put to sleep); only the gap-detection code path exists.
- DPI and monitor changes were not exercised (single display); only the off-screen recovery was.
- Tray icon clicks and menu items were not driven by automation; the tray reported as available.
- With transparency on, the blur also fills the 6 px margin outside the rounded frame.
- Off-screen recovery places the window at the working area's origin rather than centering it.
- Not code-signed; no installer; Windows x64 only.

## Next step

Create the GitHub repository (not done: no remote was authorized), push the milestone branches, open PRs and let CI run. Then a first draft release from a reviewed commit on `main`.
