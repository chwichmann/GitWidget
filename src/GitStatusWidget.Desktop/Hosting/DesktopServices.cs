using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Comparison;
using GitStatusWidget.Application.Persistence;
using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Infrastructure.Credentials;
using GitStatusWidget.Infrastructure.Logging;
using GitStatusWidget.Infrastructure.Persistence;
using GitStatusWidget.Infrastructure.Platform;
using GitStatusWidget.Infrastructure.Providers.GitHub;
using GitStatusWidget.Infrastructure.Providers.GitLab;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Desktop.Hosting;

/// <summary>Composition root (architecture §25 step 2).</summary>
internal static class DesktopServices
{
    public static HostApplicationBuilder Configure(HostApplicationBuilder builder, AppPaths paths, bool autostarted = false)
    {
        builder.Logging.ClearProviders();
#if DEBUG
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddDebug();
#else
        builder.Logging.SetMinimumLevel(LogLevel.Information);
#endif
        builder.Logging.AddProvider(new FileLoggerProvider(paths.LogsDirectory,
#if DEBUG
            LogLevel.Debug));
#else
            LogLevel.Information));
#endif
        // HttpClient factory logs full request URLs at Information; keep them out of the file log.
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

        var services = builder.Services;
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);

        // Infrastructure
        services.AddPlatformServices();
        services.AddGitHubProvider();
        services.AddGitLabProvider();
        services.AddSingleton<IRepositoryCache, JsonRepositoryCache>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton(sp => new JsonDocumentStore<WindowStateModel>(
            paths.WindowStateFile, 1, sp.GetRequiredService<ILoggerFactory>().CreateLogger("WindowState")));

        // Application
        services.AddSingleton<IRepositoryStatusProviderResolver, RepositoryStatusProviderResolver>();
        services.AddSingleton<ISnapshotComparer, SnapshotComparer>();
        services.AddSingleton<RepositoryStatusHub>();
        services.AddSingleton<IRepositoryStatusHub>(sp => sp.GetRequiredService<RepositoryStatusHub>());
        services.AddSingleton(sp => new SynchronizationEngine(
            sp.GetRequiredService<IRepositoryStatusProviderResolver>(),
            sp.GetRequiredService<IRepositoryCache>(),
            sp.GetRequiredService<ISnapshotComparer>(),
            sp.GetRequiredService<RepositoryStatusHub>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<SynchronizationEngine>>()));
        services.AddSingleton<ISynchronizationController>(sp => sp.GetRequiredService<SynchronizationEngine>());
        services.AddHostedService(sp => new SynchronizationService(
            sp.GetRequiredService<SynchronizationEngine>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<SynchronizationService>>(),
            autostarted ? TimeSpan.FromSeconds(20) : TimeSpan.Zero));
        services.AddSingleton<DashboardProjector>();

        // Desktop
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<MotionPreference>();
        services.AddSingleton<WindowManager>();
        services.AddSingleton<IAppNavigator>(sp => sp.GetRequiredService<WindowManager>());
        services.AddSingleton<ActivityFeed>();
        services.AddSingleton<DiagnosticsLog>();
        services.AddTransient<DiagnosticsViewModel>();
        services.AddSingleton(sp => new ToastService(
            sp.GetRequiredService<IRepositoryStatusHub>(),
            sp.GetRequiredService<ISynchronizationController>(),
            sp.GetRequiredService<IUiDispatcher>(),
            () => sp.GetRequiredService<WindowManager>()));
        services.AddSingleton<TrayViewModel>();
        services.AddSingleton<WidgetViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddTransient<SettingsViewModel>();
        return builder;
    }
}
