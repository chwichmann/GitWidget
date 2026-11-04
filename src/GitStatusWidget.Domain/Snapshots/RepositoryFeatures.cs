namespace GitStatusWidget.Domain.Snapshots;

/// <summary>
/// Which data sections the repository offers. Disabled or unused sections are hidden in the UI.
/// </summary>
/// <param name="Issues">Issue tracking is enabled and readable.</param>
/// <param name="ChangeRequests">Pull/merge requests are enabled and readable.</param>
/// <param name="Builds">CI pipelines / workflow runs are enabled and readable.</param>
public sealed record RepositoryFeatures(bool Issues, bool ChangeRequests, bool Builds)
{
    /// <summary>All features available.</summary>
    public static RepositoryFeatures All { get; } = new(true, true, true);
}
