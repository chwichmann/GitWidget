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

namespace GitStatusWidget.Desktop.Tests.TestData;

/// <summary>Builders for configurations, snapshots, states and events (adapted from the Application tests).</summary>
internal static class Data
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    public static RepositoryConfiguration Config(
        Guid? id = null,
        RepositoryProviderKind provider = RepositoryProviderKind.GitHub,
        string owner = "octo",
        string name = "widget",
        int sortOrder = 0,
        string? displayName = null,
        string? server = null)
    {
        var configurationId = id ?? Guid.NewGuid();
        return new RepositoryConfiguration(
            configurationId,
            provider,
            new Uri(server ?? (provider == RepositoryProviderKind.GitLab ? "https://gitlab.com/" : "https://github.com/")),
            owner,
            name,
            RepositoryConfiguration.DefaultCredentialReference(configurationId),
            RepositoryConfiguration.DefaultIssueInterval,
            RepositoryConfiguration.DefaultBuildInterval,
            RepositoryConfiguration.DefaultMetadataInterval,
            RepositoryConfiguration.DefaultReconciliationInterval,
            true)
        {
            SortOrder = sortOrder,
            DisplayName = displayName,
        };
    }

    public static RepositoryIdentity Identity(RepositoryConfiguration configuration) =>
        new(configuration.Provider, configuration.ServerUri.Host, configuration.OwnerPath, configuration.RepositoryName, $"R_{configuration.RepositoryName}");

    public static RepositorySnapshot Snapshot(
        RepositoryConfiguration configuration,
        DateTimeOffset? retrievedAt = null,
        IEnumerable<IssueSnapshot>? issues = null,
        IEnumerable<MilestoneSnapshot>? milestones = null,
        IEnumerable<ChangeRequestSnapshot>? changeRequests = null,
        IEnumerable<BuildSnapshot>? builds = null,
        IEnumerable<LabelSnapshot>? labels = null,
        string? defaultBranch = "main")
    {
        var at = retrievedAt ?? T0;
        var issueList = (issues ?? []).ToList();
        return new RepositorySnapshot(
            Identity(configuration),
            at,
            defaultBranch,
            RepositoryFeatures.All,
            new IssueCounts(issueList.Count(i => i.State == WorkItemState.Open), issueList.Count(i => i.State == WorkItemState.Closed)),
            issueList,
            at.AddDays(-14),
            (milestones ?? []).ToList(),
            (labels ?? []).ToList(),
            (changeRequests ?? []).ToList(),
            (builds ?? []).ToList());
    }

    public static RepositoryRuntimeState State(
        RepositoryConfiguration configuration,
        RepositorySnapshot? snapshot = null,
        RepositoryHealthState health = RepositoryHealthState.Healthy,
        DateTimeOffset? lastSuccessAt = null) =>
        new(configuration, snapshot, health) { LastSuccessAt = lastSuccessAt ?? (snapshot is null ? null : snapshot.RetrievedAt) };

    public static IssueSnapshot Issue(
        int number,
        WorkItemState state = WorkItemState.Open,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null,
        string? title = null,
        string[]? labels = null,
        MilestoneReference? milestone = null,
        string[]? assignees = null)
    {
        var created = createdAt ?? T0.AddDays(-30);
        var updated = updatedAt ?? created;
        return new IssueSnapshot(
            $"I_{number}",
            number,
            title ?? $"Issue {number}",
            state,
            new Uri($"https://github.com/octo/widget/issues/{number}"),
            created,
            updated,
            state == WorkItemState.Closed ? updated : null,
            null,
            (assignees ?? []).Select(a => new UserSummary($"U_{a}", a, null)).ToList(),
            milestone,
            (labels ?? []).Select(l => new LabelReference(l, "#ff0000")).ToList());
    }

    public static MilestoneSnapshot Milestone(string id, int open, int closed, bool isClosed = false, DateOnly? due = null) =>
        new(id, $"Milestone {id}", null, null, due, isClosed, open, closed);

    public static ChangeRequestSnapshot ChangeRequest(int number, ChangeRequestState state = ChangeRequestState.Open, DateTimeOffset? createdAt = null) =>
        new($"PR_{number}", number, ChangeRequestKind.PullRequest, $"PR {number}", state, false,
            new Uri($"https://github.com/octo/widget/pull/{number}"), createdAt ?? T0.AddDays(-1), createdAt ?? T0.AddDays(-1));

    public static BuildSnapshot Build(string id, BuildState state, DateTimeOffset createdAt, string? refName = "main") =>
        new(id, "CI", state, refName, "abc123def456", new Uri($"https://github.com/octo/widget/actions/runs/{id}"), createdAt,
            state is BuildState.Succeeded or BuildState.Failed or BuildState.Canceled ? createdAt : null,
            createdAt);

    public static RepositoryChangeEvent Event(
        ChangeEventType type,
        EventPriority priority = EventPriority.Medium,
        int? number = 1,
        string? title = null,
        DateTimeOffset? occurredAt = null,
        RepositoryProviderKind provider = RepositoryProviderKind.GitHub,
        string? detail = null) =>
        new(
            $"{type}-{number}-{occurredAt?.UtcTicks ?? 0}",
            type,
            priority,
            new RepositoryIdentity(provider, "github.com", "octo", "widget", "R_widget"),
            $"E_{number}",
            number,
            title ?? $"Entity {number}",
            new Uri($"https://github.com/octo/widget/issues/{number}"),
            occurredAt ?? T0,
            detail);
}
