using System.Globalization;
using System.Reflection;
using ArbetsWatch.Core.Details;
using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Places;
using ArbetsWatch.Core.Platform;
using ArbetsWatch.Core.Storage;
using ArbetsWatch.Core.Sync;
using ArbetsWatch.Core.Translation;
using ArbetsWatch.Web;
using Microsoft.Extensions.Logging;

const int defaultPort = 5317;

var portText = Environment.GetEnvironmentVariable("ARBETSWATCH_WEB_PORT");
var port = defaultPort;
if (portText is { Length: > 0 } &&
    (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1024 or > 65535))
{
    await Console.Error.WriteLineAsync("ARBETSWATCH_WEB_PORT must be a number from 1024 to 65535.").ConfigureAwait(false);
    return 2;
}

// Its own folder: the desktop app has a single writer per database, and may be running at the same time.
var dataDirectory = Path.Combine(AppPaths.Resolve().DataDirectory, "web");
if (OperatingSystem.IsWindows())
{
    Directory.CreateDirectory(dataDirectory);
}
else
{
    Directory.CreateDirectory(dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

var paths = new AppPaths(dataDirectory);
using var loggers = LoggerFactory.Create(WebApp.ConfigureLogging);
var logger = loggers.CreateLogger("ArbetsWatch.Web");

StoreOpenResult opened;
try
{
    opened = StoreOpener.Open(paths.DatabasePath);
}
catch (Exception ex) when (ex is StoreUnavailableException or InvalidOperationException)
{
    logger.LogCritical(ex, "Cannot open the database");
    return 2;
}

if (opened.QuarantinedTo is { } aside)
{
    logger.LogError("The database was unreadable and was moved to {Aside}; starting with an empty cache", aside);
}

using var store = opened.Store;
var version = typeof(WebApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
var userAgent = $"ArbetsWatch/{version.Split('+')[0]}";
var time = TimeProvider.System;

using var streamHttp = JobStreamClient.CreateHttpClient(userAgent);
var options = new SyncOptions();
var engine = new SyncEngine(store, new JobStreamClient(streamHttp), new RequestGate(time, options.MinRequestSpacing), time, options,
    loggers.CreateLogger<SyncEngine>());

// The server's own unread flags are not used: each browser keeps its read state itself.
var coordinator = new RefreshCoordinator(engine, store, time, loggers.CreateLogger<RefreshCoordinator>(),
    AdFilter.Default, TimeSpan.FromMinutes(5), paused: false);
await using (coordinator.ConfigureAwait(false))
{
    using var detailsHttp = JobSearchClient.CreateHttpClient(userAgent);
    using var translateHttp = DeepLClient.CreateHttpClient(userAgent);

    // The key is read from the environment only. No store means it can't be written anywhere, and no endpoint accepts one.
    var translation = new TitleTranslationService(new DeepLClient(translateHttp), new UnavailableSecretStore(), time,
        loggers.CreateLogger<TitleTranslationService>());

    var app = WebApp.Build(new WebAppDependencies(
        store, coordinator, PlaceCatalog.LoadBundled(), new JobSearchClient(detailsHttp), translation, time, port));
    await using (app.ConfigureAwait(false))
    {
        try
        {
            await app.RunAsync().ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // Normally a second copy of the server: the port is the single-instance lock, and sync starts only after binding.
            logger.LogCritical(ex, "Could not listen on 127.0.0.1:{Port}. Is ArbetsWatch.Web already running?", port);
            return 3;
        }
    }
}

return 0;
