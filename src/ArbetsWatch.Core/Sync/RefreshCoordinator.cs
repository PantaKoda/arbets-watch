using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Storage;
using Microsoft.Extensions.Logging;

namespace ArbetsWatch.Core.Sync;

public enum SyncPhase
{
    /// <summary>Waiting for the next scheduled check.</summary>
    Idle,

    /// <summary>Downloading and activating the full snapshot.</summary>
    LoadingSnapshot,

    /// <summary>Requesting and applying changes.</summary>
    Updating,

    /// <summary>Monitoring is paused by the user.</summary>
    Paused,

    /// <summary>The last attempt could not reach the service; retrying with backoff.</summary>
    Offline,

    /// <summary>The last attempt failed (server error, bad data, or a rejected request).</summary>
    Failed,
}

public enum RefreshReason
{
    Startup,
    Timer,
    Manual,
    Resume,
    Retry,
}

public sealed record SyncStatus
{
    public SyncPhase Phase { get; init; } = SyncPhase.Idle;

    /// <summary>True while a refresh the user asked for (or the first load) is running: show the progress line.</summary>
    public bool Foreground { get; init; }

    public DateTimeOffset? LastSuccessUtc { get; init; }

    public DateTimeOffset? NextRunUtc { get; init; }

    public string? Message { get; init; }

    public int? SnapshotAdsReceived { get; init; }

    public bool HasBaseline { get; init; }

    /// <summary>The last failure was a rejected request; automatic retries stop until a manual refresh.</summary>
    public bool NeedsManualRetry { get; init; }
}

/// <summary>
/// The single owner of synchronization. Timer ticks, manual refresh, resume and retries go through one loop, so
/// at most one refresh runs at a time; requests that arrive while it runs are absorbed by it. Commits, filter
/// changes and read-state edits are serialized by a lock held only for the commit itself, never across
/// downloads or throttling waits. Failures keep the last data visible and are retried with backoff and jitter;
/// no exception ends the loop.
/// </summary>
public sealed class RefreshCoordinator : IAsyncDisposable
{
    private readonly SyncEngine _engine;
    private readonly AdStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<RefreshCoordinator> _logger;
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _lock = new();
    private readonly Random _random;

    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;
    private AdFilter _filter;
    private TimeSpan _pollInterval;
    private bool _paused;
    private RefreshReason? _pending = RefreshReason.Startup;
    private DateTimeOffset _nextDue;
    private int _failures;
    private bool _blocked;
    private SyncStatus _status = new();

    public RefreshCoordinator(
        SyncEngine engine,
        AdStore store,
        TimeProvider time,
        ILogger<RefreshCoordinator> logger,
        AdFilter filter,
        TimeSpan pollInterval,
        bool paused,
        Random? random = null)
    {
        _engine = engine;
        _store = store;
        _time = time;
        _logger = logger;
        _filter = filter;
        _pollInterval = pollInterval;
        _paused = paused;
        _random = random ?? Random.Shared;
        _nextDue = time.GetUtcNow();
    }

    /// <summary>Raised on a background thread whenever the status changes.</summary>
    public event EventHandler<SyncStatus>? StatusChanged;

    /// <summary>Raised on a background thread after data was committed.</summary>
    public event EventHandler<ApplyResult>? DataChanged;

    public SyncStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    public AdFilter Filter
    {
        get
        {
            lock (_lock)
            {
                return _filter;
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            _loop ??= Task.Run(() => RunLoopAsync(_stopping.Token));
        }
    }

    /// <summary>Asks for a refresh. Coalesced with any pending or running refresh.</summary>
    public void RequestRefresh(RefreshReason reason)
    {
        lock (_lock)
        {
            if (reason == RefreshReason.Manual)
            {
                _blocked = false;
            }

            // Keep the strongest pending reason: a manual request is shown in the foreground.
            if (_pending is null || reason == RefreshReason.Manual)
            {
                _pending = reason;
            }
        }

        Wake();
    }

    /// <summary>
    /// Changes the filter used to decide unread markers. Waits only for a commit in progress (milliseconds), so
    /// a batch is never judged half against the old and half against the new selection.
    /// </summary>
    public async Task SetFilterAsync(AdFilter filter, CancellationToken cancellationToken = default)
    {
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                _filter = filter;
            }
        }
        finally
        {
            _mutation.Release();
        }
    }

    /// <summary>Marks the ads currently matching the filter as read, serialized with refreshes.</summary>
    public async Task<int> MarkMatchingReadAsync(CancellationToken cancellationToken = default)
    {
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _store.MarkMatchingReadAsync(Filter, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutation.Release();
        }
    }

    public void SetPollInterval(TimeSpan interval)
    {
        lock (_lock)
        {
            _pollInterval = interval;
            if (_failures == 0 && _status.LastSuccessUtc is { } last)
            {
                _nextDue = last + interval;
            }
        }

        Wake();
    }

    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            _paused = paused;
            if (!paused)
            {
                _pending ??= RefreshReason.Resume;
            }
        }

        Publish(s => s with
        {
            Phase = paused ? SyncPhase.Paused : (s.Phase == SyncPhase.Paused ? SyncPhase.Idle : s.Phase),
            NextRunUtc = paused ? null : s.NextRunUtc,
        });
        Wake();
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        Wake();
        if (_loop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The refresh loop ended with an error");
            }
        }

        _stopping.Dispose();
        _mutation.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken stopping)
    {
        // Reading the saved state can fail (database busy or unreadable): report it and try again.
        while (true)
        {
            try
            {
                await InitializeScheduleAsync(stopping).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reading the saved state failed; retrying");
                Publish(s => s with { Phase = SyncPhase.Failed, Message = "Could not read the saved data. Retrying." });
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _time, stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await RunIterationAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let an unexpected error end monitoring: report it and fall back to the backoff schedule.
                _logger.LogError(ex, "Unexpected error in the refresh loop");
                HandleFailure(ex);
            }
        }
    }

    private async Task InitializeScheduleAsync(CancellationToken stopping)
    {
        var sync = await _store.ReadSyncStateAsync(stopping).ConfigureAwait(false);
        Publish(s => s with
        {
            LastSuccessUtc = sync.LastSuccessUtc,
            HasBaseline = sync.BaselineEstablished,
            Phase = _paused ? SyncPhase.Paused : SyncPhase.Idle,
        });

        // Resume the schedule from the last success instead of hammering the service after a restart.
        lock (_lock)
        {
            if (sync.LastSuccessUtc is { } last && sync.BaselineEstablished && _engine.SnapshotReason(sync) is null)
            {
                _nextDue = last + _pollInterval;
                if (_pending == RefreshReason.Startup && _nextDue > _time.GetUtcNow())
                {
                    _pending = null;
                }
            }
        }
    }

    private async Task RunIterationAsync(CancellationToken stopping)
    {
        {
            RefreshReason? reason;
            TaskCompletionSource wake;
            TimeSpan? wait;
            lock (_lock)
            {
                wake = _wake;
                var now = _time.GetUtcNow();
                reason = _pending;
                if (reason is null && !_paused && !_blocked && now >= _nextDue)
                {
                    reason = _failures > 0 ? RefreshReason.Retry : RefreshReason.Timer;
                }

                // While paused or blocked only explicit requests run; startup and resume wait for unpause.
                if (reason is RefreshReason.Startup or RefreshReason.Resume or RefreshReason.Timer or RefreshReason.Retry && (_paused || _blocked))
                {
                    reason = null;
                }

                wait = reason is not null ? null : (_paused || _blocked ? Timeout.InfiniteTimeSpan : _nextDue - now);
                if (reason is not null)
                {
                    _pending = null;
                }
            }

            if (reason is null)
            {
                await WaitAsync(wake.Task, wait!.Value, stopping).ConfigureAwait(false);
                return;
            }

            await RunCycleAsync(reason.Value, stopping).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for a wake-up or the next due time. The delay timer exists before the next run time is published,
    /// so anyone reacting to the published time (UI, tests with a fake clock) can never move time past it unseen.
    /// </summary>
    private async Task WaitAsync(Task wake, TimeSpan wait, CancellationToken stopping)
    {
        if (wait == Timeout.InfiniteTimeSpan)
        {
            Publish(s => s with { NextRunUtc = null });
            await wake.WaitAsync(stopping).ConfigureAwait(false);
            return;
        }

        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        using var delayCancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var delay = Task.Delay(wait, _time, delayCancel.Token);
        Publish(s => s with { NextRunUtc = _nextDue });
        await Task.WhenAny(wake, delay).ConfigureAwait(false);
        await delayCancel.CancelAsync().ConfigureAwait(false);
        stopping.ThrowIfCancellationRequested();
    }

    private async Task RunCycleAsync(RefreshReason reason, CancellationToken stopping)
    {
        // Foreground (the progress line) only for a refresh the user asked for or the first load; weekly
        // reconciliation and catch-up run quietly.
        var foreground = reason == RefreshReason.Manual || !Status.HasBaseline;
        try
        {
            // Bounded catch-up: each step is one gated request; a long gap becomes a snapshot instead.
            const int maxSteps = 16;
            for (var step = 0; step < maxSteps; step++)
            {
                if (step > 0 && reason != RefreshReason.Manual && IsPaused)
                {
                    break; // each step's checkpoint is committed, so stopping between steps is safe
                }

                var sync = await _store.ReadSyncStateAsync(stopping).ConfigureAwait(false);
                SyncOutcome? outcome;
                if (_engine.SnapshotReason(sync) is { } why)
                {
                    _logger.LogInformation("Loading snapshot ({Reason}, trigger {Trigger})", why, reason);
                    Publish(s => s with { Phase = SyncPhase.LoadingSnapshot, Foreground = foreground, Message = null, SnapshotAdsReceived = 0 });
                    var progress = new InlineProgress<SnapshotProgress>(p => Publish(s => s with { SnapshotAdsReceived = p.AdsReceived }));
                    outcome = await _engine.LoadSnapshotAsync(CommitUnderLockAsync, progress, stopping).ConfigureAwait(false);
                }
                else
                {
                    Publish(s => s with { Phase = SyncPhase.Updating, Foreground = foreground, Message = null });
                    outcome = await _engine.PollIntervalAsync(CommitUnderLockAsync, stopping).ConfigureAwait(false);
                }

                if (outcome is not null)
                {
                    Raise(DataChanged, outcome.Result);
                }

                if (outcome is not { Behind: true })
                {
                    break;
                }
            }

            var after = await _store.ReadSyncStateAsync(stopping).ConfigureAwait(false);
            lock (_lock)
            {
                _failures = 0;
                _nextDue = _time.GetUtcNow() + _pollInterval;
            }

            Publish(s => s with
            {
                Phase = _paused ? SyncPhase.Paused : SyncPhase.Idle,
                Foreground = false,
                LastSuccessUtc = after.LastSuccessUtc,
                HasBaseline = after.BaselineEstablished,
                Message = null,
                SnapshotAdsReceived = null,
                NeedsManualRetry = false,
            });
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            HandleFailure(ex);
        }
        finally
        {
            lock (_lock)
            {
                // Requests made while this cycle ran are absorbed by it; after a failure the backoff schedule
                // decides the retry, so repeated clicks cannot bypass it.
                _pending = null;
            }
        }
    }

    private bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _paused;
            }
        }
    }

    /// <summary>Serializes one commit with filter changes and read-state edits, using the filter in effect now.</summary>
    private async Task<ApplyResult> CommitUnderLockAsync(Func<AdFilter, Task<ApplyResult>> commit)
    {
        await _mutation.WaitAsync(_stopping.Token).ConfigureAwait(false);
        try
        {
            return await commit(Filter).ConfigureAwait(false);
        }
        finally
        {
            _mutation.Release();
        }
    }

    private void HandleFailure(Exception ex)
    {
        var kind = ex is JobStreamException j ? j.Kind : FailureKind.Transient;
        var offline = ex is JobStreamException { IsConnectivity: true };
        TimeSpan delay;
        lock (_lock)
        {
            _failures++;
            if (kind == FailureKind.Permanent)
            {
                _blocked = true;
                delay = Timeout.InfiniteTimeSpan;
            }
            else
            {
                // 30 s, 1, 2, 4 … minutes, capped at the poll interval, ±25 % jitter.
                var exponential = TimeSpan.FromSeconds(30 * Math.Pow(2, Math.Min(_failures - 1, 10)));
                var capped = exponential < _pollInterval ? exponential : _pollInterval;
                delay = capped * (0.75 + (_random.NextDouble() * 0.5));

                // Data that will fail the same way again, or a snapshot that looked incomplete: retry rarely.
                if (kind == FailureKind.InvalidData || ex is SnapshotRejectedException)
                {
                    delay = Max(delay, Max(_pollInterval, TimeSpan.FromMinutes(30)));
                }

                // Never before the service's Retry-After (jitter is applied first, then clamped).
                if (ex is JobStreamException { RetryAfter: { } retryAfter })
                {
                    delay = Max(delay, retryAfter);
                }

                _nextDue = _time.GetUtcNow() + delay;
            }
        }

        _logger.LogWarning(ex, "Refresh failed ({Kind}, attempt {Failures})", kind, _failures);
        Publish(s => s with
        {
            Phase = offline ? SyncPhase.Offline : SyncPhase.Failed,
            Foreground = false,
            Message = Describe(ex, kind, offline),
            SnapshotAdsReceived = null,
            NeedsManualRetry = kind == FailureKind.Permanent,
            NextRunUtc = kind == FailureKind.Permanent ? null : _nextDue,
        });
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static string Describe(Exception ex, FailureKind kind, bool offline) => kind switch
    {
        _ when offline => "No connection to Arbetsförmedlingen. Showing saved ads.",
        _ when ex is SnapshotRejectedException => "The downloaded list looked incomplete, so the saved ads were kept. Trying again later.",
        FailureKind.InvalidData => "Received data ArbetsWatch could not read. Showing saved ads; trying again later.",
        FailureKind.RateLimited => "The service asked to slow down. Retrying later.",
        FailureKind.Permanent => $"The service rejected the request ({(ex as JobStreamException)?.StatusCode}). Refresh to try again.",
        _ => "Could not update. Showing saved ads; retrying.",
    };

    private void Publish(Func<SyncStatus, SyncStatus> change)
    {
        SyncStatus next;
        lock (_lock)
        {
            next = change(_status);
            if (next == _status)
            {
                return;
            }

            _status = next;
        }

        Raise(StatusChanged, next);
    }

    /// <summary>Raises an event; a failing subscriber is logged and never stops monitoring.</summary>
    private void Raise<T>(EventHandler<T>? handler, T value)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                subscriber(this, value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A {Event} subscriber failed", typeof(T).Name);
            }
        }
    }

    private void Wake()
    {
        TaskCompletionSource previous;
        lock (_lock)
        {
            previous = _wake;
            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        previous.TrySetResult();
    }

    /// <summary>Reports synchronously on the reporting thread (unlike <see cref="Progress{T}"/>).</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
