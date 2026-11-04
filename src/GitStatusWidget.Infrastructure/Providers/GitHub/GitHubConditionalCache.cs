using GitStatusWidget.Application.Abstractions;

namespace GitStatusWidget.Infrastructure.Providers.GitHub;

/// <summary>
/// ETag bookkeeping of one fetch (github-api.md §9). Keys combine the API version with the relative request URL,
/// because GitHub ETags vary with <c>X-GitHub-Api-Version</c>. Validators are only sent when a previous snapshot
/// exists, so a 304 always has a stored section to fall back to.
/// </summary>
internal sealed class GitHubConditionalCache
{
    private readonly ProviderSyncState _state;
    private readonly bool _enabled;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, ConditionalRequestState> _updates = new(StringComparer.Ordinal);

    public GitHubConditionalCache(ProviderSyncState state, bool enabled, TimeProvider time)
    {
        _state = state;
        _enabled = enabled;
        _time = time;
    }

    /// <summary>Updated request states to persist on success.</summary>
    public IEnumerable<ConditionalRequestState> Updates => _updates.Values;

    /// <summary>Cache key of a relative request URL.</summary>
    public static string Key(string relativeUrl) => $"{GitHubEndpoints.ApiVersion} {relativeUrl}";

    /// <summary>
    /// Validator to send, or <see langword="null"/>. With <paramref name="singlePageOnly"/> a validator is only used
    /// when the resource had exactly one page (a 304 carries no <c>Link</c> header, G6).
    /// </summary>
    public string? ValidatorFor(string key, bool singlePageOnly)
    {
        if (!_enabled || _state.GetRequest(key) is not { ETag: { Length: > 0 } etag } stored)
        {
            return null;
        }

        return singlePageOnly && stored.PageCount != 1 ? null : etag;
    }

    /// <summary>Records the validator of a successful (200 or 304) response.</summary>
    public void Record(string key, string? etag, int pageCount) =>
        _updates[key] = new ConditionalRequestState(key, etag, null, _time.GetUtcNow(), Math.Max(1, pageCount));

    /// <summary>Records that a resource was answered 304 (keeps the stored validator and page count).</summary>
    public void RecordNotModified(string key)
    {
        var stored = _state.GetRequest(key);
        Record(key, stored?.ETag, stored?.PageCount ?? 1);
    }
}
