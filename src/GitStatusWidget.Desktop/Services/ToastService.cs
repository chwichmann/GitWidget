using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Domain.Events;

namespace GitStatusWidget.Desktop.Services;

/// <summary>
/// Decides which detected changes become toasts: never catch-up changes, only enabled toasts above the configured
/// priority, and a single summary toast for bursts (architecture §14.3, §16.4).
/// </summary>
public sealed class ToastService : IDisposable
{
    public const int BurstThreshold = 4;

    private readonly IRepositoryStatusHub _hub;
    private readonly ISynchronizationController _controller;
    private readonly IUiDispatcher _ui;
    private readonly Func<IToastPresenter> _presenter;

    public ToastService(IRepositoryStatusHub hub, ISynchronizationController controller, IUiDispatcher ui, Func<IToastPresenter> presenter)
    {
        _hub = hub;
        _controller = controller;
        _ui = ui;
        _presenter = presenter;
        _hub.ChangesDetected += OnChanges;
    }

    public void Dispose() => _hub.ChangesDetected -= OnChanges;

    internal static IReadOnlyList<ToastViewModel> Select(RepositoryChangesDetected changes, string repositoryName, bool enabled, EventPriority minimum)
    {
        if (!enabled || changes.IsCatchUp)
        {
            return [];
        }

        var relevant = changes.Events.Where(e => e.Priority >= minimum).ToList();
        if (relevant.Count == 0)
        {
            return [];
        }

        if (relevant.Count >= BurstThreshold)
        {
            var failed = relevant.Any(e => e.Type == ChangeEventType.BuildFailed);
            return
            [
                new ToastViewModel(repositoryName, $"{relevant.Count} changes", string.Join(" · ", relevant.Take(3).Select(ActivityText.Headline)),
                    failed ? ToastKind.Failure : ToastKind.Info, null),
            ];
        }

        return relevant
            .OrderByDescending(e => e.Priority)
            .Select(e => new ToastViewModel(repositoryName, ActivityText.Headline(e), e.Title, ToastViewModel.KindOf(e.Type), e.WebUrl))
            .ToList();
    }

    private void OnChanges(object? sender, RepositoryChangesDetected changes)
    {
        var settings = _controller.Settings;
        var name = _hub.Find(changes.RepositoryId)?.Configuration.EffectiveDisplayName ?? string.Empty;
        var toasts = Select(changes, name, settings.ToastsEnabled, settings.MinimumToastPriority);
        if (toasts.Count > 0)
        {
            _ui.Post(() =>
            {
                foreach (var toast in toasts)
                {
                    _presenter().Show(toast);
                }
            });
        }
    }
}

/// <summary>Shows toast windows (implemented by the window manager).</summary>
public interface IToastPresenter
{
    void Show(ToastViewModel toast);
}
