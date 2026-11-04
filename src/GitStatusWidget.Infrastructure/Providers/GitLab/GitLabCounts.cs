using System.Globalization;
using System.Text;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Infrastructure.Providers.Http;

namespace GitStatusWidget.Infrastructure.Providers.GitLab;

/// <summary>
/// Exact issue totals (gitlab-api.md §4, §16.5). Primary source: one GraphQL query with aliases
/// (<c>types: [ISSUE]</c>, project-scoped, milestone titles passed as variables, ≤ 20 milestones per query).
/// Fallback when GraphQL is unavailable: <c>issues_statistics</c>, which counts every work-item type.
/// </summary>
internal static class GitLabCounts
{
    /// <summary>Maximum milestone aliases per GraphQL query (query complexity limit, gitlab-api.md §16.5).</summary>
    public const int MilestonesPerQuery = 20;

    /// <summary>Result of a count fetch.</summary>
    /// <param name="Counts">Totals.</param>
    /// <param name="UsedFallback">Whether <c>issues_statistics</c> was used.</param>
    public sealed record Result(CountsUpdate Counts, bool UsedFallback);

    public static async Task<Result> FetchAsync(
        GitLabApi api,
        string projectFullPath,
        string projectId,
        IReadOnlyList<MilestoneSnapshot> milestones,
        Action<ProviderFailureException> onFallback,
        CancellationToken cancellationToken)
    {
        try
        {
            return new Result(await FetchGraphQlAsync(api, projectFullPath, milestones, cancellationToken).ConfigureAwait(false), false);
        }
        catch (ProviderFailureException ex) when (CanFallBack(ex))
        {
            onFallback(ex);
            return new Result(await FetchStatisticsAsync(api, projectId, milestones, cancellationToken).ConfigureAwait(false), true);
        }
    }

    /// <summary>
    /// GraphQL is treated as unavailable when its endpoint answered with an HTTP-level failure that is neither an
    /// authentication/authorization problem nor a rate limit (route missing, blocked by a gateway, 5xx after retries).
    /// GraphQL <c>errors</c> arrive with HTTP 200 (no status code on the exception) and fail the fetch.
    /// </summary>
    internal static bool CanFallBack(ProviderFailureException ex) =>
        ex.StatusCode is not null
        && ex.Kind is not (ProviderFailureKind.AuthenticationFailed or ProviderFailureKind.AuthorizationFailed or ProviderFailureKind.RateLimited);

    internal static async Task<CountsUpdate> FetchGraphQlAsync(
        GitLabApi api,
        string projectFullPath,
        IReadOnlyList<MilestoneSnapshot> milestones,
        CancellationToken cancellationToken)
    {
        // Duplicate titles (project and ancestor group milestone) count the same issues; query each title once.
        var titles = milestones.Select(m => m.Title).Distinct(StringComparer.Ordinal).ToList();
        var chunks = titles.Chunk(MilestonesPerQuery).ToList();
        if (chunks.Count == 0)
        {
            chunks.Add([]);
        }

        int? open = null;
        var closed = 0;
        var closedByTitle = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            var chunk = chunks[chunkIndex];
            var includeTotals = chunkIndex == 0;
            var variables = new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = projectFullPath };
            for (var i = 0; i < chunk.Length; i++)
            {
                variables[Variable(i)] = chunk[i];
            }

            var data = await GraphQl.QueryAsync<GitLabCountsData>(
                api.Client,
                api.GraphQlEndpoint,
                BuildQuery(chunk.Length, includeTotals),
                variables,
                api.UserAgent,
                api.ConfigureHeaders,
                cancellationToken).ConfigureAwait(false);

            var project = data.Project
                ?? throw new ProviderFailureException(ProviderFailureKind.NotFound, "GitLab GraphQL did not return the project (not found or not visible with this token).");

            if (includeTotals)
            {
                open = Count(project, "io");
                closed = Count(project, "ic");
            }

            for (var i = 0; i < chunk.Length; i++)
            {
                closedByTitle[chunk[i]] = Count(project, Alias(i));
            }
        }

        return new CountsUpdate(open, closed, ByMilestoneId(milestones, closedByTitle));
    }

    /// <summary>
    /// <c>issues_statistics</c> fallback. It ignores <c>issue_type</c>, so tasks, incidents, test cases and
    /// requirements are included; therefore no open count is reported (it would not match the issue-only open set).
    /// </summary>
    internal static async Task<CountsUpdate> FetchStatisticsAsync(
        GitLabApi api,
        string projectId,
        IReadOnlyList<MilestoneSnapshot> milestones,
        CancellationToken cancellationToken)
    {
        var path = $"projects/{projectId}/issues_statistics";
        var totals = await GetStatisticsAsync(api, path, null, cancellationToken).ConfigureAwait(false);
        var closedByTitle = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var title in milestones.Select(m => m.Title).Distinct(StringComparer.Ordinal))
        {
            var counts = await GetStatisticsAsync(api, path, GitLabApi.Query(("milestone", title)), cancellationToken).ConfigureAwait(false);
            closedByTitle[title] = counts.Closed ?? 0;
        }

        return new CountsUpdate(null, totals.Closed ?? 0, ByMilestoneId(milestones, closedByTitle));
    }

    internal static string BuildQuery(int milestoneCount, bool includeTotals)
    {
        var query = new StringBuilder("query GitWidgetCounts($path: ID!");
        for (var i = 0; i < milestoneCount; i++)
        {
            query.Append(", $").Append(Variable(i)).Append(": String!");
        }

        query.Append(") { project(fullPath: $path) {");
        if (includeTotals)
        {
            query.Append(" io: issues(state: opened, types: [ISSUE]) { count }");
            query.Append(" ic: issues(state: closed, types: [ISSUE]) { count }");
        }

        for (var i = 0; i < milestoneCount; i++)
        {
            query.Append(' ').Append(Alias(i)).Append(": issues(state: closed, types: [ISSUE], milestoneTitle: [$").Append(Variable(i)).Append("]) { count }");
        }

        if (!includeTotals && milestoneCount == 0)
        {
            query.Append(" id");
        }

        return query.Append(" } }").ToString();
    }

    private static async Task<GitLabStatisticsCountsDto> GetStatisticsAsync(GitLabApi api, string path, string? query, CancellationToken cancellationToken)
    {
        var statistics = await api.GetAsync<GitLabIssuesStatisticsDto>(path, query, cancellationToken).ConfigureAwait(false);
        return statistics.Statistics?.Counts ?? throw new ProviderResponseException("issues_statistics response lacks counts.");
    }

    private static Dictionary<string, int> ByMilestoneId(IReadOnlyList<MilestoneSnapshot> milestones, Dictionary<string, int> closedByTitle) =>
        milestones
            .Where(m => closedByTitle.ContainsKey(m.Title))
            .GroupBy(m => m.ExternalId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => closedByTitle[g.First().Title], StringComparer.Ordinal);

    private static int Count(Dictionary<string, GitLabCountNode?> project, string alias) =>
        project.TryGetValue(alias, out var node) && node?.Count is { } count
            ? count
            : throw new ProviderResponseException($"GitLab GraphQL count '{alias}' is missing.");

    private static string Variable(int index) => string.Create(CultureInfo.InvariantCulture, $"t{index}");

    private static string Alias(int index) => string.Create(CultureInfo.InvariantCulture, $"m{index}");
}
