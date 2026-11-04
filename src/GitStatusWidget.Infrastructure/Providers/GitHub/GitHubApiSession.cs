using System.Net;
using System.Text;
using System.Text.Json;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Infrastructure.Providers.Http;

namespace GitStatusWidget.Infrastructure.Providers.GitHub;

/// <summary>
/// Request helper for one fetch or connection test: GitHub headers, same-host redirect following (renamed or
/// transferred repositories), cursor/offset pagination via <c>Link</c>, and read-only GraphQL queries.
/// </summary>
internal sealed class GitHubApiSession
{
    /// <summary>Safety cap for complete list pagination (github-api.md §8.2).</summary>
    public const int MaxPages = 500;

    private const int MaxRedirects = 3;

    private readonly ProviderHttpClient _http;
    private readonly GitHubResponsePolicy _policy;
    private readonly Action<System.Net.Http.Headers.HttpRequestHeaders> _headers;
    private readonly string _userAgent;
    private readonly TimeProvider _time;

    public GitHubApiSession(ProviderHttpClient http, GitHubResponsePolicy policy, GitHubEndpoints endpoints, Secret token, string userAgent, TimeProvider time)
    {
        _http = http;
        _policy = policy;
        Endpoints = endpoints;
        _headers = GitHubEndpoints.Headers(token);
        _userAgent = userAgent;
        _time = time;
    }

    public GitHubEndpoints Endpoints { get; }

    /// <summary>Requests sent so far (diagnostics).</summary>
    public int RequestCount => _http.RequestCount;

    /// <summary>
    /// GET with optional validator. Same-host redirects into the REST API (301/302/307/308, e.g. renamed
    /// repositories) are followed explicitly so the token is never sent to another host.
    /// </summary>
    public async Task<HttpResponseData> GetAsync(Uri uri, string? ifNoneMatch, CancellationToken cancellationToken)
    {
        for (var hop = 0; ; hop++)
        {
            try
            {
                return await _http.SendAsync(
                    new ProviderRequest { Uri = uri, IfNoneMatch = ifNoneMatch, UserAgent = _userAgent, ConfigureHeaders = _headers },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ProviderRedirectException ex) when (hop < MaxRedirects && IsFollowable(ex.StatusCode) && Endpoints.IsRestUri(ex.Location))
            {
                uri = ex.Location;
            }
        }
    }

    /// <summary>
    /// Reads a list resource, following <c>rel="next"</c> while <paramref name="shouldContinue"/> returns true for the
    /// last page. A failure of any page after the first fails the whole read; malformed or missing later pages are
    /// reported as <see cref="ProviderFailureKind.Partial"/>.
    /// </summary>
    public async Task<PagedResult<T>> GetPagesAsync<T>(
        Uri first,
        string? ifNoneMatch,
        string resource,
        Func<IReadOnlyList<T>, bool>? shouldContinue,
        int maxPages,
        CancellationToken cancellationToken)
    {
        var response = await GetAsync(first, ifNoneMatch, cancellationToken).ConfigureAwait(false);
        if (response.IsNotModified)
        {
            return new PagedResult<T>(true, [], 0, ifNoneMatch, Truncated: false, ServerDate(response));
        }

        var etag = response.Headers.ETag;
        var date = ServerDate(response);
        var page = DeserializeList<T>(response);
        var items = new List<T>(page);
        var pages = 1;
        var truncated = false;
        var next = LinkHeader.GetRelation(response.Headers["Link"], "next");
        while (next is not null && (shouldContinue?.Invoke(page) ?? true))
        {
            if (pages >= maxPages)
            {
                truncated = true;
                break;
            }

            if (!Endpoints.IsRestUri(next))
            {
                throw new ProviderFailureException(ProviderFailureKind.Partial, $"Pagination of {resource} pointed outside the GitHub API; the data is incomplete.");
            }

            pages++;
            try
            {
                var nextResponse = await GetAsync(next, null, cancellationToken).ConfigureAwait(false);
                if (nextResponse.IsNotModified)
                {
                    throw new ProviderFailureException(ProviderFailureKind.Partial, $"Page {pages} of {resource} returned 304 without a validator.");
                }

                page = DeserializeList<T>(nextResponse);
                next = LinkHeader.GetRelation(nextResponse.Headers["Link"], "next");
            }
            catch (ProviderFailureException ex) when (ex.Kind is ProviderFailureKind.Malformed or ProviderFailureKind.NotFound)
            {
                throw new ProviderFailureException(ProviderFailureKind.Partial, $"Page {pages} of {resource} failed: {ex.Message}", ex.RetryAfter, ex.StatusCode, ex);
            }
            catch (ProviderResponseException ex)
            {
                throw new ProviderFailureException(ProviderFailureKind.Partial, $"Page {pages} of {resource} failed: {ex.Message}", inner: ex);
            }

            items.AddRange(page);
        }

        return new PagedResult<T>(false, items, pages, etag, truncated, date);
    }

    /// <summary>
    /// Runs a read-only GraphQL query (POST is used for queries only, never mutations). GitHub reports errors with
    /// HTTP 200 and an <c>errors</c> array; any error fails the request (no partial data).
    /// </summary>
    public async Task<TData> QueryAsync<TData>(string query, IReadOnlyDictionary<string, object?> variables, CancellationToken cancellationToken)
        where TData : class
    {
        if (query.TrimStart().StartsWith("mutation", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("GraphQL mutations are not allowed (read-only integration).");
        }

        var payload = JsonSerializer.Serialize(new GraphQlRequest(query, variables), ProviderJson.CamelCase);
        var response = await _http.SendAsync(
            new ProviderRequest
            {
                Uri = Endpoints.GraphQl,
                Method = HttpMethod.Post,
                UserAgent = _userAgent,
                ConfigureHeaders = _headers,
                Content = () => new StringContent(payload, Encoding.UTF8, "application/json"),
            },
            cancellationToken).ConfigureAwait(false);

        var envelope = response.Deserialize<GitHubGraphQlResponse<TData>>(ProviderJson.CamelCase);
        if (envelope.Errors is { Count: > 0 } errors)
        {
            throw ClassifyGraphQlError(errors[0]);
        }

        return envelope.Data ?? throw new ProviderFailureException(ProviderFailureKind.Malformed, "GitHub GraphQL response contained no data.");
    }

    private ProviderFailureException ClassifyGraphQlError(GitHubGraphQlError error)
    {
        var message = Truncate(error.Message);
        switch (error.Type?.ToUpperInvariant())
        {
            case "RATE_LIMITED":
                var now = _time.GetUtcNow();
                var reset = _policy.ForResource("graphql")?.ResetAt;
                return new ProviderFailureException(
                    ProviderFailureKind.RateLimited,
                    "GitHub GraphQL rate limit exhausted.",
                    reset is { } value && value > now ? value : now + GitHubResponsePolicy.SecondaryLimitWait);
            case "FORBIDDEN" or "INSUFFICIENT_SCOPES":
                return new ProviderFailureException(ProviderFailureKind.AuthorizationFailed, $"GitHub GraphQL access denied: {message}");
            case "NOT_FOUND":
                return new ProviderFailureException(ProviderFailureKind.NotFound, $"GitHub GraphQL: {message}");
            default:
                return new ProviderFailureException(ProviderFailureKind.Malformed, $"GitHub GraphQL error: {message}");
        }
    }

    private static List<T> DeserializeList<T>(HttpResponseData response) =>
        response.Deserialize<List<T>>(ProviderJson.SnakeCase);

    private static DateTimeOffset? ServerDate(HttpResponseData response) =>
        DateTimeOffset.TryParse(response.Headers["Date"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToUniversalTime()
            : null;

    private static bool IsFollowable(HttpStatusCode? status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static string Truncate(string? message) =>
        string.IsNullOrEmpty(message) ? "unknown" : message.Length <= 200 ? message : message[..200];

    private sealed record GraphQlRequest(string Query, IReadOnlyDictionary<string, object?> Variables);
}

/// <summary>Result of a (possibly conditional) paged read.</summary>
/// <param name="NotModified">The first page returned 304.</param>
/// <param name="Items">All items read.</param>
/// <param name="Pages">Pages read.</param>
/// <param name="ETag">Validator of the first page.</param>
/// <param name="Truncated">More pages existed beyond the page cap.</param>
/// <param name="ServerDate">Server <c>Date</c> of the first page.</param>
internal sealed record PagedResult<T>(bool NotModified, IReadOnlyList<T> Items, int Pages, string? ETag, bool Truncated, DateTimeOffset? ServerDate);
