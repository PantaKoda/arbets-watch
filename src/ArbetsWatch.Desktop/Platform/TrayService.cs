using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;

namespace ArbetsWatch.Desktop.Platform;

public sealed record TrayActions(Action Toggle, Action Show, Action Refresh, Action TogglePause, Action OpenSettings, Action Quit);

/// <summary>
/// Notification-area icon with Show, Refresh, Pause monitoring, Settings and Quit. Treated as available only
/// where the platform reliably provides one; elsewhere the window stays in the taskbar and closing it quits.
/// </summary>
public sealed class TrayService(ILogger<TrayService> logger) : IDisposable
{
    private TrayIcon? _icon;
    private NativeMenuItem? _pause;

    public bool IsAvailable { get; private set; }

    public void Initialize(Application application, WindowIcon icon, TrayActions actions, bool paused)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(actions);

        // Linux tray support depends on the desktop environment and cannot be detected reliably yet.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            logger.LogInformation("No supported tray on this platform; the window stays in the taskbar");
            return;
        }

        try
        {
            var menu = new NativeMenu();
            menu.Items.Add(Item("Show ArbetsWatch", actions.Show));
            menu.Items.Add(Item("Refresh now", actions.Refresh));
            _pause = Item("Pause monitoring", actions.TogglePause);
            _pause.ToggleType = MenuItemToggleType.CheckBox;
            _pause.IsChecked = paused;
            menu.Items.Add(_pause);
            menu.Items.Add(Item("Settings…", actions.OpenSettings));
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(Item("Quit ArbetsWatch", actions.Quit));

            _icon = new TrayIcon { Icon = icon, ToolTipText = "ArbetsWatch", Menu = menu, IsVisible = true };
            _icon.Clicked += (_, _) => actions.Toggle();
            TrayIcon.SetIcons(application, [_icon]);
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Creating the tray icon failed; the window stays in the taskbar");
            IsAvailable = false;
        }
    }

    public void SetPaused(bool paused)
    {
        if (_pause is not null)
        {
            _pause.IsChecked = paused;
        }
    }

    public void SetToolTip(string text)
    {
        if (_icon is not null)
        {
            _icon.ToolTipText = text;
        }
    }

    public void Dispose()
    {
        if (_icon is not null)
        {
            _icon.IsVisible = false;
            _icon.Dispose();
        }
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }
}
