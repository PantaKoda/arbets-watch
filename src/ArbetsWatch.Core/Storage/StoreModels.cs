using ArbetsWatch.Core.Ads;

namespace ArbetsWatch.Core.Storage;

public sealed record SyncState(
    DateTimeOffset? CommittedThroughUtc,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastSnapshotUtc,
    bool BaselineEstablished,
    int TimeAdapterVersion);

/// <summary>An ad as shown in the list: its summary plus this monitor's read state.</summary>
public sealed record AdRow(AdSummary Ad, bool Unread, DateTimeOffset FirstSeenUtc);

/// <summary>What one committed batch or snapshot changed.</summary>
public sealed record ApplyResult(int Applied, int Stale, int NewUnread, int Removed, int Expired, int Absent, int Pruned)
{
    public static ApplyResult Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>Retention and ordering policies applied while committing. App policy, not upstream guarantees.</summary>
public sealed record StorePolicy
{
    /// <summary>How long compact state is kept after an ad becomes inactive.</summary>
    public TimeSpan InactiveRetention { get; init; } = TimeSpan.FromDays(90);

    /// <summary>
    /// A snapshot with fewer current ads than this fraction of the cache is rejected as implausible (a truncated
    /// or empty download) and the previous cache is kept.
    /// </summary>
    public double MinSnapshotFraction { get; init; } = 0.5;
}

/// <summary>Activation refused: the staged snapshot is implausibly small. Nothing was changed.</summary>
public sealed class SnapshotRejectedException(long staged, long current)
    : InvalidOperationException($"The downloaded snapshot has {staged} ads but the cache has {current}; it was not used.")
{
    public long Staged { get; } = staged;

    public long Current { get; } = current;
}

/// <summary>
/// Decides whether an incoming source state is strictly older than the stored one
/// (docs/api-contracts.md, "Ordering of states"). Ad timestamps have millisecond precision, removal dates whole
/// seconds; when either side is a removal the comparison uses whole seconds. Ties are applied (the later
/// batch wins), which keeps replays idempotent.
/// </summary>
public static class SourceOrder
{
    public const string AdKind = "ad";
    public const string RemovalKind = "removal";

    public static bool IsOlder(long incomingMs, string incomingKind, long storedMs, string storedKind) =>
        incomingKind == AdKind && storedKind == AdKind
            ? incomingMs < storedMs
            : FloorSeconds(incomingMs) < FloorSeconds(storedMs);

    private static long FloorSeconds(long ms) => (long)Math.Floor(ms / 1000d);
}
