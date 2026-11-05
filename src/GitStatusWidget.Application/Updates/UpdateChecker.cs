using GitStatusWidget.Application.Synchronization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Application.Updates;

/// <summary>Outcome of the last update check.</summary>
public enum UpdateStatus
{
    /// <summary>Not checked yet or checks disabled.</summary>
    Unknown,

    /// <summary>No newer release.</summary>
    UpToDate,

    /// <summary>A newer release exists.</summary>
    Available,

    /// <summary>The release feed could not be read.</summary>
    Unavailable,
}

/// <summary>State of update checking (issue #7).</summary>
/// <param name="Status">Outcome.</param>
/// <param name="CurrentVersion">Running version.</param>
/// <param name="Latest">Newest applicable release when <see cref="UpdateStatus.Available"/>.</param>
/// <param name="CheckedAt">Time of the last check.</param>
/// <param name="Message">Sanitized message for diagnostics.</param>
public sealed record UpdateState(UpdateStatus Status, string CurrentVersion, ReleaseInfo? Latest, DateTimeOffset? CheckedAt, string? Message);

/// <summary>
/// Checks for newer GitWidget releases: a while after startup, then once a day, and on demand. Only reads the release
/// list; nothing is downloaded or installed. Honors the <c>CheckForUpdates</c> and <c>IncludePrereleaseUpdates</c>
/// settings.
/// </summary>
public sealed partial class UpdateChecker : BackgroundService
{
    /// <summary>Interval between automatic checks.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly IReleaseFeed _feed;
    private readonly ISynchronizationController _controller;
    private readonly TimeProvider _time;
    private readonly ILogger<UpdateChecker> _logger;
    private readonly SemanticVersion? _current;
    private readonly TimeSpan _initialDelay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncSignal _wake = new();

    /// <summary>Creates the checker.</summary>
    /// <param name="feed">Release feed.</param>
    /// <param name="controller">Settings source.</param>
    /// <param name="time">Time provider.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="currentVersion">Running version (informational version).</param>
    /// <param name="initialDelay">Delay before the first automatic check.</param>
    public UpdateChecker(IReleaseFeed feed, ISynchronizationController controller, TimeProvider time, ILogger<UpdateChecker> logger, string currentVersion, TimeSpan initialDelay)
    {
        _feed = feed;
        _controller = controller;
        _time = time;
        _logger = logger;
        _initialDelay = initialDelay;
        _current = SemanticVersion.TryParse(currentVersion, out var parsed) ? parsed : null;
        State = new UpdateState(UpdateStatus.Unknown, _current?.ToString() ?? currentVersion, null, null, null);
    }

    /// <summary>Raised (on a background thread) when <see cref="State"/> changes.</summary>
    public event EventHandler<UpdateState>? StateChanged;

    /// <summary>Current state.</summary>
    public UpdateState State { get; private set; }

    /// <summary>Requests an immediate check (diagnostics "Check now").</summary>
    public void RequestCheck() => _wake.Set();

    /// <summary>Checks once.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The new state.</returns>
    public async Task<UpdateState> CheckAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _controller.Settings;
            var now = _time.GetUtcNow();
            UpdateState state;
            if (!settings.CheckForUpdates)
            {
                state = State with { Status = UpdateStatus.Unknown, Latest = null, Message = "Update checks are disabled." };
            }
            else if (_current is null)
            {
                state = State with { Status = UpdateStatus.Unavailable, CheckedAt = now, Message = "The running version is not a release version." };
            }
            else
            {
                var result = await _feed.GetReleasesAsync(cancellationToken).ConfigureAwait(false);
                state = result switch
                {
                    ReleaseFeedResult.Success success => Evaluate(success.Releases, settings.IncludePrereleaseUpdates, now),
                    ReleaseFeedResult.Unavailable unavailable => State with { Status = UpdateStatus.Unavailable, Latest = null, CheckedAt = now, Message = unavailable.Reason },
                    _ => State,
                };
            }

            Publish(state);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    /// <summary>Selects the newest applicable release newer than <paramref name="current"/>.</summary>
    /// <param name="releases">Published releases.</param>
    /// <param name="current">Running version.</param>
    /// <param name="includePrereleases">Whether pre-releases are offered.</param>
    /// <returns>The release to offer, or <see langword="null"/>.</returns>
    public static ReleaseInfo? Newest(IEnumerable<ReleaseInfo> releases, SemanticVersion current, bool includePrereleases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(current);
        return releases
            .Where(r => includePrereleases || (!r.IsPrerelease && !r.Version.IsPrerelease))
            .Where(r => r.Version > current)
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _wake.WaitAsync(_initialDelay, _time, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
                await _wake.WaitAsync(CheckInterval, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private UpdateState Evaluate(IReadOnlyList<ReleaseInfo> releases, bool includePrereleases, DateTimeOffset now)
    {
        var newest = Newest(releases, _current!, includePrereleases);
        return newest is null
            ? State with { Status = UpdateStatus.UpToDate, Latest = null, CheckedAt = now, Message = null }
            : State with { Status = UpdateStatus.Available, Latest = newest, CheckedAt = now, Message = null };
    }

    private void Publish(UpdateState state)
    {
        var changed = state != State;
        State = state;
        if (state.Status == UpdateStatus.Available && changed)
        {
            LogAvailable(_logger, state.Latest!.Version.ToString(), state.CurrentVersion);
        }
        else if (state.Status == UpdateStatus.Unavailable && changed)
        {
            LogUnavailable(_logger, state.Message ?? "unknown");
        }

        if (changed)
        {
            StateChanged?.Invoke(this, state);
        }
    }

    [LoggerMessage(EventId = 2200, Level = LogLevel.Information, Message = "GitWidget {Version} is available (running {Current})")]
    private static partial void LogAvailable(ILogger logger, string version, string current);

    [LoggerMessage(EventId = 2201, Level = LogLevel.Information, Message = "Update check unavailable: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string reason);
}
