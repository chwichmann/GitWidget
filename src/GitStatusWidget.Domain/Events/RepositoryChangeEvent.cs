using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Domain.Events;

/// <summary>
/// A semantic change detected between two accepted snapshots.
/// </summary>
/// <param name="Key">Stable deduplication key (see <see cref="EventKey"/>).</param>
/// <param name="Type">Event type.</param>
/// <param name="Priority">Presentation priority.</param>
/// <param name="Repository">Repository the event belongs to.</param>
/// <param name="EntityExternalId">Provider id of the affected entity.</param>
/// <param name="EntityNumber">Visible number of the entity, if any.</param>
/// <param name="Title">Entity title (plain text).</param>
/// <param name="WebUrl">Browser URL of the entity, if any.</param>
/// <param name="OccurredAt">Best-known time of the change (UTC).</param>
/// <param name="Detail">Optional short detail text (for example the new milestone title).</param>
public sealed record RepositoryChangeEvent(
    string Key,
    ChangeEventType Type,
    EventPriority Priority,
    RepositoryIdentity Repository,
    string EntityExternalId,
    int? EntityNumber,
    string Title,
    Uri? WebUrl,
    DateTimeOffset OccurredAt,
    string? Detail = null);
