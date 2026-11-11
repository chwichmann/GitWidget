using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitStatusWidget.Application.Usage;
using GitStatusWidget.Desktop.Services;

namespace GitStatusWidget.Desktop.ViewModels;

/// <summary>
/// Usage of the Claude subscription (issue #17): compact bars in the widget footer and the full display that opens
/// on click. Both views share this instance.
/// </summary>
public sealed partial class ClaudeUsageViewModel : ObservableObject, IDisposable
{
    private readonly ClaudeUsageMonitor? _monitor;
    private readonly IUiDispatcher _ui;
    private readonly IAppNavigator _navigator;
    private readonly TimeProvider _time;
    private ClaudeUsageState _state = new(ClaudeUsageStatus.Disabled, null, null, null);

    public ClaudeUsageViewModel(ClaudeUsageMonitor? monitor, IUiDispatcher ui, IAppNavigator navigator, TimeProvider time)
    {
        _monitor = monitor;
        _ui = ui;
        _navigator = navigator;
        _time = time;
        if (_monitor is not null)
        {
            _monitor.StateChanged += OnStateChanged;
            _state = _monitor.State;
        }

        Apply();
    }

    /// <summary>One entry per reported limit: session, weekly, model limits.</summary>
    public ObservableCollection<ClaudeUsageLimitViewModel> Limits { get; } = [];

    /// <summary>The usage footer is switched on in Settings.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial bool HasLimits { get; set; }

    /// <summary>Hint shown instead of or below the bars: loading, not signed in, rate limited, unavailable.</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasStatus { get; set; }

    /// <summary>The status is a problem (the bars, if any, show older values).</summary>
    [ObservableProperty]
    public partial bool IsProblem { get; set; }

    /// <summary>Subscription type, for example "Max plan"; empty when unknown.</summary>
    [ObservableProperty]
    public partial string PlanText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasPlan { get; set; }

    /// <summary>Age of the shown values, for example "Updated 2 min ago".</summary>
    [ObservableProperty]
    public partial string UpdatedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasUpdated { get; set; }

    /// <summary>Opens the full display.</summary>
    [RelayCommand]
    private void OpenDetails() => _navigator.ShowClaudeUsage();

    /// <summary>Polls now (the monitor ignores requests that follow each other too closely).</summary>
    [RelayCommand]
    public void Refresh() => _monitor?.RequestRefresh();

    /// <summary>Recomputes the relative times ("Resets in …", "Updated … ago").</summary>
    public void UpdateFreshness() => Apply();

    public void Dispose()
    {
        if (_monitor is not null)
        {
            _monitor.StateChanged -= OnStateChanged;
        }
    }

    private void OnStateChanged(object? sender, ClaudeUsageState state) => _ui.Post(() =>
    {
        _state = state;
        Apply();
    });

    private void Apply()
    {
        var now = _time.GetUtcNow();
        var state = _state;
        var limits = state.Snapshot?.Limits ?? [];

        IsEnabled = state.Status != ClaudeUsageStatus.Disabled;
        if (Limits.Count == limits.Count)
        {
            // Same rows: update in place so the views keep their controls.
            for (var i = 0; i < limits.Count; i++)
            {
                Limits[i].Update(limits[i], now);
            }
        }
        else
        {
            Limits.Clear();
            foreach (var limit in limits)
            {
                var row = new ClaudeUsageLimitViewModel();
                row.Update(limit, now);
                Limits.Add(row);
            }
        }

        HasLimits = Limits.Count > 0;
        StatusText = state.Status switch
        {
            ClaudeUsageStatus.Disabled => "The Claude usage is switched off in Settings.",
            ClaudeUsageStatus.Pending => "Loading the Claude usage…",
            ClaudeUsageStatus.Ok => HasLimits ? string.Empty : "Claude reports no usage limits for this account.",
            _ => state.Message ?? "Claude usage is unavailable",
        };
        HasStatus = StatusText.Length > 0;
        IsProblem = state.Status is ClaudeUsageStatus.SignedOut or ClaudeUsageStatus.RateLimited or ClaudeUsageStatus.Unavailable;

        var plan = state.Snapshot?.Plan?.Trim();
        PlanText = string.IsNullOrEmpty(plan) ? string.Empty : $"{char.ToUpper(plan[0], CultureInfo.CurrentCulture)}{plan[1..]} plan";
        HasPlan = PlanText.Length > 0;

        UpdatedText = state.Snapshot is { } snapshot ? $"Updated {Format.Age(snapshot.FetchedAt, now)}" : string.Empty;
        HasUpdated = UpdatedText.Length > 0;
    }
}

/// <summary>One usage limit as a bar with texts.</summary>
public sealed partial class ClaudeUsageLimitViewModel : ObservableObject
{
    /// <summary>Usage from which the bar is shown as a warning, in percent.</summary>
    public const double WarningPercent = 75;

    /// <summary>Usage from which the bar is shown as critical, in percent.</summary>
    public const double CriticalPercent = 90;

    /// <summary>Short label for the footer: "Session", "Weekly" or the model name.</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>Label for the full display, for example "Weekly limit · all models".</summary>
    [ObservableProperty]
    public partial string DetailTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Percent { get; set; }

    /// <summary>Culture-aware percentage, for example "58 %".</summary>
    [ObservableProperty]
    public partial string PercentText { get; set; } = string.Empty;

    /// <summary>For example "58 % used".</summary>
    [ObservableProperty]
    public partial string UsedText { get; set; } = string.Empty;

    /// <summary>For example "Resets in 2 hr 10 min" or "Resets Mon 20:00"; empty when the reset time is unknown.</summary>
    [ObservableProperty]
    public partial string ResetText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasReset { get; set; }

    [ObservableProperty]
    public partial bool IsWarning { get; set; }

    [ObservableProperty]
    public partial bool IsCritical { get; set; }

    [ObservableProperty]
    public partial string ToolTip { get; set; } = string.Empty;

    public void Update(ClaudeUsageLimit limit, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(limit);
        (Title, DetailTitle) = limit.Kind switch
        {
            ClaudeUsageLimitKind.Session => ("Session", "Current session (5 hours)"),
            ClaudeUsageLimitKind.Weekly => ("Weekly", "Weekly limit · all models"),
            _ => (limit.Model ?? "Model", $"Weekly limit · {limit.Model ?? "model"}"),
        };
        Percent = limit.Percent;
        PercentText = Format.Percent((int)Math.Round(limit.Percent, MidpointRounding.AwayFromZero));
        UsedText = $"{PercentText} used";
        ResetText = FormatReset(limit.ResetsAt, now);
        HasReset = ResetText.Length > 0;
        IsCritical = limit.Percent >= CriticalPercent;
        IsWarning = !IsCritical && limit.Percent >= WarningPercent;
        ToolTip = HasReset ? $"{DetailTitle}: {UsedText} · {ResetText}" : $"{DetailTitle}: {UsedText}";
    }

    /// <summary>Relative text within a day ("Resets in 2 hr 10 min"), weekday and local time beyond that.</summary>
    internal static string FormatReset(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } reset)
        {
            return string.Empty;
        }

        var remaining = reset - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "Resets any moment";
        }

        if (remaining < TimeSpan.FromMinutes(1))
        {
            return "Resets in less than a minute";
        }

        if (remaining < TimeSpan.FromHours(1))
        {
            return string.Create(CultureInfo.CurrentCulture, $"Resets in {(int)remaining.TotalMinutes} min");
        }

        if (remaining < TimeSpan.FromHours(24))
        {
            return string.Create(CultureInfo.CurrentCulture, $"Resets in {(int)remaining.TotalHours} hr {remaining.Minutes} min");
        }

        // "t" is only the short time pattern when it stands alone, so weekday and time are formatted separately.
        var local = reset.ToLocalTime();
        return $"Resets {local.ToString("ddd", CultureInfo.CurrentCulture)} {local.ToString("t", CultureInfo.CurrentCulture)}";
    }
}
