using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Settings;
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
using GitStatusWidget.Domain.Snapshots;
using Microsoft.Extensions.Time.Testing;

namespace GitStatusWidget.Desktop.Tests.ViewModels;

public sealed class DashboardViewModelTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(Data.T0);
    private readonly RepositoryStatusHub _hub = new();
    private readonly FakeSynchronizationController _controller = new();
    private readonly FakeNavigator _navigator = new();
    private readonly InlineUiDispatcher _ui = new();
    private readonly ActivityFeed _feed;
    private DashboardViewModel? _vm;

    public DashboardViewModelTests() => _feed = new ActivityFeed(_hub, _ui);

    public void Dispose()
    {
        _vm?.Dispose();
        _feed.Dispose();
    }

    private DashboardViewModel Create() => _vm = new DashboardViewModel(_hub, new DashboardProjector(_time), _controller, _ui, _navigator, _feed, _time);

    private static RepositorySnapshot RichSnapshot(RepositoryConfiguration configuration) => Data.Snapshot(configuration,
        issues: [Data.Issue(1, labels: ["bug"]), Data.Issue(2, WorkItemState.Closed, updatedAt: Data.T0.AddHours(-2))],
        milestones: [Data.Milestone("M1", 1, 1)],
        changeRequests: [Data.ChangeRequest(5)],
        builds: [Data.Build("10", BuildState.Succeeded, Data.T0.AddMinutes(-5))]);

    [Fact]
    public void Without_repositories_nothing_is_selected()
    {
        var vm = Create();

        vm.HasRepositories.Should().BeFalse();
        vm.SelectedRepository.Should().BeNull();
        vm.Projection.Should().BeNull();
        vm.HasChangeRequests.Should().BeFalse();
        vm.HasBuilds.Should().BeFalse();

        vm.OpenSettingsCommand.Execute(null);
        _navigator.Calls.Should().Equal("ShowSettings(,True)");
    }

    [Fact]
    public void First_repository_is_selected_and_projected()
    {
        var a = Data.Config(name: "a", sortOrder: 0, displayName: "Alpha");
        var b = Data.Config(name: "b", sortOrder: 1);
        _hub.Publish(Data.State(b, RichSnapshot(b)));
        _hub.Publish(Data.State(a, RichSnapshot(a)));

        var vm = Create();

        vm.HasRepositories.Should().BeTrue();
        vm.Repositories.Select(r => r.Name).Should().Equal("Alpha", "octo/b");
        vm.SelectedRepository!.Id.Should().Be(a.Id);
        vm.Projection!.Header.RepositoryId.Should().Be(a.Id);
        vm.Projection.Header.DisplayName.Should().Be("Alpha");
        vm.FreshnessText.Should().StartWith("Updated just now");
    }

    [Fact]
    public void Select_switches_the_projection_and_ignores_unknown_ids()
    {
        var a = Data.Config(name: "a", sortOrder: 0);
        var b = Data.Config(name: "b", sortOrder: 1);
        _hub.Publish(Data.State(a, RichSnapshot(a)));
        _hub.Publish(Data.State(b, RichSnapshot(b)));
        var vm = Create();

        vm.Select(b.Id);
        vm.SelectedRepository!.Id.Should().Be(b.Id);
        vm.Projection!.Header.RepositoryId.Should().Be(b.Id);

        vm.Select(Guid.NewGuid());
        vm.SelectedRepository!.Id.Should().Be(b.Id);

        vm.Select(null);
        vm.SelectedRepository!.Id.Should().Be(b.Id);

        vm.RefreshCommand.Execute(null);
        _controller.RefreshRequests.Should().Equal(b.Id);
        vm.OpenSettingsCommand.Execute(null);
        _navigator.Calls.Should().Equal($"ShowSettings({b.Id},False)");
    }

    [Fact]
    public void First_published_repository_is_selected_automatically()
    {
        var vm = Create();
        var configuration = Data.Config();

        _hub.Publish(Data.State(configuration, RichSnapshot(configuration)));

        vm.SelectedRepository!.Id.Should().Be(configuration.Id);
        vm.Projection!.HasData.Should().BeTrue();
        vm.HasRepositories.Should().BeTrue();
    }

    [Fact]
    public void Empty_change_request_and_build_sections_are_hidden()
    {
        var configuration = Data.Config();
        _hub.Publish(Data.State(configuration, Data.Snapshot(configuration, issues: [Data.Issue(1)])));
        var vm = Create();

        vm.HasChangeRequests.Should().BeFalse();
        vm.HasBuilds.Should().BeFalse();

        _hub.Publish(Data.State(configuration, RichSnapshot(configuration)));

        vm.HasChangeRequests.Should().BeTrue();
        vm.HasBuilds.Should().BeTrue();
    }

    [Fact]
    public void Github_titles_use_pull_requests_and_workflow_runs()
    {
        var configuration = Data.Config();
        _hub.Publish(Data.State(configuration, RichSnapshot(configuration)));

        var vm = Create();

        vm.ProviderName.Should().Be("GitHub");
        vm.ChangeRequestTitle.Should().Be("Open pull requests");
        vm.BuildTitle.Should().Be("Recent workflow runs");
    }

    [Fact]
    public void Gitlab_titles_use_merge_requests_and_pipelines()
    {
        var configuration = Data.Config(provider: RepositoryProviderKind.GitLab, owner: "group", name: "project");
        _hub.Publish(Data.State(configuration, RichSnapshot(configuration)));

        var vm = Create();

        vm.ProviderName.Should().Be("GitLab");
        vm.ChangeRequestTitle.Should().Be("Open merge requests");
        vm.BuildTitle.Should().Be("Recent pipelines");
        vm.Projection!.Summary.ChangeRequestLabel.Should().Be("merge requests");
    }

    [Fact]
    public void Health_only_update_keeps_the_list_instances()
    {
        var configuration = Data.Config();
        var snapshot = RichSnapshot(configuration);
        _hub.Publish(Data.State(configuration, snapshot));
        var vm = Create();
        var before = vm.Projection!;
        var projectionChanges = 0;
        vm.PropertyChanged += (_, e) => projectionChanges += e.PropertyName == nameof(DashboardViewModel.Projection) ? 1 : 0;

        _hub.Publish(Data.State(configuration, snapshot, RepositoryHealthState.Refreshing) with { IsRefreshing = true });

        var after = vm.Projection!;
        after.Health.State.Should().Be(RepositoryHealthState.Refreshing);
        vm.IsRefreshing.Should().BeTrue();
        after.Milestones.Should().BeSameAs(before.Milestones);
        after.Labels.Should().BeSameAs(before.Labels);
        after.RecentlyChanged.Should().BeSameAs(before.RecentlyChanged);
        after.RecentlyClosed.Should().BeSameAs(before.RecentlyClosed);
        after.OpenChangeRequests.Should().BeSameAs(before.OpenChangeRequests);
        after.RecentBuilds.Should().BeSameAs(before.RecentBuilds);
        projectionChanges.Should().Be(1, "only the health part is replaced");
    }

    [Fact]
    public void Content_change_replaces_the_lists()
    {
        var configuration = Data.Config();
        _hub.Publish(Data.State(configuration, RichSnapshot(configuration)));
        var vm = Create();
        var before = vm.Projection!;

        var changed = RichSnapshot(configuration) with { Builds = [Data.Build("11", BuildState.Failed, Data.T0)] };
        _hub.Publish(Data.State(configuration, changed));

        vm.Projection!.RecentBuilds.Should().NotBeSameAs(before.RecentBuilds);
        vm.Projection.RecentBuilds.Single().State.Should().Be(BuildState.Failed);
    }

    [Fact]
    public void Updates_of_other_repositories_do_not_rebuild_the_selection()
    {
        var a = Data.Config(name: "a", sortOrder: 0);
        var b = Data.Config(name: "b", sortOrder: 1);
        _hub.Publish(Data.State(a, RichSnapshot(a)));
        _hub.Publish(Data.State(b, RichSnapshot(b)));
        var vm = Create();
        var before = vm.Projection;

        _hub.Publish(Data.State(b, RichSnapshot(b), RepositoryHealthState.AuthenticationFailed));

        vm.Projection.Should().BeSameAs(before);
        vm.Repositories[1].HasProblem.Should().BeTrue();
        vm.Repositories[1].Status.Should().Be("Token rejected – update the token in Settings");
        vm.Repositories[0].HasProblem.Should().BeFalse();
    }

    [Fact]
    public void Problem_state_is_exposed_for_the_selected_repository()
    {
        var configuration = Data.Config();
        _hub.Publish(Data.State(configuration, RichSnapshot(configuration), RepositoryHealthState.Offline));

        var vm = Create();

        vm.HasProblem.Should().BeTrue();
        vm.Projection!.Health.Summary.Should().Be("Offline – showing last known data");
        vm.Repositories.Single().Badge.Should().Be("GH");
    }

    [Fact]
    public void Removing_the_selected_repository_selects_the_next()
    {
        var a = Data.Config(name: "a", sortOrder: 0);
        var b = Data.Config(name: "b", sortOrder: 1);
        _hub.Publish(Data.State(a, RichSnapshot(a)));
        _hub.Publish(Data.State(b, RichSnapshot(b)));
        var vm = Create();

        _hub.Remove(a.Id);

        vm.Repositories.Select(r => r.Id).Should().Equal(b.Id);
        vm.SelectedRepository!.Id.Should().Be(b.Id);
        vm.Projection!.Header.RepositoryId.Should().Be(b.Id);

        _hub.Remove(b.Id);

        vm.HasRepositories.Should().BeFalse();
        vm.SelectedRepository.Should().BeNull();
        vm.Projection.Should().BeNull();
    }

    [Fact]
    public void Activity_is_filtered_by_the_selected_repository()
    {
        var a = Data.Config(name: "a", sortOrder: 0);
        var b = Data.Config(name: "b", sortOrder: 1);
        _hub.Publish(Data.State(a, RichSnapshot(a)));
        _hub.Publish(Data.State(b, RichSnapshot(b)));
        var vm = Create();
        vm.HasActivity.Should().BeFalse();

        _hub.PublishChanges(new RepositoryChangesDetected(a.Id, [Data.Event(ChangeEventType.IssueClosed, number: 1)], IsCatchUp: false));
        _hub.PublishChanges(new RepositoryChangesDetected(b.Id, [Data.Event(ChangeEventType.IssueCreated, number: 2)], IsCatchUp: true));
        _hub.PublishChanges(new RepositoryChangesDetected(a.Id, [Data.Event(ChangeEventType.BuildFailed, EventPriority.High, number: 3)], IsCatchUp: false));

        vm.HasActivity.Should().BeTrue();
        vm.Activity.Select(i => i.Headline).Should().Equal("Workflow run failed", "Issue #1 closed");
        vm.Activity.Should().OnlyContain(i => i.RepositoryId == a.Id);

        vm.Select(b.Id);

        vm.Activity.Select(i => i.Headline).Should().Equal("Issue #2 opened");
        vm.Activity.Single().RepositoryName.Should().Be("octo/b");
    }

    [Fact]
    public void Open_url_goes_through_the_navigator()
    {
        var vm = Create();
        var url = new Uri("https://github.com/octo/widget/issues/1");

        vm.OpenUrlCommand.Execute(url);

        _navigator.Calls.Should().Equal($"OpenInBrowser({url})");
    }

    [Fact]
    public void Renaming_the_repository_updates_the_header()
    {
        var configuration = Data.Config();
        var snapshot = RichSnapshot(configuration);
        _hub.Publish(Data.State(configuration, snapshot));
        var vm = Create();

        _hub.Publish(Data.State(configuration with { DisplayName = "Renamed" }, snapshot));

        vm.Repositories.Single().Name.Should().Be("Renamed");
        vm.Projection!.Header.DisplayName.Should().Be("Renamed");
    }
}
