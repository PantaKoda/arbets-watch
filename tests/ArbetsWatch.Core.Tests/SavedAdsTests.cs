using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Storage;
using Microsoft.Data.Sqlite;
using static ArbetsWatch.Core.Tests.TempStore;

namespace ArbetsWatch.Core.Tests;

public sealed class SavedAdsTests
{
    private static readonly AdFilter Malmo = new() { MunicipalityIds = new HashSet<string> { "oYPt_yRA_Smm" } };

    [Fact]
    public async Task Saved_ads_are_starred_in_the_list_and_listed_whatever_the_filters()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1"), Fixtures.Ad("2"));

        Assert.True(await temp.Store.SaveAsync("1", T0));
        Assert.False(await temp.Store.SaveAsync("unknown", T0));

        var rows = await temp.RowsAsync();
        Assert.True(rows["1"].IsSaved);
        Assert.False(rows["2"].IsSaved);
        Assert.Empty(await temp.RowsAsync(Malmo));

        var saved = Assert.Single(await temp.Store.QuerySavedAsync(T0));
        Assert.Equal("1", saved.Ad.Id);
        Assert.True(saved.IsSaved);
        Assert.True(saved.IsPublished);
        Assert.Equal(T0, saved.FirstSeenUtc);
        Assert.Equal(1, await temp.Store.CountSavedAsync());
    }

    [Fact]
    public async Task Newest_save_comes_first_and_saving_again_keeps_the_first_time()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1"), Fixtures.Ad("2"));
        await temp.Store.SaveAsync("1", T0);
        await temp.Store.SaveAsync("2", T0.AddMinutes(1));
        await temp.Store.SaveAsync("1", T0.AddMinutes(2));

        var saved = await temp.Store.QuerySavedAsync(T0);
        Assert.Equal(["2", "1"], saved.Select(r => r.Ad.Id));
        Assert.Equal(T0, saved[1].FirstSeenUtc);
    }

    [Fact]
    public async Task Saved_copy_follows_changes_and_outlives_removal_expiry_and_pruning()
    {
        var policy = new StorePolicy { InactiveRetention = TimeSpan.FromDays(1) };
        using var temp = new TempStore(policy);
        await temp.BaselineAsync(Fixtures.Ad("1"), Fixtures.Ad("2"), Fixtures.Ad("3"));
        foreach (var id in new[] { "1", "2", "3" })
        {
            await temp.Store.SaveAsync(id, T0);
        }

        var end = T0.AddMinutes(5);
        await temp.Store.CommitBatchAsync(
            [
                Fixtures.Ad("1", changed: T0.AddMinutes(1), headline: "Ny rubrik"),
                Fixtures.Removal("2", T0.AddMinutes(1)),
                Fixtures.Ad("3", changed: T0.AddMinutes(1), headline: "Sista dagen", lastPublication: T0.AddMinutes(2)),
            ],
            AdFilter.Default, end, end);

        var saved = (await temp.Store.QuerySavedAsync(end)).ToDictionary(r => r.Ad.Id);
        Assert.Equal("Ny rubrik", saved["1"].Ad.Headline);
        Assert.True(saved["1"].IsPublished);
        Assert.False(saved["2"].IsPublished);
        Assert.Equal("Testjobb", saved["2"].Ad.Headline); // the last known state is kept
        Assert.False(saved["3"].IsPublished);
        Assert.Equal("Sista dagen", saved["3"].Ad.Headline);

        // Read state of gone ads is pruned after the retention period; saved ads are not.
        var later = end.AddDays(3);
        await temp.Store.CommitBatchAsync([], AdFilter.Default, later, later);
        Assert.Equal(3, (await temp.Store.QuerySavedAsync(later)).Count);
    }

    [Fact]
    public async Task An_ad_that_returns_is_published_again_with_its_new_details()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1"), Fixtures.Ad("2"));
        await temp.Store.SaveAsync("1", T0);

        // Absent from the next snapshot ...
        var start = T0.AddDays(1);
        await temp.Store.ResetStagingAsync();
        await temp.Store.StageSnapshotChunkAsync([Fixtures.Ad("2")]);
        await temp.Store.ActivateSnapshotAsync(AdFilter.Default, start, start, start);
        Assert.False(Assert.Single(await temp.Store.QuerySavedAsync(start)).IsPublished);

        // ... and back, changed.
        var end = start.AddMinutes(5);
        await temp.Store.CommitBatchAsync([Fixtures.Ad("1", changed: start.AddMinutes(1), headline: "Åter")], AdFilter.Default, end, end);
        var saved = Assert.Single(await temp.Store.QuerySavedAsync(end));
        Assert.True(saved.IsPublished);
        Assert.Equal("Åter", saved.Ad.Headline);
    }

    [Fact]
    public async Task Removing_from_saved_works_for_current_and_gone_ads_and_survives_restart()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1"), Fixtures.Ad("2"));
        await temp.Store.SaveAsync("1", T0);
        await temp.Store.SaveAsync("2", T0);
        var end = T0.AddMinutes(5);
        await temp.Store.CommitBatchAsync([Fixtures.Removal("2", T0.AddMinutes(1))], AdFilter.Default, end, end);

        Assert.True(await temp.Store.UnsaveAsync("1"));
        Assert.True(await temp.Store.UnsaveAsync("2"));
        Assert.False(await temp.Store.UnsaveAsync("2"));
        await temp.Store.SaveAsync("1", end);

        var store = temp.Reopen();
        Assert.Equal(["1"], (await store.QuerySavedAsync(end)).Select(r => r.Ad.Id));
        Assert.True((await temp.RowsAsync(now: end))["1"].IsSaved);
    }

    [Fact]
    public async Task Cache_from_0_1_3_gains_saved_ads()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1"));
        temp.Store.Dispose();
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TRIGGER tr_summary_update_saved;
                DROP TRIGGER tr_summary_insert_saved;
                DROP TABLE saved_ad;
                PRAGMA user_version = 3;
                """;
            downgrade.ExecuteNonQuery();
        }

        var store = temp.Reopen();
        Assert.True(await store.SaveAsync("1", T0));
        Assert.Single(await store.QuerySavedAsync(T0));
        Assert.False((await temp.RowsAsync())["1"].Unread);
    }
}
