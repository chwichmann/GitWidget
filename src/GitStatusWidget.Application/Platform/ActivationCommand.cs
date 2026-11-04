namespace GitStatusWidget.Application.Platform;

/// <summary>Request sent by a second application instance to the running one.</summary>
public enum ActivationCommand
{
    /// <summary>Show (and focus) the widget.</summary>
    Show,

    /// <summary>Open the expanded dashboard.</summary>
    OpenDashboard,
}
