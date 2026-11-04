using System.Globalization;
using System.Text.RegularExpressions;
using ArbetsWatch.Core.Details;
using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Places;
using ArbetsWatch.Core.Storage;
using ArbetsWatch.Core.Sync;
using ArbetsWatch.Core.Translation;

namespace ArbetsWatch.Web;

/// <summary>
/// The read-only JSON API. Every parameter is validated against what the app can actually use, and SQL stays
/// parameterized in Core. All answers are about public ads; nothing here knows who is asking.
/// </summary>
internal static partial class AdsApi
{
    internal const int DefaultPageSize = 100;
    internal const int MaxPageSize = 200;
    internal const int MaxNewAds = 20_000;
    internal const int MaxPublishedIds = 200;

    private static readonly TimeSpan TranslationWait = TimeSpan.FromSeconds(10);

    public static void Map(WebApplication app, WebAppDependencies deps)
    {
        var api = app.MapGroup("/api").RequireRateLimiting(WebApp.ApiPolicy);
        var cacheId = new Lazy<Task<string>>(() => GetOrCreateCacheIdAsync(deps.Store));

        api.MapGet("/status", async (CancellationToken ct) => await GetStatusAsync(deps, await cacheId.Value.ConfigureAwait(false), ct).ConfigureAwait(false));
        api.MapGet("/places", () => Results.Json(deps.Places.Regions.Select(r =>
            new PlaceRegion(r.Id, r.Label, [.. r.Municipalities.Select(m => new PlaceMunicipality(m.Id, m.Label))]))));
        api.MapGet("/ads", (HttpRequest request, CancellationToken ct) => GetAdsAsync(request, deps, ct));
        api.MapGet("/ads/new", (HttpRequest request, CancellationToken ct) => GetNewAsync(request, deps, ct));
        api.MapGet("/ads/published", (HttpRequest request, CancellationToken ct) => GetPublishedAsync(request, deps, ct));
        app.MapGet("/api/ads/{id}/details", (string id, CancellationToken ct) => GetDetailsAsync(id, deps, ct))
            .RequireRateLimiting(WebApp.UpstreamPolicy);
        app.MapFallback("/api/{**rest}", () => Results.Json(new ApiError("Not found."), statusCode: StatusCodes.Status404NotFound));
    }

    /// <summary>
    /// A random name for this database. A new database stamps every ad as first seen when it fills, so browsers need to
    /// tell it from the one they were watching, or they would take all of its ads for new ones.
    /// </summary>
    private static async Task<string> GetOrCreateCacheIdAsync(AdStore store)
    {
        const string key = "web.cacheId";
        if (await store.GetPreferenceAsync(key).ConfigureAwait(false) is { Length: > 0 } existing)
        {
            return existing;
        }

        var created = Guid.NewGuid().ToString("N");
        await store.SetPreferenceAsync(key, created).ConfigureAwait(false);
        return created;
    }

    private static async Task<IResult> GetStatusAsync(WebAppDependencies deps, string cacheId, CancellationToken ct)
    {
        var now = deps.Time.GetUtcNow();
        var status = deps.Coordinator.Status;
        var sync = await deps.Store.ReadSyncStateAsync(ct).ConfigureAwait(false);
        var total = await deps.Store.CountCurrentAsync(now, ct).ConfigureAwait(false);

        // Phases only, not the coordinator's message or anything derived from an exception. The translation answer
        // says whether titles can be translated, and nothing else about the key.
        return Results.Json(new StatusResponse(
            status.Phase.ToString(),
            sync.LastSuccessUtc,
            status.NextRunUtc,
            status.SnapshotAdsReceived,
            sync.BaselineEstablished,
            sync.BaselineEstablished && sync.LastSnapshotUtc is null,
            status.NeedsManualRetry,
            total,
            deps.Translation.Health.ToString(),
            cacheId,
            now));
    }

    private static async Task<IResult> GetAdsAsync(HttpRequest request, WebAppDependencies deps, CancellationToken ct)
    {
        if (!TryReadFilter(request.Query, deps.Places, out var filter, out var search, out var error) ||
            !TryReadCursor(request.Query["cursor"], out var cursor, out error) ||
            !TryReadInt(request.Query["limit"], DefaultPageSize, 1, MaxPageSize, out var limit, out error))
        {
            return BadRequest(error);
        }

        var now = deps.Time.GetUtcNow();
        var inclusive = request.Query["inclusive"] == "1";

        // One more than asked: a next page exists only if it came back.
        var rows = await deps.Store.QueryPageAsync(filter, now, limit + 1, cursor, inclusive, search, ct).ConfigureAwait(false);
        var page = rows.Take(limit).ToList();
        var total = await deps.Store.CountMatchingAsync(filter, now, search, ct).ConfigureAwait(false);
        var next = rows.Count > limit ? FormatCursor(page[^1]) : null;

        var translate = request.Query["english"] == "1" && deps.Translation.Health != TranslationHealth.NotConfigured;
        if (translate)
        {
            await TranslateAsync(deps.Translation, page, ct).ConfigureAwait(false);
        }

        var items = page.Select(row => AdItem.From(row, translate ? deps.Translation.TryGet(row.Ad.Headline.Trim()) : null)).ToList();
        return Results.Json(new AdPage(items, total, next, now));
    }

    /// <summary>
    /// Only titles of ads already in the cache are ever sent to DeepL, so this can't be used as a free translation
    /// service. A slow or failing translation leaves the originals showing rather than holding up the list.
    /// </summary>
    private static async Task TranslateAsync(TitleTranslationService translation, IEnumerable<AdRow> rows, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TranslationWait);
        try
        {
            await translation.EnsureTranslatedAsync(rows.Select(r => r.Ad.Headline), wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Took too long: the titles that were answered are cached and the rest show in Swedish for now.
        }
    }

    private static async Task<IResult> GetNewAsync(HttpRequest request, WebAppDependencies deps, CancellationToken ct)
    {
        if (!TryReadFilter(request.Query, deps.Places, out var filter, out var search, out var error))
        {
            return BadRequest(error);
        }

        if (!long.TryParse(request.Query["since"], NumberStyles.None, CultureInfo.InvariantCulture, out var sinceMs) || sinceMs > 253_402_300_799_000)
        {
            return BadRequest("since must be a time in milliseconds.");
        }

        // Read before querying: a sighting committed in between is reported again next time, never missed.
        var now = deps.Time.GetUtcNow();
        var ads = await deps.Store.QueryFirstSeenSinceAsync(filter, now, DateTimeOffset.FromUnixTimeMilliseconds(sinceMs), MaxNewAds + 1, search, ct)
            .ConfigureAwait(false);
        var items = ads.Take(MaxNewAds).Select(a => new NewAdItem(a.Id, a.FirstSeenUtc.ToUnixTimeMilliseconds())).ToList();
        return Results.Json(new NewAdsResponse(items, ads.Count > MaxNewAds, now));
    }

    private static async Task<IResult> GetPublishedAsync(HttpRequest request, WebAppDependencies deps, CancellationToken ct)
    {
        var ids = Split(request.Query["ids"]);
        if (ids.Length > MaxPublishedIds || !ids.All(id => AdId().IsMatch(id)))
        {
            return BadRequest("ids must be a short list of ad IDs.");
        }

        var published = await deps.Store.FilterPublishedAsync(ids, deps.Time.GetUtcNow(), ct).ConfigureAwait(false);
        return Results.Json(new PublishedResponse(published));
    }

    private static async Task<IResult> GetDetailsAsync(string id, WebAppDependencies deps, CancellationToken ct)
    {
        // Goes into the path of a request to a fixed host, so only an ad number may pass.
        if (!NumericId().IsMatch(id))
        {
            return BadRequest("Not an ad ID.");
        }

        var result = await deps.Details.GetAsync(id, ct).ConfigureAwait(false);
        return result.Status switch
        {
            AdDetailsStatus.Found => Results.Json(DetailsResponse.From(result.Details!)),
            AdDetailsStatus.NotFound => Results.Json(new ApiError("This ad is no longer published."), statusCode: StatusCodes.Status404NotFound),
            _ => Results.Json(new ApiError("The details could not be fetched from Arbetsförmedlingen."), statusCode: StatusCodes.Status502BadGateway),
        };
    }

    private static bool TryReadFilter(IQueryCollection query, PlaceCatalog places, out AdFilter filter, out TextSearch search, out string error)
    {
        filter = AdFilter.Default;
        search = TextSearch.None;
        error = string.Empty;

        var regions = Split(query["regions"]);
        var municipalities = Split(query["municipalities"]);
        if (!regions.All(id => places.FindRegion(id) is not null) || !municipalities.All(id => places.FindMunicipality(id) is not null))
        {
            error = "Unknown place.";
            return false;
        }

        if (!TryReadInt(query["worktime"], (int)WorktimeSet.All, 1, (int)WorktimeSet.All, out var worktime, out error))
        {
            return false;
        }

        var text = query["q"].ToString();
        if (text.Length > TextSearch.MaxLength)
        {
            error = "The search text is too long.";
            return false;
        }

        filter = new AdFilter
        {
            AllSweden = query["all"] == "1",
            RegionIds = new HashSet<string>(regions, StringComparer.Ordinal),
            MunicipalityIds = new HashSet<string>(municipalities, StringComparer.Ordinal),
            Worktime = (WorktimeSet)worktime,
        };
        search = TextSearch.Parse(text);
        return true;
    }

    /// <summary>A cursor is <c>publishedMs_id</c>, with nothing before the underscore for an ad without a time.</summary>
    private static bool TryReadCursor(string? text, out AdCursor? cursor, out string error)
    {
        cursor = null;
        error = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        var match = Cursor().Match(text);
        if (!match.Success)
        {
            error = "Bad cursor.";
            return false;
        }

        DateTimeOffset? published = null;
        if (match.Groups[1].Length > 0)
        {
            var ms = long.Parse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
            if (ms > 253_402_300_799_000)
            {
                error = "Bad cursor.";
                return false;
            }

            published = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        cursor = new AdCursor(published, match.Groups[2].Value);
        return true;
    }

    private static string FormatCursor(AdRow row) =>
        string.Create(CultureInfo.InvariantCulture, $"{row.Ad.PublishedUtc?.ToUnixTimeMilliseconds()}_{row.Ad.Id}");

    private static bool TryReadInt(string? text, int fallback, int min, int max, out int value, out string error)
    {
        error = string.Empty;
        value = fallback;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
        {
            return true;
        }

        error = "A number is out of range.";
        return false;
    }

    private static string[] Split(string? list) =>
        string.IsNullOrEmpty(list) ? [] : list.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static IResult BadRequest(string message) => Results.Json(new ApiError(message), statusCode: StatusCodes.Status400BadRequest);

    [GeneratedRegex(@"^[0-9]{1,15}\z")]
    private static partial Regex NumericId();

    [GeneratedRegex(@"^[0-9A-Za-z_-]{1,40}\z")]
    private static partial Regex AdId();

    [GeneratedRegex(@"^([0-9]{1,16})?_([0-9A-Za-z_-]{1,40})\z")]
    private static partial Regex Cursor();
}
