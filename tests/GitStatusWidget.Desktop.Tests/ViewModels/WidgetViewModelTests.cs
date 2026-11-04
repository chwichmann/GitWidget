using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Desktop.Tests.TestData;
using GitStatusWidget.Desktop.Tests.TestDoubles;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.Events;
using GitStatusWidget.Domain.Health;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Repositories;
using Microsoft.Extensions.Time.Testing;

namespace GitStatusWidget.Desktop.Tests.ViewModels;

public sealed class WidgetViewModelTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(Data.T0);
    private readonly RepositoryStatusHub _hub = new();
    private readonly FakeSynchronizationController _controller = new();
    private readonly FakeNavigator _navigator = new();
    private readonly InlineUiDispatcher _ui = new();
    private WidgetViewModel? _vm;

    public void Dispose() => _vm?.Dispose();

    private WidgetViewModel Create() => _vm = new WidgetViewModel(_hub, new DashboardProjector(_time), _controller, _ui, _navigator, _time);

    [Fact]
    public void Empty_widget_shows_empty_state()
    {
        var vm = Create();

        vm.Rows.Should().BeEmpty();
        vm.IsEmpty.Should().BeTrue();
        vm.FooterText.Should().Be("No repositories configured");

        vm.OpenSettingsCommand.Execute(null);
        _navigator.Calls.Should().Equal("ShowSettings(,True)");
    }

    [Fact]
    public void Existing_states_are_loaded_in_sort_order()
    {
        var b = Data.Config(name: "b", sortOrder: 1);
        var a = Data.Config(name: "a", sortOrder: 0);
        _hub.Publish(Data.State(b));
        _hub.Publish(Data.State(a));

        var vm = Create();

        vm.Rows.Select(r => r.Id).Should().Equal(a.Id, b.Id);
        vm.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Rows_published_later_are_inserted_in_sort_order()
    {
        var vm = Create();
        var c = Data.Config(name: "c", sortOrder: 2);
        var a = Data.Config(name: "a", sortOrder: 0);
        var b = Data.Config(name: "b", sortOrder: 1);

        _hub.Publish(Data.State(c));
        _hub.Publish(Data.State(a));
        _hub.Publish(Data.State(b));

        vm.Rows.Select(r => r.DisplayName).Should().Equal("octo/a", "octo/b", "octo/c");
    }

    [Fact(Skip = "Bug: reordering repositories in Settings (SortOrder change) does not reorder existing widget rows; see report.")]
    public void Rows_follow_sort_order_changes()
    {
        var vm = Create();
        var a = Data.Config(name: "a", sortOrder: 0);
        var b = Data.Config(name: "b", sortOrder: 1);
        _hub.Publish(Data.State(a));
        _hub.Publish(Data.State(b));

        _hub.Publish(Data.State(a with { SortOrder = 1 }));
        _hub.Publish(Data.State(b with { SortOrder = 0 }));

        vm.Rows.Select(r => r.Id).Should().Equal(b.Id, a.Id);
    }

    [Fact]
    public void Row_is_updated_in_place_on_state_change()
    {
        var vm = Create();
        var configuration = Data.Config();
        _hub.Publish(Data.State(configuration, Data.Snapshot(configuration, issues: [Data.Issue(1)])));
        var row = vm.Rows.Single();
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _hub.Publish(Data.State(configuration, Data.Snapshot(configuration, issues: [Data.Issue(1), Data.Issue(2), Data.Issue(3, WorkItemState.Closed)])));

        vm.Rows.Should().ContainSingle().Which.Should().BeSameAs(row);
        row.OpenIssues.Should().Be(2);
        row.ClosedIssues.Should().Be(1);
        changed.Should().Contain(nameof(RepositoryRowViewModel.OpenIssues));
    }

    [Fact]
    public void Row_projects_counts_milestone_build_and_health()
    {
        var vm = Create();
        var configuration = Data.Config(provider: RepositoryProviderKind.GitLab, owner: "group", name: "project", displayName: "Project");
        var snapshot = Data.Snapshot(configuration,
            issues:
            [
                Data.Issue(1),
                Data.Issue(2, WorkItemState.Closed, updatedAt: Data.T0.AddHours(-1)),
                Data.Issue(3, WorkItemState.Closed, updatedAt: Data.T0.AddDays(-3)),
            ],
            milestones: [Data.Milestone("M1", open: 1, closed: 3, due: new DateOnly(2026, 11, 30))],
            builds: [Data.Build("1", BuildState.Failed, Data.T0)]);

        _hub.Publish(Data.State(configuration, snapshot));

        var row = vm.Rows.Single();
        row.DisplayName.Should().Be("Project");
        row.ProviderBadge.Should().Be("GL");
        row.ProviderName.Should().Be("GitLab");
        row.HasData.Should().BeTrue();
        row.OpenIssues.Should().Be(1);
        row.ClosedIssues.Should().Be(2);
        row.ClosedToday.Should().Be(1);
        row.HasMilestone.Should().BeTrue();
        row.MilestoneTitle.Should().Be("Milestone M1");
        row.MilestonePercent.Should().Be(75);
        row.MilestoneText.Should().Be("3/4");
        row.MilestoneOverdue.Should().BeFalse();
        row.HasBuild.Should().BeTrue();
        row.BuildState.Should().Be(BuildState.Failed);
        row.BuildText.Should().Be("CI: failed");
        row.Health.Should().Be(RepositoryHealthState.Healthy);
        row.HealthText.Should().Be("Up to date");
        row.HasProblem.Should().BeFalse();
        row.FreshnessText.Should().Be("just now");
        row.ToolTipText.Should().Be("Project – Up to date");
    }

    [Fact]
    public void Row_without_snapshot_or_ci_hides_sections()
    {
        var vm = Create();
        _hub.Publish(Data.State(Data.Config(), health: RepositoryHealthState.Refreshing) with { IsRefreshing = true });

        var row = vm.Rows.Single();
        row.HasData.Should().BeFalse();
        row.HasBuild.Should().BeFalse();
        row.HasMilestone.Should().BeFalse();
        row.IsRefreshing.Should().BeTrue();
        row.HasProblem.Should().BeFalse("refreshing is not a problem");
        row.FreshnessText.Should().Be("never updated");
        vm.IsAnyRefreshing.Should().BeTrue();
        vm.FooterText.Should().StartWith("1 repository · ");
    }

    [Fact(Skip = "Bug (cosmetic): footer reads '1 repository · updated never updated' before the first success; see report.")]
    public void Footer_before_first_success_reads_naturally()
    {
        var vm = Create();
        _hub.Publish(Data.State(Data.Config(), health: RepositoryHealthState.Refreshing));

        vm.FooterText.Should().Be("1 repository · not updated yet");
    }

    [Fact]
    public void Problem_rows_expose_text_and_detail()
    {
        var vm = Create();
        var configuration = Data.Config();
        _hub.Publish(Data.State(configuration, Data.Snapshot(configuration), RepositoryHealthState.AuthenticationFailed) with { LastError = "401 Unauthorized" });

        var row = vm.Rows.Single();
        row.HasProblem.Should().BeTrue();
        row.HealthText.Should().Be("Token rejected – update the token in Settings");
        row.ToolTipText.Should().Be("octo/widget – Token rejected – update the token in Settings\n401 Unauthorized");
    }

    [Fact]
    public void Removed_repository_removes_its_row()
    {
        var vm = Create();
        var a = Data.Config(name: "a", sortOrder: 0);
        var b = Data.Config(name: "b", sortOrder: 1);
        _hub.Publish(Data.State(a));
        _hub.Publish(Data.State(b));

        _hub.Remove(a.Id);

        vm.Rows.Select(r => r.Id).Should().Equal(b.Id);
        vm.FooterText.Should().StartWith("1 repository");

        _hub.Remove(b.Id);

        vm.IsEmpty.Should().BeTrue();
        vm.FooterText.Should().Be("No repositories configured");
    }

    [Fact]
    public void Footer_shows_the_most_recent_success_and_ages_with_the_shared_timer()
    {
        var vm = Create();
        var a = Data.Config(name: "a", sortOrder: 0);
        var b = Data.Config(name: "b", sortOrder: 1);
        _hub.Publish(Data.State(a, Data.Snapshot(a), lastSuccessAt: Data.T0.AddMinutes(-10)));
        _hub.Publish(Data.State(b, Data.Snapshot(b), lastSuccessAt: Data.T0.AddMinutes(-3)));

        vm.FooterText.Should().Be("2 repositories · updated 3 min ago");
        vm.Rows[0].FreshnessText.Should().Be("10 min ago");

        _time.Advance(TimeSpan.FromMinutes(2));

        vm.FooterText.Should().Be("2 repositories · updated 5 min ago");
        vm.Rows[0].FreshnessText.Should().Be("12 min ago");
    }

    [Fact]
    public void Dispose_stops_the_timer_and_unsubscribes()
    {
        var vm = Create();
        vm.Dispose();
        var posts = _ui.PostCount;

        _hub.Publish(Data.State(Data.Config()));
        _time.Advance(TimeSpan.FromMinutes(5));

        _ui.PostCount.Should().Be(posts);
        vm.Rows.Should().BeEmpty();
    }

    [Fact]
    public void Live_changes_raise_row_animations()
    {
        var vm = Create();
        var configuration = Data.Config();
        _hub.Publish(Data.State(configuration));
        var animations = new List<RowAnimation>();
        vm.Rows.Single().AnimationRequested += (_, a) => animations.Add(a);

        _hub.PublishChanges(new RepositoryChangesDetected(configuration.Id,
        [
            Data.Event(ChangeEventType.IssueClosed, number: 1),
            Data.Event(ChangeEventType.IssueClosed, number: 2),
            Data.Event(ChangeEventType.BuildFailed, EventPriority.High, number: 3),
            Data.Event(ChangeEventType.IssueLabelsChanged, EventPriority.Low, number: 4),
        ], IsCatchUp: false));

        animations.Should().Equal(RowAnimation.IssueClosed, RowAnimation.BuildFailed);
    }

    [Theory]
    [InlineData(ChangeEventType.IssueCreated, RowAnimation.IssueOpened)]
    [InlineData(ChangeEventType.IssueReopened, RowAnimation.IssueOpened)]
    [InlineData(ChangeEventType.IssueClosed, RowAnimation.IssueClosed)]
    [InlineData(ChangeEventType.BuildFailed, RowAnimation.BuildFailed)]
    public void Event_types_map_to_row_animations(ChangeEventType type, RowAnimation expected)
    {
        var vm = Create();
        var configuration = Data.Config();
        _hub.Publish(Data.State(configuration));
        var animations = new List<RowAnimation>();
        vm.Rows.Single().AnimationRequested += (_, a) => animations.Add(a);

        _hub.PublishChanges(new RepositoryChangesDetected(configuration.Id, [Data.Event(type)], IsCatchUp: false));

        animations.Should().Equal(expected);
    }

    [Fact]
    public void Catch_up_changes_do_not_animate()
    {
        var vm = Create();
        var configuration = Data.Config();
        _hub.Publish(Data.State(configuration));
        var animations = new List<RowAnimation>();
        vm.Rows.Single().AnimationRequested += (_, a) => animations.Add(a);

        _hub.PublishChanges(new RepositoryChangesDetected(configuration.Id,
            [Data.Event(ChangeEventType.IssueClosed), Data.Event(ChangeEventType.BuildFailed, EventPriority.High)], IsCatchUp: true));

        animations.Should().BeEmpty();
    }

    [Fact]
    public void Changes_for_unknown_repositories_are_ignored()
    {
        var vm = Create();
        _hub.Publish(Data.State(Data.Config()));
        var animations = new List<RowAnimation>();
        vm.Rows.Single().AnimationRequested += (_, a) => animations.Add(a);

        _hub.PublishChanges(new RepositoryChangesDetected(Guid.NewGuid(), [Data.Event(ChangeEventType.IssueClosed)], IsCatchUp: false));

        animations.Should().BeEmpty();
    }

    [Fact]
    public void Commands_delegate_to_controller_and_navigator()
    {
        var vm = Create();
        var configuration = Data.Config();
        _hub.Publish(Data.State(configuration));

        vm.RefreshCommand.Execute(null);
        vm.OpenDashboardCommand.Execute(vm.Rows[0]);
        vm.OpenDashboardCommand.Execute(null);
        vm.OpenSettingsCommand.Execute(null);
        vm.HideCommand.Execute(null);

        _controller.RefreshRequests.Should().Equal((Guid?)null);
        _navigator.Calls.Should().Equal($"ShowDashboard({configuration.Id})", "ShowDashboard()", "ShowSettings(,False)", "HideWidget");
    }
}
