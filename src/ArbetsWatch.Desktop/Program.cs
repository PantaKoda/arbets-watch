using System.Reflection;
using ArbetsWatch.Core.Places;
using ArbetsWatch.Core.Platform;
using ArbetsWatch.Core.Settings;
using ArbetsWatch.Core.Storage;
using ArbetsWatch.Core.Sync;
using ArbetsWatch.Desktop.Platform;
using Avalonia;
using Microsoft.Extensions.Logging;

namespace ArbetsWatch.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppPaths.Resolve();

        // One instance per user and data folder: a second launch shows the running window and exits.
        using var instance = new SingleInstance(paths.DataDirectory);
        if (!instance.IsFirst)
        {
            return instance.SignalFirst(TimeSpan.FromSeconds(3)) ? 0 : 1;
        }

        using var loggers = CreateLoggerFactory(paths);
        var logger = loggers.CreateLogger(typeof(Program));
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        logger.LogInformation("ArbetsWatch {Version} starting on {OS}; data directory {DataDirectory}", version, Environment.OSVersion, paths.DataDirectory);

        StoreOpenResult opened;
        try
        {
            opened = StoreOpener.Open(paths.DatabasePath);
        }
        catch (StoreUnavailableException ex)
        {
            logger.LogCritical(ex, "Cannot open the database");
            FatalMessage.Show($"ArbetsWatch can't open its saved data in {paths.DataDirectory}.\n\n{ex.Message}\n\nNothing was changed. Close other programs that may use the folder, then start ArbetsWatch again.");
            return 2;
        }
        catch (InvalidOperationException ex)
        {
            // A database from a newer version: never overwrite it.
            logger.LogCritical(ex, "Cannot open the database");
            FatalMessage.Show($"The saved data in {paths.DataDirectory} was created by a newer ArbetsWatch. Start the newer version, or remove that folder to start fresh.");
            return 2;
        }

        var store = opened.Store;
        if (opened.QuarantinedTo is { } aside)
        {
            logger.LogError("The database was unreadable and was moved to {Aside}; starting with an empty cache", aside);
        }

        using (store)
        {
            var preferences = PreferencesStore.LoadAsync(store).GetAwaiter().GetResult();
            using var http = JobStreamClient.CreateHttpClient($"ArbetsWatch/{version.Split('+')[0]}");
            var time = TimeProvider.System;
            var options = new SyncOptions();
            var engine = new SyncEngine(store, new JobStreamClient(http), new RequestGate(time, options.MinRequestSpacing), time, options,
                loggers.CreateLogger<SyncEngine>());
            var coordinator = new RefreshCoordinator(engine, store, time, loggers.CreateLogger<RefreshCoordinator>(),
                preferences.Filter, TimeSpan.FromMinutes(preferences.PollMinutes), preferences.MonitoringPaused);

            using var shell = new AppShell(paths, store, coordinator, PlaceCatalog.LoadBundled(), preferences, instance, loggers);
            if (opened.QuarantinedTo is not null)
            {
                shell.SetStartupMessage("The saved data couldn't be read, so ArbetsWatch started fresh and downloads the ads again. The old file was kept next to the new one.");
            }
            try
            {
                return BuildAvaloniaApp(() => new App(shell)).StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Unhandled exception; shutting down");
                throw;
            }
        }
    }

    // Entry point used by the Avalonia designer/previewer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(() => new App());

    private static AppBuilder BuildAvaloniaApp(Func<App> createApp) =>
        AppBuilder.Configure(createApp)
            .UsePlatformDetect()
            .LogToTrace();

    private static ILoggerFactory CreateLoggerFactory(AppPaths paths) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddDebug();
            try
            {
                builder.AddProvider(new FileLoggerProvider(paths.LogDirectory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unwritable data directory: continue with debug logging only.
            }
        });
}
