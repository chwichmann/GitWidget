using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Application.Updates;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Infrastructure.Providers.Http;
using GitStatusWidget.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace GitStatusWidget.Infrastructure.Tests.Updates;

public sealed class GitHubReleaseFeedTests : IDisposable
{
    private const string ReleasesJson = """
        [
          { "tag_name": "v0.2.0-beta.1", "html_url": "https://github.com/chwichmann/GitWidget/releases/tag/v0.2.0-beta.1", "draft": false, "prerelease": true, "published_at": "2026-11-05T10:00:00Z" },
          { "tag_name": "v0.1.2", "html_url": "https://github.com/chwichmann/GitWidget/releases/tag/v0.1.2", "draft": false, "prerelease": false, "published_at": "2026-11-04T10:00:00Z" },
          { "tag_name": "v9.9.9", "html_url": "https://github.com/chwichmann/GitWidget/releases/tag/v9.9.9", "draft": true, "prerelease": false, "published_at": null },
          { "tag_name": "nightly", "html_url": "https://github.com/chwichmann/GitWidget/releases/tag/nightly", "draft": false, "prerelease": true, "published_at": null }
        ]
        """;

    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly HttpClient _http = new(ProviderHttpClient.CreateHandler());

    public void Dispose()
    {
        _http.Dispose();
        _server.Stop();
        _server.Dispose();
    }

    private GitHubReleaseFeed Feed(AppSettings settings, ICredentialStore? store = null) => new(
        new SingleClientFactory(_http),
        store ?? new Store(),
        new Controller(settings),
        TimeProvider.System,
        NullLogger<GitHubReleaseFeed>.Instance,
        new ReleaseFeedOptions { ApiBase = new Uri($"{_server.Url}/api/v3/"), AssetName = "GitWidget-test-rid.zip" },
        new ProviderHttpOptions { RetryDelays = [] });

    private void ServeReleases(int status = 200, string body = ReleasesJson) =>
        _server.Given(Request.Create().WithPath("/api/v3/repos/chwichmann/GitWidget/releases").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    [Fact]
    public async Task Releases_are_parsed_without_drafts_and_non_version_tags()
    {
        ServeReleases();

        var result = await Feed(new AppSettings()).GetReleasesAsync(CancellationToken.None);

        var releases = result.Should().BeOfType<ReleaseFeedResult.Success>().Subject.Releases;
        releases.Select(r => r.Tag).Should().BeEquivalentTo(["v0.2.0-beta.1", "v0.1.2"]);
        releases.Single(r => r.Tag == "v0.1.2").PageUrl.ToString().Should().Be("https://github.com/chwichmann/GitWidget/releases/tag/v0.1.2");
    }

    [Fact]
    public async Task Download_for_the_running_system_is_read_with_its_checksum()
    {
        // Issue #19. Shape of the live release list: every asset carries "digest": "sha256:<hex>".
        var digest = new string('d', 64);
        ServeReleases(body: $$"""
            [
              { "tag_name": "v0.2.0", "html_url": "https://github.com/chwichmann/GitWidget/releases/tag/v0.2.0", "draft": false, "prerelease": false,
                "assets": [
                  { "name": "GitWidget-linux-x64.tar.gz", "browser_download_url": "https://github.com/chwichmann/GitWidget/releases/download/v0.2.0/GitWidget-linux-x64.tar.gz", "size": 41, "digest": "sha256:{{new string('1', 64)}}" },
                  { "name": "GitWidget-test-rid.zip", "browser_download_url": "https://github.com/chwichmann/GitWidget/releases/download/v0.2.0/GitWidget-test-rid.zip", "size": 69, "digest": "sha256:{{digest}}" }
                ] },
              { "tag_name": "v0.1.9", "html_url": "https://github.com/chwichmann/GitWidget/releases/tag/v0.1.9", "draft": false, "prerelease": false,
                "assets": [ { "name": "GitWidget-test-rid.zip", "browser_download_url": "https://github.com/x.zip", "size": 69, "digest": null } ] },
              { "tag_name": "v0.1.8", "html_url": "https://github.com/chwichmann/GitWidget/releases/tag/v0.1.8", "draft": false, "prerelease": false }
            ]
            """);

        var result = await Feed(new AppSettings()).GetReleasesAsync(CancellationToken.None);

        var releases = result.Should().BeOfType<ReleaseFeedResult.Success>().Subject.Releases;
        releases.Single(r => r.Tag == "v0.2.0").Asset.Should().Be(new ReleaseAsset("GitWidget-test-rid.zip",
            new Uri("https://github.com/chwichmann/GitWidget/releases/download/v0.2.0/GitWidget-test-rid.zip"), 69, digest));
        releases.Single(r => r.Tag == "v0.1.9").Asset!.Sha256.Should().BeNull();
        releases.Single(r => r.Tag == "v0.1.8").Asset.Should().BeNull();
        ReleaseFeedOptions.DefaultAssetName().Should().MatchRegex(@"^GitWidget-(win|osx|linux)-[a-z0-9]+\.(zip|tar\.gz)$");
    }

    [Fact]
    public async Task Request_is_anonymous_when_the_repository_is_not_monitored()
    {
        ServeReleases();

        await Feed(new AppSettings()).GetReleasesAsync(CancellationToken.None);

        var headers = _server.LogEntries.Single().RequestMessage!.Headers!;
        headers.ContainsKey("Authorization").Should().BeFalse();
        headers["User-Agent"].Single().Should().StartWith("GitWidget/");
    }

    [Fact]
    public async Task Token_of_the_monitored_GitWidget_repository_is_reused()
    {
        ServeReleases();
        var id = Guid.NewGuid();
        var configuration = new RepositoryConfiguration(id, RepositoryProviderKind.GitHub, new Uri($"{_server.Url}/"), "chwichmann", "GitWidget",
            "ref-gitwidget", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), true);
        var store = new Store();
        await store.WriteAsync("ref-gitwidget", new Secret("ghp_releasefeedtesttoken"), CancellationToken.None);

        await Feed(new AppSettings { Repositories = [configuration] }, store).GetReleasesAsync(CancellationToken.None);

        _server.LogEntries.Single().RequestMessage!.Headers!["Authorization"].Single().Should().Be("Bearer ghp_releasefeedtesttoken");
    }

    [Fact]
    public async Task Not_found_explains_the_private_repository_case()
    {
        ServeReleases(404, """{"message":"Not Found"}""");

        var result = await Feed(new AppSettings()).GetReleasesAsync(CancellationToken.None);

        result.Should().BeOfType<ReleaseFeedResult.Unavailable>().Which.Reason.Should().Contain("private repository");
    }

    [Fact]
    public async Task Forbidden_names_the_missing_contents_permission()
    {
        // Found live: a fine-grained token without "Contents: Read-only" gets 403 on the release list.
        ServeReleases(403, """{"message":"Resource not accessible by personal access token"}""");

        var result = await Feed(new AppSettings()).GetReleasesAsync(CancellationToken.None);

        result.Should().BeOfType<ReleaseFeedResult.Unavailable>().Which.Reason.Should().Contain("Contents: Read-only");
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Controller(AppSettings settings) : ISynchronizationController
    {
        public AppSettings Settings { get; } = settings;

        public void RequestRefresh(Guid? repositoryId = null)
        {
        }

        public Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Store : ICredentialStore
    {
        private readonly Dictionary<string, Secret> _secrets = [];

        public bool IsAvailable => true;

        public string StoreName => "test";

        public Task<Secret?> ReadAsync(string key, CancellationToken cancellationToken) => Task.FromResult(_secrets.GetValueOrDefault(key));

        public Task WriteAsync(string key, Secret secret, CancellationToken cancellationToken)
        {
            _secrets[key] = secret;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            _secrets.Remove(key);
            return Task.CompletedTask;
        }
    }
}
