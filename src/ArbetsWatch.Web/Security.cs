namespace ArbetsWatch.Web;

/// <summary>
/// Sets the browser protections on every response that has a body, errors included (a refused Host name has none). The policy allows only this origin's own
/// scripts and styles, so ad text that somehow reached the page as markup still could not run, and Trusted Types
/// makes the markup-writing DOM calls fail outright (the frontend builds everything with text nodes).
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    internal const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; " +
        "base-uri 'none'; form-action 'none'; frame-ancestors 'none'; require-trusted-types-for 'script'; trusted-types 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            headers["X-Robots-Tag"] = "noindex";
            headers.CacheControl = RequestGuardMiddleware.IsApi(context.Request) ? "no-store" : "no-cache";
            return Task.CompletedTask;
        });

        return next(context);
    }
}

/// <summary>
/// The app has no state-changing request at all, so anything but GET and HEAD is refused. API calls started by another
/// site are refused too: they could not read the answer, but could still spend DeepL quota or upstream requests. The
/// required header decides that. A page on another origin can only send it after a CORS preflight, and the preflight is
/// refused here, so this holds in a browser that doesn't send Sec-Fetch-Site too.
/// </summary>
internal sealed class RequestGuardMiddleware(RequestDelegate next)
{
    internal const string RequiredHeader = "X-ArbetsWatch";

    /// <summary>Case-insensitive, because routing is: "/API/ads" reaches the same endpoint as "/api/ads".</summary>
    internal static bool IsApi(HttpRequest request) => request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

    public Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET, HEAD";
            return Task.CompletedTask;
        }

        if (IsApi(context.Request) &&
            (context.Request.Headers[RequiredHeader] != "1" || context.Request.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        return next(context);
    }
}
