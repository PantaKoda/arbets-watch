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

CI on GitHub (`PantaKoda/arbets-watch`, PR #2): `build-test-windows`, `core-tests-linux` and `format` passed after the `win-x64` RID fix for the publish step. SDK pinned to 10.0.401 with `latestPatch`, so analyzer and format rules move only with a deliberate SDK bump.

Review fixes: All Sweden includes ads without a country; `AdFilterSql` is two-valued and composable (negation and two filters per command are tested); an active ad without `timestamp` is applied as the newest state instead of being ordered by its publication date; unreadable `removed_date` text is kept; ambiguous removal dates take the later instant.

## Next step

M2: SQLite schema and migrations, summary/read-state/checkpoint storage, restart and rollback tests.
