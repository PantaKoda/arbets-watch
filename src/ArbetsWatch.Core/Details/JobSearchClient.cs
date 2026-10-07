using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArbetsWatch.Core.Details;

public interface IAdDetailsSource
{
    Task<AdDetailsResult> GetAsync(string id, CancellationToken cancellationToken);
}

/// <summary>
/// Reads one ad's full details from JobSearch (<c>GET /ad/{id}</c>, no key) when the user opens them. Nothing is
/// stored: the local cache stays owned by JobStream (docs/api-contracts.md, "Ad details").
/// </summary>
public sealed partial class JobSearchClient(HttpClient http) : IAdDetailsSource
{
    public static readonly Uri DefaultBaseAddress = new("https://jobsearch.api.jobtechdev.se/");

    public static HttpClient CreateHttpClient(string userAgent)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        })
        {
            BaseAddress = DefaultBaseAddress,
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    public async Task<AdDetailsResult> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!NumericId().IsMatch(id))
        {
            return new(AdDetailsStatus.NotFound);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"ad/{id}");
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new(AdDetailsStatus.NotFound);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(AdDetailsStatus.Failed, Error: $"Arbetsförmedlingen answered {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new(AdDetailsStatus.Found, AdDetailsParser.Parse(document.RootElement));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException ||
                                   (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new(AdDetailsStatus.Failed, Error: "No connection to Arbetsförmedlingen.");
        }
        catch (JsonException)
        {
            return new(AdDetailsStatus.Failed, Error: "The ad details could not be read.");
        }
    }

    [GeneratedRegex("^[0-9]{1,15}$")]
    private static partial Regex NumericId();
}
