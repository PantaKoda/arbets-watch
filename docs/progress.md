# Progress

Status of the milestones in `AGENTS.md`, with the validation that was actually run.

| Milestone | Status |
|---|---|
| M0 — Contracts | Done |
| M1 — Foundation | Done |
| M2 — Persistence | Not started |
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

## Next step

M2: SQLite schema and migrations, summary/read-state/checkpoint storage, restart and rollback tests.
