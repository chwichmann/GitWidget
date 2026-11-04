namespace GitStatusWidget.Domain.Events;

/// <summary>
/// How prominently an event is presented.
/// </summary>
public enum EventPriority
{
    /// <summary>Activity feed only.</summary>
    Low = 1,

    /// <summary>Animated toast.</summary>
    Medium = 2,

    /// <summary>Prominent toast / persistent warning.</summary>
    High = 3,
}
