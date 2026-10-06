using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Places;
using ArbetsWatch.Core.Platform;
using ArbetsWatch.Core.Presentation;
using ArbetsWatch.Core.Settings;
using ArbetsWatch.Core.Storage;
using ArbetsWatch.Core.Sync;
using ArbetsWatch.Core.Time;
using ArbetsWatch.Desktop.Controls;
using ArbetsWatch.Desktop.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ArbetsWatch.Desktop.ViewModels;

/// <summary>What the main window needs from the application shell.</summary>
public interface IShell
{
    void HideWindow();

    void ApplyAppearance(AppPreferences preferences);

    void SavePreferences(AppPreferences preferences);

    void Quit();
}

/// <summary>
/// The main window: status header, place and worktime filters, the ad list, and settings. All data comes from
/// the local cache; the coordinator updates it in the background and tells this view model to reload.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AdStore _store;
    private readonly RefreshCoordinator _coordinator;
    private readonly PlaceCatalog _catalog;
    private readonly BrowserLauncher _browser;
    private readonly TimeProvider _time;
    private readonly IShell _shell;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherTimer _clock;
    private AppPreferences _preferences;
    private SyncStatus _status;
    private int _reloadVersion;
    private bool _syncingWorktime;
    private bool _syncingSettings;
    private IReadOnlyList<AdRow>? _held;

    public MainViewModel(
        AdStore store,
        RefreshCoordinator coordinator,
        PlaceCatalog catalog,
        BrowserLauncher browser,
        TimeProvider time,
        AppPaths paths,
        AppPreferences preferences,
        IShell shell,
        ILogger<MainViewModel> logger)
    {
        _store = store;
        _coordinator = coordinator;
        _catalog = catalog;
        _browser = browser;
        _time = time;
        _shell = shell;
        _logger = logger;
        _preferences = preferences;
        _status = coordinator.Status;
        DataDirectory = paths.DataDirectory;
        VersionText = $"ArbetsWatch {typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "dev"}";
        TaxonomyText = $"Places: taxonomy version {catalog.TaxonomyVersion}, {catalog.Regions.Count} län, {catalog.Municipalities.Count()} kommuner";

        Places = new PlacePickerViewModel(catalog, () => _preferences.Filter, f => ApplyFilter(f));
        SyncWorktime(preferences.Filter.Worktime);
        SyncSettings(preferences);
        PlacesSummary = PlaceSelection.Summary(preferences.Filter, catalog);

        coordinator.StatusChanged += (_, status) => Dispatcher.UIThread.Post(() => OnStatus(status));
        coordinator.DataChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = ReloadAsync(ReloadReason.DataChanged));

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _clock.Tick += (_, _) => UpdateStatusText();
        _clock.Start();
        UpdateStatusText();
    }

    private enum ReloadReason
    {
        Initial,
        DataChanged,
        FilterChanged,
        ShowHeld,
    }

    /// <summary>Raised when the list should scroll to the top (after showing held-back updates).</summary>
    public event EventHandler? ScrollToTopRequested;

    public PlacePickerViewModel Places { get; }

    public ObservableCollection<AdRowViewModel> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial AdRowViewModel? SelectedRow { get; set; }

    // ---- Header and status ------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial StatusTone StatusTone { get; set; } = StatusTone.Neutral;

    [ObservableProperty]
    public partial string StatusTooltip { get; set; } = string.Empty;

    /// <summary>The scan line: only while you wait (a refresh you asked for, or the first download).</summary>
    [ObservableProperty]
    public partial bool ShowActivity { get; set; }

    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasNotice { get; set; }

    // ---- Filters ----------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string PlacesSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPlacesOpen { get; set; }

    [ObservableProperty]
    public partial bool FullTime { get; set; }

    [ObservableProperty]
    public partial bool PartTime { get; set; }

    [ObservableProperty]
    public partial bool NotSpecified { get; set; }

    // ---- List and body states ---------------------------------------------------------------------------

    [ObservableProperty]
    public partial string CountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int UnreadCount { get; set; }

    [ObservableProperty]
    public partial string HeldText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasHeldUpdates { get; set; }

    /// <summary>Set by the view: the list is scrolled to (near) the top, so updates can be applied at once.</summary>
    public bool IsListAtTop { get; set; } = true;

    [ObservableProperty]
    public partial bool HasLoaded { get; set; }

    [ObservableProperty]
    public partial bool ShowList { get; set; }

    [ObservableProperty]
    public partial bool ShowFirstDownload { get; set; }

    [ObservableProperty]
    public partial string FirstDownloadText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowNoData { get; set; }

    [ObservableProperty]
    public partial bool ShowChoosePlaces { get; set; }

    [ObservableProperty]
    public partial bool ShowNoMatches { get; set; }

    // ---- Settings ---------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    [ObservableProperty]
    public partial decimal PollMinutes { get; set; }

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    public partial bool Transparent { get; set; }

    [ObservableProperty]
    public partial bool AlwaysOnTop { get; set; }

    [ObservableProperty]
    public partial bool Paused { get; set; }

    public string[] ThemeOptions { get; } = ["System", "Light", "Dark"];

    public string DataDirectory { get; }

    public string VersionText { get; }

    public string TaxonomyText { get; }

    [ObservableProperty]
    public partial string SyncDetails { get; set; } = string.Empty;

    public bool HasUnread => UnreadCount > 0;

    public string UnreadText => UnreadCount == 1 ? "1 new" : string.Create(CultureInfo.CurrentCulture, $"{UnreadCount:N0} new");

    public Task InitializeAsync() => ReloadAsync(ReloadReason.Initial);

    public void Dispose() => _clock.Stop();

    // ---- Commands ---------------------------------------------------------------------------------------

    [RelayCommand]
    private void Refresh() => _coordinator.RequestRefresh(RefreshReason.Manual);

    [RelayCommand]
    private async Task OpenAdAsync(AdRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row?.Url is not { } url)
        {
            return;
        }

        if (_browser.Open(url))
        {
            row.Unread = false;
            await _store.MarkReadAsync(row.Id).ConfigureAwait(true);
            UnreadCount = Rows.Count(r => r.Unread && !r.IsGone);
        }
        else
        {
            ShowNotice("Could not open the browser.");
        }
    }

    [RelayCommand]
    private async Task MarkVisibleReadAsync()
    {
        await _coordinator.MarkMatchingReadAsync().ConfigureAwait(true);
        foreach (var row in Rows)
        {
            row.Unread = false;
        }

        UnreadCount = 0;
    }

    [RelayCommand]
    private Task ShowHeldAsync() => ReloadAsync(ReloadReason.ShowHeld);

    [RelayCommand]
    private void TogglePlaces()
    {
        IsSettingsOpen = false;
        IsPlacesOpen = !IsPlacesOpen;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        IsPlacesOpen = false;
        IsSettingsOpen = true;
    }

    /// <summary>Esc: closes a panel; otherwise nothing.</summary>
    [RelayCommand]
    private void ClosePanels()
    {
        IsPlacesOpen = false;
        IsSettingsOpen = false;
    }

    [RelayCommand]
    private void Hide() => _shell.HideWindow();

    [RelayCommand]
    private void Quit() => _shell.Quit();

    [RelayCommand]
    private void OpenDataFolder() => _browser.OpenFolder(DataDirectory);

    [RelayCommand]
    private void ChooseAllSweden() => ApplyFilter(PlaceSelection.SetAllSweden(_preferences.Filter, true));

    // ---- Filters ----------------------------------------------------------------------------------------

    private void ApplyFilter(AdFilter filter)
    {
        if (filter.Equals(_preferences.Filter))
        {
            return;
        }

        UpdatePreferences(_preferences with { Filter = filter });
        PlacesSummary = PlaceSelection.Summary(filter, _catalog);
        Places.Sync(filter);
        _ = ApplyFilterAsync(filter);
    }

    private async Task ApplyFilterAsync(AdFilter filter)
    {
        // Waits for a running batch, so unread decisions never mix two selections.
        await _coordinator.SetFilterAsync(filter).ConfigureAwait(true);
        await ReloadAsync(ReloadReason.FilterChanged).ConfigureAwait(true);
    }

    partial void OnFullTimeChanged(bool value) => OnWorktimeToggled();

    partial void OnPartTimeChanged(bool value) => OnWorktimeToggled();

    partial void OnNotSpecifiedChanged(bool value) => OnWorktimeToggled();

    private void OnWorktimeToggled()
    {
        if (_syncingWorktime)
        {
            return;
        }

        var set = (FullTime ? WorktimeSet.FullTime : WorktimeSet.None)
                  | (PartTime ? WorktimeSet.PartTime : WorktimeSet.None)
                  | (NotSpecified ? WorktimeSet.NotSpecified : WorktimeSet.None);
        if (set == WorktimeSet.None)
        {
            // At least one category stays selected; an empty choice would always show nothing.
            Dispatcher.UIThread.Post(() => SyncWorktime(_preferences.Filter.Worktime));
            return;
        }

        ApplyFilter(_preferences.Filter with { Worktime = set });
    }

    private void SyncWorktime(WorktimeSet set)
    {
        _syncingWorktime = true;
        FullTime = set.HasFlag(WorktimeSet.FullTime);
        PartTime = set.HasFlag(WorktimeSet.PartTime);
        NotSpecified = set.HasFlag(WorktimeSet.NotSpecified);
        _syncingWorktime = false;
    }

    // ---- Settings ---------------------------------------------------------------------------------------

    private void SyncSettings(AppPreferences p)
    {
        _syncingSettings = true;
        PollMinutes = p.PollMinutes;
        ThemeIndex = (int)p.Theme;
        Transparent = p.Transparent;
        AlwaysOnTop = p.AlwaysOnTop;
        Paused = p.MonitoringPaused;
        _syncingSettings = false;
    }

    partial void OnPollMinutesChanged(decimal value)
    {
        if (_syncingSettings)
        {
            return;
        }

        var minutes = (int)Math.Clamp(Math.Round(value), AppPreferences.MinPollMinutes, AppPreferences.MaxPollMinutes);
        UpdatePreferences(_preferences with { PollMinutes = minutes });
        _coordinator.SetPollInterval(TimeSpan.FromMinutes(minutes));
    }

    partial void OnThemeIndexChanged(int value) => UpdateAppearance(_preferences with { Theme = (ThemePreference)Math.Clamp(value, 0, 2) });

    partial void OnTransparentChanged(bool value) => UpdateAppearance(_preferences with { Transparent = value });

    partial void OnAlwaysOnTopChanged(bool value) => UpdateAppearance(_preferences with { AlwaysOnTop = value });

    partial void OnPausedChanged(bool value)
    {
        if (_syncingSettings)
        {
            return;
        }

        UpdatePreferences(_preferences with { MonitoringPaused = value });
        _coordinator.SetPaused(value);
    }

    /// <summary>Pause toggled from the tray.</summary>
    public void TogglePause() => Paused = !Paused;

    public AppPreferences Preferences => _preferences;

    /// <summary>Window bounds come from the view; they are saved with the other preferences.</summary>
    public void SetWindowBounds(WindowBounds bounds) => UpdatePreferences(_preferences with { Window = bounds });

    private void UpdateAppearance(AppPreferences next)
    {
        if (_syncingSettings)
        {
            return;
        }

        UpdatePreferences(next);
        _shell.ApplyAppearance(next);
    }

    private void UpdatePreferences(AppPreferences next)
    {
        _preferences = next.Normalized();
        _shell.SavePreferences(_preferences);
    }

    // ---- Data -------------------------------------------------------------------------------------------

    private async Task ReloadAsync(ReloadReason reason)
    {
        var version = ++_reloadVersion;
        var filter = _preferences.Filter;
        var now = _time.GetUtcNow();
        IReadOnlyList<AdRow> rows;
        int total;
        try
        {
            rows = await _store.QueryAsync(filter, now).ConfigureAwait(true);
            total = await _store.CountCurrentAsync(now).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the local cache failed");
            ShowNotice("Could not read the saved ads.");
            return;
        }

        if (version != _reloadVersion)
        {
            return; // A newer reload is under way.
        }

        HasLoaded = true;
        var applyNow = reason != ReloadReason.DataChanged || IsListAtTop || Rows.Count == 0;
        if (applyNow)
        {
            ApplyRows(rows, now, flashNew: reason == ReloadReason.DataChanged || reason == ReloadReason.ShowHeld);
            if (reason == ReloadReason.ShowHeld)
            {
                ScrollToTopRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            HoldRows(rows, now);
        }

        UpdateBodyState(total);
    }

    /// <summary>Replaces the list. Existing row objects are reused so state and selection carry over.</summary>
    private void ApplyRows(IReadOnlyList<AdRow> rows, DateTimeOffset now, bool flashNew)
    {
        _held = null;
        HasHeldUpdates = false;
        var existing = Rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var list = new List<AdRowViewModel>(rows.Count);
        var flashing = new List<AdRowViewModel>();
        foreach (var row in rows)
        {
            if (existing.TryGetValue(row.Ad.Id, out var vm))
            {
                vm.Update(row, now);
            }
            else
            {
                vm = new AdRowViewModel(row, now);
                if (flashNew && row.Unread)
                {
                    vm.IsFlashing = true;
                    flashing.Add(vm);
                }
            }

            list.Add(vm);
        }

        var selected = SelectedRow;
        Rows = new ObservableCollection<AdRowViewModel>(list);
        OnPropertyChanged(nameof(Rows));
        SelectedRow = selected is not null && existing.ContainsKey(selected.Id) && list.Contains(selected) ? selected : null;
        UnreadCount = list.Count(r => r.Unread);
        CountText = DisplayText.AdCount(list.Count);

        if (flashing.Count > 0)
        {
            DispatcherTimer.RunOnce(() => flashing.ForEach(r => r.IsFlashing = false), TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// The user is scrolled down: keep rows where they are, update their contents in place, mark rows that left
    /// the results, and offer the new ones behind a pill instead of moving the list under the pointer.
    /// </summary>
    private void HoldRows(IReadOnlyList<AdRow> rows, DateTimeOffset now)
    {
        _held = rows;
        var incoming = rows.ToDictionary(r => r.Ad.Id, StringComparer.Ordinal);
        foreach (var vm in Rows)
        {
            if (incoming.TryGetValue(vm.Id, out var row))
            {
                vm.Update(row, now);
            }
            else
            {
                vm.IsGone = true;
            }
        }

        var shown = Rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var added = rows.Count(r => !shown.Contains(r.Ad.Id));
        var gone = Rows.Count(r => r.IsGone);
        HasHeldUpdates = added > 0 || gone > 0;
        HeldText = (added, gone) switch
        {
            ( > 0, > 0) => string.Create(CultureInfo.CurrentCulture, $"{added:N0} new · {gone:N0} removed — show"),
            ( > 0, _) => added == 1 ? "1 new ad — show" : string.Create(CultureInfo.CurrentCulture, $"{added:N0} new ads — show"),
            _ => gone == 1 ? "1 ad removed — update list" : string.Create(CultureInfo.CurrentCulture, $"{gone:N0} ads removed — update list"),
        };
        UnreadCount = rows.Count(r => r.Unread);
    }

    private void UpdateBodyState(int totalCached)
    {
        var hasData = totalCached > 0 || _status.HasBaseline;
        var filter = _preferences.Filter;
        ShowFirstDownload = !hasData && _status.Phase == SyncPhase.LoadingSnapshot;
        ShowNoData = !hasData && !ShowFirstDownload;
        ShowChoosePlaces = hasData && !filter.HasGeography;
        ShowNoMatches = hasData && filter.HasGeography && Rows.Count == 0;
        ShowList = hasData && Rows.Count > 0;
    }

    private void OnStatus(SyncStatus status)
    {
        var hadBaseline = _status.HasBaseline;
        _status = status;
        UpdateStatusText();
        if (!hadBaseline && status.HasBaseline)
        {
            _ = ReloadAsync(ReloadReason.Initial);
        }
        else if (HasLoaded)
        {
            UpdateBodyState(_status.HasBaseline ? 1 : 0);
        }
    }

    private void UpdateStatusText()
    {
        var now = _time.GetUtcNow();
        var s = _status;
        var age = s.LastSuccessUtc is { } last ? DisplayText.Age(last, now) : null;
        var stale = s.LastSuccessUtc is { } l && now - l > TimeSpan.FromMinutes(Math.Max(15, 3 * (double)PollMinutes));

        (StatusText, StatusTone) = s.Phase switch
        {
            SyncPhase.LoadingSnapshot => (s.SnapshotAdsReceived is > 0 ? $"Downloading · {s.SnapshotAdsReceived:N0}" : "Downloading all ads", StatusTone.Running),
            SyncPhase.Updating => ("Updating", StatusTone.Running),
            SyncPhase.Paused => (age is null ? "Paused" : $"Paused · {age}", StatusTone.Warning),
            SyncPhase.Offline => (age is null ? "Offline" : $"Offline · {age}", StatusTone.Warning),
            SyncPhase.Failed => (age is null ? "Update failed" : $"Update failed · {age}", StatusTone.Failure),
            _ when age is null => ("Not updated yet", StatusTone.Neutral),
            _ => ($"Updated {age}", stale ? StatusTone.Warning : StatusTone.Success),
        };

        StatusTooltip = s.NextRunUtc is { } next && s.Phase is not SyncPhase.Paused
            ? $"{StatusText}. Next check {SwedishTime.ToLocal(next):HH:mm}."
            : StatusText;
        ShowActivity = s.Foreground;
        FirstDownloadText = s.SnapshotAdsReceived is > 0
            ? $"{s.SnapshotAdsReceived:N0} ads received so far."
            : "Connecting to Arbetsförmedlingen…";
        SyncDetails = s.LastSuccessUtc is { } success
            ? $"Last update {SwedishTime.ToLocal(success):yyyy-MM-dd HH:mm}"
            : "No successful update yet";

        if (s.Message is { } message)
        {
            ShowNotice(message);
        }
        else if (HasNotice && s.Phase is SyncPhase.Idle)
        {
            HasNotice = false;
        }
    }

    private void ShowNotice(string message)
    {
        Notice = message;
        HasNotice = true;
    }

    partial void OnUnreadCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(UnreadText));
    }
}
