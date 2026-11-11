using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Application.Usage;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Desktop.Tests.TestDoubles;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Desktop.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static GitStatusWidget.Desktop.Tests.TestData.Data;

namespace GitStatusWidget.Desktop.Tests.ViewModels;

/// <summary>Issue #17: Claude usage footer in the widget and the full display opened by clicking it.</summary>
public sealed class ClaudeUsageTests
{
    private readonly FakeTimeProvider _time = new(T0);
    private readonly RepositoryStatusHub _hub = new();
    private readonly FakeNavigator _navigator = new();
    private readonly InlineUiDispatcher _ui = new();
    private readonly FakeSynchronizationController _controller = new(new AppSettings { ShowClaudeUsage = true });
    private readonly Source _source = new();

    private static string Percent(int value) => (value / 100d).ToString("P0", CultureInfo.CurrentCulture);

    private ClaudeUsageMonitor Monitor() => new(_source, _controller, _time, NullLogger<ClaudeUsageMonitor>.Instance);

    private WidgetViewModel Widget(ClaudeUsageMonitor? monitor) =>
        new(_hub, new DashboardProjector(_time), _controller, _ui, _navigator, _time, usage: monitor);

    private void Serve(params ClaudeUsageLimit[] limits) =>
        _source.Result = new ClaudeUsageResult.Success(new ClaudeUsageSnapshot(limits, "max", _time.GetUtcNow()));

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static List<string> Texts(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();

    [Fact]
    public void Footer_is_hidden_without_a_monitor_and_while_the_setting_is_off()
    {
        using var plain = Widget(null);
        plain.Usage.IsEnabled.Should().BeFalse();

        using var monitor = Monitor();
        using var vm = Widget(monitor);
        vm.Usage.IsEnabled.Should().BeFalse("nothing was polled yet");
    }

    [Fact]
    public async Task Footer_shows_one_bar_per_reported_limit()
    {
        Serve(
            new ClaudeUsageLimit(ClaudeUsageLimitKind.Session, null, 58.4, T0.AddHours(2).AddMinutes(10)),
            new ClaudeUsageLimit(ClaudeUsageLimitKind.Weekly, null, 80, T0.AddDays(3)),
            new ClaudeUsageLimit(ClaudeUsageLimitKind.WeeklyModel, "Fable", 95, null));
        using var monitor = Monitor();
        using var vm = Widget(monitor);

        await monitor.CheckAsync(CancellationToken.None);

        var usage = vm.Usage;
        usage.IsEnabled.Should().BeTrue();
        usage.HasStatus.Should().BeFalse();
        usage.PlanText.Should().Be("Max plan");
        usage.UpdatedText.Should().Be("Updated just now");
        usage.Limits.Select(l => l.Title).Should().Equal("Session", "Weekly", "Fable");
        usage.Limits.Select(l => l.PercentText).Should().Equal(Percent(58), Percent(80), Percent(95));
        usage.Limits.Select(l => (l.IsWarning, l.IsCritical)).Should().Equal((false, false), (true, false), (false, true));
        usage.Limits[0].ResetText.Should().Be("Resets in 2 hr 10 min");
        usage.Limits[0].ToolTip.Should().Be($"Current session (5 hours): {Percent(58)} used · Resets in 2 hr 10 min");
        var reset = T0.AddDays(3).ToLocalTime();
        usage.Limits[1].ResetText.Should().Be($"Resets {reset.ToString("ddd", CultureInfo.CurrentCulture)} {reset.ToString("t", CultureInfo.CurrentCulture)}");
        usage.Limits[1].ResetText.Should().MatchRegex(@"^Resets \S+ \d{1,2}[:.]\d{2}", "weekday and a real time of day");
        usage.Limits[2].DetailTitle.Should().Be("Weekly limit · Fable");
        usage.Limits[2].HasReset.Should().BeFalse();
    }

    [Fact]
    public async Task Relative_times_follow_the_clock()
    {
        Serve(new ClaudeUsageLimit(ClaudeUsageLimitKind.Session, null, 10, T0.AddMinutes(90)));
        using var monitor = Monitor();
        using var vm = Widget(monitor);
        await monitor.CheckAsync(CancellationToken.None);
        var row = vm.Usage.Limits.Single();

        _time.Advance(TimeSpan.FromMinutes(45));
        vm.Usage.UpdateFreshness();

        vm.Usage.Limits.Single().Should().BeSameAs(row, "rows are updated in place");
        row.ResetText.Should().Be("Resets in 45 min");
        vm.Usage.UpdatedText.Should().Be("Updated 45 min ago");
    }

    [Fact]
    public async Task Problems_are_shown_as_a_hint_and_keep_the_last_known_bars()
    {
        Serve(new ClaudeUsageLimit(ClaudeUsageLimitKind.Weekly, null, 50, T0.AddDays(3)));
        using var monitor = Monitor();
        using var vm = Widget(monitor);
        await monitor.CheckAsync(CancellationToken.None);

        _source.Result = new ClaudeUsageResult.SignedOut("Claude Code sign-in expired – run “claude” to sign in");
        _time.Advance(ClaudeUsageMonitor.PollInterval);
        await monitor.CheckAsync(CancellationToken.None);

        vm.Usage.IsProblem.Should().BeTrue();
        vm.Usage.StatusText.Should().Be("Claude Code sign-in expired – run “claude” to sign in");
        vm.Usage.Limits.Should().ContainSingle();
    }

    [Fact]
    public async Task Not_signed_in_shows_only_the_hint()
    {
        _source.Result = new ClaudeUsageResult.SignedOut("Not signed in to Claude Code – run “claude” to sign in");
        using var monitor = Monitor();
        using var vm = Widget(monitor);

        await monitor.CheckAsync(CancellationToken.None);

        vm.Usage.IsEnabled.Should().BeTrue();
        vm.Usage.HasLimits.Should().BeFalse();
        vm.Usage.StatusText.Should().StartWith("Not signed in to Claude Code");
    }

    [Fact]
    public void Clicking_the_footer_opens_the_full_display()
    {
        using var vm = Widget(null);

        vm.Usage.OpenDetailsCommand.Execute(null);

        _navigator.Calls.Should().Equal("ShowClaudeUsage()");
    }

    [AvaloniaFact]
    public async Task Widget_window_shows_the_footer_only_while_usage_is_on()
    {
        Serve(
            new ClaudeUsageLimit(ClaudeUsageLimitKind.Weekly, null, 50, T0.AddDays(3)),
            new ClaudeUsageLimit(ClaudeUsageLimitKind.WeeklyModel, "Fable", 69, T0.AddDays(3)));
        using var monitor = Monitor();
        using var vm = Widget(monitor);
        using var errors = new BindingErrorSink();
        var window = new WidgetWindow(new MotionPreference(new FakeReducedMotionDetector())) { DataContext = vm };
        window.Show();
        Pump(window);
        var footer = window.FindControl<Button>("UsageFooter")!;
        footer.IsVisible.Should().BeFalse();

        await monitor.CheckAsync(CancellationToken.None);
        Pump(window);

        footer.IsVisible.Should().BeTrue();
        Texts(footer).Should().Contain(["Claude usage", "Weekly", "Fable", Percent(50), Percent(69)]);
        footer.GetVisualDescendants().OfType<ProgressBar>().Select(p => p.Value).Should().Equal(50, 69);
        footer.Command!.Execute(null);
        _navigator.Calls.Should().Equal("ShowClaudeUsage()");
        errors.Messages.Should().BeEmpty();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Usage_window_shows_every_limit_with_its_reset_time()
    {
        Serve(
            new ClaudeUsageLimit(ClaudeUsageLimitKind.Session, null, 58, T0.AddHours(2).AddMinutes(10)),
            new ClaudeUsageLimit(ClaudeUsageLimitKind.Weekly, null, 92, T0.AddDays(3)));
        using var monitor = Monitor();
        using var vm = Widget(monitor);
        await monitor.CheckAsync(CancellationToken.None);
        using var errors = new BindingErrorSink();
        var window = new UsageWindow { DataContext = vm.Usage };

        window.Show();
        Pump(window);

        Texts(window).Should().Contain(["Claude usage", "Max plan", "Current session (5 hours)", $"{Percent(58)} used", "Resets in 2 hr 10 min",
            "Weekly limit · all models", $"{Percent(92)} used", "Updated just now"]);
        var bars = window.GetVisualDescendants().OfType<ProgressBar>().ToList();
        bars.Select(p => p.Value).Should().Equal(58, 92);
        bars[1].Classes.Should().Contain("critical");
        errors.Messages.Should().BeEmpty();
        window.Close();
    }

    [AvaloniaFact]
    public void Usage_window_explains_a_missing_sign_in()
    {
        using var vm = Widget(null);
        using var errors = new BindingErrorSink();
        var window = new UsageWindow { DataContext = vm.Usage };

        window.Show();
        Pump(window);

        Texts(window).Should().Contain("The Claude usage is switched off in Settings.");
        window.FindControl<ItemsControl>("LimitList")!.IsVisible.Should().BeFalse();
        errors.Messages.Should().BeEmpty();
        window.Close();
    }

    [Fact]
    public async Task Setting_is_saved_from_the_settings_window()
    {
        var store = new FakeSettingsStore();
        var controller = new FakeSynchronizationController();
        var settings = new SettingsViewModel(new SettingsService(store, controller, new FakeAutostartService()), new FakeCredentialStore(),
            new GitStatusWidget.Application.Platform.CredentialStoreStatus("Test store", true, null), new FakeProviderResolver(), new FakeRepositoryCache(), _hub);
        settings.ShowClaudeUsage.Should().BeFalse("the footer is opt-in");

        settings.ShowClaudeUsage = true;
        await settings.SaveCommand.ExecuteAsync(null);

        store.Saved.Last().ShowClaudeUsage.Should().BeTrue();
        controller.Settings.ShowClaudeUsage.Should().BeTrue();
    }

    private sealed class Source : IClaudeUsageSource
    {
        public ClaudeUsageResult Result { get; set; } = new ClaudeUsageResult.Unavailable("not configured");

        public Task<ClaudeUsageResult> GetUsageAsync(CancellationToken cancellationToken) => Task.FromResult(Result);
    }
}
