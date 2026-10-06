using System.Diagnostics;
using ArbetsWatch.Core.Presentation;
using Microsoft.Extensions.Logging;

namespace ArbetsWatch.Desktop.Platform;

/// <summary>Opens Platsbanken ad pages (validated by <see cref="AdLinkPolicy"/>) in the system browser.</summary>
public sealed class BrowserLauncher(ILogger<BrowserLauncher> logger)
{
    /// <summary>Returns true when the browser was started.</summary>
    public bool Open(Uri url)
    {
        if (!AdLinkPolicy.IsAllowed(url))
        {
            logger.LogWarning("Refused to open a link that is not a Platsbanken ad page");
            return false;
        }

        try
        {
            // AbsoluteUri is percent-encoded, so it cannot inject shell arguments.
            var start = OperatingSystem.IsWindows() ? new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }
                : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", [url.AbsoluteUri])
                : new ProcessStartInfo("xdg-open", [url.AbsoluteUri]);
            using var process = Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not launch the system browser");
            return false;
        }
    }

    /// <summary>Opens a local folder in the file manager.</summary>
    public void OpenFolder(string path)
    {
        try
        {
            var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("explorer.exe", [path])
                : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", [path])
                : new ProcessStartInfo("xdg-open", [path]);
            using var process = Process.Start(start);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open the data folder");
        }
    }
}
