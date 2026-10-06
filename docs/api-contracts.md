# API contracts

What ArbetsWatch relies on from the JobTech APIs, and how each point was established. Live observations were made on **2026-10-06** (Swedish summer time, UTC+2) without API keys. They describe single requests on that date, not service guarantees. "Source" means the public JobStream implementation (`gitlab.com/arbetsformedlingen/job-ads/JobStream/jobstream-api`, branch `main`, read on the same date), which may differ from the deployed build.

## JobStream endpoints

Base URL `https://jobstream.api.jobtechdev.se`. Verified against the live `swagger.json`.

| Endpoint | Parameters | Notes |
|---|---|---|
| `GET /v2/snapshot` | none | All currently published ads. No filters. |
| `GET /v2/stream` | `updated-after` (required), `updated-before`, `location-concept-id` (repeatable), `occupation-concept-id` (repeatable) | Ads changed in the interval, including removals. No worktime filter. |

Both produce `application/json` (one array) and `application/jsonl` (one object per line). ArbetsWatch always sends `Accept: application/jsonl` and reads line by line. The legacy `/stream`, `/snapshot` and `/feed` endpoints are deprecated and not used.

A malformed date returns `400` with a JSON body such as `{"errors": {"updated-after": "Invalid date literal \"garbage\""}, ...}`. ArbetsWatch must not retry a 400.

## Snapshot measurement

One download from a home connection, 2026-10-06 18:59:40Z – 19:00:48Z:

| Measure | Value |
|---|---|
| HTTP status | 200, `content-type: application/jsonl`, chunked (no `Content-Length`) |
| Transfer | 449,429,765 bytes (≈ 449 MB, uncompressed) in 68.3 s (≈ 6.6 MB/s) |
| Ads | 40,844, no duplicate IDs, none with `removed: true` |
| Longest line | 22,595 characters |
| Parse (Python, line by line) | 2.7 s |
| Compact summary size | ≈ 13 MB for all ads (id, headline, employer, URL, dates, worktime) |
| Worktime | Heltid 30,156 · Deltid 6,075 · missing 4,613 (11.3 %) |
| No `municipality_concept_id` | 975 |
| Workplace country not Sweden | 221 |
| `publication_date` range | 2025-07-09T15:34:40 … 2026-10-06T20:58:45 |
| `last_publication_date` range | 2026-10-06T23:59:59 … 2027-04-04T23:59:59 |

Consequences: the snapshot must be streamed into staging, never buffered or deserialized as a whole. Since there is no `Content-Length`, completeness is judged by the response ending normally and every line parsing. Download time dominates; local processing is cheap. The guide's "about 300 MB" is out of date.

The measurement program was a throw-away `curl` + Python script; memory use of the .NET implementation is measured in M3.

## Time semantics

**Server zone.** The service's Dockerfile sets `TZ=Europe/Stockholm`. Query bounds are parsed with `flask_restx.inputs.datetime_from_iso8601`, then:

- converted with Python `datetime.timestamp()` to epoch seconds, truncated, × 1000, and compared against the ad's `timestamp` field (`gte` after, `lte` before);
- formatted as `%Y-%m-%dT%H:%M:%SZ` (wall-clock digits with a literal `Z`) and compared against `publication_date`.

The two comparisons are OR-ed. An ad also has to be currently published: `publication_date <= now` and `last_publication_date >= now` (minute-rounded, Stockholm wall clock).

**Offset-free bounds** are read as Stockholm local time. Live check: the interval `2026-10-06T19:00:00`–`19:10:00` returned 14 ads, the same 14 IDs as `19:00:00+02:00`–`19:10:00+02:00`, and every active ad's `timestamp` fell within `17:00:00Z`–`17:10:00Z`.

**`Z` bounds are wrong for this API.** `17:00:00Z`–`17:10:00Z` returned 29 ads: the `timestamp` clause is correct, but the `publication_date` clause compares the digits `17:00`–`17:10` against Stockholm wall-clock strings and adds unrelated ads published two hours earlier.

**ArbetsWatch policy:** send each bound as Stockholm wall-clock time **with the explicit Stockholm offset for that instant** (`2026-10-06T19:00:00+02:00`, URL-encoded `%2B`). The `timestamp` clause is then exact even in the repeated autumn hour, and the `publication_date` clause sees the right digits. Live check across both transitions: `2026-03-29T00:00:00+01:00`–`06:00:00+02:00` and `2025-10-26T00:00:00+02:00`–`06:00:00+01:00` were accepted (200) and returned 783 and 1,173 removals with `removed_date` inside the expected local range. In the repeated hour, the `publication_date` clause stays ambiguous, so the overlap must cover it (see Polling policy).

**Precision and inclusivity.** Bounds have whole-second precision (fractions are accepted but truncated). Both ends are inclusive at `.000` milliseconds of the second given. Live check: a removal with `removed_date 04:36:13` was returned by `[04:36:13, 04:36:14]` but not by `[04:36:13, 04:36:13]` or `[04:36:14, 04:36:15]`. So its `timestamp` was 04:36:13.xxx, and consecutive intervals `[a, b]`, `[b, c]` leave no gap. ArbetsWatch floors interval ends to whole seconds.

**Fields in ads:**

| Field | Format | Meaning |
|---|---|---|
| `timestamp` | integer, epoch **milliseconds**, UTC | Last change. Active ads only. |
| `publication_date`, `last_publication_date`, `application_deadline` | `YYYY-MM-DDTHH:MM:SS`, no offset | Stockholm wall-clock. Example: `publication_date 2026-10-06T19:02:25` with `timestamp` = `17:02:25.951Z`. |
| `removed_date` | `YYYY-MM-DDTHH:MM:SS`, no offset | Stockholm wall-clock, seconds (the floor of the removal's internal timestamp). |

Offset-free values are converted with the Stockholm zone rules. In the repeated autumn hour, the earlier (summer-time) instant is chosen; this can misorder states by one hour within that hour only, which weekly snapshot reconciliation repairs.

## Records in a stream response

- **Active ad** (`removed: false`): the full ad. Shape documented in `tests/ArbetsWatch.Core.Tests/Fixtures`.
- **Removal** (`removed: true`): `id`, `removed`, `removed_date`, `occupation`/`occupation_group`/`occupation_field` (each `{concept_id}` only), and top-level `municipality`, `region`, `country` holding **concept IDs**, not labels. No `timestamp`, no headline, no `workplace_address`.

Observations from a 32-hour national interval (7,368 records): each ID appeared **once** per response (the latest state), so a single response never contains two states of the same ad. Removals were grouped at the end (positions 4,338–7,367). ArbetsWatch does not rely on order. None of the 3,030 removed IDs was in a snapshot taken later. Every removal had a `removed_date`.

**Tombstone retention.** Removals from 2025-10-26 (≈ 11 months before) were still returned. This is an observation, not a documented retention promise.

**Nightly removals.** Most removals carry `removed_date` around 00:36 local time (e.g. the 2025-10-26 sample spans 00:36:11–00:37:18). This looks like a nightly job unpublishing expired ads, but the API does not promise a removal for every natural expiry, and the stream's base query hides ads whose `last_publication_date` has passed. ArbetsWatch therefore expires ads locally when `last_publication_date` (Stockholm) is in the past, and treats a later removal for an unknown or already expired ID as a no-op.

## Ordering of states

Within one response each ID appears once. Across overlapping intervals, the same ad can arrive in several batches. ArbetsWatch orders states by a **source change instant**: `timestamp` for active ads, `removed_date` (converted from Stockholm) for removals. Removals only have second precision, so comparisons use whole seconds. An incoming state is applied unless it is strictly older than the stored one; on a tie, the later batch wins. Snapshot rows use their own `timestamp`.

## Rate limits and failure behavior

The official guide says "The rate limit is one request per minute" and suggests polling every minute. No rate-limit headers (`Retry-After`, `X-RateLimit-*`) were present, and five requests within a few seconds all returned 200. ArbetsWatch still enforces at least 60 s between stream requests, shared by timer and manual refresh, and honours `Retry-After` and 429/503 if they ever appear.

## Taxonomy

REST: `GET https://taxonomy.api.jobtechdev.se/v1/taxonomy/main/concepts?type=municipality&relation=narrower&related-ids=<region id>` lists a region's municipalities (checked live for Västra Götaland). Sweden is concept `i46j_HmG_v64`. Worktime concepts: Heltid `6YE1_gAC_R2G`, Deltid `947z_JGS_Uk2`. A user-supplied `all-concepts.json` export (43,695 concepts, 8.5 MB) has 290 `municipality` and 1,519 `region` concepts (most not Swedish) and **no parent relations**, so it cannot build the hierarchy; `tools/TaxonomyExport` uses the API.

## Polling policy (app policy, not API guarantees)

- Interval `[C − 5 min overlap, E]`, where `E = floor_to_second(now − 2 min safety lag)` is fixed before the request, and `C` is the last committed `E`.
- Minimum 60 s between stream requests; default poll every 5 min, configurable 1–60 min.
- The overlap also covers the ambiguous `publication_date` clause in the repeated autumn hour only partly; a larger replay can be added if gaps are observed.
- These settings do not protect against arbitrarily late upstream indexing.

## Open questions

- Whether an ad that is unpublished and later re-published keeps its ID and gets a new `timestamp`. Assumed yes; handled as an ordinary state change.
- Whether `timestamp` changes for every field edit, or only some.
- Actual winter-time (`+01:00`) behavior of live polling; only historical intervals were probed.
- Removal retention period and whether every manual unpublish yields a removal.
- Whether snapshot and stream are served from the same index generation at a given moment (assumed eventually consistent).
