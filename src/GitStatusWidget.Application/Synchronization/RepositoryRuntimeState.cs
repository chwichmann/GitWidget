using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Domain.Health;
using GitStatusWidget.Domain.Snapshots;

namespace GitStatusWidget.Application.Synchronization;

/// <summary>
/// Immutable view of one monitored repository for the UI: last accepted snapshot plus health.
/// </summary>
/// <param name="Configuration">Repository configuration.</param>
/// <param name="Snapshot">Last accepted snapshot (possibly from the cache), or <see langword="null"/>.</param>
/// <param name="Health">Health state.</param>
public sealed record RepositoryRuntimeState(
    RepositoryConfiguration Configuration,
    RepositorySnapshot? Snapshot,
    RepositoryHealthState Health)
{
    /// <summary>Whether a refresh is running.</summary>
    public bool IsRefreshing { get; init; }

    /// <summary>Last refresh attempt.</summary>
    public DateTimeOffset? LastAttemptAt { get; init; }

    /// <summary>Last successful refresh (or the cached snapshot time after startup).</summary>
    public DateTimeOffset? LastSuccessAt { get; init; }

    /// <summary>Classification of the last failure, if the last attempt failed.</summary>
    public ProviderFailureKind? LastFailure { get; init; }

    /// <summary>Sanitized message of the last failure.</summary>
    public string? LastError { get; init; }

    /// <summary>Polling is paused until this time (rate limit or backoff).</summary>
    public DateTimeOffset? BlockedUntil { get; init; }

    /// <summary>Latest provider rate-limit information.</summary>
    public RateLimitInfo? RateLimit { get; init; }

    /// <summary>Configuration id.</summary>
    public Guid Id => Configuration.Id;
}

/// <summary>
/// Event args for newly derived repository changes.
/// </summary>
/// <param name="RepositoryId">Configuration id.</param>
/// <param name="Events">New events (already deduplicated against the ledger), in deterministic order.</param>
/// <param name="IsCatchUp">The changes accumulated while the application was not running; show them in the
/// activity feed, but do not animate or toast them.</param>
public sealed record RepositoryChangesDetected(Guid RepositoryId, IReadOnlyList<Domain.Events.RepositoryChangeEvent> Events, bool IsCatchUp);

/// <summary>
/// Thread-safe publication point of repository states and detected changes. Handlers are invoked on a
/// background thread; UI subscribers must marshal to their dispatcher.
/// </summary>
public interface IRepositoryStatusHub
{
    /// <summary>Raised when a repository state changed.</summary>
    event EventHandler<RepositoryRuntimeState>? StateChanged;

    /// <summary>Raised when new changes were detected.</summary>
    event EventHandler<RepositoryChangesDetected>? ChangesDetected;

    /// <summary>Raised when a repository was removed from monitoring.</summary>
    event EventHandler<Guid>? RepositoryRemoved;

    /// <summary>Current states, ordered by <see cref="RepositoryConfiguration.SortOrder"/>.</summary>
    IReadOnlyList<RepositoryRuntimeState> States { get; }

    /// <summary>Current state of one repository, if monitored.</summary>
    /// <param name="id">Configuration id.</param>
    /// <returns>The state or <see langword="null"/>.</returns>
    RepositoryRuntimeState? Find(Guid id);
}

/// <summary>
/// Default <see cref="IRepositoryStatusHub"/>.
/// </summary>
public sealed class RepositoryStatusHub : IRepositoryStatusHub
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, RepositoryRuntimeState> _states = [];

    /// <inheritdoc />
    public event EventHandler<RepositoryRuntimeState>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<RepositoryChangesDetected>? ChangesDetected;

    /// <inheritdoc />
    public event EventHandler<Guid>? RepositoryRemoved;

    /// <inheritdoc />
    public IReadOnlyList<RepositoryRuntimeState> States
    {
        get
        {
            lock (_gate)
            {
                return _states.Values
                    .OrderBy(s => s.Configuration.SortOrder)
                    .ThenBy(s => s.Configuration.EffectiveDisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    /// <inheritdoc />
    public RepositoryRuntimeState? Find(Guid id)
    {
        lock (_gate)
        {
            return _states.GetValueOrDefault(id);
        }
    }

    /// <summary>Stores and publishes a state.</summary>
    /// <param name="state">New state.</param>
    public void Publish(RepositoryRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (_states.TryGetValue(state.Id, out var existing) && existing == state)
            {
                return;
            }

            _states[state.Id] = state;
        }

        StateChanged?.Invoke(this, state);
    }

    /// <summary>Publishes detected changes.</summary>
    /// <param name="changes">Changes.</param>
    public void PublishChanges(RepositoryChangesDetected changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Events.Count > 0)
        {
            ChangesDetected?.Invoke(this, changes);
        }
    }

    /// <summary>Removes a repository.</summary>
    /// <param name="id">Configuration id.</param>
    public void Remove(Guid id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _states.Remove(id);
        }

        if (removed)
        {
            RepositoryRemoved?.Invoke(this, id);
        }
    }
}
