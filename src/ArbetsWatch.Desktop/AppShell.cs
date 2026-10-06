using ArbetsWatch.Core.Places;
using ArbetsWatch.Core.Platform;
using ArbetsWatch.Core.Settings;
using ArbetsWatch.Core.Storage;
using ArbetsWatch.Core.Sync;
using ArbetsWatch.Desktop.Platform;
using ArbetsWatch.Desktop.ViewModels;
using ArbetsWatch.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace ArbetsWatch.Desktop;

/// <summary>
/// Owns the window, tray, preferences and the background coordinator for the life of the process. Closing the
/// window hides it to the tray; Quit stops monitoring and exits.
/// </summary>
public sealed class AppShell : IShell, IDisposable
{
    private static readonly Uri IconUri = new("avares://ArbetsWatch/Assets/ArbetsWatch.ico");

    private readonly AppPaths _paths;
    private readonly AdStore _store;
    private readonly RefreshCoordinator _coordinator;
    private readonly PlaceCatalog _catalog;
    private readonly SingleInstance _instance;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<AppShell> _logger;
    private readonly TrayService _tray;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _boundsTimer;
    private readonly DispatcherTimer _resumeWatch;
    private AppPreferences _preferences;
    private AppPreferences? _pendingSave;
    private readonly ResumeDetector _resume = new(TimeProvider.System, TimeSpan.FromMinutes(2));
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private Application? _application;
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private bool _quitting;
    private string? _startupMessage;

    /// <summary>Shown once in the window after it opens (e.g. the database had to be reset).</summary>
    public void SetStartupMessage(string message) => _startupMessage = message;

    public AppShell(
        AppPaths paths,
        AdStore store,
        RefreshCoordinator coordinator,
        PlaceCatalog catalog,
        AppPreferences preferences,
        SingleInstance instance,
        ILoggerFactory loggers)
    {
        _paths = paths;
        _store = store;
        _coordinator = coordinator;
        _catalog = catalog;
        _preferences = preferences;
        _instance = instance;
        _loggers = loggers;
        _logger = loggers.CreateLogger<AppShell>();
        _tray = new TrayService(loggers.CreateLogger<TrayService>());

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => FlushPreferences();
        _boundsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _boundsTimer.Tick += (_, _) => CaptureBounds();
        _resumeWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _resumeWatch.Tick += (_, _) => DetectResume();
    }

    public void Start(IClassicDesktopStyleApplicationLifetime desktop, Application application)
    {
        _desktop = desktop;
        _application = application;

        var viewModel = new MainViewModel(_store, _coordinator, _catalog, new BrowserLauncher(_loggers.CreateLogger<BrowserLauncher>()),
            TimeProvider.System, _paths, _preferences, this, _loggers.CreateLogger<MainViewModel>());
        var window = new MainWindow { DataContext = viewModel, Icon = LoadIcon() };
        _viewModel = viewModel;
        _window = window;

        window.Classes.Set("reduce-motion", !SystemVisuals.AnimationsEnabled);
        RestoreBounds(window, _preferences.Window);
        window.PositionChanged += (_, _) => ScheduleBounds();
        window.SizeChanged += (_, _) => ScheduleBounds();
        window.Opened += (_, _) => ApplyAppearance(_preferences);
        window.Closing += (_, e) =>
        {
            // Only a close the user starts hides to the tray. Sign-out, shutdown and Quit close for real, so
            // Windows is never blocked and Shutdown() runs.
            if (!_quitting && _tray.IsAvailable && e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
            {
                e.Cancel = true;
                window.Hide();
            }
        };

        _tray.Initialize(application, window.Icon!, new TrayActions(
            Toggle: () => Dispatcher.UIThread.Post(ToggleWindow),
            Show: () => Dispatcher.UIThread.Post(ShowWindow),
            Refresh: () => _coordinator.RequestRefresh(RefreshReason.Manual),
            TogglePause: () => Dispatcher.UIThread.Post(() => viewModel.TogglePause()),
            OpenSettings: () => Dispatcher.UIThread.Post(() =>
            {
                ShowWindow();
                viewModel.OpenSettingsCommand.Execute(null);
            }),
            Quit: () => Dispatcher.UIThread.Post(Quit)), _preferences.MonitoringPaused);

        // Without a tray, closing the window ends the app; with one, only Quit does.
        desktop.ShutdownMode = _tray.IsAvailable ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnMainWindowClose;
        desktop.MainWindow = window;
        desktop.Exit += (_, _) => Shutdown();

        _instance.ActivationRequested += (_, _) => Dispatcher.UIThread.Post(ShowWindow);
        _instance.Listen();

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Paused))
            {
                _tray.SetPaused(viewModel.Paused);
            }
            else if (e.PropertyName == nameof(MainViewModel.StatusText))
            {
                _tray.SetToolTip($"ArbetsWatch · {viewModel.StatusText}");
            }
        };

        window.Show();
        _resumeWatch.Start();
        _coordinator.Start();
        _ = viewModel.InitializeAsync();
        if (_startupMessage is { } message)
        {
            viewModel.ShowMessage(message);
        }
        _logger.LogInformation("Window shown; tray available: {Tray}", _tray.IsAvailable);
    }

    // ---- IShell -----------------------------------------------------------------------------------------

    public void HideWindow()
    {
        if (_tray.IsAvailable)
        {
            _window?.Hide();
        }
        else if (_window is not null)
        {
            _window.WindowState = WindowState.Minimized;
        }
    }

    public void ApplyAppearance(AppPreferences preferences)
    {
        _preferences = preferences;
        if (_application is not null)
        {
            _application.RequestedThemeVariant = preferences.Theme switch
            {
                ThemePreference.Light => ThemeVariant.Light,
                ThemePreference.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }

        if (_window is not { } window)
        {
            return;
        }

        // Only the surface becomes see-through; text and controls stay fully opaque. High contrast or a
        // platform without transparency gets a solid surface.
        var wantsGlass = preferences.Transparent && !SystemVisuals.HighContrast && !SystemVisuals.RemoteSession;
        IReadOnlyList<WindowTransparencyLevel> hint = wantsGlass
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Mica, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent]
            : [WindowTransparencyLevel.Transparent];
        if (!window.TransparencyLevelHint.SequenceEqual(hint))
        {
            window.TransparencyLevelHint = hint;
        }

        var achieved = window.ActualTransparencyLevel;
        var light = window.ActualThemeVariant == ThemeVariant.Light;
        var opacity = !wantsGlass || achieved == WindowTransparencyLevel.None ? 1.0
            : achieved == WindowTransparencyLevel.Transparent ? 0.94
            : light ? 0.86 : 0.78;
        window.Surface.Opacity = opacity;
        window.Background = achieved == WindowTransparencyLevel.None &&
                            window.TryFindResource("WidgetSurfaceBrush", window.ActualThemeVariant, out var solid) && solid is IBrush brush
            ? brush
            : Brushes.Transparent;
    }

    public void SavePreferences(AppPreferences preferences)
    {
        _preferences = preferences;
        _pendingSave = preferences;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Quit()
    {
        _quitting = true;
        _desktop?.Shutdown();
    }

    public void Dispose()
    {
        _tray.Dispose();
        _viewModel?.Dispose();
    }

    // ---- Window -----------------------------------------------------------------------------------------

    private void ToggleWindow()
    {
        if (_window is { IsVisible: true, WindowState: not WindowState.Minimized, IsActive: true })
        {
            HideWindow();
        }
        else
        {
            ShowWindow();
        }
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        EnsureReachable(_window);
        _window.Activate();
    }

    private void RestoreBounds(Window window, WindowBounds? bounds)
    {
        window.WindowStartupLocation = bounds is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual;
        if (bounds is null)
        {
            return;
        }

        window.Width = Math.Max(window.MinWidth, bounds.Width);
        window.Height = Math.Max(window.MinHeight, bounds.Height);
        window.Position = new PixelPoint(bounds.X, bounds.Y);
        window.Opened += (_, _) => EnsureReachable(window);
    }

    /// <summary>Moves the window onto a working area if a monitor or DPI change left it unreachable.</summary>
    private void EnsureReachable(Window window)
    {
        var screens = window.Screens.All;
        if (screens.Count == 0)
        {
            return;
        }

        var scale = window.RenderScaling;
        var box = new PixelRect(window.Position, new PixelSize((int)(window.Width * scale), (int)(window.Height * scale)));
        // Reachable: at least the header (top 40 px, 120 px wide) is on some working area.
        var header = new PixelRect(box.X, box.Y, Math.Min(box.Width, (int)(120 * scale)), (int)(40 * scale));
        if (screens.Any(s => s.WorkingArea.Intersects(header) && s.WorkingArea.Intersect(header).Width >= header.Width / 2))
        {
            return;
        }

        var primary = screens.FirstOrDefault(s => s.IsPrimary) ?? screens[0];
        var area = primary.WorkingArea;
        var width = Math.Min(box.Width, area.Width);
        var height = Math.Min(box.Height, area.Height);
        var centered = new PixelPoint(area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2));
        _logger.LogInformation("Window at {From} was unreachable; centered on the primary display at {To}", window.Position, centered);
        window.Position = centered;
    }

    private void ScheduleBounds()
    {
        _boundsTimer.Stop();
        _boundsTimer.Start();
    }

    private void CaptureBounds()
    {
        _boundsTimer.Stop();
        if (_window is { IsVisible: true, WindowState: WindowState.Normal } window && _viewModel is not null && window.Width > 0)
        {
            _viewModel.SetWindowBounds(new WindowBounds(window.Position.X, window.Position.Y, Math.Round(window.Width), Math.Round(window.Height), false));
        }
    }

    // ---- Background ----------------------------------------------------------------------------------------

    /// <summary>
    /// The 30-second tick stops while the computer sleeps; a much longer gap means the system resumed, so the
    /// coordinator catches up instead of waiting for its next scheduled check.
    /// </summary>
    private void DetectResume()
    {
        if (_resume.Tick() is { } gap)
        {
            _logger.LogInformation("Resumed after {Gap}; catching up", gap);
            _coordinator.RequestRefresh(RefreshReason.Resume);
        }
    }

    private void FlushPreferences()
    {
        _saveTimer.Stop();
        if (_pendingSave is not { } preferences)
        {
            return;
        }

        _pendingSave = null;
        _ = SaveAsync(preferences);
    }

    private async Task SaveAsync(AppPreferences preferences)
    {
        try
        {
            await PreferencesStore.SaveAsync(_store, preferences).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving preferences failed");
        }
    }

    private void Shutdown()
    {
        _resumeWatch.Stop();
        CaptureBounds();
        FlushPreferences();
        if (_viewModel is not null)
        {
            // Make sure the latest preferences (including window bounds) are written before exit.
            try
            {
                PreferencesStore.SaveAsync(_store, _viewModel.Preferences).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Saving preferences on exit failed");
            }
        }

        try
        {
            _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stopping monitoring failed");
        }

        _logger.LogInformation("Stopped");
    }

    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(IconUri);
        return new WindowIcon(stream);
    }
}
