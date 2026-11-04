using System.Net;
using GitStatusWidget.Application.Abstractions;

namespace GitStatusWidget.Infrastructure.Providers.Http;

/// <summary>
/// Aborts a provider fetch with a classified failure. Caught at the provider boundary and converted to
/// <see cref="ProviderFetchResult.Failure"/>. Messages must be sanitized (no tokens, no issue content).
/// </summary>
internal class ProviderFailureException : Exception
{
    public ProviderFailureException(ProviderFailureKind kind, string message, DateTimeOffset? retryAfter = null, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        RetryAfter = retryAfter;
        StatusCode = statusCode;
    }

    public ProviderFailureKind Kind { get; }

    public DateTimeOffset? RetryAfter { get; }

    public HttpStatusCode? StatusCode { get; }
}

/// <summary>
/// A same-host redirect (for example a GitLab project that was moved). Providers may follow it explicitly.
/// </summary>
internal sealed class ProviderRedirectException : ProviderFailureException
{
    public ProviderRedirectException(HttpStatusCode statusCode, Uri location)
        : base(ProviderFailureKind.Malformed, $"Unexpected redirect ({(int)statusCode}).", statusCode: statusCode)
    {
        Location = location;
    }

    public Uri Location { get; }
}
