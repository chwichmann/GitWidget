using GitStatusWidget.Infrastructure.Platform;

namespace GitStatusWidget.Infrastructure.Tests.Platform;

public sealed class ReducedMotionDetectorTests
{
    /// <summary>Minimal manual clock (the test project does not reference TimeProvider.Testing).</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    [Fact(Timeout = 30_000)]
    public async Task Does_not_throw_on_current_os()
    {
        var detector = new ReducedMotionDetector();

        bool first = await Task.Run(detector.IsReducedMotionEnabled, TestContext.Current.CancellationToken);
        bool second = detector.IsReducedMotionEnabled();

        second.Should().Be(first, "the value is cached");
        ReducedMotionDetector.ProbeCurrentOs(); // Raw probe must not throw either.
    }

    [Fact]
    public void Caches_result_and_refreshes_after_expiry()
    {
        var time = new ManualClock();
        int calls = 0;
        bool? value = true;
        var detector = new ReducedMotionDetector(() => { calls++; return value; }, time, TimeSpan.FromSeconds(60));

        detector.IsReducedMotionEnabled().Should().BeTrue();
        value = false;
        detector.IsReducedMotionEnabled().Should().BeTrue("cached");
        calls.Should().Be(1);

        time.Advance(TimeSpan.FromSeconds(61));
        detector.IsReducedMotionEnabled().Should().BeFalse();
        calls.Should().Be(2);
    }

    [Fact]
    public void Unknown_or_failing_probe_means_no_reduction()
    {
        new ReducedMotionDetector(() => null, new ManualClock(), TimeSpan.Zero).IsReducedMotionEnabled().Should().BeFalse();
        new ReducedMotionDetector(() => throw new DllNotFoundException("user32"), new ManualClock(), TimeSpan.Zero)
            .IsReducedMotionEnabled().Should().BeFalse();
    }

    [Theory]
    [InlineData("(<<uint32 1>>,)\n", true)]
    [InlineData("(<<uint32 0>>,)\n", false)]
    [InlineData("Error: GDBus.Error:org.freedesktop.portal.Error.NotFound", null)]
    [InlineData(null, null)]
    public void Parses_portal_reply(string? reply, bool? expected)
    {
        LinuxReducedMotion.ParsePortalReply(reply).Should().Be(expected);
    }

    [Theory]
    [InlineData("false\n", true)]
    [InlineData("true\n", false)]
    [InlineData("No such schema", null)]
    [InlineData(null, null)]
    public void Parses_gnome_enable_animations(string? output, bool? expected)
    {
        LinuxReducedMotion.ParseGnomeEnableAnimations(output).Should().Be(expected);
    }
}
