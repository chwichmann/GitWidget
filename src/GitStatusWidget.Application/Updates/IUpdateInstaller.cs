namespace GitStatusWidget.Application.Updates;

/// <summary>Phase of an in-place update (issue #19).</summary>
public enum UpdateInstallStatus
{
    /// <summary>Nothing is being installed.</summary>
    Idle,

    /// <summary>The release archive is being downloaded.</summary>
    Downloading,

    /// <summary>The download is being checked against the checksum of the release.</summary>
    Verifying,

    /// <summary>The files are being replaced.</summary>
    Installing,

    /// <summary>The new version is in place; a restart starts it.</summary>
    ReadyToRestart,

    /// <summary>The update failed; the installed version is unchanged or was restored.</summary>
    Failed,
}

/// <summary>State of an in-place update.</summary>
/// <param name="Status">Phase.</param>
/// <param name="Progress">Download progress, 0 to 1, while downloading with a known size.</param>
/// <param name="Message">Text for the user when <see cref="UpdateInstallStatus.Failed"/>.</param>
public sealed record UpdateInstallState(UpdateInstallStatus Status, double? Progress, string? Message);

/// <summary>
/// Installs a newer GitWidget release over the running one (issue #19): download, verify, replace the files, restart.
/// Only ever acts on an explicit request; a failure leaves the installed version in place.
/// </summary>
public interface IUpdateInstaller
{
    /// <summary>Raised (on a background thread) when <see cref="State"/> changes.</summary>
    event EventHandler<UpdateInstallState>? StateChanged;

    /// <summary>Current state.</summary>
    UpdateInstallState State { get; }

    /// <summary>Says why a release cannot be installed in place.</summary>
    /// <param name="release">Release to install.</param>
    /// <returns>The reason for the user, or <see langword="null"/> when it can be installed.</returns>
    string? WhyNotInstallable(ReleaseInfo release);

    /// <summary>Downloads and verifies the release and replaces the installed files; the old files are kept as backup.</summary>
    /// <param name="release">Release to install.</param>
    /// <param name="cancellationToken">Cancels the download; nothing is replaced then.</param>
    /// <returns><see langword="true"/> when the new version is in place and waits for <see cref="Restart"/>.</returns>
    Task<bool> InstallAsync(ReleaseInfo release, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the new version; it waits for this process to exit, so the caller exits the application afterwards. When
    /// it cannot be started, the previous version is restored.
    /// </summary>
    /// <returns><see langword="true"/> when the new version was started.</returns>
    bool Restart();
}
