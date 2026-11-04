namespace GitStatusWidget.Application.Abstractions;

/// <summary>
/// Per-resource conditional request metadata (architecture §13).
/// </summary>
/// <param name="ResourceKey">Resource key, normally the request URL without credentials.</param>
/// <param name="ETag">Validator exactly as returned by the server (including a <c>W/</c> prefix).</param>
/// <param name="LastModified">Last-Modified value, if any.</param>
/// <param name="LastSuccessfulRequest">Time of the last successful request.</param>
/// <param name="PageCount">Number of pages the resource had on the last 200 response.</param>
public sealed record ConditionalRequestState(
    string ResourceKey,
    string? ETag,
    DateTimeOffset? LastModified,
    DateTimeOffset LastSuccessfulRequest,
    int PageCount = 1);

/// <summary>
/// Opaque, provider-owned state persisted between fetches (watermarks, conditional request metadata).
/// The application persists it but never interprets it. Must not contain secrets.
/// </summary>
/// <param name="Values">Provider-defined values such as watermarks (ISO 8601 strings).</param>
/// <param name="Requests">Conditional request metadata by resource key.</param>
public sealed record ProviderSyncState(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, ConditionalRequestState> Requests)
{
    /// <summary>Empty state.</summary>
    public static ProviderSyncState Empty { get; } = new(new Dictionary<string, string>(), new Dictionary<string, ConditionalRequestState>());

    /// <summary>Returns a value, or <see langword="null"/>.</summary>
    /// <param name="key">Value key.</param>
    /// <returns>The value or null.</returns>
    public string? GetValue(string key) => Values.TryGetValue(key, out var value) ? value : null;

    /// <summary>Returns the conditional request state for a resource, or <see langword="null"/>.</summary>
    /// <param name="resourceKey">Resource key.</param>
    /// <returns>The state or null.</returns>
    public ConditionalRequestState? GetRequest(string resourceKey) => Requests.TryGetValue(resourceKey, out var state) ? state : null;

    /// <summary>Returns a copy with the given values set (null removes a value).</summary>
    /// <param name="values">Values to set.</param>
    /// <returns>A new state.</returns>
    public ProviderSyncState WithValues(params (string Key, string? Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new Dictionary<string, string>(Values, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (value is null)
            {
                copy.Remove(key);
            }
            else
            {
                copy[key] = value;
            }
        }

        return this with { Values = copy };
    }

    /// <summary>Returns a copy with the given request states set.</summary>
    /// <param name="requests">States to set.</param>
    /// <returns>A new state.</returns>
    public ProviderSyncState WithRequests(IEnumerable<ConditionalRequestState> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var copy = new Dictionary<string, ConditionalRequestState>(Requests, StringComparer.Ordinal);
        foreach (var request in requests)
        {
            copy[request.ResourceKey] = request;
        }

        return this with { Requests = copy };
    }
}
