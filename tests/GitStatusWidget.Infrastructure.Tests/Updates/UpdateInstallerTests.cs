using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using GitStatusWidget.Application.Updates;
using GitStatusWidget.Infrastructure.Providers.Http;
using GitStatusWidget.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace GitStatusWidget.Infrastructure.Tests.Updates;

/// <summary>Issue #19: in-place update against a temporary install folder and a local download server.</summary>
public sealed class UpdateInstallerTests : IDisposable
{
    private const string Exe = "GitWidget.exe";
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly HttpClient _http = new(ProviderHttpClient.CreateHandler());
    private readonly string _root = Directory.CreateTempSubdirectory("gitwidget-update-test-").FullName;
    private readonly List<ProcessStartInfo> _started = [];
    private bool _startSucceeds = true;

    public UpdateInstallerTests()
    {
        Directory.CreateDirectory(InstallDirectory);
        Directory.CreateDirectory(TempDirectory);
        File.WriteAllText(Path.Combine(InstallDirectory, Exe), "old version");
        File.WriteAllText(Path.Combine(InstallDirectory, "notes.txt"), "not part of GitWidget");
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Stop();
        _server.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string InstallDirectory => Path.Combine(_root, "install");

    private string TempDirectory => Path.Combine(_root, "temp");

    private string Installed(string name = Exe) => File.ReadAllText(Path.Combine(InstallDirectory, name));

    private UpdateInstaller Installer(bool supported = true, bool allowServer = true) => new(
        new SingleClientFactory(_http),
        NullLogger<UpdateInstaller>.Instance,
        new UpdateInstallerOptions
        {
            InstallDirectory = InstallDirectory,
            TempDirectory = TempDirectory,
            IsSupportedPlatform = supported,
            IsAllowedSource = uri => allowServer && uri.Host == "localhost" && uri.AbsolutePath != "/forbidden",
            StartProcess = start =>
            {
                _started.Add(start);
                return _startSucceeds;
            },
        });

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Serves the archive behind a redirect, as GitHub does, and returns the matching release.</summary>
    private ReleaseInfo Serve(byte[] archive, string? sha256 = null, string redirectTo = "/files/GitWidget-win-x64.zip")
    {
        _server.Given(Request.Create().WithPath("/download").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", redirectTo));
        _server.Given(Request.Create().WithPath("/files/GitWidget-win-x64.zip").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(archive));
        SemanticVersion.TryParse("0.2.0", out var version).Should().BeTrue();
        return new ReleaseInfo(version, "v0.2.0", new Uri("https://github.com/chwichmann/GitWidget/releases/tag/v0.2.0"), false, null)
        {
            Asset = new ReleaseAsset("GitWidget-win-x64.zip", new Uri($"{_server.Url}/download"), archive.Length,
                sha256 ?? Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant()),
        };
    }

    [Fact]
    public async Task Release_is_downloaded_verified_and_swapped_in_with_a_backup()
    {
        var release = Serve(Zip((Exe, "new version"), ("lib/native.dll", "library")));
        using var installer = Installer();
        var states = new List<UpdateInstallStatus>();
        installer.StateChanged += (_, s) => states.Add(s.Status);

        installer.WhyNotInstallable(release).Should().BeNull();
        var installed = await installer.InstallAsync(release, CancellationToken.None);

        installed.Should().BeTrue();
        Installed().Should().Be("new version");
        Installed(Path.Combine("lib", "native.dll")).Should().Be("library");
        Installed(Exe + UpdateInstaller.BackupSuffix).Should().Be("old version", "the previous version is kept until the new one has started");
        Installed("notes.txt").Should().Be("not part of GitWidget");
        states.Distinct().Should().Equal(UpdateInstallStatus.Downloading, UpdateInstallStatus.Verifying, UpdateInstallStatus.Installing, UpdateInstallStatus.ReadyToRestart);
        Directory.EnumerateFileSystemEntries(TempDirectory).Should().BeEmpty("the download is cleaned up");
    }

    [Fact]
    public async Task Restart_starts_the_new_version_waiting_for_this_process_and_the_new_version_removes_the_backup()
    {
        var release = Serve(Zip((Exe, "new version")));
        using var installer = Installer();
        installer.Restart().Should().BeFalse("nothing is installed yet");
        await installer.InstallAsync(release, CancellationToken.None);

        installer.Restart().Should().BeTrue();

        var start = _started.Should().ContainSingle().Subject;
        start.FileName.Should().Be(Path.Combine(InstallDirectory, Exe));
        start.ArgumentList.Should().Equal(UpdateInstaller.WaitForExitArgument, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // What the started version does first (the given process id does not exist, so it does not wait).
        UpdateServiceCollectionExtensions.CompletePendingUpdate([UpdateInstaller.WaitForExitArgument, "999999999"], InstallDirectory);

        File.Exists(Path.Combine(InstallDirectory, Exe + UpdateInstaller.BackupSuffix)).Should().BeFalse();
        Installed().Should().Be("new version");
        Installed("notes.txt").Should().Be("not part of GitWidget");
    }

    [Fact]
    public async Task Previous_version_is_restored_when_the_new_one_cannot_be_started()
    {
        // For example an application-control policy that blocks the freshly downloaded exe.
        var release = Serve(Zip((Exe, "new version"), ("extra.dll", "new file")));
        using var installer = Installer();
        await installer.InstallAsync(release, CancellationToken.None);
        _startSucceeds = false;

        installer.Restart().Should().BeFalse();

        Installed().Should().Be("old version");
        File.Exists(Path.Combine(InstallDirectory, "extra.dll")).Should().BeFalse();
        Directory.EnumerateFiles(InstallDirectory, "*" + UpdateInstaller.BackupSuffix).Should().BeEmpty();
        installer.State.Status.Should().Be(UpdateInstallStatus.Failed);
        installer.State.Message.Should().Contain("previous version was restored");
    }

    [Fact]
    public async Task Download_that_does_not_match_the_checksum_changes_nothing()
    {
        var release = Serve(Zip((Exe, "tampered")), sha256: new string('a', 64));
        using var installer = Installer();

        var installed = await installer.InstallAsync(release, CancellationToken.None);

        installed.Should().BeFalse();
        installer.State.Status.Should().Be(UpdateInstallStatus.Failed);
        installer.State.Message.Should().Contain("checksum");
        Installed().Should().Be("old version");
        Directory.EnumerateFiles(InstallDirectory).Should().HaveCount(2);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("sub/../../evil.txt")]
    public async Task Archive_entries_outside_the_install_folder_are_rejected(string entry)
    {
        var release = Serve(Zip((Exe, "new version"), (entry, "escaped")));
        using var installer = Installer();

        var installed = await installer.InstallAsync(release, CancellationToken.None);

        installed.Should().BeFalse();
        installer.State.Message.Should().Contain("unexpected path");
        Installed().Should().Be("old version");
        Directory.EnumerateFiles(_root, "evil.txt", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Archive_without_the_executable_and_redirects_to_other_addresses_are_rejected()
    {
        using var installer = Installer();

        (await installer.InstallAsync(Serve(Zip(("readme.txt", "no exe"))), CancellationToken.None)).Should().BeFalse();
        installer.State.Message.Should().Contain($"does not contain {Exe}");

        _server.Reset();
        (await installer.InstallAsync(Serve(Zip((Exe, "new version")), redirectTo: "/forbidden"), CancellationToken.None)).Should().BeFalse();
        installer.State.Message.Should().Contain("not accepted");
        Installed().Should().Be("old version");
    }

    [Fact]
    public async Task Cancelling_the_download_changes_nothing_and_is_no_failure()
    {
        var release = Serve(Zip((Exe, "new version")));
        using var installer = Installer();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var installed = await installer.InstallAsync(release, cancelled.Token);

        installed.Should().BeFalse();
        installer.State.Status.Should().Be(UpdateInstallStatus.Idle);
        Installed().Should().Be("old version");
    }

    [Fact]
    public void Reasons_why_a_release_cannot_be_installed_in_place()
    {
        var release = Serve(Zip((Exe, "new version")));

        using (var installer = Installer(supported: false))
        {
            installer.WhyNotInstallable(release).Should().Contain("Windows only");
        }

        using (var installer = Installer(allowServer: false))
        {
            installer.WhyNotInstallable(release).Should().Contain("not accepted");
        }

        using (var installer = Installer())
        {
            installer.WhyNotInstallable(release with { Asset = null }).Should().Contain("no download for this system");
            installer.WhyNotInstallable(release with { Asset = release.Asset! with { Sha256 = null } }).Should().Contain("no checksum");

            File.WriteAllText(Path.Combine(InstallDirectory, "GitWidget.dll"), "managed assembly of a development build");
            installer.WhyNotInstallable(release).Should().Contain("development build");
            File.Delete(Path.Combine(InstallDirectory, "GitWidget.dll"));

            File.Delete(Path.Combine(InstallDirectory, Exe));
            installer.WhyNotInstallable(release).Should().Contain("was not found");
        }
    }

    [Fact]
    public void Default_source_check_accepts_only_https_downloads_from_github()
    {
        var allowed = new UpdateInstallerOptions().IsAllowedSource;

        allowed(new Uri("https://github.com/chwichmann/GitWidget/releases/download/v0.2.0/GitWidget-win-x64.zip")).Should().BeTrue();
        allowed(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/2")).Should().BeTrue();
        allowed(new Uri("http://github.com/chwichmann/GitWidget/releases/download/v0.2.0/GitWidget-win-x64.zip")).Should().BeFalse();
        allowed(new Uri("https://github.com.example.org/file.zip")).Should().BeFalse();
        allowed(new Uri("https://evilgithubusercontent.com/file.zip")).Should().BeFalse();
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
