using System.Reflection;
using ArbetsWatch.Core.Places;
using ArbetsWatch.Core.Platform;
using ArbetsWatch.Core.Settings;
using ArbetsWatch.Core.Storage;
using ArbetsWatch.Core.Sync;
using ArbetsWatch.Core.Updates;
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
        // The staged copy of an update replaces the installed folder and starts it; nothing else runs.
        if (UpdateApplier.TryRun(args, out var applied))
        {
            return applied;
        }

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
        var install = InstallInfo.Detect(args, version, AppContext.BaseDirectory);
        if (install.UpdatedFrom is { } from)
        {
            logger.LogInformation("Updated from {From} to {Version}", from, install.Version);
        }

        if (install.UpdateFailed is { } failure)
        {
            logger.LogWarning("Started again after a failed update: {Failure}", failure);
        }

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

            // Updates come from this repository's public GitHub releases; ARBETSWATCH_UPDATE_REPOSITORY points
            // a test build at another repository.
            var repository = Environment.GetEnvironmentVariable("ARBETSWATCH_UPDATE_REPOSITORY") is { Length: > 0 } custom
                ? custom
                : ReleaseClient.DefaultRepository;
            using var releaseHttp = ReleaseClient.CreateHttpClient($"ArbetsWatch/{version.Split('+')[0]}");
            var releases = new ReleaseClient(releaseHttp, repository);
            using var updates = new UpdateService(releases, releases.ReleasesPage, paths.DataDirectory, install, new ProcessLauncher(), time,
                new UpdateOptions(), loggers.CreateLogger<UpdateService>());

            using var shell = new AppShell(paths, store, coordinator, PlaceCatalog.LoadBundled(), preferences, instance, updates, loggers);
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
