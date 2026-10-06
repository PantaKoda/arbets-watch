using System.Reflection;
using ArbetsWatch.Core.Places;
using ArbetsWatch.Core.Platform;
using ArbetsWatch.Core.Settings;
using ArbetsWatch.Core.Storage;
using ArbetsWatch.Core.Sync;
using ArbetsWatch.Desktop.Platform;
using Avalonia;
using Microsoft.Data.Sqlite;
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

        AdStore store;
        try
        {
            store = OpenStore(paths, logger);
        }
        catch (InvalidOperationException ex)
        {
            // A database from a newer version: never overwrite it.
            logger.LogCritical(ex, "Cannot open the database");
            return 2;
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

    /// <summary>Opens the cache; an unreadable (corrupt) file is moved aside so the app can start fresh.</summary>
    private static AdStore OpenStore(AppPaths paths, ILogger logger)
    {
        try
        {
            return AdStore.Open(paths.DatabasePath);
        }
        catch (SqliteException ex)
        {
            var aside = $"{paths.DatabasePath}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}";
            logger.LogError(ex, "The database could not be opened; moving it to {Aside} and starting with an empty cache", aside);
            SqliteConnection.ClearAllPools();
            File.Move(paths.DatabasePath, aside);
            return AdStore.Open(paths.DatabasePath);
        }
    }

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
