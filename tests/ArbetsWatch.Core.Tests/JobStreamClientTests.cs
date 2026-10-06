using System.Net;
using System.Text;
using ArbetsWatch.Core.Ads;
using ArbetsWatch.Core.Sync;

namespace ArbetsWatch.Core.Tests;

public sealed class JobStreamClientTests
{
    [Fact]
    public async Task Change_request_asks_for_json_lines_with_offset_bounds()
    {
        HttpRequestMessage? seen = null;
        var client = Client(request =>
        {
            seen = request;
            return Lines(Fixtures.Read("removal.json").ReplaceLineEndings(" "));
        });

        var records = await client.ReadChangesAsync(
            new DateTimeOffset(2026, 10, 6, 17, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 6, 17, 10, 0, 900, TimeSpan.Zero),
            CancellationToken.None);

        Assert.NotNull(seen);
        Assert.Equal("application/jsonl", Assert.Single(seen.Headers.Accept).MediaType);
        Assert.Equal("/v2/stream", seen.RequestUri!.AbsolutePath);
        Assert.Equal(
            "?updated-after=2026-10-06T19%3A00%3A00%2B02%3A00&updated-before=2026-10-06T19%3A10%3A00%2B02%3A00",
            seen.RequestUri.Query);
        Assert.IsType<AdRemoval>(Assert.Single(records));
    }

    [Fact]
    public async Task Snapshot_is_read_line_by_line()
    {
        var client = Client(_ => Lines(File.ReadAllText(Fixtures.PathOf("stream-mixed.jsonl"))));
        var count = 0;
        await foreach (var _ in client.ReadSnapshotAsync(CancellationToken.None))
        {
            count++;
        }

        Assert.Equal(8, count);
    }

    [Fact]
    public async Task Rate_limit_carries_retry_after()
    {
        var client = Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return response;
        });

        var ex = await Assert.ThrowsAsync<JobStreamException>(() => client.ReadChangesAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(FailureKind.RateLimited, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(90), ex.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, FailureKind.Permanent)]
    [InlineData(HttpStatusCode.NotFound, FailureKind.Permanent)]
    [InlineData(HttpStatusCode.RequestTimeout, FailureKind.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, FailureKind.Transient)]
    [InlineData(HttpStatusCode.BadGateway, FailureKind.Transient)]
    public async Task Status_codes_map_to_failure_kinds(HttpStatusCode status, FailureKind expected)
    {
        var client = Client(_ => new HttpResponseMessage(status));
        var ex = await Assert.ThrowsAsync<JobStreamException>(() => client.ReadChangesAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public async Task Truncated_last_record_is_a_transient_failure()
    {
        var client = Client(_ => Lines("""{"id":"1","removed":true,"removed_date":"2026-10-06T19:08:13"}""" + "\n" + """{"id":"2","headl"""));
        var ex = await Assert.ThrowsAsync<JobStreamException>(() => client.ReadChangesAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(FailureKind.Transient, ex.Kind);
        Assert.Contains("Line 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connection_dropped_mid_body_is_a_transient_failure()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingStream(Encoding.UTF8.GetBytes("""{"id":"1","removed":true}""" + "\n"))),
        });

        var ex = await Assert.ThrowsAsync<JobStreamException>(async () =>
        {
            await foreach (var _ in client.ReadSnapshotAsync(CancellationToken.None))
            {
            }
        });
        Assert.Equal(FailureKind.Transient, ex.Kind);
    }

    [Fact]
    public async Task Stalled_response_is_abandoned()
    {
        var client = new JobStreamClient(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) }))
            {
                BaseAddress = JobStreamClient.DefaultBaseAddress,
            },
            stallTimeout: TimeSpan.FromMilliseconds(200));

        var ex = await Assert.ThrowsAsync<JobStreamException>(() => client.ReadChangesAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(FailureKind.Transient, ex.Kind);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_failure()
    {
        using var cts = new CancellationTokenSource();
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });
        cts.CancelAfter(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadChangesAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, cts.Token));
    }

    private static JobStreamClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)) { BaseAddress = JobStreamClient.DefaultBaseAddress });

    private static HttpResponseMessage Lines(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/jsonl") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    /// <summary>Returns its bytes, then fails as a dropped connection would.</summary>
    private sealed class FailingStream(byte[] prefix) : MemoryStream(prefix)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position < Length ? base.ReadAsync(buffer, cancellationToken) : throw new IOException("Connection reset");

        public override int Read(byte[] buffer, int offset, int count) =>
            Position < Length ? base.Read(buffer, offset, count) : throw new IOException("Connection reset");
    }

    /// <summary>Never delivers data.</summary>
    private sealed class StallingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
