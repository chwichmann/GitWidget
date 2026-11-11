using GitStatusWidget.Application.Usage;
using GitStatusWidget.Infrastructure.Providers.Http;
using GitStatusWidget.Infrastructure.Usage;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace GitStatusWidget.Infrastructure.Tests.Usage;

/// <summary>Issue #17: reading the Claude usage with the Claude Code sign-in.</summary>
public sealed class ClaudeUsageSourceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    // Shape observed live on 2026-11-11 (shortened): no active session, a weekly and a model-scoped weekly limit,
    // many unrelated fields.
    private const string UsageJson = """
        {
          "five_hour": null,
          "seven_day": { "utilization": 50.0, "resets_at": "2026-11-16T20:00:00.290757+00:00", "limit_dollars": null },
          "seven_day_opus": null,
          "seven_day_sonnet": null,
          "iguana_necktie": null,
          "extra_usage": { "is_enabled": false, "utilization": null },
          "limits": [
            { "kind": "weekly_all", "group": "weekly", "percent": 50, "resets_at": "2026-11-16T20:00:00.290757+00:00", "scope": null },
            { "kind": "weekly_scoped", "group": "weekly", "percent": 69, "resets_at": "2026-11-16T20:00:00.290925+00:00",
              "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null } }
          ],
          "spend": { "percent": 0, "enabled": false }
        }
        """;

    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly HttpClient _http = new(ProviderHttpClient.CreateHandler());
    private readonly string _directory = Directory.CreateTempSubdirectory("gitwidget-usage-").FullName;
    private readonly FixedTime _time = new();
    private readonly Dictionary<string, string?> _environment = [];

    public void Dispose()
    {
        _http.Dispose();
        _server.Stop();
        _server.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private string CredentialsFile => Path.Combine(_directory, ".credentials.json");

    private ClaudeUsageSource Source() => new(new SingleClientFactory(_http), _time, new ClaudeUsageOptions
    {
        Endpoint = new Uri($"{_server.Url}/api/oauth/usage"),
        CredentialsFile = CredentialsFile,
        Environment = name => _environment.GetValueOrDefault(name),
        UseMacKeychain = false,
    });

    private void WriteCredentials(string token = "sk-ant-oat01-usagetesttoken", DateTimeOffset? expiresAt = null) =>
        File.WriteAllText(CredentialsFile, $$"""
            {
              "mcpOAuth": { "other": { "accessToken": "must-not-be-used" } },
              "claudeAiOauth": {
                "accessToken": "{{token}}",
                "refreshToken": "sk-ant-ort01-must-not-be-used",
                "expiresAt": {{(expiresAt ?? Now.AddHours(2)).ToUnixTimeMilliseconds()}},
                "subscriptionType": "max"
              }
            }
            """);

    private void Serve(int status = 200, string body = UsageJson, string? retryAfter = null)
    {
        var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body);
        if (retryAfter is not null)
        {
            response = response.WithHeader("Retry-After", retryAfter);
        }

        _server.Given(Request.Create().WithPath("/api/oauth/usage").UsingGet()).RespondWith(response);
    }

    [Fact]
    public async Task Usage_is_parsed_and_missing_windows_are_left_out()
    {
        WriteCredentials();
        Serve();

        var result = await Source().GetUsageAsync(CancellationToken.None);

        var snapshot = result.Should().BeOfType<ClaudeUsageResult.Success>().Subject.Snapshot;
        snapshot.Plan.Should().Be("max");
        snapshot.FetchedAt.Should().Be(Now);
        snapshot.Limits.Should().HaveCount(2, "the session window is null and must not show up as 0 %");
        snapshot.Limits[0].Should().Be(new ClaudeUsageLimit(ClaudeUsageLimitKind.Weekly, null, 50,
            new DateTimeOffset(2026, 11, 16, 20, 0, 0, TimeSpan.Zero).AddTicks(2907570)));
        snapshot.Limits[1].Kind.Should().Be(ClaudeUsageLimitKind.WeeklyModel);
        snapshot.Limits[1].Model.Should().Be("Fable");
        snapshot.Limits[1].Percent.Should().Be(69);
    }

    [Fact]
    public async Task Session_window_and_older_model_windows_are_read()
    {
        WriteCredentials();
        Serve(body: """
            {
              "five_hour": { "utilization": 58, "resets_at": "2026-11-11T14:10:00+00:00" },
              "seven_day": { "utilization": 140, "resets_at": "not a date" },
              "seven_day_opus": { "utilization": 12.4, "resets_at": null }
            }
            """);

        var result = await Source().GetUsageAsync(CancellationToken.None);

        var limits = result.Should().BeOfType<ClaudeUsageResult.Success>().Subject.Snapshot.Limits;
        limits.Select(l => (l.Kind, l.Model, l.Percent)).Should().Equal(
            (ClaudeUsageLimitKind.Session, null, 58),
            (ClaudeUsageLimitKind.Weekly, null, 100),
            (ClaudeUsageLimitKind.WeeklyModel, "Opus", 12.4));
        limits[0].ResetsAt.Should().Be(new DateTimeOffset(2026, 11, 11, 14, 10, 0, TimeSpan.Zero));
        limits[1].ResetsAt.Should().BeNull();
    }

    [Fact]
    public async Task Request_carries_the_access_token_and_the_oauth_beta_header()
    {
        WriteCredentials();
        Serve();

        await Source().GetUsageAsync(CancellationToken.None);

        var headers = _server.LogEntries.Single().RequestMessage!.Headers!;
        headers["Authorization"].Single().Should().Be("Bearer sk-ant-oat01-usagetesttoken");
        headers["anthropic-beta"].Single().Should().Be("oauth-2025-04-20");
        headers["User-Agent"].Single().Should().StartWith("GitWidget/");
    }

    [Fact]
    public async Task Missing_sign_in_is_reported_without_a_request()
    {
        var result = await Source().GetUsageAsync(CancellationToken.None);

        result.Should().BeOfType<ClaudeUsageResult.SignedOut>().Which.Reason.Should().StartWith("Not signed in to Claude Code");
        _server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task Expired_sign_in_is_reported_without_a_request_and_the_file_is_left_alone()
    {
        WriteCredentials(expiresAt: Now.AddMinutes(-1));
        var before = File.ReadAllText(CredentialsFile);

        var result = await Source().GetUsageAsync(CancellationToken.None);

        result.Should().BeOfType<ClaudeUsageResult.SignedOut>().Which.Reason.Should().StartWith("Claude Code sign-in expired");
        _server.LogEntries.Should().BeEmpty("GitWidget never refreshes the token");
        File.ReadAllText(CredentialsFile).Should().Be(before);
    }

    [Fact]
    public async Task Environment_token_is_used_when_there_is_no_credentials_file()
    {
        _environment["CLAUDE_CODE_OAUTH_TOKEN"] = " sk-ant-oat01-fromenvironment ";
        Serve();

        var result = await Source().GetUsageAsync(CancellationToken.None);

        result.Should().BeOfType<ClaudeUsageResult.Success>().Which.Snapshot.Plan.Should().BeNull();
        _server.LogEntries.Single().RequestMessage!.Headers!["Authorization"].Single().Should().Be("Bearer sk-ant-oat01-fromenvironment");
    }

    [Fact]
    public async Task Rejected_token_counts_as_signed_out()
    {
        WriteCredentials();
        Serve(401, """{"type":"error","error":{"type":"authentication_error","message":"OAuth token has expired."}}""");

        var result = await Source().GetUsageAsync(CancellationToken.None);

        result.Should().BeOfType<ClaudeUsageResult.SignedOut>().Which.Reason.Should().Contain("rejected");
    }

    [Fact]
    public async Task Rate_limit_reports_the_requested_wait()
    {
        WriteCredentials();
        Serve(429, "{}", retryAfter: "120");

        var result = await Source().GetUsageAsync(CancellationToken.None);

        result.Should().BeOfType<ClaudeUsageResult.RateLimited>().Which.RetryAfter.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Theory]
    [InlineData(500, "{}", "HTTP 500")]
    [InlineData(200, "<html>gateway</html>", "unexpected answer")]
    public async Task Server_errors_and_unexpected_answers_are_unavailable_without_leaking_the_token(int status, string body, string expected)
    {
        WriteCredentials();
        Serve(status, body);

        var result = await Source().GetUsageAsync(CancellationToken.None);

        var reason = result.Should().BeOfType<ClaudeUsageResult.Unavailable>().Subject.Reason;
        reason.Should().Contain(expected);
        reason.Should().NotContain("sk-ant");
    }

    [Fact]
    public async Task Unreadable_credentials_count_as_signed_out()
    {
        File.WriteAllText(CredentialsFile, "{ not json");

        var result = await Source().GetUsageAsync(CancellationToken.None);

        result.Should().BeOfType<ClaudeUsageResult.SignedOut>().Which.Reason.Should().Be("The Claude Code sign-in could not be read");
    }

    [Fact]
    public void Default_credentials_path_follows_the_claude_config_directory()
    {
        var custom = new ClaudeUsageOptions { Environment = name => name == "CLAUDE_CONFIG_DIR" ? _directory : null };
        var standard = new ClaudeUsageOptions { Environment = _ => null };

        ClaudeCredentialReader.CredentialsPath(custom).Should().Be(Path.Combine(_directory, ".credentials.json"));
        ClaudeCredentialReader.CredentialsPath(standard).Should().Be(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json"));
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
