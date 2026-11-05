using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Application.Updates;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Desktop.Tests.TestDoubles;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Desktop.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static GitStatusWidget.Desktop.Tests.TestData.Data;

namespace GitStatusWidget.Desktop.Tests.ViewModels;

/// <summary>Issue #7: update symbol in the widget header, tray entry, diagnostics text.</summary>
public sealed class UpdateNoticeTests
{
    private static readonly Uri ReleasePage = new("https://github.com/chwichmann/GitWidget/releases/tag/v0.1.2");
    private readonly FakeTimeProvider _time = new(T0);
    private readonly RepositoryStatusHub _hub = new();
    private readonly FakeNavigator _navigator = new();
    private readonly InlineUiDispatcher _ui = new();

    private static UpdateChecker Checker(params string[] tags)
    {
        var releases = tags.Select(t => new ReleaseInfo(SemanticVersion.TryParse(t, out var v) ? v : throw new ArgumentException(t), t,
            new Uri($"https://github.com/chwichmann/GitWidget/releases/tag/{t}"), false, T0)).ToList();
        return new UpdateChecker(new Feed(new ReleaseFeedResult.Success(releases)), new FakeSynchronizationController(new AppSettings()),
            new FakeTimeProvider(T0), NullLogger<UpdateChecker>.Instance, "0.1.1", TimeSpan.Zero);
    }

    [Fact]
    public async Task Widget_shows_the_update_symbol_only_when_a_newer_release_exists_and_opens_its_page()
    {
        using var checker = Checker("v0.1.2");
        using var vm = new WidgetViewModel(_hub, new DashboardProjector(_time), new FakeSynchronizationController(), _ui, _navigator, _time, updates: checker);
        vm.UpdateAvailable.Should().BeFalse();

        await checker.CheckAsync(CancellationToken.None);

        vm.UpdateAvailable.Should().BeTrue();
        vm.UpdateToolTip.Should().Be("GitWidget 0.1.2 is available (running 0.1.1) – click to open the release page");
        vm.OpenUpdateCommand.Execute(null);
        _navigator.Calls.Should().ContainSingle().Which.Should().Contain(ReleasePage.ToString());
    }

    [Fact]
    public async Task No_symbol_when_up_to_date()
    {
        using var checker = Checker("v0.1.1");
        using var vm = new WidgetViewModel(_hub, new DashboardProjector(_time), new FakeSynchronizationController(), _ui, _navigator, _time, updates: checker);

        await checker.CheckAsync(CancellationToken.None);

        vm.UpdateAvailable.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Tray_offers_the_update_as_menu_entry()
    {
        using var checker = Checker("v0.1.2");
        var settings = new SettingsService(new FakeSettingsStore(), new FakeSynchronizationController(), new FakeAutostartService());
        using var tray = new TrayViewModel(_hub, new FakeSynchronizationController(), settings, _navigator, _ui, checker);

        await checker.CheckAsync(CancellationToken.None);

        tray.UpdateAvailable.Should().BeTrue();
        tray.UpdateMenuText.Should().Be("Update available: GitWidget 0.1.2…");
    }

    [AvaloniaFact]
    public async Task Widget_window_header_contains_the_visible_update_button()
    {
        using var checker = Checker("v0.1.2");
        using var vm = new WidgetViewModel(_hub, new DashboardProjector(_time), new FakeSynchronizationController(), _ui, _navigator, _time, updates: checker);
        await checker.CheckAsync(CancellationToken.None);
        var window = new WidgetWindow { DataContext = vm };

        window.Show();

        window.FindControl<Button>("UpdateButton")!.IsVisible.Should().BeTrue();
        window.Close();
    }

    [Fact]
    public void Diagnostics_describes_update_states()
    {
        SemanticVersion.TryParse("0.1.2", out var v).Should().BeTrue();
        var available = new UpdateState(UpdateStatus.Available, "0.1.1", new ReleaseInfo(v, "v0.1.2", ReleasePage, false, T0), null, null);

        DiagnosticsViewModel.DescribeUpdate(available).Should().Be($"GitWidget 0.1.2 is available – {ReleasePage}");
        DiagnosticsViewModel.DescribeUpdate(available with { Status = UpdateStatus.Unavailable, Latest = null, Message = "private repository" })
            .Should().Be("Update check failed: private repository");
    }

    private sealed class Feed(ReleaseFeedResult result) : IReleaseFeed
    {
        public string Source => "chwichmann/GitWidget";

        public Task<ReleaseFeedResult> GetReleasesAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
