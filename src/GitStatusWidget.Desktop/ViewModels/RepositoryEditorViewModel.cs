using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Desktop.ViewModels;

/// <summary>Milestone choice in the editor.</summary>
public sealed record MilestoneChoice(string? Id, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Editable form of one repository configuration. Never holds a stored token.</summary>
public sealed partial class RepositoryEditorViewModel : ObservableObject
{
    /// <summary>User-Agent offered when a gateway blocks the API (issue #10).</summary>
    public static string SuggestedUserAgent { get; } = GatewayUserAgent.Suggest(Services.AppInfo.Version);

    private bool _parsing;

    public RepositoryEditorViewModel(RepositoryConfiguration? configuration, bool hasStoredToken)
    {
        Id = configuration?.Id ?? Guid.NewGuid();
        IsNew = configuration is null;
        Original = configuration;
        Provider = configuration?.Provider ?? RepositoryProviderKind.GitHub;
        ServerUrl = configuration?.ServerUri.ToString() ?? "https://github.com/";
        RepositoryPath = configuration?.FullPath ?? string.Empty;
        DisplayName = configuration?.DisplayName ?? string.Empty;
        UserAgent = configuration?.UserAgent ?? string.Empty;
        AllowInsecureHttp = configuration?.AllowInsecureHttp ?? false;
        IsEnabled = configuration?.IsEnabled ?? true;
        IssueIntervalSeconds = (int)(configuration?.IssuePollingInterval ?? RepositoryConfiguration.DefaultIssueInterval).TotalSeconds;
        BuildIntervalSeconds = (int)(configuration?.BuildPollingInterval ?? RepositoryConfiguration.DefaultBuildInterval).TotalSeconds;
        MetadataIntervalMinutes = (int)(configuration?.MetadataPollingInterval ?? RepositoryConfiguration.DefaultMetadataInterval).TotalMinutes;
        HighlightedLabels = string.Join(", ", configuration?.HighlightedLabels ?? []);
        SelectedMilestoneId = configuration?.SelectedMilestoneId;
        HasStoredToken = hasStoredToken;
    }

    public Guid Id { get; }

    public bool IsNew { get; }

    public RepositoryConfiguration? Original { get; }

    public IReadOnlyList<RepositoryProviderKind> Providers { get; } = [RepositoryProviderKind.GitHub, RepositoryProviderKind.GitLab];

    [ObservableProperty]
    public partial RepositoryProviderKind Provider { get; set; }

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RepositoryPath { get; set; }

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    /// <summary>New token typed by the user; empty keeps the stored token.</summary>
    [ObservableProperty]
    public partial string NewToken { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasStoredToken { get; set; }

    [ObservableProperty]
    public partial string UserAgent { get; set; }

    [ObservableProperty]
    public partial bool AllowInsecureHttp { get; set; }

    /// <summary>The last refresh or connection test was intercepted by a login gateway.</summary>
    [ObservableProperty]
    public partial bool GatewayBlocked { get; set; }

    /// <summary>Show the git-compatible User-Agent suggestion (GitLab, or any provider while blocked).</summary>
    public bool ShowUserAgentSuggestion => IsGitLab || GatewayBlocked;

    public string GatewayHint => GatewayBlocked
        ? $"A login gateway intercepted the API calls. Gateways that only admit git clients accept a User-Agent such as '{SuggestedUserAgent}'. Also check VPN/DNS."
        : $"If your server sits behind an access gateway that only admits git clients, use a User-Agent such as '{SuggestedUserAgent}'.";

    /// <summary>Fills in <see cref="SuggestedUserAgent"/>; nothing is saved until the user saves the settings.</summary>
    [RelayCommand]
    public void UseSuggestedUserAgent() => UserAgent = SuggestedUserAgent;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial int IssueIntervalSeconds { get; set; }

    [ObservableProperty]
    public partial int BuildIntervalSeconds { get; set; }

    [ObservableProperty]
    public partial int MetadataIntervalMinutes { get; set; }

    [ObservableProperty]
    public partial string HighlightedLabels { get; set; }

    [ObservableProperty]
    public partial string? SelectedMilestoneId { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<MilestoneChoice> MilestoneChoices { get; set; } = [new(null, "Automatic (nearest due date)")];

    [ObservableProperty]
    public partial MilestoneChoice? SelectedMilestone { get; set; }

    [ObservableProperty]
    public partial string? ValidationMessage { get; set; }

    [ObservableProperty]
    public partial string? ConnectionMessage { get; set; }

    [ObservableProperty]
    public partial bool? ConnectionOk { get; set; }

    [ObservableProperty]
    public partial bool IsTesting { get; set; }

    public bool IsGitLab => Provider == RepositoryProviderKind.GitLab;

    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? (string.IsNullOrWhiteSpace(RepositoryPath) ? "New repository" : RepositoryPath) : DisplayName;

    public string TokenHint => Provider == RepositoryProviderKind.GitLab
        ? "Personal or project access token with the read_api scope (Reporter role)."
        : "Fine-grained token with read-only access to Metadata, Issues, Pull requests and Actions.";

    public string TokenStatus => HasStoredToken ? "A token is stored securely. Leave empty to keep it." : "No token stored yet.";

    public void SetMilestones(IEnumerable<MilestoneChoice> milestones)
    {
        List<MilestoneChoice> choices = [new MilestoneChoice(null, "Automatic (nearest due date)"), .. milestones];
        if (SelectedMilestoneId is { } selected && choices.All(m => m.Id != selected))
        {
            // Closed or not loaded yet (no snapshot, disabled repository): keep the configured milestone.
            choices.Add(new MilestoneChoice(selected, "Configured milestone (not loaded)"));
        }

        MilestoneChoices = choices;
        SelectedMilestone = choices.First(m => m.Id == SelectedMilestoneId);
    }

    /// <summary>Validates the form and builds the configuration, or returns <see langword="null"/> with <see cref="ValidationMessage"/> set.</summary>
    public RepositoryConfiguration? TryBuild(int sortOrder)
    {
        ValidationMessage = Validate(out var server, out var owner, out var name);
        if (ValidationMessage is not null)
        {
            return null;
        }

        var labels = HighlightedLabels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var identityChanged = Original is not null
            && (Original.Provider != Provider || Original.ServerUri != server || !string.Equals(Original.FullPath, $"{owner}/{name}", StringComparison.OrdinalIgnoreCase));
        return new RepositoryConfiguration(
            Id,
            Provider,
            server!,
            owner,
            name,
            Original?.CredentialReference ?? RepositoryConfiguration.DefaultCredentialReference(Id),
            TimeSpan.FromSeconds(Math.Max(15, IssueIntervalSeconds)),
            TimeSpan.FromSeconds(Math.Max(15, BuildIntervalSeconds)),
            TimeSpan.FromMinutes(Math.Max(1, MetadataIntervalMinutes)),
            Original?.ReconciliationInterval ?? RepositoryConfiguration.DefaultReconciliationInterval,
            IsEnabled)
        {
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim(),
            UserAgent = string.IsNullOrWhiteSpace(UserAgent) ? null : UserAgent.Trim(),
            AllowInsecureHttp = AllowInsecureHttp,
            ExternalId = identityChanged ? null : Original?.ExternalId,
            SelectedMilestoneId = identityChanged ? null : SelectedMilestone?.Id,
            HighlightedLabels = labels,
            SortOrder = sortOrder,
        };
    }

    partial void OnProviderChanged(RepositoryProviderKind value)
    {
        if (value == RepositoryProviderKind.GitLab && ServerUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            ServerUrl = "https://gitlab.com/";
        }
        else if (value == RepositoryProviderKind.GitHub && ServerUrl.Contains("gitlab.com", StringComparison.OrdinalIgnoreCase))
        {
            ServerUrl = "https://github.com/";
        }

        OnPropertyChanged(nameof(IsGitLab));
        OnPropertyChanged(nameof(TokenHint));
        OnPropertyChanged(nameof(ShowUserAgentSuggestion));
    }

    partial void OnRepositoryPathChanged(string value)
    {
        OnPropertyChanged(nameof(Title));
        if (_parsing || !value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Uri.TryCreate(ServerUrl, UriKind.Absolute, out var known);
        if (RepositoryAddress.TryParseUrl(value, known, out var server, out var path, out var provider))
        {
            _parsing = true;
            if (provider is { } detected)
            {
                Provider = detected;
            }

            ServerUrl = server.ToString();
            RepositoryPath = path;
            _parsing = false;
        }
    }

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(Title));

    partial void OnGatewayBlockedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowUserAgentSuggestion));
        OnPropertyChanged(nameof(GatewayHint));
    }

    partial void OnHasStoredTokenChanged(bool value) => OnPropertyChanged(nameof(TokenStatus));

    private string? Validate(out Uri? server, out string owner, out string name)
    {
        owner = name = string.Empty;
        server = null;
        if (!Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            return "Enter the server address, e.g. https://github.com/ or https://gitlab.example.com/.";
        }

        if (parsed.Scheme == Uri.UriSchemeHttp && !AllowInsecureHttp)
        {
            return "http is only allowed when 'Allow insecure http' is enabled (internal servers only).";
        }

        server = new Uri(parsed.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        (owner, name) = RepositoryAddress.Split(RepositoryPath);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(name))
        {
            return Provider == RepositoryProviderKind.GitLab
                ? "Enter the project path, e.g. group/subgroup/project."
                : "Enter the repository as owner/name.";
        }

        if (Provider == RepositoryProviderKind.GitHub && owner.Contains('/', StringComparison.Ordinal))
        {
            return "GitHub repositories have the form owner/name.";
        }

        if (IsNew && !HasStoredToken && string.IsNullOrWhiteSpace(NewToken))
        {
            return "Enter an access token.";
        }

        if (IssueIntervalSeconds < 15 || BuildIntervalSeconds < 15)
        {
            return string.Create(CultureInfo.CurrentCulture, $"Polling intervals must be at least {RepositoryConfiguration.MinimumInterval.TotalSeconds} seconds.");
        }

        return null;
    }
}
