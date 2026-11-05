namespace GitStatusWidget.Application.Updates;

/// <summary>A published GitWidget release.</summary>
/// <param name="Version">Parsed version (from the tag).</param>
/// <param name="Tag">Tag name, e.g. <c>v0.1.2</c>.</param>
/// <param name="PageUrl">Release page in the browser.</param>
/// <param name="IsPrerelease">Marked as pre-release by the publisher.</param>
/// <param name="PublishedAt">Publication time.</param>
public sealed record ReleaseInfo(SemanticVersion Version, string Tag, Uri PageUrl, bool IsPrerelease, DateTimeOffset? PublishedAt);

/// <summary>Result of reading the release feed.</summary>
public abstract record ReleaseFeedResult
{
    private ReleaseFeedResult()
    {
    }

    /// <summary>Releases (drafts excluded), in any order.</summary>
    /// <param name="Releases">Published releases.</param>
    public sealed record Success(IReadOnlyList<ReleaseInfo> Releases) : ReleaseFeedResult;

    /// <summary>The feed could not be read (offline, private repository, rate limit).</summary>
    /// <param name="Reason">Sanitized reason for diagnostics.</param>
    public sealed record Unavailable(string Reason) : ReleaseFeedResult;
}

/// <summary>Reads published releases of GitWidget itself (read-only).</summary>
public interface IReleaseFeed
{
    /// <summary>Repository the releases are read from, e.g. <c>chwichmann/GitWidget</c>.</summary>
    string Source { get; }

    /// <summary>Reads the releases.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The releases or the reason they are unavailable.</returns>
    Task<ReleaseFeedResult> GetReleasesAsync(CancellationToken cancellationToken);
}
