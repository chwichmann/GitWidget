using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Application.Settings;

/// <summary>How the milestones shown in the compact widget are chosen (issue #12).</summary>
public enum MilestoneSelectionMode
{
    /// <summary>Up to <see cref="RepositoryConfiguration.AutomaticMilestoneCount"/> milestones chosen by due date and activity.</summary>
    Automatic = 0,

    /// <summary>The milestones in <see cref="RepositoryConfiguration.SelectedMilestoneIds"/>, in that order.</summary>
    Manual = 1,
}

/// <summary>
/// User configuration of one monitored repository. Contains no secrets: the token lives in the OS
/// credential store under <see cref="CredentialReference"/>.
/// </summary>
/// <param name="Id">Local configuration id (also used for the cache folder and credential key).</param>
/// <param name="Provider">Hosting provider.</param>
/// <param name="ServerUri">Web root of the provider instance, e.g. <c>https://github.com/</c> or
/// <c>https://gitlab.example.com/</c> (including a relative URL root if the instance uses one).</param>
/// <param name="OwnerPath">Owner or namespace, e.g. <c>octocat</c> or <c>group/subgroup</c>.</param>
/// <param name="RepositoryName">Repository or project name.</param>
/// <param name="CredentialReference">Key of the token in the credential store.</param>
/// <param name="IssuePollingInterval">Interval for issue and change-request polling.</param>
/// <param name="BuildPollingInterval">Interval for pipeline/workflow polling.</param>
/// <param name="MetadataPollingInterval">Interval for milestones, labels and repository metadata.</param>
/// <param name="ReconciliationInterval">Interval of the forced full reconciliation.</param>
/// <param name="IsEnabled">Whether the repository is polled and shown.</param>
public sealed record RepositoryConfiguration(
    Guid Id,
    RepositoryProviderKind Provider,
    Uri ServerUri,
    string OwnerPath,
    string RepositoryName,
    string CredentialReference,
    TimeSpan IssuePollingInterval,
    TimeSpan BuildPollingInterval,
    TimeSpan MetadataPollingInterval,
    TimeSpan ReconciliationInterval,
    bool IsEnabled)
{
    /// <summary>Lowest allowed polling interval.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(15);

    /// <summary>Default issue/change-request interval.</summary>
    public static readonly TimeSpan DefaultIssueInterval = TimeSpan.FromSeconds(30);

    /// <summary>Default build interval.</summary>
    public static readonly TimeSpan DefaultBuildInterval = TimeSpan.FromSeconds(30);

    /// <summary>Default metadata interval.</summary>
    public static readonly TimeSpan DefaultMetadataInterval = TimeSpan.FromMinutes(5);

    /// <summary>Default reconciliation interval.</summary>
    public static readonly TimeSpan DefaultReconciliationInterval = TimeSpan.FromMinutes(15);

    /// <summary>Optional display name; defaults to the repository path.</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Optional User-Agent override. Needed for instances behind gateways that only admit certain agents
    /// (see architecture §9.4). Defaults to <c>GitWidget/&lt;version&gt;</c>.
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>Allows plain <c>http</c> for an explicitly configured internal instance.</summary>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>Provider id of the repository, stored after the first successful connection.</summary>
    public string? ExternalId { get; init; }

    /// <summary>Maximum number of milestones per repository in the compact widget.</summary>
    public const int MaxWidgetMilestones = 5;

    /// <summary>How the widget milestones are chosen.</summary>
    public MilestoneSelectionMode MilestoneMode { get; init; } = MilestoneSelectionMode.Automatic;

    /// <summary>Number of milestones shown in <see cref="MilestoneSelectionMode.Automatic"/> mode (1–5).</summary>
    public int AutomaticMilestoneCount { get; init; } = 1;

    /// <summary>Milestones shown in <see cref="MilestoneSelectionMode.Manual"/> mode, in display order (at most 5).</summary>
    public IReadOnlyList<string> SelectedMilestoneIds { get; init; } = [];

    /// <summary>Returns a copy with milestone settings clamped to their valid ranges.</summary>
    /// <returns>The normalized configuration.</returns>
    public RepositoryConfiguration Normalize() => this with
    {
        AutomaticMilestoneCount = Math.Clamp(AutomaticMilestoneCount, 1, MaxWidgetMilestones),
        SelectedMilestoneIds = (SelectedMilestoneIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Take(MaxWidgetMilestones).ToList(),
    };

    /// <summary>Labels to highlight in the dashboard (empty: all labels).</summary>
    public IReadOnlyList<string> HighlightedLabels { get; init; } = [];

    /// <summary>Position in the compact widget.</summary>
    public int SortOrder { get; init; }

    /// <summary>Full repository path, e.g. <c>group/subgroup/project</c>.</summary>
    public string FullPath => string.IsNullOrEmpty(OwnerPath) ? RepositoryName : $"{OwnerPath}/{RepositoryName}";

    /// <summary>Name shown in the UI.</summary>
    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? FullPath : DisplayName;

    /// <summary>Default credential reference for a configuration id.</summary>
    /// <param name="id">Configuration id.</param>
    /// <returns>The reference, e.g. <c>GitStatusWidget/{id}</c>.</returns>
    public static string DefaultCredentialReference(Guid id) => $"GitStatusWidget/{id:D}";
}
