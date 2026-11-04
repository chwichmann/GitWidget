using System.Net.Http.Headers;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Infrastructure.Providers.Http;

namespace GitStatusWidget.Infrastructure.Providers.GitHub;

/// <summary>
/// API locations of a GitHub instance: <c>api.github.com</c> for GitHub.com, <c>{server}/api/v3/</c> and
/// <c>{server}/api/graphql</c> for GitHub Enterprise Server.
/// </summary>
internal sealed class GitHubEndpoints
{
    /// <summary>REST API version sent with every request (G17).</summary>
    public const string ApiVersion = "2026-03-10";

    /// <summary>Media type sent as <c>Accept</c>.</summary>
    public const string MediaType = "application/vnd.github+json";

    private GitHubEndpoints(Uri restBase, Uri graphQl, string host)
    {
        RestBase = restBase;
        GraphQl = graphQl;
        Host = host;
    }

    /// <summary>REST base URI (ends with <c>/</c>).</summary>
    public Uri RestBase { get; }

    /// <summary>GraphQL endpoint.</summary>
    public Uri GraphQl { get; }

    /// <summary>Host of the instance used in <see cref="Domain.Repositories.RepositoryIdentity.Host"/>.</summary>
    public string Host { get; }

    /// <summary>Resolves the endpoints of a configuration (validates the server URI and the owner/name).</summary>
    public static GitHubEndpoints From(RepositoryConfiguration configuration)
    {
        var server = ProviderBoundary.ValidateServerUri(configuration);
        ValidatePath(configuration.OwnerPath, "owner");
        ValidatePath(configuration.RepositoryName, "repository name");

        var host = server.Host.ToLowerInvariant();
        if (host is "github.com" or "www.github.com" or "api.github.com")
        {
            return new GitHubEndpoints(new Uri("https://api.github.com/"), new Uri("https://api.github.com/graphql"), "github.com");
        }

        // GitHub Enterprise Server (and test servers): REST below /api/v3/, GraphQL at /api/graphql.
        return new GitHubEndpoints(new Uri(server, "api/v3/"), new Uri(server, "api/graphql"), host);
    }

    /// <summary>Relative REST path of a repository, <c>repos/{owner}/{name}</c>.</summary>
    public static string RepositoryPath(string owner, string name) =>
        $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";

    /// <summary>Absolute REST URI of a relative path.</summary>
    public Uri Rest(string relative) => new(RestBase, relative);

    /// <summary>Whether <paramref name="uri"/> points into this instance's REST API (pagination, redirects).</summary>
    public bool IsRestUri(Uri uri) =>
        uri.IsAbsoluteUri
        && string.Equals(uri.Scheme, RestBase.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Authority, RestBase.Authority, StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith(RestBase.AbsolutePath, StringComparison.Ordinal);

    /// <summary>Relative form of an API URI for conditional-request keys (never contains credentials).</summary>
    public string RelativeKey(Uri uri) =>
        IsRestUri(uri) ? uri.PathAndQuery[RestBase.AbsolutePath.Length..] : UrlRedaction.Redact(uri);

    /// <summary>Returns a header configurator adding authentication, accept and API version.</summary>
    /// <param name="token">Token; only revealed when the request is built.</param>
    public static Action<HttpRequestHeaders> Headers(Secret token) => headers =>
    {
        headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Reveal());
        headers.TryAddWithoutValidation("Accept", MediaType);
        headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
    };

    private static void ValidatePath(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/', StringComparison.Ordinal) || value.Trim() != value)
        {
            throw new ProviderFailureException(
                ProviderFailureKind.Configuration,
                $"The GitHub {what} is invalid. Configure GitHub repositories as owner and repository name (owner/name).");
        }
    }
}
