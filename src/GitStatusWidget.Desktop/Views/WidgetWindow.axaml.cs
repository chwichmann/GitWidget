using Avalonia;
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
