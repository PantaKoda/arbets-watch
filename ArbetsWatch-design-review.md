# ArbetsWatch: plan review and proposed design

Prepared 6 October 2026. Scope: a Windows-first desktop monitor for Platsbanken advertisements, with a path to macOS and Linux.

## Recommendation

The proposed UI, geographic filters, bundled taxonomy, and small first release make sense. The part I would redesign is the synchronization strategy.

For a monitor that maintains the complete current list, I would use **JobStream snapshot + JobStream updates, a local SQLite database containing compact ad summaries, and one shared filtering implementation**. JobSearch can be added when its search capabilities are needed. I would not make two independently refreshed APIs jointly responsible for the same list in the first release.

This choice has a cost: the first snapshot contains the whole Platsbanken corpus and can be large. If a fast, lightweight search window is the actual priority, use JobSearch alone initially and explicitly describe its results as a paged search. Both are reasonable products; the original proposal mixes their responsibilities.

The review below is based on official documentation, live API schemas, small live requests, and the public JobStream implementation. I have not inspected your local repo-watch project or the particular large taxonomy file quoted in the plan.

## 1. What I verified

| Point in the proposal | Finding | Design consequence |
|---|---|---|
| Bundle a small geographic file | A live Taxonomy hierarchy request returned **21 Swedish regions and 290 municipalities**. | Keep this idea; derive membership through relationships, not names ending in “län”. |
| Full-time and part-time IDs | Live taxonomy returned Heltid `6YE1_gAC_R2G` and Deltid `947z_JGS_Uk2`. | Use concept IDs internally; labels are display text. |
| Stream cannot populate the initial list | `/v2/stream` is for changes, but **`/v2/snapshot` supplies currently published ads**. | JobSearch is optional for initial population. |
| Search returns all matching jobs | Search has `limit` up to **100**, and `offset` up to **2,000**. | A large query cannot be fully enumerated by ordinary paging. Never equate one response with the full matching set. |
| Göteborg counts | Live requests returned **3,213 ads**, including **378 with the part-time filter**. | These are observations on the review date, not stable requirements or counts of individual vacancies. |
| Worktime filtering in Stream | The live Stream schema advertises geography and occupation filters, but no worktime filter. | Apply worktime filtering locally. |
| One in ten ads have no worktime | I did not measure this proportion across a representative dataset. | Support missing values without relying on the quoted percentage. |
| Avalonia 12.1.3 | This version is published on NuGet. | It is a valid candidate; confirm repo-watch's actual package versions before reusing components. |
| Authentication | Current official JobSearch and JobStream service pages say no API key or registration is required. | The app needs no sign-in flow for these public APIs. |

Sources: [JobStream schema][S1], [JobSearch schema][S2], [Taxonomy geography][S3], [Taxonomy access][S4], [service access][S5], [Avalonia package][S6]. The live checks used no API credentials.

The quoted 43,695 concepts, 1,519 regions, and 8.5 MB file size remain unverified because that source file was not supplied. None is needed to decide the app architecture.

## 2. Choose a clear data strategy

| Strategy | Strength | Limitation | My use for it |
|---|---|---|---|
| JobSearch only | Small filtered requests; quick startup; server-side search. | Paged, changing result windows; disappearance from a page does not prove removal. | A lightweight search-browser prototype. |
| JobSearch + JobStream | Fast search results with incremental changes. | Must reconcile two views, pagination limits, filter semantics, and indexing delays. | Add only after specifying which source owns the cache. |
| Snapshot + Stream | One coherent mechanism for a complete local current list. | Larger initial download and recovery snapshots. | **Recommended for the monitoring behavior described.** |

The snapshot schema does not advertise geographic filtering. Local filtering reduces retained data, not the snapshot's network transfer. JobStream's guide describes a snapshot around 300 MB; treat that as a historical indication of scale, not a measurement or promise about today's transfer. Benchmark before polishing the UI. [S1, S7]

For the recommended implementation:

- Download one initial snapshot and retain compact summaries for all current ads. Apply Sweden, region, municipality, and worktime restrictions when querying the local database.
- Poll the unfiltered change endpoint every five minutes, configurable from 1–60 minutes within the service's throttling rules. This also observes ads whose location changes out of the selected area.
- Retain only the latest summary for each ad, plus small synchronization and read-state records. Do not keep ad revision history or full descriptions.
- Run a fresh snapshot periodically and after sufficiently long interruptions or incompatible state. A weekly reconciliation and a seven-day interruption threshold are starting policies to measure, not API guarantees.

National updates carry more data than geographically filtered updates. If that cost is unacceptable, explicitly switch to a narrower product: a JobSearch-based browser, or a filtered cache with its own membership-reconciliation design. A narrowly filtered stream alone cannot establish that an ad moved outside its filter.

## 3. App structure and dependencies

Create a separate git repository named `ArbetsWatch`, which can live under the existing `Arbets` development directory. Folder location and repository boundaries are separate choices. Reuse repo-watch's styles and proven window behavior after inspecting its source.

Keep **two production projects**:

| Project or directory | Responsibility |
|---|---|
| `src/ArbetsWatch.Core` | Ad models, filter rules, update coordinator, HTTP adapters, persistence, and bundled taxonomy. No Avalonia dependency. |
| `src/ArbetsWatch.Desktop` | Avalonia views and view models, tray integration, window behavior, settings UI, and startup composition. |
| `tests/ArbetsWatch.Core.Tests` | Focused tests for synchronization, matching, and restart behavior. |
| `tools/TaxonomyExport` | Small developer-only C# program that regenerates the bundled geography data. |

Within Core, keep pure matching/reduction logic separate from HTTP and SQLite adapters. Separate projects for each layer are unnecessary initially.

Use C# with **.NET 10 LTS**, Avalonia 12.1.3 with Fluent styling, CommunityToolkit.Mvvm, built-in `HttpClient` and `System.Text.Json`, and `Microsoft.Data.Sqlite`. Pin compatible package versions. Microsoft lists .NET 10 as supported through November 2028; .NET 8 and 9 reach end of support in November 2026. [S6, S9, S10]

The app directly calls the public APIs. It needs no backend service. Polling continues while the process runs in the tray; it stops when the app exits or the computer sleeps. Resume triggers catch-up.

## 4. Taxonomy and filters

Generate `places.json` from Sweden's country concept, `i46j_HmG_v64`, traversing region and municipality relationships. Include concept IDs, Swedish labels, administrative codes as strings, and explicit parent relationships. Leading zeroes in municipality codes must survive.

Also include a schema version, generation timestamp, and the taxonomy version actually used. Keep generation reproducible and review changes before shipping. Validate 21 regions and 290 municipalities against the current expected dataset; if those counts change, require investigation rather than silently trimming the result. Ship this file as an application resource and regenerate it during maintenance releases. [S3, S4]

Define geography as a union of explicit selections:

| Selection | Meaning |
|---|---|
| All Sweden | All ads with a Swedish workplace country, including those without municipality data. |
| An entire län | All ads assigned to that region, including region-level ads with no municipality. |
| Particular municipalities | Only those municipalities. |
| Multiple selections | OR across selected regions and municipalities. |
| Geography and worktime | AND between the two filter groups. |

For example, **Göteborg + Mölndal + all Halland** must work directly. A partially selected region should not accidentally mean its whole region. An explicit “All Sweden” option also avoids making an empty selection ambiguous.

For worktime, provide Heltid, Deltid, and **Not specified** as separate selectable categories, with an All option. All includes missing and unrecognized values. Part-time alone includes only the part-time concept unless the user also selects Not specified. Preserve unrecognized future concept IDs and their labels instead of silently classifying them as part-time or full-time.

Use one `AdMatcher` for database filtering, unread decisions, and update handling. Do not separately implement slightly different rules for initial results and later updates. In a future JobSearch integration, explicitly account for the fact that a server-side part-time filter does not retrieve ads with missing worktime values.

## 5. What to save

Use SQLite for data that must change together. A list of seen IDs alone cannot restore the current list, distinguish edits from discoveries, or safely associate data with a polling checkpoint.

| Stored item | Main contents |
|---|---|
| `ad_summary` | Ad ID as text; headline; employer; country/region/municipality IDs and labels; worktime; publication and final-publication dates; source update metadata; ad URL. One current row per ID. |
| `ad_state` | First observed time; unread flag; last known source state; compact removal/order information retained when a summary is removed. |
| `sync_state` | Last committed interval end, last successful refresh, last snapshot, schema version, and time-adapter version. |
| `preferences` | Geographic selections, worktime options, poll interval, window bounds, theme, transparency, and always-on-top setting. |

Index the fields used for filtering and sorting. A single writer owns database mutations. Ad changes and the committed interval end belong in **one transaction**.

Remove inactive summaries after confirmed removal or expiry. Retain compact state for active ads and, initially, 90 days after an ad becomes inactive. That retention is an app policy, not a claim about Stream's retention. After pruning an ID, an unusually late reappearance may be treated as newly discovered; indefinite recognition would require indefinite ID retention.

On Windows, use `%LOCALAPPDATA%\ArbetsWatch`. Resolve application-data paths through a platform abstraction for future macOS/Linux builds. Keep logs bounded and avoid retaining entire API responses in ordinary logs.

## 6. Initial synchronization

1. Open the database and show any cached results immediately, with their last-refresh time.
2. On first use or required recovery, capture a starting time **before** requesting the snapshot.
3. Read the response incrementally into staging storage. Project each ad to its summary; do not deserialize the entire corpus into one large object graph.
4. After a complete successful response, replay changes from slightly before the captured start through a fixed upper boundary. This covers changes made while the snapshot was downloading.
5. Resolve replayed states without overwriting newer snapshot information with older changes. Use source update/removal metadata, not response position.
6. Activate the staged dataset and checkpoint atomically. On first-ever startup, establish a baseline without making every existing ad unread.
7. Preserve existing read state during later reconciliation. IDs newly discovered after an interruption can be described as newly found, without implying that they were just published.

A failed snapshot leaves the previous usable dataset intact. A successful snapshot is also the basis for finding ads absent from the current corpus; absence from a partial search response is not.

This is an **eventually consistent current-state monitor**, not a guaranteed record of every publication event. A snapshot followed by changes reduces race windows, but the API is not documented as providing a transactional snapshot token or an immutable event log.

## 7. Ongoing polling and failure handling

Each cycle uses a persisted checkpoint `C` and a fixed request boundary `E` chosen before the request. Start with a five-minute overlap and a small end-time safety lag, such as two minutes, then tune from measurements. Those settings add freshness delay and do not guarantee coverage of arbitrarily late indexing.

Round `E` to the API's supported time precision before sending or saving it. Request changes for `[C − overlap, E]`. After receiving and validating the complete response, commit its changes and checkpoint `E` together. Never advance to the time the download finished or just to the largest timestamp returned. Empty successful intervals must advance too.

Important rules:

- Reprocessing an interval must be safe. Deduplicate by ad ID and reconcile source state/version information; an edit is an upsert, not a new row.
- An explicit removal acts on the existing ID even if location or worktime fields are absent. Removal objects have a different shape from complete ads.
- Where source timestamps permit ordering, reject older states. If competing states cannot be ordered reliably, resolve the ambiguity through reconciliation instead of inventing an order from array position.
- Honour `last_publication_date` for expiry. Do not depend on receiving a removal event for every natural expiry, and do not substitute the application deadline for the publication end.
- Re-evaluate eligibility when location or worktime changes. The unfiltered current cache makes both entry into and exit from the selected set observable.
- A timeout, cancellation, malformed response, or failed write leaves the checkpoint unchanged. Preserve the visible list and mark it stale.
- Use one refresh coordinator for the timer, F5, the refresh button, and resume. Coalesce repeated requests and allow only one active synchronization.
- Catch up in bounded intervals after sleep; use snapshot recovery after long gaps. Respect rate limits and `Retry-After`, with backoff and jitter for transient failures. The current guide describes one Stream request per minute, so manual refresh must share that limit. [S7]

Illustrative C# orchestration, showing the transaction boundary rather than a complete implementation:

```csharp
async Task RefreshAsync(CancellationToken ct)
{
    await refreshGate.WaitAsync(ct);
    try
    {
        var state = await store.ReadSyncStateAsync(ct);
        var end = apiTime.FloorToSupportedPrecision(
            clock.GetUtcNow() - options.SafetyLag);
        if (end <= state.CommittedThroughUtc) return;

        var start = state.CommittedThroughUtc - options.Overlap;
        // The adapter converts interval bounds to verified API time semantics.
        // Staging succeeds only after the complete response has been read.
        await using var batch = await stream.StageIntervalAsync(start, end, ct);

        // Apply source-state rules and save 'end' in the same transaction.
        await store.CommitBatchAndCheckpointAsync(batch, end, ct);
    }
    finally
    {
        refreshGate.Release();
    }
}
```

Staging disposal must discard temporary data after failure. Rate scheduling, bounded catch-up, expiry, and UI publication belong around this operation. The method names represent application contracts to implement, not existing library methods.

## 8. Time handling needs its own adapter

Use UTC instants internally and Europe/Stockholm for Swedish display. Keep original source timestamps when useful for diagnostics. Do not append `Z` to an offset-free timestamp and assume that makes it UTC.

Live ad examples contained offset-free publication strings that corresponded to Swedish local time, alongside epoch-millisecond update values. For example, an ad published at `2026-10-06T17:00:07` carried a timestamp representing `15:00:07.900Z`. A one-minute query with explicit `+02:00` bounds was accepted and returned the same six IDs as the offset-free probe. This is limited evidence, not a complete contract for every date field or daylight-saving boundary.

Before relying on a polling cursor, test query bounds, update timestamps, removal dates, and both daylight-saving transitions. In particular, an autumn repeated local hour must not cause a skipped interval. Keep a verified offset-aware strategy where supported, or replay a sufficiently broad range and reconcile when the API's time semantics are ambiguous. Include boundary and timezone fixtures in the adapter tests. [S1, S8]

## 9. Define “new” separately from “updated” and “read”

| Event | UI behavior |
|---|---|
| First successful baseline | Existing ads appear without new markers. |
| Previously unknown ID arrives and matches the active selection | Mark unread and give it a brief highlight. |
| Known ID changes | Update its row; preserve unread state. An Updated indicator can be added later. |
| An existing ad starts matching after a data edit | Treat as a changed match, not a newly published ad. |
| Filter selection expands | Newly included existing ads are baseline results; preserve unread state for overlapping results. |
| User opens an ad | Mark that ad read. |
| User chooses Mark visible as read | Clear unread state for the current matching list. |
| App restarts | Restore both the list and unread state. |

“New” means **newly detected by this monitor**, not proven original publication. Serialize filter-baseline changes with update application so a filter change during a request cannot generate a flood of false unread markers. Merely downloading or rendering an ad must not count as reading it.

## 10. Window and interaction design

Use a **resizable tray window**, initially around 620 × 760 logical pixels, with optional always-on-top and transparency. This supports long titles while retaining repo-watch's compact visual language. A separate tiny widget mode can come later.

The main surface should contain:

- A header showing refresh state and **Updated 2 minutes ago**, rather than implying continuous delivery with “Live”.
- A compact geographic selector with search, and worktime chips with clear selected states.
- A virtualized list: a two-line title, employer, municipality, worktime chip, publication time, and unread dot. Missing employer/location values receive neutral labels.
- A count labelled advertisements, plus an unread count. One ad can describe multiple vacancies.
- Refresh/F5, Settings, and Mark visible as read. Preserve scroll position; offer a “new ads available” affordance instead of constantly moving rows under the pointer.
- Distinct states for initial download, no matches, failed refresh with cached results, and first startup without data.

Open the supplied Platsbanken URL in the default browser after validating it as an expected HTTPS ad link. Keep full ad rendering out of v0.1. Escape/display API text as text.

Use restrained background activity and the moving refresh line for explicit refreshes. Respect reduced-motion preferences. Keep text readable when transparency is enabled and provide an opaque fallback. Restore window bounds within the current monitor work area after monitor or DPI changes.

Closing the window can hide it to the tray; Quit must terminate the process. Provide Show, Refresh, Pause monitoring, Settings, and Quit in the tray menu. Avalonia documents Windows/macOS tray support and conditional Linux support, so do not assume identical behavior on every Linux desktop. [S11]

## 11. Implementation sequence

| Step | Deliverable | Completion condition |
|---|---|---|
| 1. Verify contracts in the console test | Small API fixtures and measurements. | Confirm timestamp behavior, removals, missing fields, response sizes, and current throttling. Measure an actual snapshot before choosing download expectations. |
| 2. Create the project and taxonomy exporter | Core/Desktop skeleton and generated resource. | The geography selector works offline and reproduces the verified hierarchy. |
| 3. Add current-state persistence | Summary/state/checkpoint tables with migrations. | Cached rows and read state survive restart; data and checkpoint changes are atomic. |
| 4. Implement snapshot staging and replay | Initial population and recovery. | An interrupted download leaves the prior dataset intact; an edit during loading is correctly reconciled. |
| 5. Implement polling and lifecycle rules | Updates, removals, expiry, overlap, catch-up, retries. | Replaying a batch has no duplicate effect; failed writes never advance the checkpoint. |
| 6. Build the usable window | Filters, virtualized rows, unread actions, browser links. | Partial-region selections and missing worktime behave consistently before and after refresh. |
| 7. Add desktop behavior and package | Tray, bounds, theme, optional transparency, Windows portable release. | Sleep/resume and offline recovery work; a second instance cannot create a competing poller. |

For Windows, start with a self-contained publish folder or ZIP. An installer can wait. Keep notifications, occupation filters, full-text search, saved multiple watch profiles, and application tracking outside v0.1.

The tests should target failure-prone behavior: overlap/restart, older updates, removal payloads, location changes, expiry, unknown fields, timezone boundaries, and filter changes during refresh. Use captured fixtures for repeatability and a small live smoke check for contract drift. UI styling does not need a large test framework before the product works.

## 12. What this first release promises

The first release maintains a recent local view of current Platsbanken advertisements, offers clear location/worktime filtering, preserves unread state, and recovers from ordinary failures. Its freshness is bounded by upstream publication/indexing delay, the chosen poll interval, and the safety lag.

It does not promise the whole Swedish vacancy market. Arbetsförmedlingen describes JobAd Links as the service for broader market coverage; integrating it would require a separate provider and deduplication design. [S12]

My preferred starting decisions are therefore: **a separate repository; one resizable tray window; bundled geography; SQLite holding the latest summaries; snapshot plus Stream as the authoritative update path; and explicit unread semantics**. Keep JobSearch available as a later search feature or choose it alone if initial download size makes a search browser the better product.

## Sources and verification notes

Official sources accessed 6 October 2026. Live observations describe individual requests on that date and are not service guarantees. Repository source explains observed behavior but may differ from a deployed build. Some prose guides still contain legacy URLs; use the live Swagger schema to select current endpoints.

1. [S1 — JobStream live Swagger schema][S1]: `/v2/snapshot`, `/v2/stream`, query parameters, and supported response types.
2. [S2 — JobSearch live Swagger schema][S2]: search filters, sorting, and pagination limits. Retrieved directly as JSON during this review.
3. [S3 — Taxonomy geography documentation][S3]: Sweden hierarchy, concept ID, and administrative-code fields.
4. [S4 — Taxonomy getting started][S4]: GraphQL access, published-version read access, versioning, and local-copy guidance.
5. [S5 — JobStream service page][S5] and [JobSearch service page][S5b]: public access without registration/API keys.
6. [S6 — Avalonia 12.1.3][S6]: package availability and framework compatibility.
7. [S7 — Current JobStream getting-started guide][S7]: snapshot/update use, payload scale, removal shape, and stated polling limit. Read its raw Markdown; endpoint examples include legacy names.
8. [S8 — JobStream query and response implementation][S8]: current-state query construction and removal formatting. Read the public source, including snapshot and endpoint modules.
9. [S9 — .NET support policy][S9]: runtime lifecycle.
10. [S10 — Microsoft.Data.Sqlite overview][S10]: embedded database access without requiring Entity Framework.
11. [S11 — Avalonia TrayIcon documentation][S11]: desktop tray support and platform differences.
12. [S12 — JobAd Links service page][S12]: broader advertised market coverage.

Live probes: Taxonomy GraphQL traversing Sweden → regions → municipalities; a `worktime-extent` concept query; JobSearch with `municipality=1480`, with and without the part-time filter; and a one-minute JobStream interval. No full snapshot, sustained bandwidth benchmark, removal fixture, or daylight-saving transition test was performed for this report. These are explicit implementation gates, not claimed completed tests.

[S1]: https://jobstream.api.jobtechdev.se/swagger.json
[S2]: https://jobsearch.api.jobtechdev.se/swagger.json
[S3]: https://arbetsformedlingen.gitlab.io/taxonomy-dev/projects/jobtech-taxonomy/about/geography.html
[S4]: https://arbetsformedlingen.gitlab.io/taxonomy-dev/projects/jobtech-taxonomy/howto/getting-started.html
[S5]: https://data.arbetsformedlingen.se/dataservice/jobstream
[S5b]: https://data.arbetsformedlingen.se/dataservice/jobsearch
[S6]: https://www.nuget.org/packages/Avalonia/12.1.3
[S7]: https://gitlab.com/arbetsformedlingen/job-ads/JobStream/jobstream-api/-/blob/main/docs/GettingStartedJobStreamEN.md
[S8]: https://gitlab.com/arbetsformedlingen/job-ads/JobStream/jobstream-api/-/blob/main/src/jobstream/load_ads/common.py
[S9]: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
[S10]: https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/
[S11]: https://docs.avaloniaui.net/controls/navigation/trayicon
[S12]: https://data.arbetsformedlingen.se/dataservice/jobad-links
