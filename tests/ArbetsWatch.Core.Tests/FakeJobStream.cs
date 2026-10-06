using System.Runtime.CompilerServices;
using ArbetsWatch.Core.Ads;
using ArbetsWatch.Core.Sync;

namespace ArbetsWatch.Core.Tests;

/// <summary>Scripted JobStream: a snapshot, change sets per request, and injectable failures.</summary>
internal sealed class FakeJobStream : IJobStreamClient
{
    public List<SourceRecord> Snapshot { get; set; } = [];

    /// <summary>When set, the snapshot fails after this many records.</summary>
    public int? FailSnapshotAfter { get; set; }

    /// <summary>Called for each change request; returns the records or throws.</summary>
    public Func<DateTimeOffset, DateTimeOffset, IReadOnlyList<SourceRecord>> Changes { get; set; } = (_, _) => [];

    /// <summary>Runs after the snapshot body has been read, e.g. to advance a fake clock.</summary>
    public Action? AfterSnapshot { get; set; }

    /// <summary>When set, change requests wait for this task (to hold a refresh "in flight").</summary>
    public TaskCompletionSource? HoldChanges { get; set; }

    public List<(DateTimeOffset After, DateTimeOffset Before)> ChangeRequests { get; } = [];

    public int SnapshotRequests { get; private set; }

    public async IAsyncEnumerable<SourceRecord> ReadSnapshotAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        SnapshotRequests++;
        var i = 0;
        foreach (var record in Snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i++ == FailSnapshotAfter)
            {
                throw new JobStreamException(FailureKind.Transient, "Simulated drop");
            }

            await Task.Yield();
            yield return record;
        }

        AfterSnapshot?.Invoke();
    }

    public async Task<IReadOnlyList<SourceRecord>> ReadChangesAsync(DateTimeOffset afterUtc, DateTimeOffset beforeUtc, CancellationToken cancellationToken)
    {
        lock (ChangeRequests)
        {
            ChangeRequests.Add((afterUtc, beforeUtc));
        }

        if (HoldChanges is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        return Changes(afterUtc, beforeUtc);
    }
}
