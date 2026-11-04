namespace GitStatusWidget.Application.Credentials;

/// <summary>
/// OS-backed secret storage (Windows Credential Manager, macOS Keychain, Linux Secret Service).
/// Implementations must never log secret values.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Whether the store is usable on this machine (for example a keyring daemon is running).</summary>
    bool IsAvailable { get; }

    /// <summary>Human-readable name of the backing store, e.g. "Windows Credential Manager".</summary>
    string StoreName { get; }

    /// <summary>Reads a secret.</summary>
    /// <param name="key">Credential reference.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The secret, or <see langword="null"/> when no entry exists.</returns>
    Task<Secret?> ReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Creates or replaces a secret.</summary>
    /// <param name="key">Credential reference.</param>
    /// <param name="secret">Secret value.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the secret is stored.</returns>
    Task WriteAsync(string key, Secret secret, CancellationToken cancellationToken);

    /// <summary>Deletes a secret; succeeds when the entry does not exist.</summary>
    /// <param name="key">Credential reference.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the secret is removed.</returns>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
