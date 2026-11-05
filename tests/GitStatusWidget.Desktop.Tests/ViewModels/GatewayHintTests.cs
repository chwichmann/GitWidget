using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Platform;
using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Desktop.Tests.TestDoubles;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Desktop.Views;
using GitStatusWidget.Domain.Health;
using GitStatusWidget.Domain.Repositories;
using Microsoft.Extensions.Time.Testing;
using static GitStatusWidget.Desktop.Tests.TestData.Data;

namespace GitStatusWidget.Desktop.Tests.ViewModels;

/// <summary>Issue #10: visible User-Agent field and actionable gateway messages.</summary>
public sealed class GatewayHintTests
{
    private readonly FakeTimeProvider _time = new(T0);
    private readonly RepositoryStatusHub _hub = new();
    private readonly InlineUiDispatcher _ui = new();
    private readonly FakeNavigator _navigator = new();

    private static RepositoryRuntimeState Blocked(RepositoryConfiguration configuration) =>
        State(configuration, Snapshot(configuration), RepositoryHealthState.GatewayBlocked) with
        {
            GatewayHost = "portal.example.net",
            LastError = "Redirected to another host (portal.example.net).",
        };

    private SettingsViewModel Settings(FakeProviderResolver? providers = null, params RepositoryConfiguration[] repositories) =>
        new(new SettingsService(new FakeSettingsStore(), new FakeSynchronizationController(new AppSettings { Repositories = repositories }), new FakeAutostartService()),
            new FakeCredentialStore(), new CredentialStoreStatus("Test", true, null), providers ?? new FakeProviderResolver(), new FakeRepositoryCache(), _hub);

    [Fact]
    public void Editor_offers_the_git_compatible_user_agent_and_fills_it_in()
    {
        var editor = new RepositoryEditorViewModel(Config(provider: RepositoryProviderKind.GitLab, owner: "group", name: "project"), hasStoredToken: true);

        editor.ShowUserAgentSuggestion.Should().BeTrue();
        editor.UseSuggestedUserAgentCommand.Execute(null);

        editor.UserAgent.Should().Be(RepositoryEditorViewModel.SuggestedUserAgent).And.EndWith(" git/2.47.0").And.StartWith("GitWidget/");
    }

    [Fact]
    public void Suggestion_is_offered_for_GitHub_only_while_blocked()
    {
        var editor = new RepositoryEditorViewModel(Config(), hasStoredToken: true);
        editor.ShowUserAgentSuggestion.Should().BeFalse();

        editor.GatewayBlocked = true;

        editor.ShowUserAgentSuggestion.Should().BeTrue();
        editor.GatewayHint.Should().Contain("login gateway intercepted");
    }

    [Fact]
    public async Task Connection_test_intercepted_by_a_gateway_marks_the_editor()
    {
        var providers = new FakeProviderResolver();
        providers.GitLab.OnTest = _ => ProviderConnectionResult.Fail(ProviderFailureKind.GatewayInterception, "Redirected to another host (portal.example.net).") with { GatewayHost = "portal.example.net" };
        var config = Config(provider: RepositoryProviderKind.GitLab, owner: "group", name: "project");
        var vm = Settings(providers, config);

        await vm.TestConnectionCommand.ExecuteAsync(null);

        vm.Selected!.GatewayBlocked.Should().BeTrue();
        vm.Selected.ConnectionOk.Should().BeFalse();
    }

    [Fact]
    public void Opening_settings_from_the_dashboard_pre_fills_the_suggestion_but_does_not_save()
    {
        var config = Config(provider: RepositoryProviderKind.GitLab, owner: "group", name: "project");
        _hub.Publish(Blocked(config));
        var vm = Settings(null, config);

        vm.Select(config.Id, addNew: false, suggestUserAgent: true);

        vm.Selected!.UserAgent.Should().Be(RepositoryEditorViewModel.SuggestedUserAgent);
        vm.Selected.GatewayBlocked.Should().BeTrue("the editor knows the repository is currently blocked");
    }

    [Fact]
    public void Dashboard_offers_the_suggestion_only_for_gateway_blocked_repositories()
    {
        var config = Config();
        _hub.Publish(Blocked(config));
        using var dashboard = new DashboardViewModel(_hub, new DashboardProjector(_time), new FakeSynchronizationController(), _ui, _navigator, new ActivityFeed(_hub, _ui), _time);

        dashboard.CanSuggestUserAgent.Should().BeTrue();
        dashboard.Projection!.Health.Summary.Should().Contain("portal.example.net");
        dashboard.UseSuggestedUserAgentCommand.Execute(null);
        _navigator.Calls.Should().ContainSingle().Which.Should().Be($"ShowSettings({config.Id},False,suggestUserAgent)");

        _hub.Publish(State(config, Snapshot(config)));
        dashboard.CanSuggestUserAgent.Should().BeFalse();
    }

    [Fact]
    public void Widget_row_shows_the_gateway_message_as_its_own_line()
    {
        var config = Config();
        _hub.Publish(Blocked(config));

        using var widget = new WidgetViewModel(_hub, new DashboardProjector(_time), new FakeSynchronizationController(), _ui, _navigator, _time);

        var row = widget.Rows.Single();
        row.ShowProblemLine.Should().BeTrue();
        row.HealthText.Should().Be("Blocked by a login gateway (portal.example.net) – adjust the User-Agent");
    }

    [AvaloniaFact]
    public void User_agent_field_is_visible_without_expanding_advanced_settings()
    {
        var config = Config(provider: RepositoryProviderKind.GitLab, owner: "group", name: "project");
        var window = new SettingsWindow { DataContext = Settings(null, config) };

        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "UserAgentBox");
        box.IsEffectivelyVisible.Should().BeTrue();
        box.FindAncestorOfType<Expander>().Should().BeNull("the field is not hidden in the Advanced expander");
        window.Close();
    }
}
