# Progress

Status of the milestones in `AGENTS.md`, with the validation that was actually run.

| Milestone | Status |
|---|---|
| M0 — Contracts | Done |
| M1 — Foundation | Done |
| M2 — Persistence | Done |
| M3 — Bootstrap | Not started |
| M4 — Monitoring | Not started |
| M5 — Usable UI | Not started |
| M6 — Desktop release | Not started |

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

## Next step

M3: streaming snapshot download into staging, overlap replay, atomic activation, first-run baseline, memory measurement.
