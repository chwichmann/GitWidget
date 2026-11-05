using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Application.Updates;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Domain.Events;

namespace GitStatusWidget.Desktop.ViewModels;

/// <summary>Compact multi-repository widget: one row per enabled repository (architecture §16.2).</summary>
public sealed partial class WidgetViewModel : ObservableObject, IDisposable
{
    private readonly IRepositoryStatusHub _hub;
    private readonly DashboardProjector _projector;
    private readonly ISynchronizationController _controller;
    private readonly IUiDispatcher _ui;
    private readonly IAppNavigator _navigator;
    private readonly TimeProvider _time;
    private readonly ITimer _freshnessTimer;
    private readonly SettingsService? _settings;
    private readonly UpdateChecker? _updates;
    private bool _reordering;

    public WidgetViewModel(
        IRepositoryStatusHub hub,
        DashboardProjector projector,
        ISynchronizationController controller,
        IUiDispatcher ui,
        IAppNavigator navigator,
        TimeProvider time,
        SettingsService? settings = null,
        UpdateChecker? updates = null)
    {
        _settings = settings;
        if (_settings is not null)
        {
            _settings.SettingsChanged += OnSettingsChanged;
        }

        _updates = updates;
        if (_updates is not null)
        {
            _updates.StateChanged += OnUpdateStateChanged;
            ApplyUpdate(_updates.State);
        }

        _hub = hub;
        _projector = projector;
        _controller = controller;
        _ui = ui;
        _navigator = navigator;
        _time = time;
        _hub.StateChanged += OnStateChanged;
        _hub.RepositoryRemoved += OnRepositoryRemoved;
        _hub.ChangesDetected += OnChangesDetected;
        foreach (var state in _hub.States)
        {
            Apply(state);
        }

        // One shared timer for relative times (architecture: no per-item timers).
        _freshnessTimer = time.CreateTimer(_ => _ui.Post(UpdateFreshness), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        UpdateFooter();
    }

    public ObservableCollection<RepositoryRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [ObservableProperty]
    public partial string FooterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsAnyRefreshing { get; set; }

    [ObservableProperty]
    public partial bool AlwaysOnTop { get; set; }

    /// <summary>A newer GitWidget release exists (issue #7).</summary>
    [ObservableProperty]
    public partial bool UpdateAvailable { get; set; }

    [ObservableProperty]
    public partial string UpdateToolTip { get; set; } = string.Empty;

    private Uri? UpdateUrl { get; set; }

    [RelayCommand]
    private void Refresh() => _controller.RequestRefresh();

    [RelayCommand]
    private void OpenDashboard(RepositoryRowViewModel? row) => _navigator.ShowDashboard(row?.Id);

    [RelayCommand]
    private void OpenSettings() => _navigator.ShowSettings(addNew: IsEmpty);

    [RelayCommand]
    private void Hide() => _navigator.HideWidget();

    [RelayCommand]
    private void OpenUpdate() => _navigator.OpenInBrowser(UpdateUrl);

    /// <summary>Starts a drag reorder: background updates no longer re-sort the rows until it is committed.</summary>
    public void BeginReorder() => _reordering = true;

    /// <summary>Moves a row during a drag reorder.</summary>
    public void MoveRow(int from, int to)
    {
        if (from >= 0 && from < Rows.Count && to >= 0 && to < Rows.Count && from != to)
        {
            Rows.Move(from, to);
        }
    }

    /// <summary>Persists the current row order as the repository order (same as reordering in Settings).</summary>
    public async Task CommitReorderAsync()
    {
        try
        {
            if (_settings is not null)
            {
                await _settings.ReorderAsync(Rows.Select(r => r.Id).ToList(), CancellationToken.None).ConfigureAwait(true);
            }
        }
        finally
        {
            _reordering = false;
            SortRows();
        }
    }

    public void Dispose()
    {
        _hub.StateChanged -= OnStateChanged;
        _hub.RepositoryRemoved -= OnRepositoryRemoved;
        _hub.ChangesDetected -= OnChangesDetected;
        if (_updates is not null)
        {
            _updates.StateChanged -= OnUpdateStateChanged;
        }

        if (_settings is not null)
        {
            _settings.SettingsChanged -= OnSettingsChanged;
        }

        _freshnessTimer.Dispose();
    }

    private void OnStateChanged(object? sender, RepositoryRuntimeState state) => _ui.Post(() => Apply(state));

    private void OnUpdateStateChanged(object? sender, UpdateState state) => _ui.Post(() => ApplyUpdate(state));

    private void ApplyUpdate(UpdateState state)
    {
        var latest = state.Status == UpdateStatus.Available ? state.Latest : null;
        UpdateAvailable = latest is not null;
        UpdateUrl = latest?.PageUrl;
        UpdateToolTip = latest is null
            ? string.Empty
            : $"GitWidget {latest.Version} is available (running {state.CurrentVersion}) – click to open the release page";
    }

    private void OnRepositoryRemoved(object? sender, Guid id) => _ui.Post(() =>
    {
        var row = Rows.FirstOrDefault(r => r.Id == id);
        if (row is not null)
        {
            Rows.Remove(row);
        }

        UpdateFooter();
    });

    private void OnChangesDetected(object? sender, RepositoryChangesDetected changes)
    {
        if (changes.IsCatchUp)
        {
            return; // Never animate old changes (architecture §16.4).
        }

        _ui.Post(() =>
        {
            var row = Rows.FirstOrDefault(r => r.Id == changes.RepositoryId);
            if (row is null)
            {
                return;
            }

            foreach (var animation in changes.Events.Select(Map).OfType<RowAnimation>().Distinct())
            {
                row.Animate(animation);
            }
        });
    }

    private static RowAnimation? Map(RepositoryChangeEvent e) => e.Type switch
    {
        ChangeEventType.IssueClosed => RowAnimation.IssueClosed,
        ChangeEventType.IssueCreated or ChangeEventType.IssueReopened => RowAnimation.IssueOpened,
        ChangeEventType.BuildFailed => RowAnimation.BuildFailed,
        _ => null,
    };

    private void Apply(RepositoryRuntimeState state)
    {
        var projection = _projector.ProjectRow(state, _controller.Settings.DailyCountMode);
        var row = Rows.FirstOrDefault(r => r.Id == state.Id);
        if (row is null)
        {
            row = new RepositoryRowViewModel(state.Id);
            Rows.Add(row);
        }

        row.Update(projection, _time.GetUtcNow());
        SortRows();
        UpdateFooter();
    }

    /// <summary>Keeps rows in user-defined order (SortOrder can change in Settings); Move keeps row instances.</summary>
    private void SortRows()
    {
        if (_reordering)
        {
            return;
        }

        var order = _hub.States.Select(s => s.Id).ToList();
        var sorted = Rows.OrderBy(r => order.IndexOf(r.Id) is var i && i >= 0 ? i : int.MaxValue).ToList();
        for (var target = 0; target < sorted.Count; target++)
        {
            var current = Rows.IndexOf(sorted[target]);
            if (current != target)
            {
                Rows.Move(current, target);
            }
        }
    }

    private void UpdateFreshness()
    {
        // Re-project instead of only refreshing texts: "last 24 h" counters age out and "today" rolls over at
        // midnight without any new event (issue #9).
        foreach (var state in _hub.States)
        {
            Apply(state);
        }

        var now = _time.GetUtcNow();
        foreach (var row in Rows)
        {
            row.UpdateFreshness(now);
        }

        UpdateFooter();
    }

    private void OnSettingsChanged(object? sender, Application.Settings.AppSettings settings) => _ui.Post(UpdateFreshness);

    private void UpdateFooter()
    {
        IsEmpty = Rows.Count == 0;
        IsAnyRefreshing = Rows.Any(r => r.IsRefreshing);
        var last = Rows.Select(r => r.LastSuccessAt).Where(t => t is not null).DefaultIfEmpty(null).Max();
        var count = $"{Rows.Count} {(Rows.Count == 1 ? "repository" : "repositories")}";
        FooterText = IsEmpty
            ? "No repositories configured"
            : last is null ? $"{count} · not updated yet" : $"{count} · updated {Format.Age(last, _time.GetUtcNow())}";
    }
}
