using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Comparison;
using GitStatusWidget.Application.Notifications;
using GitStatusWidget.Application.Persistence;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Domain.Events;
using GitStatusWidget.Domain.Health;
using GitStatusWidget.Domain.Snapshots;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Application.Synchronization;

/// <summary>
/// Commands the UI can issue to the synchronization engine.
/// </summary>
public interface ISynchronizationController
{
    /// <summary>Currently applied settings.</summary>
    AppSettings Settings { get; }

    /// <summary>
    /// Requests an immediate refresh of one or all repositories. Joins a running refresh, clears error backoff
    /// and credential suspension, but respects an active provider-imposed rate-limit wait (architecture §21).
    /// </summary>
    /// <param name="repositoryId">Repository to refresh, or <see langword="null"/> for all.</param>
    void RequestRefresh(Guid? repositoryId = null);

    /// <summary>Applies new settings (added, removed or changed repositories).</summary>
    /// <param name="settings">New settings.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when caches of new repositories are loaded.</returns>
    Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// Schedules and executes repository refreshes (architecture §12): one refresh per repository at a time,
/// bounded global concurrency, per-section intervals with jitter, backoff, rate-limit waits, complete-snapshot
/// acceptance, persistence before publication, event deduplication and staleness tracking.
/// </summary>
/// <remarks>
/// The engine performs no waiting of its own; <see cref="SynchronizationService"/> drives it. All mutable state
/// is guarded by one lock; I/O runs outside the lock.
/// </remarks>
public sealed partial class SynchronizationEngine : ISynchronizationController, IDisposable
{
    /// <summary>Snapshots older than this at startup produce catch-up events (no toasts).</summary>
    public static readonly TimeSpan CatchUpThreshold = TimeSpan.FromMinutes(15);

    /// <summary>Unchanged snapshots are re-persisted at most this often (keeps freshness after restart).</summary>
    public static readonly TimeSpan PersistUnchangedInterval = TimeSpan.FromMinutes(5);

    /// <summary>Sections due within this window are fetched together with a section that is already due.</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromMinutes(1);

    private readonly IRepositoryStatusProviderResolver _providers;
    private readonly IRepositoryCache _cache;
    private readonly ISnapshotComparer _comparer;
    private readonly RepositoryStatusHub _hub;
    private readonly TimeProvider _time;
    private readonly ILogger<SynchronizationEngine> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly AsyncSignal _wake = new();
    private readonly Random _random;
    private SemaphoreSlim _concurrency = new(2, 2);
    private AppSettings _settings = new();

    /// <summary>Creates the engine.</summary>
    /// <param name="providers">Provider resolver.</param>
    /// <param name="cache">Snapshot cache.</param>
    /// <param name="comparer">Snapshot comparer.</param>
    /// <param name="hub">Status hub.</param>
    /// <param name="time">Time provider.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="random">Random source for jitter (tests pass a seeded instance).</param>
    public SynchronizationEngine(
        IRepositoryStatusProviderResolver providers,
        IRepositoryCache cache,
        ISnapshotComparer comparer,
        RepositoryStatusHub hub,
        TimeProvider time,
        ILogger<SynchronizationEngine> logger,
        Random? random = null)
    {
        _providers = providers;
        _cache = cache;
        _comparer = comparer;
        _hub = hub;
        _time = time;
        _logger = logger;
        _random = random ?? Random.Shared;
    }

    /// <inheritdoc />
    public AppSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    /// <summary>Signal set whenever work may have become due (manual refresh, completion, settings).</summary>
    internal AsyncSignal Wake => _wake;

    /// <inheritdoc />
    public async Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var now = _time.GetUtcNow();
        var wanted = settings.Repositories.Where(r => r.IsEnabled).ToDictionary(r => r.Id);

        List<Guid> removed;
        List<RepositoryConfiguration> added = [];
        List<Guid> identityChanged = [];
        lock (_gate)
        {
            if (settings.MaxConcurrentRefreshes != _settings.MaxConcurrentRefreshes && _entries.Values.All(e => e.Running is null))
            {
                _concurrency.Dispose();
                var limit = Math.Clamp(settings.MaxConcurrentRefreshes, 1, 8);
                _concurrency = new SemaphoreSlim(limit, limit);
            }

            _settings = settings;
            removed = _entries.Keys.Where(id => !wanted.ContainsKey(id)).ToList();
            foreach (var id in removed)
            {
                _entries[id].RunCancellation?.Cancel();
                _entries.Remove(id);
            }

            foreach (var configuration in wanted.Values)
            {
                if (!_entries.TryGetValue(configuration.Id, out var entry))
                {
                    added.Add(configuration);
                    continue;
                }

                if (entry.Configuration == configuration)
                {
                    continue;
                }

                var previous = entry.Configuration;
                entry.Configuration = configuration;
                if (IdentityChanged(previous, configuration))
                {
                    // A different repository: start a new baseline (no events against the old one).
                    entry.Snapshot = null;
                    entry.SyncState = ProviderSyncState.Empty;
                    entry.Ledger = new EventLedger();
                    identityChanged.Add(configuration.Id);
                }

                if (ConnectionChanged(previous, configuration))
                {
                    entry.ResetFailureState();
                    entry.MarkAllDue(now);
                }

                PublishLocked(entry, entry.State with { Configuration = configuration, Snapshot = entry.Snapshot });
            }
        }

        foreach (var id in removed)
        {
            _hub.Remove(id);
        }

        foreach (var id in identityChanged)
        {
            await _cache.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }

        foreach (var configuration in added)
        {
            var cached = await _cache.LoadAsync(configuration.Id, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_entries.ContainsKey(configuration.Id) || !_settings.Repositories.Any(r => r.Id == configuration.Id && r.IsEnabled))
                {
                    continue;
                }

                var entry = new Entry(configuration, cached, now, Jitter(TimeSpan.FromSeconds(5)));
                _entries[configuration.Id] = entry;
                var health = cached.Snapshot is null ? RepositoryHealthState.Refreshing
                    : IsStale(configuration, cached.Snapshot.RetrievedAt, now) ? RepositoryHealthState.Stale
                    : RepositoryHealthState.Healthy;
                PublishLocked(entry, new RepositoryRuntimeState(configuration, cached.Snapshot, health)
                {
                    LastSuccessAt = cached.Snapshot?.RetrievedAt,
                });
                LogRepositoryAdded(_logger, configuration.Provider, configuration.FullPath, cached.Snapshot is not null);
            }
        }

        _wake.Set();
    }

    /// <inheritdoc />
    public void RequestRefresh(Guid? repositoryId = null)
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values.Where(e => repositoryId is null || e.Configuration.Id == repositoryId))
            {
                entry.ManualPending = true;
                entry.Suspended = false;
                if (!entry.BlockedByRateLimit)
                {
                    entry.BlockedUntil = null;
                }
            }
        }

        _wake.Set();
    }

    /// <summary>
    /// Called after the system resumed from standby: refresh everything after a short randomized delay
    /// (architecture §20).
    /// </summary>
    public void NotifyResume()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Failures = 0;
                if (!entry.BlockedByRateLimit || entry.BlockedUntil < now)
                {
                    entry.BlockedUntil = now + TimeSpan.FromSeconds(5) + Jitter(TimeSpan.FromSeconds(10));
                    entry.BlockedByRateLimit = false;
                }

                entry.NextWorkItems = now;
                entry.NextBuilds = now;
            }
        }

        LogResume(_logger);
        _wake.Set();
    }

    /// <summary>
    /// Starts all due refreshes and returns the next time work becomes due (or <see langword="null"/> when
    /// nothing is scheduled).
    /// </summary>
    /// <param name="cancellationToken">Cancels running refreshes (shutdown).</param>
    /// <returns>The next due time.</returns>
    public DateTimeOffset? StartDueRefreshes(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        DateTimeOffset? next = null;
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.Running is not null)
                {
                    continue;
                }

                var scope = DueScope(entry, now);
                if (scope == RefreshScope.None)
                {
                    var due = NextDue(entry);
                    if (due is { } value && (next is null || value < next))
                    {
                        next = value;
                    }

                    continue;
                }

                entry.RunCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                PublishLocked(entry, entry.State with { IsRefreshing = true, LastAttemptAt = now });
                entry.Running = RunAsync(entry, entry.Configuration, scope, entry.RunCancellation.Token);
            }
        }

        return next;
    }

    /// <summary>Recomputes staleness of all repositories.</summary>
    public void UpdateStaleness()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.State.Health == RepositoryHealthState.Healthy
                    && entry.State.LastSuccessAt is { } success
                    && IsStale(entry.Configuration, success, now))
                {
                    PublishLocked(entry, entry.State with { Health = RepositoryHealthState.Stale });
                }
            }
        }
    }

    /// <summary>Waits until all running refreshes have completed (tests, shutdown).</summary>
    /// <returns>A task that completes when no refresh is running.</returns>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] running;
            lock (_gate)
            {
                running = _entries.Values.Select(e => e.Running).OfType<Task>().ToArray();
            }

            if (running.Length == 0)
            {
                return;
            }

            await Task.WhenAll(running).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels running refreshes and waits (bounded) for them to finish.</summary>
    /// <param name="timeout">Maximum wait.</param>
    /// <returns>A task that completes when refreshes stopped or the timeout elapsed.</returns>
    public async Task StopAsync(TimeSpan timeout)
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.RunCancellation?.Cancel();
            }
        }

        await Task.WhenAny(WhenIdleAsync(), Task.Delay(timeout, _time)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.RunCancellation?.Dispose();
            }
        }

        _concurrency.Dispose();
    }

    private async Task RunAsync(Entry entry, RepositoryConfiguration configuration, RefreshScope scope, CancellationToken cancellationToken)
    {
        await Task.Yield();
        var concurrency = _concurrency;
        var acquired = false;
        try
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            await RefreshAsync(entry, configuration, scope, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown or removal: keep the previous snapshot untouched.
        }
#pragma warning disable CA1031 // A failing refresh must never stop the scheduler.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogUnexpected(_logger, ex, configuration.FullPath);
            ApplyFailure(entry, ProviderFailureKind.Malformed, "Unexpected internal error; see the log for details.", null, null);
        }
        finally
        {
            if (acquired)
            {
                concurrency.Release();
            }

            lock (_gate)
            {
                entry.Running = null;
                entry.RunCancellation?.Dispose();
                entry.RunCancellation = null;
                if (_entries.ContainsKey(configuration.Id))
                {
                    PublishLocked(entry, entry.State with { IsRefreshing = false });
                }
            }

            _wake.Set();
        }
    }

    private async Task RefreshAsync(Entry entry, RepositoryConfiguration configuration, RefreshScope scope, CancellationToken cancellationToken)
    {
        RepositorySnapshot? previous;
        ProviderSyncState syncState;
        EventLedger ledger;
        TimeSpan window;
        lock (_gate)
        {
            previous = entry.Snapshot;
            syncState = entry.SyncState;
            ledger = entry.Ledger;
            window = _settings.RecentlyClosedWindow;
        }

        if (previous is null)
        {
            scope = RefreshScope.All;
        }

        var started = _time.GetUtcNow();
        var provider = _providers.Resolve(configuration.Provider);
        var result = await provider.FetchAsync(
            configuration,
            new SnapshotRequestContext(previous, syncState, scope, started - window),
            cancellationToken).ConfigureAwait(false);

        if (result is ProviderFetchResult.Failure failure)
        {
            ApplyFailure(entry, failure.Kind, failure.Message, failure.RetryAfter, result.RateLimit);
            return;
        }

        var success = (ProviderFetchResult.Success)result;
        var now = _time.GetUtcNow();
        var assembly = SnapshotAssembler.Assemble(previous, success.Fetch, now, now - window);
        if (assembly is AssemblyResult.Rejected rejected)
        {
            LogRejected(_logger, configuration.FullPath, rejected.Reason);
            ApplyFailure(entry, ProviderFailureKind.Partial, $"Refresh rejected: {rejected.Reason}", null, result.RateLimit, forceReconcile: true);
            return;
        }

        var accepted = (AssemblyResult.Accepted)assembly;
        var snapshot = accepted.Snapshot;

        List<RepositoryChangeEvent> newEvents = [];
        var newLedger = new EventLedger(ledger.Capacity, ledger.Keys);
        if (previous is not null)
        {
            newEvents = _comparer.Compare(previous, snapshot).Where(e => newLedger.TryAdd(e.Key)).ToList();
        }

        bool persist;
        lock (_gate)
        {
            persist = previous is null || HasChanges(success.Fetch) || newEvents.Count > 0 || now - entry.LastPersistedAt >= PersistUnchangedInterval;
        }

        if (persist)
        {
            try
            {
                await _cache.SaveAsync(configuration.Id, new CachedRepositoryState(snapshot, success.SyncState, newLedger), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Architecture §12.2: a snapshot becomes visible only after successful persistence.
                LogPersistFailed(_logger, ex, configuration.FullPath);
                ApplyFailure(entry, ProviderFailureKind.Malformed, $"Could not write the local cache: {ex.Message}", null, result.RateLimit);
                return;
            }
        }

        bool catchUp;
        lock (_gate)
        {
            if (!_entries.TryGetValue(configuration.Id, out var current) || !ReferenceEquals(current, entry) || entry.Configuration != configuration)
            {
                return; // Removed or reconfigured while refreshing.
            }

            entry.Snapshot = snapshot;
            entry.SyncState = success.SyncState;
            entry.Ledger = newLedger;
            if (persist)
            {
                entry.LastPersistedAt = now;
            }

            entry.ResetFailureState();
            if ((scope & ManualScope) == ManualScope)
            {
                // A manual refresh is satisfied only by a refresh that covered its sections (issue #8). A manual
                // request that arrived during a narrower scheduled refresh stays pending and runs right after it.
                entry.ManualPending = false;
            }

            entry.ScheduleAfterSuccess(scope, now, configuration, Stretch(result.RateLimit, now), Jitter);
            if (accepted.ReconciliationRecommended)
            {
                entry.NextReconcile = now;
            }

            // Issues may reference milestones or labels that the snapshot does not know yet (created after the last
            // metadata poll): fetch metadata now instead of waiting for the metadata interval (issue #8). Each set of
            // unknown references triggers only once, so permanently unresolvable references cannot cause a loop.
            var unknown = UnknownReferences(snapshot);
            if (unknown is not null && !scope.HasFlag(RefreshScope.Metadata) && !string.Equals(unknown, entry.LastUnknownReferences, StringComparison.Ordinal))
            {
                entry.NextMetadata = now;
                LogUnknownReferences(_logger, configuration.FullPath);
            }

            entry.LastUnknownReferences = unknown;

            catchUp = entry.CatchUpPending;
            entry.CatchUpPending = false;
            PublishLocked(entry, new RepositoryRuntimeState(configuration, snapshot, RepositoryHealthState.Healthy)
            {
                IsRefreshing = true,
                LastAttemptAt = started,
                LastSuccessAt = now,
                RateLimit = result.RateLimit,
            });
        }

        LogAccepted(_logger, configuration.FullPath, scope, snapshot.IssueCounts.Open, snapshot.IssueCounts.Closed, newEvents.Count);
        _hub.PublishChanges(new RepositoryChangesDetected(configuration.Id, newEvents, catchUp));
    }

    private void ApplyFailure(Entry entry, ProviderFailureKind kind, string message, DateTimeOffset? retryAfter, RateLimitInfo? rateLimit, bool forceReconcile = false)
    {
        var now = _time.GetUtcNow();
        RepositoryHealthState health;
        lock (_gate)
        {
            if (!_entries.TryGetValue(entry.Configuration.Id, out var current) || !ReferenceEquals(current, entry))
            {
                return; // Removed while refreshing.
            }

            entry.Failures++;
            entry.ManualPending = false;
            entry.BlockedByRateLimit = false;
            switch (kind)
            {
                case ProviderFailureKind.RateLimited:
                    entry.BlockedUntil = retryAfter is { } until && until > now ? until : now + DefaultRateLimitWait;
                    entry.BlockedByRateLimit = true;
                    health = RepositoryHealthState.RateLimited;
                    break;
                case ProviderFailureKind.AuthenticationFailed:
                    entry.Suspended = true;
                    health = RepositoryHealthState.AuthenticationFailed;
                    break;
                case ProviderFailureKind.AuthorizationFailed:
                    entry.Suspended = true;
                    health = RepositoryHealthState.AuthorizationFailed;
                    break;
                case ProviderFailureKind.NotFound:
                    entry.Suspended = true;
                    health = RepositoryHealthState.NotFound;
                    break;
                case ProviderFailureKind.Configuration:
                    entry.Suspended = true;
                    health = RepositoryHealthState.ConfigurationError;
                    break;
                case ProviderFailureKind.Offline:
                    entry.BlockedUntil = now + Backoff(entry.Failures);
                    health = RepositoryHealthState.Offline;
                    break;
                default:
                    entry.BlockedUntil = now + Backoff(entry.Failures);
                    health = RepositoryHealthState.ProviderError;
                    break;
            }

            if (forceReconcile)
            {
                entry.NextReconcile = now;
            }

            PublishLocked(entry, entry.State with
            {
                Health = health,
                LastFailure = kind,
                LastError = message,
                LastAttemptAt = now,
                BlockedUntil = entry.BlockedUntil,
                RateLimit = rateLimit ?? entry.State.RateLimit,
            });
        }

        LogFailure(_logger, entry.Configuration.FullPath, kind, message);
    }

    private static RefreshScope DueScope(Entry entry, DateTimeOffset now)
    {
        if (entry.Suspended && !entry.ManualPending)
        {
            return RefreshScope.None;
        }

        if (entry.BlockedUntil is { } blocked && blocked > now)
        {
            return RefreshScope.None;
        }

        if (entry.Snapshot is null)
        {
            return RefreshScope.All;
        }

        var scope = RefreshScope.None;
        if (entry.ManualPending)
        {
            scope |= RefreshScope.WorkItems | RefreshScope.Builds | RefreshScope.Metadata;
        }

        if (entry.NextWorkItems <= now)
        {
            scope |= RefreshScope.WorkItems;
        }

        if (entry.NextBuilds <= now)
        {
            scope |= RefreshScope.Builds;
        }

        if (entry.NextMetadata <= now)
        {
            scope |= RefreshScope.Metadata;
        }

        if (entry.NextReconcile <= now)
        {
            scope |= RefreshScope.Reconcile;
        }

        if (scope == RefreshScope.None)
        {
            return scope;
        }

        // Coalesce: sections that become due shortly anyway ride along instead of causing a second refresh.
        var soon = now + CoalesceWindow;
        if (entry.NextWorkItems <= soon)
        {
            scope |= RefreshScope.WorkItems;
        }

        if (entry.NextBuilds <= soon)
        {
            scope |= RefreshScope.Builds;
        }

        if (entry.NextMetadata <= soon)
        {
            scope |= RefreshScope.Metadata;
        }

        return scope;
    }

    private static DateTimeOffset? NextDue(Entry entry)
    {
        if (entry.Suspended && !entry.ManualPending)
        {
            return null;
        }

        var earliest = new[] { entry.NextWorkItems, entry.NextBuilds, entry.NextMetadata, entry.NextReconcile }.Min();
        return entry.BlockedUntil is { } blocked && blocked > earliest ? blocked : earliest;
    }

    private void PublishLocked(Entry entry, RepositoryRuntimeState state)
    {
        entry.State = state;
        _hub.Publish(state);
    }

    private TimeSpan Jitter(TimeSpan span) => TimeSpan.FromMilliseconds(span.TotalMilliseconds * NextDouble());

    private double NextDouble()
    {
        lock (_random)
        {
#pragma warning disable CA5394 // Scheduling jitter does not need cryptographic randomness.
            return _random.NextDouble();
#pragma warning restore CA5394
        }
    }

    private TimeSpan Backoff(int failures)
    {
        var exponent = Math.Clamp(failures - 1, 0, 10);
        var delay = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, BaseBackoff.Ticks * (1L << exponent)));
        return delay + Jitter(TimeSpan.FromTicks(delay.Ticks / 10));
    }

    private static double Stretch(RateLimitInfo? rateLimit, DateTimeOffset now) =>
        rateLimit is { Limit: > 0, Remaining: { } remaining } info
        && remaining < info.Limit.Value / 10
        && (info.ResetAt is null || info.ResetAt > now)
            ? 2.0
            : 1.0;

    private static bool IsStale(RepositoryConfiguration configuration, DateTimeOffset lastSuccess, DateTimeOffset now)
    {
        var threshold = TimeSpan.FromTicks(Math.Max(configuration.IssuePollingInterval.Ticks * 3, TimeSpan.FromMinutes(5).Ticks));
        return now - lastSuccess > threshold;
    }

    private static bool HasChanges(ProviderFetch fetch) =>
        fetch.Counts is not null
        || fetch.Issues is not WorkItemUpdate<Domain.Issues.IssueSnapshot>.UnchangedItems and not WorkItemUpdate<Domain.Issues.IssueSnapshot>.DeltaItems { Changed.Count: 0 }
        || fetch.ChangeRequests is not WorkItemUpdate<Domain.ChangeRequests.ChangeRequestSnapshot>.UnchangedItems and not WorkItemUpdate<Domain.ChangeRequests.ChangeRequestSnapshot>.DeltaItems { Changed.Count: 0 }
        || fetch.Milestones is SectionUpdate<Domain.Milestones.MilestoneSnapshot>.ReplaceSection
        || fetch.Labels is SectionUpdate<Domain.Labels.LabelSnapshot>.ReplaceSection
        || fetch.Builds is SectionUpdate<Domain.Builds.BuildSnapshot>.ReplaceSection;

    /// <summary>Sections a manual refresh asks for.</summary>
    private const RefreshScope ManualScope = RefreshScope.WorkItems | RefreshScope.Builds | RefreshScope.Metadata;

    /// <summary>
    /// Returns a stable signature of milestone ids and label names that issues reference but the snapshot's milestone
    /// and label lists do not contain, or <see langword="null"/> when everything is known.
    /// </summary>
    internal static string? UnknownReferences(RepositorySnapshot snapshot)
    {
        var milestones = snapshot.Milestones.Select(m => m.ExternalId).ToHashSet(StringComparer.Ordinal);
        var labels = snapshot.Labels.Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = snapshot.Issues
            .Select(i => i.Milestone?.ExternalId)
            .OfType<string>()
            .Where(id => !milestones.Contains(id))
            .Select(id => "m:" + id)
            .Concat(snapshot.Issues.SelectMany(i => i.Labels).Select(l => l.Name).Where(n => !labels.Contains(n)).Select(n => "l:" + n))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return unknown.Count == 0 ? null : string.Join('|', unknown);
    }

    private static bool IdentityChanged(RepositoryConfiguration a, RepositoryConfiguration b) =>
        a.Provider != b.Provider
        || a.ServerUri != b.ServerUri
        || !string.Equals(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase);

    private static bool ConnectionChanged(RepositoryConfiguration a, RepositoryConfiguration b) =>
        IdentityChanged(a, b)
        || a.CredentialReference != b.CredentialReference
        || a.UserAgent != b.UserAgent
        || a.AllowInsecureHttp != b.AllowInsecureHttp;

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Monitoring {Provider} repository {Repository} (cached snapshot: {HasCache})")]
    private static partial void LogRepositoryAdded(ILogger logger, Domain.Repositories.RepositoryProviderKind provider, string repository, bool hasCache);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Refresh of {Repository} accepted (scope {Scope}): {Open} open / {Closed} closed issues, {Events} new events")]
    private static partial void LogAccepted(ILogger logger, string repository, RefreshScope scope, int open, int closed, int events);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Refresh of {Repository} failed: {Kind}: {Message}")]
    private static partial void LogFailure(ILogger logger, string repository, ProviderFailureKind kind, string message);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning, Message = "Snapshot of {Repository} rejected: {Reason}")]
    private static partial void LogRejected(ILogger logger, string repository, string reason);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Error, Message = "Unexpected error while refreshing {Repository}")]
    private static partial void LogUnexpected(ILogger logger, Exception exception, string repository);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Error, Message = "Could not persist snapshot of {Repository}")]
    private static partial void LogPersistFailed(ILogger logger, Exception exception, string repository);

    [LoggerMessage(EventId = 2007, Level = LogLevel.Information, Message = "Issues of {Repository} reference unknown milestones or labels; refreshing metadata now")]
    private static partial void LogUnknownReferences(ILogger logger, string repository);

    [LoggerMessage(EventId = 2006, Level = LogLevel.Information, Message = "System resume detected; refreshing all repositories shortly")]
    private static partial void LogResume(ILogger logger);

    private sealed class Entry
    {
        public Entry(RepositoryConfiguration configuration, CachedRepositoryState cached, DateTimeOffset now, TimeSpan initialDelay)
        {
            Configuration = configuration;
            Snapshot = cached.Snapshot;
            SyncState = cached.SyncState;
            Ledger = cached.Ledger;
            var first = now + initialDelay;
            NextWorkItems = NextBuilds = NextMetadata = NextReconcile = first;
            LastPersistedAt = cached.Snapshot?.RetrievedAt ?? DateTimeOffset.MinValue;
            CatchUpPending = cached.Snapshot is not null && now - cached.Snapshot.RetrievedAt > CatchUpThreshold;
            State = new RepositoryRuntimeState(configuration, cached.Snapshot, RepositoryHealthState.Refreshing);
        }

        public RepositoryConfiguration Configuration { get; set; }

        public RepositorySnapshot? Snapshot { get; set; }

        public ProviderSyncState SyncState { get; set; }

        public EventLedger Ledger { get; set; }

        public RepositoryRuntimeState State { get; set; }

        public DateTimeOffset NextWorkItems { get; set; }

        public DateTimeOffset NextBuilds { get; set; }

        public DateTimeOffset NextMetadata { get; set; }

        public DateTimeOffset NextReconcile { get; set; }

        public DateTimeOffset? BlockedUntil { get; set; }

        public bool BlockedByRateLimit { get; set; }

        public bool Suspended { get; set; }

        public bool ManualPending { get; set; }

        /// <summary>Signature of the milestone/label references unknown to the last accepted snapshot.</summary>
        public string? LastUnknownReferences { get; set; }

        public int Failures { get; set; }

        public bool CatchUpPending { get; set; }

        public DateTimeOffset LastPersistedAt { get; set; }

        public Task? Running { get; set; }

        public CancellationTokenSource? RunCancellation { get; set; }

        public void ResetFailureState()
        {
            Failures = 0;
            Suspended = false;
            BlockedUntil = null;
            BlockedByRateLimit = false;
        }

        public void MarkAllDue(DateTimeOffset now) => NextWorkItems = NextBuilds = NextMetadata = NextReconcile = now;

        public void ScheduleAfterSuccess(RefreshScope scope, DateTimeOffset now, RepositoryConfiguration configuration, double stretch, Func<TimeSpan, TimeSpan> jitter)
        {
            DateTimeOffset Next(TimeSpan interval)
            {
                var effective = TimeSpan.FromTicks((long)(Math.Max(interval.Ticks, RepositoryConfiguration.MinimumInterval.Ticks) * stretch));
                // ±10 % jitter (architecture §11).
                var tenth = TimeSpan.FromTicks(effective.Ticks / 10);
                return now + effective - tenth + jitter(tenth * 2);
            }

            if (scope.HasFlag(RefreshScope.WorkItems) || scope.HasFlag(RefreshScope.Reconcile))
            {
                NextWorkItems = Next(configuration.IssuePollingInterval);
            }

            if (scope.HasFlag(RefreshScope.Builds))
            {
                NextBuilds = Next(configuration.BuildPollingInterval);
            }

            if (scope.HasFlag(RefreshScope.Metadata))
            {
                NextMetadata = Next(configuration.MetadataPollingInterval);
            }

            if (scope.HasFlag(RefreshScope.Reconcile))
            {
                NextReconcile = Next(configuration.ReconciliationInterval);
            }
        }
    }
}

/// <summary>
/// Auto-resetting asynchronous signal.
/// </summary>
internal sealed class AsyncSignal
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Set()
    {
        lock (_gate)
        {
            _tcs.TrySetResult();
        }
    }

    /// <summary>Waits for the signal or the timeout; resets the signal afterwards.</summary>
    public async Task WaitAsync(TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        Task signal;
        lock (_gate)
        {
            signal = _tcs.Task;
        }

        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout, time, delayCancellation.Token);
        await Task.WhenAny(signal, delay).ConfigureAwait(false);
        await delayCancellation.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_tcs.Task.IsCompleted)
            {
                _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
