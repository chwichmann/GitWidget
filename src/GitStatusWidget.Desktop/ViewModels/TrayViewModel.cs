using Avalonia.Controls;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.Health;

namespace GitStatusWidget.Desktop.ViewModels;

/// <summary>Aggregate tray state (architecture §17).</summary>
public enum TrayState
{
    Normal,
    Failed,
    Warning,
    Offline,
}

/// <summary>Data context of the tray icon and its menu.</summary>
public sealed partial class TrayViewModel : ObservableObject, IDisposable
{
    private static readonly Dictionary<TrayState, WindowIcon> Icons = new()
    {
        [TrayState.Normal] = Load("tray"),
        [TrayState.Failed] = Load("tray-failed"),
        [TrayState.Warning] = Load("tray-warning"),
        [TrayState.Offline] = Load("tray-offline"),
    };

    private readonly IRepositoryStatusHub _hub;
    private readonly ISynchronizationController _controller;
    private readonly SettingsService _settings;
    private readonly IAppNavigator _navigator;
    private readonly IUiDispatcher _ui;

    public TrayViewModel(IRepositoryStatusHub hub, ISynchronizationController controller, SettingsService settings, IAppNavigator navigator, IUiDispatcher ui)
    {
        _hub = hub;
        _controller = controller;
        _settings = settings;
        _navigator = navigator;
        _ui = ui;
        _hub.StateChanged += OnStateChanged;
        _hub.RepositoryRemoved += OnRepositoryRemoved;
        _settings.SettingsChanged += OnSettingsChanged;
        AlwaysOnTop = controller.Settings.AlwaysOnTop;
        StartAtLogin = controller.Settings.StartAtLogin;
        Recompute();
    }

    /// <summary>Raised when the list of repositories changed (the tray submenu is rebuilt from code).</summary>
    public event EventHandler? RepositoriesChanged;

    [ObservableProperty]
    public partial WindowIcon Icon { get; set; } = Icons[TrayState.Offline];

    [ObservableProperty]
    public partial string ToolTip { get; set; } = "GitWidget";

    [ObservableProperty]
    public partial TrayState State { get; set; } = TrayState.Offline;

    [ObservableProperty]
    public partial bool AlwaysOnTop { get; set; }

    [ObservableProperty]
    public partial bool StartAtLogin { get; set; }

    public bool StartAtLoginSupported => _settings.AutostartSupported;

    public IReadOnlyList<(Guid Id, string Name, string Status)> Repositories { get; private set; } = [];

    [RelayCommand]
    private void ShowWidget() => _navigator.ShowWidget();

    [RelayCommand]
    private void OpenDashboard(Guid? repositoryId) => _navigator.ShowDashboard(repositoryId);

    [RelayCommand]
    private void Refresh() => _controller.RequestRefresh();

    [RelayCommand]
    private void OpenSettings() => _navigator.ShowSettings();

    [RelayCommand]
    private async Task ToggleAlwaysOnTopAsync()
    {
        var value = !_controller.Settings.AlwaysOnTop;
        await _settings.UpdateAsync(s => s with { AlwaysOnTop = value }, CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ToggleStartAtLoginAsync()
    {
        var value = !_controller.Settings.StartAtLogin;
        await _settings.UpdateAsync(s => s with { StartAtLogin = value }, CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenDiagnostics() => _navigator.ShowDiagnostics();

    [RelayCommand]
    private void Exit() => _navigator.ExitApplication();

    public void Dispose()
    {
        _hub.StateChanged -= OnStateChanged;
        _hub.RepositoryRemoved -= OnRepositoryRemoved;
        _settings.SettingsChanged -= OnSettingsChanged;
    }

    internal static (TrayState State, string Text) Aggregate(IReadOnlyList<RepositoryRuntimeState> states)
    {
        if (states.Count == 0)
        {
            return (TrayState.Offline, "GitWidget – no repository configured");
        }

        var failedBuilds = states
            .Where(s => s.Snapshot?.Builds.Where(b => s.Snapshot.DefaultBranch is null || b.RefName == s.Snapshot.DefaultBranch)
                .OrderByDescending(b => b.CreatedAt).FirstOrDefault()?.State == BuildState.Failed)
            .Select(s => s.Configuration.EffectiveDisplayName)
            .ToList();
        if (failedBuilds.Count > 0)
        {
            return (TrayState.Failed, $"GitWidget – build failed: {string.Join(", ", failedBuilds)}");
        }

        var offline = states.Where(s => s.Health == RepositoryHealthState.Offline).ToList();
        if (offline.Count == states.Count)
        {
            return (TrayState.Offline, "GitWidget – offline, showing last known data");
        }

        var problems = states
            .Where(s => s.Health is not (RepositoryHealthState.Healthy or RepositoryHealthState.Refreshing))
            .Select(s => $"{s.Configuration.EffectiveDisplayName}: {Describe(s.Health)}")
            .ToList();
        if (problems.Count > 0)
        {
            return (TrayState.Warning, $"GitWidget – {string.Join("; ", problems)}");
        }

        return (TrayState.Normal, states.Count == 1 ? "GitWidget – repository up to date" : $"GitWidget – {states.Count} repositories up to date");
    }

    private static string Describe(RepositoryHealthState health) => health switch
    {
        RepositoryHealthState.Stale => "stale",
        RepositoryHealthState.Offline => "offline",
        RepositoryHealthState.RateLimited => "rate limited",
        RepositoryHealthState.AuthenticationFailed => "token rejected",
        RepositoryHealthState.AuthorizationFailed => "access denied",
        RepositoryHealthState.NotFound => "not found",
        RepositoryHealthState.ConfigurationError => "configuration error",
        _ => "provider error",
    };

    private static WindowIcon Load(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://GitWidget/Assets/{name}.png"));
        return new WindowIcon(stream);
    }

    private void OnStateChanged(object? sender, RepositoryRuntimeState e) => _ui.Post(Recompute);

    private void OnRepositoryRemoved(object? sender, Guid e) => _ui.Post(Recompute);

    private void OnSettingsChanged(object? sender, Application.Settings.AppSettings settings) => _ui.Post(() =>
    {
        AlwaysOnTop = settings.AlwaysOnTop;
        StartAtLogin = settings.StartAtLogin;
    });

    private void Recompute()
    {
        var states = _hub.States;
        var (state, text) = Aggregate(states);
        State = state;
        Icon = Icons[state];
        ToolTip = text.Length > 120 ? text[..119] + "…" : text;

        var repositories = states.Select(s => (s.Id, s.Configuration.EffectiveDisplayName, Describe(s.Health))).ToList();
        if (!repositories.Select(r => (r.Id, r.EffectiveDisplayName)).SequenceEqual(Repositories.Select(r => (r.Id, r.Name))))
        {
            Repositories = repositories;
            RepositoriesChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
