using Avalonia.Threading;

namespace GitStatusWidget.Desktop.Services;

/// <summary><see cref="IUiDispatcher"/> backed by the Avalonia dispatcher.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
