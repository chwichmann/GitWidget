using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Infrastructure.Logging;

/// <summary>
/// Minimal daily-rolling file logger (<c>logs/git-status-widget-yyyyMMdd.log</c>, architecture §19). Writes on a
/// background thread, keeps 14 days and redacts anything that looks like a token as a last line of defense
/// (architecture §18.2). Messages must already be sanitized by their callers.
/// </summary>
public sealed partial class FileLoggerProvider : ILoggerProvider
{
    private const int RetentionDays = 14;
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);

    /// <summary>Creates the provider.</summary>
    /// <param name="directory">Log directory.</param>
    /// <param name="minimumLevel">Minimum level written.</param>
    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(directory);
        DeleteOldFiles();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "GitWidget log writer" };
        _writer.Start();
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    /// <inheritdoc />
    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    /// <summary>Redacts token-like substrings.</summary>
    /// <param name="text">Text.</param>
    /// <returns>Redacted text.</returns>
    public static string Redact(string text) => TokenPattern().Replace(text, "***");

    [GeneratedRegex(@"(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_\-]{16,}|(?i:bearer)\s+[A-Za-z0-9._\-]{16,}|(?i:private-token:)\s*\S+)")]
    private static partial Regex TokenPattern();

    private void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.TryAdd(line);
        }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    var file = Path.Combine(_directory, $"git-status-widget-{DateTime.Now:yyyyMMdd}.log");
                    File.AppendAllText(file, line, Encoding.UTF8);
                }
                catch (IOException)
                {
                    // Logging must never crash the app.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void DeleteOldFiles()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "git-status-widget-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-RetentionDays))
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var builder = new StringBuilder();
            builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(" [").Append(logLevel.ToString()[..3].ToUpperInvariant()).Append("] ")
                .Append(category);
            if (eventId.Id != 0)
            {
                builder.Append(" (").Append(eventId.Id.ToString(CultureInfo.InvariantCulture)).Append(')');
            }

            builder.Append(": ").Append(formatter(state, exception));
            if (exception is not null)
            {
                // Type and message only; stack traces may contain request details.
                builder.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
            }

            builder.AppendLine();
            provider.Enqueue(Redact(builder.ToString()));
        }
    }
}
