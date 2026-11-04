using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(GitStatusWidget.Desktop.Tests.TestAppBuilder))]

namespace GitStatusWidget.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
