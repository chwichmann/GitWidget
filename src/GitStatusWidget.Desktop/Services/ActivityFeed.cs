using System.Collections.ObjectModel;
using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Synchronization;

namespace GitStatusWidget.Desktop.Services;

/// <summary>In-memory activity feed across all repositories (newest first, bounded).</summary>
public sealed class ActivityFeed : IDisposable
{
    public const int Capacity = 200;

    private readonly IRepositoryStatusHub _hub;
    private readonly IUiDispatcher _ui;

    public ActivityFeed(IRepositoryStatusHub hub, IUiDispatcher ui)
    {
        _hub = hub;
        _ui = ui;
        _hub.ChangesDetected += OnChanges;
    }

    public ObservableCollection<ActivityProjection> Items { get; } = [];

    public void Dispose() => _hub.ChangesDetected -= OnChanges;

    private void OnChanges(object? sender, RepositoryChangesDetected changes)
    {
        var name = _hub.Find(changes.RepositoryId)?.Configuration.EffectiveDisplayName ?? string.Empty;
        var entries = changes.Events
            .OrderBy(e => e.OccurredAt)
            .Select(e => ActivityText.Describe(changes.RepositoryId, name, e))
            .ToList();
        _ui.Post(() =>
        {
            foreach (var entry in entries)
            {
                Items.Insert(0, entry);
            }

            while (Items.Count > Capacity)
            {
                Items.RemoveAt(Items.Count - 1);
            }
        });
    }
}
