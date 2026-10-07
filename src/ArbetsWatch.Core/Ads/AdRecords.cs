namespace ArbetsWatch.Core.Ads;

/// <summary>One record from a snapshot or stream response.</summary>
public abstract record SourceRecord(string Id)
{
    /// <summary>
    /// When the source says this state was produced: <c>timestamp</c> for active ads, <c>removed_date</c> for
    /// removals. Used to reject older states (docs/api-contracts.md, "Ordering of states").
    /// </summary>
    public abstract DateTimeOffset SourceChangedUtc { get; }
}

/// <summary>
/// The compact summary kept for every currently published ad. Optional fields are null when the source
/// omits them; labels and concept IDs are kept as given, including unrecognized ones.
/// </summary>
public sealed record AdSummary(
    string Id,
    string Headline,
    string? Employer,
    string? Url,
    string? CountryId,
    string? RegionId,
    string? RegionLabel,
    string? MunicipalityId,
    string? MunicipalityLabel,
    string? WorktimeId,
    string? WorktimeLabel,
    DateTimeOffset? PublishedUtc,
    string? PublishedRaw,
    DateTimeOffset? LastPublicationUtc,
    string? LastPublicationRaw,
    DateTimeOffset ChangedUtc) : SourceRecord(Id)
{
    public override DateTimeOffset SourceChangedUtc => ChangedUtc;

    /// <summary>The plain-text description (<c>description.text</c>). Only its searchable form is stored.</summary>
    public string? Description { get; init; }

    /// <summary>An ad stops being published once its last publication instant has passed.</summary>
    public bool IsExpiredAt(DateTimeOffset now) => LastPublicationUtc is { } last && last < now;
}

/// <summary>
/// A removal. Its shape differs from an ad: only the ID, removal time and location concept IDs are present.
/// When <c>removed_date</c> is missing or unreadable, <see cref="RemovedUtc"/> is the end of the requested
/// interval; <see cref="RemovedRaw"/> always holds the source text (null only when it was missing).
/// </summary>
public sealed record AdRemoval(
    string Id,
    DateTimeOffset RemovedUtc,
    string? RemovedRaw,
    string? CountryId,
    string? RegionId,
    string? MunicipalityId) : SourceRecord(Id)
{
    public override DateTimeOffset SourceChangedUtc => RemovedUtc;
}
