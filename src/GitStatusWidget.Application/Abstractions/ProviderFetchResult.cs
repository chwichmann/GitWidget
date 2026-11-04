using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Application.Abstractions;

/// <summary>
/// Classified reason of a failed fetch.
/// </summary>
public enum ProviderFailureKind
{
    /// <summary>Token missing, invalid, revoked or expired (HTTP 401).</summary>
    AuthenticationFailed = 1,

    /// <summary>Token lacks scope or role (HTTP 403 without rate-limit signals).</summary>
    AuthorizationFailed,

    /// <summary>Repository not found or not visible.</summary>
    NotFound,

    /// <summary>The provider asked to wait (HTTP 429 or rate-limited 403).</summary>
    RateLimited,

    /// <summary>Network failure, timeout or HTTP 5xx.</summary>
    Transient,

    /// <summary>Unexpected or unparsable response.</summary>
    Malformed,

    /// <summary>A gateway or login portal answered instead of the API (redirect or HTML).</summary>
    GatewayInterception,

    /// <summary>The configuration is invalid (bad URL, missing credential, http not allowed).</summary>
    Configuration,

    /// <summary>The data could not be retrieved completely (for example a later page failed).</summary>
    Partial,

    /// <summary>The server is unreachable (DNS failure, connection refused, no network).</summary>
    Offline,
}

/// <summary>
/// Rate-limit information read from the last response.
/// </summary>
/// <param name="Limit">Request limit of the window.</param>
/// <param name="Remaining">Remaining requests.</param>
/// <param name="ResetAt">Time the window resets.</param>
/// <param name="Resource">Bucket name (for example GitHub <c>core</c> / <c>graphql</c>).</param>
public sealed record RateLimitInfo(int? Limit, int? Remaining, DateTimeOffset? ResetAt, string? Resource = null);

/// <summary>
/// Result of <see cref="IRepositoryStatusProvider.FetchAsync"/>.
/// </summary>
public abstract record ProviderFetchResult
{
    private ProviderFetchResult()
    {
    }

    /// <summary>Latest rate-limit information, if the provider reports any.</summary>
    public RateLimitInfo? RateLimit { get; init; }

    /// <summary>Successful fetch.</summary>
    /// <param name="Fetch">Section updates.</param>
    /// <param name="SyncState">New provider sync state to persist with the accepted snapshot.</param>
    public sealed record Success(ProviderFetch Fetch, ProviderSyncState SyncState) : ProviderFetchResult;

    /// <summary>Failed fetch. The previous snapshot stays authoritative.</summary>
    /// <param name="Kind">Failure classification.</param>
    /// <param name="Message">Sanitized, user-presentable message (never contains secrets).</param>
    /// <param name="RetryAfter">Earliest time to retry, when the provider said so.</param>
    public sealed record Failure(ProviderFailureKind Kind, string Message, DateTimeOffset? RetryAfter = null) : ProviderFetchResult;
}

/// <summary>
/// Result of <see cref="IRepositoryStatusProvider.TestConnectionAsync"/>.
/// </summary>
/// <param name="Identity">Resolved repository identity on success.</param>
/// <param name="Failure">Failure classification, or <see langword="null"/> on success.</param>
/// <param name="Message">Sanitized message describing the outcome.</param>
public sealed record ProviderConnectionResult(RepositoryIdentity? Identity, ProviderFailureKind? Failure, string Message)
{
    /// <summary>Whether the connection works.</summary>
    public bool IsSuccess => Failure is null && Identity is not null;

    /// <summary>Server version, if known (for example <c>19.5.1-ee</c>).</summary>
    public string? ServerVersion { get; init; }

    /// <summary>Token description, for example "personal access token, scopes read_api".</summary>
    public string? CredentialDescription { get; init; }

    /// <summary>Token expiry date, if the provider reports it.</summary>
    public DateOnly? CredentialExpiresOn { get; init; }

    /// <summary>Creates a successful result.</summary>
    /// <param name="identity">Resolved identity.</param>
    /// <param name="message">Message.</param>
    /// <returns>The result.</returns>
    public static ProviderConnectionResult Ok(RepositoryIdentity identity, string message) => new(identity, null, message);

    /// <summary>Creates a failed result.</summary>
    /// <param name="kind">Failure kind.</param>
    /// <param name="message">Sanitized message.</param>
    /// <returns>The result.</returns>
    public static ProviderConnectionResult Fail(ProviderFailureKind kind, string message) => new(null, kind, message);
}
