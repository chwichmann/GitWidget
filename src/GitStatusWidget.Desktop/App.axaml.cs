using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GitStatusWidget.Application.Platform;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Infrastructure.Platform;
using GitStatusWidget.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GitStatusWidget.Desktop;

/// <summary>Avalonia application root. Runs without a main window; the tray icon owns the lifetime.</summary>
public partial class App : Avalonia.Application
{
    /// <summary>Service provider handed over by <see cref="Program"/> (null in headless tests).</summary>
    internal static IServiceProvider? Services { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override async void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || Services is null)
        {
            return;
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var tray = Services.GetRequiredService<TrayViewModel>();
        DataContext = tray;
        tray.RepositoriesChanged += (_, _) => RebuildRepositoryMenu(tray);
        RebuildRepositoryMenu(tray);

        // Instantiate notification services so they subscribe before the first refresh.
        Services.GetRequiredService<ActivityFeed>();
        Services.GetRequiredService<ToastService>();

        var windows = Services.GetRequiredService<WindowManager>();
        desktop.ShutdownRequested += (_, _) => windows.SaveNow();

        // SIGTERM / console close stop the host (and polling); the UI must not stay behind without data.
        Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
            Dispatcher.UIThread.Post(windows.ExitApplication));
        if (Services.GetService<ISingleInstanceGuard>() is SingleInstanceCoordinator instance)
        {
            // Subscribe first, then listen; the event is raised on a thread-pool thread.
            instance.ActivationRequested += (_, e) => Dispatcher.UIThread.Post(() =>
            {
                if (e.Command == ActivationCommand.OpenDashboard)
                {
                    windows.ShowDashboard();
                }
                else
                {
                    windows.ShowWidget();
                }
            });
            instance.StartListening();
        }
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                {
                    windows.ShowWidget();
                }
            };
        }

        await windows.InitializeAsync().ConfigureAwait(true);
    }

    private void RebuildRepositoryMenu(TrayViewModel tray)
    {
        var icon = TrayIcon.GetIcons(this)?.FirstOrDefault();
        var item = icon?.Menu?.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Repositories");
        if (item is null)
        {
            return;
        }

        var menu = item.Menu ??= new NativeMenu();
        menu.Items.Clear();
        foreach (var (id, name, status) in tray.Repositories)
        {
            menu.Items.Add(new NativeMenuItem($"{name} – {status}")
            {
                Command = tray.OpenDashboardCommand,
                CommandParameter = id,
            });
        }

        if (tray.Repositories.Count == 0)
        {
            menu.Items.Add(new NativeMenuItem("No repositories configured") { IsEnabled = false });
        }
    }
}
