using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GitStatusWidget.Infrastructure.Platform;

/// <summary>Access to the per-user Windows Run key, abstracted so tests never touch the real registry.</summary>
internal interface IRunKeyStore
{
    /// <summary>Reads a value of <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.</summary>
    /// <param name="name">Value name.</param>
    /// <returns>The command line, or <see langword="null"/>.</returns>
    string? GetRunValue(string name);

    /// <summary>Creates or replaces a Run value.</summary>
    /// <param name="name">Value name.</param>
    /// <param name="commandLine">Command line.</param>
    void SetRunValue(string name, string commandLine);

    /// <summary>Deletes a Run value; no error when it is missing.</summary>
    /// <param name="name">Value name.</param>
    void DeleteRunValue(string name);

    /// <summary>Whether Task Manager / Settings → Startup apps marks the entry as disabled.</summary>
    /// <param name="name">Value name.</param>
    /// <returns><see langword="true"/> when disabled by the user.</returns>
    bool IsDisabledByUser(string name);

    /// <summary>Removes the Task Manager enabled/disabled override (after the user explicitly re-enabled the entry in the app).</summary>
    /// <param name="name">Value name.</param>
    void ClearUserOverride(string name);
}

/// <summary><see cref="IRunKeyStore"/> backed by <see cref="Registry.CurrentUser"/> (no admin rights needed).</summary>
[SupportedOSPlatform("windows")]
internal sealed class RegistryRunKeyStore : IRunKeyStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Undocumented: REG_BINARY whose first byte is even (0x02, 0x06) when enabled and odd (0x03) when disabled.
    private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public string? GetRunValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(name) as string;
    }

    public void SetRunValue(string name, string commandLine)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(name, commandLine, RegistryValueKind.String);
    }

    public void DeleteRunValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public bool IsDisabledByUser(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKey);
        return key?.GetValue(name) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
    }

    public void ClearUserOverride(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
