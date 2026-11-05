using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Health;
using GitStatusWidget.Domain.Issues;

namespace GitStatusWidget.Desktop.Converters;

/// <summary>Maps semantic states to theme brushes (green/red/blue/amber/gray, architecture §16.3).</summary>
public sealed class SemanticBrushConverter : IValueConverter
{
    public static readonly SemanticBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            BuildState.Succeeded => "SuccessBrush",
            BuildState.Failed => "FailureBrush",
            BuildState.Running or BuildState.Queued => "InfoBrush",
            BuildState.Canceled => "WarningBrush",
            BuildState => "NeutralBrush",
            RepositoryHealthState.Healthy => "SuccessBrush",
            RepositoryHealthState.Refreshing => "InfoBrush",
            RepositoryHealthState.AuthenticationFailed or RepositoryHealthState.AuthorizationFailed or RepositoryHealthState.NotFound or RepositoryHealthState.ConfigurationError => "FailureBrush",
            RepositoryHealthState.Stale or RepositoryHealthState.RateLimited or RepositoryHealthState.ProviderError or RepositoryHealthState.GatewayBlocked => "WarningBrush",
            RepositoryHealthState => "NeutralBrush",
            WorkItemState.Open => "InfoBrush",
            WorkItemState.Closed => "SuccessBrush",
            ChangeRequestState.Merged => "SuccessBrush",
            ChangeRequestState.Open => "InfoBrush",
            ChangeRequestState => "NeutralBrush",
            ToastKind.Success => "SuccessBrush",
            ToastKind.Failure => "FailureBrush",
            ToastKind.Warning => "WarningBrush",
            ToastKind => "InfoBrush",
            true => "WarningBrush",
            _ => "NeutralBrush",
        };
        return Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var brush) ? brush : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Short glyph for states, so state is never conveyed by color alone (architecture §17).</summary>
public sealed class StateGlyphConverter : IValueConverter
{
    public static readonly StateGlyphConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        BuildState.Succeeded => "✓",
        BuildState.Failed => "✕",
        BuildState.Running => "▶",
        BuildState.Queued => "…",
        BuildState.Canceled => "⊘",
        BuildState.Skipped => "»",
        RepositoryHealthState.Healthy => "●",
        RepositoryHealthState.Refreshing => "↻",
        RepositoryHealthState.Stale => "◷",
        RepositoryHealthState.Offline => "⌀",
        RepositoryHealthState.RateLimited => "⏸",
        RepositoryHealthState => "!",
        WorkItemState.Open => "○",
        WorkItemState.Closed => "✓",
        _ => "?",
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Converts <c>#RRGGBB</c> strings into brushes.</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static readonly HexBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && Color.TryParse(text, out var color) ? new SolidColorBrush(color) : Brushes.Gray;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Formats timestamps as local relative/absolute text.</summary>
public sealed class LocalTimeConverter : IValueConverter
{
    public static readonly LocalTimeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset instant => Format.Age(instant, DateTimeOffset.UtcNow),
        DateOnly date => date.ToString("d", culture),
        _ => string.Empty,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>" · Widget" marker for milestones shown in the compact widget (issue #12).</summary>
public sealed class InWidgetConverter : IValueConverter
{
    public static readonly InWidgetConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "  ◆ Widget" : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>" · 99 %" suffix for milestone completion (issue #11); " · –" for milestones without issues.</summary>
public sealed class CompletionSuffixConverter : IValueConverter
{
    public static readonly CompletionSuffixConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        " · " + Format.Percent(value as int?);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Ratio (0..1) to percent (0..100).</summary>
public sealed class PercentConverter : IValueConverter
{
    public static readonly PercentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double ratio ? ratio * 100 : 0d;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Joins string lists.</summary>
public sealed class JoinConverter : IValueConverter
{
    public static readonly JoinConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<string> items ? string.Join(", ", items) : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Bold for highlighted items.</summary>
public sealed class BoolToWeightConverter : IValueConverter
{
    public static readonly BoolToWeightConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? FontWeight.Bold : FontWeight.Normal;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>" (draft)" suffix.</summary>
public sealed class DraftConverter : IValueConverter
{
    public static readonly DraftConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? " (draft)" : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Connection test result color (green ok, red failed, neutral while unknown).</summary>
public sealed class ConnectionBrushConverter : IValueConverter
{
    public static readonly ConnectionBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            true => "SuccessBrush",
            false => "FailureBrush",
            _ => "SubtleTextBrush",
        };
        return Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var brush) ? brush : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
