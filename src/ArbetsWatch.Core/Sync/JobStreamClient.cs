using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using ArbetsWatch.Core.Ads;
using ArbetsWatch.Core.Time;

namespace ArbetsWatch.Core.Sync;

public interface IJobStreamClient
{
    /// <summary>Streams all currently published ads. Throws <see cref="JobStreamException"/> if incomplete.</summary>
    IAsyncEnumerable<SourceRecord> ReadSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Reads every change in <c>[after, before]</c> completely before returning.</summary>
    Task<IReadOnlyList<SourceRecord>> ReadChangesAsync(DateTimeOffset afterUtc, DateTimeOffset beforeUtc, CancellationToken cancellationToken);
}

public enum FailureKind
{
    /// <summary>Network error, timeout, server error or an incomplete/unparseable response. Retry with backoff.</summary>
    Transient,

    /// <summary>The service asked us to slow down (429, or 503 with Retry-After).</summary>
    RateLimited,

    /// <summary>The request itself was rejected (4xx). Retrying the same request will not help.</summary>
    Permanent,
}

public sealed class JobStreamException : Exception
{
    public JobStreamException(FailureKind kind, string message, Exception? inner = null, int? statusCode = null, TimeSpan? retryAfter = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public JobStreamException()
    {
    }

    public JobStreamException(string message)
        : base(message)
    {
    }

    public JobStreamException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public FailureKind Kind { get; }

    public int? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// HTTP adapter for JobStream (docs/api-contracts.md). Always requests JSON lines and parses them one at a
/// time, so neither the snapshot nor a change set is ever buffered as one string.
/// </summary>
public sealed class JobStreamClient(HttpClient http, TimeSpan? stallTimeout = null) : IJobStreamClient
{
    public static readonly Uri DefaultBaseAddress = new("https://jobstream.api.jobtechdev.se/");

    /// <summary>A response that delivers no line for this long is abandoned as transient failure.</summary>
    private readonly TimeSpan _stallTimeout = stallTimeout ?? TimeSpan.FromMinutes(2);

    public static HttpClient CreateHttpClient(string userAgent)
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        })
        {
            BaseAddress = DefaultBaseAddress,
            // The snapshot is ~450 MB; cancellation and the read-stall watchdog bound the time instead.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return http;
    }

    public async IAsyncEnumerable<SourceRecord> ReadSnapshotAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Removals never occur in a snapshot; if one did, it would carry its own date.
        var fallback = DateTimeOffset.UtcNow;
        await foreach (var record in ReadLinesAsync("v2/snapshot", fallback, cancellationToken).ConfigureAwait(false))
        {
            yield return record;
        }
    }

    public async Task<IReadOnlyList<SourceRecord>> ReadChangesAsync(DateTimeOffset afterUtc, DateTimeOffset beforeUtc, CancellationToken cancellationToken)
    {
        var path = "v2/stream" +
                   $"?updated-after={Uri.EscapeDataString(SwedishTime.FormatQueryBound(afterUtc))}" +
                   $"&updated-before={Uri.EscapeDataString(SwedishTime.FormatQueryBound(beforeUtc))}";
        var records = new List<SourceRecord>();
        await foreach (var record in ReadLinesAsync(path, SwedishTime.FloorToSecond(beforeUtc), cancellationToken).ConfigureAwait(false))
        {
            records.Add(record);
        }

        return records;
    }

    private async IAsyncEnumerable<SourceRecord> ReadLinesAsync(
        string path,
        DateTimeOffset removalFallback,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/jsonl"));

        // Cancelled by the caller, or by the watchdog when nothing arrives for _stallTimeout.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(_stallTimeout);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new JobStreamException(FailureKind.Transient, $"Could not reach JobStream: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new JobStreamException(FailureKind.Transient, "JobStream did not respond in time.", ex);
        }

        using (response)
        {
            ThrowForStatus(response);

            Stream body;
            try
            {
                body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException ||
                                       (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                throw new JobStreamException(FailureKind.Transient, "The JobStream response could not be read.", ex);
            }

            await using (body.ConfigureAwait(false))
            {
                using var reader = new StreamReader(body);
                long lineNumber = 0;
                while (true)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new JobStreamException(FailureKind.Transient, $"The JobStream response stalled after {lineNumber} lines.", ex);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException)
                    {
                        throw new JobStreamException(FailureKind.Transient, $"The JobStream response ended early after {lineNumber} lines.", ex);
                    }

                    stall.CancelAfter(_stallTimeout);

                    if (line is null)
                    {
                        yield break;
                    }

                    lineNumber++;
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    SourceRecord record;
                    try
                    {
                        record = AdRecordParser.Parse(line, removalFallback);
                    }
                    catch (AdParseException ex)
                    {
                        throw new JobStreamException(FailureKind.Transient, AdParseException.Describe(lineNumber, ex.Message), ex);
                    }

                    yield return record;
                }
            }
        }
    }

    private static void ThrowForStatus(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        var retryAfter = RetryAfter(response);
        var kind = status switch
        {
            429 => FailureKind.RateLimited,
            503 when retryAfter is not null => FailureKind.RateLimited,
            408 => FailureKind.Transient,
            >= 400 and < 500 => FailureKind.Permanent,
            _ => FailureKind.Transient,
        };
        throw new JobStreamException(kind, $"JobStream returned {status} {response.ReasonPhrase}.", statusCode: status, retryAfter: retryAfter);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } header)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta;
        }

        return header.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }
}
