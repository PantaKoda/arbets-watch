using ArbetsWatch.Core.Ads;

namespace ArbetsWatch.Core.Tests;

public sealed class AdRecordParserTests
{
    [Fact]
    public void Full_ad_projects_to_a_summary()
    {
        var ad = Assert.IsType<AdSummary>(AdRecordParser.Parse(Fixtures.Read("active-ad.json"), Fixtures.Fallback));

        Assert.Equal("90000001", ad.Id);
        Assert.Equal("Systemutvecklare till testföretaget", ad.Headline);
        Assert.Equal("Testföretaget AB", ad.Employer);
        Assert.Equal("https://arbetsformedlingen.se/platsbanken/annonser/90000001", ad.Url);
        Assert.Equal("PVZL_BQT_XtL", ad.MunicipalityId);
        Assert.Equal("Göteborg", ad.MunicipalityLabel);
        Assert.Equal("zdoY_6u5_Krt", ad.RegionId);
        Assert.Equal("i46j_HmG_v64", ad.CountryId);
        Assert.Equal("6YE1_gAC_R2G", ad.WorktimeId);
        Assert.Equal("Heltid", ad.WorktimeLabel);
        Assert.Equal("2026-10-06T19:02:25", ad.PublishedRaw);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 17, 2, 25, TimeSpan.Zero), ad.PublishedUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791306145951), ad.ChangedUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 17, 2, 25, 951, TimeSpan.Zero), ad.ChangedUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 22, 59, 59, TimeSpan.Zero), ad.LastPublicationUtc);
    }

    [Fact]
    public void Removal_has_its_own_shape()
    {
        var removal = Assert.IsType<AdRemoval>(AdRecordParser.Parse(Fixtures.Read("removal.json"), Fixtures.Fallback));

        Assert.Equal("90000008", removal.Id);
        Assert.Equal("2026-10-06T19:08:13", removal.RemovedRaw);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 17, 8, 13, TimeSpan.Zero), removal.RemovedUtc);
        Assert.Equal("PVZL_BQT_XtL", removal.MunicipalityId);
        Assert.Equal("zdoY_6u5_Krt", removal.RegionId);
    }

    [Fact]
    public void Removal_without_date_uses_the_interval_end()
    {
        var removal = Assert.IsType<AdRemoval>(AdRecordParser.Parse("""{"id":"5","removed":true}""", Fixtures.Fallback));
        Assert.Equal(Fixtures.Fallback, removal.RemovedUtc);
        Assert.Null(removal.RemovedRaw);
    }

    [Fact]
    public void Unreadable_removal_date_keeps_its_text_and_is_applied_as_newest()
    {
        var removal = Assert.IsType<AdRemoval>(AdRecordParser.Parse("""{"id":"5","removed":true,"removed_date":"2026-10-06 19:08"}""", Fixtures.Fallback));
        Assert.Equal(Fixtures.Fallback, removal.RemovedUtc);
        Assert.Equal("2026-10-06 19:08", removal.RemovedRaw);
    }

    [Fact]
    public void Active_ad_without_timestamp_is_ordered_as_newest_not_by_publication()
    {
        var ad = AdRecordParser.Parse("""{"id":"7","headline":"x","publication_date":"2025-01-01T10:00:00"}""", Fixtures.Fallback);
        Assert.Equal(Fixtures.Fallback, ad.SourceChangedUtc);
    }

    [Fact]
    public void Removal_in_the_repeated_autumn_hour_takes_the_later_instant()
    {
        var removal = Assert.IsType<AdRemoval>(AdRecordParser.Parse("""{"id":"5","removed":true,"removed_date":"2026-10-25T02:30:00"}""", Fixtures.Fallback));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero), removal.RemovedUtc);
    }

    [Fact]
    public void Missing_and_unknown_fields_are_preserved_as_given()
    {
        var records = Fixtures.StreamMixed().ToDictionary(r => r.Id);

        var noWorktime = Assert.IsType<AdSummary>(records["90000003"]);
        Assert.Null(noWorktime.WorktimeId);
        Assert.Null(noWorktime.WorktimeLabel);

        var regionOnly = Assert.IsType<AdSummary>(records["90000004"]);
        Assert.Null(regionOnly.MunicipalityId);
        Assert.Equal("wjee_qH2_yb6", regionOnly.RegionId);

        var abroad = Assert.IsType<AdSummary>(records["90000005"]);
        Assert.Equal("QJgN_Zge_BzJ", abroad.CountryId);
        Assert.Null(abroad.RegionId);

        var nulls = Assert.IsType<AdSummary>(records["90000006"]);
        Assert.Null(nulls.Employer);

        var unknown = Assert.IsType<AdSummary>(records["90000007"]);
        Assert.Equal("ZZZZ_fut_ure", unknown.WorktimeId);
        Assert.Equal("Framtida kategori", unknown.WorktimeLabel);

        Assert.IsType<AdRemoval>(records["90000008"]);
    }

    [Fact]
    public void Numeric_ids_are_normalized_to_strings()
    {
        var ad = AdRecordParser.Parse("""{"id":123,"headline":"x","timestamp":1}""", Fixtures.Fallback);
        Assert.Equal("123", ad.Id);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"headline":"no id"}""")]
    [InlineData("""{"id":""}""")]
    [InlineData("""{"id":"1","headline":"truncated""")]
    public void Malformed_records_throw(string line) =>
        Assert.Throws<AdParseException>(() => AdRecordParser.Parse(line, Fixtures.Fallback));
}
