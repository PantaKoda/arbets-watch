using System.Net;
using System.Text;
using System.Text.Json;
using ArbetsWatch.Core.Details;

namespace ArbetsWatch.Core.Tests;

public sealed class AdDetailsTests
{
    [Theory]
    [InlineData("https://jobnet.se/stadare-stadbolaget-vastra-frolunda-7", "https://jobnet.se/stadare-stadbolaget-vastra-frolunda-7")]
    [InlineData("http://example.se/apply", "http://example.se/apply")]
    [InlineData("www.idealbemanning.se/jobb/123", "https://www.idealbemanning.se/jobb/123")]
    [InlineData("  https://example.se/a  ", "https://example.se/a")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///C:/Windows/System32/calc.exe", null)]
    [InlineData("https://user:pass@example.se/", null)]
    [InlineData("https://example .se/", null)]
    [InlineData("Skicka ansökan via e-post", null)]
    [InlineData("example.se", null)]
    [InlineData("https://localhost/", null)]
    [InlineData(null, null)]
    public void Only_web_pages_become_apply_links(string? text, string? expected) =>
        Assert.Equal(expected, ExternalLinks.WebLink(text)?.AbsoluteUri);

    [Theory]
    [InlineData("info@m-kt.se", "info@m-kt.se")]
    [InlineData(" jobb@example.co.uk ", "jobb@example.co.uk")]
    [InlineData("not an address", null)]
    [InlineData("a@b", null)]
    [InlineData("a@b.se?subject=x", null)]
    [InlineData("<a@b.se>", null)]
    public void Email_addresses_are_validated(string text, string? expected) =>
        Assert.Equal(expected, ExternalLinks.Email(text));

    [Fact]
    public void Mailto_escapes_the_subject()
    {
        var uri = ExternalLinks.MailTo("jobb@example.se", "Ref 12 & co");
        Assert.Equal("mailto:jobb@example.se?subject=Ref%2012%20%26%20co", uri.AbsoluteUri);
    }

    [Fact]
    public void Full_ad_is_organised_into_details()
    {
        using var document = JsonDocument.Parse(Fixtures.Read("active-ad.json"));
        var d = AdDetailsParser.Parse(document.RootElement);

        Assert.Equal("90000001", d.Id);
        Assert.Equal("Systemutvecklare till testföretaget", d.Headline);
        Assert.Equal("Testföretaget AB", d.Employer);
        Assert.Equal("Synthetic description for tests.", d.Description);
        Assert.Equal("Heltid", d.WorkingHours);
        Assert.Equal("100 %", d.Scope);
        Assert.Equal("Testgatan 1", d.StreetAddress);
        Assert.Equal("Göteborg", d.Municipality);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 22, 59, 59, TimeSpan.Zero), d.ApplicationDeadline);
        Assert.True(d.DrivingLicenseRequired);
        Assert.Equal(["B"], d.DrivingLicenses);
        Assert.Contains(d.Requirements, r => r is { Category: "Skill", Label: "Java, programmeringsspråk", Required: true });
        Assert.Contains(d.Requirements, r => r is { Category: "Language", Label: "Svenska", Required: false });
        Assert.Contains(d.Requirements, r => r is { Category: "Education level", Required: true });
        var contact = Assert.Single(d.Contacts);
        Assert.Equal("test@example.invalid", contact.Email);
        Assert.True(d.Application.ViaPlatsbanken);
        Assert.Equal("https://arbetsformedlingen.se/platsbanken/annonser/90000001", d.PlatsbankenPage?.AbsoluteUri);
    }

    [Fact]
    public void Application_details_are_validated_and_free_text_is_kept()
    {
        var json = """
            {"id":"5","headline":"x","application_details":{"url":"www.example.se/apply","email":"jobb@example.se",
             "reference":"REF-1","other":"Endast via e-post","via_af":false},
             "scope_of_work":{"min":50,"max":75},"employer":{"name":"A","url":"javascript:evil()"}}
            """;
        using var document = JsonDocument.Parse(json);
        var d = AdDetailsParser.Parse(document.RootElement);

        Assert.Equal("https://www.example.se/apply", d.Application.Link?.AbsoluteUri);
        Assert.Equal("jobb@example.se", d.Application.Email);
        Assert.Equal("REF-1", d.Application.Reference);
        Assert.Equal("Endast via e-post", d.Application.Other);
        Assert.False(d.Application.ViaPlatsbanken);
        Assert.Equal("50–75 %", d.Scope);
        Assert.Null(d.EmployerWebsite);
    }

    [Fact]
    public async Task Client_reads_an_ad_and_reports_missing_and_failed_ones()
    {
        var requested = new List<string>();
        var client = new JobSearchClient(new HttpClient(new Stub(r =>
        {
            requested.Add(r.RequestUri!.AbsolutePath);
            return r.RequestUri.AbsolutePath switch
            {
                "/ad/90000001" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixtures.Read("active-ad.json"), Encoding.UTF8, "application/json") },
                "/ad/404" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/ad/500" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                _ => throw new HttpRequestException("offline"),
            };
        }))
        { BaseAddress = JobSearchClient.DefaultBaseAddress });

        var found = await client.GetAsync("90000001", CancellationToken.None);
        Assert.Equal(AdDetailsStatus.Found, found.Status);
        Assert.Equal("Testföretaget AB", found.Details!.Employer);

        Assert.Equal(AdDetailsStatus.NotFound, (await client.GetAsync("404", CancellationToken.None)).Status);
        Assert.Equal(AdDetailsStatus.Failed, (await client.GetAsync("500", CancellationToken.None)).Status);
        var offline = await client.GetAsync("1", CancellationToken.None);
        Assert.Equal(AdDetailsStatus.Failed, offline.Status);
        Assert.Contains("No connection", offline.Error, StringComparison.Ordinal);

        // Non-numeric IDs never reach the network (they can't be Platsbanken ads, and must not alter the path).
        Assert.Equal(AdDetailsStatus.NotFound, (await client.GetAsync("../search", CancellationToken.None)).Status);
        Assert.DoesNotContain(requested, p => p.Contains("search", StringComparison.Ordinal));
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
