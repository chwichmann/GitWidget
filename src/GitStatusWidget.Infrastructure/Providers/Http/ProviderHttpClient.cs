using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using GitStatusWidget.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Infrastructure.Providers.Http;

/// <summary>
/// Options of <see cref="ProviderHttpClient"/>.
/// </summary>
internal sealed class ProviderHttpOptions
{
    /// <summary>Timeout of a single attempt.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Delays before the 2nd, 3rd, … attempt for transient failures (architecture §20: ~1, 3, 8 s).</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8)];

    /// <summary>Random jitter added to each retry delay (fraction of the delay).</summary>
    public double RetryJitter { get; init; } = 0.2;

    /// <summary>Maximum accepted response body size.</summary>
    public long MaxResponseBytes { get; init; } = 32L * 1024 * 1024;
}

/// <summary>
/// A request to a provider API.
/// </summary>
internal sealed class ProviderRequest
{
    public required Uri Uri { get; init; }

    public HttpMethod Method { get; init; } = HttpMethod.Get;

    /// <summary>Validator to send as <c>If-None-Match</c>, exactly as previously returned.</summary>
    public string? IfNoneMatch { get; init; }

    /// <summary>Request body factory (called once per attempt).</summary>
    public Func<HttpContent>? Content { get; init; }

    /// <summary>Adds provider headers (authentication, API version, accept).</summary>
    public Action<HttpRequestHeaders>? ConfigureHeaders { get; init; }

    /// <summary>User-Agent product string (for example <c>GitWidget/0.1 git/2.47.0</c>).</summary>
    public required string UserAgent { get; init; }
}

/// <summary>
/// Provider-specific hooks for classification and rate-limit parsing.
/// </summary>
internal interface IProviderResponsePolicy
{
    /// <summary>Extracts rate-limit information from response headers, if present.</summary>
    RateLimitInfo? ReadRateLimit(ResponseHeaders headers);

    /// <summary>
    /// Classifies a non-success response before the default rules apply. Returns <see langword="null"/> to use
    /// the defaults. <paramref name="bodySnippet"/> is at most a few hundred characters and must not be logged.
    /// </summary>
    ProviderFailureException? Classify(HttpStatusCode statusCode, ResponseHeaders headers, string bodySnippet, DateTimeOffset now);
}

/// <summary>
/// Sends provider API requests with the safety rules of the architecture: no automatic redirects, gateway and
/// login-page detection, per-attempt timeouts, bounded transient retries (never for rate limits), size limits and
/// sanitized logging. Failures are thrown as <see cref="ProviderFailureException"/>.
/// </summary>
internal sealed partial class ProviderHttpClient
{
    private readonly HttpClient _http;
    private readonly IProviderResponsePolicy _policy;
    private readonly ProviderHttpOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public ProviderHttpClient(HttpClient http, IProviderResponsePolicy policy, ProviderHttpOptions options, TimeProvider time, ILogger logger)
    {
        _http = http;
        _policy = policy;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Rate-limit information of the most recent response that carried any.</summary>
    public RateLimitInfo? LastRateLimit { get; private set; }

    /// <summary>Number of HTTP requests sent (diagnostics, tests).</summary>
    public int RequestCount { get; private set; }

    /// <summary>
    /// Creates the primary handler used for provider clients (no redirects, decompression, system proxy).
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        UseCookies = false,
    };

    public async Task<HttpResponseData> SendAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SendOnceAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (ProviderFailureException ex) when (ex.Kind is ProviderFailureKind.Transient or ProviderFailureKind.Offline && attempt < _options.RetryDelays.Count)
            {
                var delay = WithJitter(_options.RetryDelays[attempt]);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    LogTransientRetry(_logger, ex, UrlRedaction.Redact(request.Uri), attempt + 1, delay);
                }

                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<HttpResponseData> SendOnceAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(request.Method, request.Uri);
        message.Headers.TryAddWithoutValidation("User-Agent", request.UserAgent);
        request.ConfigureHeaders?.Invoke(message.Headers);
        if (!string.IsNullOrEmpty(request.IfNoneMatch))
        {
            message.Headers.TryAddWithoutValidation("If-None-Match", request.IfNoneMatch);
        }

        if (request.Content is not null)
        {
            message.Content = request.Content();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        var started = Stopwatch.GetTimestamp();
        RequestCount++;

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderFailureException(ProviderFailureKind.Transient, "The request timed out.");
        }
        catch (HttpRequestException ex)
        {
            var kind = ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
                ? ProviderFailureKind.Offline
                : ProviderFailureKind.Transient;
            throw new ProviderFailureException(kind, $"Network error: {Describe(ex)}", inner: ex);
        }

        using (response)
        {
            var headers = new ResponseHeaders(response.Headers.Concat(response.Content.Headers));
            if (_policy.ReadRateLimit(headers) is { } rateLimit)
            {
                LastRateLimit = rateLimit;
            }

            byte[] body;
            try
            {
                body = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProviderFailureException(ProviderFailureKind.Transient, "Reading the response timed out.");
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderFailureException(ProviderFailureKind.Transient, $"Network error while reading the response: {Describe(ex)}", inner: ex);
            }

            var status = response.StatusCode;
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                LogResponse(_logger, request.Method.Method, UrlRedaction.Redact(request.Uri), (int)status, body.Length, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            if (status == HttpStatusCode.NotModified || ((int)status >= 200 && (int)status < 300))
            {
                if (status != HttpStatusCode.NotModified && status != HttpStatusCode.NoContent && !IsJson(headers.ContentType))
                {
                    throw new ProviderFailureException(
                        ProviderFailureKind.GatewayInterception,
                        $"Received '{ShortContentType(headers.ContentType)}' instead of API JSON. A login page or gateway may be answering; check VPN/network and the User-Agent setting.",
                        statusCode: status);
                }

                return new HttpResponseData(status, headers, body, request.Uri);
            }

            if ((int)status >= 300 && (int)status < 400)
            {
                throw ClassifyRedirect(request.Uri, response, status);
            }

            var snippet = Snippet(body);
            throw _policy.Classify(status, headers, snippet, _time.GetUtcNow()) ?? DefaultClassification(status, headers, snippet);
        }
    }

    private ProviderFailureException DefaultClassification(HttpStatusCode status, ResponseHeaders headers, string snippet)
    {
        var retryAfter = ParseRetryAfter(headers, _time.GetUtcNow());
        return status switch
        {
            HttpStatusCode.Unauthorized => new ProviderFailureException(ProviderFailureKind.AuthenticationFailed, "The token was rejected (401). It may be invalid, revoked or expired.", statusCode: status),
            HttpStatusCode.Forbidden when retryAfter is not null => new ProviderFailureException(ProviderFailureKind.RateLimited, "The provider asked to wait (rate limit).", retryAfter, status),
            HttpStatusCode.Forbidden => new ProviderFailureException(ProviderFailureKind.AuthorizationFailed, "Access denied (403). The token lacks a required permission or role.", statusCode: status),
            HttpStatusCode.NotFound or HttpStatusCode.Gone => new ProviderFailureException(ProviderFailureKind.NotFound, "Repository or resource not found, or not visible with this token.", statusCode: status),
            HttpStatusCode.TooManyRequests => new ProviderFailureException(ProviderFailureKind.RateLimited, "Too many requests (429).", retryAfter ?? _time.GetUtcNow().AddMinutes(1), status),
            HttpStatusCode.RequestTimeout => new ProviderFailureException(ProviderFailureKind.Transient, "The server timed out (408).", statusCode: status),
            >= HttpStatusCode.InternalServerError => new ProviderFailureException(ProviderFailureKind.Transient, $"Server error ({(int)status}).", statusCode: status),
            _ when !IsJson(headers.ContentType) && snippet.Contains("<html", StringComparison.OrdinalIgnoreCase) =>
                new ProviderFailureException(ProviderFailureKind.GatewayInterception, $"HTTP {(int)status} with an HTML page instead of API JSON.", statusCode: status),
            _ => new ProviderFailureException(ProviderFailureKind.Malformed, $"Unexpected HTTP status {(int)status}.", statusCode: status),
        };
    }

    private static ProviderFailureException ClassifyRedirect(Uri requestUri, HttpResponseMessage response, HttpStatusCode status)
    {
        var location = response.Headers.Location;
        if (location is null)
        {
            return new ProviderFailureException(ProviderFailureKind.Malformed, $"Redirect ({(int)status}) without a location.", statusCode: status);
        }

        var target = location.IsAbsoluteUri ? location : new Uri(requestUri, location);
        var sameHost = string.Equals(target.Host, requestUri.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.Scheme, requestUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && target.Port == requestUri.Port;
        if (!sameHost)
        {
            return new ProviderFailureException(
                ProviderFailureKind.GatewayInterception,
                $"Redirected to another host ({target.Host}). A login portal or gateway is intercepting API calls; check VPN/DNS and the User-Agent setting.",
                statusCode: status);
        }

        return new ProviderRedirectException(status, target);
    }

    private async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } length && length > _options.MaxResponseBytes)
        {
            throw new ProviderFailureException(ProviderFailureKind.Malformed, $"Response too large ({length} bytes).");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > _options.MaxResponseBytes)
            {
                throw new ProviderFailureException(ProviderFailureKind.Malformed, "Response exceeded the size limit.");
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Parses <c>Retry-After</c> (seconds or HTTP date).</summary>
    public static DateTimeOffset? ParseRetryAfter(ResponseHeaders headers, DateTimeOffset now)
    {
        var value = headers["Retry-After"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return now.AddSeconds(Math.Max(0, seconds));
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }

    private TimeSpan WithJitter(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero || _options.RetryJitter <= 0)
        {
            return delay;
        }

#pragma warning disable CA5394 // Jitter does not need a cryptographic random source.
        var factor = 1 + ((Random.Shared.NextDouble() * 2) - 1) * _options.RetryJitter;
#pragma warning restore CA5394
        return TimeSpan.FromMilliseconds(delay.TotalMilliseconds * factor);
    }

    private static bool IsJson(string? contentType) =>
        contentType is not null
        && (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("+json", StringComparison.OrdinalIgnoreCase));

    private static string ShortContentType(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType) ? "no content type" : contentType.Split(';')[0].Trim();

    private static string Snippet(byte[] body) =>
        System.Text.Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 512));

    private static string Describe(HttpRequestException ex) =>
        ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "host name could not be resolved",
            HttpRequestError.ConnectionError => "connection failed",
            HttpRequestError.SecureConnectionError => "TLS handshake failed (certificate?)",
            HttpRequestError.ProxyTunnelError => "proxy tunnel failed",
            _ => ex.HttpRequestError.ToString(),
        };

    [LoggerMessage(EventId = 1100, Level = LogLevel.Debug, Message = "{Method} {Url} -> {Status} ({Bytes} bytes, {ElapsedMs} ms)")]
    private static partial void LogResponse(ILogger logger, string method, string url, int status, int bytes, long elapsedMs);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Debug, Message = "Transient failure for {Url}, retry {Attempt} in {Delay}")]
    private static partial void LogTransientRetry(ILogger logger, Exception exception, string url, int attempt, TimeSpan delay);
}
