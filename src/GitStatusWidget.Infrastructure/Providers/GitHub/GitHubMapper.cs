using System.Globalization;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Labels;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Infrastructure.Providers.Http;

namespace GitStatusWidget.Infrastructure.Providers.GitHub;

/// <summary>
/// Maps GitHub DTOs to canonical domain records. Messages of thrown exceptions never contain titles or bodies.
/// </summary>
internal static class GitHubMapper
{
    /// <summary>Color used when GitHub returns no or an invalid label color (GitHub's default label grey).</summary>
    public const string FallbackLabelColor = "#ededed";

    /// <summary>Maps the repository response to an identity (owner/name from <c>full_name</c>, so renames show).</summary>
    public static RepositoryIdentity MapIdentity(GitHubRepositoryDto dto, string host)
    {
        var nodeId = Required(dto.NodeId, "repository node_id");
        string owner;
        string name;
        var fullName = dto.FullName;
        var slash = fullName?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        if (fullName is not null && slash > 0 && slash < fullName.Length - 1)
        {
            owner = fullName[..slash];
            name = fullName[(slash + 1)..];
        }
        else
        {
            owner = Required(dto.Owner?.Login, "repository owner");
            name = Required(dto.Name, "repository name");
        }

        return new RepositoryIdentity(RepositoryProviderKind.GitHub, host, owner, name, nodeId);
    }

    /// <summary>Whether an issues-list item is a pull request (G1).</summary>
    public static bool IsPullRequest(GitHubIssueDto dto) => dto.PullRequest is not null;

    /// <summary>Maps an issue item (must not be a pull request).</summary>
    public static IssueSnapshot MapIssue(GitHubIssueDto dto)
    {
        var nodeId = Required(dto.NodeId, "issue node_id");
        return new IssueSnapshot(
            nodeId,
            dto.Number,
            dto.Title ?? string.Empty,
            string.Equals(dto.State, "closed", StringComparison.OrdinalIgnoreCase) ? WorkItemState.Closed : WorkItemState.Open,
            RequiredUri(dto.HtmlUrl, "issue html_url"),
            Utc(Required(dto.CreatedAt, "issue created_at")),
            Utc(Required(dto.UpdatedAt, "issue updated_at")),
            dto.ClosedAt is { } closed ? Utc(closed) : null,
            MapUser(dto.User),
            (dto.Assignees ?? []).Select(MapUser).OfType<UserSummary>().ToList(),
            dto.Milestone is { } milestone && !string.IsNullOrEmpty(milestone.NodeId)
                ? new MilestoneReference(milestone.NodeId, milestone.Title ?? string.Empty)
                : null,
            (dto.Labels ?? [])
                .Where(l => !string.IsNullOrEmpty(l?.Name))
                .Select(l => new LabelReference(l!.Name!, NormalizeColor(l.Color)))
                .ToList());
    }

    /// <summary>Maps a pull-request item of the issues list.</summary>
    public static ChangeRequestSnapshot MapChangeRequest(GitHubIssueDto dto)
    {
        var nodeId = Required(dto.NodeId, "pull request node_id");
        var state = !string.Equals(dto.State, "closed", StringComparison.OrdinalIgnoreCase)
            ? ChangeRequestState.Open
            : dto.PullRequest?.MergedAt is not null ? ChangeRequestState.Merged : ChangeRequestState.Closed;
        return new ChangeRequestSnapshot(
            nodeId,
            dto.Number,
            ChangeRequestKind.PullRequest,
            dto.Title ?? string.Empty,
            state,
            dto.Draft ?? false,
            RequiredUri(dto.PullRequest?.HtmlUrl ?? dto.HtmlUrl, "pull request html_url"),
            Utc(Required(dto.CreatedAt, "pull request created_at")),
            Utc(Required(dto.UpdatedAt, "pull request updated_at")),
            MapUser(dto.User));
    }

    /// <summary>Maps a milestone. Open/closed counts are left at 0 (REST counts include PRs, G3).</summary>
    public static MilestoneSnapshot MapMilestone(GitHubMilestoneDto dto) => new(
        Required(dto.NodeId, "milestone node_id"),
        dto.Title ?? string.Empty,
        string.IsNullOrEmpty(dto.Description) ? null : dto.Description,
        StartDate: null,
        DueDate: dto.DueOn is { } due ? DateOnly.FromDateTime(due.UtcDateTime) : null,
        IsClosed: string.Equals(dto.State, "closed", StringComparison.OrdinalIgnoreCase),
        OpenIssueCount: 0,
        ClosedIssueCount: 0,
        WebUrl: Uri.TryCreate(dto.HtmlUrl, UriKind.Absolute, out var url) ? url : null,
        UpdatedAt: dto.UpdatedAt is { } updated ? Utc(updated) : null);

    /// <summary>Maps a repository label.</summary>
    public static LabelSnapshot? MapLabel(GitHubLabelDto? dto) =>
        string.IsNullOrEmpty(dto?.Name)
            ? null
            : new LabelSnapshot(dto.Name, NormalizeColor(dto.Color) ?? FallbackLabelColor, string.IsNullOrEmpty(dto.Description) ? null : dto.Description);

    /// <summary>Maps a workflow run.</summary>
    public static BuildSnapshot MapBuild(GitHubWorkflowRunDto dto)
    {
        if (dto.Id <= 0)
        {
            throw new ProviderResponseException("Workflow run without id.");
        }

        var state = MapBuildState(dto.Status, dto.Conclusion);
        var created = Utc(Required(dto.CreatedAt, "workflow run created_at"));
        var updated = dto.UpdatedAt is { } u ? Utc(u) : created;
        var completed = string.Equals(dto.Status, "completed", StringComparison.OrdinalIgnoreCase) ? updated : (DateTimeOffset?)null;
        return new BuildSnapshot(
            dto.Id.ToString(CultureInfo.InvariantCulture),
            !string.IsNullOrWhiteSpace(dto.Name) ? dto.Name : !string.IsNullOrWhiteSpace(dto.DisplayTitle) ? dto.DisplayTitle : "Workflow run",
            state,
            dto.HeadBranch,
            dto.HeadSha,
            RequiredUri(dto.HtmlUrl, "workflow run html_url"),
            created,
            completed,
            updated);
    }

    /// <summary>Maps status/conclusion to a build state (github-api.md §7.3).</summary>
    public static BuildState MapBuildState(string? status, string? conclusion)
    {
        switch (status?.ToLowerInvariant())
        {
            case "requested" or "queued" or "pending" or "waiting":
                return BuildState.Queued;
            case "in_progress":
                return BuildState.Running;
            case "completed":
                return conclusion?.ToLowerInvariant() switch
                {
                    "success" or "neutral" => BuildState.Succeeded,
                    "failure" or "timed_out" or "startup_failure" => BuildState.Failed,
                    "cancelled" => BuildState.Canceled,
                    "skipped" => BuildState.Skipped,
                    "action_required" => BuildState.Queued,
                    _ => BuildState.Unknown, // stale, null, unrecognized
                };
            default:
                return BuildState.Unknown;
        }
    }

    /// <summary>Normalizes a GitHub color (<c>d73a4a</c>) to <c>#d73a4a</c>; returns null when invalid.</summary>
    public static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        var hex = color.Trim().TrimStart('#');
        if (hex.Length == 3 && hex.All(char.IsAsciiHexDigit))
        {
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        }

        return hex.Length == 6 && hex.All(char.IsAsciiHexDigit) ? "#" + hex.ToLowerInvariant() : null;
    }

    /// <summary>Maps a user; never maps e-mail addresses. Returns null for missing users.</summary>
    public static UserSummary? MapUser(GitHubUserDto? dto)
    {
        if (dto is null || string.IsNullOrEmpty(dto.Login))
        {
            return null;
        }

        var id = !string.IsNullOrEmpty(dto.NodeId)
            ? dto.NodeId
            : dto.Id is { } numeric ? numeric.ToString(CultureInfo.InvariantCulture) : dto.Login;
        return new UserSummary(id, dto.Login, null);
    }

    private static DateTimeOffset Utc(DateTimeOffset value) => value.ToUniversalTime();

    private static T Required<T>(T? value, string what)
        where T : struct =>
        value ?? throw new ProviderResponseException($"GitHub response is missing {what}.");

    private static string Required(string? value, string what) =>
        string.IsNullOrEmpty(value) ? throw new ProviderResponseException($"GitHub response is missing {what}.") : value;

    private static Uri RequiredUri(string? value, string what) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : throw new ProviderResponseException($"GitHub response has no valid {what}.");
}
