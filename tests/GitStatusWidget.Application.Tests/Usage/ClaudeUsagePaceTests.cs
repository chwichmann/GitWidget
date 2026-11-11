using GitStatusWidget.Application.Usage;

namespace GitStatusWidget.Application.Tests.Usage;

/// <summary>Issue #18: pace estimate and the usage history it is based on.</summary>
public sealed class ClaudeUsagePaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);

    private static ClaudeUsageLimit Weekly(double percent, double resetsInHours = 72) =>
        new(ClaudeUsageLimitKind.Weekly, null, percent, Now.AddHours(resetsInHours));

    private static ClaudeUsageSample Sample(double hoursAgo, double percent, double resetsInHours = 72) =>
        new(Now.AddHours(-hoursAgo), percent, Now.AddHours(resetsInHours));

    [Fact]
    public void Recent_pace_uses_the_sample_at_the_reference_time()
    {
        // 4 points in the last 2 hours = 2 %/h; 40 points are left, so the limit is reached in 20 hours.
        var estimate = ClaudeUsagePace.Estimate(Weekly(60), [Sample(10, 30), Sample(2, 56), Sample(1, 59)], Now, TwoHours)!;

        estimate.Basis.Should().Be(ClaudePaceBasis.Recent);
        estimate.Span.Should().Be(TwoHours);
        estimate.PercentPerHour.Should().BeApproximately(2, 1e-9);
        estimate.ReachedAt.Should().Be(Now.AddHours(20));
        estimate.ProjectedPercent.Should().Be(100);
        estimate.LimitReached.Should().BeFalse();
    }

    [Fact]
    public void A_pace_that_stays_below_the_limit_reports_the_projection_instead_of_a_time()
    {
        // 0.2 %/h over the remaining 72 hours adds 14.4 points.
        var estimate = ClaudeUsagePace.Estimate(Weekly(50), [Sample(2, 49.6)], Now, TwoHours)!;

        estimate.ReachedAt.Should().BeNull();
        estimate.ProjectedPercent.Should().BeApproximately(64.4, 1e-6);
    }

    [Fact]
    public void No_increase_in_the_reference_time_means_no_pace()
    {
        var estimate = ClaudeUsagePace.Estimate(Weekly(50), [Sample(2, 50)], Now, TwoHours)!;

        estimate.Basis.Should().Be(ClaudePaceBasis.Recent);
        estimate.PercentPerHour.Should().Be(0);
        estimate.ReachedAt.Should().BeNull();
        estimate.ProjectedPercent.Should().Be(50);
    }

    [Fact]
    public void Shorter_history_is_used_once_it_covers_the_minimum_span()
    {
        ClaudeUsagePace.MinSpan(TwoHours).Should().Be(TimeSpan.FromMinutes(20));
        ClaudeUsagePace.MinSpan(TimeSpan.FromHours(1)).Should().Be(TimeSpan.FromMinutes(10));

        var enough = ClaudeUsagePace.Estimate(Weekly(50), [Sample(0.5, 49)], Now, TwoHours)!;
        var tooShort = ClaudeUsagePace.Estimate(Weekly(50), [Sample(0.25, 49)], Now, TwoHours)!;

        enough.Basis.Should().Be(ClaudePaceBasis.Recent);
        enough.Span.Should().Be(TimeSpan.FromMinutes(30));
        enough.PercentPerHour.Should().BeApproximately(2, 1e-9);
        tooShort.Basis.Should().Be(ClaudePaceBasis.WindowAverage);
    }

    [Fact]
    public void Without_recent_samples_the_average_since_the_reset_is_used()
    {
        // Weekly window: 96 of 168 hours have passed, 48 % used = 0.5 %/h; 72 hours left add 36 points.
        var estimate = ClaudeUsagePace.Estimate(Weekly(48), [], Now, TwoHours)!;

        estimate.Basis.Should().Be(ClaudePaceBasis.WindowAverage);
        estimate.Span.Should().Be(TimeSpan.FromHours(96));
        estimate.PercentPerHour.Should().BeApproximately(0.5, 1e-9);
        estimate.ProjectedPercent.Should().BeApproximately(84, 1e-6);
        estimate.ReachedAt.Should().BeNull();
    }

    [Fact]
    public void A_sample_far_older_than_the_reference_time_is_not_used()
    {
        // The app was closed for 9 hours: the only sample is no "last 2 hours" pace.
        var estimate = ClaudeUsagePace.Estimate(Weekly(48), [Sample(9, 10)], Now, TwoHours)!;

        estimate.Basis.Should().Be(ClaudePaceBasis.WindowAverage);
    }

    [Fact]
    public void There_is_no_estimate_right_after_a_reset_without_a_reset_time_or_after_the_reset()
    {
        // 4 hours into a 168-hour window is less than 5 %.
        ClaudeUsagePace.Estimate(Weekly(3, resetsInHours: 164), [], Now, TwoHours).Should().BeNull();
        ClaudeUsagePace.Estimate(new ClaudeUsageLimit(ClaudeUsageLimitKind.Weekly, null, 50, null), [], Now, TwoHours).Should().BeNull();
        ClaudeUsagePace.Estimate(Weekly(50, resetsInHours: -1), [], Now, TwoHours).Should().BeNull();
    }

    [Fact]
    public void A_used_up_limit_is_reported_as_reached()
    {
        var estimate = ClaudeUsagePace.Estimate(Weekly(100), [Sample(2, 90)], Now, TwoHours)!;

        estimate.LimitReached.Should().BeTrue();
        estimate.ReachedAt.Should().BeNull();
    }

    [Fact]
    public void Session_window_is_five_hours()
    {
        var session = new ClaudeUsageLimit(ClaudeUsageLimitKind.Session, null, 60, Now.AddHours(2));

        // 3 of 5 hours passed, 60 % used = 20 %/h: the remaining 40 points last exactly until the reset.
        var estimate = ClaudeUsagePace.Estimate(session, [], Now, TwoHours)!;

        ClaudeUsagePace.ElapsedPercent(session, Now).Should().BeApproximately(60, 1e-9);
        ClaudeUsagePace.ElapsedPercent(Weekly(0, resetsInHours: 84), Now).Should().BeApproximately(50, 1e-9);
        ClaudeUsagePace.ElapsedPercent(new ClaudeUsageLimit(ClaudeUsageLimitKind.Weekly, null, 5, null), Now).Should().BeNull();
        estimate.PercentPerHour.Should().BeApproximately(20, 1e-9);
        estimate.ReachedAt.Should().BeNull("the limit is reached at the reset, not before it");
        estimate.ProjectedPercent.Should().BeApproximately(100, 1e-6);
    }

    private static ClaudeUsageSnapshot Snapshot(DateTimeOffset at, double percent, DateTimeOffset reset) =>
        new([new ClaudeUsageLimit(ClaudeUsageLimitKind.Weekly, null, percent, reset)], null, at);

    [Fact]
    public void History_keeps_one_sample_per_interval_and_drops_old_ones()
    {
        var history = new ClaudeUsageHistory();
        var reset = Now.AddDays(3);
        var limit = new ClaudeUsageLimit(ClaudeUsageLimitKind.Weekly, null, 0, reset);

        history.Record(Snapshot(Now, 10, reset)).Should().BeTrue();
        history.Record(Snapshot(Now.AddMinutes(1), 11, reset)).Should().BeFalse("the last sample is less than five minutes old");
        history.Record(Snapshot(Now.AddMinutes(5), 12, reset.AddSeconds(1))).Should().BeTrue();
        history.Samples(limit).Select(s => s.Percent).Should().Equal(10, 12);

        history.Record(Snapshot(Now.AddHours(26), 20, reset)).Should().BeTrue();
        history.Samples(limit).Select(s => s.Percent).Should().Equal(20);
    }

    [Fact]
    public void History_of_a_limit_starts_over_when_the_limit_resets()
    {
        var history = new ClaudeUsageHistory();
        var reset = Now.AddHours(1);
        var limit = new ClaudeUsageLimit(ClaudeUsageLimitKind.Weekly, null, 0, reset);
        history.Record(Snapshot(Now, 80, reset));

        history.Record(Snapshot(Now.AddHours(2), 3, reset.AddDays(7)));

        history.Samples(limit).Should().ContainSingle().Which.Percent.Should().Be(3);
    }

    [Fact]
    public void History_survives_export_and_import_without_stale_samples()
    {
        var history = new ClaudeUsageHistory();
        var reset = Now.AddDays(3);
        var model = new ClaudeUsageLimit(ClaudeUsageLimitKind.WeeklyModel, "Fable", 69, reset);
        history.Record(new ClaudeUsageSnapshot([model], null, Now.AddHours(-30)));
        history.Record(new ClaudeUsageSnapshot([model with { Percent = 70 }], null, Now.AddHours(-1)));
        var data = history.Export();
        data.Series["WeeklyModel:Fable"].Insert(0, new ClaudeUsageSample(Now.AddHours(-40), 1, reset));

        var restored = new ClaudeUsageHistory();
        restored.Import(data, Now);

        restored.Samples(model).Should().ContainSingle().Which.Percent.Should().Be(70);
        new ClaudeUsageHistory().Samples(model).Should().BeEmpty();
    }
}
