using System.Net;
using System.Text.Json;
using ArbetsWatch.Core.Ads;
using ArbetsWatch.Core.Details;

namespace ArbetsWatch.Web.Tests;

internal static class AdsApiLimits
{
    public const int MaxPublishedIds = AdsApi.MaxPublishedIds;
}

[Collection(WebCollection.Name)]
public sealed class ApiTests
{
    private static async Task<JsonElement> JsonAsync(WebFixture web, string path)
    {
        var response = await web.Http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string[] Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!)];

    [Fact]
    public async Task The_page_is_served_from_the_web_root()
    {
        await using var web = await WebFixture.StartAsync();

        var index = await web.Http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Contains("ARBETSWATCH", await index.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("text/javascript", (await web.Http.GetAsync("/js/app.js")).Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Paging_walks_the_whole_list_in_order()
    {
        var now = DateTimeOffset.UtcNow;
        AdSummary[] ads = [.. Enumerable.Range(1, 7).Select(i => WebFixture.Ad(i.ToString(System.Globalization.CultureInfo.InvariantCulture), published: now.AddHours(-i)))];
        await using var web = await WebFixture.StartAsync(snapshot: ads);

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await JsonAsync(web, $"/api/ads?all=1&limit=3{(cursor is null ? string.Empty : "&cursor=" + cursor)}");
            Assert.Equal(7, page.GetProperty("total").GetInt32());
            seen.AddRange(Ids(page));
            cursor = page.TryGetProperty("next", out var next) ? next.GetString() : null;
        }
        while (cursor is not null);

        Assert.Equal(["1", "2", "3", "4", "5", "6", "7"], seen);
    }

    [Fact]
    public async Task A_bookmark_cursor_starts_the_page_at_the_ad()
    {
        var now = DateTimeOffset.UtcNow;
        var published = now.AddHours(-2);
        await using var web = await WebFixture.StartAsync(snapshot: [WebFixture.Ad("1", published: now.AddHours(-1)), WebFixture.Ad("2", published: published), WebFixture.Ad("3", published: now.AddHours(-3))]);

        var page = await JsonAsync(web, $"/api/ads?all=1&inclusive=1&cursor={published.ToUnixTimeMilliseconds()}_2");

        Assert.Equal(["2", "3"], Ids(page));
    }

    [Fact]
    public async Task The_list_filters_by_place_worktime_and_search_and_validates_nothing_away()
    {
        await using var web = await WebFixture.StartAsync(snapshot: [WebFixture.Ad("1", headline: "Sjuksköterska natt"), WebFixture.Ad("2", worktimeId: "947z_JGS_Uk2"), WebFixture.Ad("3", worktimeId: null)]);

        Assert.Equal(3, Ids(await JsonAsync(web, "/api/ads?all=1")).Length);
        Assert.Equal(3, Ids(await JsonAsync(web, $"/api/ads?municipalities={WebFixture.Goteborg}")).Length);
        Assert.Empty(Ids(await JsonAsync(web, "/api/ads")));
        Assert.Equal(["1"], Ids(await JsonAsync(web, "/api/ads?all=1&worktime=1")));
        Assert.Equal(["2"], Ids(await JsonAsync(web, "/api/ads?all=1&worktime=2")));
        Assert.Equal(["3"], Ids(await JsonAsync(web, "/api/ads?all=1&worktime=4")));
        Assert.Equal(["1"], Ids(await JsonAsync(web, "/api/ads?all=1&q=" + Uri.EscapeDataString("\"sjuksköterska natt\""))));
    }

    [Fact]
    public async Task A_list_row_carries_only_what_the_list_shows()
    {
        await using var web = await WebFixture.StartAsync(snapshot: [WebFixture.Ad("1")]);

        var body = await (await web.Http.GetAsync("/api/ads?all=1")).Content.ReadAsStringAsync();

        Assert.DoesNotContain(WebFixture.DescriptionMarker, body, StringComparison.Ordinal);
        var item = JsonDocument.Parse(body).RootElement.GetProperty("items")[0];
        Assert.Equal(
            ["employer", "id", "place", "publishedUtc", "title", "url", "worktime", "worktimeKind"],
            item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Göteborg", item.GetProperty("place").GetString());
        Assert.Equal("full", item.GetProperty("worktimeKind").GetString());
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://evil.example/platsbanken/annonser/1")]
    [InlineData("http://arbetsformedlingen.se/platsbanken/annonser/1")]
    public async Task A_hostile_url_in_an_ad_never_becomes_a_link(string url)
    {
        await using var web = await WebFixture.StartAsync(snapshot: [WebFixture.Ad("1", url: url)]);

        var item = (await JsonAsync(web, "/api/ads?all=1")).GetProperty("items")[0];

        Assert.Equal("https://arbetsformedlingen.se/platsbanken/annonser/1", item.GetProperty("url").GetString());
    }

    [Fact]
    public async Task A_query_string_on_an_ad_link_is_dropped()
    {
        await using var web = await WebFixture.StartAsync(snapshot: [WebFixture.Ad("1", url: "https://arbetsformedlingen.se/platsbanken/annonser/1?utm=x")]);

        var item = (await JsonAsync(web, "/api/ads?all=1")).GetProperty("items")[0];

        Assert.Equal("https://arbetsformedlingen.se/platsbanken/annonser/1", item.GetProperty("url").GetString());
    }

    [Fact]
    public async Task The_cache_id_stays_the_same_while_the_database_does()
    {
        await using var web = await WebFixture.StartAsync();

        var first = (await JsonAsync(web, "/api/status")).GetProperty("cacheId").GetString();

        Assert.False(string.IsNullOrEmpty(first));
        Assert.Equal(first, (await JsonAsync(web, "/api/status")).GetProperty("cacheId").GetString());
    }

    [Fact]
    public async Task Markup_in_an_ad_is_sent_as_json_text()
    {
        const string headline = "<img src=x onerror=alert(1)>";
        await using var web = await WebFixture.StartAsync(snapshot: [WebFixture.Ad("1", headline: headline)]);

        var response = await web.Http.GetAsync("/api/ads?all=1");

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var item = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("items")[0];
        Assert.Equal(headline, item.GetProperty("title").GetString());
    }

    [Fact]
    public async Task New_ads_are_reported_since_a_time()
    {
        await using var web = await WebFixture.StartAsync();

        var all = await JsonAsync(web, "/api/ads/new?all=1&since=0");
        Assert.Equal(3, Ids(all).Length);
        Assert.False(all.GetProperty("truncated").GetBoolean());

        var future = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();
        Assert.Empty(Ids(await JsonAsync(web, $"/api/ads/new?all=1&since={future}")));
    }

    [Fact]
    public async Task Published_ids_are_those_in_the_cache()
    {
        await using var web = await WebFixture.StartAsync();

        var ids = (await JsonAsync(web, "/api/ads/published?ids=100,999")).GetProperty("ids");

        Assert.Equal("100", Assert.Single(ids.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Status_reports_phases_and_the_translation_state_only()
    {
        await using var web = await WebFixture.StartAsync();

        var status = await JsonAsync(web, "/api/status");

        Assert.True(status.GetProperty("hasBaseline").GetBoolean());
        Assert.Equal(3, status.GetProperty("totalAds").GetInt32());
        Assert.Equal("NotConfigured", status.GetProperty("translation").GetString());
        Assert.False(status.TryGetProperty("message", out _));
    }

    [Fact]
    public async Task Places_come_from_the_bundled_catalog()
    {
        await using var web = await WebFixture.StartAsync();

        var regions = await JsonAsync(web, "/api/places");

        Assert.Equal(21, regions.GetArrayLength());
        Assert.Equal(290, regions.EnumerateArray().Sum(r => r.GetProperty("municipalities").GetArrayLength()));
    }

    [Fact]
    public async Task English_titles_need_a_key_and_the_asking()
    {
        await using var plain = await WebFixture.StartAsync();
        var item = (await JsonAsync(plain, "/api/ads?all=1&english=1")).GetProperty("items")[0];
        Assert.False(item.TryGetProperty("titleEn", out _));
        Assert.Empty(plain.Translator.Sent);

        await using var keyed = await WebFixture.StartAsync(key: WebFixture.DeepLKey);
        item = (await JsonAsync(keyed, "/api/ads?all=1")).GetProperty("items")[0];
        Assert.False(item.TryGetProperty("titleEn", out _));
        Assert.Empty(keyed.Translator.Sent);

        item = (await JsonAsync(keyed, "/api/ads?all=1&english=1")).GetProperty("items")[0];
        Assert.Equal("en:Testjobb", item.GetProperty("titleEn").GetString());
    }

    [Fact]
    public async Task Only_titles_of_cached_ads_are_sent_for_translation()
    {
        await using var web = await WebFixture.StartAsync(snapshot: [WebFixture.Ad("1", headline: "Lagerarbetare")], key: WebFixture.DeepLKey);

        await web.Http.GetAsync("/api/ads?all=1&english=1&q=" + Uri.EscapeDataString("lagerarbetare"));

        Assert.Equal(["Lagerarbetare"], web.Translator.Sent.Distinct());
    }

    [Fact]
    public async Task Details_map_through_and_a_missing_ad_is_a_404()
    {
        var details = new StubDetails();
        await using var web = await WebFixture.StartAsync(details: details);

        var found = await JsonAsync(web, "/api/ads/123/details");
        Assert.Equal("123", found.GetProperty("id").GetString());
        Assert.Equal("<script>alert(1)</script>", found.GetProperty("description").GetString());

        details.Answer = _ => new AdDetailsResult(AdDetailsStatus.NotFound);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync("/api/ads/123/details")).StatusCode);
    }
}
