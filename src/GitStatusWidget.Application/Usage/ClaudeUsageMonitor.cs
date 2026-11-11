using GitStatusWidget.Application.Synchronization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Application.Usage;

/// <summary>Outcome of the last usage poll.</summary>
public enum ClaudeUsageStatus
{
    /// <summary>The usage footer is switched off; nothing is requested.</summary>
    Disabled,

    /// <summary>Switched on, not polled yet.</summary>
    Pending,

    /// <summary>The usage is current.</summary>
    Ok,

    /// <summary>No usable Claude Code sign-in.</summary>
    SignedOut,

    /// <summary>The endpoint asked to slow down; the last known values are kept.</summary>
    RateLimited,

    /// <summary>The usage could not be read; the last known values are kept.</summary>
    Unavailable,
}

/// <summary>State of the Claude usage display (issue #17).</summary>
/// <param name="Status">Outcome of the last poll.</param>
/// <param name="Snapshot">Last successfully read usage; kept while later polls fail.</param>
/// <param name="CheckedAt">Time of the last poll.</param>
/// <param name="Message">Text for the user when the status is not <see cref="ClaudeUsageStatus.Ok"/>.</param>
public sealed record ClaudeUsageState(ClaudeUsageStatus Status, ClaudeUsageSnapshot? Snapshot, DateTimeOffset? CheckedAt, string? Message);

/// <summary>
/// Polls the Claude usage while the <c>ShowClaudeUsage</c> setting is on: every <see cref="PollInterval"/>, backing
/// off up to <see cref="MaxPollInterval"/> while the endpoint rate limits. Nothing is requested while the setting is
/// off.
/// </summary>
public sealed partial class ClaudeUsageMonitor : BackgroundService
{
    /// <summary>Interval between polls.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    /// <summary>Longest interval between polls while rate limited.</summary>
    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromMinutes(5);

    /// <summary>Shortest time between two requests, so manual refreshes cannot hammer the endpoint.</summary>
    public static readonly TimeSpan MinRequestGap = TimeSpan.FromSeconds(15);

    /// <summary>How often the setting is looked at while the footer is switched off.</summary>
    public static readonly TimeSpan DisabledCheckInterval = TimeSpan.FromSeconds(5);

    private readonly IClaudeUsageSource _source;
    private readonly ISynchronizationController _controller;
    private readonly TimeProvider _time;
    private readonly ILogger<ClaudeUsageMonitor> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncSignal _wake = new();
    private readonly IClaudeUsageHistoryStore? _historyStore;
    private TimeSpan _interval = PollInterval;
    private DateTimeOffset? _lastRequest;
    private DateTimeOffset? _historySavedAt;
    private bool _historyDirty;

    /// <summary>Creates the monitor.</summary>
    /// <param name="source">Usage source.</param>
    /// <param name="controller">Settings source.</param>
    /// <param name="time">Time provider.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="historyStore">Keeps the usage history across restarts; <see langword="null"/> keeps it in memory only.</param>
    public ClaudeUsageMonitor(IClaudeUsageSource source, ISynchronizationController controller, TimeProvider time, ILogger<ClaudeUsageMonitor> logger, IClaudeUsageHistoryStore? historyStore = null)
    {
        _historyStore = historyStore;
        _source = source;
        _controller = controller;
        _time = time;
        _logger = logger;
        State = new ClaudeUsageState(ClaudeUsageStatus.Disabled, null, null, null);
    }

    /// <summary>Raised (on a background thread) when <see cref="State"/> changes.</summary>
    public event EventHandler<ClaudeUsageState>? StateChanged;

    /// <summary>Current state.</summary>
    public ClaudeUsageState State { get; private set; }

    /// <summary>Recent values of every limit, the basis of the pace estimate (issue #18).</summary>
    public ClaudeUsageHistory History { get; } = new();

    /// <summary>How often the history is written to disk at most.</summary>
    public static readonly TimeSpan HistorySaveInterval = TimeSpan.FromMinutes(5);

    /// <summary>Requests a poll now (settings changed, manual refresh).</summary>
    public void RequestRefresh() => _wake.Set();

    /// <summary>Polls once, unless the setting is off or the last request was less than <see cref="MinRequestGap"/> ago.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The new state.</returns>
    public async Task<ClaudeUsageState> CheckAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (!_controller.Settings.ShowClaudeUsage)
            {
                _interval = PollInterval;
                _lastRequest = null;
                Publish(new ClaudeUsageState(ClaudeUsageStatus.Disabled, null, null, null));
                return State;
            }

            if (_lastRequest is { } last && now - last < MinRequestGap)
            {
                return State;
            }

            if (State.Status == ClaudeUsageStatus.Disabled)
            {
                Publish(State with { Status = ClaudeUsageStatus.Pending });
            }

            _lastRequest = now;
            var result = await _source.GetUsageAsync(cancellationToken).ConfigureAwait(false);
            _interval = result is ClaudeUsageResult.RateLimited limited ? NextBackoff(_interval, limited.RetryAfter) : PollInterval;
            if (result is ClaudeUsageResult.Success read)
            {
                // Before publishing, so listeners already find the new sample.
                _historyDirty |= History.Record(read.Snapshot);
            }

            Publish(result switch
            {
                ClaudeUsageResult.Success success => new ClaudeUsageState(ClaudeUsageStatus.Ok, success.Snapshot, now, null),
                ClaudeUsageResult.SignedOut signedOut => State with { Status = ClaudeUsageStatus.SignedOut, CheckedAt = now, Message = signedOut.Reason },
                ClaudeUsageResult.RateLimited => State with { Status = ClaudeUsageStatus.RateLimited, CheckedAt = now, Message = "Rate limited – showing the last known values" },
                ClaudeUsageResult.Unavailable unavailable => State with { Status = ClaudeUsageStatus.Unavailable, CheckedAt = now, Message = unavailable.Reason },
                _ => State,
            });
            return State;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Next poll interval after a rate-limit answer: doubled, at least the requested wait, capped.</summary>
    /// <param name="current">Interval that was in effect.</param>
    /// <param name="retryAfter">Wait requested by the server.</param>
    /// <returns>The interval to wait before the next poll.</returns>
    public static TimeSpan NextBackoff(TimeSpan current, TimeSpan? retryAfter)
    {
        var doubled = TimeSpan.FromTicks(Math.Max(current.Ticks, PollInterval.Ticks) * 2);
        var wanted = retryAfter is { } wait && wait > doubled ? wait : doubled;
        return wanted > MaxPollInterval ? MaxPollInterval : wanted;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await LoadHistoryAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                var state = await CheckAsync(stoppingToken).ConfigureAwait(false);
                if (_historyDirty && (_historySavedAt is not { } saved || _time.GetUtcNow() - saved >= HistorySaveInterval))
                {
                    await SaveHistoryAsync(stoppingToken).ConfigureAwait(false);
                }

                // While switched off only the setting is looked at (no request), so this can be frequent: the
                // settings are loaded shortly after startup and the footer should appear promptly.
                var wait = state.Status == ClaudeUsageStatus.Disabled ? DisabledCheckInterval : _interval;
                await _wake.WaitAsync(wait, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_historyDirty)
        {
            await SaveHistoryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        if (_historyStore is null)
        {
            return;
        }

        try
        {
            History.Import(await _historyStore.LoadAsync(cancellationToken).ConfigureAwait(false), _time.GetUtcNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogHistoryProblem(_logger, "read", ex.Message);
        }
    }

    private async Task SaveHistoryAsync(CancellationToken cancellationToken)
    {
        _historyDirty = false;
        _historySavedAt = _time.GetUtcNow();
        if (_historyStore is null)
        {
            return;
        }

        try
        {
            await _historyStore.SaveAsync(History.Export(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The history is only a convenience: the estimate falls back to the average since the reset.
            LogHistoryProblem(_logger, "written", ex.Message);
        }
    }

    [LoggerMessage(EventId = 2302, Level = LogLevel.Warning, Message = "The Claude usage history could not be {Action}: {Reason}")]
    private static partial void LogHistoryProblem(ILogger logger, string action, string reason);

    private void Publish(ClaudeUsageState state)
    {
        if (state == State)
        {
            return;
        }

        var statusChanged = state.Status != State.Status;
        State = state;
        if (statusChanged && state.Status is ClaudeUsageStatus.SignedOut or ClaudeUsageStatus.RateLimited or ClaudeUsageStatus.Unavailable)
        {
            LogProblem(_logger, state.Status, state.Message ?? "unknown");
        }

        else if (statusChanged)
        {
            LogStatus(_logger, state.Status);
        }

        StateChanged?.Invoke(this, state);
    }

    [LoggerMessage(EventId = 2301, Level = LogLevel.Debug, Message = "Claude usage {Status}")]
    private static partial void LogStatus(ILogger logger, ClaudeUsageStatus status);

    [LoggerMessage(EventId = 2300, Level = LogLevel.Information, Message = "Claude usage {Status}: {Reason}")]
    private static partial void LogProblem(ILogger logger, ClaudeUsageStatus status, string reason);
}
