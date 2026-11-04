using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Synchronization;
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

    public WidgetViewModel(
        IRepositoryStatusHub hub,
        DashboardProjector projector,
        ISynchronizationController controller,
        IUiDispatcher ui,
        IAppNavigator navigator,
        TimeProvider time)
    {
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

    [RelayCommand]
    private void Refresh() => _controller.RequestRefresh();

    [RelayCommand]
    private void OpenDashboard(RepositoryRowViewModel? row) => _navigator.ShowDashboard(row?.Id);

    [RelayCommand]
    private void OpenSettings() => _navigator.ShowSettings(addNew: IsEmpty);

    [RelayCommand]
    private void Hide() => _navigator.HideWidget();

    public void Dispose()
    {
        _hub.StateChanged -= OnStateChanged;
        _hub.RepositoryRemoved -= OnRepositoryRemoved;
        _hub.ChangesDetected -= OnChangesDetected;
        _freshnessTimer.Dispose();
    }

    private void OnStateChanged(object? sender, RepositoryRuntimeState state) => _ui.Post(() => Apply(state));

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
        var projection = _projector.ProjectRow(state);
        var row = Rows.FirstOrDefault(r => r.Id == state.Id);
        if (row is null)
        {
            row = new RepositoryRowViewModel(state.Id);
            var index = 0;
            var order = _hub.States.Select(s => s.Id).ToList();
            while (index < Rows.Count && order.IndexOf(Rows[index].Id) < order.IndexOf(state.Id))
            {
                index++;
            }

            Rows.Insert(index, row);
        }

        row.Update(projection, _time.GetUtcNow());
        UpdateFooter();
    }

    private void UpdateFreshness()
    {
        var now = _time.GetUtcNow();
        foreach (var row in Rows)
        {
            row.UpdateFreshness(now);
        }

        UpdateFooter();
    }

    private void UpdateFooter()
    {
        IsEmpty = Rows.Count == 0;
        IsAnyRefreshing = Rows.Any(r => r.IsRefreshing);
        var last = Rows.Select(r => r.LastSuccessAt).Where(t => t is not null).DefaultIfEmpty(null).Max();
        FooterText = IsEmpty
            ? "No repositories configured"
            : $"{Rows.Count} {(Rows.Count == 1 ? "repository" : "repositories")} · updated {Format.Age(last, _time.GetUtcNow())}";
    }
}
