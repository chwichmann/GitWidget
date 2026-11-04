using Avalonia.Headless.XUnit;

namespace GitStatusWidget.Desktop.Tests;

public sealed class SmokeTests
{
    [AvaloniaFact]
    public void Application_starts_headless() => Avalonia.Application.Current.Should().NotBeNull();
}
