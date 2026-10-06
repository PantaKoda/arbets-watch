using ArbetsWatch.Core.Ads;
using ArbetsWatch.Core.Places;

namespace ArbetsWatch.Core.Filtering;

/// <summary>Worktime categories the user can select. Unrecognized concept IDs are only matched by <see cref="All"/>.</summary>
[Flags]
public enum WorktimeSet
{
    None = 0,
    FullTime = 1,
    PartTime = 2,
    NotSpecified = 4,
    All = FullTime | PartTime | NotSpecified,
}

public static class WorktimeConcepts
{
    public const string FullTimeId = "6YE1_gAC_R2G";
    public const string PartTimeId = "947z_JGS_Uk2";
}

/// <summary>
/// The user's selection. Geography is a union of explicit choices (All Sweden, whole regions, single
/// municipalities); geography and worktime combine with AND. A region listed in <see cref="RegionIds"/> means
/// the whole region, including ads with only a region and no municipality. Picking some municipalities of a
/// region does not select the region.
/// </summary>
public sealed record AdFilter
{
    public static AdFilter Default { get; } = new() { AllSweden = true, Worktime = WorktimeSet.All };

    public bool AllSweden { get; init; }

    public IReadOnlySet<string> RegionIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> MunicipalityIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public WorktimeSet Worktime { get; init; } = WorktimeSet.All;

    public bool HasGeography => AllSweden || RegionIds.Count > 0 || MunicipalityIds.Count > 0;

    public bool Equals(AdFilter? other) =>
        other is not null &&
        AllSweden == other.AllSweden &&
        Worktime == other.Worktime &&
        RegionIds.SetEquals(other.RegionIds) &&
        MunicipalityIds.SetEquals(other.MunicipalityIds);

    public override int GetHashCode() =>
        HashCode.Combine(AllSweden, Worktime, RegionIds.Count, MunicipalityIds.Count);
}

/// <summary>
/// The single matching specification. <see cref="Matches"/> is the in-memory form; <see cref="AdFilterSql"/>
/// is derived from the same rules and tested for equivalence.
/// </summary>
public static class AdMatcher
{
    public static bool Matches(AdFilter filter, AdSummary ad) =>
        MatchesGeography(filter, ad.CountryId, ad.RegionId, ad.MunicipalityId) &&
        MatchesWorktime(filter.Worktime, ad.WorktimeId);

    /// <summary>
    /// All Sweden means every ad not placed abroad: Swedish country, or no country at all (JobStream is
    /// Arbetsförmedlingen's service; ads abroad always carry their foreign country).
    /// </summary>
    public static bool MatchesGeography(AdFilter filter, string? countryId, string? regionId, string? municipalityId)
    {
        if (filter.AllSweden && (countryId is null || countryId == PlaceCatalog.SwedenId))
        {
            return true;
        }

        return (regionId is not null && filter.RegionIds.Contains(regionId)) ||
               (municipalityId is not null && filter.MunicipalityIds.Contains(municipalityId));
    }

    public static bool MatchesWorktime(WorktimeSet selected, string? worktimeId)
    {
        if ((selected & WorktimeSet.All) == WorktimeSet.All)
        {
            return true;
        }

        return worktimeId switch
        {
            null => selected.HasFlag(WorktimeSet.NotSpecified),
            WorktimeConcepts.FullTimeId => selected.HasFlag(WorktimeSet.FullTime),
            WorktimeConcepts.PartTimeId => selected.HasFlag(WorktimeSet.PartTime),
            _ => false,
        };
    }
}
