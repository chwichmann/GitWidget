using GitStatusWidget.Application.Persistence;
using GitStatusWidget.Application.Settings;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Infrastructure.Persistence;

/// <summary>
/// File-based <see cref="ISettingsStore"/> (<c>settings.json</c>, indented for manual inspection).
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore, IDisposable
{
    /// <summary>Current schema version of the settings document.</summary>
    public const int SchemaVersion = 1;

    private readonly AppPaths _paths;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the store.</summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public JsonSettingsStore(AppPaths paths, ILogger<JsonSettingsStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (status, settings) = await VersionedJsonFile.ReadAsync<AppSettings>(_paths.SettingsFile, SchemaVersion, null, _logger, cancellationToken).ConfigureAwait(false);
            return status switch
            {
                VersionedJsonFile.ReadStatus.Ok => settings!,
                VersionedJsonFile.ReadStatus.FutureVersion => throw new UnsupportedSchemaVersionException(
                    $"{_paths.SettingsFile} was written by a newer GitWidget version. Update GitWidget or move the file away."),
                _ => new AppSettings(),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await VersionedJsonFile.WriteAtomicAsync(_paths.SettingsFile, SchemaVersion, settings, indented: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
