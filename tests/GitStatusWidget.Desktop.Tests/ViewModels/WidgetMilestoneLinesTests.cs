using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Tests.TestDoubles;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Desktop.Views;
using GitStatusWidget.Domain.Milestones;
using Microsoft.Extensions.Time.Testing;
using static GitStatusWidget.Desktop.Tests.TestData.Data;

namespace GitStatusWidget.Desktop.Tests.ViewModels;

/// <summary>Issue #12: one line per widget milestone, updated in place.</summary>
public sealed class WidgetMilestoneLinesTests
{
    private readonly FakeTimeProvider _time = new(T0);
    private readonly RepositoryStatusHub _hub = new();
    private readonly InlineUiDispatcher _ui = new();

    private static MilestoneSnapshot M(string id, int open, int closed, DateOnly? due) => new(id, $"Milestone {id}", null, null, due, false, open, closed);

    [Fact]
    public void Row_shows_one_line_per_milestone_and_updates_lines_in_place()
    {
        var config = Config() with { AutomaticMilestoneCount = 3 };
        MilestoneSnapshot[] milestones = [M("a", 1, 1, new DateOnly(2026, 11, 10)), M("b", 2, 2, new DateOnly(2026, 11, 20)), M("c", 3, 3, null)];
        _hub.Publish(State(config, Snapshot(config, milestones: milestones)));
        using var widget = new WidgetViewModel(_hub, new DashboardProjector(_time), new FakeSynchronizationController(), _ui, new FakeNavigator(), _time);
        var row = widget.Rows.Single();
        row.Milestones.Select(m => m.Title).Should().Equal("Milestone a", "Milestone b", "Milestone c");
        var lineB = row.Milestones[1];

        // b is now due first; a has finished; c unchanged.
        MilestoneSnapshot[] updated = [M("a", 0, 2, new DateOnly(2026, 11, 25)), M("b", 1, 3, new DateOnly(2026, 11, 8)), M("c", 3, 3, null)];
        _hub.Publish(State(config, Snapshot(config, milestones: updated)));

        row.Milestones.Select(m => m.Title).Should().Equal("Milestone b", "Milestone a", "Milestone c");
        row.Milestones[0].Should().BeSameAs(lineB, "lines are moved, not recreated");
        lineB.Percent.Should().Be(75);
    }

    [Fact]
    public void Lines_disappear_when_fewer_milestones_are_selected()
    {
        var config = Config() with { AutomaticMilestoneCount = 2 };
        _hub.Publish(State(config, Snapshot(config, milestones: [M("a", 1, 1, null), M("b", 2, 2, null)])));
        using var widget = new WidgetViewModel(_hub, new DashboardProjector(_time), new FakeSynchronizationController(), _ui, new FakeNavigator(), _time);

        _hub.Publish(State(config with { AutomaticMilestoneCount = 1 }, Snapshot(config, milestones: [M("a", 1, 1, null), M("b", 2, 2, null)])));

        widget.Rows.Single().Milestones.Should().ContainSingle();
    }

    [AvaloniaFact]
    public void Widget_window_renders_several_milestone_lines()
    {
        var config = Config() with { MilestoneMode = MilestoneSelectionMode.Manual, SelectedMilestoneIds = ["b", "a"] };
        _hub.Publish(State(config, Snapshot(config, milestones: [M("a", 1, 1, null), M("b", 2, 2, null)])));
        using var widget = new WidgetViewModel(_hub, new DashboardProjector(_time), new FakeSynchronizationController(), _ui, new FakeNavigator(), _time);
        var window = new WidgetWindow { DataContext = widget };

        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        texts.IndexOf("Milestone b").Should().BeLessThan(texts.IndexOf("Milestone a"));
        window.Close();
    }
}
