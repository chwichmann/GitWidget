namespace GitStatusWidget.Domain.Snapshots;

/// <summary>
/// Which data sections the repository offers. Disabled or unused sections are hidden in the UI.
/// </summary>
/// <param name="Issues">Issue tracking is enabled and readable.</param>
/// <param name="ChangeRequests">Pull/merge requests are enabled and readable.</param>
/// <param name="Builds">CI pipelines / workflow runs are enabled and readable.</param>
/// <param name="Commits">The commit history of the default branch is readable (issue #14).</param>
public sealed record RepositoryFeatures(bool Issues, bool ChangeRequests, bool Builds, bool Commits = true)
{
    /// <summary>All features available.</summary>
    public static RepositoryFeatures All { get; } = new(true, true, true);
}
