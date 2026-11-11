namespace GitStatusWidget.Application.Updates;

/// <summary>A published GitWidget release.</summary>
/// <param name="Version">Parsed version (from the tag).</param>
/// <param name="Tag">Tag name, e.g. <c>v0.1.2</c>.</param>
/// <param name="PageUrl">Release page in the browser.</param>
/// <param name="IsPrerelease">Marked as pre-release by the publisher.</param>
/// <param name="PublishedAt">Publication time.</param>
public sealed record ReleaseInfo(SemanticVersion Version, string Tag, Uri PageUrl, bool IsPrerelease, DateTimeOffset? PublishedAt)
{
    /// <summary>Download for the running system, if the release has one (issue #19).</summary>
    public ReleaseAsset? Asset { get; init; }
}

/// <summary>A downloadable file of a release.</summary>
/// <param name="Name">File name, e.g. <c>GitWidget-win-x64.zip</c>.</param>
/// <param name="DownloadUrl">Download address.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Sha256">SHA-256 of the file as hex, when the publisher reports one.</param>
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size, string? Sha256);

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
