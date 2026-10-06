namespace ArbetsWatch.Core.Platform;

/// <summary>Where ArbetsWatch keeps its local data on each platform.</summary>
public sealed record AppPaths(string DataDirectory)
{
    public string DatabasePath => Path.Combine(DataDirectory, "arbetswatch.db");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Windows: <c>%LOCALAPPDATA%\ArbetsWatch</c>. macOS: <c>~/Library/Application Support/ArbetsWatch</c>.
    /// Linux: <c>$XDG_DATA_HOME/ArbetsWatch</c> or <c>~/.local/share/ArbetsWatch</c>.
    /// <c>ARBETSWATCH_DATA_DIR</c> overrides all of them (tests, portable use).
    /// </summary>
    public static AppPaths Resolve()
    {
        if (Environment.GetEnvironmentVariable("ARBETSWATCH_DATA_DIR") is { Length: > 0 } overridden)
        {
            return new AppPaths(Path.GetFullPath(overridden));
        }

        string root;
        if (OperatingSystem.IsWindows())
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else if (OperatingSystem.IsMacOS())
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        }
        else
        {
            root = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return new AppPaths(Path.Combine(root, "ArbetsWatch"));
    }
}
