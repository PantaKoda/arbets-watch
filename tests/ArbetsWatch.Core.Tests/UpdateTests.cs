using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ArbetsWatch.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArbetsWatch.Core.Tests;

public sealed class UpdateModelTests
{
    [Theory]
    [InlineData("v0.1.0", 0, 1, 0, null)]
    [InlineData("1.12.3", 1, 12, 3, null)]
    [InlineData("0.2.0-rc1", 0, 2, 0, "rc1")]
    [InlineData("0.1.0+663509f", 0, 1, 0, null)]
    public void Versions_parse(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(AppVersion.TryParse(text, out var v));
        Assert.Equal(new AppVersion(major, minor, patch, pre), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nightly")]
    [InlineData("1.2")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    public void Invalid_versions_are_rejected(string? text) => Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void Prereleases_sort_before_their_release()
    {
        Assert.True(V("0.2.0-rc1") < V("0.2.0"));
        Assert.True(V("0.1.9") < V("0.2.0-rc1"));
        Assert.True(V("0.10.0") > V("0.9.9"));
    }

    [Fact]
    public void Only_newer_published_stable_releases_count_newest_first()
    {
        var releases = new[]
        {
            Release("0.1.0"), Release("0.1.1"), Release("0.2.0"), Release("0.3.0", draft: true),
            Release("0.3.0-rc1"), Release("0.2.5", pre: true), Release("0.1.1"),
        };

        var newer = UpdatePolicy.Newer(releases, V("0.1.0"));

        Assert.Equal(["0.2.0", "0.1.1"], newer.Select(r => r.Version.ToString()));
    }

    [Fact]
    public void Checksum_file_must_name_the_zip()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, UpdatePolicy.ParseChecksum($"{hash.ToUpperInvariant()}  ArbetsWatch-0.1.1-win-x64.zip\n", "ArbetsWatch-0.1.1-win-x64.zip"));
        Assert.Equal(hash, UpdatePolicy.ParseChecksum($"{hash} *ArbetsWatch-0.1.1-win-x64.zip", "ArbetsWatch-0.1.1-win-x64.zip"));
        Assert.Null(UpdatePolicy.ParseChecksum($"{hash}  Other.zip", "ArbetsWatch-0.1.1-win-x64.zip"));
        Assert.Null(UpdatePolicy.ParseChecksum("abc  ArbetsWatch-0.1.1-win-x64.zip", "ArbetsWatch-0.1.1-win-x64.zip"));
        Assert.Null(UpdatePolicy.ParseChecksum(new string('z', 64) + "  ArbetsWatch-0.1.1-win-x64.zip", "ArbetsWatch-0.1.1-win-x64.zip"));
    }

    [Fact]
    public void Release_notes_are_shown_as_plain_text()
    {
        var text = ReleaseNotesText.ToPlainText("## Changes\n- **Faster** start\n- See [docs](https://example.com) and `code`");
        Assert.Equal("Changes\n• Faster start\n• See docs and code", text);
        Assert.Equal("No notes were published for this release.", ReleaseNotesText.ToPlainText("  "));
    }

    internal static AppVersion V(string text) => AppVersion.TryParse(text, out var v) ? v : throw new ArgumentException(text);

    internal static ReleaseInfo Release(string version, bool draft = false, bool pre = false, string repository = ReleaseClient.DefaultRepository)
    {
        var v = V(version);
        var zip = ReleaseInfo.ZipName(v);
        var download = $"https://github.com/{repository}/releases/download/v{v}/";
        return new ReleaseInfo
        {
            Version = v,
            Tag = "v" + v,
            Title = "ArbetsWatch " + v,
            Notes = "- Something changed",
            HtmlUrl = new Uri($"https://github.com/{repository}/releases/tag/v{v}"),
            IsDraft = draft,
            IsPreRelease = pre,
            Assets = [new ReleaseAsset(zip, new Uri(download + zip), 1), new ReleaseAsset(zip + ".sha256", new Uri(download + zip + ".sha256"), 1)],
        };
    }
}

public sealed class ReleaseClientTests
{
    [Fact]
    public async Task Releases_are_read_anonymously_and_mapped()
    {
        HttpRequestMessage? seen = null;
        var json = """
            [
              {"tag_name":"v0.1.1","name":"","body":"Notes","html_url":"https://github.com/PantaKoda/arbets-watch/releases/tag/v0.1.1","draft":false,"prerelease":false,
               "published_at":"2026-10-07T10:00:00Z","assets":[{"name":"ArbetsWatch-0.1.1-win-x64.zip","browser_download_url":"https://github.com/PantaKoda/arbets-watch/releases/download/v0.1.1/ArbetsWatch-0.1.1-win-x64.zip","size":10},
                                                          {"name":"ArbetsWatch-0.1.1-win-x64.zip.sha256","browser_download_url":"https://github.com/PantaKoda/arbets-watch/releases/download/v0.1.1/ArbetsWatch-0.1.1-win-x64.zip.sha256","size":1}]},
              {"tag_name":"nightly","html_url":"https://github.com/x","draft":false,"prerelease":false}
            ]
            """;
        var client = new ReleaseClient(new HttpClient(new Stub(r => { seen = r; return Json(json); })), ReleaseClient.DefaultRepository);

        var result = await client.GetReleasesAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        var release = Assert.Single(result.Releases!);
        Assert.Equal("ArbetsWatch 0.1.1", release.Title);
        Assert.True(release.CanInstallOnWindows);
        Assert.Equal("https://api.github.com/repos/PantaKoda/arbets-watch/releases?per_page=30", seen!.RequestUri!.ToString());
        Assert.Null(seen.Headers.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "no public releases")]
    [InlineData(HttpStatusCode.Forbidden, "limit")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Failures_are_explained(HttpStatusCode status, string expected)
    {
        var client = new ReleaseClient(new HttpClient(new Stub(_ => new HttpResponseMessage(status))), ReleaseClient.DefaultRepository);
        var result = await client.GetReleasesAsync(CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://github.com/PantaKoda/arbets-watch/releases/download/v0.1.1/ArbetsWatch-0.1.1-win-x64.zip", true)]
    [InlineData("http://github.com/PantaKoda/arbets-watch/releases/download/v0.1.1/a.zip", false)]
    [InlineData("https://github.com/Someone/arbets-watch/releases/download/v0.1.1/a.zip", false)]
    [InlineData("https://github.com.evil.example/PantaKoda/arbets-watch/releases/download/v0.1.1/a.zip", false)]
    [InlineData("https://user@github.com/PantaKoda/arbets-watch/releases/download/v0.1.1/a.zip", false)]
    [InlineData("https://github.com/PantaKoda/arbets-watch/archive/main.zip", false)]
    public void Downloads_are_limited_to_this_repository_releases(string url, bool allowed) =>
        Assert.Equal(allowed, new ReleaseClient(new HttpClient(), ReleaseClient.DefaultRepository).IsReleaseDownload(new Uri(url)));

    [Fact]
    public async Task Oversized_downloads_are_refused()
    {
        var client = new ReleaseClient(new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100]) })), ReleaseClient.DefaultRepository);
        using var target = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadAsync(
            new Uri("https://github.com/PantaKoda/arbets-watch/releases/download/v0.1.1/x.zip"), target, 10, null, CancellationToken.None));
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}

/// <summary>The install flow end to end with a real zip, a fake release source and a fake launcher.</summary>
public sealed class UpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arbetswatch-update-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingLauncher _launcher = new();

    public UpdateServiceTests()
    {
        InstallDirectory = Path.Combine(_root, "Programs", "ArbetsWatch");
        DataDirectory = Path.Combine(_root, "Data");
        Directory.CreateDirectory(InstallDirectory);
        Directory.CreateDirectory(DataDirectory);
    }

    private string InstallDirectory { get; }

    private string DataDirectory { get; }

    [Fact]
    public async Task Install_downloads_verifies_stages_and_hands_over()
    {
        var source = new FakeSource(ReleaseZip("0.1.1"));
        using var service = Service(source);
        var exitRequested = false;
        service.ExitRequested += (_, _) => exitRequested = true;

        Assert.Equal(UpdateCheckOutcome.Available, (await service.CheckNowAsync()).Outcome);
        Assert.Null(service.CannotInstallReason);
        await service.InstallAsync();

        Assert.Equal(UpdateStage.Restarting, service.Stage);
        Assert.True(exitRequested);
        var (exe, args) = Assert.Single(_launcher.Started);
        Assert.Equal(Path.Combine(DataDirectory, "updates", "0.1.1", "ArbetsWatch", "ArbetsWatch.exe"), exe);
        Assert.Equal([UpdateArguments.Apply, UpdateArguments.Target, InstallDirectory, UpdateArguments.WaitPid,
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), UpdateArguments.From, "0.1.0"], args);
    }

    [Fact]
    public async Task Checksum_mismatch_installs_nothing()
    {
        var source = new FakeSource(ReleaseZip("0.1.1")) { Checksum = new string('0', 64) };
        using var service = Service(source);
        await service.CheckNowAsync();

        await service.InstallAsync();

        Assert.Equal(UpdateStage.InstallFailed, service.Stage);
        Assert.Contains("checksum", service.Message, StringComparison.Ordinal);
        Assert.Empty(_launcher.Started);
        Assert.False(Directory.Exists(Path.Combine(DataDirectory, "updates")));
    }

    [Fact]
    public async Task Zip_with_another_version_installs_nothing()
    {
        var source = new FakeSource(ReleaseZip("0.1.2")) { Version = "0.1.1" };
        using var service = Service(source);
        await service.CheckNowAsync();

        await service.InstallAsync();

        Assert.Equal(UpdateStage.InstallFailed, service.Stage);
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public async Task Builds_from_source_and_data_inside_the_app_folder_cannot_self_update()
    {
        using (var fromSource = Service(new FakeSource(ReleaseZip("0.1.1")), portable: false))
        {
            await fromSource.CheckNowAsync();
            Assert.Contains("wasn't installed from a release zip", fromSource.CannotInstallReason, StringComparison.Ordinal);
        }

        using var inside = Service(new FakeSource(ReleaseZip("0.1.1")), dataDirectory: InstallDirectory);
        await inside.CheckNowAsync();
        Assert.Contains("inside its own folder", inside.CannotInstallReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_can_be_called_twice()
    {
        var service = Service(new FakeSource(ReleaseZip("0.1.1")));
        service.Dispose();
        service.Dispose();
    }

    [Fact]
    public async Task Up_to_date_when_nothing_newer()
    {
        using var service = Service(new FakeSource(ReleaseZip("0.1.0")) { Version = "0.1.0" });
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await service.CheckNowAsync()).Outcome);
        Assert.False(service.IsUpdateAvailable);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private UpdateService Service(FakeSource source, bool portable = true, string? dataDirectory = null) =>
        new(source, new Uri("https://github.com/PantaKoda/arbets-watch/releases"), dataDirectory ?? DataDirectory,
            new InstallInfo(UpdateModelTests.V("0.1.0"), InstallDirectory, portable), _launcher, TimeProvider.System,
            new UpdateOptions(), NullLogger<UpdateService>.Instance);

    /// <summary>A zip shaped like the release script's: ArbetsWatch/ArbetsWatch.exe and release.json.</summary>
    private static byte[] ReleaseZip(string version)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var exe = new StreamWriter(zip.CreateEntry("ArbetsWatch/ArbetsWatch.exe").Open()))
            {
                exe.Write("not really an exe");
            }

            using var manifest = new StreamWriter(zip.CreateEntry("ArbetsWatch/release.json").Open());
            manifest.Write($$"""{"version":"{{version}}","files":["ArbetsWatch.exe","release.json"]}""");
        }

        return buffer.ToArray();
    }

    private sealed class FakeSource(byte[] zip) : IReleaseSource
    {
        public string Version { get; init; } = "0.1.1";

        public string? Checksum { get; init; }

        public Task<ReleaseListResult> GetReleasesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ReleaseListResult([UpdateModelTests.Release(Version)], null));

        public async Task DownloadAsync(Uri url, Stream destination, long maxBytes, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            var name = ReleaseInfo.ZipName(UpdateModelTests.V(Version));
            var bytes = url.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes($"{Checksum ?? Convert.ToHexStringLower(SHA256.HashData(zip))}  {name}")
                : zip;
            await destination.WriteAsync(bytes, cancellationToken);
            progress?.Report(1);
        }
    }
}

public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arbetswatch-applier-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingLauncher _launcher = new();
    private readonly List<string> _log = [];

    public UpdateApplierTests()
    {
        Target = Path.Combine(_root, "ArbetsWatch");
        Staged = Path.Combine(_root, "Data", "updates", "0.1.1", "ArbetsWatch");
        Write(Target, "ArbetsWatch.exe", "old");
        Write(Target, "old-only.dll", "old");
        Write(Staged, "ArbetsWatch.exe", "new");
        Write(Staged, Path.Combine("sub", "new.dll"), "new");
    }

    private string Target { get; }

    private string Staged { get; }

    [Fact]
    public void Swaps_the_folder_keeps_a_backup_and_starts_the_new_version()
    {
        var code = Applier(exited: true).Apply(Staged, Target, 1234, "0.1.0");

        Assert.Equal(0, code);
        Assert.Equal("new", File.ReadAllText(Path.Combine(Target, "ArbetsWatch.exe")));
        Assert.True(File.Exists(Path.Combine(Target, "sub", "new.dll")));
        Assert.False(File.Exists(Path.Combine(Target, "old-only.dll")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Target + ".previous", "ArbetsWatch.exe")));
        var (exe, args) = Assert.Single(_launcher.Started);
        Assert.Equal(Path.Combine(Target, "ArbetsWatch.exe"), exe);
        Assert.Equal([UpdateArguments.UpdatedFrom, "0.1.0"], args);
    }

    [Fact]
    public void A_second_update_replaces_the_previous_backup()
    {
        Write(Target + ".previous", "ArbetsWatch.exe", "older");
        Assert.Equal(0, Applier(exited: true).Apply(Staged, Target, 1234, "0.1.0"));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Target + ".previous", "ArbetsWatch.exe")));
    }

    [Fact]
    public void When_the_old_app_does_not_quit_nothing_changes_and_the_installed_version_is_started()
    {
        Assert.Equal(2, Applier(exited: false).Apply(Staged, Target, 1234, "0.1.0"));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Target, "ArbetsWatch.exe")));
        var (exe, args) = Assert.Single(_launcher.Started);
        Assert.Equal(Path.Combine(Target, "ArbetsWatch.exe"), exe);
        Assert.Equal([UpdateArguments.UpdateFailed, UpdateFailures.Timeout], args);
    }

    [Fact]
    public void When_the_new_version_cannot_start_the_previous_one_is_put_back()
    {
        var launcher = new RecordingLauncher { Fail = exe => File.ReadAllText(exe) == "new" };
        var code = new UpdateApplier((_, _) => true, launcher, _log.Add, TimeSpan.FromMilliseconds(10)).Apply(Staged, Target, 1234, "0.1.0");

        Assert.Equal(6, code);
        Assert.Equal("old", File.ReadAllText(Path.Combine(Target, "ArbetsWatch.exe")));
        Assert.False(Directory.Exists(Target + ".previous"));
        Assert.Equal([UpdateArguments.UpdateFailed, UpdateFailures.StartFailed], launcher.Started[^1].Arguments);
    }

    [Fact]
    public void Copy_failure_restores_the_previous_version()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Relies on Windows file sharing locks.");
        using (new FileStream(Path.Combine(Staged, "sub", "new.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(4, Applier(exited: true).Apply(Staged, Target, 1234, "0.1.0"));
        }

        Assert.Equal("old", File.ReadAllText(Path.Combine(Target, "ArbetsWatch.exe")));
        Assert.True(File.Exists(Path.Combine(Target, "old-only.dll")));
        Assert.False(Directory.Exists(Target + ".previous"));
        Assert.Equal([UpdateArguments.UpdateFailed, UpdateFailures.CopyFailed], Assert.Single(_launcher.Started).Arguments);
    }

    [Fact]
    public void Locked_install_folder_is_left_untouched()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Relies on Windows file sharing locks.");
        using (new FileStream(Path.Combine(Target, "old-only.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(3, Applier(exited: true).Apply(Staged, Target, 1234, "0.1.0"));
        }

        Assert.Equal("old", File.ReadAllText(Path.Combine(Target, "ArbetsWatch.exe")));
        Assert.Equal([UpdateArguments.UpdateFailed, UpdateFailures.CouldNotMove], Assert.Single(_launcher.Started).Arguments);
    }

    [Fact]
    public void Refuses_a_staged_copy_inside_the_target_and_restarts_the_old_version()
    {
        var inside = Path.Combine(Target, "nested");
        Write(inside, "ArbetsWatch.exe", "new");

        Assert.Equal(1, Applier(exited: true).Apply(inside, Target, 1234, "0.1.0"));

        Assert.Equal("old", File.ReadAllText(Path.Combine(Target, "ArbetsWatch.exe")));
        var (_, args) = Assert.Single(_launcher.Started);
        Assert.Equal([UpdateArguments.UpdateFailed, UpdateFailures.Refused], args);
    }

    [Fact]
    public void Failure_codes_become_messages()
    {
        Assert.Contains("still on version 0.1.0", InstallInfo.FailureMessage(UpdateFailures.CopyFailed, UpdateModelTests.V("0.1.0")), StringComparison.Ordinal);
        Assert.Null(InstallInfo.FailureMessage(null, UpdateModelTests.V("0.1.0")));
    }

    [Fact]
    public void Only_a_folder_with_a_matching_manifest_is_a_portable_release()
    {
        File.WriteAllText(Path.Combine(Target, InstallInfo.ManifestFile), """{"version":"0.1.0"}""");
        var info = InstallInfo.Detect([UpdateArguments.UpdatedFrom, "0.0.9"], "0.1.0+abc", Target);
        Assert.Equal(OperatingSystem.IsWindows(), info.IsPortableRelease);
        Assert.Equal("0.0.9", info.UpdatedFrom);
        Assert.False(InstallInfo.Detect([], "0.2.0", Target).IsPortableRelease);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private UpdateApplier Applier(bool exited) => new((_, _) => exited, _launcher, _log.Add, TimeSpan.FromMilliseconds(10));

    private static void Write(string directory, string relative, string content)
    {
        var path = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}

internal sealed class RecordingLauncher : IProcessLauncher
{
    public List<(string Executable, IReadOnlyList<string> Arguments)> Started { get; } = [];

    /// <summary>When it returns true for an executable, starting it fails.</summary>
    public Func<string, bool> Fail { get; init; } = _ => false;

    public bool Start(string executable, IReadOnlyList<string> arguments)
    {
        Started.Add((executable, [.. arguments]));
        return !Fail(executable);
    }
}
