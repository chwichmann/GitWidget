using Avalonia.Controls;
using Avalonia.Input;

namespace GitStatusWidget.Desktop.Views;

/// <summary>Full display of the Claude usage (issue #17).</summary>
public partial class UsageWindow : Window
{
    public UsageWindow() => InitializeComponent();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled)
        {
            e.Handled = true;
            Close();
        }
    }
}
