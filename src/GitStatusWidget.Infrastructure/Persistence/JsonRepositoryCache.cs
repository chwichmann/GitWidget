using System.Collections.Concurrent;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Notifications;
using GitStatusWidget.Application.Persistence;
using GitStatusWidget.Domain.Snapshots;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Infrastructure.Persistence;

/// <summary>
/// File-based <see cref="IRepositoryCache"/>: <c>cache/{id}/snapshot.json</c>, <c>request-cache.json</c>,
/// <c>event-ledger.json</c>.
/// </summary>
public sealed class JsonRepositoryCache : IRepositoryCache, IDisposable
{
    /// <summary>Current schema version of cache documents.</summary>
    public const int SchemaVersion = 1;

    private readonly AppPaths _paths;
    private readonly ILogger<JsonRepositoryCache> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>Creates the cache.</summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public JsonRepositoryCache(AppPaths paths, ILogger<JsonRepositoryCache> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CachedRepositoryState> LoadAsync(Guid configurationId, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(configurationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = _paths.CacheDirectory(configurationId);
            var (snapshotStatus, snapshot) = await VersionedJsonFile.ReadAsync<RepositorySnapshot>(
                Path.Combine(directory, "snapshot.json"), SchemaVersion, null, _logger, cancellationToken).ConfigureAwait(false);
            var (_, sync) = await VersionedJsonFile.ReadAsync<ProviderSyncState>(
                Path.Combine(directory, "request-cache.json"), SchemaVersion, null, _logger, cancellationToken).ConfigureAwait(false);
            var (_, ledger) = await VersionedJsonFile.ReadAsync<LedgerDocument>(
                Path.Combine(directory, "event-ledger.json"), SchemaVersion, null, _logger, cancellationToken).ConfigureAwait(false);

            // Sync state (watermarks, ETags) is only meaningful together with the snapshot it belongs to.
            var syncState = snapshotStatus == VersionedJsonFile.ReadStatus.Ok && sync is not null ? sync : ProviderSyncState.Empty;
            return new CachedRepositoryState(
                snapshotStatus == VersionedJsonFile.ReadStatus.Ok ? snapshot : null,
                syncState,
                new EventLedger(EventLedger.DefaultCapacity, ledger?.Keys));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(Guid configurationId, CachedRepositoryState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var gate = _locks.GetOrAdd(configurationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = _paths.CacheDirectory(configurationId);
            if (state.Snapshot is not null)
            {
                await VersionedJsonFile.WriteAtomicAsync(Path.Combine(directory, "snapshot.json"), SchemaVersion, state.Snapshot, indented: false, cancellationToken).ConfigureAwait(false);
            }

            await VersionedJsonFile.WriteAtomicAsync(Path.Combine(directory, "request-cache.json"), SchemaVersion, state.SyncState, indented: false, cancellationToken).ConfigureAwait(false);
            await VersionedJsonFile.WriteAtomicAsync(Path.Combine(directory, "event-ledger.json"), SchemaVersion, new LedgerDocument(state.Ledger.Keys), indented: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(Guid configurationId, CancellationToken cancellationToken)
    {
        var directory = _paths.CacheDirectory(configurationId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private sealed record LedgerDocument(IReadOnlyList<string> Keys);

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var gate in _locks.Values)
        {
            gate.Dispose();
        }
    }
}
