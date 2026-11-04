using GitStatusWidget.Application.Abstractions;

namespace GitStatusWidget.Infrastructure.Tests.Providers.GitHub;

public sealed class GitHubErrorTests : IDisposable
{
    private static readonly DateTimeOffset Now = GitHubFixtures.Now;

    private readonly GitHubProviderHarness _h = new();

    public void Dispose() => _h.Dispose();

    private void RepoAnswers(int status, object? body, IReadOnlyDictionary<string, string>? headers = null, string contentType = "application/json; charset=utf-8") =>
        _h.Api.Get(GitHubFakeServer.RepoPath, body, status: status, headers: headers, contentType: contentType);

    [Fact]
    public async Task Unauthorized_is_authentication_failure()
    {
        RepoAnswers(401, new { message = "Bad credentials", documentation_url = "https://docs.github.com/rest" });

        var failure = await _h.FetchFailureAsync();

        failure.Kind.Should().Be(ProviderFailureKind.AuthenticationFailed);
        _h.Api.Requests.Should().ContainSingle("401 is never retried");
    }

    [Fact]
    public async Task Forbidden_with_permission_header_is_authorization_failure()
    {
        RepoAnswers(403, new { message = "Resource not accessible by personal access token" }, new Dictionary<string, string>
        {
            ["X-Accepted-GitHub-Permissions"] = "issues=read",
            ["X-RateLimit-Remaining"] = "4000",
            ["X-RateLimit-Limit"] = "5000",
        });

        var failure = await _h.FetchFailureAsync();

        failure.Kind.Should().Be(ProviderFailureKind.AuthorizationFailed);
        failure.Message.Should().Contain("issues=read");
    }

    [Fact]
    public async Task Forbidden_with_sso_header_mentions_single_sign_on()
    {
        RepoAnswers(403, new { message = "Resource protected by organization SAML enforcement." }, new Dictionary<string, string>
        {
            ["X-GitHub-SSO"] = "required; url=https://github.com/orgs/octo-org/sso?authorization_request=synthetic",
        });

        var failure = await _h.FetchFailureAsync();

        failure.Kind.Should().Be(ProviderFailureKind.AuthorizationFailed);
        failure.Message.Should().Contain("single sign-on").And.NotContain("authorization_request");
    }

    [Fact]
    public async Task Primary_rate_limit_waits_until_reset()
    {
        var reset = Now.AddMinutes(17).ToUnixTimeSeconds();
        RepoAnswers(403, new { message = "API rate limit exceeded for user ID 1." }, GitHubFakeServer.RateHeaders("core", remaining: 0, reset: reset));

        var failure = await _h.FetchFailureAsync();

        failure.Kind.Should().Be(ProviderFailureKind.RateLimited);
        failure.RetryAfter.Should().Be(DateTimeOffset.FromUnixTimeSeconds(reset));
        failure.RateLimit.Should().NotBeNull();
        failure.RateLimit!.Remaining.Should().Be(0);
        failure.RateLimit.Resource.Should().Be("core");
        _h.Api.Requests.Should().ContainSingle("rate limits are never retried");
    }

    [Fact]
    public async Task Secondary_rate_limit_body_waits_at_least_a_minute()
    {
        RepoAnswers(403, new { message = "You have exceeded a secondary rate limit. Please wait a few minutes before you try again." });

        var failure = await _h.FetchFailureAsync();

        failure.Kind.Should().Be(ProviderFailureKind.RateLimited);
        failure.RetryAfter.Should().BeOnOrAfter(Now.AddSeconds(60));
    }

    [Fact]
    public async Task Retry_after_is_honoured()
    {
        RepoAnswers(403, new { message = "slow down" }, new Dictionary<string, string> { ["Retry-After"] = "120" });

        var failure = await _h.FetchFailureAsync();

        failure.Kind.Should().Be(ProviderFailureKind.RateLimited);
        failure.RetryAfter.Should().Be(Now.AddSeconds(120));
    }

    [Fact]
    public async Task Too_many_requests_is_rate_limited()
    {
        RepoAnswers(429, new { message = "Too Many Requests" });

        var failure = await _h.FetchFailureAsync();

        failure.Kind.Should().Be(ProviderFailureKind.RateLimited);
        failure.RetryAfter.Should().BeOnOrAfter(Now.AddSeconds(60));
        _h.Api.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Forbidden_html_page_is_a_configuration_problem()
    {
        RepoAnswers(403, "<!DOCTYPE html><html><body>Request forbidden by administrative rules. Please make sure your request has a User-Agent header.</body></html>", contentType: "text/html; charset=utf-8");

        var failure = await _h.FetchFailureAsync();

        failure.Kind.Should().Be(ProviderFailureKind.Configuration);
        failure.Message.Should().Contain("User-Agent");
    }

    [Fact]
    public async Task Plain_forbidden_is_authorization_failure()
    {
        RepoAnswers(403, new { message = "Forbidden" });

        (await _h.FetchFailureAsync()).Kind.Should().Be(ProviderFailureKind.AuthorizationFailed);
    }

    [Fact]
    public async Task Not_found_hints_at_token_visibility()
    {
        RepoAnswers(404, new { message = "Not Found" });

        var failure = await _h.FetchFailureAsync();

        failure.Kind.Should().Be(ProviderFailureKind.NotFound);
        failure.Message.Should().Contain("token cannot see it");
    }

    [Fact]
    public async Task Unsupported_api_version_is_a_configuration_problem()
    {
        RepoAnswers(400, new { message = "Unsupported 'X-GitHub-Api-Version' header" });

        (await _h.FetchFailureAsync()).Kind.Should().Be(ProviderFailureKind.Configuration);
    }

    [Fact]
    public async Task Validation_failure_is_malformed()
    {
        RepoAnswers(422, new { message = "Validation Failed", status = "422" });

        (await _h.FetchFailureAsync()).Kind.Should().Be(ProviderFailureKind.Malformed);
    }

    [Fact]
    public async Task Server_errors_are_retried_then_reported_as_transient()
    {
        using var harness = new GitHubProviderHarness(retries: 2);
        harness.Api.Get(GitHubFakeServer.RepoPath, new { message = "Server Error" }, status: 502);

        var failure = await harness.FetchFailureAsync();

        failure.Kind.Should().Be(ProviderFailureKind.Transient);
        harness.Api.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Malformed_json_is_rejected()
    {
        RepoAnswers(200, "{\"node_id\": \"R_1\", \"full_name\": ");

        (await _h.FetchFailureAsync()).Kind.Should().Be(ProviderFailureKind.Malformed);
    }

    [Fact]
    public async Task Unexpected_json_shape_is_rejected()
    {
        _h.Api.StandardRepository();
        _h.Api.Get($"{GitHubFakeServer.RepoPath}/issues", new { message = "not a list" }, query: [("state", "open")], priority: 1);

        (await _h.FetchFailureAsync()).Kind.Should().Be(ProviderFailureKind.Malformed);
    }

    [Fact]
    public async Task Missing_required_fields_are_rejected()
    {
        _h.Api.StandardRepository();
        _h.Api.Get($"{GitHubFakeServer.RepoPath}/issues", new[] { new { number = 1, title = "no ids" } }, query: [("state", "open")], priority: 1);

        (await _h.FetchFailureAsync()).Kind.Should().Be(ProviderFailureKind.Malformed);
    }

    [Fact]
    public async Task Html_success_page_is_gateway_interception()
    {
        RepoAnswers(200, "<html><body>Sign in to your network</body></html>", contentType: "text/html");

        (await _h.FetchFailureAsync()).Kind.Should().Be(ProviderFailureKind.GatewayInterception);
    }

    [Fact]
    public async Task Failing_later_page_fails_the_whole_fetch()
    {
        _h.Api.StandardRepository();
        _h.Api.Get(
            $"{GitHubFakeServer.RepoPath}/issues",
            Enumerable.Range(1, 100).Select(n => GitHubFixtures.Issue(n, Now.AddHours(-n))).ToArray(),
            query: [("state", "open")],
            link: _h.Api.NextLink("/api/v3/repositories/4200000001/issues", "state=open&after=broken"),
            priority: 1);
        _h.Api.Get("/api/v3/repositories/4200000001/issues", new { message = "Server Error" }, status: 500, query: [("after", "broken")]);

        var result = await _h.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.Should().BeOfType<ProviderFetchResult.Failure>().Which.Kind.Should().Be(ProviderFailureKind.Transient);
    }

    [Fact]
    public async Task Malformed_later_page_is_reported_as_partial()
    {
        _h.Api.StandardRepository();
        _h.Api.Get(
            $"{GitHubFakeServer.RepoPath}/issues",
            new[] { GitHubFixtures.Issue(1, Now.AddHours(-1)) },
            query: [("state", "open")],
            link: _h.Api.NextLink("/api/v3/repositories/4200000001/issues", "state=open&after=garbled"),
            priority: 1);
        _h.Api.Get("/api/v3/repositories/4200000001/issues", "[{\"node_id\":", query: [("after", "garbled")]);

        var failure = await _h.FetchFailureAsync();

        failure.Kind.Should().Be(ProviderFailureKind.Partial);
        failure.Message.Should().Contain("Page 2");
    }

    [Fact]
    public async Task Graphql_failure_fails_the_fetch()
    {
        _h.Api.StandardRepository(counts: new { data = (object?)null, errors = new[] { new { type = "SOMETHING_ELSE", message = "Something went wrong while executing your query." } } });

        (await _h.FetchFailureAsync()).Kind.Should().Be(ProviderFailureKind.Malformed);
    }

    [Fact]
    public async Task Graphql_rate_limited_error_uses_graphql_reset()
    {
        var reset = Now.AddMinutes(9).ToUnixTimeSeconds();
        _h.Api.StandardRepository(counts: new { errors = new[] { new { type = "RATE_LIMITED", message = "API rate limit exceeded for user ID 1." } } });
        _h.Api.GraphQl(
            new { errors = new[] { new { type = "RATE_LIMITED", message = "API rate limit exceeded for user ID 1." } } },
            headers: GitHubFakeServer.RateHeaders("graphql", remaining: 0, reset: reset),
            priority: 1);

        var failure = await _h.FetchFailureAsync();

        failure.Kind.Should().Be(ProviderFailureKind.RateLimited);
        failure.RetryAfter.Should().Be(DateTimeOffset.FromUnixTimeSeconds(reset));
        failure.RateLimit!.Resource.Should().Be("graphql", "the most constrained bucket is reported");
    }

    [Fact]
    public async Task Graphql_forbidden_error_is_authorization_failure()
    {
        _h.Api.StandardRepository(counts: new { errors = new[] { new { type = "FORBIDDEN", message = "Resource not accessible by personal access token" } } });

        (await _h.FetchFailureAsync()).Kind.Should().Be(ProviderFailureKind.AuthorizationFailed);
    }

    [Fact]
    public async Task Graphql_not_found_is_not_found()
    {
        _h.Api.StandardRepository(counts: new
        {
            data = new { repository = (object?)null },
            errors = new[] { new { type = "NOT_FOUND", message = "Could not resolve to a Repository with the name 'octo-org/widget'." } },
        });

        (await _h.FetchFailureAsync()).Kind.Should().Be(ProviderFailureKind.NotFound);
    }

    [Fact]
    public async Task Graphql_html_timeout_is_transient()
    {
        _h.Api.StandardRepository();
        _h.Api.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(GitHubFakeServer.GraphQlPath).UsingPost()).AtPriority(1)
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(502).WithHeader("Content-Type", "text/html").WithBody("<html>Bad gateway</html>"));

        (await _h.FetchFailureAsync()).Kind.Should().Be(ProviderFailureKind.Transient);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        _h.Api.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(GitHubFakeServer.RepoPath).UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5))
                .WithHeader("Content-Type", "application/json").WithBody("{}"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => _h.FetchAsync(cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Missing_token_is_authentication_failure_without_requests()
    {
        await _h.Credentials.DeleteAsync(_h.Configuration.CredentialReference, CancellationToken.None);

        var failure = await _h.FetchFailureAsync();

        failure.Kind.Should().Be(ProviderFailureKind.AuthenticationFailed);
        _h.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Unavailable_credential_store_is_configuration_failure()
    {
        _h.Credentials.IsAvailable = false;

        (await _h.FetchFailureAsync()).Kind.Should().Be(ProviderFailureKind.Configuration);
    }

    [Fact]
    public async Task Plain_http_requires_explicit_opt_in()
    {
        var result = await _h.Provider.FetchAsync(_h.Configuration with { AllowInsecureHttp = false }, GitHubProviderHarness.FirstFetch(), CancellationToken.None);

        result.Should().BeOfType<ProviderFetchResult.Failure>().Which.Kind.Should().Be(ProviderFailureKind.Configuration);
        _h.Api.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("group/subgroup", "widget")]
    [InlineData("", "widget")]
    [InlineData("octo-org", "")]
    public async Task Invalid_owner_or_name_is_configuration_failure(string owner, string name)
    {
        var result = await _h.Provider.FetchAsync(_h.Configuration with { OwnerPath = owner, RepositoryName = name }, GitHubProviderHarness.FirstFetch(), CancellationToken.None);

        result.Should().BeOfType<ProviderFetchResult.Failure>().Which.Kind.Should().Be(ProviderFailureKind.Configuration);
    }
}
