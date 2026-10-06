using System.Globalization;
using ArbetsWatch.Core.Ads;
using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Time;

namespace ArbetsWatch.Core.Presentation;

/// <summary>Display strings for ads and refresh state. Times are shown in Swedish local time.</summary>
public static class DisplayText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>"Today 14:05", "Yesterday 09:12", "3 Oct", or "3 Oct 2025" for another year.</summary>
    public static string Published(DateTimeOffset? published, DateTimeOffset now)
    {
        if (published is not { } instant)
        {
            return string.Empty;
        }

        var local = SwedishTime.ToLocal(instant);
        var today = SwedishTime.ToLocal(now).Date;
        if (local.Date == today)
        {
            return "Today " + local.ToString("HH:mm", Invariant);
        }

        if (local.Date == today.AddDays(-1))
        {
            return "Yesterday " + local.ToString("HH:mm", Invariant);
        }

        return local.Year == today.Year ? local.ToString("d MMM", Invariant) : local.ToString("d MMM yyyy", Invariant);
    }

    /// <summary>"just now", "4 min ago", "2 h ago", "yesterday", "3 days ago".</summary>
    public static string Age(DateTimeOffset since, DateTimeOffset now)
    {
        var age = now - since;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return string.Create(Invariant, $"{(int)age.TotalMinutes} min ago");
        }

        if (age < TimeSpan.FromDays(1))
        {
            return string.Create(Invariant, $"{(int)age.TotalHours} h ago");
        }

        return age < TimeSpan.FromDays(2) ? "yesterday" : string.Create(Invariant, $"{(int)age.TotalDays} days ago");
    }

    /// <summary>Municipality, else region, else a neutral label.</summary>
    public static string Place(AdSummary ad) =>
        ad.MunicipalityLabel ?? ad.RegionLabel ?? (ad.CountryId is null or Places.PlaceCatalog.SwedenId ? "Place not specified" : "Abroad");

    /// <summary>
    /// The source's own label (e.g. "Heltid"); unknown categories keep their label. A missing value reads
    /// "Ej angiven", matching the filter chip.
    /// </summary>
    public static string Worktime(AdSummary ad) =>
        ad.WorktimeId is null ? "Ej angiven" : ad.WorktimeLabel ?? ad.WorktimeId;

    public static WorktimeSet WorktimeCategory(AdSummary ad) => ad.WorktimeId switch
    {
        null => WorktimeSet.NotSpecified,
        WorktimeConcepts.FullTimeId => WorktimeSet.FullTime,
        WorktimeConcepts.PartTimeId => WorktimeSet.PartTime,
        _ => WorktimeSet.None,
    };

    /// <summary>"1 ad" / "12 345 ads" (grouped per <paramref name="culture"/>): advertisements, not vacancies.</summary>
    public static string AdCount(int count, IFormatProvider? culture = null) =>
        count == 1 ? "1 ad" : count.ToString("N0", culture ?? CultureInfo.CurrentCulture) + " ads";
}
