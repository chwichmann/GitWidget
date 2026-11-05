using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Events;
using GitStatusWidget.Domain.Health;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Labels;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Domain.Snapshots;
using Microsoft.Extensions.Time.Testing;
using static GitStatusWidget.Application.Tests.TestData.Snapshots;

namespace GitStatusWidget.Application.Tests.Projection;

public sealed class DashboardProjectorTests
{
    // 2026-11-04 23:30 UTC is already 2026-11-05 00:30 in Zurich (UTC+1).
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 23, 30, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);
    private readonly DashboardProjector _projector;

    public DashboardProjectorTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"));
        _projector = new DashboardProjector(_time);
    }

    private static RepositoryConfiguration Config(string? selectedMilestone = null, params string[] highlighted)
    {
        var id = Guid.Parse("8a1f0c0e-1111-4c4c-9a9a-123456789abc");
        return new RepositoryConfiguration(id, RepositoryProviderKind.GitLab, new Uri("https://gitlab.example.com/"), "group", "project",
            "ref", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), true)
        {
            SelectedMilestoneId = selectedMilestone,
            HighlightedLabels = highlighted,
        };
    }

    private static RepositorySnapshot Snapshot(params IssueSnapshot[] issues) =>
        TestData.Snapshots.Snapshot(Now, issues) with
        {
            Repository = new RepositoryIdentity(RepositoryProviderKind.GitLab, "gitlab.example.com", "group", "project", "99"),
            IssueCounts = new IssueCounts(issues.Count(i => i.State == WorkItemState.Open), 1134),
        };

    [Fact]
    public void Closed_today_uses_the_local_time_zone()
    {
        var closedJustAfterLocalMidnight = Issue(1, WorkItemState.Closed, updatedAt: new DateTimeOffset(2026, 11, 4, 23, 15, 0, TimeSpan.Zero));
        var closedYesterdayLocal = Issue(2, WorkItemState.Closed, updatedAt: new DateTimeOffset(2026, 11, 4, 22, 45, 0, TimeSpan.Zero));
        var state = new RepositoryRuntimeState(Config(), Snapshot(closedJustAfterLocalMidnight, closedYesterdayLocal, Issue(3)), RepositoryHealthState.Healthy);

        var row = _projector.ProjectRow(state);

        row.Summary.ClosedToday.Should().Be(1);
        row.Summary.ClosedIssues.Should().Be(1134, "closed totals come from the repository count, not the loaded window");
        row.Summary.OpenIssues.Should().Be(1);
    }

    [Fact]
    public void Last_24_hours_counts_a_rolling_window_across_midnight()
    {
        // Now: 2026-11-04 23:30 UTC = 2026-11-05 00:30 Zurich.
        var closedYesterdayLocal = Issue(1, WorkItemState.Closed, updatedAt: new DateTimeOffset(2026, 11, 4, 20, 0, 0, TimeSpan.Zero));
        var closedTooLongAgo = Issue(2, WorkItemState.Closed, updatedAt: new DateTimeOffset(2026, 11, 3, 23, 0, 0, TimeSpan.Zero));
        var openedTwoHoursAgo = Issue(3, createdAt: Now.AddHours(-2));
        var state = new RepositoryRuntimeState(Config(), Snapshot(closedYesterdayLocal, closedTooLongAgo, openedTwoHoursAgo), RepositoryHealthState.Healthy);

        var calendar = _projector.ProjectDashboard(state).Summary;
        var rolling = _projector.ProjectDashboard(state, DailyCountMode.Last24Hours).Summary;

        calendar.ClosedToday.Should().Be(0);
        calendar.PeriodLabel.Should().Be("today");
        rolling.ClosedToday.Should().Be(1);
        rolling.OpenedToday.Should().Be(1);
        rolling.PeriodLabel.Should().Be("last 24 h");
    }

    [Fact]
    public void Daylight_saving_day_counts_calendar_day_and_rolling_window_correctly()
    {
        // 2026-10-25 has 25 hours in Zurich (CEST -> CET at 03:00). Now: 23:30 CET = 22:30 UTC.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 22, 30, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"));
        var projector = new DashboardProjector(time);
        // 00:30 CEST local = 22:30 UTC the day before: same calendar day, but exactly 24 h ago.
        var closed = Issue(1, WorkItemState.Closed, updatedAt: new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.Zero));
        var state = new RepositoryRuntimeState(Config(), Snapshot(closed), RepositoryHealthState.Healthy);

        projector.ProjectRow(state).Summary.ClosedToday.Should().Be(1);
        projector.ProjectRow(state, DailyCountMode.Last24Hours).Summary.ClosedToday.Should().Be(0);
    }

    [Fact]
    public void Current_milestone_prefers_configured_then_nearest_due_date()
    {
        var snapshot = Snapshot(Issue(1)) with
        {
            Milestones =
            [
                new MilestoneSnapshot("A", "Later", null, null, new DateOnly(2027, 3, 1), false, 5, 5),
                new MilestoneSnapshot("B", "0.10.1 Network Services", null, null, new DateOnly(2026, 11, 30), false, 3, 602),
                new MilestoneSnapshot("C", "Past", null, null, new DateOnly(2026, 1, 1), false, 9, 1),
            ],
        };

        _projector.ProjectRow(new RepositoryRuntimeState(Config(), snapshot, RepositoryHealthState.Healthy))
            .CurrentMilestone!.ExternalId.Should().Be("B");
        _projector.ProjectRow(new RepositoryRuntimeState(Config("A"), snapshot, RepositoryHealthState.Healthy))
            .CurrentMilestone!.ExternalId.Should().Be("A");

        var dashboard = _projector.ProjectDashboard(new RepositoryRuntimeState(Config(), snapshot, RepositoryHealthState.Healthy));
        dashboard.Milestones.Single(m => m.ExternalId == "C").IsOverdue.Should().BeTrue();
        dashboard.Milestones.Single(m => m.ExternalId == "B").Completion.Should().BeApproximately(602d / 605, 1e-9);
    }

    [Fact]
    public void Automatic_selection_picks_a_new_open_milestone_without_due_date()
    {
        // Issue #8: milestone 0.1.2 has no due date; once loaded it is the automatic choice.
        var snapshot = Snapshot(Issue(7, milestone: new MilestoneReference("MI", "0.1.2"))) with
        {
            Milestones = [new MilestoneSnapshot("MI", "0.1.2", null, null, null, false, 1, 0)],
        };

        _projector.ProjectRow(new RepositoryRuntimeState(Config(), snapshot, RepositoryHealthState.Healthy))
            .CurrentMilestone!.Title.Should().Be("0.1.2");
    }

    [Fact]
    public void Gateway_blocked_health_names_the_gateway_and_the_remedy()
    {
        var state = new RepositoryRuntimeState(Config(), Snapshot(Issue(1)), RepositoryHealthState.GatewayBlocked) { GatewayHost = "portal.example.net" };

        var health = _projector.ProjectRow(state).Health;

        health.Summary.Should().Be("Blocked by a login gateway (portal.example.net) – adjust the User-Agent");
        GatewayUserAgent.Suggest("0.1.2").Should().Be("GitWidget/0.1.2 git/2.47.0");
    }

    [Theory]
    [InlineData(3, 602, 99)]
    [InlineData(2, 1, 33)]
    [InlineData(71, 29, 29)]
    [InlineData(1, 999, 99)]
    [InlineData(0, 605, 100)]
    [InlineData(5, 0, 0)]
    public void Completion_percent_is_rounded_down(int open, int closed, int expected)
    {
        // Issue #11: 100 % only when nothing is open; integer arithmetic (29 % must not become 28 %).
        new MilestoneProgressProjection("m", "m", open, closed, null, false, false, null).CompletionPercent.Should().Be(expected);
    }

    [Fact]
    public void Milestone_without_issues_has_no_percentage()
    {
        new MilestoneProgressProjection("m", "m", 0, 0, null, false, false, null).CompletionPercent.Should().BeNull();
    }

    [Fact]
    public void Milestone_without_issues_has_zero_completion()
    {
        new MilestoneProgressProjection("x", "x", 0, 0, null, false, false, null).Completion.Should().Be(0);
    }

    [Fact]
    public void Labels_put_highlighted_first_and_hide_unused()
    {
        var snapshot = Snapshot(Issue(1)) with
        {
            Labels =
            [
                new LabelSnapshot("Docs", "#00ff00", null, 7),
                new LabelSnapshot("Security", "#ff0000", null, 0),
                new LabelSnapshot("Bug", "#ff0000", null, 3),
                new LabelSnapshot("Unused", "#cccccc", null, 0),
            ],
        };

        var labels = _projector.ProjectDashboard(new RepositoryRuntimeState(Config(null, "Bug", "Security"), snapshot, RepositoryHealthState.Healthy)).Labels;

        labels.Select(l => l.Name).Should().Equal("Bug", "Security", "Docs");
    }

    [Fact]
    public void Summary_counts_unassigned_and_unplanned_open_issues_and_change_requests()
    {
        var snapshot = Snapshot(
            Issue(1, milestone: new MilestoneReference("B", "x"), assignees: ["ann"]),
            Issue(2),
            Issue(3, WorkItemState.Closed)) with
        {
            ChangeRequests = [ChangeRequest(5, ChangeRequestState.Open, Now), ChangeRequest(6, ChangeRequestState.Merged, Now)],
        };

        var summary = _projector.ProjectRow(new RepositoryRuntimeState(Config(), snapshot, RepositoryHealthState.Healthy)).Summary;

        summary.OpenWithoutMilestone.Should().Be(1);
        summary.OpenWithoutAssignee.Should().Be(1);
        summary.OpenChangeRequests.Should().Be(1);
        summary.ChangeRequestLabel.Should().Be("merge requests");
    }

    [Fact]
    public void Latest_build_is_taken_from_the_default_branch_and_absent_without_ci()
    {
        var noCi = _projector.ProjectRow(new RepositoryRuntimeState(Config(), Snapshot(Issue(1)), RepositoryHealthState.Healthy));
        noCi.LatestBuild.Should().BeNull();

        var snapshot = Snapshot(Issue(1)) with
        {
            Builds =
            [
                Build("2", BuildState.Failed, Now.AddMinutes(-1)) with { RefName = "feature" },
                Build("1", BuildState.Succeeded, Now.AddMinutes(-10)),
            ],
        };
        var row = _projector.ProjectRow(new RepositoryRuntimeState(Config(), snapshot, RepositoryHealthState.Healthy));

        row.LatestBuild!.State.Should().Be(BuildState.Succeeded);
    }

    [Fact]
    public void Repository_without_snapshot_projects_an_empty_dashboard()
    {
        var dashboard = _projector.ProjectDashboard(new RepositoryRuntimeState(Config(), null, RepositoryHealthState.Refreshing));

        dashboard.HasData.Should().BeFalse();
        dashboard.Health.Summary.Should().Be("Loading…");
    }

    [Fact]
    public void Activity_headlines_use_provider_presentation_terms()
    {
        var gitlab = new RepositoryIdentity(RepositoryProviderKind.GitLab, "h", "g", "p", "1");
        var mr = new RepositoryChangeEvent("k", ChangeEventType.ChangeRequestMerged, EventPriority.Medium, gitlab, "e", 4, "t", null, Now);
        var build = mr with { Type = ChangeEventType.BuildFailed, EntityNumber = null, Detail = "master" };
        var issue = mr with { Repository = Repo, Type = ChangeEventType.IssueClosed, EntityNumber = 12 };

        ActivityText.Headline(mr).Should().Be("Merge request !4 merged");
        ActivityText.Headline(build).Should().Be("Pipeline failed on master");
        ActivityText.Headline(issue).Should().Be("Issue #12 closed");
    }
}
