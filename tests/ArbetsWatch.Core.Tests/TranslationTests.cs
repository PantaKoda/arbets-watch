using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using ArbetsWatch.Core.Settings;
using ArbetsWatch.Core.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ArbetsWatch.Core.Tests;

public sealed class TranslationTests
{
    // Obviously fake: the all-zero key shape is also what the repository secret scan allows.
    private const string FreeKey = "00000000-0000-0000-0000-000000000000:fx";
    private const string ProKey = "00000000-0000-0000-0000-000000000001";

    [Fact]
    public async Task Titles_are_sent_in_one_request_and_come_back_in_order()
    {
        var handler = new DeepLStub();
        var client = new DeepLClient(new HttpClient(handler));

        var result = await client.TranslateAsync(FreeKey, ["Sjuksköterska", "Lagerarbetare"], CancellationToken.None);

        Assert.Equal(TranslationStatus.Ok, result.Status);
        Assert.Equal(["en:Sjuksköterska", "en:Lagerarbetare"], result.Texts);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("api-free.deepl.com", request.Uri.Host);
        Assert.Equal("/v2/translate", request.Uri.AbsolutePath);
        Assert.Equal("DeepL-Auth-Key", request.Authorization?.Scheme);
        Assert.Equal(FreeKey, request.Authorization?.Parameter);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("SV", body.RootElement.GetProperty("source_lang").GetString());
        Assert.Equal("EN-GB", body.RootElement.GetProperty("target_lang").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("text").GetArrayLength());
    }

    [Fact]
    public async Task The_key_is_never_part_of_the_url_or_body()
    {
        var handler = new DeepLStub();
        var client = new DeepLClient(new HttpClient(handler));

        await client.TranslateAsync(FreeKey, ["Städare"], CancellationToken.None);
        await client.GetUsageAsync(FreeKey, CancellationToken.None);

        Assert.All(handler.Requests, r =>
        {
            Assert.DoesNotContain(FreeKey, r.Uri.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(FreeKey, r.Body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Paid_keys_use_the_paid_host()
    {
        var handler = new DeepLStub();
        var client = new DeepLClient(new HttpClient(handler));

        await client.TranslateAsync(ProKey, ["Städare"], CancellationToken.None);

        Assert.Equal("api.deepl.com", Assert.Single(handler.Requests).Uri.Host);
    }

    [Fact]
    public async Task More_than_fifty_titles_are_split_into_batches()
    {
        var handler = new DeepLStub();
        var client = new DeepLClient(new HttpClient(handler));
        var titles = Enumerable.Range(0, 120).Select(i => $"Titel {i}").ToArray();

        var result = await client.TranslateAsync(FreeKey, titles, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(titles.Select(t => "en:" + t), result.Texts);
    }

    [Theory]
    [InlineData(403, TranslationStatus.InvalidKey)]
    [InlineData(456, TranslationStatus.QuotaExceeded)]
    [InlineData(429, TranslationStatus.RateLimited)]
    [InlineData(500, TranslationStatus.Failed)]
    public async Task Error_statuses_map_to_results_without_request_detail(int status, TranslationStatus expected)
    {
        var handler = new DeepLStub { Status = (HttpStatusCode)status };
        var client = new DeepLClient(new HttpClient(handler));

        var result = await client.TranslateAsync(FreeKey, ["Städare"], CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Texts);
    }

    [Fact]
    public async Task A_short_answer_is_a_failure_not_misaligned_titles()
    {
        var handler = new DeepLStub { DropLast = true };
        var client = new DeepLClient(new HttpClient(handler));

        var result = await client.TranslateAsync(FreeKey, ["A", "B"], CancellationToken.None);

        Assert.Equal(TranslationStatus.Failed, result.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("key with spaces and enough length")]
    [InlineData("0000000000\r\nX-Injected: 1")]
    public async Task Malformed_keys_never_reach_the_network(string key)
    {
        var handler = new DeepLStub();
        var client = new DeepLClient(new HttpClient(handler));

        var result = await client.TranslateAsync(key, ["A"], CancellationToken.None);

        Assert.Equal(TranslationStatus.InvalidKey, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_redirect_answer_is_a_failure()
    {
        var handler = new DeepLStub { Status = HttpStatusCode.Redirect };
        var client = new DeepLClient(new HttpClient(handler));

        var result = await client.TranslateAsync(FreeKey, ["A"], CancellationToken.None);

        Assert.Equal(TranslationStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Cached_titles_are_not_sent_again_and_duplicates_go_once()
    {
        var translator = new FakeTranslator();
        var service = Service(translator, new MemorySecretStore(FreeKey));

        await service.EnsureTranslatedAsync(["Städare", "Städare", " Städare "], CancellationToken.None);
        await service.EnsureTranslatedAsync(["Städare"], CancellationToken.None);

        var call = Assert.Single(translator.Calls);
        Assert.Equal(["Städare"], call);
        Assert.Equal("en:Städare", service.TryGet("Städare"));
        Assert.Equal(TranslationHealth.Ready, service.Health);
    }

    [Fact]
    public async Task Without_a_key_nothing_is_requested()
    {
        var translator = new FakeTranslator();
        var service = Service(translator, new MemorySecretStore(null));

        await service.EnsureTranslatedAsync(["Städare"], CancellationToken.None);

        Assert.Empty(translator.Calls);
        Assert.Equal(TranslationHealth.NotConfigured, service.Health);
        Assert.Null(service.TryGet("Städare"));
    }

    [Fact]
    public async Task Quota_exhaustion_pauses_requests_and_leaves_originals()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-10T08:00:00Z"));
        var translator = new FakeTranslator { Status = TranslationStatus.QuotaExceeded };
        var service = Service(translator, new MemorySecretStore(FreeKey), time);

        await service.EnsureTranslatedAsync(["A"], CancellationToken.None);
        await service.EnsureTranslatedAsync(["B"], CancellationToken.None);

        Assert.Single(translator.Calls);
        Assert.Equal(TranslationHealth.QuotaExceeded, service.Health);
        Assert.Null(service.TryGet("A"));

        translator.Status = TranslationStatus.Ok;
        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await service.EnsureTranslatedAsync(["B"], CancellationToken.None);

        Assert.Equal("en:B", service.TryGet("B"));
        Assert.Equal(TranslationHealth.Ready, service.Health);
    }

    [Fact]
    public async Task A_rejected_key_stops_further_requests_until_a_new_key_is_saved()
    {
        var translator = new FakeTranslator { Status = TranslationStatus.InvalidKey };
        var secrets = new MemorySecretStore(FreeKey);
        var service = Service(translator, secrets);

        await service.EnsureTranslatedAsync(["A"], CancellationToken.None);
        await service.EnsureTranslatedAsync(["B"], CancellationToken.None);

        Assert.Single(translator.Calls);
        Assert.Equal(TranslationHealth.InvalidKey, service.Health);

        translator.Status = TranslationStatus.Ok;
        Assert.Equal(KeyCheck.Saved, await service.SaveKeyAsync(ProKey, CancellationToken.None));
        await service.EnsureTranslatedAsync(["B"], CancellationToken.None);

        Assert.Equal("en:B", service.TryGet("B"));
    }

    [Fact]
    public async Task Saving_verifies_the_key_first_and_stores_only_an_accepted_one()
    {
        var translator = new FakeTranslator { UsageStatus = TranslationStatus.InvalidKey };
        var secrets = new MemorySecretStore(null);
        var service = Service(translator, secrets);

        Assert.Equal(KeyCheck.Malformed, await service.SaveKeyAsync("nope", CancellationToken.None));
        Assert.Equal(KeyCheck.Rejected, await service.SaveKeyAsync(FreeKey, CancellationToken.None));
        translator.UsageStatus = TranslationStatus.Failed;
        Assert.Equal(KeyCheck.CouldNotVerify, await service.SaveKeyAsync(FreeKey, CancellationToken.None));
        Assert.Null(secrets.Stored);

        translator.UsageStatus = TranslationStatus.Ok;
        Assert.Equal(KeyCheck.Saved, await service.SaveKeyAsync($"  {FreeKey}  ", CancellationToken.None));
        Assert.Equal(FreeKey, secrets.Stored);
        Assert.True(service.IsConfigured);
    }

    [Fact]
    public async Task Removing_the_key_deletes_it_and_forgets_translations()
    {
        var secrets = new MemorySecretStore(FreeKey);
        var service = Service(new FakeTranslator(), secrets);
        await service.EnsureTranslatedAsync(["A"], CancellationToken.None);

        service.RemoveKey();

        Assert.Null(secrets.Stored);
        Assert.False(service.IsConfigured);
        Assert.Null(service.TryGet("A"));
        Assert.Equal(TranslationHealth.NotConfigured, service.Health);
    }

    [Fact]
    public async Task Failures_log_a_status_but_never_the_key_or_titles()
    {
        var logger = new CapturingLogger();
        var translator = new FakeTranslator { Status = TranslationStatus.Failed };
        var service = new TitleTranslationService(translator, new MemorySecretStore(FreeKey), TimeProvider.System, logger);

        await service.EnsureTranslatedAsync(["Hemligt jobb"], CancellationToken.None);

        Assert.NotEmpty(logger.Lines);
        Assert.All(logger.Lines, line =>
        {
            Assert.DoesNotContain(FreeKey, line, StringComparison.Ordinal);
            Assert.DoesNotContain("Hemligt", line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_cache_is_bounded_and_drops_the_oldest_first()
    {
        var cache = new TitleTranslationCache(3);

        foreach (var title in new[] { "a", "b", "c", "d" })
        {
            cache.Set(title, title.ToUpperInvariant());
        }

        Assert.Equal(3, cache.Count);
        Assert.Null(cache.TryGet("a"));
        Assert.Equal("D", cache.TryGet("d"));
    }

    [Fact]
    public async Task Titles_over_the_length_limit_are_left_alone()
    {
        var translator = new FakeTranslator();
        var service = Service(translator, new MemorySecretStore(FreeKey));

        await service.EnsureTranslatedAsync([new string('x', 301)], CancellationToken.None);

        Assert.Empty(translator.Calls);
    }

    [Fact]
    public void The_english_preference_round_trips_and_defaults_off()
    {
        Assert.False(PreferencesStore.Deserialize(null).ShowEnglishTitles);
        Assert.False(PreferencesStore.Deserialize("{}").ShowEnglishTitles);

        var json = PreferencesStore.Serialize(new AppPreferences { ShowEnglishTitles = true });

        Assert.True(PreferencesStore.Deserialize(json).ShowEnglishTitles);
        Assert.DoesNotContain("deepl", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "DPAPI exists only on Windows.")]
    [SupportedOSPlatform("windows")]
    public void The_encrypted_file_holds_no_plaintext_and_round_trips()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "deepl-key.bin");
        var store = new DpapiSecretStore(path);

        store.Save(FreeKey);

        var bytes = File.ReadAllBytes(path);
        Assert.DoesNotContain(FreeKey, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain(FreeKey, Encoding.Unicode.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(FreeKey, store.Load());

        store.Delete();
        Assert.Null(store.Load());
        Assert.False(File.Exists(path));
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "DPAPI exists only on Windows.")]
    [SupportedOSPlatform("windows")]
    public void A_damaged_key_file_reads_as_no_key()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "deepl-key.bin");
        File.WriteAllBytes(path, [1, 2, 3, 4]);

        Assert.Null(new DpapiSecretStore(path).Load());
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    private static TitleTranslationService Service(FakeTranslator translator, MemorySecretStore secrets, TimeProvider? time = null) =>
        new(translator, secrets, time ?? TimeProvider.System, NullLogger<TitleTranslationService>.Instance);

    private sealed class MemorySecretStore(string? initial) : ISecretStore
    {
        public string? Stored { get; private set; } = initial;

        public bool IsAvailable => true;

        public string? Load() => Stored;

        public void Save(string secret) => Stored = secret;

        public void Delete() => Stored = null;
    }

    private sealed class FakeTranslator : ITitleTranslator
    {
        public List<string[]> Calls { get; } = [];

        public TranslationStatus Status { get; set; } = TranslationStatus.Ok;

        public TranslationStatus UsageStatus { get; set; } = TranslationStatus.Ok;

        public Task<TranslationResult> TranslateAsync(string apiKey, IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            Calls.Add([.. texts]);
            return Task.FromResult(Status == TranslationStatus.Ok
                ? new TranslationResult(Status, texts.Select(t => "en:" + t).ToArray())
                : new TranslationResult(Status));
        }

        public Task<UsageResult> GetUsageAsync(string apiKey, CancellationToken cancellationToken) =>
            Task.FromResult(new UsageResult(UsageStatus, 10, 500_000));
    }

    private sealed record Seen(Uri Uri, string Body, AuthenticationHeaderValue? Authorization);

    private sealed class DeepLStub : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public bool DropLast { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Seen(request.RequestUri!, body, request.Headers.Authorization));
            if (Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(Status);
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal))
            {
                return Json("""{"character_count":42,"character_limit":500000}""");
            }

            using var document = JsonDocument.Parse(body);
            var texts = document.RootElement.GetProperty("text").EnumerateArray().Select(e => e.GetString()!).ToList();
            if (DropLast)
            {
                texts.RemoveAt(texts.Count - 1);
            }

            var items = texts.Select(t => new { detected_source_language = "SV", text = "en:" + t });
            return Json(JsonSerializer.Serialize(new { translations = items }));
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<TitleTranslationService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + exception);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arbetswatch-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
