namespace GitStatusWidget.Application.Platform;

/// <summary>Event data for <see cref="ISingleInstanceGuard.ActivationRequested"/>.</summary>
/// <param name="command">The requested action.</param>
public sealed class ActivationRequestedEventArgs(ActivationCommand command) : EventArgs
{
    /// <summary>The requested action.</summary>
    public ActivationCommand Command { get; } = command;
}
