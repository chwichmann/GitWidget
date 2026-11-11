using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Application.Updates;
using GitStatusWidget.Application.Usage;
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
    private readonly ClaudeUsageMonitor? _usageMonitor;
    private bool _reordering;

    public WidgetViewModel(
        IRepositoryStatusHub hub,
        DashboardProjector projector,
        ISynchronizationController controller,
        IUiDispatcher ui,
        IAppNavigator navigator,
        TimeProvider time,
        SettingsService? settings = null,
        UpdateChecker? updates = null,
        ClaudeUsageMonitor? usage = null)
    {
        _usageMonitor = usage;
        Usage = new ClaudeUsageViewModel(usage, controller, ui, navigator, time);
        _settings = settings;
        if (_settings is not null)
        {
            _settings.SettingsChanged += OnSettingsChanged;
            _settings.WidgetTransparencyPreviewChanged += OnTransparencyPreviewChanged;
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
        ApplyTransparency(_controller.Settings);
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

    /// <summary>Claude usage footer and its full display (issue #17).</summary>
    public ClaudeUsageViewModel Usage { get; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [ObservableProperty]
    public partial string FooterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsAnyRefreshing { get; set; }

    [ObservableProperty]
    public partial bool AlwaysOnTop { get; set; }

    /// <summary>Opacity of the widget while the mouse is elsewhere (issue #15).</summary>
    [ObservableProperty]
    public partial double IdleOpacity { get; set; } = 1;

    /// <summary>Opacity of the widget while the mouse is over it; never below <see cref="IdleOpacity"/>.</summary>
    [ObservableProperty]
    public partial double HoverOpacity { get; set; } = 1;

    /// <summary>
    /// Opacity pinned by the transparency preview in Settings, regardless of the mouse; <see langword="null"/> when no
    /// preview is running. The window clears it when the mouse enters the widget.
    /// </summary>
    [ObservableProperty]
    public partial double? PreviewOpacity { get; set; }

    /// <summary>The rows are sample data because no repository is configured.</summary>
    [ObservableProperty]
    public partial bool IsDemo { get; set; }

    /// <summary>A newer GitWidget release exists (issue #7).</summary>
    [ObservableProperty]
    public partial bool UpdateAvailable { get; set; }

    [ObservableProperty]
    public partial string UpdateToolTip { get; set; } = string.Empty;

    [RelayCommand]
    private void Refresh()
    {
        _controller.RequestRefresh();
        _usageMonitor?.RequestRefresh();
    }

    [RelayCommand]
    private void OpenDashboard(RepositoryRowViewModel? row)
    {
        if (row?.IsDemo == true)
        {
            // Demo rows have no dashboard; lead to adding a real repository instead.
            _navigator.ShowSettings(addNew: true);
            return;
        }

        _navigator.ShowDashboard(row?.Id);
    }

    [RelayCommand]
    private void OpenSettings() => _navigator.ShowSettings(addNew: IsEmpty);

    [RelayCommand]
    private void Hide() => _navigator.HideWidget();

    [RelayCommand]
    private void OpenUpdate() => _navigator.ShowUpdate();

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
            _settings.WidgetTransparencyPreviewChanged -= OnTransparencyPreviewChanged;
        }

        Usage.Dispose();
        _freshnessTimer.Dispose();
    }

    private void OnStateChanged(object? sender, RepositoryRuntimeState state) => _ui.Post(() => Apply(state));

    private void OnUpdateStateChanged(object? sender, UpdateState state) => _ui.Post(() => ApplyUpdate(state));

    private void ApplyUpdate(UpdateState state)
    {
        var latest = state.Status == UpdateStatus.Available ? state.Latest : null;
        UpdateAvailable = latest is not null;
        UpdateToolTip = latest is null
            ? string.Empty
            : $"GitWidget {latest.Version} is available (running {state.CurrentVersion}) – click to update";
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
        RemoveDemoRows();
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

        Usage.UpdateFreshness();
        UpdateFooter();
    }

    private void OnSettingsChanged(object? sender, Application.Settings.AppSettings settings) => _ui.Post(() =>
    {
        PreviewOpacity = null;
        ApplyTransparency(settings);
        UpdateFreshness();

        // The usage footer may have been switched on or off: let the monitor notice now instead of at its next poll.
        _usageMonitor?.RequestRefresh();
    });

    private void OnTransparencyPreviewChanged(object? sender, WidgetTransparencyPreview? preview) => _ui.Post(() =>
    {
        if (preview is null)
        {
            PreviewOpacity = null;
            ApplyTransparency(_controller.Settings);
            return;
        }

        ApplyTransparency(_controller.Settings with
        {
            WidgetIdleTransparency = preview.IdleTransparency,
            WidgetHoverTransparency = preview.HoverTransparency,
        });
        PreviewOpacity = preview.ShowHover ? HoverOpacity : IdleOpacity;
    });

    /// <summary>Shows sample rows while no repository is configured, and removes them once one exists.</summary>
    private void UpdateDemoRows()
    {
        if (_hub.States.Count > 0)
        {
            RemoveDemoRows();
            return;
        }

        var now = _time.GetUtcNow();
        var period = _controller.Settings.DailyCountMode == Application.Settings.DailyCountMode.Last24Hours
            ? DashboardProjector.Last24HoursLabel
            : DashboardProjector.TodayLabel;
        foreach (var projection in DemoData.Rows(now, period))
        {
            var row = Rows.FirstOrDefault(r => r.Id == projection.Header.RepositoryId);
            if (row is null)
            {
                row = new RepositoryRowViewModel(projection.Header.RepositoryId);
                Rows.Add(row);
            }

            row.Update(projection, now);
        }
    }

    private void RemoveDemoRows()
    {
        foreach (var demo in Rows.Where(r => r.IsDemo).ToList())
        {
            Rows.Remove(demo);
        }
    }

    private void ApplyTransparency(Application.Settings.AppSettings settings)
    {
        var normalized = settings.Normalize();
        IdleOpacity = 1 - (normalized.WidgetIdleTransparency / 100.0);
        HoverOpacity = 1 - (normalized.WidgetHoverTransparency / 100.0);
    }

    private void UpdateFooter()
    {
        UpdateDemoRows();
        IsDemo = Rows.Any(r => r.IsDemo);
        IsEmpty = IsDemo || Rows.Count == 0;
        IsAnyRefreshing = Rows.Any(r => r.IsRefreshing);
        var last = Rows.Select(r => r.LastSuccessAt).Where(t => t is not null).DefaultIfEmpty(null).Max();
        var count = $"{Rows.Count} {(Rows.Count == 1 ? "repository" : "repositories")}";
        FooterText = IsEmpty
            ? "No repositories configured"
            : last is null ? $"{count} · not updated yet" : $"{count} · updated {Format.Age(last, _time.GetUtcNow())}";
    }
}
