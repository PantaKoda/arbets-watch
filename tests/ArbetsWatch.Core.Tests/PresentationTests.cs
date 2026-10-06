using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Places;
using ArbetsWatch.Core.Presentation;

namespace ArbetsWatch.Core.Tests;

public sealed class PresentationTests
{
    private const string Goteborg = "PVZL_BQT_XtL";
    private const string Molndal = "mc45_ki9_Bv3";
    private const string VastraGotaland = "zdoY_6u5_Krt";
    private const string Halland = "wjee_qH2_yb6";
    private static readonly PlaceCatalog Catalog = PlaceCatalog.LoadBundled();

    [Theory]
    [InlineData("31560730", "https://arbetsformedlingen.se/platsbanken/annonser/31560730", "https://arbetsformedlingen.se/platsbanken/annonser/31560730")]
    [InlineData("31560730", "https://www.arbetsformedlingen.se/platsbanken/annonser/31560730/", "https://www.arbetsformedlingen.se/platsbanken/annonser/31560730/")]
    [InlineData("31560730", "http://arbetsformedlingen.se/platsbanken/annonser/31560730", "https://arbetsformedlingen.se/platsbanken/annonser/31560730")]
    [InlineData("31560730", "https://evil.example/platsbanken/annonser/31560730", "https://arbetsformedlingen.se/platsbanken/annonser/31560730")]
    [InlineData("31560730", "https://arbetsformedlingen.se.evil.example/platsbanken/annonser/1", "https://arbetsformedlingen.se/platsbanken/annonser/31560730")]
    [InlineData("31560730", "https://user@arbetsformedlingen.se/platsbanken/annonser/31560730", "https://arbetsformedlingen.se/platsbanken/annonser/31560730")]
    [InlineData("31560730", "https://arbetsformedlingen.se/other/31560730", "https://arbetsformedlingen.se/platsbanken/annonser/31560730")]
    [InlineData("31560730", null, "https://arbetsformedlingen.se/platsbanken/annonser/31560730")]
    [InlineData("abc", "javascript:alert(1)", null)]
    [InlineData("abc", null, null)]
    public void Ad_links_are_limited_to_platsbanken_ad_pages(string id, string? url, string? expected) =>
        Assert.Equal(expected, AdLinkPolicy.Resolve(id, url)?.ToString());

    [Fact]
    public void Selecting_a_whole_region_absorbs_its_municipalities()
    {
        var filter = PlaceSelection.SetMunicipality(new AdFilter(), Goteborg, true);
        filter = PlaceSelection.SetMunicipality(filter, "3JKV_KSK_x6z", true); // Kungsbacka, Halland

        filter = PlaceSelection.SetRegion(filter, Catalog, Halland, true);

        Assert.Equal([Goteborg], filter.MunicipalityIds);
        Assert.Equal([Halland], filter.RegionIds);
        Assert.False(filter.AllSweden);
    }

    [Fact]
    public void Selecting_municipalities_never_selects_their_region()
    {
        var filter = new AdFilter();
        foreach (var m in Catalog.FindRegion(VastraGotaland)!.Municipalities)
        {
            filter = PlaceSelection.SetMunicipality(filter, m.Id, true);
        }

        Assert.Empty(filter.RegionIds);
        Assert.Equal(49, filter.MunicipalityIds.Count);
    }

    [Fact]
    public void All_sweden_replaces_other_choices_and_any_choice_turns_it_off()
    {
        var filter = PlaceSelection.SetMunicipality(new AdFilter(), Goteborg, true);
        filter = PlaceSelection.SetAllSweden(filter, true);
        Assert.True(filter.AllSweden);
        Assert.Empty(filter.MunicipalityIds);
        Assert.True(PlaceSelection.IsCovered(filter, Catalog.FindMunicipality(Molndal)!));

        filter = PlaceSelection.SetMunicipality(filter, Molndal, true);
        Assert.False(filter.AllSweden);
    }

    [Fact]
    public void Summary_lists_municipalities_then_regions()
    {
        var filter = new AdFilter
        {
            MunicipalityIds = new HashSet<string> { Molndal, Goteborg },
            RegionIds = new HashSet<string> { Halland },
        };
        Assert.Equal("Göteborg, Mölndal, Hallands län", PlaceSelection.Summary(filter, Catalog));
        Assert.Equal("Göteborg +2", PlaceSelection.Summary(filter, Catalog, maxNames: 1));
        Assert.Equal("All of Sweden", PlaceSelection.Summary(AdFilter.Default, Catalog));
        Assert.Equal("Choose places", PlaceSelection.Summary(new AdFilter(), Catalog));
    }

    [Fact]
    public void Published_times_are_swedish_local()
    {
        var now = new DateTimeOffset(2026, 10, 6, 18, 0, 0, TimeSpan.Zero); // 20:00 local
        Assert.Equal("Today 14:05", DisplayText.Published(new DateTimeOffset(2026, 10, 6, 12, 5, 0, TimeSpan.Zero), now));
        Assert.Equal("Yesterday 23:30", DisplayText.Published(new DateTimeOffset(2026, 10, 5, 21, 30, 0, TimeSpan.Zero), now));
        // 22:30Z on the 5th is already the 6th in Sweden.
        Assert.Equal("Today 00:30", DisplayText.Published(new DateTimeOffset(2026, 10, 5, 22, 30, 0, TimeSpan.Zero), now));
        Assert.Equal("3 Oct", DisplayText.Published(new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero), now));
        Assert.Equal("9 Jul 2025", DisplayText.Published(new DateTimeOffset(2025, 7, 9, 10, 0, 0, TimeSpan.Zero), now));
        Assert.Equal(string.Empty, DisplayText.Published(null, now));
    }

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(4 * 60, "4 min ago")]
    [InlineData(2 * 3600 + 59, "2 h ago")]
    [InlineData(30 * 3600, "yesterday")]
    [InlineData(3 * 86400, "3 days ago")]
    public void Ages_are_short(int seconds, string expected)
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(100);
        Assert.Equal(expected, DisplayText.Age(now.AddSeconds(-seconds), now));
    }

    [Fact]
    public void Missing_values_get_neutral_labels()
    {
        var records = Fixtures.StreamMixed().OfType<Ads.AdSummary>().ToDictionary(a => a.Id);
        Assert.Equal("Göteborg", DisplayText.Place(records["90000001"]));
        Assert.Equal("Hallands län", DisplayText.Place(records["90000004"]));
        Assert.Equal("Abroad", DisplayText.Place(records["90000005"]));
        Assert.Equal("Ej angiven", DisplayText.Worktime(records["90000003"]));
        Assert.Equal("Framtida kategori", DisplayText.Worktime(records["90000007"]));
        Assert.Equal(WorktimeSet.None, DisplayText.WorktimeCategory(records["90000007"]));
        Assert.Equal("12,345 ads", DisplayText.AdCount(12345, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("1 ad", DisplayText.AdCount(1));
    }
}
