using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Domain.Health;
using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Desktop.ViewModels;

/// <summary>Navigation entry of the dashboard's repository selector.</summary>
public sealed partial class RepositoryNavItem : ObservableObject
{
    public RepositoryNavItem(Guid id) => Id = id;

    public Guid Id { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Badge { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasProblem { get; set; }
}

/// <summary>Expanded dashboard (architecture §16.3). Uses the same projections as the widget.</summary>
public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IRepositoryStatusHub _hub;
    private readonly DashboardProjector _projector;
    private readonly ISynchronizationController _controller;
    private readonly IUiDispatcher _ui;
    private readonly IAppNavigator _navigator;
    private readonly ActivityFeed _feed;
    private readonly TimeProvider _time;
    private string? _signature;
    private readonly SettingsService? _settings;
    private readonly ITimer _periodTimer;
    private bool _reordering;

    public DashboardViewModel(
        IRepositoryStatusHub hub,
        DashboardProjector projector,
        ISynchronizationController controller,
        IUiDispatcher ui,
        IAppNavigator navigator,
        ActivityFeed feed,
        TimeProvider time,
        SettingsService? settings = null)
    {
        _settings = settings;
        if (_settings is not null)
        {
            _settings.SettingsChanged += OnSettingsChanged;
        }

        _hub = hub;
        _projector = projector;
        _controller = controller;
        _ui = ui;
        _navigator = navigator;
        _feed = feed;
        _time = time;
        // Counters for "today" / "last 24 h" change with time alone (issue #9).
        _periodTimer = time.CreateTimer(_ => _ui.Post(Rebuild), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        _hub.StateChanged += OnStateChanged;
        _hub.RepositoryRemoved += OnRemoved;
        _feed.Items.CollectionChanged += OnFeedChanged;
        foreach (var state in _hub.States)
        {
            UpsertNav(state);
        }

        SelectedRepository = Repositories.FirstOrDefault();
    }

    public ObservableCollection<RepositoryNavItem> Repositories { get; } = [];

    public ObservableCollection<ActivityProjection> Activity { get; } = [];

    [ObservableProperty]
    public partial RepositoryNavItem? SelectedRepository { get; set; }

    [ObservableProperty]
    public partial DashboardProjection? Projection { get; set; }

    [ObservableProperty]
    public partial bool HasRepositories { get; set; }

    [ObservableProperty]
    public partial bool HasChangeRequests { get; set; }

    [ObservableProperty]
    public partial bool HasBuilds { get; set; }

    [ObservableProperty]
    public partial bool HasActivity { get; set; }

    [ObservableProperty]
    public partial bool HasProblem { get; set; }
    /// <summary>The repository is blocked by a login gateway; offer the User-Agent suggestion (issue #10).</summary>
    [ObservableProperty]
    public partial bool CanSuggestUserAgent { get; set; }

    [ObservableProperty]
    public partial string FreshnessText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ChangeRequestTitle { get; set; } = "Open pull requests";

    [ObservableProperty]
    public partial string BuildTitle { get; set; } = "Recent workflow runs";

    [ObservableProperty]
    public partial string ProviderName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    public void Select(Guid? repositoryId)
    {
        if (repositoryId is { } id && Repositories.FirstOrDefault(r => r.Id == id) is { } item)
        {
            SelectedRepository = item;
        }
        else
        {
            SelectedRepository ??= Repositories.FirstOrDefault();
        }
    }

    [RelayCommand]
    private void Refresh() => _controller.RequestRefresh(SelectedRepository?.Id);

    [RelayCommand]
    private void OpenUrl(Uri? url) => _navigator.OpenInBrowser(url);

    [RelayCommand]
    private void OpenDiagnostics() => _navigator.ShowDiagnostics();

    [RelayCommand]
    private void UseSuggestedUserAgent() => _navigator.ShowSettings(SelectedRepository?.Id, suggestUserAgent: true);

    [RelayCommand]
    private void OpenSettings() => _navigator.ShowSettings(SelectedRepository?.Id, addNew: SelectedRepository is null);

    /// <summary>Starts a drag reorder of the repository list.</summary>
    public void BeginReorder() => _reordering = true;

    /// <summary>Moves a repository in the selector and keeps the selection.</summary>
    public void MoveRepository(int from, int to)
    {
        if (from < 0 || from >= Repositories.Count || to < 0 || to >= Repositories.Count || from == to)
        {
            return;
        }

        var selected = SelectedRepository;
        var wasReordering = _reordering;
        _reordering = true;
        Repositories.Move(from, to);
        SelectedRepository = null;
        SelectedRepository = selected;
        _reordering = wasReordering;
    }

    /// <summary>Persists the current selector order as the repository order.</summary>
    public async Task CommitReorderAsync()
    {
        try
        {
            if (_settings is not null)
            {
                await _settings.ReorderAsync(Repositories.Select(r => r.Id).ToList(), CancellationToken.None).ConfigureAwait(true);
            }
        }
        finally
        {
            _reordering = false;
            SortNav();
        }
    }

    public void Dispose()
    {
        _hub.StateChanged -= OnStateChanged;
        _hub.RepositoryRemoved -= OnRemoved;
        _feed.Items.CollectionChanged -= OnFeedChanged;
        if (_settings is not null)
        {
            _settings.SettingsChanged -= OnSettingsChanged;
        }

        _periodTimer.Dispose();
    }

    private void OnSettingsChanged(object? sender, Application.Settings.AppSettings settings) => _ui.Post(Rebuild);

    partial void OnSelectedRepositoryChanged(RepositoryNavItem? value)
    {
        if (_reordering && value is null)
        {
            return; // The list control clears its selection while items move; the selection is restored right after.
        }

        _signature = null;
        Rebuild();
        RebuildActivity();
    }

    private void OnStateChanged(object? sender, RepositoryRuntimeState state) => _ui.Post(() =>
    {
        UpsertNav(state);
        SelectedRepository ??= Repositories.FirstOrDefault();
        if (SelectedRepository?.Id == state.Id)
        {
            Rebuild();
        }
    });

    private void OnRemoved(object? sender, Guid id) => _ui.Post(() =>
    {
        var item = Repositories.FirstOrDefault(r => r.Id == id);
        if (item is not null)
        {
            Repositories.Remove(item);
        }

        HasRepositories = Repositories.Count > 0;
        if (SelectedRepository?.Id == id)
        {
            SelectedRepository = Repositories.FirstOrDefault();
        }
    });

    private void OnFeedChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildActivity();

    private void UpsertNav(RepositoryRuntimeState state)
    {
        var item = Repositories.FirstOrDefault(r => r.Id == state.Id);
        if (item is null)
        {
            item = new RepositoryNavItem(state.Id);
            Repositories.Add(item);
        }

        var health = _projector.ProjectRow(state).Health;
        item.Name = state.Configuration.EffectiveDisplayName;
        item.Badge = state.Configuration.Provider == RepositoryProviderKind.GitLab ? "GL" : "GH";
        item.Status = health.Summary;
        item.HasProblem = health.State is not (RepositoryHealthState.Healthy or RepositoryHealthState.Refreshing);
        HasRepositories = Repositories.Count > 0;
        SortNav();
    }

    /// <summary>Keeps the selector in the user-defined repository order (Move keeps instances and selection).</summary>
    private void SortNav()
    {
        if (_reordering)
        {
            return;
        }

        var order = _hub.States.Select(s => s.Id).ToList();
        var sorted = Repositories.OrderBy(r => order.IndexOf(r.Id) is var i && i >= 0 ? i : int.MaxValue).ToList();
        for (var target = 0; target < sorted.Count; target++)
        {
            var current = Repositories.IndexOf(sorted[target]);
            if (current != target)
            {
                MoveRepository(current, target);
            }
        }
    }

    private void Rebuild()
    {
        var state = SelectedRepository is null ? null : _hub.Find(SelectedRepository.Id);
        if (state is null)
        {
            Projection = null;
            HasChangeRequests = HasBuilds = HasProblem = false;
            return;
        }

        var projection = _projector.ProjectDashboard(state, _controller.Settings.DailyCountMode);
        IsRefreshing = projection.Health.IsRefreshing;
        HasProblem = projection.Health.State is not (RepositoryHealthState.Healthy or RepositoryHealthState.Refreshing);
        CanSuggestUserAgent = projection.Health.State == RepositoryHealthState.GatewayBlocked;
        FreshnessText = projection.Health.LastSuccessAt is { } success
            ? $"Updated {Format.Age(success, _time.GetUtcNow())} ({success.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)})"
            : "Not updated yet";

        var signature = Signature(projection);
        if (signature == _signature)
        {
            // Same content: keep list instances (and scroll positions); only health/freshness changed.
            if (Projection is not null)
            {
                Projection = Projection with { Header = projection.Header, Health = projection.Health };
            }

            return;
        }

        _signature = signature;
        Projection = projection;
        HasChangeRequests = projection.OpenChangeRequests.Count > 0;
        HasBuilds = projection.RecentBuilds.Count > 0;
        var gitlab = projection.Header.Provider == RepositoryProviderKind.GitLab;
        ProviderName = gitlab ? "GitLab" : "GitHub";
        ChangeRequestTitle = gitlab ? "Open merge requests" : "Open pull requests";
        BuildTitle = gitlab ? "Recent pipelines" : "Recent workflow runs";
    }

    private void RebuildActivity()
    {
        Activity.Clear();
        foreach (var item in _feed.Items.Where(i => SelectedRepository is null || i.RepositoryId == SelectedRepository.Id).Take(50))
        {
            Activity.Add(item);
        }

        HasActivity = Activity.Count > 0;
    }

    private static string Signature(DashboardProjection p)
    {
        var builder = new StringBuilder();
        builder.Append(p.HasData).Append('|').Append(p.Summary).Append('|').Append(p.CurrentMilestone).Append('|');
        foreach (var m in p.Milestones)
        {
            builder.Append(m).Append(';');
        }

        foreach (var l in p.Labels)
        {
            builder.Append(l).Append(';');
        }

        foreach (var i in p.RecentlyChanged.Concat(p.RecentlyClosed))
        {
            builder.Append(i.Number).Append(':').Append(i.UpdatedAt.UtcTicks).Append(i.State).Append(';');
        }

        foreach (var c in p.OpenChangeRequests)
        {
            builder.Append(c).Append(';');
        }

        foreach (var b in p.RecentBuilds)
        {
            builder.Append(b).Append(';');
        }

        return builder.ToString();
    }
}
