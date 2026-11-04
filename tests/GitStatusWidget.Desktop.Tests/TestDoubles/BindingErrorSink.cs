using System.Globalization;
using Avalonia.Logging;

namespace GitStatusWidget.Desktop.Tests.TestDoubles;

/// <summary>
/// Captures Avalonia binding warnings/errors while installed (binding failures are only logged, never thrown).
/// Headless tests run serially on the Avalonia dispatcher, so swapping the global sink is safe there.
/// </summary>
internal sealed class BindingErrorSink : ILogSink, IDisposable
{
    private readonly ILogSink? _previous;

    public BindingErrorSink()
    {
        _previous = Logger.Sink;
        Logger.Sink = this;
    }

    public List<string> Messages { get; } = [];

    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Log(level, area, source, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        // A null intermediate (e.g. Projection before the first selection) is expected and covered by FallbackValue;
        // Avalonia still logs it as a warning.
        if (IsEnabled(level, area) && !propertyValues.Any(v => v is "Value is null."))
        {
            var values = string.Join(", ", propertyValues.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)));
            Messages.Add($"[{level}] {source?.GetType().Name}: {messageTemplate} ({values})");
        }
    }

    public void Dispose() => Logger.Sink = _previous;
}
