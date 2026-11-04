using System.Net;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Infrastructure.Providers.GitLab;
using GitStatusWidget.Infrastructure.Providers.Http;

namespace GitStatusWidget.Infrastructure.Tests.Providers.GitLab;

public sealed class GitLabResponsePolicyTests
{
    private static readonly DateTimeOffset Now = GitLabTestHarness.Now;

    private static ResponseHeaders Headers(params (string Name, string Value)[] values) =>
        new(values.Select(v => new KeyValuePair<string, IEnumerable<string>>(v.Name, [v.Value])));

    private static ProviderFailureException Classify(HttpStatusCode status, string body, string contentType = "application/json", params (string, string)[] headers) =>
        GitLabResponsePolicy.Instance.Classify(status, Headers([("Content-Type", contentType), .. headers]), body, Now)
        ?? throw new InvalidOperationException("expected a classification");

    [Fact]
    public void Rate_limit_headers_are_optional()
    {
        GitLabResponsePolicy.Instance.ReadRateLimit(Headers(("Content-Type", "application/json"))).Should().BeNull();
        GitLabResponsePolicy.Instance.ReadRateLimit(Headers(("RateLimit-Remaining", "5"), ("RateLimit-ResetTime", "Wed, 04 Nov 2026 19:46:00 GMT")))
            .Should().Be(new RateLimitInfo(null, 5, new DateTimeOffset(2026, 11, 4, 19, 46, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Too_many_requests_without_retry_after_backs_off_a_minute()
    {
        var failure = Classify(HttpStatusCode.TooManyRequests, "Retry later", "text/plain");

        failure.Kind.Should().Be(ProviderFailureKind.RateLimited);
        failure.RetryAfter.Should().Be(Now.AddSeconds(60));
    }

    [Fact]
    public void Too_many_requests_uses_rate_limit_reset_when_no_retry_after()
    {
        var reset = Now.AddMinutes(3);
        var failure = Classify(HttpStatusCode.TooManyRequests, "Retry later", "text/plain", ("RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));

        failure.RetryAfter.Should().Be(reset);
    }

    [Fact]
    public void Expired_token_message_says_expired()
    {
        var failure = Classify(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_token\",\"error_description\":\"Token is expired. You can either do re-authorization or token refresh.\"}");

        failure.Kind.Should().Be(ProviderFailureKind.AuthenticationFailed);
        failure.Message.Should().Contain("expired");
    }

    [Fact]
    public void Insufficient_scope_is_authorization_with_read_api_hint()
    {
        var failure = Classify(HttpStatusCode.Forbidden, "{\"error\":\"insufficient_scope\",\"error_description\":\"The request requires higher privileges than provided by the access token.\",\"scope\":\"api read_api\"}");

        failure.Kind.Should().Be(ProviderFailureKind.AuthorizationFailed);
        failure.Message.Should().Contain("needs read_api");
        failure.Should().BeOfType<GitLabApiException>().Which.IsInsufficientScope.Should().BeTrue();
    }

    [Fact]
    public void Plain_forbidden_is_authorization_and_throttled_forbidden_is_rate_limit()
    {
        Classify(HttpStatusCode.Forbidden, "{\"message\":\"403 Forbidden\"}").Kind.Should().Be(ProviderFailureKind.AuthorizationFailed);
        Classify(HttpStatusCode.Forbidden, "Retry later", "text/plain").Kind.Should().Be(ProviderFailureKind.RateLimited);
        Classify(HttpStatusCode.Forbidden, "{\"message\":\"403 Forbidden\"}", "application/json", ("RateLimit-Remaining", "0")).Kind.Should().Be(ProviderFailureKind.RateLimited);
        Classify(HttpStatusCode.Forbidden, "<html>denied</html>", "text/html").Kind.Should().Be(ProviderFailureKind.GatewayInterception);
    }

    [Fact]
    public void Not_found_distinguishes_route_from_project()
    {
        Classify(HttpStatusCode.NotFound, "{\"error\":\"404 Not Found\"}").Kind.Should().Be(ProviderFailureKind.Configuration);
        Classify(HttpStatusCode.NotFound, "{\"message\":\"404 Project Not Found\"}").Kind.Should().Be(ProviderFailureKind.NotFound);
        Classify(HttpStatusCode.NotFound, "<html>404</html>", "text/html").Kind.Should().Be(ProviderFailureKind.Configuration);
    }

    [Fact]
    public void Bad_request_is_malformed_with_reason()
    {
        var failure = Classify(HttpStatusCode.BadRequest, "{\"error\":\"order_by does not have a valid value\"}");

        failure.Kind.Should().Be(ProviderFailureKind.Malformed);
        failure.Message.Should().Contain("order_by");
    }

    [Fact]
    public void Server_errors_use_shared_defaults()
    {
        GitLabResponsePolicy.Instance.Classify(HttpStatusCode.BadGateway, Headers(("Content-Type", "text/html")), "<html>", Now).Should().BeNull();
    }
}

public sealed class GitLabMapperTests
{
    [Theory]
    [InlineData("created", BuildState.Queued)]
    [InlineData("waiting_for_resource", BuildState.Queued)]
    [InlineData("preparing", BuildState.Queued)]
    [InlineData("pending", BuildState.Queued)]
    [InlineData("scheduled", BuildState.Queued)]
    [InlineData("manual", BuildState.Queued)]
    [InlineData("running", BuildState.Running)]
    [InlineData("waiting_for_callback", BuildState.Running)]
    [InlineData("success", BuildState.Succeeded)]
    [InlineData("failed", BuildState.Failed)]
    [InlineData("canceled", BuildState.Canceled)]
    [InlineData("canceling", BuildState.Canceled)]
    [InlineData("skipped", BuildState.Skipped)]
    [InlineData("something_new", BuildState.Unknown)]
    [InlineData(null, BuildState.Unknown)]
    public void Pipeline_statuses_map_to_build_states(string? status, BuildState expected) =>
        GitLabMapper.MapPipelineStatus(status).Should().Be(expected);

    [Fact]
    public void Terminal_pipeline_completes_at_updated_at_and_named_pipeline_keeps_name()
    {
        var updated = GitLabTestHarness.Now;
        var pipeline = new GitLabPipelineDto
        {
            Id = 3_028_771_539,
            Iid = 5,
            Status = "failed",
            Name = "Nightly",
            Ref = "main",
            Sha = "abc",
            WebUrl = "https://gitlab.example.invalid/p/-/pipelines/3028771539",
            CreatedAt = updated.AddMinutes(-3),
            UpdatedAt = updated,
        };

        var build = GitLabMapper.MapPipeline(pipeline);

        build.DisplayName.Should().Be("Nightly");
        build.CompletedAt.Should().Be(updated);
        build.State.Should().Be(BuildState.Failed);
    }

    [Theory]
    [InlineData("opened", ChangeRequestState.Open)]
    [InlineData("locked", ChangeRequestState.Open)]
    [InlineData("merged", ChangeRequestState.Merged)]
    [InlineData("closed", ChangeRequestState.Closed)]
    public void Merge_request_states_map(string state, ChangeRequestState expected) =>
        GitLabMapper.MapMergeRequestState(state).Should().Be(expected);

    [Theory]
    [InlineData("#d9534f", "#D9534F")]
    [InlineData("428bca", "#428BCA")]
    [InlineData(" #FFF ", "#FFF")]
    [InlineData("red", "red")]
    [InlineData("", null)]
    public void Label_colors_are_normalized(string input, string? expected) =>
        GitLabMapper.NormalizeColor(input).Should().Be(expected);

    [Fact]
    public void Project_identity_and_features_map_from_access_levels()
    {
        var project = new GitLabProjectDto
        {
            Id = 4242,
            Path = "widget",
            PathWithNamespace = "acme/tools/widget",
            Namespace = new GitLabNamespaceDto { FullPath = "acme/tools" },
            IssuesAccessLevel = "private",
            MergeRequestsAccessLevel = "disabled",
            BuildsAccessLevel = "enabled",
        };

        var identity = GitLabMapper.MapIdentity(project, new Uri("https://git.example.invalid:8443/gitlab/"));
        identity.Host.Should().Be("git.example.invalid:8443");
        identity.FullPath.Should().Be("acme/tools/widget");
        identity.ExternalId.Should().Be("4242");
        GitLabMapper.MapFeatures(project).Should().Be(new GitStatusWidget.Domain.Snapshots.RepositoryFeatures(true, false, true));
        GitLabMapper.MapFeatures(new GitLabProjectDto { Id = 1, Path = "x" }).Should().Be(GitStatusWidget.Domain.Snapshots.RepositoryFeatures.All,
            "missing access levels (reduced view) mean enabled");
    }

    [Fact]
    public void Timestamps_are_sent_in_utc_with_z()
    {
        GitLabMapper.FormatTimestamp(new DateTimeOffset(2026, 11, 4, 18, 0, 0, TimeSpan.FromHours(1))).Should().Be("2026-11-04T17:00:00.000Z");
    }

    [Fact]
    public void Graphql_query_declares_one_variable_per_milestone()
    {
        var query = GitLabCounts.BuildQuery(2, includeTotals: true);

        query.Should().Be(
            "query GitWidgetCounts($path: ID!, $t0: String!, $t1: String!) { project(fullPath: $path) {"
            + " io: issues(state: opened, types: [ISSUE]) { count }"
            + " ic: issues(state: closed, types: [ISSUE]) { count }"
            + " m0: issues(state: closed, types: [ISSUE], milestoneTitle: [$t0]) { count }"
            + " m1: issues(state: closed, types: [ISSUE], milestoneTitle: [$t1]) { count } } }");
    }

    [Fact]
    public void Graphql_fallback_only_for_endpoint_failures()
    {
        GitLabCounts.CanFallBack(new ProviderFailureException(ProviderFailureKind.Configuration, "route", statusCode: HttpStatusCode.NotFound)).Should().BeTrue();
        GitLabCounts.CanFallBack(new ProviderFailureException(ProviderFailureKind.Transient, "5xx", statusCode: HttpStatusCode.ServiceUnavailable)).Should().BeTrue();
        GitLabCounts.CanFallBack(new ProviderFailureException(ProviderFailureKind.GatewayInterception, "html", statusCode: HttpStatusCode.OK)).Should().BeTrue();
        GitLabCounts.CanFallBack(new ProviderFailureException(ProviderFailureKind.Malformed, "GraphQL error: x")).Should().BeFalse();
        GitLabCounts.CanFallBack(new ProviderFailureException(ProviderFailureKind.AuthenticationFailed, "401", statusCode: HttpStatusCode.Unauthorized)).Should().BeFalse();
        GitLabCounts.CanFallBack(new ProviderFailureException(ProviderFailureKind.AuthorizationFailed, "403", statusCode: HttpStatusCode.Forbidden)).Should().BeFalse();
        GitLabCounts.CanFallBack(new ProviderFailureException(ProviderFailureKind.RateLimited, "429", statusCode: HttpStatusCode.TooManyRequests)).Should().BeFalse();
    }

    [Fact]
    public void Project_path_is_escaped_once()
    {
        GitLabApi.EncodeProjectPath("group/sub group/repo").Should().Be("group%2Fsub%20group%2Frepo");
    }
}
