using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Labels;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Domain.Snapshots;

namespace GitStatusWidget.Application.Abstractions;

/// <summary>
/// Update of a list section that is always replaced as a whole (milestones, labels, builds).
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public abstract record SectionUpdate<T>
{
    private SectionUpdate()
    {
    }

    /// <summary>The section was not requested or did not change (for example HTTP 304).</summary>
    public static SectionUpdate<T> Unchanged { get; } = new UnchangedSection();

    /// <summary>Creates a replacing update.</summary>
    /// <param name="items">The complete new list.</param>
    /// <returns>The update.</returns>
    public static SectionUpdate<T> Replace(IReadOnlyList<T> items) => new ReplaceSection(items);

    /// <summary>Unchanged section.</summary>
    public sealed record UnchangedSection : SectionUpdate<T>;

    /// <summary>Complete replacement.</summary>
    /// <param name="Items">The complete new list.</param>
    public sealed record ReplaceSection(IReadOnlyList<T> Items) : SectionUpdate<T>;
}

/// <summary>
/// Update of a work-item section (issues or change requests).
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public abstract record WorkItemUpdate<T>
{
    private WorkItemUpdate()
    {
    }

    /// <summary>Nothing changed or the section was not requested.</summary>
    public static WorkItemUpdate<T> Unchanged { get; } = new UnchangedItems();

    /// <summary>Creates a delta update.</summary>
    /// <param name="changed">Items created or updated since the provider's watermark, in any state.</param>
    /// <returns>The update.</returns>
    public static WorkItemUpdate<T> Delta(IReadOnlyList<T> changed) => new DeltaItems(changed);

    /// <summary>Creates a full update.</summary>
    /// <param name="open">The complete set of open items.</param>
    /// <param name="recentlyClosed">Every closed (or merged) item updated at or after <paramref name="closedSince"/>.</param>
    /// <param name="closedSince">Lower bound of <paramref name="recentlyClosed"/>.</param>
    /// <returns>The update.</returns>
    public static WorkItemUpdate<T> Full(IReadOnlyList<T> open, IReadOnlyList<T> recentlyClosed, DateTimeOffset closedSince) =>
        new FullItems(open, recentlyClosed, closedSince);

    /// <summary>Unchanged section.</summary>
    public sealed record UnchangedItems : WorkItemUpdate<T>;

    /// <summary>Items changed since the last fetch; merged into the previous snapshot.</summary>
    /// <param name="Changed">Changed items.</param>
    public sealed record DeltaItems(IReadOnlyList<T> Changed) : WorkItemUpdate<T>;

    /// <summary>Complete open set plus recently closed items; replaces the previous section.</summary>
    /// <param name="Open">All open items.</param>
    /// <param name="RecentlyClosed">Closed items updated since <paramref name="ClosedSince"/>.</param>
    /// <param name="ClosedSince">Window start.</param>
    public sealed record FullItems(IReadOnlyList<T> Open, IReadOnlyList<T> RecentlyClosed, DateTimeOffset ClosedSince) : WorkItemUpdate<T>;
}

/// <summary>
/// Exact totals from a provider count source (GraphQL, statistics endpoint). Pull/merge requests excluded.
/// </summary>
/// <param name="OpenIssues">Total open issues, if known (used as a consistency check against the open set).</param>
/// <param name="ClosedIssues">Total closed issues.</param>
/// <param name="ClosedIssuesByMilestone">Closed issue count per milestone external id.</param>
/// <param name="ClosedIssuesByLabel">Closed issue count per label name, if the provider supplies them.</param>
public sealed record CountsUpdate(
    int? OpenIssues,
    int ClosedIssues,
    IReadOnlyDictionary<string, int> ClosedIssuesByMilestone,
    IReadOnlyDictionary<string, int>? ClosedIssuesByLabel = null);

/// <summary>
/// Data returned by one successful provider fetch. The assembler merges it into the previous snapshot.
/// </summary>
/// <param name="Repository">Resolved repository identity.</param>
/// <param name="DefaultBranch">Default branch (from the previous snapshot when metadata was not refreshed).</param>
/// <param name="Features">Available sections (from the previous snapshot when metadata was not refreshed).</param>
/// <param name="Issues">Issue section update.</param>
/// <param name="ChangeRequests">Change request section update.</param>
/// <param name="Milestones">Milestones. <see cref="MilestoneSnapshot.OpenIssueCount"/> is recomputed from the open set.</param>
/// <param name="Labels">Labels.</param>
/// <param name="Builds">Most recent builds, newest first.</param>
/// <param name="Counts">Totals, or <see langword="null"/> when not refreshed.</param>
/// <param name="Commits">Commit times of the default branch since <see cref="CommitWindow"/> ago, or
/// <see langword="null"/> when not refreshed (issue #14).</param>
public sealed record ProviderFetch(
    RepositoryIdentity Repository,
    string? DefaultBranch,
    RepositoryFeatures Features,
    WorkItemUpdate<IssueSnapshot> Issues,
    WorkItemUpdate<ChangeRequestSnapshot> ChangeRequests,
    SectionUpdate<MilestoneSnapshot> Milestones,
    SectionUpdate<LabelSnapshot> Labels,
    SectionUpdate<BuildSnapshot> Builds,
    CountsUpdate? Counts,
    SectionUpdate<DateTimeOffset>? Commits = null)
{
    /// <summary>
    /// How far back providers read the commit history. Covers both counting periods ("today" and "last 24 h")
    /// with a margin for polling gaps.
    /// </summary>
    public static readonly TimeSpan CommitWindow = TimeSpan.FromHours(26);
}
