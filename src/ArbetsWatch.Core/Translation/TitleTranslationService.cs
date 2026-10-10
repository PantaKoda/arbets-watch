using Microsoft.Extensions.Logging;

namespace ArbetsWatch.Core.Translation;

public enum TranslationHealth
{
    NotConfigured,
    Ready,
    InvalidKey,
    QuotaExceeded,
    TemporarilyUnavailable,
}

public enum KeyCheck
{
    Saved,
    Malformed,
    Rejected,
    CouldNotVerify,
    NotSupported,
}

/// <summary>
/// Translates ad titles from Swedish to English on demand. Results live only in a bounded in-memory cache: nothing
/// is written to SQLite, preferences or logs. The API key is read from the environment or the encrypted store,
/// held in memory, and never logged or returned.
/// </summary>
public sealed class TitleTranslationService
{
    /// <summary>Environment override (not saved anywhere): the key to use instead of the stored one.</summary>
    public const string KeyEnvironmentVariable = "ARBETSWATCH_DEEPL_KEY";

    private const int MaxTitleLength = 300;

    private readonly ITitleTranslator _translator;
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _time;
    private readonly ILogger<TitleTranslationService> _logger;
    private readonly TitleTranslationCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _state = new();
    private string? _key;
    private bool _keyLoaded;
    private TranslationHealth _health;
    private DateTimeOffset _pausedUntil;

    public TitleTranslationService(
        ITitleTranslator translator,
        ISecretStore secrets,
        TimeProvider time,
        ILogger<TitleTranslationService> logger,
        int cacheCapacity = 5000)
    {
        _translator = translator;
        _secrets = secrets;
        _time = time;
        _logger = logger;
        _cache = new TitleTranslationCache(cacheCapacity);
        _health = Key() is null ? TranslationHealth.NotConfigured : TranslationHealth.Ready;
    }

    /// <summary>Raised (on any thread) when the key or the service state changes.</summary>
    public event EventHandler? Changed;

    public bool CanStoreKey => _secrets.IsAvailable;

    public bool IsConfigured => Key() is not null;

    public TranslationHealth Health
    {
        get
        {
            lock (_state)
            {
                return _health;
            }
        }
    }

    /// <summary>The English title if it was already translated; never makes a request.</summary>
    public string? TryGet(string title) => _cache.TryGet(title);

    /// <summary>
    /// Translates the titles that are not cached yet and fills the cache. Safe to call often: concurrent calls are
    /// serialized, duplicates are sent once, and a failure or pause leaves the originals showing.
    /// </summary>
    public async Task EnsureTranslatedAsync(IEnumerable<string> titles, CancellationToken cancellationToken)
    {
        var wanted = titles.Select(t => t?.Trim() ?? string.Empty)
            .Where(t => t.Length is > 0 and <= MaxTitleLength)
            .Distinct(StringComparer.Ordinal)
            .Where(t => _cache.TryGet(t) is null)
            .ToList();
        if (wanted.Count == 0 || Key() is not { } key)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsPaused())
            {
                return;
            }

            // Another caller may have translated these while this one waited.
            wanted = [.. wanted.Where(t => _cache.TryGet(t) is null)];
            if (wanted.Count == 0)
            {
                return;
            }

            var result = await _translator.TranslateAsync(key, wanted, cancellationToken).ConfigureAwait(false);
            if (result.Status == TranslationStatus.Ok)
            {
                for (var i = 0; i < wanted.Count; i++)
                {
                    _cache.Set(wanted[i], result.Texts![i]);
                }

                SetHealth(TranslationHealth.Ready, null);
                return;
            }

            _logger.LogWarning("Title translation did not complete: {Status}", result.Status);
            switch (result.Status)
            {
                case TranslationStatus.InvalidKey:
                    SetHealth(TranslationHealth.InvalidKey, null);
                    break;
                case TranslationStatus.QuotaExceeded:
                    SetHealth(TranslationHealth.QuotaExceeded, TimeSpan.FromHours(1));
                    break;
                case TranslationStatus.RateLimited:
                    SetHealth(TranslationHealth.TemporarilyUnavailable, Clamp(result.RetryAfter, TimeSpan.FromSeconds(30)));
                    break;
                default:
                    SetHealth(TranslationHealth.TemporarilyUnavailable, TimeSpan.FromSeconds(30));
                    break;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Checks a typed key against DeepL and, only if it is accepted, stores it encrypted.</summary>
    public async Task<KeyCheck> SaveKeyAsync(string input, CancellationToken cancellationToken)
    {
        var candidate = input.Trim();
        if (!DeepLKey.IsWellFormed(candidate))
        {
            return KeyCheck.Malformed;
        }

        if (!_secrets.IsAvailable)
        {
            return KeyCheck.NotSupported;
        }

        var usage = await _translator.GetUsageAsync(candidate, cancellationToken).ConfigureAwait(false);
        switch (usage.Status)
        {
            case TranslationStatus.InvalidKey:
                return KeyCheck.Rejected;
            case TranslationStatus.Ok or TranslationStatus.QuotaExceeded:
                break;
            default:
                return KeyCheck.CouldNotVerify;
        }

        _secrets.Save(candidate);
        lock (_state)
        {
            _key = candidate;
            _keyLoaded = true;
            _pausedUntil = default;
        }

        _cache.Clear();
        SetHealth(usage.Status == TranslationStatus.QuotaExceeded ? TranslationHealth.QuotaExceeded : TranslationHealth.Ready, null);
        return KeyCheck.Saved;
    }

    /// <summary>Forgets the key (memory and encrypted file) and every cached translation.</summary>
    public void RemoveKey()
    {
        _secrets.Delete();
        lock (_state)
        {
            _key = null;
            _keyLoaded = true;
            _pausedUntil = default;
        }

        _cache.Clear();
        SetHealth(TranslationHealth.NotConfigured, null);
    }

    /// <summary>Characters used this billing period, or null when unknown (no key, offline, rejected).</summary>
    public async Task<UsageResult?> GetUsageAsync(CancellationToken cancellationToken)
    {
        if (Key() is not { } key)
        {
            return null;
        }

        var usage = await _translator.GetUsageAsync(key, cancellationToken).ConfigureAwait(false);
        return usage.Status == TranslationStatus.Ok ? usage : null;
    }

    private string? Key()
    {
        lock (_state)
        {
            if (!_keyLoaded)
            {
                var fromEnvironment = Environment.GetEnvironmentVariable(KeyEnvironmentVariable)?.Trim();
                var found = DeepLKey.IsWellFormed(fromEnvironment) ? fromEnvironment : _secrets.Load();
                _key = DeepLKey.IsWellFormed(found) ? found : null;
                _keyLoaded = true;
            }

            return _key;
        }
    }

    private bool IsPaused()
    {
        lock (_state)
        {
            return _time.GetUtcNow() < _pausedUntil || _health == TranslationHealth.InvalidKey;
        }
    }

    private void SetHealth(TranslationHealth health, TimeSpan? pause)
    {
        bool changed;
        lock (_state)
        {
            changed = _health != health;
            _health = health;
            _pausedUntil = pause is { } p ? _time.GetUtcNow() + p : default;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static TimeSpan Clamp(TimeSpan? value, TimeSpan fallback) =>
        value is { } v && v > TimeSpan.Zero ? TimeSpan.FromSeconds(Math.Min(v.TotalSeconds, 600)) : fallback;
}

/// <summary>A bounded, thread-safe map from Swedish title to its English translation; oldest entries leave first.</summary>
public sealed class TitleTranslationCache(int capacity)
{
    private readonly Dictionary<string, string> _items = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Lock _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public string? TryGet(string title)
    {
        lock (_lock)
        {
            return _items.GetValueOrDefault(title.Trim());
        }
    }

    public void Set(string title, string translation)
    {
        var key = title.Trim();
        lock (_lock)
        {
            if (_items.TryAdd(key, translation))
            {
                _order.Enqueue(key);
                while (_items.Count > capacity && _order.TryDequeue(out var oldest))
                {
                    _items.Remove(oldest);
                }
            }
            else
            {
                _items[key] = translation;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            _order.Clear();
        }
    }
}
