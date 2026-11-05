using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GitStatusWidget.Application.Projection;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.Health;
using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Desktop.ViewModels;

/// <summary>One repository row of the compact widget. Updated in place to avoid flicker.</summary>
public sealed partial class RepositoryRowViewModel : ObservableObject
{
    public RepositoryRowViewModel(Guid id) => Id = id;

    /// <summary>Raised to play a one-shot animation (the view decides whether motion is allowed).</summary>
    public event EventHandler<RowAnimation>? AnimationRequested;

    public Guid Id { get; }

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProviderBadge { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProviderName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasData { get; set; }

    [ObservableProperty]
    public partial int OpenIssues { get; set; }

    [ObservableProperty]
    public partial int ClosedIssues { get; set; }

    [ObservableProperty]
    public partial int ClosedToday { get; set; }

    /// <summary>Suffix after the closed-in-period count, e.g. " today" or " in 24 h" (issue #9).</summary>
    [ObservableProperty]
    public partial string PeriodSuffix { get; set; } = " today";

    [ObservableProperty]
    public partial string PeriodToolTip { get; set; } = "Issues closed today (since midnight)";

    [ObservableProperty]
    public partial bool HasMilestone { get; set; }

    [ObservableProperty]
    public partial string MilestoneTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double MilestonePercent { get; set; }

    [ObservableProperty]
    public partial string MilestoneText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool MilestoneOverdue { get; set; }

    [ObservableProperty]
    public partial bool HasBuild { get; set; }

    [ObservableProperty]
    public partial BuildState BuildState { get; set; }

    [ObservableProperty]
    public partial string BuildText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial RepositoryHealthState Health { get; set; }

    [ObservableProperty]
    public partial string HealthText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? HealthDetail { get; set; }

    [ObservableProperty]
    public partial bool HasProblem { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    public partial string FreshnessText { get; set; } = string.Empty;

    public DateTimeOffset? LastSuccessAt { get; private set; }

    public string ToolTipText => HealthDetail is null ? $"{DisplayName} – {HealthText}" : $"{DisplayName} – {HealthText}\n{HealthDetail}";

    public void Update(CompactRowProjection row, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);
        DisplayName = row.Header.DisplayName;
        ProviderBadge = row.Header.Provider == RepositoryProviderKind.GitLab ? "GL" : "GH";
        ProviderName = row.Header.Provider == RepositoryProviderKind.GitLab ? "GitLab" : "GitHub";
        HasData = row.HasData;
        OpenIssues = row.Summary.OpenIssues;
        ClosedIssues = row.Summary.ClosedIssues;
        ClosedToday = row.Summary.ClosedToday;
        var last24Hours = row.Summary.PeriodLabel == DashboardProjector.Last24HoursLabel;
        PeriodSuffix = last24Hours ? " in 24 h" : " today";
        PeriodToolTip = last24Hours ? "Issues closed in the last 24 hours" : "Issues closed today (since midnight)";

        var milestone = row.CurrentMilestone;
        HasMilestone = milestone is not null;
        MilestoneTitle = milestone?.Title ?? string.Empty;
        MilestonePercent = milestone is null ? 0 : Math.Round(milestone.Completion * 100, 1);
        MilestoneText = milestone is null ? string.Empty : string.Create(CultureInfo.CurrentCulture, $"{milestone.ClosedIssues}/{milestone.TotalIssues}");
        MilestoneOverdue = milestone?.IsOverdue ?? false;

        HasBuild = row.LatestBuild is not null;
        BuildState = row.LatestBuild?.State ?? BuildState.Unknown;
        BuildText = row.LatestBuild is null ? string.Empty : $"{row.LatestBuild.Name}: {Describe(row.LatestBuild.State)}";

        Health = row.Health.State;
        HealthText = row.Health.Summary;
        HealthDetail = row.Health.Detail;
        HasProblem = row.Health.State is not (RepositoryHealthState.Healthy or RepositoryHealthState.Refreshing);
        IsRefreshing = row.Health.IsRefreshing;
        LastSuccessAt = row.Health.LastSuccessAt;
        UpdateFreshness(now);
        OnPropertyChanged(nameof(ToolTipText));
    }

    public void UpdateFreshness(DateTimeOffset now) => FreshnessText = Format.Age(LastSuccessAt, now);

    public void Animate(RowAnimation animation) => AnimationRequested?.Invoke(this, animation);

    internal static string Describe(BuildState state) => state switch
    {
        BuildState.Succeeded => "passed",
        BuildState.Failed => "failed",
        BuildState.Running => "running",
        BuildState.Queued => "queued",
        BuildState.Canceled => "canceled",
        BuildState.Skipped => "skipped",
        _ => "unknown",
    };
}

/// <summary>Formatting helpers for relative times.</summary>
internal static class Format
{
    public static string Age(DateTimeOffset? instant, DateTimeOffset now)
    {
        if (instant is not { } value)
        {
            return "never updated";
        }

        var age = now - value;
        return age.TotalSeconds < 45 ? "just now"
            : age.TotalMinutes < 60 ? string.Create(CultureInfo.CurrentCulture, $"{Math.Max(1, (int)age.TotalMinutes)} min ago")
            : age.TotalHours < 24 ? string.Create(CultureInfo.CurrentCulture, $"{(int)age.TotalHours} h ago")
            : value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }
}
