using ArbetsWatch.Core.Ads;
using ArbetsWatch.Core.Filtering;
using Microsoft.Data.Sqlite;

namespace ArbetsWatch.Core.Tests;

public sealed class AdFilterTests
{
    private const string Goteborg = "PVZL_BQT_XtL";
    private const string Molndal = "mc45_ki9_Bv3";
    private const string Kungsbacka = "3JKV_KSK_x6z";
    private const string VastraGotaland = "zdoY_6u5_Krt";
    private const string Halland = "wjee_qH2_yb6";
    private const string Sweden = "i46j_HmG_v64";
    private const string Norway = "QJgN_Zge_BzJ";

    private static readonly AdFilter GoteborgMolndalAndHalland = new()
    {
        RegionIds = Set(Halland),
        MunicipalityIds = Set(Goteborg, Molndal),
        Worktime = WorktimeSet.All,
    };

    [Fact]
    public void Municipalities_and_whole_regions_combine_with_or()
    {
        Assert.True(AdMatcher.Matches(GoteborgMolndalAndHalland, Fixtures.Ad("1", Goteborg, VastraGotaland)));
        Assert.True(AdMatcher.Matches(GoteborgMolndalAndHalland, Fixtures.Ad("2", Molndal, VastraGotaland)));
        Assert.True(AdMatcher.Matches(GoteborgMolndalAndHalland, Fixtures.Ad("3", Kungsbacka, Halland)));
        // Region-only ad in a fully selected region.
        Assert.True(AdMatcher.Matches(GoteborgMolndalAndHalland, Fixtures.Ad("4", null, Halland)));
    }

    [Fact]
    public void Partly_selected_region_does_not_select_the_whole_region()
    {
        // Another Västra Götaland municipality, and a region-only Västra Götaland ad.
        Assert.False(AdMatcher.Matches(GoteborgMolndalAndHalland, Fixtures.Ad("1", "other_muni", VastraGotaland)));
        Assert.False(AdMatcher.Matches(GoteborgMolndalAndHalland, Fixtures.Ad("2", null, VastraGotaland)));
    }

    [Fact]
    public void All_sweden_includes_ads_without_municipality_but_not_abroad()
    {
        var all = AdFilter.Default;
        Assert.True(AdMatcher.Matches(all, Fixtures.Ad("1", null, null)));
        Assert.True(AdMatcher.Matches(all, Fixtures.Ad("2", null, Halland)));
        Assert.False(AdMatcher.Matches(all, Fixtures.Ad("3", null, null, Norway)));
    }

    [Fact]
    public void All_sweden_includes_ads_without_a_country()
    {
        // Not observed in the snapshot (docs/api-contracts.md), but All Sweden must never show less than a län.
        Assert.True(AdMatcher.Matches(AdFilter.Default, Fixtures.Ad("1", null, Halland, countryId: null)));
        Assert.True(AdMatcher.Matches(AdFilter.Default, Fixtures.Ad("2", null, null, countryId: null)));
        Assert.True(AdMatcher.Matches(new AdFilter { RegionIds = Set(Halland) }, Fixtures.Ad("3", null, Halland, countryId: null)));
    }

    [Fact]
    public void Empty_geography_matches_nothing()
    {
        var none = new AdFilter { Worktime = WorktimeSet.All };
        Assert.False(none.HasGeography);
        Assert.False(AdMatcher.Matches(none, Fixtures.Ad("1")));
    }

    [Theory]
    [InlineData(WorktimeSet.All, "6YE1_gAC_R2G", true)]
    [InlineData(WorktimeSet.All, null, true)]
    [InlineData(WorktimeSet.All, "ZZZZ_fut_ure", true)]
    [InlineData(WorktimeSet.PartTime, "947z_JGS_Uk2", true)]
    [InlineData(WorktimeSet.PartTime, null, false)]
    [InlineData(WorktimeSet.PartTime, "6YE1_gAC_R2G", false)]
    [InlineData(WorktimeSet.PartTime | WorktimeSet.NotSpecified, null, true)]
    [InlineData(WorktimeSet.FullTime | WorktimeSet.PartTime, "ZZZZ_fut_ure", false)]
    [InlineData(WorktimeSet.FullTime | WorktimeSet.PartTime, null, false)]
    [InlineData(WorktimeSet.None, "6YE1_gAC_R2G", false)]
    public void Worktime_rules(WorktimeSet selected, string? worktimeId, bool expected) =>
        Assert.Equal(expected, AdMatcher.MatchesWorktime(selected, worktimeId));

    [Fact]
    public void Sql_form_matches_the_in_memory_form_for_every_combination()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE ad_summary (id TEXT PRIMARY KEY, country_id TEXT, region_id TEXT, municipality_id TEXT, worktime_id TEXT)";
            create.ExecuteNonQuery();
        }

        var ads = new List<AdSummary>();
        var n = 0;
        foreach (var country in new[] { Sweden, Norway, null })
        {
            foreach (var region in new[] { VastraGotaland, Halland, null })
            {
                foreach (var municipality in new[] { Goteborg, Molndal, Kungsbacka, "other_muni", null })
                {
                    foreach (var worktime in new[] { WorktimeConcepts.FullTimeId, WorktimeConcepts.PartTimeId, "ZZZZ_fut_ure", null })
                    {
                        ads.Add(Fixtures.Ad((n++).ToString(System.Globalization.CultureInfo.InvariantCulture), municipality, region, country, worktime));
                    }
                }
            }
        }

        foreach (var ad in ads)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO ad_summary VALUES ($id, $c, $r, $m, $w)";
            insert.Parameters.AddWithValue("$id", ad.Id);
            insert.Parameters.AddWithValue("$c", (object?)ad.CountryId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$r", (object?)ad.RegionId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$m", (object?)ad.MunicipalityId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$w", (object?)ad.WorktimeId ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        var geographies = new[]
        {
            new AdFilter(),
            AdFilter.Default,
            GoteborgMolndalAndHalland,
            new AdFilter { RegionIds = Set(VastraGotaland) },
            new AdFilter { MunicipalityIds = Set(Kungsbacka) },
            new AdFilter { AllSweden = true, MunicipalityIds = Set(Goteborg) },
        };

        foreach (var geography in geographies)
        {
            for (var w = 0; w <= (int)WorktimeSet.All; w++)
            {
                var filter = geography with { Worktime = (WorktimeSet)w };
                var expected = ads.Where(a => AdMatcher.Matches(filter, a)).Select(a => a.Id).Order(StringComparer.Ordinal).ToList();

                using var query = connection.CreateCommand();
                query.CommandText = $"SELECT id FROM ad_summary s WHERE {AdFilterSql.Where(filter, query)}";
                var actual = new List<string>();
                using (var reader = query.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        actual.Add(reader.GetString(0));
                    }
                }

                actual.Sort(StringComparer.Ordinal);
                Assert.Equal(expected, actual);

                // Negation is the exact complement, also for rows with NULL location or worktime columns.
                using var negated = connection.CreateCommand();
                negated.CommandText = $"SELECT id FROM ad_summary s WHERE NOT {AdFilterSql.Where(filter, negated)}";
                Assert.Equal(ads.Count - expected.Count, Count(negated));
            }
        }

        // Two filters on one command keep their own parameters: "matches new and not old".
        var oldFilter = new AdFilter { MunicipalityIds = Set(Goteborg) };
        var newFilter = new AdFilter { MunicipalityIds = Set(Goteborg, Molndal), RegionIds = Set(Halland) };
        using var both = connection.CreateCommand();
        both.CommandText = $"SELECT id FROM ad_summary s WHERE {AdFilterSql.Where(newFilter, both)} AND NOT {AdFilterSql.Where(oldFilter, both)}";
        Assert.Equal(ads.Count(a => AdMatcher.Matches(newFilter, a) && !AdMatcher.Matches(oldFilter, a)), Count(both));
    }

    private static int Count(SqliteCommand command)
    {
        var n = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            n++;
        }

        return n;
    }

    [Fact]
    public void Filters_with_the_same_selection_are_equal()
    {
        var a = new AdFilter { RegionIds = Set(Halland), MunicipalityIds = Set(Goteborg, Molndal) };
        var b = new AdFilter { RegionIds = Set(Halland), MunicipalityIds = Set(Molndal, Goteborg) };
        Assert.Equal(a, b);
        Assert.NotEqual(a, b with { Worktime = WorktimeSet.PartTime });
    }

    private static HashSet<string> Set(params string[] ids) => new(ids, StringComparer.Ordinal);
}
