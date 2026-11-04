using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using ArbetsWatch.Core.Ads;
using ArbetsWatch.Core.Details;
using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Places;
using ArbetsWatch.Core.Storage;
using ArbetsWatch.Core.Sync;
using ArbetsWatch.Core.Translation;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArbetsWatch.Web.Tests;

/// <summary>Tests here change process-wide environment variables, so they run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WebCollection
{
    public const string Name = "web";
}

/// <summary>
/// The real host on a real loopback socket, with the upstream services faked. No test reaches the internet.
/// </summary>
internal sealed class WebFixture : IAsyncDisposable
{
    public const string Goteborg = "PVZL_BQT_XtL";
    public const string DeepLKey = "00000000-0000-0000-0000-000000000000:fx";
    public const string DescriptionMarker = "HEMLIGBESKRIVNING";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arbetswatch-web-tests", Guid.NewGuid().ToString("N"));
    private readonly RefreshCoordinator _coordinator;

    private WebFixture(IReadOnlyList<SourceRecord> snapshot, string? key, string[]? args, IAdDetailsSource? details)
    {
        Directory.CreateDirectory(_directory);
        Store = AdStore.Open(Path.Combine(_directory, "test.db"));
        var time = TimeProvider.System;
        var options = new SyncOptions();
        var engine = new SyncEngine(Store, new StubStream(snapshot), new RequestGate(time, TimeSpan.Zero), time, options, NullLogger<SyncEngine>.Instance);
        _coordinator = new RefreshCoordinator(engine, Store, time, NullLogger<RefreshCoordinator>.Instance, AdFilter.Default, TimeSpan.FromHours(1), paused: false);
        Translator = new StubTranslator();
        Details = details ?? new StubDetails();
        var translationLogger = new CapturingLoggerProvider(Logs).CreateLogger(nameof(TitleTranslationService));
        var translation = new TitleTranslationService(Translator, new UnavailableSecretStore(), time, new LoggerAdapter<TitleTranslationService>(translationLogger),
            environmentKey: () => key);

        App = WebApp.Build(
            new WebAppDependencies(Store, _coordinator, PlaceCatalog.LoadBundled(), Details, translation, time, Port: 0),
            args,
            logging =>
            {
                WebApp.ConfigureLogging(logging);
                logging.ClearProviders();
                logging.AddProvider(new CapturingLoggerProvider(Logs));
            });
    }

    public AdStore Store { get; }

    public WebApplication App { get; }

    public StubTranslator Translator { get; }

    public IAdDetailsSource Details { get; }

    public ConcurrentQueue<string> Logs { get; } = new();

    public HttpClient Http { get; private set; } = null!;

    public Uri BaseAddress { get; private set; } = null!;

    public static async Task<WebFixture> StartAsync(
        IReadOnlyList<SourceRecord>? snapshot = null,
        string? key = null,
        string[]? args = null,
        IAdDetailsSource? details = null)
    {
        var fixture = new WebFixture(snapshot ?? [Ad("100"), Ad("101"), Ad("102")], key, args, details);
        await fixture.App.StartAsync();
        fixture.BaseAddress = new Uri(WebApp.Addresses(fixture.App).Single());
        fixture.Http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = fixture.BaseAddress };
        fixture.Http.DefaultRequestHeaders.Add("X-ArbetsWatch", "1");

        // The first sync runs from the real coordinator; wait until it has committed.
        for (var i = 0; i < 200 && !(await fixture.Store.ReadSyncStateAsync()).BaselineEstablished; i++)
        {
            await Task.Delay(25);
        }

        Assert.True((await fixture.Store.ReadSyncStateAsync()).BaselineEstablished);
        return fixture;
    }

    public static AdSummary Ad(
        string id,
        string headline = "Testjobb",
        string? url = null,
        DateTimeOffset? published = null,
        string? worktimeId = "6YE1_gAC_R2G")
    {
        var now = DateTimeOffset.UtcNow;
        return new AdSummary(
            id,
            headline,
            "Testföretaget AB",
            url ?? $"https://arbetsformedlingen.se/platsbanken/annonser/{id}",
            PlaceCatalog.SwedenId,
            null,
            null,
            Goteborg,
            "Göteborg",
            worktimeId,
            worktimeId is null ? null : "Heltid",
            published ?? now.AddHours(-1),
            null,
            now.AddDays(30),
            null,
            now.AddHours(-1))
        {
            Description = $"Beskrivning {DescriptionMarker} av jobbet",
        };
    }

    public async ValueTask DisposeAsync()
    {
        Http?.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        await _coordinator.DisposeAsync();
        Store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp cleanup removes leftovers.
        }
    }

    private sealed class StubStream(IReadOnlyList<SourceRecord> snapshot) : IJobStreamClient
    {
        public async IAsyncEnumerable<SourceRecord> ReadSnapshotAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var record in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return record;
            }
        }

        public Task<IReadOnlyList<SourceRecord>> ReadChangesAsync(DateTimeOffset afterUtc, DateTimeOffset beforeUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceRecord>>([]);
    }
}

internal sealed class StubTranslator : ITitleTranslator
{
    public ConcurrentQueue<string> Sent { get; } = new();

    public ConcurrentQueue<string> KeysUsed { get; } = new();

    public TranslationStatus Status { get; set; } = TranslationStatus.Ok;

    public Task<TranslationResult> TranslateAsync(string apiKey, IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        KeysUsed.Enqueue(apiKey);
        foreach (var text in texts)
        {
            Sent.Enqueue(text);
        }

        return Task.FromResult(Status == TranslationStatus.Ok
            ? new TranslationResult(TranslationStatus.Ok, [.. texts.Select(t => "en:" + t)])
            : new TranslationResult(Status));
    }

    public Task<UsageResult> GetUsageAsync(string apiKey, CancellationToken cancellationToken) =>
        Task.FromResult(new UsageResult(TranslationStatus.Ok));
}

internal sealed class StubDetails : IAdDetailsSource
{
    public ConcurrentQueue<string> Requested { get; } = new();

    public Func<string, AdDetailsResult> Answer { get; set; } = id =>
        new AdDetailsResult(AdDetailsStatus.Found, new AdDetails { Id = id, Headline = "Rubrik", Description = "<script>alert(1)</script>" });

    public Task<AdDetailsResult> GetAsync(string id, CancellationToken cancellationToken)
    {
        Requested.Enqueue(id);
        return Task.FromResult(Answer(id));
    }
}

internal sealed class LoggerAdapter<T>(ILogger inner) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        inner.Log(logLevel, eventId, state, exception, formatter);
}

internal sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, sink);

    public void Dispose()
    {
    }

    private sealed class Capturing(string category, ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue($"{category}: {formatter(state, exception)} {exception}");
    }
}

internal static class HttpExtensions
{
    public static Task<HttpResponseMessage> SendWithAsync(this HttpClient http, HttpMethod method, string path, string? header = null, string? value = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (header is not null)
        {
            request.Headers.TryAddWithoutValidation(header, value);
        }

        return http.SendAsync(request);
    }

    public static HttpStatusCode Code(this HttpResponseMessage response) => response.StatusCode;
}
