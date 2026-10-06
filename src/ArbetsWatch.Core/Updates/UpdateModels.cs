using System.Globalization;

namespace ArbetsWatch.Core.Updates;

/// <summary>A release version "major.minor.patch", optionally with a "-pre" suffix. Tags may start with "v".</summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, string? PreRelease = null) : IComparable<AppVersion>
{
    public bool IsPreRelease => PreRelease is not null;

    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        value = value.Split('+')[0]; // build metadata, e.g. "0.2.0+commit", doesn't order
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        var pre = dash >= 0 ? value[(dash + 1)..] : null;
        var parts = (dash >= 0 ? value[..dash] : value).Split('.');
        if (parts.Length != 3 || pre is { Length: 0 }
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new AppVersion(major, minor, patch, pre);
        return true;
    }

    /// <summary>Orders by number; a pre-release sorts before its release (0.2.0-rc1 &lt; 0.2.0).</summary>
    public int CompareTo(AppVersion other)
    {
        var byNumber = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (byNumber != 0)
        {
            return byNumber;
        }

        return (PreRelease, other.PreRelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(PreRelease, other.PreRelease),
        };
    }

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}") + (PreRelease is null ? string.Empty : "-" + PreRelease);
}

/// <summary>A downloadable file attached to a release.</summary>
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

/// <summary>
/// One GitHub release, as published. <see cref="Notes"/> is untrusted text written on GitHub: show it as
/// plain text only.
/// </summary>
public sealed record ReleaseInfo
{
    public required AppVersion Version { get; init; }

    public required string Tag { get; init; }

    public required string Title { get; init; }

    public string Notes { get; init; } = string.Empty;

    public required Uri HtmlUrl { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public bool IsDraft { get; init; }

    public bool IsPreRelease { get; init; }

    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];

    /// <summary>The portable Windows zip, named as scripts/publish-windows.ps1 names it.</summary>
    public ReleaseAsset? WindowsZip => Assets.FirstOrDefault(a => string.Equals(a.Name, ZipName(Version), StringComparison.OrdinalIgnoreCase));

    /// <summary>Its SHA-256 file ("hash  name").</summary>
    public ReleaseAsset? WindowsChecksum => Assets.FirstOrDefault(a => string.Equals(a.Name, ZipName(Version) + ".sha256", StringComparison.OrdinalIgnoreCase));

    public bool CanInstallOnWindows => WindowsZip is not null && WindowsChecksum is not null;

    public static string ZipName(AppVersion version) => $"ArbetsWatch-{version}-win-x64.zip";
}

public static class UpdatePolicy
{
    /// <summary>
    /// Published, stable releases newer than <paramref name="current"/>, newest first: everything the user
    /// would get by updating, so all of their notes are shown.
    /// </summary>
    public static IReadOnlyList<ReleaseInfo> Newer(IEnumerable<ReleaseInfo> releases, AppVersion current)
    {
        ArgumentNullException.ThrowIfNull(releases);
        return [.. releases
            .Where(r => !r.IsDraft && !r.IsPreRelease && !r.Version.IsPreRelease && r.Version > current)
            .GroupBy(r => r.Version)
            .Select(g => g.First())
            .OrderByDescending(r => r.Version)];
    }

    /// <summary>
    /// Reads a "*.sha256" file as written by the release script ("&lt;64 hex&gt;  &lt;file name&gt;"). Returns the
    /// lowercase hash only when the file names <paramref name="expectedFileName"/>.
    /// </summary>
    public static string? ParseChecksum(string content, string expectedFileName)
    {
        ArgumentNullException.ThrowIfNull(content);
        var parts = content.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit)
            || !string.Equals(parts[1].Trim().TrimStart('*'), expectedFileName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return parts[0].ToLowerInvariant();
    }
}
