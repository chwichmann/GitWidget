namespace GitStatusWidget.Infrastructure.Platform;

/// <summary>
/// OS-facing identifiers of the application. Debug builds use separate names so a development run
/// never replaces the autostart entry or single-instance lock of an installed build.
/// </summary>
internal static class PlatformIdentity
{
    /// <summary>Command-line flag passed by start-at-login entries.</summary>
    public const string AutostartArgument = "--autostart";

    /// <summary>Display name used in OS UI (Login Items, autostart dialogs).</summary>
    public const string DisplayName = "GitWidget";

#if DEBUG
    /// <summary>Windows Run value name.</summary>
    public const string WindowsRunValueName = "GitStatusWidget.Debug";

    /// <summary>
    /// launchd label and plist file name. Placeholder reverse-DNS id: keep it equal to the
    /// <c>CFBundleIdentifier</c> of the .app once that is chosen.
    /// </summary>
    public const string LaunchAgentLabel = "io.github.chwichmann.GitStatusWidget.Debug";

    /// <summary>XDG autostart file name.</summary>
    public const string DesktopFileName = "gitwidget-debug.desktop";

    /// <summary>Base name of the single-instance mutex and pipe.</summary>
    public const string SingleInstanceId = "GitStatusWidget.Debug";
#else
    /// <summary>Windows Run value name.</summary>
    public const string WindowsRunValueName = "GitStatusWidget";

    /// <summary>
    /// launchd label and plist file name. Placeholder reverse-DNS id: keep it equal to the
    /// <c>CFBundleIdentifier</c> of the .app once that is chosen.
    /// </summary>
    public const string LaunchAgentLabel = "io.github.chwichmann.GitStatusWidget";

    /// <summary>XDG autostart file name.</summary>
    public const string DesktopFileName = "gitwidget.desktop";

    /// <summary>Base name of the single-instance mutex and pipe.</summary>
    public const string SingleInstanceId = "GitStatusWidget";
#endif
}
