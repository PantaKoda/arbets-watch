using System.Globalization;
using ArbetsWatch.Core.Details;
using ArbetsWatch.Core.Presentation;
using ArbetsWatch.Desktop.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ArbetsWatch.Desktop.ViewModels;

/// <summary>One label/value row of a table in the details window.</summary>
public sealed record FactRow(string Label, string Value);

public sealed record RequirementRow(string Category, string Label, string Kind, bool Required);

public sealed record ContactRow(string Name, string Role, string? Email, string? Phone)
{
    public bool HasEmail => Email is not null;

    public bool HasPhone => Phone is not null;
}

/// <summary>
/// The details window for one ad: fetched on demand from JobSearch, organised into tables, with every way to
/// apply that the ad publishes. All ad text is untrusted and shown as plain text.
/// </summary>
public sealed partial class AdDetailsViewModel(
    IAdDetailsSource source,
    BrowserLauncher browser,
    Func<AdRowViewModel, Task> markRead,
    TimeProvider time,
    ILogger<AdDetailsViewModel> logger) : ObservableObject
{
    private CancellationTokenSource? _loading;
    private AdRowViewModel? _row;
    private AdDetails? _details;

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial bool CanRetry { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<FactRow> Facts { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<RequirementRow> Requirements { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ContactRow> Contacts { get; private set; } = [];

    [ObservableProperty]
    public partial string? Description { get; private set; }

    // ---- How to apply ---------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string? ApplyLinkText { get; private set; }

    [ObservableProperty]
    public partial string? ApplyLinkTarget { get; private set; }

    [ObservableProperty]
    public partial string? ApplyEmail { get; private set; }

    [ObservableProperty]
    public partial string? ApplyNote { get; private set; }

    [ObservableProperty]
    public partial string? ApplyReference { get; private set; }

    [ObservableProperty]
    public partial bool ApplyViaPlatsbanken { get; private set; }

    [ObservableProperty]
    public partial bool HasNoApplyInfo { get; private set; }

    [ObservableProperty]
    public partial string? LinkNotice { get; private set; }

    public bool HasError => Error is not null;

    public bool HasRequirements => Requirements.Count > 0;

    public bool HasContacts => Contacts.Count > 0;

    public bool HasDescription => Description is not null;

    public bool HasApplyLink => ApplyLinkTarget is not null;

    public bool HasApplyEmail => ApplyEmail is not null;

    public bool HasApplyNote => ApplyNote is not null;

    public bool HasApplyReference => ApplyReference is not null;

    public bool HasEmployerWebsite => _details?.EmployerWebsite is not null;

    public string EmployerWebsiteText => _details?.EmployerWebsite?.Host ?? string.Empty;

    public string WindowTitle => string.IsNullOrEmpty(Title) ? "Ad details" : $"{Title} – ArbetsWatch";

    /// <summary>Shows <paramref name="row"/> right away from the list, then loads its details.</summary>
    public async Task LoadAsync(AdRowViewModel row)
    {
        _row = row;
        Title = row.OriginalTitle;
        Subtitle = $"{row.Employer} · {row.Place}";
        OnPropertyChanged(nameof(WindowTitle));
        Clear();

        if (_loading is not null)
        {
            await _loading.CancelAsync().ConfigureAwait(true);
        }

        using var loading = new CancellationTokenSource();
        _loading = loading;
        IsLoading = true;
        AdDetailsResult result;
        try
        {
            result = await source.GetAsync(row.Id, loading.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // another ad was opened meanwhile
        }
        finally
        {
            if (ReferenceEquals(_loading, loading))
            {
                IsLoading = false;
                _loading = null;
            }
        }

        if (!ReferenceEquals(_row, row))
        {
            return;
        }

        switch (result.Status)
        {
            case AdDetailsStatus.Found:
                Show(result.Details!);
                await markRead(row).ConfigureAwait(true);
                break;
            case AdDetailsStatus.NotFound:
                SetError("This ad is no longer published, so its details aren't available.", retry: false);
                break;
            default:
                SetError($"{result.Error ?? "The details could not be loaded."} Try again, or open the ad on Platsbanken.", retry: true);
                break;
        }
    }

    [RelayCommand]
    private Task RetryAsync() => _row is { } row ? LoadAsync(row) : Task.CompletedTask;

    [RelayCommand]
    private void OpenApplyLink()
    {
        if (_details?.Application.Link is { } link)
        {
            Report(browser.OpenExternal(link));
        }
    }

    [RelayCommand]
    private void EmailApply()
    {
        if (_details?.Application.Email is { } email)
        {
            Report(browser.OpenExternal(ExternalLinks.MailTo(email, _details.Application.Reference ?? _details.Headline)));
        }
    }

    [RelayCommand]
    private void OpenEmployerWebsite()
    {
        if (_details?.EmployerWebsite is { } site)
        {
            Report(browser.OpenExternal(site));
        }
    }

    [RelayCommand]
    private void OpenPlatsbanken()
    {
        if ((_details?.PlatsbankenPage ?? _row?.Url) is { } page)
        {
            Report(browser.Open(page));
        }
    }

    [RelayCommand]
    private void EmailContact(ContactRow? contact)
    {
        if (contact?.Email is { } email)
        {
            Report(browser.OpenExternal(ExternalLinks.MailTo(email, _details?.Application.Reference ?? _details?.Headline)));
        }
    }

    private void Report(bool opened) => LinkNotice = opened ? null : "Could not open the link.";

    private void Clear()
    {
        _details = null;
        Error = null;
        CanRetry = false;
        IsLoaded = false;
        LinkNotice = null;
        Facts = [];
        Requirements = [];
        Contacts = [];
        Description = null;
        ApplyLinkText = null;
        ApplyLinkTarget = null;
        ApplyEmail = null;
        ApplyNote = null;
        ApplyReference = null;
        ApplyViaPlatsbanken = false;
        HasNoApplyInfo = false;
        RaiseDerived();
    }

    private void SetError(string message, bool retry)
    {
        Error = message;
        CanRetry = retry;
        RaiseDerived();
    }

    private void Show(AdDetails d)
    {
        _details = d;
        var now = time.GetUtcNow();
        Title = d.Headline;
        Subtitle = string.Join(" · ", new[] { d.Employer, d.Municipality ?? d.City ?? d.Region }.Where(s => s is not null));
        OnPropertyChanged(nameof(WindowTitle));

        var facts = new List<FactRow>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                facts.Add(new FactRow(label, value));
            }
        }

        Add("Employer", d.Employer);
        if (d.Workplace is not null && !string.Equals(d.Workplace, d.Employer, StringComparison.OrdinalIgnoreCase))
        {
            Add("Workplace", d.Workplace);
        }

        Add("Occupation", d.Occupation);
        Add("Worktime", JoinParts(d.WorkingHours, d.Scope));
        Add("Employment", JoinParts(d.EmploymentType, d.Duration));
        Add("Positions", d.Vacancies is > 1 ? d.Vacancies.Value.ToString(CultureInfo.CurrentCulture) : null);
        Add("Salary", JoinParts(d.SalaryType, d.SalaryDescription));
        Add("Address", JoinParts(d.StreetAddress, JoinWords(d.Postcode, d.City)));
        Add("Municipality", JoinParts(d.Municipality, d.Region));
        if (d.Country is not null && d.Country != "Sverige")
        {
            Add("Country", d.Country);
        }

        Add("Published", Date(d.Published));
        Add("Apply by", Deadline(d.ApplicationDeadline, now));
        Add("Experience", d.ExperienceRequired switch { true => "Required", false => "Not required", _ => null });
        Add("Driving licence", d.DrivingLicenseRequired switch
        {
            true => d.DrivingLicenses.Count > 0 ? "Required: " + string.Join(", ", d.DrivingLicenses) : "Required",
            false => "Not required",
            _ => null,
        });
        Add("Own car", d.OwnCarRequired == true ? "Required" : null);
        Add("Ad ID", d.Id);
        Facts = facts;

        Requirements = [.. d.Requirements
            .OrderBy(r => r.Required ? 0 : 1)
            .Select(r => new RequirementRow(r.Category, r.Label, r.Required ? "Required" : "Merit", r.Required))];
        Contacts = [.. d.Contacts.Select(c => new ContactRow(c.Name ?? "Contact", c.Role ?? string.Empty, c.Email, c.Phone))];
        Description = d.Description;

        var apply = d.Application;
        ApplyLinkTarget = apply.Link?.AbsoluteUri;
        ApplyLinkText = apply.Link is { } link ? $"Apply on {link.Host}" : null;
        ApplyEmail = apply.Email;
        ApplyNote = JoinLines(apply.Information, apply.Other);
        ApplyReference = apply.Reference;
        ApplyViaPlatsbanken = apply.ViaPlatsbanken;
        HasNoApplyInfo = !apply.HasAny;
        IsLoaded = true;
        RaiseDerived();
        logger.LogInformation("Showed details of ad {Id}", d.Id);
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasRequirements));
        OnPropertyChanged(nameof(HasContacts));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(HasApplyLink));
        OnPropertyChanged(nameof(HasApplyEmail));
        OnPropertyChanged(nameof(HasApplyNote));
        OnPropertyChanged(nameof(HasApplyReference));
        OnPropertyChanged(nameof(HasEmployerWebsite));
        OnPropertyChanged(nameof(EmployerWebsiteText));
    }

    private static string? JoinParts(params string?[] parts) =>
        parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() is { Count: > 0 } list ? string.Join(", ", list) : null;

    private static string? JoinWords(params string?[] parts) =>
        parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() is { Count: > 0 } list ? string.Join(" ", list) : null;

    private static string? JoinLines(params string?[] parts) =>
        parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() is { Count: > 0 } list ? string.Join("\n", list) : null;

    private static string? Date(DateTimeOffset? instant) =>
        instant is { } i ? Core.Time.SwedishTime.ToLocal(i).ToString("d MMM yyyy", CultureInfo.InvariantCulture) : null;

    private static string? Deadline(DateTimeOffset? deadline, DateTimeOffset now)
    {
        if (deadline is not { } d)
        {
            return null;
        }

        var days = (int)Math.Ceiling((d - now).TotalDays);
        var relative = days switch
        {
            < 0 => "passed",
            0 => "today",
            1 => "tomorrow",
            _ => string.Create(CultureInfo.InvariantCulture, $"in {days} days"),
        };
        return $"{Date(d)} ({relative})";
    }
}
