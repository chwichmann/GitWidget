using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitStatusWidget.Infrastructure.Providers.GitLab;

// GitLab REST v4 DTOs (docs/reference/gitlab-api.md). Deserialized with ProviderJson.SnakeCase: unknown members are
// skipped, every id is a long, and nothing is required at the JSON level — required fields are validated by
// GitLabMapper so a missing field becomes a classified "malformed" failure instead of a crash.
// Personal data that the widget never needs (e-mail addresses, IPs, descriptions of issues) is not declared at all.
#pragma warning disable CA1812 // Instantiated by System.Text.Json.

/// <summary><c>GET projects/:id</c>.</summary>
internal sealed class GitLabProjectDto
{
    public long Id { get; init; }

    public string? Path { get; init; }

    public string? PathWithNamespace { get; init; }

    public GitLabNamespaceDto? Namespace { get; init; }

    public string? DefaultBranch { get; init; }

    public string? WebUrl { get; init; }

    public bool? Archived { get; init; }

    public string? IssuesAccessLevel { get; init; }

    public string? MergeRequestsAccessLevel { get; init; }

    public string? BuildsAccessLevel { get; init; }

    public bool? IssuesEnabled { get; init; }

    public bool? MergeRequestsEnabled { get; init; }

    public bool? JobsEnabled { get; init; }
}

/// <summary>Namespace embedded in a project.</summary>
internal sealed class GitLabNamespaceDto
{
    public string? FullPath { get; init; }
}

/// <summary>User object (author, assignees). <c>public_email</c> is deliberately not mapped.</summary>
internal sealed class GitLabUserDto
{
    public long Id { get; init; }

    public string? Username { get; init; }

    public string? Name { get; init; }
}

/// <summary>Milestone embedded in an issue.</summary>
internal sealed class GitLabMilestoneRefDto
{
    public long Id { get; init; }

    public string? Title { get; init; }
}

/// <summary>Label on an issue: a name (default) or an object with colour (<c>with_labels_details=true</c>).</summary>
[JsonConverter(typeof(GitLabIssueLabelConverter))]
internal sealed class GitLabIssueLabelDto
{
    public string? Name { get; init; }

    public string? Color { get; init; }
}

/// <summary><c>GET projects/:id/issues</c> item.</summary>
internal sealed class GitLabIssueDto
{
    public long Id { get; init; }

    public long Iid { get; init; }

    public string? Title { get; init; }

    public string? State { get; init; }

    public string? IssueType { get; init; }

    public string? WebUrl { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }

    public GitLabUserDto? Author { get; init; }

    public IReadOnlyList<GitLabUserDto>? Assignees { get; init; }

    public GitLabMilestoneRefDto? Milestone { get; init; }

    public IReadOnlyList<GitLabIssueLabelDto>? Labels { get; init; }
}

/// <summary><c>GET projects/:id/merge_requests</c> item.</summary>
internal sealed class GitLabMergeRequestDto
{
    public long Id { get; init; }

    public long Iid { get; init; }

    public string? Title { get; init; }

    public string? State { get; init; }

    public bool? Draft { get; init; }

    public string? WebUrl { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public GitLabUserDto? Author { get; init; }
}

/// <summary><c>GET projects/:id/milestones</c> item.</summary>
internal sealed class GitLabMilestoneDto
{
    public long Id { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? State { get; init; }

    public string? StartDate { get; init; }

    public string? DueDate { get; init; }

    public string? WebUrl { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary><c>GET projects/:id/labels</c> item.</summary>
internal sealed class GitLabLabelDto
{
    public string? Name { get; init; }

    public string? Color { get; init; }

    public string? Description { get; init; }
}

/// <summary><c>GET projects/:id/pipelines</c> item (list fields only).</summary>
internal sealed class GitLabPipelineDto
{
    public long Id { get; init; }

    public long Iid { get; init; }

    public string? Sha { get; init; }

    public string? Ref { get; init; }

    public string? Status { get; init; }

    public string? Name { get; init; }

    public string? WebUrl { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary><c>GET projects/:id/issues_statistics</c>.</summary>
internal sealed class GitLabIssuesStatisticsDto
{
    public GitLabStatisticsDto? Statistics { get; init; }
}

/// <summary>Statistics wrapper.</summary>
internal sealed class GitLabStatisticsDto
{
    public GitLabStatisticsCountsDto? Counts { get; init; }
}

/// <summary>Statistics counts (all work-item types).</summary>
internal sealed class GitLabStatisticsCountsDto
{
    public int? All { get; init; }

    public int? Closed { get; init; }

    public int? Opened { get; init; }
}

/// <summary><c>GET metadata</c> / <c>GET version</c>.</summary>
internal sealed class GitLabVersionDto
{
    public string? Version { get; init; }

    public bool? Enterprise { get; init; }
}

/// <summary><c>GET personal_access_tokens/self</c>. <c>last_used_ips</c> is deliberately not mapped.</summary>
internal sealed class GitLabTokenDto
{
    public string? Name { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }

    public string? ExpiresAt { get; init; }

    public bool? Active { get; init; }

    public bool? Revoked { get; init; }
}

/// <summary>GraphQL <c>data</c> of the counts query: <c>project</c> is a map of alias to <c>{ count }</c>.</summary>
internal sealed class GitLabCountsData
{
    public Dictionary<string, GitLabCountNode?>? Project { get; init; }
}

/// <summary>GraphQL <c>{ count }</c> node.</summary>
internal sealed class GitLabCountNode
{
    public int? Count { get; init; }
}

#pragma warning restore CA1812

/// <summary>
/// Reads issue labels in either shape: <c>"bug"</c> or <c>{"name":"bug","color":"#d9534f",…}</c>.
/// </summary>
internal sealed class GitLabIssueLabelConverter : JsonConverter<GitLabIssueLabelDto>
{
    public override GitLabIssueLabelDto? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new GitLabIssueLabelDto { Name = reader.GetString() };
            case JsonTokenType.StartObject:
                using (var document = JsonDocument.ParseValue(ref reader))
                {
                    var root = document.RootElement;
                    return new GitLabIssueLabelDto
                    {
                        Name = ReadString(root, "name"),
                        Color = ReadString(root, "color"),
                    };
                }

            case JsonTokenType.Null:
                return null;
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for an issue label.");
        }
    }

    public override void Write(Utf8JsonWriter writer, GitLabIssueLabelDto value, JsonSerializerOptions options) =>
        throw new NotSupportedException("GitLab DTOs are read-only.");

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
