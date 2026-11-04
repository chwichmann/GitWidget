using System.Net;
using System.Text.Json;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Infrastructure.Providers.GitHub;
using GitStatusWidget.Infrastructure.Providers.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GitStatusWidget.Infrastructure.Tests.Providers.GitHub;

public sealed class GitHubUnitTests
{
    private static readonly DateTimeOffset Now = GitHubFixtures.Now;

    private static RepositoryConfiguration Config(string server) => new(
        Guid.NewGuid(),
        RepositoryProviderKind.GitHub,
        new Uri(server),
        "octo-org",
        "widget",
        "ref",
        RepositoryConfiguration.DefaultIssueInterval,
        RepositoryConfiguration.DefaultBuildInterval,
        RepositoryConfiguration.DefaultMetadataInterval,
        RepositoryConfiguration.DefaultReconciliationInterval,
        IsEnabled: true);

    [Theory]
    [InlineData("https://github.com/")]
    [InlineData("https://www.github.com")]
    [InlineData("https://GitHub.com/some/path")]
    [InlineData("https://api.github.com/")]
    public void GitHub_com_uses_the_public_api(string server)
    {
        var endpoints = GitHubEndpoints.From(Config(server));

        endpoints.RestBase.Should().Be(new Uri("https://api.github.com/"));
        endpoints.GraphQl.Should().Be(new Uri("https://api.github.com/graphql"));
        endpoints.Host.Should().Be("github.com");
        endpoints.Rest("repos/a/b").Should().Be(new Uri("https://api.github.com/repos/a/b"));
    }

    [Fact]
    public void Enterprise_server_uses_api_v3_and_api_graphql()
    {
        var endpoints = GitHubEndpoints.From(Config("https://ghe.example.invalid/root"));

        endpoints.RestBase.Should().Be(new Uri("https://ghe.example.invalid/root/api/v3/"));
        endpoints.GraphQl.Should().Be(new Uri("https://ghe.example.invalid/root/api/graphql"));
        endpoints.Host.Should().Be("ghe.example.invalid");
        endpoints.IsRestUri(new Uri("https://ghe.example.invalid/root/api/v3/repositories/1/issues?after=x")).Should().BeTrue();
        endpoints.IsRestUri(new Uri("https://other.example.invalid/root/api/v3/repositories/1")).Should().BeFalse();
        endpoints.IsRestUri(new Uri("https://ghe.example.invalid/elsewhere")).Should().BeFalse();
    }

    [Fact]
    public void Repository_path_is_escaped()
    {
        GitHubEndpoints.RepositoryPath("octo org", "a?b").Should().Be("repos/octo%20org/a%3Fb");
    }

    [Theory]
    [InlineData("requested", null, BuildState.Queued)]
    [InlineData("queued", null, BuildState.Queued)]
    [InlineData("pending", null, BuildState.Queued)]
    [InlineData("waiting", null, BuildState.Queued)]
    [InlineData("in_progress", null, BuildState.Running)]
    [InlineData("completed", "success", BuildState.Succeeded)]
    [InlineData("completed", "neutral", BuildState.Succeeded)]
    [InlineData("completed", "failure", BuildState.Failed)]
    [InlineData("completed", "timed_out", BuildState.Failed)]
    [InlineData("completed", "startup_failure", BuildState.Failed)]
    [InlineData("completed", "cancelled", BuildState.Canceled)]
    [InlineData("completed", "skipped", BuildState.Skipped)]
    [InlineData("completed", "action_required", BuildState.Queued)]
    [InlineData("completed", "stale", BuildState.Unknown)]
    [InlineData("completed", null, BuildState.Unknown)]
    [InlineData("completed", "brand_new_value", BuildState.Unknown)]
    [InlineData(null, null, BuildState.Unknown)]
    [InlineData("mystery", null, BuildState.Unknown)]
    [InlineData("COMPLETED", "SUCCESS", BuildState.Succeeded)]
    public void Build_states_follow_the_mapping_table(string? status, string? conclusion, BuildState expected)
    {
        GitHubMapper.MapBuildState(status, conclusion).Should().Be(expected);
    }

    [Fact]
    public void Completed_run_without_updated_at_uses_created_at()
    {
        var run = JsonSerializer.Deserialize<GitHubWorkflowRunDto>(
            """{"id": 41899607319, "status": "completed", "conclusion": "success", "html_url": "https://github.com/o/r/actions/runs/41899607319", "created_at": "2026-11-04T10:00:00Z"}""",
            ProviderJson.SnakeCase)!;

        var build = GitHubMapper.MapBuild(run);

        build.ExternalId.Should().Be("41899607319");
        build.CompletedAt.Should().Be(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
        build.DisplayName.Should().Be("Workflow run");
    }

    [Theory]
    [InlineData("d73a4a", "#d73a4a")]
    [InlineData("D73A4A", "#d73a4a")]
    [InlineData("#00FF00", "#00ff00")]
    [InlineData("abc", "#aabbcc")]
    [InlineData("zzzzzz", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("1234567", null)]
    public void Label_colors_are_normalized(string? input, string? expected)
    {
        GitHubMapper.NormalizeColor(input).Should().Be(expected);
    }

    [Fact]
    public void Issue_dtos_tolerate_unknown_missing_and_large_fields()
    {
        var json = """
            {
              "id": 6209691474, "node_id": "I_kwX", "number": 7, "state": "open",
              "html_url": "https://github.com/o/r/issues/7",
              "created_at": "2026-11-04T10:00:00Z", "updated_at": "2026-11-04T11:00:00+01:00",
              "user": null, "labels": ["a", {"name": "b", "color": "ffffff", "archived_at": null}, null],
              "assignees": [null, {"login": "x", "id": 1}],
              "milestone": {"id": 1, "number": 1, "title": "no node id"},
              "type": {"name": "Bug"}, "sub_issues_summary": {"total": 0}, "issue_field_values": []
            }
            """;

        var dto = JsonSerializer.Deserialize<GitHubIssueDto>(json, ProviderJson.SnakeCase)!;
        var issue = GitHubMapper.MapIssue(dto);

        GitHubMapper.IsPullRequest(dto).Should().BeFalse();
        issue.Title.Should().BeEmpty();
        issue.Author.Should().BeNull();
        issue.Labels.Select(l => l.Name).Should().Equal("a", "b");
        issue.Assignees.Should().ContainSingle().Which.ExternalId.Should().Be("1");
        issue.Milestone.Should().BeNull("a milestone reference needs the node id");
        issue.UpdatedAt.Should().Be(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
        issue.UpdatedAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Milestone_due_date_uses_the_utc_date()
    {
        var dto = JsonSerializer.Deserialize<GitHubMilestoneDto>(
            """{"node_id": "MI_x", "title": "t", "state": "open", "due_on": "2026-12-01T07:00:00Z"}""", ProviderJson.SnakeCase)!;

        GitHubMapper.MapMilestone(dto).DueDate.Should().Be(new DateOnly(2026, 12, 1));
    }

    [Theory]
    [InlineData("2026-12-31 08:00:00 UTC", 2026, 12, 31)]
    [InlineData("2026-12-31 23:00:00 -0700", 2026, 12, 31)]
    [InlineData("2026-12-31 00:00:00 +02:00", 2026, 12, 31)]
    public void Token_expiration_header_is_parsed(string header, int year, int month, int day)
    {
        GitHubStatusProvider.ParseTokenExpiration(header).Should().Be(new DateOnly(year, month, day));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    public void Invalid_token_expiration_is_ignored(string? header)
    {
        GitHubStatusProvider.ParseTokenExpiration(header).Should().BeNull();
    }

    [Fact]
    public void Rate_limit_headers_are_parsed_and_most_constrained_bucket_wins()
    {
        var policy = new GitHubResponsePolicy();

        var core = policy.ReadRateLimit(Headers(("X-RateLimit-Limit", "5000"), ("X-RateLimit-Remaining", "4000"), ("X-RateLimit-Reset", "1793824139"), ("X-RateLimit-Resource", "core")));
        policy.ReadRateLimit(Headers(("X-RateLimit-Limit", "5000"), ("X-RateLimit-Remaining", "100"), ("X-RateLimit-Reset", "1793825340"), ("X-RateLimit-Resource", "graphql")));
        policy.ReadRateLimit(Headers(("Content-Type", "application/json"))).Should().BeNull();

        core.Should().Be(new RateLimitInfo(5000, 4000, DateTimeOffset.FromUnixTimeSeconds(1793824139), "core"));
        policy.MostConstrained!.Resource.Should().Be("graphql");
        policy.ForResource("core")!.Remaining.Should().Be(4000);
    }

    [Fact]
    public void Server_errors_are_left_to_the_default_rules()
    {
        new GitHubResponsePolicy().Classify(HttpStatusCode.BadGateway, Headers(), "<html>", Now).Should().BeNull();
    }

    [Fact]
    public void Forbidden_with_remaining_zero_and_past_reset_waits_a_minute()
    {
        var failure = new GitHubResponsePolicy().Classify(
            HttpStatusCode.Forbidden, Headers(("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", Now.AddMinutes(-5).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))), "{}", Now)!;

        failure.Kind.Should().Be(ProviderFailureKind.RateLimited);
        failure.RetryAfter.Should().Be(Now.AddSeconds(60));
    }

    [Fact]
    public void Service_registration_resolves_the_provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICredentialStore>(new InMemoryCredentialStore());
        services.AddGitHubProvider();
        services.AddGitHubProvider();
        services.AddSingleton<IRepositoryStatusProviderResolver, RepositoryStatusProviderResolver>();

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IRepositoryStatusProvider>().Should().ContainSingle().Which.Kind.Should().Be(RepositoryProviderKind.GitHub);
        provider.GetRequiredService<IRepositoryStatusProviderResolver>().Resolve(RepositoryProviderKind.GitHub).Should().BeOfType<GitHubStatusProvider>();
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubStatusProvider.HttpClientName).Should().NotBeNull();
    }

    private static ResponseHeaders Headers(params (string Name, string Value)[] values) =>
        new(values.Select(v => new KeyValuePair<string, IEnumerable<string>>(v.Name, [v.Value])));
}
