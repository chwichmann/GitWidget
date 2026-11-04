using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Infrastructure.Tests.Providers.GitHub;

public sealed class GitHubConnectionTests : IDisposable
{
    private readonly GitHubProviderHarness _h = new();

    public void Dispose() => _h.Dispose();

    private Task<ProviderConnectionResult> TestAsync() => _h.Provider.TestConnectionAsync(_h.Configuration, CancellationToken.None);

    private void StubProbes()
    {
        _h.Api.Get($"{GitHubFakeServer.RepoPath}/issues", Array.Empty<object>());
        _h.Api.Get($"{GitHubFakeServer.RepoPath}/actions/runs", GitHubFixtures.Runs());
    }

    [Fact]
    public async Task Success_resolves_identity_and_token_expiry()
    {
        _h.Api.Get(GitHubFakeServer.RepoPath, GitHubFixtures.Repository(), headers: new Dictionary<string, string>
        {
            ["github-authentication-token-expiration"] = "2026-12-31 08:00:00 UTC",
            ["X-OAuth-Scopes"] = "repo, read:org",
        });
        StubProbes();

        var result = await TestAsync();

        result.IsSuccess.Should().BeTrue(result.Message);
        result.Identity.Should().Be(new RepositoryIdentity(RepositoryProviderKind.GitHub, "localhost", "octo-org", "widget", GitHubFakeServer.RepoNodeId));
        result.CredentialExpiresOn.Should().Be(new DateOnly(2026, 12, 31));
        result.CredentialDescription.Should().Contain("repo");
        result.Message.Should().Contain("octo-org/widget").And.Contain("main").And.NotContain(GitHubProviderHarness.Token);
        _h.Api.Requests.Should().OnlyContain(r => r.RequestMessage!.Method == "GET");
    }

    [Fact]
    public async Task Not_found_explains_private_repository_visibility()
    {
        _h.Api.Get(GitHubFakeServer.RepoPath, new { message = "Not Found" }, status: 404);

        var result = await TestAsync();

        result.Failure.Should().Be(ProviderFailureKind.NotFound);
        result.Message.Should().Contain("Private repositories").And.Contain("octo-org/widget");
    }

    [Fact]
    public async Task Bad_credentials_are_reported()
    {
        _h.Api.Get(GitHubFakeServer.RepoPath, new { message = "Bad credentials" }, status: 401);

        var result = await TestAsync();

        result.Failure.Should().Be(ProviderFailureKind.AuthenticationFailed);
        result.Message.Should().NotContain(GitHubProviderHarness.Token);
    }

    [Fact]
    public async Task Missing_issue_permission_is_explained()
    {
        _h.Api.Get(GitHubFakeServer.RepoPath, GitHubFixtures.Repository());
        _h.Api.Get($"{GitHubFakeServer.RepoPath}/issues", new { message = "Resource not accessible by personal access token" }, status: 403,
            headers: new Dictionary<string, string> { ["X-Accepted-GitHub-Permissions"] = "issues=read" });

        var result = await TestAsync();

        result.Failure.Should().Be(ProviderFailureKind.AuthorizationFailed);
        result.Message.Should().Contain("Issues: Read");
    }

    [Fact]
    public async Task Disabled_actions_still_connects()
    {
        _h.Api.Get(GitHubFakeServer.RepoPath, GitHubFixtures.Repository());
        _h.Api.Get($"{GitHubFakeServer.RepoPath}/issues", Array.Empty<object>());
        _h.Api.Get($"{GitHubFakeServer.RepoPath}/actions/runs", new { message = "Not Found" }, status: 404);

        var result = await TestAsync();

        result.IsSuccess.Should().BeTrue();
        result.Message.Should().Contain("Actions is not enabled");
        result.CredentialExpiresOn.Should().BeNull();
    }

    [Fact]
    public async Task Forbidden_rate_limit_during_test_is_rate_limited()
    {
        _h.Api.Get(GitHubFakeServer.RepoPath, new { message = "API rate limit exceeded" }, status: 403, headers: GitHubFakeServer.RateHeaders("core", remaining: 0));

        (await TestAsync()).Failure.Should().Be(ProviderFailureKind.RateLimited);
    }
}
