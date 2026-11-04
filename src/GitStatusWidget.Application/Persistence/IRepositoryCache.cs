using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Notifications;
using GitStatusWidget.Domain.Snapshots;

namespace GitStatusWidget.Application.Persistence;

/// <summary>
/// Locally cached state of one repository.
/// </summary>
/// <param name="Snapshot">Last accepted snapshot, if any.</param>
/// <param name="SyncState">Provider sync state belonging to <paramref name="Snapshot"/>.</param>
/// <param name="Ledger">Recent event keys.</param>
public sealed record CachedRepositoryState(RepositorySnapshot? Snapshot, ProviderSyncState SyncState, EventLedger Ledger)
{
    /// <summary>Empty state (no cache yet).</summary>
    public static CachedRepositoryState Empty => new(null, ProviderSyncState.Empty, new EventLedger());
}

/// <summary>
/// Persists the last accepted snapshot, provider sync state and event ledger per repository configuration.
/// </summary>
public interface IRepositoryCache
{
    /// <summary>Loads the cached state; returns <see cref="CachedRepositoryState.Empty"/> when missing, corrupt or from an unsupported version.</summary>
    /// <param name="configurationId">Repository configuration id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The cached state.</returns>
    Task<CachedRepositoryState> LoadAsync(Guid configurationId, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically persists an accepted snapshot together with its sync state and ledger. The snapshot is
    /// written first; a crash between files leaves a newer snapshot with older sync state, which is safe
    /// (it only causes an overlapping delta).
    /// </summary>
    /// <param name="configurationId">Repository configuration id.</param>
    /// <param name="state">State to persist.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the state is durable.</returns>
    Task SaveAsync(Guid configurationId, CachedRepositoryState state, CancellationToken cancellationToken);

    /// <summary>Deletes all cached files of a repository configuration.</summary>
    /// <param name="configurationId">Repository configuration id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the files are removed.</returns>
    Task DeleteAsync(Guid configurationId, CancellationToken cancellationToken);
}

/// <summary>
/// Loads and saves <see cref="Settings.AppSettings"/>.
/// </summary>
public interface ISettingsStore
{
    /// <summary>Loads settings; returns defaults when no file exists.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="UnsupportedSchemaVersionException">The file was written by a newer version.</exception>
    Task<Settings.AppSettings> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Saves settings atomically.</summary>
    /// <param name="settings">Settings.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the settings are durable.</returns>
    Task SaveAsync(Settings.AppSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// A persisted document was written by a newer application version and must not be overwritten.
/// </summary>
public sealed class UnsupportedSchemaVersionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public UnsupportedSchemaVersionException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Message.</param>
    public UnsupportedSchemaVersionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Inner exception.</param>
    public UnsupportedSchemaVersionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
