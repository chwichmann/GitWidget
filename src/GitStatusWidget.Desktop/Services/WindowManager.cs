using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using GitStatusWidget.Application.Platform;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Desktop.Views;
using GitStatusWidget.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace GitStatusWidget.Desktop.Services;

/// <summary>Persisted window placement (architecture §16.5).</summary>
public sealed record WindowStateModel
{
    public int? WidgetX { get; init; }

    public int? WidgetY { get; init; }

    public string? WidgetScreen { get; init; }

    public bool WidgetVisible { get; init; } = true;

    public int? DashboardX { get; init; }

    public int? DashboardY { get; init; }

    public double DashboardWidth { get; init; } = 1000;

    public double DashboardHeight { get; init; } = 650;

    public bool DashboardMaximized { get; init; }
}

/// <summary>Creates, shows and hides windows; presents toasts; persists placement.</summary>
public sealed partial class WindowManager : IAppNavigator, IToastPresenter, IDisposable
{
    private const int MaxVisibleToasts = 3;
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(6);

    private readonly IServiceProvider _services;
    private readonly JsonDocumentStore<WindowStateModel> _stateStore;
    private readonly SettingsService _settings;
    private readonly IUrlLauncher _urls;
    private readonly MotionPreference _motion;
    private readonly List<ToastWindow> _toasts = [];
    private readonly Queue<ToastViewModel> _pendingToasts = new();
    private WidgetWindow? _widget;
    private DashboardWindow? _dashboard;
    private SettingsWindow? _settingsWindow;
    private WindowStateModel _state = new();
    private DispatcherTimer? _saveTimer;

    public WindowManager(IServiceProvider services, JsonDocumentStore<WindowStateModel> stateStore, SettingsService settings, IUrlLauncher urls, MotionPreference motion)
    {
        _services = services;
        _stateStore = stateStore;
        _settings = settings;
        _urls = urls;
        _motion = motion;
        _settings.SettingsChanged += (_, s) => Dispatcher.UIThread.Post(() => ApplySettings(s));
    }

    public async Task InitializeAsync()
    {
        _state = await _stateStore.LoadAsync(CancellationToken.None).ConfigureAwait(true) ?? new WindowStateModel();
        ApplySettings(_settings.Current);
        var settings = _settings.Current;
        if (settings.Repositories.Count == 0)
        {
            ShowWidget(activate: false);
            ShowSettings(addNew: true);
        }
        else if (settings.ShowWidgetOnStartup && _state.WidgetVisible)
        {
            ShowWidget(activate: false);
        }
    }

    public void ShowWidget(bool activate = true)
    {
        if (_widget is null)
        {
            var vm = _services.GetRequiredService<WidgetViewModel>();
            vm.AlwaysOnTop = _settings.Current.AlwaysOnTop;
            _widget = new WidgetWindow(_motion) { DataContext = vm, ShowActivated = activate };
            _widget.Position = InitialWidgetPosition(_widget);
            _widget.PositionChanged += (_, _) => ScheduleSave();
        }

        _widget.Show();
        if (activate)
        {
            _widget.Activate();
        }

        _state = _state with { WidgetVisible = true };
        ScheduleSave();
    }

    public void HideWidget()
    {
        _widget?.Hide();
        _state = _state with { WidgetVisible = false };
        ScheduleSave();
    }

    public void ToggleWidget()
    {
        if (_widget?.IsVisible == true)
        {
            HideWidget();
        }
        else
        {
            ShowWidget();
        }
    }

    public void ShowDashboard(Guid? repositoryId = null)
    {
        if (_dashboard is null)
        {
            _dashboard = new DashboardWindow { DataContext = _services.GetRequiredService<DashboardViewModel>() };
            if (_state.DashboardX is { } x && _state.DashboardY is { } y)
            {
                _dashboard.WindowStartupLocation = WindowStartupLocation.Manual;
                _dashboard.Width = _state.DashboardWidth;
                _dashboard.Height = _state.DashboardHeight;
                _dashboard.Position = Clamp(_dashboard, new PixelPoint(x, y), _state.DashboardWidth, _state.DashboardHeight, null);
            }

            if (_state.DashboardMaximized)
            {
                _dashboard.WindowState = WindowState.Maximized;
            }

            _dashboard.PositionChanged += (_, _) => ScheduleSave();
            _dashboard.Resized += (_, _) => ScheduleSave();
        }

        ((DashboardViewModel)_dashboard.DataContext!).Select(repositoryId);
        _dashboard.Show();
        _dashboard.Activate();
    }

    public void ShowSettings(Guid? repositoryId = null, bool addNew = false)
    {
        if (_settingsWindow is null)
        {
            var vm = _services.GetRequiredService<SettingsViewModel>();
            _settingsWindow = new SettingsWindow { DataContext = vm };
            vm.CloseRequested += (_, _) => _settingsWindow?.Close();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        ((SettingsViewModel)_settingsWindow.DataContext!).Select(repositoryId, addNew);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void OpenInBrowser(Uri? uri)
    {
        if (uri is null)
        {
            return;
        }

        var allowHttp = _settings.Current.Repositories.Any(r => r.AllowInsecureHttp && string.Equals(r.ServerUri.Host, uri.Host, StringComparison.OrdinalIgnoreCase));
        _urls.TryOpen(uri, allowHttp);
    }

    public void Show(ToastViewModel toast)
    {
        if (_toasts.Count >= MaxVisibleToasts)
        {
            _pendingToasts.Enqueue(toast);
            return;
        }

        var window = new ToastWindow { DataContext = toast };
        var screen = ReferenceScreen(window);
        window.Opened += (_, _) =>
        {
            if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle() is { } handle)
            {
                NativeMethods.MakeNonActivatingToolWindow(handle.Handle);
            }

            if (!_motion.ReduceMotion)
            {
                window.PlayEnterAnimation();
            }

            LayoutToasts(screen);
        };
        window.Clicked += (_, _) =>
        {
            OpenInBrowser(toast.Url);
            window.Close();
        };
        window.Closed += (_, _) =>
        {
            _toasts.Remove(window);
            LayoutToasts(screen);
            if (_pendingToasts.TryDequeue(out var next))
            {
                Show(next);
            }
        };
        _toasts.Add(window);
        window.Position = new PixelPoint(screen?.WorkingArea.Right ?? 0, screen?.WorkingArea.Bottom ?? 0);
        window.Show();
        DispatcherTimer.RunOnce(window.Close, ToastDuration);
    }

    public void ExitApplication()
    {
        SaveNow();
        foreach (var toast in _toasts.ToList())
        {
            toast.Close();
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    public void SaveNow()
    {
        CaptureState();
        _stateStore.SaveAsync(_state, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose() => _saveTimer?.Stop();

    private void ApplySettings(AppSettings settings)
    {
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = settings.Theme switch
            {
                "Light" => ThemeVariant.Light,
                "Dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }

        if (_widget?.DataContext is WidgetViewModel vm)
        {
            vm.AlwaysOnTop = settings.AlwaysOnTop;
        }
    }

    private PixelPoint InitialWidgetPosition(Window window)
    {
        if (_state.WidgetX is { } x && _state.WidgetY is { } y)
        {
            return Clamp(window, new PixelPoint(x, y), window.Width, 240, _state.WidgetScreen);
        }

        var screen = window.Screens.Primary ?? FirstScreen(window.Screens);
        if (screen is null)
        {
            return new PixelPoint(40, 40);
        }

        var area = screen.WorkingArea;
        var width = (int)Math.Ceiling((window.Width + 16) * screen.Scaling);
        return new PixelPoint(area.Right - width, area.Y + (int)(16 * screen.Scaling));
    }

    /// <summary>Keeps a window fully visible on a currently available working area.</summary>
    private static PixelPoint Clamp(Window window, PixelPoint wanted, double widthDip, double heightDip, string? screenName)
    {
        var screens = window.Screens;
        var target = screens.All.FirstOrDefault(s => s.DisplayName == screenName && s.WorkingArea.Contains(wanted))
            ?? screens.ScreenFromPoint(wanted)
            ?? screens.Primary
            ?? FirstScreen(screens);
        if (target is null)
        {
            return wanted;
        }

        var area = target.WorkingArea;
        var width = (int)Math.Ceiling(widthDip * target.Scaling);
        var height = (int)Math.Ceiling(heightDip * target.Scaling);
        return new PixelPoint(
            Math.Clamp(wanted.X, area.X, Math.Max(area.X, area.Right - width)),
            Math.Clamp(wanted.Y, area.Y, Math.Max(area.Y, area.Bottom - height)));
    }

    private Screen? ReferenceScreen(Window window) =>
        (_widget is { IsVisible: true } widget ? widget.Screens.ScreenFromWindow(widget) : null)
        ?? window.Screens.Primary
        ?? FirstScreen(window.Screens);

    private static Screen? FirstScreen(Screens screens) => screens.All.Count > 0 ? screens.All[0] : null;

    private void LayoutToasts(Screen? screen)
    {
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var bottom = area.Bottom - (int)(12 * scale);
        foreach (var toast in _toasts)
        {
            var width = (int)Math.Ceiling(toast.Bounds.Width * scale);
            var height = (int)Math.Ceiling(Math.Max(toast.Bounds.Height, 60) * scale);
            bottom -= height;
            toast.Position = new PixelPoint(area.Right - width - (int)(12 * scale), bottom);
            bottom -= (int)(8 * scale);
        }
    }

    private void ScheduleSave()
    {
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            CaptureState();
            _ = _stateStore.SaveAsync(_state, CancellationToken.None);
        });
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void CaptureState()
    {
        if (_widget is not null)
        {
            _state = _state with
            {
                WidgetX = _widget.Position.X,
                WidgetY = _widget.Position.Y,
                WidgetScreen = _widget.Screens.ScreenFromWindow(_widget)?.DisplayName,
            };
        }

        if (_dashboard is not null)
        {
            var maximized = _dashboard.WindowState == WindowState.Maximized;
            _state = _state with
            {
                DashboardMaximized = maximized,
                DashboardX = maximized ? _state.DashboardX : _dashboard.Position.X,
                DashboardY = maximized ? _state.DashboardY : _dashboard.Position.Y,
                DashboardWidth = maximized ? _state.DashboardWidth : _dashboard.Width,
                DashboardHeight = maximized ? _state.DashboardHeight : _dashboard.Height,
            };
        }
    }

    [SupportedOSPlatform("windows")]
    private static partial class NativeMethods
    {
        private const int GwlExStyle = -20;
        private const long WsExToolWindow = 0x00000080;
        private const long WsExNoActivate = 0x08000000;

        public static void MakeNonActivatingToolWindow(nint hwnd)
        {
            var style = GetWindowLongPtr(hwnd, GwlExStyle);
            SetWindowLongPtr(hwnd, GwlExStyle, (nint)(style | WsExNoActivate | WsExToolWindow));
        }

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    }
}
