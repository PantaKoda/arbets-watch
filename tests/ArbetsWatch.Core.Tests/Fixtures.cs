using ArbetsWatch.Core.Ads;

namespace ArbetsWatch.Core.Tests;

internal static class Fixtures
{
    public static readonly DateTimeOffset Fallback = new(2026, 10, 6, 17, 10, 0, TimeSpan.Zero);

    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string Read(string name) => File.ReadAllText(PathOf(name));

    public static IReadOnlyList<SourceRecord> StreamMixed() =>
        [.. File.ReadAllLines(PathOf("stream-mixed.jsonl"))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => AdRecordParser.Parse(l, Fallback))];

    public static AdSummary Ad(
        string id,
        string? municipalityId = "PVZL_BQT_XtL",
        string? regionId = "zdoY_6u5_Krt",
        string? countryId = "i46j_HmG_v64",
        string? worktimeId = "6YE1_gAC_R2G",
        DateTimeOffset? changed = null,
        DateTimeOffset? published = null,
        DateTimeOffset? lastPublication = null,
        string headline = "Testjobb") =>
        new(
            id,
            headline,
            Employer: "Testföretaget AB",
            Url: $"https://arbetsformedlingen.se/platsbanken/annonser/{id}",
            CountryId: countryId,
            RegionId: regionId,
            RegionLabel: regionId is null ? null : "Region",
            MunicipalityId: municipalityId,
            MunicipalityLabel: municipalityId is null ? null : "Kommun",
            WorktimeId: worktimeId,
            WorktimeLabel: worktimeId is null ? null : "Heltid",
            PublishedUtc: published ?? new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero),
            PublishedRaw: null,
            LastPublicationUtc: lastPublication ?? new DateTimeOffset(2026, 11, 30, 22, 59, 59, TimeSpan.Zero),
            LastPublicationRaw: null,
            ChangedUtc: changed ?? new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero));

    public static AdRemoval Removal(string id, DateTimeOffset removed) =>
        new(id, removed, null, "i46j_HmG_v64", "zdoY_6u5_Krt", "PVZL_BQT_XtL");
}
