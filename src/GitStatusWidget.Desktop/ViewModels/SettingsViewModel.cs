using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Application.Persistence;
using GitStatusWidget.Application.Platform;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Domain.Events;

namespace GitStatusWidget.Desktop.ViewModels;

/// <summary>Settings window: repositories, tokens, polling, startup and appearance (architecture §16.1).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ICredentialStore _credentials;
    private readonly IRepositoryStatusProviderResolver _providers;
    private readonly IRepositoryCache _cache;
    private readonly IRepositoryStatusHub _hub;
    private readonly List<RepositoryEditorViewModel> _removed = [];

    private readonly CredentialStoreStatus _credentialStatus;

    public SettingsViewModel(SettingsService settings, ICredentialStore credentials, CredentialStoreStatus credentialStatus, IRepositoryStatusProviderResolver providers, IRepositoryCache cache, IRepositoryStatusHub hub)
    {
        _credentialStatus = credentialStatus;
        _settings = settings;
        _credentials = credentials;
        _providers = providers;
        _cache = cache;
        _hub = hub;
        Reload();
    }

    /// <summary>Raised after a successful save that should close the window.</summary>
    public event EventHandler? CloseRequested;

    public ObservableCollection<RepositoryEditorViewModel> Repositories { get; } = [];

    public IReadOnlyList<EventPriority> Priorities { get; } = [EventPriority.Low, EventPriority.Medium, EventPriority.High];

    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];

    public IReadOnlyList<DailyCountChoice> DailyCountModes { get; } =
    [
        new(DailyCountMode.CalendarDay, "Today (since midnight)"),
        new(DailyCountMode.Last24Hours, "Last 24 hours"),
    ];

    public string CredentialStoreName => _credentials.StoreName;

    public bool CredentialStoreAvailable => _credentials.IsAvailable && _credentialStatus.IsPersistent;

    public string? CredentialFallbackReason => _credentialStatus.FallbackReason;

    public bool StartAtLoginSupported => _settings.AutostartSupported;

    [ObservableProperty]
    public partial RepositoryEditorViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial bool ShowWidgetOnStartup { get; set; }

    [ObservableProperty]
    public partial bool AlwaysOnTop { get; set; }

    [ObservableProperty]
    public partial bool StartAtLogin { get; set; }

    [ObservableProperty]
    public partial bool ToastsEnabled { get; set; }

    [ObservableProperty]
    public partial EventPriority MinimumToastPriority { get; set; }

    [ObservableProperty]
    public partial int RecentlyClosedDays { get; set; }

    [ObservableProperty]
    public partial DailyCountChoice? DailyCount { get; set; }

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    public partial bool IncludePrereleaseUpdates { get; set; }

    [ObservableProperty]
    public partial string Theme { get; set; } = "System";

    [ObservableProperty]
    public partial bool DeleteTokenOnRemove { get; set; } = true;

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public void Select(Guid? repositoryId, bool addNew)
    {
        if (addNew)
        {
            Add();
            return;
        }

        Selected = Repositories.FirstOrDefault(r => r.Id == repositoryId) ?? Repositories.FirstOrDefault();
    }

    [RelayCommand]
    private void Add()
    {
        var editor = new RepositoryEditorViewModel(null, hasStoredToken: false);
        Repositories.Add(editor);
        Selected = editor;
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is null)
        {
            return;
        }

        var index = Repositories.IndexOf(Selected);
        if (!Selected.IsNew)
        {
            _removed.Add(Selected);
        }

        Repositories.Remove(Selected);
        Selected = Repositories.Count == 0 ? null : Repositories[Math.Clamp(index, 0, Repositories.Count - 1)];
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        var editor = Selected;
        if (editor is null)
        {
            return;
        }

        var configuration = editor.TryBuild(0);
        if (configuration is null)
        {
            return;
        }

        editor.IsTesting = true;
        editor.ConnectionOk = null;
        editor.ConnectionMessage = "Testing…";
        try
        {
            await StoreTokenAsync(editor, configuration).ConfigureAwait(true);
            var result = await _providers.Resolve(configuration.Provider).TestConnectionAsync(configuration, CancellationToken.None).ConfigureAwait(true);
            editor.ConnectionOk = result.IsSuccess;
            var details = new[] { result.ServerVersion is null ? null : $"server {result.ServerVersion}", result.CredentialDescription, result.CredentialExpiresOn is { } expires ? $"token expires {expires:yyyy-MM-dd}" : null }
                .Where(d => d is not null);
            editor.ConnectionMessage = string.Join(" · ", new[] { result.Message }.Concat(details));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            editor.ConnectionOk = false;
            editor.ConnectionMessage = $"Could not test the connection: {ex.Message}";
        }
        finally
        {
            editor.IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        StatusMessage = null;
        var configurations = new List<RepositoryConfiguration>();
        foreach (var (editor, index) in Repositories.Select((e, i) => (e, i)))
        {
            var configuration = editor.TryBuild(index);
            if (configuration is null)
            {
                Selected = editor;
                StatusMessage = $"{editor.Title}: {editor.ValidationMessage}";
                return;
            }

            configurations.Add(configuration);
        }

        IsBusy = true;
        try
        {
            foreach (var (editor, configuration) in Repositories.Zip(configurations))
            {
                await StoreTokenAsync(editor, configuration).ConfigureAwait(true);
            }

            foreach (var removed in _removed)
            {
                await _cache.DeleteAsync(removed.Id, CancellationToken.None).ConfigureAwait(true);
                if (DeleteTokenOnRemove && removed.Original is { } original)
                {
                    await _credentials.DeleteAsync(original.CredentialReference, CancellationToken.None).ConfigureAwait(true);
                }
            }

            _removed.Clear();
            var settings = _settings.Current with
            {
                Repositories = configurations,
                ShowWidgetOnStartup = ShowWidgetOnStartup,
                AlwaysOnTop = AlwaysOnTop,
                StartAtLogin = StartAtLogin,
                ToastsEnabled = ToastsEnabled,
                MinimumToastPriority = MinimumToastPriority,
                RecentlyClosedWindow = TimeSpan.FromDays(Math.Clamp(RecentlyClosedDays, 1, 90)),
                DailyCountMode = DailyCount?.Mode ?? DailyCountMode.CalendarDay,
                CheckForUpdates = CheckForUpdates,
                IncludePrereleaseUpdates = IncludePrereleaseUpdates,
                Theme = Theme,
            };
            await _settings.SaveAsync(settings, CancellationToken.None).ConfigureAwait(true);
            StatusMessage = "Saved.";
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage = $"Could not save: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Reload();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task StoreTokenAsync(RepositoryEditorViewModel editor, RepositoryConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(editor.NewToken))
        {
            return;
        }

        await _credentials.WriteAsync(configuration.CredentialReference, new Secret(editor.NewToken), CancellationToken.None).ConfigureAwait(true);
        editor.NewToken = string.Empty;
        editor.HasStoredToken = true;
    }

    /// <summary>Moves a repository (buttons and drag &amp; drop) and keeps it selected.</summary>
    public void MoveTo(int from, int to)
    {
        if (from < 0 || from >= Repositories.Count || to < 0 || to >= Repositories.Count || from == to)
        {
            return;
        }

        var item = Repositories[from];
        Repositories.Move(from, to);

        // The list control drops its selection on Move; re-assigning the same value would not notify.
        Selected = null;
        Selected = item;
    }

    private void Move(int delta)
    {
        if (Selected is null)
        {
            return;
        }

        var index = Repositories.IndexOf(Selected);
        MoveTo(index, index + delta);
    }

    private void Reload()
    {
        var settings = _settings.Current;
        Repositories.Clear();
        _removed.Clear();
        foreach (var configuration in settings.Repositories.OrderBy(r => r.SortOrder))
        {
            var editor = new RepositoryEditorViewModel(configuration, hasStoredToken: true);
            var milestones = _hub.Find(configuration.Id)?.Snapshot?.Milestones.Where(m => !m.IsClosed)
                .Select(m => new MilestoneChoice(m.ExternalId, m.Title)) ?? [];
            editor.SetMilestones(milestones);
            Repositories.Add(editor);
        }

        ShowWidgetOnStartup = settings.ShowWidgetOnStartup;
        AlwaysOnTop = settings.AlwaysOnTop;
        StartAtLogin = settings.StartAtLogin;
        ToastsEnabled = settings.ToastsEnabled;
        MinimumToastPriority = settings.MinimumToastPriority;
        RecentlyClosedDays = (int)settings.RecentlyClosedWindow.TotalDays;
        DailyCount = DailyCountModes.First(m => m.Mode == settings.DailyCountMode);
        CheckForUpdates = settings.CheckForUpdates;
        IncludePrereleaseUpdates = settings.IncludePrereleaseUpdates;
        Theme = settings.Theme;
        Selected = Repositories.FirstOrDefault();
    }
}

/// <summary>Choice of the counting period shown in Settings.</summary>
public sealed record DailyCountChoice(DailyCountMode Mode, string Title)
{
    public override string ToString() => Title;
}
