using GitStatusWidget.Application.Settings;
using GitStatusWidget.Desktop.Tests.TestData;
using GitStatusWidget.Desktop.Tests.TestDoubles;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Desktop.Tests.ViewModels;

public sealed class RepositoryEditorViewModelTests
{
    private static RepositoryEditorViewModel NewEditor(string path = "octo/widget", string token = "ghp_token") =>
        new(null, hasStoredToken: false) { RepositoryPath = path, NewToken = token };

    [Theory]
    [InlineData(null)]
    [InlineData(RepositoryProviderKind.GitHub)]
    [InlineData(RepositoryProviderKind.GitLab)]
    public void Constructor_does_not_throw(RepositoryProviderKind? provider)
    {
        var configuration = provider is { } kind ? Data.Config(provider: kind) : null;

        var create = () => new RepositoryEditorViewModel(configuration, hasStoredToken: configuration is not null);

        create.Should().NotThrow();
    }

    [Fact]
    public void New_editor_has_github_defaults()
    {
        var editor = new RepositoryEditorViewModel(null, hasStoredToken: false);

        editor.IsNew.Should().BeTrue();
        editor.Original.Should().BeNull();
        editor.Provider.Should().Be(RepositoryProviderKind.GitHub);
        editor.ServerUrl.Should().Be("https://github.com/");
        editor.IssueIntervalSeconds.Should().Be(30);
        editor.BuildIntervalSeconds.Should().Be(30);
        editor.MetadataIntervalMinutes.Should().Be(5);
        editor.IsEnabled.Should().BeTrue();
        editor.Title.Should().Be("New repository");
        editor.TokenStatus.Should().Be("No token stored yet.");
    }

    [Fact]
    public void Existing_configuration_is_loaded_into_the_form()
    {
        var configuration = Data.Config(provider: RepositoryProviderKind.GitLab, owner: "group/sub", name: "project", displayName: "My project") with
        {
            UserAgent = "Agent/1",
            AllowInsecureHttp = true,
            HighlightedLabels = ["bug", "feature"],
            IssuePollingInterval = TimeSpan.FromSeconds(45),
        };

        var editor = new RepositoryEditorViewModel(configuration, hasStoredToken: true);

        editor.IsNew.Should().BeFalse();
        editor.Id.Should().Be(configuration.Id);
        editor.IsGitLab.Should().BeTrue();
        editor.ServerUrl.Should().Be("https://gitlab.com/");
        editor.RepositoryPath.Should().Be("group/sub/project");
        editor.DisplayName.Should().Be("My project");
        editor.Title.Should().Be("My project");
        editor.UserAgent.Should().Be("Agent/1");
        editor.AllowInsecureHttp.Should().BeTrue();
        editor.HighlightedLabels.Should().Be("bug, feature");
        editor.IssueIntervalSeconds.Should().Be(45);
        editor.NewToken.Should().BeEmpty("a stored token is never loaded into the form");
        editor.TokenStatus.Should().Contain("stored securely");
    }

    [Fact]
    public void Missing_github_path_is_reported()
    {
        var editor = NewEditor(path: "");

        editor.TryBuild(0).Should().BeNull();
        editor.ValidationMessage.Should().Be("Enter the repository as owner/name.");
    }

    [Fact]
    public void Missing_gitlab_path_is_reported()
    {
        var editor = NewEditor(path: "project");
        editor.Provider = RepositoryProviderKind.GitLab;

        editor.TryBuild(0).Should().BeNull();
        editor.ValidationMessage.Should().Be("Enter the project path, e.g. group/subgroup/project.");
    }

    [Fact]
    public void GitHub_owner_with_slash_is_rejected()
    {
        var editor = NewEditor(path: "octo/sub/widget");

        editor.TryBuild(0).Should().BeNull();
        editor.ValidationMessage.Should().Be("GitHub repositories have the form owner/name.");
    }

    [Fact]
    public void GitLab_subgroups_are_accepted()
    {
        var editor = NewEditor(path: "group/sub/project");
        editor.Provider = RepositoryProviderKind.GitLab;

        var configuration = editor.TryBuild(0);

        configuration.Should().NotBeNull();
        configuration!.OwnerPath.Should().Be("group/sub");
        configuration.RepositoryName.Should().Be("project");
        configuration.ServerUri.Should().Be(new Uri("https://gitlab.com/"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("gitlab.example.com")]
    [InlineData("ftp://gitlab.example.com/")]
    public void Invalid_server_is_reported(string server)
    {
        var editor = NewEditor();
        editor.ServerUrl = server;

        editor.TryBuild(0).Should().BeNull();
        editor.ValidationMessage.Should().StartWith("Enter the server address");
    }

    [Fact]
    public void Http_requires_allow_insecure_http()
    {
        var editor = NewEditor(path: "group/project");
        editor.Provider = RepositoryProviderKind.GitLab;
        editor.ServerUrl = "http://gitlab.internal/";

        editor.TryBuild(0).Should().BeNull();
        editor.ValidationMessage.Should().Contain("http is only allowed when 'Allow insecure http' is enabled");

        editor.AllowInsecureHttp = true;
        var configuration = editor.TryBuild(0);

        configuration.Should().NotBeNull();
        editor.ValidationMessage.Should().BeNull();
        configuration!.ServerUri.Should().Be(new Uri("http://gitlab.internal/"));
        configuration.AllowInsecureHttp.Should().BeTrue();
    }

    [Fact]
    public void New_repository_requires_a_token()
    {
        var editor = NewEditor(token: "   ");

        editor.TryBuild(0).Should().BeNull();
        editor.ValidationMessage.Should().Be("Enter an access token.");
    }

    [Fact]
    public void Existing_repository_with_stored_token_needs_no_new_token()
    {
        var editor = new RepositoryEditorViewModel(Data.Config(), hasStoredToken: true);

        editor.TryBuild(0).Should().NotBeNull();
        editor.ValidationMessage.Should().BeNull();
    }

    [Theory]
    [InlineData(14, 30)]
    [InlineData(30, 14)]
    [InlineData(0, 0)]
    public void Polling_intervals_below_15_seconds_are_rejected(int issueSeconds, int buildSeconds)
    {
        var editor = NewEditor();
        editor.IssueIntervalSeconds = issueSeconds;
        editor.BuildIntervalSeconds = buildSeconds;

        editor.TryBuild(0).Should().BeNull();
        editor.ValidationMessage.Should().StartWith("Polling intervals must be at least 15");
    }

    [Fact]
    public void Validation_message_is_cleared_after_a_successful_build()
    {
        var editor = NewEditor(path: "");
        editor.TryBuild(0).Should().BeNull();

        editor.RepositoryPath = "octo/widget";

        editor.TryBuild(0).Should().NotBeNull();
        editor.ValidationMessage.Should().BeNull();
    }

    [Fact]
    public void TryBuild_of_a_new_repository_uses_defaults_and_form_values()
    {
        var editor = NewEditor(path: " octo/widget ");
        editor.DisplayName = "  Widget  ";
        editor.UserAgent = "   ";
        editor.HighlightedLabels = " bug, ,feature ,  ";
        editor.IssueIntervalSeconds = 20;
        editor.BuildIntervalSeconds = 60;
        editor.MetadataIntervalMinutes = 0;
        editor.IsEnabled = false;

        var configuration = editor.TryBuild(sortOrder: 3);

        configuration.Should().NotBeNull();
        configuration!.Id.Should().Be(editor.Id);
        configuration.Provider.Should().Be(RepositoryProviderKind.GitHub);
        configuration.ServerUri.Should().Be(new Uri("https://github.com/"));
        configuration.OwnerPath.Should().Be("octo");
        configuration.RepositoryName.Should().Be("widget");
        configuration.CredentialReference.Should().Be(RepositoryConfiguration.DefaultCredentialReference(editor.Id));
        configuration.SortOrder.Should().Be(3);
        configuration.DisplayName.Should().Be("Widget");
        configuration.UserAgent.Should().BeNull();
        configuration.HighlightedLabels.Should().Equal("bug", "feature");
        configuration.IssuePollingInterval.Should().Be(TimeSpan.FromSeconds(20));
        configuration.BuildPollingInterval.Should().Be(TimeSpan.FromSeconds(60));
        configuration.MetadataPollingInterval.Should().Be(TimeSpan.FromMinutes(1), "metadata polling is clamped to at least one minute");
        configuration.ReconciliationInterval.Should().Be(RepositoryConfiguration.DefaultReconciliationInterval);
        configuration.IsEnabled.Should().BeFalse();
        configuration.ExternalId.Should().BeNull();
        configuration.SelectedMilestoneId.Should().BeNull();
    }

    [Fact]
    public void Server_url_is_normalized_with_trailing_slash_and_relative_root()
    {
        var editor = NewEditor(path: "team/app");
        editor.Provider = RepositoryProviderKind.GitLab;
        editor.ServerUrl = " https://code.example.com/gitlab?x=1 ";

        var configuration = editor.TryBuild(0);

        configuration!.ServerUri.Should().Be(new Uri("https://code.example.com/gitlab/"));
    }

    [Fact]
    public void TryBuild_of_an_existing_repository_keeps_credential_reference_identity_and_reconciliation()
    {
        var original = Data.Config() with
        {
            CredentialReference = "custom/reference",
            ExternalId = "R_42",
            SelectedMilestoneId = "M1",
            ReconciliationInterval = TimeSpan.FromMinutes(42),
        };
        var editor = new RepositoryEditorViewModel(original, hasStoredToken: true);
        editor.SetMilestones([new MilestoneChoice("M1", "Milestone 1"), new MilestoneChoice("M2", "Milestone 2")]);
        editor.SelectedMilestone.Should().Be(new MilestoneChoice("M1", "Milestone 1"));

        var configuration = editor.TryBuild(1);

        configuration!.CredentialReference.Should().Be("custom/reference");
        configuration.ExternalId.Should().Be("R_42");
        configuration.SelectedMilestoneId.Should().Be("M1");
        configuration.ReconciliationInterval.Should().Be(TimeSpan.FromMinutes(42));
        configuration.SortOrder.Should().Be(1);
    }

    [Fact]
    public void Changing_the_milestone_selection_is_saved()
    {
        var editor = new RepositoryEditorViewModel(Data.Config() with { SelectedMilestoneId = "M1" }, hasStoredToken: true);
        editor.SetMilestones([new MilestoneChoice("M1", "Milestone 1"), new MilestoneChoice("M2", "Milestone 2")]);

        editor.SelectedMilestone = editor.MilestoneChoices.Single(m => m.Id == "M2");

        editor.TryBuild(0)!.SelectedMilestoneId.Should().Be("M2");
    }

    [Fact]
    public void Milestone_choices_always_start_with_automatic()
    {
        var editor = new RepositoryEditorViewModel(Data.Config(), hasStoredToken: true);

        editor.SetMilestones([new MilestoneChoice("M1", "Milestone 1")]);

        editor.MilestoneChoices.Should().HaveCount(2);
        editor.MilestoneChoices[0].Id.Should().BeNull();
        editor.SelectedMilestone.Should().BeSameAs(editor.MilestoneChoices[0]);
        editor.MilestoneChoices[1].ToString().Should().Be("Milestone 1");
    }

    [Theory]
    [InlineData("octo/other", RepositoryProviderKind.GitHub, "https://github.com/")]
    [InlineData("octo/widget", RepositoryProviderKind.GitHub, "https://github.example.com/")]
    [InlineData("octo/widget", RepositoryProviderKind.GitLab, "https://gitlab.com/")]
    public void Identity_change_clears_external_id_and_selected_milestone(string path, RepositoryProviderKind provider, string server)
    {
        var original = Data.Config() with { ExternalId = "R_42", SelectedMilestoneId = "M1" };
        var editor = new RepositoryEditorViewModel(original, hasStoredToken: true);
        editor.SetMilestones([new MilestoneChoice("M1", "Milestone 1")]);

        editor.Provider = provider;
        editor.ServerUrl = server;
        editor.RepositoryPath = path;
        var configuration = editor.TryBuild(0);

        configuration.Should().NotBeNull();
        configuration!.ExternalId.Should().BeNull();
        configuration.SelectedMilestoneId.Should().BeNull();
        configuration.CredentialReference.Should().Be(original.CredentialReference);
    }

    [Fact]
    public void Path_case_change_is_not_an_identity_change()
    {
        var original = Data.Config() with { ExternalId = "R_42" };
        var editor = new RepositoryEditorViewModel(original, hasStoredToken: true) { RepositoryPath = "Octo/Widget" };

        editor.TryBuild(0)!.ExternalId.Should().Be("R_42");
    }

    [Fact]
    public void Selected_milestone_survives_when_milestones_are_not_loaded()
    {
        var editor = new RepositoryEditorViewModel(Data.Config() with { SelectedMilestoneId = "M1" }, hasStoredToken: true);

        // SettingsViewModel.Reload always calls SetMilestones, with an empty list when the hub has no snapshot.
        editor.SetMilestones([]);

        editor.TryBuild(0)!.SelectedMilestoneId.Should().Be("M1");
    }

    [Fact]
    public void Switching_provider_switches_the_public_server()
    {
        var editor = new RepositoryEditorViewModel(null, hasStoredToken: false);
        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        editor.Provider = RepositoryProviderKind.GitLab;

        editor.ServerUrl.Should().Be("https://gitlab.com/");
        editor.IsGitLab.Should().BeTrue();
        editor.TokenHint.Should().Contain("read_api");
        changed.Should().Contain([nameof(RepositoryEditorViewModel.IsGitLab), nameof(RepositoryEditorViewModel.TokenHint), nameof(RepositoryEditorViewModel.ServerUrl)]);

        editor.Provider = RepositoryProviderKind.GitHub;

        editor.ServerUrl.Should().Be("https://github.com/");
        editor.IsGitLab.Should().BeFalse();
        editor.TokenHint.Should().Contain("Fine-grained");
    }

    [Fact]
    public void Switching_provider_keeps_a_self_hosted_server()
    {
        var editor = new RepositoryEditorViewModel(null, hasStoredToken: false) { ServerUrl = "https://git.example.com/" };

        editor.Provider = RepositoryProviderKind.GitLab;

        editor.ServerUrl.Should().Be("https://git.example.com/");
    }

    [Fact]
    public void Pasting_a_gitlab_url_fills_provider_server_and_path()
    {
        var editor = new RepositoryEditorViewModel(null, hasStoredToken: false);

        editor.RepositoryPath = "https://gitlab.com/group/sub/project/-/work_items/5";

        editor.Provider.Should().Be(RepositoryProviderKind.GitLab);
        editor.ServerUrl.Should().Be("https://gitlab.com/");
        editor.RepositoryPath.Should().Be("group/sub/project");
        editor.Title.Should().Be("group/sub/project");
    }

    [Fact]
    public void Pasting_a_github_url_into_a_gitlab_editor_switches_to_github()
    {
        var editor = new RepositoryEditorViewModel(null, hasStoredToken: false) { Provider = RepositoryProviderKind.GitLab };

        editor.RepositoryPath = "https://github.com/octo/widget/issues";

        editor.Provider.Should().Be(RepositoryProviderKind.GitHub);
        editor.ServerUrl.Should().Be("https://github.com/");
        editor.RepositoryPath.Should().Be("octo/widget");
    }

    [Fact]
    public void Pasting_a_url_below_the_configured_relative_root_keeps_the_root()
    {
        var editor = new RepositoryEditorViewModel(null, hasStoredToken: false) { Provider = RepositoryProviderKind.GitLab };
        editor.ServerUrl = "https://code.example.com/gitlab/";

        editor.RepositoryPath = "https://code.example.com/gitlab/team/app.git";

        editor.Provider.Should().Be(RepositoryProviderKind.GitLab, "an unknown host keeps the selected provider");
        editor.ServerUrl.Should().Be("https://code.example.com/gitlab/");
        editor.RepositoryPath.Should().Be("team/app");
    }

    [Fact]
    public void Pasting_an_unparseable_url_leaves_the_input_for_validation()
    {
        var editor = new RepositoryEditorViewModel(null, hasStoredToken: false) { NewToken = "t" };

        editor.RepositoryPath = "https://github.com/octo";

        editor.RepositoryPath.Should().Be("https://github.com/octo");
        editor.ServerUrl.Should().Be("https://github.com/");
        editor.TryBuild(0).Should().BeNull();
    }

    [Fact]
    public void Title_and_token_status_raise_change_notifications()
    {
        var editor = new RepositoryEditorViewModel(null, hasStoredToken: false);
        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        editor.RepositoryPath = "octo/widget";
        editor.DisplayName = "Widget";
        editor.HasStoredToken = true;

        changed.Where(n => n == nameof(RepositoryEditorViewModel.Title)).Should().HaveCount(2);
        changed.Should().Contain(nameof(RepositoryEditorViewModel.TokenStatus));
        editor.Title.Should().Be("Widget");
    }
}
