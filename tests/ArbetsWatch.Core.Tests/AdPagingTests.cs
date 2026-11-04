using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Storage;
using static ArbetsWatch.Core.Tests.TempStore;

namespace ArbetsWatch.Core.Tests;

public sealed class AdPagingTests
{
    private static readonly DateTimeOffset Morning = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

    private static async Task<TempStore> SeededAsync()
    {
        var temp = new TempStore();
        await temp.BaselineAsync(
            Fixtures.Ad("1", published: Morning),
            Fixtures.Ad("2", published: Morning),
            Fixtures.Ad("3", published: Morning.AddHours(1)),
            Fixtures.Ad("4", published: Morning.AddHours(-1)),
            Fixtures.Ad("5", published: Morning.AddHours(2), headline: "Sjuksköterska natt"),
            Fixtures.Ad("6", published: Morning.AddHours(-2)));
        return temp;
    }

    private static async Task<List<string>> AllPagesAsync(AdStore store, AdFilter filter, int size, TextSearch? search = null)
    {
        var ids = new List<string>();
        AdCursor? cursor = null;
        while (true)
        {
            var page = await store.QueryPageAsync(filter, T0, size, cursor, search: search);
            ids.AddRange(page.Select(r => r.Ad.Id));
            if (page.Count < size)
            {
                return ids;
            }

            cursor = new AdCursor(page[^1].Ad.PublishedUtc, page[^1].Ad.Id);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(100)]
    public async Task Pages_add_up_to_the_full_list_in_the_same_order(int size)
    {
        using var temp = await SeededAsync();

        var full = await temp.Store.QueryAsync(AdFilter.Default, T0);

        Assert.Equal(full.Select(r => r.Ad.Id), await AllPagesAsync(temp.Store, AdFilter.Default, size));
    }

    [Fact]
    public async Task Ads_without_a_publication_time_page_after_the_rest()
    {
        using var temp = new TempStore();
        await temp.Store.ResetStagingAsync();
        await temp.Store.StageSnapshotChunkAsync([
            Fixtures.Ad("1", published: Morning),
            Fixtures.Ad("2", published: Morning.AddHours(1)),
            Fixtures.Ad("3") with { PublishedUtc = null },
            Fixtures.Ad("4") with { PublishedUtc = null },
        ]);
        await temp.Store.ActivateSnapshotAsync(AdFilter.Default, T0, T0, T0);

        var full = await temp.Store.QueryAsync(AdFilter.Default, T0);
        Assert.Equal(["2", "1", "4", "3"], full.Select(r => r.Ad.Id));
        Assert.Equal(["2", "1", "4", "3"], await AllPagesAsync(temp.Store, AdFilter.Default, 1));
    }

    [Fact]
    public async Task An_ad_arriving_between_pages_neither_repeats_nor_skips_a_row()
    {
        using var temp = await SeededAsync();
        var first = await temp.Store.QueryPageAsync(AdFilter.Default, T0, 2);

        var end = T0.AddMinutes(5);
        await temp.Store.CommitBatchAsync([Fixtures.Ad("9", published: Morning.AddHours(5), changed: T0.AddMinutes(1))], AdFilter.Default, end, end);

        var second = await temp.Store.QueryPageAsync(AdFilter.Default, T0, 2, new AdCursor(first[^1].Ad.PublishedUtc, first[^1].Ad.Id));
        Assert.Equal(["5", "3"], first.Select(r => r.Ad.Id));
        Assert.Equal(["2", "1"], second.Select(r => r.Ad.Id));
    }

    [Fact]
    public async Task An_inclusive_cursor_starts_at_the_bookmark_or_the_next_ad_after_it()
    {
        using var temp = await SeededAsync();

        var exact = await temp.Store.QueryPageAsync(AdFilter.Default, T0, 2, new AdCursor(Morning, "2"), inclusive: true);
        Assert.Equal(["2", "1"], exact.Select(r => r.Ad.Id));

        var gone = await temp.Store.QueryPageAsync(AdFilter.Default, T0, 2, new AdCursor(Morning.AddMinutes(30), "77"), inclusive: true);
        Assert.Equal(["2", "1"], gone.Select(r => r.Ad.Id));
    }

    [Fact]
    public async Task Counting_applies_the_filter_the_search_and_expiry()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(
            Fixtures.Ad("1", headline: "Sjuksköterska natt"),
            Fixtures.Ad("2", worktimeId: "947z_JGS_Uk2"),
            Fixtures.Ad("3", lastPublication: T0.AddDays(-1)));

        Assert.Equal(2, await temp.Store.CountMatchingAsync(AdFilter.Default, T0));
        Assert.Equal(1, await temp.Store.CountMatchingAsync(AdFilter.Default with { Worktime = WorktimeSet.PartTime }, T0));
        Assert.Equal(1, await temp.Store.CountMatchingAsync(AdFilter.Default, T0, TextSearch.Parse("natt")));
        Assert.Equal(0, await temp.Store.CountMatchingAsync(new AdFilter(), T0));
    }

    [Fact]
    public async Task First_sightings_since_a_time_respect_the_filter_and_the_limit()
    {
        using var temp = await SeededAsync();
        var later = T0.AddMinutes(5);
        await temp.Store.CommitBatchAsync(
            [Fixtures.Ad("8", changed: T0.AddMinutes(1)), Fixtures.Ad("9", worktimeId: "947z_JGS_Uk2", changed: T0.AddMinutes(1))],
            AdFilter.Default, later, later);

        var seenNow = await temp.Store.QueryFirstSeenSinceAsync(AdFilter.Default, T0, T0.AddSeconds(1), 10);
        Assert.DoesNotContain(seenNow, a => a.Id is "1" or "2");

        var all = await temp.Store.QueryFirstSeenSinceAsync(AdFilter.Default, T0, DateTimeOffset.UnixEpoch, 100);
        Assert.Equal(8, all.Count);
        Assert.Equal(2, (await temp.Store.QueryFirstSeenSinceAsync(AdFilter.Default, T0, DateTimeOffset.UnixEpoch, 2)).Count);

        var partTime = await temp.Store.QueryFirstSeenSinceAsync(AdFilter.Default with { Worktime = WorktimeSet.PartTime }, T0, DateTimeOffset.UnixEpoch, 100);
        Assert.Equal("9", Assert.Single(partTime).Id);
    }

    [Fact]
    public async Task Filtering_published_ids_leaves_out_expired_removed_and_unknown_ads()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1"), Fixtures.Ad("2", lastPublication: T0.AddDays(-1)), Fixtures.Ad("3"));
        var end = T0.AddMinutes(5);
        await temp.Store.CommitBatchAsync([Fixtures.Removal("3", T0.AddMinutes(1))], AdFilter.Default, end, end);

        Assert.Equal(["1"], await temp.Store.FilterPublishedAsync(["1", "2", "3", "4"], T0));
        Assert.Empty(await temp.Store.FilterPublishedAsync([], T0));
    }
}
