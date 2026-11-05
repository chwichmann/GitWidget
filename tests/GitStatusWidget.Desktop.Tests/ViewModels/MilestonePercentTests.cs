using System.Globalization;
using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Converters;
using GitStatusWidget.Desktop.Tests.TestDoubles;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Domain.Milestones;
using Microsoft.Extensions.Time.Testing;
using static GitStatusWidget.Desktop.Tests.TestData.Data;

namespace GitStatusWidget.Desktop.Tests.ViewModels;

/// <summary>Issue #11: completion percentage in widget and dashboard.</summary>
public sealed class MilestonePercentTests
{
    [Fact]
    public void Widget_and_dashboard_show_the_same_percentage()
    {
        var time = new FakeTimeProvider(T0);
        var hub = new RepositoryStatusHub();
        var config = Config();
        var milestone = new MilestoneSnapshot("M1", "0.10.1 Network Services", null, null, new DateOnly(2026, 12, 1), false, 3, 602);
        hub.Publish(State(config, Snapshot(config, milestones: [milestone])));
        var ui = new InlineUiDispatcher();

        using var widget = new WidgetViewModel(hub, new DashboardProjector(time), new FakeSynchronizationController(), ui, new FakeNavigator(), time);
        using var dashboard = new DashboardViewModel(hub, new DashboardProjector(time), new FakeSynchronizationController(), ui, new FakeNavigator(), new GitStatusWidget.Desktop.Services.ActivityFeed(hub, ui), time);

        var expected = (0.99).ToString("P0", CultureInfo.CurrentCulture);
        widget.Rows.Single().MilestoneText.Should().Be($"602/605 · {expected}");
        var suffix = CompletionSuffixConverter.Instance.Convert(dashboard.Projection!.Milestones.Single().CompletionPercent, typeof(string), null, CultureInfo.CurrentCulture);
        suffix.Should().Be($" · {expected}");
    }

    [Fact]
    public void Milestone_without_issues_shows_a_dash_instead_of_zero_percent()
    {
        CompletionSuffixConverter.Instance.Convert(null, typeof(string), null, CultureInfo.CurrentCulture).Should().Be(" · –");
    }

    [Fact]
    public void Percent_formatting_follows_the_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-CH");
            CompletionSuffixConverter.Instance.Convert(99, typeof(string), null, CultureInfo.CurrentCulture).Should().Be($" · {(0.99).ToString("P0", CultureInfo.GetCultureInfo("de-CH"))}");
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CompletionSuffixConverter.Instance.Convert(33, typeof(string), null, CultureInfo.CurrentCulture).Should().Be(" · 33 %");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
