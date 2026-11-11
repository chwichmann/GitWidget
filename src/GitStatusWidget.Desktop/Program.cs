using Avalonia;
using Avalonia.Controls;
using GitStatusWidget.Application.Platform;
using GitStatusWidget.Desktop.Hosting;
using GitStatusWidget.Infrastructure.Persistence;
using GitStatusWidget.Infrastructure.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GitStatusWidget.Desktop;

internal static class Program
{
    /// <summary>
    /// Entry point (architecture §25): single instance, generic host, Avalonia UI thread, bounded shutdown (§26).
    /// Main stays synchronous so the UI thread keeps its STA apartment on Windows.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // In-place update (issue #19): a version started by the updater waits for its predecessor, then cleans up.
        GitStatusWidget.Infrastructure.Updates.UpdateServiceCollectionExtensions.CompletePendingUpdate(args, AppContext.BaseDirectory);

        // Step 1: single instance. A second launch asks the running instance to show itself and exits.
        using var instance = new SingleInstanceCoordinator();
        if (!instance.IsPrimary)
        {
            var command = args.Contains("--dashboard") ? ActivationCommand.OpenDashboard : ActivationCommand.Show;
            return instance.SignalPrimary(command, TimeSpan.FromSeconds(3)) ? 0 : 1;
        }

        var paths = AppPaths.CreateDefault();
        var builder = DesktopServices.Configure(Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        }), paths, autostarted: args.Contains("--autostart"));
        builder.Services.AddSingleton<ISingleInstanceGuard>(instance);

        using var host = builder.Build();
        App.Services = host.Services;
        host.StartAsync().GetAwaiter().GetResult();
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }

    /// <summary>Avalonia configuration; also used by the previewer and headless tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .WithInterFont()
            .LogToTrace();
}
