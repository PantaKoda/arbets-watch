using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ArbetsWatch.Desktop;

public sealed partial class App : Application
{
    private readonly AppShell? _shell;

    // Used by the designer/previewer, which has no composition root.
    public App()
    {
    }

    internal App(AppShell shell)
    {
        _shell = shell;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (_shell is not null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _shell.Start(desktop, this);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
