using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GitStatusWidget.Application.Persistence;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Infrastructure.Persistence;

/// <summary>
/// Reads and writes versioned JSON documents <c>{"schemaVersion": n, "data": …}</c> with atomic replacement
/// (architecture §12.2, §19.1).
/// </summary>
internal static partial class VersionedJsonFile
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        IgnoreReadOnlyProperties = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public static JsonSerializerOptions IndentedOptions { get; } = new(Options) { WriteIndented = true };

    /// <summary>Outcome of reading a document.</summary>
    public enum ReadStatus
    {
        /// <summary>Document read successfully.</summary>
        Ok,

        /// <summary>No file exists.</summary>
        Missing,

        /// <summary>File was unreadable and has been moved aside.</summary>
        Corrupt,

        /// <summary>File was written by a newer version and has been left untouched.</summary>
        FutureVersion,
    }

    public static async Task<(ReadStatus Status, T? Value)> ReadAsync<T>(
        string path,
        int currentVersion,
        Func<int, JsonNode, JsonNode>? migrate,
        ILogger logger,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
        {
            return (ReadStatus.Missing, null);
        }

        try
        {
            JsonNode? root;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            {
                root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var version = root?["schemaVersion"]?.GetValue<int>() ?? throw new JsonException("Missing schemaVersion.");
            var data = root["data"] ?? throw new JsonException("Missing data.");
            if (version > currentVersion)
            {
                LogFutureVersion(logger, Path.GetFileName(path), version, currentVersion);
                return (ReadStatus.FutureVersion, null);
            }

            while (version < currentVersion)
            {
                if (migrate is null)
                {
                    throw new JsonException($"No migration from schema version {version}.");
                }

                data = migrate(version, data);
                version++;
                LogMigrated(logger, Path.GetFileName(path), version);
            }

            var value = data.Deserialize<T>(Options) ?? throw new JsonException("Document data is null.");
            return (ReadStatus.Ok, value);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException)
        {
            var aside = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            TryMove(path, aside);
            LogCorrupt(logger, ex, Path.GetFileName(path), Path.GetFileName(aside));
            return (ReadStatus.Corrupt, null);
        }
    }

    public static async Task WriteAtomicAsync<T>(string path, int version, T value, bool indented, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var envelope = new JsonObject
                {
                    ["schemaVersion"] = version,
                    ["data"] = JsonSerializer.SerializeToNode(value, indented ? IndentedOptions : Options),
                };
                await JsonSerializer.SerializeAsync(stream, envelope, indented ? IndentedOptions : Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            await ReplaceWithRetryAsync(temp, path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temp))
            {
                TryDelete(temp);
            }
        }
    }

    private static async Task ReplaceWithRetryAsync(string temp, string path, CancellationToken cancellationToken)
    {
        // Virus scanners or sync clients may briefly hold the target open on Windows.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LoggerMessage(EventId = 1200, Level = LogLevel.Warning, Message = "{File} has schema version {Version}, newer than supported {Supported}; ignored")]
    private static partial void LogFutureVersion(ILogger logger, string file, int version, int supported);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning, Message = "{File} is corrupt and was moved to {Aside}")]
    private static partial void LogCorrupt(ILogger logger, Exception exception, string file, string aside);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Information, Message = "{File} migrated to schema version {Version}")]
    private static partial void LogMigrated(ILogger logger, string file, int version);
}
