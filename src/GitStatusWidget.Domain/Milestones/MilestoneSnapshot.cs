namespace GitStatusWidget.Domain.Milestones;

/// <summary>
/// State of a milestone at the time of a snapshot.
/// </summary>
/// <param name="ExternalId">Provider milestone id.</param>
/// <param name="Title">Title.</param>
/// <param name="Description">Description, if any (never logged).</param>
/// <param name="StartDate">Start date, if set.</param>
/// <param name="DueDate">Due date, if set.</param>
/// <param name="IsClosed">Whether the milestone is closed.</param>
/// <param name="OpenIssueCount">Number of open issues (pull/merge requests excluded).</param>
/// <param name="ClosedIssueCount">Number of closed issues (pull/merge requests excluded).</param>
/// <param name="WebUrl">Browser URL, if available.</param>
/// <param name="UpdatedAt">Last update of the milestone itself (UTC), if available.</param>
public sealed record MilestoneSnapshot(
    string ExternalId,
    string Title,
    string? Description,
    DateOnly? StartDate,
    DateOnly? DueDate,
    bool IsClosed,
    int OpenIssueCount,
    int ClosedIssueCount,
    Uri? WebUrl = null,
    DateTimeOffset? UpdatedAt = null)
{
    /// <summary>Total number of issues in the milestone.</summary>
    public int TotalIssueCount => OpenIssueCount + ClosedIssueCount;

    /// <summary>Completion ratio in [0, 1]; 0 when the milestone has no issues.</summary>
    public double Completion => TotalIssueCount == 0 ? 0d : (double)ClosedIssueCount / TotalIssueCount;
}
