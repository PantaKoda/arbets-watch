using System.Net;
using ArbetsWatch.Core.Details;
using ArbetsWatch.Core.Translation;

namespace ArbetsWatch.Web.Tests;

[Collection(WebCollection.Name)]
public sealed class SecurityTests
{
    private static readonly string[] Pages =
    [
        "/", "/index.html", "/js/app.js", "/app.css", "/api/status", "/api/places", "/api/ads?all=1", "/api/nothing", "/no-such-page",
    ];

    [Fact]
    public async Task The_server_listens_on_loopback_only_whatever_the_command_line_says()
    {
        await using var web = await WebFixture.StartAsync(args: ["--urls", "http://0.0.0.0:5999;http://[::]:5999"]);

        var address = new Uri(Assert.Single(WebApp.Addresses(web.App)));
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(address.Host)), address.ToString());
    }

    [Theory]
    [InlineData("ASPNETCORE_URLS", "http://0.0.0.0:0")]
    [InlineData("URLS", "http://0.0.0.0:0")]
    [InlineData("ASPNETCORE_HTTP_PORTS", "5998")]
    [InlineData("Kestrel__Endpoints__Extra__Url", "http://0.0.0.0:0")]
    public async Task The_server_listens_on_loopback_only_whatever_the_environment_says(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        Environment.SetEnvironmentVariable("ASPNETCORE_PREFERHOSTINGURLS", "true");
        try
        {
            await using var web = await WebFixture.StartAsync();

            var address = new Uri(Assert.Single(WebApp.Addresses(web.App)));
            Assert.True(IPAddress.IsLoopback(IPAddress.Parse(address.Host)), address.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable("ASPNETCORE_PREFERHOSTINGURLS", null);
        }
    }

    [Fact]
    public async Task The_environment_cannot_bring_back_request_logging()
    {
        Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Trace");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Microsoft", "Trace");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Microsoft.AspNetCore", "Trace");
        try
        {
            await using var web = await WebFixture.StartAsync();

            await web.Http.GetAsync("/api/ads?all=1&q=PRIVATSOKORD");

            Assert.DoesNotContain(web.Logs, line => line.Contains("PRIVATSOKORD", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Logging__LogLevel__Default", null);
            Environment.SetEnvironmentVariable("Logging__LogLevel__Microsoft", null);
            Environment.SetEnvironmentVariable("Logging__LogLevel__Microsoft.AspNetCore", null);
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:5317", true)]
    [InlineData("http://[::1]:5317", true)]
    [InlineData("http://0.0.0.0:5317", false)]
    [InlineData("http://[::]:5317", false)]
    [InlineData("http://192.168.1.5:5317", false)]
    [InlineData("http://*:5317", false)]
    [InlineData("http://example.se:5317", false)]
    public void Only_loopback_addresses_pass_the_startup_check(string address, bool loopback)
    {
        Assert.Equal(loopback, WebApp.IsLoopback(address));
    }

    [Fact]
    public async Task The_environment_cannot_switch_on_development_behaviour()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        try
        {
            var details = new StubDetails { Answer = _ => throw new InvalidOperationException("C:\\secret\\path boom") };
            await using var web = await WebFixture.StartAsync(details: details);

            Assert.Equal("Production", web.App.Environment.EnvironmentName);
            var body = await (await web.Http.GetAsync("/api/ads/1/details")).Content.ReadAsStringAsync();
            Assert.DoesNotContain("boom", body, StringComparison.Ordinal);
            Assert.DoesNotContain("InvalidOperation", body, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);
        }
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("localhost.evil.example")]
    [InlineData("127.0.0.1.nip.io")]
    public async Task A_foreign_host_name_is_refused_so_dns_rebinding_gets_nothing(string host)
    {
        await using var web = await WebFixture.StartAsync();

        var response = await web.Http.SendWithAsync(HttpMethod.Get, "/api/ads?all=1", "Host", host);

        // Refused before the rest of the pipeline, with nothing in the body to protect.
        Assert.Equal(HttpStatusCode.BadRequest, response.Code());
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public async Task The_loopback_names_are_accepted(string host)
    {
        await using var web = await WebFixture.StartAsync();

        var response = await web.Http.SendWithAsync(HttpMethod.Get, "/api/status", "Host", $"{host}:{web.BaseAddress.Port}");

        Assert.Equal(HttpStatusCode.OK, response.Code());
    }

    [Fact]
    public async Task Every_response_carries_the_browser_protections_and_no_cookie()
    {
        await using var web = await WebFixture.StartAsync();

        var responses = new List<HttpResponseMessage>();
        foreach (var page in Pages)
        {
            responses.Add(await web.Http.GetAsync(page));
        }

        responses.Add(await web.Http.PostAsync("/api/ads", null));

        foreach (var response in responses)
        {
            var at = $"{response.RequestMessage!.Method} {response.RequestMessage.RequestUri} ({(int)response.StatusCode})";
            Assert.True(response.Headers.Contains("Content-Security-Policy"), at);
            var csp = string.Join(' ', response.Headers.GetValues("Content-Security-Policy"));
            Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
            Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
            Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
            Assert.Contains("require-trusted-types-for 'script'", csp, StringComparison.Ordinal);
            Assert.DoesNotContain("unsafe", csp, StringComparison.Ordinal);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
            Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
            Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Cross-Origin-Resource-Policy")));
            Assert.False(response.Headers.Contains("Set-Cookie"), at);
            Assert.False(response.Headers.Contains("Server"), at);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"), at);
        }
    }

    [Fact]
    public async Task Api_answers_are_never_cached()
    {
        await using var web = await WebFixture.StartAsync();

        var response = await web.Http.GetAsync("/api/ads?all=1&q=natt");

        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    public async Task Anything_but_reading_is_refused_everywhere(string method)
    {
        await using var web = await WebFixture.StartAsync();

        foreach (var path in new[] { "/", "/api/status", "/api/ads", "/api/ads/100/details" })
        {
            var response = await web.Http.SendWithAsync(new HttpMethod(method), path);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.Code());
        }
    }

    [Theory]
    [InlineData("/api/ads?all=1&english=1", "cross-site")]
    [InlineData("/api/ads?all=1&english=1", "same-site")]
    [InlineData("/API/ads?all=1&english=1", "cross-site")]
    [InlineData("/Api/ADS?all=1&english=1", "cross-site")]
    [InlineData("/%61pi/ads?all=1&english=1", "cross-site")]
    public async Task Api_calls_started_by_another_site_are_refused(string path, string site)
    {
        await using var web = await WebFixture.StartAsync(key: WebFixture.DeepLKey);

        var response = await web.Http.SendWithAsync(HttpMethod.Get, path, "Sec-Fetch-Site", site);

        Assert.Equal(HttpStatusCode.Forbidden, response.Code());
        Assert.Empty(web.Translator.Sent);
    }

    [Theory]
    [InlineData("/api/ads?all=1&english=1")]
    [InlineData("/API/ads?all=1&english=1")]
    public async Task Without_the_page_s_own_header_the_api_answers_nothing(string path)
    {
        await using var web = await WebFixture.StartAsync(key: WebFixture.DeepLKey);

        // A plain request, as a no-cors fetch or an image tag makes one; also a browser that sends no Sec-Fetch-Site.
        using var plain = new HttpClient { BaseAddress = web.BaseAddress };
        var response = await plain.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.Code());
        Assert.Empty(web.Translator.Sent);
    }

    [Fact]
    public async Task The_preflight_that_header_would_need_is_refused()
    {
        await using var web = await WebFixture.StartAsync();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/ads?all=1");
        preflight.Headers.Add("Origin", "https://evil.example");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "x-arbetswatch");

        var response = await web.Http.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.Code());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Headers"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Api_answers_keep_no_store_whatever_the_case_of_the_path()
    {
        await using var web = await WebFixture.StartAsync();

        Assert.True((await web.Http.GetAsync("/API/status")).Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    public async Task Api_calls_from_the_page_itself_pass(string site)
    {
        await using var web = await WebFixture.StartAsync();

        var response = await web.Http.SendWithAsync(HttpMethod.Get, "/api/status", "Sec-Fetch-Site", site);

        Assert.Equal(HttpStatusCode.OK, response.Code());
    }

    [Theory]
    [InlineData("/ArbetsWatch.Web.dll")]
    [InlineData("/ArbetsWatch.Web.deps.json")]
    [InlineData("/appsettings.json")]
    [InlineData("/.env")]
    [InlineData("/test.db")]
    [InlineData("/../ArbetsWatch.Web.dll")]
    [InlineData("/..%2fArbetsWatch.Web.dll")]
    [InlineData("/%2e%2e/ArbetsWatch.Web.dll")]
    [InlineData("/js/..%2f..%2fArbetsWatch.Web.dll")]
    [InlineData("/js/")]
    public async Task Only_the_web_root_is_served(string path)
    {
        await using var web = await WebFixture.StartAsync();

        // Sent as written; HttpClient would otherwise tidy the dots away.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(web.BaseAddress + path.TrimStart('/'), new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }));
        var response = await web.Http.SendAsync(request);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.BadRequest });
    }

    [Theory]
    [InlineData("/api/ads?regions=not-a-place")]
    [InlineData("/api/ads?municipalities=%27%3B%20DROP%20TABLE%20ad_summary%3B--")]
    [InlineData("/api/ads?all=1&worktime=0")]
    [InlineData("/api/ads?all=1&worktime=8")]
    [InlineData("/api/ads?all=1&worktime=abc")]
    [InlineData("/api/ads?all=1&limit=0")]
    [InlineData("/api/ads?all=1&limit=201")]
    [InlineData("/api/ads?all=1&limit=-1")]
    [InlineData("/api/ads?all=1&cursor=nonsense")]
    [InlineData("/api/ads?all=1&cursor=1_%27%20OR%201%3D1")]
    [InlineData("/api/ads?all=1&cursor=999999999999999_1")]
    [InlineData("/api/ads/new?all=1")]
    [InlineData("/api/ads/new?all=1&since=-5")]
    [InlineData("/api/ads/new?all=1&since=99999999999999999")]
    [InlineData("/api/ads/published?ids=%27%3BDROP")]
    public async Task Bad_parameters_are_rejected_without_detail(string path)
    {
        await using var web = await WebFixture.StartAsync();

        var response = await web.Http.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.Code());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(3, (await web.Store.FilterPublishedAsync(["100", "101", "102"], DateTimeOffset.UtcNow)).Count);
    }

    [Fact]
    public async Task Too_long_a_search_or_id_list_is_rejected()
    {
        await using var web = await WebFixture.StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.GetAsync("/api/ads?all=1&q=" + new string('a', 201))).Code());
        var ids = string.Join(',', Enumerable.Range(1, AdsApiLimits.MaxPublishedIds + 1));
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.GetAsync("/api/ads/published?ids=" + ids)).Code());
    }

    [Fact]
    public async Task Sql_in_the_search_box_is_only_ever_text()
    {
        await using var web = await WebFixture.StartAsync();

        var response = await web.Http.GetAsync("/api/ads?all=1&q=" + Uri.EscapeDataString("'; DROP TABLE ad_summary; --"));

        Assert.Equal(HttpStatusCode.OK, response.Code());
        Assert.Contains("\"total\":0", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(3, (await web.Store.FilterPublishedAsync(["100", "101", "102"], DateTimeOffset.UtcNow)).Count);
    }

    [Theory]
    [InlineData("..%2f..%2fetc")]
    [InlineData("1;rm")]
    [InlineData("abc")]
    [InlineData("1234567890123456")]
    [InlineData("123%0A")]
    public async Task Only_an_ad_number_can_reach_the_upstream_details_request(string id)
    {
        var details = new StubDetails();
        await using var web = await WebFixture.StartAsync(details: details);

        var response = await web.Http.GetAsync($"/api/ads/{id}/details");

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound });
        Assert.Empty(details.Requested);
    }

    [Fact]
    public async Task Upstream_failures_and_server_errors_answer_with_fixed_text()
    {
        var details = new StubDetails
        {
            Answer = _ => new AdDetailsResult(AdDetailsStatus.Failed, Error: "internal hostname db-7 refused"),
        };
        await using var web = await WebFixture.StartAsync(details: details);

        var failed = await web.Http.GetAsync("/api/ads/1/details");
        Assert.Equal(HttpStatusCode.BadGateway, failed.Code());
        Assert.DoesNotContain("db-7", await failed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        details.Answer = _ => throw new InvalidOperationException("Connection string Password=hunter2");
        var crashed = await web.Http.GetAsync("/api/ads/1/details");
        Assert.Equal(HttpStatusCode.InternalServerError, crashed.Code());
        var body = await crashed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hunter2", body, StringComparison.Ordinal);
        Assert.Contains("Something went wrong", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_that_cost_an_upstream_call_are_rate_limited()
    {
        var details = new StubDetails();
        await using var web = await WebFixture.StartAsync(details: details);

        HttpResponseMessage? refused = null;
        for (var i = 0; i < 70 && refused is null; i++)
        {
            var response = await web.Http.GetAsync("/api/ads/1/details");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                refused = response;
            }
        }

        Assert.NotNull(refused);
        Assert.Contains("60", refused.Headers.GetValues("Retry-After"));
        Assert.True(details.Requested.Count <= 60, $"{details.Requested.Count} upstream requests");
    }

    [Theory]
    [InlineData(TranslationStatus.Ok)]
    [InlineData(TranslationStatus.InvalidKey)]
    [InlineData(TranslationStatus.QuotaExceeded)]
    [InlineData(TranslationStatus.RateLimited)]
    [InlineData(TranslationStatus.Failed)]
    public async Task The_deepl_key_never_appears_in_an_answer_or_a_log(TranslationStatus deepL)
    {
        await using var web = await WebFixture.StartAsync(key: WebFixture.DeepLKey);
        web.Translator.Status = deepL;

        foreach (var page in new[]
        {
            "/api/ads?all=1&english=1", "/api/ads?all=1", "/api/status", "/api/places", "/api/ads/new?all=1&since=0",
            "/api/ads/published?ids=100", "/api/ads/100/details", "/", "/js/app.js", "/js/logic.js", "/no-such-page", "/api/nothing",
        })
        {
            var response = await web.Http.GetAsync(page);
            var everything = await response.Content.ReadAsStringAsync() + response.Headers;
            Assert.DoesNotContain(WebFixture.DeepLKey, everything, StringComparison.Ordinal);
            Assert.DoesNotContain("00000000-0000", everything, StringComparison.Ordinal);
        }

        Assert.Equal(WebFixture.DeepLKey, Assert.Single(web.Translator.KeysUsed.Distinct()));
        Assert.DoesNotContain(web.Logs, line => line.Contains("00000000-0000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task English_titles_have_a_tighter_budget_that_a_plain_list_does_not_share()
    {
        await using var web = await WebFixture.StartAsync(key: WebFixture.DeepLKey);

        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 61; i++)
        {
            last = (await web.Http.GetAsync("/api/ads?all=1&english=1&limit=1")).Code();
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync("/api/ads?all=1&limit=1")).Code());
    }

    [Fact]
    public async Task Search_words_places_and_saved_ids_stay_out_of_the_logs()
    {
        await using var web = await WebFixture.StartAsync();

        await web.Http.GetAsync("/api/ads?all=1&q=PRIVATSOKORD");
        await web.Http.GetAsync("/api/ads?municipalities=" + WebFixture.Goteborg);
        await web.Http.GetAsync("/api/ads/published?ids=31415926");
        await web.Http.GetAsync("/api/ads/27182818/details");
        await web.Http.GetAsync("/api/ads?regions=PRIVATSOKORD");

        foreach (var marker in new[] { "PRIVATSOKORD", WebFixture.Goteborg, "31415926", "27182818" })
        {
            Assert.DoesNotContain(web.Logs, line => line.Contains(marker, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task No_endpoint_accepts_or_reports_a_key()
    {
        await using var web = await WebFixture.StartAsync(key: WebFixture.DeepLKey);

        var status = await (await web.Http.GetAsync("/api/status")).Content.ReadAsStringAsync();
        Assert.Contains("\"translation\":\"Ready\"", status, StringComparison.Ordinal);
        Assert.DoesNotContain("key\"", status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await web.Http.PostAsync("/api/translation/key", null)).Code());
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync("/api/translation/key?key=x")).Code());
    }
}
