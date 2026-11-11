namespace GitStatusWidget.Application.Usage;

/// <summary>Which limit of the Claude subscription a value belongs to (issue #17).</summary>
public enum ClaudeUsageLimitKind
{
    /// <summary>Rolling 5-hour session.</summary>
    Session,

    /// <summary>Weekly limit across all models.</summary>
    Weekly,

    /// <summary>Weekly limit of one model.</summary>
    WeeklyModel,
}

/// <summary>One usage limit of the Claude subscription.</summary>
/// <param name="Kind">Limit kind.</param>
/// <param name="Model">Model name for <see cref="ClaudeUsageLimitKind.WeeklyModel"/>, otherwise <see langword="null"/>.</param>
/// <param name="Percent">Used share in percent, 0 to 100.</param>
/// <param name="ResetsAt">When the limit resets; <see langword="null"/> when not reported.</param>
public sealed record ClaudeUsageLimit(ClaudeUsageLimitKind Kind, string? Model, double Percent, DateTimeOffset? ResetsAt);

/// <summary>Usage of the Claude subscription at one point in time.</summary>
/// <param name="Limits">Reported limits: session, weekly, then model limits. Limits the account does not report are absent.</param>
/// <param name="Plan">Subscription type as stored by Claude Code (for example <c>max</c>), if known.</param>
/// <param name="FetchedAt">Time of the request.</param>
public sealed record ClaudeUsageSnapshot(IReadOnlyList<ClaudeUsageLimit> Limits, string? Plan, DateTimeOffset FetchedAt);

/// <summary>Result of reading the Claude usage.</summary>
public abstract record ClaudeUsageResult
{
    private ClaudeUsageResult()
    {
    }

    /// <summary>The usage was read.</summary>
    /// <param name="Snapshot">Current usage.</param>
    public sealed record Success(ClaudeUsageSnapshot Snapshot) : ClaudeUsageResult;

    /// <summary>No usable Claude Code sign-in: none found, expired or rejected.</summary>
    /// <param name="Reason">Text for the user.</param>
    public sealed record SignedOut(string Reason) : ClaudeUsageResult;

    /// <summary>The usage endpoint asked to slow down.</summary>
    /// <param name="RetryAfter">Wait requested by the server, if any.</param>
    public sealed record RateLimited(TimeSpan? RetryAfter) : ClaudeUsageResult;

    /// <summary>The usage could not be read (offline, server error, unexpected answer).</summary>
    /// <param name="Reason">Sanitized reason.</param>
    public sealed record Unavailable(string Reason) : ClaudeUsageResult;
}

/// <summary>
/// Reads the usage of the Claude subscription with the sign-in of Claude Code (read-only; the sign-in is never
/// refreshed or changed).
/// </summary>
public interface IClaudeUsageSource
{
    /// <summary>Reads the current usage.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The usage or the reason it is not available.</returns>
    Task<ClaudeUsageResult> GetUsageAsync(CancellationToken cancellationToken);
}
