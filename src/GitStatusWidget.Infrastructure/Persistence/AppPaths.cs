namespace GitStatusWidget.Infrastructure.Persistence;

/// <summary>
/// Locations of local files (architecture §19). Root: <c>LocalApplicationData/GitStatusWidget</c>
/// (Windows <c>%LocalAppData%</c>, macOS <c>~/Library/Application Support</c>, Linux <c>~/.local/share</c>),
/// overridable with the <c>GITWIDGET_DATA_DIR</c> environment variable (development and tests).
/// </summary>
public sealed class AppPaths
{
    /// <summary>Environment variable that overrides the data root.</summary>
    public const string DataDirectoryVariable = "GITWIDGET_DATA_DIR";

    /// <summary>Creates paths below <paramref name="root"/>.</summary>
    /// <param name="root">Root directory.</param>
    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>Data root directory.</summary>
    public string Root { get; }

    /// <summary><c>settings.json</c>.</summary>
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary><c>window-state.json</c>.</summary>
    public string WindowStateFile => Path.Combine(Root, "window-state.json");

    /// <summary><c>claude-usage-history.json</c>: recent usage percentages for the pace estimate (issue #18).</summary>
    public string ClaudeUsageHistoryFile => Path.Combine(Root, "claude-usage-history.json");

    /// <summary>Log directory.</summary>
    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>Cache directory of all repositories.</summary>
    public string CacheRoot => Path.Combine(Root, "cache");

    /// <summary>Creates the default paths for the current user.</summary>
    /// <returns>The paths.</returns>
    public static AppPaths CreateDefault()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new AppPaths(overridden);
        }

        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrEmpty(baseDirectory))
        {
            baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return new AppPaths(Path.Combine(baseDirectory, "GitStatusWidget"));
    }

    /// <summary>Cache directory of one repository configuration.</summary>
    /// <param name="configurationId">Configuration id.</param>
    /// <returns>The directory path.</returns>
    public string CacheDirectory(Guid configurationId) => Path.Combine(CacheRoot, configurationId.ToString("D"));
}
