namespace GitStatusWidget.Application.Platform;

/// <summary>
/// Per-user single-instance ownership. The primary instance receives activation requests from
/// instances started later (which then exit).
/// </summary>
public interface ISingleInstanceGuard
{
    /// <summary>Whether this process owns the single-instance lock.</summary>
    bool IsPrimary { get; }

    /// <summary>
    /// Raised in the primary instance when another instance asks it to show itself.
    /// Raised on a thread-pool thread: marshal to the UI dispatcher before touching windows.
    /// </summary>
    event EventHandler<ActivationRequestedEventArgs>? ActivationRequested;
}
