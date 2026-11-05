using GitStatusWidget.Domain.Events;

namespace GitStatusWidget.Application.Settings;

/// <summary>What "today" means for the closed/opened counters (issue #9).</summary>
public enum DailyCountMode
{
    /// <summary>Since local midnight (calendar day in the user's time zone).</summary>
    CalendarDay = 0,

    /// <summary>Rolling window of the last 24 hours.</summary>
    Last24Hours = 1,
}

/// <summary>
/// Application-wide user settings (persisted as <c>settings.json</c>). Contains no secrets.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Monitored repositories.</summary>
    public IReadOnlyList<RepositoryConfiguration> Repositories { get; init; } = [];

    /// <summary>Show the compact widget when the application starts.</summary>
    public bool ShowWidgetOnStartup { get; init; } = true;

    /// <summary>Keep the widget above other windows.</summary>
    public bool AlwaysOnTop { get; init; }

    /// <summary>Start the application at login (mirrors the OS registration).</summary>
    public bool StartAtLogin { get; init; }

    /// <summary>Show toast notifications.</summary>
    public bool ToastsEnabled { get; init; } = true;

    /// <summary>Lowest priority that produces a toast; lower priorities go to the activity feed only.</summary>
    public EventPriority MinimumToastPriority { get; init; } = EventPriority.Medium;

    /// <summary>Closed issues and change requests updated within this window are kept for "recently closed".</summary>
    public TimeSpan RecentlyClosedWindow { get; init; } = TimeSpan.FromDays(14);

    /// <summary>Maximum number of repositories refreshed concurrently.</summary>
    public int MaxConcurrentRefreshes { get; init; } = 2;

    /// <summary>Period of the "closed today" / "opened today" counters (issue #9).</summary>
    public DailyCountMode DailyCountMode { get; init; } = DailyCountMode.CalendarDay;

    /// <summary>Check for newer GitWidget releases (issue #7).</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Offer pre-releases as updates.</summary>
    public bool IncludePrereleaseUpdates { get; init; } = true;

    /// <summary>Theme: <c>System</c>, <c>Light</c> or <c>Dark</c>.</summary>
    public string Theme { get; init; } = "System";

    /// <summary>Returns a copy with all values clamped to their valid ranges (applied when loading).</summary>
    /// <returns>The normalized settings.</returns>
    public AppSettings Normalize() => this with { Repositories = Repositories.Select(r => r.Normalize()).ToList() };
}
