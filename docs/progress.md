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
| M6 — Desktop release | In progress: real sleep/resume, a DPI change and the tray menu still need a manual check |
| M7 — Releases and in-app updates | Done (first real release pending) |

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

CI on GitHub (`PantaKoda/arbets-watch`, PR #2): `build-test-windows`, `core-tests-linux` and `format` passed after the `win-x64` RID fix for the publish step. SDK pinned to 10.0.401 with `latestPatch`, so analyzer and format rules move only with a deliberate SDK bump.

Review fixes: All Sweden includes ads without a country; `AdFilterSql` is two-valued and composable (negation and two filters per command are tested); an active ad without `timestamp` is applied as the newest state instead of being ordered by its publication date; unreadable `removed_date` text is kept; ambiguous removal dates take the later instant.

## M2 — Persistence

- Schema v1 (`PRAGMA user_version`): `ad_summary`, `ad_state`, `sync_state`, `preferences`, `snapshot_staging`; instants as Unix ms, source text kept.
- `AdStore`: single writer, WAL, every operation off the caller's thread. `CommitBatchAsync` applies a batch, expiry and pruning, and the checkpoint in one transaction. Snapshot staging and activation (used in M3) keep read state and preserve newer live states.
- `SourceOrder`: incoming states strictly older than the stored one are rejected; ad vs ad in milliseconds, anything involving a removal in whole seconds; ties apply.
- Unread is decided once, when an ID is first seen after the baseline, from the filter in effect then. Edits, re-publication, filter expansion and re-matching never create unread markers.
- `AppPreferences` (filter, interval 1–60 min, window, theme, transparency, always-on-top, pause) stored as one JSON value; unreadable values fall back to defaults. `AppPaths` resolves the per-platform data directory.

Review fixes (PR #3): `ad_state.first_seen_utc` is the first sighting *as an ad* (null while an ID is known only from a removal), so an ID first seen as a removal still becomes unread when it appears; a completed snapshot is authoritative for membership (a re-published ad with an older timestamp is restored); activation refuses a snapshot with fewer than half the cached ads (`SnapshotRejectedException`, cache and checkpoint kept); expired ads never become unread on activation; `synchronous = NORMAL` is set on every connection; a stored filter without worktime falls back to all categories.

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
- Review fixes (PR #4): valid JSON that isn't an ad fails as `InvalidData` (not retried like a transient error); the stall watchdog runs only while waiting for the network, not while staging writes run; corrupt compressed data is classified; connectivity failures are flagged (`IsConnectivity`); a completed download whose replay or activation failed is reused for 30 minutes instead of downloading ~450 MB again.
- `PollIntervalAsync` (interval clamp, nothing due, unchanged checkpoint on failure, `Retry-After` deferral) is exercised by the M4 tests, not here.

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
- Review fixes (PR #5): the coordinator's lock is held only around each commit (`CommitScope`), never across downloads, throttling waits or a whole catch-up, so filter changes and mark-read stay instant; the loop survives startup read failures, unexpected exceptions and failing event subscribers; jitter is applied before clamping to `Retry-After`; invalid data and rejected snapshots retry after at least 30 minutes; offline is detected from the client's connectivity flag (stalls and dropped connections included); background reconciliation snapshots are not shown as foreground; pausing stops a running catch-up between steps.

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

Review fixes (PR #6): "Mark these as read" clears exactly the rows shown (`AdStore.MarkReadAsync(ids)`), never held-back or newly committed ads; an unreadable database is moved aside only when SQLite reports it corrupt or not a database, never paired with old sidecar files, and any other open error stops with a message box and changes nothing (`StoreOpener`, tested; a leaked connection on a failed open was fixed too); a newer-version database also shows a message instead of exiting silently; filter changes reload the list immediately; read-state write failures show a notice instead of crashing; only a user close hides to the tray, so sign-out and shutdown close normally; resume detection is a tested `ResumeDetector` on `TimeProvider`; shutdown steps can't skip each other. Live: a garbage `arbetswatch.db` started fresh with the notice and the old file kept as `arbetswatch.db.unreadable-<time>`.

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
- Off-screen bounds: with saved bounds beyond both displays (x = 6000), the window opened at (1940, 100), at the right edge of the primary display. Windows clamps a start position outside every display onto the nearest display before the window opens, so ArbetsWatch's own recovery (which centers the window and now logs "was unreachable") did not need to run. The earlier (−6000, −6000) → (0, 0) result had the same cause.
- Two displays (2560×1440 primary, 1920×1080 to its right, both 96 DPI): the window moved onto the second display was saved there on Quit and reopened at the same place (2700, 150). Both displays have the same scaling, so this is not a DPI-change check.
- Transparent + dark theme: frosted surface with opaque text (captured).

Limitations and checks not performed:

- Real sleep/resume was not exercised (the machine was not put to sleep). `ResumeDetector` is unit-tested with a fake clock (a missed 30 s tick after an hour reports the gap), and the coordinator's catch-up after a 30 h gap is tested; the live path from a real suspend is unverified.
- A display-scaling (DPI) change was not exercised; both displays run at 96 DPI.
- The tray icon and its menu were not driven: UI Automation reaches only the taskbar button, not the notification-area icon. The app reports the tray as available.
- Manual check still needed: put the PC to sleep for a few minutes and wake it (expect `Resumed after … catching up` in the log and a refresh), change display scaling with the app open, and click each tray menu item.
- With transparency on, the blur also fills the 6 px margin outside the rounded frame.
- Not code-signed; no installer; Windows x64 only.

## M7 — Releases and in-app updates

Same design as Repo Watch (details and security notes in `docs/updates.md`):

- `.github/workflows/release.yml`: a `vX.Y.Z` tag on `main` matching `<Version>` builds, tests and packages with `scripts/publish-windows.ps1`, takes the notes from `CHANGELOG.md` (`scripts/release-notes.ps1`) and creates the GitHub release with the zip and `.sha256`. Actions pinned to release commit SHAs; only this job gets `contents: write`.
- Packaging: reproducible zip (ordinal entry order, commit timestamp, no symbols, English resources only; 47.7 MB) with a `release.json` marker (version, commit, files).
- Core `Updates`: `ReleaseClient` (anonymous GitHub releases, downloads only from this repository's release URLs, size limit, stall timeout), `UpdateService` (daily check, user-initiated install: checksum, staging in the data folder, manifest version check, hand-over), `UpdateApplier` (waits for the old process, moves the folder to `.previous`, copies, restarts; puts the previous version back on failure), `InstallInfo`.
- UI: UPDATE pill in the header, update window with plain-text notes, Install / Cancel / View on GitHub / Check again / Later, Settings → Check for updates, "Updated to X from Y" notice after the hand-over.
- CI now packages with the release script and uploads the zip.
- CI fix: the Windows job's locked publish failed (NU1004) on M1–M5 because Core lacked the `win-x64` RID; fixed on M1 and merged forward.

Validation on Windows 11:

```text
dotnet test ArbetsWatch.slnx → 154 passed (36 new: versions, policy, checksum files, notes text, release client, install flow with a real zip, folder swap)
```

- Real hand-over with release zips built by the script: 0.1.0 installed in a scratch `Programs\ArbetsWatch` and running; 0.1.1 staged in the data folder's `updates`; updater started with `--apply-update`; 0.1.0 quit → `Programs\ArbetsWatch` now 0.1.1, `ArbetsWatch.previous` holds 0.1.0, `logs\update.log` records it, 0.1.1 started from the install folder and showed "Updated to 0.1.1 from 0.1.0.", with the saved list intact.
- Check for updates from the installed 0.1.0 against the real repository: "Version 0.1.0 is the latest" (no releases yet).

Review fixes (PR #8): `UpdateService.Dispose` is idempotent and owned by `Program` only (every quit used to end in `ObjectDisposedException`); the release workflow is split so restore/build/tests run with a read-only token and no persisted credentials, and only a separate job with `contents: write` publishes; the hand-over never leaves nothing running (timeout restarts the installed version, a new version that can't start is rolled back), and restore steps retry; rollback paths are tested with real Windows file locks; the install precondition is checked after taking the gate (no leak), a busy gate says so, and staging is also cleaned after a failed hand-over; release notes are written outside the tree and the publish script refuses a dirty tree unless `-AllowDirty`.

Not yet verified: download of a real GitHub release asset (needs a published release) and the release workflow itself (runs on the first tag).

## Next step

Publish `v0.1.0` (tag on `main`, Release workflow), then run the manual M6 checks (sleep/resume, DPI change, tray menu). A later `v0.1.1` exercises the in-app update against GitHub end to end.
