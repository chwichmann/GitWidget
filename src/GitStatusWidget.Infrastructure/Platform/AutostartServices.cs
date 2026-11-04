using System.Text;
using GitStatusWidget.Application.Platform;

namespace GitStatusWidget.Infrastructure.Platform;

/// <summary>Windows: <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> value with the quoted executable path.</summary>
/// <param name="store">Registry access.</param>
/// <param name="valueName">Run value name.</param>
/// <param name="command">Command to register, or <see langword="null"/> when it cannot be determined.</param>
internal sealed class WindowsRunKeyAutostartService(IRunKeyStore store, string valueName, AutostartCommand? command) : IAutostartService
{
    public bool IsSupported => command is not null;

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(store.GetRunValue(valueName) is { Length: > 0 } && !store.IsDisabledByUser(valueName));
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (enabled)
        {
            store.SetRunValue(valueName, AutostartFormats.WindowsCommandLine(RequireCommand(command)));
            store.ClearUserOverride(valueName); // The user explicitly asked for autostart in the app.
        }
        else
        {
            store.DeleteRunValue(valueName);
        }

        return Task.CompletedTask;
    }

    internal static AutostartCommand RequireCommand(AutostartCommand? command) =>
        command ?? throw new NotSupportedException("The application executable path could not be determined, so start at login cannot be enabled.");
}

/// <summary>macOS: per-user LaunchAgent plist (<c>~/Library/LaunchAgents/&lt;label&gt;.plist</c>), loaded by launchd at login.</summary>
/// <param name="launchAgentsDirectory">Directory of the plist (normally <c>~/Library/LaunchAgents</c>).</param>
/// <param name="label">launchd label, also the file name.</param>
/// <param name="command">Command to register, or <see langword="null"/>.</param>
internal sealed class MacLaunchAgentAutostartService(string launchAgentsDirectory, string label, AutostartCommand? command) : IAutostartService
{
    public string PlistPath { get; } = Path.Combine(launchAgentsDirectory, label + ".plist");

    public bool IsSupported => command is not null;

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(PlistPath));
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(PlistPath); // Takes effect at the next login; no error when missing.
            return Task.CompletedTask;
        }

        string content = AutostartFormats.LaunchAgentPlist(label, WindowsRunKeyAutostartService.RequireCommand(command));
        return AutostartFiles.WriteAtomicAsync(PlistPath, content, cancellationToken);
    }
}

/// <summary>Linux: XDG autostart desktop entry (<c>$XDG_CONFIG_HOME/autostart/gitwidget.desktop</c>).</summary>
/// <param name="autostartDirectory">Autostart directory (normally <c>~/.config/autostart</c>).</param>
/// <param name="fileName">Desktop file name.</param>
/// <param name="displayName">Entry name.</param>
/// <param name="command">Command to register, or <see langword="null"/>.</param>
internal sealed class XdgAutostartService(string autostartDirectory, string fileName, string displayName, AutostartCommand? command) : IAutostartService
{
    public string DesktopFilePath { get; } = Path.Combine(autostartDirectory, fileName);

    public bool IsSupported => command is not null;

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(DesktopFilePath));
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(DesktopFilePath);
            return Task.CompletedTask;
        }

        string content = AutostartFormats.XdgDesktopEntry(displayName, WindowsRunKeyAutostartService.RequireCommand(command));
        return AutostartFiles.WriteAtomicAsync(DesktopFilePath, content, cancellationToken);
    }

    /// <summary>Default autostart directory: <c>$XDG_CONFIG_HOME/autostart</c> (absolute only) or <c>~/.config/autostart</c>.</summary>
    /// <param name="xdgConfigHome">Value of <c>XDG_CONFIG_HOME</c>.</param>
    /// <param name="userProfile">Home directory.</param>
    /// <returns>The directory.</returns>
    internal static string DefaultDirectory(string? xdgConfigHome, string userProfile) =>
        Path.Combine(
            !string.IsNullOrWhiteSpace(xdgConfigHome) && Path.IsPathRooted(xdgConfigHome) ? xdgConfigHome : Path.Combine(userProfile, ".config"),
            "autostart");
}

/// <summary>Fallback for unsupported operating systems.</summary>
internal sealed class UnsupportedAutostartService : IAutostartService
{
    public bool IsSupported => false;

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        enabled ? Task.FromException(new NotSupportedException("Start at login is not supported on this operating system.")) : Task.CompletedTask;
}

/// <summary>Creates the autostart service for the current OS and user profile.</summary>
internal static class AutostartServiceFactory
{
    public static IAutostartService CreateDefault()
    {
        AutostartCommand? command = AutostartCommand.ResolveCurrent();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            return new WindowsRunKeyAutostartService(new RegistryRunKeyStore(), PlatformIdentity.WindowsRunValueName, command);
        }

        if (string.IsNullOrEmpty(home))
        {
            return new UnsupportedAutostartService();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacLaunchAgentAutostartService(Path.Combine(home, "Library", "LaunchAgents"), PlatformIdentity.LaunchAgentLabel, command);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            string directory = XdgAutostartService.DefaultDirectory(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), home);
            return new XdgAutostartService(directory, PlatformIdentity.DesktopFileName, PlatformIdentity.DisplayName, command);
        }

        return new UnsupportedAutostartService();
    }
}

/// <summary>File helper for autostart entries.</summary>
internal static class AutostartFiles
{
    /// <summary>Writes UTF-8 (no BOM) to a temporary sibling, then replaces the target.</summary>
    /// <param name="path">Target path.</param>
    /// <param name="content">Content.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the file is in place.</returns>
    public static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("Path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
