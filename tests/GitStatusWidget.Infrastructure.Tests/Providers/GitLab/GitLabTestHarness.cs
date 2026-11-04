using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Infrastructure.Providers.GitLab;
using GitStatusWidget.Infrastructure.Providers.Http;
using Microsoft.Extensions.Logging;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace GitStatusWidget.Infrastructure.Tests.Providers.GitLab;

/// <summary>
/// WireMock-backed GitLab test environment with synthetic, sanitized fixtures shaped like GitLab 19.5 responses
/// (key lists from gitlab-api.md §19). Contains no real data and no real token.
/// </summary>
internal sealed class GitLabTestHarness : IDisposable
{
    public const string Token = "glpat-TEST-ONLY-0000000000";
    public const long ProjectId = 4242;
    public const string FullPath = "acme/tools/widget";
    public const string EncodedPath = "acme%2Ftools%2Fwidget";
    public const string UserAgent = "GitWidget/0.1 git/2.47.0";

    public static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset WindowStart = Now.AddDays(-14);

    private readonly HttpClient _http = new(ProviderHttpClient.CreateHandler());

    public GitLabTestHarness(string root = "/")
    {
        Root = root;
        Server = WireMockServer.Start();
        Credentials = new InMemoryCredentialStore();
        Credentials.Set(CredentialReference, Token);
        Logger = new CapturingLogger<GitLabStatusProvider>();
        Factory = new SingleClientFactory(_http);
    }

    public string CredentialReference { get; } = "GitStatusWidget/test";

    public string Root { get; }

    public WireMockServer Server { get; }

    public InMemoryCredentialStore Credentials { get; }

    public CapturingLogger<GitLabStatusProvider> Logger { get; }

    public SingleClientFactory Factory { get; }

    /// <summary>Path prefix of the REST API, e.g. <c>/api/v4/</c> or <c>/gitlab/api/v4/</c>.</summary>
    public string Api => Root + "api/v4/";

    public string ProjectApi => Api + "projects/" + ProjectId.ToString(CultureInfo.InvariantCulture);

    public string WebBase => Server.Url + Root.TrimEnd('/');

    public GitLabStatusProvider CreateProvider(int retries = 0) => new(
        Factory,
        Credentials,
        new FixedTimeProvider(Now),
        Logger,
        new ProviderHttpOptions
        {
            RetryDelays = Enumerable.Repeat(TimeSpan.Zero, retries).ToArray(),
            RetryJitter = 0,
            RequestTimeout = TimeSpan.FromSeconds(10),
        });

    public RepositoryConfiguration Configuration(string fullPath = FullPath, string? userAgent = UserAgent)
    {
        var slash = fullPath.LastIndexOf('/');
        return new RepositoryConfiguration(
            Guid.Parse("6d1f1a8e-3c2b-4b7e-9d55-0a1b2c3d4e5f"),
            RepositoryProviderKind.GitLab,
            new Uri(Server.Url + Root),
            slash > 0 ? fullPath[..slash] : string.Empty,
            slash > 0 ? fullPath[(slash + 1)..] : fullPath,
            CredentialReference,
            RepositoryConfiguration.DefaultIssueInterval,
            RepositoryConfiguration.DefaultBuildInterval,
            RepositoryConfiguration.DefaultMetadataInterval,
            RepositoryConfiguration.DefaultReconciliationInterval,
            IsEnabled: true)
        {
            UserAgent = userAgent,
            AllowInsecureHttp = true,
        };
    }

    public static SnapshotRequestContext FirstContext() => new(null, ProviderSyncState.Empty, RefreshScope.All, WindowStart);

    public IEnumerable<ILogEntry> Requests(string pathContains) =>
        Server.LogEntries.Where(e => e.RequestMessage!.AbsolutePath.Contains(pathContains, StringComparison.Ordinal));

    public void Dispose()
    {
        _http.Dispose();
        Server.Stop();
        Server.Dispose();
    }

    // ---------------------------------------------------------------- stubs

    public void StubJson(string path, object body, int status = 200, Action<IRequestBuilder>? match = null, Action<IResponseBuilder>? response = null, int priority = 10)
    {
        var request = Request.Create().WithPath(path).UsingGet();
        match?.Invoke(request);
        var builder = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(Serialize(body));
        response?.Invoke(builder);
        Server.Given(request).AtPriority(priority).RespondWith(builder);
    }

    public void StubProject(object? project = null)
    {
        project ??= Fixtures.Project(WebBase);

        // WireMock matches the decoded path; tests assert the raw URL (RequestMessage.Url) keeps %2F.
        StubJson(Api + "projects/" + FullPath, project);
        StubJson(ProjectApi, project);
    }

    public void StubLabels(params object[] labels) => StubJson(ProjectApi + "/labels", labels);

    public void StubMilestones(params object[] milestones) => StubJson(ProjectApi + "/milestones", milestones);

    public void StubIssues(string state, object[] issues, int priority = 10) =>
        StubJson(ProjectApi + "/issues", issues, match: r => r.WithParam("state", state), priority: priority);

    public void StubMergeRequests(string state, object[] mergeRequests) =>
        StubJson(ProjectApi + "/merge_requests", mergeRequests, match: r => r.WithParam("state", state));

    public void StubPipelines(object[] pipelines, string? etag = null) =>
        StubJson(ProjectApi + "/pipelines", pipelines, response: etag is null ? null : r => r.WithHeader("ETag", etag));

    public void StubGraphQl(object data, int priority = 10)
    {
        Server.Given(Request.Create().WithPath(Root + "api/graphql").UsingPost()).AtPriority(priority)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(Serialize(new { data })));
    }

    public void StubCounts(int open, int closed, params int[] milestoneClosed)
    {
        var project = new Dictionary<string, object> { ["io"] = new { count = open }, ["ic"] = new { count = closed } };
        for (var i = 0; i < milestoneClosed.Length; i++)
        {
            project[$"m{i}"] = new { count = milestoneClosed[i] };
        }

        StubGraphQl(new { project });
    }

    /// <summary>A small, consistent project: 2 open issues, 1 recently closed, 1 open MR, 1 merged MR, 1 pipeline.</summary>
    public void StubStandardProject()
    {
        StubProject();
        StubLabels(Fixtures.Label("bug", "#d9534f"), Fixtures.Label("priority::high", "#FF0000", groupLabel: true));
        StubMilestones(Fixtures.Milestone(501, "v1.2"), Fixtures.Milestone(7324682, "19.9", group: true));
        StubIssues("opened", [
            Fixtures.Issue(WebBase, 900001, 1, "opened", Now.AddHours(-5), milestone: (501, "v1.2"), labels: ["bug"]),
            Fixtures.Issue(WebBase, 900002, 2, "opened", Now.AddHours(-4)),
        ]);
        StubIssues("closed", [Fixtures.Issue(WebBase, 900003, 3, "closed", Now.AddHours(-3), milestone: (501, "v1.2"))]);
        StubMergeRequests("opened", [Fixtures.MergeRequest(WebBase, 562001, 11, "opened", Now.AddHours(-2), draft: true)]);
        StubMergeRequests("all", [Fixtures.MergeRequest(WebBase, 562002, 12, "merged", Now.AddHours(-1))]);
        StubPipelines([Fixtures.Pipeline(WebBase, 3028771539, 54761, "running", Now.AddMinutes(-10))], etag: "W/\"pipelines-v1\"");
        StubCounts(open: 2, closed: 40, 7, 3);
    }

    public static string Serialize(object body) => JsonSerializer.Serialize(body);
}

/// <summary>Synthetic GitLab 19.5-shaped JSON objects.</summary>
internal static class Fixtures
{
    public static string Ts(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static object User(long id, string username) => new
    {
        id,
        username,
        public_email = "should-never-be-mapped@example.invalid",
        name = "User " + username,
        state = "active",
        locked = false,
        avatar_url = (string?)null,
        web_url = "https://gitlab.example.invalid/" + username,
    };

    public static object Project(string webBase, long id = GitLabTestHarness.ProjectId, string issues = "enabled", string mergeRequests = "enabled", string builds = "enabled", string? defaultBranch = "main") => new
    {
        id,
        description = (string?)null,
        name = "widget",
        name_with_namespace = "acme / tools / widget",
        path = "widget",
        path_with_namespace = GitLabTestHarness.FullPath,
        created_at = "2025-01-01T00:00:00.000Z",
        default_branch = defaultBranch,
        web_url = webBase + "/acme/tools/widget",
        @namespace = new { id = 77, name = "tools", path = "tools", kind = "group", full_path = "acme/tools", parent_id = 76 },
        archived = false,
        empty_repo = false,
        visibility = "private",
        issues_enabled = issues != "disabled",
        merge_requests_enabled = mergeRequests != "disabled",
        jobs_enabled = builds != "disabled",
        issues_access_level = issues,
        merge_requests_access_level = mergeRequests,
        builds_access_level = builds,
        repository_access_level = "enabled",
        open_issues_count = 999,
        last_activity_at = "2026-11-04T10:00:00.000Z",
    };

    public static object Label(string name, string color, bool groupLabel = false) => new
    {
        id = Math.Abs(name.GetHashCode(StringComparison.Ordinal)) % 100000,
        name,
        description = (string?)null,
        description_html = "",
        text_color = "#FFFFFF",
        color,
        subscribed = false,
        priority = (int?)null,
        is_project_label = !groupLabel,
        archived = false,
    };

    public static object Milestone(long id, string title, string state = "active", bool group = false) => group
        ? new
        {
            id,
            iid = id % 1000,
            group_id = 9970L,
            title,
            description = "",
            state,
            created_at = "2026-01-01T00:00:00.000Z",
            updated_at = "2026-01-01T00:00:00.000Z",
            due_date = "2027-02-12",
            start_date = "2027-01-09",
            expired = false,
            web_url = "https://gitlab.example.invalid/groups/acme/-/milestones/" + id,
        }
        : (object)new
        {
            id,
            iid = id % 1000,
            project_id = GitLabTestHarness.ProjectId,
            title,
            description = (string?)null,
            state,
            created_at = "2026-01-01T00:00:00.000Z",
            updated_at = "2026-01-01T00:00:00.000Z",
            due_date = "2026-11-30",
            start_date = "2026-11-01",
            expired = false,
            web_url = "https://gitlab.example.invalid/acme/tools/widget/-/milestones/" + id,
        };

    public static object Issue(
        string webBase,
        long id,
        long iid,
        string state,
        DateTimeOffset updatedAt,
        (long Id, string Title)? milestone = null,
        string[]? labels = null,
        string issueType = "issue") => new
        {
            id,
            iid,
            project_id = GitLabTestHarness.ProjectId,
            title = $"Synthetic issue {iid}",
            description = "never mapped",
            state,
            created_at = Ts(updatedAt.AddDays(-1)),
            updated_at = Ts(updatedAt),
            closed_at = state == "closed" ? Ts(updatedAt) : null,
            closed_by = (object?)null,
            labels = (labels ?? []).Select(l => new { id = 11, name = l, color = "#d9534f", text_color = "#FFFFFF", description = (string?)null, description_html = "", archived = false }).ToArray(),
            milestone = milestone is { } m ? new { id = m.Id, iid = 7, project_id = GitLabTestHarness.ProjectId, title = m.Title, state = "active" } : null,
            assignees = new[] { User(77, "jdoe") },
            author = User(78, "asmith"),
            type = issueType.ToUpperInvariant(),
            issue_type = issueType,
            confidential = false,
            moved_to_id = (long?)null,
            web_url = $"{webBase}/acme/tools/widget/-/work_items/{iid}",
        };

    public static object MergeRequest(string webBase, long id, long iid, string state, DateTimeOffset updatedAt, bool draft = false) => new
    {
        id,
        iid,
        project_id = GitLabTestHarness.ProjectId,
        title = (draft ? "Draft: " : string.Empty) + $"Synthetic MR {iid}",
        description = "never mapped",
        state,
        draft,
        work_in_progress = draft,
        created_at = Ts(updatedAt.AddDays(-1)),
        updated_at = Ts(updatedAt),
        merged_at = state == "merged" ? Ts(updatedAt) : null,
        closed_at = state == "closed" ? Ts(updatedAt) : null,
        merge_user = (object?)null,
        source_branch = "feature/x",
        target_branch = "main",
        detailed_merge_status = "mergeable",
        author = User(78, "asmith"),
        web_url = $"{webBase}/acme/tools/widget/-/merge_requests/{iid}",
    };

    public static object Pipeline(string webBase, long id, long iid, string status, DateTimeOffset updatedAt, string? name = null) => new
    {
        id,
        iid,
        project_id = GitLabTestHarness.ProjectId,
        sha = "dc9a8ed4fe020c69915a3b0a43c8b262d07f865b",
        @ref = "main",
        status,
        source = "push",
        name,
        created_at = Ts(updatedAt.AddMinutes(-5)),
        updated_at = Ts(updatedAt),
        web_url = $"{webBase}/acme/tools/widget/-/pipelines/{id}",
    };
}

internal static class ResultAssertions
{
    public static ProviderFetchResult.Success ShouldSucceed(this ProviderFetchResult result, GitLabTestHarness harness)
    {
        if (result is ProviderFetchResult.Failure failure)
        {
            var requests = string.Join("\n", harness.Server.LogEntries.Select(e => $"{e.RequestMessage!.Method} {e.RequestMessage.Url} [path {e.RequestMessage.Path} abs {e.RequestMessage.AbsolutePath}] -> {e.ResponseMessage?.StatusCode}"));
            Assert.Fail($"Fetch failed: {failure.Kind} {failure.Message}\n{requests}");
        }

        return result.Should().BeOfType<ProviderFetchResult.Success>().Subject;
    }

    public static ProviderFetchResult.Failure ShouldFail(this ProviderFetchResult result, ProviderFailureKind kind)
    {
        var failure = result.Should().BeOfType<ProviderFetchResult.Failure>().Subject;
        failure.Kind.Should().Be(kind, failure.Message);
        return failure;
    }
}

internal sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public bool IsAvailable { get; set; } = true;

    public string StoreName => "In-memory test store";

    public int ReadCount { get; private set; }

    public void Set(string key, string value) => _secrets[key] = value;

    public Task<Secret?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        ReadCount++;
        return Task.FromResult(_secrets.TryGetValue(key, out var value) ? new Secret(value) : null);
    }

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

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
{
    public List<string> Names { get; } = [];

    public HttpClient CreateClient(string name)
    {
        Names.Add(name);
        return client;
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Messages.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}
