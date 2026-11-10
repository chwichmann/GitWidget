using GitStatusWidget.Application.Persistence;
using GitStatusWidget.Application.Platform;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;

namespace GitStatusWidget.Desktop.Services;

/// <summary>Single place that persists settings, applies them to the engine and mirrors OS autostart.</summary>
public sealed class SettingsService
{
    private readonly ISettingsStore _store;
    private readonly ISynchronizationController _controller;
    private readonly IAutostartService _autostart;

    public SettingsService(ISettingsStore store, ISynchronizationController controller, IAutostartService autostart)
    {
        _store = store;
        _controller = controller;
        _autostart = autostart;
    }

    public event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>
    /// Raised while the transparency sliders in Settings are moved (not saved yet), and with <see langword="null"/>
    /// when the preview ends (issue #15).
    /// </summary>
    public event EventHandler<WidgetTransparencyPreview?>? WidgetTransparencyPreviewChanged;

    public AppSettings Current => _controller.Settings;

    /// <summary>Shows unsaved transparency values in the widget.</summary>
    public void PreviewWidgetTransparency(WidgetTransparencyPreview preview) => WidgetTransparencyPreviewChanged?.Invoke(this, preview);

    /// <summary>Ends the preview; the widget returns to the saved values.</summary>
    public void EndWidgetTransparencyPreview() => WidgetTransparencyPreviewChanged?.Invoke(this, null);

    public bool AutostartSupported => _autostart.IsSupported;

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var previous = _controller.Settings;
        await _store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        await _controller.ApplySettingsAsync(settings, cancellationToken).ConfigureAwait(false);
        if (_autostart.IsSupported && settings.StartAtLogin != previous.StartAtLogin)
        {
            await _autostart.SetEnabledAsync(settings.StartAtLogin, cancellationToken).ConfigureAwait(false);
        }

        SettingsChanged?.Invoke(this, settings);
    }

    /// <summary>
    /// Makes the start-at-login setting follow the OS registration (the user may have disabled the entry in the
    /// OS startup settings).
    /// </summary>
    public async Task SyncAutostartAsync(CancellationToken cancellationToken)
    {
        if (!_autostart.IsSupported)
        {
            return;
        }

        var registered = await _autostart.IsEnabledAsync(cancellationToken).ConfigureAwait(false);
        var current = _controller.Settings;
        if (registered != current.StartAtLogin)
        {
            var updated = current with { StartAtLogin = registered };
            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            await _controller.ApplySettingsAsync(updated, cancellationToken).ConfigureAwait(false);
            SettingsChanged?.Invoke(this, updated);
        }
    }

    /// <summary>Persists a new order of the given repositories (widget/dashboard drag &amp; drop).</summary>
    public Task ReorderAsync(IReadOnlyList<Guid> order, CancellationToken cancellationToken) =>
        UpdateAsync(s => s with { Repositories = Reorder(s.Repositories, order) }, cancellationToken);

    /// <summary>
    /// Puts the repositories listed in <paramref name="order"/> into that order, using the slots they already
    /// occupy, so repositories not listed (for example disabled ones) keep their position. Renumbers SortOrder.
    /// </summary>
    internal static IReadOnlyList<RepositoryConfiguration> Reorder(IReadOnlyList<RepositoryConfiguration> repositories, IReadOnlyList<Guid> order)
    {
        var sorted = repositories.OrderBy(r => r.SortOrder).ToList();
        var byId = sorted.ToDictionary(r => r.Id);
        var moving = order.Where(byId.ContainsKey).Distinct().Select(id => byId[id]).ToList();
        var movingIds = moving.Select(r => r.Id).ToHashSet();
        var queue = new Queue<RepositoryConfiguration>(moving);
        var result = sorted.Select(r => movingIds.Contains(r.Id) ? queue.Dequeue() : r).ToList();
        return result.Select((r, index) => r with { SortOrder = index }).ToList();
    }

    public Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return SaveAsync(change(_controller.Settings), cancellationToken);
    }
}

/// <summary>Unsaved widget transparency values being tried in Settings (issue #15).</summary>
/// <param name="IdleTransparency">Transparency while the mouse is elsewhere, in percent.</param>
/// <param name="HoverTransparency">Transparency while the mouse is over the widget, in percent.</param>
/// <param name="ShowHover">The mouse-over value is the one being adjusted and should be shown now.</param>
public sealed record WidgetTransparencyPreview(int IdleTransparency, int HoverTransparency, bool ShowHover);
