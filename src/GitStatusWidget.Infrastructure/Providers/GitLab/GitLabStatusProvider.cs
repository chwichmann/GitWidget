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

namespace GitStatusWidget.Infrastructure.Providers.GitLab;

/// <summary>
/// Read-only GitLab adapter (REST v4 for items, GraphQL for exact counts) for GitLab.com and self-managed
/// instances, including relative URL roots and access gateways (architecture §9, gitlab-api.md §16).
/// </summary>
internal sealed partial class GitLabStatusProvider : IRepositoryStatusProvider
{
    /// <summary>Name of the configured <see cref="HttpClient"/>.</summary>
    public const string HttpClientName = "GitLab";

    /// <summary>Overlap subtracted from watermarks to absorb clock skew and commit lag (gitlab-api.md §16.1).</summary>
    public static readonly TimeSpan WatermarkOverlap = TimeSpan.FromSeconds(120);

    /// <summary>Number of recent pipelines requested.</summary>
    public const int PipelinePageSize = 20;

    internal const string ProjectIdKey = "gitlab.projectId";
    internal const string ResolvedForKey = "gitlab.resolvedFor";
    internal const string IssuesWatermarkKey = "gitlab.issues.watermark";
    internal const string MergeRequestsWatermarkKey = "gitlab.mergeRequests.watermark";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICredentialStore _credentials;
    private readonly TimeProvider _time;
    private readonly ILogger<GitLabStatusProvider> _logger;
    private readonly ProviderHttpOptions _httpOptions;

    /// <summary>Creates the provider.</summary>
    /// <param name="httpClientFactory">Factory for the <c>"GitLab"</c> client.</param>
    /// <param name="credentials">Credential store holding the token.</param>
    /// <param name="time">Time source.</param>
    /// <param name="logger">Logger (never receives tokens).</param>
    /// <param name="httpOptions">HTTP options (timeouts, retries); defaults when omitted.</param>
    public GitLabStatusProvider(
        IHttpClientFactory httpClientFactory,
        ICredentialStore credentials,
        TimeProvider time,
        ILogger<GitLabStatusProvider> logger,
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
    public RepositoryProviderKind Kind => RepositoryProviderKind.GitLab;

    /// <inheritdoc />
    public Task<ProviderFetchResult> FetchAsync(RepositoryConfiguration configuration, SnapshotRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);
        ProviderHttpClient? client = null;
        return ProviderBoundary.RunAsync(
            async () =>
            {
                var serverUri = ProviderBoundary.ValidateServerUri(configuration);
                var token = await ProviderBoundary.LoadTokenAsync(_credentials, configuration, cancellationToken).ConfigureAwait(false);
                client = CreateClient();
                var api = new GitLabApi(client, serverUri, token, ProviderBoundary.UserAgent(configuration));
                return await FetchCoreAsync(api, configuration, context, cancellationToken).ConfigureAwait(false);
            },
            () => client?.LastRateLimit,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ProviderConnectionResult> TestConnectionAsync(RepositoryConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return ProviderBoundary.RunConnectionAsync(
            async () =>
            {
                var serverUri = ProviderBoundary.ValidateServerUri(configuration);
                var token = await ProviderBoundary.LoadTokenAsync(_credentials, configuration, cancellationToken).ConfigureAwait(false);
                var api = new GitLabApi(CreateClient(), serverUri, token, ProviderBoundary.UserAgent(configuration));
                return await TestConnectionCoreAsync(api, configuration, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    private ProviderHttpClient CreateClient() =>
        new(_httpClientFactory.CreateClient(HttpClientName), GitLabResponsePolicy.Instance, _httpOptions, _time, _logger);

    private async Task<ProviderFetchResult.Success> FetchCoreAsync(
        GitLabApi api,
        RepositoryConfiguration configuration,
        SnapshotRequestContext context,
        CancellationToken cancellationToken)
    {
        var previous = context.Previous;
        var scope = previous is null ? RefreshScope.All : context.Scope;
        var syncState = context.SyncState ?? ProviderSyncState.Empty;
        var refreshMetadata = scope.HasFlag(RefreshScope.Metadata);
        var reconcile = scope.HasFlag(RefreshScope.Reconcile);
        var resolvedFor = ResolvedFor(api.ServerUri, configuration);

        // 1. Project: by stored numeric id when known, otherwise resolve the configured path (handles 301).
        var projectId = KnownProjectId(syncState, resolvedFor);
        GitLabProjectDto? project = null;
        if (refreshMetadata || projectId is null || previous is null)
        {
            project = await ResolveProjectAsync(api, projectId ?? GitLabApi.EncodeProjectPath(configuration.FullPath), cancellationToken).ConfigureAwait(false);
            projectId = GitLabMapper.Id(project.Id);
        }

        var identity = project is not null ? GitLabMapper.MapIdentity(project, api.ServerUri) : previous!.Repository;
        var defaultBranch = project is not null ? project.DefaultBranch : previous!.DefaultBranch;
        var features = project is not null
            ? GitLabMapper.MapFeatures(project) with { Commits = previous?.Features.Commits ?? true }
            : previous!.Features;
        var projectPath = $"projects/{projectId}";

        // 2. Metadata: labels and milestones (all states, inherited ones included).
        var labels = SectionUpdate<LabelSnapshot>.Unchanged;
        var milestones = SectionUpdate<MilestoneSnapshot>.Unchanged;
        IReadOnlyList<MilestoneSnapshot> knownMilestones = previous?.Milestones ?? [];
        if (refreshMetadata)
        {
            var labelDtos = await api.GetAllPagesAsync<GitLabLabelDto>(
                $"{projectPath}/labels",
                GitLabApi.Query(("include_ancestor_groups", "true"), ("per_page", PageSizeText)),
                cancellationToken).ConfigureAwait(false);
            labels = SectionUpdate<LabelSnapshot>.Replace(labelDtos.Select(GitLabMapper.MapLabel).ToList());

            var milestoneDtos = await api.GetAllPagesAsync<GitLabMilestoneDto>(
                $"{projectPath}/milestones",
                GitLabApi.Query(("include_ancestors", "true"), ("per_page", PageSizeText)),
                cancellationToken).ConfigureAwait(false);
            knownMilestones = milestoneDtos
                .GroupBy(m => m.Id)
                .Select(g => GitLabMapper.MapMilestone(g.First()))
                .ToList();
            milestones = SectionUpdate<MilestoneSnapshot>.Replace(knownMilestones);
        }

        // 3. Work items.
        var issuesWatermark = ReadWatermark(syncState, IssuesWatermarkKey);
        var mergeRequestsWatermark = ReadWatermark(syncState, MergeRequestsWatermarkKey);
        var issues = WorkItemUpdate<IssueSnapshot>.Unchanged;
        var changeRequests = WorkItemUpdate<ChangeRequestSnapshot>.Unchanged;
        var issueDeltaHadItems = false;
        var workItems = scope.HasFlag(RefreshScope.WorkItems);

        if (features.Issues)
        {
            if (reconcile || (workItems && issuesWatermark is null))
            {
                (issues, issuesWatermark) = await FetchFullIssuesAsync(api, projectPath, context.ClosedWindowStart, issuesWatermark, cancellationToken).ConfigureAwait(false);
            }
            else if (workItems)
            {
                var (delta, watermark) = await FetchIssueDeltaAsync(api, projectPath, issuesWatermark!.Value, cancellationToken).ConfigureAwait(false);
                issues = WorkItemUpdate<IssueSnapshot>.Delta(delta);
                issuesWatermark = watermark;
                issueDeltaHadItems = delta.Count > 0;
            }
        }
        else if (reconcile || workItems)
        {
            issues = WorkItemUpdate<IssueSnapshot>.Full([], [], context.ClosedWindowStart);
        }

        if (features.ChangeRequests)
        {
            if (reconcile || (workItems && mergeRequestsWatermark is null))
            {
                (changeRequests, mergeRequestsWatermark) = await FetchFullMergeRequestsAsync(api, projectPath, context.ClosedWindowStart, mergeRequestsWatermark, cancellationToken).ConfigureAwait(false);
            }
            else if (workItems)
            {
                var (delta, watermark) = await FetchMergeRequestDeltaAsync(api, projectPath, mergeRequestsWatermark!.Value, cancellationToken).ConfigureAwait(false);
                changeRequests = WorkItemUpdate<ChangeRequestSnapshot>.Delta(delta);
                mergeRequestsWatermark = watermark;
            }
        }
        else if (reconcile || workItems)
        {
            changeRequests = WorkItemUpdate<ChangeRequestSnapshot>.Full([], [], context.ClosedWindowStart);
        }

        // 4. Builds.
        var builds = SectionUpdate<BuildSnapshot>.Unchanged;
        var requestStates = new List<ConditionalRequestState>();
        if (scope.HasFlag(RefreshScope.Builds))
        {
            (builds, var buildsReadable, var requestState) = await FetchBuildsAsync(
                api, projectPath, features.Builds ? defaultBranch : null, syncState, previous is not null, cancellationToken).ConfigureAwait(false);
            if (!buildsReadable)
            {
                features = features with { Builds = false };
            }

            if (requestState is not null)
            {
                requestStates.Add(requestState);
            }
        }

        // 4b. Commits of the default branch (issue #14), with every work-item poll.
        SectionUpdate<DateTimeOffset>? commits = null;
        if (reconcile || workItems)
        {
            (commits, var commitsReadable) = await FetchCommitsAsync(api, projectPath, defaultBranch, cancellationToken).ConfigureAwait(false);
            features = features with { Commits = commitsReadable };
        }

        // 5. Counts: with metadata/reconciliation, and when the issue delta changed something.
        CountsUpdate? counts = null;
        if (previous is null || refreshMetadata || reconcile || issueDeltaHadItems)
        {
            if (!features.Issues)
            {
                counts = new CountsUpdate(0, 0, new Dictionary<string, int>(StringComparer.Ordinal));
            }
            else
            {
                // Closed milestones change rarely: count them only with metadata/reconciliation.
                var countedMilestones = previous is null || refreshMetadata || reconcile
                    ? knownMilestones
                    : knownMilestones.Where(m => !m.IsClosed).ToList();
                counts = await FetchCountsAsync(api, identity, project is not null, projectId, countedMilestones, cancellationToken).ConfigureAwait(false);
            }
        }

        var fetch = new ProviderFetch(identity, defaultBranch, features, issues, changeRequests, milestones, labels, builds, counts, commits);
        var newState = syncState
            .WithValues(
                (ProjectIdKey, projectId),
                (ResolvedForKey, resolvedFor),
                (IssuesWatermarkKey, issuesWatermark?.ToString("O", CultureInfo.InvariantCulture)),
                (MergeRequestsWatermarkKey, mergeRequestsWatermark?.ToString("O", CultureInfo.InvariantCulture)))
            .WithRequests(requestStates);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            LogFetchCompleted(_logger, scope, projectId, api.Client.RequestCount, api.PageCount, issues.GetType().Name, changeRequests.GetType().Name);
        }

        return new ProviderFetchResult.Success(fetch, newState);
    }

    private async Task<GitLabProjectDto> ResolveProjectAsync(GitLabApi api, string idOrEncodedPath, CancellationToken cancellationToken)
    {
        var (project, moved) = await api.GetProjectAsync(idOrEncodedPath, cancellationToken).ConfigureAwait(false);
        if (moved && _logger.IsEnabled(LogLevel.Information))
        {
            LogProjectMoved(_logger, project.Id);
        }

        return project;
    }

    private static async Task<(WorkItemUpdate<IssueSnapshot> Update, DateTimeOffset? Watermark)> FetchFullIssuesAsync(
        GitLabApi api, string projectPath, DateTimeOffset closedSince, DateTimeOffset? previousWatermark, CancellationToken cancellationToken)
    {
        // Keyset pagination (GitLab 18.3+, verified on 19.5.1-ee) is stable under concurrent updates; older servers
        // ignore it and fall back to offset pages, which the paginator also follows.
        var open = await api.GetAllPagesAsync<GitLabIssueDto>(
            $"{projectPath}/issues",
            GitLabApi.Query(
                ("issue_type", GitLabMapper.IssueType),
                ("state", "opened"),
                ("with_labels_details", "true"),
                ("pagination", "keyset"),
                ("order_by", "created_at"),
                ("sort", "asc"),
                ("per_page", PageSizeText)),
            cancellationToken).ConfigureAwait(false);

        var closed = await api.GetAllPagesAsync<GitLabIssueDto>(
            $"{projectPath}/issues",
            GitLabApi.Query(
                ("issue_type", GitLabMapper.IssueType),
                ("state", "closed"),
                ("updated_after", GitLabMapper.FormatTimestamp(closedSince)),
                ("with_labels_details", "true"),
                ("pagination", "keyset"),
                ("order_by", "updated_at"),
                ("sort", "asc"),
                ("per_page", PageSizeText)),
            cancellationToken).ConfigureAwait(false);

        var watermark = MaxWatermark(previousWatermark, open.Concat(closed).Select(i => i.UpdatedAt)) ?? closedSince;
        var items = Latest(open.Concat(closed).Where(GitLabMapper.IsIssue).Select(GitLabMapper.MapIssue), i => i.ExternalId, i => i.UpdatedAt);
        return (WorkItemUpdate<IssueSnapshot>.Full(
            items.Where(i => i.State == WorkItemState.Open).ToList(),
            items.Where(i => i.State == WorkItemState.Closed).ToList(),
            closedSince), watermark);
    }

    private static async Task<(IReadOnlyList<IssueSnapshot> Changed, DateTimeOffset Watermark)> FetchIssueDeltaAsync(
        GitLabApi api, string projectPath, DateTimeOffset watermark, CancellationToken cancellationToken)
    {
        var changed = await api.GetAllPagesAsync<GitLabIssueDto>(
            $"{projectPath}/issues",
            GitLabApi.Query(
                ("issue_type", GitLabMapper.IssueType),
                ("state", "all"),
                ("updated_after", GitLabMapper.FormatTimestamp(watermark - WatermarkOverlap)),
                ("with_labels_details", "true"),
                ("pagination", "keyset"),
                ("order_by", "updated_at"),
                ("sort", "asc"),
                ("per_page", PageSizeText)),
            cancellationToken).ConfigureAwait(false);

        var items = Latest(changed.Where(GitLabMapper.IsIssue).Select(GitLabMapper.MapIssue), i => i.ExternalId, i => i.UpdatedAt);
        return (items, MaxWatermark(watermark, changed.Select(i => i.UpdatedAt)) ?? watermark);
    }

    private static async Task<(WorkItemUpdate<ChangeRequestSnapshot> Update, DateTimeOffset? Watermark)> FetchFullMergeRequestsAsync(
        GitLabApi api, string projectPath, DateTimeOffset closedSince, DateTimeOffset? previousWatermark, CancellationToken cancellationToken)
    {
        // Merge requests support offset pagination only (keyset is silently ignored).
        var open = await api.GetAllPagesAsync<GitLabMergeRequestDto>(
            $"{projectPath}/merge_requests",
            GitLabApi.Query(("state", "opened"), ("order_by", "created_at"), ("sort", "asc"), ("per_page", PageSizeText)),
            cancellationToken).ConfigureAwait(false);

        // One request for closed and merged: every state updated inside the window. Open ones found here are newer
        // copies of (or additions to) the open set and are merged into it.
        var recent = await api.GetAllPagesAsync<GitLabMergeRequestDto>(
            $"{projectPath}/merge_requests",
            GitLabApi.Query(
                ("state", "all"),
                ("updated_after", GitLabMapper.FormatTimestamp(closedSince)),
                ("order_by", "updated_at"),
                ("sort", "asc"),
                ("per_page", PageSizeText)),
            cancellationToken).ConfigureAwait(false);

        var watermark = MaxWatermark(previousWatermark, open.Concat(recent).Select(m => m.UpdatedAt)) ?? closedSince;
        var items = Latest(open.Concat(recent).Select(GitLabMapper.MapMergeRequest), m => m.ExternalId, m => m.UpdatedAt);
        return (WorkItemUpdate<ChangeRequestSnapshot>.Full(
            items.Where(m => m.State == ChangeRequestState.Open).ToList(),
            items.Where(m => m.State != ChangeRequestState.Open).ToList(),
            closedSince), watermark);
    }

    private static async Task<(IReadOnlyList<ChangeRequestSnapshot> Changed, DateTimeOffset Watermark)> FetchMergeRequestDeltaAsync(
        GitLabApi api, string projectPath, DateTimeOffset watermark, CancellationToken cancellationToken)
    {
        var changed = await api.GetAllPagesAsync<GitLabMergeRequestDto>(
            $"{projectPath}/merge_requests",
            GitLabApi.Query(
                ("state", "all"),
                ("updated_after", GitLabMapper.FormatTimestamp(watermark - WatermarkOverlap)),
                ("order_by", "updated_at"),
                ("sort", "asc"),
                ("per_page", PageSizeText)),
            cancellationToken).ConfigureAwait(false);

        var items = Latest(changed.Select(GitLabMapper.MapMergeRequest), m => m.ExternalId, m => m.UpdatedAt);
        return (items, MaxWatermark(watermark, changed.Select(m => m.UpdatedAt)) ?? watermark);
    }

    private async Task<(SectionUpdate<BuildSnapshot> Update, bool Readable, ConditionalRequestState? RequestState)> FetchBuildsAsync(
        GitLabApi api,
        string projectPath,
        string? defaultBranch,
        ProviderSyncState syncState,
        bool hasPrevious,
        CancellationToken cancellationToken)
    {
        // CI disabled, or an empty repository (no default branch): nothing to poll.
        if (string.IsNullOrEmpty(defaultBranch))
        {
            return (SectionUpdate<BuildSnapshot>.Replace([]), true, null);
        }

        var uri = api.Rest(
            $"{projectPath}/pipelines",
            GitLabApi.Query(("ref", defaultBranch), ("per_page", PipelinePageSize.ToString(CultureInfo.InvariantCulture)), ("order_by", "id"), ("sort", "desc")));
        var resourceKey = UrlRedaction.Redact(uri);
        var etag = hasPrevious ? syncState.GetRequest(resourceKey)?.ETag : null;

        HttpResponseData response;
        try
        {
            response = await api.SendAsync(uri, etag, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderFailureException ex) when (ex.StatusCode == HttpStatusCode.Forbidden
            && ex.Kind == ProviderFailureKind.AuthorizationFailed
            && ex is not GitLabApiException { IsInsufficientScope: true })
        {
            // CI/CD disabled for the project, or the role cannot read jobs: hide builds instead of failing.
            if (_logger.IsEnabled(LogLevel.Information))
            {
                LogBuildsUnavailable(_logger);
            }

            return (SectionUpdate<BuildSnapshot>.Replace([]), false, null);
        }

        if (response.IsNotModified)
        {
            return (SectionUpdate<BuildSnapshot>.Unchanged, true, new ConditionalRequestState(resourceKey, etag, null, _time.GetUtcNow()));
        }

        var pipelines = response.Deserialize<List<GitLabPipelineDto>>(ProviderJson.SnakeCase);
        var builds = pipelines.Select(GitLabMapper.MapPipeline).ToList();
        var state = new ConditionalRequestState(resourceKey, response.Headers.ETag, null, _time.GetUtcNow());
        return (SectionUpdate<BuildSnapshot>.Replace(builds), true, state);
    }

    /// <summary>
    /// Reads the commit times of the default branch within <see cref="ProviderFetch.CommitWindow"/> (issue #14).
    /// A disabled or unreadable repository (403, e.g. a Guest token on a private project) or a missing branch (404)
    /// hides the counter instead of failing the fetch.
    /// </summary>
    private async Task<(SectionUpdate<DateTimeOffset>? Update, bool Readable)> FetchCommitsAsync(
        GitLabApi api, string projectPath, string? defaultBranch, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(defaultBranch))
        {
            // Empty repository: no commits.
            return (SectionUpdate<DateTimeOffset>.Replace([]), true);
        }

        var since = _time.GetUtcNow() - ProviderFetch.CommitWindow;
        List<GitLabCommitDto> commits;
        try
        {
            commits = await api.GetAllPagesAsync<GitLabCommitDto>(
                $"{projectPath}/repository/commits",
                GitLabApi.Query(
                    ("ref_name", defaultBranch),
                    ("since", GitLabMapper.FormatTimestamp(since)),
                    ("per_page", GitLabApi.PageSize.ToString(CultureInfo.InvariantCulture))),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderFailureException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            && ex is not GitLabApiException { IsInsufficientScope: true })
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                LogCommitsUnavailable(_logger, (int?)ex.StatusCode);
            }

            return (null, false);
        }

        var times = commits
            .Select(c => c.CommittedDate ?? c.CreatedAt)
            .OfType<DateTimeOffset>()
            .Select(d => d.ToUniversalTime())
            .ToList();
        return (SectionUpdate<DateTimeOffset>.Replace(times), true);
    }

    private async Task<CountsUpdate> FetchCountsAsync(
        GitLabApi api,
        RepositoryIdentity identity,
        bool projectFresh,
        string projectId,
        IReadOnlyList<MilestoneSnapshot> milestones,
        CancellationToken cancellationToken)
    {
        try
        {
            return await FetchCountsOnceAsync(api, identity.FullPath, projectId, milestones, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderFailureException ex) when (ex.Kind == ProviderFailureKind.NotFound && ex.StatusCode is null && !projectFresh)
        {
            // GraphQL addresses the project by path; it may have been renamed since the last metadata refresh.
            var project = await ResolveProjectAsync(api, projectId, cancellationToken).ConfigureAwait(false);
            return await FetchCountsOnceAsync(api, project.PathWithNamespace ?? identity.FullPath, projectId, milestones, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<CountsUpdate> FetchCountsOnceAsync(
        GitLabApi api, string fullPath, string projectId, IReadOnlyList<MilestoneSnapshot> milestones, CancellationToken cancellationToken)
    {
        var result = await GitLabCounts.FetchAsync(
            api,
            fullPath,
            projectId,
            milestones,
            ex =>
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    LogCountsFallback(_logger, ex.Kind, (int?)ex.StatusCode);
                }
            },
            cancellationToken).ConfigureAwait(false);
        return result.Counts;
    }

    private static async Task<ProviderConnectionResult> TestConnectionCoreAsync(GitLabApi api, RepositoryConfiguration configuration, CancellationToken cancellationToken)
    {
        var version = await GetVersionAsync(api, cancellationToken).ConfigureAwait(false);
        var (project, moved) = await api.GetProjectAsync(GitLabApi.EncodeProjectPath(configuration.FullPath), cancellationToken).ConfigureAwait(false);
        var identity = GitLabMapper.MapIdentity(project, api.ServerUri);

        GitLabTokenDto? token = null;
        try
        {
            token = await api.GetAsync<GitLabTokenDto>("personal_access_tokens/self", null, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderFailureException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            // Some token types or older servers cannot introspect themselves; the connection still works.
        }

        var scopes = token?.Scopes ?? [];
        var expiresOn = DateOnly.TryParseExact(token?.ExpiresAt, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : (DateOnly?)null;

        var message = string.Create(CultureInfo.InvariantCulture, $"Connected to GitLab {version ?? "(version unknown)"}: project {identity.FullPath} (id {identity.ExternalId}).");
        if (moved)
        {
            message += string.Create(CultureInfo.InvariantCulture, $" The project was moved; update the configured path to {identity.FullPath}.");
        }

        if (token is not null)
        {
            var extra = scopes.Where(s => !string.Equals(s, "read_api", StringComparison.Ordinal)).ToList();
            if (extra.Count > 0)
            {
                message += $" Warning: the token has more scopes than needed ({string.Join(", ", extra)}); GitWidget only needs read_api.";
            }

            if (!scopes.Contains("read_api", StringComparer.Ordinal) && !scopes.Contains("api", StringComparer.Ordinal))
            {
                message += " Warning: the token lacks the read_api scope.";
            }
        }

        return ProviderConnectionResult.Ok(identity, message) with
        {
            ServerVersion = version,
            CredentialDescription = token is null ? null : $"scopes: {(scopes.Count == 0 ? "none" : string.Join(", ", scopes))}",
            CredentialExpiresOn = expiresOn,
        };
    }

    private static async Task<string?> GetVersionAsync(GitLabApi api, CancellationToken cancellationToken)
    {
        try
        {
            return (await api.GetAsync<GitLabVersionDto>("metadata", null, cancellationToken).ConfigureAwait(false)).Version;
        }
        catch (ProviderFailureException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // /metadata appeared in 15.2; /version is deprecated but still served everywhere.
            return (await api.GetAsync<GitLabVersionDto>("version", null, cancellationToken).ConfigureAwait(false)).Version;
        }
    }

    private static string ResolvedFor(Uri serverUri, RepositoryConfiguration configuration) =>
        $"{serverUri.AbsoluteUri}|{configuration.FullPath.Trim().Trim('/')}";

    private static string? KnownProjectId(ProviderSyncState state, string resolvedFor) =>
        string.Equals(state.GetValue(ResolvedForKey), resolvedFor, StringComparison.OrdinalIgnoreCase)
        && state.GetValue(ProjectIdKey) is { } id
        && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) && numeric > 0
            ? id
            : null;

    private static DateTimeOffset? ReadWatermark(ProviderSyncState state, string key) =>
        DateTimeOffset.TryParse(state.GetValue(key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;

    private static DateTimeOffset? MaxWatermark(DateTimeOffset? current, IEnumerable<DateTimeOffset?> seen)
    {
        var max = current;
        foreach (var value in seen)
        {
            if (value is { } v && (max is null || v > max))
            {
                max = v;
            }
        }

        return max;
    }

    /// <summary>De-duplicates by id (offset pages and overlapping windows can repeat items), keeping the newest copy.</summary>
    private static List<T> Latest<T>(IEnumerable<T> items, Func<T, string> id, Func<T, DateTimeOffset> updatedAt) =>
        items
            .GroupBy(id, StringComparer.Ordinal)
            .Select(g => g.MaxBy(updatedAt)!)
            .ToList();

    private static string PageSizeText { get; } = GitLabApi.PageSize.ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(EventId = 1200, Level = LogLevel.Information, Message = "GitLab fetch {Scope} for project {ProjectId}: {Requests} requests, {Pages} list pages, issues {Issues}, merge requests {MergeRequests}")]
    private static partial void LogFetchCompleted(ILogger logger, RefreshScope scope, string projectId, int requests, int pages, string issues, string mergeRequests);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information, Message = "GitLab project was moved; now addressed as project {ProjectId}")]
    private static partial void LogProjectMoved(ILogger logger, long projectId);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Information, Message = "GitLab pipelines are not readable (403); builds are hidden")]
    private static partial void LogBuildsUnavailable(ILogger logger);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Information, Message = "GitLab commits are not readable (HTTP {Status}); the commit counter is hidden")]
    private static partial void LogCommitsUnavailable(ILogger logger, int? status);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Warning, Message = "GitLab GraphQL unavailable ({Kind}, HTTP {Status}); using issues_statistics (counts include all work-item types)")]
    private static partial void LogCountsFallback(ILogger logger, ProviderFailureKind kind, int? status);
}
