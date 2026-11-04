using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ArbetsWatch.Core.Details;
using ArbetsWatch.Core.Places;
using ArbetsWatch.Core.Storage;
using ArbetsWatch.Core.Sync;
using ArbetsWatch.Core.Translation;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.RateLimiting;

namespace ArbetsWatch.Web;

public sealed record WebAppDependencies(
    AdStore Store,
    RefreshCoordinator Coordinator,
    PlaceCatalog Places,
    IAdDetailsSource Details,
    TitleTranslationService Translation,
    TimeProvider Time,
    int Port);

/// <summary>
/// The web host. It only ever listens on the loopback interface, whatever the environment or command line say, and
/// serves a read-only API over public ad data: there are no accounts, cookies or state-changing endpoints.
/// </summary>
public static class WebApp
{
    internal const string ApiPolicy = "api";
    internal const string UpstreamPolicy = "upstream";

    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1"];

    /// <summary>
    /// Console only, and framework categories at Warning: request logging at Information would write full URLs, and
    /// with them search words and saved ad IDs.
    /// </summary>
    public static void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.SetMinimumLevel(LogLevel.Information);
        logging.AddFilter("Microsoft", LogLevel.Warning);
        logging.AddFilter("System.Net.Http", LogLevel.Warning);
        logging.AddSimpleConsole(o => o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ");
    }

    public static WebApplication Build(WebAppDependencies deps, string[]? args = null, Action<ILoggingBuilder>? configureLogging = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args ?? [],

            // Never Development, whatever ASPNETCORE_ENVIRONMENT says: that would enable the page showing stack traces.
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        // Nothing outside this code configures the server: environment variables or a file next to the binary could
        // otherwise add a listener on the network (Kestrel__Endpoints__*) or bring back request logging. The only settings
        // read are ARBETSWATCH_WEB_PORT, ARBETSWATCH_DATA_DIR and ARBETSWATCH_DEEPL_KEY, by name.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.PreferHostingUrls(false);

        (configureLogging ?? ConfigureLogging)(builder.Logging);

        // An explicit listener replaces ASPNETCORE_URLS and --urls.
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 0;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Limits.MaxRequestLineSize = 8 * 1024;
            kestrel.Listen(IPAddress.Loopback, deps.Port);
        });

        builder.Services.Configure<HostFilteringOptions>(o =>
        {
            // Rejects DNS rebinding: a hostile name resolving to 127.0.0.1 still arrives with that name as its Host.
            o.AllowedHosts = LoopbackHosts;
            o.AllowEmptyHosts = false;
            o.IncludeFailureMessage = false;
        });

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };

            o.AddPolicy(ApiPolicy, context =>
            {
                // English titles spend the operator's DeepL quota, so they get the tighter budget.
                var english = context.Request.Query.ContainsKey("english");
                return PerClient(context, english ? "english" : "api", english ? 60 : 600);
            });

            // Each request here costs a request to Arbetsförmedlingen on the visitor's behalf.
            o.AddPolicy(UpstreamPolicy, context => PerClient(context, UpstreamPolicy, 60));
        });

        var app = builder.Build();

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            // The last check, should the framework ever add a way around the above: stop rather than be exposed.
            if (!Addresses(app).All(IsLoopback))
            {
                app.Logger.LogCritical("Refusing to run: the server is bound to a non-loopback address");
                app.Lifetime.StopApplication();
                return;
            }

            deps.Coordinator.Start();
        });

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseExceptionHandler(failure => failure.Run(context =>
        {
            // A fixed answer: exception text stays in the server log.
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return context.RequestAborted.IsCancellationRequested
                ? Task.CompletedTask
                : context.Response.WriteAsJsonAsync(new ApiError("Something went wrong on the server."));
        }));
        app.UseHostFiltering();
        app.UseMiddleware<RequestGuardMiddleware>();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();

        AdsApi.Map(app, deps);

        return app;
    }

    /// <summary>The address the server listens on, once it has started.</summary>
    internal static IEnumerable<string> Addresses(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses ?? [];

    internal static bool IsLoopback(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip);

    private static RateLimitPartition<string> PerClient(HttpContext context, string budget, int perMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"{budget}:{context.Connection.RemoteIpAddress}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = perMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
}
