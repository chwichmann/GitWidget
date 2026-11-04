namespace GitStatusWidget.Domain.Events;

/// <summary>
/// Semantic change derived by comparing two accepted snapshots.
/// </summary>
public enum ChangeEventType
{
    /// <summary>A new issue appeared.</summary>
    IssueCreated = 1,

    /// <summary>An open issue was closed.</summary>
    IssueClosed,

    /// <summary>A closed issue was reopened.</summary>
    IssueReopened,

    /// <summary>The issue title changed.</summary>
    IssueTitleChanged,

    /// <summary>The issue's label set changed.</summary>
    IssueLabelsChanged,

    /// <summary>The issue moved to another (or no) milestone.</summary>
    IssueMilestoneChanged,

    /// <summary>The issue's assignee set changed.</summary>
    IssueAssigneesChanged,

    /// <summary>A milestone was created.</summary>
    MilestoneCreated,

    /// <summary>A milestone was closed.</summary>
    MilestoneClosed,

    /// <summary>A milestone was reopened.</summary>
    MilestoneReopened,

    /// <summary>Open/closed counts of a milestone changed.</summary>
    MilestoneProgressChanged,

    /// <summary>A pull/merge request was opened.</summary>
    ChangeRequestOpened,

    /// <summary>A pull/merge request was closed without merging.</summary>
    ChangeRequestClosed,

    /// <summary>A pull/merge request was merged.</summary>
    ChangeRequestMerged,

    /// <summary>A build started running.</summary>
    BuildStarted,

    /// <summary>A build succeeded.</summary>
    BuildSucceeded,

    /// <summary>A build failed.</summary>
    BuildFailed,

    /// <summary>A build was canceled.</summary>
    BuildCanceled,
}
