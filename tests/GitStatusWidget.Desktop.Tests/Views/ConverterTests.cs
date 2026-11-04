using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using GitStatusWidget.Desktop.Converters;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.Health;

namespace GitStatusWidget.Desktop.Tests.Views;

public sealed class ConverterTests
{
    private static readonly string[] Items = ["a", "b"];

    private static object? Convert(Avalonia.Data.Converters.IValueConverter converter, object? value) =>
        converter.Convert(value, typeof(object), null, CultureInfo.InvariantCulture);

    [AvaloniaFact]
    public void Every_build_and_health_state_has_a_glyph_and_a_theme_brush()
    {
        // Not color-only (architecture §17): each state has a glyph; each color comes from the theme.
        var values = Enum.GetValues<BuildState>().Cast<object>().Concat(Enum.GetValues<RepositoryHealthState>().Cast<object>());
        foreach (var value in values)
        {
            Convert(StateGlyphConverter.Instance, value).Should().BeOfType<string>().Which.Should().NotBeNullOrWhiteSpace();
            Convert(SemanticBrushConverter.Instance, value).Should().BeAssignableTo<IBrush>().And.NotBeSameAs(Brushes.Gray, $"{value} must resolve a theme brush");
        }
    }

    [AvaloniaFact]
    public void Semantic_colors_are_consistent()
    {
        var success = Convert(SemanticBrushConverter.Instance, BuildState.Succeeded);
        var failure = Convert(SemanticBrushConverter.Instance, BuildState.Failed);

        Convert(SemanticBrushConverter.Instance, RepositoryHealthState.Healthy).Should().BeSameAs(success);
        Convert(SemanticBrushConverter.Instance, ToastKind.Success).Should().BeSameAs(success);
        Convert(SemanticBrushConverter.Instance, RepositoryHealthState.AuthenticationFailed).Should().BeSameAs(failure);
        Convert(SemanticBrushConverter.Instance, ToastKind.Failure).Should().BeSameAs(failure);
        Convert(ConnectionBrushConverter.Instance, true).Should().BeSameAs(success);
        Convert(ConnectionBrushConverter.Instance, false).Should().BeSameAs(failure);
    }

    [AvaloniaFact]
    public void Simple_converters()
    {
        Convert(PercentConverter.Instance, 0.25).Should().Be(25d);
        Convert(PercentConverter.Instance, "x").Should().Be(0d);
        Convert(JoinConverter.Instance, Items).Should().Be("a, b");
        Convert(JoinConverter.Instance, null).Should().Be(string.Empty);
        Convert(DraftConverter.Instance, true).Should().Be(" (draft)");
        Convert(DraftConverter.Instance, false).Should().Be(string.Empty);
        Convert(BoolToWeightConverter.Instance, true).Should().Be(FontWeight.Bold);
        Convert(BoolToWeightConverter.Instance, null).Should().Be(FontWeight.Normal);
        Convert(LocalTimeConverter.Instance, new DateOnly(2026, 11, 1)).Should().Be("11/01/2026");
        Convert(LocalTimeConverter.Instance, null).Should().Be(string.Empty);
    }

    [AvaloniaFact]
    public void Hex_brush_converter_parses_colors_and_falls_back_to_gray()
    {
        Convert(HexBrushConverter.Instance, "#d73a4a").Should().BeOfType<SolidColorBrush>().Which.Color.Should().Be(Color.Parse("#d73a4a"));
        Convert(HexBrushConverter.Instance, "nonsense").Should().BeSameAs(Brushes.Gray);
    }
}
