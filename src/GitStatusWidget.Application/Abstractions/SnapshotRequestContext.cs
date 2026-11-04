using GitStatusWidget.Domain.Snapshots;

namespace GitStatusWidget.Application.Abstractions;

/// <summary>
/// Input of a provider fetch.
/// </summary>
/// <param name="Previous">Last accepted snapshot, or <see langword="null"/> for the first fetch. When null the
/// provider must return full sections (the scope is then always <see cref="RefreshScope.All"/>).</param>
/// <param name="SyncState">Provider-owned state from the previous successful fetch (watermarks, ETags).</param>
/// <param name="Scope">Requested sections.</param>
/// <param name="ClosedWindowStart">Closed items updated at or after this instant must be included in full
/// work-item sections ("recently closed").</param>
public sealed record SnapshotRequestContext(
    RepositorySnapshot? Previous,
    ProviderSyncState SyncState,
    RefreshScope Scope,
    DateTimeOffset ClosedWindowStart);
