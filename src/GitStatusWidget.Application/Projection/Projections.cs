using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Health;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Application.Projection;

/// <summary>Progress of one milestone.</summary>
/// <param name="ExternalId">Milestone id.</param>
/// <param name="Title">Title.</param>
/// <param name="OpenIssues">Open issues.</param>
/// <param name="ClosedIssues">Closed issues.</param>
/// <param name="DueDate">Due date.</param>
/// <param name="IsClosed">Milestone closed.</param>
/// <param name="IsOverdue">Open milestone past its due date.</param>
/// <param name="WebUrl">Browser URL.</param>
public sealed record MilestoneProgressProjection(
    string ExternalId,
    string Title,
    int OpenIssues,
    int ClosedIssues,
    DateOnly? DueDate,
    bool IsClosed,
    bool IsOverdue,
    Uri? WebUrl)
{
    /// <summary>Total issues.</summary>
    public int TotalIssues => OpenIssues + ClosedIssues;

    /// <summary>Shown in the compact widget (marked in the dashboard, issue #12).</summary>
    public bool IsInWidget { get; init; }

    /// <summary>Completion in [0, 1]; 0 without issues.</summary>
    public double Completion => TotalIssues == 0 ? 0 : (double)ClosedIssues / TotalIssues;

    /// <summary>
    /// Completion in percent, rounded down so 100 appears only when no issue is open; <see langword="null"/> for a
    /// milestone without issues (issue #11). Integer arithmetic avoids floating-point artifacts.
    /// </summary>
    public int? CompletionPercent => TotalIssues == 0 ? null : (int)((long)ClosedIssues * 100 / TotalIssues);
}

/// <summary>Issue count of one label. Issues with several labels count for every label.</summary>
/// <param name="Name">Label name.</param>
/// <param name="Color">Color <c>#RRGGBB</c>.</param>
/// <param name="OpenIssues">Open issues with the label.</param>
/// <param name="ClosedIssues">Closed issues with the label, if known.</param>
/// <param name="IsHighlighted">Configured as highlighted label.</param>
public sealed record LabelCountProjection(string Name, string Color, int OpenIssues, int? ClosedIssues, bool IsHighlighted);

/// <summary>An issue in a list.</summary>
/// <param name="Number">Visible number.</param>
/// <param name="Title">Title (plain text).</param>
/// <param name="State">State.</param>
/// <param name="UpdatedAt">Last update (UTC).</param>
/// <param name="ClosedAt">Closing time (UTC).</param>
/// <param name="Milestone">Milestone title.</param>
/// <param name="Labels">Label names.</param>
/// <param name="Assignees">Assignee logins.</param>
/// <param name="WebUrl">Browser URL.</param>
public sealed record IssueListItemProjection(
    int Number,
    string Title,
    WorkItemState State,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    string? Milestone,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Assignees,
    Uri WebUrl);

/// <summary>A pull/merge request in a list.</summary>
/// <param name="Number">Visible number.</param>
/// <param name="Title">Title.</param>
/// <param name="State">State.</param>
/// <param name="IsDraft">Draft.</param>
/// <param name="UpdatedAt">Last update.</param>
/// <param name="WebUrl">Browser URL.</param>
public sealed record ChangeRequestProjection(int Number, string Title, ChangeRequestState State, bool IsDraft, DateTimeOffset UpdatedAt, Uri WebUrl);

/// <summary>A build in a list.</summary>
/// <param name="Name">Workflow/pipeline name.</param>
/// <param name="State">State.</param>
/// <param name="RefName">Branch or tag.</param>
/// <param name="ShortSha">Abbreviated commit SHA.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="CompletedAt">Completion time.</param>
/// <param name="WebUrl">Browser URL.</param>
public sealed record BuildProjection(string Name, BuildState State, string? RefName, string? ShortSha, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, Uri WebUrl);

/// <summary>Connection/freshness information.</summary>
/// <param name="State">Health state.</param>
/// <param name="Summary">Short, user-facing description (not color-only, architecture §17).</param>
/// <param name="Detail">Sanitized error detail, if any.</param>
/// <param name="LastSuccessAt">Last successful refresh.</param>
/// <param name="LastAttemptAt">Last attempt.</param>
/// <param name="BlockedUntil">Polling paused until.</param>
/// <param name="IsRefreshing">Refresh running.</param>
/// <param name="RateLimit">Rate-limit info (diagnostics).</param>
public sealed record RepositoryHealthProjection(
    RepositoryHealthState State,
    string Summary,
    string? Detail,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? BlockedUntil,
    bool IsRefreshing,
    RateLimitInfo? RateLimit);

/// <summary>Header of a repository view.</summary>
/// <param name="RepositoryId">Configuration id.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="FullPath">Repository path.</param>
/// <param name="Provider">Provider (icon only).</param>
/// <param name="Host">Server host.</param>
/// <param name="DefaultBranch">Default branch.</param>
/// <param name="WebUrl">Repository browser URL.</param>
public sealed record RepositoryHeaderProjection(Guid RepositoryId, string DisplayName, string FullPath, RepositoryProviderKind Provider, string Host, string? DefaultBranch, Uri? WebUrl);

/// <summary>Summary numbers.</summary>
/// <param name="OpenIssues">Open issues.</param>
/// <param name="ClosedIssues">Closed issues (whole repository).</param>
/// <param name="ClosedToday">Issues closed in the counting period (today in the local time zone, or the last 24 hours).</param>
/// <param name="OpenedToday">Issues created in the counting period.</param>
/// <param name="OpenWithoutMilestone">Open issues without milestone.</param>
/// <param name="OpenWithoutAssignee">Open issues without assignee.</param>
/// <param name="OpenChangeRequests">Open pull/merge requests.</param>
/// <param name="ChangeRequestLabel">Presentation label: "pull requests" or "merge requests".</param>
/// <param name="PeriodLabel">Label of the counting period: "today" or "last 24 h" (issue #9).</param>
/// <param name="CommitsToday">Commits on the default branch in the counting period, or <see langword="null"/> when
/// the commit history is not available (issue #14).</param>
public sealed record SummaryProjection(
    int OpenIssues,
    int ClosedIssues,
    int ClosedToday,
    int OpenedToday,
    int OpenWithoutMilestone,
    int OpenWithoutAssignee,
    int OpenChangeRequests,
    string ChangeRequestLabel,
    string PeriodLabel = DashboardProjector.TodayLabel,
    int? CommitsToday = null);

/// <summary>One row of the compact multi-repository widget.</summary>
/// <param name="Header">Header.</param>
/// <param name="HasData">A snapshot exists.</param>
/// <param name="Summary">Summary numbers (zero when no data).</param>
/// <param name="Milestones">Milestones shown in the widget, in display order (issue #12).</param>
/// <param name="LatestBuild">Latest default-branch build, or <see langword="null"/> when the repository has no CI data.</param>
/// <param name="Health">Health.</param>
public sealed record CompactRowProjection(
    RepositoryHeaderProjection Header,
    bool HasData,
    SummaryProjection Summary,
    IReadOnlyList<MilestoneProgressProjection> Milestones,
    BuildProjection? LatestBuild,
    RepositoryHealthProjection Health)
{
    /// <summary>First widget milestone, if any.</summary>
    public MilestoneProgressProjection? CurrentMilestone => Milestones.Count > 0 ? Milestones[0] : null;
}

/// <summary>Expanded dashboard of one repository (architecture §15).</summary>
/// <param name="Header">Header.</param>
/// <param name="HasData">A snapshot exists.</param>
/// <param name="Summary">Summary numbers.</param>
/// <param name="CurrentMilestone">First widget milestone.</param>
/// <param name="Milestones">Milestones (open first, by due date); widget milestones have <see cref="MilestoneProgressProjection.IsInWidget"/>.</param>
/// <param name="Labels">Label counts (highlighted first, then by open count).</param>
/// <param name="RecentlyChanged">Recently updated issues.</param>
/// <param name="RecentlyClosed">Recently closed issues.</param>
/// <param name="OpenChangeRequests">Open change requests (empty list hides the section).</param>
/// <param name="RecentBuilds">Recent builds (empty list hides the section).</param>
/// <param name="Health">Health.</param>
public sealed record DashboardProjection(
    RepositoryHeaderProjection Header,
    bool HasData,
    SummaryProjection Summary,
    MilestoneProgressProjection? CurrentMilestone,
    IReadOnlyList<MilestoneProgressProjection> Milestones,
    IReadOnlyList<LabelCountProjection> Labels,
    IReadOnlyList<IssueListItemProjection> RecentlyChanged,
    IReadOnlyList<IssueListItemProjection> RecentlyClosed,
    IReadOnlyList<ChangeRequestProjection> OpenChangeRequests,
    IReadOnlyList<BuildProjection> RecentBuilds,
    RepositoryHealthProjection Health);
