using Avalonia.Controls;
using Avalonia.Input;

namespace GitStatusWidget.Desktop.Views;

public partial class ToastWindow : Window
{
    public ToastWindow()
    {
        InitializeComponent();
        Card.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                Clicked?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public event EventHandler? Clicked;

    public void PlayEnterAnimation() => Card.Classes.Add("toast-enter");
}
