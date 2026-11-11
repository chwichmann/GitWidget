using System.Net;
using System.Text.Json.Serialization;
using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Application.Updates;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Infrastructure.Providers;
using GitStatusWidget.Infrastructure.Providers.GitHub;
using GitStatusWidget.Infrastructure.Providers.Http;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Infrastructure.Updates;

/// <summary>Where GitWidget's own releases are published.</summary>
public sealed class ReleaseFeedOptions
{
    /// <summary>Repository as <c>owner/name</c>.</summary>
    public string Repository { get; init; } = "chwichmann/GitWidget";

    /// <summary>GitHub REST base (overridable for tests).</summary>
    public Uri ApiBase { get; init; } = new("https://api.github.com/");

    /// <summary>Name of the release file for the running system, e.g. <c>GitWidget-win-x64.zip</c> (issue #19).</summary>
    public string AssetName { get; init; } = DefaultAssetName();

    /// <summary>Release file name as the CI publishes it for an operating system and architecture.</summary>
    /// <returns>The file name; for systems without a published build a name no release carries.</returns>
    public static string DefaultAssetName()
    {
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return OperatingSystem.IsWindows() ? $"GitWidget-win-{architecture}.zip"
            : OperatingSystem.IsMacOS() ? $"GitWidget-osx-{architecture}.zip"
            : $"GitWidget-linux-{architecture}.tar.gz";
    }
}

/// <summary>
/// Reads GitWidget releases from GitHub (issue #7). Private repositories need authentication: when the same
/// repository is configured for monitoring, its token is reused; otherwise the request is anonymous. Drafts and tags
/// that are not versions are ignored. Read-only.
/// </summary>
internal sealed class GitHubReleaseFeed : IReleaseFeed
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICredentialStore _credentials;
    private readonly ISynchronizationController _controller;
    private readonly TimeProvider _time;
    private readonly ILogger<GitHubReleaseFeed> _logger;
    private readonly ReleaseFeedOptions _options;
    private readonly ProviderHttpOptions _httpOptions;

    /// <summary>Creates the feed.</summary>
    /// <param name="httpClientFactory">HTTP client factory (uses the <c>GitHub</c> client).</param>
    /// <param name="credentials">Credential store.</param>
    /// <param name="controller">Settings source (for token reuse).</param>
    /// <param name="time">Time provider.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Feed options.</param>
    /// <param name="httpOptions">HTTP options (tests use zero retry delays).</param>
    public GitHubReleaseFeed(
        IHttpClientFactory httpClientFactory,
        ICredentialStore credentials,
        ISynchronizationController controller,
        TimeProvider time,
        ILogger<GitHubReleaseFeed> logger,
        ReleaseFeedOptions? options = null,
        ProviderHttpOptions? httpOptions = null)
    {
        _httpClientFactory = httpClientFactory;
        _credentials = credentials;
        _controller = controller;
        _time = time;
        _logger = logger;
        _options = options ?? new ReleaseFeedOptions();
        _httpOptions = httpOptions ?? new ProviderHttpOptions();
    }

    /// <inheritdoc />
    public string Source => _options.Repository;

    /// <inheritdoc />
    public async Task<ReleaseFeedResult> GetReleasesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var token = await FindTokenAsync(cancellationToken).ConfigureAwait(false);
            var client = new ProviderHttpClient(
                _httpClientFactory.CreateClient(GitHubStatusProvider.HttpClientName), new GitHubResponsePolicy(), _httpOptions, _time, _logger);
            var response = await client.SendAsync(
                new ProviderRequest
                {
                    Uri = new Uri(_options.ApiBase, $"repos/{_options.Repository}/releases?per_page=30"),
                    UserAgent = ProviderBoundary.DefaultUserAgent,
                    ConfigureHeaders = headers =>
                    {
                        if (token is not null)
                        {
                            GitHubEndpoints.Headers(token)(headers);
                        }
                        else
                        {
                            headers.TryAddWithoutValidation("Accept", GitHubEndpoints.MediaType);
                            headers.TryAddWithoutValidation("X-GitHub-Api-Version", GitHubEndpoints.ApiVersion);
                        }
                    },
                },
                cancellationToken).ConfigureAwait(false);

            var releases = response.Deserialize<List<ReleaseDto>>(ProviderJson.SnakeCase)
                .Where(r => r is { Draft: false } && Uri.TryCreate(r.HtmlUrl, UriKind.Absolute, out _))
                .Select(r => SemanticVersion.TryParse(r.TagName, out var version)
                    ? new ReleaseInfo(version, r.TagName!, new Uri(r.HtmlUrl!), r.Prerelease, r.PublishedAt) { Asset = SelectAsset(r.Assets) }
                    : null)
                .OfType<ReleaseInfo>()
                .ToList();
            return new ReleaseFeedResult.Success(releases);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderFailureException ex)
        {
            var reason = ex.StatusCode switch
            {
                HttpStatusCode.NotFound => $"Releases of {_options.Repository} are not accessible (private repository? Add it as a monitored GitHub repository so its token can be used).",
                HttpStatusCode.Forbidden when ex.Kind == Application.Abstractions.ProviderFailureKind.AuthorizationFailed =>
                    $"The token of {_options.Repository} cannot read releases. Give the fine-grained token the repository permission \"Contents: Read-only\" (monitoring itself does not need it).",
                _ => $"Could not read releases: {ex.Message}",
            };
            return new ReleaseFeedResult.Unavailable(reason);
        }
        catch (ProviderResponseException ex)
        {
            return new ReleaseFeedResult.Unavailable($"Unexpected release list: {ex.Message}");
        }
    }

    private async Task<Secret?> FindTokenAsync(CancellationToken cancellationToken)
    {
        var apiIsGitHubCom = _options.ApiBase.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);
        var configuration = _controller.Settings.Repositories.FirstOrDefault(r =>
            r.Provider == RepositoryProviderKind.GitHub
            && string.Equals(r.FullPath, _options.Repository, StringComparison.OrdinalIgnoreCase)
            && (!apiIsGitHubCom || IsGitHubCom(r)));
        if (configuration is null || !_credentials.IsAvailable)
        {
            return null;
        }

        return await _credentials.ReadAsync(configuration.CredentialReference, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsGitHubCom(RepositoryConfiguration configuration) =>
        configuration.ServerUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        || configuration.ServerUri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Picks the download for the running system (issue #19); its checksum comes as <c>sha256:&lt;hex&gt;</c>.</summary>
    private ReleaseAsset? SelectAsset(List<AssetDto>? assets)
    {
        var asset = assets?.FirstOrDefault(a => string.Equals(a?.Name, _options.AssetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null || asset.Size <= 0 || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var url))
        {
            return null;
        }

        const string Prefix = "sha256:";
        var digest = asset.Digest?.Trim();
        var sha256 = digest is not null && digest.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && digest.Length == Prefix.Length + 64
            ? digest[Prefix.Length..]
            : null;
        return new ReleaseAsset(asset.Name!, url, asset.Size, sha256);
    }

    private sealed record ReleaseDto(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        bool Draft,
        bool Prerelease,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        List<AssetDto>? Assets = null);

    private sealed record AssetDto(
        string? Name,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl,
        long Size,
        string? Digest);
}
