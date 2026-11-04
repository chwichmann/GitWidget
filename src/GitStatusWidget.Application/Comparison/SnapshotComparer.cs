using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Events;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Snapshots;

namespace GitStatusWidget.Application.Comparison;

/// <summary>
/// Derives semantic change events from two accepted snapshots of the same repository.
/// </summary>
public interface ISnapshotComparer
{
    /// <summary>
    /// Compares two complete snapshots. The result is deterministic: identical inputs yield identical
    /// events in identical order, and identical snapshots yield no events.
    /// </summary>
    /// <param name="previous">The previously accepted snapshot.</param>
    /// <param name="current">The newly accepted snapshot.</param>
    /// <returns>Derived events, ordered by type, entity number and key.</returns>
    IReadOnlyList<RepositoryChangeEvent> Compare(RepositorySnapshot previous, RepositorySnapshot current);
}

/// <summary>
/// Default provider-neutral <see cref="ISnapshotComparer"/>.
/// </summary>
/// <remarks>
/// Snapshots contain all open items plus closed items updated inside a time window
/// (<see cref="RepositorySnapshot.ClosedIssuesSince"/>). Consequently:
/// <list type="bullet">
/// <item>An item that disappears from the snapshot produces no event (it left the window, was deleted,
/// transferred or became invisible). This is what prevents false "mass closure" events.</item>
/// <item>An unknown open item that was created after the previous snapshot is "created"; an unknown open
/// item created earlier but updated after the previous snapshot was reopened from outside the window.</item>
/// <item>An unknown closed item produces events only if it is new (created and closed between polls).</item>
/// </list>
/// </remarks>
public sealed class SnapshotComparer : ISnapshotComparer
{
    /// <summary>
    /// Tolerance for clock differences between provider timestamps and the local retrieval time.
    /// </summary>
    public static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(2);

    /// <inheritdoc />
    public IReadOnlyList<RepositoryChangeEvent> Compare(RepositorySnapshot previous, RepositorySnapshot current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (previous.Repository.Provider != current.Repository.Provider
            || !string.Equals(previous.Repository.ExternalId, current.Repository.ExternalId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Snapshots belong to different repositories.", nameof(current));
        }

        var context = new ComparisonContext(previous, current);
        CompareIssues(context);
        CompareMilestones(context);
        CompareChangeRequests(context);
        CompareBuilds(context);

        return context.Events
            .DistinctBy(e => e.Key)
            .OrderBy(e => e.Type)
            .ThenBy(e => e.EntityNumber ?? int.MaxValue)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static void CompareIssues(ComparisonContext ctx)
    {
        var previousById = ToDictionary(ctx.Previous.Issues, i => i.ExternalId);

        foreach (var issue in ctx.Current.Issues)
        {
            if (!previousById.TryGetValue(issue.ExternalId, out var before))
            {
                CompareUnknownIssue(ctx, issue);
                continue;
            }

            if (before.State == WorkItemState.Open && issue.State == WorkItemState.Closed)
            {
                ctx.AddIssue(ChangeEventType.IssueClosed, issue, $"closed:{issue.ClosedAt?.UtcTicks}", issue.ClosedAt ?? issue.UpdatedAt);
            }
            else if (before.State == WorkItemState.Closed && issue.State == WorkItemState.Open)
            {
                ctx.AddIssue(ChangeEventType.IssueReopened, issue, "reopened", issue.UpdatedAt);
            }

            if (!string.Equals(before.Title, issue.Title, StringComparison.Ordinal))
            {
                ctx.AddIssue(ChangeEventType.IssueTitleChanged, issue, issue.Title, issue.UpdatedAt, detail: before.Title);
            }

            var labelsBefore = LabelSet(before);
            var labelsNow = LabelSet(issue);
            if (!labelsBefore.SequenceEqual(labelsNow, StringComparer.Ordinal))
            {
                ctx.AddIssue(ChangeEventType.IssueLabelsChanged, issue, string.Join('|', labelsNow), issue.UpdatedAt, detail: string.Join(", ", labelsNow));
            }

            if (!string.Equals(before.Milestone?.ExternalId, issue.Milestone?.ExternalId, StringComparison.Ordinal))
            {
                ctx.AddIssue(ChangeEventType.IssueMilestoneChanged, issue, issue.Milestone?.ExternalId ?? "-", issue.UpdatedAt, detail: issue.Milestone?.Title);
            }

            var assigneesBefore = AssigneeSet(before);
            var assigneesNow = AssigneeSet(issue);
            if (!assigneesBefore.SequenceEqual(assigneesNow, StringComparer.Ordinal))
            {
                ctx.AddIssue(ChangeEventType.IssueAssigneesChanged, issue, string.Join('|', assigneesNow), issue.UpdatedAt,
                    detail: string.Join(", ", issue.Assignees.Select(a => a.Login).Order(StringComparer.Ordinal)));
            }
        }
    }

    private static void CompareUnknownIssue(ComparisonContext ctx, IssueSnapshot issue)
    {
        var isNew = issue.CreatedAt > ctx.Threshold;
        if (isNew)
        {
            ctx.AddIssue(ChangeEventType.IssueCreated, issue, "created", issue.CreatedAt);
            if (issue.State == WorkItemState.Closed)
            {
                ctx.AddIssue(ChangeEventType.IssueClosed, issue, $"closed:{issue.ClosedAt?.UtcTicks}", issue.ClosedAt ?? issue.UpdatedAt);
            }
        }
        else if (issue.State == WorkItemState.Open && issue.UpdatedAt > ctx.Threshold)
        {
            // Every open issue is part of every snapshot, so an old open issue that was not in the previous
            // snapshot must have been closed before (outside the closed window) and has now been reopened.
            ctx.AddIssue(ChangeEventType.IssueReopened, issue, "reopened", issue.UpdatedAt);
        }
    }

    private static void CompareMilestones(ComparisonContext ctx)
    {
        var previousById = ToDictionary(ctx.Previous.Milestones, m => m.ExternalId);
        foreach (var milestone in ctx.Current.Milestones)
        {
            if (!previousById.TryGetValue(milestone.ExternalId, out var before))
            {
                ctx.AddMilestone(ChangeEventType.MilestoneCreated, milestone, "created");
                continue;
            }

            if (!before.IsClosed && milestone.IsClosed)
            {
                ctx.AddMilestone(ChangeEventType.MilestoneClosed, milestone, "closed");
            }
            else if (before.IsClosed && !milestone.IsClosed)
            {
                ctx.AddMilestone(ChangeEventType.MilestoneReopened, milestone, "reopened");
            }

            if (before.OpenIssueCount != milestone.OpenIssueCount || before.ClosedIssueCount != milestone.ClosedIssueCount)
            {
                ctx.AddMilestone(
                    ChangeEventType.MilestoneProgressChanged,
                    milestone,
                    $"{milestone.OpenIssueCount}/{milestone.ClosedIssueCount}",
                    detail: $"{milestone.ClosedIssueCount}/{milestone.TotalIssueCount}");
            }
        }
    }

    private static void CompareChangeRequests(ComparisonContext ctx)
    {
        var previousById = ToDictionary(ctx.Previous.ChangeRequests, c => c.ExternalId);
        foreach (var request in ctx.Current.ChangeRequests)
        {
            if (!previousById.TryGetValue(request.ExternalId, out var before))
            {
                var isNew = request.CreatedAt > ctx.Threshold;
                if (isNew || (request.State == ChangeRequestState.Open && request.UpdatedAt > ctx.Threshold))
                {
                    ctx.AddChangeRequest(ChangeEventType.ChangeRequestOpened, request, "opened", isNew ? request.CreatedAt : request.UpdatedAt);
                }

                if (isNew)
                {
                    AddChangeRequestTerminal(ctx, request);
                }

                continue;
            }

            if (before.State == request.State)
            {
                continue;
            }

            if (request.State == ChangeRequestState.Open)
            {
                ctx.AddChangeRequest(ChangeEventType.ChangeRequestOpened, request, "reopened", request.UpdatedAt);
            }
            else
            {
                AddChangeRequestTerminal(ctx, request);
            }
        }
    }

    private static void AddChangeRequestTerminal(ComparisonContext ctx, ChangeRequestSnapshot request)
    {
        switch (request.State)
        {
            case ChangeRequestState.Merged:
                ctx.AddChangeRequest(ChangeEventType.ChangeRequestMerged, request, "merged", request.UpdatedAt);
                break;
            case ChangeRequestState.Closed:
                ctx.AddChangeRequest(ChangeEventType.ChangeRequestClosed, request, "closed", request.UpdatedAt);
                break;
            case ChangeRequestState.Open:
            default:
                break;
        }
    }

    private static void CompareBuilds(ComparisonContext ctx)
    {
        var previousById = ToDictionary(ctx.Previous.Builds, b => b.ExternalId);
        foreach (var build in ctx.Current.Builds)
        {
            var hadBefore = previousById.TryGetValue(build.ExternalId, out var before);
            if (hadBefore && before!.State == build.State)
            {
                continue;
            }

            // A build that is new to the list and finished long ago is history, not news.
            if (!hadBefore && (build.CompletedAt ?? build.UpdatedAt) <= ctx.Threshold)
            {
                continue;
            }

            var type = build.State switch
            {
                BuildState.Running => ChangeEventType.BuildStarted,
                BuildState.Succeeded => ChangeEventType.BuildSucceeded,
                BuildState.Failed => ChangeEventType.BuildFailed,
                BuildState.Canceled => ChangeEventType.BuildCanceled,
                _ => (ChangeEventType?)null,
            };

            if (type is { } eventType)
            {
                ctx.AddBuild(eventType, build);
            }
        }
    }

    private static string[] LabelSet(IssueSnapshot issue) =>
        issue.Labels.Select(l => l.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string[] AssigneeSet(IssueSnapshot issue) =>
        issue.Assignees.Select(a => a.ExternalId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static Dictionary<string, T> ToDictionary<T>(IEnumerable<T> items, Func<T, string> key)
    {
        // Providers should never return duplicates; tolerate them deterministically (last wins).
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            result[key(item)] = item;
        }

        return result;
    }

    /// <summary>Returns the default presentation priority of an event type.</summary>
    /// <param name="type">Event type.</param>
    /// <returns>The priority.</returns>
    public static EventPriority PriorityOf(ChangeEventType type) => type switch
    {
        ChangeEventType.BuildFailed => EventPriority.High,
        ChangeEventType.IssueCreated
            or ChangeEventType.IssueClosed
            or ChangeEventType.IssueReopened
            or ChangeEventType.MilestoneClosed
            or ChangeEventType.ChangeRequestOpened
            or ChangeEventType.ChangeRequestMerged => EventPriority.Medium,
        _ => EventPriority.Low,
    };

    private sealed class ComparisonContext(RepositorySnapshot previous, RepositorySnapshot current)
    {
        public RepositorySnapshot Previous { get; } = previous;

        public RepositorySnapshot Current { get; } = current;

        /// <summary>Items created/updated after this instant are considered changes since the previous snapshot.</summary>
        public DateTimeOffset Threshold { get; } = previous.RetrievedAt - ClockSkewTolerance;

        public List<RepositoryChangeEvent> Events { get; } = [];

        public void AddIssue(ChangeEventType type, IssueSnapshot issue, string state, DateTimeOffset occurredAt, string? detail = null) =>
            Add(type, issue.ExternalId, issue.Number, issue.Title, issue.WebUrl, state, issue.UpdatedAt, occurredAt, detail);

        public void AddMilestone(ChangeEventType type, MilestoneSnapshot milestone, string state, string? detail = null) =>
            Add(type, milestone.ExternalId, null, milestone.Title, milestone.WebUrl, state, milestone.UpdatedAt ?? DateTimeOffset.UnixEpoch,
                milestone.UpdatedAt ?? Current.RetrievedAt, detail);

        public void AddChangeRequest(ChangeEventType type, ChangeRequestSnapshot request, string state, DateTimeOffset occurredAt) =>
            Add(type, request.ExternalId, request.Number, request.Title, request.WebUrl, state, request.UpdatedAt, occurredAt, null);

        public void AddBuild(ChangeEventType type, BuildSnapshot build) =>
            Add(type, build.ExternalId, null, build.DisplayName, build.WebUrl, build.State.ToString(), build.CreatedAt,
                build.CompletedAt ?? build.UpdatedAt, build.RefName);

        private void Add(
            ChangeEventType type,
            string entityId,
            int? number,
            string title,
            Uri? url,
            string state,
            DateTimeOffset updatedAt,
            DateTimeOffset occurredAt,
            string? detail)
        {
            var key = EventKey.Create(Current.Repository.ExternalId, type, entityId, state, updatedAt);
            Events.Add(new RepositoryChangeEvent(key, type, PriorityOf(type), Current.Repository, entityId, number, title, url, occurredAt, detail));
        }
    }
}
