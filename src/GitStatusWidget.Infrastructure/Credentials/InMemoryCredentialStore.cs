using System.Collections.Concurrent;
using GitStatusWidget.Application.Credentials;

namespace GitStatusWidget.Infrastructure.Credentials;

/// <summary>
/// Session-only credential store: secrets live in process memory and are lost when the
/// application exits. Used for tests and as the visible fallback when no OS store is usable.
/// </summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    /// <summary>Default <see cref="StoreName"/>.</summary>
    public const string DefaultStoreName = "Session only (tokens are not saved and must be re-entered after restart)";

    private readonly ConcurrentDictionary<string, Secret> _secrets = new(StringComparer.Ordinal);

    /// <summary>Creates an empty store.</summary>
    /// <param name="storeName">Display name; defaults to <see cref="DefaultStoreName"/>.</param>
    public InMemoryCredentialStore(string? storeName = null)
    {
        StoreName = string.IsNullOrWhiteSpace(storeName) ? DefaultStoreName : storeName;
    }

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public string StoreName { get; }

    /// <inheritdoc />
    public Task<Secret?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CredentialStoreNames.ValidateKey(key);
        return Task.FromResult(_secrets.TryGetValue(key, out var secret) ? secret : null);
    }

    /// <inheritdoc />
    public Task WriteAsync(string key, Secret secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CredentialStoreNames.ValidateKey(key);
        ArgumentNullException.ThrowIfNull(secret);
        _secrets[key] = secret;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CredentialStoreNames.ValidateKey(key);
        _secrets.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
