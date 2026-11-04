using System.Globalization;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Labels;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Domain.Snapshots;
using GitStatusWidget.Infrastructure.Providers.Http;

namespace GitStatusWidget.Infrastructure.Providers.GitLab;

/// <summary>
/// Maps GitLab DTOs to canonical domain models. Missing required fields throw
/// <see cref="ProviderResponseException"/> (classified as malformed at the provider boundary).
/// </summary>
internal static class GitLabMapper
{
    /// <summary>The only work-item type GitWidget treats as an issue (tasks, incidents, … are excluded).</summary>
    public const string IssueType = "issue";

    public static RepositoryIdentity MapIdentity(GitLabProjectDto project, Uri serverUri)
    {
        var fullPath = project.PathWithNamespace;
        var name = project.Path;
        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(fullPath))
        {
            name = fullPath[(fullPath.LastIndexOf('/') + 1)..];
        }

        if (project.Id <= 0 || string.IsNullOrWhiteSpace(name))
        {
            throw new ProviderResponseException("Project response lacks id or path.");
        }

        var owner = project.Namespace?.FullPath;
        if (string.IsNullOrWhiteSpace(owner))
        {
            var slash = fullPath?.LastIndexOf('/') ?? -1;
            owner = slash > 0 ? fullPath![..slash] : string.Empty;
        }

        return new RepositoryIdentity(RepositoryProviderKind.GitLab, serverUri.Authority, owner, name, Id(project.Id));
    }

    public static RepositoryFeatures MapFeatures(GitLabProjectDto project) => new(
        IsEnabled(project.IssuesAccessLevel, project.IssuesEnabled),
        IsEnabled(project.MergeRequestsAccessLevel, project.MergeRequestsEnabled),
        IsEnabled(project.BuildsAccessLevel, project.JobsEnabled));

    /// <summary>Whether an issue-list item is a real issue (not a task, incident, test case, requirement, …).</summary>
    public static bool IsIssue(GitLabIssueDto issue) =>
        issue.IssueType is null || string.Equals(issue.IssueType, IssueType, StringComparison.OrdinalIgnoreCase);

    public static IssueSnapshot MapIssue(GitLabIssueDto issue)
    {
        var labels = (issue.Labels ?? [])
            .Where(l => l is not null && !string.IsNullOrEmpty(l.Name))
            .Select(l => new LabelReference(l.Name!, NormalizeColor(l.Color)))
            .ToList();

        return new IssueSnapshot(
            Id(issue.Id),
            Number(issue.Iid),
            issue.Title ?? throw Missing("issue", "title"),
            string.Equals(issue.State, "closed", StringComparison.OrdinalIgnoreCase) ? WorkItemState.Closed : WorkItemState.Open,
            WebUrl(issue.WebUrl, "issue"),
            issue.CreatedAt ?? throw Missing("issue", "created_at"),
            issue.UpdatedAt ?? throw Missing("issue", "updated_at"),
            issue.ClosedAt,
            MapUser(issue.Author),
            (issue.Assignees ?? []).Select(MapUser).OfType<UserSummary>().ToList(),
            issue.Milestone is { Id: > 0 } milestone ? new MilestoneReference(Id(milestone.Id), milestone.Title ?? string.Empty) : null,
            labels);
    }

    public static ChangeRequestSnapshot MapMergeRequest(GitLabMergeRequestDto mergeRequest) => new(
        Id(mergeRequest.Id),
        Number(mergeRequest.Iid),
        ChangeRequestKind.MergeRequest,
        mergeRequest.Title ?? throw Missing("merge request", "title"),
        MapMergeRequestState(mergeRequest.State),
        mergeRequest.Draft ?? false,
        WebUrl(mergeRequest.WebUrl, "merge request"),
        mergeRequest.CreatedAt ?? throw Missing("merge request", "created_at"),
        mergeRequest.UpdatedAt ?? throw Missing("merge request", "updated_at"),
        MapUser(mergeRequest.Author));

    /// <summary><c>locked</c> is a short transitional state during merge and is treated as open.</summary>
    public static ChangeRequestState MapMergeRequestState(string? state) => state?.ToUpperInvariant() switch
    {
        "MERGED" => ChangeRequestState.Merged,
        "CLOSED" => ChangeRequestState.Closed,
        _ => ChangeRequestState.Open,
    };

    public static MilestoneSnapshot MapMilestone(GitLabMilestoneDto milestone) => new(
        Id(milestone.Id),
        milestone.Title ?? throw Missing("milestone", "title"),
        milestone.Description,
        ParseDate(milestone.StartDate),
        ParseDate(milestone.DueDate),
        string.Equals(milestone.State, "closed", StringComparison.OrdinalIgnoreCase),
        0,
        0,
        TryWebUrl(milestone.WebUrl),
        milestone.UpdatedAt);

    public static LabelSnapshot MapLabel(GitLabLabelDto label) => new(
        label.Name ?? throw Missing("label", "name"),
        NormalizeColor(label.Color) ?? string.Empty,
        string.IsNullOrEmpty(label.Description) ? null : label.Description);

    public static BuildSnapshot MapPipeline(GitLabPipelineDto pipeline)
    {
        var state = MapPipelineStatus(pipeline.Status);
        var updatedAt = pipeline.UpdatedAt ?? pipeline.CreatedAt ?? throw Missing("pipeline", "updated_at");
        var snapshot = new BuildSnapshot(
            Id(pipeline.Id),
            string.IsNullOrWhiteSpace(pipeline.Name)
                ? string.Create(CultureInfo.InvariantCulture, $"Pipeline #{(pipeline.Iid > 0 ? pipeline.Iid : pipeline.Id)}")
                : pipeline.Name,
            state,
            pipeline.Ref,
            pipeline.Sha,
            WebUrl(pipeline.WebUrl, "pipeline"),
            pipeline.CreatedAt ?? throw Missing("pipeline", "created_at"),
            null,
            updatedAt);

        // The list endpoint has no finished_at; for terminal pipelines updated_at is the completion time
        // (status changes are the last update a pipeline receives).
        return snapshot.IsCompleted ? snapshot with { CompletedAt = updatedAt } : snapshot;
    }

    /// <summary>Maps all GitLab pipeline statuses (gitlab-api.md §8.3).</summary>
    public static BuildState MapPipelineStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "created" or "waiting_for_resource" or "preparing" or "pending" or "scheduled" => BuildState.Queued,

        // Blocked on a manual job; there is no "blocked" state in the domain (yet).
        "manual" => BuildState.Queued,
        "running" or "waiting_for_callback" => BuildState.Running,
        "success" => BuildState.Succeeded,
        "failed" => BuildState.Failed,

        // Map "canceling" straight to Canceled so BuildCanceled fires once, at the earliest moment.
        "canceled" or "canceling" => BuildState.Canceled,
        "skipped" => BuildState.Skipped,
        _ => BuildState.Unknown,
    };

    public static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        var value = color.Trim();
        var hex = value.StartsWith('#') ? value[1..] : value;
        return hex.Length is 6 or 3 && hex.All(Uri.IsHexDigit) ? "#" + hex.ToUpperInvariant() : value;
    }

    public static string Id(long id) =>
        id > 0 ? id.ToString(CultureInfo.InvariantCulture) : throw new ProviderResponseException("Response item lacks an id.");

    public static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static UserSummary? MapUser(GitLabUserDto? user) =>
        user is { Id: > 0 } && !string.IsNullOrEmpty(user.Username)
            ? new UserSummary(Id(user.Id), user.Username, string.IsNullOrEmpty(user.Name) ? null : user.Name)
            : null;

    private static bool IsEnabled(string? accessLevel, bool? legacyFlag) =>
        accessLevel is not null
            ? !string.Equals(accessLevel, "disabled", StringComparison.OrdinalIgnoreCase)
            : legacyFlag ?? true;

    private static int Number(long iid) =>
        iid is > 0 and <= int.MaxValue ? (int)iid : throw new ProviderResponseException("Response item has an invalid iid.");

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static Uri WebUrl(string? value, string entity) =>
        TryWebUrl(value) ?? throw new ProviderResponseException($"The {entity} has no valid web_url.");

    private static Uri? TryWebUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;

    private static ProviderResponseException Missing(string entity, string field) =>
        new($"The {entity} response lacks '{field}'.");
}
