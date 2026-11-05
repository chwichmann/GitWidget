using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Notifications;
using GitStatusWidget.Application.Persistence;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Labels;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Domain.Snapshots;
using GitStatusWidget.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace GitStatusWidget.Infrastructure.Tests.Persistence;

public sealed class PersistenceTests : IDisposable
{
    private static readonly DateTimeOffset T = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gitwidget-tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public PersistenceTests() => _paths = new AppPaths(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static RepositorySnapshot SampleSnapshot()
    {
        var repo = new RepositoryIdentity(RepositoryProviderKind.GitLab, "gitlab.example.com", "group/sub", "project", "99");
        var milestone = new MilestoneReference("7", "0.10.1 Network Services");
        var issue = new IssueSnapshot("1001", 12, "Title with \"quotes\" and ümlauts", WorkItemState.Open,
            new Uri("https://gitlab.example.com/group/sub/project/-/work_items/12"), T.AddDays(-3), T, null,
            new UserSummary("5", "ann", "Ann"), [new UserSummary("6", "bob", null)], milestone, [new LabelReference("Bug", "#ff0000")]);
        return new RepositorySnapshot(
            repo, T, "master", new RepositoryFeatures(true, true, false), new IssueCounts(1, 1134),
            [issue, issue with { ExternalId = "1002", Number = 13, State = WorkItemState.Closed, ClosedAt = T }],
            T.AddDays(-14),
            [new MilestoneSnapshot("7", "0.10.1 Network Services", null, new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 1), false, 1, 602)],
            [new LabelSnapshot("Bug", "#ff0000", "Something is broken", 1, null)],
            [new ChangeRequestSnapshot("2001", 4, ChangeRequestKind.MergeRequest, "MR", ChangeRequestState.Merged, false, new Uri("https://gitlab.example.com/mr/4"), T, T)],
            [new BuildSnapshot("3001", "Pipeline #1", BuildState.Failed, "master", "abc", new Uri("https://gitlab.example.com/p/1"), T, T, T)]);
    }

    [Fact]
    public async Task Snapshot_sync_state_and_ledger_round_trip()
    {
        using var cache = new JsonRepositoryCache(_paths, NullLogger<JsonRepositoryCache>.Instance);
        var id = Guid.NewGuid();
        var snapshot = SampleSnapshot();
        var sync = ProviderSyncState.Empty.WithValues(("issuesWatermark", T.ToString("O"))).WithRequests([new ConditionalRequestState("labels", "W/\"x\"", null, T, 1)]);
        var ledger = new EventLedger(10, ["a", "b"]);

        await cache.SaveAsync(id, new CachedRepositoryState(snapshot, sync, ledger), CancellationToken.None);
        var loaded = await cache.LoadAsync(id, CancellationToken.None);

        loaded.Snapshot.Should().BeEquivalentTo(snapshot);
        loaded.SyncState.GetValue("issuesWatermark").Should().Be(T.ToString("O"));
        loaded.SyncState.GetRequest("labels")!.ETag.Should().Be("W/\"x\"");
        loaded.Ledger.Keys.Should().Equal("a", "b");
        Directory.GetFiles(_paths.CacheDirectory(id), "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_cache_returns_empty_state()
    {
        using var cache = new JsonRepositoryCache(_paths, NullLogger<JsonRepositoryCache>.Instance);

        var loaded = await cache.LoadAsync(Guid.NewGuid(), CancellationToken.None);

        loaded.Snapshot.Should().BeNull();
        loaded.SyncState.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task Corrupt_snapshot_is_moved_aside_and_sync_state_is_discarded()
    {
        using var cache = new JsonRepositoryCache(_paths, NullLogger<JsonRepositoryCache>.Instance);
        var id = Guid.NewGuid();
        await cache.SaveAsync(id, new CachedRepositoryState(SampleSnapshot(), ProviderSyncState.Empty.WithValues(("w", "1")), new EventLedger()), CancellationToken.None);
        var snapshotFile = Path.Combine(_paths.CacheDirectory(id), "snapshot.json");
        await File.WriteAllTextAsync(snapshotFile, "{\"schemaVersion\":1,\"data\":{trunc");

        var loaded = await cache.LoadAsync(id, CancellationToken.None);

        loaded.Snapshot.Should().BeNull();
        loaded.SyncState.Values.Should().BeEmpty("watermarks without their snapshot would skip data");
        Directory.GetFiles(_paths.CacheDirectory(id), "snapshot.json.corrupt-*").Should().ContainSingle();
    }

    [Fact]
    public async Task Future_cache_version_is_ignored_but_left_in_place()
    {
        using var cache = new JsonRepositoryCache(_paths, NullLogger<JsonRepositoryCache>.Instance);
        var id = Guid.NewGuid();
        Directory.CreateDirectory(_paths.CacheDirectory(id));
        var snapshotFile = Path.Combine(_paths.CacheDirectory(id), "snapshot.json");
        await File.WriteAllTextAsync(snapshotFile, "{\"schemaVersion\":99,\"data\":{}}");

        var loaded = await cache.LoadAsync(id, CancellationToken.None);

        loaded.Snapshot.Should().BeNull();
        File.Exists(snapshotFile).Should().BeTrue();
    }

    [Fact]
    public async Task Settings_round_trip_without_secrets()
    {
        using var store = new JsonSettingsStore(_paths, NullLogger<JsonSettingsStore>.Instance);
        var id = Guid.NewGuid();
        var settings = new AppSettings
        {
            AlwaysOnTop = true,
            Repositories =
            [
                new RepositoryConfiguration(id, RepositoryProviderKind.GitLab, new Uri("https://gitlab.example.com/"), "group", "project",
                    RepositoryConfiguration.DefaultCredentialReference(id), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
                    TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), true)
                {
                    UserAgent = "GitWidget/0.1 git/2.47.0",
                    HighlightedLabels = ["Bug", "Security"],
                },
            ],
        };

        await store.SaveAsync(settings, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        loaded.Should().BeEquivalentTo(settings);
        var text = await File.ReadAllTextAsync(_paths.SettingsFile);
        text.Should().Contain("\"schemaVersion\": 2").And.Contain($"GitStatusWidget/{id:D}").And.NotContainAny("token\":", "PRIVATE-TOKEN", "Bearer");
    }

    [Fact]
    public async Task Missing_settings_yield_defaults()
    {
        using var store = new JsonSettingsStore(_paths, NullLogger<JsonSettingsStore>.Instance);

        var loaded = await store.LoadAsync(CancellationToken.None);

        loaded.Should().BeEquivalentTo(new AppSettings());
    }

    [Fact]
    public async Task Future_settings_version_throws_instead_of_being_overwritten()
    {
        using var store = new JsonSettingsStore(_paths, NullLogger<JsonSettingsStore>.Instance);
        Directory.CreateDirectory(_paths.Root);
        await File.WriteAllTextAsync(_paths.SettingsFile, "{\"schemaVersion\":3,\"data\":{}}");

        var act = () => store.LoadAsync(CancellationToken.None);

        await act.Should().ThrowAsync<UnsupportedSchemaVersionException>();
    }

    [Fact]
    public async Task Settings_version_1_are_migrated_without_losing_the_selected_milestone()
    {
        // Issue #12: selectedMilestoneId (v1) becomes manual mode with one milestone; no selection stays automatic (N = 1).
        using var store = new JsonSettingsStore(_paths, NullLogger<JsonSettingsStore>.Instance);
        Directory.CreateDirectory(_paths.Root);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await File.WriteAllTextAsync(_paths.SettingsFile, $$$"""
            {"schemaVersion":1,"data":{"repositories":[
              {"id":"{{{a}}}","provider":"GitLab","serverUri":"https://gitlab.example.com/","ownerPath":"group","repositoryName":"project",
               "credentialReference":"GitStatusWidget/{{{a}}}","issuePollingInterval":"00:00:30","buildPollingInterval":"00:00:30",
               "metadataPollingInterval":"00:05:00","reconciliationInterval":"00:15:00","isEnabled":true,"selectedMilestoneId":"42"},
              {"id":"{{{b}}}","provider":"GitHub","serverUri":"https://github.com/","ownerPath":"octo","repositoryName":"widget",
               "credentialReference":"GitStatusWidget/{{{b}}}","issuePollingInterval":"00:00:30","buildPollingInterval":"00:00:30",
               "metadataPollingInterval":"00:05:00","reconciliationInterval":"00:15:00","isEnabled":true}
            ],"alwaysOnTop":true}}
            """);

        var settings = await store.LoadAsync(CancellationToken.None);

        settings.AlwaysOnTop.Should().BeTrue();
        var gitlab = settings.Repositories.Single(r => r.Id == a);
        gitlab.MilestoneMode.Should().Be(MilestoneSelectionMode.Manual);
        gitlab.SelectedMilestoneIds.Should().Equal("42");
        var github = settings.Repositories.Single(r => r.Id == b);
        github.MilestoneMode.Should().Be(MilestoneSelectionMode.Automatic);
        github.AutomaticMilestoneCount.Should().Be(1);
    }

    [Fact]
    public async Task Milestone_counts_outside_the_range_are_clamped_when_loading()
    {
        using var store = new JsonSettingsStore(_paths, NullLogger<JsonSettingsStore>.Instance);
        var id = Guid.NewGuid();
        var configuration = new RepositoryConfiguration(id, RepositoryProviderKind.GitHub, new Uri("https://github.com/"), "octo", "widget",
            RepositoryConfiguration.DefaultCredentialReference(id), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), true)
        {
            AutomaticMilestoneCount = 12,
            SelectedMilestoneIds = ["1", "2", "3", "4", "5", "6"],
        };
        await store.SaveAsync(new AppSettings { Repositories = [configuration] }, CancellationToken.None);

        var loaded = (await store.LoadAsync(CancellationToken.None)).Repositories.Single();

        loaded.AutomaticMilestoneCount.Should().Be(5);
        loaded.SelectedMilestoneIds.Should().HaveCount(5);
    }

    [Fact]
    public void Ledger_is_bounded_and_deduplicates()
    {
        var ledger = new EventLedger(3);

        ledger.TryAdd("a").Should().BeTrue();
        ledger.TryAdd("a").Should().BeFalse();
        ledger.TryAdd("b");
        ledger.TryAdd("c");
        ledger.TryAdd("d");

        ledger.Keys.Should().Equal("b", "c", "d");
        ledger.Contains("a").Should().BeFalse();
    }
}
