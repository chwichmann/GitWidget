using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Labels;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Domain.Snapshots;
using GitStatusWidget.Infrastructure.Providers.Http;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Infrastructure.Providers.GitHub;

/// <summary>
/// Read-only GitHub adapter (architecture §10, github-api.md §13): REST v3 for items, GraphQL for exact counts,
/// ETags for cheap change detection, Link-header pagination, and no partial results.
/// </summary>
internal sealed partial class GitHubStatusProvider : IRepositoryStatusProvider
{
    /// <summary>Name of the configured <see cref="HttpClient"/>.</summary>
    public const string HttpClientName = "GitHub";

    /// <summary>Sync-state key of the work-item watermark (max <c>updated_at</c> seen, ISO 8601).</summary>
    public const string WatermarkKey = "github.workItems.watermark";

    /// <summary>Number of runs requested for the default branch.</summary>
    public const int BuildPageSize = 20;

    /// <summary>Delta pages read before falling back to a full reconciliation.</summary>
    public const int MaxDeltaPages = 10;

    /// <summary>Overlap applied to the watermark (G18: <c>since</c>/<c>updated_at</c> have second precision).</summary>
    public static readonly TimeSpan DeltaOverlap = TimeSpan.FromMinutes(2);

    private const int PageSize = 100;
    private const int MaxMilestoneQueryPages = 50;

    // Variables only; owner and name are never interpolated into the query text.
    internal const string CountsQuery = """
        query GitWidgetCounts($owner: String!, $name: String!, $milestoneCursor: String) {
          repository(owner: $owner, name: $name) {
            openIssues: issues(states: OPEN) { totalCount }
            closedIssues: issues(states: CLOSED) { totalCount }
            milestones(first: 100, after: $milestoneCursor, states: [OPEN, CLOSED]) {
              pageInfo { hasNextPage endCursor }
              nodes { id closedIssues: issues(states: CLOSED) { totalCount } }
            }
          }
        }
        """;

    private static readonly ConcurrentDictionary<string, byte> ReportedUnknownBuildStates = new(StringComparer.Ordinal);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICredentialStore _credentials;
    private readonly TimeProvider _time;
    private readonly ILogger<GitHubStatusProvider> _logger;
    private readonly ProviderHttpOptions _httpOptions;

    public GitHubStatusProvider(
        IHttpClientFactory httpClientFactory,
        ICredentialStore credentials,
        TimeProvider time,
        ILogger<GitHubStatusProvider> logger,
        ProviderHttpOptions? httpOptions = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _credentials = credentials;
        _time = time;
        _logger = logger;
        _httpOptions = httpOptions ?? new ProviderHttpOptions();
    }

    /// <inheritdoc />
    public RepositoryProviderKind Kind => RepositoryProviderKind.GitHub;

    /// <inheritdoc />
    public Task<ProviderFetchResult> FetchAsync(RepositoryConfiguration configuration, SnapshotRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);
        var policy = new GitHubResponsePolicy();
        return ProviderBoundary.RunAsync(() => FetchCoreAsync(configuration, context, policy, cancellationToken), () => policy.MostConstrained, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ProviderConnectionResult> TestConnectionAsync(RepositoryConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return ProviderBoundary.RunConnectionAsync(() => TestConnectionCoreAsync(configuration, cancellationToken), cancellationToken);
    }

    private async Task<ProviderFetchResult.Success> FetchCoreAsync(
        RepositoryConfiguration configuration,
        SnapshotRequestContext context,
        GitHubResponsePolicy policy,
        CancellationToken cancellationToken)
    {
        var endpoints = GitHubEndpoints.From(configuration);
        var previous = context.Previous;
        var scope = previous is null ? RefreshScope.All : context.Scope;
        var (owner, name) = ResolvePath(configuration, previous);

        var session = await CreateSessionAsync(configuration, endpoints, policy, cancellationToken).ConfigureAwait(false);
        var cache = new GitHubConditionalCache(context.SyncState, enabled: previous is not null, _time);

        var identity = previous?.Repository;
        var defaultBranch = previous?.DefaultBranch;
        var features = previous?.Features ?? RepositoryFeatures.All;
        var milestones = SectionUpdate<MilestoneSnapshot>.Unchanged;
        var labels = SectionUpdate<LabelSnapshot>.Unchanged;
        var builds = SectionUpdate<BuildSnapshot>.Unchanged;
        var issues = WorkItemUpdate<IssueSnapshot>.Unchanged;
        var changeRequests = WorkItemUpdate<ChangeRequestSnapshot>.Unchanged;
        CountsUpdate? counts = null;
        var needCounts = false;
        var watermark = ParseWatermark(context.SyncState.GetValue(WatermarkKey));

        if (scope.HasFlag(RefreshScope.Metadata))
        {
            var repository = await FetchRepositoryAsync(session, cache, owner, name, cancellationToken).ConfigureAwait(false);
            if (repository is not null)
            {
                identity = GitHubMapper.MapIdentity(repository, endpoints.Host);
                defaultBranch = string.IsNullOrEmpty(repository.DefaultBranch) ? null : repository.DefaultBranch;
                features = features with { Issues = repository.HasIssues ?? true, ChangeRequests = repository.HasPullRequests ?? true };
                if (!string.Equals(identity.FullPath, configuration.FullPath, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(identity.FullPath, previous?.Repository.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    LogRepositoryRenamed(_logger, configuration.FullPath, identity.FullPath);
                }
            }

            (owner, name) = (identity!.OwnerPath, identity.RepositoryName);
            var path = GitHubEndpoints.RepositoryPath(owner, name);
            milestones = await FetchListSectionAsync<GitHubMilestoneDto, MilestoneSnapshot>(
                session, cache, $"{path}/milestones?state=all&per_page={PageSize}", "milestones", GitHubMapper.MapMilestone, cancellationToken).ConfigureAwait(false);
            labels = await FetchListSectionAsync<GitHubLabelDto?, LabelSnapshot>(
                session, cache, $"{path}/labels?per_page={PageSize}", "labels", l => GitHubMapper.MapLabel(l), cancellationToken).ConfigureAwait(false);
            needCounts = true;
        }

        if (identity is null)
        {
            throw new ProviderFailureException(ProviderFailureKind.Malformed, "Repository identity could not be resolved.");
        }

        var repositoryPath = GitHubEndpoints.RepositoryPath(owner, name);
        var reconcile = scope.HasFlag(RefreshScope.Reconcile) || (scope.HasFlag(RefreshScope.WorkItems) && watermark is null);
        WorkItemsResult? workItems = null;
        if (!reconcile && scope.HasFlag(RefreshScope.WorkItems))
        {
            workItems = await FetchDeltaAsync(session, cache, repositoryPath, watermark!.Value, cancellationToken).ConfigureAwait(false);
            if (workItems is null)
            {
                LogDeltaFallback(_logger, identity.FullPath, MaxDeltaPages);
                reconcile = true;
            }
        }

        if (reconcile)
        {
            workItems = await ReconcileAsync(session, repositoryPath, context.ClosedWindowStart, watermark, cancellationToken).ConfigureAwait(false);
            needCounts = true;
        }

        if (workItems is not null)
        {
            issues = workItems.Issues;
            changeRequests = workItems.ChangeRequests;
            watermark = workItems.Watermark ?? watermark;
            needCounts |= workItems.IssuesChanged;
        }

        if (scope.HasFlag(RefreshScope.Builds))
        {
            var (update, enabled) = await FetchBuildsAsync(
                session, cache, repositoryPath, identity.FullPath, defaultBranch, scope.HasFlag(RefreshScope.Metadata), features.Builds, cancellationToken).ConfigureAwait(false);
            builds = update;
            features = features with { Builds = enabled };
        }

        if (needCounts)
        {
            counts = await FetchCountsAsync(session, owner, name, cancellationToken).ConfigureAwait(false);
            if (!features.Issues)
            {
                // With issues disabled the REST list is empty (G12) while GraphQL may still count the hidden issues;
                // skip the open-set consistency check instead of rejecting every snapshot.
                counts = counts with { OpenIssues = null };
            }
        }

        var syncState = context.SyncState
            .WithValues((WatermarkKey, watermark?.ToString("o", CultureInfo.InvariantCulture)))
            .WithRequests(cache.Updates);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            LogFetchCompleted(_logger, identity.FullPath, scope, session.RequestCount, Describe(issues), Describe(changeRequests), counts is not null);
        }

        return new ProviderFetchResult.Success(
            new ProviderFetch(identity, defaultBranch, features, issues, changeRequests, milestones, labels, builds, counts),
            syncState);
    }

    private async Task<ProviderConnectionResult> TestConnectionCoreAsync(RepositoryConfiguration configuration, CancellationToken cancellationToken)
    {
        var endpoints = GitHubEndpoints.From(configuration);
        var policy = new GitHubResponsePolicy();
        var session = await CreateSessionAsync(configuration, endpoints, policy, cancellationToken).ConfigureAwait(false);

        HttpResponseData response;
        try
        {
            response = await session.GetAsync(
                endpoints.Rest(GitHubEndpoints.RepositoryPath(configuration.OwnerPath, configuration.RepositoryName)), null, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderFailureException ex) when (ex.Kind == ProviderFailureKind.NotFound)
        {
            return ProviderConnectionResult.Fail(
                ProviderFailureKind.NotFound,
                $"Repository {configuration.FullPath} was not found on {endpoints.Host}, or the token cannot see it. Private repositories look like 404 without access: check the token's resource owner, its repository selection and the Metadata: Read permission.");
        }

        var repository = response.Deserialize<GitHubRepositoryDto>(ProviderJson.SnakeCase);
        var identity = GitHubMapper.MapIdentity(repository, endpoints.Host);
        var path = GitHubEndpoints.RepositoryPath(identity.OwnerPath, identity.RepositoryName);
        var expiresOn = ParseTokenExpiration(response.Headers["github-authentication-token-expiration"]);
        var scopes = response.Headers["X-OAuth-Scopes"];
        var serverVersion = response.Headers["X-GitHub-Enterprise-Version"];

        try
        {
            await session.GetAsync(endpoints.Rest($"{path}/issues?per_page=1"), null, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderFailureException ex) when (ex.Kind == ProviderFailureKind.AuthorizationFailed)
        {
            return ProviderConnectionResult.Fail(
                ProviderFailureKind.AuthorizationFailed,
                $"The token can see {identity.FullPath} but cannot read its issues and pull requests. Grant 'Issues: Read' and 'Pull requests: Read' (fine-grained token) or the 'repo' scope (classic token, private repositories). {ex.Message}");
        }

        var actionsEnabled = true;
        try
        {
            await session.GetAsync(endpoints.Rest($"{path}/actions/runs?per_page=1&exclude_pull_requests=true"), null, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderFailureException ex) when (ex.Kind == ProviderFailureKind.NotFound)
        {
            actionsEnabled = false;
        }
        catch (ProviderFailureException ex) when (ex.Kind == ProviderFailureKind.AuthorizationFailed)
        {
            return ProviderConnectionResult.Fail(
                ProviderFailureKind.AuthorizationFailed,
                $"The token cannot read GitHub Actions runs of {identity.FullPath}. Grant 'Actions: Read'. {ex.Message}");
        }

        var visibility = repository.Visibility ?? (repository.Private == true ? "private" : "public");
        var message = $"Connected to {identity.FullPath} ({visibility}). Default branch: {repository.DefaultBranch ?? "none"}."
            + (repository.Archived == true ? " The repository is archived." : string.Empty)
            + (repository.HasIssues == false ? " Issues are disabled." : string.Empty)
            + (actionsEnabled ? string.Empty : " GitHub Actions is not enabled.")
            + (expiresOn is { } date ? $" The token expires on {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}." : string.Empty);

        return ProviderConnectionResult.Ok(identity, message) with
        {
            CredentialExpiresOn = expiresOn,
            CredentialDescription = scopes is null ? null : string.IsNullOrWhiteSpace(scopes) ? "classic token without scopes" : $"token scopes: {scopes.Trim()}",
            ServerVersion = string.IsNullOrWhiteSpace(serverVersion) ? null : serverVersion.Trim(),
        };
    }

    private async Task<GitHubApiSession> CreateSessionAsync(RepositoryConfiguration configuration, GitHubEndpoints endpoints, GitHubResponsePolicy policy, CancellationToken cancellationToken)
    {
        // Loaded just before the requests (architecture §18.2); only revealed while building request headers.
        var token = await ProviderBoundary.LoadTokenAsync(_credentials, configuration, cancellationToken).ConfigureAwait(false);
        var http = new ProviderHttpClient(_httpClientFactory.CreateClient(HttpClientName), policy, _httpOptions, _time, _logger);
        return new GitHubApiSession(http, policy, endpoints, token, ProviderBoundary.UserAgent(configuration), _time);
    }

    private static async Task<GitHubRepositoryDto?> FetchRepositoryAsync(
        GitHubApiSession session, GitHubConditionalCache cache, string owner, string name, CancellationToken cancellationToken)
    {
        var relative = GitHubEndpoints.RepositoryPath(owner, name);
        var key = GitHubConditionalCache.Key(relative);
        var response = await session.GetAsync(session.Endpoints.Rest(relative), cache.ValidatorFor(key, singlePageOnly: false), cancellationToken).ConfigureAwait(false);
        if (response.IsNotModified)
        {
            cache.RecordNotModified(key);
            return null;
        }

        var repository = response.Deserialize<GitHubRepositoryDto>(ProviderJson.SnakeCase);
        cache.Record(key, response.Headers.ETag, 1);
        return repository;
    }

    private static async Task<SectionUpdate<TItem>> FetchListSectionAsync<TDto, TItem>(
        GitHubApiSession session,
        GitHubConditionalCache cache,
        string relative,
        string resource,
        Func<TDto, TItem?> map,
        CancellationToken cancellationToken)
        where TItem : class
    {
        var key = GitHubConditionalCache.Key(relative);
        var result = await session.GetPagesAsync<TDto>(
            session.Endpoints.Rest(relative), cache.ValidatorFor(key, singlePageOnly: true), resource, null, GitHubApiSession.MaxPages, cancellationToken).ConfigureAwait(false);
        if (result.NotModified)
        {
            cache.RecordNotModified(key);
            return SectionUpdate<TItem>.Unchanged;
        }

        EnsureComplete(result, resource);
        cache.Record(key, result.ETag, result.Pages);
        return SectionUpdate<TItem>.Replace(result.Items.Select(map).OfType<TItem>().ToList());
    }

    private static async Task<WorkItemsResult?> FetchDeltaAsync(
        GitHubApiSession session, GitHubConditionalCache cache, string repositoryPath, DateTimeOffset watermark, CancellationToken cancellationToken)
    {
        // Fixed URL without `since`, so its ETag stays valid and a quiet repository costs a free 304 (G7, G8).
        var relative = $"{repositoryPath}/issues?state=all&sort=updated&direction=desc&per_page={PageSize}";
        var key = GitHubConditionalCache.Key(relative);
        var bound = watermark - DeltaOverlap;
        var result = await session.GetPagesAsync<GitHubIssueDto>(
            session.Endpoints.Rest(relative),
            cache.ValidatorFor(key, singlePageOnly: false),
            "changed issues",
            page => page.Count > 0 && page.Min(i => i.UpdatedAt ?? DateTimeOffset.MinValue) >= bound,
            MaxDeltaPages,
            cancellationToken).ConfigureAwait(false);

        if (result.NotModified)
        {
            cache.RecordNotModified(key);
            return new WorkItemsResult(WorkItemUpdate<IssueSnapshot>.Unchanged, WorkItemUpdate<ChangeRequestSnapshot>.Unchanged, null, IssuesChanged: false);
        }

        if (result.Truncated)
        {
            return null;
        }

        cache.Record(key, result.ETag, result.Pages);
        var changed = Deduplicate(result.Items.Where(i => i.UpdatedAt >= bound));
        var changedIssues = changed.Where(i => !GitHubMapper.IsPullRequest(i)).Select(GitHubMapper.MapIssue).ToList();
        var changedRequests = changed.Where(GitHubMapper.IsPullRequest).Select(GitHubMapper.MapChangeRequest).ToList();
        var newest = MaxUpdated(result.Items);
        return new WorkItemsResult(
            changedIssues.Count == 0 ? WorkItemUpdate<IssueSnapshot>.Unchanged : WorkItemUpdate<IssueSnapshot>.Delta(changedIssues),
            changedRequests.Count == 0 ? WorkItemUpdate<ChangeRequestSnapshot>.Unchanged : WorkItemUpdate<ChangeRequestSnapshot>.Delta(changedRequests),
            newest is { } n && n > watermark ? n : watermark,
            IssuesChanged: changedIssues.Count > 0);
    }

    private async Task<WorkItemsResult> ReconcileAsync(
        GitHubApiSession session, string repositoryPath, DateTimeOffset closedWindowStart, DateTimeOffset? watermark, CancellationToken cancellationToken)
    {
        // Ascending by creation so items created while paging append at the end (github-api.md §8.2).
        var open = await session.GetPagesAsync<GitHubIssueDto>(
            session.Endpoints.Rest($"{repositoryPath}/issues?state=open&sort=created&direction=asc&per_page={PageSize}"),
            null, "open issues", null, GitHubApiSession.MaxPages, cancellationToken).ConfigureAwait(false);
        EnsureComplete(open, "open issues");

        var since = Uri.EscapeDataString(closedWindowStart.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        var closed = await session.GetPagesAsync<GitHubIssueDto>(
            session.Endpoints.Rest($"{repositoryPath}/issues?state=closed&since={since}&sort=updated&direction=desc&per_page={PageSize}"),
            null, "recently closed issues", null, GitHubApiSession.MaxPages, cancellationToken).ConfigureAwait(false);
        EnsureComplete(closed, "recently closed issues");

        // Items may move between the lists while paging; keep the newest copy and partition by its state.
        var all = Deduplicate(open.Items.Concat(closed.Items));
        bool IsOpen(GitHubIssueDto i) => !string.Equals(i.State, "closed", StringComparison.OrdinalIgnoreCase);

        var openIssues = all.Where(i => IsOpen(i) && !GitHubMapper.IsPullRequest(i)).Select(GitHubMapper.MapIssue).ToList();
        var closedIssues = all.Where(i => !IsOpen(i) && !GitHubMapper.IsPullRequest(i)).Select(GitHubMapper.MapIssue).ToList();
        var openRequests = all.Where(i => IsOpen(i) && GitHubMapper.IsPullRequest(i)).Select(GitHubMapper.MapChangeRequest).ToList();
        var closedRequests = all.Where(i => !IsOpen(i) && GitHubMapper.IsPullRequest(i)).Select(GitHubMapper.MapChangeRequest).ToList();

        var newest = MaxUpdated(all);
        DateTimeOffset? newWatermark = (newest, watermark) switch
        {
            ({ } n, { } w) => n > w ? n : w,
            ({ } n, null) => n,
            (null, { } w) => w,
            _ => open.ServerDate ?? _time.GetUtcNow(),
        };

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            LogReconciled(_logger, repositoryPath, open.Pages + closed.Pages, openIssues.Count, openRequests.Count, closedIssues.Count + closedRequests.Count);
        }

        return new WorkItemsResult(
            WorkItemUpdate<IssueSnapshot>.Full(openIssues, closedIssues, closedWindowStart),
            WorkItemUpdate<ChangeRequestSnapshot>.Full(openRequests, closedRequests, closedWindowStart),
            newWatermark,
            IssuesChanged: true);
    }

    private async Task<(SectionUpdate<BuildSnapshot> Update, bool Enabled)> FetchBuildsAsync(
        GitHubApiSession session,
        GitHubConditionalCache cache,
        string repositoryPath,
        string repositoryFullPath,
        string? defaultBranch,
        bool repositoryJustVerified,
        bool previouslyEnabled,
        CancellationToken cancellationToken)
    {
        if (defaultBranch is null)
        {
            // Empty repository: no default branch, hence no default-branch runs.
            return (SectionUpdate<BuildSnapshot>.Replace([]), previouslyEnabled);
        }

        var relative = $"{repositoryPath}/actions/runs?branch={Uri.EscapeDataString(defaultBranch)}&per_page={BuildPageSize}&exclude_pull_requests=true";
        var key = GitHubConditionalCache.Key(relative);
        HttpResponseData response;
        try
        {
            response = await session.GetAsync(session.Endpoints.Rest(relative), cache.ValidatorFor(key, singlePageOnly: false), cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderFailureException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Actions disabled answers 404 although the repository exists. Confirm the repository (a cheap,
            // usually free conditional request) so a deleted repository still fails as NotFound.
            if (!repositoryJustVerified)
            {
                var repoKey = GitHubConditionalCache.Key(repositoryPath);
                await session.GetAsync(session.Endpoints.Rest(repositoryPath), cache.ValidatorFor(repoKey, singlePageOnly: false), cancellationToken).ConfigureAwait(false);
            }

            cache.Record(key, null, 1);
            return (SectionUpdate<BuildSnapshot>.Replace([]), false);
        }

        if (response.IsNotModified)
        {
            cache.RecordNotModified(key);
            return (SectionUpdate<BuildSnapshot>.Unchanged, true);
        }

        var dto = response.Deserialize<GitHubWorkflowRunsDto>(ProviderJson.SnakeCase);
        var runs = new List<BuildSnapshot>();
        foreach (var run in dto.WorkflowRuns ?? [])
        {
            if (run is null || IsForeignForkRun(run, repositoryFullPath))
            {
                continue;
            }

            var build = GitHubMapper.MapBuild(run);
            if (build.State == BuildState.Unknown)
            {
                ReportUnknownBuildState(run.Status, run.Conclusion);
            }

            runs.Add(build);
        }

        cache.Record(key, response.Headers.ETag, 1);
        return (SectionUpdate<BuildSnapshot>.Replace(runs), true);
    }

    private static async Task<CountsUpdate> FetchCountsAsync(GitHubApiSession session, string owner, string name, CancellationToken cancellationToken)
    {
        int? open = null;
        int? closed = null;
        var byMilestone = new Dictionary<string, int>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 1; ; page++)
        {
            var variables = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["owner"] = owner,
                ["name"] = name,
                ["milestoneCursor"] = cursor,
            };
            var data = await session.QueryAsync<GitHubCountsData>(CountsQuery, variables, cancellationToken).ConfigureAwait(false);
            var repository = data.Repository
                ?? throw new ProviderFailureException(ProviderFailureKind.NotFound, "The repository is not visible to GitHub GraphQL with this token.");
            open ??= repository.OpenIssues?.TotalCount;
            closed ??= repository.ClosedIssues?.TotalCount;

            // Milestone.id in GraphQL equals node_id in REST (verified), so it matches MilestoneSnapshot.ExternalId.
            foreach (var node in repository.Milestones?.Nodes ?? [])
            {
                if (node?.Id is { Length: > 0 } id && node.ClosedIssues?.TotalCount is { } count)
                {
                    byMilestone[id] = count;
                }
            }

            var pageInfo = repository.Milestones?.PageInfo;
            if (pageInfo is not { HasNextPage: true })
            {
                break;
            }

            if (page >= MaxMilestoneQueryPages || string.IsNullOrEmpty(pageInfo.EndCursor))
            {
                throw new ProviderFailureException(ProviderFailureKind.Partial, "Milestone counts could not be read completely.");
            }

            cursor = pageInfo.EndCursor;
        }

        return new CountsUpdate(
            open,
            closed ?? throw new ProviderFailureException(ProviderFailureKind.Malformed, "GitHub GraphQL returned no closed issue count."),
            byMilestone);
    }

    private static (string Owner, string Name) ResolvePath(RepositoryConfiguration configuration, RepositorySnapshot? previous)
    {
        // After a rename the previous snapshot carries the canonical name; using it avoids a redirect per request.
        if (previous?.Repository is { Provider: RepositoryProviderKind.GitHub } identity
            && (string.Equals(identity.FullPath, configuration.FullPath, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(configuration.ExternalId) && string.Equals(configuration.ExternalId, identity.ExternalId, StringComparison.Ordinal))))
        {
            return (identity.OwnerPath, identity.RepositoryName);
        }

        return (configuration.OwnerPath, configuration.RepositoryName);
    }

    private static bool IsForeignForkRun(GitHubWorkflowRunDto run, string repositoryFullPath) =>
        run.Event is { } evt
        && evt.StartsWith("pull_request", StringComparison.OrdinalIgnoreCase)
        && run.HeadRepository?.FullName is { Length: > 0 } head
        && !string.Equals(head, repositoryFullPath, StringComparison.OrdinalIgnoreCase);

    private static List<GitHubIssueDto> Deduplicate(IEnumerable<GitHubIssueDto> items)
    {
        var byId = new Dictionary<string, GitHubIssueDto>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.NodeId))
            {
                throw new ProviderResponseException("GitHub issue item without node_id.");
            }

            if (!byId.TryGetValue(item.NodeId, out var existing))
            {
                order.Add(item.NodeId);
                byId[item.NodeId] = item;
            }
            else if ((item.UpdatedAt ?? DateTimeOffset.MinValue) >= (existing.UpdatedAt ?? DateTimeOffset.MinValue))
            {
                byId[item.NodeId] = item;
            }
        }

        return order.Select(id => byId[id]).ToList();
    }

    private static DateTimeOffset? MaxUpdated(IEnumerable<GitHubIssueDto> items)
    {
        DateTimeOffset? max = null;
        foreach (var item in items)
        {
            if (item.UpdatedAt is { } updated && (max is null || updated > max))
            {
                max = updated.ToUniversalTime();
            }
        }

        return max;
    }

    private static void EnsureComplete<T>(PagedResult<T> result, string resource)
    {
        if (result.Truncated)
        {
            throw new ProviderFailureException(
                ProviderFailureKind.Partial,
                $"GitHub returned more than {GitHubApiSession.MaxPages} pages of {resource}; the snapshot would be incomplete.");
        }
    }

    private static DateTimeOffset? ParseWatermark(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed.ToUniversalTime() : null;

    /// <summary>Parses <c>github-authentication-token-expiration</c> (e.g. <c>2026-12-31 08:00:00 UTC</c> or <c>… -0700</c>).</summary>
    internal static DateOnly? ParseTokenExpiration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4] + " +00:00";
        }

        string[] formats = ["yyyy-MM-dd HH:mm:ss zzz", "yyyy-MM-dd HH:mm:ss zz", "yyyy-MM-dd HH:mm:ss K"];
        if (DateTimeOffset.TryParseExact(normalized, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            || TryParseCompactOffset(normalized, out parsed)
            || DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
        {
            // The calendar date as GitHub states it (in the header's own offset), which is what the user sees.
            return DateOnly.FromDateTime(parsed.DateTime);
        }

        return null;
    }

    private static bool TryParseCompactOffset(string value, out DateTimeOffset parsed)
    {
        // "2026-12-31 00:00:00 -0700": insert the colon .NET expects in the offset.
        parsed = default;
        var space = value.LastIndexOf(' ');
        if (space < 0 || value.Length - space - 1 != 5 || value[space + 1] is not ('+' or '-'))
        {
            return false;
        }

        var withColon = $"{value[..(space + 4)]}:{value[(space + 4)..]}";
        return DateTimeOffset.TryParseExact(withColon, "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
    }

    private void ReportUnknownBuildState(string? status, string? conclusion)
    {
        var key = $"{status}/{conclusion}";
        if (ReportedUnknownBuildStates.TryAdd(key, 0))
        {
            LogUnknownBuildState(_logger, status ?? "null", conclusion ?? "null");
        }
    }

    private static string Describe<T>(WorkItemUpdate<T> update) => update switch
    {
        WorkItemUpdate<T>.FullItems full => $"full {full.Open.Count} open/{full.RecentlyClosed.Count} closed",
        WorkItemUpdate<T>.DeltaItems delta => $"delta {delta.Changed.Count}",
        _ => "unchanged",
    };

    [LoggerMessage(EventId = 2100, Level = LogLevel.Debug, Message = "GitHub fetch {Repository} ({Scope}): {Requests} requests, issues {Issues}, pull requests {PullRequests}, counts refreshed {Counts}")]
    private static partial void LogFetchCompleted(ILogger logger, string repository, RefreshScope scope, int requests, string issues, string pullRequests, bool counts);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "GitHub repository {Configured} is now {Current} (renamed or transferred)")]
    private static partial void LogRepositoryRenamed(ILogger logger, string configured, string current);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Information, Message = "GitHub delta for {Repository} exceeded {Pages} pages; running a full reconciliation instead")]
    private static partial void LogDeltaFallback(ILogger logger, string repository, int pages);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Warning, Message = "Unknown GitHub workflow run state {Status}/{Conclusion}; mapped to Unknown")]
    private static partial void LogUnknownBuildState(ILogger logger, string status, string conclusion);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Debug, Message = "GitHub reconciliation {Repository}: {Pages} pages, {OpenIssues} open issues, {OpenPullRequests} open pull requests, {Closed} recently closed")]
    private static partial void LogReconciled(ILogger logger, string repository, int pages, int openIssues, int openPullRequests, int closed);

    private sealed record WorkItemsResult(
        WorkItemUpdate<IssueSnapshot> Issues,
        WorkItemUpdate<ChangeRequestSnapshot> ChangeRequests,
        DateTimeOffset? Watermark,
        bool IssuesChanged);
}
