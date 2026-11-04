using System.Globalization;
using System.Net;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Infrastructure.Providers.Http;

namespace GitStatusWidget.Infrastructure.Providers.GitHub;

/// <summary>
/// GitHub rate-limit parsing and error classification (github-api.md §10.4, §11). One instance per fetch: it also
/// remembers the latest rate-limit state per bucket (<c>core</c>, <c>graphql</c>, …).
/// </summary>
internal sealed class GitHubResponsePolicy : IProviderResponsePolicy
{
    /// <summary>Minimum wait for a secondary rate limit without <c>Retry-After</c>.</summary>
    public static readonly TimeSpan SecondaryLimitWait = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, RateLimitInfo> _buckets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The most constrained bucket seen (lowest remaining fraction). Reported to the scheduler so that a nearly
    /// exhausted bucket is never hidden by a later response of a fuller one.
    /// </summary>
    public RateLimitInfo? MostConstrained
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Values
                    .OrderBy(b => b.Limit is > 0 && b.Remaining is { } r ? (double)r / b.Limit.Value : 1d)
                    .FirstOrDefault();
            }
        }
    }

    /// <summary>Latest state of one bucket, or <see langword="null"/>.</summary>
    public RateLimitInfo? ForResource(string resource)
    {
        lock (_gate)
        {
            return _buckets.GetValueOrDefault(resource);
        }
    }

    /// <inheritdoc />
    public RateLimitInfo? ReadRateLimit(ResponseHeaders headers)
    {
        var limit = headers.GetInt("X-RateLimit-Limit");
        var remaining = headers.GetInt("X-RateLimit-Remaining");
        var reset = headers.GetLong("X-RateLimit-Reset");
        if (limit is null && remaining is null && reset is null)
        {
            // 401 responses carry no rate-limit headers (verified); absence is not an error.
            return null;
        }

        var resource = headers["X-RateLimit-Resource"];
        var info = new RateLimitInfo(
            limit,
            remaining,
            reset is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null,
            string.IsNullOrWhiteSpace(resource) ? "core" : resource.Trim());
        lock (_gate)
        {
            _buckets[info.Resource!] = info;
        }

        return info;
    }

    /// <inheritdoc />
    public ProviderFailureException? Classify(HttpStatusCode statusCode, ResponseHeaders headers, string bodySnippet, DateTimeOffset now)
    {
        var status = (int)statusCode;
        switch (status)
        {
            case 401:
                return new ProviderFailureException(
                    ProviderFailureKind.AuthenticationFailed,
                    "GitHub rejected the token (401 Bad credentials). It may be invalid, expired or revoked; store a new token.",
                    statusCode: statusCode);
            case 403 or 429 when ClassifyRateLimit(statusCode, headers, bodySnippet, now) is { } rateLimited:
                return rateLimited;
            case 403:
                return ClassifyForbidden(statusCode, headers, bodySnippet);
            case 404:
                return new ProviderFailureException(
                    ProviderFailureKind.NotFound,
                    "GitHub returned 404: the repository or resource does not exist, or the token cannot see it (private repositories appear as 404 without access; check the token's resource owner and repository selection).",
                    statusCode: statusCode);
            case 400:
                return new ProviderFailureException(
                    ProviderFailureKind.Configuration,
                    $"GitHub rejected the request (400). The server may not support API version {GitHubEndpoints.ApiVersion} (outdated GitHub Enterprise Server?).",
                    statusCode: statusCode);
            case 422:
                return new ProviderFailureException(
                    ProviderFailureKind.Malformed,
                    "GitHub rejected a request parameter (422 Validation Failed).",
                    statusCode: statusCode);
            default:
                return null;
        }
    }

    private static ProviderFailureException? ClassifyRateLimit(HttpStatusCode statusCode, ResponseHeaders headers, string body, DateTimeOffset now)
    {
        var resource = headers["X-RateLimit-Resource"] is { Length: > 0 } r ? r.Trim() : "core";
        if (headers.GetInt("X-RateLimit-Remaining") == 0)
        {
            var reset = headers.GetLong("X-RateLimit-Reset") is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null;
            var until = reset is { } value && value > now ? value : now + SecondaryLimitWait;
            return RateLimited($"GitHub {resource} rate limit exhausted; requests are suspended until {until.ToString("u", CultureInfo.InvariantCulture)}.", until, statusCode);
        }

        if (ProviderHttpClient.ParseRetryAfter(headers, now) is { } retryAfter)
        {
            return RateLimited("GitHub asked to wait (secondary rate limit).", retryAfter > now ? retryAfter : now + TimeSpan.FromSeconds(1), statusCode);
        }

        if (body.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)
            || body.Contains("abuse detection", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimited("GitHub secondary rate limit hit; waiting at least 60 seconds.", now + SecondaryLimitWait, statusCode);
        }

        if (body.Contains("rate limit exceeded", StringComparison.OrdinalIgnoreCase))
        {
            var reset = headers.GetLong("X-RateLimit-Reset") is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null;
            return RateLimited("GitHub rate limit exceeded.", reset is { } value && value > now ? value : now + SecondaryLimitWait, statusCode);
        }

        if ((int)statusCode == 429)
        {
            return RateLimited("Too many requests to GitHub (429).", now + SecondaryLimitWait, statusCode);
        }

        return null;
    }

    private static ProviderFailureException ClassifyForbidden(HttpStatusCode statusCode, ResponseHeaders headers, string body)
    {
        if (!string.IsNullOrWhiteSpace(headers["X-GitHub-SSO"]))
        {
            return new ProviderFailureException(
                ProviderFailureKind.AuthorizationFailed,
                "Access denied (403): the organization uses SAML single sign-on and the token is not authorized for it. Authorize the token for the organization in the GitHub token settings.",
                statusCode: statusCode);
        }

        var accepted = headers["X-Accepted-GitHub-Permissions"];
        if (!string.IsNullOrWhiteSpace(accepted) || body.Contains("Resource not accessible", StringComparison.OrdinalIgnoreCase))
        {
            var required = string.IsNullOrWhiteSpace(accepted) ? string.Empty : $" Required permissions: {Sanitize(accepted)}.";
            return new ProviderFailureException(
                ProviderFailureKind.AuthorizationFailed,
                $"Access denied (403): the token lacks a required read permission (Metadata, Issues, Pull requests or Actions).{required}",
                statusCode: statusCode);
        }

        if (!IsJson(headers.ContentType) && (body.Contains("<html", StringComparison.OrdinalIgnoreCase) || body.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)))
        {
            // Verified: a missing User-Agent yields 403 text/html (G15). Proxies blocking the client look the same.
            return new ProviderFailureException(
                ProviderFailureKind.Configuration,
                "GitHub refused the request with an HTML page (403). Check the User-Agent setting and any proxy between this machine and GitHub.",
                statusCode: statusCode);
        }

        return new ProviderFailureException(
            ProviderFailureKind.AuthorizationFailed,
            "Access denied (403). The token lacks permission for this repository, or GitHub temporarily blocked authentication after repeated failures.",
            statusCode: statusCode);
    }

    private static ProviderFailureException RateLimited(string message, DateTimeOffset until, HttpStatusCode statusCode) =>
        new(ProviderFailureKind.RateLimited, message, until, statusCode);

    private static bool IsJson(string? contentType) =>
        contentType is not null && (contentType.Contains("json", StringComparison.OrdinalIgnoreCase));

    private static string Sanitize(string value)
    {
        var trimmed = new string(value.Where(c => char.IsLetterOrDigit(c) || c is '_' or '=' or ',' or ';' or ' ' or '-').ToArray()).Trim();
        return trimmed.Length <= 120 ? trimmed : trimmed[..120];
    }
}
