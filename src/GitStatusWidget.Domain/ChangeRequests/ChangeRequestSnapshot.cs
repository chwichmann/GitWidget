namespace GitStatusWidget.Domain.ChangeRequests;

/// <summary>
/// State of a pull request (GitHub) or merge request (GitLab).
/// </summary>
/// <param name="ExternalId">Provider's global id.</param>
/// <param name="Number">Visible number (GitHub number, GitLab iid).</param>
/// <param name="Kind">Pull request or merge request.</param>
/// <param name="Title">Title as plain text.</param>
/// <param name="State">Open, closed or merged.</param>
/// <param name="IsDraft">Whether the request is a draft.</param>
/// <param name="WebUrl">Browser URL.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="UpdatedAt">Last update (UTC).</param>
/// <param name="Author">Author, if known.</param>
public sealed record ChangeRequestSnapshot(
    string ExternalId,
    int Number,
    ChangeRequestKind Kind,
    string Title,
    ChangeRequestState State,
    bool IsDraft,
    Uri WebUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Issues.UserSummary? Author = null);
