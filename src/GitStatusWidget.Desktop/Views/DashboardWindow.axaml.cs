using Avalonia.Controls;
using GitStatusWidget.Desktop.Controls;
using GitStatusWidget.Desktop.ViewModels;

namespace GitStatusWidget.Desktop.Views;

public partial class DashboardWindow : Window
{
    private readonly DragReorder _reorder;

    public DashboardWindow()
    {
        InitializeComponent();
        _reorder = new DragReorder(
            RepositoryList,
            (from, to) => (DataContext as DashboardViewModel)?.MoveRepository(from, to),
            async _ =>
            {
                if (DataContext is DashboardViewModel vm)
                {
                    await vm.CommitReorderAsync().ConfigureAwait(true);
                }
            });
        _reorder.DragStarted += (_, _) => (DataContext as DashboardViewModel)?.BeginReorder();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
