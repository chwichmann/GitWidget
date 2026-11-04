using Avalonia;
using Avalonia.Controls;

namespace GitStatusWidget.Desktop;

internal static class Program
{
    /// <summary>
    /// Application entry point. Avalonia must not be used before this method runs.
    /// </summary>
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

    /// <summary>
    /// Avalonia configuration; also used by the visual designer and headless tests.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
