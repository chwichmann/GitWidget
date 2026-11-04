namespace GitStatusWidget.Domain.Issues;

/// <summary>
/// Open/closed state of an issue.
/// </summary>
public enum WorkItemState
{
    /// <summary>The item is open.</summary>
    Open = 1,

    /// <summary>The item is closed.</summary>
    Closed = 2,
}
