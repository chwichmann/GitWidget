using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Infrastructure.Providers.Http;

namespace GitStatusWidget.Infrastructure.Providers.GitLab;

/// <summary>
/// Per-fetch access to one GitLab instance: URL building below a (possibly relative) root, the
/// <c>PRIVATE-TOKEN</c> header, pagination and project resolution including moved projects.
/// </summary>
internal sealed partial class GitLabApi
{
    /// <summary>Upper bound of pages per list (100 items each) — protects against pagination loops.</summary>
    public const int MaxPages = 1000;

    public const int PageSize = 100;

    private readonly Secret _token;

    public GitLabApi(ProviderHttpClient client, Uri serverUri, Secret token, string userAgent)
    {
        Client = client;
        ServerUri = serverUri;
        _token = token;
        UserAgent = userAgent;

        // ServerUri ends with '/', so relative paths (without a leading slash) stay below a relative URL root.
        ApiBase = new Uri(serverUri, "api/v4/");
        GraphQlEndpoint = new Uri(serverUri, "api/graphql");
    }

    public ProviderHttpClient Client { get; }

    public Uri ServerUri { get; }

    public Uri ApiBase { get; }

    public Uri GraphQlEndpoint { get; }

    public string UserAgent { get; }

    /// <summary>Number of pages fetched by list calls (diagnostics).</summary>
    public int PageCount { get; private set; }

    /// <summary>Encodes a project path for <c>projects/:id</c> exactly once (<c>group%2Fsub%2Fproject</c>).</summary>
    public static string EncodeProjectPath(string fullPath) => Uri.EscapeDataString(fullPath.Trim().Trim('/'));

    public static string Query(params (string Name, string? Value)[] parameters) =>
        string.Join('&', parameters.Where(p => p.Value is not null).Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}"));

    public Uri Rest(string relativePath, string? query = null)
    {
        if (relativePath.StartsWith('/'))
        {
            throw new ArgumentException("Relative API paths must not start with '/' (the relative URL root would be lost).", nameof(relativePath));
        }

        return new Uri(ApiBase, string.IsNullOrEmpty(query) ? relativePath : relativePath + "?" + query);
    }

    public void ConfigureHeaders(HttpRequestHeaders headers)
    {
        // Loaded just before the request; never logged (ProviderHttpClient logs only redacted URLs).
        headers.TryAddWithoutValidation("PRIVATE-TOKEN", _token.Reveal());
        headers.Accept.ParseAdd("application/json");
    }

    public Task<HttpResponseData> SendAsync(Uri uri, string? ifNoneMatch, CancellationToken cancellationToken) =>
        Client.SendAsync(
            new ProviderRequest
            {
                Uri = uri,
                UserAgent = UserAgent,
                IfNoneMatch = ifNoneMatch,
                ConfigureHeaders = ConfigureHeaders,
            },
            cancellationToken);

    public async Task<T> GetAsync<T>(string relativePath, string? query, CancellationToken cancellationToken)
    {
        var response = await SendAsync(Rest(relativePath, query), null, cancellationToken).ConfigureAwait(false);
        if (response.IsNotModified)
        {
            throw new ProviderResponseException("Unexpected 304 for an unconditional request.");
        }

        return response.Deserialize<T>(ProviderJson.SnakeCase);
    }

    /// <summary>
    /// Reads every page of a list. Follows <c>Link: rel="next"</c> (keyset cursors and offset pages) and otherwise
    /// <c>X-Next-Page</c>; stops on a missing/empty next marker or an empty page. The next request always keeps the
    /// path of the first request and takes only the query from the <c>Link</c> URL, so a gateway or mis-configured
    /// <c>external_url</c> cannot redirect authenticated calls to another host. Any failing page fails the whole list.
    /// </summary>
    public async Task<List<T>> GetAllPagesAsync<T>(string relativePath, string query, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        var uri = Rest(relativePath, query);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; ; page++)
        {
            if (page > MaxPages || !seen.Add(uri.AbsoluteUri))
            {
                throw new ProviderFailureException(ProviderFailureKind.Malformed, "GitLab pagination did not terminate.");
            }

            var response = await SendAsync(uri, null, cancellationToken).ConfigureAwait(false);
            PageCount++;
            if (response.IsNotModified)
            {
                throw new ProviderResponseException("Unexpected 304 for an unconditional request.");
            }

            var pageItems = response.Deserialize<List<T>>(ProviderJson.SnakeCase);
            if (pageItems.Count == 0)
            {
                return items;
            }

            items.AddRange(pageItems);
            var next = NextPage(uri, response.Headers);
            if (next is null)
            {
                return items;
            }

            uri = next;
        }
    }

    /// <summary>
    /// Resolves a project by numeric id or encoded path. A <c>301</c> (moved or renamed project) pointing to
    /// <c>…/api/v4/projects/&lt;id&gt;</c> on the same host is followed once by numeric id.
    /// </summary>
    /// <returns>The project and whether a redirect was followed.</returns>
    public async Task<(GitLabProjectDto Project, bool Moved)> GetProjectAsync(string idOrEncodedPath, CancellationToken cancellationToken)
    {
        try
        {
            return (await GetAsync<GitLabProjectDto>($"projects/{idOrEncodedPath}", null, cancellationToken).ConfigureAwait(false), false);
        }
        catch (ProviderRedirectException redirect) when (redirect.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            var match = MovedProjectPattern().Match(redirect.Location.AbsolutePath);
            if (!match.Success)
            {
                throw new ProviderFailureException(
                    ProviderFailureKind.GatewayInterception,
                    $"Unexpected redirect ({(int)redirect.StatusCode!.Value}) of the project request. A gateway may be intercepting API calls.",
                    statusCode: redirect.StatusCode);
            }

            var project = await GetAsync<GitLabProjectDto>($"projects/{match.Groups["id"].Value}", null, cancellationToken).ConfigureAwait(false);
            return (project, true);
        }
    }

    private static Uri? NextPage(Uri current, ResponseHeaders headers)
    {
        var path = current.GetLeftPart(UriPartial.Path);
        if (LinkHeader.GetRelation(headers["Link"], "next") is { } link)
        {
            return string.IsNullOrEmpty(link.Query) ? null : new Uri(path + link.Query);
        }

        var nextPage = headers["X-Next-Page"];
        if (string.IsNullOrWhiteSpace(nextPage) || !int.TryParse(nextPage, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        return new Uri(path + "?" + WithPage(current.Query, number));
    }

    private static string WithPage(string query, int page)
    {
        var builder = new StringBuilder();
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("page=", StringComparison.Ordinal))
            {
                continue;
            }

            builder.Append(part).Append('&');
        }

        return builder.Append("page=").Append(page.ToString(CultureInfo.InvariantCulture)).ToString();
    }

    [GeneratedRegex(@"/api/v4/projects/(?<id>\d+)/?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MovedProjectPattern();
}
