namespace GitStatusWidget.Application.Usage;

/// <summary>What a pace estimate is based on.</summary>
public enum ClaudePaceBasis
{
    /// <summary>The usage increase over the recent reference time.</summary>
    Recent,

    /// <summary>The average since the window started; used until enough recent samples exist.</summary>
    WindowAverage,
}

/// <summary>Projection of a usage limit from its current pace (issue #18).</summary>
/// <param name="Basis">What the pace was derived from.</param>
/// <param name="Span">Time the pace was measured over.</param>
/// <param name="PercentPerHour">Pace in percentage points per hour.</param>
/// <param name="ProjectedPercent">Expected usage at the reset, capped at 100.</param>
/// <param name="ReachedAt">When the limit is reached, if that is before the reset.</param>
/// <param name="LimitReached">The limit is already used up.</param>
public sealed record ClaudePaceEstimate(ClaudePaceBasis Basis, TimeSpan Span, double PercentPerHour, double ProjectedPercent, DateTimeOffset? ReachedAt, bool LimitReached);

/// <summary>Estimates from the current pace whether and when a usage limit will be reached.</summary>
public static class ClaudeUsagePace
{
    /// <summary>Selectable reference times in hours.</summary>
    public static readonly IReadOnlyList<int> LookbackHours = [1, 2, 10, 24];

    /// <summary>Default reference time in hours.</summary>
    public const int DefaultLookbackHours = 2;

    /// <summary>Share of the window that must have passed before the average since its start is used.</summary>
    public const double MinWindowShare = 0.05;

    /// <summary>Length of the window of a limit.</summary>
    /// <param name="kind">Limit kind.</param>
    /// <returns>5 hours for the session, 7 days otherwise.</returns>
    public static TimeSpan WindowLength(ClaudeUsageLimitKind kind) =>
        kind == ClaudeUsageLimitKind.Session ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);

    /// <summary>Shortest history the recent pace is derived from: a sixth of the reference time, at least 10 minutes.</summary>
    /// <param name="lookback">Reference time.</param>
    /// <returns>The minimum span.</returns>
    public static TimeSpan MinSpan(TimeSpan lookback) =>
        TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(10).Ticks, lookback.Ticks / 6));

    /// <summary>Share of the window that has passed, in percent: where even consumption would be now.</summary>
    /// <param name="limit">Limit.</param>
    /// <param name="now">Current time.</param>
    /// <returns>0 to 100, or <see langword="null"/> without a reset time.</returns>
    public static double? ElapsedPercent(ClaudeUsageLimit limit, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(limit);
        if (limit.ResetsAt is not { } reset)
        {
            return null;
        }

        var length = WindowLength(limit.Kind);
        return Math.Clamp((now - (reset - length)) / length * 100, 0, 100);
    }

    /// <summary>Projects a limit from its pace.</summary>
    /// <param name="limit">Current value of the limit.</param>
    /// <param name="samples">Remembered values of the same limit, oldest first.</param>
    /// <param name="now">Current time.</param>
    /// <param name="lookback">Reference time for the recent pace.</param>
    /// <returns>The estimate, or <see langword="null"/> when there is no basis for one yet.</returns>
    public static ClaudePaceEstimate? Estimate(ClaudeUsageLimit limit, IReadOnlyList<ClaudeUsageSample> samples, DateTimeOffset now, TimeSpan lookback)
    {
        ArgumentNullException.ThrowIfNull(limit);
        ArgumentNullException.ThrowIfNull(samples);
        if (limit.ResetsAt is not { } reset || reset <= now)
        {
            return null;
        }

        if (limit.Percent >= 100)
        {
            return new ClaudePaceEstimate(ClaudePaceBasis.Recent, TimeSpan.Zero, 0, 100, null, true);
        }

        var length = WindowLength(limit.Kind);
        var windowStart = reset - length;
        ClaudePaceBasis basis;
        TimeSpan span;
        double rate;
        if (Reference(samples, now, lookback, windowStart) is { } reference)
        {
            basis = ClaudePaceBasis.Recent;
            span = now - reference.At;
            rate = Math.Max(0, limit.Percent - reference.Percent) / span.TotalHours;
        }
        else
        {
            basis = ClaudePaceBasis.WindowAverage;
            span = now - windowStart;
            if (span < length * MinWindowShare || span > length)
            {
                return null;
            }

            rate = limit.Percent / span.TotalHours;
        }

        var remaining = reset - now;
        var projected = Math.Min(100, limit.Percent + (rate * remaining.TotalHours));
        DateTimeOffset? reachedAt = null;
        if (rate > 0)
        {
            var hours = (100 - limit.Percent) / rate;
            if (hours < remaining.TotalHours)
            {
                reachedAt = now + TimeSpan.FromHours(hours);
            }
        }

        return new ClaudePaceEstimate(basis, span, rate, projected, reachedAt, false);
    }

    /// <summary>
    /// The sample the recent pace is measured from: the newest one at least <paramref name="lookback"/> old, or the
    /// oldest one when the history is shorter but still long enough.
    /// </summary>
    private static ClaudeUsageSample? Reference(IReadOnlyList<ClaudeUsageSample> samples, DateTimeOffset now, TimeSpan lookback, DateTimeOffset windowStart)
    {
        ClaudeUsageSample? reference = null;
        foreach (var sample in samples)
        {
            if (sample.At >= now || sample.At < windowStart)
            {
                continue;
            }

            if (sample.At <= now - lookback)
            {
                // Older than the reference time: only the newest of these matters, and only if it is not far older.
                reference = now - sample.At <= lookback + lookback / 2 ? sample : null;
            }
            else
            {
                reference ??= sample;
                break;
            }
        }

        return reference is not null && now - reference.At >= MinSpan(lookback) ? reference : null;
    }
}
