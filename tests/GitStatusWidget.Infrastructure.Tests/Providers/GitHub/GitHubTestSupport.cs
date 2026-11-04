using System.Collections.Concurrent;
using System.Text.Json;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Infrastructure.Providers.GitHub;
using GitStatusWidget.Infrastructure.Providers.Http;
using Microsoft.Extensions.Logging;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace GitStatusWidget.Infrastructure.Tests.Providers.GitHub;

internal sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public bool IsAvailable { get; set; } = true;

    public string StoreName => "In-memory test store";

    public Task<Secret?> ReadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_secrets.TryGetValue(key, out var value) ? new Secret(value) : null);

    public Task WriteAsync(string key, Secret secret, CancellationToken cancellationToken)
    {
        _secrets[key] = secret.Reveal();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        _secrets.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<string> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var structured = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? string.Join(";", pairs.Select(p => $"{p.Key}={p.Value}"))
            : string.Empty;
        Entries.Enqueue($"{logLevel} {eventId.Id} {formatter(state, exception)} [{structured}] {exception}");
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class TestHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly HttpMessageHandler _handler = ProviderHttpClient.CreateHandler();

    public List<string> RequestedNames { get; } = [];

    public HttpClient CreateClient(string name)
    {
        RequestedNames.Add(name);
        return new HttpClient(_handler, disposeHandler: false);
    }

    public void Dispose() => _handler.Dispose();
}

/// <summary>
/// WireMock server emulating a GitHub Enterprise Server layout (<c>/api/v3/</c>, <c>/api/graphql</c>) with synthetic,
/// sanitized data.
/// </summary>
internal sealed class GitHubFakeServer : IDisposable
{
    public const string Owner = "octo-org";
    public const string Name = "widget";
    public const string RepoNodeId = "R_kgDOTestRepo1";
    public const string RepoPath = "/api/v3/repos/octo-org/widget";
    public const string GraphQlPath = "/api/graphql";

    public WireMockServer Server { get; } = WireMockServer.Start();

    public string Url => Server.Url!;

    public Uri ServerUri => new(Url + "/");

    public IReadOnlyList<ILogEntry> Requests => Server.LogEntries.ToList();

    public void Dispose()
    {
        Server.Stop();
        Server.Dispose();
    }

    public void Get(
        string path,
        object? body,
        int status = 200,
        string? etag = null,
        string? link = null,
        (string Key, string Value)[]? query = null,
        string? whenIfNoneMatch = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string contentType = "application/json; charset=utf-8",
        int priority = 10)
    {
        var request = Request.Create().WithPath(path).UsingGet();
        foreach (var (key, value) in query ?? [])
        {
            request = request.WithParam(key, value);
        }

        if (whenIfNoneMatch is not null)
        {
            request = request.WithHeader("If-None-Match", whenIfNoneMatch);
        }

        Server.Given(request).AtPriority(priority).RespondWith(BuildResponse(body, status, etag, link, headers, contentType));
    }

    public void NotModified(string path, string etag, (string Key, string Value)[]? query = null)
    {
        var request = Request.Create().WithPath(path).UsingGet().WithHeader("If-None-Match", etag);
        foreach (var (key, value) in query ?? [])
        {
            request = request.WithParam(key, value);
        }

        // 304: no body, no Link header (G6); the strong form of the validator is echoed (G7).
        Server.Given(request).AtPriority(1).RespondWith(Response.Create().WithStatusCode(304).WithHeader("ETag", etag.Replace("W/", string.Empty, StringComparison.Ordinal)));
    }

    public void GraphQl(object body, Func<string, bool>? when = null, int status = 200, IReadOnlyDictionary<string, string>? headers = null, int priority = 10)
    {
        var request = Request.Create().WithPath(GraphQlPath).UsingPost();
        if (when is not null)
        {
            request = request.WithBody(new Func<string?, bool>(b => b is not null && when(b)));
        }

        Server.Given(request).AtPriority(priority).RespondWith(BuildResponse(body, status, null, null, headers ?? RateHeaders("graphql"), "application/json; charset=utf-8"));
    }

    public string NextLink(string path, string query) => $"<{Url}{path}?{query}>; rel=\"next\"";

    /// <summary>Stubs every request of a complete first fetch.</summary>
    public void StandardRepository(
        IReadOnlyList<object>? open = null,
        IReadOnlyList<object>? closed = null,
        IReadOnlyList<object>? milestones = null,
        IReadOnlyList<object>? labels = null,
        object? runs = null,
        object? counts = null,
        string repoPath = RepoPath,
        object? repository = null)
    {
        open ??= [GitHubFixtures.Issue(1, GitHubFixtures.Now.AddHours(-5))];
        closed ??= [];
        milestones ??= [];
        labels ??= [];
        Get(repoPath, repository ?? GitHubFixtures.Repository(), etag: "W/\"repo-1\"", headers: RateHeaders("core"));
        Get($"{repoPath}/milestones", milestones, etag: "W/\"ms-1\"", query: [("state", "all")]);
        Get($"{repoPath}/labels", labels, etag: "W/\"labels-1\"");
        Get($"{repoPath}/issues", open, query: [("state", "open")]);
        Get($"{repoPath}/issues", closed, query: [("state", "closed")]);
        Get($"{repoPath}/actions/runs", runs ?? GitHubFixtures.Runs(), etag: "W/\"runs-1\"");
        var openIssueCount = open.Count(o => !JsonSerializer.Serialize(o).Contains("\"pull_request\"", StringComparison.Ordinal));
        GraphQl(counts ?? GitHubFixtures.Counts(openIssueCount, 0));
    }

    public static Dictionary<string, string> RateHeaders(string resource, int remaining = 4990, int limit = 5000, long reset = 1793824139) => new()
    {
        ["X-RateLimit-Limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["X-RateLimit-Remaining"] = remaining.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["X-RateLimit-Reset"] = reset.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["X-RateLimit-Resource"] = resource,
    };

    public static string? Header(ILogEntry entry, string name) =>
        entry.RequestMessage?.Headers?.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();

    private static IResponseBuilder BuildResponse(object? body, int status, string? etag, string? link, IReadOnlyDictionary<string, string>? headers, string contentType)
    {
        var response = Response.Create().WithStatusCode(status);
        if (body is not null)
        {
            response = response.WithHeader("Content-Type", contentType).WithBody(body as string ?? JsonSerializer.Serialize(body));
        }

        if (etag is not null)
        {
            response = response.WithHeader("ETag", etag);
        }

        if (link is not null)
        {
            response = response.WithHeader("Link", link);
        }

        foreach (var (key, value) in headers ?? new Dictionary<string, string>())
        {
            response = response.WithHeader(key, value);
        }

        return response;
    }
}

/// <summary>Synthetic GitHub payloads (no real users, e-mails or tokens).</summary>
internal static class GitHubFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset ClosedWindowStart = Now.AddDays(-14);

    public static object Repository(string fullName = "octo-org/widget", string defaultBranch = "main", bool hasIssues = true, bool? hasPullRequests = true) => new
    {
        id = 4_200_000_001L,
        node_id = GitHubFakeServer.RepoNodeId,
        name = fullName.Split('/')[1],
        full_name = fullName,
        owner = new { login = fullName.Split('/')[0], id = 101L, node_id = "O_kgDOOwner01", type = "Organization" },
        @private = false,
        visibility = "public",
        archived = false,
        default_branch = defaultBranch,
        has_issues = hasIssues,
        has_pull_requests = hasPullRequests,
        open_issues_count = 999,
        html_url = $"https://github.com/{fullName}",
        some_future_field = new { nested = true },
    };

    public static object User(string login) => new
    {
        login,
        id = 7_000_000_000L + login.Length,
        node_id = $"U_{login}",
        type = "User",
        email = $"{login}@example.invalid",
    };

    public static object Issue(
        int number,
        DateTimeOffset updated,
        string state = "open",
        object? milestone = null,
        object[]? labels = null,
        object[]? assignees = null,
        DateTimeOffset? created = null) => new
        {
            id = 6_000_000_000L + number,
            node_id = $"I_kwTest{number:D5}",
            number,
            title = $"Synthetic issue {number}",
            state,
            state_reason = state == "closed" ? "completed" : null,
            html_url = $"https://github.com/octo-org/widget/issues/{number}",
            user = User("alice"),
            labels = labels ?? [],
            assignees = assignees ?? [],
            milestone,
            comments = 0,
            created_at = Iso(created ?? updated.AddDays(-1)),
            updated_at = Iso(updated),
            closed_at = state == "closed" ? Iso(updated) : null,
            body = "Synthetic body that must never be logged.",
        };

    public static object PullRequest(int number, DateTimeOffset updated, string state = "open", bool merged = false, bool draft = false) => new
    {
        id = 6_100_000_000L + number,
        node_id = $"PR_kwTest{number:D5}",
        number,
        title = $"Synthetic pull request {number}",
        state,
        draft,
        html_url = $"https://github.com/octo-org/widget/pull/{number}",
        user = User("bob"),
        labels = Array.Empty<object>(),
        assignees = Array.Empty<object>(),
        milestone = (object?)null,
        created_at = Iso(updated.AddDays(-2)),
        updated_at = Iso(updated),
        closed_at = state == "closed" ? Iso(updated) : null,
        pull_request = new
        {
            url = $"https://api.github.com/repos/octo-org/widget/pulls/{number}",
            html_url = $"https://github.com/octo-org/widget/pull/{number}",
            merged_at = merged ? Iso(updated) : null,
        },
    };

    public static object Milestone(int number, string nodeId, string title, string state = "open", string? dueOn = null) => new
    {
        id = 9_000_000L + number,
        node_id = nodeId,
        number,
        title,
        description = (string?)null,
        state,
        open_issues = 999,
        closed_issues = 999,
        html_url = $"https://github.com/octo-org/widget/milestone/{number}",
        created_at = "2026-01-01T00:00:00Z",
        updated_at = "2026-10-01T00:00:00Z",
        closed_at = (string?)null,
        due_on = dueOn,
    };

    public static object Label(string name, string color, string? description = null) => new
    {
        id = 8_955_746_444L,
        node_id = $"LA_{name}",
        name,
        color,
        description,
        @default = false,
    };

    public static object Run(long id, string? status, string? conclusion, string name = "CI", DateTimeOffset? created = null, string evt = "push", string headRepository = "octo-org/widget") => new
    {
        id,
        node_id = $"WFR_{id}",
        name,
        display_title = "Synthetic commit",
        run_number = 7,
        run_attempt = 1,
        @event = evt,
        status,
        conclusion,
        workflow_id = 389_529_352L,
        head_branch = "main",
        head_sha = "eeb44bc432c0107de002dadd34a61b4e2bd15882",
        head_repository = new { full_name = headRepository },
        html_url = $"https://github.com/octo-org/widget/actions/runs/{id}",
        created_at = Iso(created ?? Now.AddMinutes(-30)),
        run_started_at = Iso(created ?? Now.AddMinutes(-30)),
        updated_at = Iso((created ?? Now.AddMinutes(-30)).AddMinutes(5)),
        pull_requests = Array.Empty<object>(),
    };

    public static object Runs(params object[] runs) => new { total_count = runs.Length, workflow_runs = runs };

    public static object Counts(int open, int closed, (string Id, int Closed)[]? milestones = null, bool hasNextPage = false, string? endCursor = null) => new
    {
        data = new
        {
            repository = new
            {
                openIssues = new { totalCount = open },
                closedIssues = new { totalCount = closed },
                milestones = new
                {
                    pageInfo = new { hasNextPage, endCursor },
                    nodes = (milestones ?? []).Select(m => new { id = m.Id, closedIssues = new { totalCount = m.Closed } }).ToArray(),
                },
            },
        },
    };

    public static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Provider plus fake server, credential store, logger and clock.</summary>
internal sealed class GitHubProviderHarness : IDisposable
{
    public const string Token = "ghp_SyntheticTestTokenDoNotUse0123456789";

    private readonly TestHttpClientFactory _factory = new();

    public GitHubProviderHarness(int retries = 0)
    {
        var id = Guid.Parse("6f4f7c1e-0000-4000-8000-000000000001");
        Configuration = new RepositoryConfiguration(
            id,
            RepositoryProviderKind.GitHub,
            Api.ServerUri,
            GitHubFakeServer.Owner,
            GitHubFakeServer.Name,
            RepositoryConfiguration.DefaultCredentialReference(id),
            RepositoryConfiguration.DefaultIssueInterval,
            RepositoryConfiguration.DefaultBuildInterval,
            RepositoryConfiguration.DefaultMetadataInterval,
            RepositoryConfiguration.DefaultReconciliationInterval,
            IsEnabled: true)
        { AllowInsecureHttp = true };
        Credentials.WriteAsync(Configuration.CredentialReference, new Secret(Token), CancellationToken.None).GetAwaiter().GetResult();
        Provider = new GitHubStatusProvider(
            _factory,
            Credentials,
            Time,
            Logger,
            new ProviderHttpOptions { RetryDelays = Enumerable.Repeat(TimeSpan.Zero, retries).ToArray(), RequestTimeout = TimeSpan.FromSeconds(10) });
    }

    public GitHubFakeServer Api { get; } = new();

    public InMemoryCredentialStore Credentials { get; } = new();

    public CapturingLogger<GitHubStatusProvider> Logger { get; } = new();

    public FixedTimeProvider Time { get; } = new(GitHubFixtures.Now);

    public GitHubStatusProvider Provider { get; }

    public IReadOnlyList<string> ClientNames => _factory.RequestedNames;

    public RepositoryConfiguration Configuration { get; }

    public static SnapshotRequestContext FirstFetch() =>
        new(null, ProviderSyncState.Empty, RefreshScope.All, GitHubFixtures.ClosedWindowStart);

    public Task<ProviderFetchResult> FetchAsync(SnapshotRequestContext? context = null, CancellationToken cancellationToken = default) =>
        Provider.FetchAsync(Configuration, context ?? FirstFetch(), cancellationToken);

    public async Task<ProviderFetchResult.Success> FetchSuccessAsync(SnapshotRequestContext? context = null)
    {
        var result = await FetchAsync(context);
        return result.Should().BeOfType<ProviderFetchResult.Success>().Which;
    }

    public async Task<ProviderFetchResult.Failure> FetchFailureAsync(SnapshotRequestContext? context = null)
    {
        var result = await FetchAsync(context);
        return result.Should().BeOfType<ProviderFetchResult.Failure>().Which;
    }

    public void Dispose()
    {
        _factory.Dispose();
        Api.Dispose();
    }
}
