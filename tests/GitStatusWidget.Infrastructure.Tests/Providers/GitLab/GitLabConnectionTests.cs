using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Infrastructure.Providers.GitLab;
using Microsoft.Extensions.DependencyInjection;

namespace GitStatusWidget.Infrastructure.Tests.Providers.GitLab;

public sealed class GitLabConnectionTests : IDisposable
{
    // Synthetic; asserted never to reach any output.
    private static readonly string[] LastUsedIps = ["10.0.0.5"];

    private readonly GitLabTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void StubVersion() => _h.StubJson(_h.Api + "metadata", new { version = "19.5.1-ee", revision = "abc1234def5", enterprise = true, plan = (string?)null });

    private void StubToken(string[] scopes, string? expiresAt = "2027-05-01") => _h.StubJson(_h.Api + "personal_access_tokens/self", new
    {
        id = 4711,
        name = "gitwidget-readonly",
        revoked = false,
        created_at = "2026-05-01T08:00:00.000Z",
        description = "GitWidget",
        scopes,
        user_id = 1234,
        last_used_at = "2026-11-04T07:59:12.345Z",
        last_used_ips = LastUsedIps,
        active = true,
        granular = false,
        expires_at = expiresAt,
    });

    [Fact]
    public async Task Successful_connection_reports_version_identity_and_token_expiry()
    {
        StubVersion();
        _h.StubProject();
        StubToken(["read_api"]);

        var result = await _h.CreateProvider().TestConnectionAsync(_h.Configuration(), Ct);

        result.IsSuccess.Should().BeTrue(result.Message);
        result.Identity!.ExternalId.Should().Be("4242");
        result.ServerVersion.Should().Be("19.5.1-ee");
        result.CredentialExpiresOn.Should().Be(new DateOnly(2027, 5, 1));
        result.CredentialDescription.Should().Be("scopes: read_api");
        result.Message.Should().NotContain("Warning").And.NotContain("10.0.0.5");
        _h.Server.LogEntries.Should().OnlyContain(e => e.RequestMessage!.Method == "GET");
        _h.Server.LogEntries.Should().Contain(e => e.RequestMessage!.Url.EndsWith("/projects/acme%2Ftools%2Fwidget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Broad_token_scope_is_warned_about()
    {
        StubVersion();
        _h.StubProject();
        StubToken(["api", "read_api"], expiresAt: null);

        var result = await _h.CreateProvider().TestConnectionAsync(_h.Configuration(), Ct);

        result.IsSuccess.Should().BeTrue();
        result.Message.Should().Contain("Warning").And.Contain("api").And.Contain("only needs read_api");
        result.CredentialExpiresOn.Should().BeNull();
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Token_self_info_failures_are_tolerated(int status)
    {
        StubVersion();
        _h.StubProject();
        _h.StubJson(_h.Api + "personal_access_tokens/self", new { message = "not available" }, status: status);

        var result = await _h.CreateProvider().TestConnectionAsync(_h.Configuration(), Ct);

        result.IsSuccess.Should().BeTrue(result.Message);
        result.CredentialDescription.Should().BeNull();
        result.CredentialExpiresOn.Should().BeNull();
    }

    [Fact]
    public async Task Missing_metadata_endpoint_falls_back_to_version()
    {
        _h.StubJson(_h.Api + "metadata", new { error = "404 Not Found" }, status: 404);
        _h.StubJson(_h.Api + "version", new { version = "15.1.0-ee", revision = "x" });
        _h.StubProject();
        StubToken(["read_api"]);

        var result = await _h.CreateProvider().TestConnectionAsync(_h.Configuration(), Ct);

        result.ServerVersion.Should().Be("15.1.0-ee");
    }

    [Fact]
    public async Task Invalid_token_fails_the_connection_test()
    {
        _h.StubJson(_h.Api + "metadata", new { message = "401 Unauthorized" }, status: 401);

        var result = await _h.CreateProvider().TestConnectionAsync(_h.Configuration(), Ct);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(ProviderFailureKind.AuthenticationFailed);
        result.Message.Should().NotContain(GitLabTestHarness.Token);
    }

    [Fact]
    public async Task Moved_project_is_reported()
    {
        StubVersion();
        _h.StubProject();
        StubToken(["read_api"]);
        _h.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(_h.Api + "projects/old/widget").UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(301).WithHeader("Location", _h.Server.Url + "/api/v4/projects/4242"));

        var result = await _h.CreateProvider().TestConnectionAsync(_h.Configuration("old/widget"), Ct);

        result.IsSuccess.Should().BeTrue(result.Message);
        result.Message.Should().Contain("moved").And.Contain(GitLabTestHarness.FullPath);
    }

    [Fact]
    public void Service_registration_resolves_the_provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICredentialStore>(new InMemoryCredentialStore());
        services.AddGitLabProvider();

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IRepositoryStatusProvider>().Should().ContainSingle().Which.Kind.Should().Be(RepositoryProviderKind.GitLab);
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(GitLabStatusProvider.HttpClientName).Should().NotBeNull();
    }
}
