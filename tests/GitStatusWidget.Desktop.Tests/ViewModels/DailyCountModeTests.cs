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
        row.PeriodToolTip.Should().Be("Issues closed in the last 24 hours");
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
