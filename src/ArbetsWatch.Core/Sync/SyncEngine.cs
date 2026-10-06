using ArbetsWatch.Core.Ads;
using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Storage;
using ArbetsWatch.Core.Time;
using Microsoft.Extensions.Logging;

namespace ArbetsWatch.Core.Sync;

/// <summary>Synchronization policy. App choices to measure and tune, not upstream guarantees.</summary>
public sealed record SyncOptions
{
    /// <summary>Each stream interval starts this far before the committed checkpoint.</summary>
    public TimeSpan Overlap { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Interval ends stay this far behind "now" to allow for upstream indexing delay.</summary>
    public TimeSpan SafetyLag { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Minimum spacing between JobStream requests (the guide states one request per minute).</summary>
    public TimeSpan MinRequestSpacing { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Longest interval requested at once while catching up after sleep or downtime.</summary>
    public TimeSpan MaxInterval { get; init; } = TimeSpan.FromHours(12);

    /// <summary>A gap longer than this is recovered with a fresh snapshot instead of catching up.</summary>
    public TimeSpan SnapshotAfterGap { get; init; } = TimeSpan.FromDays(7);

    /// <summary>A full snapshot reconciles the cache at least this often.</summary>
    public TimeSpan ReconcileEvery { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Snapshot ads are staged in transactions of this many rows.</summary>
    public int SnapshotChunkSize { get; init; } = 2000;
}

public enum SyncKind
{
    Snapshot,
    Interval,
}

public sealed record SyncOutcome(SyncKind Kind, DateTimeOffset CheckpointUtc, ApplyResult Result, int Received)
{
    /// <summary>True when the checkpoint is still more than one interval behind and another request is due.</summary>
    public bool Behind { get; init; }
}

public sealed record SnapshotProgress(int AdsReceived);

/// <summary>
/// Runs a commit with the filter in effect at that moment. The coordinator serializes commits with filter
/// changes and read-state edits through this, without holding its lock across downloads or throttling waits.
/// </summary>
public delegate Task<ApplyResult> CommitScope(Func<AdFilter, Task<ApplyResult>> commit);

/// <summary>
/// Executes one synchronization step against the store: a full snapshot, or one bounded stream interval.
/// Scheduling, coalescing and retries belong to the refresh coordinator (M4); this class assumes it is never
/// called concurrently.
/// </summary>
public sealed class SyncEngine(
    AdStore store,
    IJobStreamClient client,
    RequestGate gate,
    TimeProvider time,
    SyncOptions options,
    ILogger<SyncEngine> logger)
{
    /// <summary>A completed snapshot download still in staging: reused when only the replay or activation failed.</summary>
    private (DateTimeOffset Start, int Received)? _completedStaging;

    /// <summary>How long a completed staging set may be reused instead of downloading the snapshot again.</summary>
    public static readonly TimeSpan StagingReuseWindow = TimeSpan.FromMinutes(30);

    public SyncOptions Options => options;

    /// <summary>Whether the next step must be a snapshot, and why (null when an interval is enough).</summary>
    public string? SnapshotReason(SyncState sync)
    {
        var now = time.GetUtcNow();
        if (!sync.BaselineEstablished || sync.CommittedThroughUtc is null)
        {
            return "first start";
        }

        if (sync.TimeAdapterVersion != SwedishTime.AdapterVersion)
        {
            return "time handling changed";
        }

        if (now - sync.CommittedThroughUtc > options.SnapshotAfterGap)
        {
            return "long interruption";
        }

        if (sync.LastSnapshotUtc is null || now - sync.LastSnapshotUtc > options.ReconcileEvery)
        {
            return "weekly reconciliation";
        }

        return null;
    }

    /// <summary>
    /// Downloads the snapshot into staging, replays changes that overlap the download, then activates the
    /// staged dataset and checkpoint atomically. Any failure leaves the active cache and checkpoint unchanged.
    /// </summary>
    public Task<SyncOutcome> LoadSnapshotAsync(AdFilter filter, IProgress<SnapshotProgress>? progress, CancellationToken cancellationToken) =>
        LoadSnapshotAsync(commit => commit(filter), progress, cancellationToken);

    public async Task<SyncOutcome> LoadSnapshotAsync(CommitScope commitScope, IProgress<SnapshotProgress>? progress, CancellationToken cancellationToken)
    {
        // A download that completed but whose replay request failed (e.g. throttled) is reused for a while
        // instead of fetching ~450 MB again.
        if (_completedStaging is { } completed && time.GetUtcNow() - completed.Start < StagingReuseWindow)
        {
            logger.LogInformation("Reusing the staged snapshot from {Start:o} ({Count} ads)", completed.Start, completed.Received);
            return await ReplayAndActivateAsync(commitScope, completed.Start, completed.Received, cancellationToken).ConfigureAwait(false);
        }

        _completedStaging = null;
        await store.ResetStagingAsync(cancellationToken).ConfigureAwait(false);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var start = SwedishTime.FloorToSecond(time.GetUtcNow());
        logger.LogInformation("Snapshot download started at {Start:o}", start);

        var received = 0;
        var chunk = new List<AdSummary>(options.SnapshotChunkSize);
        try
        {
            await foreach (var record in client.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false))
            {
                if (record is not AdSummary ad)
                {
                    continue;
                }

                chunk.Add(ad);
                received++;
                if (chunk.Count >= options.SnapshotChunkSize)
                {
                    await store.StageSnapshotChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
                    chunk.Clear();
                    progress?.Report(new SnapshotProgress(received));
                }
            }

            if (chunk.Count > 0)
            {
                await store.StageSnapshotChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
                progress?.Report(new SnapshotProgress(received));
            }
        }
        catch (JobStreamException ex)
        {
            Defer(ex);
            throw;
        }

        logger.LogInformation("Snapshot received {Count} ads", received);
        _completedStaging = (start, received);
        return await ReplayAndActivateAsync(commitScope, start, received, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SyncOutcome> ReplayAndActivateAsync(CommitScope commitScope, DateTimeOffset start, int received, CancellationToken cancellationToken)
    {
        // Changes made while the snapshot was downloading. The end is fixed before the request.
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var end = IntervalEnd();
        if (end > start - options.Overlap)
        {
            var changes = await FetchAsync(start - options.Overlap, end, cancellationToken).ConfigureAwait(false);
            await store.ReplayIntoStagingAsync(changes, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Replayed {Count} changes from {After:o} to {Before:o}", changes.Count, start - options.Overlap, end);
        }

        // If the download was faster than the safety lag, the checkpoint stays before the snapshot start and the
        // next interval covers the download period instead.
        ApplyResult result;
        try
        {
            result = await commitScope(filter => store.ActivateSnapshotAsync(filter, start, end, time.GetUtcNow(), cancellationToken)).ConfigureAwait(false);
        }
        catch (SnapshotRejectedException)
        {
            _completedStaging = null; // the staged data itself is unusable
            throw;
        }

        _completedStaging = null;
        logger.LogInformation("Snapshot activated: {Result}", result);
        return new SyncOutcome(SyncKind.Snapshot, end, result, received)
        {
            Behind = time.GetUtcNow() - options.SafetyLag - end > options.MaxInterval,
        };
    }

    /// <summary>
    /// Requests one interval <c>[checkpoint − overlap, end]</c>, where <c>end</c> is fixed before the request and
    /// at most <see cref="SyncOptions.MaxInterval"/> after the checkpoint, and commits it with the checkpoint.
    /// Returns null when no interval is due yet.
    /// </summary>
    public Task<SyncOutcome?> PollIntervalAsync(AdFilter filter, CancellationToken cancellationToken) =>
        PollIntervalAsync(commit => commit(filter), cancellationToken);

    public async Task<SyncOutcome?> PollIntervalAsync(CommitScope commitScope, CancellationToken cancellationToken)
    {
        var sync = await store.ReadSyncStateAsync(cancellationToken).ConfigureAwait(false);
        if (sync.CommittedThroughUtc is not { } committed)
        {
            throw new InvalidOperationException("An interval requires an established baseline.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var latest = IntervalEnd();
        var end = latest - committed > options.MaxInterval ? SwedishTime.FloorToSecond(committed + options.MaxInterval) : latest;
        if (end <= committed)
        {
            return null;
        }

        var after = committed - options.Overlap;
        var changes = await FetchAsync(after, end, cancellationToken).ConfigureAwait(false);
        var result = await commitScope(filter => store.CommitBatchAsync(changes, filter, end, time.GetUtcNow(), cancellationToken)).ConfigureAwait(false);
        logger.LogInformation("Interval {After:o} – {Before:o}: {Count} records, {Result}", after, end, changes.Count, result);
        return new SyncOutcome(SyncKind.Interval, end, result, changes.Count) { Behind = end < latest };
    }

    private DateTimeOffset IntervalEnd() => SwedishTime.FloorToSecond(time.GetUtcNow() - options.SafetyLag);

    private async Task<IReadOnlyList<SourceRecord>> FetchAsync(DateTimeOffset after, DateTimeOffset before, CancellationToken cancellationToken)
    {
        try
        {
            return await client.ReadChangesAsync(after, before, cancellationToken).ConfigureAwait(false);
        }
        catch (JobStreamException ex)
        {
            Defer(ex);
            throw;
        }
    }

    private void Defer(JobStreamException ex)
    {
        if (ex.RetryAfter is { } wait && wait > TimeSpan.Zero)
        {
            gate.Defer(wait);
        }
    }
}
