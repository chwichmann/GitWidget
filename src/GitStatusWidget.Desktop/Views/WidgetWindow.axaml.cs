using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitStatusWidget.Desktop.Controls;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Desktop.ViewModels;

namespace GitStatusWidget.Desktop.Views;

public partial class WidgetWindow : Window
{
    private readonly MotionPreference? _motion;
    private readonly DragReorder _reorder;
    private WidgetViewModel? _observed;
    private bool _translucent;

    public WidgetWindow()
    {
        InitializeComponent();
        DragArea.PointerPressed += OnDragPointerPressed;

        // Rows can be dragged to reorder repositories; the new order is saved on drop.
        _reorder = new DragReorder(
            RowsList,
            (from, to) => (DataContext as WidgetViewModel)?.MoveRow(from, to),
            async _ =>
            {
                if (DataContext is WidgetViewModel vm)
                {
                    await vm.CommitReorderAsync().ConfigureAwait(true);
                }
            });
        _reorder.DragStarted += (_, _) => (DataContext as WidgetViewModel)?.BeginReorder();
        RowsList.ContainerPrepared += OnRowPrepared;
        RowsList.ContainerClearing += OnRowClearing;
    }

    public WidgetWindow(MotionPreference motion)
        : this() => _motion = motion;

    /// <summary>Duration of the fade between idle and mouse-over opacity (issue #15).</summary>
    public static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(150);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _observed = DataContext as WidgetViewModel;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnViewModelPropertyChanged;
        }

        ApplyOpacity(animate: false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPointerOverProperty)
        {
            if (IsPointerOver && _observed is not null)
            {
                // The mouse is back on the widget: stop pinning the previewed value so mouse-over can be tried.
                _observed.PreviewOpacity = null;
            }

            ApplyOpacity(animate: true);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WidgetViewModel.IdleOpacity) or nameof(WidgetViewModel.HoverOpacity) or nameof(WidgetViewModel.PreviewOpacity))
        {
            ApplyOpacity(animate: true);
        }
    }

    /// <summary>
    /// Makes the background fill translucent while the mouse is elsewhere and less so while it is over the widget
    /// (issue #15). Only the fill fades; the content stays fully opaque and readable.
    /// </summary>
    private void ApplyOpacity(bool animate)
    {
        if (WidgetBackground is null)
        {
            return;
        }

        var target = _observed is null
            ? 1
            : _observed.PreviewOpacity ?? (IsPointerOver ? _observed.HoverOpacity : _observed.IdleOpacity);
        WidgetBackground.Transitions = animate && _motion?.ReduceMotion != true
            ? [new DoubleTransition { Property = OpacityProperty, Duration = FadeDuration }]
            : null;
        WidgetBackground.Opacity = target;

        // With a translucent fill the desktop itself should show through: the blur material behind the window is
        // light and would turn a dark widget gray. Blur stays for the opaque (default) widget.
        var translucent = _observed is not null && (_observed.IdleOpacity < 1 || _observed.PreviewOpacity < 1);
        if (translucent != _translucent)
        {
            _translucent = translucent;
            TransparencyLevelHint = translucent
                ? [WindowTransparencyLevel.Transparent]
                : [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent];
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // User close hides to tray; only application/OS shutdown really closes (architecture §16.5).
        if (e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void OnDragPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnRowPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container.DataContext is RepositoryRowViewModel row)
        {
            row.AnimationRequested += OnAnimationRequested;
            e.Container.Tapped += OnRowTapped;
        }
    }

    private void OnRowClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container.DataContext is RepositoryRowViewModel row)
        {
            row.AnimationRequested -= OnAnimationRequested;
            e.Container.Tapped -= OnRowTapped;
        }
    }

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (_reorder.JustDragged)
        {
            return;
        }

        if (sender is Control { DataContext: RepositoryRowViewModel row } && DataContext is WidgetViewModel vm)
        {
            vm.OpenDashboardCommand.Execute(row);
        }
    }

    private async void OnAnimationRequested(object? sender, RowAnimation animation)
    {
        if (_motion?.ReduceMotion == true || !IsVisible || sender is not RepositoryRowViewModel row)
        {
            return;
        }

        var container = RowsList.ContainerFromItem(row);
        if (container is null)
        {
            return;
        }

        var (targetName, cssClass) = animation switch
        {
            RowAnimation.IssueClosed => ("ClosedPulse", "anim-closed"),
            RowAnimation.IssueOpened => ("OpenPulse", "anim-opened"),
            _ => ("BuildPulse", "anim-failed"),
        };
        var target = container.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == targetName);
        if (target is null)
        {
            return;
        }

        var check = animation == RowAnimation.IssueClosed
            ? container.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "ClosedCheck")
            : null;
        target.Classes.Remove(cssClass);
        target.Classes.Add(cssClass);
        if (check is not null)
        {
            check.IsVisible = true;
            check.Classes.Add("anim-check");
        }

        await Task.Delay(TimeSpan.FromSeconds(1.2)).ConfigureAwait(true);
        target.Classes.Remove(cssClass);
        if (check is not null)
        {
            check.Classes.Remove("anim-check");
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
            check.IsVisible = false;
        }
    }

}
