using System.Collections.Concurrent;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Comparison;
using GitStatusWidget.Application.Persistence;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Events;
using GitStatusWidget.Domain.Health;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Labels;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Domain.Snapshots;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static GitStatusWidget.Application.Tests.TestData.Snapshots;

namespace GitStatusWidget.Application.Tests.Synchronization;

public sealed class SynchronizationEngineTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(T0);
    private readonly FakeProvider _provider = new();
    private readonly InMemoryCache _cache = new();
    private readonly RepositoryStatusHub _hub = new();
    private readonly List<RepositoryChangesDetected> _changes = [];
    private readonly SynchronizationEngine _engine;
    private readonly RepositoryConfiguration _config;

    public SynchronizationEngineTests()
    {
        _engine = new SynchronizationEngine(
            new RepositoryStatusProviderResolver([_provider]), _cache, new SnapshotComparer(), _hub, _time,
            NullLogger<SynchronizationEngine>.Instance, new Random(42));
        _hub.ChangesDetected += (_, e) => _changes.Add(e);
        var id = Guid.NewGuid();
        _config = new RepositoryConfiguration(id, RepositoryProviderKind.GitHub, new Uri("https://github.com/"), "octo", "widget",
            RepositoryConfiguration.DefaultCredentialReference(id), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), true);
    }

    public void Dispose() => _engine.Dispose();

    private RepositoryRuntimeState State => _hub.Find(_config.Id)!;

    private async Task StartAsync(RepositoryConfiguration? config = null)
    {
        await _engine.ApplySettingsAsync(new AppSettings { Repositories = [config ?? _config] }, CancellationToken.None);
    }

    private async Task RunDueAsync(TimeSpan? advance = null)
    {
        if (advance is { } a)
        {
            _time.Advance(a);
        }

        _engine.StartDueRefreshes(CancellationToken.None);
        await _engine.WhenIdleAsync();
    }

    [Fact]
    public async Task First_refresh_establishes_baseline_without_events()
    {
        _provider.Enqueue(_ => FakeProvider.Full([Issue(1), Issue(2)]));
        await StartAsync();

        await RunDueAsync(TimeSpan.FromSeconds(6));

        _provider.Contexts.Single().Scope.Should().Be(RefreshScope.All);
        State.Health.Should().Be(RepositoryHealthState.Healthy);
        State.Snapshot!.IssueCounts.Open.Should().Be(2);
        _changes.Should().BeEmpty();
        _cache.Saved.Should().ContainKey(_config.Id);
    }

    [Fact]
    public async Task Later_changes_are_published_once_even_if_the_provider_repeats_itself()
    {
        _provider.Enqueue(_ => FakeProvider.Full([Issue(1), Issue(2)]));
        await StartAsync();
        await RunDueAsync(TimeSpan.FromSeconds(6));

        var closed = Issue(2, WorkItemState.Closed, updatedAt: _time.GetUtcNow().AddSeconds(10));
        _provider.Enqueue(_ => FakeProvider.Delta([closed], closedCount: 101));
        _provider.Enqueue(_ => FakeProvider.Delta([closed], closedCount: 101));

        await RunDueAsync(TimeSpan.FromSeconds(40));
        await RunDueAsync(TimeSpan.FromSeconds(40));

        _changes.Should().ContainSingle().Which.Events.Should().ContainSingle(e => e.Type == ChangeEventType.IssueClosed);
        _changes[0].IsCatchUp.Should().BeFalse();
        State.Snapshot!.IssueCounts.Should().Be(new IssueCounts(1, 101));
    }

    [Fact]
    public async Task Section_intervals_decide_the_scope()
    {
        _provider.Enqueue(_ => FakeProvider.Full([Issue(1)]));
        await StartAsync();
        await RunDueAsync(TimeSpan.FromSeconds(6));

        _provider.EnqueueRepeating(_ => FakeProvider.Delta([]));
        await RunDueAsync(TimeSpan.FromSeconds(35));
        _provider.Contexts.Last().Scope.Should().Be(RefreshScope.WorkItems | RefreshScope.Builds);

        await RunDueAsync(TimeSpan.FromMinutes(5));
        _provider.Contexts.Last().Scope.Should().HaveFlag(RefreshScope.Metadata).And.NotHaveFlag(RefreshScope.Reconcile);

        await RunDueAsync(TimeSpan.FromMinutes(11));
        _provider.Contexts.Last().Scope.Should().HaveFlag(RefreshScope.Reconcile);
    }

    [Fact]
    public async Task Rate_limit_pauses_polling_until_retry_time_and_manual_refresh_respects_it()
    {
        _provider.Enqueue(_ => FakeProvider.Full([Issue(1)]));
        await StartAsync();
        await RunDueAsync(TimeSpan.FromSeconds(6));
        var retryAt = _time.GetUtcNow().AddMinutes(10);
        _provider.Enqueue(_ => new ProviderFetchResult.Failure(ProviderFailureKind.RateLimited, "limited", retryAt));

        await RunDueAsync(TimeSpan.FromSeconds(35));
        State.Health.Should().Be(RepositoryHealthState.RateLimited);
        var calls = _provider.Contexts.Count;

        _engine.RequestRefresh();
        await RunDueAsync(TimeSpan.FromMinutes(5));
        _provider.Contexts.Should().HaveCount(calls, "the provider-imposed wait is still active");

        _provider.EnqueueRepeating(_ => FakeProvider.Delta([]));
        await RunDueAsync(TimeSpan.FromMinutes(6));
        _provider.Contexts.Should().HaveCount(calls + 1);
        State.Health.Should().Be(RepositoryHealthState.Healthy);
    }

    [Fact]
    public async Task Authentication_failure_suspends_until_manual_refresh()
    {
        _provider.Enqueue(_ => new ProviderFetchResult.Failure(ProviderFailureKind.AuthenticationFailed, "401"));
        await StartAsync();
        await RunDueAsync(TimeSpan.FromSeconds(6));
        State.Health.Should().Be(RepositoryHealthState.AuthenticationFailed);

        await RunDueAsync(TimeSpan.FromMinutes(30));
        _provider.Contexts.Should().HaveCount(1);

        _provider.Enqueue(_ => FakeProvider.Full([Issue(1)]));
        _engine.RequestRefresh(_config.Id);
        await RunDueAsync();
        State.Health.Should().Be(RepositoryHealthState.Healthy);
    }

    [Fact]
    public async Task Transient_failures_back_off_and_keep_the_previous_snapshot()
    {
        _provider.Enqueue(_ => FakeProvider.Full([Issue(1)]));
        await StartAsync();
        await RunDueAsync(TimeSpan.FromSeconds(6));
        var baseline = State.Snapshot;
        _provider.EnqueueRepeating(_ => new ProviderFetchResult.Failure(ProviderFailureKind.Offline, "offline"));

        await RunDueAsync(TimeSpan.FromSeconds(35));
        State.Health.Should().Be(RepositoryHealthState.Offline);
        State.Snapshot.Should().BeSameAs(baseline);
        var firstBlock = State.BlockedUntil!.Value - _time.GetUtcNow();

        await RunDueAsync(firstBlock + TimeSpan.FromSeconds(1));
        var secondBlock = State.BlockedUntil!.Value - _time.GetUtcNow();
        secondBlock.Should().BeGreaterThan(firstBlock);
    }

    [Fact]
    public async Task Rejected_snapshot_keeps_previous_and_forces_reconciliation()
    {
        _provider.Enqueue(_ => FakeProvider.Full([Issue(1), Issue(2)]));
        await StartAsync();
        await RunDueAsync(TimeSpan.FromSeconds(6));
        var baseline = State.Snapshot;

        // Full open set with 1 item while the count source says 300 → rejected (partial data).
        _provider.Enqueue(_ => FakeProvider.Full([Issue(1)], openCount: 300));
        _engine.RequestRefresh();
        await RunDueAsync();

        State.Health.Should().Be(RepositoryHealthState.ProviderError);
        State.LastFailure.Should().Be(ProviderFailureKind.Partial);
        State.Snapshot.Should().BeSameAs(baseline);
        _changes.Should().BeEmpty();
    }

    [Fact]
    public async Task Changes_after_an_old_cache_are_flagged_as_catch_up()
    {
        var old = Snapshot(T0.AddHours(-5), [Issue(1)]);
        _cache.Saved[_config.Id] = new CachedRepositoryState(old, ProviderSyncState.Empty, new Notifications.EventLedger());
        await StartAsync();
        State.Snapshot.Should().BeSameAs(old, "the cached dashboard is shown immediately");
        State.Health.Should().Be(RepositoryHealthState.Stale);

        _provider.Enqueue(_ => FakeProvider.Delta([Issue(1, WorkItemState.Closed, updatedAt: T0.AddMinutes(-30))], closedCount: 1));
        await RunDueAsync(TimeSpan.FromSeconds(6));

        _changes.Should().ContainSingle().Which.IsCatchUp.Should().BeTrue();
    }

    [Fact]
    public async Task Removing_a_repository_removes_its_state()
    {
        _provider.Enqueue(_ => FakeProvider.Full([Issue(1)]));
        await StartAsync();
        await RunDueAsync(TimeSpan.FromSeconds(6));

        await _engine.ApplySettingsAsync(new AppSettings(), CancellationToken.None);

        _hub.States.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_one_refresh_per_repository_runs_at_a_time()
    {
        var gate = new TaskCompletionSource();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.Enqueue(async _ =>
        {
            called.TrySetResult();
            await gate.Task;
            return FakeProvider.Full([Issue(1)]);
        });
        await StartAsync();
        _time.Advance(TimeSpan.FromSeconds(6));
        _engine.StartDueRefreshes(CancellationToken.None);
        await called.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _engine.RequestRefresh();
        _engine.StartDueRefreshes(CancellationToken.None);
        await Task.Delay(100);

        _provider.Contexts.Should().HaveCount(1, "a second refresh must not start while the first is running");
        gate.SetResult();
        await _engine.WhenIdleAsync();
        State.Health.Should().Be(RepositoryHealthState.Healthy);
    }

    [Fact]
    public async Task Staleness_is_reported_when_refreshes_stop_succeeding()
    {
        _provider.Enqueue(_ => FakeProvider.Full([Issue(1)]));
        await StartAsync();
        await RunDueAsync(TimeSpan.FromSeconds(6));

        _time.Advance(TimeSpan.FromMinutes(6));
        _engine.UpdateStaleness();

        State.Health.Should().Be(RepositoryHealthState.Stale);
    }

    [Fact]
    public async Task Issue_referencing_an_unknown_milestone_triggers_a_metadata_refresh_right_away()
    {
        // Issue #8: a milestone created after the last metadata poll must not wait for the metadata interval.
        _provider.Enqueue(_ => FakeProvider.Full([Issue(1)]));
        await StartAsync();
        await RunDueAsync(TimeSpan.FromSeconds(6));
        var assigned = Issue(2, updatedAt: _time.GetUtcNow().AddSeconds(5), milestone: new MilestoneReference("M9", "0.1.2"));
        _provider.Enqueue(_ => FakeProvider.Delta([assigned]));
        _provider.EnqueueRepeating(_ => FakeProvider.Delta([]));

        await RunDueAsync(TimeSpan.FromSeconds(35));
        _provider.Contexts.Last().Scope.Should().NotHaveFlag(RefreshScope.Metadata);

        await RunDueAsync();

        _provider.Contexts.Last().Scope.Should().HaveFlag(RefreshScope.Metadata, "the unknown milestone is fetched without waiting 5 minutes");
    }

    [Fact]
    public async Task Unresolvable_reference_does_not_cause_repeated_metadata_refreshes()
    {
        _provider.Enqueue(_ => FakeProvider.Full([Issue(1, milestone: new MilestoneReference("GONE", "deleted"))]));
        _provider.EnqueueRepeating(_ => FakeProvider.Delta([]));
        await StartAsync();
        await RunDueAsync(TimeSpan.FromSeconds(6));
        var calls = _provider.Contexts.Count;

        await RunDueAsync();
        await RunDueAsync();

        _provider.Contexts.Should().HaveCount(calls, "the first (full) refresh already included metadata and the reference stays unknown");
    }

    [Fact]
    public async Task Manual_refresh_during_a_narrower_scheduled_refresh_runs_afterwards_with_metadata()
    {
        _provider.Enqueue(_ => FakeProvider.Full([Issue(1)]));
        await StartAsync();
        await RunDueAsync(TimeSpan.FromSeconds(6));
        var gate = new TaskCompletionSource();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.Enqueue(async _ =>
        {
            called.TrySetResult();
            await gate.Task;
            return FakeProvider.Delta([]);
        });
        _provider.EnqueueRepeating(_ => FakeProvider.Delta([]));
        _time.Advance(TimeSpan.FromSeconds(35));
        _engine.StartDueRefreshes(CancellationToken.None);
        await called.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _provider.Contexts.Last().Scope.Should().NotHaveFlag(RefreshScope.Metadata);

        _engine.RequestRefresh(); // user clicks "Refresh now" while the scheduled refresh runs
        gate.SetResult();
        await _engine.WhenIdleAsync();
        await RunDueAsync();

        _provider.Contexts.Last().Scope.Should().HaveFlag(RefreshScope.Metadata);
    }

    [Fact]
    public async Task Gateway_interception_blocks_polling_until_refresh_resume_or_user_agent_change()
    {
        // Issue #10: retrying with the same User-Agent cannot succeed.
        _provider.EnqueueRepeating(_ => new ProviderFetchResult.Failure(ProviderFailureKind.GatewayInterception, "Redirected to another host") { GatewayHost = "portal.example.net" });
        await StartAsync();
        await RunDueAsync(TimeSpan.FromSeconds(6));

        State.Health.Should().Be(RepositoryHealthState.GatewayBlocked);
        State.GatewayHost.Should().Be("portal.example.net");
        await RunDueAsync(TimeSpan.FromMinutes(30));
        _provider.Contexts.Should().HaveCount(1, "no automatic retries while blocked");

        _engine.RequestRefresh(_config.Id);
        await RunDueAsync();
        _provider.Contexts.Should().HaveCount(2);

        _engine.NotifyResume();
        await RunDueAsync(TimeSpan.FromSeconds(20));
        _provider.Contexts.Should().HaveCount(3, "a resume from standby retries (VPN/DNS may have changed)");

        _provider.EnqueueRepeating(_ => FakeProvider.Full([Issue(1)]));
        await _engine.ApplySettingsAsync(new AppSettings { Repositories = [_config with { UserAgent = "GitWidget/0.1.2 git/2.47.0" }] }, CancellationToken.None);
        await RunDueAsync();
        State.Health.Should().Be(RepositoryHealthState.Healthy);
        State.GatewayHost.Should().BeNull();
    }

    private sealed class FakeProvider : IRepositoryStatusProvider
    {
        private readonly Queue<Func<SnapshotRequestContext, Task<ProviderFetchResult>>> _responses = new();
        private Func<SnapshotRequestContext, Task<ProviderFetchResult>>? _repeating;

        public RepositoryProviderKind Kind => RepositoryProviderKind.GitHub;

        public ConcurrentQueue<SnapshotRequestContext> ContextQueue { get; } = new();

        public IReadOnlyList<SnapshotRequestContext> Contexts => [.. ContextQueue];

        public void Enqueue(Func<SnapshotRequestContext, ProviderFetchResult> response) => _responses.Enqueue(c => Task.FromResult(response(c)));

        public void Enqueue(Func<SnapshotRequestContext, Task<ProviderFetchResult>> response) => _responses.Enqueue(response);

        public void EnqueueRepeating(Func<SnapshotRequestContext, ProviderFetchResult> response) => _repeating = c => Task.FromResult(response(c));

        public Task<ProviderConnectionResult> TestConnectionAsync(RepositoryConfiguration configuration, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderConnectionResult.Ok(Repo, "ok"));

        public Task<ProviderFetchResult> FetchAsync(RepositoryConfiguration configuration, SnapshotRequestContext context, CancellationToken cancellationToken)
        {
            ContextQueue.Enqueue(context);
            var response = _responses.Count > 0 ? _responses.Dequeue() : _repeating ?? throw new InvalidOperationException("No response queued.");
            return response(context);
        }

        public static ProviderFetchResult Full(IReadOnlyList<IssueSnapshot> open, int? openCount = null) => new ProviderFetchResult.Success(
            new ProviderFetch(Repo, "main", RepositoryFeatures.All,
                WorkItemUpdate<IssueSnapshot>.Full(open, [], T0.AddDays(-14)),
                WorkItemUpdate<ChangeRequestSnapshot>.Full([], [], T0.AddDays(-14)),
                SectionUpdate<MilestoneSnapshot>.Replace([]),
                SectionUpdate<LabelSnapshot>.Replace([]),
                SectionUpdate<BuildSnapshot>.Replace([]),
                new CountsUpdate(openCount ?? open.Count, 100, new Dictionary<string, int>())),
            ProviderSyncState.Empty);

        public static ProviderFetchResult Delta(IReadOnlyList<IssueSnapshot> changed, int? closedCount = null) => new ProviderFetchResult.Success(
            new ProviderFetch(Repo, "main", RepositoryFeatures.All,
                WorkItemUpdate<IssueSnapshot>.Delta(changed),
                WorkItemUpdate<ChangeRequestSnapshot>.Unchanged,
                SectionUpdate<MilestoneSnapshot>.Unchanged,
                SectionUpdate<LabelSnapshot>.Unchanged,
                SectionUpdate<BuildSnapshot>.Unchanged,
                closedCount is { } closed ? new CountsUpdate(null, closed, new Dictionary<string, int>()) : null),
            ProviderSyncState.Empty);
    }

    private sealed class InMemoryCache : IRepositoryCache
    {
        public ConcurrentDictionary<Guid, CachedRepositoryState> Saved { get; } = new();

        public Task<CachedRepositoryState> LoadAsync(Guid configurationId, CancellationToken cancellationToken) =>
            Task.FromResult(Saved.TryGetValue(configurationId, out var state) ? state : CachedRepositoryState.Empty);

        public Task SaveAsync(Guid configurationId, CachedRepositoryState state, CancellationToken cancellationToken)
        {
            Saved[configurationId] = state;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid configurationId, CancellationToken cancellationToken)
        {
            Saved.TryRemove(configurationId, out _);
            return Task.CompletedTask;
        }
    }
}
