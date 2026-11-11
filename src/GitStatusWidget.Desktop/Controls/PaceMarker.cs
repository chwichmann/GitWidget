using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GitStatusWidget.Desktop.Controls;

/// <summary>
/// Thin vertical tick laid over a usage bar at <see cref="Position"/> percent of its width: where even consumption
/// would be now (issue #18).
/// </summary>
public sealed class PaceMarker : Control
{
    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<PaceMarker, double>(nameof(Position));

    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<PaceMarker, IBrush?>(nameof(Brush));

    private const double Thickness = 2;

    static PaceMarker()
    {
        AffectsRender<PaceMarker>(PositionProperty, BrushProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<PaceMarker>(false);
    }

    /// <summary>Position in percent of the width, 0 to 100.</summary>
    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public IBrush? Brush
    {
        get => GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Brush is null || Bounds.Width <= Thickness || !double.IsFinite(Position))
        {
            return;
        }

        var x = Math.Clamp(Position, 0, 100) / 100 * (Bounds.Width - Thickness);
        context.FillRectangle(Brush, new Rect(x, 0, Thickness, Bounds.Height));
    }
}
