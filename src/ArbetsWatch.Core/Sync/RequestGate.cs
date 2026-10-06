namespace ArbetsWatch.Core.Sync;

/// <summary>
/// Spaces JobStream requests at least <c>minSpacing</c> apart and honours server back-off. Shared by every
/// caller (timer, manual refresh, resume), so a manual refresh cannot exceed the service's limit.
/// </summary>
public sealed class RequestGate(TimeProvider time, TimeSpan minSpacing)
{
    private readonly Lock _lock = new();
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

    /// <summary>The earliest time the next request may start.</summary>
    public DateTimeOffset NotBefore
    {
        get
        {
            lock (_lock)
            {
                return _notBefore;
            }
        }
    }

    /// <summary>Waits until a request is allowed, then reserves the slot.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay;
            lock (_lock)
            {
                var now = time.GetUtcNow();
                delay = _notBefore - now;
                if (delay <= TimeSpan.Zero)
                {
                    _notBefore = now + minSpacing;
                    return;
                }
            }

            await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Pushes the next allowed request back by at least <paramref name="wait"/> from now.</summary>
    public void Defer(TimeSpan wait)
    {
        lock (_lock)
        {
            var until = time.GetUtcNow() + wait;
            if (until > _notBefore)
            {
                _notBefore = until;
            }
        }
    }
}
