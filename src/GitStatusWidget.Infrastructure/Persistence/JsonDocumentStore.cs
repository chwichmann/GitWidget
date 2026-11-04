using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Infrastructure.Persistence;

/// <summary>
/// Versioned JSON document with atomic writes, for small UI state such as <c>window-state.json</c>.
/// Missing, corrupt or future-version files yield <see langword="null"/>.
/// </summary>
/// <typeparam name="T">Document type.</typeparam>
public sealed class JsonDocumentStore<T> : IDisposable
    where T : class
{
    private readonly string _path;
    private readonly int _version;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the store.</summary>
    /// <param name="path">File path.</param>
    /// <param name="version">Schema version.</param>
    /// <param name="logger">Logger.</param>
    public JsonDocumentStore(string path, int version, ILogger logger)
    {
        _path = path;
        _version = version;
        _logger = logger;
    }

    /// <summary>Loads the document.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The document or <see langword="null"/>.</returns>
    public async Task<T?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (_, value) = await VersionedJsonFile.ReadAsync<T>(_path, _version, null, _logger, cancellationToken).ConfigureAwait(false);
            return value;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Saves the document atomically.</summary>
    /// <param name="value">Document.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the file is written.</returns>
    public async Task SaveAsync(T value, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await VersionedJsonFile.WriteAtomicAsync(_path, _version, value, indented: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
