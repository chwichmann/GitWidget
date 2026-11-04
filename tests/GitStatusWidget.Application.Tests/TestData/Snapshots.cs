using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Labels;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Domain.Snapshots;

namespace GitStatusWidget.Application.Tests.TestData;

/// <summary>Builders for provider-neutral test snapshots.</summary>
internal static class Snapshots
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    public static readonly RepositoryIdentity Repo = new(RepositoryProviderKind.GitHub, "github.com", "octo", "widget", "R_1");

    public static RepositorySnapshot Snapshot(
        DateTimeOffset retrievedAt,
        IEnumerable<IssueSnapshot>? issues = null,
        IEnumerable<MilestoneSnapshot>? milestones = null,
        IEnumerable<ChangeRequestSnapshot>? changeRequests = null,
        IEnumerable<BuildSnapshot>? builds = null)
    {
        var issueList = (issues ?? []).ToList();
        return new RepositorySnapshot(
            Repo,
            retrievedAt,
            "main",
            RepositoryFeatures.All,
            new IssueCounts(issueList.Count(i => i.State == WorkItemState.Open), issueList.Count(i => i.State == WorkItemState.Closed)),
            issueList,
            retrievedAt.AddDays(-14),
            (milestones ?? []).ToList(),
            [],
            (changeRequests ?? []).ToList(),
            (builds ?? []).ToList());
    }

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

    public static MilestoneSnapshot Milestone(string id, int open, int closed, bool isClosed = false) =>
        new(id, $"Milestone {id}", null, null, null, isClosed, open, closed);

    public static ChangeRequestSnapshot ChangeRequest(int number, ChangeRequestState state, DateTimeOffset createdAt, DateTimeOffset? updatedAt = null) =>
        new($"PR_{number}", number, ChangeRequestKind.PullRequest, $"PR {number}", state, false,
            new Uri($"https://github.com/octo/widget/pull/{number}"), createdAt, updatedAt ?? createdAt);

    public static BuildSnapshot Build(string id, BuildState state, DateTimeOffset createdAt, DateTimeOffset? updatedAt = null) =>
        new(id, "CI", state, "main", "abc123", new Uri($"https://github.com/octo/widget/actions/runs/{id}"), createdAt,
            state is BuildState.Succeeded or BuildState.Failed or BuildState.Canceled ? updatedAt ?? createdAt : null,
            updatedAt ?? createdAt);
}
