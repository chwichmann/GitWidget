namespace GitStatusWidget.Domain.ChangeRequests;

/// <summary>
/// Provider flavour of a change request. Used for presentation labels only.
/// </summary>
public enum ChangeRequestKind
{
    /// <summary>GitLab merge request.</summary>
    MergeRequest = 1,

    /// <summary>GitHub pull request.</summary>
    PullRequest = 2,
}
