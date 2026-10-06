using System.Net;
using System.Text;
using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ArbetsWatch.Core.Tests;

public sealed class MonitoringTests
{
    [Fact]
    public async Task Intervals_across_the_autumn_transition_are_contiguous_and_unambiguous()
    {
        // Committed at 00:20Z on 2026-10-25 (02:20 summer time); clocks go back at 01:00Z (03:00 → 02:00).
        var committed = new DateTimeOffset(2026, 10, 25, 0, 20, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(committed + TimeSpan.FromMinutes(2));
        using var temp = new TempStore();
        await temp.BaselineAsync();
        await temp.Store.CommitBatchAsync([], AdFilter.Default, committed, committed);

        var queries = new List<string>();
        var http = new HttpClient(new RecordingHandler(queries)) { BaseAddress = JobStreamClient.DefaultBaseAddress };
        var engine = new SyncEngine(temp.Store, new JobStreamClient(http), new RequestGate(time, TimeSpan.Zero), time,
            new SyncOptions(), NullLogger<SyncEngine>.Instance);

        // Two polls an hour apart: the second one ends inside the repeated local hour.
        time.Advance(TimeSpan.FromMinutes(60));
        var first = await engine.PollIntervalAsync(AdFilter.Default, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(60));
        var second = await engine.PollIntervalAsync(AdFilter.Default, CancellationToken.None);

        Assert.Equal(committed.AddMinutes(60), first!.CheckpointUtc);
        Assert.Equal(committed.AddMinutes(120), second!.CheckpointUtc);
        Assert.Equal(
            [
                "updated-after=2026-10-25T02:15:00+02:00&updated-before=2026-10-25T02:20:00+01:00",
                "updated-after=2026-10-25T02:15:00+01:00&updated-before=2026-10-25T03:20:00+01:00",
            ],
            queries);
    }

    [Fact]
    public async Task No_interval_is_requested_before_one_is_due()
    {
        var now = new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        using var temp = new TempStore();
        await temp.BaselineAsync();
        await temp.Store.CommitBatchAsync([], AdFilter.Default, now - TimeSpan.FromMinutes(2), now);
        var stream = new FakeJobStream();
        var engine = new SyncEngine(temp.Store, stream, new RequestGate(time, TimeSpan.Zero), time, new SyncOptions(), NullLogger<SyncEngine>.Instance);

        Assert.Null(await engine.PollIntervalAsync(AdFilter.Default, CancellationToken.None));
        Assert.Empty(stream.ChangeRequests);
    }

    [Fact]
    public async Task Rate_limit_defers_the_shared_gate()
    {
        var now = new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        using var temp = new TempStore();
        await temp.BaselineAsync();
        await temp.Store.CommitBatchAsync([], AdFilter.Default, now - TimeSpan.FromMinutes(10), now);
        var gate = new RequestGate(time, TimeSpan.FromMinutes(1));
        var stream = new FakeJobStream
        {
            Changes = (_, _) => throw new JobStreamException(FailureKind.RateLimited, "slow down", retryAfter: TimeSpan.FromMinutes(5)),
        };
        var engine = new SyncEngine(temp.Store, stream, gate, time, new SyncOptions(), NullLogger<SyncEngine>.Instance);

        await Assert.ThrowsAsync<JobStreamException>(() => engine.PollIntervalAsync(AdFilter.Default, CancellationToken.None));

        Assert.Equal(now + TimeSpan.FromMinutes(5), gate.NotBefore);
        Assert.Equal(now - TimeSpan.FromMinutes(10), (await temp.Store.ReadSyncStateAsync()).CommittedThroughUtc);
    }

    [Fact]
    public async Task Gate_spaces_requests_from_every_caller()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero));
        var gate = new RequestGate(time, TimeSpan.FromMinutes(1));

        await gate.WaitAsync(CancellationToken.None);
        var second = gate.WaitAsync(CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Sparse_removal_and_expiry_during_polling()
    {
        var now = new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        using var temp = new TempStore();
        await temp.BaselineAsync(
            Fixtures.Ad("removed-later"),
            Fixtures.Ad("expires", lastPublication: now.AddMinutes(10)),
            Fixtures.Ad("stays"));
        await temp.Store.CommitBatchAsync([], AdFilter.Default, now - TimeSpan.FromMinutes(2), now);

        var stream = new FakeJobStream
        {
            // Removal records carry only ID, date and location IDs (docs/api-contracts.md).
            Changes = (_, _) => [Ads.AdRecordParser.Parse("""{"id":"removed-later","removed":true,"removed_date":"2026-10-06T12:05:00"}""", now)],
        };
        var engine = new SyncEngine(temp.Store, stream, new RequestGate(time, TimeSpan.Zero), time, new SyncOptions(), NullLogger<SyncEngine>.Instance);

        time.Advance(TimeSpan.FromMinutes(15));
        var outcome = await engine.PollIntervalAsync(AdFilter.Default, CancellationToken.None);

        Assert.Equal(1, outcome!.Result.Removed);
        Assert.Equal(1, outcome.Result.Expired);
        Assert.Equal(["stays"], (await temp.RowsAsync(now: time.GetUtcNow())).Keys);
    }

    private sealed class RecordingHandler(List<string> queries) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            queries.Add(Uri.UnescapeDataString(request.RequestUri!.Query.TrimStart('?')));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty, Encoding.UTF8, "application/jsonl") });
        }
    }
}
