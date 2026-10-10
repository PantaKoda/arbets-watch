using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArbetsWatch.Core.Translation;

public enum TranslationStatus
{
    Ok,
    InvalidKey,
    QuotaExceeded,
    RateLimited,
    Failed,
}

public sealed record TranslationResult(TranslationStatus Status, IReadOnlyList<string>? Texts = null, TimeSpan? RetryAfter = null);

public sealed record UsageResult(TranslationStatus Status, long CharactersUsed = 0, long CharacterLimit = 0);

/// <summary>Anything that turns Swedish titles into English; DeepL today.</summary>
public interface ITitleTranslator
{
    Task<TranslationResult> TranslateAsync(string apiKey, IReadOnlyList<string> texts, CancellationToken cancellationToken);

    Task<UsageResult> GetUsageAsync(string apiKey, CancellationToken cancellationToken);
}

/// <summary>
/// The DeepL REST API (<c>POST /v2/translate</c>, <c>GET /v2/usage</c>). Stateless: the key is passed per call and
/// sent only in the Authorization header of a request to a fixed DeepL host. Redirects are never followed, so the key
/// cannot be forwarded elsewhere, and nothing about the key appears in a result or message.
/// </summary>
public sealed class DeepLClient(HttpClient http) : ITitleTranslator
{
    /// <summary>The API accepts up to 50 texts per request.</summary>
    public const int MaxBatch = 50;

    private static readonly Uri FreeHost = new("https://api-free.deepl.com/");
    private static readonly Uri ProHost = new("https://api.deepl.com/");

    public static HttpClient CreateHttpClient(string userAgent)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    /// <summary>Free-plan keys end in <c>:fx</c> and use a different host from paid keys.</summary>
    public static Uri HostFor(string apiKey) => apiKey.EndsWith(":fx", StringComparison.Ordinal) ? FreeHost : ProHost;

    public async Task<TranslationResult> TranslateAsync(string apiKey, IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (!DeepLKey.IsWellFormed(apiKey))
        {
            return new(TranslationStatus.InvalidKey);
        }

        var all = new List<string>(texts.Count);
        for (var offset = 0; offset < texts.Count; offset += MaxBatch)
        {
            var batch = texts.Skip(offset).Take(MaxBatch).ToArray();
            var result = await TranslateBatchAsync(apiKey, batch, cancellationToken).ConfigureAwait(false);
            if (result.Status != TranslationStatus.Ok)
            {
                // Earlier batches were answered (and counted by DeepL): hand them back so they are cached.
                return all.Count > 0 ? result with { Texts = all } : result;
            }

            all.AddRange(result.Texts!);
        }

        return new(TranslationStatus.Ok, all);
    }

    public async Task<UsageResult> GetUsageAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (!DeepLKey.IsWellFormed(apiKey))
        {
            return new(TranslationStatus.InvalidKey);
        }

        using var request = Request(HttpMethod.Get, apiKey, "v2/usage");
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (Classify(response) is { } failure)
            {
                return new(failure.Status);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var usage = JsonSerializer.Deserialize(body, DeepLJsonContext.Default.UsageDto);
            return usage is null
                ? new(TranslationStatus.Failed)
                : new(TranslationStatus.Ok, usage.CharacterCount, usage.CharacterLimit);
        }
        catch (Exception ex) when (IsTransient(ex, cancellationToken))
        {
            return new(TranslationStatus.Failed);
        }
    }

    private async Task<TranslationResult> TranslateBatchAsync(string apiKey, string[] batch, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, apiKey, "v2/translate");
        // Serialized to a string first so the request carries a Content-Length rather than chunked encoding.
        request.Content = new StringContent(
            JsonSerializer.Serialize(new TranslateRequest(batch, "SV", "EN-GB"), DeepLJsonContext.Default.TranslateRequest),
            Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (Classify(response) is { } failure)
            {
                return failure;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize(body, DeepLJsonContext.Default.TranslateResponse);

            // A short or reordered answer would attach titles to the wrong ads: treat it as a failure.
            if (parsed?.Translations is not { } items || items.Length != batch.Length || items.Any(t => t.Text is null))
            {
                return new(TranslationStatus.Failed);
            }

            return new(TranslationStatus.Ok, items.Select(t => t.Text!).ToArray());
        }
        catch (Exception ex) when (IsTransient(ex, cancellationToken))
        {
            return new(TranslationStatus.Failed);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string apiKey, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(HostFor(apiKey), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    /// <summary>Null for success; otherwise the failure to report (without any request detail).</summary>
    private static TranslationResult? Classify(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return null;
        }

        return (int)response.StatusCode switch
        {
            401 or 403 => new(TranslationStatus.InvalidKey),
            456 => new(TranslationStatus.QuotaExceeded),
            429 or 529 => new(TranslationStatus.RateLimited, RetryAfter: RetryAfter(response)),
            _ => new(TranslationStatus.Failed),
        };
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or IOException or JsonException ||
        (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);
}

/// <summary>What a well-formed key looks like. Rejecting anything else also rules out header injection.</summary>
public static class DeepLKey
{
    public static bool IsWellFormed(string? key) =>
        key is { Length: >= 10 and <= 100 } && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or ':');
}

internal sealed record TranslateRequest(
    [property: JsonPropertyName("text")] string[] Text,
    [property: JsonPropertyName("source_lang")] string SourceLang,
    [property: JsonPropertyName("target_lang")] string TargetLang);

internal sealed record TranslateResponse(
    [property: JsonPropertyName("translations")] TranslatedText[]? Translations);

internal sealed record TranslatedText([property: JsonPropertyName("text")] string? Text);

internal sealed record UsageDto(
    [property: JsonPropertyName("character_count")] long CharacterCount,
    [property: JsonPropertyName("character_limit")] long CharacterLimit);

[JsonSerializable(typeof(TranslateRequest))]
[JsonSerializable(typeof(TranslateResponse))]
[JsonSerializable(typeof(UsageDto))]
internal sealed partial class DeepLJsonContext : JsonSerializerContext;
