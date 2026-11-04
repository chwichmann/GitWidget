using Avalonia.Headless.XUnit;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Platform;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Desktop.Tests.TestDoubles;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Desktop.Views;
using GitStatusWidget.Domain.Health;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using static GitStatusWidget.Desktop.Tests.TestData.Data;

namespace GitStatusWidget.Desktop.Tests.ViewModels;

public sealed class DiagnosticsViewModelTests
{
    private readonly RepositoryStatusHub _hub = new();
    private readonly FakeTimeProvider _time = new(T0.AddMinutes(5));

    private DiagnosticsViewModel Create(bool persistentStore = true)
    {
        var log = new DiagnosticsLog(_hub, new InlineUiDispatcher());
        var status = new CredentialStoreStatus("Test store", persistentStore, persistentStore ? null : "no keyring");
        return new DiagnosticsViewModel(_hub, new AppPaths(Path.Combine(Path.GetTempPath(), "gitwidget-diag")), status, log, _time);
    }

    [Theory]
    [InlineData(RepositoryProviderKind.GitHub, "https://github.com/", "https://api.github.com/")]
    [InlineData(RepositoryProviderKind.GitHub, "https://ghe.example.com/", "https://ghe.example.com/api/v3/")]
    [InlineData(RepositoryProviderKind.GitLab, "https://gitlab.example.com/gitlab/", "https://gitlab.example.com/gitlab/api/v4/")]
    public void Api_base_is_derived_per_provider(RepositoryProviderKind provider, string server, string expected) =>
        DiagnosticsViewModel.ApiBaseOf(provider, new Uri(server)).Should().Be(expected);

    [Fact]
    public void Report_lists_repositories_errors_and_store_without_secrets()
    {
        var config = Config(displayName: "Widget");
        _hub.Publish(State(config, Snapshot(config, issues: [Issue(1)])));
        var vm = Create(persistentStore: false);
        _hub.Publish(new RepositoryRuntimeState(config, null, RepositoryHealthState.AuthenticationFailed)
        {
            LastFailure = ProviderFailureKind.AuthenticationFailed,
            LastError = "The token was rejected (401).",
            LastAttemptAt = _time.GetUtcNow(),
        });
        vm.ReloadCommand.Execute(null);

        var report = vm.BuildReport();

        report.Should().Contain("[Widget] GitHub https://api.github.com/")
            .And.Contain("The token was rejected (401).")
            .And.Contain("not persistent (no keyring)")
            .And.NotContainAny("Bearer", "PRIVATE-TOKEN");
        vm.Errors.Should().ContainSingle().Which.Kind.Should().Be(ProviderFailureKind.AuthenticationFailed);
    }

    [Fact]
    public void Same_failure_attempt_is_recorded_once()
    {
        var config = Config();
        var vm = Create();
        var failed = new RepositoryRuntimeState(config, null, RepositoryHealthState.Offline)
        {
            LastFailure = ProviderFailureKind.Offline,
            LastError = "Network error",
            LastAttemptAt = _time.GetUtcNow(),
        };

        _hub.Publish(failed);
        _hub.Publish(failed with { IsRefreshing = true });

        vm.Errors.Should().ContainSingle();
    }

    [AvaloniaFact]
    public void Diagnostics_window_shows_headlessly()
    {
        var config = Config();
        _hub.Publish(State(config, Snapshot(config)));
        var window = new DiagnosticsWindow { DataContext = Create() };

        window.Show();

        window.IsVisible.Should().BeTrue();
        window.Close();
    }
}
