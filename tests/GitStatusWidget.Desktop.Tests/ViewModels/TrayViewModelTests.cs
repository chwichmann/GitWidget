using Avalonia.Headless.XUnit;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Desktop.Tests.TestData;
using GitStatusWidget.Desktop.Tests.TestDoubles;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.Health;

namespace GitStatusWidget.Desktop.Tests.ViewModels;

/// <summary>
/// TrayViewModel loads its icons through AssetLoader in a static initializer, so every test that touches the type
/// runs on the Avalonia headless dispatcher (a failed type initializer would poison the type for the whole run).
/// </summary>
public sealed class TrayViewModelTests
{
    private static RepositoryRuntimeState WithBuilds(string name, params BuildSnapshot[] builds)
    {
        var configuration = Data.Config(name: name);
        return Data.State(configuration, Data.Snapshot(configuration, builds: builds));
    }

    [AvaloniaFact]
    public void No_repositories_is_offline_with_explanation()
    {
        var (state, text) = TrayViewModel.Aggregate([]);

        state.Should().Be(TrayState.Offline);
        text.Should().Be("GitWidget – no repository configured");
    }

    [AvaloniaFact]
    public void Failed_latest_default_branch_build_is_failed()
    {
        var states = new[]
        {
            WithBuilds("ok", Data.Build("1", BuildState.Succeeded, Data.T0)),
            WithBuilds("broken", Data.Build("2", BuildState.Succeeded, Data.T0.AddHours(-1)), Data.Build("3", BuildState.Failed, Data.T0)),
        };

        var (state, text) = TrayViewModel.Aggregate(states);

        state.Should().Be(TrayState.Failed);
        text.Should().Be("GitWidget – build failed: octo/broken");
    }

    [AvaloniaFact]
    public void Failed_build_on_another_branch_is_not_failed()
    {
        var states = new[]
        {
            WithBuilds("widget",
                Data.Build("1", BuildState.Succeeded, Data.T0.AddHours(-1)),
                Data.Build("2", BuildState.Failed, Data.T0, refName: "feature/x")),
        };

        TrayViewModel.Aggregate(states).State.Should().Be(TrayState.Normal);
    }

    [AvaloniaFact]
    public void Older_failed_default_branch_build_followed_by_success_is_not_failed()
    {
        var states = new[]
        {
            WithBuilds("widget", Data.Build("1", BuildState.Failed, Data.T0.AddHours(-1)), Data.Build("2", BuildState.Succeeded, Data.T0)),
        };

        TrayViewModel.Aggregate(states).State.Should().Be(TrayState.Normal);
    }

    [AvaloniaFact]
    public void Without_default_branch_any_latest_failed_build_counts()
    {
        var configuration = Data.Config();
        var snapshot = Data.Snapshot(configuration, builds: [Data.Build("1", BuildState.Failed, Data.T0, refName: "dev")], defaultBranch: null);

        TrayViewModel.Aggregate([Data.State(configuration, snapshot)]).State.Should().Be(TrayState.Failed);
    }

    [AvaloniaFact]
    public void Failed_build_wins_over_offline_and_warnings()
    {
        var broken = WithBuilds("broken", Data.Build("1", BuildState.Failed, Data.T0)) with { Health = RepositoryHealthState.Offline };

        TrayViewModel.Aggregate([broken]).State.Should().Be(TrayState.Failed, "the last known build failed");
    }

    [AvaloniaFact]
    public void All_offline_is_offline()
    {
        var states = new[]
        {
            Data.State(Data.Config(name: "a"), health: RepositoryHealthState.Offline),
            Data.State(Data.Config(name: "b"), health: RepositoryHealthState.Offline),
        };

        var (state, text) = TrayViewModel.Aggregate(states);

        state.Should().Be(TrayState.Offline);
        text.Should().Be("GitWidget – offline, showing last known data");
    }

    [AvaloniaFact]
    public void Mixed_problems_are_a_warning_with_descriptive_text()
    {
        var states = new[]
        {
            Data.State(Data.Config(name: "healthy"), health: RepositoryHealthState.Healthy),
            Data.State(Data.Config(name: "limited"), health: RepositoryHealthState.RateLimited),
            Data.State(Data.Config(name: "denied"), health: RepositoryHealthState.AuthenticationFailed),
            Data.State(Data.Config(name: "away"), health: RepositoryHealthState.Offline),
            Data.State(Data.Config(name: "old", displayName: "Old repo"), health: RepositoryHealthState.Stale),
        };

        var (state, text) = TrayViewModel.Aggregate(states);

        state.Should().Be(TrayState.Warning);
        text.Should().Be("GitWidget – octo/limited: rate limited; octo/denied: token rejected; octo/away: offline; Old repo: stale");
        text.Should().NotContain("healthy");
    }

    [AvaloniaFact]
    public void Every_problem_state_has_a_textual_description()
    {
        foreach (var health in Enum.GetValues<RepositoryHealthState>().Where(h => h is not (RepositoryHealthState.Healthy or RepositoryHealthState.Refreshing)))
        {
            var states = new[] { Data.State(Data.Config(name: "a")), Data.State(Data.Config(name: "b"), health: health) };

            var (state, text) = TrayViewModel.Aggregate(states);

            state.Should().Be(TrayState.Warning);
            text.Should().MatchRegex(@"octo/b: [a-z ]+$", $"{health} needs a description");
        }
    }

    [AvaloniaFact]
    public void Healthy_and_refreshing_repositories_are_normal()
    {
        TrayViewModel.Aggregate([Data.State(Data.Config())]).Should().Be((TrayState.Normal, "GitWidget – repository up to date"));
        TrayViewModel.Aggregate([Data.State(Data.Config(name: "a")), Data.State(Data.Config(name: "b"), health: RepositoryHealthState.Refreshing)])
            .Should().Be((TrayState.Normal, "GitWidget – 2 repositories up to date"));
    }

    [AvaloniaFact]
    public void View_model_tracks_hub_state_icon_and_repository_menu()
    {
        var hub = new RepositoryStatusHub();
        var controller = new FakeSynchronizationController();
        var settings = new SettingsService(new FakeSettingsStore(), controller, new FakeAutostartService());
        using var vm = new TrayViewModel(hub, controller, settings, new FakeNavigator(), new InlineUiDispatcher());
        var menuChanges = 0;
        vm.RepositoriesChanged += (_, _) => menuChanges++;

        vm.State.Should().Be(TrayState.Offline);
        vm.Icon.Should().NotBeNull();
        var offlineIcon = vm.Icon;

        var configuration = Data.Config();
        hub.Publish(Data.State(configuration));

        vm.State.Should().Be(TrayState.Normal);
        vm.ToolTip.Should().Be("GitWidget – repository up to date");
        vm.Icon.Should().NotBeSameAs(offlineIcon);
        vm.Repositories.Select(r => (r.Id, r.Name)).Should().Equal((configuration.Id, "octo/widget"));
        menuChanges.Should().Be(1);

        hub.Publish(Data.State(configuration) with { IsRefreshing = true, LastAttemptAt = Data.T0 });

        menuChanges.Should().Be(1, "the native menu is not rebuilt when nothing it shows changed");

        hub.Publish(Data.State(configuration, health: RepositoryHealthState.RateLimited));

        vm.State.Should().Be(TrayState.Warning);
        vm.ToolTip.Should().Be("GitWidget – octo/widget: rate limited");
        var beforeRemoval = menuChanges;

        hub.Remove(configuration.Id);

        vm.State.Should().Be(TrayState.Offline);
        vm.Repositories.Should().BeEmpty();
        menuChanges.Should().Be(beforeRemoval + 1);
    }

    [AvaloniaFact]
    public void Repository_menu_status_describes_the_current_health()
    {
        var hub = new RepositoryStatusHub();
        var controller = new FakeSynchronizationController();
        var settings = new SettingsService(new FakeSettingsStore(), controller, new FakeAutostartService());
        using var vm = new TrayViewModel(hub, controller, settings, new FakeNavigator(), new InlineUiDispatcher());
        var configuration = Data.Config();

        hub.Publish(Data.State(configuration));
        vm.Repositories.Single().Status.Should().Be("up to date");

        hub.Publish(Data.State(configuration, health: RepositoryHealthState.AuthenticationFailed));
        vm.Repositories.Single().Status.Should().Be("token rejected");
    }

    [AvaloniaFact]
    public void Long_tooltips_are_truncated_for_the_os_tray()
    {
        var hub = new RepositoryStatusHub();
        var controller = new FakeSynchronizationController();
        var settings = new SettingsService(new FakeSettingsStore(), controller, new FakeAutostartService());
        using var vm = new TrayViewModel(hub, controller, settings, new FakeNavigator(), new InlineUiDispatcher());

        for (var i = 0; i < 10; i++)
        {
            hub.Publish(Data.State(Data.Config(name: $"repository-with-a-long-name-{i}", sortOrder: i), health: RepositoryHealthState.ProviderError));
        }

        vm.ToolTip.Length.Should().Be(120);
        vm.ToolTip.Should().EndWith("…");
    }

    [AvaloniaFact]
    public async Task Toggle_commands_save_settings_and_mirror_them()
    {
        var hub = new RepositoryStatusHub();
        var controller = new FakeSynchronizationController(new AppSettings { AlwaysOnTop = false, StartAtLogin = false });
        var store = new FakeSettingsStore();
        var autostart = new FakeAutostartService();
        var settings = new SettingsService(store, controller, autostart);
        var navigator = new FakeNavigator();
        using var vm = new TrayViewModel(hub, controller, settings, navigator, new InlineUiDispatcher());

        await vm.ToggleAlwaysOnTopCommand.ExecuteAsync(null);
        await vm.ToggleStartAtLoginCommand.ExecuteAsync(null);

        store.Saved.Should().HaveCount(2);
        controller.Settings.AlwaysOnTop.Should().BeTrue();
        controller.Settings.StartAtLogin.Should().BeTrue();
        autostart.SetCalls.Should().Equal(true);
        vm.AlwaysOnTop.Should().BeTrue();
        vm.StartAtLogin.Should().BeTrue();
        vm.StartAtLoginSupported.Should().BeTrue();

        vm.RefreshCommand.Execute(null);
        vm.ShowWidgetCommand.Execute(null);
        vm.OpenSettingsCommand.Execute(null);
        vm.ExitCommand.Execute(null);
        controller.RefreshRequests.Should().Equal((Guid?)null);
        navigator.Calls.Should().Equal("ShowWidget(True)", "ShowSettings(,False)", "Exit");
    }
}
