namespace GitStatusWidget.Domain.Builds;

/// <summary>
/// State of a CI pipeline or workflow run.
/// </summary>
/// <param name="ExternalId">Provider run/pipeline id.</param>
/// <param name="DisplayName">Workflow or pipeline name.</param>
/// <param name="State">Provider-neutral state.</param>
/// <param name="RefName">Branch or tag.</param>
/// <param name="CommitSha">Commit SHA.</param>
/// <param name="WebUrl">Browser URL.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="CompletedAt">Completion time (UTC), if completed.</param>
/// <param name="UpdatedAt">Last update time (UTC).</param>
public sealed record BuildSnapshot(
    string ExternalId,
    string DisplayName,
    BuildState State,
    string? RefName,
    string? CommitSha,
    Uri WebUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Whether the build has reached a terminal state.</summary>
    public bool IsCompleted => State is BuildState.Succeeded or BuildState.Failed or BuildState.Canceled or BuildState.Skipped;
}
