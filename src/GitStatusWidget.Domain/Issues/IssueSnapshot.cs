using GitStatusWidget.Domain.Labels;
using GitStatusWidget.Domain.Milestones;

namespace GitStatusWidget.Domain.Issues;

/// <summary>
/// State of one issue at the time of a snapshot. Pull/merge requests are never represented as issues.
/// </summary>
/// <param name="ExternalId">Provider's globally unique issue id (not the visible number).</param>
/// <param name="Number">Visible issue number (GitHub number, GitLab iid).</param>
/// <param name="Title">Title as plain text.</param>
/// <param name="State">Open or closed.</param>
/// <param name="WebUrl">Browser URL of the issue.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="UpdatedAt">Last update time (UTC).</param>
/// <param name="ClosedAt">Closing time (UTC), if closed.</param>
/// <param name="Author">Author, if known.</param>
/// <param name="Assignees">Assignees; empty when unassigned.</param>
/// <param name="Milestone">Milestone, if any.</param>
/// <param name="Labels">Labels attached to the issue.</param>
public sealed record IssueSnapshot(
    string ExternalId,
    int Number,
    string Title,
    WorkItemState State,
    Uri WebUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    UserSummary? Author,
    IReadOnlyList<UserSummary> Assignees,
    MilestoneReference? Milestone,
    IReadOnlyList<LabelReference> Labels);
