using ArbetsWatch.Core.Details;
using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Presentation;
using ArbetsWatch.Core.Storage;

namespace ArbetsWatch.Web;

internal sealed record ApiError(string Error);

/// <summary>What the list needs and nothing more; the stored description text is never sent with it.</summary>
internal sealed record AdItem(
    string Id,
    string Title,
    string? TitleEn,
    string? Employer,
    string Place,
    string Worktime,
    string WorktimeKind,
    DateTimeOffset? PublishedUtc,
    string? Url)
{
    public static AdItem From(AdRow row, string? titleEn) => new(
        row.Ad.Id,
        row.Ad.Headline,
        titleEn,
        row.Ad.Employer,
        DisplayText.Place(row.Ad),
        DisplayText.Worktime(row.Ad),
        DisplayText.WorktimeCategory(row.Ad) switch
        {
            WorktimeSet.FullTime => "full",
            WorktimeSet.PartTime => "part",
            WorktimeSet.NotSpecified => "none",
            _ => "other",
        },
        row.Ad.PublishedUtc,
        AdLinkPolicy.Resolve(row.Ad.Id, row.Ad.Url)?.GetLeftPart(UriPartial.Path));
}

internal sealed record AdPage(IReadOnlyList<AdItem> Items, int Total, string? Next, DateTimeOffset AsOfUtc);

internal sealed record NewAdItem(string Id, long FirstSeenMs);

internal sealed record NewAdsResponse(IReadOnlyList<NewAdItem> Items, bool Truncated, DateTimeOffset AsOfUtc);

internal sealed record PublishedResponse(IReadOnlyList<string> Ids);

internal sealed record StatusResponse(
    string Phase,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? NextRunUtc,
    int? SnapshotAdsReceived,
    bool HasBaseline,
    bool DescriptionsPending,
    bool NeedsRestart,
    int TotalAds,
    string Translation,
    string CacheId,
    DateTimeOffset NowUtc);

internal sealed record PlaceMunicipality(string Id, string Label);

internal sealed record PlaceRegion(string Id, string Label, IReadOnlyList<PlaceMunicipality> Municipalities);

internal sealed record DetailsResponse(
    string Id,
    string Headline,
    string? Employer,
    string? Workplace,
    string? EmployerWebsite,
    string? Description,
    string? Occupation,
    string? EmploymentType,
    string? Duration,
    string? WorkingHours,
    string? Scope,
    int? Vacancies,
    string? SalaryType,
    string? SalaryDescription,
    string? StreetAddress,
    string? Postcode,
    string? City,
    string? Municipality,
    string? Region,
    string? Country,
    DateTimeOffset? Published,
    DateTimeOffset? ApplicationDeadline,
    bool? ExperienceRequired,
    bool? DrivingLicenseRequired,
    IReadOnlyList<string> DrivingLicenses,
    bool? OwnCarRequired,
    IReadOnlyList<AdRequirement> Requirements,
    IReadOnlyList<AdContact> Contacts,
    DetailsApplication Application,
    string? PlatsbankenPage)
{
    public static DetailsResponse From(AdDetails d) => new(
        d.Id, d.Headline, d.Employer, d.Workplace, d.EmployerWebsite?.AbsoluteUri, d.Description, d.Occupation,
        d.EmploymentType, d.Duration, d.WorkingHours, d.Scope, d.Vacancies, d.SalaryType, d.SalaryDescription,
        d.StreetAddress, d.Postcode, d.City, d.Municipality, d.Region, d.Country, d.Published, d.ApplicationDeadline,
        d.ExperienceRequired, d.DrivingLicenseRequired, d.DrivingLicenses, d.OwnCarRequired, d.Requirements, d.Contacts,
        new DetailsApplication(d.Application.Link?.AbsoluteUri, d.Application.Email, d.Application.Reference,
            d.Application.Information, d.Application.Other, d.Application.ViaPlatsbanken),
        d.PlatsbankenPage?.AbsoluteUri);
}

internal sealed record DetailsApplication(string? Link, string? Email, string? Reference, string? Information, string? Other, bool ViaPlatsbanken);
