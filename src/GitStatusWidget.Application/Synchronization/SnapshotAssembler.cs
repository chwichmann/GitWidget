using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Labels;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Snapshots;

namespace GitStatusWidget.Application.Synchronization;

/// <summary>
/// Outcome of assembling a snapshot.
/// </summary>
public abstract record AssemblyResult
{
    private AssemblyResult()
    {
    }

    /// <summary>The new snapshot is complete and may be accepted.</summary>
    /// <param name="Snapshot">The assembled snapshot.</param>
    /// <param name="ReconciliationRecommended">The open set disagrees with the provider's count source;
    /// the next refresh should run a full reconciliation.</param>
    public sealed record Accepted(RepositorySnapshot Snapshot, bool ReconciliationRecommended) : AssemblyResult;

    /// <summary>The data is incomplete or inconsistent; the previous snapshot stays authoritative.</summary>
    /// <param name="Reason">Diagnostic reason (no secrets, no titles).</param>
    public sealed record Rejected(string Reason) : AssemblyResult;
}

/// <summary>
/// Merges provider section updates into the previous accepted snapshot, provider-neutrally.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
/// <item>Without a previous snapshot every work-item section must be full and counts must be present.</item>
/// <item>Delta updates upsert by external id; they never remove items.</item>
/// <item>Full updates replace the open set; open items missing from it are dropped (deleted, transferred,
/// made invisible) — the comparer never derives events from disappearance.</item>
/// <item>Closed items are kept while updated inside the closed window, then pruned.</item>
/// <item>Open counts (repository, milestones, labels) are computed from the open set; closed counts come from
/// the provider count source, otherwise from the previous snapshot.</item>
/// <item>A full open set that disagrees substantially with the count source is rejected (ADR-004).</item>
/// </list>
/// </remarks>
public static class SnapshotAssembler
{
    /// <summary>Number of builds retained in a snapshot.</summary>
    public const int MaxBuilds = 20;

    /// <summary>
    /// Assembles a new snapshot.
    /// </summary>
    /// <param name="previous">Last accepted snapshot, or <see langword="null"/>.</param>
    /// <param name="fetch">Provider fetch.</param>
    /// <param name="retrievedAt">Time of the fetch (UTC).</param>
    /// <param name="closedWindowStart">Closed items updated before this instant are pruned.</param>
    /// <returns>The assembly result.</returns>
    public static AssemblyResult Assemble(
        RepositorySnapshot? previous,
        ProviderFetch fetch,
        DateTimeOffset retrievedAt,
        DateTimeOffset closedWindowStart)
    {
        ArgumentNullException.ThrowIfNull(fetch);

        if (previous is null)
        {
            if (fetch.Issues is not WorkItemUpdate<IssueSnapshot>.FullItems
                || fetch.ChangeRequests is not WorkItemUpdate<ChangeRequestSnapshot>.FullItems
                || fetch.Milestones is not SectionUpdate<MilestoneSnapshot>.ReplaceSection
                || fetch.Labels is not SectionUpdate<LabelSnapshot>.ReplaceSection
                || fetch.Builds is not SectionUpdate<BuildSnapshot>.ReplaceSection
                || fetch.Counts is null)
            {
                return new AssemblyResult.Rejected("First snapshot requires full sections and counts.");
            }
        }

        var (issues, issuesSince) = MergeWorkItems(
            previous?.Issues ?? [],
            previous?.ClosedIssuesSince,
            fetch.Issues,
            i => i.ExternalId,
            i => i.State == WorkItemState.Open,
            i => i.UpdatedAt,
            closedWindowStart);

        var (changeRequests, _) = MergeWorkItems(
            previous?.ChangeRequests ?? [],
            previous?.ClosedIssuesSince,
            fetch.ChangeRequests,
            c => c.ExternalId,
            c => c.State == ChangeRequestState.Open,
            c => c.UpdatedAt,
            closedWindowStart);

        var openIssues = issues.Where(i => i.State == WorkItemState.Open).ToList();
        var localOpenCount = openIssues.Count;

        var reconciliationRecommended = false;
        if (fetch.Counts?.OpenIssues is { } reportedOpen && reportedOpen != localOpenCount)
        {
            if (fetch.Issues is WorkItemUpdate<IssueSnapshot>.FullItems && IsSubstantialMismatch(localOpenCount, reportedOpen))
            {
                return new AssemblyResult.Rejected(
                    $"Open issue set ({localOpenCount}) disagrees with provider count ({reportedOpen}).");
            }

            // Count sources and list endpoints are only eventually consistent; small drifts are tolerated
            // after a full fetch, while a drift after a delta suggests missed deletions.
            reconciliationRecommended = fetch.Issues is not WorkItemUpdate<IssueSnapshot>.FullItems;
        }

        var closedCount = fetch.Counts?.ClosedIssues ?? previous!.IssueCounts.Closed;

        var milestones = BuildMilestones(previous, fetch, openIssues);
        var labels = BuildLabels(previous, fetch, openIssues);
        var builds = fetch.Builds is SectionUpdate<BuildSnapshot>.ReplaceSection replaceBuilds
            ? replaceBuilds.Items
            : previous!.Builds;

        // Commits (issue #14): replaced as a whole; aged-out entries are pruned so the list stays small.
        var commitsSince = retrievedAt - ProviderFetch.CommitWindow;
        var commitSource = fetch.Commits is SectionUpdate<DateTimeOffset>.ReplaceSection replaceCommits
            ? replaceCommits.Items
            : previous?.RecentCommits;
        var commits = fetch.Features.Commits
            ? commitSource?.Where(c => c >= commitsSince).OrderByDescending(c => c).ToList()
            : null;

        var snapshot = new RepositorySnapshot(
            fetch.Repository,
            retrievedAt,
            fetch.DefaultBranch,
            fetch.Features,
            new IssueCounts(localOpenCount, closedCount),
            issues,
            issuesSince,
            milestones,
            labels,
            changeRequests,
            builds
                .OrderByDescending(b => b.CreatedAt)
                .ThenBy(b => b.ExternalId, StringComparer.Ordinal)
                .Take(MaxBuilds)
                .ToList(),
            commits);

        return new AssemblyResult.Accepted(snapshot, reconciliationRecommended);
    }

    private static bool IsSubstantialMismatch(int local, int reported)
    {
        var tolerance = Math.Max(5, (int)Math.Ceiling(reported * 0.02));
        return Math.Abs(local - reported) > tolerance;
    }

    private static (IReadOnlyList<T> Items, DateTimeOffset ClosedSince) MergeWorkItems<T>(
        IReadOnlyList<T> previous,
        DateTimeOffset? previousClosedSince,
        WorkItemUpdate<T> update,
        Func<T, string> id,
        Func<T, bool> isOpen,
        Func<T, DateTimeOffset> updatedAt,
        DateTimeOffset closedWindowStart)
    {
        var byId = new Dictionary<string, T>(StringComparer.Ordinal);
        DateTimeOffset baselineSince;

        switch (update)
        {
            case WorkItemUpdate<T>.FullItems full:
                // Previous closed items stay (they may predate the full window); previous open items do not.
                foreach (var item in previous.Where(i => !isOpen(i)))
                {
                    byId[id(item)] = item;
                }

                foreach (var item in full.RecentlyClosed.Concat(full.Open))
                {
                    Upsert(byId, item, id, updatedAt);
                }

                baselineSince = previousClosedSince is { } since && since < full.ClosedSince ? since : full.ClosedSince;
                break;

            case WorkItemUpdate<T>.DeltaItems delta:
                foreach (var item in previous)
                {
                    byId[id(item)] = item;
                }

                foreach (var item in delta.Changed)
                {
                    Upsert(byId, item, id, updatedAt);
                }

                baselineSince = previousClosedSince ?? closedWindowStart;
                break;

            default:
                foreach (var item in previous)
                {
                    byId[id(item)] = item;
                }

                baselineSince = previousClosedSince ?? closedWindowStart;
                break;
        }

        var items = byId.Values
            .Where(i => isOpen(i) || updatedAt(i) >= closedWindowStart)
            .OrderBy(i => id(i), StringComparer.Ordinal)
            .ToList();
        var closedSince = baselineSince > closedWindowStart ? baselineSince : closedWindowStart;
        return (items, closedSince);
    }

    private static void Upsert<T>(Dictionary<string, T> byId, T item, Func<T, string> id, Func<T, DateTimeOffset> updatedAt)
    {
        // Overlapping fetch windows can deliver an older copy; keep the newest version.
        var key = id(item);
        if (!byId.TryGetValue(key, out var existing) || updatedAt(item) >= updatedAt(existing))
        {
            byId[key] = item;
        }
    }

    private static List<MilestoneSnapshot> BuildMilestones(RepositorySnapshot? previous, ProviderFetch fetch, List<IssueSnapshot> openIssues)
    {
        var source = fetch.Milestones is SectionUpdate<MilestoneSnapshot>.ReplaceSection replace
            ? replace.Items
            : previous!.Milestones;
        var previousById = (previous?.Milestones ?? []).ToDictionary(m => m.ExternalId, StringComparer.Ordinal);
        var openByMilestone = openIssues
            .Where(i => i.Milestone is not null)
            .GroupBy(i => i.Milestone!.ExternalId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return source
            .Select(m => m with
            {
                OpenIssueCount = openByMilestone.GetValueOrDefault(m.ExternalId),
                ClosedIssueCount = fetch.Counts?.ClosedIssuesByMilestone.TryGetValue(m.ExternalId, out var closed) == true
                    ? closed
                    : previousById.TryGetValue(m.ExternalId, out var before) ? before.ClosedIssueCount : m.ClosedIssueCount,
            })
            .OrderBy(m => m.IsClosed)
            .ThenBy(m => m.DueDate ?? DateOnly.MaxValue)
            .ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<LabelSnapshot> BuildLabels(RepositorySnapshot? previous, ProviderFetch fetch, List<IssueSnapshot> openIssues)
    {
        var source = fetch.Labels is SectionUpdate<LabelSnapshot>.ReplaceSection replace
            ? replace.Items
            : previous!.Labels;
        var previousByName = (previous?.Labels ?? [])
            .GroupBy(l => l.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var openByLabel = openIssues
            .SelectMany(i => i.Labels.Select(l => l.Name).Distinct(StringComparer.Ordinal))
            .GroupBy(n => n, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return source
            .Select(l => l with
            {
                OpenIssueCount = openByLabel.GetValueOrDefault(l.Name),
                ClosedIssueCount = fetch.Counts?.ClosedIssuesByLabel?.TryGetValue(l.Name, out var closed) == true
                    ? closed
                    : previousByName.TryGetValue(l.Name, out var before) ? before.ClosedIssueCount : l.ClosedIssueCount,
            })
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
