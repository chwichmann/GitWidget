using System.Collections.ObjectModel;
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

    /// <summary>Milestones shown in the widget, one line each (issue #12); updated in place.</summary>
    public ObservableCollection<MilestoneLineViewModel> Milestones { get; } = [];





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

    /// <summary>Show the health summary as its own line (problem while data is shown, issue #10).</summary>
    [ObservableProperty]
    public partial bool ShowProblemLine { get; set; }

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

        UpdateMilestones(row.Milestones);

        HasBuild = row.LatestBuild is not null;
        BuildState = row.LatestBuild?.State ?? BuildState.Unknown;
        BuildText = row.LatestBuild is null ? string.Empty : $"{row.LatestBuild.Name}: {Describe(row.LatestBuild.State)}";

        Health = row.Health.State;
        HealthText = row.Health.Summary;
        HealthDetail = row.Health.Detail;
        HasProblem = row.Health.State is not (RepositoryHealthState.Healthy or RepositoryHealthState.Refreshing);
        ShowProblemLine = HasProblem && HasData;
        IsRefreshing = row.Health.IsRefreshing;
        LastSuccessAt = row.Health.LastSuccessAt;
        UpdateFreshness(now);
        OnPropertyChanged(nameof(ToolTipText));
    }

    private void UpdateMilestones(IReadOnlyList<MilestoneProgressProjection> milestones)
    {
        // Same algorithm as the rows: update matching lines in place, add new ones, move to order, remove the rest.
        for (var index = 0; index < milestones.Count; index++)
        {
            var projection = milestones[index];
            var line = Milestones.FirstOrDefault(m => m.ExternalId == projection.ExternalId);
            if (line is null)
            {
                line = new MilestoneLineViewModel(projection.ExternalId);
                Milestones.Insert(Math.Min(index, Milestones.Count), line);
            }
            else if (Milestones.IndexOf(line) != index)
            {
                Milestones.Move(Milestones.IndexOf(line), index);
            }

            line.Update(projection);
        }

        while (Milestones.Count > milestones.Count)
        {
            Milestones.RemoveAt(Milestones.Count - 1);
        }

        HasMilestone = Milestones.Count > 0;
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

/// <summary>One milestone line in a widget row (issue #12).</summary>
public sealed partial class MilestoneLineViewModel : ObservableObject
{
    public MilestoneLineViewModel(string externalId) => ExternalId = externalId;

    public string ExternalId { get; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsOverdue { get; set; }

    public void Update(MilestoneProgressProjection milestone)
    {
        ArgumentNullException.ThrowIfNull(milestone);
        Title = milestone.Title;
        Percent = Math.Round(milestone.Completion * 100, 1);
        Text = string.Create(CultureInfo.CurrentCulture, $"{milestone.ClosedIssues}/{milestone.TotalIssues} · {Format.Percent(milestone.CompletionPercent)}");
        IsOverdue = milestone.IsOverdue;
    }
}

/// <summary>Formatting helpers for relative times and percentages.</summary>
internal static class Format
{
    /// <summary>Culture-aware percentage such as "99 %" (de-CH) or "99%" (en-US); "–" when there is no value.</summary>
    public static string Percent(int? percent) =>
        percent is { } value ? (value / 100d).ToString("P0", CultureInfo.CurrentCulture) : "–";

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
