using System.Net;
using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ArbetsWatch.Core.Tests;

public sealed class RefreshCoordinatorTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Poll = TimeSpan.FromMinutes(5);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeJobStream _stream = new();
    private readonly TempStore _temp = new();
    private RefreshCoordinator? _coordinator;

    public ValueTask InitializeAsync()
    {
        _stream.Snapshot = [Fixtures.Ad("1")];
        _stream.AfterSnapshot = () => _time.Advance(TimeSpan.FromMinutes(3));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync();
        }

        _temp.Dispose();
    }

    [Fact]
    public async Task Startup_without_a_baseline_loads_the_snapshot()
    {
        var coordinator = Create();
        coordinator.Start();

        await Until(() => coordinator.Status is { HasBaseline: true, Phase: SyncPhase.Idle });
        Assert.Equal(1, _stream.SnapshotRequests);
        Assert.NotNull(coordinator.Status.LastSuccessUtc);
        Assert.Equal(_time.GetUtcNow() + Poll, coordinator.Status.NextRunUtc);
    }

    [Fact]
    public async Task Timer_polls_after_the_interval()
    {
        var coordinator = await StartedWithBaselineAsync();
        var requests = _stream.ChangeRequests.Count;

        _time.Advance(Poll);

        await Until(() => _stream.ChangeRequests.Count == requests + 1 && coordinator.Status.Phase == SyncPhase.Idle);
    }

    [Fact]
    public async Task Manual_refreshes_during_a_running_refresh_are_coalesced()
    {
        var coordinator = await StartedWithBaselineAsync();
        var requests = _stream.ChangeRequests.Count;
        _stream.HoldChanges = new TaskCompletionSource();
        _time.Advance(TimeSpan.FromMinutes(1));

        coordinator.RequestRefresh(RefreshReason.Manual);
        await Until(() => _stream.ChangeRequests.Count == requests + 1);
        Assert.True(coordinator.Status.Foreground);
        for (var i = 0; i < 5; i++)
        {
            coordinator.RequestRefresh(RefreshReason.Manual);
        }

        _stream.HoldChanges.SetResult();
        await Until(() => coordinator.Status is { Phase: SyncPhase.Idle, Foreground: false });
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(requests + 1, _stream.ChangeRequests.Count);
    }

    [Fact]
    public async Task Transient_failure_keeps_data_and_retries_with_backoff()
    {
        var coordinator = await StartedWithBaselineAsync();
        var lastSuccess = coordinator.Status.LastSuccessUtc;
        _stream.Changes = (_, _) => throw new JobStreamException(FailureKind.Transient, "boom", new HttpRequestException("offline"));

        _time.Advance(Poll);
        await Until(() => coordinator.Status.Phase == SyncPhase.Offline);

        var status = coordinator.Status;
        Assert.Equal(lastSuccess, status.LastSuccessUtc);
        Assert.InRange(status.NextRunUtc!.Value - _time.GetUtcNow(), TimeSpan.FromSeconds(22.5), TimeSpan.FromSeconds(37.5));
        Assert.Single(await _temp.RowsAsync(now: _time.GetUtcNow()));

        _stream.Changes = (_, _) => [];
        _time.Advance(TimeSpan.FromSeconds(40));
        await Until(() => coordinator.Status.Phase == SyncPhase.Idle && coordinator.Status.LastSuccessUtc > lastSuccess);
    }

    [Fact]
    public async Task Rejected_request_stops_automatic_retries_until_manual_refresh()
    {
        var coordinator = await StartedWithBaselineAsync();
        _stream.Changes = (_, _) => throw new JobStreamException(FailureKind.Permanent, "bad", statusCode: (int)HttpStatusCode.BadRequest);

        _time.Advance(Poll);
        await Until(() => coordinator.Status.NeedsManualRetry);
        var requests = _stream.ChangeRequests.Count;

        _time.Advance(TimeSpan.FromHours(3));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(requests, _stream.ChangeRequests.Count);

        _stream.Changes = (_, _) => [];
        coordinator.RequestRefresh(RefreshReason.Manual);
        await Until(() => coordinator.Status is { Phase: SyncPhase.Idle, NeedsManualRetry: false });
        Assert.Equal(requests + 1, _stream.ChangeRequests.Count);
    }

    [Fact]
    public async Task Paused_monitoring_does_not_poll_and_unpausing_refreshes()
    {
        var coordinator = await StartedWithBaselineAsync();
        coordinator.SetPaused(true);
        var requests = _stream.ChangeRequests.Count;

        _time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(requests, _stream.ChangeRequests.Count);
        Assert.Equal(SyncPhase.Paused, coordinator.Status.Phase);

        coordinator.SetPaused(false);
        await Until(() => _stream.ChangeRequests.Count == requests + 1 && coordinator.Status.Phase == SyncPhase.Idle);
    }

    [Fact]
    public async Task Filter_change_waits_for_the_running_batch()
    {
        var coordinator = await StartedWithBaselineAsync();
        var goteborg = new AdFilter { MunicipalityIds = new HashSet<string> { "PVZL_BQT_XtL" } };
        _stream.HoldChanges = new TaskCompletionSource();
        _stream.Changes = (_, before) => [Fixtures.Ad("malmo", "oYPt_yRA_Smm", "CaRE_1nn_cSU", changed: before.AddMinutes(-1))];
        _time.Advance(TimeSpan.FromMinutes(1));

        coordinator.RequestRefresh(RefreshReason.Manual);
        await Until(() => coordinator.Status.Phase == SyncPhase.Updating);
        var change = coordinator.SetFilterAsync(goteborg);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(change.IsCompleted);

        _stream.HoldChanges.SetResult();
        await change;

        // The batch was judged with the filter in effect when it started (All Sweden): Malmö is unread.
        Assert.True((await _temp.RowsAsync(now: _time.GetUtcNow()))["malmo"].Unread);
        Assert.Equal(goteborg, coordinator.Filter);
    }

    [Fact]
    public async Task Catch_up_after_sleep_uses_bounded_contiguous_intervals()
    {
        var coordinator = await StartedWithBaselineAsync();
        var committed = (await _temp.Store.ReadSyncStateAsync()).CommittedThroughUtc!.Value;
        var first = _stream.ChangeRequests.Count;

        _time.Advance(TimeSpan.FromHours(30));
        coordinator.RequestRefresh(RefreshReason.Resume);
        await Until(() => coordinator.Status.Phase == SyncPhase.Idle && _stream.ChangeRequests.Count >= first + 3);

        var requests = _stream.ChangeRequests.Skip(first).ToList();
        Assert.Equal(3, requests.Count);
        var previousEnd = committed;
        foreach (var (after, before) in requests)
        {
            Assert.Equal(previousEnd - TimeSpan.FromMinutes(5), after);
            Assert.True(before - previousEnd <= TimeSpan.FromHours(12));
            previousEnd = before;
        }

        Assert.Equal(_time.GetUtcNow() - TimeSpan.FromMinutes(2), previousEnd);
    }

    [Fact]
    public async Task Restart_soon_after_a_success_waits_for_the_schedule()
    {
        var first = await StartedWithBaselineAsync();
        await first.DisposeAsync();
        _coordinator = null;
        var requests = _stream.ChangeRequests.Count;

        _time.Advance(TimeSpan.FromMinutes(1));
        var second = Create();
        second.Start();
        await Until(() => second.Status.NextRunUtc is not null);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(requests, _stream.ChangeRequests.Count);
        Assert.Equal(1, _stream.SnapshotRequests);
    }

    private RefreshCoordinator Create()
    {
        var engine = new SyncEngine(_temp.Store, _stream, new RequestGate(_time, TimeSpan.Zero), _time, new SyncOptions(), NullLogger<SyncEngine>.Instance);
        _coordinator = new RefreshCoordinator(engine, _temp.Store, _time, NullLogger<RefreshCoordinator>.Instance, AdFilter.Default, Poll, paused: false, new Random(1));
        return _coordinator;
    }

    private async Task<RefreshCoordinator> StartedWithBaselineAsync()
    {
        var coordinator = Create();
        coordinator.Start();
        await Until(() => coordinator.Status is { HasBaseline: true, Phase: SyncPhase.Idle, NextRunUtc: not null });
        return coordinator;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Condition not reached within 10 s.");
            }

            await Task.Delay(10);
        }
    }
}
