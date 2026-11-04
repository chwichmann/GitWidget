using System.Globalization;
using System.Net;
using System.Text.Json;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Infrastructure.Providers.Http;

namespace GitStatusWidget.Infrastructure.Providers.GitLab;

/// <summary>
/// A classified GitLab error that keeps the machine-readable error code (for example <c>insufficient_scope</c>) so the
/// adapter can tell a scope problem from a disabled feature.
/// </summary>
internal sealed class GitLabApiException : ProviderFailureException
{
    public GitLabApiException(ProviderFailureKind kind, string message, HttpStatusCode statusCode, string? errorCode = null, DateTimeOffset? retryAfter = null)
        : base(kind, message, retryAfter, statusCode)
    {
        ErrorCode = errorCode;
    }

    /// <summary>GitLab <c>error</c> code such as <c>insufficient_scope</c> or <c>invalid_token</c>, if any.</summary>
    public string? ErrorCode { get; }

    public bool IsInsufficientScope =>
        ErrorCode is not null && ErrorCode.StartsWith("insufficient_", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// GitLab-specific response classification (gitlab-api.md §1.4, §11, §12, §19). Handles the error shapes seen on
/// 19.5.1-ee and GitLab.com; <c>RateLimit-*</c> headers are optional (the target instance sends none).
/// </summary>
internal sealed class GitLabResponsePolicy : IProviderResponsePolicy
{
    /// <summary>Back-off used when a 429 carries no retry information (gitlab-api.md §11.1).</summary>
    public static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromSeconds(60);

    public static GitLabResponsePolicy Instance { get; } = new();

    /// <inheritdoc />
    public RateLimitInfo? ReadRateLimit(ResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var limit = headers.GetInt("RateLimit-Limit");
        var remaining = headers.GetInt("RateLimit-Remaining");
        var reset = ReadReset(headers);
        if (limit is null && remaining is null && reset is null)
        {
            return null;
        }

        return new RateLimitInfo(limit, remaining, reset, headers["RateLimit-Name"]);
    }

    /// <inheritdoc />
    public ProviderFailureException? Classify(HttpStatusCode statusCode, ResponseHeaders headers, string bodySnippet, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var body = ErrorBody.Parse(headers.ContentType, bodySnippet);
        switch (statusCode)
        {
            case HttpStatusCode.Unauthorized:
                return Unauthorized(body);

            case HttpStatusCode.Forbidden:
                return Forbidden(headers, body, now);

            case HttpStatusCode.NotFound:
                return NotFound(body);

            case HttpStatusCode.TooManyRequests:
                // The body is plain text ("Retry later") and is never parsed.
                return new GitLabApiException(
                    ProviderFailureKind.RateLimited,
                    "GitLab rate limit reached (429). Polling pauses until the server allows new requests.",
                    statusCode,
                    retryAfter: RetryAt(headers, now));

            case HttpStatusCode.BadRequest:
            case HttpStatusCode.UnprocessableEntity:
                return new GitLabApiException(
                    ProviderFailureKind.Malformed,
                    $"GitLab rejected the request ({(int)statusCode}): {body.Text ?? "bad request"}. The server version may not support a parameter.",
                    statusCode,
                    body.Error);

            default:
                // 408, 5xx, other statuses: shared defaults (transient retry, HTML detection).
                return null;
        }
    }

    private static GitLabApiException Unauthorized(ErrorBody body)
    {
        var description = body.ErrorDescription ?? body.Message ?? string.Empty;
        string message;
        if (description.Contains("expired", StringComparison.OrdinalIgnoreCase))
        {
            message = "The GitLab token has expired (401). Create a new token with the read_api scope and update it in Settings.";
        }
        else if (description.Contains("revoked", StringComparison.OrdinalIgnoreCase))
        {
            message = "The GitLab token was revoked (401). Create a new token with the read_api scope and update it in Settings.";
        }
        else if ((body.Error ?? string.Empty).Contains("dpop", StringComparison.OrdinalIgnoreCase))
        {
            message = "GitLab requires DPoP-bound requests for this token's owner (401), which GitWidget does not support. Use a project access token.";
        }
        else
        {
            message = "The GitLab token was rejected (401 Unauthorized). It may be invalid, revoked or expired.";
        }

        return new GitLabApiException(ProviderFailureKind.AuthenticationFailed, message, HttpStatusCode.Unauthorized, body.Error);
    }

    private static GitLabApiException Forbidden(ResponseHeaders headers, ErrorBody body, DateTimeOffset now)
    {
        if (body.Error is not null && body.Error.StartsWith("insufficient_", StringComparison.OrdinalIgnoreCase))
        {
            return new GitLabApiException(
                ProviderFailureKind.AuthorizationFailed,
                "The GitLab token's scope is insufficient (403); GitWidget needs read_api.",
                HttpStatusCode.Forbidden,
                body.Error);
        }

        // Some throttled endpoints answer 403 instead of 429: detectable by headers or a plain-text body.
        if (headers.GetInt("RateLimit-Remaining") == 0 || headers["Retry-After"] is not null || body.IsPlainText)
        {
            return new GitLabApiException(
                ProviderFailureKind.RateLimited,
                "GitLab throttled the request (403). Polling pauses until the server allows new requests.",
                HttpStatusCode.Forbidden,
                retryAfter: RetryAt(headers, now));
        }

        if (body.IsHtml)
        {
            return new GitLabApiException(
                ProviderFailureKind.GatewayInterception,
                "HTTP 403 with an HTML page instead of API JSON. A gateway may be blocking the request; check VPN/network and the User-Agent setting.",
                HttpStatusCode.Forbidden);
        }

        return new GitLabApiException(
            ProviderFailureKind.AuthorizationFailed,
            "Access denied (403). The token needs at least the Reporter role and the read_api scope.",
            HttpStatusCode.Forbidden,
            body.Error);
    }

    private static GitLabApiException NotFound(ErrorBody body)
    {
        // {"error":"404 Not Found"}: no API route matched (wrong relative root, or a proxy decoded %2F).
        if (body.Error is not null && body.Error.StartsWith("404", StringComparison.Ordinal))
        {
            return new GitLabApiException(
                ProviderFailureKind.Configuration,
                "GitLab API route not found (404). Check the server URL and relative URL root, and that no reverse proxy decodes %2F in project paths.",
                HttpStatusCode.NotFound,
                body.Error);
        }

        if (body.IsHtml)
        {
            return new GitLabApiException(
                ProviderFailureKind.Configuration,
                "The server returned an HTML 404 page instead of the GitLab API. Check the server URL and relative URL root.",
                HttpStatusCode.NotFound);
        }

        // {"message":"404 Project Not Found"} and friends: missing, or hidden from this token.
        var what = body.Message is { } message && message.StartsWith("404 ", StringComparison.Ordinal)
            ? message[4..]
            : "Not Found";
        return new GitLabApiException(
            ProviderFailureKind.NotFound,
            $"GitLab reports '{Truncate(what, 80)}' (404): the project does not exist or is not visible with this token.",
            HttpStatusCode.NotFound,
            body.Error);
    }

    private static DateTimeOffset RetryAt(ResponseHeaders headers, DateTimeOffset now)
    {
        if (ProviderHttpClient.ParseRetryAfter(headers, now) is { } retryAfter)
        {
            return retryAfter;
        }

        return ReadReset(headers) is { } reset && reset > now ? reset : now + DefaultRateLimitBackoff;
    }

    private static DateTimeOffset? ReadReset(ResponseHeaders headers)
    {
        if (headers.GetLong("RateLimit-Reset") is { } unixSeconds and > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        return DateTimeOffset.TryParse(headers["RateLimit-ResetTime"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var resetTime)
            ? resetTime
            : null;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>
    /// Defensive parse of a GitLab error body: <c>message</c> (string or object), <c>error</c>,
    /// <c>error_description</c>. Never logged.
    /// </summary>
    private sealed record ErrorBody(string? Message, string? Error, string? ErrorDescription, bool IsHtml, bool IsPlainText)
    {
        public string? Text => Truncate(Error ?? Message ?? ErrorDescription ?? string.Empty, 200) is { Length: > 0 } text ? text : null;

        public static ErrorBody Parse(string? contentType, string snippet)
        {
            var isJson = contentType is not null && contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
            var isHtml = (contentType?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? false)
                || snippet.Contains("<html", StringComparison.OrdinalIgnoreCase);
            var trimmed = snippet.TrimStart();
            if (!isJson && !trimmed.StartsWith('{'))
            {
                var isText = !isHtml && trimmed.Length > 0;
                return new ErrorBody(null, null, null, isHtml, isText);
            }

            try
            {
                using var document = JsonDocument.Parse(snippet);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return new ErrorBody(null, null, null, false, false);
                }

                return new ErrorBody(ReadText(root, "message"), ReadText(root, "error"), ReadText(root, "error_description"), false, false);
            }
            catch (JsonException)
            {
                // Truncated or broken JSON: fall back to substring checks on the raw snippet.
                return new ErrorBody(
                    null,
                    snippet.Contains("insufficient_scope", StringComparison.Ordinal) ? "insufficient_scope" : null,
                    snippet.Contains("expired", StringComparison.OrdinalIgnoreCase) ? "expired" : null,
                    false,
                    false);
            }
        }

        private static string? ReadText(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),

                // Validation errors: {"message":{"field":["msg"]}} → keep the field names only.
                JsonValueKind.Object => string.Join(", ", value.EnumerateObject().Select(p => p.Name)),
                _ => null,
            };
        }
    }
}
