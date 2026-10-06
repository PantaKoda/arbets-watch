using ArbetsWatch.Core.Places;

namespace ArbetsWatch.Core.Tests;

public sealed class PlaceCatalogTests
{
    private static readonly PlaceCatalog Catalog = PlaceCatalog.LoadBundled();

    [Fact]
    public void Bundled_hierarchy_has_the_reviewed_counts()
    {
        Assert.Equal(21, Catalog.Regions.Count);
        Assert.Equal(290, Catalog.Municipalities.Count());
        Assert.True(Catalog.TaxonomyVersion > 0);
    }

    [Fact]
    public void Every_municipality_points_to_its_region()
    {
        foreach (var region in Catalog.Regions)
        {
            Assert.NotEmpty(region.Municipalities);
            Assert.All(region.Municipalities, m => Assert.Equal(region.Id, m.RegionId));
        }
    }

    [Fact]
    public void Known_places_resolve_with_codes_as_strings()
    {
        var goteborg = Catalog.FindMunicipality("PVZL_BQT_XtL");
        Assert.NotNull(goteborg);
        Assert.Equal("Göteborg", goteborg.Label);
        Assert.Equal("1480", goteborg.Code);
        Assert.Equal("zdoY_6u5_Krt", goteborg.RegionId);
        Assert.Equal("Västra Götalands län", Catalog.FindRegion("zdoY_6u5_Krt")?.Label);

        // Leading zeroes survive.
        Assert.Equal("01", Catalog.FindRegion("CifL_Rzy_Mku")?.Code);
        Assert.Contains(Catalog.Municipalities, m => m.Code == "0114");
    }

    [Fact]
    public void Ids_are_unique()
    {
        var ids = Catalog.Regions.Select(r => r.Id).Concat(Catalog.Municipalities.Select(m => m.Id)).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }
}
