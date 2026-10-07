namespace ArbetsWatch.Core.Details;

/// <summary>A requirement or merit from the ad, e.g. ("Kompetens", "Storstädning", required: true).</summary>
public sealed record AdRequirement(string Category, string Label, bool Required);

/// <summary>A named contact person published in the ad.</summary>
public sealed record AdContact(string? Name, string? Role, string? Email, string? Phone);

/// <summary>How to apply, as published. Links and addresses are validated; free text is shown as text only.</summary>
public sealed record AdApplication(
    Uri? Link,
    string? Email,
    string? Reference,
    string? Information,
    string? Other,
    bool ViaPlatsbanken)
{
    public bool HasAny => Link is not null || Email is not null || Information is not null || Other is not null || ViaPlatsbanken;
}

/// <summary>
/// The full details of one ad, fetched on demand (never stored). All text comes from the ad and is untrusted:
/// it is shown as plain text, never rendered as markup.
/// </summary>
public sealed record AdDetails
{
    public required string Id { get; init; }

    public required string Headline { get; init; }

    public string? Employer { get; init; }

    public string? Workplace { get; init; }

    public Uri? EmployerWebsite { get; init; }

    public string? Description { get; init; }

    public string? Occupation { get; init; }

    public string? EmploymentType { get; init; }

    public string? Duration { get; init; }

    public string? WorkingHours { get; init; }

    /// <summary>"100 %" or "50–75 %" of full time.</summary>
    public string? Scope { get; init; }

    public int? Vacancies { get; init; }

    public string? SalaryType { get; init; }

    public string? SalaryDescription { get; init; }

    public string? StreetAddress { get; init; }

    public string? Postcode { get; init; }

    public string? City { get; init; }

    public string? Municipality { get; init; }

    public string? Region { get; init; }

    public string? Country { get; init; }

    public DateTimeOffset? Published { get; init; }

    public DateTimeOffset? LastPublication { get; init; }

    public DateTimeOffset? ApplicationDeadline { get; init; }

    public bool? ExperienceRequired { get; init; }

    public bool? DrivingLicenseRequired { get; init; }

    public IReadOnlyList<string> DrivingLicenses { get; init; } = [];

    public bool? OwnCarRequired { get; init; }

    public IReadOnlyList<AdRequirement> Requirements { get; init; } = [];

    public IReadOnlyList<AdContact> Contacts { get; init; } = [];

    public AdApplication Application { get; init; } = new(null, null, null, null, null, false);

    public Uri? PlatsbankenPage { get; init; }
}

public enum AdDetailsStatus
{
    Found,

    /// <summary>The ad is no longer published (or the ID is unknown).</summary>
    NotFound,

    Failed,
}

public sealed record AdDetailsResult(AdDetailsStatus Status, AdDetails? Details = null, string? Error = null);
