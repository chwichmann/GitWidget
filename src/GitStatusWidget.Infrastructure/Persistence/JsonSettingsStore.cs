using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// <remarks>Version 2 (issue #12): <c>selectedMilestoneId</c> replaced by <c>milestoneMode</c>,
    /// <c>automaticMilestoneCount</c> and <c>selectedMilestoneIds</c>.</remarks>
    public const int SchemaVersion = 2;

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
            var (status, settings) = await VersionedJsonFile.ReadAsync<AppSettings>(_paths.SettingsFile, SchemaVersion, Migrate, _logger, cancellationToken).ConfigureAwait(false);
            return status switch
            {
                VersionedJsonFile.ReadStatus.Ok => settings!.Normalize(),
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

    /// <summary>Explicit migrations of older settings documents (architecture §19.1).</summary>
    /// <param name="fromVersion">Version of <paramref name="data"/>.</param>
    /// <param name="data">The <c>data</c> node of the document.</param>
    /// <returns>The migrated node (version <paramref name="fromVersion"/> + 1).</returns>
    internal static JsonNode Migrate(int fromVersion, JsonNode data)
    {
        if (fromVersion == 1)
        {
            // v1 -> v2: one optional selected milestone becomes the manual mode with one milestone; no selection stays
            // automatic with one milestone (unchanged widget).
            foreach (var repository in data["repositories"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var selected = repository["selectedMilestoneId"]?.GetValue<string>();
                repository.Remove("selectedMilestoneId");
                if (!string.IsNullOrWhiteSpace(selected))
                {
                    repository["milestoneMode"] = "Manual";
                    repository["selectedMilestoneIds"] = new JsonArray(selected);
                }
                else
                {
                    repository["milestoneMode"] = "Automatic";
                    repository["automaticMilestoneCount"] = 1;
                }
            }

            return data;
        }

        throw new JsonException($"No migration from settings schema version {fromVersion}.");
    }
}
