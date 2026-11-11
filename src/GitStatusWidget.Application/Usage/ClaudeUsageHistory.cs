namespace GitStatusWidget.Application.Usage;

/// <summary>One remembered value of a usage limit.</summary>
/// <param name="At">Time of the poll.</param>
/// <param name="Percent">Used share in percent.</param>
/// <param name="ResetsAt">Reset time reported with the value.</param>
public sealed record ClaudeUsageSample(DateTimeOffset At, double Percent, DateTimeOffset? ResetsAt);

/// <summary>Persisted form of <see cref="ClaudeUsageHistory"/>.</summary>
public sealed record ClaudeUsageHistoryData
{
    /// <summary>Samples per limit, oldest first; the key is <see cref="ClaudeUsageHistory.Key"/>.</summary>
    public Dictionary<string, List<ClaudeUsageSample>> Series { get; init; } = [];
}

/// <summary>Keeps the usage history across restarts (issue #18).</summary>
public interface IClaudeUsageHistoryStore
{
    /// <summary>Loads the history.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The stored history, or <see langword="null"/> when there is none.</returns>
    Task<ClaudeUsageHistoryData?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Saves the history.</summary>
    /// <param name="data">History.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the history is stored.</returns>
    Task SaveAsync(ClaudeUsageHistoryData data, CancellationToken cancellationToken);
}

/// <summary>
/// Recent values of every usage limit, the basis of the pace estimate (issue #18): at most one sample per
/// <see cref="SampleInterval"/>, kept for <see cref="Retention"/>. The samples of a limit are dropped when it resets.
/// Thread-safe.
/// </summary>
public sealed class ClaudeUsageHistory
{
    /// <summary>Shortest time between two remembered samples of a limit.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(5);

    /// <summary>How long samples are kept; a little more than the longest pace reference.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(25);

    /// <summary>Reset times closer than this belong to the same window (the endpoint reports them with jitter).</summary>
    public static readonly TimeSpan ResetTolerance = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<ClaudeUsageSample>> _series = [];

    /// <summary>Identifies a limit across polls.</summary>
    /// <param name="limit">Limit.</param>
    /// <returns>The key.</returns>
    public static string Key(ClaudeUsageLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        return limit.Kind == ClaudeUsageLimitKind.WeeklyModel ? $"{limit.Kind}:{limit.Model}" : limit.Kind.ToString();
    }

    /// <summary>Remembers the values of a poll.</summary>
    /// <param name="snapshot">Usage that was read.</param>
    /// <returns><see langword="true"/> when the history changed.</returns>
    public bool Record(ClaudeUsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var changed = false;
        lock (_gate)
        {
            foreach (var limit in snapshot.Limits)
            {
                var key = Key(limit);
                if (!_series.TryGetValue(key, out var samples))
                {
                    _series[key] = samples = [];
                }

                if (samples.Count > 0 && !SameWindow(samples[^1], limit))
                {
                    samples.Clear();
                    changed = true;
                }

                changed |= samples.RemoveAll(s => s.At < snapshot.FetchedAt - Retention || s.At > snapshot.FetchedAt) > 0;
                if (samples.Count == 0 || snapshot.FetchedAt - samples[^1].At >= SampleInterval)
                {
                    samples.Add(new ClaudeUsageSample(snapshot.FetchedAt, limit.Percent, limit.ResetsAt));
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>Remembered samples of a limit, oldest first.</summary>
    /// <param name="limit">Limit.</param>
    /// <returns>A copy of the samples.</returns>
    public IReadOnlyList<ClaudeUsageSample> Samples(ClaudeUsageLimit limit)
    {
        lock (_gate)
        {
            return _series.TryGetValue(Key(limit), out var samples) ? [.. samples] : [];
        }
    }

    /// <summary>Copies the history for persistence.</summary>
    /// <returns>The history.</returns>
    public ClaudeUsageHistoryData Export()
    {
        lock (_gate)
        {
            return new ClaudeUsageHistoryData { Series = _series.Where(s => s.Value.Count > 0).ToDictionary(s => s.Key, s => s.Value.ToList()) };
        }
    }

    /// <summary>Replaces the history with stored samples, dropping what is too old or implausible.</summary>
    /// <param name="data">Stored history.</param>
    /// <param name="now">Current time.</param>
    public void Import(ClaudeUsageHistoryData? data, DateTimeOffset now)
    {
        lock (_gate)
        {
            _series.Clear();
            foreach (var (key, samples) in data?.Series ?? [])
            {
                var kept = (samples ?? [])
                    .Where(s => s is not null && s.At <= now && s.At >= now - Retention && double.IsFinite(s.Percent))
                    .OrderBy(s => s.At)
                    .ToList();
                if (!string.IsNullOrEmpty(key) && kept.Count > 0)
                {
                    _series[key] = kept;
                }
            }
        }
    }

    private static bool SameWindow(ClaudeUsageSample last, ClaudeUsageLimit limit)
    {
        if (limit.Percent < last.Percent - 0.5)
        {
            return false; // Usage only grows within a window.
        }

        return last.ResetsAt is not { } before || limit.ResetsAt is not { } after || (after - before).Duration() <= ResetTolerance;
    }
}
