using System.Text.Json;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Desktop.Tests.TestData;
using GitStatusWidget.Desktop.Tests.TestDoubles;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Domain.Events;
using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Desktop.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    private const string Token = "ghp_SuperSecretValue123";

    private readonly FakeSettingsStore _store = new();
    private readonly FakeCredentialStore _credentials = new();
    private readonly FakeProviderResolver _providers = new();
    private readonly FakeRepositoryCache _cache = new();
    private readonly FakeAutostartService _autostart = new();
    private readonly RepositoryStatusHub _hub = new();
    private FakeSynchronizationController _controller = new();

    private SettingsViewModel Create(params RepositoryConfiguration[] repositories)
    {
        _controller = new FakeSynchronizationController(new AppSettings { Repositories = repositories });
        return new SettingsViewModel(new SettingsService(_store, _controller, _autostart), _credentials, new GitStatusWidget.Application.Platform.CredentialStoreStatus("Test store", true, null), _providers, _cache, _hub);
    }

    [Fact]
    public void Reload_lists_repositories_in_sort_order_and_selects_the_first()
    {
        var second = Data.Config(name: "second", sortOrder: 1);
        var first = Data.Config(name: "first", sortOrder: 0);

        var vm = Create(second, first);

        vm.Repositories.Select(r => r.RepositoryPath).Should().Equal("octo/first", "octo/second");
        vm.Repositories.Should().OnlyContain(r => r.HasStoredToken && !r.IsNew);
        vm.Selected.Should().BeSameAs(vm.Repositories[0]);
        vm.CredentialStoreName.Should().Be("Test store");
        vm.StartAtLoginSupported.Should().BeTrue();
    }

    [Fact]
    public void Milestone_choices_come_from_the_hub_snapshot_and_exclude_closed_milestones()
    {
        var configuration = Data.Config() with { SelectedMilestoneId = "M2" };
        _hub.Publish(Data.State(configuration, Data.Snapshot(configuration, milestones:
        [
            Data.Milestone("M1", 1, 1),
            Data.Milestone("M2", 2, 0),
            Data.Milestone("M3", 0, 5, isClosed: true),
        ])));

        var editor = Create(configuration).Repositories.Single();

        editor.MilestoneChoices.Select(m => m.Id).Should().Equal(null, "M1", "M2");
        editor.SelectedMilestone!.Id.Should().Be("M2");
    }

    [Fact]
    public void Select_picks_the_repository_or_adds_a_new_one()
    {
        var a = Data.Config(name: "a", sortOrder: 0);
        var b = Data.Config(name: "b", sortOrder: 1);
        var vm = Create(a, b);

        vm.Select(b.Id, addNew: false);
        vm.Selected!.Id.Should().Be(b.Id);

        vm.Select(Guid.NewGuid(), addNew: false);
        vm.Selected!.Id.Should().Be(a.Id, "unknown ids fall back to the first repository");

        vm.Select(null, addNew: true);
        vm.Repositories.Should().HaveCount(3);
        vm.Selected!.IsNew.Should().BeTrue();
    }

    [Fact]
    public async Task Save_stores_a_new_token_in_the_credential_store_only()
    {
        var vm = Create();
        vm.AddCommand.Execute(null);
        var editor = vm.Selected!;
        editor.RepositoryPath = "octo/widget";
        editor.NewToken = $"  {Token}  ";
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        await vm.SaveCommand.ExecuteAsync(null);

        var reference = RepositoryConfiguration.DefaultCredentialReference(editor.Id);
        _credentials.Secrets.Should().ContainKey(reference).WhoseValue.Should().Be(Token);
        editor.NewToken.Should().BeEmpty("the typed token is cleared from the form after storing it");
        editor.HasStoredToken.Should().BeTrue();

        _store.Saved.Should().ContainSingle();
        var saved = _store.Saved[0];
        saved.Repositories.Should().ContainSingle().Which.CredentialReference.Should().Be(reference);
        JsonSerializer.Serialize(saved).Should().NotContain(Token, "settings never contain secrets");
        _controller.Applied.Should().ContainSingle().Which.Should().BeSameAs(saved);
        vm.StatusMessage.Should().Be("Saved.");
        closed.Should().Be(1);
    }

    [Fact]
    public async Task Save_without_new_token_does_not_touch_the_credential_store()
    {
        var vm = Create(Data.Config());

        await vm.SaveCommand.ExecuteAsync(null);

        _credentials.Writes.Should().BeEmpty();
        _credentials.Deletes.Should().BeEmpty();
        _store.Saved.Should().ContainSingle();
    }

    [Fact]
    public async Task Save_writes_global_options_and_sort_order()
    {
        var a = Data.Config(name: "a", sortOrder: 0);
        var b = Data.Config(name: "b", sortOrder: 1);
        var vm = Create(a, b);
        vm.Select(b.Id, addNew: false);
        vm.MoveUpCommand.Execute(null);
        vm.AlwaysOnTop = true;
        vm.ToastsEnabled = false;
        vm.MinimumToastPriority = EventPriority.High;
        vm.RecentlyClosedDays = 500;
        vm.Theme = "Dark";
        vm.ShowWidgetOnStartup = false;

        await vm.SaveCommand.ExecuteAsync(null);

        var saved = _store.Saved.Single();
        saved.Repositories.Select(r => (r.Id, r.SortOrder)).Should().Equal((b.Id, 0), (a.Id, 1));
        saved.AlwaysOnTop.Should().BeTrue();
        saved.ToastsEnabled.Should().BeFalse();
        saved.MinimumToastPriority.Should().Be(EventPriority.High);
        saved.RecentlyClosedWindow.Should().Be(TimeSpan.FromDays(90), "the window is clamped to 1..90 days");
        saved.Theme.Should().Be("Dark");
        saved.ShowWidgetOnStartup.Should().BeFalse();
    }

    [Fact]
    public async Task Save_mirrors_start_at_login_to_the_os()
    {
        var vm = Create(Data.Config());
        vm.StartAtLogin = true;

        await vm.SaveCommand.ExecuteAsync(null);

        _autostart.SetCalls.Should().Equal(true);
    }

    [Fact]
    public async Task Validation_failure_selects_the_offending_repository_and_does_not_save()
    {
        var good = Data.Config(name: "good", sortOrder: 0);
        var bad = Data.Config(name: "bad", sortOrder: 1);
        var vm = Create(good, bad);
        var badEditor = vm.Repositories[1];
        badEditor.IssueIntervalSeconds = 5;
        vm.Selected.Should().BeSameAs(vm.Repositories[0]);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        await vm.SaveCommand.ExecuteAsync(null);

        vm.Selected.Should().BeSameAs(badEditor);
        vm.StatusMessage.Should().StartWith("octo/bad: Polling intervals must be at least 15");
        _store.Saved.Should().BeEmpty();
        _controller.Applied.Should().BeEmpty();
        closed.Should().BeFalse();
    }

    [Fact]
    public async Task Validation_failure_does_not_store_tokens_of_other_repositories()
    {
        var vm = Create();
        vm.AddCommand.Execute(null);
        vm.Selected!.RepositoryPath = "octo/widget";
        vm.Selected.NewToken = Token;
        vm.AddCommand.Execute(null);
        vm.Selected!.RepositoryPath = "";

        await vm.SaveCommand.ExecuteAsync(null);

        _credentials.Writes.Should().BeEmpty();
        vm.Repositories[0].NewToken.Should().Be(Token, "the token is kept in the form until a successful save");
    }

    [Fact]
    public async Task Removing_a_repository_deletes_cache_and_token_when_requested()
    {
        var keep = Data.Config(name: "keep", sortOrder: 0);
        var remove = Data.Config(name: "remove", sortOrder: 1) with { CredentialReference = "custom/remove" };
        _credentials.Secrets["custom/remove"] = "x";
        var vm = Create(keep, remove);
        vm.Select(remove.Id, addNew: false);
        vm.DeleteTokenOnRemove.Should().BeTrue("deleting the token is the default");

        vm.RemoveCommand.Execute(null);

        vm.Repositories.Select(r => r.Id).Should().Equal(keep.Id);
        vm.Selected!.Id.Should().Be(keep.Id);
        _cache.Deleted.Should().BeEmpty("nothing is deleted before saving");

        await vm.SaveCommand.ExecuteAsync(null);

        _cache.Deleted.Should().Equal(remove.Id);
        _credentials.Deletes.Should().Equal("custom/remove");
        _store.Saved.Single().Repositories.Select(r => r.Id).Should().Equal(keep.Id);
    }

    [Fact]
    public async Task Removing_a_repository_can_keep_the_token()
    {
        var remove = Data.Config();
        var vm = Create(remove);
        vm.DeleteTokenOnRemove = false;

        vm.RemoveCommand.Execute(null);
        vm.Selected.Should().BeNull();
        await vm.SaveCommand.ExecuteAsync(null);

        _cache.Deleted.Should().Equal(remove.Id);
        _credentials.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task Removing_an_unsaved_repository_deletes_nothing()
    {
        var vm = Create();
        vm.AddCommand.Execute(null);

        vm.RemoveCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);

        _cache.Deleted.Should().BeEmpty();
        _credentials.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_discards_removals_and_edits()
    {
        var configuration = Data.Config();
        var vm = Create(configuration);
        vm.RemoveCommand.Execute(null);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.CancelCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);

        closed.Should().BeTrue();
        vm.Repositories.Select(r => r.Id).Should().Equal(configuration.Id);
        _cache.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_connection_uses_the_selected_provider_and_shows_the_result()
    {
        var vm = Create(Data.Config(provider: RepositoryProviderKind.GitLab, owner: "group", name: "project"));
        _providers.GitLab.OnTest = c => ProviderConnectionResult.Ok(Data.Identity(c), "Connected as alice") with
        {
            ServerVersion = "19.5.1-ee",
            CredentialDescription = "personal access token, scopes read_api",
            CredentialExpiresOn = new DateOnly(2027, 1, 31),
        };
        var editor = vm.Selected!;

        await vm.TestConnectionCommand.ExecuteAsync(null);

        _providers.GitLab.Tested.Should().ContainSingle().Which.FullPath.Should().Be("group/project");
        _providers.GitHub.Tested.Should().BeEmpty();
        editor.ConnectionOk.Should().BeTrue();
        editor.IsTesting.Should().BeFalse();
        editor.ConnectionMessage.Should().Be("Connected as alice · server 19.5.1-ee · personal access token, scopes read_api · token expires 2027-01-31");
        _store.Saved.Should().BeEmpty("testing does not save settings");
    }

    [Fact]
    public async Task Test_connection_reports_failures()
    {
        var vm = Create(Data.Config());
        _providers.GitHub.OnTest = _ => ProviderConnectionResult.Fail(ProviderFailureKind.AuthenticationFailed, "Token rejected");

        await vm.TestConnectionCommand.ExecuteAsync(null);

        vm.Selected!.ConnectionOk.Should().BeFalse();
        vm.Selected.ConnectionMessage.Should().Be("Token rejected");
    }

    [Fact]
    public async Task Test_connection_reports_provider_exceptions()
    {
        var vm = Create(Data.Config());
        _providers.GitHub.OnTest = _ => throw new InvalidOperationException("boom");

        await vm.TestConnectionCommand.ExecuteAsync(null);

        vm.Selected!.ConnectionOk.Should().BeFalse();
        vm.Selected.ConnectionMessage.Should().Be("Could not test the connection: boom");
        vm.Selected.IsTesting.Should().BeFalse();
    }

    [Fact]
    public async Task Test_connection_stores_a_newly_typed_token_first()
    {
        var vm = Create();
        vm.AddCommand.Execute(null);
        vm.Selected!.RepositoryPath = "octo/widget";
        vm.Selected.NewToken = Token;
        var reference = RepositoryConfiguration.DefaultCredentialReference(vm.Selected.Id);
        string? tokenSeenByProvider = null;
        _providers.GitHub.OnTest = c =>
        {
            tokenSeenByProvider = _credentials.Secrets.GetValueOrDefault(c.CredentialReference);
            return ProviderConnectionResult.Ok(Data.Identity(c), "ok");
        };

        await vm.TestConnectionCommand.ExecuteAsync(null);

        tokenSeenByProvider.Should().Be(Token);
        _credentials.Writes.Should().Equal(reference);
    }

    [Fact]
    public async Task Test_connection_with_invalid_form_shows_validation_and_does_not_call_the_provider()
    {
        var vm = Create();
        vm.AddCommand.Execute(null);

        await vm.TestConnectionCommand.ExecuteAsync(null);

        vm.Selected!.ValidationMessage.Should().NotBeNull();
        vm.Selected.ConnectionMessage.Should().BeNull();
        _providers.GitHub.Tested.Should().BeEmpty();
    }
}
