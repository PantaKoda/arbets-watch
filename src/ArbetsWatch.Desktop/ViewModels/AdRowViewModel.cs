using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Presentation;
using ArbetsWatch.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArbetsWatch.Desktop.ViewModels;

/// <summary>One ad in the list. Updated in place when the ad changes, so selection and scrolling stay put.</summary>
public sealed partial class AdRowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Employer { get; set; } = string.Empty;

    /// <summary>The title as published (Swedish). <see cref="Title"/> is what is shown: this, or its English translation.</summary>
    public string OriginalTitle { get; private set; } = string.Empty;

    /// <summary>Hover text: the Swedish original when the title is translated, otherwise the open hint.</summary>
    [ObservableProperty]
    public partial string TitleTip { get; set; } = OpenTip;

    [ObservableProperty]
    public partial string Place { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Worktime { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsFullTime { get; set; }

    [ObservableProperty]
    public partial bool IsPartTime { get; set; }

    [ObservableProperty]
    public partial string PublishedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Unread { get; set; }

    /// <summary>Newly detected: glows once.</summary>
    [ObservableProperty]
    public partial bool IsFlashing { get; set; }

    /// <summary>No longer in the current results (held back while you are scrolled down).</summary>
    [ObservableProperty]
    public partial bool IsGone { get; set; }

    /// <summary>On the user's saved list (shown as a filled star).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveTip), nameof(SaveName), nameof(AccessibleName))]
    public partial bool IsSaved { get; set; }

    /// <summary>False for a saved ad that Platsbanken no longer publishes.</summary>
    [ObservableProperty]
    public partial bool IsPublished { get; set; } = true;

    public AdRowViewModel(AdRow row, DateTimeOffset now)
    {
        Id = row.Ad.Id;
        Update(row, now);
    }

    private const string OpenTip = "Open on Platsbanken (Enter)";

    private string? _english;
    private bool _preferEnglish;

    public string Id { get; }

    /// <summary>The translation is memory-only and tied to the exact original: a changed title is shown untranslated until translated again.</summary>
    public void SetTranslation(string original, string? english)
    {
        if (original != OriginalTitle || english == _english)
        {
            return;
        }

        _english = english;
        RefreshTitle();
    }

    public void ShowEnglish(bool value)
    {
        if (_preferEnglish != value)
        {
            _preferEnglish = value;
            RefreshTitle();
        }
    }

    private void RefreshTitle()
    {
        var translated = _preferEnglish && !string.IsNullOrWhiteSpace(_english) && _english != OriginalTitle;
        Title = translated ? _english! : OriginalTitle;
        TitleTip = translated ? $"Original: {OriginalTitle}\n{OpenTip}" : OpenTip;
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(SaveName));
    }

    public Uri? Url { get; private set; }

    public string AccessibleName =>
        $"{Title}, {Employer}, {Place}, {Worktime}, published {PublishedText}{(Unread ? ", new" : string.Empty)}{(IsSaved ? ", saved" : string.Empty)}";

    public string SaveTip => IsSaved ? "Remove from saved ads (Ctrl+S)" : "Save this ad (Ctrl+S)";

    public string SaveName => IsSaved ? $"Remove {Title} from saved ads" : $"Save {Title}";

    public void Update(AdRow row, DateTimeOffset now)
    {
        var ad = row.Ad;
        var original = string.IsNullOrWhiteSpace(ad.Headline) ? "Untitled ad" : ad.Headline;
        if (original != OriginalTitle)
        {
            _english = null;
        }

        OriginalTitle = original;
        RefreshTitle();
        Employer = ad.Employer ?? "Employer not specified";
        Place = DisplayText.Place(ad);
        Worktime = DisplayText.Worktime(ad);
        var category = DisplayText.WorktimeCategory(ad);
        IsFullTime = category == WorktimeSet.FullTime;
        IsPartTime = category == WorktimeSet.PartTime;
        PublishedText = DisplayText.Published(ad.PublishedUtc, now);
        Unread = row.Unread;
        IsSaved = row.IsSaved;
        IsPublished = row.IsPublished;
        IsGone = false;
        Url = AdLinkPolicy.Resolve(ad.Id, ad.Url);
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(SaveName));
    }
}
