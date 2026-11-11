using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using GitStatusWidget.Application.Updates;
using GitStatusWidget.Infrastructure.Providers;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Infrastructure.Updates;

/// <summary>Where and how an update is installed (overridable for tests).</summary>
public sealed class UpdateInstallerOptions
{
    /// <summary>Folder GitWidget runs from.</summary>
    public string InstallDirectory { get; init; } = AppContext.BaseDirectory;

    /// <summary>Name of the executable inside <see cref="InstallDirectory"/> and inside the release archive.</summary>
    public string ExecutableName { get; init; } = "GitWidget.exe";

    /// <summary>In-place updates are implemented for Windows (zip archive, a running exe can be renamed).</summary>
    public bool IsSupportedPlatform { get; init; } = OperatingSystem.IsWindows();

    /// <summary>Folder for the download and the unpacked files.</summary>
    public string TempDirectory { get; init; } = Path.GetTempPath();

    /// <summary>Largest accepted download and largest accepted unpacked size.</summary>
    public long MaxBytes { get; init; } = 600L * 1024 * 1024;

    /// <summary>Which addresses a download may be read from, including redirects.</summary>
    public Func<Uri, bool> IsAllowedSource { get; init; } = static uri =>
        uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Starts the new version; returns whether it was started.</summary>
    public Func<ProcessStartInfo, bool> StartProcess { get; init; } = static start =>
    {
        using var process = Process.Start(start);
        return process is not null;
    };
}

/// <summary>
/// Installs a GitWidget release over the running one on Windows (issue #19). The archive is downloaded from the
/// release, checked against its SHA-256 and unpacked to a temporary folder; only then the installed files are renamed
/// to <c>*.gitwidget-old</c> (possible for a running exe) and the new files moved in. The backups are deleted by the
/// new version at its start (<see cref="DeleteLeftovers"/>) and restored when the new version cannot be started.
/// </summary>
internal sealed partial class UpdateInstaller : IUpdateInstaller, IDisposable
{
    /// <summary>Name of the <see cref="HttpClient"/> (no automatic redirects: each hop is checked).</summary>
    public const string HttpClientName = "GitWidgetUpdate";

    /// <summary>Argument that makes a started GitWidget wait for the given process id to exit first.</summary>
    public const string WaitForExitArgument = "--wait-for-exit";

    internal const string BackupSuffix = ".gitwidget-old";
    private const int MaxRedirects = 5;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UpdateInstaller> _logger;
    private readonly UpdateInstallerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<(string Target, string? Backup)> _replaced = [];

    /// <summary>Creates the installer.</summary>
    /// <param name="httpClientFactory">HTTP client factory (uses the <see cref="HttpClientName"/> client).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Locations.</param>
    public UpdateInstaller(IHttpClientFactory httpClientFactory, ILogger<UpdateInstaller> logger, UpdateInstallerOptions? options = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _options = options ?? new UpdateInstallerOptions();
    }

    /// <inheritdoc />
    public event EventHandler<UpdateInstallState>? StateChanged;

    /// <inheritdoc />
    public UpdateInstallState State { get; private set; } = new(UpdateInstallStatus.Idle, null, null);

    /// <summary>Deletes the backups a finished update left behind. Best effort; never throws.</summary>
    /// <param name="installDirectory">Folder GitWidget runs from.</param>
    public static void DeleteLeftovers(string installDirectory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(installDirectory, "*" + BackupSuffix, SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still in use by the exiting previous version; the next start removes it.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <inheritdoc />
    public string? WhyNotInstallable(ReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!_options.IsSupportedPlatform)
        {
            return "Updating in place is available on Windows only.";
        }

        if (release.Asset is not { } asset)
        {
            return "This release has no download for this system.";
        }

        if (asset.Sha256 is null)
        {
            return "This release has no checksum to verify the download with.";
        }

        if (asset.Size > _options.MaxBytes || !_options.IsAllowedSource(asset.DownloadUrl))
        {
            return "The download of this release is not accepted (unexpected size or address).";
        }

        var directory = _options.InstallDirectory;
        if (File.Exists(Path.Combine(directory, Path.ChangeExtension(_options.ExecutableName, ".dll"))))
        {
            return "This is a development build, not an installed release.";
        }

        if (!File.Exists(Path.Combine(directory, _options.ExecutableName)))
        {
            return $"{_options.ExecutableName} was not found in {directory}.";
        }

        return CanWrite(directory) ? null : $"GitWidget is not allowed to change its folder ({directory}).";
    }

    /// <inheritdoc />
    public async Task<bool> InstallAsync(ReleaseInfo release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!await _gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            return false; // An installation is already running.
        }

        string? work = null;
        try
        {
            if (State.Status == UpdateInstallStatus.ReadyToRestart)
            {
                return true;
            }

            if (WhyNotInstallable(release) is { } reason)
            {
                return Fail(reason);
            }

            var asset = release.Asset!;
            work = Path.Combine(_options.TempDirectory, "GitWidget-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var archive = Path.Combine(work, "download.zip");

            Set(UpdateInstallStatus.Downloading, 0);
            var hash = await DownloadAsync(asset, archive, cancellationToken).ConfigureAwait(false);

            Set(UpdateInstallStatus.Verifying, null);
            if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Fail("The download does not match the checksum of the release. Nothing was changed.");
            }

            var staging = Path.Combine(work, "files");
            var files = Extract(archive, staging);
            if (!files.Contains(_options.ExecutableName, StringComparer.OrdinalIgnoreCase))
            {
                return Fail($"The release archive does not contain {_options.ExecutableName}. Nothing was changed.");
            }

            // From here on the installed files change; no cancellation in between.
            Set(UpdateInstallStatus.Installing, null);
            Replace(staging, files);
            LogInstalled(_logger, release.Version.ToString());
            Set(UpdateInstallStatus.ReadyToRestart, null);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Set(UpdateInstallStatus.Idle, null);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException or OperationCanceledException)
        {
            Rollback();
            return Fail($"The update could not be installed: {ex.Message} The installed version was kept.");
        }
        finally
        {
            TryDeleteDirectory(work);
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public bool Restart()
    {
        if (State.Status != UpdateInstallStatus.ReadyToRestart)
        {
            return false;
        }

        var start = new ProcessStartInfo(Path.Combine(_options.InstallDirectory, _options.ExecutableName))
        {
            UseShellExecute = false,
            WorkingDirectory = _options.InstallDirectory,
        };
        start.ArgumentList.Add(WaitForExitArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        try
        {
            if (_options.StartProcess(start))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            LogStartFailed(_logger, ex.Message);
        }

        // For example blocked by an application-control policy: put the previous version back.
        Rollback();
        Fail("The new version could not be started; the previous version was restored.");
        return false;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task<string> DownloadAsync(ReleaseAsset asset, string path, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var uri = asset.DownloadUrl;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!_options.IsAllowedSource(uri))
            {
                throw new HttpRequestException($"The download was redirected to an address that is not accepted ({uri.Host}).");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", ProviderBoundary.DefaultUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/octet-stream");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"The download failed (HTTP {(int)response.StatusCode}).");
            }

            if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
            {
                throw new HttpRequestException("The download has an unexpected size.");
            }

            return await SaveAsync(response.Content, path, asset.Size, cancellationToken).ConfigureAwait(false);
        }

        throw new HttpRequestException("The download was redirected too often.");
    }

    private async Task<string> SaveAsync(HttpContent content, string path, long expectedSize, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (target.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long total = 0;
                var reported = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > expectedSize)
                    {
                        throw new HttpRequestException("The download is larger than the release says.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    var percent = (int)(total * 100 / expectedSize);
                    if (percent != reported)
                    {
                        reported = percent;
                        Set(UpdateInstallStatus.Downloading, percent / 100d);
                    }
                }

                if (total != expectedSize)
                {
                    throw new HttpRequestException("The download is incomplete.");
                }
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Unpacks the archive; entries that would land outside the target folder are rejected.</summary>
    /// <returns>Relative paths of the unpacked files.</returns>
    private List<string> Extract(string archive, string staging)
    {
        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        var files = new List<string>();
        long total = 0;
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue; // Directory entry.
            }

            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || relative.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The release archive contains an unexpected path ({entry.FullName}).");
            }

            total += entry.Length;
            if (total > _options.MaxBytes)
            {
                throw new InvalidDataException("The release archive unpacks to more than is accepted.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
            files.Add(Path.GetRelativePath(root, target));
        }

        return files;
    }

    private void Replace(string staging, List<string> files)
    {
        _replaced.Clear();
        foreach (var relative in files)
        {
            var target = Path.Combine(_options.InstallDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string? backup = null;
            if (File.Exists(target))
            {
                backup = target + BackupSuffix;
                File.Delete(backup);
                File.Move(target, backup);
            }

            _replaced.Add((target, backup));
            File.Move(Path.Combine(staging, relative), target);
        }
    }

    /// <summary>Puts back every file replaced so far, newest first.</summary>
    private void Rollback()
    {
        for (var i = _replaced.Count - 1; i >= 0; i--)
        {
            var (target, backup) = _replaced[i];
            try
            {
                File.Delete(target);
                if (backup is not null && File.Exists(backup))
                {
                    File.Move(backup, target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogRollbackFailed(_logger, Path.GetFileName(target), ex.Message);
            }
        }

        _replaced.Clear();
    }

    private static bool CanWrite(string directory)
    {
        var probe = Path.Combine(directory, ".gitwidget-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string? directory)
    {
        try
        {
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private bool Fail(string message)
    {
        LogFailed(_logger, message);
        State = new UpdateInstallState(UpdateInstallStatus.Failed, null, message);
        StateChanged?.Invoke(this, State);
        return false;
    }

    private void Set(UpdateInstallStatus status, double? progress)
    {
        State = new UpdateInstallState(status, progress, null);
        StateChanged?.Invoke(this, State);
    }

    [LoggerMessage(EventId = 2210, Level = LogLevel.Information, Message = "GitWidget {Version} was installed in place; waiting for the restart")]
    private static partial void LogInstalled(ILogger logger, string version);

    [LoggerMessage(EventId = 2211, Level = LogLevel.Warning, Message = "In-place update failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 2212, Level = LogLevel.Warning, Message = "The updated GitWidget could not be started: {Reason}")]
    private static partial void LogStartFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 2213, Level = LogLevel.Error, Message = "Could not restore {File} after a failed update: {Reason}")]
    private static partial void LogRollbackFailed(ILogger logger, string file, string reason);
}
