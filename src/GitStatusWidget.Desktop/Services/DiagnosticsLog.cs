using System.Collections.ObjectModel;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Synchronization;

namespace GitStatusWidget.Desktop.Services;

/// <summary>A failed refresh, as shown in diagnostics (sanitized message only).</summary>
public sealed record DiagnosticsError(DateTimeOffset At, string Repository, ProviderFailureKind Kind, string Message);

/// <summary>Collects the most recent refresh failures across repositories (architecture §22: "sanitized recent errors").</summary>
public sealed class DiagnosticsLog : IDisposable
{
    public const int Capacity = 50;

    private readonly IRepositoryStatusHub _hub;
    private readonly IUiDispatcher _ui;
    private readonly Dictionary<Guid, DateTimeOffset?> _lastRecorded = [];

    public DiagnosticsLog(IRepositoryStatusHub hub, IUiDispatcher ui)
    {
        _hub = hub;
        _ui = ui;
        _hub.StateChanged += OnStateChanged;
    }

    public ObservableCollection<DiagnosticsError> Errors { get; } = [];

    public void Dispose() => _hub.StateChanged -= OnStateChanged;

    private void OnStateChanged(object? sender, RepositoryRuntimeState state)
    {
        if (state.LastFailure is not { } kind || state.LastError is null || state.LastAttemptAt is not { } at)
        {
            return;
        }

        lock (_lastRecorded)
        {
            if (_lastRecorded.TryGetValue(state.Id, out var previous) && previous == at)
            {
                return;
            }

            _lastRecorded[state.Id] = at;
        }

        var entry = new DiagnosticsError(at, state.Configuration.EffectiveDisplayName, kind, state.LastError);
        _ui.Post(() =>
        {
            Errors.Insert(0, entry);
            while (Errors.Count > Capacity)
            {
                Errors.RemoveAt(Errors.Count - 1);
            }
        });
    }
}
