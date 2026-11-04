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

    public AppSettings Current => _controller.Settings;

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

    public Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return SaveAsync(change(_controller.Settings), cancellationToken);
    }
}
