using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Desktop.Tests.TestData;
using GitStatusWidget.Desktop.Tests.TestDoubles;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Desktop.Views;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.Events;
using GitStatusWidget.Domain.Health;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Repositories;
using Microsoft.Extensions.Time.Testing;

namespace GitStatusWidget.Desktop.Tests.Views;

/// <summary>
/// Headless smoke tests: every window is constructed with a populated view model and shown, which loads the XAML,
/// applies templates and evaluates all bindings. Binding failures are only logged by Avalonia, so they are captured
/// and asserted to be absent.
/// </summary>
public sealed class WindowSmokeTests
{
    private readonly FakeTimeProvider _time = new(Data.T0);
    private readonly RepositoryStatusHub _hub = new();
    private readonly FakeSynchronizationController _controller = new();
    private readonly FakeNavigator _navigator = new();
    private readonly InlineUiDispatcher _ui = new();

    private RepositoryConfiguration PublishGitHub(RepositoryHealthState health = RepositoryHealthState.Healthy)
    {
        var configuration = Data.Config(name: "widget", sortOrder: 0, displayName: "Widget");
        _hub.Publish(Data.State(configuration, Data.Snapshot(configuration,
            issues:
            [
                Data.Issue(1, labels: ["bug"], assignees: ["alice"]),
                Data.Issue(2, WorkItemState.Closed, updatedAt: Data.T0.AddHours(-1)),
            ],
            milestones: [Data.Milestone("M1", 1, 1, due: new DateOnly(2026, 10, 1)), Data.Milestone("M2", 0, 3, isClosed: true)],
            labels: [new Domain.Labels.LabelSnapshot("bug", "#d73a4a", null, 1, 0)],
            changeRequests: [Data.ChangeRequest(5)],
            builds: [Data.Build("10", BuildState.Failed, Data.T0), Data.Build("9", BuildState.Running, Data.T0.AddMinutes(-1), refName: "dev")]),
            health) with { LastError = health == RepositoryHealthState.Healthy ? null : "401 Unauthorized" });
        return configuration;
    }

    private RepositoryConfiguration PublishGitLabWithoutData()
    {
        var configuration = Data.Config(provider: RepositoryProviderKind.GitLab, owner: "group/sub", name: "project", sortOrder: 1);
        _hub.Publish(Data.State(configuration, health: RepositoryHealthState.Refreshing) with { IsRefreshing = true });
        return configuration;
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Simulates the platform's close request (title-bar button, Alt+F4), which arrives through
    /// <c>IWindowImpl.Closing</c> as a non-programmatic close. The member is hidden from the reference assembly,
    /// so it is invoked via reflection.
    /// </summary>
    /// <returns><see langword="true"/> when the window cancelled the close.</returns>
    private static bool RequestCloseByUser(Window window)
    {
        var impl = window.PlatformImpl ?? throw new InvalidOperationException("Window has no platform implementation.");
        var property = typeof(Avalonia.Platform.IWindowImpl).GetProperty("Closing")
            ?? throw new InvalidOperationException("IWindowImpl.Closing not found; Avalonia changed its platform API.");
        var closing = (Func<WindowCloseReason, bool>?)property.GetValue(impl)
            ?? throw new InvalidOperationException("Window did not subscribe to IWindowImpl.Closing.");
        return closing(WindowCloseReason.WindowClosing);
    }

    private static IEnumerable<string> Texts(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : t.Text ?? string.Empty);

    private WidgetViewModel WidgetViewModel() => new(_hub, new DashboardProjector(_time), _controller, _ui, _navigator, _time);

    [AvaloniaFact]
    public void Widget_window_shows_rows_without_binding_errors()
    {
        PublishGitHub();
        PublishGitLabWithoutData();
        using var vm = WidgetViewModel();
        using var errors = new BindingErrorSink();
        var window = new WidgetWindow(new MotionPreference(new FakeReducedMotionDetector())) { DataContext = vm };

        window.Show();
        Pump(window);

        window.IsVisible.Should().BeTrue();
        var texts = Texts(window).ToList();
        texts.Should().Contain(["Widget", "group/sub/project", "GH", "GL", "1 open", "1 closed", "Milestone M1", $"1/2 · {(0.5).ToString("P0", System.Globalization.CultureInfo.CurrentCulture)}"]);
        texts.Should().Contain(vm.FooterText);
        errors.Messages.Should().BeEmpty();
        window.Close();
    }

    [AvaloniaFact]
    public void Empty_widget_window_shows_the_empty_state()
    {
        using var vm = WidgetViewModel();
        using var errors = new BindingErrorSink();
        var window = new WidgetWindow(new MotionPreference(new FakeReducedMotionDetector())) { DataContext = vm };

        window.Show();
        Pump(window);

        Texts(window).Should().Contain(["No repositories yet.", "No repositories configured"]);
        errors.Messages.Should().BeEmpty();
        window.Close();
    }

    [AvaloniaFact]
    public void Closing_the_widget_by_the_user_hides_it_instead_of_closing()
    {
        using var vm = WidgetViewModel();
        var window = new WidgetWindow(new MotionPreference(new FakeReducedMotionDetector())) { DataContext = vm };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        Pump(window);

        var cancelled = RequestCloseByUser(window);
        Pump(window);

        cancelled.Should().BeTrue();
        closed.Should().BeFalse();
        window.IsVisible.Should().BeFalse();

        window.Show();
        window.IsVisible.Should().BeTrue("a hidden widget can be shown again");
        window.Close();
        closed.Should().BeTrue("programmatic close (application exit) really closes");
    }

    [AvaloniaFact]
    public void Closing_the_dashboard_by_the_user_hides_it()
    {
        using var feed = new ActivityFeed(_hub, _ui);
        using var vm = new DashboardViewModel(_hub, new DashboardProjector(_time), _controller, _ui, _navigator, feed, _time);
        var window = new DashboardWindow { DataContext = vm };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();

        RequestCloseByUser(window).Should().BeTrue();

        closed.Should().BeFalse();
        window.IsVisible.Should().BeFalse();
        window.Close();
    }

    [AvaloniaFact]
    public void Row_animation_marks_the_row_unless_motion_is_reduced()
    {
        var configuration = PublishGitHub();
        using var vm = WidgetViewModel();
        var window = new WidgetWindow(new MotionPreference(new FakeReducedMotionDetector())) { DataContext = vm };
        window.Show();
        Pump(window);

        _hub.PublishChanges(new RepositoryChangesDetected(configuration.Id,
            [Data.Event(ChangeEventType.IssueClosed), Data.Event(ChangeEventType.BuildFailed, EventPriority.High)], IsCatchUp: false));

        var borders = window.GetVisualDescendants().OfType<Border>().ToList();
        borders.Single(b => b.Name == "ClosedPulse").Classes.Should().Contain("anim-closed");
        borders.Single(b => b.Name == "BuildPulse").Classes.Should().Contain("anim-failed");
        window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ClosedCheck").IsVisible.Should().BeTrue();
        window.Close();
    }

    [AvaloniaFact]
    public void Reduced_motion_suppresses_row_animations()
    {
        var configuration = PublishGitHub();
        using var vm = WidgetViewModel();
        var window = new WidgetWindow(new MotionPreference(new FakeReducedMotionDetector { Enabled = true })) { DataContext = vm };
        window.Show();
        Pump(window);

        _hub.PublishChanges(new RepositoryChangesDetected(configuration.Id, [Data.Event(ChangeEventType.IssueClosed)], IsCatchUp: false));

        window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ClosedPulse").Classes.Should().NotContain("anim-closed");
        window.Close();
    }

    private List<string> ShowPopulatedDashboard()
    {
        PublishGitHub(RepositoryHealthState.AuthenticationFailed);
        PublishGitLabWithoutData();
        using var feed = new ActivityFeed(_hub, _ui);
        using var vm = new DashboardViewModel(_hub, new DashboardProjector(_time), _controller, _ui, _navigator, feed, _time);
        _hub.PublishChanges(new RepositoryChangesDetected(vm.SelectedRepository!.Id, [Data.Event(ChangeEventType.IssueClosed, number: 2)], IsCatchUp: false));
        using var errors = new BindingErrorSink();
        var window = new DashboardWindow { DataContext = vm };

        window.Show();
        Pump(window);

        Texts(window).Should().Contain(["Widget", "Open pull requests", "Recent workflow runs", "Token rejected – update the token in Settings", "Issue #2 closed · Entity 2"]);

        // Switch to the GitLab repository without data (null lists, FallbackValues).
        vm.Select(vm.Repositories[1].Id);
        Pump(window);

        Texts(window).Should().Contain("group/sub/project");
        window.Close();
        return errors.Messages;
    }

    [AvaloniaFact]
    public void Dashboard_window_shows_a_repository_without_binding_errors() =>
        ShowPopulatedDashboard().Should().BeEmpty();

    [AvaloniaFact]
    public void Dashboard_window_logs_no_binding_warnings_at_all() =>
        ShowPopulatedDashboard().Should().BeEmpty();

    [AvaloniaFact]
    public void Empty_dashboard_window_renders()
    {
        using var feed = new ActivityFeed(_hub, _ui);
        using var vm = new DashboardViewModel(_hub, new DashboardProjector(_time), _controller, _ui, _navigator, feed, _time);
        using var errors = new BindingErrorSink();
        var window = new DashboardWindow { DataContext = vm };

        window.Show();
        Pump(window);

        window.IsVisible.Should().BeTrue();
        errors.Messages.Should().BeEmpty();
        window.Close();
    }

    [AvaloniaFact]
    public void Settings_window_without_repositories_renders()
    {
        var vm = new SettingsViewModel(new SettingsService(new FakeSettingsStore(), new FakeSynchronizationController(), new FakeAutostartService()),
            new FakeCredentialStore(), new GitStatusWidget.Application.Platform.CredentialStoreStatus("Test store", true, null), new FakeProviderResolver(), new FakeRepositoryCache(), _hub);
        using var errors = new BindingErrorSink();
        var window = new SettingsWindow { DataContext = vm };

        window.Show();
        Pump(window);

        window.IsVisible.Should().BeTrue();
        Texts(window).Should().Contain("Repositories");
        errors.Messages.Should().BeEmpty();
        window.Close();
    }

    [AvaloniaFact]
    public void Settings_window_shows_repositories_without_binding_errors()
    {
        var configuration = PublishGitHub();
        var controller = new FakeSynchronizationController(new AppSettings { Repositories = [configuration, Data.Config(provider: RepositoryProviderKind.GitLab, owner: "group", name: "project", sortOrder: 1)] });
        var vm = new SettingsViewModel(new SettingsService(new FakeSettingsStore(), controller, new FakeAutostartService()),
            new FakeCredentialStore(), new GitStatusWidget.Application.Platform.CredentialStoreStatus("Test store", true, null), new FakeProviderResolver(), new FakeRepositoryCache(), _hub);
        using var errors = new BindingErrorSink();
        var window = new SettingsWindow { DataContext = vm };

        window.Show();
        Pump(window);

        Texts(window).Should().Contain("Widget");
        errors.Messages.Should().BeEmpty();

        vm.Select(null, addNew: true);
        Pump(window);
        vm.Selected!.Provider = RepositoryProviderKind.GitLab;
        Pump(window);

        errors.Messages.Should().BeEmpty();
        window.Close();
    }

    [AvaloniaFact]
    public void Toast_window_shows_content_and_reports_clicks()
    {
        using var errors = new BindingErrorSink();
        var toast = new ToastViewModel("octo/widget", "Issue #7 closed", "Fix login", ToastKind.Success, new Uri("https://github.com/octo/widget/issues/7"));
        var window = new ToastWindow { DataContext = toast };
        var clicks = 0;
        window.Clicked += (_, _) => clicks++;

        window.Show();
        Pump(window);
        window.PlayEnterAnimation();
        window.MouseDown(new Point(60, 20), MouseButton.Left);
        window.MouseUp(new Point(60, 20), MouseButton.Left);

        Texts(window).Should().Contain(["octo/widget", "Issue #7 closed", "Fix login", "✓"]);
        clicks.Should().Be(1);
        errors.Messages.Should().BeEmpty();
        window.Close();
    }

    [AvaloniaFact]
    public void Every_toast_kind_renders()
    {
        using var errors = new BindingErrorSink();
        foreach (var kind in Enum.GetValues<ToastKind>())
        {
            var window = new ToastWindow { DataContext = new ToastViewModel("r", "h", "t", kind, null) };
            window.Show();
            Pump(window);
            window.Close();
        }

        errors.Messages.Should().BeEmpty();
    }
}
