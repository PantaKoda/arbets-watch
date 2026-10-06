using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ArbetsWatch.Core.Updates;

public enum UpdateStage
{
    /// <summary>Not checked yet in this session.</summary>
    Idle,
    Checking,
    UpToDate,
    Available,

    /// <summary>The last check failed; <see cref="UpdateService.Message"/> says why.</summary>
    CheckFailed,
    Downloading,
    Verifying,
    Installing,

    /// <summary>The new version took over; this app is quitting.</summary>
    Restarting,

    /// <summary>An install attempt failed and nothing was changed; <see cref="UpdateService.Message"/> says why.</summary>
    InstallFailed,
}

public enum UpdateCheckOutcome
{
    UpToDate,
    Available,
    Failed,
    Busy,
}

public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseInfo? Latest = null, string? Error = null);

public sealed record UpdateOptions
{
    /// <summary>How often to look for a new release. Zero or less: only when the user asks.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The first automatic check waits this long after startup.</summary>
    public TimeSpan FirstCheckDelay { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Finds and installs ArbetsWatch updates from the repository's public GitHub releases (as Repo Watch does).
/// <list type="bullet">
/// <item>Checks once a day and when the user asks; never downloads on its own.</item>
/// <item>Installing is always the user's choice. The zip is downloaded from the release, verified against the
/// release's SHA-256 file, unpacked into the data folder, and handed to the new copy, which replaces the install
/// folder after this app quits (see <see cref="UpdateApplier"/>).</item>
/// </list>
/// The checksum proves the download is the file the release published; it is not a code signature.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const long MaxDownloadBytes = 500L * 1024 * 1024;

    private readonly IReleaseSource _source;
    private readonly string _dataDirectory;
    private readonly IProcessLauncher _launcher;
    private readonly TimeProvider _time;
    private readonly UpdateOptions _options;
    private readonly ILogger<UpdateService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _state = new();
    private CancellationTokenSource? _install;

    public UpdateService(
        IReleaseSource source,
        Uri releasesPage,
        string dataDirectory,
        InstallInfo install,
        IProcessLauncher launcher,
        TimeProvider time,
        UpdateOptions options,
        ILogger<UpdateService> logger)
    {
        _source = source;
        ReleasesPage = releasesPage;
        _dataDirectory = dataDirectory;
        Install = install;
        _launcher = launcher;
        _time = time;
        _options = options;
        _logger = logger;
    }

    public InstallInfo Install { get; }

    public AppVersion Current => Install.Version;

    public Uri ReleasesPage { get; }

    public UpdateStage Stage { get; private set; }

    /// <summary>Why the last check or install failed, in words for the user.</summary>
    public string? Message { get; private set; }

    /// <summary>Download progress, 0 to 1.</summary>
    public double Progress { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>Releases newer than this copy, newest first (all of their notes describe the update).</summary>
    public IReadOnlyList<ReleaseInfo> Available { get; private set; } = [];

    public ReleaseInfo? Latest => Available.Count > 0 ? Available[0] : null;

    public bool IsUpdateAvailable => Latest is not null;

    public bool IsBusy => Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Verifying or UpdateStage.Installing or UpdateStage.Restarting;

    public bool CanCancelInstall => Stage is UpdateStage.Downloading or UpdateStage.Verifying;

    private string StagingRoot => Path.Combine(_dataDirectory, "updates");

    /// <summary>Raised when anything above changes. May be raised on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The new version is ready to take over: the app must quit now so it can replace the folder.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Why <see cref="InstallAsync"/> can't run, or null when it can.</summary>
    public string? CannotInstallReason
    {
        get
        {
            if (!Install.IsPortableRelease)
            {
                return "This copy of ArbetsWatch wasn't installed from a release zip (for example, it was built from source), so it can't replace itself. Download the update from GitHub instead.";
            }

            if (IsSameOrInside(_dataDirectory, Install.InstallDirectory))
            {
                return $"ArbetsWatch keeps its data inside its own folder ({_dataDirectory}), so it can't replace that folder. Move ArbetsWatch to a folder of its own (for example %LOCALAPPDATA%\\Programs\\ArbetsWatch), or download the update from GitHub.";
            }

            if (Latest is not { } latest)
            {
                return "There is no update to install.";
            }

            if (!latest.CanInstallOnWindows)
            {
                return $"Release {latest.Version} has no Windows download.";
            }

            return CanWrite(Install.InstallDirectory) && CanWrite(Path.GetDirectoryName(Install.InstallDirectory))
                ? null
                : $"ArbetsWatch can't change its folder ({Install.InstallDirectory}). Download the update from GitHub and replace the folder yourself.";
        }
    }

    /// <summary>Starts the daily check (the first one shortly after startup) and tidies up after an update.</summary>
    public void Start() => _ = RunAsync(_stop.Token);

    public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new(UpdateCheckOutcome.Busy, Latest);
        }

        try
        {
            Set(UpdateStage.Checking, null);
            var result = await _source.GetReleasesAsync(cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Checking for updates failed: {Error}", result.Error);
                Set(UpdateStage.CheckFailed, result.Error);
                return new(UpdateCheckOutcome.Failed, Latest, result.Error);
            }

            var newer = UpdatePolicy.Newer(result.Releases!, Current);
            lock (_state)
            {
                Available = newer;
                LastChecked = _time.GetUtcNow();
            }

            Set(newer.Count > 0 ? UpdateStage.Available : UpdateStage.UpToDate, null);
            _logger.LogInformation("Checked for updates: current {Current}, latest {Latest}", Current, Latest?.Version.ToString() ?? "none newer");
            return new(newer.Count > 0 ? UpdateCheckOutcome.Available : UpdateCheckOutcome.UpToDate, Latest);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Downloads, verifies and stages the latest release, then hands over to it and asks the app to quit.
    /// Any failure leaves the installed app untouched and is reported in <see cref="Message"/>.
    /// </summary>
    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        if (CannotInstallReason is { } reason)
        {
            Set(UpdateStage.InstallFailed, reason);
            return;
        }

        if (!await _gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            return;
        }

        // Cancelled by the user (Cancel), by quitting, or by the caller.
        using var install = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        lock (_state)
        {
            _install = install;
        }

        var token = install.Token;
        var release = Latest!;
        var zipName = release.WindowsZip!.Name;
        var root = StagingRoot;
        try
        {
            Progress = 0;
            Set(UpdateStage.Downloading, null);
            DeleteQuietly(root);
            Directory.CreateDirectory(root);

            using var checksumBuffer = new MemoryStream();
            await _source.DownloadAsync(release.WindowsChecksum!.DownloadUrl, checksumBuffer, 4096, null, token).ConfigureAwait(false);
            var expected = UpdatePolicy.ParseChecksum(Encoding.UTF8.GetString(checksumBuffer.ToArray()), zipName)
                ?? throw new InvalidDataException("the release's checksum file is not in the expected format");

            var zipPath = Path.Combine(root, zipName);
            var progress = new ThrottledProgress(this);
            var file = File.Create(zipPath);
            await using (file.ConfigureAwait(false))
            {
                await _source.DownloadAsync(release.WindowsZip.DownloadUrl, file, MaxDownloadBytes, progress, token).ConfigureAwait(false);
            }

            Set(UpdateStage.Verifying, null);
            string actual;
            var read = File.OpenRead(zipPath);
            await using (read.ConfigureAwait(false))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(read, token).ConfigureAwait(false));
            }

            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                DeleteQuietly(root);
                Set(UpdateStage.InstallFailed, "The download doesn't match the checksum published with the release, so it wasn't installed. Nothing was changed.");
                _logger.LogWarning("Update {Version}: checksum mismatch", release.Version);
                return;
            }

            Set(UpdateStage.Installing, null);
            var unpacked = Path.Combine(root, release.Version.ToString());
            ZipFile.ExtractToDirectory(zipPath, unpacked); // rejects entries that would land outside the folder
            var staged = Path.Combine(unpacked, "ArbetsWatch");
            var executable = Path.Combine(staged, InstallInfo.ExecutableName);
            if (!File.Exists(executable) || InstallInfo.ReadManifestVersion(staged) != release.Version)
            {
                throw new InvalidDataException($"the download doesn't contain ArbetsWatch {release.Version}");
            }

            string[] arguments =
            [
                UpdateArguments.Apply,
                UpdateArguments.Target, Install.InstallDirectory,
                UpdateArguments.WaitPid, Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                UpdateArguments.From, Current.ToString(),
            ];
            if (!_launcher.Start(executable, arguments))
            {
                throw new IOException("the new version couldn't be started");
            }

            _logger.LogInformation("Update {Version} staged and handed over; quitting so it can replace {Directory}", release.Version, Install.InstallDirectory);
            Set(UpdateStage.Restarting, null);
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogInformation("Installing update {Version} was cancelled", release.Version);
            DeleteQuietly(root);
            Set(UpdateStage.InstallFailed, "The update was cancelled. Nothing was changed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Installing update {Version} failed", release.Version);
            DeleteQuietly(root);
            Set(UpdateStage.InstallFailed, $"Couldn't install the update ({ex.Message}). Nothing was changed.");
        }
        finally
        {
            lock (_state)
            {
                _install = null;
            }

            _gate.Release();
        }
    }

    /// <summary>Stops a download in progress (the Cancel button). Once the new version has taken over, it's too late.</summary>
    public void CancelInstall()
    {
        lock (_state)
        {
            if (CanCancelInstall)
            {
                _install?.Cancel();
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.FirstCheckDelay, _time, cancellationToken).ConfigureAwait(false);
            if (Install.UpdatedFrom is not null && Stage is UpdateStage.Idle)
            {
                DeleteQuietly(StagingRoot); // the hand-over copy has finished by now
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckNowAsync(cancellationToken).ConfigureAwait(false);
                if (_options.CheckInterval <= TimeSpan.Zero)
                {
                    return;
                }

                await Task.Delay(_options.CheckInterval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Quitting.
        }
        catch (ObjectDisposedException)
        {
            // Quitting.
        }
    }

    private void Set(UpdateStage stage, string? message)
    {
        lock (_state)
        {
            Stage = stage;
            Message = message;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        var inner = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var outer = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return string.Equals(inner, outer, StringComparison.OrdinalIgnoreCase)
            || inner.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanWrite(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        try
        {
            var probe = Path.Combine(directory, $".arbetswatch-write-test-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Couldn't remove {Directory}", directory);
        }
    }

    /// <summary>Reports download progress in whole percent only, so the UI isn't flooded.</summary>
    private sealed class ThrottledProgress(UpdateService owner) : IProgress<double>
    {
        private int _percent = -1;

        public void Report(double value)
        {
            var percent = (int)Math.Clamp(value * 100, 0, 100);
            if (Interlocked.Exchange(ref _percent, percent) != percent)
            {
                owner.Progress = percent / 100.0;
                owner.Changed?.Invoke(owner, EventArgs.Empty);
            }
        }
    }
}
