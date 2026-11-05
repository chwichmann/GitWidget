using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Repositories;
using static GitStatusWidget.Application.Tests.TestData.Snapshots;

namespace GitStatusWidget.Application.Tests.Projection;

/// <summary>Issue #12: several widget milestones per repository, automatic (up to N) or manual.</summary>
public sealed class WidgetMilestoneSelectionTests
{
    private static readonly DateOnly Today = new(2026, 11, 5);
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static RepositoryConfiguration Config(MilestoneSelectionMode mode = MilestoneSelectionMode.Automatic, int count = 1, params string[] ids) =>
        new(Guid.NewGuid(), RepositoryProviderKind.GitHub, new Uri("https://github.com/"), "octo", "widget", "ref",
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), true)
        {
            MilestoneMode = mode,
            AutomaticMilestoneCount = count,
            SelectedMilestoneIds = ids,
        };

    private static MilestoneSnapshot M(string id, int open, int closed, DateOnly? due = null, bool isClosed = false) =>
        new(id, $"Milestone {id}", null, null, due, isClosed, open, closed);

    private static IReadOnlyList<string> Select(RepositoryConfiguration configuration, IEnumerable<MilestoneSnapshot> milestones, IEnumerable<IssueSnapshot>? issues = null)
    {
        var snapshot = Snapshot(Now, issues ?? []) with { Milestones = milestones.ToList() };
        return DashboardProjector.WidgetMilestones(snapshot, configuration, Today, Now).Select(m => m.ExternalId).ToList();
    }

    private readonly MilestoneSnapshot[] _mix =
    [
        M("later", 4, 1, new DateOnly(2027, 2, 1)),
        M("soon", 2, 9, new DateOnly(2026, 11, 20)),
        M("busy", 12, 3),                            // no due date
        M("overdue", 5, 5, new DateOnly(2026, 10, 1)),
        M("quiet", 1, 0),                            // no due date
        M("done", 0, 8, isClosed: true),             // closed: never automatic
    ];

    [Fact]
    public void Automatic_with_one_milestone_behaves_like_before()
    {
        Select(Config(), _mix).Should().Equal("soon");
    }

    [Fact]
    public void Automatic_takes_due_dates_first_then_the_busiest_open_milestones()
    {
        Select(Config(count: 5), _mix).Should().Equal("soon", "later", "busy", "overdue", "quiet");
    }

    [Fact]
    public void Automatic_is_capped_at_five()
    {
        var many = Enumerable.Range(1, 8).Select(i => M($"m{i}", i, 0)).ToList();

        Select(Config(count: 9), many).Should().HaveCount(5);
    }

    [Fact]
    public void Recently_completed_milestone_without_due_date_stays_for_seven_days()
    {
        var milestone = new MilestoneReference("finished", "Milestone finished");
        var closedTwoDaysAgo = Issue(1, WorkItemState.Closed, updatedAt: Now.AddDays(-2), milestone: milestone);
        var closedTenDaysAgo = Issue(2, WorkItemState.Closed, updatedAt: Now.AddDays(-10), milestone: new MilestoneReference("stale", "Milestone stale"));
        MilestoneSnapshot[] milestones = [M("busy", 3, 0), M("finished", 0, 4), M("stale", 0, 6)];

        Select(Config(count: 3), milestones, [closedTwoDaysAgo, closedTenDaysAgo]).Should().Equal("busy", "finished");
    }

    [Fact]
    public void Completed_milestone_with_future_due_date_stays_until_the_due_date()
    {
        MilestoneSnapshot[] milestones = [M("released", 0, 7, new DateOnly(2026, 11, 30)), M("next", 3, 0, new DateOnly(2026, 12, 15))];

        Select(Config(count: 2), milestones).Should().Equal("released", "next");
        Select(Config(count: 2), [M("released", 0, 7, new DateOnly(2026, 11, 1))]).Should().BeEmpty("its due date has passed");
    }

    [Fact]
    public void Manual_mode_keeps_the_configured_order_and_skips_unknown_milestones()
    {
        Select(Config(MilestoneSelectionMode.Manual, 1, "quiet", "missing", "done", "soon"), _mix).Should().Equal("quiet", "done", "soon");
    }

    [Fact]
    public void Dashboard_marks_widget_milestones()
    {
        var snapshot = Snapshot(Now, []) with { Milestones = _mix.ToList() };
        var projector = new DashboardProjector(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now));

        var dashboard = projector.ProjectDashboard(new GitStatusWidget.Application.Synchronization.RepositoryRuntimeState(Config(count: 2), snapshot, Domain.Health.RepositoryHealthState.Healthy));

        dashboard.Milestones.Where(m => m.IsInWidget).Select(m => m.ExternalId).Should().BeEquivalentTo(["soon", "later"]);
        dashboard.CurrentMilestone!.ExternalId.Should().Be("soon");
    }
}
