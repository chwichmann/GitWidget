using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitStatusWidget.Application.Platform;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Infrastructure.Persistence;

namespace GitStatusWidget.Desktop.ViewModels;

/// <summary>One repository in the diagnostics view.</summary>
public sealed record DiagnosticsRepositoryItem(
    string Name,
    string Provider,
    string ApiBase,
    string Health,
    string LastAttempt,
    string LastSuccess,
    string SnapshotAge,
    string Contents,
    string RateLimit,
    string? BlockedUntil,
    string? LastError);

/// <summary>Diagnostics view (architecture §22). Shows no secrets, no issue contents.</summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly IRepositoryStatusHub _hub;
    private readonly AppPaths _paths;
    private readonly CredentialStoreStatus _credentialStatus;
    private readonly TimeProvider _time;

    public DiagnosticsViewModel(IRepositoryStatusHub hub, AppPaths paths, CredentialStoreStatus credentialStatus, DiagnosticsLog log, TimeProvider time)
    {
        _hub = hub;
        _paths = paths;
        _credentialStatus = credentialStatus;
        _time = time;
        Errors = log.Errors;
        Reload();
    }

    /// <summary>Asks the view to put text on the clipboard.</summary>
    public event EventHandler<string>? CopyRequested;

    public string AppVersion { get; } =
        typeof(DiagnosticsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(DiagnosticsViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";

    public string Runtime { get; } = $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    public string DataDirectory => _paths.Root;

    public string LogDirectory => _paths.LogsDirectory;

    public string CredentialStore => _credentialStatus.IsPersistent
        ? _credentialStatus.StoreName
        : $"{_credentialStatus.StoreName} – not persistent{(_credentialStatus.FallbackReason is { } reason ? $" ({reason})" : string.Empty)}";

    public ObservableCollection<DiagnosticsRepositoryItem> Repositories { get; } = [];

    public ObservableCollection<DiagnosticsError> Errors { get; }

    [RelayCommand]
    private void Reload()
    {
        var now = _time.GetUtcNow();
        Repositories.Clear();
        foreach (var state in _hub.States)
        {
            Repositories.Add(Describe(state, now));
        }
    }

    [RelayCommand]
    private void OpenLogFolder() => FolderOpener.TryOpen(_paths.LogsDirectory);

    [RelayCommand]
    private void OpenDataFolder() => FolderOpener.TryOpen(_paths.Root);

    [RelayCommand]
    private void Copy() => CopyRequested?.Invoke(this, BuildReport());

    internal string BuildReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"GitWidget {AppVersion}");
        builder.AppendLine(Runtime);
        builder.AppendLine(CultureInfo.InvariantCulture, $"Credential store: {CredentialStore}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Data: {DataDirectory}");
        foreach (var repository in Repositories)
        {
            builder.AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"[{repository.Name}] {repository.Provider} {repository.ApiBase}")
                .AppendLine(CultureInfo.InvariantCulture, $"  health: {repository.Health}; last attempt {repository.LastAttempt}; last success {repository.LastSuccess}; snapshot age {repository.SnapshotAge}")
                .AppendLine(CultureInfo.InvariantCulture, $"  contents: {repository.Contents}; rate limit: {repository.RateLimit}");
            if (repository.BlockedUntil is not null)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"  paused until {repository.BlockedUntil}");
            }

            if (repository.LastError is not null)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"  last error: {repository.LastError}");
            }
        }

        if (Errors.Count > 0)
        {
            builder.AppendLine().AppendLine("Recent errors:");
            foreach (var error in Errors.Take(20))
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"  {error.At.ToLocalTime():yyyy-MM-dd HH:mm:ss} {error.Repository}: {error.Kind}: {error.Message}");
            }
        }

        return builder.ToString();
    }

    internal static string ApiBaseOf(RepositoryProviderKind provider, Uri server) => provider switch
    {
        RepositoryProviderKind.GitHub when server.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) => "https://api.github.com/",
        RepositoryProviderKind.GitHub => new Uri(server, "api/v3/").ToString(),
        _ => new Uri(server, "api/v4/").ToString(),
    };

    private static DiagnosticsRepositoryItem Describe(RepositoryRuntimeState state, DateTimeOffset now)
    {
        static string Time(DateTimeOffset? value) => value?.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) ?? "never";

        var snapshot = state.Snapshot;
        var contents = snapshot is null
            ? "no snapshot yet"
            : string.Create(CultureInfo.CurrentCulture,
                $"{snapshot.IssueCounts.Open} open / {snapshot.IssueCounts.Closed} closed issues, {snapshot.Issues.Count} loaded, {snapshot.ChangeRequests.Count} change requests, {snapshot.Milestones.Count} milestones, {snapshot.Labels.Count} labels, {snapshot.Builds.Count} builds");
        var rate = state.RateLimit is { } limit
            ? string.Create(CultureInfo.CurrentCulture, $"{limit.Remaining?.ToString(CultureInfo.CurrentCulture) ?? "?"}/{limit.Limit?.ToString(CultureInfo.CurrentCulture) ?? "?"}{(limit.Resource is null ? string.Empty : $" ({limit.Resource})")}, resets {Time(limit.ResetAt)}")
            : "not reported";
        return new DiagnosticsRepositoryItem(
            state.Configuration.EffectiveDisplayName,
            state.Configuration.Provider.ToString(),
            ApiBaseOf(state.Configuration.Provider, state.Configuration.ServerUri),
            state.IsRefreshing ? $"{state.Health} (refreshing)" : state.Health.ToString(),
            Time(state.LastAttemptAt),
            Time(state.LastSuccessAt),
            snapshot is null ? "–" : Format.Age(snapshot.RetrievedAt, now),
            contents,
            rate,
            state.BlockedUntil is { } blocked && blocked > now ? Time(blocked) : null,
            state.LastError);
    }
}
