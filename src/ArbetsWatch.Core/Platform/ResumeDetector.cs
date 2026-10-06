namespace ArbetsWatch.Core.Platform;

/// <summary>
/// Detects that the computer slept: a periodic tick that arrives much later than scheduled means the process
/// was suspended. Uses wall-clock time, so a large clock correction is also reported; that only causes one
/// extra (rate-limited) refresh.
/// </summary>
public sealed class ResumeDetector(TimeProvider time, TimeSpan threshold)
{
    private DateTimeOffset _last = time.GetUtcNow();

    /// <summary>Call on every tick. Returns the gap when it exceeds the threshold, otherwise null.</summary>
    public TimeSpan? Tick()
    {
        var now = time.GetUtcNow();
        var gap = now - _last;
        _last = now;
        return gap > threshold ? gap : null;
    }
}
