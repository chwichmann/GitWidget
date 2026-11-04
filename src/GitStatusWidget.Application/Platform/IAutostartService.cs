namespace GitStatusWidget.Application.Platform;

/// <summary>
/// Registers the application to start when the current user logs in (Windows Run key,
/// macOS LaunchAgent, Linux XDG autostart). Per-user only; never requires administrator rights.
/// </summary>
public interface IAutostartService
{
    /// <summary>
    /// Whether start-at-login can be configured on this system (supported OS and a resolvable executable path).
    /// When <see langword="false"/>, hide or disable the setting.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>Returns whether a start-at-login entry for this application is registered and not disabled by the user.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><see langword="true"/> when the application starts at login.</returns>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken);

    /// <summary>Creates (pointing at the current executable) or removes the start-at-login entry.</summary>
    /// <param name="enabled"><see langword="true"/> to register, <see langword="false"/> to remove.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the entry has been written or removed.</returns>
    /// <exception cref="NotSupportedException">Enabling while <see cref="IsSupported"/> is <see langword="false"/>.</exception>
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken);
}
