using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Labels;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Domain.Snapshots;

/// <summary>
/// A complete, validated view of a repository at one point in time. Only complete snapshots exist as
/// instances of this type; partial refreshes are represented as failed results and never compared.
/// </summary>
/// <remarks>
/// To scale to large repositories the snapshot does not contain every closed issue. It contains
/// <b>all open issues</b> plus <b>every closed issue updated at or after <see cref="ClosedIssuesSince"/></b>.
/// Repository totals come from <see cref="IssueCounts"/>. The same rule applies to change requests.
/// </remarks>
/// <param name="Repository">Repository identity.</param>
/// <param name="RetrievedAt">Time the snapshot was retrieved (UTC).</param>
/// <param name="DefaultBranch">Default branch name, if known.</param>
/// <param name="Features">Available data sections.</param>
/// <param name="IssueCounts">Repository-wide issue totals.</param>
/// <param name="Issues">All open issues and closed issues updated since <paramref name="ClosedIssuesSince"/>.</param>
/// <param name="ClosedIssuesSince">Lower bound of the closed-issue window.</param>
/// <param name="Milestones">Milestones with issue counts.</param>
/// <param name="Labels">Repository labels.</param>
/// <param name="ChangeRequests">Open change requests and those closed/merged since <paramref name="ClosedIssuesSince"/>.</param>
/// <param name="Builds">Most recent builds, newest first.</param>
/// <param name="RecentCommits">Commit times (committer date, UTC) of the default branch within roughly the last day,
/// newest first; <see langword="null"/> when the history is not readable or was never fetched (issue #14).</param>
public sealed record RepositorySnapshot(
    RepositoryIdentity Repository,
    DateTimeOffset RetrievedAt,
    string? DefaultBranch,
    RepositoryFeatures Features,
    IssueCounts IssueCounts,
    IReadOnlyList<IssueSnapshot> Issues,
    DateTimeOffset ClosedIssuesSince,
    IReadOnlyList<MilestoneSnapshot> Milestones,
    IReadOnlyList<LabelSnapshot> Labels,
    IReadOnlyList<ChangeRequestSnapshot> ChangeRequests,
    IReadOnlyList<BuildSnapshot> Builds,
    IReadOnlyList<DateTimeOffset>? RecentCommits = null);
