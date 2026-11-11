using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Application.Usage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GitStatusWidget.Application.Tests.Usage;

/// <summary>Issue #17: polling of the Claude usage.</summary>
public sealed class ClaudeUsageMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);

    private static ClaudeUsageSnapshot Snapshot(double weekly = 50) =>
        new([new ClaudeUsageLimit(ClaudeUsageLimitKind.Weekly, null, weekly, Now.AddDays(5))], "max", Now);

    private ClaudeUsageMonitor Create(FakeSource source, bool enabled = true) =>
        new(source, new Controller(new AppSettings { ShowClaudeUsage = enabled }), _time, NullLogger<ClaudeUsageMonitor>.Instance);

    [Fact]
    public async Task Nothing_is_requested_while_the_setting_is_off()
    {
        var source = new FakeSource(new ClaudeUsageResult.Success(Snapshot()));
        using var monitor = Create(source, enabled: false);

        var state = await monitor.CheckAsync(CancellationToken.None);

        state.Status.Should().Be(ClaudeUsageStatus.Disabled);
        source.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Usage_is_published_when_read()
    {
        var source = new FakeSource(new ClaudeUsageResult.Success(Snapshot(50)));
        using var monitor = Create(source);
        var raised = new List<ClaudeUsageStatus>();
        monitor.StateChanged += (_, s) => raised.Add(s.Status);

        var state = await monitor.CheckAsync(CancellationToken.None);

        state.Status.Should().Be(ClaudeUsageStatus.Ok);
        state.Snapshot!.Limits.Single().Percent.Should().Be(50);
        state.CheckedAt.Should().Be(Now);
        raised.Should().Equal(ClaudeUsageStatus.Pending, ClaudeUsageStatus.Ok);
    }

    [Fact]
    public async Task Last_known_values_are_kept_when_a_later_poll_fails()
    {
        var source = new FakeSource(new ClaudeUsageResult.Success(Snapshot(50)));
        using var monitor = Create(source);
        await monitor.CheckAsync(CancellationToken.None);

        source.Result = new ClaudeUsageResult.SignedOut("Claude Code sign-in expired");
        _time.Advance(ClaudeUsageMonitor.PollInterval);
        var signedOut = await monitor.CheckAsync(CancellationToken.None);
        source.Result = new ClaudeUsageResult.Unavailable("no connection");
        _time.Advance(ClaudeUsageMonitor.PollInterval);
        var unavailable = await monitor.CheckAsync(CancellationToken.None);

        signedOut.Status.Should().Be(ClaudeUsageStatus.SignedOut);
        signedOut.Message.Should().Be("Claude Code sign-in expired");
        signedOut.Snapshot!.Limits.Single().Percent.Should().Be(50);
        unavailable.Status.Should().Be(ClaudeUsageStatus.Unavailable);
        unavailable.Snapshot.Should().NotBeNull();
    }

    [Fact]
    public async Task Requests_closer_than_the_minimum_gap_are_skipped()
    {
        var source = new FakeSource(new ClaudeUsageResult.Success(Snapshot()));
        using var monitor = Create(source);

        await monitor.CheckAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(5));
        await monitor.CheckAsync(CancellationToken.None);
        _time.Advance(ClaudeUsageMonitor.MinRequestGap);
        await monitor.CheckAsync(CancellationToken.None);

        source.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Switching_the_setting_off_clears_the_usage()
    {
        var source = new FakeSource(new ClaudeUsageResult.Success(Snapshot()));
        var controller = new Controller(new AppSettings { ShowClaudeUsage = true });
        using var monitor = new ClaudeUsageMonitor(source, controller, _time, NullLogger<ClaudeUsageMonitor>.Instance);
        await monitor.CheckAsync(CancellationToken.None);

        controller.Settings = new AppSettings { ShowClaudeUsage = false };
        var state = await monitor.CheckAsync(CancellationToken.None);

        state.Should().Be(new ClaudeUsageState(ClaudeUsageStatus.Disabled, null, null, null));
    }

    [Fact]
    public void Rate_limit_backoff_doubles_honors_retry_after_and_is_capped()
    {
        ClaudeUsageMonitor.NextBackoff(ClaudeUsageMonitor.PollInterval, null).Should().Be(TimeSpan.FromMinutes(2));
        ClaudeUsageMonitor.NextBackoff(TimeSpan.FromMinutes(2), null).Should().Be(TimeSpan.FromMinutes(4));
        ClaudeUsageMonitor.NextBackoff(TimeSpan.FromMinutes(4), null).Should().Be(ClaudeUsageMonitor.MaxPollInterval);
        ClaudeUsageMonitor.NextBackoff(ClaudeUsageMonitor.PollInterval, TimeSpan.FromMinutes(3)).Should().Be(TimeSpan.FromMinutes(3));
        ClaudeUsageMonitor.NextBackoff(ClaudeUsageMonitor.PollInterval, TimeSpan.FromHours(1)).Should().Be(ClaudeUsageMonitor.MaxPollInterval);
    }

    [Fact]
    public async Task Background_loop_polls_at_start_and_again_after_the_interval()
    {
        var source = new FakeSource(new ClaudeUsageResult.Success(Snapshot()));
        using var monitor = Create(source);
        using var cts = new CancellationTokenSource();

        await monitor.StartAsync(cts.Token);
        await WaitForAsync(() => source.Calls == 1);
        await Task.Delay(50);
        _time.Advance(ClaudeUsageMonitor.PollInterval);
        await WaitForAsync(() => source.Calls == 2);

        source.Calls.Should().Be(2);
        await cts.CancelAsync();
        await monitor.StopAsync(CancellationToken.None);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    private sealed class FakeSource(ClaudeUsageResult result) : IClaudeUsageSource
    {
        public ClaudeUsageResult Result { get; set; } = result;

        public int Calls { get; private set; }

        public Task<ClaudeUsageResult> GetUsageAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class Controller(AppSettings settings) : ISynchronizationController
    {
        public AppSettings Settings { get; set; } = settings;

        public void RequestRefresh(Guid? repositoryId = null)
        {
        }

        public Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
