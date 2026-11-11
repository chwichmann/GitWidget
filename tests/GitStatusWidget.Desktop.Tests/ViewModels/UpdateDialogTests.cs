using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Updates;
using GitStatusWidget.Desktop.Tests.TestDoubles;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Desktop.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static GitStatusWidget.Desktop.Tests.TestData.Data;

namespace GitStatusWidget.Desktop.Tests.ViewModels;

/// <summary>Issue #19: update dialog with in-place installation.</summary>
public sealed class UpdateDialogTests
{
    private static readonly Uri ReleasePage = new("https://github.com/chwichmann/GitWidget/releases/tag/v0.1.2");
    private readonly FakeNavigator _navigator = new();
    private readonly InlineUiDispatcher _ui = new();

    private static async Task<UpdateChecker> CheckerAsync(string tag = "v0.1.2")
    {
        SemanticVersion.TryParse(tag, out var version).Should().BeTrue();
        var release = new ReleaseInfo(version, tag, ReleasePage, false, T0)
        {
            Asset = new ReleaseAsset("GitWidget-win-x64.zip", new Uri("https://github.com/chwichmann/GitWidget/releases/download/v0.1.2/GitWidget-win-x64.zip"), 100, new string('a', 64)),
        };
        var checker = new UpdateChecker(new Feed(new ReleaseFeedResult.Success([release])), new FakeSynchronizationController(new AppSettings()),
            new FakeTimeProvider(T0), NullLogger<UpdateChecker>.Instance, "0.1.1", TimeSpan.Zero);
        await checker.CheckAsync(CancellationToken.None);
        return checker;
    }

    [Fact]
    public async Task Dialog_offers_the_in_place_update_when_it_is_possible()
    {
        using var checker = await CheckerAsync();
        using var vm = new UpdateViewModel(checker, _navigator, _ui, new Installer());

        vm.Title.Should().Be("GitWidget 0.1.2 is available");
        vm.CurrentText.Should().Be("You are running 0.1.1.");
        vm.InstallText.Should().Be("Update to 0.1.2 and restart");
        vm.CanInstall.Should().BeTrue();
        vm.HasCannotInstallReason.Should().BeFalse();
        vm.InstallCommand.CanExecute(null).Should().BeTrue();

        vm.OpenReleasePageCommand.Execute(null);
        _navigator.Calls.Should().Equal($"OpenInBrowser({ReleasePage})");
    }

    [Fact]
    public async Task Successful_installation_restarts_into_the_new_version()
    {
        using var checker = await CheckerAsync();
        var installer = new Installer();
        using var vm = new UpdateViewModel(checker, _navigator, _ui, installer);

        await vm.InstallCommand.ExecuteAsync(null);

        installer.Installed.Should().ContainSingle().Which.Tag.Should().Be("v0.1.2");
        installer.Restarts.Should().Be(1);
        _navigator.Calls.Should().Equal("Exit");
    }

    [Fact]
    public async Task Progress_is_shown_while_downloading_and_only_the_download_can_be_cancelled()
    {
        using var checker = await CheckerAsync();
        var installer = new Installer { Hold = new TaskCompletionSource<bool>() };
        using var vm = new UpdateViewModel(checker, _navigator, _ui, installer);

        var running = vm.InstallCommand.ExecuteAsync(null);
        installer.Report(UpdateInstallStatus.Downloading, 0.42);

        vm.IsBusy.Should().BeTrue();
        vm.Progress.Should().Be(42);
        vm.StatusText.Should().StartWith("Downloading… 42");
        vm.CanCancel.Should().BeTrue();
        vm.InstallCommand.CanExecute(null).Should().BeFalse();

        installer.Report(UpdateInstallStatus.Installing, null);
        vm.CanCancel.Should().BeFalse("replacing the files is never interrupted");
        vm.IsIndeterminate.Should().BeTrue();

        installer.Hold.SetResult(true);
        await running;
        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Failed_installation_keeps_the_application_running_and_says_why()
    {
        using var checker = await CheckerAsync();
        var installer = new Installer { FailWith = "The download does not match the checksum of the release. Nothing was changed." };
        using var vm = new UpdateViewModel(checker, _navigator, _ui, installer);

        await vm.InstallCommand.ExecuteAsync(null);

        vm.IsFailed.Should().BeTrue();
        vm.StatusText.Should().Be("The download does not match the checksum of the release. Nothing was changed.");
        vm.IsBusy.Should().BeFalse();
        vm.InstallCommand.CanExecute(null).Should().BeTrue("the update can be tried again");
        installer.Restarts.Should().Be(0);
        _navigator.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task New_version_that_cannot_be_started_does_not_exit_the_application()
    {
        using var checker = await CheckerAsync();
        var installer = new Installer { RestartSucceeds = false };
        using var vm = new UpdateViewModel(checker, _navigator, _ui, installer);

        await vm.InstallCommand.ExecuteAsync(null);

        _navigator.Calls.Should().BeEmpty();
        vm.IsFailed.Should().BeTrue();
    }

    [Fact]
    public async Task Release_that_cannot_be_installed_in_place_leads_to_the_release_page()
    {
        using var checker = await CheckerAsync();
        using var blocked = new UpdateViewModel(checker, _navigator, _ui, new Installer { Reason = "This is a development build, not an installed release." });
        using var none = new UpdateViewModel(checker, _navigator, _ui);

        blocked.CanInstall.Should().BeFalse();
        blocked.InstallCommand.CanExecute(null).Should().BeFalse();
        blocked.CannotInstallReason.Should().Be("This is a development build, not an installed release. Use the release page to update manually.");
        none.CanInstall.Should().BeFalse();
        none.CannotInstallReason.Should().StartWith("Updating in place is not available in this build.");
    }

    [AvaloniaFact]
    public async Task Update_window_shows_the_install_button_without_binding_errors()
    {
        using var checker = await CheckerAsync();
        using var vm = new UpdateViewModel(checker, _navigator, _ui, new Installer());
        using var errors = new BindingErrorSink();
        var window = new UpdateWindow { DataContext = vm };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var install = window.FindControl<Button>("InstallButton")!;
        install.IsVisible.Should().BeTrue();
        install.Content.Should().Be("Update to 0.1.2 and restart");
        window.FindControl<ProgressBar>("DownloadProgress")!.IsVisible.Should().BeFalse();
        window.FindControl<TextBlock>("CannotInstallText")!.IsVisible.Should().BeFalse();
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain(["GitWidget 0.1.2 is available", "You are running 0.1.1."]);
        errors.Messages.Should().BeEmpty();
        window.Close();
    }

    private sealed class Feed(ReleaseFeedResult result) : IReleaseFeed
    {
        public string Source => "chwichmann/GitWidget";

        public Task<ReleaseFeedResult> GetReleasesAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class Installer : IUpdateInstaller
    {
        public event EventHandler<UpdateInstallState>? StateChanged;

        public UpdateInstallState State { get; private set; } = new(UpdateInstallStatus.Idle, null, null);

        public string? Reason { get; init; }

        public string? FailWith { get; init; }

        public bool RestartSucceeds { get; init; } = true;

        public TaskCompletionSource<bool>? Hold { get; init; }

        public List<ReleaseInfo> Installed { get; } = [];

        public int Restarts { get; private set; }

        public string? WhyNotInstallable(ReleaseInfo release) => Reason;

        public void Report(UpdateInstallStatus status, double? progress, string? message = null)
        {
            State = new UpdateInstallState(status, progress, message);
            StateChanged?.Invoke(this, State);
        }

        public async Task<bool> InstallAsync(ReleaseInfo release, CancellationToken cancellationToken)
        {
            Installed.Add(release);
            if (Hold is not null)
            {
                await Hold.Task;
            }

            if (FailWith is not null)
            {
                Report(UpdateInstallStatus.Failed, null, FailWith);
                return false;
            }

            Report(UpdateInstallStatus.ReadyToRestart, null);
            return true;
        }

        public bool Restart()
        {
            Restarts++;
            if (!RestartSucceeds)
            {
                Report(UpdateInstallStatus.Failed, null, "The new version could not be started; the previous version was restored.");
            }

            return RestartSucceeds;
        }
    }
}
