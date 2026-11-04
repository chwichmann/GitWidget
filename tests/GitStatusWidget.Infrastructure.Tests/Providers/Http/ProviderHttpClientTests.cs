using System.Net;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Infrastructure.Providers.Http;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace GitStatusWidget.Infrastructure.Tests.Providers.Http;

public sealed class ProviderHttpClientTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly HttpClient _http = new(ProviderHttpClient.CreateHandler());

    public void Dispose()
    {
        _http.Dispose();
        _server.Stop();
        _server.Dispose();
    }

    private ProviderHttpClient Client(int retries = 2) => new(
        _http,
        new NoPolicy(),
        new ProviderHttpOptions { RetryDelays = Enumerable.Repeat(TimeSpan.Zero, retries).ToArray(), RequestTimeout = TimeSpan.FromSeconds(5) },
        TimeProvider.System,
        NullLogger.Instance);

    private ProviderRequest Get(string path, string? etag = null) => new()
    {
        Uri = new Uri($"{_server.Url}{path}"),
        UserAgent = "GitWidget/0.1 git/2.47.0",
        IfNoneMatch = etag,
        ConfigureHeaders = h => h.TryAddWithoutValidation("PRIVATE-TOKEN", "test-token"),
    };

    [Fact]
    public async Task Json_success_returns_body_and_sends_user_agent_and_validator()
    {
        _server.Given(Request.Create().WithPath("/ok").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithHeader("ETag", "W/\"abc\"").WithBody("[1,2]"));

        var response = await Client().SendAsync(Get("/ok", "W/\"old\""), CancellationToken.None);

        response.Deserialize<int[]>(ProviderJson.SnakeCase).Should().Equal(1, 2);
        response.Headers.ETag.Should().Be("W/\"abc\"");
        var headers = _server.LogEntries.Single().RequestMessage!.Headers!;
        headers["User-Agent"].Single().Should().Be("GitWidget/0.1 git/2.47.0");
        headers["If-None-Match"].Single().Should().Be("W/\"old\"");
    }

    [Fact]
    public async Task Not_modified_is_returned_as_data()
    {
        _server.Given(Request.Create().WithPath("/same").UsingGet()).RespondWith(Response.Create().WithStatusCode(304));

        var response = await Client().SendAsync(Get("/same", "W/\"abc\""), CancellationToken.None);

        response.IsNotModified.Should().BeTrue();
    }

    [Fact]
    public async Task Html_success_is_gateway_interception()
    {
        _server.Given(Request.Create().WithPath("/html").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/html").WithBody("<html>logon</html>"));

        var act = () => Client().SendAsync(Get("/html"), CancellationToken.None);

        (await act.Should().ThrowAsync<ProviderFailureException>()).Which.Kind.Should().Be(ProviderFailureKind.GatewayInterception);
    }

    [Fact]
    public async Task Redirect_to_other_host_is_gateway_interception_and_not_followed()
    {
        _server.Given(Request.Create().WithPath("/api").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", "https://portal.example.net/cgi/tm?code=1"));

        var act = () => Client().SendAsync(Get("/api"), CancellationToken.None);

        (await act.Should().ThrowAsync<ProviderFailureException>()).Which.Kind.Should().Be(ProviderFailureKind.GatewayInterception);
        _server.LogEntries.Should().HaveCount(1);
    }

    [Fact]
    public async Task Same_host_redirect_is_reported_with_location()
    {
        _server.Given(Request.Create().WithPath("/old").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(301).WithHeader("Location", $"{_server.Url}/projects/99"));

        var act = () => Client().SendAsync(Get("/old"), CancellationToken.None);

        (await act.Should().ThrowAsync<ProviderRedirectException>()).Which.Location.AbsolutePath.Should().Be("/projects/99");
    }

    [Theory]
    [InlineData(401, ProviderFailureKind.AuthenticationFailed)]
    [InlineData(403, ProviderFailureKind.AuthorizationFailed)]
    [InlineData(404, ProviderFailureKind.NotFound)]
    public async Task Client_errors_are_classified(int status, ProviderFailureKind expected)
    {
        _server.Given(Request.Create().WithPath("/e").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody("{\"message\":\"x\"}"));

        var act = () => Client().SendAsync(Get("/e"), CancellationToken.None);

        (await act.Should().ThrowAsync<ProviderFailureException>()).Which.Kind.Should().Be(expected);
        _server.LogEntries.Should().HaveCount(1, "client errors are not retried");
    }

    [Fact]
    public async Task Rate_limit_is_not_retried_and_carries_retry_after()
    {
        _server.Given(Request.Create().WithPath("/limited").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "120").WithBody("Retry later"));

        var before = DateTimeOffset.UtcNow;
        var act = () => Client().SendAsync(Get("/limited"), CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<ProviderFailureException>()).Which;
        failure.Kind.Should().Be(ProviderFailureKind.RateLimited);
        failure.RetryAfter.Should().BeOnOrAfter(before.AddSeconds(119));
        _server.LogEntries.Should().HaveCount(1);
    }

    [Fact]
    public async Task Server_errors_are_retried_then_succeed()
    {
        _server.Given(Request.Create().WithPath("/flaky").UsingGet()).InScenario("flaky").WillSetStateTo("second")
            .RespondWith(Response.Create().WithStatusCode(503));
        _server.Given(Request.Create().WithPath("/flaky").UsingGet()).InScenario("flaky").WhenStateIs("second")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{}"));

        var response = await Client().SendAsync(Get("/flaky"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _server.LogEntries.Should().HaveCount(2);
    }

    [Fact]
    public async Task Server_errors_give_up_after_configured_retries()
    {
        _server.Given(Request.Create().WithPath("/down").UsingGet()).RespondWith(Response.Create().WithStatusCode(502));

        var act = () => Client(retries: 2).SendAsync(Get("/down"), CancellationToken.None);

        (await act.Should().ThrowAsync<ProviderFailureException>()).Which.Kind.Should().Be(ProviderFailureKind.Transient);
        _server.LogEntries.Should().HaveCount(3);
    }

    [Fact]
    public async Task Oversized_responses_are_rejected()
    {
        _server.Given(Request.Create().WithPath("/big").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(new string('1', 2048)));
        var client = new ProviderHttpClient(_http, new NoPolicy(), new ProviderHttpOptions { MaxResponseBytes = 1024, RetryDelays = [] }, TimeProvider.System, NullLogger.Instance);

        var act = () => client.SendAsync(Get("/big"), CancellationToken.None);

        (await act.Should().ThrowAsync<ProviderFailureException>()).Which.Kind.Should().Be(ProviderFailureKind.Malformed);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        _server.Given(Request.Create().WithPath("/slow").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(3)).WithHeader("Content-Type", "application/json").WithBody("{}"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => Client().SendAsync(Get("/slow"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class NoPolicy : IProviderResponsePolicy
    {
        public RateLimitInfo? ReadRateLimit(ResponseHeaders headers) => null;

        public ProviderFailureException? Classify(HttpStatusCode statusCode, ResponseHeaders headers, string bodySnippet, DateTimeOffset now) => null;
    }
}
