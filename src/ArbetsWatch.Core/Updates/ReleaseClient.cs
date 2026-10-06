using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArbetsWatch.Core.Updates;

/// <summary>Releases, or why they couldn't be read (shown to the user as-is).</summary>
public sealed record ReleaseListResult(IReadOnlyList<ReleaseInfo>? Releases, string? Error)
{
    public bool IsSuccess => Releases is not null;
}

/// <summary>Where updates come from. Implemented by <see cref="ReleaseClient"/>; tests substitute their own.</summary>
public interface IReleaseSource
{
    Task<ReleaseListResult> GetReleasesAsync(CancellationToken cancellationToken);

    Task DownloadAsync(Uri url, Stream destination, long maxBytes, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Reads a public repository's GitHub releases anonymously and downloads release files. Downloads are
/// accepted only from that repository's release download URLs on github.com; GitHub then redirects to its
/// file storage over HTTPS.
/// </summary>
public sealed class ReleaseClient(HttpClient http, string repository) : IReleaseSource
{
    public const string DefaultRepository = "PantaKoda/arbets-watch";
    public static readonly Uri ApiBase = new("https://api.github.com/");
    public static readonly Uri WebBase = new("https://github.com/");

    private const int MaxNotesLength = 20_000;
    private readonly string _repository = repository.Trim();

    /// <summary>A download that receives nothing for this long is abandoned.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public Uri ReleasesApi => new(ApiBase, $"repos/{_repository}/releases?per_page=30");

    public Uri ReleasesPage => new(WebBase, $"{_repository}/releases");

    public static HttpClient CreateHttpClient(string userAgent)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            // Release downloads redirect from github.com to GitHub's file storage.
            AllowAutoRedirect = true,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    public async Task<ReleaseListResult> GetReleasesAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new(null, $"GitHub has no public releases for {_repository}.");
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return new(null, "GitHub's limit for anonymous checks was reached. Try again in an hour.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(null, $"GitHub answered {(int)response.StatusCode} when checking for updates.");
            }

            var dtos = await response.Content.ReadFromJsonAsync(ReleaseJsonContext.Default.ListReleaseDto, timeout.Token).ConfigureAwait(false) ?? [];
            return new([.. dtos.Select(Map).OfType<ReleaseInfo>()], null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new(null, "Couldn't reach GitHub to check for updates.");
        }
    }

    /// <summary>True when <paramref name="url"/> is a release download of the configured repository.</summary>
    public bool IsReleaseDownload(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(url.UserInfo) && url.IsDefaultPort
            && string.Equals(url.IdnHost, WebBase.IdnHost, StringComparison.OrdinalIgnoreCase)
            && url.AbsolutePath.StartsWith($"/{_repository}/releases/download/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Downloads a release file into <paramref name="destination"/>, refusing anything larger than <paramref name="maxBytes"/>.</summary>
    public async Task DownloadAsync(Uri url, Stream destination, long maxBytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!IsReleaseDownload(url))
        {
            throw new InvalidOperationException($"Refusing to download '{url}': it isn't a release file of {_repository}.");
        }

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { Scheme: not "https" })
        {
            throw new InvalidOperationException("The download was redirected away from HTTPS.");
        }

        var total = response.Content.Headers.ContentLength;
        if (total > maxBytes)
        {
            throw new InvalidOperationException($"The download is larger ({total} bytes) than expected.");
        }

        var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            long received = 0;
            while (true)
            {
                int read;
                using (var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    stall.CancelAfter(StallTimeout);
                    try
                    {
                        read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException($"the download stalled (nothing received for {StallTimeout.TotalSeconds:0} seconds)");
                    }
                }

                if (read == 0)
                {
                    break;
                }

                received += read;
                if (received > maxBytes)
                {
                    throw new InvalidOperationException("The download is larger than expected.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (total is > 0)
                {
                    progress?.Report((double)received / total.Value);
                }
            }
        }
    }

    private static ReleaseInfo? Map(ReleaseDto dto)
    {
        if (!AppVersion.TryParse(dto.TagName, out var version) || !Uri.TryCreate(dto.HtmlUrl, UriKind.Absolute, out var html) || html.Scheme != Uri.UriSchemeHttps)
        {
            return null; // not a version tag (e.g. "nightly"): not an update
        }

        var notes = dto.Body ?? string.Empty;
        return new ReleaseInfo
        {
            Version = version,
            Tag = dto.TagName!,
            Title = string.IsNullOrWhiteSpace(dto.Name) ? $"ArbetsWatch {version}" : dto.Name.Trim(),
            Notes = notes.Length > MaxNotesLength ? notes[..MaxNotesLength] + "\n…" : notes,
            HtmlUrl = html,
            PublishedAt = dto.PublishedAt,
            IsDraft = dto.Draft,
            IsPreRelease = dto.Prerelease,
            Assets = [.. (dto.Assets ?? [])
                .Where(a => !string.IsNullOrEmpty(a.Name) && Uri.TryCreate(a.BrowserDownloadUrl, UriKind.Absolute, out _))
                .Select(a => new ReleaseAsset(a.Name!, new Uri(a.BrowserDownloadUrl!), a.Size))],
        };
    }
}

internal sealed record ReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    [JsonPropertyName("draft")]
    public bool Draft { get; init; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; init; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; init; }

    [JsonPropertyName("assets")]
    public List<ReleaseAssetDto>? Assets { get; init; }
}

internal sealed record ReleaseAssetDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }
}

[JsonSerializable(typeof(List<ReleaseDto>))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext;
