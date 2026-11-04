using Avalonia.Controls;

namespace GitStatusWidget.Desktop.Views;

public partial class DashboardWindow : Window
{
    public DashboardWindow() => InitializeComponent();

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
