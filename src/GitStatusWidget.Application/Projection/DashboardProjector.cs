using System.Globalization;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Health;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Domain.Snapshots;

namespace GitStatusWidget.Application.Projection;

/// <summary>
/// Builds provider-neutral projections from repository runtime states (architecture §15). Compact rows and the
/// dashboard use the same rules; view models never group raw snapshots themselves.
/// </summary>
public sealed class DashboardProjector
{
    /// <summary>Number of items in recent lists.</summary>
    public const int RecentListSize = 15;

    private readonly TimeProvider _time;

    /// <summary>Creates the projector.</summary>
    /// <param name="time">Time provider (its local time zone defines "today").</param>
    public DashboardProjector(TimeProvider time) => _time = time;

    /// <summary>Period label of <see cref="DailyCountMode.CalendarDay"/>.</summary>
    public const string TodayLabel = "today";

    /// <summary>Period label of <see cref="DailyCountMode.Last24Hours"/>.</summary>
    public const string Last24HoursLabel = "last 24 h";

    /// <summary>Projects a compact row.</summary>
    /// <param name="state">Runtime state.</param>
    /// <param name="mode">Counting period of the closed/opened counters.</param>
    /// <returns>The row.</returns>
    public CompactRowProjection ProjectRow(RepositoryRuntimeState state, DailyCountMode mode = DailyCountMode.CalendarDay)
    {
        ArgumentNullException.ThrowIfNull(state);
        var snapshot = state.Snapshot;
        var today = Today();
        return new CompactRowProjection(
            Header(state),
            snapshot is not null,
            Summary(snapshot, today, mode),
            snapshot is null ? [] : WidgetMilestones(snapshot, state.Configuration, today, _time.GetUtcNow()),
            snapshot is null ? null : LatestDefaultBranchBuild(snapshot),
            Health(state));
    }

    /// <summary>Projects the dashboard of a repository.</summary>
    /// <param name="state">Runtime state.</param>
    /// <param name="mode">Counting period of the closed/opened counters.</param>
    /// <returns>The dashboard projection.</returns>
    public DashboardProjection ProjectDashboard(RepositoryRuntimeState state, DailyCountMode mode = DailyCountMode.CalendarDay)
    {
        ArgumentNullException.ThrowIfNull(state);
        var snapshot = state.Snapshot;
        var today = Today();
        if (snapshot is null)
        {
            return new DashboardProjection(Header(state), false, Summary(null, today, mode), null, [], [], [], [], [], [], Health(state));
        }

        var highlighted = new HashSet<string>(state.Configuration.HighlightedLabels, StringComparer.OrdinalIgnoreCase);
        var widgetMilestones = WidgetMilestones(snapshot, state.Configuration, today, _time.GetUtcNow());
        var inWidget = widgetMilestones.Select(m => m.ExternalId).ToHashSet(StringComparer.Ordinal);
        var milestones = snapshot.Milestones
            .Select(m => Milestone(m, today) with { IsInWidget = inWidget.Contains(m.ExternalId) })
            .OrderBy(m => m.IsClosed)
            .ThenBy(m => m.DueDate ?? DateOnly.MaxValue)
            .ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var labels = snapshot.Labels
            .Select(l => new LabelCountProjection(l.Name, l.Color, l.OpenIssueCount ?? 0, l.ClosedIssueCount, highlighted.Contains(l.Name)))
            .Where(l => l.IsHighlighted || l.OpenIssues > 0)
            .OrderByDescending(l => l.IsHighlighted)
            .ThenByDescending(l => l.OpenIssues)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var recentlyChanged = snapshot.Issues
            .OrderByDescending(i => i.UpdatedAt)
            .ThenByDescending(i => i.Number)
            .Take(RecentListSize)
            .Select(IssueItem)
            .ToList();
        var recentlyClosed = snapshot.Issues
            .Where(i => i.State == WorkItemState.Closed)
            .OrderByDescending(i => i.ClosedAt ?? i.UpdatedAt)
            .ThenByDescending(i => i.Number)
            .Take(RecentListSize)
            .Select(IssueItem)
            .ToList();
        var openChangeRequests = snapshot.ChangeRequests
            .Where(c => c.State == ChangeRequestState.Open)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new ChangeRequestProjection(c.Number, c.Title, c.State, c.IsDraft, c.UpdatedAt, c.WebUrl))
            .ToList();
        var builds = snapshot.Builds.Select(Build).ToList();

        return new DashboardProjection(
            Header(state),
            true,
            Summary(snapshot, today, mode),
            widgetMilestones.Count > 0 ? widgetMilestones[0] : null,
            milestones,
            labels,
            recentlyChanged,
            recentlyClosed,
            openChangeRequests,
            builds,
            Health(state));
    }

    /// <summary>How long a completed milestone without due date stays in the automatic selection.</summary>
    public static readonly TimeSpan RecentlyCompletedWindow = TimeSpan.FromDays(7);

    /// <summary>Selects the milestones shown in the compact widget (issue #12).</summary>
    /// <remarks>
    /// <para><b>Manual:</b> the configured milestones that exist in the snapshot, in the configured order.</para>
    /// <para><b>Automatic (up to N):</b> open milestones with a due date today or later (nearest first, then more open
    /// issues); then open milestones without due date or overdue that still have open issues (most open first, then
    /// title); then recently completed milestones without due date (all issues closed, last one within
    /// <see cref="RecentlyCompletedWindow"/>). Completed milestones with a future due date are part of the first group.
    /// Closed milestones are never chosen automatically.</para>
    /// </remarks>
    /// <param name="snapshot">Snapshot.</param>
    /// <param name="configuration">Repository configuration.</param>
    /// <param name="today">Today's local date.</param>
    /// <param name="now">Current time (UTC).</param>
    /// <returns>The widget milestones in display order.</returns>
    public static IReadOnlyList<MilestoneProgressProjection> WidgetMilestones(RepositorySnapshot snapshot, RepositoryConfiguration configuration, DateOnly today, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.MilestoneMode == MilestoneSelectionMode.Manual)
        {
            var byId = snapshot.Milestones.ToDictionary(m => m.ExternalId, StringComparer.Ordinal);
            return configuration.SelectedMilestoneIds
                .Take(RepositoryConfiguration.MaxWidgetMilestones)
                .Where(byId.ContainsKey)
                .Select(id => Milestone(byId[id], today) with { IsInWidget = true })
                .ToList();
        }

        var lastClosed = snapshot.Issues
            .Where(i => i.State == WorkItemState.Closed && i.Milestone is not null)
            .GroupBy(i => i.Milestone!.ExternalId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(i => i.ClosedAt ?? i.UpdatedAt), StringComparer.Ordinal);
        var open = snapshot.Milestones.Where(m => !m.IsClosed).ToList();
        var upcoming = open
            .Where(m => m.DueDate is { } due && due >= today)
            .OrderBy(m => m.DueDate)
            .ThenByDescending(m => m.OpenIssueCount)
            .ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase);
        var active = open
            .Where(m => (m.DueDate is null || m.DueDate < today) && m.OpenIssueCount > 0)
            .OrderByDescending(m => m.OpenIssueCount)
            .ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase);
        var recentlyCompleted = open
            .Where(m => m.DueDate is null && m.OpenIssueCount == 0 && m.ClosedIssueCount > 0
                && lastClosed.TryGetValue(m.ExternalId, out var closedAt) && now - closedAt <= RecentlyCompletedWindow)
            .OrderByDescending(m => lastClosed[m.ExternalId])
            .ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase);
        return upcoming.Concat(active).Concat(recentlyCompleted)
            .Take(Math.Clamp(configuration.AutomaticMilestoneCount, 1, RepositoryConfiguration.MaxWidgetMilestones))
            .Select(m => Milestone(m, today) with { IsInWidget = true })
            .ToList();
    }

    private DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).DateTime);

    private bool IsToday(DateTimeOffset? instant, DateOnly today) =>
        instant is { } value && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, _time.LocalTimeZone).DateTime) == today;

    private bool InPeriod(DateTimeOffset? instant, DateOnly today, DailyCountMode mode) => mode switch
    {
        DailyCountMode.Last24Hours => instant is { } value && value > _time.GetUtcNow().AddHours(-24) && value <= _time.GetUtcNow(),
        _ => IsToday(instant, today),
    };

    private SummaryProjection Summary(RepositorySnapshot? snapshot, DateOnly today, DailyCountMode mode)
    {
        var period = mode == DailyCountMode.Last24Hours ? Last24HoursLabel : TodayLabel;
        if (snapshot is null)
        {
            return new SummaryProjection(0, 0, 0, 0, 0, 0, 0, "change requests", period);
        }

        var open = snapshot.Issues.Where(i => i.State == WorkItemState.Open).ToList();
        var label = snapshot.Repository.Provider == RepositoryProviderKind.GitLab ? "merge requests" : "pull requests";
        return new SummaryProjection(
            snapshot.IssueCounts.Open,
            snapshot.IssueCounts.Closed,
            snapshot.Issues.Count(i => i.State == WorkItemState.Closed && InPeriod(i.ClosedAt, today, mode)),
            snapshot.Issues.Count(i => InPeriod(i.CreatedAt, today, mode)),
            open.Count(i => i.Milestone is null),
            open.Count(i => i.Assignees.Count == 0),
            snapshot.ChangeRequests.Count(c => c.State == ChangeRequestState.Open),
            label,
            period,
            snapshot.RecentCommits?.Count(c => InPeriod(c, today, mode)));
    }

    private static MilestoneProgressProjection Milestone(MilestoneSnapshot m, DateOnly today) =>
        new(m.ExternalId, m.Title, m.OpenIssueCount, m.ClosedIssueCount, m.DueDate, m.IsClosed,
            !m.IsClosed && m.DueDate is { } due && due < today, m.WebUrl);

    private static BuildProjection? LatestDefaultBranchBuild(RepositorySnapshot snapshot)
    {
        var builds = snapshot.Builds.Where(b => snapshot.DefaultBranch is null || b.RefName == snapshot.DefaultBranch);
        var latest = builds.OrderByDescending(b => b.CreatedAt).FirstOrDefault();
        return latest is null ? null : Build(latest);
    }

    private static BuildProjection Build(BuildSnapshot b) =>
        new(b.DisplayName, b.State, b.RefName, b.CommitSha is { Length: > 8 } sha ? sha[..8] : b.CommitSha, b.CreatedAt, b.CompletedAt, b.WebUrl);

    private static IssueListItemProjection IssueItem(IssueSnapshot i) =>
        new(i.Number, i.Title, i.State, i.UpdatedAt, i.ClosedAt, i.Milestone?.Title,
            i.Labels.Select(l => l.Name).ToList(), i.Assignees.Select(a => a.Login).ToList(), i.WebUrl);

    private static RepositoryHeaderProjection Header(RepositoryRuntimeState state)
    {
        var configuration = state.Configuration;
        var repository = state.Snapshot?.Repository;
        var webUrl = Uri.TryCreate(configuration.ServerUri, configuration.FullPath, out var url) ? url : null;
        return new RepositoryHeaderProjection(
            configuration.Id,
            configuration.EffectiveDisplayName,
            repository?.FullPath ?? configuration.FullPath,
            configuration.Provider,
            configuration.ServerUri.Host,
            state.Snapshot?.DefaultBranch,
            webUrl);
    }

    private static RepositoryHealthProjection Health(RepositoryRuntimeState state)
    {
        var summary = state.Health switch
        {
            RepositoryHealthState.Healthy => "Up to date",
            RepositoryHealthState.Refreshing => "Loading…",
            RepositoryHealthState.Stale => "Data may be outdated",
            RepositoryHealthState.Offline => "Offline – showing last known data",
            RepositoryHealthState.RateLimited => state.BlockedUntil is { } until
                ? string.Create(CultureInfo.CurrentCulture, $"Rate limited until {until.ToLocalTime():t}")
                : "Rate limited",
            RepositoryHealthState.AuthenticationFailed => "Token rejected – update the token in Settings",
            RepositoryHealthState.AuthorizationFailed => "Access denied – the token lacks permissions",
            RepositoryHealthState.NotFound => "Repository not found or not accessible",
            RepositoryHealthState.ProviderError => "Provider error – retrying",
            RepositoryHealthState.ConfigurationError => "Configuration error – check Settings",
            RepositoryHealthState.GatewayBlocked => state.GatewayHost is { } host
                ? $"Blocked by a login gateway ({host}) – adjust the User-Agent"
                : "Blocked by a login gateway – adjust the User-Agent",
            _ => state.Health.ToString(),
        };
        return new RepositoryHealthProjection(state.Health, summary, state.LastError, state.LastSuccessAt, state.LastAttemptAt, state.BlockedUntil, state.IsRefreshing, state.RateLimit);
    }
}
