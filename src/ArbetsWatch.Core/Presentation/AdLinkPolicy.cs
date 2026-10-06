using System.Text.RegularExpressions;

namespace ArbetsWatch.Core.Presentation;

/// <summary>
/// Only Platsbanken ad pages are opened in the browser. A URL from the API is used when it is an HTTPS link to
/// an ad page on arbetsformedlingen.se; otherwise the canonical page is derived from a numeric ad ID.
/// </summary>
public static partial class AdLinkPolicy
{
    private const string Host = "arbetsformedlingen.se";
    private const string AdPathPrefix = "/platsbanken/annonser/";

    public static Uri? Resolve(string id, string? url)
    {
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var candidate) && IsAllowed(candidate))
        {
            return candidate;
        }

        return NumericId().IsMatch(id) ? new Uri($"https://{Host}{AdPathPrefix}{id}") : null;
    }

    public static bool IsAllowed(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps &&
        url.IsDefaultPort &&
        string.IsNullOrEmpty(url.UserInfo) &&
        (url.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) || url.Host.Equals("www." + Host, StringComparison.OrdinalIgnoreCase)) &&
        url.AbsolutePath.StartsWith(AdPathPrefix, StringComparison.Ordinal) &&
        NumericId().IsMatch(url.AbsolutePath[AdPathPrefix.Length..].TrimEnd('/'));

    [GeneratedRegex("^[0-9]{1,15}$")]
    private static partial Regex NumericId();
}
