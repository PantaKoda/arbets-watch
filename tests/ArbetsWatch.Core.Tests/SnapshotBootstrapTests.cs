using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ArbetsWatch.Core.Tests;

public sealed class SnapshotBootstrapTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeJobStream _stream = new();

    [Fact]
    public async Task First_snapshot_establishes_a_baseline_without_unread_ads()
    {
        using var temp = new TempStore();
        _stream.Snapshot = [Fixtures.Ad("1"), Fixtures.Ad("2"), Fixtures.Ad("3")];
        _stream.AfterSnapshot = () => _time.Advance(TimeSpan.FromMinutes(3));

        var outcome = await Engine(temp).LoadSnapshotAsync(AdFilter.Default, null, CancellationToken.None);

        Assert.Equal(3, outcome.Received);
        var rows = await temp.RowsAsync(now: _time.GetUtcNow());
        Assert.Equal(3, rows.Count);
        Assert.All(rows.Values, r => Assert.False(r.Unread));
        var sync = await temp.Store.ReadSyncStateAsync();
        Assert.True(sync.BaselineEstablished);
        Assert.Equal(Start, sync.LastSnapshotUtc);
        // Interval end fixed before the replay request: now − 2 min safety lag.
        Assert.Equal(Start.AddMinutes(1), sync.CommittedThroughUtc);
        Assert.Equal((Start.AddMinutes(-5), Start.AddMinutes(1)), Assert.Single(_stream.ChangeRequests));
    }

    [Fact]
    public async Task Interrupted_snapshot_preserves_the_previous_cache()
    {
        using var temp = new TempStore();
        _stream.Snapshot = [Fixtures.Ad("1"), Fixtures.Ad("2")];
        _stream.AfterSnapshot = () => _time.Advance(TimeSpan.FromMinutes(3));
        var engine = Engine(temp);
        await engine.LoadSnapshotAsync(AdFilter.Default, null, CancellationToken.None);
        var before = await temp.Store.ReadSyncStateAsync();

        _time.Advance(TimeSpan.FromDays(8));
        _stream.Snapshot = [.. Enumerable.Range(10, 5000).Select(i => Fixtures.Ad(i.ToString(System.Globalization.CultureInfo.InvariantCulture)))];
        _stream.FailSnapshotAfter = 4500;

        await Assert.ThrowsAsync<JobStreamException>(() => engine.LoadSnapshotAsync(AdFilter.Default, null, CancellationToken.None));

        // Two chunks of 2000 were staged before the failure; none of them is visible.
        var rows = await temp.RowsAsync(now: Start);
        Assert.Equal(["1", "2"], rows.Keys.Order());
        Assert.Equal(before, await temp.Store.ReadSyncStateAsync());
    }

    [Fact]
    public async Task Changes_during_the_download_are_reconciled()
    {
        using var temp = new TempStore();
        var t = Start.AddMinutes(-30);
        _stream.Snapshot =
        [
            Fixtures.Ad("edited", changed: t, headline: "Snapshot"),
            Fixtures.Ad("removed", changed: t),
            Fixtures.Ad("older-replay", changed: Start.AddMinutes(1), headline: "Snapshot"),
        ];
        _stream.AfterSnapshot = () => _time.Advance(TimeSpan.FromMinutes(4));
        _stream.Changes = (_, _) =>
        [
            Fixtures.Ad("edited", changed: Start.AddMinutes(1), headline: "Edited during download"),
            Fixtures.Removal("removed", Start.AddMinutes(1)),
            Fixtures.Ad("older-replay", changed: Start.AddMinutes(-2), headline: "Older"),
            Fixtures.Ad("published-during-download", changed: Start.AddMinutes(1)),
        ];

        await Engine(temp).LoadSnapshotAsync(AdFilter.Default, null, CancellationToken.None);

        var rows = await temp.RowsAsync(now: _time.GetUtcNow());
        Assert.Equal("Edited during download", rows["edited"].Ad.Headline);
        Assert.False(rows.ContainsKey("removed"));
        Assert.Equal("Snapshot", rows["older-replay"].Ad.Headline);
        // Still part of the first baseline: no unread markers.
        Assert.False(rows["published-during-download"].Unread);
    }

    [Fact]
    public async Task Reconciliation_keeps_read_state_marks_new_ids_and_drops_absent_ads()
    {
        using var temp = new TempStore();
        _stream.Snapshot = [Fixtures.Ad("kept"), Fixtures.Ad("gone")];
        _stream.AfterSnapshot = () => _time.Advance(TimeSpan.FromMinutes(3));
        var engine = Engine(temp);
        await engine.LoadSnapshotAsync(AdFilter.Default, null, CancellationToken.None);

        // An ad detected by polling and left unread.
        var polled = _time.GetUtcNow();
        await temp.Store.CommitBatchAsync([Fixtures.Ad("polled", changed: polled.AddMinutes(-3))], AdFilter.Default, polled, polled);

        _time.Advance(TimeSpan.FromDays(8));
        _stream.Snapshot = [Fixtures.Ad("kept"), Fixtures.Ad("polled", changed: polled.AddMinutes(-3)), Fixtures.Ad("found-later")];
        await engine.LoadSnapshotAsync(AdFilter.Default, null, CancellationToken.None);

        var rows = await temp.RowsAsync(now: _time.GetUtcNow());
        Assert.Equal(["found-later", "kept", "polled"], rows.Keys.Order());
        Assert.False(rows["kept"].Unread);
        Assert.True(rows["polled"].Unread);
        Assert.True(rows["found-later"].Unread);
    }

    [Fact]
    public async Task Failed_replay_reuses_the_completed_download()
    {
        using var temp = new TempStore();
        _stream.Snapshot = [Fixtures.Ad("1"), Fixtures.Ad("2")];
        _stream.AfterSnapshot = () => _time.Advance(TimeSpan.FromMinutes(3));
        var fail = true;
        _stream.Changes = (_, _) => fail ? throw new JobStreamException(FailureKind.RateLimited, "slow down") : [];
        var engine = Engine(temp);

        await Assert.ThrowsAsync<JobStreamException>(() => engine.LoadSnapshotAsync(AdFilter.Default, null, CancellationToken.None));
        Assert.Empty(await temp.RowsAsync(now: _time.GetUtcNow()));

        fail = false;
        _time.Advance(TimeSpan.FromMinutes(2));
        var outcome = await engine.LoadSnapshotAsync(AdFilter.Default, null, CancellationToken.None);

        Assert.Equal(1, _stream.SnapshotRequests);
        Assert.Equal(2, outcome.Received);
        Assert.Equal(2, (await temp.RowsAsync(now: _time.GetUtcNow())).Count);
    }

    [Fact]
    public void Snapshot_reasons_cover_every_branch()
    {
        var engine = new SyncEngine(new TempStore().Store, _stream, new RequestGate(_time, TimeSpan.Zero), _time,
            new SyncOptions { ReconcileEvery = TimeSpan.FromDays(3), SnapshotAfterGap = TimeSpan.FromDays(7) }, NullLogger<SyncEngine>.Instance);
        var now = _time.GetUtcNow();
        var fresh = new Storage.SyncState(now, now, now, true, Time.SwedishTime.AdapterVersion);

        Assert.Null(engine.SnapshotReason(fresh));
        Assert.Equal("first start", engine.SnapshotReason(fresh with { BaselineEstablished = false }));
        Assert.Equal("time handling changed", engine.SnapshotReason(fresh with { TimeAdapterVersion = 0 }));
        Assert.Equal("long interruption", engine.SnapshotReason(fresh with { CommittedThroughUtc = now.AddDays(-8) }));
        Assert.Equal("weekly reconciliation", engine.SnapshotReason(fresh with { LastSnapshotUtc = now.AddDays(-4) }));
    }

    [Fact]
    public async Task Snapshot_reasons_follow_the_policy()
    {
        using var temp = new TempStore();
        var engine = Engine(temp);
        Assert.Equal("first start", engine.SnapshotReason(await temp.Store.ReadSyncStateAsync()));

        _stream.Snapshot = [Fixtures.Ad("1")];
        _stream.AfterSnapshot = () => _time.Advance(TimeSpan.FromMinutes(3));
        await engine.LoadSnapshotAsync(AdFilter.Default, null, CancellationToken.None);
        Assert.Null(engine.SnapshotReason(await temp.Store.ReadSyncStateAsync()));

        _time.Advance(TimeSpan.FromDays(8));
        Assert.Equal("long interruption", engine.SnapshotReason(await temp.Store.ReadSyncStateAsync()));
    }

    private SyncEngine Engine(TempStore temp) =>
        new(temp.Store, _stream, new RequestGate(_time, TimeSpan.Zero), _time, new SyncOptions(), NullLogger<SyncEngine>.Instance);
}
