using GitStatusWidget.Application.Platform;
using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Desktop.Tests.TestDoubles;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Domain.Issues;
using Microsoft.Extensions.Time.Testing;
using static GitStatusWidget.Desktop.Tests.TestData.Data;

namespace GitStatusWidget.Desktop.Tests.ViewModels;

/// <summary>Issue #9: "closed today" as calendar day or last 24 hours.</summary>
public sealed class DailyCountModeTests
{
    private readonly FakeTimeProvider _time = new(T0);
    private readonly RepositoryStatusHub _hub = new();
    private readonly InlineUiDispatcher _ui = new();

    [Fact]
    public void Widget_row_labels_the_rolling_window_and_counts_it()
    {
        var config = Config();
        _hub.Publish(State(config, Snapshot(config, issues: [Issue(1, WorkItemState.Closed, updatedAt: T0.AddHours(-3))])));
        var controller = new FakeSynchronizationController(new AppSettings { DailyCountMode = DailyCountMode.Last24Hours });

        using var vm = new WidgetViewModel(_hub, new DashboardProjector(_time), controller, _ui, new FakeNavigator(), _time);

        var row = vm.Rows.Single();
        row.ClosedToday.Should().Be(1);
        row.PeriodSuffix.Should().Be(" in 24 h");
        row.PeriodText.Should().Be("1 closed in 24 h");
        row.PeriodToolTip.Should().Be("In the last 24 hours: 0 issues created, 1 issue closed");
    }

    [Fact]
    public void Widget_row_shows_new_issues_next_to_closed_ones()
    {
        var config = Config();
        _hub.Publish(State(config, Snapshot(config, issues:
        [
            Issue(1, WorkItemState.Open, createdAt: T0.AddHours(-1)),
            Issue(2, WorkItemState.Open, createdAt: T0.AddHours(-2)),
            Issue(3, WorkItemState.Closed, updatedAt: T0.AddHours(-3)),
        ])));
        var controller = new FakeSynchronizationController(new AppSettings { DailyCountMode = DailyCountMode.Last24Hours });

        using var vm = new WidgetViewModel(_hub, new DashboardProjector(_time), controller, _ui, new FakeNavigator(), _time);

        var row = vm.Rows.Single();
        row.OpenedToday.Should().Be(2);
        row.HasPeriodActivity.Should().BeTrue();
        row.PeriodText.Should().Be("2 new · 1 closed in 24 h");
        row.PeriodToolTip.Should().Be("In the last 24 hours: 2 issues created, 1 issue closed");
    }

    [Fact]
    public void Widget_row_leaves_out_zero_counters_and_hides_an_empty_period_line()
    {
        var config = Config();
        _hub.Publish(State(config, Snapshot(config, issues: [Issue(1, WorkItemState.Open, createdAt: T0.AddHours(-1))])));
        var controller = new FakeSynchronizationController(new AppSettings { DailyCountMode = DailyCountMode.Last24Hours });
        using var vm = new WidgetViewModel(_hub, new DashboardProjector(_time), controller, _ui, new FakeNavigator(), _time);
        vm.Rows.Single().PeriodText.Should().Be("1 new in 24 h");

        _time.Advance(TimeSpan.FromHours(24)); // the new issue ages out

        var row = vm.Rows.Single();
        row.HasPeriodActivity.Should().BeFalse();
        row.PeriodText.Should().BeEmpty();
    }

    [Fact]
    public void Rolling_counter_ages_out_without_new_events()
    {
        var config = Config();
        _hub.Publish(State(config, Snapshot(config, issues: [Issue(1, WorkItemState.Closed, updatedAt: T0.AddHours(-23).AddMinutes(-59))])));
        var controller = new FakeSynchronizationController(new AppSettings { DailyCountMode = DailyCountMode.Last24Hours });
        using var vm = new WidgetViewModel(_hub, new DashboardProjector(_time), controller, _ui, new FakeNavigator(), _time);
        vm.Rows.Single().ClosedToday.Should().Be(1);

        _time.Advance(TimeSpan.FromSeconds(90)); // the widget's 30 s timer re-projects

        vm.Rows.Single().ClosedToday.Should().Be(0);
    }

    [Fact]
    public void Calendar_day_is_the_default_label()
    {
        var config = Config();
        _hub.Publish(State(config, Snapshot(config)));

        using var vm = new WidgetViewModel(_hub, new DashboardProjector(_time), new FakeSynchronizationController(), _ui, new FakeNavigator(), _time);

        vm.Rows.Single().PeriodSuffix.Should().Be(" today");
    }

    [Fact]
    public async Task Settings_save_the_counting_period()
    {
        var controller = new FakeSynchronizationController(new AppSettings());
        var store = new FakeSettingsStore();
        var vm = new SettingsViewModel(new SettingsService(store, controller, new FakeAutostartService()), new FakeCredentialStore(),
            new CredentialStoreStatus("Test", true, null), new FakeProviderResolver(), new FakeRepositoryCache(), _hub);
        vm.DailyCount!.Mode.Should().Be(DailyCountMode.CalendarDay);

        vm.DailyCount = vm.DailyCountModes.Single(m => m.Mode == DailyCountMode.Last24Hours);
        await vm.SaveCommand.ExecuteAsync(null);

        store.Saved.Single().DailyCountMode.Should().Be(DailyCountMode.Last24Hours);
    }
}
