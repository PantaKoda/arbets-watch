using System.Text.RegularExpressions;

namespace ArbetsWatch.Core.Details;

/// <summary>
/// Validates links and addresses published in ads before they are offered as buttons. Only web pages
/// (http/https) and plain e-mail addresses pass; anything else (other schemes, embedded credentials, spaces)
/// is shown as text at most.
/// </summary>
public static partial class ExternalLinks
{
    /// <summary>
    /// A web link from ad text, or null. Scheme-less values such as <c>www.example.se/jobb</c> (seen in live
    /// ads) are read as https.
    /// </summary>
    public static Uri? WebLink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        if (value.Any(char.IsWhiteSpace))
        {
            return null;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            if (!value.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            value = "https://" + value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               uri.Host.Contains('.', StringComparison.Ordinal)
            ? uri
            : null;
    }

    /// <summary>A plain e-mail address, or null.</summary>
    public static string? Email(string? text) =>
        text?.Trim() is { Length: > 0 and <= 254 } value && EmailPattern().IsMatch(value) ? value : null;

    /// <summary>A <c>mailto:</c> link for a validated address, with an optional subject (e.g. the ad reference).</summary>
    public static Uri MailTo(string email, string? subject) =>
        new(subject is { Length: > 0 }
            ? $"mailto:{email}?subject={Uri.EscapeDataString(subject)}"
            : $"mailto:{email}");

    [GeneratedRegex(@"^[^@\s<>()\[\]\\,;:""]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$")]
    private static partial Regex EmailPattern();
}
