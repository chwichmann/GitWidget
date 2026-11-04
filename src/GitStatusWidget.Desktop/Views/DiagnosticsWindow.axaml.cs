using Avalonia.Controls;
using Avalonia.Input.Platform;
using GitStatusWidget.Desktop.ViewModels;

namespace GitStatusWidget.Desktop.Views;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DiagnosticsViewModel vm)
            {
                vm.CopyRequested += async (_, text) =>
                {
                    if (Clipboard is { } clipboard)
                    {
                        await clipboard.SetTextAsync(text).ConfigureAwait(true);
                    }
                };
            }
        };
    }
}
