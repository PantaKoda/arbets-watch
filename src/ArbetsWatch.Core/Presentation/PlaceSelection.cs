using System.Globalization;
using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Places;

namespace ArbetsWatch.Core.Presentation;

/// <summary>
/// Editing rules for the geographic part of <see cref="AdFilter"/>. Choices are explicit: All Sweden, whole
/// regions, or single municipalities. Selecting a whole region absorbs its individually selected
/// municipalities; selecting municipalities never selects their region.
/// </summary>
public static class PlaceSelection
{
    public static AdFilter SetAllSweden(AdFilter filter, bool on) =>
        on
            ? filter with { AllSweden = true, RegionIds = Empty(), MunicipalityIds = Empty() }
            : filter with { AllSweden = false };

    public static AdFilter SetRegion(AdFilter filter, PlaceCatalog catalog, string regionId, bool on)
    {
        var regions = new HashSet<string>(filter.RegionIds, StringComparer.Ordinal);
        var municipalities = new HashSet<string>(filter.MunicipalityIds, StringComparer.Ordinal);
        if (on)
        {
            regions.Add(regionId);
            if (catalog.FindRegion(regionId) is { } region)
            {
                municipalities.ExceptWith(region.Municipalities.Select(m => m.Id));
            }
        }
        else
        {
            regions.Remove(regionId);
        }

        return filter with { AllSweden = false, RegionIds = regions, MunicipalityIds = municipalities };
    }

    public static AdFilter SetMunicipality(AdFilter filter, string municipalityId, bool on)
    {
        var municipalities = new HashSet<string>(filter.MunicipalityIds, StringComparer.Ordinal);
        if (on)
        {
            municipalities.Add(municipalityId);
        }
        else
        {
            municipalities.Remove(municipalityId);
        }

        return filter with { AllSweden = false, MunicipalityIds = municipalities };
    }

    /// <summary>True when the municipality is covered by a selected whole region or by All Sweden.</summary>
    public static bool IsCovered(AdFilter filter, Municipality municipality) =>
        filter.AllSweden || filter.RegionIds.Contains(municipality.RegionId);

    /// <summary>A short description such as "Göteborg, Mölndal, Hallands län" or "All of Sweden".</summary>
    public static string Summary(AdFilter filter, PlaceCatalog catalog, int maxNames = 3)
    {
        if (filter.AllSweden)
        {
            return "All of Sweden";
        }

        var sv = StringComparer.Create(CultureInfo.GetCultureInfo("sv-SE"), false);
        var names = filter.MunicipalityIds.Select(id => catalog.FindMunicipality(id)?.Label ?? id).Order(sv)
            .Concat(filter.RegionIds.Select(id => catalog.FindRegion(id)?.Label ?? id).Order(sv))
            .ToList();
        if (names.Count == 0)
        {
            return "Choose places";
        }

        return names.Count <= maxNames
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(maxNames)) + string.Create(CultureInfo.InvariantCulture, $" +{names.Count - maxNames}");
    }

    private static HashSet<string> Empty() => new(StringComparer.Ordinal);
}
