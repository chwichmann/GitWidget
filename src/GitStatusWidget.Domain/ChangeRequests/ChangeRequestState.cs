namespace GitStatusWidget.Domain.ChangeRequests;

/// <summary>
/// Lifecycle state of a pull or merge request.
/// </summary>
public enum ChangeRequestState
{
    /// <summary>Open (including draft).</summary>
    Open = 1,

    /// <summary>Closed without merging.</summary>
    Closed = 2,

    /// <summary>Merged.</summary>
    Merged = 3,
}
