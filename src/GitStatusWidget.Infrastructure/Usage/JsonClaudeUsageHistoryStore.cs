using GitStatusWidget.Application.Usage;
using GitStatusWidget.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Infrastructure.Usage;

/// <summary>
/// Keeps the Claude usage history in <c>claude-usage-history.json</c> (issue #18). It holds only percentages and
/// times, no credentials. A missing or corrupt file yields no history.
/// </summary>
internal sealed class JsonClaudeUsageHistoryStore : IClaudeUsageHistoryStore, IDisposable
{
    private const int SchemaVersion = 1;
    private readonly JsonDocumentStore<ClaudeUsageHistoryData> _store;

    /// <summary>Creates the store.</summary>
    /// <param name="paths">File locations.</param>
    /// <param name="logger">Logger.</param>
    public JsonClaudeUsageHistoryStore(AppPaths paths, ILogger<JsonClaudeUsageHistoryStore> logger) =>
        _store = new JsonDocumentStore<ClaudeUsageHistoryData>(paths.ClaudeUsageHistoryFile, SchemaVersion, logger);

    /// <inheritdoc />
    public Task<ClaudeUsageHistoryData?> LoadAsync(CancellationToken cancellationToken) => _store.LoadAsync(cancellationToken);

    /// <inheritdoc />
    public Task SaveAsync(ClaudeUsageHistoryData data, CancellationToken cancellationToken) => _store.SaveAsync(data, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _store.Dispose();
}
