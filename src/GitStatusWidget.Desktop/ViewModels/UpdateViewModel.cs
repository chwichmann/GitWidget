using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitStatusWidget.Application.Updates;
using GitStatusWidget.Desktop.Services;

namespace GitStatusWidget.Desktop.ViewModels;

/// <summary>
/// Update dialog (issue #19): offers to install the available release in place and restart, or to open its release
/// page. Nothing is installed without the explicit click.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject, IDisposable
{
    private readonly UpdateChecker _updates;
    private readonly IUpdateInstaller? _installer;
    private readonly IAppNavigator _navigator;
    private readonly IUiDispatcher _ui;
    private CancellationTokenSource? _cancellation;
    private ReleaseInfo? _release;

    public UpdateViewModel(UpdateChecker updates, IAppNavigator navigator, IUiDispatcher ui, IUpdateInstaller? installer = null)
    {
        _updates = updates;
        _installer = installer;
        _navigator = navigator;
        _ui = ui;
        _updates.StateChanged += OnUpdateStateChanged;
        if (_installer is not null)
        {
            _installer.StateChanged += OnInstallStateChanged;
        }

        ApplyUpdate(_updates.State);
    }

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>For example "GitWidget 0.1.4 is available".</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>For example "You are running 0.1.3."</summary>
    [ObservableProperty]
    public partial string CurrentText { get; set; } = string.Empty;

    /// <summary>A newer release exists.</summary>
    [ObservableProperty]
    public partial bool IsAvailable { get; set; }

    /// <summary>The release can be installed in place and no installation is running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool CanInstall { get; set; }

    /// <summary>Why the release cannot be installed in place; empty when it can.</summary>
    [ObservableProperty]
    public partial string CannotInstallReason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasCannotInstallReason { get; set; }

    /// <summary>Download, verification or installation is running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Only the download can be cancelled; replacing the files cannot.</summary>
    [ObservableProperty]
    public partial bool CanCancel { get; set; }

    /// <summary>Download progress in percent.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>The progress is not measurable (verifying, installing).</summary>
    [ObservableProperty]
    public partial bool IsIndeterminate { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasStatus { get; set; }

    /// <summary>The last installation failed; the status text says why.</summary>
    [ObservableProperty]
    public partial bool IsFailed { get; set; }

    /// <summary>Label of the install button, for example "Update to 0.1.4 and restart".</summary>
    [ObservableProperty]
    public partial string InstallText { get; set; } = "Update and restart";

    [RelayCommand(CanExecute = nameof(CanStartInstall))]
    private async Task InstallAsync()
    {
        if (_installer is null || _release is not { } release)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        try
        {
            var installed = await _installer.InstallAsync(release, cancellation.Token).ConfigureAwait(true);
            if (installed && _installer.Restart())
            {
                // The new version waits for this process; exiting hands over.
                _navigator.ExitApplication();
            }
        }
        finally
        {
            _cancellation = null;
            IsBusy = false;
            ApplyInstall(_installer.State);
        }
    }

    private bool CanStartInstall() => CanInstall && !IsBusy;

    [RelayCommand]
    private void CancelInstall() => _cancellation?.Cancel();

    [RelayCommand]
    private void OpenReleasePage() => _navigator.OpenInBrowser(_release?.PageUrl);

    [RelayCommand]
    private void Close()
    {
        _cancellation?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _updates.StateChanged -= OnUpdateStateChanged;
        if (_installer is not null)
        {
            _installer.StateChanged -= OnInstallStateChanged;
        }
    }

    private void OnUpdateStateChanged(object? sender, UpdateState state) => _ui.Post(() => ApplyUpdate(state));

    private void OnInstallStateChanged(object? sender, UpdateInstallState state) => _ui.Post(() => ApplyInstall(state));

    private void ApplyUpdate(UpdateState state)
    {
        _release = state.Status == UpdateStatus.Available ? state.Latest : null;
        IsAvailable = _release is not null;
        Title = _release is null ? "GitWidget is up to date" : $"GitWidget {_release.Version} is available";
        CurrentText = $"You are running {state.CurrentVersion}.";
        InstallText = _release is null ? "Update and restart" : $"Update to {_release.Version} and restart";

        var reason = _release is null ? string.Empty
            : _installer is null ? "Updating in place is not available in this build."
            : _installer.WhyNotInstallable(_release) ?? string.Empty;
        CannotInstallReason = reason.Length == 0 ? string.Empty : $"{reason} Use the release page to update manually.";
        HasCannotInstallReason = reason.Length > 0;
        CanInstall = _release is not null && reason.Length == 0;
    }

    private void ApplyInstall(UpdateInstallState state)
    {
        Progress = (state.Progress ?? 0) * 100;
        IsIndeterminate = state.Status is UpdateInstallStatus.Verifying or UpdateInstallStatus.Installing or UpdateInstallStatus.ReadyToRestart;
        CanCancel = IsBusy && state.Status == UpdateInstallStatus.Downloading;
        IsFailed = state.Status == UpdateInstallStatus.Failed;
        StatusText = state.Status switch
        {
            UpdateInstallStatus.Downloading => string.Create(CultureInfo.CurrentCulture, $"Downloading… {Progress:0} %"),
            UpdateInstallStatus.Verifying => "Verifying the download…",
            UpdateInstallStatus.Installing => "Installing…",
            UpdateInstallStatus.ReadyToRestart => "Restarting…",
            UpdateInstallStatus.Failed => state.Message ?? "The update failed.",
            _ => string.Empty,
        };
        HasStatus = StatusText.Length > 0;
    }
}
