using GitStatusWidget.Application.Platform;
using GitStatusWidget.Infrastructure.Platform;

namespace GitStatusWidget.Infrastructure.Tests.Platform;

public sealed class SingleInstanceCoordinatorTests
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);

    private static string UniqueId() => "GwTest." + Guid.NewGuid().ToString("N")[..12];

    [Fact(Timeout = 30_000)]
    public async Task Second_instance_is_not_primary_and_activates_the_first()
    {
        string id = UniqueId();
        await using var first = new SingleInstanceCoordinator(id);
        var received = new TaskCompletionSource<ActivationCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ActivationRequested += (_, e) => received.TrySetResult(e.Command);
        first.StartListening();

        await using var second = new SingleInstanceCoordinator(id);

        first.IsPrimary.Should().BeTrue();
        first.HasLock.Should().BeTrue();
        second.IsPrimary.Should().BeFalse();
        (await second.SignalPrimaryAsync(ActivationCommand.Show, SignalTimeout, TestContext.Current.CancellationToken)).Should().BeTrue();
        (await received.Task.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken)).Should().Be(ActivationCommand.Show);
    }

    [Fact(Timeout = 60_000)]
    public async Task Back_to_back_signals_are_all_acknowledged_and_delivered()
    {
        const int count = 20;
        string id = UniqueId();
        await using var first = new SingleInstanceCoordinator(id);
        var commands = new List<ActivationCommand>();
        using var received = new SemaphoreSlim(0);
        first.ActivationRequested += (_, e) =>
        {
            lock (commands)
            {
                commands.Add(e.Command);
            }

            received.Release();
        };
        first.StartListening();

        using var second = new SingleInstanceCoordinator(id);
        var expected = new List<ActivationCommand>();
        for (int i = 0; i < count; i++)
        {
            var command = i % 2 == 0 ? ActivationCommand.OpenDashboard : ActivationCommand.Show;
            expected.Add(command);
            second.SignalPrimary(command, SignalTimeout).Should().BeTrue($"signal {i} must be acknowledged");
        }

        for (int i = 0; i < count; i++)
        {
            (await received.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken)).Should().BeTrue($"activation {i} must arrive");
        }

        lock (commands)
        {
            commands.Should().Equal(expected);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Signal_without_primary_listener_times_out_with_false()
    {
        await using var lonely = new SingleInstanceCoordinator(UniqueId());

        bool delivered = await lonely.SignalPrimaryAsync(ActivationCommand.Show, TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        delivered.Should().BeFalse();
        lonely.SignalPrimary(ActivationCommand.Show, TimeSpan.FromMilliseconds(300)).Should().BeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task Lock_is_released_after_dispose()
    {
        string id = UniqueId();
        var first = new SingleInstanceCoordinator(id);
        first.StartListening();
        first.IsPrimary.Should().BeTrue();

        await first.DisposeAsync();

        await using var next = new SingleInstanceCoordinator(id);
        next.IsPrimary.Should().BeTrue("the previous owner disposed its handle");
        next.StartListening();
        await using var probe = new SingleInstanceCoordinator(id);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        next.ActivationRequested += (_, _) => received.TrySetResult();
        (await probe.SignalPrimaryAsync(ActivationCommand.Show, SignalTimeout, TestContext.Current.CancellationToken)).Should().BeTrue();
        await received.Task.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Non_primary_cannot_listen_and_dispose_is_idempotent()
    {
        string id = UniqueId();
        using var first = new SingleInstanceCoordinator(id);
        var second = new SingleInstanceCoordinator(id);

        FluentActions.Invoking(second.StartListening).Should().Throw<InvalidOperationException>();
        second.Dispose();
        second.Dispose();
    }

    [Theory]
    [InlineData("show", true, ActivationCommand.Show)]
    [InlineData("show\r", true, ActivationCommand.Show)]
    [InlineData("dashboard", true, ActivationCommand.OpenDashboard)]
    [InlineData("SHOW", false, ActivationCommand.Show)]
    [InlineData("exec calc", false, ActivationCommand.Show)]
    [InlineData("", false, ActivationCommand.Show)]
    public void Only_whitelisted_commands_are_accepted(string text, bool accepted, ActivationCommand expected)
    {
        SingleInstanceCoordinator.TryParseCommand(text, out var command).Should().Be(accepted);
        if (accepted)
        {
            command.Should().Be(expected);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("back\\slash")]
    [InlineData("Global/x")]
    [InlineData("a-name-that-is-much-too-long-for-a-socket")]
    public void Rejects_invalid_application_ids(string id)
    {
        FluentActions.Invoking(() => new SingleInstanceCoordinator(id)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Pipe_name_fits_unix_socket_path_limit()
    {
        using var coordinator = new SingleInstanceCoordinator(new string('a', SingleInstanceCoordinator.MaxApplicationIdLength));

        // .NET puts the socket at $TMPDIR/CoreFxPipe_<name>; sun_path is 104 bytes on macOS.
        string socketPath = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + coordinator.PipeName);
        if (!OperatingSystem.IsWindows())
        {
            socketPath.Length.Should().BeLessThan(104);
        }
    }
}
