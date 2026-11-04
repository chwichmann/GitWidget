namespace GitStatusWidget.Domain.Snapshots;

/// <summary>
/// Repository-wide issue totals (pull/merge requests excluded), taken from provider count sources
/// so that closed totals are correct even when not every closed issue is loaded.
/// </summary>
/// <param name="Open">Total open issues.</param>
/// <param name="Closed">Total closed issues.</param>
public sealed record IssueCounts(int Open, int Closed)
{
    /// <summary>All issues.</summary>
    public int Total => Open + Closed;
}
