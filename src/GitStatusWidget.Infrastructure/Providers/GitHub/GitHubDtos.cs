using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitStatusWidget.Infrastructure.Providers.GitHub;

// Internal GitHub REST/GraphQL DTOs. Deserialized with ProviderJson.SnakeCase (REST) or ProviderJson.CamelCase
// (GraphQL): unknown fields are ignored, every field is optional, numeric ids are long (G13). E-mail addresses
// are deliberately not modelled.

/// <summary><c>GET /repos/{owner}/{repo}</c>.</summary>
internal sealed class GitHubRepositoryDto
{
    public long Id { get; init; }

    public string? NodeId { get; init; }

    public string? Name { get; init; }

    public string? FullName { get; init; }

    public GitHubUserDto? Owner { get; init; }

    public string? DefaultBranch { get; init; }

    public bool? HasIssues { get; init; }

    public bool? HasPullRequests { get; init; }

    public bool? Archived { get; init; }

    public bool? Private { get; init; }

    public string? Visibility { get; init; }

    public string? HtmlUrl { get; init; }
}

/// <summary>User reference (author, assignee). Ghost users may lack fields.</summary>
internal sealed class GitHubUserDto
{
    public long? Id { get; init; }

    public string? NodeId { get; init; }

    public string? Login { get; init; }

    public string? Type { get; init; }
}

/// <summary>Item of <c>GET /repos/{owner}/{repo}/issues</c> (issue or pull request, G1).</summary>
internal sealed class GitHubIssueDto
{
    public long Id { get; init; }

    public string? NodeId { get; init; }

    public int Number { get; init; }

    public string? Title { get; init; }

    public string? State { get; init; }

    public string? HtmlUrl { get; init; }

    public GitHubUserDto? User { get; init; }

    public IReadOnlyList<GitHubLabelDto?>? Labels { get; init; }

    public IReadOnlyList<GitHubUserDto?>? Assignees { get; init; }

    public GitHubMilestoneDto? Milestone { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }

    public bool? Draft { get; init; }

    /// <summary>Present (non-null) only for pull requests.</summary>
    public GitHubIssuePullRequestDto? PullRequest { get; init; }
}

/// <summary>The <c>pull_request</c> object of a PR item in the issues list.</summary>
internal sealed class GitHubIssuePullRequestDto
{
    public string? HtmlUrl { get; init; }

    public DateTimeOffset? MergedAt { get; init; }
}

/// <summary>Milestone (list item or embedded in an issue).</summary>
internal sealed class GitHubMilestoneDto
{
    public long Id { get; init; }

    public string? NodeId { get; init; }

    public int Number { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? State { get; init; }

    public string? HtmlUrl { get; init; }

    public DateTimeOffset? DueOn { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }
}

/// <summary>Label. Inside issues a label may also be a plain string (schema <c>oneOf</c>).</summary>
[JsonConverter(typeof(GitHubLabelDtoConverter))]
internal sealed class GitHubLabelDto
{
    public long? Id { get; init; }

    public string? NodeId { get; init; }

    public string? Name { get; init; }

    public string? Color { get; init; }

    public string? Description { get; init; }
}

/// <summary><c>GET /repos/{owner}/{repo}/actions/runs</c>.</summary>
internal sealed class GitHubWorkflowRunsDto
{
    public int? TotalCount { get; init; }

    public IReadOnlyList<GitHubWorkflowRunDto?>? WorkflowRuns { get; init; }
}

/// <summary>One workflow run.</summary>
internal sealed class GitHubWorkflowRunDto
{
    public long Id { get; init; }

    public string? Name { get; init; }

    public string? DisplayTitle { get; init; }

    public string? Status { get; init; }

    public string? Conclusion { get; init; }

    public string? Event { get; init; }

    public string? HeadBranch { get; init; }

    public string? HeadSha { get; init; }

    public string? HtmlUrl { get; init; }

    public int? RunAttempt { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public DateTimeOffset? RunStartedAt { get; init; }

    public GitHubRunRepositoryDto? HeadRepository { get; init; }
}

/// <summary>Head repository of a run (a fork for fork pull requests).</summary>
internal sealed class GitHubRunRepositoryDto
{
    public string? FullName { get; init; }
}

/// <summary>Accepts a label object or a plain label name.</summary>
internal sealed class GitHubLabelDtoConverter : JsonConverter<GitHubLabelDto>
{
    public override GitHubLabelDto? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return new GitHubLabelDto { Name = reader.GetString() };
            case JsonTokenType.StartObject:
                using (var document = JsonDocument.ParseValue(ref reader))
                {
                    var root = document.RootElement;
                    return new GitHubLabelDto
                    {
                        Id = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var value) ? value : null,
                        NodeId = GetString(root, "node_id"),
                        Name = GetString(root, "name"),
                        Color = GetString(root, "color"),
                        Description = GetString(root, "description"),
                    };
                }

            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for a label.");
        }
    }

    public override void Write(Utf8JsonWriter writer, GitHubLabelDto value, JsonSerializerOptions options) =>
        throw new NotSupportedException("GitHub DTOs are read-only.");

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>GraphQL envelope.</summary>
internal sealed class GitHubGraphQlResponse<TData>
    where TData : class
{
    public TData? Data { get; init; }

    public IReadOnlyList<GitHubGraphQlError>? Errors { get; init; }
}

/// <summary>GraphQL error entry. GitHub sets <c>type</c> (e.g. <c>NOT_FOUND</c>, <c>RATE_LIMITED</c>, <c>FORBIDDEN</c>).</summary>
internal sealed class GitHubGraphQlError
{
    public string? Type { get; init; }

    public string? Message { get; init; }
}

/// <summary><c>data</c> of the counts query.</summary>
internal sealed class GitHubCountsData
{
    public GitHubCountsRepository? Repository { get; init; }
}

/// <summary>Repository part of the counts query.</summary>
internal sealed class GitHubCountsRepository
{
    public GitHubTotalCount? OpenIssues { get; init; }

    public GitHubTotalCount? ClosedIssues { get; init; }

    public GitHubMilestoneConnection? Milestones { get; init; }
}

/// <summary>A connection reduced to <c>totalCount</c>.</summary>
internal sealed class GitHubTotalCount
{
    public int? TotalCount { get; init; }
}

/// <summary>Milestone connection page.</summary>
internal sealed class GitHubMilestoneConnection
{
    public GitHubPageInfo? PageInfo { get; init; }

    public IReadOnlyList<GitHubMilestoneCountNode?>? Nodes { get; init; }
}

/// <summary>GraphQL page info.</summary>
internal sealed class GitHubPageInfo
{
    public bool HasNextPage { get; init; }

    public string? EndCursor { get; init; }
}

/// <summary>Milestone node with closed issue count. <c>id</c> equals the REST <c>node_id</c> (verified).</summary>
internal sealed class GitHubMilestoneCountNode
{
    public string? Id { get; init; }

    public GitHubTotalCount? ClosedIssues { get; init; }
}
