using System.Collections;
using ArbetsWatch.Core.Ads;
using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Storage;
using Microsoft.Data.Sqlite;
using static ArbetsWatch.Core.Tests.TempStore;

namespace ArbetsWatch.Core.Tests;

public sealed class AdStoreTests
{
    private const string Goteborg = "PVZL_BQT_XtL";
    private const string Malmo = "oYPt_yRA_Smm";
    private const string Skane = "CaRE_1nn_cSU";

    private static readonly AdFilter GoteborgOnly = new() { MunicipalityIds = new HashSet<string> { Goteborg } };

    [Fact]
    public async Task First_baseline_has_no_unread_ads()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1"), Fixtures.Ad("2"));

        var rows = await temp.RowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows.Values, r => Assert.False(r.Unread));
        Assert.True((await temp.Store.ReadSyncStateAsync()).BaselineEstablished);
    }

    [Fact]
    public async Task Data_read_state_and_checkpoint_survive_restart()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1"));
        var end = T0.AddMinutes(5);
        await temp.Store.CommitBatchAsync([Fixtures.Ad("2", changed: T0.AddMinutes(1))], AdFilter.Default, end, end);
        await temp.Store.SetPreferenceAsync("app", "{\"x\":1}");

        temp.Reopen();

        var rows = await temp.RowsAsync();
        Assert.False(rows["1"].Unread);
        Assert.True(rows["2"].Unread);
        var sync = await temp.Store.ReadSyncStateAsync();
        Assert.Equal(end, sync.CommittedThroughUtc);
        Assert.Equal(end, sync.LastSuccessUtc);
        Assert.Equal("{\"x\":1}", await temp.Store.GetPreferenceAsync("app"));
    }

    [Fact]
    public async Task Failed_batch_rolls_back_rows_and_checkpoint()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1"));
        var before = await temp.Store.ReadSyncStateAsync();

        var failing = new FailingList([Fixtures.Ad("2"), Fixtures.Removal("1", T0.AddMinutes(1))], failAt: 2);
        await Assert.ThrowsAsync<IOException>(() =>
            temp.Store.CommitBatchAsync(failing, AdFilter.Default, T0.AddMinutes(5), T0.AddMinutes(5)));

        var rows = await temp.RowsAsync();
        Assert.Equal(["1"], rows.Keys);
        Assert.Equal(before, await temp.Store.ReadSyncStateAsync());
    }

    [Fact]
    public async Task Cancelled_batch_leaves_the_checkpoint_unchanged()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            temp.Store.CommitBatchAsync([Fixtures.Ad("2")], AdFilter.Default, T0.AddMinutes(5), T0.AddMinutes(5), cts.Token));

        Assert.Equal(T0, (await temp.Store.ReadSyncStateAsync()).CommittedThroughUtc);
        Assert.Single(await temp.RowsAsync());
    }

    [Fact]
    public async Task Replaying_the_same_batch_has_no_further_effect()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1"));
        IReadOnlyList<SourceRecord> batch =
        [
            Fixtures.Ad("2", changed: T0.AddMinutes(1)),
            Fixtures.Ad("1", changed: T0.AddMinutes(1), headline: "Edited"),
            Fixtures.Removal("3", T0.AddMinutes(1)),
        ];

        var first = await temp.Store.CommitBatchAsync(batch, AdFilter.Default, T0.AddMinutes(5), T0.AddMinutes(5));
        await temp.Store.MarkReadAsync("2");
        var second = await temp.Store.CommitBatchAsync(batch, AdFilter.Default, T0.AddMinutes(10), T0.AddMinutes(10));

        Assert.Equal(1, first.NewUnread);
        Assert.Equal(0, second.NewUnread);
        var rows = await temp.RowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.False(rows["2"].Unread);
        Assert.Equal("Edited", rows["1"].Ad.Headline);
    }

    [Fact]
    public async Task Older_states_are_rejected()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync();
        var newer = T0.AddSeconds(5).AddMilliseconds(500);
        await temp.Store.CommitBatchAsync([Fixtures.Ad("1", changed: newer, headline: "Newer")], AdFilter.Default, T0.AddMinutes(1), T0.AddMinutes(1));

        var result = await temp.Store.CommitBatchAsync(
            [Fixtures.Ad("1", changed: newer.AddMilliseconds(-300), headline: "Older")], AdFilter.Default, T0.AddMinutes(2), T0.AddMinutes(2));

        Assert.Equal(1, result.Stale);
        Assert.Equal("Newer", (await temp.RowsAsync())["1"].Ad.Headline);
    }

    [Fact]
    public async Task Removal_in_the_same_second_as_the_ad_state_wins()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync();
        await temp.Store.CommitBatchAsync([Fixtures.Ad("1", changed: T0.AddSeconds(5).AddMilliseconds(700))], AdFilter.Default, T0.AddMinutes(1), T0.AddMinutes(1));

        // removed_date has whole seconds only.
        var result = await temp.Store.CommitBatchAsync([Fixtures.Removal("1", T0.AddSeconds(5))], AdFilter.Default, T0.AddMinutes(2), T0.AddMinutes(2));

        Assert.Equal(1, result.Removed);
        Assert.Empty(await temp.RowsAsync());
    }

    [Fact]
    public async Task Removal_of_an_unknown_id_blocks_an_older_replayed_ad()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync();
        await temp.Store.CommitBatchAsync([Fixtures.Removal("9", T0.AddMinutes(1))], AdFilter.Default, T0.AddMinutes(2), T0.AddMinutes(2));

        var result = await temp.Store.CommitBatchAsync([Fixtures.Ad("9", changed: T0)], AdFilter.Default, T0.AddMinutes(3), T0.AddMinutes(3));

        Assert.Equal(1, result.Stale);
        Assert.Empty(await temp.RowsAsync());
    }

    [Fact]
    public async Task Republished_ad_keeps_its_read_state()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync();
        await temp.Store.CommitBatchAsync([Fixtures.Ad("1", changed: T0.AddMinutes(1))], AdFilter.Default, T0.AddMinutes(2), T0.AddMinutes(2));
        await temp.Store.MarkReadAsync("1");
        await temp.Store.CommitBatchAsync([Fixtures.Removal("1", T0.AddMinutes(3))], AdFilter.Default, T0.AddMinutes(4), T0.AddMinutes(4));

        await temp.Store.CommitBatchAsync([Fixtures.Ad("1", changed: T0.AddMinutes(5))], AdFilter.Default, T0.AddMinutes(6), T0.AddMinutes(6));

        Assert.False((await temp.RowsAsync())["1"].Unread);
    }

    [Fact]
    public async Task Id_first_seen_as_a_removal_becomes_unread_when_it_appears_as_an_ad()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync();
        await temp.Store.CommitBatchAsync([Fixtures.Removal("9", T0.AddMinutes(1))], AdFilter.Default, T0.AddMinutes(2), T0.AddMinutes(2));

        var result = await temp.Store.CommitBatchAsync([Fixtures.Ad("9", changed: T0.AddMinutes(3))], AdFilter.Default, T0.AddMinutes(4), T0.AddMinutes(4));

        Assert.Equal(1, result.NewUnread);
        Assert.True((await temp.RowsAsync())["9"].Unread);
    }

    [Fact]
    public async Task Snapshot_is_authoritative_and_restores_a_republished_ad_with_an_older_timestamp()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1", changed: T0.AddMinutes(-10)));
        await temp.Store.CommitBatchAsync([Fixtures.Removal("1", T0.AddMinutes(1))], AdFilter.Default, T0.AddMinutes(2), T0.AddMinutes(2));

        // Re-published keeping its old timestamp, older than the stored removal.
        await temp.Store.ResetStagingAsync();
        await temp.Store.StageSnapshotChunkAsync([Fixtures.Ad("1", changed: T0.AddMinutes(-10))]);
        await temp.Store.ActivateSnapshotAsync(AdFilter.Default, T0.AddMinutes(5), T0.AddMinutes(5), T0.AddMinutes(5));

        Assert.Single(await temp.RowsAsync(now: T0.AddMinutes(5)));
    }

    [Fact]
    public async Task Empty_or_much_smaller_snapshot_is_rejected_and_the_cache_kept()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1"), Fixtures.Ad("2"), Fixtures.Ad("3"), Fixtures.Ad("4"));
        var before = await temp.Store.ReadSyncStateAsync();

        await temp.Store.ResetStagingAsync();
        await Assert.ThrowsAsync<SnapshotRejectedException>(() => temp.Store.ActivateSnapshotAsync(AdFilter.Default, T0.AddDays(8), T0.AddDays(8), T0.AddDays(8)));
        await temp.Store.StageSnapshotChunkAsync([Fixtures.Ad("1")]);
        await Assert.ThrowsAsync<SnapshotRejectedException>(() => temp.Store.ActivateSnapshotAsync(AdFilter.Default, T0.AddDays(8), T0.AddDays(8), T0.AddDays(8)));

        Assert.Equal(4, (await temp.RowsAsync()).Count);
        Assert.Equal(before, await temp.Store.ReadSyncStateAsync());
    }

    [Fact]
    public async Task Reconciliation_never_marks_an_expired_ad_unread()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1"));
        var now = T0.AddDays(8);

        await temp.Store.ResetStagingAsync();
        await temp.Store.StageSnapshotChunkAsync([Fixtures.Ad("1"), Fixtures.Ad("expired", lastPublication: now.AddMinutes(-1)), Fixtures.Ad("new")]);
        var result = await temp.Store.ActivateSnapshotAsync(AdFilter.Default, now, now, now);

        Assert.Equal(1, result.NewUnread);
        Assert.True((await temp.RowsAsync(now: now))["new"].Unread);
    }

    [Fact]
    public async Task Unread_requires_a_match_at_detection_and_filter_expansion_creates_none()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync();
        await temp.Store.CommitBatchAsync(
            [Fixtures.Ad("gbg", Goteborg, changed: T0.AddMinutes(1)), Fixtures.Ad("mmo", Malmo, Skane, changed: T0.AddMinutes(1))],
            GoteborgOnly, T0.AddMinutes(2), T0.AddMinutes(2));

        var all = await temp.RowsAsync(AdFilter.Default);
        Assert.True(all["gbg"].Unread);
        Assert.False(all["mmo"].Unread);
    }

    [Fact]
    public async Task Known_ad_that_starts_matching_after_an_edit_is_not_new()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1", Malmo, Skane));

        await temp.Store.CommitBatchAsync([Fixtures.Ad("1", Goteborg, changed: T0.AddMinutes(1))], GoteborgOnly, T0.AddMinutes(2), T0.AddMinutes(2));

        var rows = await temp.RowsAsync(GoteborgOnly);
        Assert.False(rows["1"].Unread);
    }

    [Fact]
    public async Task Location_change_moves_an_ad_out_of_the_filter()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1", Goteborg));
        Assert.Single(await temp.RowsAsync(GoteborgOnly));

        await temp.Store.CommitBatchAsync([Fixtures.Ad("1", Malmo, Skane, changed: T0.AddMinutes(1))], GoteborgOnly, T0.AddMinutes(2), T0.AddMinutes(2));

        Assert.Empty(await temp.RowsAsync(GoteborgOnly));
        Assert.Single(await temp.RowsAsync(AdFilter.Default));
    }

    [Fact]
    public async Task Mark_matching_read_only_clears_the_current_result_set()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync();
        await temp.Store.CommitBatchAsync(
            [Fixtures.Ad("gbg", Goteborg, changed: T0.AddMinutes(1)), Fixtures.Ad("mmo", Malmo, Skane, changed: T0.AddMinutes(1))],
            AdFilter.Default, T0.AddMinutes(2), T0.AddMinutes(2));

        var cleared = await temp.Store.MarkMatchingReadAsync(GoteborgOnly, T0.AddMinutes(2));

        Assert.Equal(1, cleared);
        var all = await temp.RowsAsync();
        Assert.False(all["gbg"].Unread);
        Assert.True(all["mmo"].Unread);
    }

    [Fact]
    public async Task Ads_expire_at_their_last_publication_instant_without_a_removal()
    {
        using var temp = new TempStore();
        var last = T0.AddHours(1);
        await temp.BaselineAsync(Fixtures.Ad("1", lastPublication: last), Fixtures.Ad("2"));

        // Hidden from queries as soon as it has passed, and removed at the next commit.
        Assert.Single(await temp.RowsAsync(now: last.AddSeconds(1)));
        var result = await temp.Store.CommitBatchAsync([], AdFilter.Default, last.AddMinutes(1), last.AddMinutes(1));

        Assert.Equal(1, result.Expired);
        Assert.Equal(1, await temp.Store.CountCurrentAsync(last.AddMinutes(1)));
    }

    [Fact]
    public async Task Empty_successful_interval_advances_the_checkpoint()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync();

        await temp.Store.CommitBatchAsync([], AdFilter.Default, T0.AddMinutes(5), T0.AddMinutes(5));

        Assert.Equal(T0.AddMinutes(5), (await temp.Store.ReadSyncStateAsync()).CommittedThroughUtc);
    }

    [Fact]
    public async Task Inactive_state_is_pruned_after_the_retention_period()
    {
        using var temp = new TempStore(new StorePolicy { InactiveRetention = TimeSpan.FromDays(90) });
        await temp.BaselineAsync();
        await temp.Store.CommitBatchAsync([Fixtures.Ad("1", changed: T0.AddMinutes(1))], AdFilter.Default, T0.AddMinutes(2), T0.AddMinutes(2));
        await temp.Store.CommitBatchAsync([Fixtures.Removal("1", T0.AddMinutes(3))], AdFilter.Default, T0.AddMinutes(4), T0.AddMinutes(4));

        var later = T0.AddMinutes(4).AddDays(91);
        var result = await temp.Store.CommitBatchAsync([], AdFilter.Default, later, later);

        Assert.Equal(1, result.Pruned);
        // After pruning, an older replay is no longer recognized; this is the documented retention trade-off.
        var replay = await temp.Store.CommitBatchAsync([Fixtures.Ad("1", changed: T0.AddMinutes(1), lastPublication: later.AddDays(1))], AdFilter.Default, later, later);
        Assert.Equal(0, replay.Stale);
    }

    [Fact]
    public async Task Database_from_a_newer_version_is_refused()
    {
        using var temp = new TempStore();
        temp.Store.Dispose();
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(() => AdStore.Open(temp.DatabasePath));
    }

    /// <summary>A list whose enumeration fails at a given index, simulating a failure during the transaction.</summary>
    private sealed class FailingList(IReadOnlyList<SourceRecord> items, int failAt) : IReadOnlyList<SourceRecord>
    {
        public int Count => items.Count + 1;

        public SourceRecord this[int index] => index < items.Count ? items[index] : throw new IOException("Simulated failure");

        public IEnumerator<SourceRecord> GetEnumerator()
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (i == failAt)
                {
                    break;
                }

                yield return items[i];
            }

            throw new IOException("Simulated failure");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
