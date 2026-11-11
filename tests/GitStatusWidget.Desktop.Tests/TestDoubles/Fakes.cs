using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Application.Persistence;
using GitStatusWidget.Application.Platform;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Desktop.Tests.TestDoubles;

/// <summary>Runs posted actions inline, so hub events are applied synchronously.</summary>
internal sealed class InlineUiDispatcher : IUiDispatcher
{
    public int PostCount { get; private set; }

    public void Post(Action action)
    {
        PostCount++;
        action();
    }
}

internal sealed class FakeSynchronizationController : ISynchronizationController
{
    public FakeSynchronizationController(AppSettings? settings = null) => Settings = settings ?? new AppSettings();

    public AppSettings Settings { get; private set; }

    public List<Guid?> RefreshRequests { get; } = [];

    public List<AppSettings> Applied { get; } = [];

    public void RequestRefresh(Guid? repositoryId = null) => RefreshRequests.Add(repositoryId);

    public Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Settings = settings;
        Applied.Add(settings);
        return Task.CompletedTask;
    }
}

internal sealed class FakeNavigator : IAppNavigator
{
    public List<string> Calls { get; } = [];

    public void ShowWidget(bool activate = true) => Calls.Add($"ShowWidget({activate})");

    public void HideWidget() => Calls.Add("HideWidget");

    public void ToggleWidget() => Calls.Add("ToggleWidget");

    public void ShowDashboard(Guid? repositoryId = null) => Calls.Add($"ShowDashboard({repositoryId})");

    public void ShowSettings(Guid? repositoryId = null, bool addNew = false, bool suggestUserAgent = false) =>
        Calls.Add(suggestUserAgent ? $"ShowSettings({repositoryId},{addNew},suggestUserAgent)" : $"ShowSettings({repositoryId},{addNew})");

    public void ShowDiagnostics() => Calls.Add("ShowDiagnostics()");

    public void ShowClaudeUsage() => Calls.Add("ShowClaudeUsage()");

    public void ShowUpdate() => Calls.Add("ShowUpdate()");

    public void OpenInBrowser(Uri? uri) => Calls.Add($"OpenInBrowser({uri})");

    public void ExitApplication() => Calls.Add("Exit");
}

/// <summary>In-memory credential store; never touches the OS store.</summary>
internal sealed class FakeCredentialStore : ICredentialStore
{
    public Dictionary<string, string> Secrets { get; } = [];

    public List<string> Writes { get; } = [];

    public List<string> Deletes { get; } = [];

    public bool IsAvailable => true;

    public string StoreName => "Test store";

    public Task<Secret?> ReadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Secrets.TryGetValue(key, out var value) ? new Secret(value) : null);

    public Task WriteAsync(string key, Secret secret, CancellationToken cancellationToken)
    {
        Writes.Add(key);
        Secrets[key] = secret.Reveal();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        Deletes.Add(key);
        Secrets.Remove(key);
        return Task.CompletedTask;
    }
}

internal sealed class FakeAutostartService : IAutostartService
{
    public bool IsSupported { get; init; } = true;

    public List<bool> SetCalls { get; } = [];

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(SetCalls.LastOrDefault());

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        SetCalls.Add(enabled);
        return Task.CompletedTask;
    }
}

internal sealed class FakeSettingsStore : ISettingsStore
{
    public List<AppSettings> Saved { get; } = [];

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Saved.LastOrDefault() ?? new AppSettings());

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Saved.Add(settings);
        return Task.CompletedTask;
    }
}

internal sealed class FakeRepositoryCache : IRepositoryCache
{
    public List<Guid> Deleted { get; } = [];

    public Task<CachedRepositoryState> LoadAsync(Guid configurationId, CancellationToken cancellationToken) => Task.FromResult(CachedRepositoryState.Empty);

    public Task SaveAsync(Guid configurationId, CachedRepositoryState state, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteAsync(Guid configurationId, CancellationToken cancellationToken)
    {
        Deleted.Add(configurationId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeProvider : IRepositoryStatusProvider
{
    public FakeProvider(RepositoryProviderKind kind) => Kind = kind;

    public RepositoryProviderKind Kind { get; }

    public Func<RepositoryConfiguration, ProviderConnectionResult> OnTest { get; set; } =
        c => ProviderConnectionResult.Ok(new RepositoryIdentity(c.Provider, c.ServerUri.Host, c.OwnerPath, c.RepositoryName, "R_1"), "Connected");

    public List<RepositoryConfiguration> Tested { get; } = [];

    public Task<ProviderConnectionResult> TestConnectionAsync(RepositoryConfiguration configuration, CancellationToken cancellationToken)
    {
        Tested.Add(configuration);
        return Task.FromResult(OnTest(configuration));
    }

    public Task<ProviderFetchResult> FetchAsync(RepositoryConfiguration configuration, SnapshotRequestContext context, CancellationToken cancellationToken) =>
        throw new NotSupportedException("View-model tests never fetch.");
}

internal sealed class FakeProviderResolver : IRepositoryStatusProviderResolver
{
    public FakeProvider GitHub { get; } = new(RepositoryProviderKind.GitHub);

    public FakeProvider GitLab { get; } = new(RepositoryProviderKind.GitLab);

    public IRepositoryStatusProvider Resolve(RepositoryProviderKind provider) => provider == RepositoryProviderKind.GitLab ? GitLab : GitHub;
}

internal sealed class FakeReducedMotionDetector : IReducedMotionDetector
{
    public bool Enabled { get; set; }

    public bool IsReducedMotionEnabled() => Enabled;
}

internal sealed class RecordingToastPresenter : IToastPresenter
{
    public List<ToastViewModel> Shown { get; } = [];

    public void Show(ToastViewModel toast) => Shown.Add(toast);
}
