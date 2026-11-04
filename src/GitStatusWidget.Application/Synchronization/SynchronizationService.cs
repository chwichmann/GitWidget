using GitStatusWidget.Application.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Application.Synchronization;

/// <summary>
/// Background loop driving <see cref="SynchronizationEngine"/>: loads settings, starts due refreshes, sleeps until
/// the next due time or a wake signal, detects resume from standby, and shuts down gracefully (architecture §25, §26).
/// </summary>
public sealed partial class SynchronizationService : BackgroundService
{
    private static readonly TimeSpan MaxSleep = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinSleep = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ResumeGap = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly SynchronizationEngine _engine;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<SynchronizationService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="engine">Engine.</param>
    /// <param name="settings">Settings store.</param>
    /// <param name="time">Time provider.</param>
    /// <param name="logger">Logger.</param>
    public SynchronizationService(SynchronizationEngine engine, ISettingsStore settings, TimeProvider time, ILogger<SynchronizationService> logger)
    {
        _engine = engine;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var settings = await _settings.LoadAsync(stoppingToken).ConfigureAwait(false);
            await _engine.ApplySettingsAsync(settings, stoppingToken).ConfigureAwait(false);
        }
        catch (UnsupportedSchemaVersionException ex)
        {
            LogSettingsUnsupported(_logger, ex);
            return;
        }

        var lastWake = _time.GetUtcNow();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var next = _engine.StartDueRefreshes(stoppingToken);
                var now = _time.GetUtcNow();
                var sleep = next is { } due ? due - now : MaxSleep;
                sleep = sleep < MinSleep ? MinSleep : sleep > MaxSleep ? MaxSleep : sleep;

                await _engine.Wake.WaitAsync(sleep, _time, stoppingToken).ConfigureAwait(false);

                // Timers do not fire during standby: a wall-clock gap far beyond the requested sleep means resume.
                var woke = _time.GetUtcNow();
                if (woke - now > sleep + ResumeGap || woke < lastWake)
                {
                    _engine.NotifyResume();
                }

                lastWake = woke;
                _engine.UpdateStaleness();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await _engine.StopAsync(ShutdownTimeout).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 2100, Level = LogLevel.Error, Message = "Settings were written by a newer version; synchronization is disabled")]
    private static partial void LogSettingsUnsupported(ILogger logger, Exception exception);
}
