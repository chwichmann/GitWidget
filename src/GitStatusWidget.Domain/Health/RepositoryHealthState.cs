namespace GitStatusWidget.Domain.Health;

/// <summary>
/// Connection and freshness state of a monitored repository.
/// </summary>
public enum RepositoryHealthState
{
    /// <summary>Last refresh succeeded and data is fresh.</summary>
    Healthy = 1,

    /// <summary>A refresh is running.</summary>
    Refreshing = 2,

    /// <summary>Data is older than the staleness threshold.</summary>
    Stale = 3,

    /// <summary>The provider is not reachable.</summary>
    Offline = 4,

    /// <summary>The provider asked us to wait.</summary>
    RateLimited = 5,

    /// <summary>The token is missing, invalid, revoked or expired.</summary>
    AuthenticationFailed = 6,

    /// <summary>The token lacks permissions.</summary>
    AuthorizationFailed = 7,

    /// <summary>Repository not found or not visible with the token.</summary>
    NotFound = 8,

    /// <summary>Provider error, malformed response or gateway interception.</summary>
    ProviderError = 9,

    /// <summary>The local configuration is invalid.</summary>
    ConfigurationError = 10,
}
