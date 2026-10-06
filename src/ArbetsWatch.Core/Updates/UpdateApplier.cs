using System.Globalization;
using ArbetsWatch.Core.Platform;

namespace ArbetsWatch.Core.Updates;

/// <summary>
/// The hand-over step of an update, run by the <em>new</em> copy from its staging folder after the old app has
/// quit: move the installed folder aside as "&lt;folder&gt;.previous", copy the new version in, and start it. If
/// anything fails, the previous folder is put back and started, so a failed update never leaves the user
/// without a working app. The previous version stays next to the install folder for a manual rollback until
/// the next update replaces it.
/// </summary>
public sealed class UpdateApplier(Func<int, TimeSpan, bool> waitForExit, IProcessLauncher launcher, Action<string> log, TimeSpan? retryDelay = null)
{
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Handles <c>--apply-update</c> before anything else starts. Returns false for a normal launch.</summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args is not [UpdateArguments.Apply, ..])
        {
            return false;
        }

        var logFile = Path.Combine(AppPaths.Resolve().LogDirectory, "update.log");
        void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
                File.AppendAllText(logFile, string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never stop the update.
            }
        }

        var target = Value(args, UpdateArguments.Target);
        var from = Value(args, UpdateArguments.From) ?? string.Empty;
        if (target is null || !int.TryParse(Value(args, UpdateArguments.WaitPid), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            Log("Update hand-over started with incomplete arguments; nothing was changed.");
            exitCode = 1;
            return true;
        }

        var applier = new UpdateApplier(WaitForExit, new ProcessLauncher(), Log);
        exitCode = applier.Apply(AppContext.BaseDirectory, target, pid, from);
        return true;
    }

    public int Apply(string stagedDirectory, string targetDirectory, int waitForPid, string fromVersion)
    {
        var staged = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagedDirectory));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var backup = target + ".previous";
        if (string.Equals(staged, target, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(staged, InstallInfo.ExecutableName))
            || staged.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            log($"Refusing to update '{target}' from '{staged}'.");
            if (waitForExit(waitForPid, ExitTimeout))
            {
                Restart(target, UpdateFailures.Refused); // the old app has quit for the update: bring it back
            }

            return 1;
        }

        if (!waitForExit(waitForPid, ExitTimeout))
        {
            // Start the installed version anyway: if the old process is still running, single-instance hands
            // over to it; if it quits later, the user isn't left without the app.
            log($"ArbetsWatch (process {waitForPid}) didn't quit; the update was not applied.");
            Restart(target, UpdateFailures.Timeout);
            return 2;
        }

        try
        {
            if (Directory.Exists(backup))
            {
                Retry(() => Directory.Delete(backup, recursive: true));
            }

            Retry(() => Directory.Move(target, backup)); // files can stay locked briefly after exit (antivirus, indexer)
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Couldn't move '{target}' aside ({ex.Message}); the update was not applied.");
            Restart(target, UpdateFailures.CouldNotMove);
            return 3;
        }

        try
        {
            CopyDirectory(staged, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Copying the new version failed ({ex.Message}); restoring the previous version.");
            return RestorePrevious(target, backup, UpdateFailures.CopyFailed) ? 4 : 5;
        }

        if (launcher.Start(Path.Combine(target, InstallInfo.ExecutableName), [UpdateArguments.UpdatedFrom, fromVersion]))
        {
            log($"Updated '{target}' from {fromVersion}; the previous version is in '{backup}'.");
            return 0;
        }

        log($"The new version in '{target}' couldn't be started; restoring the previous version.");
        return RestorePrevious(target, backup, UpdateFailures.StartFailed) ? 6 : 5;
    }

    /// <summary>Puts the previous version back and starts it; if that fails, starts it from the backup folder.</summary>
    private bool RestorePrevious(string target, string backup, string failure)
    {
        try
        {
            if (Directory.Exists(target))
            {
                Retry(() => Directory.Delete(target, recursive: true));
            }

            Retry(() => Directory.Move(backup, target));
        }
        catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
        {
            log($"Restoring failed too ({restore.Message}). The previous version is in '{backup}'.");
            Restart(backup, UpdateFailures.RestoreFailed);
            return false;
        }

        Restart(target, failure);
        return true;
    }

    /// <summary>Starts the previous version again and tells it why, so the user isn't silently offered the same update.</summary>
    private void Restart(string folder, string failure) =>
        launcher.Start(Path.Combine(folder, InstallInfo.ExecutableName), [UpdateArguments.UpdateFailed, failure]);

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool WaitForExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
    }

    private void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(_retryDelay);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }
}
